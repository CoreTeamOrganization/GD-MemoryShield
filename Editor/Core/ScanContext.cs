// Editor/Core/ScanContext.cs
// Shared asset index, built ONCE before any analyzer runs. Analyzers read from
// this and never re-query the AssetDatabase themselves — without that rule a
// 10k-asset project takes ten minutes.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;

namespace GameDistrict.MemoryShield.Core
{
    public class ScanContext
    {
        public class AssetEntry
        {
            public string guid;
            public string path;
            public string extension;   // lowercase, with dot
        }

        public class CsFile
        {
            public string path;
            public string content;
            public string[] lines;
            public string guid;        // GUID of the .cs asset (for prefab reference walks)
        }

        // ── Indexes ──────────────────────────────────────────────────────────
        public List<AssetEntry> AllAssets = new List<AssetEntry>();
        public Dictionary<string, AssetEntry> ByPath = new Dictionary<string, AssetEntry>();

        public List<string> TexturePaths = new List<string>();
        public List<string> AudioPaths = new List<string>();
        public List<string> ScenePaths = new List<string>();
        public List<string> PrefabPaths = new List<string>();
        public List<string> AtlasPaths = new List<string>();
        public List<string> ModelPaths = new List<string>();

        // scene/prefab path -> every asset path it (transitively) depends on
        public Dictionary<string, HashSet<string>> SceneDeps = new Dictionary<string, HashSet<string>>();
        public Dictionary<string, HashSet<string>> PrefabDeps = new Dictionary<string, HashSet<string>>();

        // asset path -> the scenes that (transitively) reference it
        public Dictionary<string, HashSet<string>> ReferencedByScenes = new Dictionary<string, HashSet<string>>();
        // asset path -> the prefabs that (transitively) reference it
        public Dictionary<string, HashSet<string>> ReferencedByPrefabs = new Dictionary<string, HashSet<string>>();

        // project .cs files outside Packages/ and Plugins/
        public List<CsFile> CsFiles = new List<CsFile>();
        // one lowercase blob of all code, for cheap "does anything reference X" checks
        public string AllCodeLower = "";

        // sprite path -> atlas paths that pack it (folder packables expanded recursively)
        public Dictionary<string, List<string>> SpriteToAtlases = new Dictionary<string, List<string>>();
        // atlas path -> expanded list of packed sprite/texture paths
        public Dictionary<string, List<string>> AtlasContents = new Dictionary<string, List<string>>();

        // texture path -> content hash (for duplicate detection), cached in Library
        public Dictionary<string, string> FileHashes = new Dictionary<string, string>();

        // texture path -> estimated resident bytes (filled by TextureAnalyzer, read by Scene/Atlas)
        public Dictionary<string, long> TextureEstimates = new Dictionary<string, long>();
        // audio path -> estimated resident bytes (filled by AudioAnalyzer)
        public Dictionary<string, long> AudioEstimates = new Dictionary<string, long>();

        public bool TextSerialization;   // EditorSettings.serializationMode == ForceText

        // ── Hash cache (Library/GDMemoryShield/cache.json) ───────────────────
        [Serializable] private class HashEntry { public string guid; public long mtime; public string hash; }
        [Serializable] private class HashCache { public List<HashEntry> entries = new List<HashEntry>(); }

        private const string CacheDir = "Library/GDMemoryShield";
        private const string CachePath = CacheDir + "/cache.json";
        private HashCache _cache;
        private Dictionary<string, HashEntry> _cacheByGuid;
        private bool _cacheDirty;

        public static void ClearCache()
        {
            if (File.Exists(CachePath)) File.Delete(CachePath);
        }

        // ── Build ─────────────────────────────────────────────────────────────
        // Driven as a coroutine so the progress bar updates and the editor stays
        // responsive. Yields a status string roughly every batch of work.
        public IEnumerator BuildSteps(Action<string, float> progress)
        {
            TextSerialization = EditorSettings.serializationMode == SerializationMode.ForceText;
            LoadHashCache();

            progress("Indexing assets", 0.02f);
            string[] guids = AssetDatabase.FindAssets("", new[] { "Assets" });
            var seen = new HashSet<string>();
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (string.IsNullOrEmpty(path) || !seen.Add(path)) continue;
                if (AssetDatabase.IsValidFolder(path)) continue;
                var e = new AssetEntry
                {
                    guid = guids[i],
                    path = path,
                    extension = Path.GetExtension(path).ToLowerInvariant(),
                };
                AllAssets.Add(e);
                ByPath[path] = e;
                Bucket(e);
                if (i % 2000 == 0) { progress("Indexing assets", 0.02f + 0.08f * i / Mathf.Max(1, guids.Length)); yield return null; }
            }
            yield return null;

            progress("Reading scripts", 0.12f);
            var codeBlob = new System.Text.StringBuilder();
            for (int i = 0; i < AllAssets.Count; i++)
            {
                var e = AllAssets[i];
                if (e.extension != ".cs") continue;
                if (IsExcludedCodePath(e.path)) continue;
                string content;
                try { content = File.ReadAllText(e.path); }
                catch (IOException) { continue; }
                CsFiles.Add(new CsFile
                {
                    path = e.path,
                    content = content,
                    lines = content.Split('\n'),
                    guid = e.guid,
                });
                codeBlob.Append(content.ToLowerInvariant()).Append('\n');
                if (i % 400 == 0) yield return null;
            }
            AllCodeLower = codeBlob.ToString();
            yield return null;

            progress("Walking scene dependencies", 0.20f);
            for (int i = 0; i < ScenePaths.Count; i++)
            {
                var deps = new HashSet<string>(AssetDatabase.GetDependencies(ScenePaths[i], true));
                deps.Remove(ScenePaths[i]);
                SceneDeps[ScenePaths[i]] = deps;
                foreach (var d in deps)
                {
                    if (!ReferencedByScenes.TryGetValue(d, out var set))
                        ReferencedByScenes[d] = set = new HashSet<string>();
                    set.Add(ScenePaths[i]);
                }
                progress("Walking scene dependencies", 0.20f + 0.10f * i / Mathf.Max(1, ScenePaths.Count));
                yield return null;
            }

            progress("Walking prefab dependencies", 0.30f);
            for (int i = 0; i < PrefabPaths.Count; i++)
            {
                var deps = new HashSet<string>(AssetDatabase.GetDependencies(PrefabPaths[i], true));
                deps.Remove(PrefabPaths[i]);
                PrefabDeps[PrefabPaths[i]] = deps;
                foreach (var d in deps)
                {
                    if (!ReferencedByPrefabs.TryGetValue(d, out var set))
                        ReferencedByPrefabs[d] = set = new HashSet<string>();
                    set.Add(PrefabPaths[i]);
                }
                if (i % 20 == 0)
                {
                    progress("Walking prefab dependencies", 0.30f + 0.10f * i / Mathf.Max(1, PrefabPaths.Count));
                    yield return null;
                }
            }

            progress("Expanding sprite atlases", 0.42f);
            for (int i = 0; i < AtlasPaths.Count; i++)
            {
                ExpandAtlas(AtlasPaths[i]);
                yield return null;
            }

            progress("Hashing textures", 0.48f);
            for (int i = 0; i < TexturePaths.Count; i++)
            {
                string p = TexturePaths[i];
                string h = HashFor(p);
                if (h != null) FileHashes[p] = h;
                if (i % 100 == 0)
                {
                    progress("Hashing textures", 0.48f + 0.10f * i / Mathf.Max(1, TexturePaths.Count));
                    yield return null;
                }
            }
            SaveHashCache();
            progress("Index ready", 0.60f);
        }

        private void Bucket(AssetEntry e)
        {
            switch (e.extension)
            {
                case ".png": case ".jpg": case ".jpeg": case ".tga": case ".psd":
                case ".tif": case ".tiff": case ".bmp": case ".exr": case ".webp":
                    TexturePaths.Add(e.path); break;
                case ".wav": case ".mp3": case ".ogg": case ".aif": case ".aiff":
                    AudioPaths.Add(e.path); break;
                case ".unity":
                    ScenePaths.Add(e.path); break;
                case ".prefab":
                    PrefabPaths.Add(e.path); break;
                case ".spriteatlas": case ".spriteatlasv2":
                    AtlasPaths.Add(e.path); break;
                case ".fbx": case ".obj": case ".blend": case ".dae":
                    ModelPaths.Add(e.path); break;
            }
        }

        public static bool IsExcludedCodePath(string path)
        {
            return path.Contains("/Plugins/") || path.StartsWith("Packages/")
                || path.Contains("/Editor/");   // editor code never ships
        }

        // ── Atlas expansion — CRITICAL: packables can be folders ─────────────
        private void ExpandAtlas(string atlasPath)
        {
            var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(atlasPath);
            if (atlas == null) return;
            var contents = new List<string>();
            var packables = atlas.GetPackables();
            if (packables != null)
            {
                foreach (var packable in packables)
                {
                    if (packable == null) continue;
                    string p = AssetDatabase.GetAssetPath(packable);
                    if (string.IsNullOrEmpty(p)) continue;
                    if (AssetDatabase.IsValidFolder(p))
                    {
                        // Expand folder packables recursively. Failing to do this
                        // reports half the project as unatlased.
                        string[] inside = AssetDatabase.FindAssets("t:Sprite", new[] { p });
                        var folderSeen = new HashSet<string>();
                        foreach (var g in inside)
                        {
                            string sp = AssetDatabase.GUIDToAssetPath(g);
                            if (folderSeen.Add(sp)) contents.Add(sp);
                        }
                    }
                    else
                    {
                        contents.Add(p);
                    }
                }
            }
            AtlasContents[atlasPath] = contents;
            foreach (var sprite in contents)
            {
                if (!SpriteToAtlases.TryGetValue(sprite, out var list))
                    SpriteToAtlases[sprite] = list = new List<string>();
                if (!list.Contains(atlasPath)) list.Add(atlasPath);
            }
        }

        // ── Hashing with Library cache ────────────────────────────────────────
        private string HashFor(string path)
        {
            long mtime;
            try { mtime = File.GetLastWriteTimeUtc(path).Ticks; }
            catch (IOException) { return null; }

            string guid = ByPath.TryGetValue(path, out var e) ? e.guid : null;
            if (guid != null && _cacheByGuid.TryGetValue(guid, out var hit) && hit.mtime == mtime)
                return hit.hash;

            string hash;
            try
            {
                using (var md5 = MD5.Create())
                using (var stream = File.OpenRead(path))
                    hash = BitConverter.ToString(md5.ComputeHash(stream)).Replace("-", "");
            }
            catch (IOException) { return null; }

            if (guid != null)
            {
                if (_cacheByGuid.TryGetValue(guid, out var stale))
                {
                    stale.mtime = mtime;
                    stale.hash = hash;
                }
                else
                {
                    var entry = new HashEntry { guid = guid, mtime = mtime, hash = hash };
                    _cache.entries.Add(entry);
                    _cacheByGuid[guid] = entry;
                }
                _cacheDirty = true;
            }
            return hash;
        }

        private void LoadHashCache()
        {
            _cache = new HashCache();
            _cacheByGuid = new Dictionary<string, HashEntry>();
            try
            {
                if (File.Exists(CachePath))
                    _cache = JsonUtility.FromJson<HashCache>(File.ReadAllText(CachePath)) ?? new HashCache();
            }
            catch (Exception) { _cache = new HashCache(); }
            foreach (var e in _cache.entries)
                if (!string.IsNullOrEmpty(e.guid)) _cacheByGuid[e.guid] = e;
        }

        private void SaveHashCache()
        {
            if (!_cacheDirty) return;
            try
            {
                Directory.CreateDirectory(CacheDir);
                File.WriteAllText(CachePath, JsonUtility.ToJson(_cache));
            }
            catch (Exception) { /* cache is an optimization, never fail the scan over it */ }
            _cacheDirty = false;
        }
    }
}

// Editor/Analyzers/TextureAnalyzer.cs
// TEX rules. Per-platform import settings are the correctness crux here:
// everything reads GetPlatformTextureSettings("Android") / ("iPhone"), never
// the default settings — reading defaults is the single biggest source of
// false negatives in tools like this.

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GameDistrict.MemoryShield.Core;
using GameDistrict.MemoryShield.Model;
using UnityEditor;
using UnityEngine;

namespace GameDistrict.MemoryShield.Analyzers
{
    public class TextureAnalyzer : IMemoryAnalyzer
    {
        public string CategoryName { get { return "Textures"; } }

        private static MethodInfo _getSourceSize;

        // Textures above this get their own row with the full story; smaller ones
        // fold into one row per rule per folder. 2000 identical rows is noise —
        // the folder is the actionable unit (multi-select, one apply).
        private const long BigTextureBytes = 1024L * 1024;

        private class GroupBucket
        {
            public string ruleId;
            public string folder;
            public int count;
            public long recoverBytes;
            public string samplePath;
        }

        private readonly Dictionary<string, GroupBucket> _groups = new Dictionary<string, GroupBucket>();

        private void Collect(string ruleId, string path, long recoverBytes)
        {
            string folder = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
            string key = ruleId + "|" + folder;
            if (!_groups.TryGetValue(key, out var b))
                _groups[key] = b = new GroupBucket { ruleId = ruleId, folder = folder, samplePath = path };
            b.count++;
            b.recoverBytes += recoverBytes;
        }

        public IEnumerator Analyze(ScanContext ctx, CategoryResult result, MemoryReport report)
        {
            _groups.Clear();
            var stats = new List<TextureStat>();
            var byHash = new Dictionary<string, List<string>>();
            var noOverride = new List<string>();

            for (int i = 0; i < ctx.TexturePaths.Count; i++)
            {
                string path = ctx.TexturePaths[i];
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null) continue;

                var android = importer.GetPlatformTextureSettings("Android");
                var ios = importer.GetPlatformTextureSettings("iPhone");
                bool isSpriteOrUi = importer.textureType == TextureImporterType.Sprite
                                 || importer.textureType == TextureImporterType.GUI;

                GetSourceSize(importer, path, out int srcW, out int srcH);
                int effW = srcW, effH = srcH;
                int maxSize = android.overridden ? android.maxTextureSize : importer.maxTextureSize;
                if (maxSize > 0 && (effW > maxSize || effH > maxSize))
                {
                    float scale = Mathf.Min((float)maxSize / effW, (float)maxSize / effH);
                    effW = Mathf.Max(1, Mathf.RoundToInt(effW * scale));
                    effH = Mathf.Max(1, Mathf.RoundToInt(effH * scale));
                }

                TextureImporterFormat fmt = ResolveFormat(importer, android, "Android");
                float bpp = BitsPerPixel(fmt);
                long estBytes = (long)(effW * (long)effH * bpp / 8f);
                if (importer.mipmapEnabled) estBytes = (long)(estBytes * 1.33f);
                if (importer.isReadable) estBytes *= 2;
                ctx.TextureEstimates[path] = estBytes;

                stats.Add(new TextureStat
                {
                    path = path, width = effW, height = effH,
                    format = fmt.ToString(),
                    readable = importer.isReadable,
                    mipmaps = importer.mipmapEnabled,
                    estimatedBytes = estBytes,
                });

                // TEX-001 — Read/Write doubles the footprint with a permanent CPU copy
                if (importer.isReadable)
                {
                    if (estBytes >= BigTextureBytes)
                        result.findings.Add(Finding.Make("TEX-001", Severity.High, path,
                            string.Format("Read/Write is on — that keeps a full CPU-side copy alive, roughly {0} extra.", Fmt(estBytes / 2)),
                            "Untick Read/Write Enabled unless code actually calls GetPixels on it.",
                            estBytes / 2, 0, "S"));
                    else Collect("TEX-001", path, estBytes / 2);
                }

                // TEX-002 — uncompressed format on the platform override
                if (android.overridden && IsUncompressed(android.format))
                {
                    if (estBytes >= BigTextureBytes)
                        result.findings.Add(Finding.Make("TEX-002", Severity.High, path,
                            string.Format("Android override is {0} — uncompressed. ASTC would be ~4-8x smaller.", android.format),
                            "Set the Android override to ASTC 6x6 (or 4x4 for UI that shows compression artifacts).",
                            (long)(estBytes * 0.75f), 0, "S"));
                    else Collect("TEX-002", path, (long)(estBytes * 0.75f));
                }

                // TEX-003 — no platform override at all. Collected and grouped per
                // folder below: on an untended project this fires thousands of
                // times, and 2000 identical rows is noise nobody can act on.
                if (!android.overridden && !ios.overridden)
                    noOverride.Add(path);

                // TEX-004 — mipmaps on sprite/UI textures
                if (importer.mipmapEnabled && isSpriteOrUi)
                {
                    long waste = estBytes - (long)(estBytes / 1.33f);
                    if (estBytes >= BigTextureBytes)
                        result.findings.Add(Finding.Make("TEX-004", Severity.Medium, path,
                            string.Format("Mipmaps on a sprite/UI texture — +33% ({0}) for nothing, UI never minifies.", Fmt(waste)),
                            "Untick Generate Mip Maps.", waste, 0, "S"));
                    else Collect("TEX-004", path, waste);
                }

                // TEX-005 — maxTextureSize above source, or >=2048 on UI
                if (srcW > 0 && srcH > 0)
                {
                    if (maxSize > Mathf.Max(srcW, srcH) * 2 && maxSize > 1024)
                        Collect("TEX-005", path, 0);   // hygiene, not weight — always grouped
                    else if (isSpriteOrUi && maxSize >= 2048 && Mathf.Max(effW, effH) >= 2048)
                        result.findings.Add(Finding.Make("TEX-005", Severity.Medium, path,
                            string.Format("A {0}x{1} UI texture — on phone screens 1024 is almost always enough.", effW, effH),
                            "Cap Android max size at 1024 and eyeball it on device.",
                            estBytes - estBytes / 4, 0, "S"));
                }

                // TEX-006 — dimensions not divisible by 4 block ASTC/ETC2
                if (srcW > 0 && (srcW % 4 != 0 || srcH % 4 != 0) && !isSpriteOrUi)
                {
                    if (estBytes >= BigTextureBytes)
                        result.findings.Add(Finding.Make("TEX-006", Severity.Medium, path,
                            string.Format("Source is {0}x{1} — not divisible by 4, which blocks ASTC/ETC2 and silently falls back to an uncompressed format.", srcW, srcH),
                            "Resize the source to multiples of 4.", 0, 0, "S"));
                    else Collect("TEX-006", path, 0);
                }

                // TEX-008 — duplicate detection via content hash
                if (ctx.FileHashes.TryGetValue(path, out string hash))
                {
                    if (!byHash.TryGetValue(hash, out var same)) byHash[hash] = same = new List<string>();
                    same.Add(path);
                }

                if (i % 50 == 0) yield return null;
            }

            // TEX-003 — grouped per folder. Fixing these is a multi-select in the
            // inspector anyway, so one row per folder is the actionable unit.
            var noOverrideByFolder = new Dictionary<string, List<string>>();
            foreach (var p in noOverride)
            {
                string folder = System.IO.Path.GetDirectoryName(p).Replace('\\', '/');
                if (!noOverrideByFolder.TryGetValue(folder, out var list))
                    noOverrideByFolder[folder] = list = new List<string>();
                list.Add(p);
            }
            foreach (var kv in noOverrideByFolder)
            {
                long folderBytes = 0;
                foreach (var p in kv.Value)
                    if (ctx.TextureEstimates.TryGetValue(p, out long b)) folderBytes += b;
                if (kv.Value.Count == 1)
                    result.findings.Add(Finding.Make("TEX-003", Severity.High, kv.Value[0],
                        "No Android or iOS platform override — the texture ships with default settings, which usually means a fatter format than needed.",
                        "Add an Android override with ASTC and a sensible max size.", 0, 0, "S"));
                else
                    result.findings.Add(Finding.Make("TEX-003", Severity.High, kv.Key,
                        string.Format("{0} textures in this folder have no Android or iOS platform override (~{1} at current settings) — they all ship with defaults, which usually means a fatter format than needed.",
                            kv.Value.Count, Fmt(folderBytes)),
                        "Select them all, add an Android override with ASTC, one apply.",
                        (long)(folderBytes * 0.5f), 0, "S", kv.Value.Count));
            }
            yield return null;

            // grouped small-texture rows — one per rule per folder
            foreach (var b in _groups.Values)
            {
                string what, fix;
                Severity sev;
                switch (b.ruleId)
                {
                    case "TEX-001":
                        what = "{0} smaller textures in this folder have Read/Write on — ~{1} of CPU-side copies combined.";
                        fix = "Multi-select and untick Read/Write Enabled."; sev = Severity.High; break;
                    case "TEX-002":
                        what = "{0} smaller textures here use an uncompressed Android override — ~{1} combined that ASTC would mostly recover.";
                        fix = "Multi-select, set the Android override to ASTC 6x6."; sev = Severity.High; break;
                    case "TEX-004":
                        what = "{0} sprite/UI textures here have mipmaps on — ~{1} combined for nothing, UI never minifies.";
                        fix = "Multi-select and untick Generate Mip Maps."; sev = Severity.Medium; break;
                    case "TEX-005":
                        what = "{0} textures here have a max size cap far above their source — the cap does nothing and invites future bloat.";
                        fix = "Drop max size to the next power of two above each source."; sev = Severity.Medium; break;
                    default: // TEX-006
                        what = "{0} textures here have dimensions not divisible by 4 — that blocks ASTC/ETC2 and falls back to uncompressed.";
                        fix = "Resize the sources to multiples of 4."; sev = Severity.Medium; break;
                }
                if (b.count == 1)
                    result.findings.Add(Finding.Make(b.ruleId, sev, b.samplePath,
                        string.Format(what, 1, Fmt(b.recoverBytes)).Replace("1 smaller textures", "This small texture").Replace("1 sprite/UI textures", "This sprite/UI texture").Replace("1 textures", "This texture").Replace("have ", "has "),
                        fix, b.recoverBytes, 0, "S"));
                else
                    result.findings.Add(Finding.Make(b.ruleId, sev, b.folder,
                        string.Format(what, b.count, Fmt(b.recoverBytes)),
                        fix, b.recoverBytes, 0, "S", b.count));
            }
            yield return null;

            // TEX-008 — identical file hash, different paths
            foreach (var kv in byHash)
            {
                if (kv.Value.Count < 2) continue;
                long each = ctx.TextureEstimates.TryGetValue(kv.Value[0], out long b) ? b : 0;
                result.findings.Add(Finding.Make("TEX-008", Severity.Medium, kv.Value[0],
                    string.Format("Identical texture in {0} places: {1}. Each copy loads separately.",
                        kv.Value.Count, string.Join(", ", kv.Value)),
                    "Keep one, repoint the references.",
                    each * (kv.Value.Count - 1), 0, "M"));
            }
            yield return null;

            // TEX-009 — ASTC 4x4 where 6x6/8x8 would hold up (big non-UI textures)
            foreach (var s in stats)
            {
                if (!s.format.Contains("ASTC_4x4")) continue;
                if (s.width < 512 && s.height < 512) continue;
                var importer = AssetImporter.GetAtPath(s.path) as TextureImporter;
                if (importer != null && importer.textureType == TextureImporterType.Sprite) continue;
                result.findings.Add(Finding.Make("TEX-009", Severity.Low, s.path,
                    string.Format("ASTC 4x4 on a {0}x{1} non-UI texture — 6x6 is usually indistinguishable at half the size.", s.width, s.height),
                    "Try ASTC 6x6 and compare on device.",
                    s.estimatedBytes / 2, 0, "S"));
            }
            yield return null;

            // TEX-010 — RenderTexture assets at high resolution or fat formats
            foreach (var e in ctx.AllAssets)
            {
                if (e.extension != ".rendertexture") continue;
                var rt = AssetDatabase.LoadAssetAtPath<RenderTexture>(e.path);
                if (rt == null) continue;
                long rtBytes = (long)rt.width * rt.height * 4;
                if (rt.width >= 1024 || rt.height >= 1024 || rt.format == RenderTextureFormat.ARGBFloat
                    || rt.format == RenderTextureFormat.ARGBHalf)
                    result.findings.Add(Finding.Make("TEX-010", Severity.Medium, e.path,
                        string.Format("RenderTexture asset at {0}x{1} {2} (~{3}) — check it genuinely needs that resolution and precision.",
                            rt.width, rt.height, rt.format, Fmt(rtBytes)),
                        "Halve the resolution or drop to ARGB32 unless the effect visibly needs more.",
                        rtBytes / 2, 0, "S"));
            }

            // Deliverable: top 20 heaviest textures. Estimates everywhere — they
            // ignore atlas packing and streaming, and the report labels them so.
            report.topTextures = stats.OrderByDescending(s => s.estimatedBytes).Take(20).ToList();
            yield return null;
        }

        // ── helpers ───────────────────────────────────────────────────────────

        private static void GetSourceSize(TextureImporter importer, string path, out int w, out int h)
        {
            w = 0; h = 0;
            // Internal but stable since 4.x; fall back to the imported texture size.
            if (_getSourceSize == null)
                _getSourceSize = typeof(TextureImporter).GetMethod(
                    "GetSourceTextureWidthAndHeight",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            if (_getSourceSize != null)
            {
                object[] args = { 0, 0 };
                try
                {
                    _getSourceSize.Invoke(importer, args);
                    w = (int)args[0]; h = (int)args[1];
                    if (w > 0 && h > 0) return;
                }
                catch (System.Exception) { /* fall through */ }
            }
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (tex != null) { w = tex.width; h = tex.height; }
        }

        private static TextureImporterFormat ResolveFormat(
            TextureImporter importer, TextureImporterPlatformSettings platform, string platformName)
        {
            if (platform.overridden && platform.format != TextureImporterFormat.Automatic)
                return platform.format;
            try { return importer.GetAutomaticFormat(platformName); }
            catch (System.Exception) { return TextureImporterFormat.RGBA32; }
        }

        private static bool IsUncompressed(TextureImporterFormat f)
        {
            return f == TextureImporterFormat.RGBA32 || f == TextureImporterFormat.ARGB32
                || f == TextureImporterFormat.RGB24 || f == TextureImporterFormat.Alpha8
                || f == TextureImporterFormat.RGBA16 || f == TextureImporterFormat.RGB16;
        }

        // Estimated bits per pixel by format. Estimates, not gospel.
        public static float BitsPerPixel(TextureImporterFormat f)
        {
            switch (f)
            {
                case TextureImporterFormat.RGBA32:
                case TextureImporterFormat.ARGB32: return 32f;
                case TextureImporterFormat.RGB24: return 24f;
                case TextureImporterFormat.RGBA16:
                case TextureImporterFormat.ARGB16:
                case TextureImporterFormat.RGB16: return 16f;
                case TextureImporterFormat.Alpha8: return 8f;
                case TextureImporterFormat.ASTC_4x4: return 8f;
                case TextureImporterFormat.ASTC_5x5: return 5.12f;
                case TextureImporterFormat.ASTC_6x6: return 3.56f;
                case TextureImporterFormat.ASTC_8x8: return 2f;
                case TextureImporterFormat.ASTC_10x10: return 1.28f;
                case TextureImporterFormat.ASTC_12x12: return 0.89f;
                case TextureImporterFormat.ETC2_RGBA8: return 8f;
                case TextureImporterFormat.ETC2_RGB4:
                case TextureImporterFormat.ETC_RGB4: return 4f;
                case TextureImporterFormat.PVRTC_RGBA4:
                case TextureImporterFormat.PVRTC_RGB4: return 4f;
                case TextureImporterFormat.PVRTC_RGBA2:
                case TextureImporterFormat.PVRTC_RGB2: return 2f;
                case TextureImporterFormat.DXT1: return 4f;
                case TextureImporterFormat.DXT5:
                case TextureImporterFormat.BC7: return 8f;
                default: return 16f;   // unknown: assume mid-fat rather than flattering
            }
        }

        public static string Fmt(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return (bytes / (1024f * 1024f * 1024f)).ToString("0.##") + " GB";
            if (bytes >= 1024L * 1024) return (bytes / (1024f * 1024f)).ToString("0.#") + " MB";
            if (bytes >= 1024L) return (bytes / 1024f).ToString("0.#") + " KB";
            return bytes + " B";
        }
    }
}

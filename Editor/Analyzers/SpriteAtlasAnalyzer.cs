// Editor/Analyzers/SpriteAtlasAnalyzer.cs
// ATL rules, estimate mode. An atlas is not automatically a memory win — this
// answers packing efficiency, lifetime mixing, and POT padding.
//
// POT padding is CALIBRATED, not hardcoded: Unity's atlas packer POT behaviour
// has shifted across versions. Defaults ship marked unverified; the Calibration
// utility (window footer) packs a throwaway atlas per format on the project's
// actual Unity version and records the real padding behaviour.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameDistrict.MemoryShield.Core;
using GameDistrict.MemoryShield.Model;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;

namespace GameDistrict.MemoryShield.Analyzers
{
    [Serializable]
    public class AtlasCalibration
    {
        public string unityVersion = "";
        public bool verified = false;          // false = shipping defaults, take with salt
        public bool astcRoundsToBlock = true;  // ~1x padding
        public bool etc2RequiresPotPerAxis = true;
        public bool pvrtcRequiresSquarePot = true;

        private const string PathOnDisk = "Library/GDMemoryShield/calibration.json";

        public static AtlasCalibration Load()
        {
            try
            {
                if (File.Exists(PathOnDisk))
                {
                    var c = JsonUtility.FromJson<AtlasCalibration>(File.ReadAllText(PathOnDisk));
                    if (c != null && c.unityVersion == Application.unityVersion && c.verified)
                        return c;
                }
            }
            catch (Exception) { }
            return new AtlasCalibration { unityVersion = Application.unityVersion };
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory("Library/GDMemoryShield");
                File.WriteAllText(PathOnDisk, JsonUtility.ToJson(this, true));
            }
            catch (Exception) { }
        }
    }

    public class SpriteAtlasAnalyzer : IMemoryAnalyzer
    {
        public string CategoryName { get { return "Sprite Atlases"; } }

        public IEnumerator Analyze(ScanContext ctx, CategoryResult result, MemoryReport report)
        {
            var calibration = AtlasCalibration.Load();
            var spriteSizes = new Dictionary<string, Vector2Int>();

            for (int a = 0; a < ctx.AtlasPaths.Count; a++)
            {
                string atlasPath = ctx.AtlasPaths[a];
                var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(atlasPath);
                if (atlas == null) continue;
                if (!ctx.AtlasContents.TryGetValue(atlasPath, out var contents)) continue;

                var packing = atlas.GetPackingSettings();
                var texSettings = atlas.GetTextureSettings();
                var android = atlas.GetPlatformSettings("Android");
                var ios = atlas.GetPlatformSettings("iPhone");

                // ── measure ──────────────────────────────────────────────────
                long packedArea = 0, paddedArea = 0;
                int pad = Mathf.Max(0, packing.padding);
                foreach (var sp in contents)
                {
                    var size = SpriteSize(ctx, sp, spriteSizes);
                    packedArea += (long)size.x * size.y;
                    paddedArea += (long)(size.x + pad) * (size.y + pad);
                }

                int maxSize = android.overridden && android.maxTextureSize > 0
                    ? android.maxTextureSize
                    : 2048;
                var fmt = android.overridden ? android.format : TextureImporterFormat.Automatic;
                bool potRequired = FormatNeedsPot(fmt, calibration);
                bool squareRequired = FormatNeedsSquarePot(fmt, calibration);

                EstimatePages(paddedArea, maxSize, potRequired, squareRequired,
                    out int pageW, out int pageH, out int pageCount);

                long pageArea = (long)pageW * pageH * pageCount;
                float multiplier = packedArea > 0 ? (float)pageArea / packedArea : 1f;
                float efficiency = pageArea > 0 ? 100f * packedArea / pageArea : 0f;
                float bpp = android.overridden && fmt != TextureImporterFormat.Automatic
                    ? TextureAnalyzer.BitsPerPixel(fmt) : 32f;
                long residentBytes = (long)(pageArea * bpp / 8f);

                report.atlases.Add(new AtlasStat
                {
                    path = atlasPath,
                    spriteCount = contents.Count,
                    packedAreaPx = packedArea,
                    estPageWidth = pageW,
                    estPageHeight = pageH,
                    estPageCount = pageCount,
                    paddingMultiplier = (float)Math.Round(multiplier, 2),
                    efficiencyPct = (float)Math.Round(efficiency, 1),
                    format = android.overridden ? fmt.ToString() : "DEFAULT (uncompressed)",
                    estResidentBytes = residentBytes,
                    calibrated = calibration.verified,
                });

                string oneLiner = string.Format(
                    "{0} sprites packed -> est. {1}x{2}{3} resident ({4:0.#}x, {5}, wasting {6})",
                    contents.Count, pageW, pageH,
                    pageCount > 1 ? " x" + pageCount + " pages" : "",
                    multiplier, TextureAnalyzer.Fmt(residentBytes),
                    TextureAnalyzer.Fmt((long)(residentBytes * (1f - packedArea / (float)Math.Max(1, pageArea)))));

                // ── ATL-010 — no platform override, inherits uncompressed default
                if (!android.overridden && !ios.overridden)
                    result.findings.Add(Finding.Make("ATL-010", Severity.High, atlasPath,
                        "Atlas has no platform override — the generated pages inherit the uncompressed default. " + oneLiner,
                        "Add an Android override with ASTC.", (long)(residentBytes * 0.75f), 0, "S"));

                // ── ATL-011 — Read/Write on the atlas
                if (texSettings.readable)
                    result.findings.Add(Finding.Make("ATL-011", Severity.High, atlasPath,
                        "Read/Write is on for the whole atlas — every generated page keeps a CPU copy.",
                        "Untick Read/Write on the atlas.", residentBytes, 0, "S"));

                // ── ATL-012 — mipmaps on a UI atlas
                if (texSettings.generateMipMaps)
                    result.findings.Add(Finding.Make("ATL-012", Severity.Medium, atlasPath,
                        "Mipmaps on a sprite atlas — +33% per page, and UI sprites never minify.",
                        "Untick Generate Mip Maps on the atlas.", residentBytes / 4, 0, "S"));

                // ── ATL-003 — packing efficiency below ~65%
                if (contents.Count > 0 && efficiency < 65f)
                    result.findings.Add(Finding.Make("ATL-003", Severity.High, atlasPath,
                        string.Format("Packing efficiency ~{0:0}% — a mostly-empty page stays resident. {1}", efficiency, oneLiner),
                        "Consolidate with another atlas of the same lifetime, or drop maxSize.",
                        (long)(residentBytes * (1f - efficiency / 100f)), 0, "M"));

                // ── ATL-005 — padding waste ratio (scaled severity)
                if (multiplier > 1.5f)
                {
                    var sev = multiplier > 3f ? Severity.High : Severity.Medium;
                    result.findings.Add(Finding.Make("ATL-005", sev, atlasPath,
                        oneLiner + (calibration.verified ? "" : " [uncalibrated estimate — run Calibration in the footer for exact numbers]"),
                        "Trim contents or adjust maxSize so the page lands on a tighter size.",
                        (long)(residentBytes * (multiplier - 1f) / multiplier), 0, "M"));
                }

                // ── ATL-016 — within ~10% of a POT boundary: cheapest win in the report
                if (potRequired && packedArea > 0)
                {
                    long half = (long)pageW * pageH / 2;
                    if (paddedArea <= half * 110 / 100 && paddedArea > half * 90 / 100 && pageCount == 1)
                        result.findings.Add(Finding.Make("ATL-016", Severity.High, atlasPath,
                            string.Format("Contents sit just over half the {0}x{1} page — trimming a few sprites drops it a whole size class for a 2x saving.", pageW, pageH),
                            "Move the least-used sprites out and repack; usually ten minutes of work.",
                            residentBytes / 2, 0, "S"));
                }

                // ── ATL-017 — PVRTC on iOS with a non-square page estimate
                if (ios.overridden && IsPvrtc(ios.format) && pageW != pageH)
                    result.findings.Add(Finding.Make("ATL-017", Severity.High, atlasPath,
                        string.Format("PVRTC forces square power-of-two pages — a {0}x{1} atlas pads to {2}x{2}.", pageW, pageH, Mathf.Max(pageW, pageH)),
                        "Switch iOS to ASTC; PVRTC only matters for pre-A8 devices nobody ships to.",
                        0, 0, "S"));

                // ── ATL-018 — would fit one page at a smaller maxSize but spills to two
                if (pageCount == 2 && paddedArea < (long)maxSize * maxSize)
                    result.findings.Add(Finding.Make("ATL-018", Severity.Medium, atlasPath,
                        "Contents spill to a second page but the total area fits one — usually one oversized sprite is forcing the split.",
                        "Find the biggest sprite and consider downsizing it or moving it out.",
                        0, 0, "M"));

                // ── ATL-009 — tight packing off with sliced-sprite caveat
                if (!packing.enableTightPacking && contents.Count >= 8)
                {
                    bool anySliced = contents.Any(sp => HasSpriteBorder(sp));
                    if (!anySliced)
                        result.findings.Add(Finding.Make("ATL-009", Severity.Medium, atlasPath,
                            "Tight packing is off and no sprite here is sliced — irregular sprites are packing as full rects.",
                            "Enable Tight Packing. (Skip this if you add 9-sliced sprites later — they need full rects.)",
                            0, 0, "S"));
                }

                // ── ATL-014 — Include in Build off with no late binding in code
                if (!atlas.IsIncludeInBuild() && !ctx.AllCodeLower.Contains("atlasrequested"))
                    result.findings.Add(Finding.Make("ATL-014", Severity.High, atlasPath,
                        "Include in Build is off and nothing subscribes to SpriteAtlasManager.atlasRequested — these sprites resolve to nothing at runtime. (Correctness, not memory.)",
                        "Tick Include in Build, or add the late-binding handler.", 0, 0, "S"));

                // ── ATL-004 — mixed-lifetime atlas: the rule that matters most
                CheckMixedLifetime(ctx, atlasPath, contents, residentBytes, result);

                // ── ATL-002 — sprite atlased AND referenced directly: the silent one
                foreach (var sp in contents)
                {
                    bool direct = ctx.ReferencedByScenes.ContainsKey(sp) || ctx.ReferencedByPrefabs.ContainsKey(sp);
                    bool viaResources = sp.Contains("/Resources/");
                    if (viaResources && direct)
                        result.findings.Add(Finding.Make("ATL-002", Severity.High, sp,
                            "Sprite is packed in " + Path.GetFileName(atlasPath) + " AND sits under Resources/ where code can load it standalone — the project pays for both copies with no visible symptom.",
                            "Move it out of Resources or out of the atlas; one or the other.",
                            ctx.TextureEstimates.TryGetValue(sp, out long tb) ? tb : 0, 0, "S"));
                }

                // ── ATL-015 — sprite in an atlas that nothing references
                foreach (var sp in contents)
                {
                    if (!ctx.ReferencedByScenes.ContainsKey(sp) && !ctx.ReferencedByPrefabs.ContainsKey(sp)
                        && !ctx.AllCodeLower.Contains(Path.GetFileNameWithoutExtension(sp).ToLowerInvariant()))
                        result.findings.Add(Finding.Make("ATL-015", Severity.Low, sp,
                            "Packed into " + Path.GetFileName(atlasPath) + " but no scene, prefab or script references it — it still costs page area.",
                            "Remove it from the atlas.", 0, 0, "S"));
                }

                yield return null;
            }

            // ── ATL-001 — sprite present in two or more atlases
            foreach (var kv in ctx.SpriteToAtlases)
            {
                if (kv.Value.Count < 2) continue;
                result.findings.Add(Finding.Make("ATL-001", Severity.High, kv.Key,
                    "Packed into " + kv.Value.Count + " atlases (" + string.Join(", ", kv.Value.Select(Path.GetFileName)) + ") — it's duplicated in memory whenever both pages are resident.",
                    "Keep it in one atlas.",
                    ctx.TextureEstimates.TryGetValue(kv.Key, out long b) ? b : 0, 0, "S"));
            }
            yield return null;

            // ── ATL-013 — loose sprite clusters (absorbs TEX-007)
            FindLooseClusters(ctx, result);
            yield return null;
        }

        // ── ATL-004: group each atlas's sprites by referencing scene; flag pages
        // whose sprites span 3+ scenes with little overlap.
        private static void CheckMixedLifetime(ScanContext ctx, string atlasPath,
            List<string> contents, long residentBytes, CategoryResult result)
        {
            var sceneBuckets = new Dictionary<string, int>();
            int referencedSprites = 0;
            foreach (var sp in contents)
            {
                if (!ctx.ReferencedByScenes.TryGetValue(sp, out var scenes) || scenes.Count == 0) continue;
                referencedSprites++;
                foreach (var s in scenes)
                {
                    sceneBuckets.TryGetValue(s, out int n);
                    sceneBuckets[s] = n + 1;
                }
            }
            if (referencedSprites < 6 || sceneBuckets.Count < 3) return;

            // "little overlap": no single scene uses the majority of the sprites
            int maxInOneScene = sceneBuckets.Values.Max();
            if (maxInOneScene < referencedSprites * 0.6f)
            {
                result.findings.Add(Finding.Make("ATL-004", Severity.High, atlasPath,
                    string.Format("Mixed-lifetime atlas: its sprites are used across {0} scenes with little overlap, so one rarely-shown sprite keeps the whole ~{1} page resident all session.",
                        sceneBuckets.Count, TextureAnalyzer.Fmt(residentBytes)),
                    "Split the atlas by usage lifetime (per scene or per feature), not by folder.",
                    residentBytes / 2, 0, "M"));
            }
        }

        // ── ATL-013: 10+ unatlased sprites under one folder, referenced by the same scene
        private static void FindLooseClusters(ScanContext ctx, CategoryResult result)
        {
            var byFolder = new Dictionary<string, List<string>>();
            foreach (var tex in ctx.TexturePaths)
            {
                if (ctx.SpriteToAtlases.ContainsKey(tex)) continue;
                var importer = AssetImporter.GetAtPath(tex) as TextureImporter;
                if (importer == null || importer.textureType != TextureImporterType.Sprite) continue;
                if (!ctx.ReferencedByScenes.ContainsKey(tex)) continue;
                string folder = Path.GetDirectoryName(tex);
                if (!byFolder.TryGetValue(folder, out var list)) byFolder[folder] = list = new List<string>();
                list.Add(tex);
            }
            foreach (var kv in byFolder)
            {
                if (kv.Value.Count < 10) continue;
                // same scene? intersect the referencing scene sets
                HashSet<string> common = null;
                foreach (var sp in kv.Value)
                {
                    var scenes = ctx.ReferencedByScenes[sp];
                    if (common == null) common = new HashSet<string>(scenes);
                    else common.IntersectWith(scenes);
                }
                if (common == null || common.Count == 0) continue;
                result.findings.Add(Finding.Make("ATL-013", Severity.Medium, kv.Key,
                    string.Format("{0} loose sprites under {1}, all referenced by {2} — unatlased, so each is its own texture with its own overhead.",
                        kv.Value.Count, kv.Key, Path.GetFileName(common.First())),
                    "Pack them into one atlas scoped to that scene's lifetime.", 0, 0, "M"));
            }
        }

        // ── estimate helpers ──────────────────────────────────────────────────

        private static Vector2Int SpriteSize(ScanContext ctx, string path, Dictionary<string, Vector2Int> cache)
        {
            if (cache.TryGetValue(path, out var v)) return v;
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            v = tex != null ? new Vector2Int(tex.width, tex.height) : Vector2Int.zero;
            cache[path] = v;
            return v;
        }

        private static bool HasSpriteBorder(string path)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            return importer != null && importer.spriteBorder != Vector4.zero;
        }

        private static bool FormatNeedsPot(TextureImporterFormat f, AtlasCalibration cal)
        {
            if (IsPvrtc(f)) return true;
            if (f == TextureImporterFormat.ETC2_RGBA8 || f == TextureImporterFormat.ETC2_RGB4
                || f == TextureImporterFormat.ETC_RGB4)
                return cal.etc2RequiresPotPerAxis;
            // ASTC and uncompressed only round to block/4 — treat as no POT constraint
            return false;
        }

        private static bool FormatNeedsSquarePot(TextureImporterFormat f, AtlasCalibration cal)
        {
            return IsPvrtc(f) && cal.pvrtcRequiresSquarePot;
        }

        private static bool IsPvrtc(TextureImporterFormat f)
        {
            return f == TextureImporterFormat.PVRTC_RGBA4 || f == TextureImporterFormat.PVRTC_RGB4
                || f == TextureImporterFormat.PVRTC_RGBA2 || f == TextureImporterFormat.PVRTC_RGB2;
        }

        // Estimate generated page dimensions: assume the packer fills ~85% of a page,
        // grows the page to the smallest size that fits, and only spills to more
        // pages past maxSize. Good enough to rank atlases against each other —
        // the deep scan (v1.1) replaces this with real packed pages.
        private static void EstimatePages(long paddedArea, int maxSize,
            bool potRequired, bool squareRequired,
            out int pageW, out int pageH, out int pageCount)
        {
            const float fill = 0.85f;
            long needed = (long)(paddedArea / fill);
            pageCount = 1;

            long maxPageArea = (long)maxSize * maxSize;
            if (needed > maxPageArea)
            {
                pageCount = (int)((needed + maxPageArea - 1) / maxPageArea);
                pageW = maxSize; pageH = maxSize;
                return;
            }

            if (squareRequired)
            {
                int s = 32;
                while ((long)s * s < needed && s < maxSize) s *= 2;
                pageW = s; pageH = s;
                return;
            }

            if (potRequired)
            {
                // smallest POT rectangle (w >= h, w <= 2h to stay near-square) that fits
                int bestW = maxSize, bestH = maxSize;
                long bestArea = long.MaxValue;
                for (int w = 32; w <= maxSize; w *= 2)
                {
                    for (int h = 32; h <= maxSize; h *= 2)
                    {
                        long area = (long)w * h;
                        if (area >= needed && area < bestArea)
                        {
                            bestArea = area; bestW = w; bestH = h;
                        }
                    }
                }
                pageW = bestW; pageH = bestH;
                return;
            }

            // block-rounding formats: page tracks content, round each axis up to 4
            int side = Mathf.CeilToInt(Mathf.Sqrt(needed));
            side = ((side + 3) / 4) * 4;
            if (side > maxSize) side = maxSize;
            pageW = side; pageH = Mathf.Min(maxSize, ((Mathf.CeilToInt((float)needed / side) + 3) / 4) * 4);
            if (pageH < 32) pageH = 32;
        }

        // ── Calibration pack — the spec's build task, exposed as a utility. Packs
        // a throwaway atlas at a deliberately awkward size per format and records
        // the resulting page behaviour for THIS Unity version.
        public static AtlasCalibration RunCalibration()
        {
            var cal = new AtlasCalibration { unityVersion = Application.unityVersion };
            const string dir = "Assets/MemoryShieldCalibration~Temp";
            try
            {
                Directory.CreateDirectory(dir);
                // awkward source: 381x300 (x4 sprites -> ~1524x300 packed)
                string texPath = dir + "/ms_cal_tex.png";
                var tex = new Texture2D(381, 300, TextureFormat.RGBA32, false);
                var px = new Color32[381 * 300];
                for (int i = 0; i < px.Length; i++) px[i] = new Color32((byte)(i % 255), 128, 64, 255);
                tex.SetPixels32(px); tex.Apply();
                File.WriteAllBytes(texPath, tex.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(tex);
                AssetDatabase.ImportAsset(texPath);
                var ti = AssetImporter.GetAtPath(texPath) as TextureImporter;
                if (ti != null)
                {
                    ti.textureType = TextureImporterType.Sprite;
                    ti.SaveAndReimport();
                }

                cal.etc2RequiresPotPerAxis = MeasurePotBehaviour(dir, texPath, TextureImporterFormat.ETC2_RGBA8);
                cal.astcRoundsToBlock = !MeasurePotBehaviour(dir, texPath, TextureImporterFormat.ASTC_6x6);
                cal.verified = true;
                cal.Save();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[MemoryShield] Calibration failed, keeping defaults: " + e.Message);
            }
            finally
            {
                if (AssetDatabase.IsValidFolder(dir)) AssetDatabase.DeleteAsset(dir);
            }
            return cal;
        }

        // Packs one throwaway atlas with the given format and reports whether the
        // generated page snapped to POT rather than tracking content size.
        private static bool MeasurePotBehaviour(string dir, string texPath, TextureImporterFormat format)
        {
            string atlasPath = dir + "/ms_cal_" + format + ".spriteatlas";
            var atlas = new SpriteAtlas();
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(texPath);
            if (sprite != null) atlas.Add(new UnityEngine.Object[] { sprite });
            atlas.SetPlatformSettings(new TextureImporterPlatformSettings
            {
                name = "Android", overridden = true, format = format, maxTextureSize = 2048,
            });
            AssetDatabase.CreateAsset(atlas, atlasPath);
            SpriteAtlasUtility.PackAtlases(new[] { atlas }, EditorUserBuildSettings.activeBuildTarget);

            // GetPreviewTextures is internal; reflection is the documented workaround.
            var mi = typeof(SpriteAtlasExtensions).GetMethod("GetPreviewTextures",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (mi != null)
            {
                var pages = mi.Invoke(null, new object[] { atlas }) as Texture2D[];
                if (pages != null && pages.Length > 0)
                {
                    int w = pages[0].width, h = pages[0].height;
                    bool potW = (w & (w - 1)) == 0, potH = (h & (h - 1)) == 0;
                    // content is 381x300; if the page came back POT on both axes
                    // and meaningfully bigger than content, the format forces POT
                    return potW && potH && (w >= 512 || h >= 512);
                }
            }
            return format != TextureImporterFormat.ASTC_6x6;   // fall back to defaults
        }
    }
}

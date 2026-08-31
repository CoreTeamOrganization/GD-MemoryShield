// Editor/Analyzers/SceneAnalyzer.cs
// SCN rules. Requires text serialization — on binary or mixed we emit SCN-000
// BLOCKER and skip rather than guessing.

using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GameDistrict.MemoryShield.Core;
using GameDistrict.MemoryShield.Model;
using UnityEngine;

namespace GameDistrict.MemoryShield.Analyzers
{
    public class SceneAnalyzer : IMemoryAnalyzer
    {
        public string CategoryName { get { return "Scenes"; } }

        private const int AssetRefThreshold = 400;   // SCN-001 default
        private const long MaxSceneFileBytes = 200L * 1024 * 1024;

        private static readonly Regex GameObjectRx = new Regex(@"^--- !u!1 &", RegexOptions.Multiline);
        private static readonly Regex InactiveRx = new Regex(@"m_IsActive:\s*0\b");
        private static readonly Regex CanvasRx = new Regex(@"^--- !u!223 &", RegexOptions.Multiline);
        private static readonly Regex HdrRx = new Regex(@"m_HDR:\s*1\b");
        private static readonly Regex MsaaRx = new Regex(@"m_AllowMSAA:\s*1\b");
        private static readonly Regex LightmapRx = new Regex(@"m_Lightmaps:\s*\n\s*-", RegexOptions.Multiline);
        private static readonly Regex ReflectionProbeRx = new Regex(@"^--- !u!215 &", RegexOptions.Multiline);

        public IEnumerator Analyze(ScanContext ctx, CategoryResult result, MemoryReport report)
        {
            if (!ctx.TextSerialization)
            {
                result.State = CategoryState.Skipped;
                result.stateNote = "Asset serialization is not Force Text — scene analysis skipped.";
                result.findings.Add(Finding.Make("SCN-000", Severity.Blocker, "",
                    "Scenes can't be analyzed on binary/mixed serialization. Set Edit > Project Settings > Editor > Asset Serialization > Force Text and rescan.",
                    "Force Text also makes scene diffs reviewable — it's worth doing regardless.", 0, 0, "S"));
                yield break;
            }

            bool is2D = LooksLike2DProject(ctx);
            var perSceneStats = new List<SceneStat>();
            long totalRefs = 0;

            for (int i = 0; i < ctx.ScenePaths.Count; i++)
            {
                string scenePath = ctx.ScenePaths[i];
                if (!ctx.SceneDeps.TryGetValue(scenePath, out var deps)) continue;

                long estBytes = 0;
                foreach (var d in deps)
                {
                    if (ctx.TextureEstimates.TryGetValue(d, out long tb)) estBytes += tb;
                    else if (ctx.AudioEstimates.TryGetValue(d, out long ab)) estBytes += ab;
                }

                string yaml = "";
                try
                {
                    var info = new FileInfo(scenePath);
                    if (info.Exists && info.Length < MaxSceneFileBytes)
                        yaml = File.ReadAllText(scenePath);
                }
                catch (IOException) { }

                int goCount = yaml.Length > 0 ? GameObjectRx.Matches(yaml).Count : 0;
                int disabledCount = yaml.Length > 0 ? InactiveRx.Matches(yaml).Count : 0;
                int canvasCount = yaml.Length > 0 ? CanvasRx.Matches(yaml).Count : 0;

                perSceneStats.Add(new SceneStat
                {
                    path = scenePath,
                    assetRefCount = deps.Count,
                    estResidentBytes = estBytes,
                    gameObjectCount = goCount,
                    disabledObjectCount = disabledCount,
                });
                totalRefs += deps.Count;

                // SCN-001 — too many distinct assets referenced
                if (deps.Count > AssetRefThreshold)
                    result.findings.Add(Finding.Make("SCN-001", Severity.High, scenePath,
                        string.Format("References {0} distinct assets (threshold {1}) — everything a scene references loads with it, ~{2} estimated here.",
                            deps.Count, AssetRefThreshold, TextureAnalyzer.Fmt(estBytes)),
                        "Move on-demand content behind Addressables or additive scenes.",
                        0, 0, "L"));

                // SCN-002 — disabled objects still load their assets. Commonly misunderstood.
                if (disabledCount > 20)
                    result.findings.Add(Finding.Make("SCN-002", Severity.High, scenePath,
                        string.Format("{0} disabled GameObjects in the hierarchy — disabling skips Update, not loading. Their textures, clips and meshes are all resident.",
                            disabledCount),
                        "Instantiate rarely-used objects on demand, or move them to an additive scene.",
                        0, 0, "M"));

                // SCN-003 — many full-screen UI panels instantiated at load
                if (canvasCount >= 5 && disabledCount > 10)
                    result.findings.Add(Finding.Make("SCN-003", Severity.High, scenePath,
                        string.Format("{0} Canvases with {1} disabled objects — this looks like every popup pre-instantiated at load, each with its sprites resident.",
                            canvasCount, disabledCount),
                        "Instantiate popups from prefabs when opened; destroy on close.", 0, 0, "M"));

                // SCN-005 — lightmaps or reflection probes in a 2D project
                if (is2D && yaml.Length > 0 &&
                    (LightmapRx.IsMatch(yaml) || ReflectionProbeRx.Matches(yaml).Count > 0))
                    result.findings.Add(Finding.Make("SCN-005", Severity.Medium, scenePath,
                        "Baked lightmaps or reflection probes in what looks like a 2D project — baked data loads with the scene and lights nothing.",
                        "Clear baked data (Lighting window > Clear Baked Data) and turn off auto-generate.", 0, 0, "S"));

                // SCN-006 — HDR/MSAA camera in a 2D casual project
                if (is2D && yaml.Length > 0 && (HdrRx.IsMatch(yaml) || MsaaRx.IsMatch(yaml)))
                    result.findings.Add(Finding.Make("SCN-006", Severity.Medium, scenePath,
                        "A camera here has HDR or MSAA on — in a 2D casual game that's an extra full-screen render target for no visible gain.",
                        "Untick HDR and MSAA on the camera.", 0, 0, "S"));

                yield return null;
            }

            // SCN-004 — mega-scene: one scene holds over 60% of scene-referenced assets
            if (perSceneStats.Count > 1 && totalRefs > 0)
            {
                foreach (var s in perSceneStats)
                {
                    if (s.assetRefCount > totalRefs * 0.6f)
                        result.findings.Add(Finding.Make("SCN-004", Severity.Medium, s.path,
                            string.Format("Holds {0} of the {1} scene-referenced assets in the project — a mega-scene that loads most of the game at once.",
                                s.assetRefCount, totalRefs),
                            "Split by feature into additive scenes.", 0, 0, "L"));
                }
            }
            yield return null;

            // SCN-008 — assets referenced by every scene: shared-group candidates (INFO)
            if (ctx.ScenePaths.Count >= 3)
            {
                HashSet<string> common = null;
                foreach (var sp in ctx.ScenePaths)
                {
                    if (!ctx.SceneDeps.TryGetValue(sp, out var deps)) continue;
                    if (common == null) common = new HashSet<string>(deps);
                    else common.IntersectWith(deps);
                }
                if (common != null)
                {
                    var interesting = common.Where(p =>
                        ctx.TextureEstimates.ContainsKey(p) || ctx.AudioEstimates.ContainsKey(p)).ToList();
                    if (interesting.Count >= 5)
                        result.findings.Add(Finding.Make("SCN-008", Severity.Info, "",
                            string.Format("{0} assets are referenced by every scene — natural candidates for one shared bundle/group instead of duplicating into each scene's load.",
                                interesting.Count),
                            "Group them under a shared Addressables group.", 0, 0, "M"));
                }
            }

            report.scenes = perSceneStats.OrderByDescending(s => s.estResidentBytes).ToList();

            // SCN-009 — per-scene heaviness, one row per scene so the weight is
            // visible in the window, not just in the exported table. Severity by
            // estimated resident load: >250MB HIGH, >120MB MEDIUM, else INFO.
            foreach (var s in report.scenes.Take(15))
            {
                var sev = s.estResidentBytes > 250L * 1024 * 1024 ? Severity.High
                        : s.estResidentBytes > 120L * 1024 * 1024 ? Severity.Medium
                        : Severity.Info;
                result.findings.Add(Finding.Make("SCN-009", sev, s.path,
                    string.Format("Loads ~{0} of texture/audio (estimate) — {1} asset refs, {2} GameObjects, {3} disabled.{4}",
                        TextureAnalyzer.Fmt(s.estResidentBytes), s.assetRefCount,
                        s.gameObjectCount, s.disabledObjectCount,
                        sev == Severity.High ? " That alone is most of a 2GB device's app budget." : ""),
                    sev == Severity.Info ? "" : "Move on-demand content behind Addressables; check the disabled objects first.",
                    0, 0, sev == Severity.Info ? "" : "M"));
            }
            yield return null;
        }

        // Heuristic: if the project's textures are overwhelmingly sprites and there
        // are almost no 3D models, treat it as 2D for the 2D-only rules.
        private static bool LooksLike2DProject(ScanContext ctx)
        {
            return ctx.ModelPaths.Count < 5 && ctx.TexturePaths.Count > 20;
        }
    }
}

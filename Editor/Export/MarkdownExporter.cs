// Editor/Export/MarkdownExporter.cs
// Report sections per spec §6. Writing style: casual, direct, the way a
// colleague would say it. Severity wording stays neutral — a HIGH is a HIGH.

using System.Linq;
using System.Text;
using GameDistrict.MemoryShield.Analyzers;
using GameDistrict.MemoryShield.Model;

namespace GameDistrict.MemoryShield.Export
{
    public static class MarkdownExporter
    {
        // Printed verbatim ahead of the Persistent Root Map, per spec §4.7.
        public const string RetentionFraming =
            "Singleton *count* is not a memory problem — an instance is a few hundred bytes. " +
            "What costs memory is what a persistent object *holds*, and for how long. " +
            "A `DontDestroyOnLoad` manager caching prefabs, sprites or audio clips becomes a GC root " +
            "that survives every scene change, and `Resources.UnloadUnusedAssets()` will not free " +
            "anything reachable from it.";

        public static string Export(MemoryReport r)
        {
            var sb = new StringBuilder(64 * 1024);

            // 1. Cover
            sb.AppendLine("# MemoryShield Report — " + r.projectName);
            sb.AppendLine();
            sb.AppendLine("| | |");
            sb.AppendLine("|---|---|");
            sb.AppendLine("| Score | **" + r.score.ToString("0") + " / 100** |");
            sb.AppendLine("| Unity | " + r.unityVersion + " |");
            sb.AppendLine("| Scanned | " + r.scanDateUtc + " |");
            sb.AppendLine("| Tool | MemoryShield " + r.toolVersion + " |");
            sb.AppendLine();
            sb.AppendLine("> " + r.verdict);
            sb.AppendLine();

            // 2. Executive summary
            sb.AppendLine("## Executive summary");
            sb.AppendLine();
            sb.AppendLine(r.executiveSummary);
            sb.AppendLine();

            // 3. Score table
            sb.AppendLine("## Scores");
            sb.AppendLine();
            sb.AppendLine("| Category | Subscore | High | Medium | Low | State |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var c in r.categories)
            {
                string state = c.State == CategoryState.Complete ? "complete" :
                    (c.State == CategoryState.Incomplete ? "INCOMPLETE — " + c.stateNote : "SKIPPED — " + c.stateNote);
                sb.AppendLine(string.Format("| {0} | {1:0} | {2} | {3} | {4} | {5} |",
                    c.category, c.subscore,
                    c.CountOf(Severity.High) + c.CountOf(Severity.Blocker),
                    c.CountOf(Severity.Medium), c.CountOf(Severity.Low), state));
            }
            sb.AppendLine();

            // 4. Top 10 issues by impact
            sb.AppendLine("## Top 10 issues by impact");
            sb.AppendLine();
            if (r.topIssues.Count == 0) sb.AppendLine("Nothing made the cut. Good sign.");
            else
            {
                sb.AppendLine("| # | Issue | Where | Est. recoverable | Fix | Effort |");
                sb.AppendLine("|---|---|---|---|---|---|");
                for (int i = 0; i < r.topIssues.Count; i++)
                {
                    var t = r.topIssues[i];
                    sb.AppendLine(string.Format("| {0} | {1} — {2} | `{3}` | {4} | {5} | {6} |",
                        i + 1, t.id, Trunc(t.message, 140), t.path,
                        t.estimatedBytes > 0 ? TextureAnalyzer.Fmt(t.estimatedBytes) : "—",
                        Trunc(t.fix, 100), t.effort));
                }
            }
            sb.AppendLine();

            // 5. Persistent Root Map
            sb.AppendLine("## Persistent Root Map");
            sb.AppendLine();
            sb.AppendLine(RetentionFraming);
            sb.AppendLine();
            if (r.persistentRoots.Count == 0)
                sb.AppendLine("No persistent roots found holding asset references.");
            else
            {
                sb.AppendLine("| Root | Why persistent | Guarded | Prefabs | Asset-typed fields | Est. pinned |");
                sb.AppendLine("|---|---|---|---|---|---|");
                foreach (var p in r.persistentRoots)
                    sb.AppendLine(string.Format("| `{0}` ({1}:{2}) | {3} | {4} | {5} | {6} | {7} |",
                        p.typeName, p.scriptPath, p.line, p.reason,
                        p.singleton ? (p.duplicateGuard ? "yes" : "**no**") : "n/a",
                        p.referencingPrefabs > 0 ? p.referencingPrefabs.ToString() : "—",
                        p.heldAssetFields.Count > 0 ? string.Join("; ", p.heldAssetFields) : "—",
                        p.estTransitiveBytes > 0 ? "~" + TextureAnalyzer.Fmt(p.estTransitiveBytes) : "—"));
                sb.AppendLine();
                sb.AppendLine("\"Guarded\" = the singleton destroys a duplicate copy of itself in Awake. " +
                    "\"Prefabs\" = prefabs referencing the script; their texture/audio dependencies feed the pinned estimate.");
            }
            sb.AppendLine();

            // 6. Heaviest assets
            sb.AppendLine("## Heaviest assets (estimates — atlas packing and streaming not included)");
            sb.AppendLine();
            sb.AppendLine("### Top 20 textures");
            sb.AppendLine();
            sb.AppendLine("| Texture | Size | Format | R/W | Mips | Est. bytes |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var t in r.topTextures)
                sb.AppendLine(string.Format("| `{0}` | {1}x{2} | {3} | {4} | {5} | {6} |",
                    t.path, t.width, t.height, t.format,
                    t.readable ? "on" : "", t.mipmaps ? "on" : "",
                    TextureAnalyzer.Fmt(t.estimatedBytes)));
            sb.AppendLine();
            sb.AppendLine("### Top 10 audio clips");
            sb.AppendLine();
            sb.AppendLine("| Clip | Length | Load type | Est. resident |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var a in r.topAudio)
                sb.AppendLine(string.Format("| `{0}` | {1:0.#}s {2}Hz x{3} | {4} | {5} |",
                    a.path, a.lengthSeconds, a.frequency, a.channels, a.loadType,
                    TextureAnalyzer.Fmt(a.estimatedBytes)));
            sb.AppendLine();

            // 6b. Atlas table
            sb.AppendLine("### Atlases");
            sb.AppendLine();
            if (r.atlases.Count == 0) sb.AppendLine("No sprite atlases in the project.");
            else
            {
                bool calibrated = r.atlases.All(a => a.calibrated);
                if (!calibrated)
                    sb.AppendLine("*Padding numbers below are uncalibrated estimates — run Calibration in the MemoryShield window for exact page behaviour on this Unity version.*");
                sb.AppendLine();
                sb.AppendLine("| Atlas | Sprites | Packed px | Est. page(s) | Multiplier | Efficiency | Format | Est. resident |");
                sb.AppendLine("|---|---|---|---|---|---|---|---|");
                foreach (var a in r.atlases.OrderByDescending(x => x.estResidentBytes))
                    sb.AppendLine(string.Format("| `{0}` | {1} | {2} | {3}x{4}{5} | {6:0.#}x | {7:0}% | {8} | {9} |",
                        a.path, a.spriteCount, a.packedAreaPx,
                        a.estPageWidth, a.estPageHeight,
                        a.estPageCount > 1 ? " x" + a.estPageCount : "",
                        a.paddingMultiplier, a.efficiencyPct, a.format,
                        TextureAnalyzer.Fmt(a.estResidentBytes)));
            }
            sb.AppendLine();

            // 7. Per-scene breakdown
            sb.AppendLine("## Per-scene breakdown");
            sb.AppendLine();
            if (r.scenes.Count == 0) sb.AppendLine("No scene data (see the Scenes category state).");
            else
            {
                sb.AppendLine("| Scene | Asset refs | Est. resident | GameObjects | Disabled |");
                sb.AppendLine("|---|---|---|---|---|");
                foreach (var s in r.scenes)
                    sb.AppendLine(string.Format("| `{0}` | {1} | {2} | {3} | {4} |",
                        s.path, s.assetRefCount, TextureAnalyzer.Fmt(s.estResidentBytes),
                        s.gameObjectCount, s.disabledObjectCount));
            }
            sb.AppendLine();

            // 8. Full findings by category
            sb.AppendLine("## Full findings");
            sb.AppendLine();
            foreach (var c in r.categories)
            {
                sb.AppendLine("### " + c.category + " — " + c.findings.Count + " finding" + (c.findings.Count == 1 ? "" : "s"));
                sb.AppendLine();
                if (c.State != CategoryState.Complete)
                    sb.AppendLine("*" + c.stateNote + "*");
                foreach (var f in c.findings.OrderBy(x => x.severity))
                {
                    string loc = string.IsNullOrEmpty(f.path) ? "" :
                        " — `" + f.path + (f.line > 0 ? ":" + f.line : "") + "`";
                    sb.AppendLine("- **" + f.severityLabel + " " + f.id + "**" + loc);
                    sb.AppendLine("  " + f.message);
                    if (!string.IsNullOrEmpty(f.fix)) sb.AppendLine("  *Fix:* " + f.fix);
                }
                sb.AppendLine();
            }

            // 9. Declared blind spots
            sb.AppendLine("## What this scan does not see");
            sb.AppendLine();
            foreach (var b in r.blindSpots) sb.AppendLine("- " + b);
            sb.AppendLine();

            // 10. Suggested next steps
            sb.AppendLine("## Suggested next steps");
            sb.AppendLine();
            for (int i = 0; i < r.nextSteps.Count; i++)
                sb.AppendLine((i + 1) + ". " + r.nextSteps[i]);
            sb.AppendLine();

            return sb.ToString();
        }

        private static string Trunc(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("|", "\\|").Replace("\n", " ");
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }
    }
}

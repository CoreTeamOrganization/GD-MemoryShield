// Editor/Export/HtmlExporter.cs
// Short-form visual report: one self-contained HTML file, no external assets,
// opens offline anywhere. This is the "hand it to a producer" artifact — the
// Markdown export stays the full findings record.
//
// Chart rules honored here: one metric per chart, single-hue magnitude bars,
// status colors (pass/warn/fail) only where they encode state and always next
// to a visible number, labels in ink rather than series color, thin rounded
// bars on a quiet grid.

using System.Linq;
using System.Text;
using GameDistrict.MemoryShield.Analyzers;
using GameDistrict.MemoryShield.Model;

namespace GameDistrict.MemoryShield.Export
{
    public static class HtmlExporter
    {
        // Builder Notes palette
        private const string Cream = "#EEEDE6";
        private const string Navy = "#0E1A33";
        private const string Gold = "#F4C430";
        private const string Ink = "#3D3D3A";
        private const string Gray = "#6B6B66";
        private const string Taupe = "#D3D1C7";
        private const string Red = "#C0392B";
        private const string Amber = "#C88C14";
        private const string Green = "#6FA76F";

        public static string Export(MemoryReport r)
        {
            var sb = new StringBuilder(64 * 1024);
            sb.Append("<!doctype html><html><head><meta charset=\"utf-8\">");
            sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
            sb.Append("<title>").Append(E(r.projectName)).Append(" — MemoryShield</title><style>");
            sb.Append(Css());
            sb.Append("</style></head><body><div class=\"page\">");

            Header(sb, r);
            StatTiles(sb, r);
            CategoryChart(sb, r);
            TopIssuesChart(sb, r);
            SceneChart(sb, r);
            RootsTable(sb, r);
            AtlasTable(sb, r);
            TexturesTable(sb, r);
            Footer(sb, r);

            sb.Append("</div></body></html>");
            return sb.ToString();
        }

        private static string Css()
        {
            return
"body{margin:0;background:" + Cream + ";color:" + Ink + ";font:14px/1.5 -apple-system,'Segoe UI',Roboto,Helvetica,Arial,sans-serif}" +
".page{max-width:900px;margin:0 auto;padding:36px 28px 60px;border-left:6px solid " + Gold + ";min-height:100vh;box-sizing:border-box}" +
"h1,h2{font-family:Georgia,'Times New Roman',serif;color:" + Navy + ";margin:0}" +
"h1{font-size:30px}h2{font-size:19px;margin:36px 0 4px}" +
".eyebrow{font-size:10px;letter-spacing:1.5px;font-weight:700;color:" + Gray + ";text-transform:uppercase}" +
".sub{color:" + Gray + ";font-size:12px;margin-top:2px}" +
".head{display:flex;justify-content:space-between;align-items:flex-start;gap:16px;border-bottom:1px solid " + Taupe + ";padding-bottom:18px}" +
".grade{font-family:Georgia,serif;font-size:44px;font-weight:700;line-height:1;text-align:center}" +
".verdict{font-family:Georgia,serif;font-style:italic;color:" + Ink + ";margin:14px 0 0;font-size:15px}" +
".tiles{display:flex;gap:10px;margin-top:18px;flex-wrap:wrap}" +
".tile{flex:1;min-width:150px;background:rgba(255,255,255,.55);border:1px solid " + Taupe + ";border-radius:6px;padding:12px 14px}" +
".tile .n{font-family:Georgia,serif;font-size:24px;color:" + Navy + ";font-weight:700}" +
".tile .l{font-size:11px;color:" + Gray + "}" +
".chart{margin-top:10px}" +
".row{display:flex;align-items:center;gap:10px;margin:6px 0}" +
".row .name{width:270px;flex-shrink:0;font-size:12px;color:" + Ink + ";white-space:nowrap;overflow:hidden;text-overflow:ellipsis;text-align:right}" +
".row .track{flex:1;background:rgba(0,0,0,.05);border-radius:4px;height:16px;position:relative}" +
".row .bar{height:16px;border-radius:4px;min-width:2px}" +
".row .val{width:90px;flex-shrink:0;font-size:12px;font-weight:600;color:" + Navy + "}" +
".note{font-size:11px;color:" + Gray + ";margin-top:6px}" +
"table{border-collapse:collapse;width:100%;margin-top:8px;font-size:12px}" +
"th{text-align:left;font-size:10px;letter-spacing:1px;text-transform:uppercase;color:" + Gray + ";border-bottom:1px solid " + Taupe + ";padding:6px 8px}" +
"td{border-bottom:1px solid rgba(211,209,199,.5);padding:6px 8px;vertical-align:top}" +
"td.num{text-align:right;white-space:nowrap;font-weight:600;color:" + Navy + "}" +
"code{background:rgba(0,0,0,.05);padding:1px 5px;border-radius:3px;font-size:11px}" +
".pill{display:inline-block;font-size:10px;font-weight:700;color:#fff;border-radius:9px;padding:2px 9px}" +
".foot{margin-top:44px;border-top:1px solid " + Taupe + ";padding-top:12px;font-size:11px;color:" + Gray + "}";
        }

        private static void Header(StringBuilder sb, MemoryReport r)
        {
            sb.Append("<div class=\"head\"><div>");
            sb.Append("<div class=\"eyebrow\">GD MemoryShield ").Append(E(r.toolVersion)).Append("</div>");
            sb.Append("<h1>").Append(E(r.projectName)).Append("</h1>");
            sb.Append("<div class=\"sub\">Unity ").Append(E(r.unityVersion))
              .Append(" · scanned ").Append(E(r.scanDateUtc)).Append("</div>");
            sb.Append("<div class=\"verdict\">").Append(E(r.verdict)).Append("</div>");
            long recoverable = r.AllFindings().Sum(f => f.estimatedBytes);
            sb.Append("</div><div style=\"text-align:center\">");
            sb.Append("<div class=\"eyebrow\">Score</div>");
            sb.Append("<div class=\"grade\" style=\"color:").Append(ScoreColor(r.score)).Append("\">")
              .Append((int)r.score).Append("</div>");
            sb.Append("<div class=\"sub\">of 100 · ~").Append(TextureAnalyzer.Fmt(recoverable))
              .Append(" recoverable</div>");
            sb.Append("</div></div>");
        }

        private static void StatTiles(StringBuilder sb, MemoryReport r)
        {
            var all = r.AllFindings();
            int high = all.Where(f => f.Sev <= Severity.High).Sum(f => f.instances);
            int med = all.Where(f => f.Sev == Severity.Medium).Sum(f => f.instances);
            long recoverable = all.Sum(f => f.estimatedBytes);

            sb.Append("<div class=\"tiles\">");
            Tile(sb, TextureAnalyzer.Fmt(r.estimatedTotalBytes),
                "est. asset footprint (" + E(r.budgetTier) + " tier ceiling " + TextureAnalyzer.Fmt(r.budgetCeilingBytes) + ")");
            Tile(sb, high + " high · " + med + " medium", "finding instances");
            Tile(sb, "~" + TextureAnalyzer.Fmt(recoverable), "recoverable (rough sum of estimates)");
            sb.Append("</div>");
        }

        private static void Tile(StringBuilder sb, string n, string l)
        {
            sb.Append("<div class=\"tile\"><div class=\"n\">").Append(E(n))
              .Append("</div><div class=\"l\">").Append(E(l)).Append("</div></div>");
        }

        private static void CategoryChart(StringBuilder sb, MemoryReport r)
        {
            sb.Append("<h2>Category scores</h2><div class=\"sub\">out of 100 — deduction per finding instance</div><div class=\"chart\">");
            foreach (var c in r.categories)
            {
                bool ran = c.State == CategoryState.Complete;
                float v = ran ? c.subscore : 0f;
                string color = !ran ? Taupe : v >= 85 ? Green : v >= 55 ? Amber : Red;
                string label = ran ? ((int)v) + " / 100" : c.State.ToString().ToUpperInvariant();
                Bar(sb, c.category, ran ? v : 100f, 100f, color, label,
                    ran ? c.findings.Sum(f => f.instances) + " finding instances" : c.stateNote);
            }
            sb.Append("</div>");
        }

        private static void TopIssuesChart(StringBuilder sb, MemoryReport r)
        {
            var top = r.AllFindings().Where(f => f.estimatedBytes > 0)
                .OrderByDescending(f => f.estimatedBytes).Take(10).ToList();
            if (top.Count == 0) return;
            long max = top[0].estimatedBytes;

            sb.Append("<h2>Biggest recoverable estimates</h2><div class=\"sub\">top 10 findings by estimated MB — fix these first</div><div class=\"chart\">");
            foreach (var f in top)
            {
                string name = f.id + " · " + ShortName(f.path);
                Bar(sb, name, f.estimatedBytes, max, Navy, "~" + TextureAnalyzer.Fmt(f.estimatedBytes),
                    f.path + " — " + f.message);
            }
            sb.Append("</div><div class=\"note\">Estimates ignore atlas packing and streaming — treat as ranking, verify on device.</div>");
        }

        private static void SceneChart(StringBuilder sb, MemoryReport r)
        {
            if (r.scenes.Count == 0) return;
            var top = r.scenes.Take(8).ToList();
            long max = System.Math.Max(1, top.Max(s => s.estResidentBytes));

            sb.Append("<h2>Heaviest scenes</h2><div class=\"sub\">estimated texture+audio resident when loaded</div><div class=\"chart\">");
            foreach (var s in top)
                Bar(sb, ShortName(s.path), s.estResidentBytes, max, Navy,
                    "~" + TextureAnalyzer.Fmt(s.estResidentBytes),
                    s.path + " — " + s.assetRefCount + " asset refs, " + s.gameObjectCount + " GameObjects, " + s.disabledObjectCount + " disabled");
            sb.Append("</div>");
        }

        private static void RootsTable(StringBuilder sb, MemoryReport r)
        {
            if (r.persistentRoots.Count == 0) return;
            sb.Append("<h2>Persistent Root Map</h2><div class=\"sub\">what survives every scene change, and what it pins</div>");
            sb.Append("<table><tr><th>Root</th><th>Why persistent</th><th>Guarded</th><th>Prefabs</th><th style=\"text-align:right\">Est. pinned</th></tr>");
            foreach (var p in r.persistentRoots.Take(10))
            {
                sb.Append("<tr><td><code>").Append(E(p.typeName)).Append("</code></td><td>")
                  .Append(E(p.reason)).Append("</td><td>")
                  .Append(p.singleton ? (p.duplicateGuard ? "yes" : "<b style=\"color:" + Red + "\">no</b>") : "n/a")
                  .Append("</td><td>").Append(p.referencingPrefabs > 0 ? p.referencingPrefabs.ToString() : "—")
                  .Append("</td><td class=\"num\">")
                  .Append(p.estTransitiveBytes > 0 ? "~" + TextureAnalyzer.Fmt(p.estTransitiveBytes) : "—")
                  .Append("</td></tr>");
            }
            sb.Append("</table>");
        }

        private static void AtlasTable(StringBuilder sb, MemoryReport r)
        {
            if (r.atlases.Count == 0) return;
            sb.Append("<h2>Sprite atlases</h2>");
            sb.Append("<table><tr><th>Atlas</th><th>Sprites</th><th>Page (est.)</th><th>Padding ×</th><th>Efficiency</th><th style=\"text-align:right\">Est. resident</th></tr>");
            foreach (var a in r.atlases.OrderByDescending(x => x.estResidentBytes).Take(12))
            {
                string padColor = a.paddingMultiplier > 3f ? Red : a.paddingMultiplier > 1.5f ? Amber : Ink;
                sb.Append("<tr><td>").Append(E(Trail(a.path, 42))).Append("</td><td>").Append(a.spriteCount)
                  .Append("</td><td>").Append(a.estPageWidth).Append("×").Append(a.estPageHeight)
                  .Append(a.estPageCount > 1 ? " ×" + a.estPageCount : "")
                  .Append("</td><td style=\"color:").Append(padColor).Append(";font-weight:600\">")
                  .Append(a.paddingMultiplier.ToString("0.0")).Append("x</td><td>")
                  .Append(a.efficiencyPct.ToString("0")).Append("%</td><td class=\"num\">~")
                  .Append(TextureAnalyzer.Fmt(a.estResidentBytes)).Append("</td></tr>");
            }
            sb.Append("</table>");
            if (r.atlases.Count > 0 && !r.atlases[0].calibrated)
                sb.Append("<div class=\"note\">Padding numbers use default assumptions — run Calibrate Atlas Padding in the tool for this Unity version's real packer behavior.</div>");
        }

        private static void TexturesTable(StringBuilder sb, MemoryReport r)
        {
            if (r.topTextures.Count == 0) return;
            sb.Append("<h2>Heaviest textures</h2>");
            sb.Append("<table><tr><th>Texture</th><th>Size</th><th>Format</th><th style=\"text-align:right\">Est. bytes</th></tr>");
            foreach (var t in r.topTextures.Take(10))
                sb.Append("<tr><td>").Append(E(Trail(t.path, 52))).Append("</td><td>")
                  .Append(t.width).Append("×").Append(t.height).Append("</td><td>")
                  .Append(E(t.format)).Append(t.readable ? " · R/W" : "").Append("</td><td class=\"num\">~")
                  .Append(TextureAnalyzer.Fmt(t.estimatedBytes)).Append("</td></tr>");
            sb.Append("</table>");
        }

        private static void Footer(StringBuilder sb, MemoryReport r)
        {
            sb.Append("<div class=\"foot\"><b>Not measured (by design):</b> ");
            sb.Append(E(string.Join(" · ", r.blindSpots)));
            sb.Append("<br>Full findings with file paths and line numbers: the Markdown/JSON exports. Generated by GD MemoryShield ")
              .Append(E(r.toolVersion)).Append(".</div>");
        }

        // one horizontal bar row. Native title tooltip carries the detail.
        private static void Bar(StringBuilder sb, string name, float value, float max,
                                string color, string label, string tooltip)
        {
            float pct = max <= 0 ? 0 : System.Math.Min(100f, value / max * 100f);
            sb.Append("<div class=\"row\" title=\"").Append(E(tooltip)).Append("\">");
            sb.Append("<div class=\"name\">").Append(E(name)).Append("</div>");
            sb.Append("<div class=\"track\"><div class=\"bar\" style=\"width:")
              .Append(pct.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture))
              .Append("%;background:").Append(color).Append("\"></div></div>");
            sb.Append("<div class=\"val\">").Append(E(label)).Append("</div></div>");
        }

        private static string ScoreColor(float score)
        {
            if (score >= 85f) return Green;
            if (score >= 55f) return Amber;
            return Red;
        }

        // Last two path segments — "Manager/EnemyManager.cs" reads; a mid-string
        // ellipsis doesn't. The full path lives in the row's tooltip.
        private static string ShortName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "(project-wide)";
            var parts = path.Replace('\\', '/').Split('/');
            return parts.Length <= 2 ? path : parts[parts.Length - 2] + "/" + parts[parts.Length - 1];
        }

        private static string Trail(string path, int max)
        {
            if (string.IsNullOrEmpty(path)) return "(project-wide)";
            return path.Length <= max ? path : "…" + path.Substring(path.Length - max);
        }

        private static string E(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }
    }
}

// Editor/Core/ScoreCalculator.cs
// Deduction from 100: HIGH -6 (capped at -30 per category), MEDIUM -2, LOW -0.5,
// INFO 0. Forced maximum grade C for single-issue project killers regardless of
// score — see the spec's §5 list.

using System.Collections.Generic;
using GameDistrict.MemoryShield.Model;

namespace GameDistrict.MemoryShield.Core
{
    public static class ScoreCalculator
    {
        private const float HighDeduction = 6f;
        private const float HighCategoryCapDeduction = 30f;
        private const float MediumDeduction = 2f;
        private const float LowDeduction = 0.5f;

        public static void Score(MemoryReport report)
        {
            float total = 100f;

            foreach (var cat in report.categories)
            {
                float catDeduction = 0f;
                float highDeduction = 0f;
                foreach (var f in cat.findings)
                {
                    // a grouped row stands in for f.instances identical findings —
                    // grouping is presentation, the score stays per-instance honest
                    int n = f.instances < 1 ? 1 : f.instances;
                    switch (f.Sev)
                    {
                        case Severity.Blocker:
                        case Severity.High:
                            highDeduction += HighDeduction * n;
                            break;
                        case Severity.Medium:
                            catDeduction += MediumDeduction * n;
                            break;
                        case Severity.Low:
                            catDeduction += LowDeduction * n;
                            break;
                        default:
                            break;
                    }
                }
                if (highDeduction > HighCategoryCapDeduction)
                    highDeduction = HighCategoryCapDeduction;
                catDeduction += highDeduction;

                cat.subscore = Clamp01to100(100f - catDeduction);
                total -= catDeduction;
            }

            report.score = Clamp01to100(total);
            string grade = GradeFor(report.score);

            // Letter grades stay in the JSON (schema stability, CI gates) but are
            // no longer shown to people — a red F demotivates; recoverable MB and a
            // rising score motivate. The killer cap still bites the stored grade.
            string killer = KillerFor(report);
            if (killer != null && (grade == "A" || grade == "B"))
            {
                grade = "C";
                report.verdict = "One issue needs attention before anything else: " + killer + ".";
            }
            report.grade = grade;

            if (string.IsNullOrEmpty(report.verdict))
                report.verdict = VerdictFor(grade);
        }

        private static float Clamp01to100(float v)
        {
            if (v < 0f) return 0f;
            if (v > 100f) return 100f;
            return v;
        }

        public static string GradeFor(float score)
        {
            if (score >= 85f) return "A";
            if (score >= 70f) return "B";
            if (score >= 55f) return "C";
            if (score >= 40f) return "D";
            return "F";
        }

        // Single-issue project killers that cap the grade at C.
        private static string KillerFor(MemoryReport report)
        {
            var counts = new Dictionary<string, int>();
            int addressableAcquires = 0, addressableReleases = 0;
            float worstAtl005 = 0f;

            foreach (var f in report.AllFindings())
            {
                counts.TryGetValue(f.id, out int n);
                counts[f.id] = n + (f.instances < 1 ? 1 : f.instances);

                if (f.id == "AUD-001" && f.message.Contains("60s+"))
                    counts["AUD-001-60"] = (counts.TryGetValue("AUD-001-60", out int m) ? m : 0) + 1;
                if (f.id == "LOD-BALANCE")
                {
                    // message carries "acquires N : releases M"
                    ParseBalance(f.message, ref addressableAcquires, ref addressableReleases);
                }
                if (f.id == "ATL-005" && f.estimatedBytes > 0)
                {
                    float mult = ExtractMultiplier(f.message);
                    if (mult > worstAtl005) worstAtl005 = mult;
                }
            }

            if (Count(counts, "TEX-001") > 20)
                return "more than 20 Read/Write-enabled textures";
            if (Count(counts, "AUD-001-60") > 0)
                return "a 60s+ clip on Decompress On Load";
            if (addressableAcquires > 0 && addressableReleases < addressableAcquires * 0.5f)
                return "Addressables release ratio under 0.5";
            if (Count(counts, "RET-007") > 5)
                return "more than 5 undestroyed runtime-created native objects";
            if (Count(counts, "SHD-001") > 0)
                return "no shader variant stripping configured";
            if (worstAtl005 > 3f)
                return "an atlas padding to over 3x its packed size";
            if (Count(counts, "ATL-002") > 0)
                return "a sprite loading both atlased and standalone";
            return null;
        }

        private static int Count(Dictionary<string, int> counts, string id)
        {
            return counts.TryGetValue(id, out int n) ? n : 0;
        }

        private static void ParseBalance(string message, ref int acquires, ref int releases)
        {
            // "acquires 47 : releases 12"
            var m = System.Text.RegularExpressions.Regex.Match(
                message, @"acquires\s+(\d+)\s*:\s*releases\s+(\d+)");
            if (m.Success)
            {
                int.TryParse(m.Groups[1].Value, out acquires);
                int.TryParse(m.Groups[2].Value, out releases);
            }
        }

        private static float ExtractMultiplier(string message)
        {
            // the multiplier is printed as "(9.2x, ..." — anchor on the paren so
            // page dimensions like "2048x2048" never match
            var m = System.Text.RegularExpressions.Regex.Match(message, @"\((\d+(?:\.\d+)?)x");
            return m.Success && float.TryParse(m.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : 0f;
        }

        private static string VerdictFor(string grade)
        {
            switch (grade)
            {
                case "A": return "Memory posture is in good shape; keep it that way with a scan per release.";
                case "B": return "Solid overall with a handful of cheap wins left on the table.";
                case "C": return "Real recoverable footprint here; the top issues list is where to start.";
                case "D": return "Memory has not been managed; expect measurable gains from a focused pass.";
                default:  return "Memory needs a dedicated cleanup before the next content push.";
            }
        }
    }
}

// Editor/Core/MemoryScanner.cs
// Orchestrator. Analyzers are independent and run in sequence as an IEnumerator
// so the progress bar updates and the editor stays responsive. Per-analyzer
// timeout of 120s: on timeout the category is marked INCOMPLETE and the report
// says so, rather than silently under-reporting.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GameDistrict.MemoryShield.Analyzers;
using GameDistrict.MemoryShield.Model;
using UnityEditor;
using UnityEngine;

namespace GameDistrict.MemoryShield.Core
{
    public class MemoryScanner
    {
        private const float AnalyzerTimeoutSeconds = 120f;

        public MemoryReport Report { get; private set; }
        public bool Running { get; private set; }
        public string CurrentStep { get; private set; } = "";
        public float Progress { get; private set; }

        public event Action<MemoryReport> Completed;
        public event Action Updated;

        private IEnumerator _routine;

        private static readonly Func<IMemoryAnalyzer>[] AnalyzerFactories =
        {
            () => new TextureAnalyzer(),      // highest value, simplest — validates the pipeline
            () => new SpriteAtlasAnalyzer(),  // sits on TextureAnalyzer's data
            () => new AudioAnalyzer(),
            () => new SceneAnalyzer(),        // needs Texture/Audio estimates for per-scene bytes
            () => new RetentionAnalyzer(),
            () => new UpdateLoopAnalyzer(),   // frame cost, not footprint — declared as such
        };

        public void StartScan(bool fullRescan)
        {
            if (Running) return;
            if (fullRescan) ScanContext.ClearCache();
            Running = true;
            Report = new MemoryReport
            {
                projectName = Application.productName,
                unityVersion = Application.unityVersion,
                scanDateUtc = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            };
            _routine = ScanRoutine();
            EditorApplication.update += Pump;
        }

        public void Cancel()
        {
            if (!Running) return;
            EditorApplication.update -= Pump;
            Running = false;
            CurrentStep = "Cancelled";
            Updated?.Invoke();
        }

        private void Pump()
        {
            if (_routine == null) { Finish(); return; }
            try
            {
                // run a slice of work per editor tick
                var sliceEnd = EditorApplication.timeSinceStartup + 0.03;
                while (EditorApplication.timeSinceStartup < sliceEnd)
                {
                    if (!_routine.MoveNext()) { Finish(); return; }
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[MemoryShield] Scan failed: " + e);
                Finish();
                return;
            }
            Updated?.Invoke();
        }

        private void Finish()
        {
            EditorApplication.update -= Pump;
            _routine = null;
            if (!Running) return;
            Running = false;
            CurrentStep = "Done";
            Progress = 1f;
            if (Report != null) Completed?.Invoke(Report);
            Updated?.Invoke();
        }

        private IEnumerator ScanRoutine()
        {
            var ctx = new ScanContext();
            var contextSteps = ctx.BuildSteps((label, p) => { CurrentStep = label; Progress = p; });
            while (contextSteps.MoveNext()) yield return null;

            float perAnalyzer = 0.38f / AnalyzerFactories.Length;
            for (int i = 0; i < AnalyzerFactories.Length; i++)
            {
                var analyzer = AnalyzerFactories[i]();
                var result = CategoryResult.Make(analyzer.CategoryName);
                CurrentStep = "Analyzing " + analyzer.CategoryName;
                Progress = 0.60f + perAnalyzer * i;

                double started = EditorApplication.timeSinceStartup;
                IEnumerator steps = null;
                try { steps = analyzer.Analyze(ctx, result, Report); }
                catch (Exception e)
                {
                    result.State = CategoryState.Incomplete;
                    result.stateNote = "analyzer crashed: " + e.Message;
                }

                while (steps != null)
                {
                    bool more;
                    try { more = steps.MoveNext(); }
                    catch (Exception e)
                    {
                        result.State = CategoryState.Incomplete;
                        result.stateNote = "analyzer crashed mid-run: " + e.Message;
                        break;
                    }
                    if (!more) break;
                    if (EditorApplication.timeSinceStartup - started > AnalyzerTimeoutSeconds)
                    {
                        result.State = CategoryState.Incomplete;
                        result.stateNote = string.Format(
                            "timed out after {0:0}s — findings are partial", AnalyzerTimeoutSeconds);
                        break;
                    }
                    yield return null;
                }

                result.elapsedSeconds = (float)(EditorApplication.timeSinceStartup - started);
                Report.categories.Add(result);
                yield return null;
            }

            CurrentStep = "Scoring";
            Progress = 0.99f;
            FinalizeReport(ctx, Report);
            yield return null;
        }

        private static void FinalizeReport(ScanContext ctx, MemoryReport report)
        {
            // estimated total (textures + audio) against the budget tier
            long total = ctx.TextureEstimates.Values.Sum() + ctx.AudioEstimates.Values.Sum();
            report.estimatedTotalBytes = total;
            var budget = MemoryBudget.LoadOrCreate();
            var tier = budget.Selected;
            if (tier != null)
            {
                report.budgetTier = tier.name;
                report.budgetCeilingBytes = tier.ceilingBytes;
                report.budgetCalibrated = budget.calibrated;
            }

            ScoreCalculator.Score(report);

            // top 10 issues by estimated impact
            report.topIssues = report.AllFindings()
                .Where(f => f.Sev == Severity.High || f.Sev == Severity.Blocker || f.estimatedBytes > 0)
                .OrderByDescending(f => f.estimatedBytes)
                .ThenBy(f => f.severity)
                .Take(10)
                .Select(f => new TopIssue
                {
                    id = f.id, path = f.path, message = f.message,
                    fix = f.fix, effort = f.effort, estimatedBytes = f.estimatedBytes,
                })
                .ToList();

            report.executiveSummary = BuildExecutiveSummary(report);

            // Declared blind spots — being explicit about what wasn't measured is
            // more credible than a clean bill of health.
            report.blindSpots = new List<string>
            {
                "Native memory held by ad SDKs (AppLovin MAX, IronSource, AdMob) for MREC and video creatives — invisible to any static tool and to the Unity Profiler's managed view.",
                "Native allocations by Firebase, Adjust, AppMetrica, Metica.",
                "Runtime peak, fragmentation, and actual GC behaviour.",
                "Anything gated behind runtime conditions the scanner can't evaluate.",
                "For native plugin isolation, the method is removing plugins one at a time behind Scripting Define Symbols and comparing device snapshots. This tool does not measure it.",
            };

            report.nextSteps = BuildNextSteps(report);
        }

        private static string BuildExecutiveSummary(MemoryReport r)
        {
            int highs = 0, mediums = 0;
            foreach (var c in r.categories) { highs += c.CountOf(Severity.High); mediums += c.CountOf(Severity.Medium); }
            long recoverable = r.topIssues.Sum(t => t.estimatedBytes);

            var worstCat = r.categories.OrderBy(c => c.subscore).FirstOrDefault();
            string biggest = r.topIssues.Count > 0
                ? r.topIssues[0].id + " (" + System.IO.Path.GetFileName(r.topIssues[0].path) + ")"
                : "nothing in particular";

            return string.Format(
                "The project scores {0:0} ({1}) with {2} high and {3} medium findings across {4} categories. " +
                "The weakest category is {5}. The single biggest issue is {6}. " +
                "The top-10 list alone accounts for roughly {7} of estimated recoverable footprint. " +
                "Estimated total asset footprint is {8} against the {9} tier ceiling of {10}{11}.",
                r.score, r.grade, highs, mediums, r.categories.Count,
                worstCat != null ? worstCat.category : "n/a", biggest,
                TextureAnalyzer.Fmt(recoverable),
                TextureAnalyzer.Fmt(r.estimatedTotalBytes),
                r.budgetTier, TextureAnalyzer.Fmt(r.budgetCeilingBytes),
                r.budgetCalibrated ? "" : " (budget not yet calibrated against device captures — treat as relative)");
        }

        private static List<string> BuildNextSteps(MemoryReport r)
        {
            var steps = new List<string>();
            if (r.topIssues.Count > 0)
                steps.Add("Work the Top 10 list top-down — it's sorted by estimated recoverable MB.");
            if (r.persistentRoots.Any(p => p.heldAssetFields.Count > 0))
                steps.Add("Review the Persistent Root Map with whoever owns the managers — decide per field whether it should really survive scene changes.");
            if (r.categories.Any(c => c.State == CategoryState.Skipped))
                steps.Add("Fix the blocked category (see its note) and rescan.");
            steps.Add("Rescan after each fix batch; the score is cheap to re-earn and regressions show immediately.");
            steps.Add("Confirm the biggest wins on a device with the Unity Memory Profiler before and after.");
            return steps;
        }
    }
}

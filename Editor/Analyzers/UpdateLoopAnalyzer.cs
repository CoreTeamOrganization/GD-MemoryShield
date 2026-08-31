// Editor/Analyzers/UpdateLoopAnalyzer.cs
// UPD rules — per-frame cost in Update/FixedUpdate/LateUpdate. This category is
// about frame time and GC pressure, not resident footprint: a scene walk or a
// LINQ allocation repeated 60 times a second is what players feel as stutter.
//
// Estimated-bytes stays 0 on every UPD rule — these cost milliseconds and GC
// spikes, and pretending that converts to MB would be a lie.

using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using GameDistrict.MemoryShield.Core;
using GameDistrict.MemoryShield.Model;

namespace GameDistrict.MemoryShield.Analyzers
{
    public class UpdateLoopAnalyzer : IMemoryAnalyzer
    {
        public string CategoryName { get { return "Update Loops"; } }

        private static readonly string[] PerFrameMethods = { "Update", "FixedUpdate", "LateUpdate" };

        private static readonly Regex FindRx = new Regex(
            @"\b(GameObject\.Find|FindObjectOfType|FindObjectsOfType|FindAnyObjectByType|FindFirstObjectByType)\s*[<(]");
        private static readonly Regex GetComponentRx = new Regex(
            @"\bGetComponent(?:s)?(?:InChildren|InParent)?\s*[<(]");
        private static readonly Regex InstantiateDestroyRx = new Regex(
            @"\b(Instantiate|Destroy)\s*\(");
        private static readonly Regex AllocRx = new Regex(
            @"\bnew\s+(?:List|Dictionary|HashSet|Queue|Stack|StringBuilder)\s*<|\bnew\s+\w+\s*\[|\.To(?:List|Array)\s*\(|\.(?:Where|Select|OrderBy|OrderByDescending|Concat|GroupBy)\s*\(");
        private static readonly Regex LoadRx = new Regex(
            @"\bResources\.Load|Addressables\.(?:Load|Instantiate)");
        private static readonly Regex CameraMainRx = new Regex(@"\bCamera\.main\b");
        private static readonly Regex DebugLogRx = new Regex(@"\bDebug\.Log(?:Warning|Error|Format)?\s*\(");
        private static readonly Regex SendMessageRx = new Regex(@"\b(?:SendMessage|BroadcastMessage)\s*\(");
        private static readonly Regex TransformFindRx = new Regex(@"\btransform\.Find\s*\(");

        public IEnumerator Analyze(ScanContext ctx, CategoryResult result, MemoryReport report)
        {
            var types = new List<CsTypeSpan>();
            foreach (var cs in ctx.CsFiles)
                CsParse.ParseTypes(cs, types);
            yield return null;

            int scriptsWithPerFrame = 0;
            int batch = 0;

            foreach (var t in types)
            {
                bool anyPerFrame = false;
                foreach (var methodName in PerFrameMethods)
                {
                    string body = CsParse.MethodBody(t.body, methodName);
                    if (body == null) continue;
                    anyPerFrame = true;
                    int line = CsParse.MethodLine(t, methodName);
                    string where = t.name + "." + methodName;

                    // UPD-008 — empty per-frame method: Unity still invokes the
                    // magic method for every instance, every frame
                    string inner = body.Trim('{', '}', ' ', '\t', '\r', '\n');
                    if (inner.Length == 0)
                    {
                        result.findings.Add(Finding.Make("UPD-008", Severity.Low, t.file.path,
                            where + "() is empty — Unity still calls it for every instance every frame; delete it.",
                            "Remove the empty method.", 0, line, "S"));
                        continue;
                    }

                    // UPD-001 — scene-wide searches per frame
                    var mFind = FindRx.Match(body);
                    if (mFind.Success)
                        result.findings.Add(Finding.Make("UPD-001", Severity.High, t.file.path,
                            string.Format("{0} calls {1} every frame — that walks the whole scene 60 times a second.", where, mFind.Groups[1].Value),
                            "Resolve the reference once in Awake/Start and cache it in a field.",
                            0, line, "S"));

                    // UPD-002 — GetComponent per frame
                    if (GetComponentRx.IsMatch(body))
                        result.findings.Add(Finding.Make("UPD-002", Severity.Medium, t.file.path,
                            where + " calls GetComponent every frame — cache the component in Awake instead.",
                            "private Rigidbody _rb; void Awake() { _rb = GetComponent<Rigidbody>(); }",
                            0, line, "S"));

                    // UPD-003 — Instantiate/Destroy churn per frame
                    var mInst = InstantiateDestroyRx.Match(body);
                    if (mInst.Success)
                        result.findings.Add(Finding.Make("UPD-003", Severity.High, t.file.path,
                            string.Format("{0} calls {1} from a per-frame method — even behind an if, verify the branch isn't hot. Spawn churn is GC spikes plus fragmentation.", where, mInst.Groups[1].Value),
                            "Pool the objects, or move the spawn to an event instead of polling.",
                            0, line, "M"));

                    // UPD-004 — managed allocation per frame (collections, arrays, LINQ)
                    var mAlloc = AllocRx.Match(body);
                    if (mAlloc.Success)
                        result.findings.Add(Finding.Make("UPD-004", Severity.Medium, t.file.path,
                            where + " allocates every frame (new collection, array, or LINQ) — that's steady GC pressure feeding periodic hitches.",
                            "Hoist the allocation to a reused field, or replace the LINQ with a plain loop.",
                            0, line, "S"));

                    // UPD-005 — asset loading per frame
                    if (LoadRx.IsMatch(body))
                        result.findings.Add(Finding.Make("UPD-005", Severity.High, t.file.path,
                            where + " loads assets (Resources/Addressables) from a per-frame method — disk IO and decode work on the hot path.",
                            "Load once up front or on the event that needs it; never poll a loader.",
                            0, line, "S"));

                    // UPD-006 — Camera.main per frame
                    if (CameraMainRx.IsMatch(body))
                        result.findings.Add(Finding.Make("UPD-006", Severity.Low, t.file.path,
                            where + " reads Camera.main every frame — cheap since 2020.2 but still a lookup; cache it.",
                            "private Camera _cam; void Awake() { _cam = Camera.main; }",
                            0, line, "S"));

                    // UPD-007 — logging per frame
                    if (DebugLogRx.IsMatch(body))
                        result.findings.Add(Finding.Make("UPD-007", Severity.Medium, t.file.path,
                            where + " logs every frame — Debug.Log allocates its string and stays active in release builds unless stripped.",
                            "Delete it, or wrap in #if UNITY_EDITOR / a debug flag.",
                            0, line, "S"));

                    // UPD-009 — reflection messaging per frame
                    if (SendMessageRx.IsMatch(body))
                        result.findings.Add(Finding.Make("UPD-009", Severity.Medium, t.file.path,
                            where + " uses SendMessage/BroadcastMessage every frame — reflection dispatch on the hot path.",
                            "Replace with a direct call or a C# event.", 0, line, "S"));

                    // UPD-010 — transform.Find per frame
                    if (TransformFindRx.IsMatch(body))
                        result.findings.Add(Finding.Make("UPD-010", Severity.Low, t.file.path,
                            where + " does transform.Find every frame — a string-compare walk over children.",
                            "Cache the child reference in Awake.", 0, line, "S"));
                }
                if (anyPerFrame) scriptsWithPerFrame++;
                if (++batch % 40 == 0) yield return null;
            }

            // census line so the number is visible even when everything is clean
            result.findings.Add(Finding.Make("UPD-000", Severity.Info, "",
                string.Format("{0} of {1} project types run per-frame logic (Update/FixedUpdate/LateUpdate). Each is a per-instance native->managed call every frame — fewer is faster.",
                    scriptsWithPerFrame, types.Count),
                scriptsWithPerFrame > 50 ? "Consider a single manager ticking plain C# objects instead of many MonoBehaviour Updates." : "",
                0, 0, ""));
            yield return null;
        }
    }
}

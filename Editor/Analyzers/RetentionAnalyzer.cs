// Editor/Analyzers/RetentionAnalyzer.cs
// RET + SGL rules — the reason the tool exists. Static analysis over project .cs
// (Packages/, Plugins/ and Editor/ excluded by ScanContext).
//
// RET-003 and RET-004 are deliberately conservative: a retention analyzer that
// cries wolf twice gets ignored, and then the whole tool is dead. When in doubt
// these rules stay silent.
//
// Singleton framing (printed verbatim in the report): singleton COUNT is not a
// memory problem — an instance is a few hundred bytes. What costs memory is what
// a persistent object HOLDS, and for how long. The census (SGL-000) gives the
// count; the Persistent Root Map gives the retention that actually matters.

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using GameDistrict.MemoryShield.Core;
using GameDistrict.MemoryShield.Model;

namespace GameDistrict.MemoryShield.Analyzers
{
    public class RetentionAnalyzer : IMemoryAnalyzer
    {
        public string CategoryName { get { return "Retention"; } }

        private static readonly string[] AssetTypes =
            { "GameObject", "Sprite", "Texture2D", "Texture", "AudioClip", "Material", "Mesh" };

        private class RootInfo
        {
            public CsTypeSpan type;
            public bool singleton;
            public bool ddol;
            public string persistReason;
        }

        public IEnumerator Analyze(ScanContext ctx, CategoryResult result, MemoryReport report)
        {
            var types = new List<CsTypeSpan>();
            foreach (var cs in ctx.CsFiles)
                CsParse.ParseTypes(cs, types);
            yield return null;

            // ── persistence detection: generous by design ────────────────────
            var roots = new Dictionary<CsTypeSpan, RootInfo>();
            foreach (var t in types)
            {
                var info = new RootInfo { type = t };
                if (Regex.IsMatch(t.body, @"static\s+" + Regex.Escape(t.name) + @"\s+(Instance|instance|I|_instance|_Instance)\b")
                    || Regex.IsMatch(t.body, @"static\s+" + Regex.Escape(t.name) + @"\s+(Instance|instance)\s*\{"))
                {
                    info.singleton = true;
                    info.persistReason = "static Instance field";
                }
                if (t.body.Contains("DontDestroyOnLoad("))
                {
                    info.ddol = true;
                    info.persistReason = string.IsNullOrEmpty(info.persistReason)
                        ? "DontDestroyOnLoad call" : info.persistReason + " + DontDestroyOnLoad";
                }
                if (info.singleton || info.ddol) roots[t] = info;
            }
            yield return null;

            var fieldRx = new Regex(
                @"(?:public|private|protected|internal|\[SerializeField\][\s\r\n]*(?:private|public)?)\s+" +
                @"(?<coll>(?:List|Dictionary|Queue|Stack|HashSet)<[^>]*)?(?<type>" + string.Join("|", AssetTypes) + @")(?<arr>\[\])?[>\s,]*\s+(?<name>\w+)\s*[;=]");

            long worstRetained = 0;
            string worstRetainer = null;
            int batch = 0;

            foreach (var t in types)
            {
                bool persistent = roots.ContainsKey(t);
                var rootInfo = persistent ? roots[t] : null;

                // ── RET-001 — persistent type holding asset-typed fields ──────
                if (persistent)
                {
                    var held = new List<string>();
                    foreach (Match m in fieldRx.Matches(t.body))
                    {
                        string label = (m.Groups["coll"].Success ? m.Groups["coll"].Value + m.Groups["type"].Value + "> " : m.Groups["type"].Value + (m.Groups["arr"].Success ? "[] " : " "))
                                       + m.Groups["name"].Value;
                        held.Add(label.Trim());
                    }

                    long transitive = 0;
                    int prefabRefs = 0;
                    if (held.Count > 0)
                        transitive = EstimateTransitive(ctx, t.file.guid, out prefabRefs);
                    else
                        EstimateTransitive(ctx, t.file.guid, out prefabRefs);

                    bool guard = HasDuplicateGuard(t);

                    if (held.Count > 0)
                    {
                        result.findings.Add(Finding.Make("RET-001", Severity.High, t.file.path,
                            string.Format("{0} is persistent ({1}) and holds {2} asset-typed field{3}: {4}.{5} Everything reachable from it survives every scene change — Resources.UnloadUnusedAssets can't free it.",
                                t.name, rootInfo.persistReason, held.Count, held.Count == 1 ? "" : "s",
                                string.Join(", ", held),
                                prefabRefs > 0 ? string.Format(" {0} prefab{1} reference this script (~{2} of texture/audio reachable through them).",
                                    prefabRefs, prefabRefs == 1 ? "" : "s", TextureAnalyzer.Fmt(transitive)) : ""),
                            "Hold addressable keys or ids instead of the assets, or clear the fields on scene transition.",
                            transitive, t.declLine, "M"));
                    }

                    report.persistentRoots.Add(new PersistentRoot
                    {
                        typeName = t.name,
                        scriptPath = t.file.path,
                        line = t.declLine,
                        reason = rootInfo.persistReason,
                        heldAssetFields = held,
                        estTransitiveBytes = transitive,
                        singleton = rootInfo.singleton,
                        dontDestroyOnLoad = rootInfo.ddol,
                        duplicateGuard = guard,
                        referencingPrefabs = prefabRefs,
                    });
                    if (transitive > worstRetained)
                    {
                        worstRetained = transitive;
                        worstRetainer = t.name;
                    }

                    // ── SGL-001 — singleton without a duplicate guard ─────────
                    if (rootInfo.singleton && !guard)
                        result.findings.Add(Finding.Make("SGL-001", Severity.Medium, t.file.path,
                            string.Format("{0} is a singleton with no duplicate guard in Awake — load its scene twice (or place it in two scenes) and both copies stay alive, doubling everything it retains.",
                                t.name),
                            "In Awake: if (Instance != null && Instance != this) { Destroy(gameObject); return; } Instance = this;",
                            0, t.declLine, "S"));
                }

                // ── RET-002 — static field holding a Unity Object ─────────────
                foreach (Match m in Regex.Matches(t.body,
                    @"static\s+(?:readonly\s+)?(?:List<|Dictionary<[^>]*,\s*)?(" + string.Join("|", AssetTypes) + @")[>\[\]\s]*\s+(\w+)\s*[;=]"))
                {
                    result.findings.Add(Finding.Make("RET-002", Severity.High, t.file.path,
                        string.Format("static {0} {1} in {2} — a static Unity Object reference is a GC root for the whole session.",
                            m.Groups[1].Value, m.Groups[2].Value, t.name),
                        "Make it an instance field with a managed lifetime, or null it on scene unload.",
                        0, CsParse.LineOf(t, m.Index), "S"));
                }

                // ── RET-003 — static event with no -= anywhere in the project ──
                foreach (Match m in Regex.Matches(t.body,
                    @"public\s+static\s+(?:event\s+)?(?:Action|UnityAction|EventHandler|Func)[<\w,\s>]*\s+(\w+)\s*;"))
                {
                    string evName = m.Groups[1].Value;
                    if (!ctx.AllCodeLower.Contains(evName.ToLowerInvariant() + " -=")
                        && !ctx.AllCodeLower.Contains(evName.ToLowerInvariant() + "-=")
                        && ctx.AllCodeLower.Contains(evName.ToLowerInvariant() + " +="))
                    {
                        result.findings.Add(Finding.Make("RET-003", Severity.High, t.file.path,
                            string.Format("static event {0}.{1} is subscribed to (+=) but never unsubscribed (-=) anywhere in the project — every subscriber lives for the whole session.",
                                t.name, evName),
                            "Add the matching -= in OnDisable/OnDestroy of each subscriber.",
                            0, CsParse.LineOf(t, m.Index), "S"));
                    }
                }

                // ── RET-004 — += in a lifecycle hook with no matching -= ────────
                CheckSubscriptionBalance(t, result);

                // ── RET-005 — grow-only collection on a persistent type ────────
                if (persistent)
                {
                    foreach (Match m in Regex.Matches(t.body,
                        @"(?:List|Dictionary|Queue|HashSet)<[^>]+>\s+(\w+)\s*[;=]"))
                    {
                        string coll = m.Groups[1].Value;
                        bool adds = t.body.Contains(coll + ".Add(") || t.body.Contains(coll + ".Enqueue(")
                                 || t.body.Contains(coll + "[");
                        bool shrinks = t.body.Contains(coll + ".Remove") || t.body.Contains(coll + ".Clear(")
                                    || t.body.Contains(coll + ".Dequeue(") || t.body.Contains(coll + ".TrimExcess(");
                        if (adds && !shrinks)
                            result.findings.Add(Finding.Make("RET-005", Severity.Medium, t.file.path,
                                string.Format("{0}.{1} on a persistent type only ever grows — no Remove, Clear, or cap in sight.", t.name, coll),
                                "Add an eviction path or clear it on scene transition.",
                                0, CsParse.LineOf(t, m.Index), "S"));
                    }
                }

                // ── RET-006 — singleton caching prefabs in Awake ────────────────
                if (persistent)
                {
                    var awake = CsParse.MethodBody(t.body, "Awake");
                    if (awake != null && (awake.Contains("Instantiate(") || awake.Contains("Resources.Load")))
                        result.findings.Add(Finding.Make("RET-006", Severity.Medium, t.file.path,
                            t.name + " instantiates or loads assets in Awake — everything it caches there is pinned for the singleton's (whole-session) lifetime.",
                            "Load on first use and release on scene change instead.", 0, t.declLine, "M"));
                }

                // ── RET-007 — runtime-created native objects never destroyed ────
                foreach (Match m in Regex.Matches(t.body, @"new\s+(Texture2D|Mesh|RenderTexture)\s*\("))
                {
                    bool destroyed = t.body.Contains("Destroy(") || t.body.Contains("DestroyImmediate(")
                                  || t.body.Contains(".Release(");
                    if (!destroyed)
                        result.findings.Add(Finding.Make("RET-007", Severity.High, t.file.path,
                            string.Format("new {0}() in {1} with no Destroy or Release in the type — that's native memory the GC cannot reclaim; it leaks once per call.",
                                m.Groups[1].Value, t.name),
                            "Destroy() it when done (or Release() for RenderTextures).",
                            0, CsParse.LineOf(t, m.Index), "S"));
                }

                // ── RET-008 — .material / .mesh instead of shared ───────────────
                foreach (Match m in Regex.Matches(t.body, @"\.(material|mesh)\b"))
                {
                    int idx = m.Index;
                    if (idx > 0 && (char.IsLetterOrDigit(t.body[idx - 1]) || t.body[idx - 1] == ')' || t.body[idx - 1] == ']'))
                    {
                        result.findings.Add(Finding.Make("RET-008", Severity.High, t.file.path,
                            string.Format(".{0} accessed in {1} — the getter silently instantiates a per-renderer copy that outlives the access.",
                                m.Groups[1].Value, t.name),
                            "Use .shared" + Cap(m.Groups[1].Value) + " unless you intend a unique instance (then Destroy it).",
                            0, CsParse.LineOf(t, idx), "S"));
                    }
                }

                // ── RET-009 — DDOL object carrying renderers/audio ──────────────
                if (t.body.Contains("DontDestroyOnLoad(")
                    && (t.body.Contains("Renderer") || t.body.Contains("AudioSource") || t.body.Contains("ParticleSystem")))
                    result.findings.Add(Finding.Make("RET-009", Severity.Medium, t.file.path,
                        t.name + " calls DontDestroyOnLoad and references renderers/audio sources — visual and audio content pinned across every scene.",
                        "Keep the persistent object logic-only; spawn its visuals per scene.", 0, t.declLine, "M"));

                // ── RET-010 — destroyed reference not nulled (persistent types) ──
                if (persistent)
                {
                    foreach (Match m in Regex.Matches(t.body, @"Destroy\s*\(\s*(\w+)\s*\)"))
                    {
                        string fieldName = m.Groups[1].Value;
                        if (fieldName == "gameObject" || fieldName == "this") continue;
                        bool isField = Regex.IsMatch(t.body, @"\b\w+[>\]]?\s+" + Regex.Escape(fieldName) + @"\s*[;=]");
                        bool nulled = t.body.Contains(fieldName + " = null");
                        if (isField && !nulled)
                            result.findings.Add(Finding.Make("RET-010", Severity.Medium, t.file.path,
                                string.Format("{0} destroys {1} but never nulls the field — the managed shell (and anything it references) stays retained on this persistent object.", t.name, fieldName),
                                "Set the field to null after Destroy.", 0, CsParse.LineOf(t, m.Index), "S"));
                    }
                }

                // ── RET-011 — WaitForSeconds allocated per call ─────────────────
                foreach (Match m in Regex.Matches(t.body, @"yield\s+return\s+new\s+WaitForSeconds\s*\("))
                {
                    result.findings.Add(Finding.Make("RET-011", Severity.Low, t.file.path,
                        "yield return new WaitForSeconds(...) allocates on every iteration — cache it in a field.",
                        "private static readonly WaitForSeconds _wait = new WaitForSeconds(x);",
                        0, CsParse.LineOf(t, m.Index), "S"));
                }

                // ── RET-012 — string concat in Update (allocation pressure) ─────
                var update = CsParse.MethodBody(t.body, "Update") ?? "";
                var fixedUpdate = CsParse.MethodBody(t.body, "FixedUpdate") ?? "";
                foreach (var body in new[] { update, fixedUpdate })
                {
                    if (body.Length == 0) continue;
                    if (Regex.IsMatch(body, "\"\\s*\\+") || body.Contains("$\""))
                    {
                        result.findings.Add(Finding.Make("RET-012", Severity.Low, t.file.path,
                            t.name + " builds strings in Update/FixedUpdate — allocation pressure, not footprint, but it feeds GC spikes.",
                            "Cache the string, or only rebuild when the value changes.", 0, t.declLine, "S"));
                        break;
                    }
                }

                if (++batch % 25 == 0) yield return null;
            }

            // ── SGL-000 — singleton census. The count is context, not a defect. ──
            int singletons = roots.Values.Count(r => r.singleton);
            int ddols = roots.Values.Count(r => r.ddol);
            int unguarded = report.persistentRoots.Count(p => p.singleton && !p.duplicateGuard);
            result.findings.Add(Finding.Make("SGL-000", Severity.Info, "",
                string.Format("Singleton census: {0} singleton{1}, {2} with DontDestroyOnLoad, {3} without a duplicate guard.{4} Count alone is fine — what matters is what each one retains; see the Persistent Root Map.",
                    singletons, singletons == 1 ? "" : "s", ddols, unguarded,
                    worstRetainer != null && worstRetained > 0
                        ? string.Format(" Heaviest retainer: {0} (~{1} reachable through referencing prefabs).",
                            worstRetainer, TextureAnalyzer.Fmt(worstRetained)) : ""),
                "", 0, 0, ""));

            report.persistentRoots = report.persistentRoots
                .OrderByDescending(r => r.estTransitiveBytes).ToList();
            yield return null;
        }

        // Duplicate-guard heuristic: Awake (or OnEnable) both checks an
        // Instance-style field and destroys something. Conservative on purpose.
        private static bool HasDuplicateGuard(CsTypeSpan t)
        {
            string guardBodies = string.Concat(
                CsParse.MethodBody(t.body, "Awake") ?? "",
                CsParse.MethodBody(t.body, "OnEnable") ?? "");
            if (guardBodies.Length == 0) return false;
            bool checksInstance = Regex.IsMatch(guardBodies, @"\b(?:Instance|instance|_instance|_Instance)\s*[!=]=\s*(?:null|this)");
            bool destroys = guardBodies.Contains("Destroy(") || guardBodies.Contains("DestroyImmediate(");
            return checksInstance && destroys;
        }

        // ── RET-004 — conservative: only flags += on static/Instance-owned
        // sources, subscribed in a lifecycle hook, with no matching -= in
        // OnDisable/OnDestroy in the same type.
        private static void CheckSubscriptionBalance(CsTypeSpan t, CategoryResult result)
        {
            string subscribeBodies = string.Concat(
                CsParse.MethodBody(t.body, "OnEnable") ?? "",
                CsParse.MethodBody(t.body, "Start") ?? "",
                CsParse.MethodBody(t.body, "Awake") ?? "");
            if (subscribeBodies.Length == 0) return;
            string unsubscribeBodies = string.Concat(
                CsParse.MethodBody(t.body, "OnDisable") ?? "",
                CsParse.MethodBody(t.body, "OnDestroy") ?? "");

            foreach (Match m in Regex.Matches(subscribeBodies, @"([\w\.]+)\s*\+=\s*([\w\.]+)\s*;"))
            {
                string source = m.Groups[1].Value;
                bool outlives = source.Contains(".Instance.") || source.Contains(".instance.")
                    || (source.Contains(".") && char.IsUpper(source[0]) && !source.StartsWith("this."));
                if (!outlives) continue;
                string evt = source.Substring(source.LastIndexOf('.') + 1);
                if (unsubscribeBodies.Contains(evt) && unsubscribeBodies.Contains("-=")) continue;

                result.findings.Add(Finding.Make("RET-004", Severity.High, t.file.path,
                    string.Format("{0} subscribes to {1} in a lifecycle hook but has no matching -= in OnDisable/OnDestroy — the publisher outlives this object and keeps it (and its scene's assets) reachable.",
                        t.name, source),
                    "Unsubscribe in OnDisable or OnDestroy.", 0, t.declLine, "S"));
            }
        }

        // ── rough transitive estimate: prefabs whose text contains this script's
        // guid, summed over their texture/audio dependency estimates. ForceText only.
        // Prefab texts are cached for the scan — this runs once per persistent root.
        private readonly Dictionary<string, string> _prefabTextCache = new Dictionary<string, string>();

        private long EstimateTransitive(ScanContext ctx, string scriptGuid, out int prefabCount)
        {
            prefabCount = 0;
            if (!ctx.TextSerialization || string.IsNullOrEmpty(scriptGuid)) return 0;
            long total = 0;
            foreach (var kv in ctx.PrefabDeps)
            {
                if (!_prefabTextCache.TryGetValue(kv.Key, out string prefabText))
                {
                    prefabText = "";
                    try
                    {
                        var info = new System.IO.FileInfo(kv.Key);
                        if (info.Exists && info.Length <= 20L * 1024 * 1024)
                            prefabText = System.IO.File.ReadAllText(kv.Key);
                    }
                    catch (System.IO.IOException) { }
                    _prefabTextCache[kv.Key] = prefabText;
                }
                if (prefabText.Length == 0 || !prefabText.Contains(scriptGuid)) continue;
                prefabCount++;
                foreach (var d in kv.Value)
                {
                    if (ctx.TextureEstimates.TryGetValue(d, out long tb)) total += tb;
                    else if (ctx.AudioEstimates.TryGetValue(d, out long ab)) total += ab;
                }
            }
            return total;
        }

        private static string Cap(string s)
        {
            return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }
    }
}

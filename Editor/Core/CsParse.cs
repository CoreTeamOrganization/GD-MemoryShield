// Editor/Core/CsParse.cs
// Tiny structural C# parser shared by the code analyzers: class spans via brace
// matching, first-method-body extraction, line resolution. Regex-and-braces, not
// Roslyn — good enough for the conservative pattern rules built on it.

using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace GameDistrict.MemoryShield.Core
{
    public class CsTypeSpan
    {
        public string name;
        public string body;
        public int declLine;              // 1-based
        public ScanContext.CsFile file;
    }

    public static class CsParse
    {
        private static readonly Regex TypeRx = new Regex(@"\b(?:class|struct)\s+(\w+)");

        public static void ParseTypes(ScanContext.CsFile cs, List<CsTypeSpan> into)
        {
            foreach (Match m in TypeRx.Matches(cs.content))
            {
                int braceStart = cs.content.IndexOf('{', m.Index);
                if (braceStart < 0) continue;
                int depth = 0, end = -1;
                for (int i = braceStart; i < cs.content.Length; i++)
                {
                    char c = cs.content[i];
                    if (c == '{') depth++;
                    else if (c == '}')
                    {
                        depth--;
                        if (depth == 0) { end = i; break; }
                    }
                }
                if (end < 0) continue;
                into.Add(new CsTypeSpan
                {
                    name = m.Groups[1].Value,
                    body = cs.content.Substring(braceStart, end - braceStart + 1),
                    declLine = 1 + CountNewlines(cs.content, m.Index),
                    file = cs,
                });
            }
        }

        // body of the first method with this name inside a type body, or null
        public static string MethodBody(string typeBody, string methodName)
        {
            var m = Regex.Match(typeBody, @"\b(?:void|IEnumerator)\s+" + methodName + @"\s*\([^)]*\)");
            if (!m.Success) return null;
            int braceStart = typeBody.IndexOf('{', m.Index);
            if (braceStart < 0) return null;
            int depth = 0;
            for (int i = braceStart; i < typeBody.Length; i++)
            {
                if (typeBody[i] == '{') depth++;
                else if (typeBody[i] == '}')
                {
                    depth--;
                    if (depth == 0) return typeBody.Substring(braceStart, i - braceStart + 1);
                }
            }
            return null;
        }

        // 1-based line of the method declaration inside the file, or declLine fallback
        public static int MethodLine(CsTypeSpan t, string methodName)
        {
            var m = Regex.Match(t.body, @"\b(?:void|IEnumerator)\s+" + methodName + @"\s*\(");
            return m.Success ? LineOf(t, m.Index) : t.declLine;
        }

        public static int LineOf(CsTypeSpan t, int indexInBody)
        {
            int bodyStart = t.file.content.IndexOf(t.body, System.StringComparison.Ordinal);
            if (bodyStart < 0) return t.declLine;
            return 1 + CountNewlines(t.file.content, bodyStart + indexInBody);
        }

        public static int CountNewlines(string s, int upTo)
        {
            int n = 0;
            int max = System.Math.Min(upTo, s.Length);
            for (int i = 0; i < max; i++) if (s[i] == '\n') n++;
            return n;
        }
    }
}

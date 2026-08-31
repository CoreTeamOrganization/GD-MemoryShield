// Editor/Model/Finding.cs

using System;

namespace GameDistrict.MemoryShield.Model
{
    [Serializable]
    public class Finding
    {
        public string id;              // rule id, e.g. "TEX-001"
        public int severity;           // (int)Severity — frozen schema
        public string severityLabel;   // "HIGH" etc., for humans reading raw JSON
        public string path;            // asset or script path, "" for project-wide findings
        public int line;               // 1-based script line, 0 when not applicable
        public string message;         // what a colleague would say, one or two sentences
        public string fix;             // suggested fix, may be ""
        public long estimatedBytes;    // estimated recoverable bytes, 0 when unknown
        public string effort;          // "S", "M", "L" or ""
        public int instances = 1;      // >1 when a folder/group row stands in for many
                                       // identical findings; scoring weights by this

        public Severity Sev
        {
            get { return (Severity)severity; }
        }

        public static Finding Make(string id, Severity sev, string path, string message,
                                   string fix = "", long estimatedBytes = 0,
                                   int line = 0, string effort = "", int instances = 1)
        {
            return new Finding
            {
                id = id,
                severity = (int)sev,
                severityLabel = SeverityUtil.Label(sev),
                path = path ?? "",
                line = line,
                message = message ?? "",
                fix = fix ?? "",
                estimatedBytes = estimatedBytes,
                effort = effort ?? "",
                instances = instances < 1 ? 1 : instances,
            };
        }
    }
}

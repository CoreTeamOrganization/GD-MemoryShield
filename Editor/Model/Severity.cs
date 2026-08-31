// Editor/Model/Severity.cs

namespace GameDistrict.MemoryShield.Model
{
    // Order is frozen — the ints are part of the JSON export schema from v1.0.
    public enum Severity
    {
        Blocker = 0,   // category cannot run until the user fixes something (e.g. SCN-000)
        High    = 1,
        Medium  = 2,
        Low     = 3,
        Info    = 4,
    }

    public enum CategoryState
    {
        Complete   = 0,
        Incomplete = 1,   // analyzer hit its 120s timeout — findings are partial
        Skipped    = 2,   // blocked (e.g. binary scene serialization) or disabled
    }

    public static class SeverityUtil
    {
        public static string Label(Severity s)
        {
            switch (s)
            {
                case Severity.Blocker: return "BLOCKER";
                case Severity.High:    return "HIGH";
                case Severity.Medium:  return "MEDIUM";
                case Severity.Low:     return "LOW";
                default:               return "INFO";
            }
        }
    }
}

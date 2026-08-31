// Editor/Export/JsonExporter.cs
// The JSON export IS the MemoryReport object. Schema is stable from v1.0 —
// changing it later breaks whatever consumes it (future CI gates included).

using GameDistrict.MemoryShield.Model;
using UnityEngine;

namespace GameDistrict.MemoryShield.Export
{
    public static class JsonExporter
    {
        public static string Export(MemoryReport report)
        {
            return JsonUtility.ToJson(report, true);
        }
    }
}

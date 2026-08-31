// Editor/Telemetry/MemoryShieldTelemetry.cs
// Local-only usage log (Library/GDMemoryShield/telemetry.log). Nothing leaves
// the machine — this exists so a studio can tell us "we ran it N times and the
// score went from X to Y" without anyone screenshotting windows.

using System;
using System.IO;

namespace GameDistrict.MemoryShield.Telemetry
{
    public static class MemoryShieldTelemetry
    {
        private const string LogPath = "Library/GDMemoryShield/telemetry.log";

        public static void Event(string name, string detail = "")
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                File.AppendAllText(LogPath, string.Format("{0:yyyy-MM-dd HH:mm:ss}\t{1}\t{2}\n",
                    DateTime.UtcNow, name, detail));
            }
            catch (Exception) { /* never let telemetry break the tool */ }
        }
    }
}

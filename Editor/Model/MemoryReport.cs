// Editor/Model/MemoryReport.cs
// The full structured report. Everything is [Serializable] and JsonUtility-friendly
// (lists only, no dictionaries) because the JSON export IS this object, and the
// schema must stay stable from v1.0 — CI gates may consume it later.

using System;
using System.Collections.Generic;

namespace GameDistrict.MemoryShield.Model
{
    [Serializable]
    public class TextureStat
    {
        public string path;
        public int width;
        public int height;
        public string format;          // resolved Android format name
        public bool readable;
        public bool mipmaps;
        public long estimatedBytes;    // estimate — ignores atlas packing and streaming
    }

    [Serializable]
    public class AudioStat
    {
        public string path;
        public float lengthSeconds;
        public int frequency;
        public int channels;
        public string loadType;        // resolved Android load type
        public long estimatedBytes;
    }

    [Serializable]
    public class AtlasStat
    {
        public string path;
        public int spriteCount;
        public long packedAreaPx;      // sum of sprite w*h
        public int estPageWidth;       // estimated generated page dimensions
        public int estPageHeight;
        public int estPageCount;
        public float paddingMultiplier; // page area / packed area
        public float efficiencyPct;     // packed / page, 0..100
        public string format;           // Android override format, or "DEFAULT (uncompressed)"
        public long estResidentBytes;
        public bool calibrated;         // true if numbers came from a calibration table, not defaults
    }

    [Serializable]
    public class SceneStat
    {
        public string path;
        public int assetRefCount;      // distinct assets referenced (transitive)
        public long estResidentBytes;  // estimate over texture+audio deps
        public int gameObjectCount;
        public int disabledObjectCount;
    }

    [Serializable]
    public class PersistentRoot
    {
        public string typeName;
        public string scriptPath;
        public int line;                          // line of the class declaration
        public string reason;                     // "static Instance field" / "DontDestroyOnLoad call"
        public List<string> heldAssetFields = new List<string>();  // "Sprite winIcon", "List<AudioClip> stingers"
        public long estTransitiveBytes;           // rough estimate of what stays resident because of it
        public bool singleton;                    // has a static Instance-style field
        public bool dontDestroyOnLoad;            // calls DontDestroyOnLoad
        public bool duplicateGuard;               // Awake destroys a second copy
        public int referencingPrefabs;            // prefabs whose text carries this script's guid
    }

    [Serializable]
    public class TopIssue
    {
        public string id;
        public string path;
        public string message;
        public string fix;
        public string effort;          // S / M / L
        public long estimatedBytes;
    }

    [Serializable]
    public class MemoryReport
    {
        public string schemaVersion = "1.0";
        public string toolVersion = MemoryShieldVersion.Version;
        public string projectName;
        public string unityVersion;
        public string scanDateUtc;     // ISO 8601

        public string grade;           // A..F
        public float score;            // 0..100
        public string verdict;         // one line
        public string executiveSummary;

        public List<CategoryResult> categories = new List<CategoryResult>();
        public List<TopIssue> topIssues = new List<TopIssue>();
        public List<PersistentRoot> persistentRoots = new List<PersistentRoot>();

        public List<TextureStat> topTextures = new List<TextureStat>();  // top 20
        public List<AudioStat> topAudio = new List<AudioStat>();         // top 10
        public List<AtlasStat> atlases = new List<AtlasStat>();          // all of them
        public List<SceneStat> scenes = new List<SceneStat>();

        public long estimatedTotalBytes;   // sum of texture+audio estimates
        public string budgetTier;          // "Low" / "Mid" / "High"
        public long budgetCeilingBytes;
        public bool budgetCalibrated;      // false until real device captures back the numbers

        public List<string> blindSpots = new List<string>();
        public List<string> nextSteps = new List<string>();

        public CategoryResult Category(string name)
        {
            for (int i = 0; i < categories.Count; i++)
                if (categories[i].category == name) return categories[i];
            return null;
        }

        public List<Finding> AllFindings()
        {
            var all = new List<Finding>();
            for (int i = 0; i < categories.Count; i++)
                all.AddRange(categories[i].findings);
            return all;
        }
    }

    public static class MemoryShieldVersion
    {
        public const string Version = "1.0.3";
    }
}

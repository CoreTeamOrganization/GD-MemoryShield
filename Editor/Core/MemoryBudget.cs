// Editor/Core/MemoryBudget.cs
// Per-tier estimated-footprint ceilings the team owns. The starting numbers are
// a proposal — calibrate against device captures of two or three shipped GD
// titles before anyone treats them as real. Until `calibrated` is ticked, the
// report labels the MB comparison accordingly.

using UnityEditor;
using UnityEngine;

namespace GameDistrict.MemoryShield.Core
{
    public class MemoryBudget : ScriptableObject
    {
        [System.Serializable]
        public class Tier
        {
            public string name;
            public string deviceRam;
            public long ceilingBytes;
        }

        public Tier[] tiers =
        {
            new Tier { name = "Low",  deviceRam = "2 GB",  ceilingBytes = 350L * 1024 * 1024 },
            new Tier { name = "Mid",  deviceRam = "4 GB",  ceilingBytes = 600L * 1024 * 1024 },
            new Tier { name = "High", deviceRam = "6 GB+", ceilingBytes = 900L * 1024 * 1024 },
        };

        [Tooltip("Tick only after the ceilings have been checked against real device captures.")]
        public bool calibrated = false;

        public int selectedTier = 1;   // Mid by default

        public Tier Selected
        {
            get
            {
                if (tiers == null || tiers.Length == 0) return null;
                int i = Mathf.Clamp(selectedTier, 0, tiers.Length - 1);
                return tiers[i];
            }
        }

        private const string AssetPath = "Assets/Editor/MemoryShield/MemoryBudget.asset";

        public static MemoryBudget LoadOrCreate()
        {
            var existing = AssetDatabase.LoadAssetAtPath<MemoryBudget>(AssetPath);
            if (existing != null) return existing;

            var budget = CreateInstance<MemoryBudget>();
            string dir = System.IO.Path.GetDirectoryName(AssetPath);
            if (!AssetDatabase.IsValidFolder(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
                AssetDatabase.Refresh();
            }
            AssetDatabase.CreateAsset(budget, AssetPath);
            AssetDatabase.SaveAssets();
            return budget;
        }
    }
}

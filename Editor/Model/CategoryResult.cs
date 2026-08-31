// Editor/Model/CategoryResult.cs

using System;
using System.Collections.Generic;

namespace GameDistrict.MemoryShield.Model
{
    [Serializable]
    public class CategoryResult
    {
        public string category;        // "Textures", "Sprite Atlases", ...
        public int state;              // (int)CategoryState
        public string stateNote;       // e.g. "timed out after 120s — findings are partial"
        public float subscore;         // 0..100, filled by ScoreCalculator
        public float elapsedSeconds;
        public List<Finding> findings = new List<Finding>();

        public CategoryState State
        {
            get { return (CategoryState)state; }
            set { state = (int)value; }
        }

        public int CountOf(Severity s)
        {
            int n = 0;
            for (int i = 0; i < findings.Count; i++)
                if (findings[i].severity == (int)s) n++;
            return n;
        }

        public static CategoryResult Make(string category)
        {
            return new CategoryResult { category = category, state = (int)CategoryState.Complete, stateNote = "" };
        }
    }
}

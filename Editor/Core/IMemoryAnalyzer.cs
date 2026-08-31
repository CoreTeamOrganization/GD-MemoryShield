// Editor/Core/IMemoryAnalyzer.cs

using System.Collections;
using GameDistrict.MemoryShield.Model;

namespace GameDistrict.MemoryShield.Core
{
    public interface IMemoryAnalyzer
    {
        string CategoryName { get; }

        // Runs as a coroutine: yield null between batches so the editor stays
        // responsive. The orchestrator enforces the 120s timeout between yields;
        // on timeout the category is marked INCOMPLETE, never silently dropped.
        IEnumerator Analyze(ScanContext ctx, CategoryResult result, MemoryReport report);
    }
}

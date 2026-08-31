// Editor/Core/SampleReport.cs
// Build-order step 1: a hardcoded fake report that exercises the window,
// scoring and both exporters end to end without touching the AssetDatabase.
// Keep it — it's the fastest way to verify the pipeline after UI changes.

using System.Collections.Generic;
using GameDistrict.MemoryShield.Model;
using GameDistrict.MemoryShield.Window;
using UnityEditor;
using UnityEngine;

namespace GameDistrict.MemoryShield.Core
{
    public static class SampleReport
    {
        [MenuItem("Tools/GD MemoryShield Sample Report (pipeline check)", false, 2000)]
        public static void Load()
        {
            var report = Build();
            ScoreCalculator.Score(report);
            MemoryShieldWindow.Open();
            var window = EditorWindow.GetWindow<MemoryShieldWindow>();
            window.SetReport(report);
            Debug.Log("[MemoryShield] Sample report loaded — grade " + report.grade
                + " (" + report.score.ToString("0") + "). Markdown export length: "
                + Export.MarkdownExporter.Export(report).Length + " chars, JSON length: "
                + Export.JsonExporter.Export(report).Length + " chars.");
        }

        public static MemoryReport Build()
        {
            var r = new MemoryReport
            {
                projectName = "Sample Project",
                unityVersion = Application.unityVersion,
                scanDateUtc = "2026-01-01T00:00:00Z",
                estimatedTotalBytes = 512L * 1024 * 1024,
                budgetTier = "Mid",
                budgetCeilingBytes = 600L * 1024 * 1024,
            };

            var tex = CategoryResult.Make("Textures");
            tex.findings.Add(Finding.Make("TEX-001", Severity.High, "Assets/UI/big_background.png",
                "Read/Write is on — that keeps a full CPU-side copy alive, roughly 8 MB extra.",
                "Untick Read/Write Enabled.", 8L * 1024 * 1024, 0, "S"));
            tex.findings.Add(Finding.Make("TEX-004", Severity.Medium, "Assets/UI/button.png",
                "Mipmaps on a sprite/UI texture — +33% for nothing.",
                "Untick Generate Mip Maps.", 350 * 1024, 0, "S"));
            r.categories.Add(tex);

            var atl = CategoryResult.Make("Sprite Atlases");
            atl.findings.Add(Finding.Make("ATL-005", Severity.High, "Assets/Atlases/HUD.spriteatlas",
                "48 sprites packed -> est. 2048x2048 resident (9.2x, 1.86 MB, wasting 1.66 MB)",
                "Trim contents or adjust maxSize.", 1740 * 1024, 0, "M"));
            r.categories.Add(atl);

            var aud = CategoryResult.Make("Audio");
            aud.findings.Add(Finding.Make("AUD-001", Severity.High, "Assets/Audio/theme.mp3",
                "184.2s clip (60s+) on Decompress On Load — the full 62 MB of PCM sits resident while it's loaded.",
                "Switch to Streaming.", 62L * 1024 * 1024, 0, "S"));
            r.categories.Add(aud);

            var scn = CategoryResult.Make("Scenes");
            scn.findings.Add(Finding.Make("SCN-002", Severity.High, "Assets/Scenes/Main.unity",
                "63 disabled GameObjects in the hierarchy — disabling skips Update, not loading.",
                "Instantiate rarely-used objects on demand.", 0, 0, "M"));
            r.categories.Add(scn);

            var ret = CategoryResult.Make("Retention");
            ret.findings.Add(Finding.Make("RET-001", Severity.High, "Assets/Scripts/GameManager.cs",
                "GameManager is persistent (static Instance field + DontDestroyOnLoad) and holds 3 asset-typed fields: Sprite winIcon, List<AudioClip> stingers, GameObject levelPrefab.",
                "Hold addressable keys instead of the assets.", 24L * 1024 * 1024, 12, "M"));
            r.categories.Add(ret);

            r.persistentRoots.Add(new PersistentRoot
            {
                typeName = "GameManager",
                scriptPath = "Assets/Scripts/GameManager.cs",
                line = 12,
                reason = "static Instance field + DontDestroyOnLoad",
                heldAssetFields = new List<string> { "Sprite winIcon", "List<AudioClip> stingers", "GameObject levelPrefab" },
                estTransitiveBytes = 24L * 1024 * 1024,
            });

            r.topTextures.Add(new TextureStat
            {
                path = "Assets/UI/big_background.png", width = 2048, height = 2048,
                format = "ASTC_6x6", readable = true, mipmaps = false,
                estimatedBytes = 16L * 1024 * 1024,
            });
            r.topAudio.Add(new AudioStat
            {
                path = "Assets/Audio/theme.mp3", lengthSeconds = 184.2f, frequency = 44100,
                channels = 2, loadType = "DecompressOnLoad", estimatedBytes = 62L * 1024 * 1024,
            });
            r.atlases.Add(new AtlasStat
            {
                path = "Assets/Atlases/HUD.spriteatlas", spriteCount = 48,
                packedAreaPx = 457200, estPageWidth = 2048, estPageHeight = 2048,
                estPageCount = 1, paddingMultiplier = 9.2f, efficiencyPct = 10.9f,
                format = "ETC2_RGBA8", estResidentBytes = 4L * 1024 * 1024, calibrated = false,
            });
            r.scenes.Add(new SceneStat
            {
                path = "Assets/Scenes/Main.unity", assetRefCount = 412,
                estResidentBytes = 210L * 1024 * 1024, gameObjectCount = 1834, disabledObjectCount = 63,
            });

            r.topIssues.Add(new TopIssue
            {
                id = "AUD-001", path = "Assets/Audio/theme.mp3",
                message = "184.2s clip on Decompress On Load.",
                fix = "Switch to Streaming.", effort = "S", estimatedBytes = 62L * 1024 * 1024,
            });

            r.executiveSummary = "Sample data. If this renders and both exports run, the pipeline is intact.";
            r.blindSpots = new List<string> { "This is a fake report — it measured nothing." };
            r.nextSteps = new List<string> { "Run a real scan." };
            return r;
        }
    }
}

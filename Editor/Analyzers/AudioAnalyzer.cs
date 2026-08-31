// Editor/Analyzers/AudioAnalyzer.cs
// AUD rules. Routinely the most under-attended category in casual projects and
// often tens of MB. All settings read from the Android override when present.

using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameDistrict.MemoryShield.Core;
using GameDistrict.MemoryShield.Model;
using UnityEditor;
using UnityEngine;

namespace GameDistrict.MemoryShield.Analyzers
{
    public class AudioAnalyzer : IMemoryAnalyzer
    {
        public string CategoryName { get { return "Audio"; } }

        public IEnumerator Analyze(ScanContext ctx, CategoryResult result, MemoryReport report)
        {
            var stats = new List<AudioStat>();

            for (int i = 0; i < ctx.AudioPaths.Count; i++)
            {
                string path = ctx.AudioPaths[i];
                var importer = AssetImporter.GetAtPath(path) as AudioImporter;
                if (importer == null) continue;
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                if (clip == null) continue;

                var settings = importer.ContainsSampleSettingsOverride("Android")
                    ? importer.GetOverrideSampleSettings("Android")
                    : importer.defaultSampleSettings;

                float length = clip.length;
                int freq = clip.frequency;
                int channels = clip.channels;

                // Anything on Decompress On Load sits in memory as full PCM.
                long pcmBytes = (long)(length * freq * channels * 2);
                long estBytes = settings.loadType == AudioClipLoadType.DecompressOnLoad
                    ? pcmBytes
                    : settings.loadType == AudioClipLoadType.CompressedInMemory
                        ? (long)(pcmBytes * QualityFraction(settings))
                        : 256 * 1024;   // streaming keeps a small buffer
                ctx.AudioEstimates[path] = estBytes;

                stats.Add(new AudioStat
                {
                    path = path, lengthSeconds = length, frequency = freq,
                    channels = channels, loadType = settings.loadType.ToString(),
                    estimatedBytes = estBytes,
                });

                // AUD-001 — Decompress On Load on a clip over 10s
                if (settings.loadType == AudioClipLoadType.DecompressOnLoad && length > 10f)
                {
                    string tag = length > 60f ? " (60s+)" : "";
                    result.findings.Add(Finding.Make("AUD-001", Severity.High, path,
                        string.Format("{0:0.#}s clip{1} on Decompress On Load — the full {2} of PCM sits resident while it's loaded.",
                            length, tag, TextureAnalyzer.Fmt(pcmBytes)),
                        "Switch to Streaming (music) or Compressed In Memory (long stingers).",
                        pcmBytes - 256 * 1024, 0, "S"));
                }

                // AUD-002 — over 30s and not streaming
                if (length > 30f && settings.loadType != AudioClipLoadType.Streaming)
                    result.findings.Add(Finding.Make("AUD-002", Severity.High, path,
                        string.Format("{0:0.#}s clip not set to Streaming — music-length audio should never be fully resident.", length),
                        "Set Load Type to Streaming.", estBytes - 256 * 1024, 0, "S"));

                // AUD-003 — preload on a large clip
                if (settings.preloadAudioData && estBytes > 2L * 1024 * 1024)
                    result.findings.Add(Finding.Make("AUD-003", Severity.Medium, path,
                        string.Format("Preload Audio Data on a ~{0} clip — it loads with the scene whether or not it ever plays.", TextureAnalyzer.Fmt(estBytes)),
                        "Untick Preload Audio Data; the first play takes the tiny hit instead.",
                        0, 0, "S"));

                // AUD-004 — short SFX not on Compressed In Memory + ADPCM
                if (length < 3f && length > 0f
                    && (settings.loadType != AudioClipLoadType.CompressedInMemory
                        || settings.compressionFormat != AudioCompressionFormat.ADPCM))
                    result.findings.Add(Finding.Make("AUD-004", Severity.Medium, path,
                        string.Format("{0:0.##}s SFX on {1}/{2} — short one-shots decode fastest and smallest as Compressed In Memory + ADPCM.",
                            length, settings.loadType, settings.compressionFormat),
                        "Set Compressed In Memory + ADPCM.", 0, 0, "S"));

                // AUD-005 — mono-source clip without forceToMono
                if (channels >= 2 && !importer.forceToMono && length < 5f)
                    result.findings.Add(Finding.Make("AUD-005", Severity.Low, path,
                        "Stereo SFX without Force To Mono — positional/UI sounds don't need the second channel; it doubles the size.",
                        "Tick Force To Mono.", estBytes / 2, 0, "S"));

                // AUD-006 — 48kHz on short SFX
                if (freq >= 48000 && length < 5f)
                    result.findings.Add(Finding.Make("AUD-006", Severity.Low, path,
                        string.Format("{0}Hz on a {1:0.##}s SFX — 22-24kHz is inaudibly different on phone speakers at half the size.", freq, length),
                        "Set Sample Rate Setting to Override and pick 22050Hz.", estBytes / 2, 0, "S"));

                // AUD-007 — zero references anywhere
                bool referenced = ctx.ReferencedByScenes.ContainsKey(path)
                    || ctx.ReferencedByPrefabs.ContainsKey(path)
                    || ctx.AllCodeLower.Contains(Path.GetFileNameWithoutExtension(path).ToLowerInvariant());
                if (!referenced)
                    result.findings.Add(Finding.Make("AUD-007", Severity.Medium, path,
                        "No scene, prefab or script references this clip — dead weight in the build (and resident if it's under Resources/).",
                        "Delete it or move it out of the project.",
                        path.Contains("/Resources/") ? estBytes : 0, 0, "S"));

                if (i % 25 == 0) yield return null;
            }

            report.topAudio = stats.OrderByDescending(s => s.estimatedBytes).Take(10).ToList();
            yield return null;
        }

        private static float QualityFraction(AudioImporterSampleSettings s)
        {
            // Vorbis at quality q keeps very roughly q/10 of PCM; floor it so the
            // estimate never claims free audio.
            float q = Mathf.Clamp01(s.quality);
            return Mathf.Max(0.05f, q * 0.1f);
        }
    }
}

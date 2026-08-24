using System;
using System.Collections.Generic;
using System.Linq;

namespace ReplayKitSetup
{
    // recording presets -- resolution + fps + cqp target tier. the actual encoder json is built per-machine in Encoder.cs from the detected gpu + codec preference, so the same preset works on nvidia/amd/intel/software. ported from obs_replaykit/recording.py.
    public sealed class RecordingPreset
    {
        public string Name { get; }
        public string Label { get; }
        public string Description { get; }
        // section -> key -> value overrides for basic.ini.
        public Dictionary<string, Dictionary<string, string>> BasicIni { get; }
        // on the nvenc scale (0-51, lower = higher quality); Encoder.cs translates to whatever scale the chosen encoder uses.
        public int CqpTarget { get; }

        public RecordingPreset(string name, string label, string description, Dictionary<string, Dictionary<string, string>> basicIni, int cqpTarget)
        {
            Name = name; Label = label; Description = description; BasicIni = basicIni; CqpTarget = cqpTarget;
        }
    }

    public static class Recording
    {
        public static readonly IReadOnlyList<RecordingPreset> PRESETS = new List<RecordingPreset>
        {
            new RecordingPreset(
                name: "performance",
                label: "Performance",
                description: "720p30 - low GPU/CPU cost, smaller files. Works on any PC.",
                basicIni: new Dictionary<string, Dictionary<string, string>>
                {
                    ["Output"] = new Dictionary<string, string> { ["Mode"] = "Advanced" },
                    ["AdvOut"] = new Dictionary<string, string> { ["RecType"] = "Standard", ["RecTracks"] = "1" },
                    ["Video"] = new Dictionary<string, string>
                    {
                        ["BaseCX"] = "1920", ["BaseCY"] = "1080",
                        ["OutputCX"] = "1280", ["OutputCY"] = "720",
                        ["FPSCommon"] = "30",
                        ["ScaleType"] = "lanczos",
                    },
                },
                cqpTarget: 26),

            new RecordingPreset(
                name: "balanced",
                label: "Balanced (recommended)",
                description: "Native canvas -> 1080p60, high-quality target - good on most modern PCs.",
                basicIni: new Dictionary<string, Dictionary<string, string>>
                {
                    ["Output"] = new Dictionary<string, string> { ["Mode"] = "Advanced" },
                    ["AdvOut"] = new Dictionary<string, string> { ["RecType"] = "Standard", ["RecTracks"] = "1" },
                    ["Video"] = new Dictionary<string, string>
                    {
                        ["BaseCX"] = "1920", ["BaseCY"] = "1080",
                        ["OutputCX"] = "1920", ["OutputCY"] = "1080",
                        ["FPSCommon"] = "60",
                        ["ScaleType"] = "lanczos",
                    },
                },
                cqpTarget: 22),

            new RecordingPreset(
                name: "quality",
                label: "Quality (high-end PCs)",
                description: "Native canvas -> 1080p60, very high quality target. Best for replay clips.",
                basicIni: new Dictionary<string, Dictionary<string, string>>
                {
                    ["Output"] = new Dictionary<string, string> { ["Mode"] = "Advanced" },
                    ["AdvOut"] = new Dictionary<string, string> { ["RecType"] = "Standard", ["RecTracks"] = "1" },
                    ["Video"] = new Dictionary<string, string>
                    {
                        ["BaseCX"] = "2560", ["BaseCY"] = "1440",
                        ["OutputCX"] = "1920", ["OutputCY"] = "1080",
                        ["FPSCommon"] = "60",
                        ["ScaleType"] = "lanczos",
                    },
                },
                cqpTarget: 20),
        };

        public static RecordingPreset GetPreset(string name)
        {
            var preset = PRESETS.FirstOrDefault(p => p.Name == name);
            if (preset == null) throw new ArgumentException($"Unknown recording preset: '{name}'");
            return preset;
        }
    }
}

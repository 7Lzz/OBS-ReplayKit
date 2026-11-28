using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // the limits the clip editor offers and its plans are checked against; HttpResponse writes them into any page that names RK_EDITOR_LIMITS, so the editor keeps no copy of its own
    internal static class EditorLimits
    {
        // the whole video, a piece of it and a clip laid over it
        internal const double VideoSpeedMin = 0.1;
        internal const double VideoSpeedMax = 5;
        // a piece of audio, wider than the video range since a linked sound multiplies the speed of its video into its own
        internal const double AudioSpeedMin = 0.0625;
        internal const double AudioSpeedMax = 16;
        internal const int AudioRowsMax = 8;
        // the tracks one combined audio piece plays at once
        internal const int CombinedTracksMax = 8;
        internal const int LayersMax = 50;
        internal const int SubtitleTextMax = 300;
        internal const int PresetNameMax = 40;
        internal const double ClipSecondsMin = 0.5;
        internal const int VoiceoverSecondsMax = 15 * 60;
        internal const double PictureScaleMin = 0.1;
        internal const double PictureScaleMax = 3;
        internal const double LayerWidthMin = 0.02;
        internal const double LayerWidthMax = 4;
        internal const double LayerPosMin = -0.5;
        internal const double LayerPosMax = 1.5;

        internal static bool VideoSpeed(double speed) => speed >= VideoSpeedMin && speed <= VideoSpeedMax;
        internal static bool AudioSpeed(double speed) => speed >= AudioSpeedMin && speed <= AudioSpeedMax;

        internal static JObject Json() => new JObject
        {
            ["videoSpeed"] = new JObject { ["min"] = VideoSpeedMin, ["max"] = VideoSpeedMax },
            ["audioSpeed"] = new JObject { ["min"] = AudioSpeedMin, ["max"] = AudioSpeedMax },
            ["audioRows"] = AudioRowsMax,
            ["combinedTracks"] = CombinedTracksMax,
            ["layers"] = LayersMax,
            ["subtitleText"] = SubtitleTextMax,
            ["presetNameMax"] = PresetNameMax,
            ["clipSecondsMin"] = ClipSecondsMin,
            ["voiceoverSecondsMax"] = VoiceoverSecondsMax,
            ["pictureScale"] = new JObject { ["min"] = PictureScaleMin, ["max"] = PictureScaleMax },
            ["layerWidth"] = new JObject { ["min"] = LayerWidthMin, ["max"] = LayerWidthMax },
            ["layerPos"] = new JObject { ["min"] = LayerPosMin, ["max"] = LayerPosMax },
            ["aspects"] = EditorFrame.Json(),
            ["renderFps"] = new JArray(Trim.RenderOptions.Rates),
            ["speechLanguages"] = new JArray(Speech.LanguageOrder),
            ["importBase"] = AudioPlan.ImportBase,
            ["importBytes"] = AudioImports.MaxUploadBytes,
        };
    }
}

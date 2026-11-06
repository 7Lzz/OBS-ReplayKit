using System;
using Newtonsoft.Json.Linq;

namespace ReplayKitShared
{
    internal static class AudioVolume
    {
        public const int Max = 200;
        public const string SavedLevel = "volume_before_zero";

        public static double ToDb(int percent)
        {
            if (percent < 0 || percent > Max) throw new ArgumentOutOfRangeException(nameof(percent));
            return (percent - 100) * 0.3;
        }

        public static int FromDb(double db)
        {
            if (double.IsNaN(db) || double.IsInfinity(db)) throw new ArgumentOutOfRangeException(nameof(db));
            return (int)Math.Round(Math.Max(0, Math.Min(Max, 100 + db / 0.3)), MidpointRounding.AwayFromZero);
        }

        public static double ToGain(int percent) => percent == 0 ? 0 : Math.Pow(10, ToDb(percent) / 20);

        public static int FromGain(double gain)
        {
            if (double.IsNaN(gain) || double.IsInfinity(gain) || gain < 0) throw new ArgumentOutOfRangeException(nameof(gain));
            return gain == 0 ? 0 : FromDb(20 * Math.Log10(gain));
        }

        public static double Level(JToken token)
        {
            if (token == null || (token.Type != JTokenType.Float && token.Type != JTokenType.Integer))
                throw new InvalidOperationException("Invalid OBS input volume.");
            double level = token.Value<double>();
            if (double.IsNaN(level) || double.IsInfinity(level) || level < 0 || level > 20)
                throw new InvalidOperationException("Invalid OBS input volume.");
            return level;
        }

        public static JObject Prepare(JObject current, int percent, double inputLevel, out double outputLevel)
        {
            var next = current == null ? new JObject() : (JObject)current.DeepClone();
            next["db"] = ToDb(percent);
            outputLevel = inputLevel;
            if (percent == 0)
            {
                // The gain filter's -30 dB floor is not silence. Preserve the fader before setting it to zero.
                if (inputLevel != 0 || next[SavedLevel] == null) next[SavedLevel] = inputLevel;
                Level(next[SavedLevel]);
                outputLevel = 0;
            }
            else if (next[SavedLevel] != null)
            {
                double saved = Level(next[SavedLevel]);
                if (inputLevel == 0) outputLevel = saved;
                next.Remove(SavedLevel);
            }
            return next;
        }

        public static bool RepairSource(JObject source)
        {
            string filterName;
            switch (source.Value<string>("name"))
            {
                case "Audio Input Capture": filterName = "ReplayKit Mic Volume"; break;
                case "Desktop Audio (excl. Discord)": filterName = "ReplayKit Desktop Volume"; break;
                case "Discord Audio (record only)": filterName = "ReplayKit Discord Volume"; break;
                default: return false;
            }
            foreach (var token in source["filters"] as JArray ?? new JArray())
            {
                if (!(token is JObject filter) || filter.Value<string>("name") != filterName || filter.Value<string>("id") != "gain_filter" ||
                    filter["enabled"]?.Value<bool>() == false || !(filter["settings"] is JObject settings)) continue;
                int volume = FromDb(settings.Value<double>("db"));
                if (volume != 0 && settings[SavedLevel] == null) return false;
                double current = source["volume"] == null ? 1 : Level(source["volume"]);
                var next = Prepare(settings, volume, current, out double level);
                bool changed = !JToken.DeepEquals(settings, next) || level != current;
                if (changed) { filter["settings"] = next; source["volume"] = level; }
                return changed;
            }
            return false;
        }
    }
}

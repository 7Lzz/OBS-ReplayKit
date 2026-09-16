using System;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    internal static class SettingsStore
    {
        // Acquire before OBS apply/preview locks; keep a save and its live application ordered.
        internal static readonly object Gate = new object();

        public static void Update(Action<JObject> change)
        {
            lock (Gate)
            {
                var settings = ReplaykitSettings.ReadSettings();
                change(settings);
                ReplaykitSettings.WriteSettings(ReplaykitSettings.Normalize(settings));
            }
        }
    }
}

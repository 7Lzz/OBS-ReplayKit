using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitSetup
{
    // defaults a build turns on for installs that already exist. every step runs once per install: the id of a finished step goes into defaults_applied.json and from then on whatever the user sets stays as it is. runs at the end of a setup or an update with obs closed, since it edits the files of obs itself
    public static class DefaultsMigration
    {
        private const string ToolbarsStep = "toolbars-off-v1";
        private const string TracksStep = "separate-audio-tracks-v1";
        private const string NoiseStep = "mic-noise-suppression-v1";
        private const string OpenClipsKeyStep = "open-clips-alt-z-v1";

        private const string MicSourceName = "Audio Input Capture";
        private const string NoiseFilterName = "ReplayKit Noise Suppression";
        private const string NoiseFilterKindPrefix = "noise_suppress_filter";

        public static string MarkerPath => Path.Combine(Config.REPLAYKIT_CONFIG, "defaults_applied.json");

        private static string ScenePath => Path.Combine(Config.OBS_CONFIG, "basic", "scenes", "Untitled.json");
        private static string BasicIniPath => Path.Combine(Config.OBS_CONFIG, "basic", "profiles", "Untitled", "basic.ini");
        private static string UserIniPath => Path.Combine(Config.OBS_CONFIG, "user.ini");

        public static bool Run(Action<string> log = null)
        {
            HashSet<string> done = ReadDone(log);
            // fail closed: a marker that cannot be read could mean the defaults already ran, and running them again would put them over a choice the user made
            if (done == null) return false;
            bool complete = true;

            var steps = new (string Id, string What, Func<bool> Apply)[]
            {
                (ToolbarsStep, "source toolbar and status bar off", HideToolbarsAndStatusBar),
                (TracksStep, "separate audio tracks on", EnableSeparateAudioTracks),
                (NoiseStep, "mic noise suppression on", EnableMicNoiseSuppression),
                (OpenClipsKeyStep, "open clips hotkey alt + z", SetOpenClipsHotkey),
            };
            foreach (var step in steps)
            {
                if (done.Contains(step.Id)) continue;
                try
                {
                    if (!step.Apply())
                    {
                        log?.Invoke("warn: default not applied (" + step.What + "), it is tried again on the next update");
                        complete = false;
                        continue;
                    }
                    done.Add(step.Id);
                    WriteDone(done);
                    log?.Invoke("default applied: " + step.What);
                }
                // best effort on purpose: whatever goes wrong inside a step (a locked file, a scene that does not parse, a missing bundle) must never fail the install or the update around it
                catch (Exception ex)
                {
                    complete = false;
                    log?.Invoke("warn: default not applied (" + step.What + "): " + ex.Message);
                }
            }
            return complete;
        }

        private static HashSet<string> ReadDone(Action<string> log)
        {
            var done = new HashSet<string>(StringComparer.Ordinal);
            if (!File.Exists(MarkerPath)) return done;
            try
            {
                var marker = JObject.Parse(File.ReadAllText(MarkerPath));
                if (!(marker["steps"] is JArray steps)) throw new JsonException("The defaults marker has no valid steps list.");
                foreach (var id in steps)
                {
                    string value = id.Type == JTokenType.String ? id.Value<string>() : null;
                    if (string.IsNullOrEmpty(value)) throw new JsonException("The defaults marker contains an invalid step.");
                    done.Add(value);
                }
                return done;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                log?.Invoke("warn: default settings were not applied, " + MarkerPath + " cannot be read: " + ex.Message);
                return null;
            }
        }

        private static void WriteDone(HashSet<string> done)
        {
            var marker = new JObject
            {
                ["steps"] = new JArray(done.OrderBy(id => id, StringComparer.Ordinal)),
                ["version"] = VersionInfo.Version,
                ["updatedAt"] = DateTime.UtcNow.ToString("o"),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath));
            WriteText(MarkerPath, marker.ToString(Formatting.Indented) + "\n");
        }

        private static void WriteText(string path, string text) =>
            Installer.WriteWithRetry(() => File.WriteAllText(path, text, new UTF8Encoding(false)));

        // the view menu toggles of obs; a machine without a user.ini yet reads the bundled seed, which has both off
        private static bool HideToolbarsAndStatusBar()
        {
            if (!File.Exists(UserIniPath)) return true;
            string text = Config.ReadTextFileFlexible(UserIniPath);
            string next = Transform.SetIniValue(text, "BasicWindow", "ShowListboxToolbars", "false");
            next = Transform.SetIniValue(next, "BasicWindow", "ShowStatusBar", "false");
            if (!string.Equals(text, next, StringComparison.Ordinal)) WriteText(UserIniPath, next);
            return true;
        }

        // the open clips hotkey had no key before, so an install that never set one gets alt + z; a key someone picked stays
        private static bool SetOpenClipsHotkey()
        {
            if (!File.Exists(Prefs.RUNTIME_SETTINGS_FILE)) return true;
            var settings = JObject.Parse(File.ReadAllText(Prefs.RUNTIME_SETTINGS_FILE));
            if (settings["openClipsKeybind"] is JObject current && !string.IsNullOrWhiteSpace(current.Value<string>("key"))) return true;
            settings["openClipsKeybind"] = new JObject { ["alt"] = true, ["key"] = "OBS_KEY_Z" };
            WriteText(Prefs.RUNTIME_SETTINGS_FILE, settings.ToString(Formatting.Indented) + "\n");
            return true;
        }

        // the setting the helper reads, the track mask of the replaykit profile, and the track bits of the three recording audio sources in the scene
        private static bool EnableSeparateAudioTracks()
        {
            if (File.Exists(Prefs.RUNTIME_SETTINGS_FILE))
            {
                var settings = JObject.Parse(File.ReadAllText(Prefs.RUNTIME_SETTINGS_FILE));
                if (settings["recordingSeparateAudioTracks"]?.Type != JTokenType.Boolean || !settings.Value<bool>("recordingSeparateAudioTracks"))
                {
                    settings["recordingSeparateAudioTracks"] = true;
                    WriteText(Prefs.RUNTIME_SETTINGS_FILE, settings.ToString(Formatting.Indented) + "\n");
                }
            }
            if (File.Exists(BasicIniPath))
            {
                string text = Config.ReadTextFileFlexible(BasicIniPath);
                string next = Transform.SetIniValue(text, "AdvOut", "RecTracks", "15");
                if (!string.Equals(text, next, StringComparison.Ordinal)) WriteText(BasicIniPath, next);
            }
            return EditScene(data =>
            {
                bool changed = false;
                foreach (var token in data["sources"] as JArray ?? new JArray())
                {
                    if (token is JObject source && Transform.ApplySeparateTrackMixers(source)) changed = true;
                }
                return changed;
            });
        }

        // adds the rnnoise filter the bundled scene ships to the microphone, unless it already has a noise suppression filter -- one that was switched off is a choice and stays off
        private static bool EnableMicNoiseSuppression()
        {
            JObject filter = BundledNoiseFilter();
            return EditScene(data =>
            {
                foreach (var token in data["sources"] as JArray ?? new JArray())
                {
                    if (!(token is JObject source) || source.Value<string>("name") != MicSourceName) continue;
                    var filters = source["filters"] as JArray;
                    if (filters == null)
                    {
                        filters = new JArray();
                        source["filters"] = filters;
                    }
                    if (filters.OfType<JObject>().Any(f => (f.Value<string>("id") ?? "").StartsWith(NoiseFilterKindPrefix, StringComparison.Ordinal) ||
                                                           (f.Value<string>("versioned_id") ?? "").StartsWith(NoiseFilterKindPrefix, StringComparison.Ordinal)))
                        return false;
                    filters.Insert(0, filter.DeepClone());
                    return true;
                }
                return false;
            });
        }

        // the filter as the scene of this build ships it, so a new install and an updated one end up with the very same one
        private static JObject BundledNoiseFilter()
        {
            string bundled = Path.Combine(Config.OBS_ASSETS_DIR, "basic", "scenes", "Untitled.json");
            if (!File.Exists(bundled)) throw new FileNotFoundException("The bundled scene is missing: " + bundled);
            var data = JObject.Parse(Config.ReadTextFileFlexible(bundled));
            foreach (var token in data["sources"] as JArray ?? new JArray())
            {
                if (!(token is JObject source) || source.Value<string>("name") != MicSourceName) continue;
                foreach (var filter in source["filters"] as JArray ?? new JArray())
                {
                    if (filter is JObject f && f.Value<string>("name") == NoiseFilterName) return f;
                }
            }
            throw new InvalidDataException("The bundled scene has no " + NoiseFilterName + " filter.");
        }

        // runs the edit on the scene collection of replaykit and saves it when the edit says it changed something; no scene file means there is nothing to edit
        private static bool EditScene(Func<JObject, bool> edit)
        {
            if (!File.Exists(ScenePath)) return true;
            var data = JObject.Parse(Config.ReadTextFileFlexible(ScenePath));
            if (edit(data)) WriteText(ScenePath, data.ToString(Formatting.Indented) + "\n");
            return true;
        }
    }
}

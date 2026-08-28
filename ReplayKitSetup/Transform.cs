using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitSetup
{
    // splice user prefs into bundled obs config files (basic.ini, recordencoder.json, scenes json, user.ini, global.ini). ported from obs_replaykit/transform.py.
    public static class Transform
    {
        // targeted regex over configparser becuase obss basic.ini has formatting we want byte-identical (no spaces around =, blank lines, base64 blobs in geometry=).

        // set [section] key=value, preserving formatting. value is treated literally.
        public static string SetIniValue(string text, string section, string key, string value)
        {
            string keyRe = Regex.Escape(key);

            // both section and key present -> replace value in place.
            var updateRe = new Regex(@"(\[" + Regex.Escape(section) + @"\][^\[]*?\n)" + keyRe + "=[^\r\n]*", RegexOptions.Singleline);
            var updateMatch = updateRe.Match(text);
            if (updateMatch.Success)
            {
                return updateRe.Replace(text, m => m.Groups[1].Value + key + "=" + value, 1);
            }

            // section present, key missing -> insert before the next section header.
            var sectionRe = new Regex(@"(\[" + Regex.Escape(section) + @"\][^\[]*?)(?=\n\[|\z)", RegexOptions.Singleline);
            var m2 = sectionRe.Match(text);
            if (m2.Success)
            {
                string body = m2.Groups[1].Value.TrimEnd();
                return text.Substring(0, m2.Index) + body + $"\n{key}={value}\n" + text.Substring(m2.Index + m2.Length);
            }

            // section missing entirely -> append a fresh one at end of file.
            string sep = text.EndsWith("\n") ? "" : "\n";
            return $"{text}{sep}\n[{section}]\n{key}={value}\n";
        }

        // memoised so ApplyBasicIni and ApplyRecordEncoderJson agree on which encoder got picked.
        private static (string CodecPreference, string RecordingPreset, string CompressionMode, EncoderChoice Choice)? _encoderCache;

        // pick the recording encoder once per install, memoised across basic.ini + recordencoder.json writes.
        private static EncoderChoice ResolveEncoder(Preferences prefs)
        {
            if (_encoderCache.HasValue &&
                _encoderCache.Value.CodecPreference == prefs.CodecPreference &&
                _encoderCache.Value.RecordingPreset == prefs.RecordingPreset &&
                _encoderCache.Value.CompressionMode == prefs.CompressionMode)
            {
                return _encoderCache.Value.Choice;
            }
            var preset = Recording.GetPreset(prefs.RecordingPreset);
            var choice = Encoder.PickEncoder(Gpu.PrimaryGpu(), prefs.CodecPreference, preset.CqpTarget, prefs.CompressionMode);
            _encoderCache = (prefs.CodecPreference, prefs.RecordingPreset, prefs.CompressionMode, choice);
            return choice;
        }

        // force a re-pick on the next encoder resolve (e.g. user swapped gpu between runs).
        public static void ResetEncoderCache() => _encoderCache = null;

        private static int EvenDimension(double value)
        {
            int number = Math.Max(2, Math.Min(4096, (int)Math.Round(value, MidpointRounding.AwayFromZero)));
            if (number % 2 != 0) number -= 1;
            return Math.Max(2, number);
        }

        private static (int W, int H) ScaledEvenSize(int sourceW, int sourceH, int maxW, int maxH)
        {
            if (sourceW < 2 || sourceH < 2 || maxW < 2 || maxH < 2) return (1920, 1080);
            double scale = Math.Min(1.0, Math.Min((double)maxW / sourceW, (double)maxH / sourceH));
            return (EvenDimension(sourceW * scale), EvenDimension(sourceH * scale));
        }

        private static Dictionary<string, string> PresetVideoIni(Preferences prefs)
        {
            var preset = Recording.GetPreset(prefs.RecordingPreset);
            var video = preset.BasicIni.TryGetValue("Video", out var v) ? new Dictionary<string, string>(v) : new Dictionary<string, string>();
            var primary = Display.PrimaryDisplay();

            if (primary != null && primary.Width >= 320 && primary.Height >= 240)
            {
                int targetW = video.TryGetValue("OutputCX", out var ow) ? int.Parse(ow) : 1920;
                int targetH = video.TryGetValue("OutputCY", out var oh) ? int.Parse(oh) : 1080;
                var (baseW, baseH) = ScaledEvenSize(primary.Width, primary.Height, 4096, 4096);
                var (outputW, outputH) = ScaledEvenSize(baseW, baseH, targetW, targetH);
                video["BaseCX"] = baseW.ToString();
                video["BaseCY"] = baseH.ToString();
                video["OutputCX"] = outputW.ToString();
                video["OutputCY"] = outputH.ToString();
            }

            video["ScaleType"] = "lanczos";
            return video;
        }

        private const int MinRbSizeMb = 32;
        // realistic peak cqp bitrate per preset tier (mbps) -- a high-motion scene at that resolution/cqp_target, not a padded worst-of-worst-case number. cqp output size is content-driven so this is an estimate, not exact.
        private static readonly Dictionary<string, int> RbPeakMbps = new Dictionary<string, int> { ["performance"] = 8, ["balanced"] = 20, ["quality"] = 32 };
        private const double RbSafetyFactor = 1.5; // headroom over the peak estimate, not a second multiplier on top of an already-padded one

        // size the replay-buffer memory cap from an actual bitrate estimate for this preset x the users chosen buffer length, so it tracks ram usage realistically instead of ballooning at long buffer lengths.
        private static int ScaledRbSizeMb(RecordingPreset preset, Preferences prefs)
        {
            int peakMbps = RbPeakMbps.TryGetValue(preset.Name, out var v) ? v : RbPeakMbps["balanced"];
            double mbPerSecond = peakMbps * RbSafetyFactor / 8;
            return Math.Max(MinRbSizeMb, (int)Math.Ceiling(mbPerSecond * prefs.ReplayBufferSeconds));
        }

        public static string ApplyBasicIni(string text, Preferences prefs)
        {
            var preset = Recording.GetPreset(prefs.RecordingPreset);
            foreach (var kv in preset.BasicIni)
            {
                var items = kv.Key == "Video" ? PresetVideoIni(prefs) : kv.Value;
                foreach (var item in items) text = SetIniValue(text, kv.Key, item.Key, item.Value);
            }

            // advout.recencoder must match the encoder whose settings we write into recordencoder.json.
            var encoder = ResolveEncoder(prefs);
            text = SetIniValue(text, "AdvOut", "RecEncoder", encoder.ObsEncoderId);

            text = SetIniValue(text, "AdvOut", "RecRB", "true");
            text = SetIniValue(text, "AdvOut", "RecRBTime", prefs.ReplayBufferSeconds.ToString());
            text = SetIniValue(text, "AdvOut", "RecRBSize", ScaledRbSizeMb(preset, prefs).ToString());

            // forward slashes -- obs accepts them in ini values and skips escaping.
            string recPath = prefs.RecordingPath.Replace("\\", "/");
            text = SetIniValue(text, "SimpleOutput", "FilePath", recPath);
            text = SetIniValue(text, "AdvOut", "RecFilePath", recPath);
            text = SetIniValue(text, "AdvOut", "FFFilePath", recPath);

            text = SetIniValue(text, "Hotkeys", "ReplayBuffer", Keybind.ToBasicIniValue(prefs.ClipKeybind));
            if (prefs.RecordingKeybind != null && prefs.RecordingKeybind.Count > 0)
            {
                string recordingHotkey = Keybind.ToObsHotkeyValue(prefs.RecordingKeybind);
                text = SetIniValue(text, "Hotkeys", "OBSBasic.StartRecording", recordingHotkey);
                text = SetIniValue(text, "Hotkeys", "OBSBasic.StopRecording", recordingHotkey);
            }

            var monitor = FindObsStreamAudioRenderEndpoint();
            if (monitor != null)
            {
                text = SetIniValue(text, "Audio", "MonitoringDeviceId", monitor.Value.DeviceId);
                text = SetIniValue(text, "Audio", "MonitoringDeviceName", monitor.Value.Name);
            }

            return text;
        }

        private static (string DeviceId, string Name)? FindObsStreamAudioRenderEndpoint()
        {
            const string rootPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
            const string friendlyKey = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";
            var candidates = new List<(int Rank, string DisplayName, string DeviceId)>();

            using (var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(rootPath))
            {
                if (root == null) return null;
                foreach (var guid in root.GetSubKeyNames())
                {
                    string displayName;
                    try
                    {
                        using (var endpoint = root.OpenSubKey(guid))
                        {
                            var stateValue = endpoint?.GetValue("DeviceState");
                            if (stateValue == null) continue;
                            if ((Convert.ToInt32(stateValue) & 0x1) == 0) continue;
                        }
                        using (var props = root.OpenSubKey(guid + @"\Properties"))
                        {
                            var name = props?.GetValue(friendlyKey) as string;
                            if (name == null) continue;
                            displayName = name.Trim();
                        }
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException || ex is System.IO.IOException)
                    {
                        continue;
                    }

                    string canonical = Regex.Replace(displayName, @"^\s*\d+\s*-\s*", "").Trim().ToLowerInvariant();
                    if (Regex.IsMatch(canonical, "surround|16ch|loopback|do not select")) continue;
                    int rank = 999;
                    if (canonical == "obs stream audio") rank = 0;
                    else if (canonical.StartsWith("obs stream audio")) rank = 1;
                    else if (canonical.StartsWith("cable input")) rank = 2;
                    if (rank == 999) continue;
                    candidates.Add((rank, displayName, "{0.0.0.00000000}." + guid));
                }
            }

            if (candidates.Count == 0) return null;
            var best = candidates.OrderBy(c => c.Rank).ThenBy(c => c.DisplayName.ToLowerInvariant(), StringComparer.Ordinal).First();
            return (best.DeviceId, best.DisplayName);
        }

        // replace recordencoder.json with the gpu-aware encoder config picked in Encoder.cs.
        public static string ApplyRecordEncoderJson(string _text, Preferences prefs)
        {
            var encoder = ResolveEncoder(prefs);
            return JsonConvert.SerializeObject(encoder.Settings);
        }

        // stable uuid for our custom controls dock entry. repeat applies update the same row instead of stacking duplicates.
        private const string CustomControlsDockUuid = "a59ce0ef-5d6f-4a4f-91d9-c7c3c1d4e2b0";

        // bare windows path (no file:// prefix, backslashes) -- same shape obs writes when the user adds a custom browser dock via the dialog.
        private static string DockUrl() => System.IO.Path.Combine(Config.DOCK_TARGET, "controls_app.html").Replace("/", "\\");

        private static JObject ManagedDockEntry() => new JObject
        {
            ["title"] = "Custom Controls",
            ["url"] = DockUrl(),
            ["uuid"] = CustomControlsDockUuid,
        };

        // render basicwindow.extrabrowserdocks as json with backslashes double-escaped (obss ini parser unescapes once on read).
        private static string ExtraBrowserDocksValue()
        {
            string raw = JsonConvert.SerializeObject(new JArray { ManagedDockEntry() });
            return raw.Replace("\\", "\\\\");
        }

        // match the managed obs replaykit dock entry.
        private static bool IsObsReplaykitDock(JToken item)
        {
            if (!(item is JObject obj)) return false;
            string title = (obj.Value<string>("title") ?? "").Trim().ToLowerInvariant();
            string url = (obj.Value<string>("url") ?? "").Replace("\\", "/").ToLowerInvariant();
            string uuid = (obj.Value<string>("uuid") ?? "").Replace("-", "").ToLowerInvariant();
            string managedUuid = CustomControlsDockUuid.Replace("-", "").ToLowerInvariant();
            return uuid == managedUuid || title == "custom controls" ||
                   url.Contains("obs-replaykit/obs-custom-dock/controls.html") ||
                   url.Contains("obs-replaykit/obs-custom-dock/controls_app.html");
        }

        // inject our custom controls entry into the existing extrabrowserdocks array, replacing any stale obs-replaykit row. other user-added docks are preserved.
        private static string MergeExtraBrowserDocksValue(string existing)
        {
            JArray docks;
            try
            {
                string unescaped = existing.Replace("\\\\", "\\");
                var parsed = JsonConvert.DeserializeObject<JToken>(unescaped);
                docks = parsed as JArray ?? new JArray();
            }
            catch (JsonException)
            {
                docks = new JArray();
            }

            var target = ManagedDockEntry();
            var rebuilt = new JArray();
            bool inserted = false;
            foreach (var item in docks)
            {
                if (IsObsReplaykitDock(item))
                {
                    if (!inserted)
                    {
                        rebuilt.Add(target);
                        inserted = true;
                    }
                }
                else
                {
                    rebuilt.Add(item);
                }
            }
            if (!inserted) rebuilt.Add(target);

            return JsonConvert.SerializeObject(rebuilt).Replace("\\", "\\\\");
        }

        // raw value of [BasicWindow] <key>= from the live user.ini, or null. used to preserve dockstate and feed the extrabrowserdocks merge.
        private static string ReadLiveUserIniValue(string key)
        {
            string livePath = System.IO.Path.Combine(Config.OBS_CONFIG, "user.ini");
            if (!System.IO.File.Exists(livePath)) return null;
            string liveText;
            try
            {
                liveText = System.IO.File.ReadAllText(livePath, System.Text.Encoding.UTF8);
            }
            catch (System.IO.IOException)
            {
                return null;
            }
            var m = Regex.Match(liveText, @"\[BasicWindow\][^\[]*?\n" + Regex.Escape(key) + @"=([^\r\n]*)", RegexOptions.Singleline);
            return m.Success ? m.Groups[1].Value : null;
        }

        // write one canonical custom controls dock entry and reset stale dock layout state.
        public static string ApplyUserIni(string text, Preferences prefs)
        {
            var sectionRe = new Regex(@"(\[BasicWindow\][^\[]*?\n)ExtraBrowserDocks=([^\r\n]*)", RegexOptions.Singleline);
            var m = sectionRe.Match(text);
            if (m.Success)
            {
                // fold the live ini into the bundle seed so user-added docks are preserved.
                string liveValue = ReadLiveUserIniValue("ExtraBrowserDocks") ?? "";
                string seed = m.Groups[2].Value;
                if (liveValue.Length > 0 && liveValue != seed) seed = CombineExtraBrowserDocks(seed, liveValue);
                string merged = MergeExtraBrowserDocksValue(seed);
                text = sectionRe.Replace(text, mm => m.Groups[1].Value + "ExtraBrowserDocks=" + merged, 1);
                return SetIniValue(text, "General", "ConfirmOnExit", ConfirmOnExitValue(prefs));
            }

            text = SetIniValue(text, "BasicWindow", "ExtraBrowserDocks", ExtraBrowserDocksValue());
            return SetIniValue(text, "General", "ConfirmOnExit", ConfirmOnExitValue(prefs));
        }

        // concat bundle + live extrabrowserdocks arrays, dedupe by json serialisation. obs-custom-dock duplicates are collapsed in the downstream merge.
        private static string CombineExtraBrowserDocks(string bundleValue, string liveValue)
        {
            JArray Decode(string value)
            {
                try
                {
                    string unescaped = value.Replace("\\\\", "\\");
                    var parsed = JsonConvert.DeserializeObject<JToken>(unescaped);
                    return parsed as JArray ?? new JArray();
                }
                catch (JsonException)
                {
                    return new JArray();
                }
            }

            var combined = new JArray();
            var seen = new HashSet<string>();
            foreach (var item in Decode(bundleValue).Concat(Decode(liveValue)))
            {
                string key = JsonConvert.SerializeObject(SortKeysDeep(item));
                if (seen.Add(key)) combined.Add(item);
            }

            return JsonConvert.SerializeObject(combined).Replace("\\", "\\\\");
        }

        // recursively sorts object keys so json comparison matches pythons json.dumps(item, sort_keys=True) semantics.
        private static JToken SortKeysDeep(JToken token)
        {
            if (token is JObject obj)
            {
                var sorted = new JObject();
                foreach (var prop in obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    sorted[prop.Name] = SortKeysDeep(prop.Value);
                }
                return sorted;
            }
            if (token is JArray arr)
            {
                var sortedArr = new JArray();
                foreach (var item in arr) sortedArr.Add(SortKeysDeep(item));
                return sortedArr;
            }
            return token;
        }

        // global.ini is merged not overwritten -- preserve installguid, [crashhandler], [locations], and any input-overlay plugin settings; only force-set the perf/ux toggles below.
        private static readonly (string Section, string Key, string Value)[] EnforcedGlobalIni =
        {
            ("General", "BrowserHWAccel", "true"),
            ("Audio", "DisableAudioDucking", "true"),
        };

        private static string ConfirmOnExitValue(Preferences prefs) => prefs.DisableObsCloseWarning ? "false" : "true";

        // merge the bundled global.ini into the users existing file -- preserves installguid, [crashhandler], [locations], input-overlay plugin settings; force-sets browserhwaccel + disableaudioducking.
        public static string ApplyGlobalIni(string text, Preferences prefs)
        {
            string existingPath = System.IO.Path.Combine(Config.OBS_CONFIG, "global.ini");
            string @base;
            if (System.IO.File.Exists(existingPath))
            {
                @base = Config.ReadTextFileFlexible(existingPath);
            }
            else
            {
                @base = text; // fresh install -- start from the bundled defaults.
            }

            foreach (var (section, key, value) in EnforcedGlobalIni) @base = SetIniValue(@base, section, key, value);
            @base = SetIniValue(@base, "General", "ConfirmOnExit", ConfirmOnExitValue(prefs));
            return @base;
        }

        // scenes json editing

        private const string InputOverlaySourceId = "input-overlay";
        private const string InputOverlayWasdName = "WASD Overlay";
        private const string InputOverlayMouseName = "Mouse Overlay";
        private const double InputOverlayGroupW = 628.0;
        private const double InputOverlayGroupH = 292.0;
        private const double InputOverlayMouseOffsetX = 431.0;
        private const double InputOverlayWasdSourceW = 568.0;
        private const double InputOverlayWasdSourceH = 394.0;
        private const double InputOverlayMouseSourceW = 285.0;
        private const double InputOverlayMouseSourceH = 421.0;
        private const double InputOverlayWasdScaleX = 0.7395833134651184;
        private const double InputOverlayWasdScaleY = 0.7388888597488403;
        private const double InputOverlayMouseScale = 0.6909722089767456;
        private const double InputOverlayPosRefH = 1080.0;
        private const double InputOverlayPosXAtRef = 15.0;
        private const double InputOverlayBottomMarginAtRef = 16.0;
        private const string OverlayOpacityFilterName = "ReplayKit Overlay Opacity";
        private const string OverlayOpacityFilterId = "color_filter";
        private const string BongoCatSourceId = "bongobs-cat";
        private const string BongoCatSourceName = "Bongo Cat Overlay";
        private const string BongoCatSourceUuid = "c93f3934-0dfd-4f4f-96e4-0abf45423f0f";
        private const double BongoCatSourceW = 1280.0;
        private const double BongoCatSourceH = 768.0;
        private const double BongoCatCanvasH = 1080.0;
        private const double BongoCatScaleX = 0.4892578125;
        private const double BongoCatScaleY = 0.48828125;
        private const int BongoCatItemId = 13;
        private static readonly Dictionary<string, string> OverlayOpacityFilterUuids = new Dictionary<string, string>
        {
            [InputOverlayWasdName] = "a65fb4f0-a894-463e-9b9b-f0a9d5fb4fa1",
            [InputOverlayMouseName] = "c097fe72-641f-4da5-94f6-71f7c6353f9f",
            [BongoCatSourceName] = "4ecb70c4-e8f0-4207-a2cc-0307ff771722",
        };
        private const string MicSourceId = "wasapi_input_capture";
        private const string MonitorSourceId = "monitor_capture";
        private const string MotionBlurFilterName = "ReplayKit Motion Blur";
        private const string MotionBlurFilterId = "shader_filter";
        private const string RetiredMotionBlurFilterId = "obs_composite_blur";
        private const string GameCaptureSourceId = "game_capture";
        private const string GameCaptureSourceName = "Game Capture";
        private const int GameCaptureHookRateFast = 2;
        private const string WindowCaptureSourceId = "window_capture";
        private const string WindowCaptureSourceName = "Window Capture";
        private const string WindowCaptureSourceUuid = "edb2d9d4-7b53-4f3a-a760-61cd03ce9b6c";
        private const string DesktopAudioSourceName = "Desktop Audio (excl. Discord)";
        private static readonly string[] DesktopAudioExcludeProcesses =
        {
            "Discord.exe", "DiscordSystemHelper.exe", "DiscordCanary.exe", "DiscordPTB.exe", "DiscordDevelopment.exe",
            "obs64.exe", "obs32.exe", "obs.exe",
            // obss cef subprocess -- excluded so clip playback audio in the dock isnt captured as desktop audio and monitored back into the discord share, doubling the copy discord already grabs from the same process tree.
            "obs-browser-page.exe",
        };
        private static readonly Dictionary<string, string> MotionBlurSourceUuids = new Dictionary<string, string>
        {
            ["Display Capture"] = "e371efc8-8c99-44cb-95e7-94381d9c9e41",
            ["Game Capture"] = "26bc5a11-5315-4390-b028-f77667c7fda3",
            ["Window Capture"] = "9b73d6cb-b65e-44a1-895f-4e2f326a8d77",
        };

        // match an input-overlay preset path and capture the tail so it can be re-rooted.
        private static readonly Regex InputOverlayPathRe = new Regex(@"^.*?-presets[\\/](?<tail>[^\r\n]+)$");

        private const int ObsBoundsScaleInner = 2; // libobs obs_bounds_type from scene-item-properties.cpp -- fit inside bounds, preserve aspect.

        // single obs-visible script entry; feature scripts are loaded inside replaykit.lua to keep tools->scripts uncluttered.
        private const string ReplaykitScriptRelpath = "obs-replayKit/scripts/replaykit.lua";

        // forward-slashed, lowercased ~/pictures/videos -- matches the lua and ps fallback.
        private static string DefaultClipDirNorm() => System.IO.Path.Combine(Config.USERPROFILE, "Pictures", "Videos").Replace('\\', '/').ToLowerInvariant();

        // inject replaykit runtime settings into the managed obs script entry.
        private static void ApplyReplaykitRuntimeSettings(JObject settings, Preferences prefs)
        {
            string recDirNorm = prefs.RecordingPath.Replace("\\", "/").TrimEnd('/').ToLowerInvariant();
            settings["clip_dir"] = recDirNorm == DefaultClipDirNorm() ? "" : prefs.RecordingPath.Replace("\\", "/");
            settings["clip_notification_enabled"] = prefs.ClipNotificationEnabled;
            settings["recording_notification_enabled"] = prefs.RecordingNotificationEnabled;
            settings["clip_notification_seconds"] = prefs.ReplayBufferSeconds;
            settings["clip_sound_volume"] = prefs.ClipSoundVolume;
            settings["recording_sound_volume"] = prefs.RecordingSoundVolume;
        }

        // write the runtime settings consumed by the obs dock helper.
        public static string ApplyReplaykitSettingsJson(string _text, Preferences prefs)
        {
            string recDirNorm = prefs.RecordingPath.Replace("\\", "/").TrimEnd('/').ToLowerInvariant();
            string clipDir = recDirNorm == DefaultClipDirNorm() ? "" : prefs.RecordingPath.Replace("\\", "/");
            var obj = new JObject
            {
                ["recordingPreset"] = prefs.RecordingPreset,
                ["compressionMode"] = prefs.CompressionMode,
                ["codecPreference"] = prefs.CodecPreference,
                ["replaySeconds"] = prefs.ReplayBufferSeconds,
                ["clipDir"] = clipDir,
                ["clipKeybind"] = JObject.FromObject(prefs.ClipKeybind),
                ["recordingKeybind"] = JObject.FromObject(prefs.RecordingKeybind),
                ["overlayStyle"] = prefs.OverlayStyle,
                ["overlayOpacity"] = prefs.OverlayOpacity,
                ["overlayScale"] = prefs.OverlayScale,
                ["overlayHueShift"] = prefs.OverlayHueShift,
                ["overlayColorMultiply"] = prefs.OverlayColorMultiply,
                ["overlayColorAdd"] = prefs.OverlayColorAdd,
                ["obsStartupEnabled"] = prefs.ObsStartupEnabled,
                ["disableObsCloseWarning"] = prefs.DisableObsCloseWarning,
                ["closeToTray"] = prefs.CloseToTray,
                ["allowSleepWhileActive"] = prefs.AllowSleepWhileActive,
                ["clipNotificationEnabled"] = prefs.ClipNotificationEnabled,
                ["recordingNotificationEnabled"] = prefs.RecordingNotificationEnabled,
                ["clipNotificationSeconds"] = prefs.ReplayBufferSeconds,
                ["trimPreciseDefault"] = prefs.TrimPreciseDefault,
                ["debugLoggingEnabled"] = prefs.DebugLoggingEnabled,
                ["clipSoundVolume"] = prefs.ClipSoundVolume,
                ["recordingSoundVolume"] = prefs.RecordingSoundVolume,
                ["shareMode"] = prefs.ShareMode,
                ["discord_screenshare_enabled"] = prefs.DiscordScreenshareEnabled,
                ["discord_output_mode"] = prefs.DiscordOutputMode,
                ["discord_projector_enabled"] = prefs.DiscordProjectorEnabled,
                ["discord_projector_width"] = prefs.DiscordProjectorWidth,
                ["discord_projector_height"] = prefs.DiscordProjectorHeight,
                ["discord_projector_visible_pixels"] = prefs.DiscordProjectorVisiblePixels,
                ["discord_projector_monitor_index"] = prefs.DiscordProjectorMonitorIndex,
                ["discord_projector_edge"] = prefs.DiscordProjectorEdge,
                ["discord_projector_title_hint"] = prefs.DiscordProjectorTitleHint,
                ["discord_projector_hide_taskbar"] = prefs.DiscordProjectorHideTaskbar,
                ["screenshareCaptureMode"] = prefs.ScreenshareCaptureMode,
                ["screenshareGameWindow"] = prefs.ScreenshareGameWindow,
                ["screenshareGameOverrides"] = JArray.FromObject(prefs.ScreenshareGameOverrides),
                ["screenshareAutoGameKeepFocused"] = prefs.ScreenshareAutoGameKeepFocused,
                ["motionBlurEnabled"] = prefs.MotionBlurEnabled,
                ["motionBlurStrength"] = prefs.MotionBlurStrength,
            };
            return obj.ToString(Formatting.Indented); // matches pythons json.dumps({...}, indent=2) -- newtonsofts default indent width is also 2
        }

        private static string ReplaykitScriptPath() => System.IO.Path.Combine(Config.OBS_CONFIG, ReplaykitScriptRelpath.Replace('/', System.IO.Path.DirectorySeparatorChar)).Replace('\\', '/');

        private static string EntryBasename(JObject entry)
        {
            string path = (entry.Value<string>("path") ?? "").Replace("\\", "/");
            var parts = path.Split('/');
            return parts[parts.Length - 1].ToLowerInvariant();
        }

        private static bool IsReplaykitEntry(JObject entry) => EntryBasename(entry) == System.IO.Path.GetFileName(ReplaykitScriptRelpath).ToLowerInvariant();

        // preserve settings from the managed replaykit.lua script entry.
        private static JObject CollectReplaykitSettings(JArray scripts, Preferences prefs)
        {
            var settings = new JObject();
            foreach (var entryToken in scripts)
            {
                if (!(entryToken is JObject entry) || !IsReplaykitEntry(entry)) continue;
                if (!(entry["settings"] is JObject entrySettings)) continue;
                foreach (var prop in entrySettings.Properties()) settings[prop.Name] = prop.Value;
            }
            ApplyReplaykitRuntimeSettings(settings, prefs);
            return settings;
        }

        // fit a scene item to canvas (obs ctrl+f equivalent). sourceW/H zero -> bounds-based fallback for sources without known dimensions (game capture, no display detected).
        private static void FitSceneItemToCanvas(JObject item, double canvasW, double canvasH, int sourceW = 0, int sourceH = 0)
        {
            if (sourceW > 0 && sourceH > 0)
            {
                // explicit-scale path: compute scale + centered pos. modern obs prefers pos_rel/bounds_rel (canvas-relative) over absolute pos/bounds, so we set both.
                double scale = Math.Min(canvasW / sourceW, canvasH / sourceH);
                double scaledW = sourceW * scale;
                double scaledH = sourceH * scale;
                double posX = (canvasW - scaledW) / 2.0;
                double posY = (canvasH - scaledH) / 2.0;

                item["align"] = 5; // obs_align_left | obs_align_top -- pos is top-left
                item["pos"] = new JObject { ["x"] = posX, ["y"] = posY };
                item["pos_rel"] = new JObject { ["x"] = (posX - canvasW / 2.0) / (canvasH / 2.0), ["y"] = (posY - canvasH / 2.0) / (canvasH / 2.0) };
                item["scale"] = new JObject { ["x"] = scale, ["y"] = scale };
                item["scale_rel"] = new JObject { ["x"] = 1.0, ["y"] = 1.0 };
                item["scale_ref"] = new JObject { ["x"] = (double)sourceW, ["y"] = (double)sourceH };
                item["bounds"] = new JObject { ["x"] = 0.0, ["y"] = 0.0 };
                item["bounds_rel"] = new JObject { ["x"] = 0.0, ["y"] = 0.0 };
                item["bounds_type"] = 0;
                item["bounds_align"] = 0;
            }
            else
            {
                // bounds-based fit. also clears bounds_rel so the bounds actualy take effect.
                item["align"] = 5;
                item["pos"] = new JObject { ["x"] = 0.0, ["y"] = 0.0 };
                item["pos_rel"] = new JObject { ["x"] = -canvasW / canvasH, ["y"] = -1.0 };
                item["bounds"] = new JObject { ["x"] = canvasW, ["y"] = canvasH };
                item["bounds_rel"] = new JObject { ["x"] = 2.0 * canvasW / canvasH, ["y"] = 2.0 };
                item["bounds_type"] = ObsBoundsScaleInner;
                item["bounds_align"] = 0;
            }
        }

        // (basecx, basecy) that the selected preset writes into basic.ini.
        private static (int W, int H) CanvasSize(Preferences prefs)
        {
            var video = PresetVideoIni(prefs);
            return (video.TryGetValue("BaseCX", out var w) ? int.Parse(w) : 1920, video.TryGetValue("BaseCY", out var h) ? int.Parse(h) : 1080);
        }

        // re-root one io.overlay_image / io.layout_file under the installs preset folder. non-preset paths are left alone.
        private static string RewriteOverlayPath(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            var match = InputOverlayPathRe.Match(value.Replace("\\", "/"));
            if (!match.Success) return value;
            string tail = match.Groups["tail"].Value.TrimStart('/', '\\');
            var parts = tail.Replace("\\", "/").Split('/').Where(p => p.Length > 0).ToArray();
            if (parts.Length > 0 && parts[0].ToLowerInvariant().StartsWith("input-overlay") && parts[0].ToLowerInvariant().EndsWith("-presets"))
            {
                tail = string.Join("/", parts.Skip(1));
            }
            return System.IO.Path.Combine(Config.INPUT_OVERLAY_TARGET, tail.Replace('/', System.IO.Path.DirectorySeparatorChar)).Replace('\\', '/');
        }

        // re-root the input-overlay sources image/layout settings in place.
        private static void ResolveOverlayPaths(JObject src)
        {
            if (!(src["settings"] is JObject settings))
            {
                settings = new JObject();
                src["settings"] = settings;
            }
            foreach (var key in new[] { "io.overlay_image", "io.layout_file" })
            {
                if (settings[key]?.Type == JTokenType.String)
                {
                    settings[key] = RewriteOverlayPath(settings.Value<string>(key));
                }
            }
        }

        // uuids of every input-overlay source in the scene file.
        private static HashSet<string> CollectOverlayUuids(JArray sources)
        {
            var result = new HashSet<string>();
            foreach (var s in sources.OfType<JObject>())
            {
                if (s.Value<string>("id") == InputOverlaySourceId)
                {
                    string uuid = s.Value<string>("uuid");
                    if (!string.IsNullOrEmpty(uuid)) result.Add(uuid);
                }
            }
            return result;
        }

        // names of groups containing input-overlay sources -- the new scene structure wraps the overlay sources in a group and toggles visibility on the group.
        private static HashSet<string> CollectOverlayGroupNames(JArray sources, JArray groups)
        {
            var overlayUuids = CollectOverlayUuids(sources);
            var overlayGroups = new HashSet<string>();
            if (overlayUuids.Count == 0) return overlayGroups;

            foreach (var grpToken in groups)
            {
                if (!(grpToken is JObject grp)) continue;
                var items = (grp["settings"] as JObject)?["items"] as JArray ?? new JArray();
                foreach (var itemToken in items)
                {
                    if (itemToken is JObject item && overlayUuids.Contains(item.Value<string>("source_uuid") ?? ""))
                    {
                        string name = grp.Value<string>("name");
                        if (!string.IsNullOrEmpty(name)) overlayGroups.Add(name);
                        break;
                    }
                }
            }
            return overlayGroups;
        }

        private static string SelectedOverlayStyle(Preferences prefs)
        {
            string style = prefs.OverlayStyle ?? "input_overlay";
            return style == "input_overlay" || style == "bongo_cat" || style == "off" ? style : "input_overlay";
        }

        private static bool IsInputOverlayGroup(JObject group, HashSet<string> overlayUuids)
        {
            var items = (group["settings"] as JObject)?["items"] as JArray ?? new JArray();
            foreach (var itemToken in items)
            {
                if (itemToken is JObject item && overlayUuids.Contains(item.Value<string>("source_uuid") ?? "")) return true;
            }
            return false;
        }

        private static void RemoveSourcesByUuidOrId(JArray sources, HashSet<string> uuids, string sourceId)
        {
            var toRemove = sources.Where(s => (s as JObject)?.Value<string>("id") == sourceId || uuids.Contains((s as JObject)?.Value<string>("uuid") ?? "\0")).ToList();
            foreach (var item in toRemove) sources.Remove(item);
        }

        private static void RemoveBongoSources(JArray sources) => RemoveSourcesByUuidOrId(sources, new HashSet<string> { BongoCatSourceUuid }, BongoCatSourceId);

        private static string MotionBlurShaderPath() => System.IO.Path.Combine(Config.PROGRAMFILES_OBS_DIR, "data", "obs-plugins", "obs-shaderfilter", "examples", "motion_blur.shader").Replace('\\', '/');

        private static JObject MotionBlurFilterSettings(double strength) => new JObject
        {
            ["from_file"] = true,
            ["shader_file_name"] = MotionBlurShaderPath(),
            ["override_entire_effect"] = false,
            ["strength"] = Math.Max(0.0, Math.Min(1.0, strength)),
        };

        private static JObject NewMotionBlurFilter(string sourceName, bool enabled, double strength) => new JObject
        {
            ["prev_ver"] = 536936450,
            ["name"] = MotionBlurFilterName,
            ["uuid"] = MotionBlurSourceUuids[sourceName],
            ["id"] = MotionBlurFilterId,
            ["versioned_id"] = MotionBlurFilterId,
            ["settings"] = MotionBlurFilterSettings(strength),
            ["mixers"] = 0,
            ["sync"] = 0,
            ["flags"] = 0,
            ["volume"] = 1.0,
            ["balance"] = 0.5,
            ["enabled"] = enabled,
            ["muted"] = false,
            ["push-to-mute"] = false,
            ["push-to-mute-delay"] = 0,
            ["push-to-talk"] = false,
            ["push-to-talk-delay"] = 0,
            ["hotkeys"] = new JObject(),
            ["deinterlace_mode"] = 0,
            ["deinterlace_field_order"] = 0,
            ["monitoring_type"] = 0,
            ["private_settings"] = new JObject(),
        };

        private static bool IsShaderfilterMotionBlur(JObject filterObj)
        {
            string shaderFile = ((filterObj["settings"] as JObject)?.Value<string>("shader_file_name") ?? "").Replace("\\", "/").ToLowerInvariant();
            return filterObj.Value<string>("id") == MotionBlurFilterId && shaderFile.EndsWith("/motion_blur.shader");
        }

        private static void ApplyMotionBlurFilter(JObject src, bool enabled, double strength)
        {
            string sourceName = src.Value<string>("name") ?? "";
            if (!MotionBlurSourceUuids.ContainsKey(sourceName)) return;
            var filters = src["filters"] as JArray;
            if (filters == null)
            {
                filters = new JArray();
                src["filters"] = filters;
            }

            bool found = false;
            foreach (var filterToken in filters)
            {
                if (!(filterToken is JObject filterObj)) continue;
                if (filterObj.Value<string>("name") == MotionBlurFilterName ||
                    filterObj.Value<string>("uuid") == MotionBlurSourceUuids[sourceName] ||
                    filterObj.Value<string>("id") == RetiredMotionBlurFilterId ||
                    IsShaderfilterMotionBlur(filterObj))
                {
                    foreach (var prop in NewMotionBlurFilter(sourceName, enabled, strength).Properties()) filterObj[prop.Name] = prop.Value;
                    found = true;
                    break;
                }
            }
            if (!found) filters.Add(NewMotionBlurFilter(sourceName, enabled, strength));
        }

        private static void RemoveMotionBlurFilter(JObject src)
        {
            if (!(src["filters"] is JArray filters)) return;
            string managedUuid = MotionBlurSourceUuids.TryGetValue(src.Value<string>("name") ?? "", out var u) ? u : null;
            var kept = filters.Where(item =>
            {
                var obj = item as JObject;
                return obj?.Value<string>("name") != MotionBlurFilterName &&
                       obj?.Value<string>("uuid") != managedUuid &&
                       obj?.Value<string>("id") != RetiredMotionBlurFilterId &&
                       !(obj != null && IsShaderfilterMotionBlur(obj));
            }).ToList();
            filters.Clear();
            foreach (var item in kept) filters.Add(item);
            if (filters.Count == 0) src.Remove("filters");
        }

        private static JObject NewBongoSource() => new JObject
        {
            ["prev_ver"] = 536936450,
            ["name"] = BongoCatSourceName,
            ["uuid"] = BongoCatSourceUuid,
            ["id"] = BongoCatSourceId,
            ["versioned_id"] = BongoCatSourceId,
            ["settings"] = new JObject
            {
                ["Mode"] = "standard",
                ["width"] = (int)BongoCatSourceW,
                ["height"] = (int)BongoCatSourceH,
                ["x"] = 0.0,
                ["y"] = 0.02,
                ["scale"] = 1.83,
                ["delay"] = 1.0,
                ["delaytime"] = 1.0,
                ["random_motion"] = true,
                ["breath"] = true,
                ["eyeblink"] = true,
                ["track"] = true,
                ["live2d"] = true,
                ["relative_mouse"] = true,
                ["mouse_horizontal_flip"] = true,
                ["mouse_vertical_flip"] = true,
                ["mask"] = false,
            },
            ["mixers"] = 0,
            ["sync"] = 0,
            ["flags"] = 0,
            ["volume"] = 1.0,
            ["balance"] = 0.5,
            ["enabled"] = true,
            ["muted"] = false,
            ["push-to-mute"] = false,
            ["push-to-mute-delay"] = 0,
            ["push-to-talk"] = false,
            ["push-to-talk-delay"] = 0,
            ["hotkeys"] = new JObject(),
            ["deinterlace_mode"] = 0,
            ["deinterlace_field_order"] = 0,
            ["monitoring_type"] = 0,
            ["private_settings"] = new JObject(),
        };

        private static void EnsureBongoSource(JArray sources)
        {
            foreach (var srcToken in sources)
            {
                if (!(srcToken is JObject src)) continue;
                if (src.Value<string>("uuid") == BongoCatSourceUuid || src.Value<string>("id") == BongoCatSourceId)
                {
                    src["name"] = BongoCatSourceName;
                    src["id"] = BongoCatSourceId;
                    src["versioned_id"] = BongoCatSourceId;
                    src["enabled"] = true;
                    if (!(src["settings"] is JObject settings)) { settings = new JObject(); src["settings"] = settings; }
                    foreach (var prop in ((JObject)NewBongoSource()["settings"]).Properties()) settings[prop.Name] = prop.Value;
                    return;
                }
            }
            sources.Add(NewBongoSource());
        }

        private static JObject WindowCaptureInputSettings(Preferences prefs) => new JObject
        {
            ["window"] = prefs.ScreenshareGameWindow ?? "",
            ["method"] = 2,
            ["priority"] = 0,
            ["cursor"] = true,
            ["client_area"] = true,
            ["compatibility"] = false,
            ["force_sdr"] = false,
            ["capture_audio"] = false,
        };

        private static JObject NewWindowCaptureSource(Preferences prefs) => new JObject
        {
            ["prev_ver"] = 536936450,
            ["name"] = WindowCaptureSourceName,
            ["uuid"] = WindowCaptureSourceUuid,
            ["id"] = WindowCaptureSourceId,
            ["versioned_id"] = WindowCaptureSourceId,
            ["settings"] = WindowCaptureInputSettings(prefs),
            ["mixers"] = 0,
            ["sync"] = 0,
            ["flags"] = 0,
            ["volume"] = 1.0,
            ["balance"] = 0.5,
            ["enabled"] = true,
            ["muted"] = false,
            ["push-to-mute"] = false,
            ["push-to-mute-delay"] = 0,
            ["push-to-talk"] = false,
            ["push-to-talk-delay"] = 0,
            ["hotkeys"] = new JObject(),
            ["deinterlace_mode"] = 0,
            ["deinterlace_field_order"] = 0,
            ["monitoring_type"] = 0,
            ["private_settings"] = new JObject(),
        };

        private static void EnsureWindowCaptureSource(JArray sources, Preferences prefs)
        {
            foreach (var srcToken in sources)
            {
                if (!(srcToken is JObject src)) continue;
                if (src.Value<string>("uuid") == WindowCaptureSourceUuid || src.Value<string>("name") == WindowCaptureSourceName)
                {
                    src["name"] = WindowCaptureSourceName;
                    src["uuid"] = WindowCaptureSourceUuid;
                    src["id"] = WindowCaptureSourceId;
                    src["versioned_id"] = WindowCaptureSourceId;
                    src["enabled"] = true;
                    if (!(src["settings"] is JObject settings)) { settings = new JObject(); src["settings"] = settings; }
                    foreach (var prop in WindowCaptureInputSettings(prefs).Properties()) settings[prop.Name] = prop.Value;
                    return;
                }
            }
            sources.Add(NewWindowCaptureSource(prefs));
        }

        private static HashSet<int> SceneItemIds(JArray items)
        {
            var ids = new HashSet<int>();
            foreach (var itemToken in items)
            {
                if (itemToken is JObject item && item["id"] != null && int.TryParse(item["id"].ToString(), out int id)) ids.Add(id);
            }
            return ids;
        }

        private static int NextSceneItemId(JArray items)
        {
            var used = SceneItemIds(items);
            int candidate = BongoCatItemId;
            while (used.Contains(candidate)) candidate++;
            return candidate;
        }

        private static void MoveSceneItemAfter(JArray items, string targetName, string afterName)
        {
            JObject target = null;
            foreach (var itemToken in items)
            {
                if (itemToken is JObject item && item.Value<string>("name") == targetName) { target = item; break; }
            }
            if (target == null) return;
            items.Remove(target);
            int insertAt = 0;
            for (int i = 0; i < items.Count; i++)
            {
                if ((items[i] as JObject)?.Value<string>("name") == afterName) { insertAt = i + 1; break; }
            }
            items.Insert(insertAt, target);
        }

        private static JObject NewCaptureSceneItem(string sourceName, string sourceUuid, int itemId, double canvasW, double canvasH, bool visible)
        {
            var item = new JObject
            {
                ["name"] = sourceName,
                ["source_uuid"] = sourceUuid,
                ["visible"] = visible,
                ["locked"] = false,
                ["rot"] = 0.0,
                ["align"] = 5,
                ["bounds_type"] = 0,
                ["bounds_align"] = 0,
                ["bounds_crop"] = false,
                ["crop_left"] = 0,
                ["crop_top"] = 0,
                ["crop_right"] = 0,
                ["crop_bottom"] = 0,
                ["id"] = itemId,
                ["group_item_backup"] = false,
                ["scale_filter"] = "disable",
                ["blend_method"] = "default",
                ["blend_type"] = "normal",
                ["show_transition"] = new JObject { ["duration"] = 300 },
                ["hide_transition"] = new JObject { ["duration"] = 300 },
                ["private_settings"] = new JObject(),
            };
            FitSceneItemToCanvas(item, canvasW, canvasH);
            return item;
        }

        private static JObject SceneRelPos(double x, double y, double canvasW, double canvasH) => new JObject
        {
            ["x"] = (x - canvasW / 2.0) / (canvasH / 2.0),
            ["y"] = (y - canvasH / 2.0) / (canvasH / 2.0),
        };

        private static double OverlayScaleFactor(Preferences prefs)
        {
            int scale = prefs.OverlayScale;
            return Math.Max(50, Math.Min(200, scale)) / 100.0;
        }

        private static int OverlayOpacityValue(Preferences prefs) => Math.Max(0, Math.Min(100, prefs.OverlayOpacity));

        private static double OverlayHueShiftValue(Preferences prefs) => Math.Max(-180.0, Math.Min(180.0, prefs.OverlayHueShift));

        private static string OverlayHexColor(Preferences prefs, string attr, string @default)
        {
            string value = (attr == "overlay_color_multiply" ? prefs.OverlayColorMultiply : prefs.OverlayColorAdd) ?? "";
            value = value.Trim().ToLowerInvariant();
            if (value.Length == 7 && value[0] == '#' && value.Substring(1).All(ch => "0123456789abcdef".IndexOf(ch) >= 0)) return value;
            return @default;
        }

        private static int OverlayColorValue(string hexColor)
        {
            int red = Convert.ToInt32(hexColor.Substring(1, 2), 16);
            int green = Convert.ToInt32(hexColor.Substring(3, 2), 16);
            int blue = Convert.ToInt32(hexColor.Substring(5, 2), 16);
            return red | (green << 8) | (blue << 16);
        }

        private static bool HasOverlayColorAdjustments(Preferences prefs)
        {
            return Math.Abs(OverlayHueShiftValue(prefs)) >= 0.001 ||
                   OverlayHexColor(prefs, "overlay_color_multiply", "#ffffff") != "#ffffff" ||
                   OverlayHexColor(prefs, "overlay_color_add", "#000000") != "#000000";
        }

        private struct ContentRect { public double X, Y, W, H, Scale; }

        private static ContentRect OverlayContentRect(double canvasW, double canvasH, int captureW = 0, int captureH = 0)
        {
            if (canvasW <= 0.0 || canvasH <= 0.0) { canvasW = 1920.0; canvasH = 1080.0; }
            double sourceW = captureW > 0 ? captureW : canvasW;
            double sourceH = captureH > 0 ? captureH : canvasH;
            double scale = Math.Min(canvasW / sourceW, canvasH / sourceH);
            if (scale <= 0.0) scale = 1.0;
            double width = sourceW * scale;
            double height = sourceH * scale;
            return new ContentRect { X = (canvasW - width) / 2.0, Y = (canvasH - height) / 2.0, W = width, H = height, Scale = scale };
        }

        private static (double X, double Y) InputOverlayPos(ContentRect content, double sourceW, double sourceH, double scaleX, double scaleY)
        {
            double refScale = content.H / InputOverlayPosRefH;
            double x = content.X + (InputOverlayPosXAtRef * refScale);
            double y = content.Y + content.H - (sourceH * scaleY) - (InputOverlayBottomMarginAtRef * refScale);
            return (Math.Max(0.0, x), Math.Max(0.0, y));
        }

        private static (double X, double Y) BottomLeftCornerOverlayPos(ContentRect content, double sourceW, double sourceH, double scaleX, double scaleY)
        {
            double x = content.X;
            double y = content.Y + content.H - (sourceH * scaleY);
            return (Math.Max(0.0, x), Math.Max(0.0, y));
        }

        private static JObject OverlayOpacityFilterSettings(Preferences prefs, bool legacyPercent = false)
        {
            int opacity = OverlayOpacityValue(prefs);
            var settings = new JObject { ["hue_shift"] = OverlayHueShiftValue(prefs) };
            if (legacyPercent)
            {
                settings["opacity"] = opacity;
                settings["color"] = OverlayColorValue(OverlayHexColor(prefs, "overlay_color_multiply", "#ffffff"));
                return settings;
            }
            settings["opacity"] = Math.Max(0.0, Math.Min(1.0, opacity / 100.0));
            settings["color_multiply"] = OverlayColorValue(OverlayHexColor(prefs, "overlay_color_multiply", "#ffffff"));
            settings["color_add"] = OverlayColorValue(OverlayHexColor(prefs, "overlay_color_add", "#000000"));
            return settings;
        }

        private static JObject NewOverlayOpacityFilter(string sourceName, Preferences prefs) => new JObject
        {
            ["prev_ver"] = 536936450,
            ["name"] = OverlayOpacityFilterName,
            ["uuid"] = OverlayOpacityFilterUuids[sourceName],
            ["id"] = OverlayOpacityFilterId,
            ["versioned_id"] = "color_filter_v2",
            ["settings"] = OverlayOpacityFilterSettings(prefs),
            ["mixers"] = 0,
            ["sync"] = 0,
            ["flags"] = 0,
            ["volume"] = 1.0,
            ["balance"] = 0.5,
            ["enabled"] = true,
            ["muted"] = false,
            ["push-to-mute"] = false,
            ["push-to-mute-delay"] = 0,
            ["push-to-talk"] = false,
            ["push-to-talk-delay"] = 0,
            ["hotkeys"] = new JObject(),
            ["deinterlace_mode"] = 0,
            ["deinterlace_field_order"] = 0,
            ["monitoring_type"] = 0,
            ["private_settings"] = new JObject(),
        };

        private static void ApplyOverlayOpacityFilter(JObject src, Preferences prefs)
        {
            string sourceName = src.Value<string>("name") ?? "";
            if (!OverlayOpacityFilterUuids.ContainsKey(sourceName)) return;
            int opacity = OverlayOpacityValue(prefs);
            bool hasColorAdjustments = HasOverlayColorAdjustments(prefs);
            string managedUuid = OverlayOpacityFilterUuids[sourceName];
            var filters = src["filters"] as JArray ?? new JArray();

            bool IsManagedOpacityFilter(JObject item) => item.Value<string>("name") == OverlayOpacityFilterName || item.Value<string>("uuid") == managedUuid;

            JObject managedFilter = null;
            var keptFilters = new JArray();
            foreach (var itemToken in filters)
            {
                if (itemToken is JObject item && IsManagedOpacityFilter(item))
                {
                    if (managedFilter == null) managedFilter = item;
                    continue;
                }
                keptFilters.Add(itemToken);
            }

            if (managedFilter != null)
            {
                if (!(managedFilter["settings"] is JObject filterSettings)) { filterSettings = new JObject(); managedFilter["settings"] = filterSettings; }
                string versionedId = managedFilter.Value<string>("versioned_id") ?? "";
                var newSettings = managedFilter.Value<string>("id") == OverlayOpacityFilterId && versionedId != "color_filter_v2"
                    ? OverlayOpacityFilterSettings(prefs, true)
                    : OverlayOpacityFilterSettings(prefs, false);
                foreach (var prop in newSettings.Properties()) filterSettings[prop.Name] = prop.Value;
                managedFilter["enabled"] = true;
                keptFilters.Add(managedFilter);
            }
            else if (opacity < 100 || hasColorAdjustments)
            {
                keptFilters.Add(NewOverlayOpacityFilter(sourceName, prefs));
            }

            if (keptFilters.Count > 0) src["filters"] = keptFilters;
            else src.Remove("filters");
        }

        private static void RemoveOverlayOpacityFilter(JObject src)
        {
            string sourceName = src.Value<string>("name") ?? "";
            string managedUuid = OverlayOpacityFilterUuids.TryGetValue(sourceName, out var u) ? u : null;
            if (!(src["filters"] is JArray filters)) return;
            var kept = filters.Where(item =>
            {
                var obj = item as JObject;
                return obj?.Value<string>("name") != OverlayOpacityFilterName && (managedUuid == null || obj?.Value<string>("uuid") != managedUuid);
            }).ToList();
            filters.Clear();
            foreach (var item in kept) filters.Add(item);
            if (filters.Count == 0) src.Remove("filters");
        }

        private static (double X, double Y, double Scale) InputOverlayGroupGeometry(double canvasW, double canvasH, Preferences prefs, ContentRect? content = null)
        {
            var c = content ?? OverlayContentRect(canvasW, canvasH);
            double scale = (c.H / 1440.0) * OverlayScaleFactor(prefs);
            var (x, y) = InputOverlayPos(c, InputOverlayGroupW, InputOverlayGroupH, scale, scale);
            return (x, y, scale);
        }

        private static void ApplyInputOverlaySceneItemGeometry(JObject item, string name, double canvasW, double canvasH, Preferences prefs, ContentRect? content = null)
        {
            var (groupX, groupY, groupScale) = InputOverlayGroupGeometry(canvasW, canvasH, prefs, content);
            double sourceW, sourceH, scaleX, scaleY, x;
            if (name == InputOverlayMouseName)
            {
                sourceW = InputOverlayMouseSourceW;
                sourceH = InputOverlayMouseSourceH;
                scaleX = InputOverlayMouseScale * groupScale;
                scaleY = InputOverlayMouseScale * groupScale;
                x = groupX + (InputOverlayMouseOffsetX * groupScale);
            }
            else
            {
                sourceW = InputOverlayWasdSourceW;
                sourceH = InputOverlayWasdSourceH;
                scaleX = InputOverlayWasdScaleX * groupScale;
                scaleY = InputOverlayWasdScaleY * groupScale;
                x = groupX;
            }
            double y = groupY;
            item["align"] = 5;
            item["pos"] = new JObject { ["x"] = x, ["y"] = y };
            item["pos_rel"] = SceneRelPos(x, y, canvasW, canvasH);
            item["scale"] = new JObject { ["x"] = scaleX, ["y"] = scaleY };
            item["scale_rel"] = new JObject { ["x"] = scaleX * 1440.0 / canvasH, ["y"] = scaleY * 1440.0 / canvasH };
            item["scale_ref"] = new JObject { ["x"] = sourceW, ["y"] = sourceH };
            item["bounds"] = new JObject { ["x"] = 0.0, ["y"] = 0.0 };
            item["bounds_rel"] = new JObject { ["x"] = 0.0, ["y"] = 0.0 };
            item["bounds_type"] = 0;
            item["bounds_align"] = 0;
        }

        private static void ApplyInputOverlayGroupGeometry(JObject item, double canvasW, double canvasH, Preferences prefs, ContentRect? content = null)
        {
            var (x, y, scale) = InputOverlayGroupGeometry(canvasW, canvasH, prefs, content);
            item["align"] = 5;
            item["pos"] = new JObject { ["x"] = x, ["y"] = y };
            item["pos_rel"] = SceneRelPos(x, y, canvasW, canvasH);
            item["scale"] = new JObject { ["x"] = scale, ["y"] = scale };
            double scaleRel = OverlayScaleFactor(prefs);
            item["scale_rel"] = new JObject { ["x"] = scaleRel, ["y"] = scaleRel };
            item["scale_ref"] = new JObject { ["x"] = canvasW, ["y"] = canvasH };
            item["bounds"] = new JObject { ["x"] = 0.0, ["y"] = 0.0 };
            item["bounds_rel"] = new JObject { ["x"] = 0.0, ["y"] = 0.0 };
            item["bounds_type"] = 0;
            item["bounds_align"] = 0;
        }

        private static void ApplyBongoGeometry(JObject item, double canvasW, double canvasH, Preferences prefs, ContentRect? content = null)
        {
            var c = content ?? OverlayContentRect(canvasW, canvasH);
            double scaleRatio = (c.H / BongoCatCanvasH) * OverlayScaleFactor(prefs);
            double scaleX = BongoCatScaleX * scaleRatio;
            double scaleY = BongoCatScaleY * scaleRatio;
            double scaleRelX = scaleX * BongoCatSourceH / Math.Max(1.0, canvasH);
            double scaleRelY = scaleY * BongoCatSourceH / Math.Max(1.0, canvasH);
            var (x0, y0) = BottomLeftCornerOverlayPos(c, BongoCatSourceW, BongoCatSourceH, scaleX, scaleY);
            double x = Math.Abs(x0) < 0.001 ? 0.0 : x0;
            double y = Math.Abs(y0) < 0.001 ? 0.0 : y0;

            item["name"] = BongoCatSourceName;
            item["source_uuid"] = BongoCatSourceUuid;
            item["visible"] = true;
            item["locked"] = false;
            item["rot"] = 0.0;
            item["scale_ref"] = new JObject { ["x"] = BongoCatSourceW, ["y"] = BongoCatSourceH };
            item["align"] = 5;
            item["bounds_type"] = 0;
            item["bounds_align"] = 0;
            item["bounds_crop"] = false;
            item["crop_left"] = 0;
            item["crop_top"] = 0;
            item["crop_right"] = 0;
            item["crop_bottom"] = 0;
            item["group_item_backup"] = false;
            item["pos"] = new JObject { ["x"] = x, ["y"] = y };
            item["pos_rel"] = SceneRelPos(x, y, canvasW, canvasH);
            item["scale"] = new JObject { ["x"] = scaleX, ["y"] = scaleY };
            item["scale_rel"] = new JObject { ["x"] = scaleRelX, ["y"] = scaleRelY };
            item["bounds"] = new JObject { ["x"] = 0.0, ["y"] = 0.0 };
            item["bounds_rel"] = new JObject { ["x"] = 0.0, ["y"] = 0.0 };
            item["scale_filter"] = "disable";
            item["blend_method"] = "default";
            item["blend_type"] = "normal";
            item["show_transition"] = new JObject { ["duration"] = 300 };
            item["hide_transition"] = new JObject { ["duration"] = 300 };
            item["private_settings"] = new JObject();
        }

        private static JObject NewBongoSceneItem(int itemId, double canvasW, double canvasH, Preferences prefs, ContentRect? content = null)
        {
            var item = new JObject { ["id"] = itemId };
            ApplyBongoGeometry(item, canvasW, canvasH, prefs, content);
            return item;
        }

        private static void EnsureBongoSceneItem(JArray items, JObject settings, double canvasW, double canvasH, Preferences prefs, ContentRect? content = null)
        {
            foreach (var itemToken in items)
            {
                if (itemToken is JObject item && (item.Value<string>("source_uuid") == BongoCatSourceUuid || item.Value<string>("name") == BongoCatSourceName))
                {
                    ApplyBongoGeometry(item, canvasW, canvasH, prefs, content);
                    return;
                }
            }

            var newItem = NewBongoSceneItem(NextSceneItemId(items), canvasW, canvasH, prefs, content);
            items.Add(newItem);
            int existingCounter = settings["id_counter"] != null && int.TryParse(settings["id_counter"].ToString(), out var ic) ? ic : 0;
            settings["id_counter"] = Math.Max(existingCounter, SceneItemIds(items).DefaultIfEmpty(0).Max());
        }

        private static JObject GameCaptureInputSettings(Preferences prefs) => new JObject
        {
            ["capture_audio"] = false,
            ["hook_rate"] = GameCaptureHookRateFast,
            ["limit_framerate"] = true,
            ["capture_cursor"] = false,
            ["capture_overlays"] = false,
            ["anti_cheat_hook"] = true,
            ["capture_mode"] = "any_fullscreen",
            ["window"] = "",
        };

        private static void ApplyGameCaptureDefaults(JObject source, Preferences prefs)
        {
            if (source.Value<string>("id") != GameCaptureSourceId && source.Value<string>("name") != GameCaptureSourceName) return;
            if (!(source["settings"] is JObject settings)) { settings = new JObject(); source["settings"] = settings; }
            foreach (var prop in GameCaptureInputSettings(prefs).Properties()) settings[prop.Name] = prop.Value;
        }

        private static void ApplyDesktopAudioExclusions(JObject source)
        {
            if (source.Value<string>("id") != "audio_capture" || source.Value<string>("name") != DesktopAudioSourceName) return;
            if (!(source["settings"] is JObject settings)) { settings = new JObject(); source["settings"] = settings; }
            settings["mode"] = "session";
            settings["executable_list"] = new JArray(DesktopAudioExcludeProcesses.Select(name => new JObject { ["value"] = name }));
            settings["exclude"] = true;
        }

        // apply user prefs to the scenes file: mic device, display id, selected overlay, display + game capture fit-to-canvas, scripts-tool replaykit entry.
        public static string ApplyScenesJson(string text, Preferences prefs)
        {
            var data = JObject.Parse(text);
            // only assign back when creating a fresh array -- reassigning a value already read from this same property makes newtonsoft clone it (a token cant have two parents), silently forking every later mutation onto a detached copy that never reaches the final serialized data.
            if (!(data["sources"] is JArray sources)) { sources = new JArray(); data["sources"] = sources; }
            if (!(data["groups"] is JArray groups)) { groups = new JArray(); data["groups"] = groups; }

            var primary = Display.PrimaryDisplay();
            var (canvasW, canvasH) = CanvasSize(prefs);
            string overlayStyle = SelectedOverlayStyle(prefs);
            bool useInputOverlay = overlayStyle == "input_overlay";
            bool useBongoCat = overlayStyle == "bongo_cat";
            bool useMotionBlur = prefs.MotionBlurEnabled;
            double motionBlurStrength = prefs.MotionBlurStrength;
            var overlayUuids = CollectOverlayUuids(sources);
            var overlayGroupNames = CollectOverlayGroupNames(sources, groups);

            if (!useInputOverlay)
            {
                RemoveSourcesByUuidOrId(sources, overlayUuids, InputOverlaySourceId);
                var keptGroups = groups.Where(g => g is JObject go &&
                    !overlayGroupNames.Contains(go.Value<string>("name") ?? "\0") &&
                    !IsInputOverlayGroup(go, overlayUuids)).ToList();
                groups.Clear();
                foreach (var g in keptGroups) groups.Add(g);
            }

            if (useBongoCat) EnsureBongoSource(sources);
            else RemoveBongoSources(sources);
            EnsureWindowCaptureSource(sources, prefs);

            int primaryW = primary?.Width ?? 0;
            int primaryH = primary?.Height ?? 0;
            var overlayContent = OverlayContentRect(canvasW, canvasH, primaryW, primaryH);

            foreach (var srcToken in sources)
            {
                if (!(srcToken is JObject src)) continue;
                string sid = src.Value<string>("id");

                if (sid == MicSourceId)
                {
                    if (!(src["settings"] is JObject micSettings)) { micSettings = new JObject(); src["settings"] = micSettings; }
                    micSettings["device_id"] = prefs.MicrophoneDeviceId;
                }
                else if (sid == MonitorSourceId && primary != null)
                {
                    if (!(src["settings"] is JObject monSettings)) { monSettings = new JObject(); src["settings"] = monSettings; }
                    monSettings["monitor_id"] = primary.DeviceId;
                    monSettings["capture_cursor"] = false;
                }

                ApplyGameCaptureDefaults(src, prefs);
                if (src.Value<string>("name") == WindowCaptureSourceName)
                {
                    src["id"] = WindowCaptureSourceId;
                    src["versioned_id"] = WindowCaptureSourceId;
                    if (!(src["settings"] is JObject wcSettings)) { wcSettings = new JObject(); src["settings"] = wcSettings; }
                    foreach (var prop in WindowCaptureInputSettings(prefs).Properties()) wcSettings[prop.Name] = prop.Value;
                }
                ApplyDesktopAudioExclusions(src);

                if (MotionBlurSourceUuids.ContainsKey(src.Value<string>("name") ?? "\0"))
                {
                    if (useMotionBlur) ApplyMotionBlurFilter(src, true, motionBlurStrength);
                    else RemoveMotionBlurFilter(src);
                }

                if (sid == InputOverlaySourceId)
                {
                    src["enabled"] = useInputOverlay;
                    ResolveOverlayPaths(src);
                    if (useInputOverlay) ApplyOverlayOpacityFilter(src, prefs);
                    else RemoveOverlayOpacityFilter(src);
                }
                else if (sid == BongoCatSourceId || src.Value<string>("name") == BongoCatSourceName)
                {
                    if (useBongoCat) ApplyOverlayOpacityFilter(src, prefs);
                }
                else if (sid == "scene")
                {
                    if (!(src["settings"] is JObject settings)) { settings = new JObject(); src["settings"] = settings; }
                    var items = settings["items"] as JArray ?? new JArray();
                    var keptItems = new JArray();
                    string mode = prefs.ScreenshareCaptureMode ?? "hybrid_auto";
                    bool foundWindowCapture = false;

                    foreach (var itemToken in items)
                    {
                        if (!(itemToken is JObject item)) continue;
                        string name = item.Value<string>("name") ?? "";
                        // match the overlay scene item by source_uuid so renaming the source doesnt break the toggle.
                        if (overlayUuids.Contains(item.Value<string>("source_uuid") ?? "\0"))
                        {
                            if (useInputOverlay)
                            {
                                item["visible"] = true;
                                ApplyInputOverlaySceneItemGeometry(item, name, canvasW, canvasH, prefs, overlayContent);
                                keptItems.Add(item);
                            }
                            continue;
                        }
                        // group-style structure: hide/show by group name (groups dont expose source_uuid here).
                        if (overlayGroupNames.Contains(name))
                        {
                            if (useInputOverlay)
                            {
                                item["visible"] = true;
                                ApplyInputOverlayGroupGeometry(item, canvasW, canvasH, prefs, overlayContent);
                                keptItems.Add(item);
                            }
                            continue;
                        }
                        if (item.Value<string>("source_uuid") == BongoCatSourceUuid || name == BongoCatSourceName)
                        {
                            if (useBongoCat)
                            {
                                ApplyBongoGeometry(item, canvasW, canvasH, prefs, overlayContent);
                                keptItems.Add(item);
                            }
                            continue;
                        }
                        if (name == "Display Capture")
                        {
                            item["visible"] = mode == "hybrid_auto" || mode == "desktop";
                            FitSceneItemToCanvas(item, canvasW, canvasH, primaryW, primaryH);
                        }
                        else if (name == WindowCaptureSourceName)
                        {
                            foundWindowCapture = true;
                            item["source_uuid"] = WindowCaptureSourceUuid;
                            item["visible"] = mode == "game_window";
                            FitSceneItemToCanvas(item, canvasW, canvasH);
                        }
                        else if (name == "Game Capture")
                        {
                            item["visible"] = mode == "game_auto";
                            // game capture has no install-time source dims -- bounds-based fit lets obs scale at runtime.
                            FitSceneItemToCanvas(item, canvasW, canvasH);
                        }
                        keptItems.Add(item);
                    }

                    if (!foundWindowCapture)
                    {
                        var windowItem = NewCaptureSceneItem(WindowCaptureSourceName, WindowCaptureSourceUuid, NextSceneItemId(keptItems), canvasW, canvasH, mode == "game_window");
                        int insertAt = 0;
                        for (int i = 0; i < keptItems.Count; i++)
                        {
                            if ((keptItems[i] as JObject)?.Value<string>("name") == "Display Capture") { insertAt = i + 1; break; }
                        }
                        keptItems.Insert(insertAt, windowItem);
                        int existingCounter = settings["id_counter"] != null && int.TryParse(settings["id_counter"].ToString(), out var ic) ? ic : 0;
                        settings["id_counter"] = Math.Max(existingCounter, SceneItemIds(keptItems).DefaultIfEmpty(0).Max());
                    }
                    MoveSceneItemAfter(keptItems, WindowCaptureSourceName, "Display Capture");

                    if (useBongoCat) EnsureBongoSceneItem(keptItems, settings, canvasW, canvasH, prefs, overlayContent);
                    settings["items"] = keptItems;
                }
            }

            if (!(data["modules"] is JObject modules)) { modules = new JObject(); data["modules"] = modules; }
            var scripts = modules["scripts-tool"] as JArray ?? new JArray();
            var replaykitSettings = CollectReplaykitSettings(scripts, prefs);

            var keptScripts = new JArray(scripts.Where(e => !(e is JObject eo && IsReplaykitEntry(eo))));
            keptScripts.Add(new JObject
            {
                ["path"] = ReplaykitScriptPath(),
                ["settings"] = replaykitSettings,
            });
            modules["scripts-tool"] = keptScripts;

            return ToIndented4(data);
        }

        // JToken.ToString(Formatting.Indented) defaults to a 2-space indent; matches pythons json.dumps(data, indent=4).
        private static string ToIndented4(JToken token)
        {
            var sb = new System.Text.StringBuilder();
            using (var sw = new System.IO.StringWriter(sb))
            using (var jw = new JsonTextWriter(sw) { Formatting = Formatting.Indented, Indentation = 4, IndentChar = ' ' })
            {
                token.WriteTo(jw);
            }
            return sb.ToString();
        }

        // (rel_path within assets/obs-studio) -> transformer
        private static readonly Dictionary<string, Func<string, Preferences, string>> Dispatch = new Dictionary<string, Func<string, Preferences, string>>
        {
            ["basic/profiles/Untitled/basic.ini"] = ApplyBasicIni,
            ["basic/profiles/Untitled/recordEncoder.json"] = ApplyRecordEncoderJson,
            ["basic/scenes/Untitled.json"] = ApplyScenesJson,
            ["obs-replayKit/scripts/replaykit_settings.json"] = ApplyReplaykitSettingsJson,
            ["user.ini"] = ApplyUserIni,
            ["global.ini"] = ApplyGlobalIni,
        };

        // rewrite content according to prefs if a transformer is registered, else return as-is.
        public static string ApplyPreferences(string relPath, string content, Preferences prefs)
        {
            string key = relPath.Replace('\\', '/');
            return Dispatch.TryGetValue(key, out var transformer) ? transformer(content, prefs) : content;
        }
    }
}

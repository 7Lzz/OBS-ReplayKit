using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ReplayKitShared;
using Encoder = ReplayKitShared.Encoder;
using static ReplayKitHelper.SettingsSceneJson;
using static ReplayKitHelper.SettingsOverlayLive;
using static ReplayKitHelper.SettingsObsConfig;
using static ReplayKitHelper.SettingsRecording;
using static ReplayKitHelper.SettingsLiveApply;
using static ReplayKitHelper.SettingsHotkeys;
using static ReplayKitHelper.SettingsHttp;

namespace ReplayKitHelper
{
    // the replaykit_settings.json schema (defaults, per-field validation/coercion, normalize, read, write, the auto-game-list window-token codec); applying it to obs lives in the Settings*.cs classes next to this one, one concern each: SettingsSceneJson, SettingsOverlayLive, SettingsObsConfig, SettingsRecording, SettingsLiveApply, SettingsHotkeys and SettingsHttp
    internal static class ReplaykitSettings
    {
        public static string GetScriptsDir()
        {
            string dir = (AppConfig.GetScriptDir() ?? "").TrimEnd('\\', '/');
            return Directory.GetParent(dir).FullName;
        }

        public static string GetSettingsPath() => StateLayout.Resolve(AppConfig.GetReplayKitRoot(), StateLayout.SettingsFile);

        internal const bool DefaultSeparateAudioTracks = SettingDefaults.RecordingSeparateAudioTracks;
        internal const string DefaultCodecPreference = SettingDefaults.CodecPreference;
        internal const string OutputDeviceRecordingMode = "output_device";

        internal static bool UsesOutputDeviceRecording(JObject settings) =>
            settings["recordingAudioMode"]?.Value<string>() == OutputDeviceRecordingMode;

        public static JObject GetDefaultSettings()
        {
            return new JObject
            {
                ["recordingPreset"] = SettingDefaults.RecordingPreset,
                ["compressionMode"] = SettingDefaults.CompressionMode,
                ["codecPreference"] = DefaultCodecPreference,
                ["replaySeconds"] = SettingDefaults.ReplaySeconds,
                ["fps"] = 60,
                ["fpsNumerator"] = 60,
                ["fpsDenominator"] = 1,
                // recording output resolution: "native" = encode at the canvas (monitor) size, no downscale; "downscale" = scale the output down to downscaleHeight using downscaleFilter. default is canvas-aware -- native at/below 1080p, downscale-to-1080 above. the performance preset always caps at 720 regardless.
                ["recordingScaleMode"] = DefaultRecordingScaleMode(),
                ["downscaleHeight"] = 1080,
                ["downscaleFilter"] = "lanczos",
                // where the clip buffer is kept: disk unless memory is picked, see ClipBuffer
                ["recordingBuffer"] = ClipBuffer.Disk,
                // which kinds of capture show the mouse cursor in recordings and clips
                ["showCursorDisplayCapture"] = true,
                ["showCursorGameCapture"] = true,
                ["showCursorWindowCapture"] = true,
                // the display Display Capture shows: "primary" follows whichever display windows calls primary, anything else is the obs monitor_id of one display
                ["captureDisplay"] = "primary",
                ["clipDir"] = "",
                // the most the clips may take, in GB (1024-based, the way the storage card shows sizes); 0 = no limit. going over it only warns and offers to delete the oldest clips after a review, nothing is removed on its own
                ["storageLimitGb"] = 0,
                ["storageCompressionType"] = "fast",
                // the days a deleted clip or screenshot stays in Deleted before it is gone for good; 0 = deletes are final at once
                ["deletedHistoryDays"] = 7,
                ["clipKeybind"] = new JObject { ["key"] = "OBS_KEY_F8" },
                ["recordingKeybind"] = new JObject(),
                ["openClipsKeybind"] = new JObject { ["alt"] = true, ["key"] = "OBS_KEY_Z" },
                // the screenshot of the obs output, saved into the Screenshots folder of the clips; unbound until a key is picked, like recording
                ["screenshotKeybind"] = new JObject(),
                // the one extra binding each hotkey may have (the + of the hotkeys page)
                ["clipKeybindExtra"] = new JObject(),
                ["recordingKeybindExtra"] = new JObject(),
                ["openClipsKeybindExtra"] = new JObject(),
                ["screenshotKeybindExtra"] = new JObject(),
                ["overlayStyle"] = SettingDefaults.OverlayStyle,
                ["bongoMode"] = SettingDefaults.BongoMode,
                // the keys each bongo cat mode listens to in place of its own: { mode: [KeyboardEvent.code or "" per key of the mode] }
                ["bongoKeys"] = new JObject(),
                // the settings of the bongo cat source: the animated cat, its head following the mouse, breathing and blinking, the faces of F1 to F4, the mouse read as raw movement for games that hold the cursor still, and how fast it animates
                ["bongoLive2d"] = true,
                ["bongoTrack"] = true,
                ["bongoBreath"] = true,
                ["bongoBlink"] = true,
                ["bongoFaces"] = false,
                ["bongoRelativeMouse"] = true,
                ["bongoSpeed"] = 1.0,
                // the keyboard and mouse of the wasd overlay, see InputOverlayLayouts
                ["inputOverlayKeyboard"] = InputOverlayLayout.DefaultKeyboard,
                ["inputOverlayCustomKeys"] = new JArray(InputOverlayLayouts.DefaultCustomKeys),
                ["inputOverlayMouse"] = InputOverlayLayout.DefaultMouse,
                ["overlayOpacity"] = SettingDefaults.OverlayOpacity,
                ["overlayScale"] = SettingDefaults.OverlayScale,
                ["overlayRotation"] = SettingDefaults.OverlayRotation,
                ["overlayFlipH"] = SettingDefaults.OverlayFlipH,
                ["overlayHueShift"] = SettingDefaults.OverlayHueShift,
                ["overlayColorMultiply"] = SettingDefaults.OverlayColorMultiply,
                ["overlayColorAdd"] = SettingDefaults.OverlayColorAdd,
                // where the user dragged each overlay, as canvas fractions of its visible top-left, keyed by style. empty = the canonical corner the transforms compute.
                ["overlayPosition"] = new JObject(),
                ["obsStartupEnabled"] = SettingDefaults.ObsStartupEnabled,
                ["runObsAsAdmin"] = false,
                ["disableObsCloseWarning"] = SettingDefaults.DisableObsCloseWarning,
                // helper keeps obss .sentinel\run_* swept while it runs so a crash / power loss cant leave the "did not properly shut down" prompt behind for the next launch. off = hand crash detection back to obs.
                ["disableObsCrashPopup"] = true,
                // when on, the native plugin turns the OBS windows X into "minimize to tray" -- real quits still go through the tray Exit / restart routes.
                ["closeToTray"] = SettingDefaults.CloseToTray,
                // seconds the obs window stays hidden before the native plugin puts the replaykit pages to sleep (a blank page) so their renderer and gpu memory go; 0 keeps them loaded. a page holding unsaved work or a running job keeps it awake
                ["browserSleepSeconds"] = 30,
                ["allowSleepWhileActive"] = SettingDefaults.AllowSleepWhileActive,
                ["pinObsTrayIcon"] = SettingDefaults.PinObsTrayIcon,
                ["clipNotificationEnabled"] = SettingDefaults.ClipNotificationEnabled,
                ["recordingNotificationEnabled"] = SettingDefaults.RecordingNotificationEnabled,
                ["clipNotificationSeconds"] = 90,
                // keeps the clip saved and recording popups out of screenshots and screen recordings
                ["hideNotificationsFromCapture"] = true,
                // copy the streamable link to the clipboard once the uploaded video is watchable
                ["autoCopyLink"] = true,
                ["hevcFixEnabled"] = true,
                // the clip uploaded notification that goes with it
                ["linkNotificationEnabled"] = true,
                // obs starts in the tray instead of showing its window; the native plugin keeps this equal to the start minimized switch in the system tray settings of obs
                ["startMinimized"] = true,
                // what the compress list of the storage card starts out as every time the card opens: favorites are left out of it
                ["compressSkipFavorites"] = true,
                // shows the development settings, and nothing in them does anything while this is off
                ["developmentMode"] = false,
                // a local command channel to talk to replaykit while it runs, on top of development mode
                ["sdkMode"] = false,
                // looks for a newer obs release and shows it on a grey badge of the title bar, on top of development mode; obs updates are locked, so this is how a developer hears of one
                ["checkObsUpdates"] = false,
                // the native plugin writes what the interface does (views, panels, the preview) to the obs log, on top of development mode
                ["logInterfaceEvents"] = false,
                ["trimPreciseDefault"] = SettingDefaults.TrimPreciseDefault,
                // the saves that make the video again are encoded by an nvidia card when there is one, in the recording codec; off keeps them on the processor
                ["trimGpuEncode"] = true,
                ["showCodecTags"] = false,
                // whether the game on screen is looked up for new clips and screenshots; clips that already have a game keep it either way
                ["gameRecognition"] = true,
                // how the date line of the clip and screenshot cards reads: the time ago, the date, the date and time, or the short date of the region
                ["cardDateFormat"] = "date",
                // the font every page and the native chrome draw their text in, one of Fonts.Families
                ["uiFont"] = "Segoe UI",
                // how the clip editor behaves: whether it starts playing and loops the selection, how far the arrow keys skip in seconds, the mouse wheel zoom and how a zoomed in video is drawn (smooth or sharp)
                ["editorAutoplay"] = false,
                ["editorLoopPreview"] = false,
                ["editorSeekStep"] = 5,
                ["gifFrameDelay"] = 3,
                ["editorWheelZoom"] = true,
                ["editorZoomScaling"] = "smooth",
                // how a clip is smoothed while it plays large in the theater view, full screen and the editor: none, msaa or fxaa
                ["clipAntialiasing"] = "none",
                ["debugLoggingEnabled"] = SettingDefaults.DebugLoggingEnabled,
                ["autoDeleteLogsOnLaunch"] = true,
                ["lastUpdatePromptVersion"] = "",
                ["clipSoundVolume"] = SettingDefaults.ClipSoundVolume,
                ["recordingSoundVolume"] = SettingDefaults.RecordingSoundVolume,
                // Mic volume uses a named gain filter; zero silences the input. Suppression uses RNNoise and sensitivity controls the noise gate.
                ["micDeviceId"] = SettingDefaults.MicDeviceId,
                ["micVolume"] = 100,
                ["micNoiseSuppression"] = true,
                ["micInputSensitivity"] = MicTest.SensitivityDefault,
                // same 0-200-onto-a-gain-filter deal as micVolume, on "Desktop Audio (excl. Discord)" and "Discord Audio (record only)" -- discord audio is an optional source, missing entirely on installs that never added it.
                ["desktopAudioVolume"] = 100,
                ["discordAudioVolume"] = 100,
                // which programs are in the recorded desktop sound. "all" keeps every program except the ones switched off, "specific" keeps only the ones switched on; apps maps a program name (brave.exe) to true or false, and a program that is not in it follows the mode
                ["recordingAudioMode"] = "all",
                ["recordingAudioApps"] = new JObject(),
                // saves the pc audio, discord and the microphone as tracks of their own in every recording, next to the mixed track 1 that players play. on by default; the setup exe turns it on once for an install that existed before
                ["recordingSeparateAudioTracks"] = DefaultSeparateAudioTracks,
                ["motionBlurEnabled"] = SettingDefaults.MotionBlurEnabled,
                ["motionBlurStrength"] = SettingDefaults.MotionBlurStrength,
                // legacy shareMode is kept only so older settings files dont break parsing; discord output uses the obs windowed projector directly.
                ["shareMode"] = SettingDefaults.ShareMode,
                ["discord_screenshare_enabled"] = SettingDefaults.DiscordScreenshareEnabled,
                ["discord_output_mode"] = SettingDefaults.DiscordOutputMode,
                ["discord_projector_enabled"] = SettingDefaults.DiscordProjectorEnabled,
                ["discord_projector_width"] = SettingDefaults.DiscordProjectorWidth,
                ["discord_projector_height"] = SettingDefaults.DiscordProjectorHeight,
                ["discord_projector_visible_pixels"] = SettingDefaults.DiscordProjectorVisiblePixels,
                ["discord_projector_monitor_index"] = SettingDefaults.DiscordProjectorMonitorIndex,
                ["discord_projector_edge"] = SettingDefaults.DiscordProjectorEdge,
                ["discord_projector_title_hint"] = SettingDefaults.DiscordProjectorTitleHint,
                ["discord_projector_hide_taskbar"] = SettingDefaults.DiscordProjectorHideTaskbar,
                ["screenshareCaptureMode"] = SettingDefaults.ScreenshareCaptureMode,
                ["screenshareGameWindow"] = SettingDefaults.ScreenshareGameWindow,
                ["screenshareGameOverrides"] = new JArray(),
                ["screenshareAutoGameKeepFocused"] = SettingDefaults.ScreenshareAutoGameKeepFocused,
                ["screenshareSwitchDelaySeconds"] = 1.0,
                // "default" = leave obs/replaykit icons alone; "custom" = use appIconCustomPath; anything else = a filename in the bundled icons/ folder.
                ["appIcon"] = "default",
                ["appIconCustomPath"] = "",
                // the red recording dot on the tray icon while recording or clipping, on the default obs icon and on a custom one alike; off takes it away everywhere.
                ["appIconRecordingDot"] = true,
                // "default" (obs yami) / a bundled preset id (see Themes.PresetOrder) / "custom" / "user/<name>" (a saved custom). drives the dock, replaykit window chrome, and a generated obs theme variant.
                ["theme"] = "default",
                // the custom-editor working palette; the second gradient stop is optional.
                ["themeCustom"] = new JObject
                {
                    ["bg"] = "#161617", ["panel"] = "#1D1F26", ["field"] = "#2F323C",
                    ["text"] = "#FFFFFF", ["accent"] = "#284CB8", ["border"] = "#3C404D",
                    ["danger"] = "#E33B57", ["gradient"] = "", ["gradients"] = new JObject(), ["dark"] = true,
                },
                // "clean" = the sidebar layout (menu bar folded into the title-bar menu, only the controls sidebar docked); "classic" = the stock obs dock layout. the native plugin polls this and re-lays the main window live, no restart.
                ["interfaceLayout"] = "clean",
                // armed by the installer on a genuine first install; the dock opens the setup wizard on the next obs launch and /setup/complete clears it.
                ["setupWizardPending"] = false,
            };
        }

        internal static bool GetBoolSetting(JObject data, string key, bool defaultValue)
        {
            var token = data[key];
            if (token == null) return defaultValue;
            if (token.Type == JTokenType.Boolean) return token.Value<bool>();
            if (token.Type == JTokenType.String)
            {
                string s = token.Value<string>().Trim().ToLowerInvariant();
                if (s == "true" || s == "1" || s == "yes" || s == "on") return true;
                if (s == "false" || s == "0" || s == "no" || s == "off") return false;
            }
            throw new InvalidOperationException("Invalid boolean setting: " + key);
        }

        internal static int GetIntSetting(JObject data, string key, int defaultValue, int min, int max)
        {
            var token = data[key];
            if (token == null) return defaultValue;
            if (!int.TryParse(token.ToString(), out int n)) throw new InvalidOperationException("Invalid number setting: " + key);
            if (n < min || n > max) throw new InvalidOperationException(key + " must be between " + min + " and " + max + ".");
            return n;
        }

        internal static double GetFloatSetting(JObject data, string key, double defaultValue, double min, double max)
        {
            var token = data[key];
            if (token == null) return defaultValue;
            string text = token is JValue scalar ? Convert.ToString(scalar.Value, CultureInfo.InvariantCulture) : "";
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
                throw new InvalidOperationException("Invalid number setting: " + key);
            if (double.IsNaN(n) || double.IsInfinity(n) || n < min || n > max) throw new InvalidOperationException(key + " must be between " + min + " and " + max + ".");
            return n;
        }

        internal static string GetHexColorSetting(JObject data, string key, string defaultValue)
        {
            var token = data[key];
            if (token == null) return defaultValue;
            string v = token.ToString().Trim().ToLowerInvariant();
            if (Regex.IsMatch(v, "^#[0-9a-f]{6}$")) return v;
            throw new InvalidOperationException("Invalid color setting: " + key);
        }

        internal static string GetEnumSetting(JObject data, string key, string defaultValue, string[] allowed)
        {
            var token = data[key];
            if (token == null) return defaultValue;
            string v = token.ToString().Trim();
            if (allowed.Contains(v)) return v;
            throw new InvalidOperationException("Invalid option for " + key + ": " + v);
        }

        internal static string GetTextSetting(JObject data, string key, string defaultValue, int maxLength)
        {
            var token = data[key];
            if (token == null) return defaultValue;
            string v = token.ToString().Trim();
            if (string.IsNullOrWhiteSpace(v)) return defaultValue;
            if (v.Length > maxLength || Regex.IsMatch(v, @"[\x00-\x1F]")) throw new InvalidOperationException("Invalid text setting: " + key);
            return v;
        }

        internal static string GetVersionMarkerSetting(JObject data, string key, string defaultValue)
        {
            var token = data[key];
            if (token == null) return defaultValue;
            string v = token.ToString().Trim();
            if (string.IsNullOrWhiteSpace(v)) return "";
            if (v.Length > 32 || !Regex.IsMatch(v, @"^\d+(?:\.\d+){0,3}$")) throw new InvalidOperationException("Invalid version setting: " + key);
            return v;
        }

        // drops a single pair of wrapping quotes -- explorers "copy as path" gives "C:\...\file", and users paste that verbatim.
        public static string StripWrappingQuotes(string value)
        {
            if (value == null) return "";
            value = value.Trim();
            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
                value = value.Substring(1, value.Length - 2).Trim();
            return value;
        }

        internal static string ResolveClipDirSetting(string value)
        {
            value = StripWrappingQuotes(value);
            if (string.IsNullOrWhiteSpace(value)) return "";
            if (value.Length > 4096) throw new InvalidOperationException("Clip folder path is too long.");
            string expanded = Environment.ExpandEnvironmentVariables(value);
            if (!Path.IsPathRooted(expanded)) throw new InvalidOperationException("Clip folder must be an absolute path.");
            return Path.GetFullPath(expanded);
        }

        internal static JObject NormalizeHotkeyCombo(JToken value, JObject defaultValue, string settingName)
        {
            if (value == null || value.Type == JTokenType.Null) return defaultValue;
            var data = value as JObject ?? new JObject();
            var keyToken = data["key"];
            if (keyToken == null || string.IsNullOrWhiteSpace(keyToken.Value<string>())) return new JObject();
            string key = keyToken.Value<string>().Trim();
            if (!Regex.IsMatch(key, "^OBS_KEY_[A-Z0-9_]{1,48}$")) throw new InvalidOperationException("Invalid " + settingName + ".");
            var outObj = new JObject { ["key"] = key };
            foreach (var mod in new[] { "control", "alt", "shift", "command" })
            {
                if (data[mod] != null && GetBoolSetting(data, mod, false)) outObj[mod] = true;
            }
            return outObj;
        }

        internal static JObject NormalizeClipKeybind(JToken value) =>
            NormalizeHotkeyCombo(value, new JObject { ["key"] = "OBS_KEY_F8" }, "clip keybind");

        internal static JObject NormalizeRecordingKeybind(JToken value) =>
            NormalizeHotkeyCombo(value, new JObject(), "recording keybind");

        internal static JObject NormalizeOpenClipsKeybind(JToken value) =>
            NormalizeHotkeyCombo(value, new JObject { ["alt"] = true, ["key"] = "OBS_KEY_Z" }, "open clips keybind");

        // a display that is unplugged right now still counts, so a choice survives the monitor being off; only the shape of an obs monitor_id is checked, the way obs itself writes it
        internal static string NormalizeCaptureDisplay(JToken value)
        {
            string v = value?.Type == JTokenType.String ? value.Value<string>().Trim() : "";
            if (v.Length == 0 || v.Length > 260 || string.Equals(v, "primary", StringComparison.OrdinalIgnoreCase)) return "primary";
            return Regex.IsMatch(v, @"^(\\\\\?\\DISPLAY#[A-Za-z0-9&#_{}.\-]+|\\\\\.\\DISPLAY\d{1,2})$", RegexOptions.IgnoreCase) ? v : "primary";
        }

        // the choices of the Display row: the primary first, then every active display with its size, and a chosen display that is not connected right now so the row still says what was picked
        internal static JArray CaptureDisplayOptions(string current)
        {
            var displays = Displays.Active();
            var primary = displays.Find(d => d.Primary);
            var options = new JArray(new JObject
            {
                ["value"] = "primary",
                ["label"] = "Primary display",
                ["blurb"] = primary != null ? "Now " + primary.Name : "Follows the display Windows calls primary.",
            });
            bool currentListed = current == "primary";
            foreach (var d in displays)
            {
                options.Add(new JObject { ["value"] = d.Id, ["label"] = d.Name + (d.Width > 0 ? " (" + d.Width + "x" + d.Height + ")" : ""), ["blurb"] = d.Primary ? "Primary" : "" });
                if (string.Equals(d.Id, current, StringComparison.OrdinalIgnoreCase)) currentListed = true;
            }
            if (!currentListed) options.Add(new JObject { ["value"] = current, ["label"] = "Display not connected", ["blurb"] = "Shown again when it is plugged back in." });
            return options;
        }

        // obs keeps the choice on the Display Capture source as replaykit_wanted_monitor_id, beside monitor_id which is what it shows right now; a display picked in the properties of the source becomes the wanted one through the capture script, so the setting follows the source
        internal const string WantedMonitorKey = "replaykit_wanted_monitor_id";

        internal static JObject SyncCaptureDisplayFromObs(JObject settings)
        {
            var input = ObsWebSocket.InvokeRequest("GetInputSettings", new JObject { ["inputName"] = "Display Capture" }, 3000);
            if (!input.Ok) return settings;
            string wanted = NormalizeCaptureDisplay(input.Data?["inputSettings"]?[WantedMonitorKey]);
            if (string.Equals(wanted, settings["captureDisplay"]?.Value<string>(), StringComparison.Ordinal)) return settings;
            settings["captureDisplay"] = wanted;
            WriteSettings(settings);
            return settings;
        }

        // the capture method a Display Capture that was never given one gets (see CaptureMethod); empty when it has one, or when obs cannot be asked, so nothing someone picked is overwritten
        internal static JObject DisplayCaptureMethodSettings()
        {
            if (!CaptureMethod.PreferWgc) return new JObject();
            var input = ObsWebSocket.InvokeRequest("GetInputSettings", new JObject { ["inputName"] = "Display Capture" }, 3000);
            JObject current = input.Ok ? input.Data?["inputSettings"] as JObject : null;
            // a source that does not exist yet is made with the method; one obs could not be asked about is left as it is
            if (!input.Ok && input.Code != 600) return new JObject();
            int? method = CaptureMethod.DisplayDefault(current);
            return method.HasValue ? new JObject { [CaptureMethod.Key] = method.Value } : new JObject();
        }

        // the capture script switches only once a change has held for a couple of checks, so this sets both keys itself and the new display shows at once
        internal static JObject ApplyCaptureDisplayLive(JObject settings)
        {
            string wanted = settings["captureDisplay"]?.Value<string>() ?? "primary";
            var displays = Displays.Active();
            var chosen = wanted == "primary" ? displays.Find(d => d.Primary) : displays.Find(d => string.Equals(d.Id, wanted, StringComparison.OrdinalIgnoreCase));
            var inputSettings = new JObject { [WantedMonitorKey] = wanted };
            // a chosen display that is not connected keeps whatever shows now, and the script moves to it once it is back
            if (chosen != null) inputSettings["monitor_id"] = chosen.Id;
            var set = ObsWebSocket.InvokeRequest("SetInputSettings", new JObject { ["inputName"] = "Display Capture", ["inputSettings"] = inputSettings, ["overlay"] = true }, 3000);
            if (set.Ok) return new JObject { ["applied"] = new JArray("Display Capture display"), ["warnings"] = new JArray() };
            if (set.Code == 600) return new JObject { ["applied"] = new JArray(), ["warnings"] = new JArray() };
            return new JObject { ["applied"] = new JArray(), ["warnings"] = new JArray("The display was saved, but Display Capture could not be updated: " + set.Message) };
        }

        internal static JObject NormalizeExtraKeybind(JToken value, string settingName) =>
            NormalizeHotkeyCombo(value, new JObject(), settingName);

        // a stale or hand-edited id falls back to "default" the way an unknown theme does, rather than failing the whole settings load.
        internal static string NormalizeMicDeviceId(string value)
        {
            value = (value ?? "").Trim();
            if (value.Length == 0 || string.Equals(value, MicDevices.DefaultId, StringComparison.OrdinalIgnoreCase)) return MicDevices.DefaultId;
            return MicDevices.IsValidId(value) ? value : MicDevices.DefaultId;
        }

        // per-style overlay position: { "<style>": { "x": <canvas fraction>, "y": <canvas fraction> } }. kept per style because the boxes are nothing alike -- bongo is 1280x768, the input overlay group 628x292 -- so one shared corner would shove the other overlay off the canvas. anything unrecognised is dropped rather than repaired, which just falls back to the canonical corner.
        internal static JObject NormalizeOverlayPosition(JToken value)
        {
            var result = new JObject();
            if (!(value is JObject map)) return result;
            foreach (var style in new[] { "input_overlay", "bongo_cat" })
            {
                if (!(map[style] is JObject at)) continue;
                var xt = at["x"];
                var yt = at["y"];
                if (xt == null || yt == null || xt.Type == JTokenType.Null || yt.Type == JTokenType.Null) continue;
                double x, y;
                try { x = xt.Value<double>(); y = yt.Value<double>(); }
                catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is ArgumentException) { continue; }
                // same window the drag itself is clamped to -- a sliver may hang off any edge, nothing may be parked far outside
                if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y)) continue;
                if (x < -1.0 || x > 2.0 || y < -1.0 || y > 2.0) continue;
                result[style] = new JObject { ["x"] = x, ["y"] = y };
            }
            return result;
        }

        internal static JArray NormalizeScreenshareGameOverrides(JToken value)
        {
            if (value == null || value.Type == JTokenType.Null) return new JArray();
            if (value.Type == JTokenType.String && string.IsNullOrWhiteSpace(value.Value<string>())) return new JArray();

            List<JToken> items;
            if (value.Type == JTokenType.String) items = new List<JToken> { value };
            else if (value is JArray arr) items = arr.ToList();
            else throw new InvalidOperationException("screenshareGameOverrides must be a list.");
            if (items.Count > 32) throw new InvalidOperationException("screenshareGameOverrides cannot contain more than 32 games.");

            var seen = new HashSet<string>();
            var outArr = new JArray();
            foreach (var item in items)
            {
                if (item == null || item.Type == JTokenType.Null) continue;
                string token;
                if (item.Type == JTokenType.String)
                {
                    token = item.Value<string>();
                }
                else
                {
                    var data = item as JObject;
                    token = data?["token"]?.ToString() ?? data?["value"]?.ToString() ?? "";
                }
                var entry = ConvertFromObsWindowToken(token);
                if (entry == null) throw new InvalidOperationException("Invalid Auto Game List entry.");
                string entryToken = entry["token"].Value<string>();
                if (seen.Contains(entryToken)) continue;
                seen.Add(entryToken);
                outArr.Add(entry);
            }
            return outArr;
        }

        // ranges only the helper checks, named so the normalizer and the limits it publishes use the same numbers
        internal const int DownscaleHeightMin = 240;
        internal const int DownscaleHeightMax = 4320;
        internal const double SwitchDelaySecondsMin = 0.05;
        internal const double SwitchDelaySecondsMax = 5.0;
        internal const double BongoSpeedMin = 0.1;
        internal const double BongoSpeedMax = 3.0;
        // 0 is no limit, the default
        internal const int StorageLimitGbMax = 1000000;

        // the range Normalize holds each setting to that a page has an input for; HttpResponse writes it into any page that names RK_SETTING_LIMITS and onto every input with a data-limit attribute, so a page never offers a value the helper would refuse
        public static JObject GetSettingLimits() => new JObject
        {
            ["replaySeconds"] = LimitRange(SettingLimits.ReplaySecondsMin, SettingLimits.ReplaySecondsMax),
            ["fps"] = LimitRange(SettingLimits.FpsMin, SettingLimits.FpsMax),
            ["storageLimitGb"] = LimitRange(0, StorageLimitGbMax),
            ["downscaleHeight"] = LimitRange(DownscaleHeightMin, DownscaleHeightMax),
            ["screenshareSwitchDelaySeconds"] = LimitRange(SwitchDelaySecondsMin, SwitchDelaySecondsMax),
            ["clipSoundVolume"] = LimitRange(SettingLimits.SoundVolumeMin, SettingLimits.SoundVolumeMax),
            ["recordingSoundVolume"] = LimitRange(SettingLimits.SoundVolumeMin, SettingLimits.SoundVolumeMax),
            ["desktopAudioVolume"] = LimitRange(0, MicTest.VolumeMax),
            ["discordAudioVolume"] = LimitRange(0, MicTest.VolumeMax),
            ["micVolume"] = LimitRange(0, MicTest.VolumeMax),
            ["micInputSensitivity"] = LimitRange(MicTest.SensitivityMin, MicTest.SensitivityMax),
            ["bongoSpeed"] = LimitRange(BongoSpeedMin, BongoSpeedMax),
            ["overlayOpacity"] = LimitRange(SettingLimits.OverlayOpacityMin, SettingLimits.OverlayOpacityMax),
            ["overlayScale"] = LimitRange(SettingLimits.OverlayScaleMin, SettingLimits.OverlayScaleMax),
            ["overlayRotation"] = LimitRange(SettingLimits.OverlayRotationMin, SettingLimits.OverlayRotationMax),
            ["overlayHueShift"] = LimitRange(SettingLimits.OverlayHueShiftMin, SettingLimits.OverlayHueShiftMax),
            ["motionBlurStrength"] = LimitRange(SettingLimits.MotionBlurStrengthMin, SettingLimits.MotionBlurStrengthMax),
        };

        internal static JObject LimitRange(int min, int max) => new JObject { ["min"] = min, ["max"] = max };
        internal static JObject LimitRange(double min, double max) => new JObject { ["min"] = min, ["max"] = max };

        public static JObject Normalize(JObject raw)
        {
            var defaults = GetDefaultSettings();
            var data = (JObject)defaults.DeepClone();
            if (raw != null) foreach (var prop in raw.Properties()) data[prop.Name] = prop.Value;

            string preset = GetEnumSetting(data, "recordingPreset", defaults["recordingPreset"].Value<string>(), new[] { "performance", "balanced", "quality" });
            string compressionDefault = preset == "performance" ? "lower_gpu" : preset == "quality" ? "smaller_files" : "balanced";
            string compression = GetEnumSetting(data, "compressionMode", compressionDefault, SettingLimits.CompressionModes);

            string clipDir = ResolveClipDirSetting(data["clipDir"]?.ToString());
            int replaySeconds = GetIntSetting(data, "replaySeconds", defaults["replaySeconds"].Value<int>(), SettingLimits.ReplaySecondsMin, SettingLimits.ReplaySecondsMax);

            string shareMode = GetEnumSetting(data, "shareMode", defaults["shareMode"].Value<string>(), new[] { "projector", "share_bridge", "virtual_camera_legacy", "vcam", "screenshare" });
            if (shareMode != "projector")
            {
                Log.Write("Discord shareMode '" + shareMode + "' ignored; ReplayKit now uses OBS projector mode.");
                shareMode = "projector";
            }
            string discordOutputMode = GetEnumSetting(data, "discord_output_mode", defaults["discord_output_mode"].Value<string>(), new[] { "projector", "share_bridge", "virtual_camera_legacy" });
            if (discordOutputMode != "projector")
            {
                Log.Write("Discord output mode '" + discordOutputMode + "' ignored; ReplayKit now uses OBS projector mode.");
                discordOutputMode = "projector";
            }

            // auto and av1 stay readable so older settings files do not throw on load, but neither is offered: auto always picked the h264 encoder anyway, and av1 has no decoder on most iphones and plenty of android devices, which unlike the hevc hev1/hvc1 tag no remux can fix. both read as h264
            string codecPreference = GetEnumSetting(data, "codecPreference", defaults["codecPreference"].Value<string>(), new[] { "h264", "h265", "auto", "av1" });
            if (codecPreference == "auto" || codecPreference == "av1") codecPreference = DefaultCodecPreference;

            // logging used to be a plain setting of the advanced tab and lives under development mode now, so a settings file that has it on and never heard of development mode keeps its logging
            bool developmentMode = raw != null && raw["developmentMode"] == null && GetBoolSetting(data, "debugLoggingEnabled", false)
                ? true
                : GetBoolSetting(data, "developmentMode", defaults["developmentMode"].Value<bool>());

            string interfaceLayout = GetEnumSetting(data, "interfaceLayout", defaults["interfaceLayout"].Value<string>(), new[] { "clean", "classic" });

            // obs keeps its own start-minimized switch, so a settings file that never saved this choice takes the one obs has
            bool startMinimized = raw != null && raw["startMinimized"] == null
                ? ReadObsStartMinimized() ?? defaults["startMinimized"].Value<bool>()
                : GetBoolSetting(data, "startMinimized", defaults["startMinimized"].Value<bool>());

            return new JObject
            {
                ["recordingPreset"] = preset,
                ["compressionMode"] = compression,
                ["codecPreference"] = codecPreference,
                ["replaySeconds"] = replaySeconds,
                // older builds allowed a single FPS value above the current 240 cap; clamp that legacy value during migration so it cannot block the settings page from loading.
                ["fps"] = Math.Min(SettingLimits.FpsMax, GetIntSetting(data, "fps", defaults["fps"].Value<int>(), SettingLimits.FpsMin, 1000)),
                ["fpsNumerator"] = Math.Min(SettingLimits.FpsMax, GetIntSetting(data, "fpsNumerator", GetIntSetting(data, "fps", defaults["fps"].Value<int>(), SettingLimits.FpsMin, 1000), SettingLimits.FpsMin, 1000)),
                ["fpsDenominator"] = GetIntSetting(data, "fpsDenominator", defaults["fpsDenominator"].Value<int>(), 1, 1000),
                ["recordingScaleMode"] = GetEnumSetting(data, "recordingScaleMode", defaults["recordingScaleMode"].Value<string>(), new[] { "native", "downscale" }),
                ["downscaleHeight"] = GetIntSetting(data, "downscaleHeight", defaults["downscaleHeight"].Value<int>(), DownscaleHeightMin, DownscaleHeightMax),
                ["downscaleFilter"] = GetEnumSetting(data, "downscaleFilter", defaults["downscaleFilter"].Value<string>(), new[] { "bilinear", "area", "bicubic", "lanczos" }),
                ["recordingBuffer"] = GetEnumSetting(data, "recordingBuffer", defaults["recordingBuffer"].Value<string>(), new[] { ClipBuffer.Disk, ClipBuffer.Memory }),
                ["showCursorDisplayCapture"] = GetBoolSetting(data, "showCursorDisplayCapture", defaults["showCursorDisplayCapture"].Value<bool>()),
                ["showCursorGameCapture"] = GetBoolSetting(data, "showCursorGameCapture", defaults["showCursorGameCapture"].Value<bool>()),
                ["showCursorWindowCapture"] = GetBoolSetting(data, "showCursorWindowCapture", defaults["showCursorWindowCapture"].Value<bool>()),
                ["captureDisplay"] = NormalizeCaptureDisplay(data["captureDisplay"]),
                ["clipDir"] = clipDir,
                ["storageLimitGb"] = GetIntSetting(data, "storageLimitGb", defaults["storageLimitGb"].Value<int>(), 0, StorageLimitGbMax),
                ["storageCompressionType"] = GetEnumSetting(data, "storageCompressionType", defaults["storageCompressionType"].Value<string>(), new[] { "fast", "smaller" }),
                ["deletedHistoryDays"] = GetIntSetting(data, "deletedHistoryDays", defaults["deletedHistoryDays"].Value<int>(), 0, 30),
                ["clipKeybind"] = NormalizeClipKeybind(data["clipKeybind"]),
                ["recordingKeybind"] = NormalizeRecordingKeybind(data["recordingKeybind"]),
                ["openClipsKeybind"] = NormalizeOpenClipsKeybind(data["openClipsKeybind"]),
                ["clipKeybindExtra"] = NormalizeExtraKeybind(data["clipKeybindExtra"], "extra clip keybind"),
                ["recordingKeybindExtra"] = NormalizeExtraKeybind(data["recordingKeybindExtra"], "extra recording keybind"),
                ["openClipsKeybindExtra"] = NormalizeExtraKeybind(data["openClipsKeybindExtra"], "extra open clips keybind"),
                ["screenshotKeybind"] = NormalizeExtraKeybind(data["screenshotKeybind"], "screenshot keybind"),
                ["screenshotKeybindExtra"] = NormalizeExtraKeybind(data["screenshotKeybindExtra"], "extra screenshot keybind"),
                ["overlayStyle"] = GetEnumSetting(data, "overlayStyle", defaults["overlayStyle"].Value<string>(), SettingLimits.OverlayStyles),
                ["overlayOpacity"] = GetIntSetting(data, "overlayOpacity", defaults["overlayOpacity"].Value<int>(), SettingLimits.OverlayOpacityMin, SettingLimits.OverlayOpacityMax),
                ["overlayScale"] = GetIntSetting(data, "overlayScale", defaults["overlayScale"].Value<int>(), SettingLimits.OverlayScaleMin, SettingLimits.OverlayScaleMax),
                ["overlayRotation"] = GetIntSetting(data, "overlayRotation", SettingDefaults.OverlayRotation, SettingLimits.OverlayRotationMin, SettingLimits.OverlayRotationMax),
                ["overlayFlipH"] = GetBoolSetting(data, "overlayFlipH", defaults["overlayFlipH"].Value<bool>()),
                ["bongoMode"] = GetEnumSetting(data, "bongoMode", defaults["bongoMode"].Value<string>(), BongoModes),
                ["bongoKeys"] = NormalizeBongoKeys(data["bongoKeys"]),
                ["bongoLive2d"] = GetBoolSetting(data, "bongoLive2d", defaults["bongoLive2d"].Value<bool>()),
                ["bongoTrack"] = GetBoolSetting(data, "bongoTrack", defaults["bongoTrack"].Value<bool>()),
                ["bongoBreath"] = GetBoolSetting(data, "bongoBreath", defaults["bongoBreath"].Value<bool>()),
                ["bongoBlink"] = GetBoolSetting(data, "bongoBlink", defaults["bongoBlink"].Value<bool>()),
                ["bongoFaces"] = GetBoolSetting(data, "bongoFaces", defaults["bongoFaces"].Value<bool>()),
                ["bongoRelativeMouse"] = GetBoolSetting(data, "bongoRelativeMouse", defaults["bongoRelativeMouse"].Value<bool>()),
                ["bongoSpeed"] = GetFloatSetting(data, "bongoSpeed", defaults["bongoSpeed"].Value<double>(), BongoSpeedMin, BongoSpeedMax),
                ["inputOverlayKeyboard"] = GetEnumSetting(data, "inputOverlayKeyboard", defaults["inputOverlayKeyboard"].Value<string>(), InputOverlayLayout.Keyboards.Select(l => l.Id).ToArray()),
                ["inputOverlayCustomKeys"] = InputOverlayLayouts.NormalizeCustomKeys(data["inputOverlayCustomKeys"]),
                ["inputOverlayMouse"] = GetEnumSetting(data, "inputOverlayMouse", defaults["inputOverlayMouse"].Value<string>(), InputOverlayLayout.Mice.Select(l => l.Id).ToArray()),
                ["overlayHueShift"] = GetFloatSetting(data, "overlayHueShift", defaults["overlayHueShift"].Value<double>(), SettingLimits.OverlayHueShiftMin, SettingLimits.OverlayHueShiftMax),
                ["overlayColorMultiply"] = GetHexColorSetting(data, "overlayColorMultiply", defaults["overlayColorMultiply"].Value<string>()),
                ["overlayColorAdd"] = GetHexColorSetting(data, "overlayColorAdd", defaults["overlayColorAdd"].Value<string>()),
                ["overlayPosition"] = NormalizeOverlayPosition(data["overlayPosition"]),
                ["obsStartupEnabled"] = GetBoolSetting(data, "obsStartupEnabled", defaults["obsStartupEnabled"].Value<bool>()),
                ["runObsAsAdmin"] = GetBoolSetting(data, "runObsAsAdmin", defaults["runObsAsAdmin"].Value<bool>()),
                ["disableObsCloseWarning"] = GetBoolSetting(data, "disableObsCloseWarning", defaults["disableObsCloseWarning"].Value<bool>()),
                ["disableObsCrashPopup"] = GetBoolSetting(data, "disableObsCrashPopup", defaults["disableObsCrashPopup"].Value<bool>()),
                ["closeToTray"] = GetBoolSetting(data, "closeToTray", defaults["closeToTray"].Value<bool>()),
                ["browserSleepSeconds"] = GetIntSetting(data, "browserSleepSeconds", defaults["browserSleepSeconds"].Value<int>(), 0, 86400),
                ["allowSleepWhileActive"] = GetBoolSetting(data, "allowSleepWhileActive", defaults["allowSleepWhileActive"].Value<bool>()),
                ["pinObsTrayIcon"] = GetBoolSetting(data, "pinObsTrayIcon", defaults["pinObsTrayIcon"].Value<bool>()),
                ["clipNotificationEnabled"] = GetBoolSetting(data, "clipNotificationEnabled", defaults["clipNotificationEnabled"].Value<bool>()),
                ["recordingNotificationEnabled"] = GetBoolSetting(data, "recordingNotificationEnabled", defaults["recordingNotificationEnabled"].Value<bool>()),
                ["clipNotificationSeconds"] = GetIntSetting(data, "clipNotificationSeconds", replaySeconds, 1, 1200),
                ["hideNotificationsFromCapture"] = GetBoolSetting(data, "hideNotificationsFromCapture", defaults["hideNotificationsFromCapture"].Value<bool>()),
                ["autoCopyLink"] = GetBoolSetting(data, "autoCopyLink", defaults["autoCopyLink"].Value<bool>()),
                ["hevcFixEnabled"] = GetBoolSetting(data, "hevcFixEnabled", defaults["hevcFixEnabled"].Value<bool>()),
                ["linkNotificationEnabled"] = GetBoolSetting(data, "linkNotificationEnabled", defaults["linkNotificationEnabled"].Value<bool>()),
                ["startMinimized"] = startMinimized,
                ["compressSkipFavorites"] = GetBoolSetting(data, "compressSkipFavorites", defaults["compressSkipFavorites"].Value<bool>()),
                ["developmentMode"] = developmentMode,
                ["sdkMode"] = GetBoolSetting(data, "sdkMode", defaults["sdkMode"].Value<bool>()),
                ["checkObsUpdates"] = GetBoolSetting(data, "checkObsUpdates", defaults["checkObsUpdates"].Value<bool>()),
                ["logInterfaceEvents"] = GetBoolSetting(data, "logInterfaceEvents", defaults["logInterfaceEvents"].Value<bool>()),
                ["trimPreciseDefault"] = true,
                ["trimGpuEncode"] = GetBoolSetting(data, "trimGpuEncode", defaults["trimGpuEncode"].Value<bool>()),
                ["showCodecTags"] = GetBoolSetting(data, "showCodecTags", defaults["showCodecTags"].Value<bool>()),
                ["gameRecognition"] = GetBoolSetting(data, "gameRecognition", defaults["gameRecognition"].Value<bool>()),
                ["cardDateFormat"] = GetEnumSetting(data, "cardDateFormat", defaults["cardDateFormat"].Value<string>(), new[] { "relative", "date", "datetime", "short" }),
                ["uiFont"] = GetEnumSetting(data, "uiFont", defaults["uiFont"].Value<string>(), Fonts.Families),
                ["editorAutoplay"] = GetBoolSetting(data, "editorAutoplay", defaults["editorAutoplay"].Value<bool>()),
                ["editorLoopPreview"] = GetBoolSetting(data, "editorLoopPreview", defaults["editorLoopPreview"].Value<bool>()),
                ["editorSeekStep"] = GetIntSetting(data, "editorSeekStep", defaults["editorSeekStep"].Value<int>(), 1, 60),
                ["gifFrameDelay"] = int.Parse(GetEnumSetting(data, "gifFrameDelay", defaults["gifFrameDelay"].Value<int>().ToString(CultureInfo.InvariantCulture), GifFrameDelays), CultureInfo.InvariantCulture),
                ["editorWheelZoom"] = GetBoolSetting(data, "editorWheelZoom", defaults["editorWheelZoom"].Value<bool>()),
                ["editorZoomScaling"] = GetEnumSetting(data, "editorZoomScaling", defaults["editorZoomScaling"].Value<string>(), new[] { "smooth", "sharp" }),
                ["clipAntialiasing"] = GetEnumSetting(data, "clipAntialiasing", defaults["clipAntialiasing"].Value<string>(), new[] { "none", "msaa", "fxaa" }),
                ["debugLoggingEnabled"] = GetBoolSetting(data, "debugLoggingEnabled", defaults["debugLoggingEnabled"].Value<bool>()),
                ["autoDeleteLogsOnLaunch"] = GetBoolSetting(data, "autoDeleteLogsOnLaunch", defaults["autoDeleteLogsOnLaunch"].Value<bool>()),
                ["lastUpdatePromptVersion"] = GetVersionMarkerSetting(data, "lastUpdatePromptVersion", defaults["lastUpdatePromptVersion"].Value<string>()),
                ["clipSoundVolume"] = GetIntSetting(data, "clipSoundVolume", defaults["clipSoundVolume"].Value<int>(), SettingLimits.SoundVolumeMin, SettingLimits.SoundVolumeMax),
                ["recordingSoundVolume"] = GetIntSetting(data, "recordingSoundVolume", defaults["recordingSoundVolume"].Value<int>(), SettingLimits.SoundVolumeMin, SettingLimits.SoundVolumeMax),
                ["micDeviceId"] = NormalizeMicDeviceId(data["micDeviceId"]?.ToString()),
                ["micVolume"] = GetIntSetting(data, "micVolume", defaults["micVolume"].Value<int>(), 0, MicTest.VolumeMax),
                ["micNoiseSuppression"] = GetBoolSetting(data, "micNoiseSuppression", defaults["micNoiseSuppression"].Value<bool>()),
                ["micInputSensitivity"] = GetIntSetting(data, "micInputSensitivity", defaults["micInputSensitivity"].Value<int>(), MicTest.SensitivityMin, MicTest.SensitivityMax),
                ["desktopAudioVolume"] = GetIntSetting(data, "desktopAudioVolume", defaults["desktopAudioVolume"].Value<int>(), 0, MicTest.VolumeMax),
                ["discordAudioVolume"] = GetIntSetting(data, "discordAudioVolume", defaults["discordAudioVolume"].Value<int>(), 0, MicTest.VolumeMax),
                ["recordingAudioMode"] = GetEnumSetting(data, "recordingAudioMode", defaults["recordingAudioMode"].Value<string>(), new[] { "all", "specific", OutputDeviceRecordingMode }),
                ["recordingAudioApps"] = AudioApps.NormalizeChoices(data["recordingAudioApps"]),
                ["recordingSeparateAudioTracks"] = GetBoolSetting(data, "recordingSeparateAudioTracks", defaults["recordingSeparateAudioTracks"].Value<bool>()),
                ["motionBlurEnabled"] = GetBoolSetting(data, "motionBlurEnabled", defaults["motionBlurEnabled"].Value<bool>()),
                ["motionBlurStrength"] = GetFloatSetting(data, "motionBlurStrength", defaults["motionBlurStrength"].Value<double>(), SettingLimits.MotionBlurStrengthMin, SettingLimits.MotionBlurStrengthMax),
                ["shareMode"] = shareMode,
                ["discord_screenshare_enabled"] = GetBoolSetting(data, "discord_screenshare_enabled", defaults["discord_screenshare_enabled"].Value<bool>()),
                ["discord_output_mode"] = discordOutputMode,
                ["discord_projector_enabled"] = GetBoolSetting(data, "discord_projector_enabled", defaults["discord_projector_enabled"].Value<bool>()),
                ["discord_projector_width"] = GetIntSetting(data, "discord_projector_width", defaults["discord_projector_width"].Value<int>(), 0, SettingLimits.DiscordProjectorWidthMax),
                ["discord_projector_height"] = GetIntSetting(data, "discord_projector_height", defaults["discord_projector_height"].Value<int>(), 0, SettingLimits.DiscordProjectorHeightMax),
                ["discord_projector_visible_pixels"] = GetIntSetting(data, "discord_projector_visible_pixels", defaults["discord_projector_visible_pixels"].Value<int>(), 0, SettingLimits.DiscordProjectorVisiblePixelsMax),
                ["discord_projector_monitor_index"] = GetIntSetting(data, "discord_projector_monitor_index", defaults["discord_projector_monitor_index"].Value<int>(), 0, SettingLimits.DiscordProjectorMonitorIndexMax),
                ["discord_projector_edge"] = GetEnumSetting(data, "discord_projector_edge", defaults["discord_projector_edge"].Value<string>(), SettingLimits.DiscordProjectorEdges),
                ["discord_projector_title_hint"] = GetTextSetting(data, "discord_projector_title_hint", defaults["discord_projector_title_hint"].Value<string>(), SettingLimits.DiscordProjectorTitleHintMaxLength),
                ["discord_projector_hide_taskbar"] = true,
                ["screenshareCaptureMode"] = GetEnumSetting(data, "screenshareCaptureMode", defaults["screenshareCaptureMode"].Value<string>(), SettingLimits.ScreenshareCaptureModes),
                ["screenshareGameWindow"] = GetTextSetting(data, "screenshareGameWindow", defaults["screenshareGameWindow"].Value<string>(), SettingLimits.ScreenshareGameWindowMaxLength),
                ["screenshareGameOverrides"] = NormalizeScreenshareGameOverrides(data["screenshareGameOverrides"]),
                ["screenshareAutoGameKeepFocused"] = GetBoolSetting(data, "screenshareAutoGameKeepFocused", defaults["screenshareAutoGameKeepFocused"].Value<bool>()),
                ["screenshareSwitchDelaySeconds"] = GetFloatSetting(data, "screenshareSwitchDelaySeconds", defaults["screenshareSwitchDelaySeconds"].Value<double>(), SwitchDelaySecondsMin, SwitchDelaySecondsMax),
                ["appIcon"] = NormalizeAppIcon(data["appIcon"]?.ToString(), StripWrappingQuotes(data["appIconCustomPath"]?.ToString())),
                ["appIconCustomPath"] = StripWrappingQuotes(GetTextSetting(data, "appIconCustomPath", defaults["appIconCustomPath"].Value<string>(), 1024)),
                ["appIconRecordingDot"] = GetBoolSetting(data, "appIconRecordingDot", defaults["appIconRecordingDot"].Value<bool>()),
                ["theme"] = NormalizeTheme(data["theme"]?.ToString()),
                ["themeCustom"] = NormalizeThemeCustom(data["themeCustom"], (JObject)defaults["themeCustom"]),
                ["interfaceLayout"] = developmentMode ? interfaceLayout : defaults["interfaceLayout"].Value<string>(),
                ["setupWizardPending"] = GetBoolSetting(data, "setupWizardPending", defaults["setupWizardPending"].Value<bool>()),
            };
        }

        // whether the first-run setup wizard still needs to be shown. fail-closed: any read error -> not pending, so a broken settings file never traps the user in a wizard loop.
        public static JObject GetSetupState()
        {
            bool pending = false;
            try { pending = ReadSettings()["setupWizardPending"]?.Value<bool>() ?? false; }
            catch (Exception ex) { Log.Write("GetSetupState: " + ex.Message); }
            bool deferred, alreadyOpened;
            lock (Server.State.UpdateCheckLock)
            {
                deferred = Server.State.StartupUpdatePending;
                alreadyOpened = Server.State.SetupWizardOpened;
            }
            if (pending && alreadyOpened)
                return new JObject { ["ok"] = true, ["pending"] = false, ["alreadyOpened"] = true };
            // an update wins the race: report not-pending so nothing opens the wizard now, and leave the flag set in settings so the wizard opens on the next launch once the install has landed
            if (pending && deferred)
                return new JObject { ["ok"] = true, ["pending"] = false, ["deferredForUpdate"] = true };
            return new JObject { ["ok"] = true, ["pending"] = pending };
        }

        // clear the first-run flag once the wizard has written its choices through the normal /settings path.
        public static JObject SetSetupComplete()
        {
            lock (SettingsStore.Gate)
            {
                var settings = ReadSettings();
                settings["setupWizardPending"] = false;
                WriteSettings(settings);
                return new JObject { ["ok"] = true };
            }
        }

        // "default" / "custom" / a bundled preset id / "user/<name>" (an existing saved theme). unknown -> "default".
        internal static string NormalizeTheme(string value)
        {
            value = (value ?? "").Trim();
            if (value == "" || value == "default") return "default";
            if (value == "custom") return "custom";
            if (Themes.IsPreset(value)) return value;
            if (value.StartsWith("user/", StringComparison.Ordinal))
            {
                string name = Path.GetFileNameWithoutExtension(Path.GetFileName(value.Substring(5)));
                try { return (!string.IsNullOrEmpty(name) && File.Exists(Path.Combine(Constants.USER_THEMES_DIR, name + ".json"))) ? "user/" + name : "default"; }
                catch { return "default"; }
            }
            return "default";
        }

        internal static JObject NormalizeThemeCustom(JToken raw, JObject defaults)
        {
            var src = raw as JObject ?? new JObject();
            var outObj = new JObject();
            foreach (var k in new[] { "bg", "panel", "field", "text", "accent", "border", "danger" })
                outObj[k] = Themes.CleanHex(src[k]?.ToString(), defaults[k].ToString());
            outObj["bar"] = Themes.CleanOptionalHex(src["bar"]?.ToString());
            outObj["gradient"] = Themes.CleanOptionalHex(src["gradient"]?.ToString());
            // gradients is the current per-token shape; the flat gradient* keys are still carried through so a settings file written before per-token gradients still resolves its background gradient (Themes.ApplyGradient reads both).
            outObj["gradients"] = src["gradients"] is JObject gradients ? gradients.DeepClone() : new JObject();
            foreach (var legacy in new[] { "gradientType", "gradientAngle", "gradientCenterX", "gradientCenterY", "gradientStops" })
            {
                if (src[legacy] != null) outObj[legacy] = src[legacy].DeepClone();
            }
            outObj["dark"] = src["dark"]?.Type == JTokenType.Boolean ? src["dark"].Value<bool>() : defaults["dark"].Value<bool>();
            if (src["offsets"] != null) outObj["offsets"] = src["offsets"].DeepClone();
            return Themes.FromCustom(outObj).ToSeedJson();
        }

        internal static readonly string[] AppIconExts = { ".ico", ".png", ".jpg", ".jpeg", ".bmp" };

        // a "user/<name>.ico" id -> absolute path in USER_ICONS_DIR, or null if the id is malformed. Path.GetFileName strips any traversal.
        internal static string UserIconIdToPath(string id)
        {
            if (string.IsNullOrEmpty(id) || !id.StartsWith("user/", StringComparison.Ordinal)) return null;
            string name = Path.GetFileName(id.Substring(5));
            if (string.IsNullOrEmpty(name) || !name.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)) return null;
            return Path.Combine(Constants.USER_ICONS_DIR, name);
        }

        // "default" / "custom" / "user/<name>.ico" (a saved custom pick) / a bare filename in the bundled icons/ folder. anything unrecognised falls back to "default" so a stale settings file never leaves obs iconless.
        internal static string NormalizeAppIcon(string value, string customPath)
        {
            value = (value ?? "").Trim();
            if (value == "" || value == "default") return "default";
            if (value == "custom")
                return TestUsableIconFile(customPath) ? "custom" : "default";
            if (value.StartsWith("user/", StringComparison.Ordinal))
            {
                string up = UserIconIdToPath(value);
                try { return (up != null && File.Exists(up)) ? "user/" + Path.GetFileName(up) : "default"; }
                catch { return "default"; }
            }
            string safe = Path.GetFileName(value);
            if (string.Equals(safe, "replaykit.ico", StringComparison.OrdinalIgnoreCase)) return "default";
            if (safe != value || !AppIconExts.Contains(Path.GetExtension(safe))) return "default";
            try { return File.Exists(Path.Combine(Constants.APP_ICONS_DIR, safe)) ? safe : "default"; }
            catch { return "default"; }
        }

        internal static bool TestUsableIconFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try { return File.Exists(path) && AppIconExts.Contains(Path.GetExtension(path)); }
            catch { return false; }
        }

        // absolute path to the image the current appIcon resolves to, or "" when it should be left at the obs/replaykit default.
        public static string ResolveAppIconPath(JObject settings)
        {
            string id = settings?["appIcon"]?.Value<string>() ?? "default";
            if (id == "default") return "";
            if (id == "custom")
            {
                string custom = settings?["appIconCustomPath"]?.Value<string>() ?? "";
                return TestUsableIconFile(custom) ? custom : "";
            }
            if (id.StartsWith("user/", StringComparison.Ordinal))
            {
                try { string up = UserIconIdToPath(id); return (up != null && File.Exists(up)) ? up : ""; }
                catch { return ""; }
            }
            try
            {
                string p = Path.Combine(Constants.APP_ICONS_DIR, Path.GetFileName(id));
                return File.Exists(p) ? p : "";
            }
            catch { return ""; }
        }

        // converts a picked image to a multi-res .ico under USER_ICONS_DIR so it becomes a deletable preset, and returns its "user/<name>.ico" id (or null on failure). same source bytes -> same name, so re-picking a file never duplicates.
        public static string ImportCustomIcon(string srcPath)
        {
            if (!TestUsableIconFile(srcPath)) return null;
            try
            {
                Directory.CreateDirectory(Constants.USER_ICONS_DIR);
                string hash;
                using (var sha = System.Security.Cryptography.SHA1.Create())
                using (var fs = File.OpenRead(srcPath))
                    hash = BitConverter.ToString(sha.ComputeHash(fs), 0, 4).Replace("-", "").ToLowerInvariant();
                string name = SanitizeIconBaseName(Path.GetFileNameWithoutExtension(srcPath)) + "-" + hash + ".ico";
                string dest = Path.Combine(Constants.USER_ICONS_DIR, name);
                if (!File.Exists(dest)) Native.ConvertImageToIco(srcPath, dest);
                return "user/" + name;
            }
            catch (Exception ex) { Log.Write("ImportCustomIcon: " + ex.Message); return null; }
        }

        internal static string SanitizeIconBaseName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "icon";
            var sb = new System.Text.StringBuilder();
            foreach (char c in s.Trim())
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-');
            string outp = sb.ToString().Trim('-');
            if (outp.Length > 40) outp = outp.Substring(0, 40);
            return outp.Length == 0 ? "icon" : outp;
        }

        // read settings, set appIcon to id, persist, and push it live. used by /appearance/delete-icon when the removed icon was the active one.
        public static JObject SetAppIconAndApply(string id)
        {
            lock (SettingsStore.Gate)
            {
                var settings = Normalize(ReadSettings());
                settings["appIcon"] = id;
                settings = Normalize(settings);
                WriteSettings(settings);
                return ApplyAppIconLive(settings);
            }
        }

        // set theme to id, persist, write the obs .ovt + user.ini. returns true (obs restart still needed to pick it up). used by /appearance/delete-theme when the removed theme was the active one.
        public static bool SetThemeAndApply(string id)
        {
            lock (SettingsStore.Gate)
            {
                var settings = Normalize(ReadSettings());
                settings["theme"] = id;
                settings = Normalize(settings);
                WriteSettings(settings);
                try { Themes.ApplyToObs(settings); } catch (Exception ex) { Log.Write("SetThemeAndApply: " + ex.Message); }
                return true;
            }
        }

        // what a ReplayKit-branded surface (own windows, toasts, dock favicon) should use right now: the chosen custom/preset icon, or the bundled replaykit .ico when appIcon is "default".
        public static string EffectiveReplayKitIconPath()
        {
            try
            {
                string p = ResolveAppIconPath(Normalize(ReadSettings()));
                if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
            }
            catch { }
            return File.Exists(Constants.OBS_ICON_PATH) ? Constants.OBS_ICON_PATH : null;
        }

        public static bool TestDiscordScreenshareEnabled(JObject settings)
        {
            if (settings == null) return true;
            var token = settings["discord_screenshare_enabled"];
            return token == null || token.Value<bool>();
        }

        // sound/media device setup class; one NNNN subkey per installed audio driver. mirrors VbCable.IsVbcableInstalled in the setup exe -- the settings dock uses this to show Install vs Uninstall on the Share Preview audio row.
        internal const string MediaClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e96c-e325-11ce-bfc1-08002be10318}";

        public static bool IsShareAudioDriverInstalled()
        {
            try
            {
                using var classKey = Registry.LocalMachine.OpenSubKey(MediaClassKey);
                if (classKey == null) return false;
                foreach (string sub in classKey.GetSubKeyNames())
                {
                    if (sub.Length != 4 || !sub.All(char.IsDigit)) continue;
                    using var dev = classKey.OpenSubKey(sub);
                    string desc = dev?.GetValue("DriverDesc") as string;
                    if (!string.IsNullOrEmpty(desc) && desc.IndexOf("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException) { }
            return false;
        }

        public static JObject ReadSettings()
        {
            string path = GetSettingsPath();
            if (!File.Exists(path)) return Normalize(new JObject());
            try
            {
                var fi = new FileInfo(path);
                if (fi.Length > 65536) throw new InvalidOperationException("Settings file is too large.");
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return Normalize(new JObject());
                JObject raw;
                try { raw = JObject.Parse(json); }
                catch (Newtonsoft.Json.JsonException parse) { return SetAsideUnreadableSettings(path, parse); }
                return Normalize(raw);
            }
            catch (Exception ex)
            {
                Log.Write("Read-ReplayKitSettings failed: " + ex.Message);
                throw new InvalidOperationException("ReplayKit settings file is invalid: " + ex.Message, ex);
            }
        }

        // a file that is not json at all (cut short, or zeros where the text was after a power cut) left every page and every save refusing until it was found and deleted by hand, so it is kept aside and the defaults take over, and the bell says so; a file that only could not be opened right now is left alone by the caller
        private static JObject SetAsideUnreadableSettings(string path, Exception error)
        {
            string aside = path + ".unreadable";
            lock (SettingsStore.Gate)
            {
                // another request may have set it aside and saved a good file since
                if (File.Exists(path))
                {
                    try { return Normalize(JObject.Parse(File.ReadAllText(path))); }
                    catch (Newtonsoft.Json.JsonException) { }
                    Native.MoveFileReplace(path, aside);
                    Log.Write("Read-ReplayKitSettings: the settings file was not json (" + error.Message + "), kept it as " + aside + " and went back to the defaults.");
                    try { Notifications.Add("warning", "settings-unreadable", Locales.T("ReplayKit settings were reset"), Locales.T("The settings file could not be read, so ReplayKit went back to its defaults. The old file was kept as {file} in the user-data folder of ReplayKit.", "file", Path.GetFileName(aside))); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is Newtonsoft.Json.JsonException) { Log.Write("settings reset notice: " + ex.Message); }
                }
            }
            return Normalize(new JObject());
        }

        // whether the compress list of the storage card starts out without the favorites
        public static bool GetCompressSkipFavorites()
        {
            try { return ReadSettings()["compressSkipFavorites"]?.Value<bool>() ?? true; }
            catch (InvalidOperationException) { return true; }
        }

        public static string GetStorageCompressionType() => ReadSettings()["storageCompressionType"].Value<string>();

        // the time each gif frame shows, in hundredths of a second: a gif stores frame times in whole hundredths, so only rates that divide 100 evenly play at an even pace (2 = 50 fps, 3 = 33 fps, 4 = 25 fps, 5 = 20 fps, 10 = 10 fps)
        internal static readonly string[] GifFrameDelays = { "2", "3", "4", "5", "10" };

        public static int GetGifFrameDelay()
        {
            try
            {
                string v = ReadSettings()["gifFrameDelay"]?.ToString() ?? "3";
                return GifFrameDelays.Contains(v) ? int.Parse(v, CultureInfo.InvariantCulture) : 3;
            }
            catch (InvalidOperationException) { return 3; }
        }

        // the days deleted clips and screenshots are kept, 0 when deleted history is off; a settings file that cannot be read keeps the default, so a delete is never made final by a read error
        public static int GetDeletedHistoryDays()
        {
            try { return Math.Max(0, Math.Min(30, ReadSettings()["deletedHistoryDays"]?.Value<int>() ?? 7)); }
            catch (InvalidOperationException) { return 7; }
        }

        // whether the saves that make the video again may use the graphics card encoder
        public static bool GetTrimGpuEncode()
        {
            try { return ReadSettings()["trimGpuEncode"]?.Value<bool>() ?? true; }
            catch (InvalidOperationException) { return true; }
        }

        // the recording codec the saves follow: auto, h264 or h265
        public static string GetCodecPreference()
        {
            try { return ReadSettings()["codecPreference"]?.Value<string>() ?? DefaultCodecPreference; }
            catch (InvalidOperationException) { return DefaultCodecPreference; }
        }

        // development mode is what lets the development settings do anything, so logging needs both
        public static bool DevelopmentFlag(JObject settings, string key) =>
            (settings["developmentMode"]?.Value<bool>() ?? false) && (settings[key]?.Value<bool>() ?? false);

        // the storage limit in GB, 0 when none is set or the settings cannot be read
        public static long GetStorageLimitGb()
        {
            try { return Math.Max(0, ReadSettings()["storageLimitGb"]?.Value<long>() ?? 0); }
            catch (InvalidOperationException) { return 0; }
        }

        public static void WriteSettings(JObject settings)
        {
            lock (SettingsStore.Gate)
            {
                string path = GetSettingsPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                AppConfig.WriteUtf8(path, settings.ToString(Formatting.Indented));
                SyncClipDirToRuntimeConfig(settings);
                ClipGames.SetRecognition(settings["gameRecognition"]?.Type != JTokenType.Boolean || settings["gameRecognition"].Value<bool>());
                ObsUpdate.SetEnabled(DevelopmentFlag(settings, "checkObsUpdates"));
            }
        }

        // the clip listing resolves its folder through AppConfig.GetClipDir, which reads the runtime helper_config -- that config only carries what the lua bootstrap wrote, so a folder chosen in ReplayKit Settings never reached it and the listing silently walked the default folder instead. mirror the saved value across on every write and at startup so Settings is the single source of truth.
        public static void SyncClipDirToRuntimeConfig(JObject settings)
        {
            try
            {
                if (Server.State.Config == null) return;
                string clipDir = settings?["clipDir"]?.Value<string>();
                if (string.IsNullOrWhiteSpace(clipDir)) return;
                if (string.Equals(Server.State.Config["clipDir"]?.Value<string>(), clipDir, StringComparison.Ordinal)) return;
                Server.State.Config["clipDir"] = clipDir;
            }
            catch (Exception ex) { Log.Write("SyncClipDirToRuntimeConfig: " + ex.Message); }
        }

        // codec for the Auto Game List: a saved games (title, window class, exe name) triple packed into one string token (# and : escaped) so it fits a json string array
        internal static string ConvertToObsWindowTokenPart(string value) => (value ?? "").Replace("#", "#22").Replace(":", "#3A");
        internal static string ConvertFromObsWindowTokenPart(string value) => (value ?? "").Replace("#3A", ":").Replace("#22", "#");

        internal static string NewObsWindowToken(string title, string className, string exeName) =>
            ConvertToObsWindowTokenPart(title) + ":" + ConvertToObsWindowTokenPart(className) + ":" + ConvertToObsWindowTokenPart(exeName);

        public static string GetWindowLabelFromToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return "Saved game window";
            var parts = token.Split(new[] { ':' }, 3);
            if (parts.Length < 3) return "Saved game window";
            string title = ConvertFromObsWindowTokenPart(parts[0]);
            string exe = ConvertFromObsWindowTokenPart(parts[2]);
            return string.IsNullOrWhiteSpace(title) ? "[" + exe + "]" : "[" + exe + "]: " + title;
        }

        internal static readonly string[] BlockedGameWindowExes =
        {
            "applicationframehost.exe", "discord.exe", "discordcanary.exe", "discorddevelopment.exe",
            "discordptb.exe", "discordsystemhelper.exe", "explorer.exe", "lockapp.exe",
            "obs.exe", "obs32.exe", "obs64.exe", "searchapp.exe", "searchhost.exe",
            "shellexperiencehost.exe", "startmenuexperiencehost.exe", "systemsettings.exe",
            "textinputhost.exe", "time.exe", "video.ui.exe",
        };

        public static bool TestBlockedGameWindowExe(string exeName)
        {
            string exe = (exeName ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(exe)) return true;
            return BlockedGameWindowExes.Contains(exe);
        }

        public static JObject ConvertFromObsWindowToken(string token)
        {
            string raw = (token ?? "").Trim();
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (raw.Length > 512 || Regex.IsMatch(raw, @"[\x00-\x1F]")) throw new InvalidOperationException("Invalid game window token.");
            var parts = raw.Split(new[] { ':' }, 3);
            if (parts.Length < 3) throw new InvalidOperationException("Invalid game window token.");

            string title = ConvertFromObsWindowTokenPart(parts[0]).Trim();
            string className = ConvertFromObsWindowTokenPart(parts[1]).Trim();
            string exe = ConvertFromObsWindowTokenPart(parts[2]).Trim();
            if (title.Length > 160 || className.Length > 120 || exe.Length > 96) throw new InvalidOperationException("Invalid game window token.");
            if (Regex.IsMatch(title + className + exe, @"[\x00-\x1F]")) throw new InvalidOperationException("Invalid game window token.");
            if (!Regex.IsMatch(exe, @"^[^\\/:*?""<>|]+\.exe$")) throw new InvalidOperationException("Invalid game window executable.");
            if (TestBlockedGameWindowExe(exe)) throw new InvalidOperationException("That application cannot be added to the Auto Game List.");

            string cleanToken = NewObsWindowToken(title, className, exe);
            return new JObject
            {
                ["token"] = cleanToken,
                ["value"] = cleanToken,
                ["label"] = GetWindowLabelFromToken(cleanToken),
                ["title"] = title,
                ["className"] = className,
                ["exeName"] = exe,
            };
        }

        // everything below changes the running obs or its live scene collection file, as opposed to the schema/normalize code above

        internal static JObject ObsResultAsJson(ObsWebSocketResult r) => r.Ok ? new JObject { ["ok"] = true } : new JObject { ["ok"] = false, ["message"] = r.Message };

        // memory cap for the replay buffer: a peak bitrate estimate per preset tier (the tiers assume 720p for performance and 1080p for the others), raised in proportion to the pixel count of the output actually recorded, times the buffer length; a smaller output never lowers it below the tier estimate.
        internal static double ScaledRbSizeMb(string presetName, int replaySeconds, JObject preset)
        {
            double peakMbps = presetName == "performance" ? 8 : presetName == "quality" ? 32 : 20;
            double referencePixels = presetName == "performance" ? 1280.0 * 720 : 1920.0 * 1080;
            double outputPixels = (preset?["video"]?["outputWidth"]?.Value<double>() ?? 0) * (preset?["video"]?["outputHeight"]?.Value<double>() ?? 0);
            double mbPerSecond = peakMbps * Math.Max(1.0, outputPixels / referencePixels) * 1.5 / 8;
            return Math.Max(32, Math.Ceiling(mbPerSecond * replaySeconds));
        }

        internal static JObject StopObsOutputIfActive(string statusRequest, string stopRequest, string label)
        {
            var status = ObsWebSocket.InvokeRequest(statusRequest, null, 3000);
            if (!status.Ok) return new JObject { ["ok"] = true, ["wasActive"] = false, ["warning"] = "Could not read " + label + " state: " + status.Message };
            if (!(status.Data?["outputActive"]?.Value<bool>() ?? false)) return new JObject { ["ok"] = true, ["wasActive"] = false };
            var stop = ObsWebSocket.InvokeRequest(stopRequest, null, 8000);
            if (!stop.Ok) return new JObject { ["ok"] = false, ["wasActive"] = true, ["warning"] = "Could not stop " + label + " before applying live settings: " + stop.Message };
            var wait = WaitObsOutputState(statusRequest, false, label, 8000);
            if (wait["ok"]?.Value<bool>() != true) return new JObject { ["ok"] = false, ["wasActive"] = true, ["warning"] = wait["warning"] };
            return new JObject { ["ok"] = true, ["wasActive"] = true };
        }

        internal static JObject WaitObsOutputState(string statusRequest, bool desiredActive, string label, int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(1000, timeoutMs));
            do
            {
                var status = ObsWebSocket.InvokeRequest(statusRequest, null, 3000);
                if (status.Ok && (status.Data?["outputActive"]?.Value<bool>() ?? false) == desiredActive) return new JObject { ["ok"] = true };
                // a status read over the cached socket costs a few ms, and every poll gap is time the clip buffer stays stopped
                Thread.Sleep(50);
            } while (DateTime.UtcNow < deadline);

            string want = desiredActive ? "start" : "stop";
            return new JObject { ["ok"] = false, ["warning"] = "Timed out waiting for " + label + " to " + want + "." };
        }

        internal static JObject StartObsOutputIfNeeded(JObject state, string startRequest, string label)
        {
            if (state["wasActive"]?.Value<bool>() != true) return new JObject { ["ok"] = true };
            PipeClient.ReleaseObsOutputsAndWait(1500);
            var start = ObsWebSocket.InvokeRequest(startRequest, null, 8000);
            if (!start.Ok) return new JObject { ["ok"] = false, ["warning"] = "Could not restart " + label + " after applying live settings: " + start.Message };
            string statusRequest = startRequest == "StartRecord" ? "GetRecordStatus"
                : startRequest == "StartReplayBuffer" ? "GetReplayBufferStatus"
                : startRequest == "StartVirtualCam" ? "GetVirtualCamStatus"
                : "";
            if (statusRequest != "")
            {
                var wait = WaitObsOutputState(statusRequest, true, label, 8000);
                if (wait["ok"]?.Value<bool>() != true) return new JObject { ["ok"] = false, ["warning"] = wait["warning"] };
            }
            return new JObject { ["ok"] = true };
        }

        internal static ObsWebSocketResult SetVideoSettingsLive(JObject preset) => ObsWebSocket.InvokeRequest("SetVideoSettings", preset["video"], 5000);

        internal static void SetFractionalFpsProfile(JObject preset, List<string> warnings)
        {
            int numerator = preset["video"]?["fpsNumerator"]?.Value<int>() ?? 0;
            int denominator = preset["video"]?["fpsDenominator"]?.Value<int>() ?? 0;
            if (numerator < 1 || denominator < 1) {
                warnings.Add("OBS video settings were applied, but the fractional FPS value was invalid.");
                return;
            }
            foreach (var update in new[] {
                new[] { "FPSType", "2" }, new[] { "FPSNum", numerator.ToString() },
                new[] { "FPSDen", denominator.ToString() },
            }) {
                var result = SetObsProfileParameterSafe("Video", update[0], update[1]);
                if (!result.Ok) warnings.Add(Locales.T("OBS did not accept Video.{update}: {error}", "update", update[0], "error", result.Message));
            }
        }

        internal static ObsWebSocketResult SetReplayBufferOutputLive(JObject settings, JObject preset)
        {
            JObject outputSettings = new JObject();
            var existing = ObsWebSocket.InvokeRequest("GetOutputSettings", new JObject { ["outputName"] = "Replay Buffer" }, 3000);
            if (existing.Ok && existing.Data?["outputSettings"] is JObject existingSettings) outputSettings = (JObject)existingSettings.DeepClone();

            string clipDir = settings["clipDir"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(clipDir)) clipDir = AppConfig.GetDefaultClipDir();
            outputSettings["max_time_sec"] = settings["replaySeconds"]?.Value<int>() ?? 0;
            outputSettings["max_size_mb"] = ScaledRbSizeMb(settings["recordingPreset"]?.Value<string>(), settings["replaySeconds"]?.Value<int>() ?? 0, preset);
            outputSettings["directory"] = clipDir;
            outputSettings["path"] = clipDir;

            return ObsWebSocket.InvokeRequest("SetOutputSettings", new JObject { ["outputName"] = "Replay Buffer", ["outputSettings"] = outputSettings }, 5000);
        }

        internal static JObject GetSceneName()
        {
            var scenes = ObsWebSocket.InvokeRequest("GetSceneList", null, 3000);
            if (!scenes.Ok) return new JObject { ["ok"] = false, ["message"] = scenes.Message };
            string current = scenes.Data?["currentProgramSceneName"]?.Value<string>();
            if (!string.IsNullOrEmpty(current)) return new JObject { ["ok"] = true, ["name"] = current };
            if (scenes.Data?["scenes"] is JArray list && list.Count > 0) return new JObject { ["ok"] = true, ["name"] = list[0]["sceneName"]?.Value<string>() };
            return new JObject { ["ok"] = false, ["message"] = "No OBS scene is available." };
        }

        internal static JObject GetSceneItems(string sceneName)
        {
            var r = ObsWebSocket.InvokeRequest("GetSceneItemList", new JObject { ["sceneName"] = sceneName }, 3000);
            if (!r.Ok) return new JObject { ["ok"] = false, ["message"] = r.Message, ["items"] = new JArray() };
            return new JObject { ["ok"] = true, ["items"] = r.Data?["sceneItems"] as JArray ?? new JArray() };
        }

        internal static JObject FindSceneItem(JArray items, string sourceName)
        {
            if (items == null) return null;
            foreach (var item in items)
            {
                if (item is JObject obj && obj["sourceName"]?.Value<string>() == sourceName) return obj;
            }
            return null;
        }

        internal static JObject SetSceneItemEnabled(string sceneName, JObject item, bool enabled)
        {
            if (item == null) return new JObject { ["ok"] = true, ["skipped"] = true };
            var r = ObsWebSocket.InvokeRequest("SetSceneItemEnabled", new JObject
            {
                ["sceneName"] = sceneName,
                ["sceneItemId"] = item["sceneItemId"]?.Value<int>() ?? 0,
                ["sceneItemEnabled"] = enabled,
            }, 3000);
            return ObsResultAsJson(r);
        }

        // removes the scene-item reference, not the source. used to drop duplicate top-level overlay items -- the source stays alive because "Group" still references it.
        internal static JObject RemoveSceneItem(string sceneName, JObject item)
        {
            if (item == null) return new JObject { ["ok"] = true, ["skipped"] = true };
            var r = ObsWebSocket.InvokeRequest("RemoveSceneItem", new JObject
            {
                ["sceneName"] = sceneName,
                ["sceneItemId"] = item["sceneItemId"]?.Value<int>() ?? 0,
            }, 3000);
            return ObsResultAsJson(r);
        }

        // obs-websocket only accepts a specific subset of transform fields in SetSceneItemTransform (a full GetSceneItemTransform readback includes derived/readonly fields like sourceWidth that the setter rejects) -- this is the allow-list.
        internal static readonly string[] SettableTransformKeys = { "positionX", "positionY", "scaleX", "scaleY", "rotation", "alignment", "boundsType", "boundsAlignment", "cropToBounds", "cropLeft", "cropRight", "cropTop", "cropBottom" };

        internal static JObject GetSettableSceneItemTransform(JObject transform)
        {
            var outObj = new JObject();
            if (transform == null) return outObj;
            foreach (var key in SettableTransformKeys)
            {
                var value = transform[key];
                if (value != null && value.Type != JTokenType.Null) outObj[key] = value;
            }
            foreach (var key in new[] { "boundsWidth", "boundsHeight" })
            {
                var value = transform[key];
                if (value == null || value.Type == JTokenType.Null) continue;
                try { if (value.Value<double>() > 0.0) outObj[key] = value; } catch (FormatException) { } catch (InvalidCastException) { }
            }
            return outObj;
        }

        internal static JObject SetSceneItemTransform(string sceneName, JObject item, JObject transform)
        {
            if (item == null) return new JObject { ["ok"] = true, ["skipped"] = true };
            var r = ObsWebSocket.InvokeRequest("SetSceneItemTransform", new JObject
            {
                ["sceneName"] = sceneName,
                ["sceneItemId"] = item["sceneItemId"]?.Value<int>() ?? 0,
                ["sceneItemTransform"] = GetSettableSceneItemTransform(transform),
            }, 3000);
            return ObsResultAsJson(r);
        }

        internal static JObject GetSceneItemTransformLive(string sceneName, JObject item)
        {
            if (item == null) return new JObject { ["ok"] = true, ["skipped"] = true, ["transform"] = null };
            var r = ObsWebSocket.InvokeRequest("GetSceneItemTransform", new JObject
            {
                ["sceneName"] = sceneName,
                ["sceneItemId"] = item["sceneItemId"]?.Value<int>() ?? 0,
            }, 3000);
            if (!r.Ok) return new JObject { ["ok"] = false, ["message"] = r.Message, ["transform"] = null };
            JToken transform = r.Data?["sceneItemTransform"] ?? r.Data;
            return new JObject { ["ok"] = true, ["skipped"] = false, ["transform"] = transform };
        }

        // the one scene item that carries the whole overlay for a style -- bongo is a single source, the input overlay is the wasd+mouse group.
        internal static string OverlayItemNameForStyle(string style)
        {
            if (style == "bongo_cat") return "Bongo Cat Overlay";
            if (style == "input_overlay") return "Group";
            return null;
        }

        internal static JObject GetCanvasSizeLive()
        {
            double w = 0.0, h = 0.0;
            var r = ObsWebSocket.InvokeRequest("GetVideoSettings", null, 3000);
            if (r.Ok)
            {
                w = GetDoubleValue(r.Data, "baseWidth", 0.0);
                h = GetDoubleValue(r.Data, "baseHeight", 0.0);
            }
            if (w <= 0.0 || h <= 0.0) { w = 1920.0; h = 1080.0; }
            return new JObject { ["width"] = w, ["height"] = h };
        }

        // resolves the live overlay item plus the scene it sits in, or nulls when obs/the item isnt there. style comes back so callers can say which overlay the rect actually belongs to.
        internal static JObject ResolveOverlayItemLive(out string sceneName, out string style)
        {
            sceneName = null;
            style = Normalize(ReadSettings())["overlayStyle"]?.Value<string>() ?? "";
            string itemName = OverlayItemNameForStyle(style);
            if (itemName == null) return null;
            var scene = GetSceneName();
            if (scene["ok"]?.Value<bool>() != true) return null;
            sceneName = scene["name"]?.Value<string>();
            return FindSceneItem(GetSceneItems(sceneName)["items"] as JArray, itemName);
        }

        // the overlays live rect as canvas fractions. the in-page projector seeds and polls this so it mirrors wherever the item actually sits, including drags the user did in obs itself.
        public static JObject GetOverlayPositionLive()
        {
            // the canvas size rides along even when the item cannot be resolved (overlay off, scene not built yet) so the fake projector still matches the real ones aspect and resolution
            JObject miss;
            try
            {
                var c = GetCanvasSizeLive();
                miss = new JObject { ["ok"] = true, ["available"] = false, ["canvasWidth"] = c["width"], ["canvasHeight"] = c["height"] };
            }
            catch { miss = new JObject { ["ok"] = true, ["available"] = false }; }
            try
            {
                var item = ResolveOverlayItemLive(out string sceneName, out string style);
                if (item == null) return miss;
                var cur = GetSceneItemTransformLive(sceneName, item);
                var t = cur["transform"] as JObject;
                if (cur["ok"]?.Value<bool>() != true || t == null) return miss;
                var canvas = GetCanvasSizeLive();
                double cw = canvas["width"].Value<double>(), ch = canvas["height"].Value<double>();
                var rect = OverlayRotation.Rectangle(t);
                double x = rect["x"].Value<double>(), y = rect["y"].Value<double>();
                double w = rect["w"].Value<double>(), h = rect["h"].Value<double>();
                return new JObject
                {
                    ["ok"] = true, ["available"] = true,
                    ["x"] = cw > 0 ? x / cw : 0.0, ["y"] = ch > 0 ? y / ch : 0.0,
                    ["w"] = cw > 0 ? w / cw : 0.0, ["h"] = ch > 0 ? h / ch : 0.0,
                    ["canvasWidth"] = cw, ["canvasHeight"] = ch,
                    // which overlay this rect belongs to -- the preview ignores it while the style dropdown shows a different one, since that item is not in the scene until apply
                    ["style"] = style,
                    // the saved overlayScale this rect was produced at -- the preview divides by it so an unapplied size edit can grow the box from the real one
                    ["scalePct"] = OverlayScaleFactor(Normalize(ReadSettings())) * 100.0,
                    ["rotation"] = GetDoubleValue(t, "rotation", 0.0),
                };
            }
            catch (Exception ex) { Log.Write("GetOverlayPositionLive: " + ex.Message); return miss; }
        }

        internal static double GetDoubleValue(JToken obj, string key, double def)
        {
            var value = obj?[key];
            if (value == null || value.Type == JTokenType.Null) return def;
            try { return value.Value<double>(); } catch (FormatException) { return def; } catch (InvalidCastException) { return def; }
        }

        // which side of an axis stays put when an overlay is resized in place: -1 the near edge, 1 the far edge, 0 the center. an edge touching the canvas edge wins, so the bottom-left bongo cat stays flush however tall it gets -- judging by the center alone sent a bongo cat at the maximum size into the middle third and it shrank back up off the bottom; otherwise an item in an outer third keeps to that side
        internal static int ScaleAnchor(double start, double size, double canvas)
        {
            if (canvas <= 0.0 || size <= 0.0) return 0;
            if (start <= 1.0) return -1;
            if (start + size >= canvas - 1.0) return 1;
            double center = start + size / 2.0;
            if (center <= canvas / 3.0) return -1;
            if (center >= canvas * 2.0 / 3.0) return 1;
            return 0;
        }

        // re-derives a new position/scale from whatever transform obs currently reports, for a live overlay resize without a full geometry recompute, anchored by ScaleAnchor. a mirrored item has its position on its right edge, so the box is worked out from that first.
        internal static JObject GetScaledTransformFromCurrent(JToken transform, double scaleRatio, JObject preset = null)
        {
            if (transform == null) return new JObject();
            if (scaleRatio <= 0.0) scaleRatio = 1.0;

            var rect = OverlayRotation.Rectangle((JObject)transform);
            double width = rect["w"].Value<double>(), height = rect["h"].Value<double>();

            double nextWidth = width * scaleRatio;
            double nextHeight = height * scaleRatio;
            double left = rect["x"].Value<double>(), top = rect["y"].Value<double>();

            double canvasW = 0.0, canvasH = 0.0;
            if (preset?["video"] != null)
            {
                canvasW = GetDoubleValue(preset["video"], "baseWidth", 0.0);
                canvasH = GetDoubleValue(preset["video"], "baseHeight", 0.0);
            }
            double Moved(double start, double size, double next, double canvas)
            {
                int anchor = ScaleAnchor(start, size, canvas);
                return anchor < 0 ? start : anchor > 0 ? start + size - next : start + (size - next) / 2.0;
            }
            double nextLeft = Moved(left, width, nextWidth, canvasW);
            double nextTop = Moved(top, height, nextHeight, canvasH);

            return OverlayRotation.Resize((JObject)transform, scaleRatio, nextLeft, nextTop);
        }

        internal static JObject SetSceneItemScaledFromCurrent(string sceneName, JObject item, double scaleRatio, JObject preset = null)
        {
            if (item == null) return new JObject { ["ok"] = true, ["skipped"] = true };
            var current = GetSceneItemTransformLive(sceneName, item);
            if (current["ok"]?.Value<bool>() != true) return current;
            var transform = GetScaledTransformFromCurrent(current["transform"], scaleRatio, preset);
            return SetSceneItemTransform(sceneName, item, transform);
        }

        // finds the named input as a scene item, or creates it if missing -- CreateInput can fail if a same-named input already exists outside this scene (obs inputs are global), so the failure path falls back to SetInputSettings + CreateSceneItem against the existing input instead of treating that as fatal.
        internal static JObject EnsureInputSceneItem(string sceneName, string name, string kind, JObject inputSettings, bool enabled)
        {
            var items = GetSceneItems(sceneName);
            if (items["ok"]?.Value<bool>() != true) return items;
            var item = FindSceneItem(items["items"] as JArray, name);
            if (item != null)
            {
                var settingsResult = ObsWebSocket.InvokeRequest("SetInputSettings", new JObject { ["inputName"] = name, ["inputSettings"] = inputSettings, ["overlay"] = true }, 3000);
                if (!settingsResult.Ok) return ObsResultAsJson(settingsResult);
                var enableResult = SetSceneItemEnabled(sceneName, item, enabled);
                if (enableResult["ok"]?.Value<bool>() != true) return enableResult;
                return new JObject { ["ok"] = true, ["item"] = item };
            }

            var created = ObsWebSocket.InvokeRequest("CreateInput", new JObject
            {
                ["sceneName"] = sceneName,
                ["inputName"] = name,
                ["inputKind"] = kind,
                ["inputSettings"] = inputSettings,
                ["sceneItemEnabled"] = enabled,
            }, 5000);
            if (!created.Ok)
            {
                var settingsResult = ObsWebSocket.InvokeRequest("SetInputSettings", new JObject { ["inputName"] = name, ["inputSettings"] = inputSettings, ["overlay"] = true }, 3000);
                if (!settingsResult.Ok) return ObsResultAsJson(created);
                var sceneItem = ObsWebSocket.InvokeRequest("CreateSceneItem", new JObject { ["sceneName"] = sceneName, ["sourceName"] = name, ["sceneItemEnabled"] = enabled }, 5000);
                if (!sceneItem.Ok) return ObsResultAsJson(created);
                return new JObject { ["ok"] = true, ["item"] = new JObject { ["sceneItemId"] = sceneItem.Data?["sceneItemId"]?.Value<int>() ?? 0, ["sourceName"] = name } };
            }
            return new JObject { ["ok"] = true, ["item"] = new JObject { ["sceneItemId"] = created.Data?["sceneItemId"]?.Value<int>() ?? 0, ["sourceName"] = name } };
        }

        internal static JObject GetSceneItemIndexValue(string sceneName, JObject item)
        {
            if (item == null) return new JObject { ["ok"] = false, ["message"] = "Scene item is missing.", ["index"] = -1 };
            var value = item["sceneItemIndex"];
            if (value != null && value.Type != JTokenType.Null)
            {
                try { return new JObject { ["ok"] = true, ["index"] = value.Value<int>() }; } catch (FormatException) { } catch (InvalidCastException) { }
            }
            var result = ObsWebSocket.InvokeRequest("GetSceneItemIndex", new JObject { ["sceneName"] = sceneName, ["sceneItemId"] = item["sceneItemId"]?.Value<int>() ?? 0 }, 3000);
            if (!result.Ok) return new JObject { ["ok"] = false, ["message"] = result.Message, ["index"] = -1 };
            return new JObject { ["ok"] = true, ["index"] = result.Data?["sceneItemIndex"]?.Value<int>() ?? -1 };
        }

        internal static JObject SetSceneItemIndex(string sceneName, JObject item, int index)
        {
            if (item == null) return new JObject { ["ok"] = true, ["skipped"] = true };
            if (index < 0) index = 0;
            var r = ObsWebSocket.InvokeRequest("SetSceneItemIndex", new JObject { ["sceneName"] = sceneName, ["sceneItemId"] = item["sceneItemId"]?.Value<int>() ?? 0, ["sceneItemIndex"] = index }, 3000);
            return ObsResultAsJson(r);
        }

        // keeps Window Capture positioned directly after Display Capture in scene-item order, so it paints on top when both happen to be enabled during a capture-mode switch.
        internal static JObject SetWindowCaptureSceneOrder(string sceneName, JArray items)
        {
            var window = FindSceneItem(items, "Window Capture");
            if (window == null) return new JObject { ["ok"] = true, ["skipped"] = true };
            var display = FindSceneItem(items, "Display Capture");
            if (display == null) return new JObject { ["ok"] = true, ["skipped"] = true };

            var displayIndex = GetSceneItemIndexValue(sceneName, display);
            if (displayIndex["ok"]?.Value<bool>() != true) return displayIndex;
            var windowIndex = GetSceneItemIndexValue(sceneName, window);
            if (windowIndex["ok"]?.Value<bool>() != true) return windowIndex;

            int targetIndex = Math.Max(0, displayIndex["index"].Value<int>() + 1);
            if (windowIndex["index"].Value<int>() == targetIndex) return new JObject { ["ok"] = true, ["skipped"] = true };
            return SetSceneItemIndex(sceneName, window, targetIndex);
        }

    }
}

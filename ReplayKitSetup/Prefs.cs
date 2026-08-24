using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace ReplayKitSetup
{
    // persisted user preferences for OBS ReplayKit. ported from obs_replaykit/prefs.py. json property names are a cross-language contract with existing users prefs.json files and the powershell helpers runtime settings -- keep them exactly snake_case matching the python field names, never the c# PascalCase property names.
    public sealed class Preferences
    {
        [JsonProperty("recording_preset")] public string RecordingPreset { get; set; } = Prefs.DEFAULT_RECORDING_PRESET;
        [JsonProperty("input_overlay_enabled")] public bool InputOverlayEnabled { get; set; } = Prefs.DEFAULT_INPUT_OVERLAY;
        [JsonProperty("overlay_style")] public string OverlayStyle { get; set; } = Prefs.DEFAULT_OVERLAY_STYLE;
        [JsonProperty("overlay_opacity")] public int OverlayOpacity { get; set; } = Prefs.DEFAULT_OVERLAY_OPACITY;
        [JsonProperty("overlay_scale")] public int OverlayScale { get; set; } = Prefs.DEFAULT_OVERLAY_SCALE;
        [JsonProperty("overlay_hue_shift")] public double OverlayHueShift { get; set; } = Prefs.DEFAULT_OVERLAY_HUE_SHIFT;
        [JsonProperty("overlay_color_multiply")] public string OverlayColorMultiply { get; set; } = Prefs.DEFAULT_OVERLAY_COLOR_MULTIPLY;
        [JsonProperty("overlay_color_add")] public string OverlayColorAdd { get; set; } = Prefs.DEFAULT_OVERLAY_COLOR_ADD;
        [JsonProperty("microphone_device_id")] public string MicrophoneDeviceId { get; set; } = Audio.DEFAULT_DEVICE_ID;
        [JsonProperty("microphone_name")] public string MicrophoneName { get; set; } = Audio.DEFAULT_DEVICE_NAME;
        [JsonProperty("replay_buffer_seconds")] public int ReplayBufferSeconds { get; set; } = Prefs.DEFAULT_REPLAY_BUFFER_SECS;
        [JsonProperty("recording_path")] public string RecordingPath { get; set; } = Prefs.DefaultRecordingPath();
        [JsonProperty("clip_keybind")] public Dictionary<string, object> ClipKeybind { get; set; } = Keybind.DefaultCombo();
        [JsonProperty("recording_keybind")] public Dictionary<string, object> RecordingKeybind { get; set; } = new Dictionary<string, object>();
        [JsonProperty("codec_preference")] public string CodecPreference { get; set; } = Prefs.DEFAULT_CODEC_PREFERENCE;
        [JsonProperty("compression_mode")] public string CompressionMode { get; set; } = Prefs.DEFAULT_COMPRESSION_MODE;
        [JsonProperty("obs_startup_enabled")] public bool ObsStartupEnabled { get; set; } = Prefs.DEFAULT_OBS_STARTUP;
        [JsonProperty("disable_obs_close_warning")] public bool DisableObsCloseWarning { get; set; } = Prefs.DEFAULT_DISABLE_OBS_CLOSE_WARNING;
        [JsonProperty("allow_sleep_while_active")] public bool AllowSleepWhileActive { get; set; } = Prefs.DEFAULT_ALLOW_SLEEP_WHILE_ACTIVE;
        [JsonProperty("pin_obs_tray_icon")] public bool PinObsTrayIcon { get; set; } = Prefs.DEFAULT_PIN_OBS_TRAY_ICON;
        [JsonProperty("clip_notification_enabled")] public bool ClipNotificationEnabled { get; set; } = Prefs.DEFAULT_CLIP_NOTIFICATION;
        [JsonProperty("recording_notification_enabled")] public bool RecordingNotificationEnabled { get; set; } = Prefs.DEFAULT_RECORDING_NOTIFICATION;
        [JsonProperty("trim_precise_default")] public bool TrimPreciseDefault { get; set; } = Prefs.DEFAULT_TRIM_PRECISE;
        [JsonProperty("debug_logging_enabled")] public bool DebugLoggingEnabled { get; set; } = Prefs.DEFAULT_DEBUG_LOGGING_ENABLED;
        [JsonProperty("clip_sound_volume")] public int ClipSoundVolume { get; set; } = Prefs.DEFAULT_CLIP_SOUND_VOLUME;
        [JsonProperty("recording_sound_volume")] public int RecordingSoundVolume { get; set; } = Prefs.DEFAULT_RECORDING_SOUND_VOLUME;
        [JsonProperty("share_mode")] public string ShareMode { get; set; } = Prefs.DEFAULT_SHARE_MODE;
        [JsonProperty("discord_screenshare_enabled")] public bool DiscordScreenshareEnabled { get; set; } = Prefs.DEFAULT_DISCORD_SCREENSHARE_ENABLED;
        [JsonProperty("discord_output_mode")] public string DiscordOutputMode { get; set; } = Prefs.DEFAULT_DISCORD_OUTPUT_MODE;
        [JsonProperty("discord_projector_enabled")] public bool DiscordProjectorEnabled { get; set; } = Prefs.DEFAULT_DISCORD_PROJECTOR_ENABLED;
        [JsonProperty("discord_projector_width")] public int DiscordProjectorWidth { get; set; } = Prefs.DEFAULT_DISCORD_PROJECTOR_WIDTH;
        [JsonProperty("discord_projector_height")] public int DiscordProjectorHeight { get; set; } = Prefs.DEFAULT_DISCORD_PROJECTOR_HEIGHT;
        [JsonProperty("discord_projector_visible_pixels")] public int DiscordProjectorVisiblePixels { get; set; } = Prefs.DEFAULT_DISCORD_PROJECTOR_VISIBLE_PIXELS;
        [JsonProperty("discord_projector_monitor_index")] public int DiscordProjectorMonitorIndex { get; set; } = Prefs.DEFAULT_DISCORD_PROJECTOR_MONITOR_INDEX;
        [JsonProperty("discord_projector_edge")] public string DiscordProjectorEdge { get; set; } = Prefs.DEFAULT_DISCORD_PROJECTOR_EDGE;
        [JsonProperty("discord_projector_title_hint")] public string DiscordProjectorTitleHint { get; set; } = Prefs.DEFAULT_DISCORD_PROJECTOR_TITLE_HINT;
        [JsonProperty("discord_projector_hide_taskbar")] public bool DiscordProjectorHideTaskbar { get; set; } = Prefs.DEFAULT_DISCORD_PROJECTOR_HIDE_TASKBAR;
        [JsonProperty("screenshare_capture_mode")] public string ScreenshareCaptureMode { get; set; } = Prefs.DEFAULT_SCREENSHARE_CAPTURE_MODE;
        [JsonProperty("screenshare_game_window")] public string ScreenshareGameWindow { get; set; } = Prefs.DEFAULT_SCREENSHARE_GAME_WINDOW;
        [JsonProperty("screenshare_game_overrides")] public List<Dictionary<string, string>> ScreenshareGameOverrides { get; set; } = new List<Dictionary<string, string>>();
        [JsonProperty("screenshare_auto_game_keep_focused")] public bool ScreenshareAutoGameKeepFocused { get; set; } = Prefs.DEFAULT_SCREENSHARE_AUTO_GAME_KEEP_FOCUSED;
        [JsonProperty("motion_blur_enabled")] public bool MotionBlurEnabled { get; set; } = Prefs.DEFAULT_MOTION_BLUR;
        [JsonProperty("motion_blur_strength")] public double MotionBlurStrength { get; set; } = Prefs.DEFAULT_MOTION_BLUR_STRENGTH;

        // encoding.utf8 writes a bom by default; python's encoding="utf-8" never does, and anything else that reads this file (the python side during the transition, the powershell helper) expects bom-less utf-8.
        private static readonly System.Text.Encoding Utf8NoBom = new System.Text.UTF8Encoding(false);

        public void Save()
        {
            Directory.CreateDirectory(Prefs.PREFS_DIR);
            File.WriteAllText(Prefs.PREFS_FILE, JsonConvert.SerializeObject(this, Formatting.Indented), Utf8NoBom);
        }
    }

    public static class Prefs
    {
        private static string PrefsDir()
        {
            // an exe launched directly (not via dotnet run/build) always reports its own real directory here -- no frozen-vs-source-mode distinction like pyinstaller needed.
            return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
        }

        public static readonly string PREFS_DIR = PrefsDir();
        public static readonly string PREFS_FILE = Path.Combine(PREFS_DIR, "prefs.json");
        public static readonly string SETUP_CACHE_PREFS_FILE = Path.Combine(Config.REPLAYKIT_SETUP_CACHE, "prefs.json");
        public static readonly string RUNTIME_SETTINGS_FILE = Path.Combine(Config.REPLAYKIT_CONFIG, "scripts", "replaykit_settings.json");

        // defaults match the bundled config so an unconfigured run produces the same install the previous version did.
        public const string DEFAULT_RECORDING_PRESET = "balanced";
        public const bool DEFAULT_INPUT_OVERLAY = true;
        public const string DEFAULT_OVERLAY_STYLE = "input_overlay";
        public static readonly string[] ALLOWED_OVERLAY_STYLES = { "input_overlay", "bongo_cat", "off" };
        public const int DEFAULT_OVERLAY_OPACITY = 100;
        public const int DEFAULT_OVERLAY_SCALE = 100;
        public const double DEFAULT_OVERLAY_HUE_SHIFT = 0.0;
        public const string DEFAULT_OVERLAY_COLOR_MULTIPLY = "#ffffff";
        public const string DEFAULT_OVERLAY_COLOR_ADD = "#000000";
        public const int OVERLAY_OPACITY_MIN = 0;
        public const int OVERLAY_OPACITY_MAX = 100;
        public const int OVERLAY_SCALE_MIN = 50;
        public const int OVERLAY_SCALE_MAX = 200;
        public const double OVERLAY_HUE_SHIFT_MIN = -180.0;
        public const double OVERLAY_HUE_SHIFT_MAX = 180.0;
        public const int DEFAULT_REPLAY_BUFFER_SECS = 90;
        public const int REPLAY_BUFFER_MIN = 5;
        public const int REPLAY_BUFFER_MAX = 1200;
        public const bool DEFAULT_OBS_STARTUP = true;
        public const bool DEFAULT_DISABLE_OBS_CLOSE_WARNING = true;
        public const bool DEFAULT_ALLOW_SLEEP_WHILE_ACTIVE = true;
        public const bool DEFAULT_PIN_OBS_TRAY_ICON = true;
        public const bool DEFAULT_DEBUG_LOGGING_ENABLED = false;
        public const bool DEFAULT_CLIP_NOTIFICATION = true;
        public const bool DEFAULT_RECORDING_NOTIFICATION = true;
        public const bool DEFAULT_TRIM_PRECISE = false;
        public const int DEFAULT_CLIP_SOUND_VOLUME = 100;
        public const int DEFAULT_RECORDING_SOUND_VOLUME = 100;
        public const string DEFAULT_SHARE_MODE = "projector";
        public static readonly string[] ALLOWED_SHARE_MODES = { "projector", "virtual_camera_legacy", "vcam", "screenshare" };
        public const bool DEFAULT_DISCORD_SCREENSHARE_ENABLED = true;
        public const string DEFAULT_DISCORD_OUTPUT_MODE = "projector";
        public static readonly string[] ALLOWED_DISCORD_OUTPUT_MODES = { "projector", "virtual_camera_legacy" };
        public const bool DEFAULT_DISCORD_PROJECTOR_ENABLED = true;
        public const int DEFAULT_DISCORD_PROJECTOR_WIDTH = 0;
        public const int DEFAULT_DISCORD_PROJECTOR_HEIGHT = 0;
        public const int DEFAULT_DISCORD_PROJECTOR_VISIBLE_PIXELS = 1;
        public const int DEFAULT_DISCORD_PROJECTOR_MONITOR_INDEX = 0;
        public const string DEFAULT_DISCORD_PROJECTOR_EDGE = "bottom";
        public static readonly string[] ALLOWED_DISCORD_PROJECTOR_EDGES = { "right", "left", "top", "bottom" };
        public const string DEFAULT_DISCORD_PROJECTOR_TITLE_HINT = "OBS ReplayKit Discord Share";
        public const bool DEFAULT_DISCORD_PROJECTOR_HIDE_TASKBAR = true;
        public const string DEFAULT_SCREENSHARE_CAPTURE_MODE = "hybrid_auto";
        public static readonly string[] ALLOWED_SCREENSHARE_CAPTURE_MODES = { "hybrid_auto", "desktop", "game_auto", "game_window" };
        public const string DEFAULT_SCREENSHARE_GAME_WINDOW = "";
        public const bool DEFAULT_SCREENSHARE_AUTO_GAME_KEEP_FOCUSED = false;
        public const bool DEFAULT_MOTION_BLUR = false;
        public const double DEFAULT_MOTION_BLUR_STRENGTH = 0.075;
        public const double MOTION_BLUR_STRENGTH_MIN = 0.0;
        public const double MOTION_BLUR_STRENGTH_MAX = 1.0;

        // "auto" picks the best hevc/h.264 combo the users gpu can run, or the user can force h.264 for maximum playback support -- av1 is deliberately not offered since most iphones and plenty of android devices have no av1 decoder at all, and unlike a container-tag issue theres no fix for missing silicon.
        public const string DEFAULT_CODEC_PREFERENCE = "auto";
        public static readonly string[] ALLOWED_CODEC_PREFERENCES = { "auto", "h264", "h265" };

        // compression mode trades gpu cost against file size at the same visual quality. balanced is the validated default (~10-11% gpu on a 3060 ti at 1080p60 hevc). lower_gpu uses a faster nvenc preset + no lookahead + no b-frames (bigger files); smaller_files uses a slower preset + multipass + lookahead + more b-frames (more gpu, tighter files).
        public const string DEFAULT_COMPRESSION_MODE = "balanced";
        public static readonly string[] ALLOWED_COMPRESSION_MODES = { "lower_gpu", "balanced", "smaller_files" };

        public static readonly Dictionary<string, string> PRESET_COMPRESSION_DEFAULTS = new Dictionary<string, string>
        {
            ["performance"] = "lower_gpu",
            ["balanced"] = "balanced",
            ["quality"] = "smaller_files",
        };

        public static string DefaultCompressionForPreset(string presetName)
        {
            return PRESET_COMPRESSION_DEFAULTS.TryGetValue(presetName, out var v) ? v : DEFAULT_COMPRESSION_MODE;
        }

        // ~/pictures/videos forward-slashed -- matches the bundled obs profiles advout.recfilepath/ffflepath defaults so the path is consistent across simple/advanced output modes.
        public static string DefaultRecordingPath()
        {
            return Path.Combine(Config.USERPROFILE, "Pictures", "Videos").Replace('\\', '/');
        }

        // newtonsoft deserializes a nested json object inside a Dictionary<string,object> as a JObject wrapper, not a native dictionary -- normalize before doing anything else so callers only ever see native types, same as python json.loads always would.
        private static Dictionary<string, object> AsNativeDict(object value)
        {
            if (value is Newtonsoft.Json.Linq.JObject jobj) return jobj.ToObject<Dictionary<string, object>>();
            return value as Dictionary<string, object>;
        }

        // fall back to the default combo on anything malformed so a corrupted prefs file never blocks startup.
        private static Dictionary<string, object> CoerceKeybind(object value)
        {
            var dict = AsNativeDict(value);
            if (dict != null)
            {
                if (dict.Count == 0) return dict;
                if (dict.TryGetValue("key", out var key) && key is string) return dict;
            }
            return Keybind.DefaultCombo();
        }

        // recording hotkeys default to none; malformed values stay disabled.
        private static Dictionary<string, object> CoerceOptionalKeybind(object value)
        {
            var dict = AsNativeDict(value);
            if (dict != null)
            {
                if (dict.Count == 0) return dict;
                if (dict.TryGetValue("key", out var key) && key is string) return dict;
            }
            return new Dictionary<string, object>();
        }

        private static bool CoerceBool(object value, bool @default)
        {
            return value is bool b ? b : @default;
        }

        private static int CoerceIntRange(object value, int @default, int minimum, int maximum)
        {
            if (!TryToInt(value, out int number)) return @default;
            return Math.Max(minimum, Math.Min(maximum, number));
        }

        private static double CoerceFloatRange(object value, double @default, double minimum, double maximum)
        {
            if (!TryToDouble(value, out double number)) return @default;
            return Math.Max(minimum, Math.Min(maximum, number));
        }

        private static bool TryToInt(object value, out int result)
        {
            result = 0;
            if (value == null) return false;
            try { result = Convert.ToInt32(value); return true; }
            catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is OverflowException) { return false; }
        }

        private static bool TryToDouble(object value, out double result)
        {
            result = 0;
            if (value == null) return false;
            try { result = Convert.ToDouble(value); return true; }
            catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is OverflowException) { return false; }
        }

        private static string CoerceHexColor(object value, string @default)
        {
            string text = (value?.ToString() ?? "").Trim().ToLowerInvariant();
            if (text.Length == 7 && text[0] == '#' && text.Substring(1).All(ch => "0123456789abcdef".IndexOf(ch) >= 0))
            {
                return text;
            }
            return @default;
        }

        private static string CleanOverrideText(object value, int maxLength)
        {
            string text = (value?.ToString() ?? "").Trim();
            if (text.Length == 0 || text.Length > maxLength || text.Any(ch => ch < 32)) return "";
            return text;
        }

        private static List<Dictionary<string, string>> CoerceGameOverrides(object value)
        {
            var result = new List<Dictionary<string, string>>();
            IEnumerable<object> items;
            if (value is Newtonsoft.Json.Linq.JArray jarr) items = jarr.Select(t => (object)t);
            else if (value is IEnumerable<object> native) items = native;
            else return result;

            var seen = new HashSet<string>();
            foreach (var itemObj in items.Take(32))
            {
                var item = AsNativeDict(itemObj);
                if (item == null) continue;
                object tokenSrc = item.TryGetValue("token", out var t) && !(t is null) && t.ToString() != "" ? t
                    : (item.TryGetValue("value", out var v) ? v : null);
                string token = CleanOverrideText(tokenSrc, 512);
                string exeName = CleanOverrideText(item.TryGetValue("exeName", out var e) ? e : null, 96);
                if (token == "" || exeName == "" || !exeName.ToLowerInvariant().EndsWith(".exe")) continue;
                if (!seen.Add(token)) continue;

                string label = CleanOverrideText(item.TryGetValue("label", out var l) ? l : null, 256);
                var entry = new Dictionary<string, string>
                {
                    ["token"] = token,
                    ["value"] = token,
                    ["label"] = label != "" ? label : "[" + exeName + "]",
                    ["exeName"] = exeName,
                };
                string title = CleanOverrideText(item.TryGetValue("title", out var ti) ? ti : null, 160);
                string className = CleanOverrideText(item.TryGetValue("className", out var cn) ? cn : null, 120);
                if (title != "") entry["title"] = title;
                if (className != "") entry["className"] = className;
                result.Add(entry);
            }
            return result;
        }

        // maps the live in-obs custom controls settings json onto Preferences field names.
        private static readonly Dictionary<string, string> RuntimeKeyMap = new Dictionary<string, string>
        {
            ["recordingPreset"] = "recording_preset",
            ["compressionMode"] = "compression_mode",
            ["codecPreference"] = "codec_preference",
            ["replaySeconds"] = "replay_buffer_seconds",
            ["overlayStyle"] = "overlay_style",
            ["overlayOpacity"] = "overlay_opacity",
            ["overlayScale"] = "overlay_scale",
            ["overlayHueShift"] = "overlay_hue_shift",
            ["overlayColorMultiply"] = "overlay_color_multiply",
            ["overlayColorAdd"] = "overlay_color_add",
            ["obsStartupEnabled"] = "obs_startup_enabled",
            ["disableObsCloseWarning"] = "disable_obs_close_warning",
            ["allowSleepWhileActive"] = "allow_sleep_while_active",
            ["pinObsTrayIcon"] = "pin_obs_tray_icon",
            ["clipNotificationEnabled"] = "clip_notification_enabled",
            ["recordingNotificationEnabled"] = "recording_notification_enabled",
            ["trimPreciseDefault"] = "trim_precise_default",
            ["debugLoggingEnabled"] = "debug_logging_enabled",
            ["clipSoundVolume"] = "clip_sound_volume",
            ["recordingSoundVolume"] = "recording_sound_volume",
            ["shareMode"] = "share_mode",
            ["discord_screenshare_enabled"] = "discord_screenshare_enabled",
            ["discordScreenshareEnabled"] = "discord_screenshare_enabled",
            ["discord_output_mode"] = "discord_output_mode",
            ["discord_projector_enabled"] = "discord_projector_enabled",
            ["discord_projector_width"] = "discord_projector_width",
            ["discord_projector_height"] = "discord_projector_height",
            ["discord_projector_visible_pixels"] = "discord_projector_visible_pixels",
            ["discord_projector_monitor_index"] = "discord_projector_monitor_index",
            ["discord_projector_edge"] = "discord_projector_edge",
            ["discord_projector_title_hint"] = "discord_projector_title_hint",
            ["discord_projector_hide_taskbar"] = "discord_projector_hide_taskbar",
            ["screenshareCaptureMode"] = "screenshare_capture_mode",
            ["screenshare_capture_mode"] = "screenshare_capture_mode",
            ["screenshareGameWindow"] = "screenshare_game_window",
            ["screenshare_game_window"] = "screenshare_game_window",
            ["screenshareGameOverrides"] = "screenshare_game_overrides",
            ["screenshare_game_overrides"] = "screenshare_game_overrides",
            ["screenshareAutoGameKeepFocused"] = "screenshare_auto_game_keep_focused",
            ["screenshare_auto_game_keep_focused"] = "screenshare_auto_game_keep_focused",
            ["motionBlurEnabled"] = "motion_blur_enabled",
            ["motionBlurStrength"] = "motion_blur_strength",
        };

        // import settings changed from the in-obs custom controls window.
        private static Dictionary<string, object> RuntimeSettingsOverlay()
        {
            var mapped = new Dictionary<string, object>();
            if (!File.Exists(RUNTIME_SETTINGS_FILE)) return mapped;

            Dictionary<string, object> runtime;
            try
            {
                string text = ReadUtf8SigText(RUNTIME_SETTINGS_FILE);
                runtime = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, object>>(text);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                return mapped;
            }
            if (runtime == null) return mapped;

            foreach (var kv in RuntimeKeyMap)
            {
                if (runtime.TryGetValue(kv.Key, out var value)) mapped[kv.Value] = value;
            }

            if (runtime.TryGetValue("clipDir", out var clipDirObj))
            {
                string clipDir = (clipDirObj?.ToString() ?? "").Trim();
                mapped["recording_path"] = clipDir != "" ? clipDir : DefaultRecordingPath();
            }
            if (runtime.TryGetValue("clipKeybind", out var clipKb) && clipKb is Newtonsoft.Json.Linq.JObject)
            {
                mapped["clip_keybind"] = ((Newtonsoft.Json.Linq.JObject)clipKb).ToObject<Dictionary<string, object>>();
            }
            if (runtime.TryGetValue("recordingKeybind", out var recKb) && recKb is Newtonsoft.Json.Linq.JObject)
            {
                mapped["recording_keybind"] = ((Newtonsoft.Json.Linq.JObject)recKb).ToObject<Dictionary<string, object>>();
            }

            return mapped;
        }

        private static string ReadUtf8SigText(string path)
        {
            // matches pythons "utf-8-sig": strip a leading bom if present, otherwise plain utf-8.
            byte[] bytes = File.ReadAllBytes(path);
            var enc = new System.Text.UTF8Encoding(false);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return enc.GetString(bytes, 3, bytes.Length - 3);
            }
            return enc.GetString(bytes);
        }

        // return saved preferences, or defaults if the file is missing/corrupt.
        public static Preferences LoadPrefs()
        {
            Dictionary<string, object> data = new Dictionary<string, object>();
            string prefsFile = PREFS_FILE;
            if (!File.Exists(prefsFile) && File.Exists(SETUP_CACHE_PREFS_FILE)) prefsFile = SETUP_CACHE_PREFS_FILE;

            if (File.Exists(prefsFile))
            {
                try
                {
                    var loaded = JsonConvert.DeserializeObject<Dictionary<string, object>>(File.ReadAllText(prefsFile, System.Text.Encoding.UTF8));
                    if (loaded != null) data = loaded;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
                {
                }
            }
            foreach (var kv in RuntimeSettingsOverlay()) data[kv.Key] = kv.Value;

            object Get(string key) => data.TryGetValue(key, out var v) ? v : null;
            string GetString(string key, string fallback) => Get(key) is string s ? s : fallback;

            string codecPref = GetString("codec_preference", DEFAULT_CODEC_PREFERENCE);
            if (!ALLOWED_CODEC_PREFERENCES.Contains(codecPref)) codecPref = DEFAULT_CODEC_PREFERENCE;

            string compressionMode = GetString("compression_mode", DEFAULT_COMPRESSION_MODE);
            if (!ALLOWED_COMPRESSION_MODES.Contains(compressionMode)) compressionMode = DEFAULT_COMPRESSION_MODE;

            string overlayStyle = GetString("overlay_style", DEFAULT_OVERLAY_STYLE);
            if (!ALLOWED_OVERLAY_STYLES.Contains(overlayStyle)) overlayStyle = DEFAULT_OVERLAY_STYLE;
            bool inputOverlayEnabled = overlayStyle != "off";

            string shareMode = GetString("share_mode", DEFAULT_SHARE_MODE);
            if (shareMode == "share_bridge") shareMode = DEFAULT_SHARE_MODE;
            if (!ALLOWED_SHARE_MODES.Contains(shareMode)) shareMode = DEFAULT_SHARE_MODE;
            if (shareMode == "vcam" || shareMode == "screenshare") shareMode = DEFAULT_SHARE_MODE;

            string discordOutputMode = GetString("discord_output_mode", DEFAULT_DISCORD_OUTPUT_MODE);
            if (discordOutputMode == "share_bridge") discordOutputMode = DEFAULT_DISCORD_OUTPUT_MODE;
            if (!ALLOWED_DISCORD_OUTPUT_MODES.Contains(discordOutputMode)) discordOutputMode = DEFAULT_DISCORD_OUTPUT_MODE;

            string discordProjectorEdge = GetString("discord_projector_edge", DEFAULT_DISCORD_PROJECTOR_EDGE);
            if (!ALLOWED_DISCORD_PROJECTOR_EDGES.Contains(discordProjectorEdge)) discordProjectorEdge = DEFAULT_DISCORD_PROJECTOR_EDGE;

            string discordProjectorTitleHint = (Get("discord_projector_title_hint")?.ToString() ?? DEFAULT_DISCORD_PROJECTOR_TITLE_HINT).Trim();
            if (discordProjectorTitleHint.Length == 0 || discordProjectorTitleHint.Length > 128 || discordProjectorTitleHint.Any(ch => ch < 32))
            {
                discordProjectorTitleHint = DEFAULT_DISCORD_PROJECTOR_TITLE_HINT;
            }

            string screenshareCaptureMode = GetString("screenshare_capture_mode", DEFAULT_SCREENSHARE_CAPTURE_MODE);
            if (!ALLOWED_SCREENSHARE_CAPTURE_MODES.Contains(screenshareCaptureMode)) screenshareCaptureMode = DEFAULT_SCREENSHARE_CAPTURE_MODE;

            string screenshareGameWindow = (Get("screenshare_game_window")?.ToString() ?? DEFAULT_SCREENSHARE_GAME_WINDOW).Trim();
            if (screenshareGameWindow.Length > 512 || screenshareGameWindow.Any(ch => ch < 32)) screenshareGameWindow = DEFAULT_SCREENSHARE_GAME_WINDOW;
            var screenshareGameOverrides = CoerceGameOverrides(Get("screenshare_game_overrides"));

            return new Preferences
            {
                RecordingPreset = GetString("recording_preset", DEFAULT_RECORDING_PRESET),
                InputOverlayEnabled = inputOverlayEnabled,
                OverlayStyle = overlayStyle,
                OverlayOpacity = CoerceIntRange(Get("overlay_opacity"), DEFAULT_OVERLAY_OPACITY, OVERLAY_OPACITY_MIN, OVERLAY_OPACITY_MAX),
                OverlayScale = CoerceIntRange(Get("overlay_scale"), DEFAULT_OVERLAY_SCALE, OVERLAY_SCALE_MIN, OVERLAY_SCALE_MAX),
                OverlayHueShift = CoerceFloatRange(Get("overlay_hue_shift"), DEFAULT_OVERLAY_HUE_SHIFT, OVERLAY_HUE_SHIFT_MIN, OVERLAY_HUE_SHIFT_MAX),
                OverlayColorMultiply = CoerceHexColor(Get("overlay_color_multiply"), DEFAULT_OVERLAY_COLOR_MULTIPLY),
                OverlayColorAdd = CoerceHexColor(Get("overlay_color_add"), DEFAULT_OVERLAY_COLOR_ADD),
                MicrophoneDeviceId = GetString("microphone_device_id", Audio.DEFAULT_DEVICE_ID),
                MicrophoneName = GetString("microphone_name", Audio.DEFAULT_DEVICE_NAME),
                ReplayBufferSeconds = TryToInt(Get("replay_buffer_seconds"), out int rbs) ? rbs : DEFAULT_REPLAY_BUFFER_SECS,
                RecordingPath = GetString("recording_path", DefaultRecordingPath()),
                ClipKeybind = CoerceKeybind(Get("clip_keybind")),
                RecordingKeybind = CoerceOptionalKeybind(Get("recording_keybind")),
                CodecPreference = codecPref,
                CompressionMode = compressionMode,
                ObsStartupEnabled = CoerceBool(Get("obs_startup_enabled"), DEFAULT_OBS_STARTUP),
                DisableObsCloseWarning = CoerceBool(Get("disable_obs_close_warning"), DEFAULT_DISABLE_OBS_CLOSE_WARNING),
                AllowSleepWhileActive = CoerceBool(Get("allow_sleep_while_active"), DEFAULT_ALLOW_SLEEP_WHILE_ACTIVE),
                PinObsTrayIcon = CoerceBool(Get("pin_obs_tray_icon"), DEFAULT_PIN_OBS_TRAY_ICON),
                ClipNotificationEnabled = CoerceBool(Get("clip_notification_enabled"), DEFAULT_CLIP_NOTIFICATION),
                RecordingNotificationEnabled = CoerceBool(Get("recording_notification_enabled"), DEFAULT_RECORDING_NOTIFICATION),
                TrimPreciseDefault = CoerceBool(Get("trim_precise_default"), DEFAULT_TRIM_PRECISE),
                DebugLoggingEnabled = CoerceBool(Get("debug_logging_enabled"), DEFAULT_DEBUG_LOGGING_ENABLED),
                ClipSoundVolume = CoerceIntRange(Get("clip_sound_volume"), DEFAULT_CLIP_SOUND_VOLUME, 0, 100),
                RecordingSoundVolume = CoerceIntRange(Get("recording_sound_volume"), DEFAULT_RECORDING_SOUND_VOLUME, 0, 100),
                ShareMode = shareMode,
                DiscordScreenshareEnabled = CoerceBool(Get("discord_screenshare_enabled"), DEFAULT_DISCORD_SCREENSHARE_ENABLED),
                DiscordOutputMode = discordOutputMode,
                DiscordProjectorEnabled = CoerceBool(Get("discord_projector_enabled"), DEFAULT_DISCORD_PROJECTOR_ENABLED),
                DiscordProjectorWidth = CoerceIntRange(Get("discord_projector_width"), DEFAULT_DISCORD_PROJECTOR_WIDTH, 0, 7680),
                DiscordProjectorHeight = CoerceIntRange(Get("discord_projector_height"), DEFAULT_DISCORD_PROJECTOR_HEIGHT, 0, 4320),
                DiscordProjectorVisiblePixels = CoerceIntRange(Get("discord_projector_visible_pixels"), DEFAULT_DISCORD_PROJECTOR_VISIBLE_PIXELS, 0, 200),
                DiscordProjectorMonitorIndex = CoerceIntRange(Get("discord_projector_monitor_index"), DEFAULT_DISCORD_PROJECTOR_MONITOR_INDEX, 0, 64),
                DiscordProjectorEdge = discordProjectorEdge,
                DiscordProjectorTitleHint = discordProjectorTitleHint,
                DiscordProjectorHideTaskbar = CoerceBool(Get("discord_projector_hide_taskbar"), DEFAULT_DISCORD_PROJECTOR_HIDE_TASKBAR),
                ScreenshareCaptureMode = screenshareCaptureMode,
                ScreenshareGameWindow = screenshareGameWindow,
                ScreenshareGameOverrides = screenshareGameOverrides,
                ScreenshareAutoGameKeepFocused = CoerceBool(Get("screenshare_auto_game_keep_focused"), DEFAULT_SCREENSHARE_AUTO_GAME_KEEP_FOCUSED),
                MotionBlurEnabled = CoerceBool(Get("motion_blur_enabled"), DEFAULT_MOTION_BLUR),
                MotionBlurStrength = CoerceFloatRange(Get("motion_blur_strength"), DEFAULT_MOTION_BLUR_STRENGTH, MOTION_BLUR_STRENGTH_MIN, MOTION_BLUR_STRENGTH_MAX),
            };
        }
    }
}

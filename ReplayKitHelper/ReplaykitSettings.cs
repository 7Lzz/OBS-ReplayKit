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

namespace ReplayKitHelper
{
    // the replaykit_settings.json schema (defaults, per-field validation/coercion, normalize, read, write, the auto-game-list window-token codec) plus the live-apply-into-running-obs half: scene-item CRUD and json-file scene mutation over obs-websocket, overlay/motion-blur/opacity-filter geometry, screenshare/discord/audio-mixer live-apply, hotkey capture, and the http-facing settings payload/save-request handlers. ported from obs_replaykit helper modules/62_replaykit_settings.ps1.
    internal static class ReplaykitSettings
    {
        public static string GetScriptsDir()
        {
            string dir = (AppConfig.GetScriptDir() ?? "").TrimEnd('\\', '/');
            return Directory.GetParent(dir).FullName;
        }

        public static string GetSettingsPath() => Path.Combine(GetScriptsDir(), "replaykit_settings.json");

        public static JObject GetDefaultSettings()
        {
            return new JObject
            {
                ["recordingPreset"] = "balanced",
                ["compressionMode"] = "balanced",
                ["codecPreference"] = "auto",
                ["replaySeconds"] = 90,
                ["fps"] = 60,
                ["fpsNumerator"] = 60,
                ["fpsDenominator"] = 1,
                // recording output resolution: "native" = encode at the canvas (monitor) size, no downscale; "downscale" = scale the output down to downscaleHeight using downscaleFilter. default is canvas-aware -- native at/below 1080p, downscale-to-1080 above. the performance preset always caps at 720 regardless.
                ["recordingScaleMode"] = DefaultRecordingScaleMode(),
                ["downscaleHeight"] = 1080,
                ["downscaleFilter"] = "lanczos",
                ["clipDir"] = "",
                ["clipKeybind"] = new JObject { ["shift"] = true, ["key"] = "OBS_KEY_BACKSLASH" },
                ["recordingKeybind"] = new JObject(),
                ["openClipsKeybind"] = new JObject(),
                ["overlayStyle"] = "off",
                ["overlayOpacity"] = 100,
                ["overlayScale"] = 100,
                ["overlayFlipH"] = false,
                ["overlayHueShift"] = 0,
                ["overlayColorMultiply"] = "#ffffff",
                ["overlayColorAdd"] = "#000000",
                ["obsStartupEnabled"] = true,
                ["runObsAsAdmin"] = false,
                ["disableObsCloseWarning"] = true,
                // helper keeps obs's .sentinel\run_* swept while it runs so a crash / power loss cant leave the "did not properly shut down" prompt behind for the next launch. off = hand crash detection back to obs.
                ["disableObsCrashPopup"] = true,
                // when on, the tray plugin turns the OBS window's X into "minimize to tray" -- real quits still go through the tray Exit / restart routes.
                ["closeToTray"] = true,
                ["allowSleepWhileActive"] = true,
                ["pinObsTrayIcon"] = true,
                ["clipNotificationEnabled"] = true,
                ["recordingNotificationEnabled"] = true,
                ["clipNotificationSeconds"] = 90,
                ["trimPreciseDefault"] = false,
                ["showCodecTags"] = false,
                ["debugLoggingEnabled"] = false,
                ["autoDeleteLogsOnLaunch"] = true,
                ["autoUpdateEnabled"] = true,
                ["lastUpdatePromptVersion"] = "",
                ["clipSoundVolume"] = 25,
                ["recordingSoundVolume"] = 25,
                ["motionBlurEnabled"] = false,
                ["motionBlurStrength"] = 0.075,
                // legacy shareMode is kept only so older settings files dont break parsing; discord output uses the obs windowed projector directly.
                ["shareMode"] = "projector",
                ["discord_screenshare_enabled"] = true,
                ["discord_output_mode"] = "projector",
                ["discord_projector_enabled"] = true,
                ["discord_projector_width"] = 0,
                ["discord_projector_height"] = 0,
                ["discord_projector_visible_pixels"] = 1,
                ["discord_projector_monitor_index"] = 0,
                ["discord_projector_edge"] = "bottom",
                ["discord_projector_title_hint"] = "OBS ReplayKit Discord Share",
                ["discord_projector_hide_taskbar"] = true,
                ["screenshareCaptureMode"] = "hybrid_auto",
                ["screenshareGameWindow"] = "",
                ["screenshareGameOverrides"] = new JArray(),
                ["screenshareAutoGameKeepFocused"] = false,
                ["screenshareSwitchDelaySeconds"] = 1.0,
                // "default" = leave obs/replaykit icons alone; "custom" = use appIconCustomPath; anything else = a filename in the bundled icons/ folder.
                ["appIcon"] = "default",
                ["appIconCustomPath"] = "",
                // red recording dot on a custom app icon while recording / replay buffer is active (mirrors obs's own tray indicator).
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
            };
        }

        private static bool GetBoolSetting(JObject data, string key, bool defaultValue)
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

        private static int GetIntSetting(JObject data, string key, int defaultValue, int min, int max)
        {
            var token = data[key];
            if (token == null) return defaultValue;
            if (!int.TryParse(token.ToString(), out int n)) throw new InvalidOperationException("Invalid number setting: " + key);
            if (n < min || n > max) throw new InvalidOperationException(key + " must be between " + min + " and " + max + ".");
            return n;
        }

        private static double GetFloatSetting(JObject data, string key, double defaultValue, double min, double max)
        {
            var token = data[key];
            if (token == null) return defaultValue;
            if (!double.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
                throw new InvalidOperationException("Invalid number setting: " + key);
            if (n < min || n > max) throw new InvalidOperationException(key + " must be between " + min + " and " + max + ".");
            return n;
        }

        private static string GetHexColorSetting(JObject data, string key, string defaultValue)
        {
            var token = data[key];
            if (token == null) return defaultValue;
            string v = token.ToString().Trim().ToLowerInvariant();
            if (Regex.IsMatch(v, "^#[0-9a-f]{6}$")) return v;
            throw new InvalidOperationException("Invalid color setting: " + key);
        }

        private static string GetEnumSetting(JObject data, string key, string defaultValue, string[] allowed)
        {
            var token = data[key];
            if (token == null) return defaultValue;
            string v = token.ToString().Trim();
            if (allowed.Contains(v)) return v;
            throw new InvalidOperationException("Invalid option for " + key + ": " + v);
        }

        private static string GetTextSetting(JObject data, string key, string defaultValue, int maxLength)
        {
            var token = data[key];
            if (token == null) return defaultValue;
            string v = token.ToString().Trim();
            if (string.IsNullOrWhiteSpace(v)) return defaultValue;
            if (v.Length > maxLength || Regex.IsMatch(v, @"[\x00-\x1F]")) throw new InvalidOperationException("Invalid text setting: " + key);
            return v;
        }

        private static string GetVersionMarkerSetting(JObject data, string key, string defaultValue)
        {
            var token = data[key];
            if (token == null) return defaultValue;
            string v = token.ToString().Trim();
            if (string.IsNullOrWhiteSpace(v)) return "";
            if (v.Length > 32 || !Regex.IsMatch(v, @"^\d+(?:\.\d+){0,3}$")) throw new InvalidOperationException("Invalid version setting: " + key);
            return v;
        }

        // drops a single pair of wrapping quotes -- explorer's "copy as path" gives "C:\...\file", and users paste that verbatim.
        public static string StripWrappingQuotes(string value)
        {
            if (value == null) return "";
            value = value.Trim();
            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
                value = value.Substring(1, value.Length - 2).Trim();
            return value;
        }

        private static string ResolveClipDirSetting(string value)
        {
            value = StripWrappingQuotes(value);
            if (string.IsNullOrWhiteSpace(value)) return "";
            if (value.Length > 4096) throw new InvalidOperationException("Clip folder path is too long.");
            string expanded = Environment.ExpandEnvironmentVariables(value);
            if (!Path.IsPathRooted(expanded)) throw new InvalidOperationException("Clip folder must be an absolute path.");
            return Path.GetFullPath(expanded);
        }

        private static JObject NormalizeHotkeyCombo(JToken value, JObject defaultValue, string settingName)
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

        private static JObject NormalizeClipKeybind(JToken value) =>
            NormalizeHotkeyCombo(value, new JObject { ["shift"] = true, ["key"] = "OBS_KEY_BACKSLASH" }, "clip keybind");

        private static JObject NormalizeRecordingKeybind(JToken value) =>
            NormalizeHotkeyCombo(value, new JObject(), "recording keybind");

        private static JObject NormalizeOpenClipsKeybind(JToken value) =>
            NormalizeHotkeyCombo(value, new JObject(), "open clips keybind");

        private static JArray NormalizeScreenshareGameOverrides(JToken value)
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

        public static JObject Normalize(JObject raw)
        {
            var defaults = GetDefaultSettings();
            var data = (JObject)defaults.DeepClone();
            if (raw != null) foreach (var prop in raw.Properties()) data[prop.Name] = prop.Value;

            string preset = GetEnumSetting(data, "recordingPreset", defaults["recordingPreset"].Value<string>(), new[] { "performance", "balanced", "quality" });
            string compressionDefault = preset == "performance" ? "lower_gpu" : preset == "quality" ? "smaller_files" : "balanced";
            string compression = GetEnumSetting(data, "compressionMode", compressionDefault, new[] { "lower_gpu", "balanced", "smaller_files" });

            string clipDir = ResolveClipDirSetting(data["clipDir"]?.ToString());
            int replaySeconds = GetIntSetting(data, "replaySeconds", defaults["replaySeconds"].Value<int>(), 5, 1200);

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

            // av1 stays in the allowed list so existing settings.json values dont throw on load, but its no longer offered -- most iphones and plenty of android devices have no av1 decoder at all, and unlike the hevc hev1/hvc1 tag that can be fixed with a remux, theres no fix for missing silicon.
            string codecPreference = GetEnumSetting(data, "codecPreference", defaults["codecPreference"].Value<string>(), new[] { "auto", "h264", "h265", "av1" });
            if (codecPreference == "av1")
            {
                Log.Write("Recording codec 'av1' is no longer offered (playback compatibility); falling back to auto.");
                codecPreference = "auto";
            }

            return new JObject
            {
                ["recordingPreset"] = preset,
                ["compressionMode"] = compression,
                ["codecPreference"] = codecPreference,
                ["replaySeconds"] = replaySeconds,
                // Older builds allowed a single FPS value above the current 240 cap; clamp that legacy value during migration so it cannot block the settings page from loading.
                ["fps"] = Math.Min(240, GetIntSetting(data, "fps", defaults["fps"].Value<int>(), 1, 1000)),
                ["fpsNumerator"] = Math.Min(240, GetIntSetting(data, "fpsNumerator", GetIntSetting(data, "fps", defaults["fps"].Value<int>(), 1, 1000), 1, 1000)),
                ["fpsDenominator"] = GetIntSetting(data, "fpsDenominator", defaults["fpsDenominator"].Value<int>(), 1, 1000),
                ["recordingScaleMode"] = GetEnumSetting(data, "recordingScaleMode", defaults["recordingScaleMode"].Value<string>(), new[] { "native", "downscale" }),
                ["downscaleHeight"] = GetIntSetting(data, "downscaleHeight", defaults["downscaleHeight"].Value<int>(), 240, 4320),
                ["downscaleFilter"] = GetEnumSetting(data, "downscaleFilter", defaults["downscaleFilter"].Value<string>(), new[] { "bilinear", "area", "bicubic", "lanczos" }),
                ["clipDir"] = clipDir,
                ["clipKeybind"] = NormalizeClipKeybind(data["clipKeybind"]),
                ["recordingKeybind"] = NormalizeRecordingKeybind(data["recordingKeybind"]),
                ["openClipsKeybind"] = NormalizeOpenClipsKeybind(data["openClipsKeybind"]),
                ["overlayStyle"] = GetEnumSetting(data, "overlayStyle", defaults["overlayStyle"].Value<string>(), new[] { "input_overlay", "bongo_cat", "off" }),
                ["overlayOpacity"] = GetIntSetting(data, "overlayOpacity", defaults["overlayOpacity"].Value<int>(), 0, 100),
                ["overlayScale"] = GetIntSetting(data, "overlayScale", defaults["overlayScale"].Value<int>(), 50, 200),
                ["overlayFlipH"] = GetBoolSetting(data, "overlayFlipH", defaults["overlayFlipH"].Value<bool>()),
                ["overlayHueShift"] = GetFloatSetting(data, "overlayHueShift", defaults["overlayHueShift"].Value<double>(), -180.0, 180.0),
                ["overlayColorMultiply"] = GetHexColorSetting(data, "overlayColorMultiply", defaults["overlayColorMultiply"].Value<string>()),
                ["overlayColorAdd"] = GetHexColorSetting(data, "overlayColorAdd", defaults["overlayColorAdd"].Value<string>()),
                ["obsStartupEnabled"] = GetBoolSetting(data, "obsStartupEnabled", defaults["obsStartupEnabled"].Value<bool>()),
                ["runObsAsAdmin"] = GetBoolSetting(data, "runObsAsAdmin", defaults["runObsAsAdmin"].Value<bool>()),
                ["disableObsCloseWarning"] = GetBoolSetting(data, "disableObsCloseWarning", defaults["disableObsCloseWarning"].Value<bool>()),
                ["disableObsCrashPopup"] = GetBoolSetting(data, "disableObsCrashPopup", defaults["disableObsCrashPopup"].Value<bool>()),
                ["closeToTray"] = GetBoolSetting(data, "closeToTray", defaults["closeToTray"].Value<bool>()),
                ["allowSleepWhileActive"] = GetBoolSetting(data, "allowSleepWhileActive", defaults["allowSleepWhileActive"].Value<bool>()),
                ["pinObsTrayIcon"] = GetBoolSetting(data, "pinObsTrayIcon", defaults["pinObsTrayIcon"].Value<bool>()),
                ["clipNotificationEnabled"] = GetBoolSetting(data, "clipNotificationEnabled", defaults["clipNotificationEnabled"].Value<bool>()),
                ["recordingNotificationEnabled"] = GetBoolSetting(data, "recordingNotificationEnabled", defaults["recordingNotificationEnabled"].Value<bool>()),
                ["clipNotificationSeconds"] = GetIntSetting(data, "clipNotificationSeconds", replaySeconds, 1, 1200),
                ["trimPreciseDefault"] = GetBoolSetting(data, "trimPreciseDefault", defaults["trimPreciseDefault"].Value<bool>()),
                ["showCodecTags"] = GetBoolSetting(data, "showCodecTags", defaults["showCodecTags"].Value<bool>()),
                ["debugLoggingEnabled"] = GetBoolSetting(data, "debugLoggingEnabled", defaults["debugLoggingEnabled"].Value<bool>()),
                ["autoDeleteLogsOnLaunch"] = GetBoolSetting(data, "autoDeleteLogsOnLaunch", defaults["autoDeleteLogsOnLaunch"].Value<bool>()),
                ["autoUpdateEnabled"] = GetBoolSetting(data, "autoUpdateEnabled", defaults["autoUpdateEnabled"].Value<bool>()),
                ["lastUpdatePromptVersion"] = GetVersionMarkerSetting(data, "lastUpdatePromptVersion", defaults["lastUpdatePromptVersion"].Value<string>()),
                ["clipSoundVolume"] = GetIntSetting(data, "clipSoundVolume", defaults["clipSoundVolume"].Value<int>(), 0, 100),
                ["recordingSoundVolume"] = GetIntSetting(data, "recordingSoundVolume", defaults["recordingSoundVolume"].Value<int>(), 0, 100),
                ["motionBlurEnabled"] = GetBoolSetting(data, "motionBlurEnabled", defaults["motionBlurEnabled"].Value<bool>()),
                ["motionBlurStrength"] = GetFloatSetting(data, "motionBlurStrength", defaults["motionBlurStrength"].Value<double>(), 0.0, 1.0),
                ["shareMode"] = shareMode,
                ["discord_screenshare_enabled"] = GetBoolSetting(data, "discord_screenshare_enabled", defaults["discord_screenshare_enabled"].Value<bool>()),
                ["discord_output_mode"] = discordOutputMode,
                ["discord_projector_enabled"] = GetBoolSetting(data, "discord_projector_enabled", defaults["discord_projector_enabled"].Value<bool>()),
                ["discord_projector_width"] = GetIntSetting(data, "discord_projector_width", defaults["discord_projector_width"].Value<int>(), 0, 7680),
                ["discord_projector_height"] = GetIntSetting(data, "discord_projector_height", defaults["discord_projector_height"].Value<int>(), 0, 4320),
                ["discord_projector_visible_pixels"] = GetIntSetting(data, "discord_projector_visible_pixels", defaults["discord_projector_visible_pixels"].Value<int>(), 0, 32),
                ["discord_projector_monitor_index"] = GetIntSetting(data, "discord_projector_monitor_index", defaults["discord_projector_monitor_index"].Value<int>(), 0, 64),
                ["discord_projector_edge"] = GetEnumSetting(data, "discord_projector_edge", defaults["discord_projector_edge"].Value<string>(), new[] { "right", "left", "top", "bottom" }),
                ["discord_projector_title_hint"] = GetTextSetting(data, "discord_projector_title_hint", defaults["discord_projector_title_hint"].Value<string>(), 128),
                ["discord_projector_hide_taskbar"] = true,
                ["screenshareCaptureMode"] = GetEnumSetting(data, "screenshareCaptureMode", defaults["screenshareCaptureMode"].Value<string>(), new[] { "hybrid_auto", "desktop", "game_auto", "game_window" }),
                ["screenshareGameWindow"] = GetTextSetting(data, "screenshareGameWindow", defaults["screenshareGameWindow"].Value<string>(), 512),
                ["screenshareGameOverrides"] = NormalizeScreenshareGameOverrides(data["screenshareGameOverrides"]),
                ["screenshareAutoGameKeepFocused"] = GetBoolSetting(data, "screenshareAutoGameKeepFocused", defaults["screenshareAutoGameKeepFocused"].Value<bool>()),
                ["screenshareSwitchDelaySeconds"] = GetFloatSetting(data, "screenshareSwitchDelaySeconds", defaults["screenshareSwitchDelaySeconds"].Value<double>(), 0.05, 5.0),
                ["appIcon"] = NormalizeAppIcon(data["appIcon"]?.ToString(), StripWrappingQuotes(data["appIconCustomPath"]?.ToString())),
                ["appIconCustomPath"] = StripWrappingQuotes(GetTextSetting(data, "appIconCustomPath", defaults["appIconCustomPath"].Value<string>(), 1024)),
                ["appIconRecordingDot"] = GetBoolSetting(data, "appIconRecordingDot", defaults["appIconRecordingDot"].Value<bool>()),
                ["theme"] = NormalizeTheme(data["theme"]?.ToString()),
                ["themeCustom"] = NormalizeThemeCustom(data["themeCustom"], (JObject)defaults["themeCustom"]),
            };
        }

        // "default" / "custom" / a bundled preset id / "user/<name>" (an existing saved theme). unknown -> "default".
        private static string NormalizeTheme(string value)
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

        private static JObject NormalizeThemeCustom(JToken raw, JObject defaults)
        {
            var src = raw as JObject ?? new JObject();
            var outObj = new JObject();
            foreach (var k in new[] { "bg", "panel", "field", "text", "accent", "border", "danger" })
                outObj[k] = Themes.CleanHex(src[k]?.ToString(), defaults[k].ToString());
            outObj["gradient"] = Themes.CleanOptionalHex(src["gradient"]?.ToString());
            // gradients is the current per-token shape; the flat gradient* keys are still carried through so a
            // settings file written before per-token gradients still resolves its background gradient (Themes.ApplyGradient reads both).
            outObj["gradients"] = src["gradients"] is JObject gradients ? gradients.DeepClone() : new JObject();
            foreach (var legacy in new[] { "gradientType", "gradientAngle", "gradientCenterX", "gradientCenterY", "gradientStops" })
            {
                if (src[legacy] != null) outObj[legacy] = src[legacy].DeepClone();
            }
            outObj["dark"] = src["dark"]?.Type == JTokenType.Boolean ? src["dark"].Value<bool>() : defaults["dark"].Value<bool>();
            return Themes.FromCustom(outObj).ToSeedJson();
        }

        private static readonly string[] AppIconExts = { ".ico", ".png", ".jpg", ".jpeg", ".bmp" };

        // a "user/<name>.ico" id -> absolute path in USER_ICONS_DIR, or null if the id is malformed. Path.GetFileName strips any traversal.
        private static string UserIconIdToPath(string id)
        {
            if (string.IsNullOrEmpty(id) || !id.StartsWith("user/", StringComparison.Ordinal)) return null;
            string name = Path.GetFileName(id.Substring(5));
            if (string.IsNullOrEmpty(name) || !name.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)) return null;
            return Path.Combine(Constants.USER_ICONS_DIR, name);
        }

        // "default" / "custom" / "user/<name>.ico" (a saved custom pick) / a bare filename in the bundled icons/ folder. anything unrecognised falls back to "default" so a stale settings file never leaves obs iconless.
        private static string NormalizeAppIcon(string value, string customPath)
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
            if (safe != value || !AppIconExts.Contains(Path.GetExtension(safe))) return "default";
            try { return File.Exists(Path.Combine(Constants.APP_ICONS_DIR, safe)) ? safe : "default"; }
            catch { return "default"; }
        }

        private static bool TestUsableIconFile(string path)
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

        private static string SanitizeIconBaseName(string s)
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
            var settings = Normalize(ReadSettings());
            settings["appIcon"] = id;
            settings = Normalize(settings);
            WriteSettings(settings);
            return ApplyAppIconLive(settings);
        }

        // set theme to id, persist, write the obs .ovt + user.ini. returns true (obs restart still needed to pick it up).
        // used by /appearance/delete-theme when the removed theme was the active one.
        public static bool SetThemeAndApply(string id)
        {
            var settings = Normalize(ReadSettings());
            settings["theme"] = id;
            settings = Normalize(settings);
            WriteSettings(settings);
            try { Themes.ApplyToObs(settings); } catch (Exception ex) { Log.Write("SetThemeAndApply: " + ex.Message); }
            return true;
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
                return Normalize(JObject.Parse(json));
            }
            catch (Exception ex)
            {
                Log.Write("Read-ReplayKitSettings failed: " + ex.Message);
                throw new InvalidOperationException("ReplayKit settings file is invalid: " + ex.Message, ex);
            }
        }

        public static void WriteSettings(JObject settings)
        {
            string path = GetSettingsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            AppConfig.WriteUtf8(path, settings.ToString(Formatting.Indented));
        }

        // codec for the Auto Game List: a saved games (title, window class, exe name) triple gets packed into one opaque string token (# and : escaped) so it can live as a single string in the settings json array. ported from the same file, lines ~2535-2610.
        private static string ConvertToObsWindowTokenPart(string value) => (value ?? "").Replace("#", "#22").Replace(":", "#3A");
        private static string ConvertFromObsWindowTokenPart(string value) => (value ?? "").Replace("#3A", ":").Replace("#22", "#");

        private static string NewObsWindowToken(string title, string className, string exeName) =>
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

        private static readonly string[] BlockedGameWindowExes =
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

        // -- live-apply: everything below mutates a *running* obs instance or the live scene collection file,
        // as opposed to the schema/defaults/normalize code above. ported from 62_replaykit_settings.ps1 lines
        // 1152-5245. the ps original threads a generic JavaScriptSerializer dictionary/arraylist shim thru this
        // whole section (New/Set/Get/Copy-ReplayKitJsonValue) so the same code can walk either a PSCustomObject
        // or a Dictionary -- with JObject/JArray already available end to end here, that shim has no purpose and
        // is not ported; every call site below just uses JObject/JArray directly.

        private static JObject ObsResultAsJson(ObsWebSocketResult r) => r.Ok ? new JObject { ["ok"] = true } : new JObject { ["ok"] = false, ["message"] = r.Message };

        private static double ScaledRbSizeMb(string presetName, int replaySeconds)
        {
            double peakMbps = presetName == "performance" ? 8 : presetName == "quality" ? 32 : 20;
            double mbPerSecond = peakMbps * 1.5 / 8;
            return Math.Max(32, Math.Ceiling(mbPerSecond * replaySeconds));
        }

        private static JObject StopObsOutputIfActive(string statusRequest, string stopRequest, string label)
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

        private static JObject WaitObsOutputState(string statusRequest, bool desiredActive, string label, int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(1000, timeoutMs));
            do
            {
                var status = ObsWebSocket.InvokeRequest(statusRequest, null, 3000);
                if (status.Ok && (status.Data?["outputActive"]?.Value<bool>() ?? false) == desiredActive) return new JObject { ["ok"] = true };
                Thread.Sleep(250);
            } while (DateTime.UtcNow < deadline);

            string want = desiredActive ? "start" : "stop";
            return new JObject { ["ok"] = false, ["warning"] = "Timed out waiting for " + label + " to " + want + "." };
        }

        private static JObject StartObsOutputIfNeeded(JObject state, string startRequest, string label)
        {
            if (state["wasActive"]?.Value<bool>() != true) return new JObject { ["ok"] = true };
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

        private static ObsWebSocketResult SetVideoSettingsLive(JObject preset) => ObsWebSocket.InvokeRequest("SetVideoSettings", preset["video"], 5000);

        private static void SetFractionalFpsProfile(JObject preset, List<string> warnings)
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
                if (!result.Ok) warnings.Add("OBS did not accept Video." + update[0] + ": " + result.Message);
            }
        }

        private static ObsWebSocketResult SetReplayBufferOutputLive(JObject settings, JObject preset)
        {
            JObject outputSettings = new JObject();
            var existing = ObsWebSocket.InvokeRequest("GetOutputSettings", new JObject { ["outputName"] = "Replay Buffer" }, 3000);
            if (existing.Ok && existing.Data?["outputSettings"] is JObject existingSettings) outputSettings = (JObject)existingSettings.DeepClone();

            string clipDir = settings["clipDir"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(clipDir)) clipDir = AppConfig.GetDefaultClipDir();
            outputSettings["max_time_sec"] = settings["replaySeconds"]?.Value<int>() ?? 0;
            outputSettings["max_size_mb"] = ScaledRbSizeMb(settings["recordingPreset"]?.Value<string>(), settings["replaySeconds"]?.Value<int>() ?? 0);
            outputSettings["directory"] = clipDir;
            outputSettings["path"] = clipDir;

            return ObsWebSocket.InvokeRequest("SetOutputSettings", new JObject { ["outputName"] = "Replay Buffer", ["outputSettings"] = outputSettings }, 5000);
        }

        // resolves a bundled input-overlay preset path; falls back to a recursive filename search under the
        // presets root for older settings that stored just "<folder>/<file>" without the full relative path.
        private static string FindOverlayAsset(string relativePath)
        {
            string root = Path.Combine(Directory.GetParent(GetScriptsDir().TrimEnd('\\', '/')).FullName, "input-overlay-presets");
            if (!Directory.Exists(root)) return "";
            string candidate = Path.Combine(root, relativePath);
            if (File.Exists(candidate)) return candidate;

            var parts = relativePath.Split('\\', '/');
            if (parts.Length < 2) return "";
            string folder = parts[parts.Length - 2];
            string leaf = parts[parts.Length - 1];
            string suffix = "\\" + folder + "\\" + leaf;
            foreach (var file in Directory.EnumerateFiles(root, leaf, SearchOption.AllDirectories))
            {
                if (file.Replace('/', '\\').EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return file;
            }
            return "";
        }

        private static JObject GetSceneName()
        {
            var scenes = ObsWebSocket.InvokeRequest("GetSceneList", null, 3000);
            if (!scenes.Ok) return new JObject { ["ok"] = false, ["message"] = scenes.Message };
            string current = scenes.Data?["currentProgramSceneName"]?.Value<string>();
            if (!string.IsNullOrEmpty(current)) return new JObject { ["ok"] = true, ["name"] = current };
            if (scenes.Data?["scenes"] is JArray list && list.Count > 0) return new JObject { ["ok"] = true, ["name"] = list[0]["sceneName"]?.Value<string>() };
            return new JObject { ["ok"] = false, ["message"] = "No OBS scene is available." };
        }

        private static JObject GetSceneItems(string sceneName)
        {
            var r = ObsWebSocket.InvokeRequest("GetSceneItemList", new JObject { ["sceneName"] = sceneName }, 3000);
            if (!r.Ok) return new JObject { ["ok"] = false, ["message"] = r.Message, ["items"] = new JArray() };
            return new JObject { ["ok"] = true, ["items"] = r.Data?["sceneItems"] as JArray ?? new JArray() };
        }

        private static JObject FindSceneItem(JArray items, string sourceName)
        {
            if (items == null) return null;
            foreach (var item in items)
            {
                if (item is JObject obj && obj["sourceName"]?.Value<string>() == sourceName) return obj;
            }
            return null;
        }

        private static JObject SetSceneItemEnabled(string sceneName, JObject item, bool enabled)
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

        // obs-websocket only accepts a specific subset of transform fields in SetSceneItemTransform (a full
        // GetSceneItemTransform readback includes derived/readonly fields like sourceWidth that the setter
        // rejects) -- this is the allow-list.
        private static readonly string[] SettableTransformKeys = { "positionX", "positionY", "scaleX", "scaleY", "rotation", "alignment", "boundsType", "boundsAlignment", "cropToBounds", "cropLeft", "cropRight", "cropTop", "cropBottom" };

        private static JObject GetSettableSceneItemTransform(JObject transform)
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

        private static JObject SetSceneItemTransform(string sceneName, JObject item, JObject transform)
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

        private static JObject GetSceneItemTransformLive(string sceneName, JObject item)
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

        private static double GetDoubleValue(JToken obj, string key, double def)
        {
            var value = obj?[key];
            if (value == null || value.Type == JTokenType.Null) return def;
            try { return value.Value<double>(); } catch (FormatException) { return def; } catch (InvalidCastException) { return def; }
        }

        // re-derives a new position/scale from whatever transform obs currently reports, for a live overlay
        // resize without a full geometry recompute -- snaps to the nearest edge if the items center already
        // sits in the outer third of the canvas, so scaling an item pinned to a corner keeps it pinned there
        // instead of shrinking toward the canvas center.
        private static JObject GetScaledTransformFromCurrent(JToken transform, double scaleRatio, JObject preset = null)
        {
            if (transform == null) return new JObject();
            if (scaleRatio <= 0.0) scaleRatio = 1.0;

            double positionX = GetDoubleValue(transform, "positionX", 0.0);
            double positionY = GetDoubleValue(transform, "positionY", 0.0);
            double scaleX = GetDoubleValue(transform, "scaleX", 1.0);
            double scaleY = GetDoubleValue(transform, "scaleY", 1.0);
            double width = GetDoubleValue(transform, "width", 0.0);
            double height = GetDoubleValue(transform, "height", 0.0);
            if (width <= 0.0) width = GetDoubleValue(transform, "sourceWidth", 0.0) * Math.Abs(scaleX);
            if (height <= 0.0) height = GetDoubleValue(transform, "sourceHeight", 0.0) * Math.Abs(scaleY);
            if (width < 0.0) width = 0.0;
            if (height < 0.0) height = 0.0;

            double nextWidth = width * scaleRatio;
            double nextHeight = height * scaleRatio;
            double nextPositionX = positionX + (width - nextWidth) / 2.0;
            double nextPositionY = positionY + (height - nextHeight) / 2.0;

            double canvasW = 0.0, canvasH = 0.0;
            if (preset?["video"] != null)
            {
                canvasW = GetDoubleValue(preset["video"], "baseWidth", 0.0);
                canvasH = GetDoubleValue(preset["video"], "baseHeight", 0.0);
            }
            if (canvasW > 0.0 && width > 0.0)
            {
                double centerX = positionX + width / 2.0;
                if (centerX <= canvasW / 3.0) nextPositionX = positionX;
                else if (centerX >= canvasW * 2.0 / 3.0) nextPositionX = positionX + (width - nextWidth);
            }
            if (canvasH > 0.0 && height > 0.0)
            {
                double centerY = positionY + height / 2.0;
                if (centerY <= canvasH / 3.0) nextPositionY = positionY;
                else if (centerY >= canvasH * 2.0 / 3.0) nextPositionY = positionY + (height - nextHeight);
            }

            return new JObject { ["positionX"] = nextPositionX, ["positionY"] = nextPositionY, ["scaleX"] = scaleX * scaleRatio, ["scaleY"] = scaleY * scaleRatio };
        }

        private static JObject SetSceneItemScaledFromCurrent(string sceneName, JObject item, double scaleRatio, JObject preset = null)
        {
            if (item == null) return new JObject { ["ok"] = true, ["skipped"] = true };
            var current = GetSceneItemTransformLive(sceneName, item);
            if (current["ok"]?.Value<bool>() != true) return current;
            var transform = GetScaledTransformFromCurrent(current["transform"], scaleRatio, preset);
            return SetSceneItemTransform(sceneName, item, transform);
        }

        // finds the named input as a scene item, or creates it if missing -- CreateInput can fail if a same-named
        // input already exists outside this scene (obs inputs are global), so the failure path falls back to
        // SetInputSettings + CreateSceneItem against the existing input instead of treating that as fatal.
        private static JObject EnsureInputSceneItem(string sceneName, string name, string kind, JObject inputSettings, bool enabled)
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

        private static JObject GetSceneItemIndexValue(string sceneName, JObject item)
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

        private static JObject SetSceneItemIndex(string sceneName, JObject item, int index)
        {
            if (item == null) return new JObject { ["ok"] = true, ["skipped"] = true };
            if (index < 0) index = 0;
            var r = ObsWebSocket.InvokeRequest("SetSceneItemIndex", new JObject { ["sceneName"] = sceneName, ["sceneItemId"] = item["sceneItemId"]?.Value<int>() ?? 0, ["sceneItemIndex"] = index }, 3000);
            return ObsResultAsJson(r);
        }

        // keeps Window Capture positioned directly after Display Capture in scene-item order, so it paints on
        // top when both happen to be enabled during a capture-mode switch.
        private static JObject SetWindowCaptureSceneOrder(string sceneName, JArray items)
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

        // -- overlay geometry math: pure functions, no i/o. byte-for-byte formula match with
        // ReplayKitSetup/Transform.cs (a separate assembly ReplayKitHelper does not reference, so these are
        // reproduced here rather than shared) -- see Transform.OverlayOpacityValue/OverlayHueShiftValue/
        // OverlayScaleFactor/OverlayHexColor/OverlayColorValue/HasOverlayColorAdjustments/OverlayContentRect/
        // InputOverlayPos/BottomLeftCornerOverlayPos/InputOverlayGroupGeometry/ApplyInputOverlaySceneItemGeometry/
        // ApplyBongoGeometry/FitSceneItemToCanvas for the install-time twin of each function below.

        private static int OverlayOpacityValue(JObject settings)
        {
            var value = settings?["overlayOpacity"];
            if (value == null || value.Type == JTokenType.Null) return 100;
            int v = value.Value<int>();
            return v < 0 ? 0 : v > 100 ? 100 : v;
        }

        private static double OverlayHueShiftValue(JObject settings)
        {
            var value = settings?["overlayHueShift"];
            if (value == null || value.Type == JTokenType.Null) return 0.0;
            double v;
            try { v = value.Value<double>(); } catch (FormatException) { v = 0.0; } catch (InvalidCastException) { v = 0.0; }
            return v < -180.0 ? -180.0 : v > 180.0 ? 180.0 : v;
        }

        private static string OverlayHexColor(JObject settings, string key, string defaultValue)
        {
            var token = settings?[key];
            if (token == null || token.Type == JTokenType.Null) return defaultValue;
            string value = token.Value<string>().Trim().ToLowerInvariant();
            return Regex.IsMatch(value, "^#[0-9a-f]{6}$") ? value : defaultValue;
        }

        private static int OverlayColorValue(string value, string defaultValue)
        {
            string hex = value;
            if (!Regex.IsMatch(hex ?? "", "^#[0-9a-fA-F]{6}$")) hex = defaultValue;
            int red = Convert.ToInt32(hex.Substring(1, 2), 16);
            int green = Convert.ToInt32(hex.Substring(3, 2), 16);
            int blue = Convert.ToInt32(hex.Substring(5, 2), 16);
            return red | (green << 8) | (blue << 16);
        }

        private static bool HasOverlayColorAdjustments(JObject settings) =>
            Math.Abs(OverlayHueShiftValue(settings)) >= 0.001 ||
            OverlayHexColor(settings, "overlayColorMultiply", "#ffffff") != "#ffffff" ||
            OverlayHexColor(settings, "overlayColorAdd", "#000000") != "#000000";

        private static double OverlayScaleFactor(JObject settings)
        {
            var value = settings?["overlayScale"];
            if (value == null || value.Type == JTokenType.Null) return 1.0;
            int v = value.Value<int>();
            if (v < 50) v = 50;
            if (v > 200) v = 200;
            return v / 100.0;
        }

        private static JObject OverlayContentRect(JObject preset)
        {
            double canvasW = preset["video"]?["baseWidth"]?.Value<double>() ?? 0.0;
            double canvasH = preset["video"]?["baseHeight"]?.Value<double>() ?? 0.0;
            if (canvasW <= 0.0 || canvasH <= 0.0) { canvasW = 1920.0; canvasH = 1080.0; }
            double sourceW = canvasW, sourceH = canvasH;
            if (preset["source"] != null)
            {
                double candW = GetDoubleValue(preset["source"], "width", 0.0);
                double candH = GetDoubleValue(preset["source"], "height", 0.0);
                if (candW > 0.0) sourceW = candW;
                if (candH > 0.0) sourceH = candH;
            }
            if (sourceW <= 0.0) sourceW = canvasW;
            if (sourceH <= 0.0) sourceH = canvasH;
            double scale = Math.Min(canvasW / sourceW, canvasH / sourceH);
            if (scale <= 0.0) scale = 1.0;
            double width = sourceW * scale;
            double height = sourceH * scale;
            return new JObject
            {
                ["x"] = (canvasW - width) / 2.0, ["y"] = (canvasH - height) / 2.0,
                ["width"] = width, ["height"] = height, ["scale"] = scale,
                ["canvasWidth"] = canvasW, ["canvasHeight"] = canvasH,
                ["sourceWidth"] = sourceW, ["sourceHeight"] = sourceH,
            };
        }

        private static JObject InputOverlayPos(JObject preset, double sourceW, double sourceH, double scaleX, double scaleY)
        {
            var content = OverlayContentRect(preset);
            double refScale = content["height"].Value<double>() / 1080.0;
            double x = content["x"].Value<double>() + 15.0 * refScale;
            double y = content["y"].Value<double>() + content["height"].Value<double>() - sourceH * scaleY - 16.0 * refScale;
            if (x < 0.0) x = 0.0;
            if (y < 0.0) y = 0.0;
            return new JObject { ["x"] = x, ["y"] = y };
        }

        private static JObject BottomLeftCornerOverlayPos(JObject preset, double sourceW, double sourceH, double scaleX, double scaleY)
        {
            var content = OverlayContentRect(preset);
            double x = content["x"].Value<double>();
            double y = content["y"].Value<double>() + content["height"].Value<double>() - sourceH * scaleY;
            if (x < 0.0) x = 0.0;
            if (y < 0.0) y = 0.0;
            return new JObject { ["x"] = x, ["y"] = y };
        }

        private static bool OverlayFlipHorizontal(JObject settings) => settings?["overlayFlipH"]?.Value<bool>() ?? false;

        // horizontal mirror: negate scaleX and shift positionX right by the rendered width so the item stays in the
        // same on-screen box (every overlay item is top-left aligned). sourceWidth is the item's unscaled px width.
        private static void ApplyOverlayFlipH(JObject transform, double sourceWidth, JObject settings)
        {
            if (!OverlayFlipHorizontal(settings)) return;
            double sx = transform["scaleX"]?.Value<double>() ?? 1.0;
            transform["positionX"] = (transform["positionX"]?.Value<double>() ?? 0.0) + sourceWidth * Math.Abs(sx);
            transform["scaleX"] = -sx;
        }

        private static JObject InputOverlayGroupTransform(JObject preset, JObject settings = null)
        {
            var content = OverlayContentRect(preset);
            double scale = content["height"].Value<double>() / 1440.0 * OverlayScaleFactor(settings);
            var pos = InputOverlayPos(preset, 628.0, 292.0, scale, scale);
            var transform = new JObject
            {
                ["positionX"] = pos["x"].Value<double>(), ["positionY"] = pos["y"].Value<double>(), ["scaleX"] = scale, ["scaleY"] = scale,
                ["rotation"] = 0.0, ["alignment"] = 5, ["boundsType"] = "OBS_BOUNDS_NONE", ["boundsAlignment"] = 0, ["cropToBounds"] = false,
            };
            ApplyOverlayFlipH(transform, 628.0, settings);
            return transform;
        }

        private static JObject InputOverlayTransform(string name, JObject preset, JObject settings = null)
        {
            var content = OverlayContentRect(preset);
            double scale = content["height"].Value<double>() / 1440.0 * OverlayScaleFactor(settings);
            // un-flipped group base -- these transforms feed the WASD/Mouse group_item_backup items, which don't
            // render; the visible mirror comes from InputOverlayGroupTransform on the Group scene item.
            var pos = InputOverlayPos(preset, 628.0, 292.0, scale, scale);
            double groupX = pos["x"].Value<double>(), groupY = pos["y"].Value<double>();
            if (name == "Mouse Overlay")
            {
                return new JObject
                {
                    ["positionX"] = groupX + 431.0 * scale,
                    ["positionY"] = groupY,
                    ["scaleX"] = 0.6909722089767456 * scale, ["scaleY"] = 0.6909722089767456 * scale,
                    ["rotation"] = 0.0, ["alignment"] = 5, ["boundsType"] = "OBS_BOUNDS_NONE", ["boundsAlignment"] = 0, ["cropToBounds"] = false,
                };
            }
            return new JObject
            {
                ["positionX"] = groupX, ["positionY"] = groupY,
                ["scaleX"] = 0.7395833134651184 * scale, ["scaleY"] = 0.7388888597488403 * scale,
                ["rotation"] = 0.0, ["alignment"] = 5, ["boundsType"] = "OBS_BOUNDS_NONE", ["boundsAlignment"] = 0, ["cropToBounds"] = false,
            };
        }

        private static JObject BongoTransform(JObject preset, JObject settings = null)
        {
            var content = OverlayContentRect(preset);
            double ratio = content["height"].Value<double>() / 1080.0 * OverlayScaleFactor(settings);
            double scaleX = 0.4892578125 * ratio;
            double scaleY = 0.48828125 * ratio;
            var pos = BottomLeftCornerOverlayPos(preset, 1280.0, 768.0, scaleX, scaleY);
            var transform = new JObject
            {
                ["positionX"] = pos["x"].Value<double>(), ["positionY"] = pos["y"].Value<double>(), ["scaleX"] = scaleX, ["scaleY"] = scaleY,
                ["rotation"] = 0.0, ["alignment"] = 5, ["boundsType"] = "OBS_BOUNDS_NONE", ["boundsAlignment"] = 0, ["cropToBounds"] = false,
            };
            ApplyOverlayFlipH(transform, 1280.0, settings);
            return transform;
        }

        private static JObject MainCaptureTransform(string name, JObject preset)
        {
            double canvasW = preset["video"]?["baseWidth"]?.Value<double>() ?? 0.0;
            double canvasH = preset["video"]?["baseHeight"]?.Value<double>() ?? 0.0;
            if (canvasW <= 0.0 || canvasH <= 0.0) { canvasW = 1920.0; canvasH = 1080.0; }

            if (name == "Display Capture")
            {
                double sourceW = canvasW, sourceH = canvasH;
                if (preset["source"] != null)
                {
                    double sw = preset["source"]["width"]?.Value<double>() ?? 0.0;
                    double sh = preset["source"]["height"]?.Value<double>() ?? 0.0;
                    if (sw > 0.0) sourceW = sw;
                    if (sh > 0.0) sourceH = sh;
                }
                double scale = Math.Min(canvasW / sourceW, canvasH / sourceH);
                double scaledW = sourceW * scale, scaledH = sourceH * scale;
                return new JObject
                {
                    ["positionX"] = (canvasW - scaledW) / 2.0, ["positionY"] = (canvasH - scaledH) / 2.0,
                    ["scaleX"] = scale, ["scaleY"] = scale, ["rotation"] = 0.0, ["alignment"] = 5,
                    ["boundsType"] = "OBS_BOUNDS_NONE", ["boundsAlignment"] = 0, ["cropToBounds"] = false,
                    ["sourceWidth"] = sourceW, ["sourceHeight"] = sourceH,
                };
            }

            return new JObject
            {
                ["positionX"] = 0.0, ["positionY"] = 0.0, ["scaleX"] = 1.0, ["scaleY"] = 1.0,
                ["rotation"] = 0.0, ["alignment"] = 5, ["boundsType"] = "OBS_BOUNDS_SCALE_INNER",
                ["boundsWidth"] = canvasW, ["boundsHeight"] = canvasH, ["boundsAlignment"] = 0, ["cropToBounds"] = false,
                ["sourceWidth"] = canvasW, ["sourceHeight"] = canvasH,
            };
        }

        // resolves which live scene collection file obs currently has active. no Setup equivalent -- Setup
        // always targets the single fixed bundled path at install time and never has to ask a running obs.
        private static string GetSceneCollectionPath()
        {
            string scenesRoot = Path.Combine(Environment.GetEnvironmentVariable("APPDATA") ?? "", "obs-studio", "basic", "scenes");
            string collectionName = "Untitled";
            var list = ObsWebSocket.InvokeRequest("GetSceneCollectionList", null, 3000);
            if (list.Ok && !string.IsNullOrEmpty(list.Data?["currentSceneCollectionName"]?.Value<string>()))
                collectionName = list.Data["currentSceneCollectionName"].Value<string>();
            if (string.IsNullOrWhiteSpace(collectionName) || collectionName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidOperationException("OBS scene collection name is invalid.");

            string rootFull = Path.GetFullPath(scenesRoot).TrimEnd('\\');
            string path = Path.GetFullPath(Path.Combine(scenesRoot, collectionName + ".json"));
            if (!path.StartsWith(rootFull + "\\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("OBS scene collection path resolved outside the OBS scenes directory.");
            return path;
        }

        // -- json-file scene-source/scene-item builders. byte-for-byte shape match with
        // ReplayKitSetup/Transform.cs's install-time twins (NewBongoSource/NewWindowCaptureSource/
        // NewMotionBlurFilter/NewOverlayOpacityFilter and friends) -- reproduced here rather than shared since
        // ReplayKitHelper does not reference the Setup assembly. unlike Transform.cs (which always mints a fresh
        // BongoCatSourceUuid constant for a brand-new template file), the live-apply functions below preserve
        // whatever uuid an existing source already has and only mint a new one when creating from scratch --
        // callers pass whichever uuid is appropriate, these functions never generate one themselves.

        private static void SetBongoSourceJson(JObject source, string uuid)
        {
            source["prev_ver"] = 536936450; source["name"] = "Bongo Cat Overlay"; source["uuid"] = uuid;
            source["id"] = "bongobs-cat"; source["versioned_id"] = "bongobs-cat";
            source["mixers"] = 0; source["sync"] = 0; source["flags"] = 0; source["volume"] = 1.0; source["balance"] = 0.5;
            source["enabled"] = true; source["muted"] = false;
            source["push-to-mute"] = false; source["push-to-mute-delay"] = 0; source["push-to-talk"] = false; source["push-to-talk-delay"] = 0;
            source["hotkeys"] = new JObject();
            source["deinterlace_mode"] = 0; source["deinterlace_field_order"] = 0; source["monitoring_type"] = 0;
            source["private_settings"] = new JObject();

            var sourceSettings = source["settings"] as JObject;
            if (sourceSettings == null) { sourceSettings = new JObject(); source["settings"] = sourceSettings; }
            sourceSettings.Remove("mode");
            sourceSettings["Mode"] = "standard"; sourceSettings["width"] = 1280; sourceSettings["height"] = 768;
            sourceSettings["x"] = 0.0; sourceSettings["y"] = 0.02; sourceSettings["scale"] = 1.83;
            sourceSettings["delay"] = 1.0; sourceSettings["delaytime"] = 1.0;
            sourceSettings["random_motion"] = true; sourceSettings["breath"] = true; sourceSettings["eyeblink"] = true; sourceSettings["track"] = true;
            sourceSettings["live2d"] = true; sourceSettings["relative_mouse"] = true;
            sourceSettings["mouse_horizontal_flip"] = true; sourceSettings["mouse_vertical_flip"] = true; sourceSettings["mask"] = false;
        }

        private static JObject NewBongoSourceJson(string uuid)
        {
            var source = new JObject();
            SetBongoSourceJson(source, uuid);
            return source;
        }

        private static JObject GetGameCaptureInputSettings(JObject settings) => new JObject
        {
            ["capture_audio"] = false, ["hook_rate"] = 2, ["limit_framerate"] = true, ["capture_cursor"] = true,
            ["capture_overlays"] = false, ["anti_cheat_hook"] = true, ["capture_mode"] = "any_fullscreen", ["window"] = "",
        };

        private static JObject GetWindowCaptureInputSettings(JObject settings) => new JObject
        {
            ["window"] = settings?["screenshareGameWindow"]?.Value<string>() ?? "",
            ["method"] = 2, ["priority"] = 0, ["cursor"] = true, ["client_area"] = true,
            ["compatibility"] = false, ["force_sdr"] = false, ["capture_audio"] = false,
        };

        private static void SetWindowCaptureSourceJson(JObject source, string uuid, JObject settings)
        {
            source["prev_ver"] = 536936450; source["name"] = "Window Capture"; source["uuid"] = uuid;
            source["id"] = "window_capture"; source["versioned_id"] = "window_capture";
            source["mixers"] = 0; source["sync"] = 0; source["flags"] = 0; source["volume"] = 1.0; source["balance"] = 0.5;
            source["enabled"] = true; source["muted"] = false;
            source["push-to-mute"] = false; source["push-to-mute-delay"] = 0; source["push-to-talk"] = false; source["push-to-talk-delay"] = 0;
            source["hotkeys"] = new JObject();
            source["deinterlace_mode"] = 0; source["deinterlace_field_order"] = 0; source["monitoring_type"] = 0;
            source["private_settings"] = new JObject();
            source["settings"] = GetWindowCaptureInputSettings(settings);
        }

        private static JObject NewWindowCaptureSourceJson(string uuid, JObject settings)
        {
            var source = new JObject();
            SetWindowCaptureSourceJson(source, uuid, settings);
            return source;
        }

        private static void SetInputOverlaySourceJson(JObject source, string name, string uuid, string image, string layout)
        {
            if (string.IsNullOrWhiteSpace(image) || string.IsNullOrWhiteSpace(layout))
                throw new InvalidOperationException("Input overlay assets are missing for " + name + ".");
            source["prev_ver"] = 536936450; source["name"] = name; source["uuid"] = uuid;
            source["id"] = "input-overlay"; source["versioned_id"] = "input-overlay";
            source["mixers"] = 0; source["sync"] = 0; source["flags"] = 0; source["volume"] = 1.0; source["balance"] = 0.5;
            source["enabled"] = true; source["muted"] = false;
            source["push-to-mute"] = false; source["push-to-mute-delay"] = 0; source["push-to-talk"] = false; source["push-to-talk-delay"] = 0;
            source["hotkeys"] = new JObject();
            source["deinterlace_mode"] = 0; source["deinterlace_field_order"] = 0; source["monitoring_type"] = 0;
            source["private_settings"] = new JObject();

            var sourceSettings = new JObject
            {
                ["io.input_source"] = "This computer",
                ["io.overlay_image"] = image.Replace('\\', '/'),
                ["io.layout_file"] = layout.Replace('\\', '/'),
            };
            source["settings"] = sourceSettings;
        }

        private static JObject NewInputOverlaySourceJson(string name, string uuid, string image, string layout)
        {
            var source = new JObject();
            SetInputOverlaySourceJson(source, name, uuid, image, layout);
            return source;
        }

        private static void SetSceneItemBaseJson(JObject item, string name, bool visible)
        {
            item["name"] = name; item["visible"] = visible; item["locked"] = false;
            item["rot"] = 0.0; item["align"] = 5;
            item["bounds_type"] = 0; item["bounds_align"] = 0; item["bounds_crop"] = false;
            item["crop_left"] = 0; item["crop_top"] = 0; item["crop_right"] = 0; item["crop_bottom"] = 0;
            item["bounds"] = new JObject { ["x"] = 0.0, ["y"] = 0.0 };
            item["bounds_rel"] = new JObject { ["x"] = 0.0, ["y"] = 0.0 };
            item["scale_filter"] = "disable"; item["blend_method"] = "default"; item["blend_type"] = "normal";
            item["private_settings"] = new JObject();
        }

        // fit-to-canvas item builder shared by Display/Game/Window Capture.
        private static void SetMainCaptureSceneItemJson(JObject item, string name, JObject preset)
        {
            double canvasW = preset["video"]?["baseWidth"]?.Value<double>() ?? 0.0;
            double canvasH = preset["video"]?["baseHeight"]?.Value<double>() ?? 0.0;
            if (canvasW <= 0.0 || canvasH <= 0.0) { canvasW = 1920.0; canvasH = 1080.0; }
            bool visible = item["visible"]?.Value<bool>() ?? true;
            var transform = MainCaptureTransform(name, preset);
            SetSceneItemBaseJson(item, name, visible);
            double srcW = transform["sourceWidth"].Value<double>(), srcH = transform["sourceHeight"].Value<double>();
            double posX = transform["positionX"].Value<double>(), posY = transform["positionY"].Value<double>();
            item["scale_ref"] = new JObject { ["x"] = srcW, ["y"] = srcH };
            item["pos"] = new JObject { ["x"] = posX, ["y"] = posY };
            item["pos_rel"] = new JObject { ["x"] = (posX - canvasW / 2.0) / (canvasH / 2.0), ["y"] = (posY - canvasH / 2.0) / (canvasH / 2.0) };
            item["scale"] = new JObject { ["x"] = transform["scaleX"].Value<double>(), ["y"] = transform["scaleY"].Value<double>() };
            item["scale_rel"] = new JObject { ["x"] = 1.0, ["y"] = 1.0 };
            item["group_item_backup"] = false;
            if (transform["boundsType"]?.Value<string>() == "OBS_BOUNDS_SCALE_INNER")
            {
                item["bounds_type"] = 2;
                item["bounds"] = new JObject { ["x"] = canvasW, ["y"] = canvasH };
                item["bounds_rel"] = new JObject { ["x"] = 2.0 * canvasW / canvasH, ["y"] = 2.0 };
            }
        }

        private static void SetWindowCaptureSceneItemJson(JObject item, string uuid, JObject preset, bool visible)
        {
            SetMainCaptureSceneItemJson(item, "Window Capture", preset);
            item["source_uuid"] = uuid;
            item["visible"] = visible;
        }

        private static void SetBongoSceneItemJson(JObject item, string uuid, JObject preset, bool visible, JObject settings = null)
        {
            double canvasW = preset["video"]?["baseWidth"]?.Value<double>() ?? 0.0;
            double canvasH = preset["video"]?["baseHeight"]?.Value<double>() ?? 0.0;
            var transform = BongoTransform(preset, settings);
            SetSceneItemBaseJson(item, "Bongo Cat Overlay", visible);
            item["source_uuid"] = uuid;
            item["scale_ref"] = new JObject { ["x"] = 1280.0, ["y"] = 768.0 };
            double posX = transform["positionX"].Value<double>(), posY = transform["positionY"].Value<double>();
            double scaleX = transform["scaleX"].Value<double>(), scaleY = transform["scaleY"].Value<double>();
            item["pos"] = new JObject { ["x"] = posX, ["y"] = posY };
            item["pos_rel"] = new JObject { ["x"] = (posX - canvasW / 2.0) / (canvasH / 2.0), ["y"] = (posY - canvasH / 2.0) / (canvasH / 2.0) };
            item["scale"] = new JObject { ["x"] = scaleX, ["y"] = scaleY };
            item["scale_rel"] = new JObject { ["x"] = scaleX * 768.0 / canvasH, ["y"] = scaleY * 768.0 / canvasH };
            item["group_item_backup"] = false;
        }

        private static JObject NewJsonTransition(int duration) => new JObject { ["duration"] = duration };

        private static void SetInputOverlaySceneItemJson(JObject item, string name, JObject preset, bool visible, string sourceUuid = "", bool groupBackup = false, JObject settings = null)
        {
            double canvasW = preset["video"]?["baseWidth"]?.Value<double>() ?? 0.0;
            double canvasH = preset["video"]?["baseHeight"]?.Value<double>() ?? 0.0;
            var transform = InputOverlayTransform(name, preset, settings);
            double sourceW = name == "Mouse Overlay" ? 285.0 : 568.0;
            double sourceH = name == "Mouse Overlay" ? 421.0 : 394.0;
            SetSceneItemBaseJson(item, name, visible);
            if (!string.IsNullOrWhiteSpace(sourceUuid)) item["source_uuid"] = sourceUuid;
            item["scale_ref"] = new JObject { ["x"] = sourceW, ["y"] = sourceH };
            double posX = transform["positionX"].Value<double>(), posY = transform["positionY"].Value<double>();
            double scaleX = transform["scaleX"].Value<double>(), scaleY = transform["scaleY"].Value<double>();
            item["pos"] = new JObject { ["x"] = posX, ["y"] = posY };
            item["pos_rel"] = new JObject { ["x"] = (posX - canvasW / 2.0) / (canvasH / 2.0), ["y"] = (posY - canvasH / 2.0) / (canvasH / 2.0) };
            item["scale"] = new JObject { ["x"] = scaleX, ["y"] = scaleY };
            item["scale_rel"] = new JObject { ["x"] = scaleX * 1440.0 / canvasH, ["y"] = scaleY * 1440.0 / canvasH };
            item["group_item_backup"] = groupBackup;
        }

        private static void SetInputOverlayGroupMemberJson(JObject item, string name, string sourceUuid, JObject preset, int id)
        {
            const double canvasW = 2560.0, canvasH = 1440.0;
            double x, y, scaleX, scaleY;
            if (name == "Mouse Overlay") { x = 431.0; y = 0.0; scaleX = scaleY = 0.6909722089767456; }
            else { x = 0.0; y = 0.0; scaleX = 0.7395833134651184; scaleY = 0.7388888597488403; }
            SetSceneItemBaseJson(item, name, true);
            item["source_uuid"] = sourceUuid;
            item["scale_ref"] = new JObject { ["x"] = canvasW, ["y"] = canvasH };
            item["id"] = id;
            item["group_item_backup"] = false;
            item["pos"] = new JObject { ["x"] = x, ["y"] = y };
            item["pos_rel"] = new JObject { ["x"] = (x - canvasW / 2.0) / (canvasH / 2.0), ["y"] = (y - canvasH / 2.0) / (canvasH / 2.0) };
            item["scale"] = new JObject { ["x"] = scaleX, ["y"] = scaleY };
            item["scale_rel"] = new JObject { ["x"] = scaleX, ["y"] = scaleY };
            item["show_transition"] = NewJsonTransition(300);
            item["hide_transition"] = NewJsonTransition(300);
        }

        // WASD Overlay / Mouse Overlay -> source uuid + the scene-item id it ends up at once placed. Id starts
        // at 0 (unknown) and gets patched in by SetOverlaySceneFile once the corresponding scene item is written --
        // a plain mutable field, not a tuple, since that patch-after-construction is exactly what a tuple cant do.
        private sealed class InputOverlaySourceRef
        {
            public string Uuid;
            public int Id;
        }

        // builds the group-kind source that wraps WASD+Mouse together, with its own nested items array using
        // canvas-relative 2560x1440 reference geometry independent of the real canvas size. inputSources maps
        // "WASD Overlay"/"Mouse Overlay" -> {uuid, id} for whichever of the two are actually present.
        private static void SetInputOverlayGroupSourceJson(JObject group, string groupUuid, Dictionary<string, InputOverlaySourceRef> inputSources, JObject preset)
        {
            group["prev_ver"] = 536936450; group["name"] = "Group"; group["uuid"] = groupUuid;
            group["id"] = "group"; group["versioned_id"] = "group";

            var settings = new JObject { ["id_counter"] = 0, ["custom_size"] = true, ["cx"] = 628, ["cy"] = 292 };
            var items = new JArray();
            foreach (var name in new[] { "WASD Overlay", "Mouse Overlay" })
            {
                if (!inputSources.TryGetValue(name, out var src)) continue;
                var member = new JObject();
                SetInputOverlayGroupMemberJson(member, name, src.Uuid, preset, src.Id);
                items.Add(member);
            }
            settings["items"] = items;
            group["settings"] = settings;

            group["mixers"] = 0; group["sync"] = 0; group["flags"] = 0; group["volume"] = 1.0; group["balance"] = 0.5;
            group["enabled"] = true; group["muted"] = false;
            group["push-to-mute"] = false; group["push-to-mute-delay"] = 0; group["push-to-talk"] = false; group["push-to-talk-delay"] = 0;
            var hotkeys = new JObject();
            foreach (var name in new[] { "WASD Overlay", "Mouse Overlay" })
            {
                if (!inputSources.TryGetValue(name, out var src)) continue;
                hotkeys["libobs.show_scene_item." + src.Id] = new JArray();
                hotkeys["libobs.hide_scene_item." + src.Id] = new JArray();
            }
            group["hotkeys"] = hotkeys;
            group["deinterlace_mode"] = 0; group["deinterlace_field_order"] = 0; group["monitoring_type"] = 0;
            group["canvas_uuid"] = "6c69626f-6273-4c00-9d88-c5136d61696e";
            group["private_settings"] = new JObject();
        }

        private static void SetInputOverlayGroupSceneItemJson(JObject item, string groupUuid, JObject preset, bool visible, JObject settings = null)
        {
            double canvasW = preset["video"]?["baseWidth"]?.Value<double>() ?? 0.0;
            double canvasH = preset["video"]?["baseHeight"]?.Value<double>() ?? 0.0;
            var transform = InputOverlayGroupTransform(preset, settings);
            double scaleX = transform["scaleX"].Value<double>(), scaleY = transform["scaleY"].Value<double>();
            double x = transform["positionX"].Value<double>(), y = transform["positionY"].Value<double>();
            SetSceneItemBaseJson(item, "Group", visible);
            item["source_uuid"] = groupUuid;
            item["scale_ref"] = new JObject { ["x"] = canvasW, ["y"] = canvasH };
            item["group_item_backup"] = false;
            item["pos"] = new JObject { ["x"] = x, ["y"] = y };
            item["pos_rel"] = new JObject { ["x"] = (x - canvasW / 2.0) / (canvasH / 2.0), ["y"] = (y - canvasH / 2.0) / (canvasH / 2.0) };
            item["scale"] = new JObject { ["x"] = scaleX, ["y"] = scaleY };
            double scaleRel = OverlayScaleFactor(settings);
            item["scale_rel"] = new JObject { ["x"] = OverlayFlipHorizontal(settings) ? -scaleRel : scaleRel, ["y"] = scaleRel };
            item["show_transition"] = NewJsonTransition(0);
            item["hide_transition"] = NewJsonTransition(0);
            item["private_settings"] = new JObject { ["collapsed"] = false };
        }

        private static int GetNextJsonSceneItemId(JArray items)
        {
            int max = 0;
            if (items == null) return 1;
            foreach (var item in items)
            {
                var idToken = item["id"];
                if (idToken == null) continue;
                try { int id = idToken.Value<int>(); if (id > max) max = id; } catch (FormatException) { } catch (InvalidCastException) { }
            }
            return max + 1;
        }

        // moves the named item to just after another named item in the scene's item list -- used to keep Window
        // Capture positioned right after Display Capture when inserting it into an existing item list.
        private static void MoveJsonSceneItemAfter(JArray items, string targetName, string afterName)
        {
            if (items == null) return;
            JObject target = null;
            foreach (var item in items)
            {
                if (item["name"]?.Value<string>() == targetName) { target = (JObject)item; break; }
            }
            if (target == null) return;
            items.Remove(target);
            int insertAt = 0;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i]["name"]?.Value<string>() == afterName) { insertAt = i + 1; break; }
            }
            items.Insert(insertAt, target);
        }

        // -- motion blur filter (Display/Game/Window Capture) --

        private static string MotionBlurFilterUuid(string sourceName)
        {
            switch (sourceName)
            {
                case "Display Capture": return "e371efc8-8c99-44cb-95e7-94381d9c9e41";
                case "Game Capture": return "26bc5a11-5315-4390-b028-f77667c7fda3";
                case "Window Capture": return "9b73d6cb-b65e-44a1-895f-4e2f326a8d77";
                default: return "";
            }
        }

        private static string GetObsInstallRoot()
        {
            string obs = Server.State.ObsExe;
            if (string.IsNullOrWhiteSpace(obs) || !File.Exists(obs))
            {
                string candidate = Constants.ResolveObsExe();
                if (File.Exists(candidate)) obs = candidate;
            }
            try
            {
                return Directory.GetParent(Directory.GetParent(Directory.GetParent(obs).FullName).FullName).FullName;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is DirectoryNotFoundException || ex is NullReferenceException)
            {
                string programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
                return !string.IsNullOrEmpty(programFiles) ? Path.Combine(programFiles, "obs-studio") : "C:\\Program Files\\obs-studio";
            }
        }

        private static string ShaderfilterMotionBlurPath() =>
            Path.Combine(GetObsInstallRoot(), "data\\obs-plugins\\obs-shaderfilter\\examples\\motion_blur.shader").Replace('\\', '/');

        private static double MotionBlurStrength(JObject settings)
        {
            double value = settings["motionBlurStrength"]?.Value<double>() ?? 0.0;
            return value < 0.0 ? 0.0 : value > 1.0 ? 1.0 : value;
        }

        private static JObject MotionBlurFilterSettingsJson(double strength) => new JObject
        {
            ["from_file"] = true, ["shader_file_name"] = ShaderfilterMotionBlurPath(), ["override_entire_effect"] = false, ["strength"] = strength,
        };

        private static JObject MotionBlurFilterSettingsLive(double strength) => new JObject
        {
            ["from_file"] = true, ["shader_file_name"] = ShaderfilterMotionBlurPath(), ["override_entire_effect"] = false, ["strength"] = strength,
        };

        private static void SetMotionBlurFilterJson(JObject filter, string sourceName, bool enabled, double strength)
        {
            string uuid = MotionBlurFilterUuid(sourceName);
            if (string.IsNullOrWhiteSpace(uuid)) throw new InvalidOperationException("Unsupported motion blur source: " + sourceName);
            filter["prev_ver"] = 536936450; filter["name"] = "ReplayKit Motion Blur"; filter["uuid"] = uuid;
            filter["id"] = "shader_filter"; filter["versioned_id"] = "shader_filter";
            filter["settings"] = MotionBlurFilterSettingsJson(strength);
            filter["mixers"] = 0; filter["sync"] = 0; filter["flags"] = 0; filter["volume"] = 1.0; filter["balance"] = 0.5;
            filter["enabled"] = enabled; filter["muted"] = false;
            filter["push-to-mute"] = false; filter["push-to-mute-delay"] = 0; filter["push-to-talk"] = false; filter["push-to-talk-delay"] = 0;
            filter["hotkeys"] = new JObject();
            filter["deinterlace_mode"] = 0; filter["deinterlace_field_order"] = 0; filter["monitoring_type"] = 0;
            filter["private_settings"] = new JObject();
        }

        private static JObject NewMotionBlurFilterJson(string sourceName, bool enabled, double strength)
        {
            var filter = new JObject();
            SetMotionBlurFilterJson(filter, sourceName, enabled, strength);
            return filter;
        }

        private static bool IsShaderfilterMotionBlurJson(JObject filter)
        {
            if (filter["id"]?.Value<string>() != "shader_filter") return false;
            var filterSettings = filter["settings"];
            if (filterSettings == null) return false;
            string shaderFile = filterSettings["shader_file_name"]?.Value<string>() ?? "";
            return shaderFile.Replace('\\', '/').ToLowerInvariant().EndsWith("/motion_blur.shader");
        }

        private static bool IsManagedMotionBlurJson(JObject filter, string uuid)
        {
            string name = filter["name"]?.Value<string>() ?? "";
            string filterUuid = filter["uuid"]?.Value<string>() ?? "";
            string filterId = filter["id"]?.Value<string>() ?? "";
            return name == "ReplayKit Motion Blur" || filterUuid == uuid || filterId == "obs_composite_blur" || IsShaderfilterMotionBlurJson(filter);
        }

        private static void SetMotionBlurSourceJson(JObject source, bool enabled, double strength)
        {
            string sourceName = source["name"]?.Value<string>() ?? "";
            string uuid = MotionBlurFilterUuid(sourceName);
            if (string.IsNullOrWhiteSpace(uuid)) return;

            var filters = source["filters"] as JArray ?? new JArray();
            source["filters"] = filters;

            foreach (var filter in filters)
            {
                if (IsManagedMotionBlurJson((JObject)filter, uuid))
                {
                    SetMotionBlurFilterJson((JObject)filter, sourceName, enabled, strength);
                    return;
                }
            }
            filters.Add(NewMotionBlurFilterJson(sourceName, enabled, strength));
        }

        private static void RemoveMotionBlurSourceJson(JObject source)
        {
            string sourceName = source["name"]?.Value<string>() ?? "";
            string uuid = MotionBlurFilterUuid(sourceName);
            if (string.IsNullOrWhiteSpace(uuid)) return;

            var filters = source["filters"] as JArray;
            if (filters == null) return;
            for (int i = filters.Count - 1; i >= 0; i--)
            {
                if (IsManagedMotionBlurJson((JObject)filters[i], uuid)) filters.RemoveAt(i);
            }
            source["filters"] = filters;
        }

        // -- overlay opacity/color filter (WASD/Mouse/Bongo Cat) --

        private static string OverlayOpacityFilterUuid(string sourceName)
        {
            switch (sourceName)
            {
                case "WASD Overlay": return "a65fb4f0-a894-463e-9b9b-f0a9d5fb4fa1";
                case "Mouse Overlay": return "c097fe72-641f-4da5-94f6-71f7c6353f9f";
                case "Bongo Cat Overlay": return "4ecb70c4-e8f0-4207-a2cc-0307ff771722";
                default: return "";
            }
        }

        private static JObject OverlayOpacityFilterSettingsLive(int opacityPercent, bool legacyPercent, JObject settings = null)
        {
            double hueShift = OverlayHueShiftValue(settings);
            int multiply = OverlayColorValue(OverlayHexColor(settings, "overlayColorMultiply", "#ffffff"), "#ffffff");
            int add = OverlayColorValue(OverlayHexColor(settings, "overlayColorAdd", "#000000"), "#000000");
            if (legacyPercent)
            {
                int opacity = Math.Max(0, Math.Min(100, opacityPercent));
                return new JObject { ["opacity"] = opacity, ["hue_shift"] = hueShift, ["color"] = multiply };
            }
            double opacityF = Math.Max(0.0, Math.Min(1.0, opacityPercent / 100.0));
            return new JObject { ["opacity"] = opacityF, ["hue_shift"] = hueShift, ["color_multiply"] = multiply, ["color_add"] = add };
        }

        private static bool OverlayOpacityFilterUsesLegacyPercent(string kind, JObject settings = null)
        {
            if (kind == "color_filter_v2") return false;
            if (kind == "color_filter") return true;
            if (settings != null)
            {
                if (settings["versioned_id"]?.Value<string>() == "color_filter_v2") return false;
                var raw = settings["opacity"];
                if (raw != null)
                {
                    try { if (raw.Value<double>() > 1.0) return true; } catch (FormatException) { } catch (InvalidCastException) { }
                }
            }
            return false;
        }

        private static void SetOverlayOpacityFilterJson(JObject filter, string sourceName, int opacityPercent, JObject settings = null)
        {
            string uuid = OverlayOpacityFilterUuid(sourceName);
            if (string.IsNullOrWhiteSpace(uuid)) throw new InvalidOperationException("Unsupported overlay opacity source: " + sourceName);
            filter["prev_ver"] = 536936450; filter["name"] = "ReplayKit Overlay Opacity"; filter["uuid"] = uuid;
            filter["id"] = "color_filter"; filter["versioned_id"] = "color_filter_v2";
            filter["settings"] = OverlayOpacityFilterSettingsLive(opacityPercent, false, settings);
            filter["mixers"] = 0; filter["sync"] = 0; filter["flags"] = 0; filter["volume"] = 1.0; filter["balance"] = 0.5;
            filter["enabled"] = true; filter["muted"] = false;
            filter["push-to-mute"] = false; filter["push-to-mute-delay"] = 0; filter["push-to-talk"] = false; filter["push-to-talk-delay"] = 0;
            filter["hotkeys"] = new JObject();
            filter["deinterlace_mode"] = 0; filter["deinterlace_field_order"] = 0; filter["monitoring_type"] = 0;
            filter["private_settings"] = new JObject();
        }

        private static JObject NewOverlayOpacityFilterJson(string sourceName, int opacityPercent, JObject settings = null)
        {
            var filter = new JObject();
            SetOverlayOpacityFilterJson(filter, sourceName, opacityPercent, settings);
            return filter;
        }

        private static bool IsManagedOverlayOpacityJson(JObject filter, string uuid)
        {
            string name = filter["name"]?.Value<string>() ?? "";
            string filterUuid = filter["uuid"]?.Value<string>() ?? "";
            return name == "ReplayKit Overlay Opacity" || (!string.IsNullOrWhiteSpace(uuid) && filterUuid == uuid);
        }

        private static void SetOverlayOpacitySourceJson(JObject source, JObject settings)
        {
            string sourceName = source["name"]?.Value<string>() ?? "";
            string uuid = OverlayOpacityFilterUuid(sourceName);
            if (string.IsNullOrWhiteSpace(uuid)) return;
            int opacity = OverlayOpacityValue(settings);
            bool hasColorAdjustments = HasOverlayColorAdjustments(settings);

            var filters = source["filters"] as JArray ?? new JArray();
            JObject managedFilter = null;
            var keptFilters = new JArray();
            foreach (var filterToken in filters)
            {
                var filter = (JObject)filterToken;
                string name = filter["name"]?.Value<string>() ?? "";
                string filterUuid = filter["uuid"]?.Value<string>() ?? "";
                if (name != "ReplayKit Overlay Opacity" && (string.IsNullOrWhiteSpace(uuid) || filterUuid != uuid))
                {
                    keptFilters.Add(filter);
                    continue;
                }
                if (managedFilter == null) { managedFilter = filter; keptFilters.Add(filter); }
            }
            filters = keptFilters;
            if (managedFilter != null)
            {
                var filterSettings = managedFilter["settings"] as JObject ?? new JObject();
                string id = managedFilter["id"]?.Value<string>() ?? "";
                string versionedId = managedFilter["versioned_id"]?.Value<string>() ?? "";
                bool legacyPercent = id == "color_filter" && versionedId != "color_filter_v2";
                var opacitySettings = OverlayOpacityFilterSettingsLive(opacity, legacyPercent, settings);
                foreach (var kv in opacitySettings) filterSettings[kv.Key] = kv.Value;
                managedFilter["settings"] = filterSettings;
                managedFilter["enabled"] = true;
            }
            else if (opacity < 100 || hasColorAdjustments)
            {
                filters.Add(NewOverlayOpacityFilterJson(sourceName, opacity, settings));
            }
            if (filters.Count > 0) source["filters"] = filters;
            else if (source["filters"] != null) source.Remove("filters");
        }

        // -- overlay opacity filter: live-websocket half (create/read/update the filter on a running obs) --

        private static JObject GetOverlayOpacityLiveFilterInfo(JArray filters)
        {
            foreach (var filter in filters ?? new JArray())
            {
                string name = filter["filterName"]?.Value<string>() ?? "";
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (name == "ReplayKit Overlay Opacity")
                {
                    return new JObject
                    {
                        ["found"] = true, ["name"] = name,
                        ["kind"] = filter["filterKind"]?.Value<string>() ?? "",
                        ["settings"] = filter["filterSettings"] as JObject ?? new JObject(),
                        ["enabled"] = filter["filterEnabled"]?.Value<bool>() ?? true,
                        ["managed"] = true,
                    };
                }
            }
            return new JObject { ["found"] = false, ["name"] = "", ["kind"] = "", ["settings"] = new JObject(), ["enabled"] = false, ["managed"] = false };
        }

        private static string GetOverlayOpacityLiveFilterName(JArray filters)
        {
            var match = GetOverlayOpacityLiveFilterInfo(filters);
            return match["found"]?.Value<bool>() == true ? match["name"].Value<string>() : "";
        }

        private static int? GetOverlayOpacityPercentFromFilterSettings(JObject filterSettings)
        {
            var value = filterSettings?["opacity"];
            if (value == null || value.Type == JTokenType.Null) return null;
            try
            {
                double opacity = value.Value<double>();
                if (opacity > 1.0) return (int)Math.Round(Math.Min(opacity, 100.0));
                return (int)Math.Round(Math.Max(opacity, 0.0) * 100.0);
            }
            catch (FormatException) { return null; } catch (InvalidCastException) { return null; }
        }

        private static JObject GetSourceFilterListLive(string sourceName)
        {
            var list = ObsWebSocket.InvokeRequest("GetSourceFilterList", new JObject { ["sourceName"] = sourceName }, 3000);
            if (!list.Ok) return new JObject { ["ok"] = false, ["message"] = list.Message, ["filters"] = new JArray() };
            var filters = list.Data?["filters"] as JArray ?? list.Data?["sourceFilters"] as JArray ?? new JArray();
            return new JObject { ["ok"] = true, ["message"] = "", ["filters"] = filters };
        }

        private static bool TestSourceExistsForFilters(string sourceName) => GetSourceFilterListLive(sourceName)["ok"]?.Value<bool>() == true;

        private static JObject GetLatestSceneCollectionPath()
        {
            string root = Path.Combine(Environment.GetEnvironmentVariable("APPDATA") ?? "", "obs-studio", "basic", "scenes");
            if (!Directory.Exists(root)) return null;
            var file = new DirectoryInfo(root).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            return file != null ? new JObject { ["path"] = file.FullName } : null;
        }

        // last-resort readback of the opacity filter straight off disk, for the settings payload only (never a
        // write target) -- used when a live websocket filter lookup fails, so the settings dock still shows
        // something sensible even if obs isnt reachable right now. picks the most-recently-written scene file
        // under basic\scenes, a looser resolution than GetSceneCollectionPath (which asks obs which collection
        // is actually active); that imprecision is fine here since this never drives a write.
        private static JObject GetSceneFileOverlayOpacityPercent(JObject settings)
        {
            var pathToken = GetLatestSceneCollectionPath();
            string path = pathToken?["path"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return new JObject { ["ok"] = false, ["opacity"] = null, ["sourceName"] = "", ["filterName"] = "" };
            try
            {
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return new JObject { ["ok"] = false, ["opacity"] = null, ["sourceName"] = "", ["filterName"] = "" };
                var data = JObject.Parse(json);
                var sources = data["sources"] as JArray ?? new JArray();
                foreach (var name in GetOverlayCandidateSourceNames(settings["overlayStyle"]?.Value<string>() ?? ""))
                {
                    foreach (var sourceToken in sources)
                    {
                        var source = (JObject)sourceToken;
                        if (source["name"]?.Value<string>() != name) continue;
                        var filters = source["filters"] as JArray ?? new JArray();
                        foreach (var filterToken in filters)
                        {
                            var filter = (JObject)filterToken;
                            if (!IsManagedOverlayOpacityJson(filter, "")) continue;
                            var filterSettings = filter["settings"] as JObject ?? new JObject();
                            var opacity = GetOverlayOpacityPercentFromFilterSettings(filterSettings);
                            if (opacity != null)
                                return new JObject { ["ok"] = true, ["opacity"] = opacity.Value, ["sourceName"] = name, ["filterName"] = filter["name"]?.Value<string>() ?? "" };
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Get-ReplayKitSceneFileOverlayOpacityPercent failed: " + ex.Message);
            }
            return new JObject { ["ok"] = false, ["opacity"] = null, ["sourceName"] = "", ["filterName"] = "" };
        }

        private static JObject ApplyOverlayOpacityLive(string sourceName, int opacityPercent, JObject settings = null)
        {
            var list = GetSourceFilterListLive(sourceName);
            if (list["ok"]?.Value<bool>() != true) return new JObject { ["ok"] = false, ["message"] = list["message"] };
            var existingFilter = GetOverlayOpacityLiveFilterInfo(list["filters"] as JArray);
            string existingFilterName = existingFilter["found"]?.Value<bool>() == true ? existingFilter["name"].Value<string>() : "";
            bool hasColorAdjustments = HasOverlayColorAdjustments(settings);
            bool legacyPercent = false;
            if (existingFilter["found"]?.Value<bool>() == true)
                legacyPercent = OverlayOpacityFilterUsesLegacyPercent(existingFilter["kind"]?.Value<string>() ?? "", existingFilter["settings"] as JObject);

            if (opacityPercent >= 100 && !hasColorAdjustments)
            {
                if (string.IsNullOrWhiteSpace(existingFilterName)) return new JObject { ["ok"] = true, ["message"] = "" };
                var setFull = ObsWebSocket.InvokeRequest("SetSourceFilterSettings", new JObject
                {
                    ["sourceName"] = sourceName, ["filterName"] = existingFilterName,
                    ["filterSettings"] = OverlayOpacityFilterSettingsLive(100, legacyPercent, settings), ["overlay"] = true,
                }, 3000);
                if (!setFull.Ok) return new JObject { ["ok"] = false, ["message"] = setFull.Message };
                var enableFull = ObsWebSocket.InvokeRequest("SetSourceFilterEnabled", new JObject { ["sourceName"] = sourceName, ["filterName"] = existingFilterName, ["filterEnabled"] = true }, 3000);
                return enableFull.Ok ? new JObject { ["ok"] = true, ["message"] = "" } : new JObject { ["ok"] = false, ["message"] = enableFull.Message };
            }

            var filterSettings = OverlayOpacityFilterSettingsLive(opacityPercent, legacyPercent, settings);
            if (!string.IsNullOrWhiteSpace(existingFilterName))
            {
                var set = ObsWebSocket.InvokeRequest("SetSourceFilterSettings", new JObject { ["sourceName"] = sourceName, ["filterName"] = existingFilterName, ["filterSettings"] = filterSettings, ["overlay"] = true }, 3000);
                if (!set.Ok) return new JObject { ["ok"] = false, ["message"] = set.Message };
            }
            else
            {
                var create = ObsWebSocket.InvokeRequest("CreateSourceFilter", new JObject { ["sourceName"] = sourceName, ["filterName"] = "ReplayKit Overlay Opacity", ["filterKind"] = "color_filter_v2", ["filterSettings"] = filterSettings }, 3000);
                if (!create.Ok)
                {
                    var createFallback = ObsWebSocket.InvokeRequest("CreateSourceFilter", new JObject
                    {
                        ["sourceName"] = sourceName, ["filterName"] = "ReplayKit Overlay Opacity", ["filterKind"] = "color_filter",
                        ["filterSettings"] = OverlayOpacityFilterSettingsLive(opacityPercent, true, settings),
                    }, 3000);
                    if (!createFallback.Ok) return new JObject { ["ok"] = false, ["message"] = create.Message };
                    legacyPercent = true;
                }
                existingFilterName = "ReplayKit Overlay Opacity";
                var createdList = GetSourceFilterListLive(sourceName);
                if (createdList["ok"]?.Value<bool>() == true)
                {
                    var createdFilter = GetOverlayOpacityLiveFilterInfo(createdList["filters"] as JArray);
                    if (createdFilter["found"]?.Value<bool>() == true)
                    {
                        legacyPercent = OverlayOpacityFilterUsesLegacyPercent(createdFilter["kind"]?.Value<string>() ?? "", createdFilter["settings"] as JObject);
                        if (legacyPercent)
                        {
                            var setCreated = ObsWebSocket.InvokeRequest("SetSourceFilterSettings", new JObject
                            {
                                ["sourceName"] = sourceName, ["filterName"] = existingFilterName,
                                ["filterSettings"] = OverlayOpacityFilterSettingsLive(opacityPercent, true, settings), ["overlay"] = true,
                            }, 3000);
                            if (!setCreated.Ok) return new JObject { ["ok"] = false, ["message"] = setCreated.Message };
                        }
                    }
                }
            }
            var enable = ObsWebSocket.InvokeRequest("SetSourceFilterEnabled", new JObject { ["sourceName"] = sourceName, ["filterName"] = existingFilterName, ["filterEnabled"] = true }, 3000);
            return enable.Ok ? new JObject { ["ok"] = true, ["message"] = "" } : new JObject { ["ok"] = false, ["message"] = enable.Message };
        }

        // -- display capture cursor: intentionally forced true here even though ReplayKitSetup's install-time
        // template sets it false -- a deliberate product change made after the Setup template was last touched
        // (users asked for the cursor to stay visible in Display Capture); this file, not Transform.cs, is the
        // source of truth for this one value.

        private static void SetDisplayCaptureCursorSourceJson(JObject source)
        {
            string sourceName = source["name"]?.Value<string>() ?? "";
            string sourceId = source["id"]?.Value<string>() ?? "";
            if (sourceName != "Display Capture" && sourceId != "monitor_capture") return;
            var sourceSettings = source["settings"] as JObject;
            if (sourceSettings == null) { sourceSettings = new JObject(); source["settings"] = sourceSettings; }
            sourceSettings["capture_cursor"] = true;
        }

        private static JObject SetDisplayCaptureCursorLive()
        {
            var set = ObsWebSocket.InvokeRequest("SetInputSettings", new JObject
            {
                ["inputName"] = "Display Capture",
                ["inputSettings"] = new JObject { ["capture_cursor"] = true },
                ["overlay"] = true,
            }, 3000);
            return set.Ok ? new JObject { ["ok"] = true, ["message"] = "" } : new JObject { ["ok"] = false, ["message"] = set.Message };
        }

        // -- game-window enumeration for the screenshare picker: a small, self-contained p/invoke surface
        // (distinct from Native.cs/ProjectorNative.cs), matching the ps original's own lazily-declared
        // ReplayKit.WindowApi Add-Type block.

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const int WS_CHILD = 0x40000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int DWMWA_CLOAKED = 14;

        private static string GetWindowTitle(IntPtr hwnd)
        {
            int length = GetWindowTextLength(hwnd);
            if (length <= 0) return "";
            if (length > 512) length = 512;
            var builder = new StringBuilder(length + 1);
            GetWindowText(hwnd, builder, builder.Capacity);
            return builder.ToString().Trim();
        }

        private static string GetWindowClass(IntPtr hwnd)
        {
            var builder = new StringBuilder(256);
            GetClassName(hwnd, builder, builder.Capacity);
            return builder.ToString().Trim();
        }

        private static bool IsWindowCloaked(IntPtr hwnd)
        {
            try
            {
                int hr = DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, 4);
                return hr == 0 && cloaked != 0;
            }
            catch (EntryPointNotFoundException) { return false; }
        }

        // enumerates visible top-level windows for the "Auto Game List" picker: filters out child/tool windows,
        // cloaked (uwp-suspended) windows, and a hardcoded blocklist of system/obs/discord executables, then
        // encodes each candidate as a title:class:exe token matching obs's own Window Capture "window" setting
        // format. capped at 80 candidates and sorted by label; a previously-saved selection that fell out of the
        // live window list (app closed, etc.) is still surfaced as the first entry so the dropdown does not
        // silently lose the user's choice.
        public static List<JObject> GetGameWindowCandidates(string savedWindow = "")
        {
            var candidates = new List<JObject>();
            var seen = new HashSet<string>();
            var processCache = new Dictionary<int, string>();
            try
            {
                IntPtr shellWindow = GetShellWindow();
                EnumWindows((hwnd, _) =>
                {
                    if (hwnd == shellWindow) return true;
                    if (!IsWindowVisible(hwnd)) return true;
                    if (IsWindowCloaked(hwnd)) return true;

                    int style = GetWindowLong(hwnd, GWL_STYLE);
                    int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                    if ((style & WS_CHILD) != 0) return true;
                    if ((exStyle & WS_EX_TOOLWINDOW) != 0) return true;

                    string title = GetWindowTitle(hwnd);
                    if (string.IsNullOrWhiteSpace(title)) return true;
                    string className = GetWindowClass(hwnd);
                    if (string.IsNullOrWhiteSpace(className)) return true;

                    GetWindowThreadProcessId(hwnd, out uint pidRaw);
                    int processId = (int)pidRaw;
                    if (processId <= 0) return true;
                    if (!processCache.TryGetValue(processId, out string exe))
                    {
                        try
                        {
                            using (var proc = Process.GetProcessById(processId))
                            {
                                exe = proc.ProcessName;
                                if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe += ".exe";
                            }
                        }
                        catch (ArgumentException) { exe = ""; }
                        catch (InvalidOperationException) { exe = ""; }
                        processCache[processId] = exe;
                    }
                    if (string.IsNullOrWhiteSpace(exe)) return true;
                    if (TestBlockedGameWindowExe(exe)) return true;

                    if (title.Length > 160) title = title.Substring(0, 160);
                    if (className.Length > 120) className = className.Substring(0, 120);
                    if (exe.Length > 96) exe = exe.Substring(0, 96);
                    string token = NewObsWindowToken(title, className, exe);
                    if (!seen.Add(token)) return true;

                    candidates.Add(new JObject
                    {
                        ["value"] = token, ["token"] = token, ["label"] = "[" + exe + "]: " + title,
                        ["blurb"] = className, ["title"] = title, ["className"] = className, ["exeName"] = exe,
                    });
                    return candidates.Count < 80;
                }, IntPtr.Zero);
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                Log.Write("Game window enumeration unavailable: " + ex.Message);
                return new List<JObject>();
            }

            var items = candidates.OrderBy(c => c["label"]?.Value<string>() ?? "", StringComparer.CurrentCulture).ToList();
            if (!string.IsNullOrWhiteSpace(savedWindow))
            {
                bool hasSaved = items.Any(item => item["value"]?.Value<string>() == savedWindow);
                if (!hasSaved)
                {
                    try
                    {
                        var saved = ConvertFromObsWindowToken(savedWindow);
                        items.Insert(0, new JObject
                        {
                            ["value"] = saved["token"], ["token"] = saved["token"], ["label"] = saved["label"],
                            ["blurb"] = "Saved selection", ["title"] = saved["title"], ["className"] = saved["className"], ["exeName"] = saved["exeName"],
                        });
                    }
                    catch (InvalidOperationException)
                    {
                        items.Insert(0, new JObject { ["value"] = savedWindow, ["label"] = GetWindowLabelFromToken(savedWindow), ["blurb"] = "Saved selection" });
                    }
                }
            }
            return items;
        }

        // applies the selected screenshare capture mode (desktop/hybrid_auto/game_auto/game_window) live: ensures
        // all three capture sources exist (creating Display/Game Capture if a user ever deleted one by hand or a
        // scene got recreated bare -- do not regress this to a bare lookup-and-fail), toggles their scene-item
        // visibility, and repositions Window Capture directly under Display Capture in scene-item order.
        private static JObject ApplyScreenshareCaptureLive(JObject settings, JObject preset = null)
        {
            var warnings = new List<string>();
            var applied = new List<string>();
            string mode = settings["screenshareCaptureMode"]?.Value<string>() ?? "";
            bool useDesktop = mode == "desktop" || mode == "hybrid_auto";
            bool useGameCapture = mode == "game_auto";
            bool useWindowCapture = mode == "game_window";

            var scene = GetSceneName();
            if (scene["ok"]?.Value<bool>() != true)
                return new JObject { ["ok"] = false, ["applied"] = new JArray(applied), ["warnings"] = new JArray("Screenshare capture was saved, but OBS scene lookup failed: " + scene["message"]) };
            string sceneName = scene["name"]?.Value<string>();
            var itemsResult = GetSceneItems(sceneName);
            if (itemsResult["ok"]?.Value<bool>() != true)
                return new JObject { ["ok"] = false, ["applied"] = new JArray(applied), ["warnings"] = new JArray("Screenshare capture was saved, but OBS scene items could not be read: " + itemsResult["message"]) };
            var items = itemsResult["items"] as JArray;

            var gameInputSettings = GetGameCaptureInputSettings(settings);
            var displayEnsure = EnsureInputSceneItem(sceneName, "Display Capture", "monitor_capture", new JObject(), useDesktop);
            if (displayEnsure["ok"]?.Value<bool>() != true) warnings.Add("Could not prepare Display Capture: " + displayEnsure["message"]);
            var display = displayEnsure["ok"]?.Value<bool>() == true ? (JObject)displayEnsure["item"] : FindSceneItem(items, "Display Capture");
            var gameEnsure = EnsureInputSceneItem(sceneName, "Game Capture", "game_capture", gameInputSettings, useGameCapture);
            if (gameEnsure["ok"]?.Value<bool>() != true) warnings.Add("Could not prepare Game Capture: " + gameEnsure["message"]);
            var game = gameEnsure["ok"]?.Value<bool>() == true ? (JObject)gameEnsure["item"] : FindSceneItem(items, "Game Capture");

            var windowSettings = GetWindowCaptureInputSettings(settings);
            var windowEnsure = EnsureInputSceneItem(sceneName, "Window Capture", "window_capture", windowSettings, useWindowCapture);
            if (windowEnsure["ok"]?.Value<bool>() != true) warnings.Add("Could not prepare Window Capture: " + windowEnsure["message"]);
            itemsResult = GetSceneItems(sceneName);
            if (itemsResult["ok"]?.Value<bool>() == true) items = itemsResult["items"] as JArray;
            var window = FindSceneItem(items, "Window Capture");

            if (!useDesktop)
            {
                var hideDesktop = SetSceneItemEnabled(sceneName, display, false);
                if (hideDesktop["ok"]?.Value<bool>() != true) warnings.Add("Could not hide Display Capture: " + hideDesktop["message"]);
            }

            var cursorResult = SetDisplayCaptureCursorLive();
            if (cursorResult["ok"]?.Value<bool>() != true) warnings.Add("Could not enable Display Capture cursor: " + cursorResult["message"]);

            var setGame = ObsWebSocket.InvokeRequest("SetInputSettings", new JObject { ["inputName"] = "Game Capture", ["inputSettings"] = gameInputSettings, ["overlay"] = true }, 3000);
            if (!setGame.Ok) warnings.Add("Could not update Game Capture settings: " + setGame.Message);

            var setWindow = ObsWebSocket.InvokeRequest("SetInputSettings", new JObject { ["inputName"] = "Window Capture", ["inputSettings"] = windowSettings, ["overlay"] = true }, 3000);
            if (!setWindow.Ok) warnings.Add("Could not update Window Capture settings: " + setWindow.Message);

            if (useWindowCapture && string.IsNullOrWhiteSpace(settings["screenshareGameWindow"]?.Value<string>()))
                warnings.Add("Specific game capture was selected without a game window.");

            var enableGame = SetSceneItemEnabled(sceneName, game, useGameCapture);
            if (enableGame["ok"]?.Value<bool>() != true) warnings.Add("Could not toggle Game Capture: " + enableGame["message"]);
            var enableWindow = SetSceneItemEnabled(sceneName, window, useWindowCapture);
            if (enableWindow["ok"]?.Value<bool>() != true) warnings.Add("Could not toggle Window Capture: " + enableWindow["message"]);
            if (useDesktop)
            {
                var enableDisplay = SetSceneItemEnabled(sceneName, display, true);
                if (enableDisplay["ok"]?.Value<bool>() != true) warnings.Add("Could not show Display Capture: " + enableDisplay["message"]);
            }

            var order = SetWindowCaptureSceneOrder(sceneName, items);
            if (order["ok"]?.Value<bool>() != true) warnings.Add("Could not position Window Capture under overlays: " + order["message"]);

            if (preset != null)
            {
                foreach (var captureName in new[] { "Display Capture", "Window Capture", "Game Capture" })
                {
                    var capture = FindSceneItem(items, captureName);
                    var captureTransform = MainCaptureTransform(captureName, preset);
                    var captureResult = SetSceneItemTransform(sceneName, capture, captureTransform);
                    if (captureResult["ok"]?.Value<bool>() != true) warnings.Add("Could not fit " + captureName + " to canvas: " + captureResult["message"]);
                }
            }

            if (useDesktop) applied.Add("desktop capture source");
            else if (useWindowCapture) applied.Add("window capture source");
            else applied.Add("game capture source");
            return new JObject { ["ok"] = warnings.Count == 0, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };
        }

        private static void RemoveJsonListWhere(JArray list, Func<JObject, bool> predicate)
        {
            if (list == null) return;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (predicate((JObject)list[i])) list.RemoveAt(i);
            }
        }

        // full read-modify-write of the live scene collection file: locates or creates the Bongo Cat, WASD/Mouse,
        // Group, and Window Capture sources, applies motion blur and cursor settings across all capture sources,
        // then walks the scene-kind source's items array rewriting/creating/removing each scene item to match the
        // selected overlay style. this is the live-file-edit twin of ReplayKitSetup's Transform.ApplyScenesJson,
        // except that function only ever handles its own known bundled-template shape once, while this must be
        // defensive against whatever a real, possibly years-old, possibly hand-edited live scene collection
        // currently contains -- matching sources by uuid OR by legacy name/id fallback throughout.
        private static JObject SetOverlaySceneFile(JObject settings, JObject preset)
        {
            try
            {
                string path = GetSceneCollectionPath();
                if (!File.Exists(path)) return new JObject { ["ok"] = false, ["message"] = "OBS scene collection file was not found." };
                var data = JObject.Parse(File.ReadAllText(path));
                var sources = data["sources"] as JArray;
                if (sources == null) throw new InvalidOperationException("OBS scene collection has no sources list.");
                // do NOT self-assign data["sources"] / data["groups"] here -- in the bundled Newtonsoft that detaches
                // the array from `data`, so every edit below lands on an orphan and the file is written from the
                // untouched original (symptom: "switched overlay, scene still shows the old one"). they are
                // re-attached once, right before serialize.
                var groups = data["groups"] as JArray ?? new JArray();

                string overlayStyle = settings["overlayStyle"]?.Value<string>() ?? "";
                bool useInputOverlay = overlayStyle == "input_overlay";
                bool useBongo = overlayStyle == "bongo_cat";
                bool useMotionBlur = settings["motionBlurEnabled"]?.Value<bool>() ?? false;
                double motionBlurStrength = MotionBlurStrength(settings);
                var inputSources = new Dictionary<string, InputOverlaySourceRef>();
                var inputSourceNames = new[] { "WASD Overlay", "Mouse Overlay" };
                JObject inputGroup = null;
                foreach (var group in groups)
                {
                    if (group["name"]?.Value<string>() == "Group") { inputGroup = (JObject)group; break; }
                }
                string inputGroupUuid = inputGroup?["uuid"]?.Value<string>() ?? "";
                if (string.IsNullOrWhiteSpace(inputGroupUuid)) inputGroupUuid = Guid.NewGuid().ToString();

                var existingInputUuids = new HashSet<string>();
                foreach (var source in sources)
                {
                    string name = source["name"]?.Value<string>() ?? "";
                    if (inputSourceNames.Contains(name))
                    {
                        string uuid = source["uuid"]?.Value<string>() ?? "";
                        if (!string.IsNullOrWhiteSpace(uuid)) existingInputUuids.Add(uuid);
                    }
                }

                JObject bongoSource = null;
                foreach (var source in sources)
                {
                    if (source["name"]?.Value<string>() == "Bongo Cat Overlay") { bongoSource = (JObject)source; break; }
                }
                if (bongoSource == null)
                {
                    foreach (var source in sources)
                    {
                        if (source["id"]?.Value<string>() == "bongobs-cat") { bongoSource = (JObject)source; break; }
                    }
                }
                string bongoUuid = bongoSource?["uuid"]?.Value<string>() ?? "";
                JObject windowCaptureSource = null;
                foreach (var source in sources)
                {
                    string name = source["name"]?.Value<string>() ?? "";
                    string uuid = source["uuid"]?.Value<string>() ?? "";
                    if (name == "Window Capture" || uuid == "edb2d9d4-7b53-4f3a-a760-61cd03ce9b6c") { windowCaptureSource = (JObject)source; break; }
                }
                string windowCaptureUuid = windowCaptureSource?["uuid"]?.Value<string>() ?? "";
                if (string.IsNullOrWhiteSpace(windowCaptureUuid)) windowCaptureUuid = "edb2d9d4-7b53-4f3a-a760-61cd03ce9b6c";

                if (useInputOverlay)
                {
                    var inputSpecs = new[]
                    {
                        new { name = "WASD Overlay", image = FindOverlayAsset("wasd\\wasd.png"), layout = FindOverlayAsset("wasd\\wasd-minimal.json") },
                        new { name = "Mouse Overlay", image = FindOverlayAsset("mouse\\mouse.png"), layout = FindOverlayAsset("mouse\\mouse-no-movement.json") },
                    };
                    foreach (var spec in inputSpecs)
                    {
                        JObject inputSource = null;
                        foreach (var source in sources)
                        {
                            if (source["name"]?.Value<string>() == spec.name) { inputSource = (JObject)source; break; }
                        }
                        string inputUuid = inputSource?["uuid"]?.Value<string>() ?? "";
                        if (string.IsNullOrWhiteSpace(inputUuid)) inputUuid = Guid.NewGuid().ToString();
                        if (inputSource == null)
                        {
                            inputSource = NewInputOverlaySourceJson(spec.name, inputUuid, spec.image, spec.layout);
                            sources.Add(inputSource);
                        }
                        else
                        {
                            SetInputOverlaySourceJson(inputSource, spec.name, inputUuid, spec.image, spec.layout);
                        }
                        SetOverlayOpacitySourceJson(inputSource, settings);
                        inputSources[spec.name] = new InputOverlaySourceRef { Uuid = inputUuid };
                    }
                }
                else
                {
                    RemoveJsonListWhere(sources, source => inputSourceNames.Contains(source["name"]?.Value<string>() ?? ""));
                    RemoveJsonListWhere(groups, group => group["name"]?.Value<string>() == "Group" || group["uuid"]?.Value<string>() == inputGroupUuid);
                }

                if (useBongo && bongoSource == null)
                {
                    bongoSource = NewBongoSourceJson(Guid.NewGuid().ToString());
                    sources.Add(bongoSource);
                }
                if (useBongo)
                {
                    bongoUuid = bongoSource["uuid"]?.Value<string>() ?? "";
                    if (string.IsNullOrWhiteSpace(bongoUuid)) bongoUuid = Guid.NewGuid().ToString();
                    SetBongoSourceJson(bongoSource, bongoUuid);
                    SetOverlayOpacitySourceJson(bongoSource, settings);
                }
                else
                {
                    string bongoUuidCapture = bongoUuid;
                    RemoveJsonListWhere(sources, source =>
                        source["name"]?.Value<string>() == "Bongo Cat Overlay" ||
                        source["id"]?.Value<string>() == "bongobs-cat" ||
                        (!string.IsNullOrWhiteSpace(bongoUuidCapture) && source["uuid"]?.Value<string>() == bongoUuidCapture));
                }
                if (windowCaptureSource == null)
                {
                    windowCaptureSource = NewWindowCaptureSourceJson(windowCaptureUuid, settings);
                    sources.Add(windowCaptureSource);
                }
                else
                {
                    SetWindowCaptureSourceJson(windowCaptureSource, windowCaptureUuid, settings);
                }

                foreach (var sourceToken in sources)
                {
                    var source = (JObject)sourceToken;
                    string sourceName = source["name"]?.Value<string>() ?? "";
                    SetDisplayCaptureCursorSourceJson(source);
                    if (string.IsNullOrWhiteSpace(MotionBlurFilterUuid(sourceName))) continue;
                    if (useMotionBlur) SetMotionBlurSourceJson(source, true, motionBlurStrength);
                    else RemoveMotionBlurSourceJson(source);
                }

                foreach (var sourceToken in sources)
                {
                    var source = (JObject)sourceToken;
                    if (source["id"]?.Value<string>() != "scene") continue;
                    var sceneSettings = source["settings"] as JObject;
                    if (sceneSettings == null) continue;
                    var items = sceneSettings["items"] as JArray;
                    if (items == null) continue;
                    // same detach hazard as data["sources"] above -- mutate `items` in place, re-attach below.
                    if (!useInputOverlay)
                    {
                        RemoveJsonListWhere(items, item =>
                        {
                            string name = item["name"]?.Value<string>() ?? "";
                            string sourceUuid = item["source_uuid"]?.Value<string>() ?? "";
                            return inputSourceNames.Contains(name) || name == "Group" || sourceUuid == inputGroupUuid || existingInputUuids.Contains(sourceUuid);
                        });
                    }
                    if (!useBongo)
                    {
                        string bongoUuidCapture = bongoUuid;
                        RemoveJsonListWhere(items, item =>
                        {
                            string name = item["name"]?.Value<string>() ?? "";
                            string sourceUuid = item["source_uuid"]?.Value<string>() ?? "";
                            return name == "Bongo Cat Overlay" || (!string.IsNullOrWhiteSpace(bongoUuidCapture) && sourceUuid == bongoUuidCapture);
                        });
                    }

                    bool foundBongoItem = false;
                    var foundInputItems = new Dictionary<string, bool> { ["WASD Overlay"] = false, ["Mouse Overlay"] = false };
                    bool foundInputGroupItem = false;
                    bool foundWindowCaptureItem = false;
                    bool windowCaptureVisible = settings["screenshareCaptureMode"]?.Value<string>() == "game_window";
                    foreach (var itemToken in items)
                    {
                        var item = (JObject)itemToken;
                        string name = item["name"]?.Value<string>() ?? "";
                        string sourceUuid = item["source_uuid"]?.Value<string>() ?? "";
                        if (name == "Display Capture" || name == "Game Capture")
                        {
                            SetMainCaptureSceneItemJson(item, name, preset);
                        }
                        else if (name == "Window Capture" || sourceUuid == windowCaptureUuid)
                        {
                            SetWindowCaptureSceneItemJson(item, windowCaptureUuid, preset, windowCaptureVisible);
                            foundWindowCaptureItem = true;
                        }
                        else if (name == "WASD Overlay" || name == "Mouse Overlay")
                        {
                            string inputUuid = "";
                            if (inputSources.TryGetValue(name, out var srcRef)) inputUuid = srcRef.Uuid;
                            if (string.IsNullOrWhiteSpace(inputUuid)) inputUuid = sourceUuid;
                            SetInputOverlaySceneItemJson(item, name, preset, useInputOverlay, inputUuid, true, settings);
                            foundInputItems[name] = true;
                            if (inputSources.TryGetValue(name, out var srcRef2)) srcRef2.Id = item["id"]?.Value<int>() ?? 0;
                        }
                        else if (name == "Group" || sourceUuid == inputGroupUuid)
                        {
                            SetInputOverlayGroupSceneItemJson(item, inputGroupUuid, preset, useInputOverlay, settings);
                            foundInputGroupItem = true;
                        }
                        else if (name == "Bongo Cat Overlay" || sourceUuid == bongoUuid)
                        {
                            SetBongoSceneItemJson(item, bongoUuid, preset, useBongo, settings);
                            foundBongoItem = true;
                        }
                    }

                    if (!foundWindowCaptureItem)
                    {
                        var newItem = new JObject { ["id"] = GetNextJsonSceneItemId(items) };
                        SetWindowCaptureSceneItemJson(newItem, windowCaptureUuid, preset, windowCaptureVisible);
                        int insertAt = 0;
                        for (int i = 0; i < items.Count; i++)
                        {
                            if (items[i]["name"]?.Value<string>() == "Display Capture") { insertAt = i + 1; break; }
                        }
                        items.Insert(insertAt, newItem);
                        sceneSettings["id_counter"] = GetNextJsonSceneItemId(items);
                    }
                    MoveJsonSceneItemAfter(items, "Window Capture", "Display Capture");
                    if (useBongo && !foundBongoItem)
                    {
                        var newItem = new JObject { ["id"] = GetNextJsonSceneItemId(items) };
                        SetBongoSceneItemJson(newItem, bongoUuid, preset, true, settings);
                        items.Add(newItem);
                        sceneSettings["id_counter"] = GetNextJsonSceneItemId(items);
                    }
                    if (useInputOverlay)
                    {
                        foreach (var name in new[] { "WASD Overlay", "Mouse Overlay" })
                        {
                            if (foundInputItems[name]) continue;
                            if (!inputSources.TryGetValue(name, out var srcRef)) throw new InvalidOperationException("Input overlay source was not prepared for " + name + ".");
                            var newItem = new JObject { ["id"] = GetNextJsonSceneItemId(items) };
                            SetInputOverlaySceneItemJson(newItem, name, preset, true, srcRef.Uuid, true, settings);
                            srcRef.Id = newItem["id"]?.Value<int>() ?? 0;
                            items.Add(newItem);
                            sceneSettings["id_counter"] = GetNextJsonSceneItemId(items);
                        }
                        if (inputGroup == null)
                        {
                            inputGroup = new JObject();
                            groups.Add(inputGroup);
                        }
                        SetInputOverlayGroupSourceJson(inputGroup, inputGroupUuid, inputSources, preset);
                        if (!foundInputGroupItem)
                        {
                            var groupItem = new JObject { ["id"] = GetNextJsonSceneItemId(items) };
                            SetInputOverlayGroupSceneItemJson(groupItem, inputGroupUuid, preset, true, settings);
                            items.Add(groupItem);
                            sceneSettings["id_counter"] = GetNextJsonSceneItemId(items);
                        }
                    }
                    sceneSettings["items"] = items;
                }

                data["sources"] = sources;
                data["groups"] = groups;
                string json = data.ToString(Formatting.None);
                AppConfig.WriteUtf8(path, json);
                // the restart flow gracefully closes OBS so it can save window geometry -- but that exit-save also
                // writes OBS's stale in-memory scene collection back over the edit just made here. stage a copy with
                // a non-.json extension (OBS's scene scan ignores it); restart_obs.ps1 / ApplyPendingOverlayScene
                // move it onto the real file after OBS is fully gone, right before the relaunch.
                try { AppConfig.WriteUtf8(path + ".replaykit-pending", json); }
                catch (Exception ex) { Log.Write("SetOverlaySceneFile: could not stage pending scene: " + ex.Message); }
                return new JObject { ["ok"] = true, ["path"] = path };
            }
            catch (Exception ex)
            {
                return new JObject { ["ok"] = false, ["message"] = ex.Message };
            }
        }

        // -- obs ini config: close-warning + sleep override + windows startup shortcut --

        private static string GetObsConfigRoot()
        {
            string appData = Environment.GetEnvironmentVariable("APPDATA");
            if (!string.IsNullOrEmpty(appData)) return Path.Combine(appData, "obs-studio");
            return Path.Combine(AppConfig.GetUserProfile(), "AppData", "Roaming", "obs-studio");
        }

        private static string SetIniValue(string text, string section, string key, string value)
        {
            text = text ?? "";
            var lines = string.IsNullOrEmpty(text) ? new string[0] : Regex.Split(text, @"\r?\n");
            var outLines = new List<string>();
            bool inSection = false, sectionSeen = false, keySet = false;
            string keyPattern = @"^\s*" + Regex.Escape(key) + @"\s*=";

            foreach (var line in lines)
            {
                var sectionMatch = Regex.Match(line, @"^\s*\[(.+?)\]\s*$");
                if (sectionMatch.Success)
                {
                    if (inSection && !keySet) { outLines.Add(key + "=" + value); keySet = true; }
                    inSection = string.Equals(sectionMatch.Groups[1].Value, section, StringComparison.OrdinalIgnoreCase);
                    if (inSection) sectionSeen = true;
                    outLines.Add(line);
                    continue;
                }
                if (inSection && Regex.IsMatch(line, keyPattern))
                {
                    if (!keySet) { outLines.Add(key + "=" + value); keySet = true; }
                    continue;
                }
                outLines.Add(line);
            }

            if (inSection && !keySet) outLines.Add(key + "=" + value);
            if (!sectionSeen)
            {
                if (outLines.Count > 0 && outLines[outLines.Count - 1] != "") outLines.Add("");
                outLines.Add("[" + section + "]");
                outLines.Add(key + "=" + value);
            }

            return string.Join("\r\n", outLines).TrimEnd() + "\r\n";
        }

        private static JObject SetObsCloseWarningConfig(bool disabled)
        {
            try
            {
                string root = GetObsConfigRoot();
                if (!Directory.Exists(root)) Directory.CreateDirectory(root);
                string resolvedRoot = Path.GetFullPath(root);
                string appData = Environment.GetEnvironmentVariable("APPDATA");
                string expectedRoot = !string.IsNullOrEmpty(appData)
                    ? Path.GetFullPath(Path.Combine(appData, "obs-studio"))
                    : Path.GetFullPath(Path.Combine(AppConfig.GetUserProfile(), "AppData", "Roaming", "obs-studio"));
                if (!string.Equals(resolvedRoot, expectedRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("OBS config root did not resolve to the expected AppData path.");

                string confirmOnExit = disabled ? "false" : "true";
                var changed = new List<string>();
                var paths = new[] { Path.Combine(resolvedRoot, "user.ini"), Path.Combine(resolvedRoot, "global.ini") };
                foreach (var path in paths)
                {
                    string text = File.Exists(path) ? File.ReadAllText(path) : "";
                    string next = SetIniValue(text, "General", "ConfirmOnExit", confirmOnExit);
                    if (next != text)
                    {
                        AppConfig.WriteUtf8(path, next);
                        changed.Add(Path.GetFileName(path));
                    }
                }
                return new JObject { ["ok"] = true, ["confirmOnExit"] = confirmOnExit, ["changed"] = new JArray(changed) };
            }
            catch (Exception ex)
            {
                return new JObject { ["ok"] = false, ["message"] = ex.Message };
            }
        }

        // requestsoverride is scoped to obs64.exe only -- this never touches system-wide power policy, just
        // whether *this one process* is allowed to veto sleep/display-off while it's running, and only for the
        // duration the process holds the override (windows clears it automatically once obs exits).
        private static JObject InvokePowerCfg(string[] arguments)
        {
            var allowed = new HashSet<string> { "/requestsoverride", "PROCESS", "obs64.exe", "DISPLAY", "SYSTEM", "AWAYMODE" };
            foreach (var arg in arguments)
            {
                if (!allowed.Contains(arg)) return new JObject { ["ok"] = false, ["message"] = "Invalid power configuration request." };
            }
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powercfg.exe",
                    Arguments = string.Join(" ", arguments),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                var process = Process.Start(psi);
                if (process == null) return new JObject { ["ok"] = false, ["message"] = "powercfg could not start." };
                if (!process.WaitForExit(10000))
                {
                    try { process.Kill(); } catch (InvalidOperationException) { } catch (Win32Exception) { }
                    return new JObject { ["ok"] = false, ["message"] = "powercfg timed out." };
                }
                string output = (process.StandardOutput.ReadToEnd() + "\n" + process.StandardError.ReadToEnd()).Trim();
                if (process.ExitCode != 0)
                {
                    string line = output.Split('\n').Select(l => l.TrimEnd('\r')).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
                    if (string.IsNullOrWhiteSpace(line)) line = "powercfg exited " + process.ExitCode + ".";
                    return new JObject { ["ok"] = false, ["message"] = line, ["exitCode"] = process.ExitCode };
                }
                return new JObject { ["ok"] = true, ["message"] = output, ["exitCode"] = 0 };
            }
            catch (Exception ex)
            {
                return new JObject { ["ok"] = false, ["message"] = ex.Message };
            }
        }

        private static JObject SetSleepOverrideSetting(bool allowSleep)
        {
            var args = new List<string> { "/requestsoverride", "PROCESS", "obs64.exe" };
            if (allowSleep) args.AddRange(new[] { "DISPLAY", "SYSTEM", "AWAYMODE" });
            var result = InvokePowerCfg(args.ToArray());
            if (result["ok"]?.Value<bool>() != true) return new JObject { ["ok"] = false, ["message"] = result["message"] };
            string mode = allowSleep ? "allow_sleep" : "obs_default";
            return new JObject { ["ok"] = true, ["mode"] = mode };
        }

        private static JObject ApplySleepOverrideSetting(bool allowSleep)
        {
            var result = SetSleepOverrideSetting(allowSleep);
            if (result["ok"]?.Value<bool>() == true) return new JObject { ["applied"] = new JArray("Windows sleep"), ["warnings"] = new JArray() };
            return new JObject { ["applied"] = new JArray(), ["warnings"] = new JArray("Windows sleep setting was saved, but Windows rejected the change: " + result["message"]) };
        }

        private static string GetStartupShortcutPath()
        {
            string folder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            if (string.IsNullOrWhiteSpace(folder))
            {
                string appData = Environment.GetEnvironmentVariable("APPDATA");
                if (!string.IsNullOrEmpty(appData)) folder = Path.Combine(appData, "Microsoft", "Windows", "Start Menu", "Programs", "Startup");
            }
            if (string.IsNullOrWhiteSpace(folder)) throw new InvalidOperationException("Windows Startup folder was not found.");
            return Path.Combine(folder, "OBS ReplayKit.lnk");
        }

        private static string GetObsStartupTarget()
        {
            string obs = Server.State.ObsExe;
            if (string.IsNullOrEmpty(obs) || !File.Exists(obs))
            {
                string candidate = Constants.ResolveObsExe();
                if (File.Exists(candidate)) obs = candidate;
            }
            if (string.IsNullOrEmpty(obs) || !File.Exists(obs)) throw new InvalidOperationException("OBS executable was not found.");
            return obs;
        }

        private static void RemoveLegacyObsRunValue()
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true))
            {
                if (key?.GetValue("OBS ReplayKit") != null) key.DeleteValue("OBS ReplayKit", throwOnMissingValue: false);
            }
        }

        private static void NewObsStartupShortcut()
        {
            string shortcutPath = GetStartupShortcutPath();
            string obsPath = GetObsStartupTarget();
            string shortcutDir = Path.GetDirectoryName(shortcutPath);
            if (!Directory.Exists(shortcutDir)) Directory.CreateDirectory(shortcutDir);
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            try
            {
                shortcut.TargetPath = obsPath;
                shortcut.Arguments = "--background-color=ff272a33 --default-background-color=ff272a33 --disable-direct-composition-video-overlays";
                shortcut.WorkingDirectory = Path.GetDirectoryName(obsPath);
                shortcut.IconLocation = obsPath + ",0";
                shortcut.Description = "Start OBS ReplayKit when Windows signs in.";
                shortcut.Save();
            }
            finally
            {
                Marshal.ReleaseComObject(shortcut);
                Marshal.ReleaseComObject(shell);
            }
        }

        private static void RemoveObsStartupShortcut()
        {
            string shortcutPath = GetStartupShortcutPath();
            if (File.Exists(shortcutPath)) File.Delete(shortcutPath);
        }

        private static JObject SetObsStartupSetting(bool enabled)
        {
            try
            {
                RemoveLegacyObsRunValue();
                if (enabled) NewObsStartupShortcut();
                else RemoveObsStartupShortcut();
                RemoveLegacyObsRunValue();
                return new JObject { ["ok"] = true };
            }
            catch (Exception ex)
            {
                return new JObject { ["ok"] = false, ["message"] = ex.Message };
            }
        }

        // persists a dock-picked clip dir into the shared helper config json (the same file the lua bootstrap
        // and this process both read) -- psHelper is not written here since the c# port has no separate worker
        // script for it to point at.
        private static void UpdateHelperConfigClipDir(string clipDir)
        {
            AppConfig.LoadConfig();
            Server.State.Config["clipDir"] = clipDir;
            var obj = new JObject
            {
                ["port"] = Server.State.Config["port"]?.Value<int?>() ?? Constants.DEFAULT_PORT,
                ["scriptDir"] = AppConfig.GetScriptDir(),
                ["clipDir"] = clipDir,
                ["loggingEnabled"] = Server.State.LogEnabled,
            };
            AppConfig.WriteUtf8(Server.State.ConfigPath, obj.ToString(Formatting.Indented));
            Server.State.ConfigMTime = DateTime.MinValue;
            AppConfig.LoadConfig();
        }

        // -- obs profile parameters (basic.ini) + monitoring device --

        private static ObsWebSocketResult SetObsProfileParameterSafe(string category, string name, string value) =>
            ObsWebSocket.InvokeRequest("SetProfileParameter", new JObject { ["parameterCategory"] = category, ["parameterName"] = name, ["parameterValue"] = value }, 3000);

        private static ObsWebSocketResult GetObsProfileParameterSafe(string category, string name) =>
            ObsWebSocket.InvokeRequest("GetProfileParameter", new JObject { ["parameterCategory"] = category, ["parameterName"] = name }, 3000);

        private static JObject GetObsProfileParameterValue(string category, string name)
        {
            var result = GetObsProfileParameterSafe(category, name);
            if (!result.Ok) return new JObject { ["ok"] = false, ["value"] = "", ["message"] = result.Message };
            var value = result.Data?["parameterValue"];
            if (value == null || value.Type == JTokenType.Null) return new JObject { ["ok"] = false, ["value"] = "", ["message"] = "OBS did not return " + category + "." + name + "." };
            return new JObject { ["ok"] = true, ["value"] = value.Value<string>(), ["message"] = "" };
        }

        private static bool TestStringEqual(string actual, string expected) => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

        // obs does not always apply a profile-parameter write instantly -- read-current, write, verify, retry
        // with backoff up to maxAttempts before giving up.
        private static JObject SetObsMonitoringDevice(string deviceId, string deviceName, int maxAttempts = 6)
        {
            deviceId = (deviceId ?? "").Trim();
            deviceName = (deviceName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(deviceName))
                return new JObject { ["ok"] = false, ["applied"] = new JArray(), ["message"] = "OBS monitoring device id/name is missing." };
            if (deviceId.Length > 512 || deviceName.Length > 256 || Regex.IsMatch(deviceId, @"[\x00-\x1F]") || Regex.IsMatch(deviceName, @"[\x00-\x1F]"))
                return new JObject { ["ok"] = false, ["applied"] = new JArray(), ["message"] = "OBS monitoring device id/name is invalid." };

            int attempts = Math.Max(1, Math.Min(10, maxAttempts));
            string lastMessage = "";
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                var currentId = GetObsProfileParameterValue("Audio", "MonitoringDeviceId");
                var currentName = GetObsProfileParameterValue("Audio", "MonitoringDeviceName");
                if (currentId["ok"]?.Value<bool>() == true && currentName["ok"]?.Value<bool>() == true &&
                    TestStringEqual(currentId["value"]?.Value<string>(), deviceId) && TestStringEqual(currentName["value"]?.Value<string>(), deviceName))
                {
                    return new JObject { ["ok"] = true, ["applied"] = new JArray("OBS monitoring device already set"), ["message"] = "" };
                }

                var setId = SetObsProfileParameterSafe("Audio", "MonitoringDeviceId", deviceId);
                if (!setId.Ok)
                {
                    lastMessage = "MonitoringDeviceId rejected: " + setId.Message;
                    Thread.Sleep(Math.Min(1000, 150 * attempt));
                    continue;
                }
                var setName = SetObsProfileParameterSafe("Audio", "MonitoringDeviceName", deviceName);
                if (!setName.Ok)
                {
                    lastMessage = "MonitoringDeviceName rejected: " + setName.Message;
                    Thread.Sleep(Math.Min(1000, 150 * attempt));
                    continue;
                }

                Thread.Sleep(Math.Min(1000, 150 * attempt));
                var verifyId = GetObsProfileParameterValue("Audio", "MonitoringDeviceId");
                var verifyName = GetObsProfileParameterValue("Audio", "MonitoringDeviceName");
                if (verifyId["ok"]?.Value<bool>() == true && verifyName["ok"]?.Value<bool>() == true &&
                    TestStringEqual(verifyId["value"]?.Value<string>(), deviceId) && TestStringEqual(verifyName["value"]?.Value<string>(), deviceName))
                {
                    return new JObject { ["ok"] = true, ["applied"] = new JArray("OBS monitoring device set to " + deviceName), ["message"] = "" };
                }

                string actualId = verifyId["ok"]?.Value<bool>() == true ? verifyId["value"]?.Value<string>() : verifyId["message"]?.Value<string>();
                string actualName = verifyName["ok"]?.Value<bool>() == true ? verifyName["value"]?.Value<string>() : verifyName["message"]?.Value<string>();
                lastMessage = "OBS still reports monitoring device '" + actualName + "' (" + actualId + ").";
            }

            return new JObject { ["ok"] = false, ["applied"] = new JArray(), ["message"] = "Could not set OBS monitoring device to " + deviceName + " after " + attempts + " attempts. " + lastMessage };
        }

        // -- keybind <-> basic.ini hotkey json --

        private static JObject ConvertKeybindToObsBinding(JObject combo)
        {
            if (combo == null || string.IsNullOrWhiteSpace(combo["key"]?.Value<string>())) return null;
            var bind = new JObject();
            foreach (var mod in new[] { "control", "alt", "shift", "command" })
            {
                if (combo[mod]?.Value<bool>() == true) bind[mod] = true;
            }
            bind["key"] = combo["key"].Value<string>();
            return bind;
        }

        private static string ConvertClipKeybindToBasicIni(JObject combo)
        {
            var bind = ConvertKeybindToObsBinding(combo);
            return new JObject { ["ReplayBuffer.Save"] = bind == null ? new JArray() : new JArray(bind) }.ToString(Formatting.None);
        }

        private static string ConvertRecordingKeybindToBasicIni(JObject combo)
        {
            var bind = ConvertKeybindToObsBinding(combo);
            return new JObject { ["bindings"] = bind == null ? new JArray() : new JArray(bind) }.ToString(Formatting.None);
        }

        private static JObject ConvertRecordingBasicIniToKeybind(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new JObject();
            try
            {
                var data = JObject.Parse(json);
                var bindings = data["bindings"] as JArray;
                if (bindings == null || bindings.Count < 1 || bindings[0] == null || bindings[0].Type == JTokenType.Null) return new JObject();
                return NormalizeRecordingKeybind(bindings[0]);
            }
            catch (JsonException) { return new JObject(); }
        }

        private static JObject ConvertClipBasicIniToKeybind(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new JObject();
            try
            {
                var data = JObject.Parse(json);
                var bindings = data["ReplayBuffer.Save"] as JArray;
                if (bindings == null || bindings.Count < 1 || bindings[0] == null || bindings[0].Type == JTokenType.Null) return new JObject();
                return NormalizeClipKeybind(bindings[0]);
            }
            catch (JsonException) { return new JObject(); }
        }

        private static ObsWebSocketResult SetReplayBufferHotkeyJson(string json) => SetObsProfileParameterSafe("Hotkeys", "ReplayBuffer", json);

        private static JObject SetRecordingHotkeyPairJson(string startJson, string stopJson)
        {
            var errors = new List<string>();
            var names = new[] { "OBSBasic.StartRecording", "OBSBasic.StopRecording" };
            var jsons = new[] { startJson, stopJson };
            for (int i = 0; i < names.Length; i++)
            {
                var r = SetObsProfileParameterSafe("Hotkeys", names[i], jsons[i]);
                if (!r.Ok) errors.Add(names[i] + ": " + r.Message);
            }
            if (errors.Count > 0) return new JObject { ["ok"] = false, ["message"] = string.Join("; ", errors) };
            return new JObject { ["ok"] = true };
        }

        // -- canvas sizing + recording preset/encoder spec --

        private static int GetEvenDimension(double value)
        {
            int n = (int)Math.Round(value);
            n = Math.Max(2, Math.Min(4096, n));
            if (n % 2 != 0) n -= 1;
            return Math.Max(2, n);
        }

        private static JObject GetScaledEvenSize(int sourceWidth, int sourceHeight, int maxWidth, int maxHeight)
        {
            if (sourceWidth < 2 || sourceHeight < 2 || maxWidth < 2 || maxHeight < 2)
                return new JObject { ["width"] = 1920, ["height"] = 1080 };
            double scale = Math.Min(1.0, Math.Min(maxWidth / (double)sourceWidth, maxHeight / (double)sourceHeight));
            return new JObject { ["width"] = GetEvenDimension(sourceWidth * scale), ["height"] = GetEvenDimension(sourceHeight * scale) };
        }

        // canvas-aware default for recordingScaleMode: monitors above 1080p downscale to 1080 by default, 1080p and
        // below record native (nothing to downscale). only used when the settings file has no explicit value.
        private static string DefaultRecordingScaleMode()
        {
            try
            {
                var mon = GetPrimaryMonitorCanvasSize();
                if (mon["ok"]?.Value<bool>() == true && mon["height"].Value<int>() > 1080) return "downscale";
            }
            catch (Exception ex) { Log.Write("DefaultRecordingScaleMode: " + ex.Message); }
            return "native";
        }

        // OBS-style "Output (Scaled) Resolution" list derived from the real canvas -- same scale-factor steps OBS's
        // own Video settings use, so a 1440p canvas yields 2560x1440 / 2048x1152 / 1920x1080 / ... and a 4K canvas
        // yields its own set. value passed back is the height; ResolvePresetVideoSpec keeps the canvas aspect.
        private static JArray BuildDownscaleResolutionList()
        {
            var mon = GetPrimaryMonitorCanvasSize();
            var baseSize = GetScaledEvenSize(
                mon["ok"]?.Value<bool>() == true ? mon["width"].Value<int>() : 1920,
                mon["ok"]?.Value<bool>() == true ? mon["height"].Value<int>() : 1080, 4096, 4096);
            int bw = baseSize["width"].Value<int>(), bh = baseSize["height"].Value<int>();
            // exact same scale steps + rounding OBS's Video settings use: width truncated to a multiple of 4, height to a multiple of 2.
            double[] scales = { 1.0, 1.25, 1.0 / 0.75, 1.5, 1.0 / 0.6, 1.75, 2.0, 2.25, 2.5, 2.75, 3.0 };
            var list = new JArray();
            var seen = new HashSet<int>();
            foreach (var s in scales)
            {
                int cw = (int)(bw / s) & ~3;
                int ch = (int)(bh / s) & ~1;
                if (ch < 240 || !seen.Add(ch)) continue;
                list.Add(new JObject { ["label"] = cw + "x" + ch, ["height"] = ch });
            }
            return list;
        }

        private static JObject GetPrimaryMonitorCanvasSize()
        {
            try
            {
                var bounds = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                int width = bounds.Width, height = bounds.Height;
                if (width >= 320 && height >= 240) return new JObject { ["ok"] = true, ["width"] = width, ["height"] = height };
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is NullReferenceException)
            {
                Log.Write("Primary monitor size detection failed: " + ex.Message);
            }
            return new JObject { ["ok"] = false, ["width"] = 1920, ["height"] = 1080 };
        }

        // outputHeightCap of 8192 means "never downscale" (output == base); a real value scales the output down to that
        // height, aspect preserved from the canvas. scaleType is the OBS downscale filter, ignored by OBS when base==output.
        private static JObject ResolvePresetVideoSpec(int outputHeightCap, string scaleType, int fpsNumerator, int fpsDenominator)
        {
            var monitor = GetPrimaryMonitorCanvasSize();
            int sourceWidth = monitor["ok"]?.Value<bool>() == true ? monitor["width"].Value<int>() : 1920;
            int sourceHeight = monitor["ok"]?.Value<bool>() == true ? monitor["height"].Value<int>() : 1080;
            var baseSize = GetScaledEvenSize(sourceWidth, sourceHeight, 4096, 4096);
            var output = GetScaledEvenSize(baseSize["width"].Value<int>(), baseSize["height"].Value<int>(), 8192, outputHeightCap);

            return new JObject
            {
                ["video"] = new JObject
                {
                    ["baseWidth"] = baseSize["width"], ["baseHeight"] = baseSize["height"],
                    ["outputWidth"] = output["width"], ["outputHeight"] = output["height"],
                    ["fpsNumerator"] = fpsNumerator, ["fpsDenominator"] = fpsDenominator,
                },
                ["profile"] = new JArray(
                    new JArray("Output", "Mode", "Advanced"),
                    new JArray("AdvOut", "RecType", "Standard"),
                    new JArray("AdvOut", "RecTracks", "1"),
                    new JArray("Video", "BaseCX", baseSize["width"].Value<int>().ToString()),
                    new JArray("Video", "BaseCY", baseSize["height"].Value<int>().ToString()),
                    new JArray("Video", "OutputCX", output["width"].Value<int>().ToString()),
                    new JArray("Video", "OutputCY", output["height"].Value<int>().ToString()),
                    new JArray("Video", "FPSType", "2"),
                    new JArray("Video", "FPSNum", fpsNumerator.ToString()),
                    new JArray("Video", "FPSDen", fpsDenominator.ToString()),
                    new JArray("Video", "ScaleType", string.IsNullOrEmpty(scaleType) ? "lanczos" : scaleType)
                ),
                ["source"] = new JObject { ["width"] = sourceWidth, ["height"] = sourceHeight },
            };
        }

        private static JObject GetPresetSpec(string name, int fpsNumerator, int fpsDenominator, string scaleMode, int downscaleHeight, string downscaleFilter)
        {
            // performance stays capped low no matter the global scale mode -- the preset exists to keep load and file size down.
            // balanced/quality honor recordingScaleMode: native = no downscale (cap 8192), downscale = cap at downscaleHeight.
            int cap = name == "performance" ? 720 : (scaleMode == "downscale" ? downscaleHeight : 8192);
            string scaleType = string.IsNullOrEmpty(downscaleFilter) ? "lanczos" : downscaleFilter;
            int cqp;
            switch (name)
            {
                case "performance": cqp = 26; break;
                case "quality": cqp = 20; break;
                default: cqp = 22; break;
            }
            var videoSpec = ResolvePresetVideoSpec(cap, scaleType, fpsNumerator, fpsDenominator);
            return new JObject { ["cqp"] = cqp, ["video"] = videoSpec["video"], ["profile"] = videoSpec["profile"], ["source"] = videoSpec["source"] };
        }

        private static JObject GetPresetSpec(string name, JObject settings) => GetPresetSpec(name,
            settings?["fpsNumerator"]?.Value<int>() ?? settings?["fps"]?.Value<int>() ?? 60,
            settings?["fpsDenominator"]?.Value<int>() ?? 1,
            settings?["recordingScaleMode"]?.Value<string>() ?? "native",
            settings?["downscaleHeight"]?.Value<int>() ?? 1080,
            settings?["downscaleFilter"]?.Value<string>() ?? "lanczos");

        // vendor/generation candidate table, effort tuning, and cqp->icq/crf conversion live in Encoder.PickEncoder
        // (copied from ReplayKitSetup/Encoder.cs -- see its header comment) so both agree on the same tuning
        // tables; this just adapts that shared choice into the shape Write-ReplayKitRecordEncoderJson expects,
        // plus the "requested codec unavailable on this gpu" warning that PickEncoder itself has no concept of
        // (Setup never surfaces that warning to a user, since it has no way to ask one at install time).
        private static JObject GetEncoderSpec(JObject settings, JObject preset)
        {
            var gpu = Gpu.PrimaryGpu();
            string codecPreference = settings["codecPreference"]?.Value<string>() ?? "auto";
            int cqp = preset["cqp"]?.Value<int>() ?? 22;
            string compressionMode = settings["compressionMode"]?.Value<string>() ?? "balanced";
            var choice = Encoder.PickEncoder(gpu, codecPreference, cqp, compressionMode);

            var result = new JObject
            {
                ["id"] = choice.ObsEncoderId,
                ["codec"] = choice.Codec,
                ["label"] = choice.Label,
                ["settings"] = JObject.FromObject(choice.Settings),
            };
            string pref = (codecPreference ?? "").Trim().ToLowerInvariant();
            if (pref != "" && pref != "auto" && choice.Codec != pref)
                result["warning"] = "Requested codec '" + pref + "' is not supported by the detected GPU, so ReplayKit selected " + choice.Label + ".";
            return result;
        }

        private static string GetCurrentProfileDir()
        {
            string profilesRoot = Path.Combine(Environment.GetEnvironmentVariable("APPDATA") ?? "", "obs-studio", "basic", "profiles");
            string profileName = "Untitled";
            var profileList = ObsWebSocket.InvokeRequest("GetProfileList", null, 3000);
            if (profileList.Ok && !string.IsNullOrEmpty(profileList.Data?["currentProfileName"]?.Value<string>()))
                profileName = profileList.Data["currentProfileName"].Value<string>();
            string rootFull = Path.GetFullPath(profilesRoot).TrimEnd('\\');
            string profileDir = Path.GetFullPath(Path.Combine(profilesRoot, profileName));
            if (!profileDir.StartsWith(rootFull + "\\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("OBS profile path resolved outside the OBS profiles directory.");
            return profileDir;
        }

        private static JObject WriteRecordEncoderJson(JObject encoder)
        {
            try
            {
                string profileDir = GetCurrentProfileDir();
                if (!Directory.Exists(profileDir)) Directory.CreateDirectory(profileDir);
                string path = Path.Combine(profileDir, "recordEncoder.json");
                AppConfig.WriteUtf8(path, encoder["settings"].ToString(Formatting.None));
                return new JObject { ["ok"] = true, ["path"] = path };
            }
            catch (Exception ex)
            {
                return new JObject { ["ok"] = false, ["message"] = ex.Message };
            }
        }

        // the parallel live-websocket version of Set-OverlaySceneFile, used when obs must not be restarted:
        // toggle visibility, reposition, and (for bongo cat) recreate from scratch rather than merely toggling.
        private static JObject ApplyOverlayLive(JObject settings, JObject preset, bool recreateBongo = true)
        {
            var warnings = new List<string>();
            var scene = GetSceneName();
            if (scene["ok"]?.Value<bool>() != true)
                return new JObject { ["ok"] = false, ["warnings"] = new JArray("Overlay setting was saved, but OBS scene lookup failed: " + scene["message"]) };

            string sceneName = scene["name"]?.Value<string>();
            var itemsResult = GetSceneItems(sceneName);
            if (itemsResult["ok"]?.Value<bool>() != true)
                return new JObject { ["ok"] = false, ["warnings"] = new JArray("Overlay setting was saved, but OBS scene items could not be read: " + itemsResult["message"]) };
            var items = itemsResult["items"] as JArray;
            int opacity = OverlayOpacityValue(settings);
            string overlayStyle = settings["overlayStyle"]?.Value<string>() ?? "";

            foreach (var name in new[] { "WASD Overlay", "Mouse Overlay", "Group" })
            {
                var item = FindSceneItem(items, name);
                bool enabled = overlayStyle == "input_overlay";
                var r = SetSceneItemEnabled(sceneName, item, enabled);
                if (r["ok"]?.Value<bool>() != true) warnings.Add("Could not toggle " + name + ": " + r["message"]);
            }

            bool bongoEnabled = overlayStyle == "bongo_cat";
            var bongo = FindSceneItem(items, "Bongo Cat Overlay");
            // bongobs-cat (live2d) holds an "isLoad" + initialization state across hide/show that never gets reset by SetInputSettings or SetSceneItemEnabled toggling, so switching wasd/mouse -> bongo leaves the renderer producing no output until obs is restarted. only the plugin's create callback (vtubercreate -> initvtuber + updata) puts the model back into a known-good state, so when the user picks bongo cat we destroy any existing input and recreate from scratch -- heavier than a settings merge, but the only thing that survives the plugin's lifecycle bug (see the Bongobs-Cat-Plugin repo, VtuberFrameWork.cpp).
            if (bongoEnabled)
            {
                if (bongo != null && recreateBongo)
                {
                    var remove = ObsWebSocket.InvokeRequest("RemoveInput", new JObject { ["inputName"] = "Bongo Cat Overlay" }, 3000);
                    if (!remove.Ok) warnings.Add("Could not remove stale Bongo Cat overlay: " + remove.Message);
                    else bongo = null;
                }
                if (bongo == null)
                {
                    var bongoSettings = new JObject
                    {
                        ["Mode"] = "standard", ["width"] = 1280, ["height"] = 768, ["x"] = 0.0, ["y"] = 0.02, ["scale"] = 1.83,
                        ["delay"] = 1.0, ["delaytime"] = 1.0, ["random_motion"] = true, ["breath"] = true, ["eyeblink"] = true,
                        ["track"] = true, ["live2d"] = true, ["relative_mouse"] = true, ["mouse_horizontal_flip"] = true,
                        ["mouse_vertical_flip"] = true, ["mask"] = false,
                    };
                    var created = ObsWebSocket.InvokeRequest("CreateInput", new JObject
                    {
                        ["sceneName"] = sceneName, ["inputName"] = "Bongo Cat Overlay", ["inputKind"] = "bongobs-cat",
                        ["inputSettings"] = bongoSettings, ["sceneItemEnabled"] = true,
                    }, 5000);
                    if (created.Ok) bongo = new JObject { ["sceneItemId"] = created.Data?["sceneItemId"]?.Value<int>() ?? 0, ["sourceName"] = "Bongo Cat Overlay" };
                    else warnings.Add("Could not create Bongo Cat overlay: " + created.Message);
                }
                else
                {
                    var rBongo = SetSceneItemEnabled(sceneName, bongo, true);
                    if (rBongo["ok"]?.Value<bool>() != true) warnings.Add("Could not show Bongo Cat overlay: " + rBongo["message"]);
                }
            }
            else if (bongo != null)
            {
                var rBongo = SetSceneItemEnabled(sceneName, bongo, false);
                if (rBongo["ok"]?.Value<bool>() != true) warnings.Add("Could not hide Bongo Cat overlay: " + rBongo["message"]);
            }

            var cursorResult = SetDisplayCaptureCursorLive();
            if (cursorResult["ok"]?.Value<bool>() != true) warnings.Add("Could not enable Display Capture cursor: " + cursorResult["message"]);

            foreach (var captureName in new[] { "Display Capture", "Window Capture", "Game Capture" })
            {
                var capture = FindSceneItem(items, captureName);
                var captureTransform = MainCaptureTransform(captureName, preset);
                var captureResult = SetSceneItemTransform(sceneName, capture, captureTransform);
                if (captureResult["ok"]?.Value<bool>() != true) warnings.Add("Could not fit " + captureName + " to canvas: " + captureResult["message"]);
            }

            if (bongoEnabled && bongo != null)
            {
                var rTransform = SetSceneItemTransform(sceneName, bongo, BongoTransform(preset, settings));
                if (rTransform["ok"]?.Value<bool>() != true) warnings.Add("Could not position Bongo Cat overlay: " + rTransform["message"]);
                var rOpacity = ApplyOverlayOpacityLive("Bongo Cat Overlay", opacity, settings);
                if (rOpacity["ok"]?.Value<bool>() != true) warnings.Add("Could not set Bongo Cat overlay opacity: " + rOpacity["message"]);
            }

            if (overlayStyle == "input_overlay")
            {
                var group = FindSceneItem(items, "Group");
                if (group != null)
                {
                    var rGroupTransform = SetSceneItemTransform(sceneName, group, InputOverlayGroupTransform(preset, settings));
                    if (rGroupTransform["ok"]?.Value<bool>() != true) warnings.Add("Could not position input overlay group: " + rGroupTransform["message"]);
                }
                string wasdPng = FindOverlayAsset("wasd\\wasd.png");
                string wasdJson = FindOverlayAsset("wasd\\wasd-minimal.json");
                string mousePng = FindOverlayAsset("mouse\\mouse.png");
                string mouseJson = FindOverlayAsset("mouse\\mouse-no-movement.json");
                if (string.IsNullOrWhiteSpace(wasdPng) || string.IsNullOrWhiteSpace(wasdJson) || string.IsNullOrWhiteSpace(mousePng) || string.IsNullOrWhiteSpace(mouseJson))
                {
                    warnings.Add("Input overlay assets are missing from the ReplayKit install, so OBS could not switch that overlay live.");
                }
                else
                {
                    var entries = new[]
                    {
                        new { name = "WASD Overlay", image = wasdPng, layout = wasdJson },
                        new { name = "Mouse Overlay", image = mousePng, layout = mouseJson },
                    };
                    foreach (var entry in entries)
                    {
                        var created = EnsureInputSceneItem(sceneName, entry.name, "input-overlay", new JObject
                        {
                            ["io.input_source"] = "This computer", ["io.overlay_image"] = entry.image, ["io.layout_file"] = entry.layout,
                        }, true);
                        if (created["ok"]?.Value<bool>() != true) { warnings.Add("Could not create " + entry.name + ": " + created["message"]); continue; }
                        var transform = InputOverlayTransform(entry.name, preset, settings);
                        var rTransform = SetSceneItemTransform(sceneName, created["item"] as JObject, transform);
                        if (rTransform["ok"]?.Value<bool>() != true) warnings.Add("Could not position " + entry.name + ": " + rTransform["message"]);
                        var rOpacity = ApplyOverlayOpacityLive(entry.name, opacity, settings);
                        if (rOpacity["ok"]?.Value<bool>() != true) warnings.Add("Could not set " + entry.name + " opacity: " + rOpacity["message"]);
                    }
                }
            }

            return new JObject { ["ok"] = warnings.Count == 0, ["warnings"] = new JArray(warnings) };
        }

        // -- overlay candidate-name resolution + scale/opacity targeting: given the currently-selected overlay
        // style, works out which live scene item(s) a scale or opacity change should target -- preferring
        // whatever is actually visible right now over what the style nominally implies, so a live tweak lands on
        // the right source even mid-transition or if the scene is in an unexpected state.

        private static string[] GetOverlaySourceNames(string overlayStyle)
        {
            if (overlayStyle == "bongo_cat") return new[] { "Bongo Cat Overlay" };
            if (overlayStyle == "input_overlay") return new[] { "Group", "WASD Overlay", "Mouse Overlay" };
            return new string[0];
        }

        private static List<string> GetOverlayCandidateSourceNames(string overlayStyle)
        {
            var names = new List<string>();
            foreach (var name in GetOverlaySourceNames(overlayStyle))
            {
                if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name)) names.Add(name);
            }
            foreach (var style in new[] { "bongo_cat", "input_overlay" })
            {
                foreach (var name in GetOverlaySourceNames(style))
                {
                    if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name)) names.Add(name);
                }
            }
            return names;
        }

        private static void AddOverlayCandidateName(List<string> names, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            if (!names.Contains(name)) names.Add(name);
        }

        private static bool TestSceneItemVisible(JObject item)
        {
            if (item == null) return false;
            var enabled = item["sceneItemEnabled"];
            if (enabled == null || enabled.Type == JTokenType.Null) return true;
            return enabled.Value<bool>();
        }

        private static List<string> GetOrderedOverlayCandidateSourceNames(string overlayStyle)
        {
            var candidates = GetOverlayCandidateSourceNames(overlayStyle);
            var ordered = new List<string>();
            JArray items = new JArray();

            var scene = GetSceneName();
            if (scene["ok"]?.Value<bool>() == true)
            {
                var itemsResult = GetSceneItems(scene["name"]?.Value<string>());
                if (itemsResult["ok"]?.Value<bool>() == true) items = itemsResult["items"] as JArray ?? new JArray();
            }

            if (items.Count > 0)
            {
                foreach (var name in candidates)
                {
                    var item = FindSceneItem(items, name);
                    if (item != null && TestSceneItemVisible(item)) AddOverlayCandidateName(ordered, name);
                }
                foreach (var name in candidates)
                {
                    if (FindSceneItem(items, name) != null) AddOverlayCandidateName(ordered, name);
                }
            }
            foreach (var name in candidates) AddOverlayCandidateName(ordered, name);
            return ordered;
        }

        private static JObject GetLiveOverlayOpacityPercent(JObject settings)
        {
            foreach (var name in GetOrderedOverlayCandidateSourceNames(settings["overlayStyle"]?.Value<string>() ?? ""))
            {
                var filters = GetSourceFilterListLive(name);
                if (filters["ok"]?.Value<bool>() != true) continue;
                var match = GetOverlayOpacityLiveFilterInfo(filters["filters"] as JArray);
                if (match["found"]?.Value<bool>() != true) continue;
                var opacity = GetOverlayOpacityPercentFromFilterSettings(match["settings"] as JObject);
                if (opacity != null) return new JObject { ["ok"] = true, ["opacity"] = opacity.Value, ["sourceName"] = name, ["filterName"] = match["name"] };
            }
            return new JObject { ["ok"] = false, ["opacity"] = null, ["sourceName"] = "", ["filterName"] = "" };
        }

        private static List<string> GetExistingInputOverlayScaleTargets(JArray items, bool visibleOnly)
        {
            var group = FindSceneItem(items, "Group");
            if (group != null && (!visibleOnly || TestSceneItemVisible(group))) return new List<string> { "Group" };
            var targets = new List<string>();
            foreach (var name in new[] { "WASD Overlay", "Mouse Overlay" })
            {
                var item = FindSceneItem(items, name);
                if (item == null) continue;
                if (visibleOnly && !TestSceneItemVisible(item)) continue;
                targets.Add(name);
            }
            return targets;
        }

        private static List<string> GetOverlayScaleSceneItemNames(JArray items, string overlayStyle)
        {
            if (overlayStyle == "bongo_cat")
            {
                if (FindSceneItem(items, "Bongo Cat Overlay") != null) return new List<string> { "Bongo Cat Overlay" };
            }
            if (overlayStyle == "input_overlay")
            {
                var inputTargets = GetExistingInputOverlayScaleTargets(items, false);
                if (inputTargets.Count > 0) return inputTargets;
            }

            var bongo = FindSceneItem(items, "Bongo Cat Overlay");
            if (bongo != null && TestSceneItemVisible(bongo)) return new List<string> { "Bongo Cat Overlay" };
            var visibleInputTargets = GetExistingInputOverlayScaleTargets(items, true);
            if (visibleInputTargets.Count > 0) return visibleInputTargets;
            if (bongo != null) return new List<string> { "Bongo Cat Overlay" };
            return GetExistingInputOverlayScaleTargets(items, false);
        }

        private static List<string> GetOverlayOpacitySourceTargets(string overlayStyle)
        {
            var names = GetOrderedOverlayCandidateSourceNames(overlayStyle);
            if (names.Count == 0) return new List<string>();

            foreach (var name in names)
            {
                var filters = GetSourceFilterListLive(name);
                if (filters["ok"]?.Value<bool>() != true) continue;
                var match = GetOverlayOpacityLiveFilterInfo(filters["filters"] as JArray);
                if (match["found"]?.Value<bool>() == true) return new List<string> { name };
            }

            if (overlayStyle == "input_overlay")
            {
                if (TestSourceExistsForFilters("Group")) return new List<string> { "Group" };
                var fallback = new List<string>();
                foreach (var name in new[] { "WASD Overlay", "Mouse Overlay" })
                {
                    if (TestSourceExistsForFilters(name)) fallback.Add(name);
                }
                if (fallback.Count > 0) return fallback;
            }
            if (overlayStyle == "bongo_cat" && TestSourceExistsForFilters("Bongo Cat Overlay")) return new List<string> { "Bongo Cat Overlay" };
            foreach (var name in names)
            {
                if (TestSourceExistsForFilters(name)) return new List<string> { name };
            }
            return new List<string>();
        }

        private static JObject ApplyOverlayScaleLive(JObject previous, JObject settings)
        {
            var warnings = new List<string>();
            var applied = new List<string>();
            double previousScale = OverlayScaleFactor(previous);
            double nextScale = OverlayScaleFactor(settings);
            if (previousScale <= 0.0 || Math.Abs(nextScale - previousScale) < 0.0001)
                return new JObject { ["ok"] = true, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };

            var scene = GetSceneName();
            if (scene["ok"]?.Value<bool>() != true)
                return new JObject { ["ok"] = false, ["applied"] = new JArray(applied), ["warnings"] = new JArray("Overlay size was saved, but OBS scene lookup failed: " + scene["message"]) };
            string sceneName = scene["name"]?.Value<string>();
            var itemsResult = GetSceneItems(sceneName);
            if (itemsResult["ok"]?.Value<bool>() != true)
                return new JObject { ["ok"] = false, ["applied"] = new JArray(applied), ["warnings"] = new JArray("Overlay size was saved, but OBS scene items could not be read: " + itemsResult["message"]) };

            var preset = GetPresetSpec(settings["recordingPreset"]?.Value<string>() ?? "", settings);
            double scaleRatio = nextScale / previousScale;
            var items = itemsResult["items"] as JArray;
            foreach (var name in GetOverlayScaleSceneItemNames(items, settings["overlayStyle"]?.Value<string>() ?? ""))
            {
                var item = FindSceneItem(items, name);
                if (item == null) continue;
                var r = SetSceneItemScaledFromCurrent(sceneName, item, scaleRatio, preset);
                if (r["ok"]?.Value<bool>() == true) { if (r["skipped"]?.Value<bool>() != true) applied.Add(name + " size"); }
                else warnings.Add("Could not resize " + name + ": " + r["message"]);
            }
            return new JObject { ["ok"] = warnings.Count == 0, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };
        }

        // horizontal mirror of a scene-item transform, in place: negate scaleX and shift positionX by the SIGNED rendered width so the on-screen box is unchanged and the shift is its own inverse (flipping on then off lands back exactly). overlay items are align-top-left.
        private static JObject MirrorTransformInPlace(JObject current, string overlayStyle)
        {
            double px = GetDoubleValue(current, "positionX", 0.0);
            double sx = GetDoubleValue(current, "scaleX", 1.0);
            double renderedW = Math.Abs(GetDoubleValue(current, "width", 0.0));
            if (renderedW <= 0.0) renderedW = Math.Abs(GetDoubleValue(current, "sourceWidth", 0.0) * sx);
            if (renderedW <= 0.0) renderedW = (overlayStyle == "bongo_cat" ? 1280.0 : 628.0) * Math.Abs(sx);
            double dir = sx < 0.0 ? -1.0 : 1.0;
            return new JObject
            {
                ["positionX"] = px + renderedW * dir,
                ["positionY"] = GetDoubleValue(current, "positionY", 0.0),
                ["scaleX"] = -sx,
                ["scaleY"] = GetDoubleValue(current, "scaleY", 1.0),
            };
        }

        // toggle the horizontal flip live by mirroring the overlay's CURRENT scene-item transform in place -- never
        // repositions to the canonical corner, so a user who dragged the overlay somewhere keeps it there. Group for
        // input_overlay (WASD/Mouse ride along inside it), Bongo Cat Overlay for bongo.
        private static JObject ApplyOverlayFlipLive(JObject previous, JObject settings)
        {
            var warnings = new List<string>();
            var applied = new List<string>();
            if ((previous?["overlayFlipH"]?.Value<bool>() ?? false) == (settings?["overlayFlipH"]?.Value<bool>() ?? false))
                return new JObject { ["ok"] = true, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };

            var scene = GetSceneName();
            if (scene["ok"]?.Value<bool>() != true)
                return new JObject { ["ok"] = false, ["applied"] = new JArray(applied), ["warnings"] = new JArray("Overlay flip was saved, but OBS scene lookup failed: " + scene["message"]) };
            string sceneName = scene["name"]?.Value<string>();
            var items = GetSceneItems(sceneName)["items"] as JArray;
            string style = settings["overlayStyle"]?.Value<string>() ?? "";
            string itemName = style == "bongo_cat" ? "Bongo Cat Overlay" : style == "input_overlay" ? "Group" : null;
            if (itemName == null) return new JObject { ["ok"] = true, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };
            var item = FindSceneItem(items, itemName);
            if (item == null) return new JObject { ["ok"] = true, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };

            var cur = GetSceneItemTransformLive(sceneName, item);
            if (cur["ok"]?.Value<bool>() != true || cur["transform"] == null || cur["transform"].Type == JTokenType.Null)
                return new JObject { ["ok"] = false, ["applied"] = new JArray(applied), ["warnings"] = new JArray("Overlay flip was saved, but " + itemName + "'s current position could not be read: " + cur["message"]) };
            var r = SetSceneItemTransform(sceneName, item, MirrorTransformInPlace(cur["transform"] as JObject, style));
            if (r["ok"]?.Value<bool>() == true) applied.Add(itemName + " flip");
            else warnings.Add("Could not flip " + itemName + ": " + r["message"]);
            return new JObject { ["ok"] = warnings.Count == 0, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };
        }

        private static JObject ApplyOverlayOpacityForStyleLive(JObject settings)
        {
            var warnings = new List<string>();
            var applied = new List<string>();
            int opacity = OverlayOpacityValue(settings);
            foreach (var name in GetOverlayOpacitySourceTargets(settings["overlayStyle"]?.Value<string>() ?? ""))
            {
                var r = ApplyOverlayOpacityLive(name, opacity, settings);
                if (r["ok"]?.Value<bool>() == true) applied.Add(name + " opacity");
                else warnings.Add("Could not set " + name + " opacity: " + r["message"]);
            }
            return new JObject { ["ok"] = warnings.Count == 0, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };
        }

        private static JObject ApplyOverlayVisualSettingsLive(JObject previous, JObject settings)
        {
            var warnings = new List<string>();
            var applied = new List<string>();
            if (previous["overlayScale"]?.Value<int>() != settings["overlayScale"]?.Value<int>())
            {
                var scale = ApplyOverlayScaleLive(previous, settings);
                applied.AddRange((scale["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                warnings.AddRange((scale["warnings"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
            }
            if ((previous["overlayFlipH"]?.Value<bool>() ?? false) != (settings["overlayFlipH"]?.Value<bool>() ?? false))
            {
                var flip = ApplyOverlayFlipLive(previous, settings);
                applied.AddRange((flip["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                warnings.AddRange((flip["warnings"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
            }
            bool colorChanged = previous["overlayOpacity"]?.Value<int>() != settings["overlayOpacity"]?.Value<int>() ||
                previous["overlayHueShift"]?.Value<double>() != settings["overlayHueShift"]?.Value<double>() ||
                previous["overlayColorMultiply"]?.Value<string>() != settings["overlayColorMultiply"]?.Value<string>() ||
                previous["overlayColorAdd"]?.Value<string>() != settings["overlayColorAdd"]?.Value<string>();
            if (colorChanged)
            {
                var opacity = ApplyOverlayOpacityForStyleLive(settings);
                applied.AddRange((opacity["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                warnings.AddRange((opacity["warnings"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
            }
            return new JObject { ["ok"] = warnings.Count == 0, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };
        }

        // -- overlay live-preview: lets the settings dock preview overlay scale/opacity/color changes live (e.g.
        // while dragging a slider) without committing them to disk until the user releases or confirms, by
        // snapshotting the pre-preview transform and filter state once per session and restoring it on cancel.

        private static bool TestOverlayColorRequest(JObject incoming) =>
            incoming != null && (incoming["overlayOpacity"] != null || incoming["overlayHueShift"] != null || incoming["overlayColorMultiply"] != null || incoming["overlayColorAdd"] != null);

        // the in-memory preview snapshot mirrored to disk, so a session that dies mid-preview (obs crash / end-task before commit or cancel) can still be reverted on the next helper start instead of leaking the un-applied geometry into obss saved scene.
        private static string OverlayPreviewBaselinePath => Path.Combine(Constants.SCRATCH_DIR, "overlay_preview_baseline.json");

        private static void TryDeleteOverlayPreviewBaseline()
        {
            try { File.Delete(OverlayPreviewBaselinePath); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        // holds OverlayPreviewLock across the whole capture, including the obs-websocket round trips, not just
        // the null-check -- this only runs once per preview session, and a lost race here would mean two callers
        // each capture their own "baseline", whichever stores last silently winning as the revert target. worth
        // the extra lock-hold time given how rarely this is actually contended.
        private static JObject GetOverlayPreviewState(JObject settings)
        {
            if (Server.State.ReplaykitOverlayPreviewState != null) return Server.State.ReplaykitOverlayPreviewState;

            lock (Server.State.OverlayPreviewLock)
            {
                if (Server.State.ReplaykitOverlayPreviewState != null) return Server.State.ReplaykitOverlayPreviewState;

                var baselineSettings = (JObject)settings.DeepClone();
                var liveOpacity = GetLiveOverlayOpacityPercent(baselineSettings);
                if (liveOpacity["ok"]?.Value<bool>() == true && liveOpacity["opacity"]?.Type != JTokenType.Null)
                    baselineSettings["overlayOpacity"] = liveOpacity["opacity"];

                var state = new JObject
                {
                    ["settings"] = baselineSettings,
                    ["preset"] = GetPresetSpec(baselineSettings["recordingPreset"]?.Value<string>() ?? "", baselineSettings),
                    ["sceneName"] = "",
                    ["transforms"] = new JObject(),
                    ["opacityFilters"] = new JArray(),
                };

                var scene = GetSceneName();
                if (scene["ok"]?.Value<bool>() == true)
                {
                    state["sceneName"] = scene["name"];
                    var itemsResult = GetSceneItems(scene["name"]?.Value<string>());
                    if (itemsResult["ok"]?.Value<bool>() == true)
                    {
                        var items = itemsResult["items"] as JArray;
                        var transforms = (JObject)state["transforms"];
                        foreach (var name in GetOverlayScaleSceneItemNames(items, settings["overlayStyle"]?.Value<string>() ?? ""))
                        {
                            var item = FindSceneItem(items, name);
                            if (item == null) continue;
                            var current = GetSceneItemTransformLive(scene["name"]?.Value<string>(), item);
                            if (current["ok"]?.Value<bool>() == true && current["transform"] != null && current["transform"].Type != JTokenType.Null)
                                transforms[name] = new JObject { ["item"] = item, ["transform"] = current["transform"] };
                        }
                    }
                }

                var opacityFilters = new JArray();
                foreach (var name in GetOverlayOpacitySourceTargets(baselineSettings["overlayStyle"]?.Value<string>() ?? ""))
                {
                    var filters = GetSourceFilterListLive(name);
                    if (filters["ok"]?.Value<bool>() != true) continue;
                    var match = GetOverlayOpacityLiveFilterInfo(filters["filters"] as JArray);
                    if (match["found"]?.Value<bool>() == true)
                    {
                        opacityFilters.Add(new JObject
                        {
                            ["sourceName"] = name, ["found"] = true, ["filterName"] = match["name"],
                            ["settings"] = ((JObject)(match["settings"] ?? new JObject())).DeepClone(), ["enabled"] = match["enabled"],
                        });
                    }
                    else
                    {
                        opacityFilters.Add(new JObject { ["sourceName"] = name, ["found"] = false, ["filterName"] = "", ["settings"] = new JObject(), ["enabled"] = false });
                    }
                }
                state["opacityFilters"] = opacityFilters;

                Server.State.ReplaykitOverlayPreviewState = state;
                try { AppConfig.WriteUtf8(OverlayPreviewBaselinePath, state.ToString(Formatting.None)); }
                catch (Exception ex) { Log.Write("GetOverlayPreviewState: could not persist baseline: " + ex.Message); }
                return state;
            }
        }

        private static void ClearOverlayPreviewState()
        {
            lock (Server.State.OverlayPreviewLock) { Server.State.ReplaykitOverlayPreviewState = null; }
            TryDeleteOverlayPreviewBaseline();
        }

        private static long? GetOverlayPreviewRevision(JObject incoming)
        {
            var raw = incoming?["overlayPreviewRevision"];
            if (raw == null) return null;
            incoming.Remove("overlayPreviewRevision");
            long revision;
            try { revision = raw.Value<long>(); }
            catch (FormatException) { throw new InvalidOperationException("Invalid overlay preview revision."); }
            catch (InvalidCastException) { throw new InvalidOperationException("Invalid overlay preview revision."); }
            if (revision < 1) throw new InvalidOperationException("Invalid overlay preview revision.");
            return revision;
        }

        // live preview for the two geometry knobs. size is snapshot-relative (`scaleTouched` latch -> every tick
        // re-asserts scale(baseline)*ratio so a drag back to 100 restores, without compounding). flip mirrors the
        // item's CURRENT transform in place, only on a state transition (`mirrorApplied` tracks whether our preview
        // has the item mirrored) -- so it never yanks a user-repositioned overlay back to the canonical corner, and
        // the signed shift in MirrorTransformInPlace makes on->off land back exactly. size runs first so flip mirrors
        // the resized transform.
        private static JObject ApplyOverlayGeometryPreviewLive(JObject preview, JObject settings)
        {
            var warnings = new List<string>();
            var applied = new List<string>();
            var previewSettings = preview["settings"] as JObject;
            string sceneName = preview["sceneName"]?.Value<string>();
            var transforms = preview["transforms"] as JObject ?? new JObject();
            if (string.IsNullOrWhiteSpace(sceneName))
                return new JObject { ["ok"] = true, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };

            bool scaleChanged = previewSettings["overlayScale"]?.Value<int>() != settings["overlayScale"]?.Value<int>();
            bool scaleTouched = preview["scaleTouched"]?.Value<bool>() ?? false;
            if (scaleChanged) { preview["scaleTouched"] = true; scaleTouched = true; }

            bool wantMirror = (previewSettings["overlayFlipH"]?.Value<bool>() ?? false) != (settings["overlayFlipH"]?.Value<bool>() ?? false);
            bool mirrorApplied = preview["mirrorApplied"]?.Value<bool>() ?? false;
            bool flipTransition = wantMirror != mirrorApplied;

            if (!scaleTouched && !flipTransition)
                return new JObject { ["ok"] = true, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };

            double baseScale = OverlayScaleFactor(previewSettings);
            double nextScale = OverlayScaleFactor(settings);
            if (baseScale <= 0.0) baseScale = 1.0;
            double scaleRatio = nextScale / baseScale;
            var preset = preview["preset"] as JObject ?? GetPresetSpec(settings["recordingPreset"]?.Value<string>() ?? "", settings);
            string style = settings["overlayStyle"]?.Value<string>() ?? "";

            // size: snapshot-relative, over whatever items the snapshot captured
            if (scaleTouched)
            {
                foreach (var prop in transforms.Properties().ToList())
                {
                    var entry = (JObject)prop.Value;
                    var scaled = GetScaledTransformFromCurrent(entry["transform"], scaleRatio, preset);
                    if (mirrorApplied) // keep the current mirror while resizing; the flip step below toggles it if needed
                    {
                        double sw = GetDoubleValue(entry["transform"], "sourceWidth", 0.0);
                        if (sw <= 0.0) sw = style == "bongo_cat" ? 1280.0 : 628.0;
                        double sx = scaled["scaleX"].Value<double>();
                        scaled["positionX"] = scaled["positionX"].Value<double>() + sw * sx;
                        scaled["scaleX"] = -sx;
                    }
                    var rs = SetSceneItemTransform(sceneName, entry["item"] as JObject, scaled);
                    if (rs["ok"]?.Value<bool>() == true) { if (!flipTransition) applied.Add(prop.Name + " size"); }
                    else warnings.Add("Could not preview " + prop.Name + " size: " + rs["message"]);
                }
            }

            // flip: mirror the flip target's CURRENT transform in place -- looked up by style, independent of the
            // snapshot's transforms map (which can miss the item), so it never repositions to the canonical corner
            if (flipTransition)
            {
                string itemName = style == "bongo_cat" ? "Bongo Cat Overlay" : style == "input_overlay" ? "Group" : null;
                var item = itemName == null ? null : ((transforms[itemName] as JObject)?["item"] as JObject ?? FindSceneItem(GetSceneItems(sceneName)["items"] as JArray, itemName));
                if (item != null)
                {
                    var cur = GetSceneItemTransformLive(sceneName, item);
                    if (cur["ok"]?.Value<bool>() == true && cur["transform"] != null && cur["transform"].Type != JTokenType.Null)
                    {
                        // make sure the un-mirrored transform is the snapshot's revert target (adopt a manual reposition since the snapshot; add the entry if the initial capture missed it) so cancel un-flips cleanly.
                        if (!mirrorApplied && !scaleTouched)
                        {
                            if (transforms[itemName] is JObject snapEntry) snapEntry["transform"] = cur["transform"];
                            else transforms[itemName] = new JObject { ["item"] = item, ["transform"] = cur["transform"] };
                        }
                        var rf = SetSceneItemTransform(sceneName, item, MirrorTransformInPlace(cur["transform"] as JObject, style));
                        if (rf["ok"]?.Value<bool>() == true) applied.Add(itemName + " flip");
                        else warnings.Add("Could not preview " + itemName + " flip: " + rf["message"]);
                    }
                    else warnings.Add("Could not read " + itemName + " to flip: " + cur["message"]);
                }
                preview["mirrorApplied"] = wantMirror;
            }
            return new JObject { ["ok"] = warnings.Count == 0, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };
        }

        private static JObject RestoreOverlayPreviewLive(JObject preview)
        {
            var warnings = new List<string>();
            var applied = new List<string>();
            if (preview == null) return new JObject { ["ok"] = true, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };
            string sceneName = preview["sceneName"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(sceneName))
            {
                var transforms = preview["transforms"] as JObject ?? new JObject();
                foreach (var prop in transforms.Properties().ToList())
                {
                    var entry = (JObject)prop.Value;
                    var r = SetSceneItemTransform(sceneName, entry["item"] as JObject, entry["transform"] as JObject);
                    if (r["ok"]?.Value<bool>() == true) applied.Add(prop.Name + " size restored");
                    else warnings.Add("Could not restore " + prop.Name + ": " + r["message"]);
                }
            }
            bool restoredExactOpacity = false;
            var opacityFilters = preview["opacityFilters"] as JArray ?? new JArray();
            foreach (var entryToken in opacityFilters)
            {
                var entry = (JObject)entryToken;
                string sourceName = entry["sourceName"]?.Value<string>() ?? "";
                if (string.IsNullOrWhiteSpace(sourceName)) continue;
                if (entry["found"]?.Value<bool>() == true)
                {
                    string filterName = entry["filterName"]?.Value<string>() ?? "";
                    if (string.IsNullOrWhiteSpace(filterName)) continue;
                    var set = ObsWebSocket.InvokeRequest("SetSourceFilterSettings", new JObject
                    {
                        ["sourceName"] = sourceName, ["filterName"] = filterName, ["filterSettings"] = entry["settings"], ["overlay"] = true,
                    }, 3000);
                    if (set.Ok)
                    {
                        var enable = ObsWebSocket.InvokeRequest("SetSourceFilterEnabled", new JObject
                        {
                            ["sourceName"] = sourceName, ["filterName"] = filterName, ["filterEnabled"] = entry["enabled"]?.Value<bool>() ?? false,
                        }, 3000);
                        if (enable.Ok) { applied.Add(sourceName + " opacity restored"); restoredExactOpacity = true; }
                        else warnings.Add("Could not restore " + sourceName + " opacity filter enabled state: " + enable.Message);
                    }
                    else
                    {
                        warnings.Add("Could not restore " + sourceName + " opacity filter settings: " + set.Message);
                    }
                }
                else
                {
                    var filters = GetSourceFilterListLive(sourceName);
                    if (filters["ok"]?.Value<bool>() != true) continue;
                    foreach (var filterToken in filters["filters"] as JArray ?? new JArray())
                    {
                        if (filterToken["filterName"]?.Value<string>() != "ReplayKit Overlay Opacity") continue;
                        var remove = ObsWebSocket.InvokeRequest("RemoveSourceFilter", new JObject { ["sourceName"] = sourceName, ["filterName"] = "ReplayKit Overlay Opacity" }, 3000);
                        if (remove.Ok) { applied.Add(sourceName + " opacity restored"); restoredExactOpacity = true; }
                        else warnings.Add("Could not remove preview opacity filter from " + sourceName + ": " + remove.Message);
                        break;
                    }
                }
            }
            if (!restoredExactOpacity)
            {
                var opacity = ApplyOverlayOpacityForStyleLive(preview["settings"] as JObject);
                applied.AddRange((opacity["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                warnings.AddRange((opacity["warnings"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
            }
            ClearOverlayPreviewState();
            return new JObject { ["ok"] = warnings.Count == 0, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };
        }

        public static JObject OverlayPreviewFromRequest(string body, string mode = "preview")
        {
            if (mode == "cancel")
            {
                // read the snapshot under a brief lock, then release before RestoreOverlayPreviewLive does its own (possibly several) obs-websocket round trips -- a cancel and a fresh preview tick landing in the same tight window can still race on which baseline is "current", but holding the lock across the whole restore would block every other overlay-preview op for that entire duration, which is worse than this narrow, self-correcting edge case.
                JObject previewSnapshot;
                lock (Server.State.OverlayPreviewLock) { previewSnapshot = Server.State.ReplaykitOverlayPreviewState; }
                var live = RestoreOverlayPreviewLive(previewSnapshot);
                return new JObject
                {
                    ["ok"] = true, ["settings"] = ReadSettings(), ["applied"] = live["applied"], ["warnings"] = live["warnings"],
                    ["restartRequired"] = false, ["restartReason"] = "",
                };
            }
            if (string.IsNullOrWhiteSpace(body)) throw new InvalidOperationException("Missing overlay preview body.");
            var incoming = JObject.Parse(body);
            long? previewRevision = GetOverlayPreviewRevision(incoming);
            if (incoming.Count < 1) throw new InvalidOperationException("Missing overlay preview setting.");
            var allowedKeys = new HashSet<string> { "overlayOpacity", "overlayScale", "overlayFlipH", "overlayHueShift", "overlayColorMultiply", "overlayColorAdd" };
            foreach (var prop in incoming.Properties())
            {
                if (!allowedKeys.Contains(prop.Name)) throw new InvalidOperationException("Unknown overlay preview setting: " + prop.Name);
            }
            if (mode == "preview" && previewRevision != null)
            {
                // read-compare-write on the revision has to be one atomic op -- otherwise two preview ticks racing here could both pass the check and both think they own the latest revision.
                bool stale = false;
                lock (Server.State.OverlayPreviewLock)
                {
                    if (Server.State.ReplaykitOverlayPreviewRevision >= previewRevision.Value) stale = true;
                    else Server.State.ReplaykitOverlayPreviewRevision = previewRevision.Value;
                }
                if (stale)
                {
                    return new JObject
                    {
                        ["ok"] = true, ["settings"] = Normalize(ReadSettings()), ["applied"] = new JArray(), ["warnings"] = new JArray(),
                        ["skipped"] = true, ["restartRequired"] = false, ["restartReason"] = "",
                    };
                }
            }

            var currentJ = ReadSettings();
            var previous = Normalize(currentJ);
            var preview = GetOverlayPreviewState(previous);
            foreach (var prop in incoming.Properties()) currentJ[prop.Name] = prop.Value;
            var settingsJ = Normalize(currentJ);
            bool forceColorSync = TestOverlayColorRequest(incoming);
            var previewSettings = preview["settings"] as JObject;

            if (mode == "commit")
            {
                WriteSettings(settingsJ);
                var warnings = new List<string>();
                var applied = new List<string>();
                var geometryCommit = ApplyOverlayGeometryPreviewLive(preview, settingsJ);
                applied.AddRange((geometryCommit["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                warnings.AddRange((geometryCommit["warnings"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                bool colorChangedCommit = previewSettings["overlayOpacity"]?.Value<int>() != settingsJ["overlayOpacity"]?.Value<int>() ||
                    previewSettings["overlayHueShift"]?.Value<double>() != settingsJ["overlayHueShift"]?.Value<double>() ||
                    previewSettings["overlayColorMultiply"]?.Value<string>() != settingsJ["overlayColorMultiply"]?.Value<string>() ||
                    previewSettings["overlayColorAdd"]?.Value<string>() != settingsJ["overlayColorAdd"]?.Value<string>();
                if (colorChangedCommit || forceColorSync)
                {
                    var opacity = ApplyOverlayOpacityForStyleLive(settingsJ);
                    applied.AddRange((opacity["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                    warnings.AddRange((opacity["warnings"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                }
                ClearOverlayPreviewState();
                return new JObject
                {
                    ["ok"] = warnings.Count == 0, ["settings"] = settingsJ, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings),
                    ["restartRequired"] = false, ["restartReason"] = "",
                };
            }

            var previewWarnings = new List<string>();
            var previewApplied = new List<string>();
            var geometryPreview = ApplyOverlayGeometryPreviewLive(preview, settingsJ);
            previewApplied.AddRange((geometryPreview["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
            previewWarnings.AddRange((geometryPreview["warnings"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
            bool colorChanged = previewSettings["overlayOpacity"]?.Value<int>() != settingsJ["overlayOpacity"]?.Value<int>() ||
                previewSettings["overlayHueShift"]?.Value<double>() != settingsJ["overlayHueShift"]?.Value<double>() ||
                previewSettings["overlayColorMultiply"]?.Value<string>() != settingsJ["overlayColorMultiply"]?.Value<string>() ||
                previewSettings["overlayColorAdd"]?.Value<string>() != settingsJ["overlayColorAdd"]?.Value<string>();
            if (colorChanged || forceColorSync)
            {
                var opacity = ApplyOverlayOpacityForStyleLive(settingsJ);
                previewApplied.AddRange((opacity["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                previewWarnings.AddRange((opacity["warnings"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
            }

            return new JObject
            {
                ["ok"] = true, ["settings"] = settingsJ, ["applied"] = new JArray(previewApplied), ["warnings"] = new JArray(previewWarnings),
                ["restartRequired"] = false, ["restartReason"] = "",
            };
        }

        // consumes a persisted preview baseline left by a session that died before commit or cancel -- once obs is answering again, push the pre-preview transforms + opacity filters back so an un-applied live preview cant survive into obss saved scene. runs before the http accept loop, so nothing can be mid-preview yet.
        public static void RevertAbandonedOverlayPreviewAtStartup()
        {
            if (!File.Exists(OverlayPreviewBaselinePath)) return;

            string raw;
            try { raw = File.ReadAllText(OverlayPreviewBaselinePath); }
            catch (Exception ex) { Log.Write("RevertAbandonedOverlayPreview: cannot read baseline, leaving it: " + ex.Message); return; }

            // delete the file before acting on it -- if we cant delete it we must NOT run the revert, otherwise a
            // baseline that keeps coming back (locked/read-only scratch dir) would re-stomp the users applied
            // overlay on every single launch. strictly one shot.
            try { File.Delete(OverlayPreviewBaselinePath); }
            catch (Exception ex) { Log.Write("RevertAbandonedOverlayPreview: baseline undeletable, skipping revert: " + ex.Message); return; }

            JObject baseline;
            try { baseline = JObject.Parse(raw); }
            catch (Exception ex) { Log.Write("RevertAbandonedOverlayPreview: unparseable baseline discarded: " + ex.Message); return; }

            Task.Run(() =>
            {
                try
                {
                    for (int i = 0; i < 30; i++)
                    {
                        // a real preview session started while we were waiting -- it owns the overlay now, dont clobber it with a stale baseline.
                        if (Server.State.ReplaykitOverlayPreviewState != null) return;
                        if (GetSceneName()["ok"]?.Value<bool>() == true)
                        {
                            var live = RestoreOverlayPreviewLive(baseline);
                            Log.Write("RevertAbandonedOverlayPreview: " + string.Join(", ", (live["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>()));
                            return;
                        }
                        Thread.Sleep(2000);
                    }
                    Log.Write("RevertAbandonedOverlayPreview: OBS not reachable in time; abandoned-preview revert skipped.");
                }
                catch (Exception ex) { Log.Write("RevertAbandonedOverlayPreview: " + ex.Message); }
            });
        }

        // -- runtime output stop/apply/restart orchestration + projector audio-monitoring toggle --

        private static JObject NewInactiveOutputState() => new JObject { ["ok"] = true, ["wasActive"] = false };

        // serialize the whole stop-outputs -> SetVideoSettings (obs_reset_video) -> restart-outputs cycle. two of
        // these overlapping deadlock obs's video graph (2026-08-28: a user hard-froze obs by rapidly re-applying the
        // downscale resolution), and even sequential ones need a beat for the render/graphics threads to re-settle.
        private static JObject ApplyRuntimeOutputsLive(JObject settings, JObject preset, bool restartObs, bool applyVideoSettings = true, bool applyReplayBufferOutput = true)
        {
            lock (Server.State.VideoApplyLock)
            {
                double sinceLastMs = (DateTime.UtcNow - Server.State.LastVideoApplyDoneUtc).TotalMilliseconds;
                if (sinceLastMs < 2500) Thread.Sleep((int)Math.Max(0, 2500 - sinceLastMs));
                try
                {
                    return ApplyRuntimeOutputsLiveInner(settings, preset, restartObs, applyVideoSettings, applyReplayBufferOutput);
                }
                finally
                {
                    Server.State.LastVideoApplyDoneUtc = DateTime.UtcNow;
                }
            }
        }

        private static JObject ApplyRuntimeOutputsLiveInner(JObject settings, JObject preset, bool restartObs, bool applyVideoSettings = true, bool applyReplayBufferOutput = true)
        {
            var warnings = new List<string>();
            var applied = new List<string>();
            bool legacyVcam = settings["discord_output_mode"]?.Value<string>() == "virtual_camera_legacy";
            bool stopAllOutputs = restartObs || applyVideoSettings;
            var record = NewInactiveOutputState();
            var replay = NewInactiveOutputState();
            var vcam = NewInactiveOutputState();

            if (stopAllOutputs)
            {
                record = StopObsOutputIfActive("GetRecordStatus", "StopRecord", "recording");
                replay = StopObsOutputIfActive("GetReplayBufferStatus", "StopReplayBuffer", "replay buffer");
                vcam = StopObsOutputIfActive("GetVirtualCamStatus", "StopVirtualCam", "virtual camera");
            }
            else if (applyReplayBufferOutput)
            {
                replay = StopObsOutputIfActive("GetReplayBufferStatus", "StopReplayBuffer", "replay buffer");
            }

            foreach (var state in new[] { record, replay, vcam })
            {
                string warning = state["warning"]?.Value<string>();
                if (!string.IsNullOrEmpty(warning)) warnings.Add(warning);
            }

            if (record["ok"]?.Value<bool>() == true && replay["ok"]?.Value<bool>() == true && vcam["ok"]?.Value<bool>() == true)
            {
                if (applyVideoSettings)
                {
                    var video = SetVideoSettingsLive(preset);
                    if (video.Ok)
                    {
                        SetFractionalFpsProfile(preset, warnings);
                        applied.Add("OBS video format");
                    }
                    else warnings.Add("OBS video settings were saved, but live apply failed: " + video.Message);
                }

                if (applyReplayBufferOutput)
                {
                    var rb = SetReplayBufferOutputLive(settings, preset);
                    if (rb.Ok) applied.Add("OBS replay buffer output");
                    else warnings.Add("OBS replay buffer settings were saved, but live apply failed: " + rb.Message);
                }
            }
            else
            {
                warnings.Add("OBS outputs were not stopped safely, so video/output live changes were not applied.");
            }

            var outputs = new List<JObject>
            {
                new JObject { ["state"] = replay, ["request"] = "StartReplayBuffer", ["label"] = "replay buffer" },
                new JObject { ["state"] = record, ["request"] = "StartRecord", ["label"] = "recording" },
            };
            if (legacyVcam)
            {
                outputs.Insert(0, new JObject { ["state"] = vcam, ["request"] = "StartVirtualCam", ["label"] = "virtual camera (legacy)" });
            }
            else if (vcam["ok"]?.Value<bool>() == true && vcam["wasActive"]?.Value<bool>() == true)
            {
                applied.Add("virtual camera stopped for projector Discord output");
            }

            if (restartObs)
            {
                foreach (var output in outputs)
                {
                    var state = output["state"] as JObject;
                    if (state["ok"]?.Value<bool>() == true && state["wasActive"]?.Value<bool>() == true)
                        applied.Add(output["label"] + " stopped for OBS restart");
                }
                return new JObject { ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };
            }

            foreach (var restart in outputs)
            {
                var state = restart["state"] as JObject;
                var r = StartObsOutputIfNeeded(state, restart["request"]?.Value<string>(), restart["label"]?.Value<string>());
                string rWarning = r["warning"]?.Value<string>();
                if (r["ok"]?.Value<bool>() != true && !string.IsNullOrEmpty(rWarning)) warnings.Add(rWarning);
                else if (state["wasActive"]?.Value<bool>() == true) applied.Add(restart["label"] + " restarted");
            }

            return new JObject { ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };
        }

        private static JObject SetProjectorMonitoringState(JObject settings, bool enabled)
        {
            string inputName = "Desktop Audio (excl. Discord)";
            string monitorType = enabled ? "OBS_MONITORING_TYPE_MONITOR_AND_OUTPUT" : "OBS_MONITORING_TYPE_NONE";

            if (monitorType == "OBS_MONITORING_TYPE_NONE") return SetDesktopAudioMixerState(inputName, monitorType);

            var renderDevice = DiscordProjector.GetObsStreamAudioRenderDevice();
            if (!renderDevice.Ok) return new JObject { ["ok"] = false, ["applied"] = new JArray(), ["message"] = renderDevice.Message };

            var monitorDevice = SetObsMonitoringDevice(renderDevice.Id, renderDevice.Name, 6);
            if (monitorDevice["ok"]?.Value<bool>() != true) return new JObject { ["ok"] = false, ["applied"] = new JArray(), ["message"] = monitorDevice["message"] };

            var applied = new List<string>((monitorDevice["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
            var mixer = SetDesktopAudioMixerState(inputName, monitorType);
            if (mixer["ok"]?.Value<bool>() != true) return new JObject { ["ok"] = false, ["applied"] = new JArray(applied), ["message"] = mixer["message"] };
            applied.AddRange((mixer["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
            return new JObject { ["ok"] = true, ["applied"] = new JArray(applied), ["message"] = "" };
        }

        // -- desktop audio mixer: retry-with-verification wrappers, since obs does not always apply an audio
        // monitor-type/mute change instantly, plus a deliberate mute-then-unmute "flap" to force the obs mixer
        // icon to visually refresh after a monitor-type change.

        private static JObject SetDesktopAudioMixerState(string inputName, string monitorType)
        {
            var applied = new List<string>();
            bool targetIsOff = monitorType == "OBS_MONITORING_TYPE_NONE";

            string lastMonitorMessage = "";
            bool monitorReady = false;
            for (int attempt = 1; attempt <= 6; attempt++)
            {
                var actualBefore = GetInputMonitorTypeValue(inputName);
                if (actualBefore["ok"]?.Value<bool>() == true && actualBefore["value"]?.Value<string>() == monitorType) { monitorReady = true; break; }

                var monitor = SetInputMonitorTypeRaw(inputName, monitorType);
                if (monitor["ok"]?.Value<bool>() != true)
                {
                    lastMonitorMessage = monitor["message"]?.Value<string>();
                    Thread.Sleep(Math.Min(1000, 150 * attempt));
                    continue;
                }

                Thread.Sleep(Math.Min(1000, 150 * attempt));
                var actualAfter = GetInputMonitorTypeValue(inputName);
                if (actualAfter["ok"]?.Value<bool>() == true && actualAfter["value"]?.Value<string>() == monitorType) { monitorReady = true; break; }
                lastMonitorMessage = actualAfter["ok"]?.Value<bool>() == true
                    ? "OBS reported '" + actualAfter["value"] + "' instead of '" + monitorType + "'."
                    : actualAfter["message"]?.Value<string>();
            }
            if (!monitorReady) return new JObject { ["ok"] = false, ["applied"] = new JArray(applied), ["message"] = "OBS did not report the requested monitor state after retries. " + lastMonitorMessage };

            var unmute = SetInputMuteState(inputName, false, 6);
            if (unmute["ok"]?.Value<bool>() != true) return new JObject { ["ok"] = false, ["applied"] = new JArray(applied), ["message"] = "monitor state changed, but Desktop Audio could not be unmuted: " + unmute["message"] };

            applied.Add("desktop audio unmuted");
            var refresh = RefreshDesktopAudioMixerIconState(inputName, monitorType);
            if (refresh["ok"]?.Value<bool>() != true) return new JObject { ["ok"] = false, ["applied"] = new JArray(applied), ["message"] = "monitor state changed, but the OBS mixer icon could not be refreshed: " + refresh["message"] };
            applied.AddRange((refresh["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
            applied.Add(targetIsOff ? "desktop audio monitor off" : "desktop audio monitor on");
            return new JObject { ["ok"] = true, ["applied"] = new JArray(applied), ["message"] = "" };
        }

        private static JObject SetInputMonitorTypeRaw(string inputName, string monitorType)
        {
            var result = ObsWebSocket.InvokeRequest("SetInputAudioMonitorType", new JObject { ["inputName"] = inputName, ["monitorType"] = monitorType }, 3000);
            return result.Ok ? new JObject { ["ok"] = true, ["message"] = "" } : new JObject { ["ok"] = false, ["message"] = result.Message };
        }

        private static JObject SetInputMuteRaw(string inputName, bool muted)
        {
            var result = ObsWebSocket.InvokeRequest("SetInputMute", new JObject { ["inputName"] = inputName, ["inputMuted"] = muted }, 3000);
            return result.Ok ? new JObject { ["ok"] = true, ["message"] = "" } : new JObject { ["ok"] = false, ["message"] = result.Message };
        }

        private static JObject SetInputMuteState(string inputName, bool muted, int maxAttempts = 4)
        {
            int attempts = Math.Max(1, Math.Min(10, maxAttempts));
            string lastMessage = "";
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                if (TestInputMuteState(inputName, muted)) return new JObject { ["ok"] = true, ["message"] = "" };
                var result = SetInputMuteRaw(inputName, muted);
                if (result["ok"]?.Value<bool>() != true)
                {
                    lastMessage = result["message"]?.Value<string>();
                    Thread.Sleep(Math.Min(1000, 150 * attempt));
                    continue;
                }
                Thread.Sleep(Math.Min(1000, 150 * attempt));
                if (TestInputMuteState(inputName, muted)) return new JObject { ["ok"] = true, ["message"] = "" };
                lastMessage = "OBS did not report the requested mute state after setting it.";
            }
            return new JObject { ["ok"] = false, ["message"] = lastMessage };
        }

        private static JObject RefreshDesktopAudioMixerIconState(string inputName, string monitorType)
        {
            var applied = new List<string>();

            var monitor = SetInputMonitorTypeRaw(inputName, monitorType);
            if (monitor["ok"]?.Value<bool>() != true) return new JObject { ["ok"] = false, ["applied"] = new JArray(applied), ["message"] = monitor["message"] };

            if (monitorType == "OBS_MONITORING_TYPE_NONE")
            {
                var mute = SetInputMuteRaw(inputName, true);
                if (mute["ok"]?.Value<bool>() != true) return new JObject { ["ok"] = false, ["applied"] = new JArray(applied), ["message"] = mute["message"] };
                Thread.Sleep(40);
            }

            var unmute = SetInputMuteRaw(inputName, false);
            if (unmute["ok"]?.Value<bool>() != true) return new JObject { ["ok"] = false, ["applied"] = new JArray(applied), ["message"] = unmute["message"] };

            Thread.Sleep(40);
            monitor = SetInputMonitorTypeRaw(inputName, monitorType);
            if (monitor["ok"]?.Value<bool>() != true) return new JObject { ["ok"] = false, ["applied"] = new JArray(applied), ["message"] = monitor["message"] };

            for (int attempt = 1; attempt <= 4; attempt++)
            {
                Thread.Sleep(Math.Min(300, 50 * attempt));
                var actual = GetInputMonitorTypeValue(inputName);
                if (actual["ok"]?.Value<bool>() == true && actual["value"]?.Value<string>() == monitorType && TestInputMuteState(inputName, false))
                {
                    applied.Add("desktop audio mixer icon refreshed");
                    return new JObject { ["ok"] = true, ["applied"] = new JArray(applied), ["message"] = "" };
                }
            }

            return new JObject { ["ok"] = false, ["applied"] = new JArray(applied), ["message"] = "OBS did not report the refreshed monitor and mute state." };
        }

        private static JObject GetInputMonitorTypeValue(string inputName)
        {
            var verify = ObsWebSocket.InvokeRequest("GetInputAudioMonitorType", new JObject { ["inputName"] = inputName }, 3000);
            if (!verify.Ok) return new JObject { ["ok"] = false, ["value"] = "", ["message"] = verify.Message };
            string monitorType = verify.Data?["monitorType"]?.Value<string>();
            string inputAudioMonitorType = verify.Data?["inputAudioMonitorType"]?.Value<string>();
            if (!string.IsNullOrEmpty(monitorType)) return new JObject { ["ok"] = true, ["value"] = monitorType, ["message"] = "" };
            if (!string.IsNullOrEmpty(inputAudioMonitorType)) return new JObject { ["ok"] = true, ["value"] = inputAudioMonitorType, ["message"] = "" };
            return new JObject { ["ok"] = false, ["value"] = "", ["message"] = "OBS did not return an input monitor type." };
        }

        private static bool TestInputMuteState(string inputName, bool expected)
        {
            var verify = ObsWebSocket.InvokeRequest("GetInputMute", new JObject { ["inputName"] = inputName }, 3000);
            if (!verify.Ok) return false;
            var value = verify.Data?["inputMuted"];
            if (value == null || value.Type == JTokenType.Null) return false;
            return value.Value<bool>() == expected;
        }

        // obs-browser-page.exe is obs's own cef subprocess -- without excluding it, clip audio played back in the
        // dock gets treated as ordinary desktop audio, monitored back out to the discord share, and doubles up
        // with the copy discord already grabs directly from the same process tree.
        private static readonly string[] DesktopAudioExcludeExes = { "Discord.exe", "DiscordSystemHelper.exe", "DiscordCanary.exe", "DiscordPTB.exe", "DiscordDevelopment.exe", "obs64.exe", "obs32.exe", "obs.exe", "obs-browser-page.exe" };

        private static JArray GetDesktopAudioExcludeList()
        {
            var entries = new JArray();
            foreach (var name in DesktopAudioExcludeExes) entries.Add(new JObject { ["value"] = name });
            return entries;
        }

        private static void SetDesktopAudioCaptureSettingsObject(JObject settingsObject)
        {
            settingsObject["mode"] = "session";
            settingsObject["executable_list"] = GetDesktopAudioExcludeList();
            settingsObject["exclude"] = true;
        }

        private static JObject SetDesktopAudioCaptureSettingsLive(string inputName)
        {
            var inputSettings = new JObject();
            SetDesktopAudioCaptureSettingsObject(inputSettings);
            var result = ObsWebSocket.InvokeRequest("SetInputSettings", new JObject { ["inputName"] = inputName, ["inputSettings"] = inputSettings, ["overlay"] = true }, 3000);
            return result.Ok ? new JObject { ["ok"] = true, ["message"] = "" } : new JObject { ["ok"] = false, ["message"] = result.Message };
        }

        // keeps the live desktop-audio monitoring/mute change in sync with the on-disk scene file, so a disabled
        // Share Preview cannot silently come back after obs restarts (obs persists audio monitoring in the scene
        // collection, not in a profile setting this file already writes elsewhere).
        private static JObject SetShareModeSceneFile(int monitoringType, bool muted)
        {
            try
            {
                string path = GetSceneCollectionPath();
                if (!File.Exists(path)) return new JObject { ["ok"] = true, ["changed"] = false };
                var data = JObject.Parse(File.ReadAllText(path));
                var sources = data["sources"] as JArray;
                if (sources == null) return new JObject { ["ok"] = true, ["changed"] = false };
                bool changed = false;
                foreach (var sourceToken in sources)
                {
                    var source = (JObject)sourceToken;
                    if (source["name"]?.Value<string>() != "Desktop Audio (excl. Discord)") continue;
                    var settingsObject = source["settings"] as JObject;
                    if (settingsObject == null) { settingsObject = new JObject(); source["settings"] = settingsObject; }
                    string settingsBefore = settingsObject.ToString(Formatting.None);
                    SetDesktopAudioCaptureSettingsObject(settingsObject);
                    string settingsAfter = settingsObject.ToString(Formatting.None);
                    if (settingsBefore != settingsAfter) changed = true;
                    int current = source["monitoring_type"]?.Value<int>() ?? -1;
                    if (current != monitoringType) { source["monitoring_type"] = monitoringType; changed = true; }
                    bool currentMuted = source["muted"]?.Value<bool>() ?? true;
                    if (currentMuted != muted) { source["muted"] = muted; changed = true; }
                }
                if (changed) AppConfig.WriteUtf8(path, data.ToString(Formatting.Indented));
                return new JObject { ["ok"] = true, ["changed"] = changed };
            }
            catch (Exception ex)
            {
                return new JObject { ["ok"] = false, ["changed"] = false, ["message"] = ex.Message };
            }
        }

        // -- discord output live-apply + share preview: updates desktop-audio exclusion/monitoring settings,
        // starts/stops the legacy virtual camera, and parks/unparks the obs windowed projector via DiscordProjector.

        private static JObject ApplyDiscordOutputLive(JObject settings)
        {
            var warnings = new List<string>();
            var applied = new List<string>();
            bool ok = true;
            string message = "";
            string inputName = "Desktop Audio (excl. Discord)";
            string mode = settings["discord_output_mode"]?.Value<string>() ?? "";
            if (mode != "projector") mode = "projector";
            bool isLegacyVcam = false;
            bool screenshareEnabled = TestDiscordScreenshareEnabled(settings);
            bool shareEnabled = screenshareEnabled && (settings["discord_projector_enabled"]?.Value<bool>() ?? false);
            if (!screenshareEnabled) applied.Add("Discord screenshare support disabled");
            string monitorType = shareEnabled ? "OBS_MONITORING_TYPE_MONITOR_AND_OUTPUT" : "OBS_MONITORING_TYPE_NONE";

            var captureSettings = SetDesktopAudioCaptureSettingsLive(inputName);
            if (captureSettings["ok"]?.Value<bool>() == true) applied.Add("desktop audio exclusions updated");
            else warnings.Add("Discord output mode was saved, but Desktop Audio exclusions could not be updated: " + captureSettings["message"]);

            if (mode == "projector")
            {
                var mixer = SetProjectorMonitoringState(settings, shareEnabled);
                if (mixer["ok"]?.Value<bool>() == true)
                {
                    applied.AddRange((mixer["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                    int sceneMonitoringType = shareEnabled ? 2 : 0;
                    var sceneFile = SetShareModeSceneFile(sceneMonitoringType, false);
                    if (sceneFile["ok"]?.Value<bool>() == true)
                    {
                        if (sceneFile["changed"]?.Value<bool>() == true) applied.Add("desktop audio monitor state saved");
                    }
                    else
                    {
                        warnings.Add("Desktop Audio monitoring was applied live, but could not be saved for restart: " + sceneFile["message"]);
                    }
                }
                else
                {
                    ok = false;
                    message = "Desktop Audio monitoring state could not be verified: " + mixer["message"];
                    warnings.Add("Discord output mode was saved, but " + message);
                }
            }
            else
            {
                var mixer = SetDesktopAudioMixerState(inputName, monitorType);
                if (mixer["ok"]?.Value<bool>() == true)
                {
                    applied.AddRange((mixer["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                }
                else
                {
                    ok = false;
                    message = "Desktop Audio mixer state could not be changed: " + mixer["message"];
                    warnings.Add("Discord output mode was saved, but " + message);
                }
            }

            if (!ok && mode != "projector")
                return new JObject { ["ok"] = false, ["message"] = message, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings), ["restartRequired"] = false, ["restartReason"] = "" };

            var status = ObsWebSocket.InvokeRequest("GetVirtualCamStatus", null, 3000);
            if (!status.Ok)
            {
                warnings.Add("Discord output mode was saved, but virtual camera state could not be read: " + status.Message);
            }
            else
            {
                bool running = status.Data?["outputActive"]?.Value<bool>() ?? false;
                if (isLegacyVcam && !running)
                {
                    Log.Write("Virtual Camera legacy mode enabled for Discord output.");
                    var start = ObsWebSocket.InvokeRequest("StartVirtualCam", null, 8000);
                    if (start.Ok) applied.Add("virtual camera started (legacy)");
                    else warnings.Add("Discord output mode was saved, but legacy virtual camera could not be started: " + start.Message);
                }
                else if (!isLegacyVcam && running)
                {
                    var stop = ObsWebSocket.InvokeRequest("StopVirtualCam", null, 8000);
                    if (stop.Ok) applied.Add("virtual camera stopped");
                    else warnings.Add("Discord output mode was saved, but virtual camera could not be stopped: " + stop.Message);
                }
                else
                {
                    applied.Add(isLegacyVcam ? "virtual camera already on (legacy)" : "virtual camera already off");
                }
            }

            if (shareEnabled)
            {
                DiscordProjector.StopShareBridge();
                Log.Write("Discord projector mode enabled");
                Log.Write("Skipping OBS Virtual Camera for Discord output");
                var projector = DiscordProjector.Repark(settings);
                if (projector["ok"]?.Value<bool>() == true)
                {
                    applied.AddRange((projector["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                }
                else
                {
                    ok = false;
                    if (string.IsNullOrWhiteSpace(message)) message = "OBS projector was not ready: " + projector["message"];
                    warnings.Add("OBS projector was not ready: " + projector["message"]);
                }
                if (projector["warnings"] is JArray pw) warnings.AddRange(pw.Select(t => t.Value<string>()));
            }
            else if (mode == "projector")
            {
                DiscordProjector.StopShareBridge();
                var projector = DiscordProjector.Disable(settings);
                if (projector["ok"]?.Value<bool>() == true)
                {
                    applied.AddRange((projector["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                }
                else
                {
                    ok = false;
                    if (string.IsNullOrWhiteSpace(message)) message = "Share preview could not be disabled: " + projector["message"];
                    warnings.Add("Share preview could not be disabled: " + projector["message"]);
                }
                if (projector["warnings"] is JArray pw2) warnings.AddRange(pw2.Select(t => t.Value<string>()));
            }
            else
            {
                warnings.Add("Virtual Camera is legacy/deprecated for Discord output.");
            }

            return new JObject { ["ok"] = ok, ["message"] = message, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings), ["restartRequired"] = false, ["restartReason"] = "" };
        }

        public static JObject SetSharePreviewEnabled(bool enabled)
        {
            var previous = ReadSettings();
            var settings = (JObject)previous.DeepClone();
            settings["discord_output_mode"] = "projector";
            settings["shareMode"] = "projector";
            if (enabled && !TestDiscordScreenshareEnabled(settings))
            {
                settings["discord_projector_enabled"] = false;
                settings = Normalize(settings);
                WriteSettings(settings);
                var live = ApplyDiscordOutputLive(settings);
                var warnings = new JArray((live["warnings"] as JArray) ?? new JArray());
                warnings.Add("Discord screenshare support is disabled in Advanced settings.");
                return new JObject
                {
                    ["ok"] = true, ["enabled"] = false, ["available"] = false, ["settings"] = settings,
                    ["applied"] = live["applied"], ["warnings"] = warnings, ["message"] = "",
                    ["restartRequired"] = false, ["restartReason"] = "",
                };
            }
            settings["discord_projector_enabled"] = enabled;
            settings = Normalize(settings);
            WriteSettings(settings);
            var liveResult = ApplyDiscordOutputLive(settings);
            if (liveResult["ok"]?.Value<bool>() != true)
            {
                var warnings = new JArray((liveResult["warnings"] as JArray) ?? new JArray());
                warnings.Add(liveResult["message"]);
                return new JObject
                {
                    ["ok"] = true,
                    ["enabled"] = TestDiscordScreenshareEnabled(settings) && (settings["discord_projector_enabled"]?.Value<bool>() ?? false),
                    ["available"] = TestDiscordScreenshareEnabled(settings),
                    ["settings"] = settings, ["applied"] = liveResult["applied"], ["warnings"] = warnings, ["message"] = "",
                    ["restartRequired"] = false, ["restartReason"] = "",
                };
            }
            return new JObject
            {
                ["ok"] = true,
                ["enabled"] = TestDiscordScreenshareEnabled(settings) && (settings["discord_projector_enabled"]?.Value<bool>() ?? false),
                ["available"] = TestDiscordScreenshareEnabled(settings),
                ["settings"] = settings, ["applied"] = liveResult["applied"], ["warnings"] = liveResult["warnings"], ["message"] = "",
                ["restartRequired"] = false, ["restartReason"] = "",
            };
        }

        public static JObject GetSharePreviewState(bool repairMonitoring = false)
        {
            var settings = ReadSettings();
            bool available = TestDiscordScreenshareEnabled(settings);
            bool enabled = available && settings["discord_output_mode"]?.Value<string>() == "projector" && (settings["discord_projector_enabled"]?.Value<bool>() ?? false);
            string inputName = "Desktop Audio (excl. Discord)";
            string desiredMonitorType = enabled ? "OBS_MONITORING_TYPE_MONITOR_AND_OUTPUT" : "OBS_MONITORING_TYPE_NONE";
            var warnings = new List<string>();
            bool repaired = false;

            var actual = GetInputMonitorTypeValue(inputName);
            string actualMonitorType = actual["ok"]?.Value<bool>() == true ? actual["value"]?.Value<string>() : "";
            if (actual["ok"]?.Value<bool>() != true) warnings.Add("Could not read Desktop Audio monitor state: " + actual["message"]);

            var mute = ObsWebSocket.InvokeRequest("GetInputMute", new JObject { ["inputName"] = inputName }, 3000);
            bool muteOk = mute.Ok;
            bool? inputMuted = null;
            if (muteOk) inputMuted = mute.Data?["inputMuted"]?.Value<bool>() ?? false;
            else warnings.Add("Could not read Desktop Audio mute state: " + mute.Message);

            bool synced = actual["ok"]?.Value<bool>() == true && actualMonitorType == desiredMonitorType && muteOk && inputMuted != true;
            if (repairMonitoring && !synced)
            {
                var repair = SetProjectorMonitoringState(settings, enabled);
                if (repair["ok"]?.Value<bool>() == true)
                {
                    repaired = true;
                    actual = GetInputMonitorTypeValue(inputName);
                    actualMonitorType = actual["ok"]?.Value<bool>() == true ? actual["value"]?.Value<string>() : "";
                    mute = ObsWebSocket.InvokeRequest("GetInputMute", new JObject { ["inputName"] = inputName }, 3000);
                    muteOk = mute.Ok;
                    inputMuted = muteOk ? (mute.Data?["inputMuted"]?.Value<bool>() ?? false) : (bool?)null;
                    synced = actual["ok"]?.Value<bool>() == true && actualMonitorType == desiredMonitorType && muteOk && inputMuted != true;
                }
                else
                {
                    warnings.Add("Could not repair Desktop Audio monitoring state: " + repair["message"]);
                }
            }

            return new JObject
            {
                ["ok"] = true, ["enabled"] = enabled, ["available"] = available, ["settings"] = settings,
                ["monitoring"] = new JObject
                {
                    ["inputName"] = inputName, ["desiredMonitorType"] = desiredMonitorType, ["actualMonitorType"] = actualMonitorType,
                    ["actualReadOk"] = actual["ok"]?.Value<bool>() ?? false, ["inputMuted"] = inputMuted, ["muteReadOk"] = muteOk,
                    ["synced"] = synced, ["repaired"] = repaired,
                },
                ["warnings"] = new JArray(warnings),
            };
        }

        private static JObject ApplyShareModeLive(string shareMode)
        {
            var settings = ReadSettings();
            settings["discord_output_mode"] = "projector";
            settings["shareMode"] = "projector";
            return ApplyDiscordOutputLive(settings);
        }

        // -- motion blur: live-websocket half (create/update/remove the filter on a running obs, including
        // cleanup of the retired obs_composite_blur-kind filter left over from older ReplayKit versions) --

        private static bool TestShaderfilterPluginInstalled()
        {
            string root = GetObsInstallRoot();
            string dll = Path.Combine(root, "obs-plugins\\64bit\\obs-shaderfilter.dll");
            string shader = Path.Combine(root, "data\\obs-plugins\\obs-shaderfilter\\examples\\motion_blur.shader");
            return File.Exists(dll) && File.Exists(shader);
        }

        private static string GetMotionBlurLiveFilterName(JArray filters)
        {
            foreach (var filter in filters ?? new JArray())
            {
                string name = filter["filterName"]?.Value<string>() ?? "";
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (name == "ReplayKit Motion Blur") return name;
                string kind = filter["filterKind"]?.Value<string>() ?? "";
                if (kind == "shader_filter")
                {
                    var filterSettings = filter["filterSettings"];
                    string shaderFile = filterSettings?["shader_file_name"]?.Value<string>() ?? "";
                    if (shaderFile.Replace('\\', '/').ToLowerInvariant().EndsWith("/motion_blur.shader")) return name;
                }
            }
            return "";
        }

        private static List<string> GetRetiredMotionBlurLiveFilterNames(JArray filters)
        {
            var names = new List<string>();
            foreach (var filter in filters ?? new JArray())
            {
                string name = filter["filterName"]?.Value<string>() ?? "";
                string kind = filter["filterKind"]?.Value<string>() ?? "";
                if (!string.IsNullOrEmpty(name) && kind == "obs_composite_blur") names.Add(name);
            }
            return names;
        }

        private static JObject ApplyMotionBlurLive(JObject settings)
        {
            var warnings = new List<string>();
            var applied = new List<string>();
            bool enabled = settings["motionBlurEnabled"]?.Value<bool>() ?? false;
            double strength = MotionBlurStrength(settings);
            string filterName = "ReplayKit Motion Blur";
            var sourceNames = new[] { "Display Capture", "Game Capture", "Window Capture" };

            if (enabled && !TestShaderfilterPluginInstalled())
            {
                warnings.Add("Motion blur was saved, but OBS Shaderfilter is not installed. Re-run ReplayKit setup, then restart OBS after the plugin install.");
                return new JObject { ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };
            }

            foreach (var sourceName in sourceNames)
            {
                var list = ObsWebSocket.InvokeRequest("GetSourceFilterList", new JObject { ["sourceName"] = sourceName }, 3000);
                if (!list.Ok) { warnings.Add("Motion blur was saved, but " + sourceName + " filters could not be read: " + list.Message); continue; }
                var filters = list.Data?["filters"] as JArray ?? list.Data?["sourceFilters"] as JArray ?? new JArray();
                foreach (var retiredName in GetRetiredMotionBlurLiveFilterNames(filters))
                {
                    var removeRetired = ObsWebSocket.InvokeRequest("RemoveSourceFilter", new JObject { ["sourceName"] = sourceName, ["filterName"] = retiredName }, 3000);
                    if (!removeRetired.Ok) warnings.Add("Could not remove retired " + sourceName + " Composite Blur filter: " + removeRetired.Message);
                }
                string existingFilterName = GetMotionBlurLiveFilterName(filters);

                if (enabled)
                {
                    if (!string.IsNullOrWhiteSpace(existingFilterName))
                    {
                        var set = ObsWebSocket.InvokeRequest("SetSourceFilterSettings", new JObject
                        {
                            ["sourceName"] = sourceName, ["filterName"] = existingFilterName,
                            ["filterSettings"] = MotionBlurFilterSettingsLive(strength), ["overlay"] = false,
                        }, 3000);
                        if (!set.Ok) { warnings.Add("Could not update " + sourceName + " motion blur settings: " + set.Message); continue; }
                    }
                    else
                    {
                        var create = ObsWebSocket.InvokeRequest("CreateSourceFilter", new JObject
                        {
                            ["sourceName"] = sourceName, ["filterName"] = filterName, ["filterKind"] = "shader_filter",
                            ["filterSettings"] = MotionBlurFilterSettingsLive(strength),
                        }, 3000);
                        if (!create.Ok) { warnings.Add("Could not create " + sourceName + " motion blur filter: " + create.Message); continue; }
                        existingFilterName = filterName;
                    }
                    var enable = ObsWebSocket.InvokeRequest("SetSourceFilterEnabled", new JObject { ["sourceName"] = sourceName, ["filterName"] = existingFilterName, ["filterEnabled"] = true }, 3000);
                    if (enable.Ok) applied.Add(sourceName + " motion blur on");
                    else warnings.Add("Could not enable " + sourceName + " motion blur: " + enable.Message);
                }
                else if (!string.IsNullOrWhiteSpace(existingFilterName))
                {
                    var remove = ObsWebSocket.InvokeRequest("RemoveSourceFilter", new JObject { ["sourceName"] = sourceName, ["filterName"] = existingFilterName }, 3000);
                    if (remove.Ok) applied.Add(sourceName + " motion blur off");
                    else warnings.Add("Could not remove " + sourceName + " motion blur filter: " + remove.Message);
                }
            }

            return new JObject { ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };
        }

        // -- hotkey capture + sync-from-obs: lets the dock's "press a key to bind" ui temporarily blank obs's
        // native hotkeys while listening, and detects when the user rebinds a hotkey through obs's own Settings
        // dialog instead of the dock, so the dock does not silently fight/revert that edit.

        private static JObject SyncRecordingKeybindFromObs(JObject settings)
        {
            var start = GetObsProfileParameterSafe("Hotkeys", "OBSBasic.StartRecording");
            var stop = GetObsProfileParameterSafe("Hotkeys", "OBSBasic.StopRecording");
            if (!start.Ok && !stop.Ok) return settings;

            JObject combo = new JObject();
            if (start.Ok) combo = ConvertRecordingBasicIniToKeybind(start.Data?["parameterValue"]?.Value<string>() ?? "");
            if (combo["key"] == null && stop.Ok) combo = ConvertRecordingBasicIniToKeybind(stop.Data?["parameterValue"]?.Value<string>() ?? "");

            string current = (settings["recordingKeybind"] as JObject ?? new JObject()).ToString(Formatting.None);
            string next = combo.ToString(Formatting.None);
            if (current != next) settings["recordingKeybind"] = combo;
            return settings;
        }

        private static JObject SyncHotkeysFromObs(JObject settings)
        {
            string before = new JObject { ["clip"] = settings["clipKeybind"], ["recording"] = settings["recordingKeybind"] }.ToString(Formatting.None);

            var clip = GetObsProfileParameterSafe("Hotkeys", "ReplayBuffer");
            if (clip.Ok)
            {
                var combo = ConvertClipBasicIniToKeybind(clip.Data?["parameterValue"]?.Value<string>() ?? "");
                if (combo["key"] != null)
                {
                    string current = (settings["clipKeybind"] as JObject ?? new JObject()).ToString(Formatting.None);
                    string next = combo.ToString(Formatting.None);
                    if (current != next) settings["clipKeybind"] = combo;
                }
            }

            settings = SyncRecordingKeybindFromObs(settings);
            string after = new JObject { ["clip"] = settings["clipKeybind"], ["recording"] = settings["recordingKeybind"] }.ToString(Formatting.None);
            if (before != after) WriteSettings(settings);
            return settings;
        }

        // obs's own settings dialog can edit RecRBTime directly, bypassing replaykit entirely -- without this, the
        // custom settings dock keeps showing (and would re-apply) whatever replaykit last wrote, silently
        // reverting the user's obs-side edit on the next apply.
        private static JObject SyncReplayBufferSecondsFromObs(JObject settings)
        {
            var rb = GetObsProfileParameterValue("AdvOut", "RecRBTime");
            if (rb["ok"]?.Value<bool>() != true) return settings;
            if (!int.TryParse(rb["value"]?.Value<string>(), out int seconds)) return settings;
            if (seconds <= 0 || seconds == settings["replaySeconds"]?.Value<int>()) return settings;
            settings["replaySeconds"] = seconds;
            settings["clipNotificationSeconds"] = seconds;
            WriteSettings(settings);
            return settings;
        }

        // recording_hotkey.lua fast-polls this file (~50ms, separate from its normal 1s saved-settings sync) and
        // applies it via obs_hotkey_load, which rewrites the live in-memory hotkey binding immediately. this is not
        // cosmetic like SetProfileParameter below -- SetProfileParameter only ever reaches the on-disk profile ini,
        // and obs only rereads that ini into its live hotkey registry on a full profile switch, so it could never
        // actually stop the native hotkey from firing while capture was "active". obs_hotkey_load is the same api
        // the lua script already relies on to make a saved keybind change take effect without restarting obs.
        private static string GetHotkeyCaptureSignalPath() => Path.Combine(GetScriptsDir(), "hotkey_capture_signal.json");

        private static void WriteHotkeyCaptureSignal(JObject signal) => AppConfig.WriteUtf8(GetHotkeyCaptureSignalPath(), signal.ToString(Formatting.None));

        // called once at helper startup so a signal file left at active:true by a crashed prior helper process
        // (obs itself keeps running independently of the helper, so its lua side has no equivalent restart to reset on) does not leave the native hotkeys blanked forever.
        public static void ResetHotkeyCaptureSignalAtStartup() => WriteHotkeyCaptureSignal(new JObject { ["active"] = false });

        public static JObject SetHotkeyCapture(bool active)
        {
            if (active)
            {
                lock (Server.State.HotkeyCaptureLock)
                {
                    Server.State.ReplaykitHotkeyCaptureActive = true;
                }
                WriteHotkeyCaptureSignal(new JObject { ["active"] = true });
                return new JObject { ["ok"] = true, ["active"] = true };
            }

            if (!Server.State.ReplaykitHotkeyCaptureActive) return new JObject { ["ok"] = true, ["active"] = false };

            var currentSettings = ReadSettings();
            return RestoreHotkeysFromSettings(currentSettings);
        }

        private static JObject RestoreHotkeysFromSettings(JObject settings)
        {
            string clipJson = ConvertClipKeybindToBasicIni(settings["clipKeybind"] as JObject);
            string recordingJson = ConvertRecordingKeybindToBasicIni(settings["recordingKeybind"] as JObject);
            var r1 = SetReplayBufferHotkeyJson(clipJson);
            var r2 = SetRecordingHotkeyPairJson(recordingJson, recordingJson);
            var errors = new List<string>();
            if (!r1.Ok) errors.Add(r1.Message);
            if (r2["ok"]?.Value<bool>() != true) errors.Add(r2["message"]?.Value<string>());

            // unconditional even if the ini writes above failed -- those are best-effort disk consistency, not what
            // actually restores the live binding, so a websocket hiccup should not leave the native hotkey blanked.
            WriteHotkeyCaptureSignal(new JObject
            {
                ["active"] = false,
                ["clipKeybind"] = settings["clipKeybind"] as JObject ?? new JObject(),
                ["recordingKeybind"] = settings["recordingKeybind"] as JObject ?? new JObject(),
            });
            lock (Server.State.HotkeyCaptureLock)
            {
                Server.State.ReplaykitHotkeyCaptureActive = false;
            }

            if (errors.Count > 0) return new JObject { ["ok"] = false, ["message"] = string.Join("; ", errors) };
            return new JObject { ["ok"] = true, ["active"] = false };
        }

        private static JObject EnsureHotkeyCaptureReleased(JObject settings)
        {
            if (!Server.State.ReplaykitHotkeyCaptureActive) return new JObject { ["ok"] = true, ["warnings"] = new JArray(), ["applied"] = new JArray() };
            var restore = RestoreHotkeysFromSettings(settings);
            if (restore["ok"]?.Value<bool>() == true) return new JObject { ["ok"] = true, ["warnings"] = new JArray(), ["applied"] = new JArray("OBS hotkeys restored") };
            return new JObject { ["ok"] = false, ["warnings"] = new JArray("OBS hotkeys could not be restored: " + restore["message"]), ["applied"] = new JArray() };
        }

        // -- master orchestrator: given the new settings, do the minimum live work to bring a running obs (and
        // its on-disk profile/ini config) in line with them. screenshare-capture and discord-output live-apply
        // run unconditionally on every call, not gated behind applyOverlay/restartObs the way overlay and motion
        // blur are -- there is deliberately no "prepare for restart via file edit instead" branch for those two;
        // preserve that asymmetry, it is not an oversight to fix.
        private static JObject ApplyLiveSettings(JObject settings, bool restartObs = false, bool applyOverlay = true, bool recreateBongo = true, bool applyMotionBlur = true, bool applyRuntimeOutputs = true, bool applyVideoSettings = true, bool applyReplayBufferOutput = true)
        {
            var warnings = new List<string>();
            var applied = new List<string>();
            var preset = GetPresetSpec(settings["recordingPreset"]?.Value<string>() ?? "", settings);
            var encoder = GetEncoderSpec(settings, preset);
            string encoderWarning = encoder["warning"]?.Value<string>();
            if (!string.IsNullOrEmpty(encoderWarning)) warnings.Add(encoderWarning);
            if ((settings["motionBlurEnabled"]?.Value<bool>() ?? false) && !TestShaderfilterPluginInstalled())
                warnings.Add("Motion blur was saved, but OBS Shaderfilter is not installed. Re-run ReplayKit setup, then restart OBS after the plugin install.");

            try
            {
                string clipDir = settings["clipDir"]?.Value<string>();
                if (!string.IsNullOrEmpty(clipDir)) Directory.CreateDirectory(clipDir);
                UpdateHelperConfigClipDir(clipDir);
                applied.Add("clip folder");
            }
            catch (Exception ex)
            {
                warnings.Add("Clip folder was saved, but the helper could not switch to it: " + ex.Message);
            }

            var startup = SetObsStartupSetting(settings["obsStartupEnabled"]?.Value<bool>() ?? false);
            if (startup["ok"]?.Value<bool>() == true) applied.Add("Windows startup");
            else warnings.Add("Windows startup was saved, but Windows rejected the change: " + startup["message"]);

            var closeWarning = SetObsCloseWarningConfig(settings["disableObsCloseWarning"]?.Value<bool>() ?? false);
            if (closeWarning["ok"]?.Value<bool>() == true) applied.Add("OBS close warning");
            else warnings.Add("OBS close warning was saved, but OBS config could not be updated: " + closeWarning["message"]);

            var sleepOverride = ApplySleepOverrideSetting(settings["allowSleepWhileActive"]?.Value<bool>() ?? false);
            applied.AddRange((sleepOverride["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
            warnings.AddRange((sleepOverride["warnings"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());

            var profileUpdates = new List<string[]>();
            foreach (var u in preset["profile"] as JArray ?? new JArray())
            {
                var arr = (JArray)u;
                profileUpdates.Add(new[] { arr[0].Value<string>(), arr[1].Value<string>(), arr[2].Value<string>() });
            }
            int replaySeconds = settings["replaySeconds"]?.Value<int>() ?? 0;
            profileUpdates.Add(new[] { "AdvOut", "RecRB", "true" });
            profileUpdates.Add(new[] { "AdvOut", "RecRBTime", replaySeconds.ToString() });
            profileUpdates.Add(new[] { "AdvOut", "RecRBSize", ScaledRbSizeMb(settings["recordingPreset"]?.Value<string>() ?? "", replaySeconds).ToString(CultureInfo.InvariantCulture) });
            profileUpdates.Add(new[] { "AdvOut", "RecEncoder", encoder["id"]?.Value<string>() ?? "" });
            profileUpdates.Add(new[] { "Hotkeys", "ReplayBuffer", ConvertClipKeybindToBasicIni(settings["clipKeybind"] as JObject) });
            string recordingHotkey = ConvertRecordingKeybindToBasicIni(settings["recordingKeybind"] as JObject);
            profileUpdates.Add(new[] { "Hotkeys", "OBSBasic.StartRecording", recordingHotkey });
            profileUpdates.Add(new[] { "Hotkeys", "OBSBasic.StopRecording", recordingHotkey });
            // always pin obs's recording folder to the RESOLVED clip dir (custom or the default) -- the helper's clip
            // watcher/scanner uses that same resolved dir, so if obs's own RecFilePath ever drifts (it defaults to
            // %USERPROFILE%\Videos, not our Pictures\Videos) clips land where nothing is looking for them.
            string clipDirValue = settings["clipDir"]?.Value<string>();
            string resolvedClipDir = string.IsNullOrWhiteSpace(clipDirValue) ? AppConfig.GetDefaultClipDir() : clipDirValue;
            try { Directory.CreateDirectory(resolvedClipDir); } catch (Exception ex) { Log.Write("resolvedClipDir mkdir: " + ex.Message); }
            profileUpdates.Add(new[] { "SimpleOutput", "FilePath", resolvedClipDir });
            profileUpdates.Add(new[] { "AdvOut", "RecFilePath", resolvedClipDir });
            profileUpdates.Add(new[] { "AdvOut", "FFFilePath", resolvedClipDir });

            var encoderWrite = WriteRecordEncoderJson(encoder);
            if (encoderWrite["ok"]?.Value<bool>() == true) applied.Add("recording encoder settings");
            else warnings.Add("Recording codec was saved, but encoder settings could not be written: " + encoderWrite["message"]);

            foreach (var u in profileUpdates)
            {
                var r = SetObsProfileParameterSafe(u[0], u[1], u[2]);
                if (!r.Ok) warnings.Add("OBS did not accept " + u[0] + "." + u[1] + ": " + r.Message);
            }

            applied.Add("OBS profile settings");
            applied.Add("OBS recording folder");

            if (applyRuntimeOutputs)
            {
                var outputs = ApplyRuntimeOutputsLive(settings, preset, restartObs, applyVideoSettings, applyReplayBufferOutput);
                applied.AddRange((outputs["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                warnings.AddRange((outputs["warnings"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
            }

            var screenshareCapture = ApplyScreenshareCaptureLive(settings, preset);
            applied.AddRange((screenshareCapture["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
            warnings.AddRange((screenshareCapture["warnings"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());

            var discordOutput = ApplyDiscordOutputLive(settings);
            applied.AddRange((discordOutput["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
            warnings.AddRange((discordOutput["warnings"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());

            if (applyOverlay || applyMotionBlur)
            {
                if (restartObs)
                {
                    var overlayFile = SetOverlaySceneFile(settings, preset);
                    if (overlayFile["ok"]?.Value<bool>() == true) applied.Add("OBS overlay scene file");
                    else warnings.Add("Overlay setting was saved, but the OBS scene file could not be prepared for restart: " + overlayFile["message"]);
                }
                else
                {
                    if (applyOverlay)
                    {
                        var overlay = ApplyOverlayLive(settings, preset, recreateBongo);
                        if (overlay["ok"]?.Value<bool>() == true) applied.Add("OBS overlay");
                        else warnings.AddRange((overlay["warnings"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                    }
                    if (applyMotionBlur)
                    {
                        var motionBlur = ApplyMotionBlurLive(settings);
                        applied.AddRange((motionBlur["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                        warnings.AddRange((motionBlur["warnings"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                    }
                }
            }

            string restartReason = restartObs ? "Recording quality, GPU-use, clip-size, codec, theme, or overlay changes require OBS to restart." : "";

            return new JObject
            {
                ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings),
                ["restartRequired"] = restartObs, ["restartReason"] = restartReason,
            };
        }

        // -- http-facing settings payload + change-detection + save-request handler: the dock's initial page load
        // fetches GetSettingsPayload; every settings-form submit goes through SaveSettingsFromRequest, which picks
        // the cheapest live-apply path that actually covers what changed instead of always running the full
        // ApplyLiveSettings orchestrator.

        // resolved theme colours for the tray menu's custom keybind rows -- text is the normal (at-rest) label colour (a widget stylesheet on the row otherwise breaks the label's inherited menu colour), accentLight + onAccent are the active/hovered fill + ink so the rows match a native QMenu::item:selected
        private static JObject ThemeMenuColors(JObject settings)
        {
            try
            {
                var t = Themes.Resolve(settings);
                return new JObject
                {
                    ["text"] = t.Text,
                    ["accent"] = t.Accent,
                    ["accentLight"] = t.Accent2,
                    ["onAccent"] = Themes.OnColor(t.Accent),
                };
            }
            catch { return new JObject(); }
        }

        public static JObject GetSettingsPayload()
        {
            AppConfig.LoadConfig();
            var settings = ReadSettings();
            settings = SyncHotkeysFromObs(settings);
            settings = SyncReplayBufferSecondsFromObs(settings);
            if (string.IsNullOrWhiteSpace(settings["clipDir"]?.Value<string>()) && !string.IsNullOrWhiteSpace(Server.State.Config?["clipDir"]?.Value<string>()))
            {
                settings["clipDir"] = ResolveClipDirSetting(Server.State.Config["clipDir"]?.Value<string>());
            }
            var overlayOpacity = GetLiveOverlayOpacityPercent(settings);
            if (overlayOpacity["ok"]?.Value<bool>() != true) overlayOpacity = GetSceneFileOverlayOpacityPercent(settings);
            if (overlayOpacity["ok"]?.Value<bool>() == true && settings["overlayOpacity"]?.Value<int>() != overlayOpacity["opacity"]?.Value<int>())
            {
                settings["overlayOpacity"] = overlayOpacity["opacity"];
                WriteSettings(settings);
            }
            return new JObject
            {
                ["ok"] = true,
                ["settings"] = settings,
                ["menuColors"] = ThemeMenuColors(settings),
                ["options"] = new JObject
                {
                    ["recordingPresets"] = new JArray(
                        new JObject { ["value"] = "performance", ["label"] = "Performance", ["blurb"] = "720p30. Lowest load and smaller files." },
                        new JObject { ["value"] = "balanced", ["label"] = "Balanced", ["blurb"] = "1080p60. Recommended for most PCs." },
                        new JObject { ["value"] = "quality", ["label"] = "Quality", ["blurb"] = "Higher-quality target for high-end PCs." }
                    ),
                    ["compressionModes"] = new JArray(
                        new JObject { ["value"] = "lower_gpu", ["label"] = "Lowest GPU use", ["blurb"] = "Least encoder work. Larger clips." },
                        new JObject { ["value"] = "balanced", ["label"] = "Balanced", ["blurb"] = "Good file size with modest encoder load." },
                        new JObject { ["value"] = "smaller_files", ["label"] = "Smallest clips", ["blurb"] = "More encoder work for tighter files." }
                    ),
                    ["codecs"] = new JArray(
                        new JObject { ["value"] = "auto", ["label"] = "Auto", ["blurb"] = "ReplayKit picks the best supported encoder." },
                        new JObject { ["value"] = "h264", ["label"] = "H.264", ["blurb"] = "Largest files, broadest playback support." },
                        new JObject { ["value"] = "h265", ["label"] = "HEVC", ["blurb"] = "Smaller files on modern GPUs." }
                    ),
                    ["recordingScaleModes"] = new JArray(
                        new JObject { ["value"] = "native", ["label"] = "Native", ["blurb"] = "Record at your monitor's full resolution. Sharpest image, but more GPU load and bigger files." },
                        new JObject { ["value"] = "downscale", ["label"] = "Downscale", ["blurb"] = "Shrink the recording to the size below. Lighter on the GPU and smaller files, slightly softer." }
                    ),
                    ["downscaleResolutions"] = BuildDownscaleResolutionList(),
                    ["downscaleFilters"] = new JArray(
                        new JObject { ["value"] = "lanczos", ["label"] = "Lanczos", ["blurb"] = "Sharpest, 36-sample. Default." },
                        new JObject { ["value"] = "bicubic", ["label"] = "Bicubic", ["blurb"] = "16-sample, slightly softer than Lanczos." },
                        new JObject { ["value"] = "area", ["label"] = "Area", ["blurb"] = "Clean average, no sharpening pass." },
                        new JObject { ["value"] = "bilinear", ["label"] = "Bilinear", ["blurb"] = "Fastest, softest." }
                    ),
                    ["overlays"] = new JArray(
                        new JObject { ["value"] = "input_overlay", ["label"] = "WASD / mouse", ["blurb"] = "Simple keyboard and mouse overlay." },
                        new JObject { ["value"] = "bongo_cat", ["label"] = "Bongo Cat", ["blurb"] = "Animated keyboard and mouse overlay." },
                        new JObject { ["value"] = "off", ["label"] = "Off", ["blurb"] = "No input overlay in the OBS scene." }
                    ),
                    ["screenshareCaptureModes"] = new JArray(
                        new JObject { ["value"] = "hybrid_auto", ["label"] = "Auto", ["blurb"] = "Desktop fallback with fullscreen Game Capture on top." },
                        new JObject { ["value"] = "desktop", ["label"] = "Desktop", ["blurb"] = "Show the full ReplayKit desktop capture." },
                        new JObject { ["value"] = "game_auto", ["label"] = "Game only", ["blurb"] = "Use OBS Game Capture for any fullscreen game." },
                        new JObject { ["value"] = "game_window", ["label"] = "Specific game", ["blurb"] = "Use Window Capture for the selected game window." }
                    ),
                    ["screenshareGameWindows"] = new JArray(GetGameWindowCandidates(settings["screenshareGameWindow"]?.Value<string>() ?? "")),
                    ["discordOutputModes"] = new JArray(
                        new JObject { ["value"] = "projector", ["label"] = "Projector", ["blurb"] = "OBS Windowed Projector parked by ReplayKit." }
                    ),
                    ["keybinds"] = new JArray(
                        new JObject { ["value"] = "shift_backslash", ["label"] = "Shift + \\", ["blurb"] = "Default ReplayKit save hotkey.", ["combo"] = new JObject { ["shift"] = true, ["key"] = "OBS_KEY_BACKSLASH" } },
                        new JObject { ["value"] = "ctrl_shift_s", ["label"] = "Ctrl + Shift + S", ["blurb"] = "Easy to remember, uses two modifiers.", ["combo"] = new JObject { ["control"] = true, ["shift"] = true, ["key"] = "OBS_KEY_S" } },
                        new JObject { ["value"] = "f8", ["label"] = "F8", ["blurb"] = "Single function key.", ["combo"] = new JObject { ["key"] = "OBS_KEY_F8" } },
                        new JObject { ["value"] = "f9", ["label"] = "F9", ["blurb"] = "Single function key.", ["combo"] = new JObject { ["key"] = "OBS_KEY_F9" } },
                        new JObject { ["value"] = "f10", ["label"] = "F10", ["blurb"] = "Single function key.", ["combo"] = new JObject { ["key"] = "OBS_KEY_F10" } }
                    ),
                },
            };
        }

        private static bool TestRestartRequired(JObject previous, JObject settings)
        {
            // recordingScaleMode + downscaleHeight apply live (stop outputs -> SetVideoSettings -> restart outputs, see
            // ApplyRuntimeOutputsLive). downscaleFilter still needs a restart -- obs only re-reads Video/ScaleType from
            // the profile ini on load, SetVideoSettings has no scale-type parameter.
            foreach (var key in new[] { "recordingPreset", "compressionMode", "codecPreference", "fpsNumerator", "fpsDenominator", "downscaleFilter" })
            {
                if (previous[key]?.ToString() != settings[key]?.ToString()) return true;
            }
            // theme: obs only reads user.ini [Appearance] Theme= at startup, so a theme change needs a restart to reach obs (the dock + replaykit windows re-theme live on the fresh load).
            if (previous["theme"]?.ToString() != settings["theme"]?.ToString()) return true;
            string theme = settings["theme"]?.ToString() ?? "default";
            if ((theme == "custom" || theme.StartsWith("user/", StringComparison.Ordinal)) && !JToken.DeepEquals(previous["themeCustom"], settings["themeCustom"])) return true;
            return previous["overlayStyle"]?.ToString() != settings["overlayStyle"]?.ToString();
        }

        private static bool TestRuntimeVideoSettingsChanged(JObject previous, JObject settings)
        {
            return previous["recordingPreset"]?.ToString() != settings["recordingPreset"]?.ToString() ||
                previous["fpsNumerator"]?.ToString() != settings["fpsNumerator"]?.ToString() ||
                previous["fpsDenominator"]?.ToString() != settings["fpsDenominator"]?.ToString() ||
                previous["recordingScaleMode"]?.ToString() != settings["recordingScaleMode"]?.ToString() ||
                previous["downscaleHeight"]?.ToString() != settings["downscaleHeight"]?.ToString() ||
                previous["downscaleFilter"]?.ToString() != settings["downscaleFilter"]?.ToString();
        }

        private static bool TestReplayBufferOutputChanged(JObject previous, JObject settings)
        {
            foreach (var key in new[] { "recordingPreset", "replaySeconds", "clipDir" })
            {
                if (previous[key]?.ToString() != settings[key]?.ToString()) return true;
            }
            return false;
        }

        private static bool TestShareModeOnlyRequest(JObject incoming) => incoming.Count == 1 && incoming["shareMode"] != null;

        private static readonly string[] DiscordOutputOnlyAllowedKeys =
        {
            "shareMode", "discord_screenshare_enabled", "discord_output_mode", "discord_projector_enabled",
            "discord_projector_width", "discord_projector_height", "discord_projector_visible_pixels",
            "discord_projector_monitor_index", "discord_projector_edge", "discord_projector_title_hint", "discord_projector_hide_taskbar",
        };

        private static bool TestDiscordOutputOnlyRequest(JObject incoming)
        {
            if (incoming.Count < 1) return false;
            foreach (var prop in incoming.Properties())
            {
                if (!DiscordOutputOnlyAllowedKeys.Contains(prop.Name)) return false;
            }
            return true;
        }

        private static readonly string[] ScreenshareCaptureOnlyAllowedKeys =
            { "screenshareCaptureMode", "screenshareGameWindow", "screenshareGameOverrides", "screenshareAutoGameKeepFocused", "screenshareSwitchDelaySeconds" };

        private static bool TestScreenshareCaptureOnlyRequest(JObject incoming)
        {
            if (incoming.Count < 1) return false;
            foreach (var prop in incoming.Properties())
            {
                if (!ScreenshareCaptureOnlyAllowedKeys.Contains(prop.Name)) return false;
            }
            return true;
        }

        private static bool TestIncomingSettingsChanged(JObject incoming, JObject previous, JObject settings)
        {
            foreach (var prop in incoming.Properties())
            {
                if (!JToken.DeepEquals(previous[prop.Name], settings[prop.Name])) return true;
            }
            return false;
        }

        private static bool TestOnlyMotionBlurChanged(JObject previous, JObject settings)
        {
            foreach (var prop in GetDefaultSettings().Properties())
            {
                if (prop.Name == "motionBlurEnabled" || prop.Name == "motionBlurStrength") continue;
                if (!JToken.DeepEquals(previous[prop.Name], settings[prop.Name])) return false;
            }
            return previous["motionBlurEnabled"]?.Value<bool>() != settings["motionBlurEnabled"]?.Value<bool>() ||
                previous["motionBlurStrength"]?.Value<double>() != settings["motionBlurStrength"]?.Value<double>();
        }

        private static bool TestOnlyAppIconChanged(JObject previous, JObject settings)
        {
            foreach (var prop in GetDefaultSettings().Properties())
            {
                if (prop.Name == "appIcon" || prop.Name == "appIconCustomPath" || prop.Name == "appIconRecordingDot") continue;
                if (!JToken.DeepEquals(previous[prop.Name], settings[prop.Name])) return false;
            }
            return previous["appIcon"]?.Value<string>() != settings["appIcon"]?.Value<string>() ||
                previous["appIconCustomPath"]?.Value<string>() != settings["appIconCustomPath"]?.Value<string>() ||
                previous["appIconRecordingDot"]?.Value<bool>() != settings["appIconRecordingDot"]?.Value<bool>();
        }

        // pushes the resolved app icon to replaykit's own windows (helper) and to obs's window + taskbar + system tray
        // (tray plugin over the ipc pipe). the obs main window is the plugin's job alone now -- the helper's own
        // WM_SETICON on it raced the plugin at 16/32px and kept the win11 taskbar button small. all live, no restart.
        public static JObject ApplyAppIconLive(JObject settings)
        {
            string path = ResolveAppIconPath(settings);
            bool dot = settings["appIconRecordingDot"]?.Value<bool>() ?? true;
            var applied = new List<string>();
            try { Native.SetReplayKitWindowIcons(path); applied.Add("ReplayKit window icons"); }
            catch (Exception ex) { Log.Write("ApplyAppIconLive: ReplayKit windows: " + ex.Message); }
            try { PipeClient.SendSetIcon(path); PipeClient.SendSetIconDot(dot); applied.Add("OBS window + tray icon"); }
            catch (Exception ex) { Log.Write("ApplyAppIconLive: OBS icon: " + ex.Message); }
            return new JObject { ["ok"] = true, ["applied"] = new JArray(applied), ["warnings"] = new JArray() };
        }

        // re-assert the chosen icon after the helper (re)starts with obs. obs's main window isnt necessarily up yet, so poll briefly; the tray plugin also gets it via the pipe-connect send.
        public static void ApplyAppIconAtStartup()
        {
            JObject settings;
            try { settings = Normalize(ReadSettings()); }
            catch (Exception ex) { Log.Write("ApplyAppIconAtStartup: read: " + ex.Message); return; }
            if ((settings["appIcon"]?.Value<string>() ?? "default") == "default") return;
            Task.Run(() =>
            {
                try
                {
                    for (int i = 0; i < 30; i++)
                    {
                        long hwndVal;
                        lock (Server.State.IpcLock) hwndVal = Server.State.ObsMainWindowHwnd;
                        if (hwndVal != 0) { ApplyAppIconLive(settings); return; }
                        Thread.Sleep(1000);
                    }
                    ApplyAppIconLive(settings); // last try even without the hwnd -- covers the replaykit windows + tray
                }
                catch (Exception ex) { Log.Write("ApplyAppIconAtStartup: " + ex.Message); }
            });
        }

        // self-heal: obs's recording folder (SimpleOutput.FilePath / AdvOut.RecFilePath) defaults to %USERPROFILE%\Videos,
        // but the helper watches + scans the RESOLVED clip dir (custom, or our Pictures\Videos default). if obs's own
        // path drifts -- a reset profile, an obs update, a mid-session force-kill -- new replay clips land where nothing
        // is looking. runs deferred (obs websocket needs a moment) and only writes on an actual mismatch.
        public static void EnsureObsRecordingFolderAtStartup()
        {
            Task.Run(() =>
            {
                try
                {
                    string clipDir;
                    try { clipDir = Normalize(ReadSettings())["clipDir"]?.Value<string>(); }
                    catch { clipDir = null; }
                    string resolved = string.IsNullOrWhiteSpace(clipDir) ? AppConfig.GetDefaultClipDir() : clipDir;
                    try { resolved = Path.GetFullPath(resolved); } catch { }
                    try { Directory.CreateDirectory(resolved); } catch (Exception ex) { Log.Write("EnsureObsRecordingFolder mkdir: " + ex.Message); }

                    for (int i = 0; i < 30; i++)
                    {
                        var cur = GetObsProfileParameterValue("AdvOut", "RecFilePath");
                        if (cur["ok"]?.Value<bool>() == true)
                        {
                            string curPath = cur["value"]?.Value<string>() ?? "";
                            string curNorm; try { curNorm = Path.GetFullPath(curPath); } catch { curNorm = curPath; }
                            if (!string.Equals(curNorm.TrimEnd('\\', '/'), resolved.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                            {
                                SetObsProfileParameterSafe("SimpleOutput", "FilePath", resolved);
                                SetObsProfileParameterSafe("AdvOut", "RecFilePath", resolved);
                                SetObsProfileParameterSafe("AdvOut", "FFFilePath", resolved);
                                // the already-running Replay Buffer output caches its dir from init -- update it live too.
                                try
                                {
                                    var rbEx = ObsWebSocket.InvokeRequest("GetOutputSettings", new JObject { ["outputName"] = "Replay Buffer" }, 3000);
                                    JObject rbSet = rbEx.Ok && rbEx.Data?["outputSettings"] is JObject e ? (JObject)e.DeepClone() : new JObject();
                                    rbSet["directory"] = resolved;
                                    rbSet["path"] = resolved;
                                    ObsWebSocket.InvokeRequest("SetOutputSettings", new JObject { ["outputName"] = "Replay Buffer", ["outputSettings"] = rbSet }, 5000);
                                }
                                catch (Exception ex) { Log.Write("EnsureObsRecordingFolder live output: " + ex.Message); }
                                Log.Write("EnsureObsRecordingFolder: OBS recording folder was '" + curPath + "', pinned to '" + resolved + "'");
                            }
                            return;
                        }
                        Thread.Sleep(1000);
                    }
                }
                catch (Exception ex) { Log.Write("EnsureObsRecordingFolderAtStartup: " + ex.Message); }
            });
        }

        private static bool TestOnlyOverlayVisualChanged(JObject previous, JObject settings)
        {
            var skip = new HashSet<string> { "overlayOpacity", "overlayScale", "overlayFlipH", "overlayHueShift", "overlayColorMultiply", "overlayColorAdd" };
            foreach (var prop in GetDefaultSettings().Properties())
            {
                if (skip.Contains(prop.Name)) continue;
                if (!JToken.DeepEquals(previous[prop.Name], settings[prop.Name])) return false;
            }
            return previous["overlayOpacity"]?.Value<int>() != settings["overlayOpacity"]?.Value<int>() ||
                previous["overlayScale"]?.Value<int>() != settings["overlayScale"]?.Value<int>() ||
                previous["overlayFlipH"]?.Value<bool>() != settings["overlayFlipH"]?.Value<bool>() ||
                previous["overlayHueShift"]?.Value<double>() != settings["overlayHueShift"]?.Value<double>() ||
                previous["overlayColorMultiply"]?.Value<string>() != settings["overlayColorMultiply"]?.Value<string>() ||
                previous["overlayColorAdd"]?.Value<string>() != settings["overlayColorAdd"]?.Value<string>();
        }

        private static bool TestOnlyScreenshareCaptureChanged(JObject previous, JObject settings)
        {
            var skip = new HashSet<string> { "screenshareCaptureMode", "screenshareGameWindow", "screenshareGameOverrides", "screenshareAutoGameKeepFocused", "screenshareSwitchDelaySeconds" };
            foreach (var prop in GetDefaultSettings().Properties())
            {
                if (skip.Contains(prop.Name)) continue;
                if (!JToken.DeepEquals(previous[prop.Name], settings[prop.Name])) return false;
            }
            return previous["screenshareCaptureMode"]?.Value<string>() != settings["screenshareCaptureMode"]?.Value<string>() ||
                previous["screenshareGameWindow"]?.Value<string>() != settings["screenshareGameWindow"]?.Value<string>() ||
                previous["screenshareAutoGameKeepFocused"]?.Value<bool>() != settings["screenshareAutoGameKeepFocused"]?.Value<bool>() ||
                previous["screenshareSwitchDelaySeconds"]?.Value<double>() != settings["screenshareSwitchDelaySeconds"]?.Value<double>() ||
                !JToken.DeepEquals(previous["screenshareGameOverrides"], settings["screenshareGameOverrides"]);
        }

        private static bool TestOnlySleepOverrideChanged(JObject previous, JObject settings)
        {
            foreach (var prop in GetDefaultSettings().Properties())
            {
                if (prop.Name == "allowSleepWhileActive") continue;
                if (!JToken.DeepEquals(previous[prop.Name], settings[prop.Name])) return false;
            }
            return previous["allowSleepWhileActive"]?.Value<bool>() != settings["allowSleepWhileActive"]?.Value<bool>();
        }

        private static JArray ConcatArrays(JArray a, JArray b)
        {
            var result = new JArray();
            if (a != null) foreach (var item in a) result.Add(item);
            if (b != null) foreach (var item in b) result.Add(item);
            return result;
        }

        public static JObject SaveOverlayPreviewFromRequest(string body) => OverlayPreviewFromRequest(body, "preview");

        // restartRequested (the ?restart=1 query flag Routes.cs passes thru) is intentionally unused below --
        // this mirrors the ps original, where the same-named parameter is shadowed by a locally-computed
        // restartObs near the end of the function and the incoming value never actually influences anything.
        // preserved as-is rather than wired up, since it is not clear whether that is a latent bug or deliberate.
        public static JObject SaveSettingsFromRequest(string body, bool restartRequested = false)
        {
            if (string.IsNullOrWhiteSpace(body)) throw new InvalidOperationException("Missing settings body.");
            var incoming = JObject.Parse(body);
            var current = ReadSettings();
            var previous = Normalize(current);
            foreach (var prop in incoming.Properties())
            {
                if (current[prop.Name] == null) throw new InvalidOperationException("Unknown setting: " + prop.Name);
                current[prop.Name] = prop.Value;
            }
            var settings = Normalize(current);
            // a fresh "custom" pick -> convert + file it as a saved preset, then carry on as if the user had selected that preset. subsequent saves send the "user/..." id directly and skip this.
            if ((settings["appIcon"]?.Value<string>() ?? "") == "custom")
            {
                string imported = ImportCustomIcon(settings["appIconCustomPath"]?.Value<string>() ?? "");
                if (imported != null)
                {
                    settings["appIcon"] = imported;
                    settings["appIconCustomPath"] = "";
                    settings = Normalize(settings);
                }
            }
            // takes effect on this already-running helper the moment settings are saved, not just after the next reload -- Log.Write gates on this flag on every call, so flipping it here is what actually lets someone enable logging, reproduce a bug, and have it show up without restarting obs.
            Server.State.LogEnabled = settings["debugLoggingEnabled"]?.Value<bool>() ?? false;
            var hotkeyRelease = EnsureHotkeyCaptureReleased(settings);
            if (!TestIncomingSettingsChanged(incoming, previous, settings))
            {
                if (TestOverlayColorRequest(incoming))
                {
                    var live = ApplyOverlayOpacityForStyleLive(settings);
                    return new JObject
                    {
                        ["ok"] = true, ["settings"] = settings,
                        ["applied"] = ConcatArrays(hotkeyRelease["applied"] as JArray, live["applied"] as JArray),
                        ["warnings"] = ConcatArrays(hotkeyRelease["warnings"] as JArray, live["warnings"] as JArray),
                        ["restartRequired"] = false, ["restartReason"] = "",
                    };
                }
                return new JObject
                {
                    ["ok"] = true, ["settings"] = settings,
                    ["applied"] = hotkeyRelease["applied"], ["warnings"] = hotkeyRelease["warnings"],
                    ["restartRequired"] = false, ["restartReason"] = "",
                };
            }
            if (TestScreenshareCaptureOnlyRequest(incoming))
            {
                WriteSettings(settings);
                var preset = GetPresetSpec(settings["recordingPreset"]?.Value<string>() ?? "", settings);
                var live = ApplyScreenshareCaptureLive(settings, preset);
                return new JObject
                {
                    ["ok"] = true, ["settings"] = settings,
                    ["applied"] = ConcatArrays(hotkeyRelease["applied"] as JArray, live["applied"] as JArray),
                    ["warnings"] = ConcatArrays(hotkeyRelease["warnings"] as JArray, live["warnings"] as JArray),
                    ["restartRequired"] = false, ["restartReason"] = "",
                };
            }
            if (TestDiscordOutputOnlyRequest(incoming))
            {
                var live = ApplyDiscordOutputLive(settings);
                if (live["ok"]?.Value<bool>() != true)
                {
                    return new JObject
                    {
                        ["ok"] = false, ["settings"] = previous, ["applied"] = live["applied"], ["warnings"] = live["warnings"],
                        ["message"] = live["message"], ["restartRequired"] = false, ["restartReason"] = "",
                    };
                }
                WriteSettings(settings);
                return new JObject
                {
                    ["ok"] = true, ["settings"] = settings,
                    ["applied"] = ConcatArrays(hotkeyRelease["applied"] as JArray, live["applied"] as JArray),
                    ["warnings"] = ConcatArrays(hotkeyRelease["warnings"] as JArray, live["warnings"] as JArray),
                    ["restartRequired"] = false, ["restartReason"] = "",
                };
            }
            WriteSettings(settings);
            if (TestOnlySleepOverrideChanged(previous, settings))
            {
                var live = ApplySleepOverrideSetting(settings["allowSleepWhileActive"]?.Value<bool>() ?? false);
                return new JObject
                {
                    ["ok"] = true, ["settings"] = settings,
                    ["applied"] = ConcatArrays(hotkeyRelease["applied"] as JArray, live["applied"] as JArray),
                    ["warnings"] = ConcatArrays(hotkeyRelease["warnings"] as JArray, live["warnings"] as JArray),
                    ["restartRequired"] = false, ["restartReason"] = "",
                };
            }
            if (TestOnlyMotionBlurChanged(previous, settings))
            {
                var live = ApplyMotionBlurLive(settings);
                return new JObject
                {
                    ["ok"] = true, ["settings"] = settings,
                    ["applied"] = ConcatArrays(hotkeyRelease["applied"] as JArray, live["applied"] as JArray),
                    ["warnings"] = ConcatArrays(hotkeyRelease["warnings"] as JArray, live["warnings"] as JArray),
                    ["restartRequired"] = false, ["restartReason"] = "",
                };
            }
            if (TestOnlyOverlayVisualChanged(previous, settings))
            {
                var live = ApplyOverlayVisualSettingsLive(previous, settings);
                return new JObject
                {
                    ["ok"] = true, ["settings"] = settings,
                    ["applied"] = ConcatArrays(hotkeyRelease["applied"] as JArray, live["applied"] as JArray),
                    ["warnings"] = ConcatArrays(hotkeyRelease["warnings"] as JArray, live["warnings"] as JArray),
                    ["restartRequired"] = false, ["restartReason"] = "",
                };
            }
            if (TestOnlyScreenshareCaptureChanged(previous, settings))
            {
                var preset = GetPresetSpec(settings["recordingPreset"]?.Value<string>() ?? "", settings);
                var live = ApplyScreenshareCaptureLive(settings, preset);
                return new JObject
                {
                    ["ok"] = true, ["settings"] = settings,
                    ["applied"] = ConcatArrays(hotkeyRelease["applied"] as JArray, live["applied"] as JArray),
                    ["warnings"] = ConcatArrays(hotkeyRelease["warnings"] as JArray, live["warnings"] as JArray),
                    ["restartRequired"] = false, ["restartReason"] = "",
                };
            }
            if (TestOnlyAppIconChanged(previous, settings))
            {
                var live = ApplyAppIconLive(settings);
                return new JObject
                {
                    ["ok"] = true, ["settings"] = settings,
                    ["applied"] = ConcatArrays(hotkeyRelease["applied"] as JArray, live["applied"] as JArray),
                    ["warnings"] = ConcatArrays(hotkeyRelease["warnings"] as JArray, live["warnings"] as JArray),
                    ["restartRequired"] = false, ["restartReason"] = "",
                };
            }
            // theme change -> write the obs .ovt variant + user.ini key now, before ApplyLiveSettings restarts obs to pick it up.
            string nextTheme = settings["theme"]?.ToString() ?? "default";
            if (previous["theme"]?.ToString() != nextTheme ||
                ((nextTheme == "custom" || nextTheme.StartsWith("user/", StringComparison.Ordinal)) && !JToken.DeepEquals(previous["themeCustom"], settings["themeCustom"])))
            {
                Themes.ApplyToObs(settings);
            }
            bool overlayStyleChanged = previous["overlayStyle"]?.Value<string>() != settings["overlayStyle"]?.Value<string>();
            bool overlayGeometryChanged = previous["recordingPreset"]?.Value<string>() != settings["recordingPreset"]?.Value<string>() ||
                previous["overlayOpacity"]?.Value<int>() != settings["overlayOpacity"]?.Value<int>() ||
                previous["overlayScale"]?.Value<int>() != settings["overlayScale"]?.Value<int>() ||
                previous["overlayFlipH"]?.Value<bool>() != settings["overlayFlipH"]?.Value<bool>() ||
                previous["overlayHueShift"]?.Value<double>() != settings["overlayHueShift"]?.Value<double>() ||
                previous["overlayColorMultiply"]?.Value<string>() != settings["overlayColorMultiply"]?.Value<string>() ||
                previous["overlayColorAdd"]?.Value<string>() != settings["overlayColorAdd"]?.Value<string>();
            bool motionBlurChanged = previous["motionBlurEnabled"]?.Value<bool>() != settings["motionBlurEnabled"]?.Value<bool>() ||
                previous["motionBlurStrength"]?.Value<double>() != settings["motionBlurStrength"]?.Value<double>();
            bool applyOverlay = overlayStyleChanged || overlayGeometryChanged;
            bool recreateBongo = overlayStyleChanged && settings["overlayStyle"]?.Value<string>() == "bongo_cat";
            bool restartObs = TestRestartRequired(previous, settings);
            bool applyVideoSettings = TestRuntimeVideoSettingsChanged(previous, settings);
            bool applyReplayBufferOutput = TestReplayBufferOutputChanged(previous, settings);
            bool applyRuntimeOutputs = restartObs || applyVideoSettings || applyReplayBufferOutput;
            var liveResult = ApplyLiveSettings(settings, restartObs, applyOverlay, recreateBongo, motionBlurChanged, applyRuntimeOutputs, applyVideoSettings, applyReplayBufferOutput);
            if (previous["appIcon"]?.Value<string>() != settings["appIcon"]?.Value<string>() ||
                previous["appIconCustomPath"]?.Value<string>() != settings["appIconCustomPath"]?.Value<string>() ||
                previous["appIconRecordingDot"]?.Value<bool>() != settings["appIconRecordingDot"]?.Value<bool>())
            {
                var iconLive = ApplyAppIconLive(settings);
                liveResult["applied"] = ConcatArrays(liveResult["applied"] as JArray, iconLive["applied"] as JArray);
            }
            return new JObject
            {
                ["ok"] = true, ["settings"] = settings,
                ["applied"] = ConcatArrays(hotkeyRelease["applied"] as JArray, liveResult["applied"] as JArray),
                ["warnings"] = ConcatArrays(hotkeyRelease["warnings"] as JArray, liveResult["warnings"] as JArray),
                ["restartRequired"] = liveResult["restartRequired"], ["restartReason"] = liveResult["restartReason"],
            };
        }

    }
}

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
using static ReplayKitHelper.ReplaykitSettings;
using static ReplayKitHelper.SettingsSceneJson;
using static ReplayKitHelper.SettingsOverlayLive;
using static ReplayKitHelper.SettingsObsConfig;
using static ReplayKitHelper.SettingsRecording;
using static ReplayKitHelper.SettingsLiveApply;
using static ReplayKitHelper.SettingsHttp;

namespace ReplayKitHelper
{
    // hotkey capture for the press a key to bind fields, and reading the hotkeys back from obs
    internal static class SettingsHotkeys
    {
        // -- hotkey capture + sync-from-obs: lets the docks "press a key to bind" ui temporarily blank obss native hotkeys while listening, and detects when the user rebinds a hotkey through obss own Settings dialog instead of the dock, so the dock does not silently fight/revert that edit.

        internal static JObject SyncRecordingKeybindFromObs(JObject settings)
        {
            var start = GetObsProfileParameterSafe("Hotkeys", "OBSBasic.StartRecording");
            var stop = GetObsProfileParameterSafe("Hotkeys", "OBSBasic.StopRecording");
            if (!start.Ok && !stop.Ok) return settings;

            string json = start.Ok ? start.Data?["parameterValue"]?.Value<string>() ?? "" : "";
            JObject combo = ConvertRecordingBasicIniToKeybind(json);
            if (combo["key"] == null && stop.Ok)
            {
                json = stop.Data?["parameterValue"]?.Value<string>() ?? "";
                combo = ConvertRecordingBasicIniToKeybind(json);
            }
            JObject extra = ConvertRecordingBasicIniToKeybind(json, 1);

            string current = (settings["recordingKeybind"] as JObject ?? new JObject()).ToString(Formatting.None);
            string next = combo.ToString(Formatting.None);
            if (current != next) settings["recordingKeybind"] = combo;
            if ((settings["recordingKeybindExtra"] as JObject ?? new JObject()).ToString(Formatting.None) != extra.ToString(Formatting.None)) settings["recordingKeybindExtra"] = extra;
            return settings;
        }

        internal static string HotkeySignature(JObject settings) =>
            new JObject { ["clip"] = settings["clipKeybind"], ["clipExtra"] = settings["clipKeybindExtra"], ["recording"] = settings["recordingKeybind"], ["recordingExtra"] = settings["recordingKeybindExtra"], ["screenshot"] = settings["screenshotKeybind"], ["screenshotExtra"] = settings["screenshotKeybindExtra"] }.ToString(Formatting.None);

        // the screenshot hotkey is obss own Screenshot Output, which can be set in the hotkeys of obs as well, so a key picked there shows here too
        internal static JObject SyncScreenshotKeybindFromObs(JObject settings)
        {
            var shot = GetObsProfileParameterSafe("Hotkeys", "OBSBasic.Screenshot");
            if (!shot.Ok) return settings;
            string json = shot.Data?["parameterValue"]?.Value<string>() ?? "";
            var combo = ConvertRecordingBasicIniToKeybind(json);
            var extra = ConvertRecordingBasicIniToKeybind(json, 1);
            if ((settings["screenshotKeybind"] as JObject ?? new JObject()).ToString(Formatting.None) != combo.ToString(Formatting.None)) settings["screenshotKeybind"] = combo;
            if ((settings["screenshotKeybindExtra"] as JObject ?? new JObject()).ToString(Formatting.None) != extra.ToString(Formatting.None)) settings["screenshotKeybindExtra"] = extra;
            return settings;
        }

        internal static JObject SyncHotkeysFromObs(JObject settings)
        {
            string before = HotkeySignature(settings);

            var clip = GetReplayBufferHotkeyJson();
            if (clip.Ok)
            {
                string json = clip.Data?["parameterValue"]?.Value<string>() ?? "";
                var combo = ConvertClipBasicIniToKeybind(json);
                if (combo["key"] != null)
                {
                    string current = (settings["clipKeybind"] as JObject ?? new JObject()).ToString(Formatting.None);
                    string next = combo.ToString(Formatting.None);
                    if (current != next) settings["clipKeybind"] = combo;
                    var extra = ConvertClipBasicIniToKeybind(json, 1);
                    if ((settings["clipKeybindExtra"] as JObject ?? new JObject()).ToString(Formatting.None) != extra.ToString(Formatting.None)) settings["clipKeybindExtra"] = extra;
                }
            }

            settings = SyncRecordingKeybindFromObs(settings);
            settings = SyncScreenshotKeybindFromObs(settings);
            string after = HotkeySignature(settings);
            if (before != after) WriteSettings(settings);
            return settings;
        }

        // obss own settings dialog can edit RecRBTime directly, bypassing replaykit entirely -- without this, the custom settings dock keeps showing (and would re-apply) whatever replaykit last wrote, silently reverting the users obs-side edit on the next apply.
        internal static JObject SyncReplayBufferSecondsFromObs(JObject settings)
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

        // recording_hotkey.lua fast-polls this file (~50ms, separate from its normal 1s saved-settings sync) and applies it via obs_hotkey_load, which rewrites the live in-memory hotkey binding immediately. this is not cosmetic like SetProfileParameter below -- SetProfileParameter only ever reaches the on-disk profile ini, and obs only rereads that ini into its live hotkey registry on a full profile switch, so it could never actually stop the native hotkey from firing while capture was "active". obs_hotkey_load is the same api the lua script already relies on to make a saved keybind change take effect without restarting obs.
        internal static string GetHotkeyCaptureSignalPath() => StateLayout.PathFor(AppConfig.GetReplayKitRoot(), StateLayout.HotkeySignalFile);

        internal static void WriteHotkeyCaptureSignal(JObject signal) => AppConfig.WriteUtf8(GetHotkeyCaptureSignalPath(), signal.ToString(Formatting.None));

        // called once at helper startup so a signal file left at active:true by a crashed prior helper process (obs itself keeps running independently of the helper, so its lua side has no equivalent restart to reset on) does not leave the native hotkeys blanked forever.
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

        internal static JObject RestoreHotkeysFromSettings(JObject settings)
        {
            string recordingJson = ConvertRecordingKeybindToBasicIni(settings["recordingKeybind"] as JObject, settings["recordingKeybindExtra"] as JObject);
            var r1 = SetReplayBufferHotkeyJson(settings["clipKeybind"] as JObject, settings["clipKeybindExtra"] as JObject);
            var r2 = SetRecordingHotkeyPairJson(recordingJson, recordingJson);
            var r3 = SetObsProfileParameterSafe("Hotkeys", "OBSBasic.Screenshot", ConvertRecordingKeybindToBasicIni(settings["screenshotKeybind"] as JObject, settings["screenshotKeybindExtra"] as JObject));
            var errors = new List<string>();
            if (!r1.Ok) errors.Add(r1.Message);
            if (r2["ok"]?.Value<bool>() != true) errors.Add(r2["message"]?.Value<string>());
            if (!r3.Ok) errors.Add("OBSBasic.Screenshot: " + r3.Message);

            // unconditional even if the ini writes above failed -- those are best-effort disk consistency, not what actually restores the live binding, so a websocket hiccup should not leave the native hotkey blanked.
            WriteHotkeyCaptureSignal(new JObject
            {
                ["active"] = false,
                ["clipKeybind"] = settings["clipKeybind"] as JObject ?? new JObject(),
                ["recordingKeybind"] = settings["recordingKeybind"] as JObject ?? new JObject(),
                ["clipKeybindExtra"] = settings["clipKeybindExtra"] as JObject ?? new JObject(),
                ["recordingKeybindExtra"] = settings["recordingKeybindExtra"] as JObject ?? new JObject(),
                ["screenshotKeybind"] = settings["screenshotKeybind"] as JObject ?? new JObject(),
                ["screenshotKeybindExtra"] = settings["screenshotKeybindExtra"] as JObject ?? new JObject(),
            });
            lock (Server.State.HotkeyCaptureLock)
            {
                Server.State.ReplaykitHotkeyCaptureActive = false;
            }

            if (errors.Count > 0) return new JObject { ["ok"] = false, ["message"] = string.Join("; ", errors) };
            return new JObject { ["ok"] = true, ["active"] = false };
        }

        internal static JObject EnsureHotkeyCaptureReleased(JObject settings)
        {
            if (!Server.State.ReplaykitHotkeyCaptureActive) return new JObject { ["ok"] = true, ["warnings"] = new JArray(), ["applied"] = new JArray() };
            var restore = RestoreHotkeysFromSettings(settings);
            if (restore["ok"]?.Value<bool>() == true) return new JObject { ["ok"] = true, ["warnings"] = new JArray(), ["applied"] = new JArray("OBS hotkeys restored") };
            return new JObject { ["ok"] = false, ["warnings"] = new JArray("OBS hotkeys could not be restored: " + restore["message"]), ["applied"] = new JArray() };
        }

    }
}

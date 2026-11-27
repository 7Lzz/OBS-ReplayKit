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
    // hotkey capture for the press a key to bind fields, which blanks the native hotkeys of obs while one listens, and reading the hotkeys back from obs, so a hotkey rebound in the settings dialog of obs is shown instead of silently reverted
    internal static class SettingsHotkeys
    {
        private static string captureOwner;
        private static DateTime captureExpires;
        private const int CaptureLeaseSeconds = 15;

        internal static JObject SyncRecordingKeybindFromObs(JObject settings)
        {
            var start = GetObsProfileParameterSafe("Hotkeys", "OBSBasic.StartRecording");
            var stop = GetObsProfileParameterSafe("Hotkeys", "OBSBasic.StopRecording");
            if (!start.Ok || !stop.Ok) return settings;

            string startJson = start.Data?["parameterValue"]?.Value<string>() ?? "";
            string stopJson = stop.Data?["parameterValue"]?.Value<string>() ?? "";
            var startBinding = ConvertRecordingBasicIniToKeybind(startJson);
            var startExtra = ConvertRecordingBasicIniToKeybind(startJson, 1);
            var stopBinding = ConvertRecordingBasicIniToKeybind(stopJson);
            var stopExtra = ConvertRecordingBasicIniToKeybind(stopJson, 1);
            bool shared = JToken.DeepEquals(startBinding, stopBinding) && JToken.DeepEquals(startExtra, stopExtra);
            settings["recordingKeybind"] = startBinding;
            settings["recordingKeybindExtra"] = startExtra;
            settings["recordingStopKeybind"] = shared ? (JToken)JValue.CreateNull() : stopBinding;
            settings["recordingStopKeybindExtra"] = shared ? (JToken)JValue.CreateNull() : stopExtra;
            return settings;
        }

        internal static string RecordingStopHotkey(JObject settings) =>
            settings["recordingStopKeybind"] is JObject stop
                ? ConvertRecordingKeybindToBasicIni(stop, settings["recordingStopKeybindExtra"] as JObject)
                : ConvertRecordingKeybindToBasicIni(settings["recordingKeybind"] as JObject, settings["recordingKeybindExtra"] as JObject);

        internal static string HotkeySignature(JObject settings) =>
            new JObject { ["clip"] = settings["clipKeybind"], ["clipExtra"] = settings["clipKeybindExtra"], ["recording"] = settings["recordingKeybind"], ["recordingExtra"] = settings["recordingKeybindExtra"], ["recordingStop"] = settings["recordingStopKeybind"], ["recordingStopExtra"] = settings["recordingStopKeybindExtra"], ["screenshot"] = settings["screenshotKeybind"], ["screenshotExtra"] = settings["screenshotKeybindExtra"] }.ToString(Formatting.None);

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
            WriteSettings(settings);
            return settings;
        }

        public static JObject SetHotkeyCapture(bool active, string owner)
        {
            if (!Guid.TryParseExact(owner, "N", out _)) return CaptureFailure();
            lock (Server.State.HotkeyCaptureLock)
            {
                if (captureOwner != null && DateTime.UtcNow >= captureExpires) captureOwner = null;
                if (!active)
                {
                    if (captureOwner != owner) return new JObject { ["ok"] = true, ["active"] = false };
                    return EndCapture() ? new JObject { ["ok"] = true, ["active"] = false } : CaptureFailure();
                }
                if (captureOwner != null && captureOwner != owner) return CaptureFailure();
                if (!PipeClient.ApplyObsConfig("HOTKEYCAPTURE", "1", 1500))
                {
                    EndCapture();
                    return CaptureFailure();
                }
                captureOwner = owner;
                captureExpires = DateTime.UtcNow.AddSeconds(CaptureLeaseSeconds);
                return new JObject { ["ok"] = true, ["active"] = true };
            }
        }

        private static bool EndCapture()
        {
            captureOwner = null;
            return PipeClient.ApplyObsConfig("HOTKEYCAPTURE", "0", 1500);
        }

        private static JObject CaptureFailure() => new JObject
        {
            ["ok"] = false, ["message"] = Locales.Message("common.hotkeyCaptureFailed"),
        };

        internal static JObject EnsureHotkeyCaptureReleased(bool hotkeysChanged)
        {
            lock (Server.State.HotkeyCaptureLock)
            {
                if (!hotkeysChanged || captureOwner == null || DateTime.UtcNow >= captureExpires || EndCapture())
                    return new JObject { ["ok"] = true, ["warnings"] = new JArray(), ["applied"] = new JArray() };
            }
            return new JObject { ["ok"] = false, ["warnings"] = new JArray(Locales.Message("settings.hotkeysNotRestoredError", "error", Locales.Message("common.hotkeyCaptureFailed"))), ["applied"] = new JArray() };
        }

        internal static bool ApplyOpenClipsHotkeys(JObject settings)
        {
            var bindings = new JObject
            {
                ["openClipsKeybind"] = settings["openClipsKeybind"],
                ["openClipsKeybindExtra"] = settings["openClipsKeybindExtra"],
            };
            return PipeClient.ApplyObsConfig("OPENCLIPSKEYS", bindings.ToString(Formatting.None), 1500);
        }
    }
}

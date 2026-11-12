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
using static ReplayKitHelper.SettingsHotkeys;
using static ReplayKitHelper.SettingsHttp;

namespace ReplayKitHelper
{
    // applying saved settings to a running obs: the runtime outputs, the desktop audio mixer, the discord output, the live motion blur filter, and the orchestrator that does the least work a change needs
    internal static class SettingsLiveApply
    {
        // -- runtime output stop/apply/restart orchestration + projector audio-monitoring toggle --

        internal static JObject NewInactiveOutputState() => new JObject { ["ok"] = true, ["wasActive"] = false };

        // serialize the whole stop-outputs -> SetVideoSettings (obs_reset_video) -> restart-outputs cycle. two of these overlapping deadlock obss video graph (2026-08-28: a user hard-froze obs by rapidly re-applying the downscale resolution), and even sequential ones need a beat for the render/graphics threads to re-settle.
        internal static JObject ApplyRuntimeOutputsLive(JObject settings, JObject preset, bool restartObs, bool applyVideoSettings = true, bool applyReplayBufferOutput = true)
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

        internal static JObject ApplyRuntimeOutputsLiveInner(JObject settings, JObject preset, bool restartObs, bool applyVideoSettings = true, bool applyReplayBufferOutput = true)
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

        internal static JObject SetProjectorMonitoringState(JObject settings, bool enabled)
        {
            bool deviceCapture = UsesOutputDeviceRecording(settings);
            if (!deviceCapture)
            {
                var current = GetDesktopAudioCaptureSettings();
                if (!current.Ok) return new JObject { ["ok"] = false, ["applied"] = new JArray(), ["message"] = current.Message };
                if (!(current.Data?["inputSettings"] is JObject inputSettings))
                    return new JObject { ["ok"] = false, ["applied"] = new JArray(), ["message"] = "OBS did not return Desktop Audio settings." };
                deviceCapture = CapturesOutputDevice(inputSettings);
            }
            string inputName = DesktopAudioInputName;
            if (deviceCapture) return SetDesktopAudioMixerState(inputName, "OBS_MONITORING_TYPE_NONE", false);

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
            // obs opens its stream on the cable a moment after monitoring turns on, and a mute windows saved for obs there would silence the share
            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (ShareCableMute.UnmuteObs(renderDevice.Id) > 0) { applied.Add("OBS unmuted on the share cable"); break; }
                Thread.Sleep(150);
            }
            return new JObject { ["ok"] = true, ["applied"] = new JArray(applied), ["message"] = "" };
        }

        // -- desktop audio mixer: retry-with-verification wrappers, since obs does not always apply an audio monitor-type/mute change instantly, plus a deliberate mute-then-unmute "flap" to force the obs mixer icon to visually refresh after a monitor-type change.

        internal static JObject SetDesktopAudioMixerState(string inputName, string monitorType, bool ensureUnmuted = true)
        {
            var applied = new List<string>();
            bool targetIsOff = monitorType == "OBS_MONITORING_TYPE_NONE";

            string lastMonitorMessage = "";
            bool monitorReady = false;
            bool monitorChanged = false;
            for (int attempt = 1; attempt <= 6; attempt++)
            {
                var actualBefore = GetInputMonitorTypeValue(inputName);
                if (actualBefore["ok"]?.Value<bool>() == true && actualBefore["value"]?.Value<string>() == monitorType) { monitorReady = true; break; }

                monitorChanged = true;
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

            if (!ensureUnmuted)
                return new JObject { ["ok"] = true, ["applied"] = new JArray("desktop audio monitor off"), ["message"] = "" };

            var unmute = SetInputMuteState(inputName, false, 6);
            if (unmute["ok"]?.Value<bool>() != true) return new JObject { ["ok"] = false, ["applied"] = new JArray(applied), ["message"] = "monitor state changed, but Desktop Audio could not be unmuted: " + unmute["message"] };

            applied.Add("desktop audio unmuted");
            // the refresh mutes desktop audio for a moment, which leaves a gap in the clip buffer, so it only runs when the monitor type was changed here and the mixer icon is stale
            if (!monitorChanged)
            {
                applied.Add(targetIsOff ? "desktop audio monitor off" : "desktop audio monitor on");
                return new JObject { ["ok"] = true, ["applied"] = new JArray(applied), ["message"] = "" };
            }
            var refresh = RefreshDesktopAudioMixerIconState(inputName, monitorType);
            if (refresh["ok"]?.Value<bool>() != true) return new JObject { ["ok"] = false, ["applied"] = new JArray(applied), ["message"] = "monitor state changed, but the OBS mixer icon could not be refreshed: " + refresh["message"] };
            applied.AddRange((refresh["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
            applied.Add(targetIsOff ? "desktop audio monitor off" : "desktop audio monitor on");
            return new JObject { ["ok"] = true, ["applied"] = new JArray(applied), ["message"] = "" };
        }

        internal static JObject SetInputMonitorTypeRaw(string inputName, string monitorType)
        {
            var result = ObsWebSocket.InvokeRequest("SetInputAudioMonitorType", new JObject { ["inputName"] = inputName, ["monitorType"] = monitorType }, 3000);
            return result.Ok ? new JObject { ["ok"] = true, ["message"] = "" } : new JObject { ["ok"] = false, ["message"] = result.Message };
        }

        internal static JObject SetInputMuteRaw(string inputName, bool muted)
        {
            var result = ObsWebSocket.InvokeRequest("SetInputMute", new JObject { ["inputName"] = inputName, ["inputMuted"] = muted }, 3000);
            return result.Ok ? new JObject { ["ok"] = true, ["message"] = "" } : new JObject { ["ok"] = false, ["message"] = result.Message };
        }

        internal static JObject SetInputMuteState(string inputName, bool muted, int maxAttempts = 4)
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

        internal static JObject RefreshDesktopAudioMixerIconState(string inputName, string monitorType)
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

        internal static JObject GetInputMonitorTypeValue(string inputName)
        {
            var verify = ObsWebSocket.InvokeRequest("GetInputAudioMonitorType", new JObject { ["inputName"] = inputName }, 3000);
            if (!verify.Ok) return new JObject { ["ok"] = false, ["value"] = "", ["message"] = verify.Message };
            string monitorType = verify.Data?["monitorType"]?.Value<string>();
            string inputAudioMonitorType = verify.Data?["inputAudioMonitorType"]?.Value<string>();
            if (!string.IsNullOrEmpty(monitorType)) return new JObject { ["ok"] = true, ["value"] = monitorType, ["message"] = "" };
            if (!string.IsNullOrEmpty(inputAudioMonitorType)) return new JObject { ["ok"] = true, ["value"] = inputAudioMonitorType, ["message"] = "" };
            return new JObject { ["ok"] = false, ["value"] = "", ["message"] = "OBS did not return an input monitor type." };
        }

        internal static bool TestInputMuteState(string inputName, bool expected)
        {
            var verify = ObsWebSocket.InvokeRequest("GetInputMute", new JObject { ["inputName"] = inputName }, 3000);
            if (!verify.Ok) return false;
            var value = verify.Data?["inputMuted"];
            if (value == null || value.Type == JTokenType.Null) return false;
            return value.Value<bool>() == expected;
        }

        // obs-browser-page.exe is obss own cef subprocess -- without excluding it, clip audio played back in the dock gets treated as ordinary desktop audio, monitored back out to the discord share, and doubles up with the copy discord already grabs directly from the same process tree.
        internal static readonly string[] DesktopAudioExcludeExes = { "Discord.exe", "DiscordSystemHelper.exe", "DiscordCanary.exe", "DiscordPTB.exe", "DiscordDevelopment.exe", "obs64.exe", "obs32.exe", "obs.exe", "obs-browser-page.exe", HelperExeName };

        // the programs the desktop capture never takes, whatever the recording audio tab says
        internal static IReadOnlyList<string> DesktopAudioExcludeList => DesktopAudioExcludeExes;

        // keep the app list ready when switching back from device capture.
        private static void SetDesktopAudioCaptureSettingsObject(JObject settingsObject, JObject settings)
        {
            string mode = settings["recordingAudioMode"]?.Value<string>() == "specific" ? "specific" : "all";
            bool outputDevice = UsesOutputDeviceRecording(settings);
            settingsObject["mode"] = "session";
            settingsObject["executable_list"] = AudioApps.BuildCaptureList(mode, settings["recordingAudioApps"] as JObject, out bool exclude);
            settingsObject["exclude"] = exclude;
            settingsObject["output_device"] = outputDevice;
            settingsObject["device_id"] = outputDevice ? "default" : "";
        }

        internal static bool CapturesOutputDevice(JToken inputSettings) =>
            inputSettings?["output_device"]?.Value<bool>() == true;

        internal static ObsWebSocketResult GetDesktopAudioCaptureSettings() =>
            ObsWebSocket.InvokeRequest("GetInputSettings", new JObject { ["inputName"] = DesktopAudioInputName }, 3000);

        private static ObsWebSocketResult SetDesktopAudioCaptureSettingsLive(JObject settings, ObsWebSocketResult current)
        {
            if (!current.Ok) return current;
            var live = current.Data?["inputSettings"] as JObject;
            if (live == null) return new ObsWebSocketResult { Message = "OBS did not return Desktop Audio settings." };
            bool outputDevice = UsesOutputDeviceRecording(settings);
            bool wasOutputDevice = CapturesOutputDevice(live);
            if (outputDevice)
            {
                if (current.Data?["inputKind"]?.Value<string>() != "audio_capture")
                    return new ObsWebSocketResult { Message = "Desktop Audio is not the ReplayKit audio capture source." };
                var monitor = SetDesktopAudioMixerState(DesktopAudioInputName, "OBS_MONITORING_TYPE_NONE", false);
                if (monitor["ok"]?.Value<bool>() != true) return new ObsWebSocketResult { Message = monitor["message"]?.Value<string>() };
                var discord = SetAudioSourceMixersLive(DiscordAudioInputName, 0);
                if (!discord.Ok && discord.Code != 600) return discord;
            }

            var inputSettings = new JObject();
            SetDesktopAudioCaptureSettingsObject(inputSettings, settings);
            if (outputDevice != wasOutputDevice || (live["device_id"]?.Value<string>() ?? "") != inputSettings["device_id"].Value<string>()
                || live["mode"]?.Value<string>() != "session" || live["exclude"]?.Value<bool>() != inputSettings["exclude"].Value<bool>()
                || !SameProgramList(live["executable_list"], inputSettings["executable_list"]))
            {
                var set = ObsWebSocket.InvokeRequest("SetInputSettings", new JObject { ["inputName"] = DesktopAudioInputName, ["inputSettings"] = inputSettings, ["overlay"] = true }, 3000);
                if (!set.Ok) return set;
            }

            return outputDevice ? new ObsWebSocketResult { Ok = true } : RestoreDiscordAudioTracks(settings, wasOutputDevice);
        }

        // keeps the live desktop-audio monitoring/mute change in sync with the on-disk scene file, so a disabled Share Preview cannot silently come back after obs restarts (obs persists audio monitoring in the scene collection, not in a profile setting this file already writes elsewhere).
        internal static JObject SetShareModeSceneFile(int monitoringType, bool muted)
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
                    if (source["name"]?.Value<string>() != DesktopAudioInputName || CapturesOutputDevice(source["settings"])) continue;
                    int current = source["monitoring_type"]?.Value<int>() ?? -1;
                    if (current != monitoringType) { source["monitoring_type"] = monitoringType; changed = true; }
                    bool monitoringEnabled = monitoringType != 0;
                    if (source["monitoring_enabled"]?.Value<bool>() != monitoringEnabled) { source["monitoring_enabled"] = monitoringEnabled; changed = true; }
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

        // -- discord output live-apply + share preview: updates desktop-audio exclusion/monitoring settings, starts/stops the legacy virtual camera, and parks/unparks the obs windowed projector via DiscordProjector.

        internal static JObject ApplyDiscordOutputLive(JObject settings)
        {
            var warnings = new List<string>();
            var applied = new List<string>();
            bool ok = true;
            string message = "";
            string inputName = DesktopAudioInputName;
            string mode = settings["discord_output_mode"]?.Value<string>() ?? "";
            if (mode != "projector") mode = "projector";
            bool isLegacyVcam = false;
            bool screenshareEnabled = TestDiscordScreenshareEnabled(settings);
            bool shareEnabled = screenshareEnabled && (settings["discord_projector_enabled"]?.Value<bool>() ?? false);
            if (!screenshareEnabled) applied.Add("Discord screenshare support disabled");
            string monitorType = shareEnabled ? "OBS_MONITORING_TYPE_MONITOR_AND_OUTPUT" : "OBS_MONITORING_TYPE_NONE";

            var capture = ApplyRecordingAudioLive(settings);
            applied.AddRange(capture["applied"].Values<string>());
            warnings.AddRange(capture["warnings"].Values<string>());

            if (mode == "projector")
            {
                var mixer = SetProjectorMonitoringState(settings, shareEnabled);
                if (mixer["ok"]?.Value<bool>() == true)
                {
                    applied.AddRange((mixer["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                    if (!UsesOutputDeviceRecording(settings) && capture["liveApplied"]?.Value<bool>() == true)
                    {
                        int sceneMonitoringType = shareEnabled ? 2 : 0;
                        var sceneFile = SetShareModeSceneFile(sceneMonitoringType, false);
                        if (sceneFile["ok"]?.Value<bool>() != true)
                            warnings.Add("Desktop Audio monitoring was applied live, but could not be saved for restart: " + sceneFile["message"]);
                        else if (sceneFile["changed"]?.Value<bool>() == true)
                            applied.Add("desktop audio monitor state saved");
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
            lock (SettingsStore.Gate)
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
        }

        // what the share button of the shell polls every few seconds: the switches only, with no obs-websocket call, since reading the monitor type goes thru an obs api that obs 33 logs as deprecated on every call and the virtual camera script keeps the monitoring right by itself
        public static JObject GetSharePreviewFlags()
        {
            var settings = ReadSettings();
            bool available = TestDiscordScreenshareEnabled(settings);
            bool enabled = available && settings["discord_output_mode"]?.Value<string>() == "projector" && (settings["discord_projector_enabled"]?.Value<bool>() ?? false);
            return new JObject { ["ok"] = true, ["available"] = available, ["enabled"] = enabled };
        }

        public static JObject GetSharePreviewState(bool repairMonitoring = false)
        {
            var settings = ReadSettings();
            bool available = TestDiscordScreenshareEnabled(settings);
            bool enabled = available && settings["discord_output_mode"]?.Value<string>() == "projector" && (settings["discord_projector_enabled"]?.Value<bool>() ?? false);
            string inputName = DesktopAudioInputName;
            var warnings = new List<string>();
            bool deviceCapture = UsesOutputDeviceRecording(settings);
            if (!deviceCapture)
            {
                var capture = GetDesktopAudioCaptureSettings();
                if (capture.Data?["inputSettings"] is JObject inputSettings) deviceCapture = CapturesOutputDevice(inputSettings);
                else
                {
                    deviceCapture = true;
                    warnings.Add("Could not read Desktop Audio capture settings: " + capture.Message);
                }
            }
            string desiredMonitorType = enabled && !deviceCapture ? "OBS_MONITORING_TYPE_MONITOR_AND_OUTPUT" : "OBS_MONITORING_TYPE_NONE";
            bool repaired = false;

            var actual = GetInputMonitorTypeValue(inputName);
            string actualMonitorType = actual["ok"]?.Value<bool>() == true ? actual["value"]?.Value<string>() : "";
            if (actual["ok"]?.Value<bool>() != true) warnings.Add("Could not read Desktop Audio monitor state: " + actual["message"]);

            var mute = ObsWebSocket.InvokeRequest("GetInputMute", new JObject { ["inputName"] = inputName }, 3000);
            bool muteOk = mute.Ok;
            bool? inputMuted = null;
            if (muteOk) inputMuted = mute.Data?["inputMuted"]?.Value<bool>() ?? false;
            else warnings.Add("Could not read Desktop Audio mute state: " + mute.Message);

            bool synced = actual["ok"]?.Value<bool>() == true && actualMonitorType == desiredMonitorType && (deviceCapture || (muteOk && inputMuted != true));
            if (enabled && !deviceCapture && repairMonitoring && DiscordProjector.ClearCableMute() > 0) repaired = true;
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
                    synced = actual["ok"]?.Value<bool>() == true && actualMonitorType == desiredMonitorType && (deviceCapture || (muteOk && inputMuted != true));
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

        internal static JObject ApplyShareModeLive(string shareMode)
        {
            var settings = ReadSettings();
            settings["discord_output_mode"] = "projector";
            settings["shareMode"] = "projector";
            return ApplyDiscordOutputLive(settings);
        }

        // -- motion blur: live-websocket half (create/update/remove the filter on a running obs, including cleanup of the retired obs_composite_blur-kind filter left over from older ReplayKit versions) --

        internal static bool TestShaderfilterPluginInstalled()
        {
            string root = GetObsInstallRoot();
            string dll = Path.Combine(root, "obs-plugins\\64bit\\obs-shaderfilter.dll");
            string shader = Path.Combine(root, "data\\obs-plugins\\obs-shaderfilter\\examples\\motion_blur.shader");
            return File.Exists(dll) && File.Exists(shader);
        }

        internal static string GetMotionBlurLiveFilterName(JArray filters)
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

        internal static List<string> GetRetiredMotionBlurLiveFilterNames(JArray filters)
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

        internal static JObject ApplyMotionBlurLive(JObject settings)
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

        // Microphone input and its OBS filters.

        internal const string MicInputName = "Audio Input Capture";
        internal const string MicNoiseFilterName = "ReplayKit Noise Suppression";
        internal const string NoiseSuppressFilterKind = "noise_suppress_filter_v2";
        internal const string HelperExeName = "OBSReplayKit.exe";
        internal const string MicGateFilterName = "ReplayKit Noise Gate";
        internal const string NoiseGateFilterKind = "noise_gate_filter";
        internal const double NoiseGateDefaultOpenDb = -26.0;
        internal const double NoiseGateDefaultCloseDb = -32.0;
        internal const double GateHysteresisDb = 5.0;
        // Nonzero levels use a named gain filter. Zero also silences the input fader without changing its mute state.
        internal const string GainFilterKind = "gain_filter";
        internal const string MicGainFilterName = "ReplayKit Mic Volume";
        internal const string DesktopGainFilterName = "ReplayKit Desktop Volume";
        internal const string DiscordGainFilterName = "ReplayKit Discord Volume";
        internal static readonly string[] MicSettingKeys = { "micDeviceId", "micVolume", "micNoiseSuppression", "micInputSensitivity" };
        internal static readonly string[] AudioVolumeKeys = { "desktopAudioVolume", "discordAudioVolume" };

        internal static JToken FindGainFilter(JArray filters, string filterName) =>
            filters.FirstOrDefault(f => (f["filterKind"]?.Value<string>() ?? "") == GainFilterKind && (f["filterName"]?.Value<string>() ?? "") == filterName);

        // the volume a named gain filter currently represents, read from a filter list already fetched for something else -- absent filter reads as 100 (default, no boost or cut).
        internal static int GainVolumeFromFilters(JArray filters, string filterName)
        {
            var gain = FindGainFilter(filters, filterName);
            return gain == null ? 100 : AudioVolume.FromDb(gain["filterSettings"]?["db"]?.Value<double>() ?? 0.0);
        }

        // same as GainVolumeFromFilters but fetches its own filter list -- for a source nothing else already reads the filters of. null means the source itself could not be read (missing input, or obs unreachable).
        internal static int? ReadGainVolumeLive(string sourceName, string filterName)
        {
            var list = GetSourceFilterListLive(sourceName);
            if (list["ok"]?.Value<bool>() != true) return null;
            return GainVolumeFromFilters((JArray)list["filters"], filterName);
        }

        internal static void ApplyGainVolumeLive(string sourceName, string filterName, string label, int volumePercent, List<string> applied, List<string> warnings)
        {
            var list = GetSourceFilterListLive(sourceName);
            if (list["ok"]?.Value<bool>() != true) return;
            var filters = (JArray)list["filters"];
            var existing = FindGainFilter(filters, filterName);
            var current = existing?["filterSettings"] as JObject;
            if (existing == null && volumePercent == 100) return;

            double inputLevel = 1;
            if (volumePercent == 0 || current?[AudioVolume.SavedLevel] != null)
            {
                var volume = ObsWebSocket.InvokeRequest("GetInputVolume", new JObject { ["inputName"] = sourceName }, 3000);
                if (!volume.Ok) { warnings.Add("Could not read the " + label + ": " + volume.Message); return; }
                inputLevel = AudioVolume.Level(volume.Data?["inputVolumeMul"]);
            }
            var next = AudioVolume.Prepare(current, volumePercent, inputLevel, out double outputLevel);
            bool levelChanged = inputLevel != outputLevel;
            bool SetLevel()
            {
                if (!levelChanged) return true;
                var result = ObsWebSocket.InvokeRequest("SetInputVolume", new JObject { ["inputName"] = sourceName, ["inputVolumeMul"] = outputLevel }, 3000);
                if (!result.Ok) warnings.Add("Could not set the " + label + ": " + result.Message);
                return result.Ok;
            }

            // Restore before removing the saved fader level, so a failed request can be retried safely.
            if (volumePercent > 0 && !SetLevel()) return;
            bool filterChanged = !JToken.DeepEquals(current, next);
            if (existing == null)
            {
                var create = ObsWebSocket.InvokeRequest("CreateSourceFilter", new JObject
                {
                    ["sourceName"] = sourceName, ["filterName"] = filterName, ["filterKind"] = GainFilterKind,
                    ["filterSettings"] = next,
                }, 3000);
                if (!create.Ok) { warnings.Add("Could not set the " + label + ": " + create.Message); return; }
                var move = ObsWebSocket.InvokeRequest("SetSourceFilterIndex", new JObject { ["sourceName"] = sourceName, ["filterName"] = filterName, ["filterIndex"] = filters.Count }, 3000);
                if (!move.Ok) warnings.Add("The " + label + " was set but the filter could not be moved to the end of the chain: " + move.Message);
            }
            else if (filterChanged)
            {
                var set = ObsWebSocket.InvokeRequest("SetSourceFilterSettings", new JObject { ["sourceName"] = sourceName, ["filterName"] = filterName, ["filterSettings"] = next, ["overlay"] = false }, 3000);
                if (!set.Ok) { warnings.Add("Could not set the " + label + ": " + set.Message); return; }
            }
            if (volumePercent == 0 && !SetLevel()) return;
            if (filterChanged || levelChanged) applied.Add(label);
        }

        internal static bool AudioVolumeKeysChanged(JObject previous, JObject settings) =>
            AudioVolumeKeys.Any(key => !JToken.DeepEquals(previous[key], settings[key]));

        internal static bool TestOnlyAudioVolumesChanged(JObject previous, JObject settings)
        {
            foreach (var prop in GetDefaultSettings().Properties())
            {
                if (AudioVolumeKeys.Contains(prop.Name)) continue;
                if (!JToken.DeepEquals(previous[prop.Name], settings[prop.Name])) return false;
            }
            return AudioVolumeKeysChanged(previous, settings);
        }

        internal static readonly string[] RecordingAudioKeys = { "recordingAudioMode", "recordingAudioApps" };

        internal static bool RecordingAudioKeysChanged(JObject previous, JObject settings) =>
            RecordingAudioKeys.Any(key => !JToken.DeepEquals(previous[key], settings[key]));

        internal static bool TestOnlyRecordingAudioChanged(JObject previous, JObject settings)
        {
            foreach (var prop in GetDefaultSettings().Properties())
            {
                if (RecordingAudioKeys.Contains(prop.Name)) continue;
                if (!JToken.DeepEquals(previous[prop.Name], settings[prop.Name])) return false;
            }
            return RecordingAudioKeysChanged(previous, settings);
        }

        internal static JObject ApplyRecordingAudioLive(JObject settings, ObsWebSocketResult current = null)
        {
            var applied = new List<string>();
            var warnings = new List<string>();
            var live = SetDesktopAudioCaptureSettingsLive(settings, current ?? GetDesktopAudioCaptureSettings());
            if (live.Ok) applied.Add("recording audio");
            else warnings.Add("OBS could not apply recording audio: " + live.Message);
            var file = SetDesktopAudioCaptureInSceneFile(settings);
            if (file["ok"]?.Value<bool>() != true) warnings.Add("The scene file could not be updated: " + file["message"]);
            return new JObject { ["liveApplied"] = live.Ok, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };
        }

        // the tracks of a recording: track 1 is the mix every player plays, and with separate tracks on, the pc audio, discord and the microphone also get track 2, 3 and 4 of their own
        internal static string RecTracksValue(JObject settings) => (settings["recordingSeparateAudioTracks"]?.Value<bool>() ?? DefaultSeparateAudioTracks) ? "15" : "1";

        // the tracks one of the recording audio sources feeds, as obs keeps them in a bit mask; with separate tracks off every source is back on the six tracks obs starts new sources with
        internal static int AudioSourceMixers(string inputName, JObject settings)
        {
            const int track1 = 1, track2 = 2, track3 = 4, track4 = 8;
            if (inputName == DiscordAudioInputName && UsesOutputDeviceRecording(settings)) return 0;
            bool separate = settings["recordingSeparateAudioTracks"]?.Value<bool>() ?? DefaultSeparateAudioTracks;
            if (!separate) return 63;
            if (inputName == DesktopAudioInputName) return track1 | track2;
            if (inputName == DiscordAudioInputName) return track1 | track3;
            if (inputName == MicInputName) return track1 | track4;
            return 63;
        }

        private static ObsWebSocketResult SetAudioSourceMixersLive(string inputName, int mixers)
        {
            var tracks = new JObject();
            for (int i = 0; i < 6; i++) tracks[(i + 1).ToString(CultureInfo.InvariantCulture)] = (mixers & (1 << i)) != 0;
            return ObsWebSocket.InvokeRequest("SetInputAudioTracks", new JObject { ["inputName"] = inputName, ["inputAudioTracks"] = tracks }, 3000);
        }

        internal static ObsWebSocketResult RestoreDiscordAudioTracks(JObject settings, bool wasOutputDevice = false)
        {
            // a failed switch can leave the managed discord source off every track even after device capture has stopped.
            if (!wasOutputDevice)
            {
                var current = ObsWebSocket.InvokeRequest("GetInputAudioTracks", new JObject { ["inputName"] = DiscordAudioInputName }, 3000);
                if (current.Code == 600) return new ObsWebSocketResult { Ok = true };
                if (!current.Ok) return current;
                var tracks = current.Data?["inputAudioTracks"] as JObject;
                if (tracks == null || tracks.Count != 6)
                    return new ObsWebSocketResult { Message = "OBS did not return Discord audio tracks." };
                if (tracks.Values().Any(track => track.Value<bool>())) return new ObsWebSocketResult { Ok = true };
            }

            var set = SetAudioSourceMixersLive(DiscordAudioInputName, AudioSourceMixers(DiscordAudioInputName, settings));
            return set.Code == 600 ? new ObsWebSocketResult { Ok = true } : set;
        }

        // puts the recording audio sources on their tracks in the running obs and in the scene file; the recording itself only reads the tracks when obs starts its outputs, which is why changing this restarts obs
        internal static JObject ApplyAudioTracksLive(JObject settings)
        {
            var applied = new List<string>();
            var warnings = new List<string>();
            foreach (var name in new[] { DesktopAudioInputName, DiscordAudioInputName, MicInputName })
            {
                int mixers = AudioSourceMixers(name, settings);
                if (name == DiscordAudioInputName && mixers != 0)
                {
                    var desktop = GetDesktopAudioCaptureSettings();
                    if (!desktop.Ok && desktop.Code != 600)
                    {
                        warnings.Add("Discord audio tracks could not be updated: " + desktop.Message);
                        continue;
                    }
                    if (CapturesOutputDevice(desktop.Data?["inputSettings"])) continue;
                }
                var set = SetAudioSourceMixersLive(name, mixers);
                if (set.Ok) { if (!applied.Contains("audio tracks")) applied.Add("audio tracks"); }
                else if (set.Unavailable) { warnings.Add("The audio tracks were saved, but OBS could not be reached: " + set.Message + ". They apply the next time OBS starts."); break; }
                // code 600 is a source that is not in the scene, like discord audio on a machine without it
                else if (set.Code != 600) warnings.Add("The audio tracks were saved, but " + name + " could not be updated: " + set.Message);
            }
            var file = SetAudioTracksInSceneFile(settings);
            if (file["ok"]?.Value<bool>() != true) warnings.Add("The audio tracks were saved, but the scene file could not be updated: " + file["message"]);
            return new JObject { ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };
        }

        internal static JObject SetAudioTracksInSceneFile(JObject settings)
        {
            try
            {
                string path = GetSceneCollectionPath();
                if (!File.Exists(path)) return new JObject { ["ok"] = true, ["changed"] = false };
                var data = JObject.Parse(File.ReadAllText(path));
                var sources = data["sources"] as JArray;
                if (sources == null) return new JObject { ["ok"] = true, ["changed"] = false };
                bool outputDevice = sources.OfType<JObject>().Any(source =>
                    source["name"]?.Value<string>() == DesktopAudioInputName && CapturesOutputDevice(source["settings"]));
                bool changed = false;
                foreach (var sourceToken in sources)
                {
                    var source = (JObject)sourceToken;
                    string name = source["name"]?.Value<string>();
                    if (name != DesktopAudioInputName && name != DiscordAudioInputName && name != MicInputName) continue;
                    int mixers = name == DiscordAudioInputName && outputDevice ? 0 : AudioSourceMixers(name, settings);
                    if (source["mixers"]?.Value<int>() == mixers) continue;
                    source["mixers"] = mixers;
                    changed = true;
                }
                if (changed) AppConfig.WriteUtf8(path, data.ToString(Formatting.Indented));
                return new JObject { ["ok"] = true, ["changed"] = changed };
            }
            catch (Exception ex)
            {
                return new JObject { ["ok"] = false, ["changed"] = false, ["message"] = ex.Message };
            }
        }

        private static JObject SetDesktopAudioCaptureInSceneFile(JObject settings)
        {
            try
            {
                string path = GetSceneCollectionPath();
                if (!File.Exists(path)) return new JObject { ["ok"] = true, ["changed"] = false };
                var data = JObject.Parse(File.ReadAllText(path));
                var sources = data["sources"] as JArray;
                if (sources == null) return new JObject { ["ok"] = true, ["changed"] = false };
                var desktop = sources.OfType<JObject>().FirstOrDefault(source => source["name"]?.Value<string>() == DesktopAudioInputName);
                if (desktop == null) return new JObject { ["ok"] = true, ["changed"] = false };
                bool outputDevice = UsesOutputDeviceRecording(settings);
                if (outputDevice && desktop["id"]?.Value<string>() != "audio_capture")
                    return new JObject { ["ok"] = false, ["changed"] = false, ["message"] = "Desktop Audio is not the ReplayKit audio capture source." };
                var inputSettings = desktop["settings"] as JObject ?? new JObject();
                bool wasOutputDevice = CapturesOutputDevice(inputSettings);
                string before = inputSettings.ToString(Formatting.None);
                SetDesktopAudioCaptureSettingsObject(inputSettings, settings);
                desktop["settings"] = inputSettings;
                bool changed = before != inputSettings.ToString(Formatting.None);
                if (outputDevice && (desktop["monitoring_type"]?.Value<int>() != 0 || desktop["monitoring_enabled"]?.Value<bool>() == true))
                {
                    desktop["monitoring_type"] = 0;
                    desktop["monitoring_enabled"] = false;
                    changed = true;
                }
                var discord = sources.OfType<JObject>().FirstOrDefault(source => source["name"]?.Value<string>() == DiscordAudioInputName);
                if (discord != null && (outputDevice || wasOutputDevice || discord["mixers"]?.Value<int>() == 0))
                {
                    int mixers = AudioSourceMixers(DiscordAudioInputName, settings);
                    if (discord["mixers"]?.Value<int>() != mixers)
                    {
                        discord["mixers"] = mixers;
                        changed = true;
                    }
                }
                if (changed) AppConfig.WriteUtf8(path, data.ToString(Formatting.Indented));
                return new JObject { ["ok"] = true, ["changed"] = changed };
            }
            catch (Exception ex)
            {
                return new JObject { ["ok"] = false, ["changed"] = false, ["message"] = ex.Message };
            }
        }

        internal static bool MicKeysChanged(JObject previous, JObject settings) =>
            MicSettingKeys.Any(key => !JToken.DeepEquals(previous[key], settings[key]));

        internal static bool TestOnlyMicChanged(JObject previous, JObject settings)
        {
            foreach (var prop in GetDefaultSettings().Properties())
            {
                if (MicSettingKeys.Contains(prop.Name)) continue;
                if (!JToken.DeepEquals(previous[prop.Name], settings[prop.Name])) return false;
            }
            return MicKeysChanged(previous, settings);
        }

        internal static List<JToken> FindNoiseSuppressFilters(JArray filters) =>
            filters.Where(f => (f["filterKind"]?.Value<string>() ?? "").StartsWith("noise_suppress_filter", StringComparison.Ordinal)).ToList();

        internal static List<JToken> FindNoiseGateFilters(JArray filters) =>
            filters.Where(f => (f["filterKind"]?.Value<string>() ?? "") == NoiseGateFilterKind).ToList();

        // the enabled gate open threshold as a slider value; no enabled gate means nothing is being cut, which is the far left of the slider. obs leaves a setting out of the filter json while it is still at its default, so a missing threshold is the obs default.
        internal static int GateSensitivity(List<JToken> gates)
        {
            var active = gates.FirstOrDefault(g => g["filterEnabled"]?.Value<bool>() ?? false);
            if (active == null) return MicTest.SensitivityMin;
            double open = active["filterSettings"]?["open_threshold"]?.Value<double>() ?? NoiseGateDefaultOpenDb;
            int rounded = (int)Math.Round(open, MidpointRounding.AwayFromZero);
            return Math.Max(MicTest.SensitivityMin, Math.Min(MicTest.SensitivityMax, rounded));
        }

        // what obs actually has on the mic input right now, or null when obs cannot be reached or the input is missing -- callers treat null as "leave the saved values alone".
        internal static JObject ReadMicLive()
        {
            var input = ObsWebSocket.InvokeRequest("GetInputSettings", new JObject { ["inputName"] = MicInputName }, 3000);
            if (!input.Ok) return null;
            var filters = GetSourceFilterListLive(MicInputName);
            if (filters["ok"]?.Value<bool>() != true) return null;

            var filterArray = (JArray)filters["filters"];
            bool noiseOn = FindNoiseSuppressFilters(filterArray).Any(f => f["filterEnabled"]?.Value<bool>() ?? false);
            int volume = GainVolumeFromFilters(filterArray, MicGainFilterName);
            return new JObject
            {
                ["micDeviceId"] = NormalizeMicDeviceId(input.Data?["inputSettings"]?["device_id"]?.Value<string>()),
                ["micVolume"] = Math.Max(0, Math.Min(MicTest.VolumeMax, volume)),
                ["micNoiseSuppression"] = noiseOn,
                ["micInputSensitivity"] = GateSensitivity(FindNoiseGateFilters(filterArray)),
            };
        }

        // obs is the source of truth for the mic -- the mixer fader and the source properties get edited there directly, so the page follows what obs really has instead of a stale saved copy.
        internal static JObject SyncMicFromObs(JObject settings)
        {
            var live = ReadMicLive();
            if (live == null) return settings;
            bool changed = false;
            foreach (var key in MicSettingKeys)
            {
                if (JToken.DeepEquals(settings[key], live[key])) continue;
                settings[key] = live[key];
                changed = true;
            }
            if (changed) WriteSettings(settings);
            return settings;
        }

        // brings the mic input in line with the saved settings, touching only what differs from obs right now.
        internal static JObject ApplyMicLive(JObject settings)
        {
            var applied = new List<string>();
            var warnings = new List<string>();
            string deviceId = settings["micDeviceId"]?.Value<string>() ?? MicDevices.DefaultId;
            int volume = settings["micVolume"]?.Value<int>() ?? 100;
            bool noise = settings["micNoiseSuppression"]?.Value<bool>() ?? false;
            int sensitivity = settings["micInputSensitivity"]?.Value<int>() ?? MicTest.SensitivityDefault;

            var live = ReadMicLive();
            if (live == null)
            {
                warnings.Add("Microphone settings were saved, but OBS could not be reached or has no \"" + MicInputName + "\" input. Restart OBS and try again.");
                return new JObject { ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };
            }

            if (live["micDeviceId"].Value<string>() != deviceId)
            {
                var set = ObsWebSocket.InvokeRequest("SetInputSettings", new JObject { ["inputName"] = MicInputName, ["inputSettings"] = new JObject { ["device_id"] = deviceId }, ["overlay"] = true }, 3000);
                if (set.Ok) applied.Add("microphone device");
                else warnings.Add("Could not switch the microphone: " + set.Message);
            }
            if (live["micVolume"].Value<int>() != volume || volume == 0) ApplyGainVolumeLive(MicInputName, MicGainFilterName, "microphone volume", volume, applied, warnings);
            if (live["micNoiseSuppression"].Value<bool>() != noise) ApplyMicNoiseSuppressionLive(noise, applied, warnings);
            if (live["micInputSensitivity"].Value<int>() != sensitivity) ApplyMicGateLive(sensitivity, applied, warnings);

            return new JObject { ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };
        }

        // an existing noise suppression filter is only ever toggled, never replaced or moved, so a filter built in obs keeps its own settings and position.
        internal static void ApplyMicNoiseSuppressionLive(bool enabled, List<string> applied, List<string> warnings)
        {
            var list = GetSourceFilterListLive(MicInputName);
            if (list["ok"]?.Value<bool>() != true) { warnings.Add("Could not read the microphone filters: " + list["message"]); return; }
            var existing = FindNoiseSuppressFilters((JArray)list["filters"]);

            if (existing.Count == 0)
            {
                if (!enabled) return;
                var create = ObsWebSocket.InvokeRequest("CreateSourceFilter", new JObject
                {
                    ["sourceName"] = MicInputName, ["filterName"] = MicNoiseFilterName, ["filterKind"] = NoiseSuppressFilterKind,
                    ["filterSettings"] = new JObject { ["method"] = "rnnoise" },
                }, 3000);
                if (!create.Ok) { warnings.Add("Could not add noise suppression: " + create.Message); return; }
                // obs runs filters top to bottom, so suppression goes ahead of the noise gate and the gate judges the cleaned signal.
                var move = ObsWebSocket.InvokeRequest("SetSourceFilterIndex", new JObject { ["sourceName"] = MicInputName, ["filterName"] = MicNoiseFilterName, ["filterIndex"] = 0 }, 3000);
                if (!move.Ok) warnings.Add("Noise suppression was added but could not be moved ahead of the noise gate: " + move.Message);
                applied.Add("microphone noise suppression on");
                return;
            }

            // turning on only needs one of them enabled; turning off has to catch every one or the mic stays suppressed.
            var targets = enabled ? existing.Take(1) : existing.Where(f => f["filterEnabled"]?.Value<bool>() ?? false);
            foreach (var filter in targets)
            {
                var toggle = ObsWebSocket.InvokeRequest("SetSourceFilterEnabled", new JObject { ["sourceName"] = MicInputName, ["filterName"] = filter["filterName"]?.Value<string>(), ["filterEnabled"] = enabled }, 3000);
                if (toggle.Ok) applied.Add(enabled ? "microphone noise suppression on" : "microphone noise suppression off");
                else warnings.Add("Could not change noise suppression: " + toggle.Message);
            }
        }

        // the slider drives the open threshold of the mic noise gate and the far left turns the gate off. an existing gate keeps its attack, hold and release and its own open-to-close gap; one is only created when the mic has none.
        internal static void ApplyMicGateLive(int sensitivity, List<string> applied, List<string> warnings)
        {
            var list = GetSourceFilterListLive(MicInputName);
            if (list["ok"]?.Value<bool>() != true) { warnings.Add("Could not read the microphone filters: " + list["message"]); return; }
            var gates = FindNoiseGateFilters((JArray)list["filters"]);

            if (sensitivity <= MicTest.SensitivityMin)
            {
                foreach (var gate in gates.Where(g => g["filterEnabled"]?.Value<bool>() ?? false))
                {
                    var off = ObsWebSocket.InvokeRequest("SetSourceFilterEnabled", new JObject { ["sourceName"] = MicInputName, ["filterName"] = gate["filterName"]?.Value<string>(), ["filterEnabled"] = false }, 3000);
                    if (off.Ok) applied.Add("microphone input sensitivity off");
                    else warnings.Add("Could not turn the noise gate off: " + off.Message);
                }
                return;
            }

            var existing = gates.FirstOrDefault(g => g["filterEnabled"]?.Value<bool>() ?? false) ?? gates.FirstOrDefault();
            double gap = GateHysteresisDb;
            if (existing != null)
            {
                double oldOpen = existing["filterSettings"]?["open_threshold"]?.Value<double>() ?? NoiseGateDefaultOpenDb;
                double oldClose = existing["filterSettings"]?["close_threshold"]?.Value<double>() ?? NoiseGateDefaultCloseDb;
                if (oldOpen > oldClose) gap = oldOpen - oldClose;
            }
            var thresholds = new JObject { ["open_threshold"] = (double)sensitivity, ["close_threshold"] = Math.Max(sensitivity - gap, MicTest.SensitivityMin) };

            if (existing == null)
            {
                var create = ObsWebSocket.InvokeRequest("CreateSourceFilter", new JObject
                {
                    ["sourceName"] = MicInputName, ["filterName"] = MicGateFilterName, ["filterKind"] = NoiseGateFilterKind, ["filterSettings"] = thresholds,
                }, 3000);
                if (create.Ok) applied.Add("microphone input sensitivity");
                else warnings.Add("Could not add the noise gate: " + create.Message);
                return;
            }

            string name = existing["filterName"]?.Value<string>();
            var set = ObsWebSocket.InvokeRequest("SetSourceFilterSettings", new JObject { ["sourceName"] = MicInputName, ["filterName"] = name, ["filterSettings"] = thresholds, ["overlay"] = true }, 3000);
            if (!set.Ok) { warnings.Add("Could not set the input sensitivity: " + set.Message); return; }
            if (!(existing["filterEnabled"]?.Value<bool>() ?? false))
            {
                var on = ObsWebSocket.InvokeRequest("SetSourceFilterEnabled", new JObject { ["sourceName"] = MicInputName, ["filterName"] = name, ["filterEnabled"] = true }, 3000);
                if (!on.Ok) { warnings.Add("The input sensitivity was set but the noise gate could not be turned on: " + on.Message); return; }
            }
            applied.Add("microphone input sensitivity");
        }

        internal const string DesktopAudioInputName = "Desktop Audio (excl. Discord)";
        internal const string DiscordAudioInputName = "Discord Audio (record only)";

        internal static JObject ApplyAudioVolumesLive(JObject settings)
        {
            var applied = new List<string>();
            var warnings = new List<string>();
            ApplyGainVolumeLive(DesktopAudioInputName, DesktopGainFilterName, "desktop audio volume", settings["desktopAudioVolume"]?.Value<int>() ?? 100, applied, warnings);
            ApplyGainVolumeLive(DiscordAudioInputName, DiscordGainFilterName, "discord audio volume", settings["discordAudioVolume"]?.Value<int>() ?? 100, applied, warnings);
            return new JObject { ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };
        }

        // obs is the source of truth here too, same reasoning as SyncMicFromObs -- a source that does not exist (discord audio, most often) just leaves the saved value alone instead of resetting it.
        internal static JObject SyncAudioVolumesFromObs(JObject settings)
        {
            bool changed = false;
            int? desktop = ReadGainVolumeLive(DesktopAudioInputName, DesktopGainFilterName);
            if (desktop.HasValue && settings["desktopAudioVolume"]?.Value<int>() != desktop.Value) { settings["desktopAudioVolume"] = desktop.Value; changed = true; }
            int? discord = ReadGainVolumeLive(DiscordAudioInputName, DiscordGainFilterName);
            if (discord.HasValue && settings["discordAudioVolume"]?.Value<int>() != discord.Value) { settings["discordAudioVolume"] = discord.Value; changed = true; }
            if (changed) WriteSettings(settings);
            return settings;
        }

        // the mic test plays from the helper process, which desktop audio capture would otherwise record as an echo in every clip saved during the test -- the exclusion list is normally written on apply, so make sure it is there right before a test plays.
        internal static void EnsureHelperExcludedFromDesktopAudio()
        {
            lock (SettingsStore.Gate)
            {
                var current = GetDesktopAudioCaptureSettings();
                if (!current.Ok) return;
                var settings = ReadSettings();
                if (UsesOutputDeviceRecording(settings) || CapturesOutputDevice(current.Data?["inputSettings"])) return;
                // a list of the programs to take (the specific mode) never holds the helper, since the choices cannot name it
                if (current.Data?["inputSettings"]?["exclude"]?.Value<bool>() == false) return;
                var excluded = current.Data?["inputSettings"]?["executable_list"] as JArray;
                if (excluded != null && excluded.Any(e => string.Equals(e["value"]?.Value<string>(), HelperExeName, StringComparison.OrdinalIgnoreCase))) return;
                var result = SetDesktopAudioCaptureSettingsLive(settings, current);
                if (!result.Ok) Log.Write("EnsureHelperExcludedFromDesktopAudio: " + result.Message);
            }
        }

        // -- master orchestrator: given the new settings, do the minimum live work to bring a running obs (and
        // its on-disk profile/ini config) in line with them. screenshare-capture and discord-output live-apply
        // run unconditionally on every call, not gated behind applyOverlay/restartObs the way overlay and motion
        // blur are -- there is deliberately no "prepare for restart via file edit instead" branch for those two;
        // preserve that asymmetry, it is not an oversight to fix.
        internal static JObject ApplyLiveSettings(JObject settings, bool restartObs = false, bool applyOverlay = true, bool recreateBongo = true, bool applyMotionBlur = true, bool applyRuntimeOutputs = true, bool applyVideoSettings = true, bool applyReplayBufferOutput = true, bool applyMic = false, bool applyAudioVolumes = false, bool rebuildInputOverlay = false, bool applySleep = false)
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

            // only a change of the setting is applied, since applying it can take the administrator prompt
            if (applySleep)
            {
                var sleepOverride = ApplySleepOverrideSetting(settings["allowSleepWhileActive"]?.Value<bool>() ?? false);
                applied.AddRange((sleepOverride["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                warnings.AddRange((sleepOverride["warnings"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
            }

            var profileUpdates = new List<string[]>();
            foreach (var u in preset["profile"] as JArray ?? new JArray())
            {
                var arr = (JArray)u;
                profileUpdates.Add(new[] { arr[0].Value<string>(), arr[1].Value<string>(), arr[2].Value<string>() });
            }
            int replaySeconds = settings["replaySeconds"]?.Value<int>() ?? 0;
            profileUpdates.Add(new[] { "AdvOut", "RecRB", "true" });
            profileUpdates.Add(new[] { "AdvOut", "RecRBTime", replaySeconds.ToString() });
            profileUpdates.Add(new[] { "AdvOut", "RecRBSize", ScaledRbSizeMb(settings["recordingPreset"]?.Value<string>() ?? "", replaySeconds, preset).ToString(CultureInfo.InvariantCulture) });
            profileUpdates.Add(new[] { "AdvOut", "RecEncoder", encoder["id"]?.Value<string>() ?? "" });
            profileUpdates.Add(new[] { "AdvOut", "RecTracks", RecTracksValue(settings) });
            profileUpdates.Add(new[] { "Hotkeys", "ReplayBuffer", ConvertClipKeybindToBasicIni(settings["clipKeybind"] as JObject, settings["clipKeybindExtra"] as JObject) });
            profileUpdates.Add(new[] { "Hotkeys", "OBSBasic.SaveReplayBuffer", ConvertRecordingKeybindToBasicIni(settings["clipKeybind"] as JObject, settings["clipKeybindExtra"] as JObject) });
            string recordingHotkey = ConvertRecordingKeybindToBasicIni(settings["recordingKeybind"] as JObject, settings["recordingKeybindExtra"] as JObject);
            profileUpdates.Add(new[] { "Hotkeys", "OBSBasic.StartRecording", recordingHotkey });
            profileUpdates.Add(new[] { "Hotkeys", "OBSBasic.StopRecording", recordingHotkey });
            profileUpdates.Add(new[] { "Hotkeys", "OBSBasic.Screenshot", ConvertRecordingKeybindToBasicIni(settings["screenshotKeybind"] as JObject, settings["screenshotKeybindExtra"] as JObject) });
            // always pin obss recording folder to the RESOLVED clip dir (custom or the ~/Videos default) -- the helpers clip watcher/scanner uses that same resolved dir, so if obss own RecFilePath ever drifts (a reset profile, an obs update) clips land where nothing is looking for them.
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

            var audioTracks = ApplyAudioTracksLive(settings);
            applied.AddRange((audioTracks["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
            warnings.AddRange((audioTracks["warnings"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());

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
                        var overlay = ApplyOverlayLive(settings, preset, recreateBongo, rebuildInputOverlay);
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

            if (applyMic)
            {
                var mic = ApplyMicLive(settings);
                applied.AddRange((mic["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                warnings.AddRange((mic["warnings"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
            }

            if (applyAudioVolumes)
            {
                var audio = ApplyAudioVolumesLive(settings);
                applied.AddRange((audio["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                warnings.AddRange((audio["warnings"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
            }

            string restartReason = restartObs ? "Recording quality, GPU-use, clip-size, codec, audio tracks, theme, or interface layout changes require OBS to restart." : "";

            return new JObject
            {
                ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings),
                ["restartRequired"] = restartObs, ["restartReason"] = restartReason,
            };
        }

    }
}

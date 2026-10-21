using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // the record tab of the editor: the microphone of the replaykit settings through the same chain the mic test uses (volume, noise suppression, the gate of input sensitivity), or another microphone as it comes in, written to a wav that becomes an import of the editor once it stops. one take at a time, kept alive by the page polling its level like the mic test
    internal static class Voiceover
    {
        private static readonly object Gate = new object();
        private static MicLoopback _session;
        private static string _staging;
        private static string _wav;
        private static int _takes;

        private static JObject Fail(string message) => new JObject { ["ok"] = false, ["message"] = message };

        // no device is the microphone of the replaykit settings through its whole chain; a device picked in the tab is recorded as it comes in, with no noise suppression and no gate
        public static JObject Start(string device)
        {
            string deviceId;
            double gain = 1;
            bool noise = false;
            int sensitivity = MicTest.SensitivityMin;
            if (string.IsNullOrEmpty(device))
            {
                JObject settings;
                try { settings = ReplaykitSettings.ReadSettings(); }
                catch (InvalidOperationException ex) { return Fail(ex.Message); }
                deviceId = settings["micDeviceId"]?.Value<string>() ?? MicDevices.DefaultId;
                if (!MicDevices.IsValidId(deviceId)) deviceId = MicDevices.DefaultId;
                gain = ReplayKitShared.AudioVolume.ToGain(Math.Max(0, Math.Min(MicTest.VolumeMax, settings["micVolume"]?.Value<int>() ?? 100)));
                noise = settings["micNoiseSuppression"]?.Value<bool>() ?? true;
                sensitivity = Math.Max(MicTest.SensitivityMin, Math.Min(MicTest.SensitivityMax, settings["micInputSensitivity"]?.Value<int>() ?? MicTest.SensitivityDefault));
            }
            else
            {
                if (!MicDevices.IsValidId(device)) return Fail("That microphone is not valid.");
                deviceId = device;
            }

            lock (Gate)
            {
                DiscardLocked();
                // a mic test running at the same time would only meter the same microphone twice
                MicTest.Stop();
                _staging = Path.Combine(Constants.SCRATCH_DIR, "voice-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_staging);
                _wav = Path.Combine(_staging, "voiceover.wav");
                var session = MicLoopback.Start(deviceId, gain, noise, sensitivity, false, out string error, _wav);
                if (session == null)
                {
                    DiscardLocked();
                    return Fail(error);
                }
                _session = session;
                return new JObject { ["ok"] = true };
            }
        }

        public static JObject Level()
        {
            lock (Gate)
            {
                if (_session == null) return new JObject { ["ok"] = true, ["running"] = false };
                var level = _session.ReadLevel();
                return new JObject
                {
                    ["ok"] = true,
                    ["running"] = _session.IsRunning,
                    ["peak"] = Math.Round(level.Peak, 4),
                    ["seconds"] = Math.Round(_session.RecordedSeconds, 2),
                    ["ended"] = _session.IsRunning ? null : _session.EndReason,
                };
            }
        }

        // keep makes the take an import of the editor; a take shorter than a third of a second is nothing anyone meant to keep
        public static JObject Stop(bool keep)
        {
            lock (Gate)
            {
                if (_session == null) return Fail("Nothing is being recorded.");
                _session.Stop();
                double seconds = _session.RecordedSeconds;
                _session = null;
                if (!keep || seconds < 0.3 || !File.Exists(_wav))
                {
                    DiscardLocked();
                    return new JObject { ["ok"] = true, ["kept"] = false };
                }
                string wav = _wav, staging = _staging;
                _wav = null;
                _staging = null;
                _takes++;
                var import = AudioImports.BeginFromRecording(wav, staging, "Voiceover " + _takes);
                return new JObject { ["ok"] = true, ["kept"] = true, ["seconds"] = Math.Round(seconds, 2), ["import"] = import };
            }
        }

        private static void DiscardLocked()
        {
            if (_session != null) { _session.Stop(); _session = null; }
            if (_staging != null)
            {
                try { if (Directory.Exists(_staging)) Directory.Delete(_staging, true); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Log.Write("Voiceover discard: " + ex.Message); }
            }
            _staging = null;
            _wav = null;
        }
    }
}

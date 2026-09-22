using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // the http-facing half of the mic test: at most one session, started and steered by the settings and setup pages. a session is either a silent level monitor (what the input sensitivity bar reads) or a playback test (what Mic Test plays); the page polling the level is also the keep-alive, so a page that stops polling lets the session end on its own.
    internal static class MicTest
    {
        public const int VolumeMax = 200;

        // input sensitivity is the noise gate open threshold in db. the floor is the lowest threshold the obs gate accepts, and that end of the slider means the gate is off.
        public const int SensitivityMin = -96;
        public const int SensitivityMax = 0;
        public const int SensitivityDefault = -50;

        private static readonly object Gate = new object();
        private static MicLoopback _session;

        private static JObject Fail(string message) => new JObject { ["ok"] = false, ["message"] = message };

        // options for the page combo: "default" first, labelled with the device it currently resolves to.
        public static JObject GetDevicesPayload()
        {
            try
            {
                string defaultName = null;
                try { defaultName = MicDevices.DefaultDeviceName(); }
                catch (Exception ex) { Log.Write("MicTest: default device name: " + ex.Message); }

                var options = new JArray();
                var first = new JObject { ["value"] = MicDevices.DefaultId, ["label"] = "Default" };
                if (!string.IsNullOrEmpty(defaultName)) first["blurb"] = defaultName;
                options.Add(first);
                foreach (var device in MicDevices.ListActive())
                    options.Add(new JObject { ["value"] = device.Id, ["label"] = device.Name });
                return new JObject { ["ok"] = true, ["devices"] = options };
            }
            catch (Exception ex)
            {
                Log.Write("MicTest.GetDevicesPayload: " + ex.Message);
                return Fail("Could not list microphones: " + ex.Message);
            }
        }

        // (re)starts a session; a running one is replaced, so changing the device or switching between monitor and playback is just another start.
        public static JObject Start(string deviceId, int volumePercent, bool noiseSuppression, int sensitivityDb, bool playback)
        {
            if (!MicDevices.IsValidId(deviceId)) return Fail("That microphone is not valid.");
            if (volumePercent < 0 || volumePercent > VolumeMax) return Fail("Volume must be between 0 and " + VolumeMax + ".");
            if (sensitivityDb < SensitivityMin || sensitivityDb > SensitivityMax) return Fail("Input sensitivity must be between " + SensitivityMin + " and " + SensitivityMax + ".");

            try
            {
                // an id the page sends is only trusted if windows lists it right now, and the listed spelling is what gets opened.
                if (deviceId != MicDevices.DefaultId)
                {
                    var match = MicDevices.ListActive().FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase));
                    if (match == null) return Fail("That microphone is not connected.");
                    deviceId = match.Id;
                }
                // only playback makes sound from this process, so only playback needs the desktop audio exclusion
                if (playback) ReplaykitSettings.EnsureHelperExcludedFromDesktopAudio();

                lock (Gate)
                {
                    StopLocked();
                    var session = MicLoopback.Start(deviceId, volumePercent / 100.0, noiseSuppression, sensitivityDb, playback, out string error);
                    if (session == null) return Fail(error);
                    _session = session;
                    return new JObject { ["ok"] = true };
                }
            }
            catch (Exception ex)
            {
                Log.Write("MicTest.Start: " + ex.Message);
                return Fail("Could not start the microphone test: " + ex.Message);
            }
        }

        // the page pushes its current (possibly unsaved) values here, so the preview follows the controls the moment they move.
        public static JObject SetParams(int volumePercent, bool noiseSuppression, int sensitivityDb)
        {
            if (volumePercent < 0 || volumePercent > VolumeMax) return Fail("Volume must be between 0 and " + VolumeMax + ".");
            if (sensitivityDb < SensitivityMin || sensitivityDb > SensitivityMax) return Fail("Input sensitivity must be between " + SensitivityMin + " and " + SensitivityMax + ".");
            lock (Gate)
            {
                if (_session == null || !_session.IsRunning) return Fail("The microphone test is not running.");
                _session.SetParams(volumePercent / 100.0, noiseSuppression, sensitivityDb);
                return new JObject { ["ok"] = true };
            }
        }

        // ok with active=false is a normal answer -- the test ended (stopped, idle, or a device error) and message says why when it was not a plain stop.
        public static JObject ReadLevel()
        {
            lock (Gate)
            {
                var session = _session;
                if (session == null) return Level(false, false, 0, 0, 0, "");
                if (!session.IsRunning)
                {
                    _session = null;
                    return Level(false, session.Playback, 0, 0, 0, EndMessage(session.EndReason));
                }
                var level = session.ReadLevel();
                var payload = Level(true, session.Playback, level.Peak, level.GatePeak, level.Rms, "");
                // the values the session is running with right now, so a live change from the page can be confirmed instead of assumed
                payload["volume"] = (int)Math.Round(session.Gain * 100);
                payload["gate"] = session.SensitivityDb;
                payload["ns"] = session.NoiseSuppression;
                return payload;
            }
        }

        public static JObject Stop()
        {
            lock (Gate) StopLocked();
            return new JObject { ["ok"] = true };
        }

        private static void StopLocked()
        {
            var session = _session;
            _session = null;
            if (session != null) session.Stop();
        }

        // peak is after noise suppression, the gate and the volume (what would be recorded), gatePeak is before the gate and the volume (what the noise gate compares to its threshold).
        private static JObject Level(bool active, bool playback, double peak, double gatePeak, double rms, string message) =>
            new JObject { ["ok"] = true, ["active"] = active, ["playback"] = playback, ["peak"] = peak, ["gatePeak"] = gatePeak, ["rms"] = rms, ["message"] = message };

        // idle and a plain stop are expected endings; anything else is worth showing.
        private static string EndMessage(string reason)
        {
            if (string.IsNullOrEmpty(reason) || reason == "stopped" || reason == "idle" || reason == "ended") return "";
            if (reason == "time limit") return "The microphone test stopped after " + MicLoopback.MaxRunMinutes + " minutes.";
            return reason;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace ReplayKitSetup
{
    // enumerate windows audio devices. obs stores endpoint ids in mmdevice format -- {0.0.1.00000000}.<endpoint-guid> for capture, {0.0.0.00000000}.<endpoint-guid> for render. friendly names live under each endpoints properties subkey. ported from obs_replaykit/audio.py.
    public static class Audio
    {
        // pkey_device_friendlyname in the mmdevices property store.
        private const string PkeyFriendlyName = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";

        private const string CaptureKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture";
        private const string RenderKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";

        // devicestate bits. only the low nibble carries the documented mmdevice states; some virtual-audio drivers (voicemeeter etc.) set extra high bits.
        private const int StateActive = 0x1;

        // literal string obs uses for "follow the system defualt device".
        public const string DEFAULT_DEVICE_ID = "default";
        public const string DEFAULT_DEVICE_NAME = "Default (system)";

        public sealed class AudioDevice
        {
            public string DeviceId { get; }
            public string Name { get; }

            public AudioDevice(string deviceId, string name)
            {
                DeviceId = deviceId;
                Name = name;
            }
        }

        private static string ToObsId(string endpointGuid) => "{0.0.1.00000000}." + endpointGuid;
        private static string ToRenderObsId(string endpointGuid) => "{0.0.0.00000000}." + endpointGuid;

        private static IEnumerable<(string Endpoint, string Name)> ActiveEndpoints(string rootKeyPath)
        {
            using (var root = Registry.LocalMachine.OpenSubKey(rootKeyPath))
            {
                if (root == null) yield break;

                foreach (var endpoint in root.GetSubKeyNames())
                {
                    int state;
                    using (var ep = root.OpenSubKey(endpoint))
                    {
                        if (ep == null) continue;
                        var stateValue = ep.GetValue("DeviceState");
                        if (stateValue == null) continue;
                        state = Convert.ToInt32(stateValue);
                    }
                    if ((state & StateActive) == 0) continue;

                    string name = endpoint;
                    using (var props = root.OpenSubKey(endpoint + @"\Properties"))
                    {
                        var nameValue = props?.GetValue(PkeyFriendlyName) as string;
                        if (!string.IsNullOrEmpty(nameValue)) name = nameValue;
                    }

                    yield return (endpoint, name);
                }
            }
        }

        // obs-format render device id matching friendlyName (case-insensitive exact). used to write the fresh obs stream audio id straight into basic.ini so obs picks the right monitoring sink on first launch. unlike ActiveEndpoints, an endpoint whose friendly name cant be read is skipped rather than matched by its raw guid -- matches the python originals continue-on-failure behavior exactly.
        public static string FindRenderEndpoint(string friendlyName)
        {
            string needle = friendlyName.ToLowerInvariant();
            using (var render = Registry.LocalMachine.OpenSubKey(RenderKey))
            {
                if (render == null) return null;

                foreach (var endpoint in render.GetSubKeyNames())
                {
                    using (var ep = render.OpenSubKey(endpoint))
                    {
                        var stateValue = ep?.GetValue("DeviceState");
                        if (stateValue == null) continue;
                        if ((Convert.ToInt32(stateValue) & StateActive) == 0) continue;
                    }

                    using (var props = render.OpenSubKey(endpoint + @"\Properties"))
                    {
                        var name = props?.GetValue(PkeyFriendlyName) as string;
                        if (name == null) continue;
                        if (string.Equals(name, needle, StringComparison.OrdinalIgnoreCase)) return ToRenderObsId(endpoint);
                    }
                }
            }
            return null;
        }

        private static List<AudioDevice> ListRenderEndpoints()
        {
            return ActiveEndpoints(RenderKey).Select(e => new AudioDevice(ToRenderObsId(e.Endpoint), e.Name)).ToList();
        }

        // rank only the replaykit/vb-cable render sink; never match normal speakers.
        private static int? ReplaykitMonitorRank(string name)
        {
            string lower = name.Trim().ToLowerInvariant();
            if (lower.Contains("surround") || lower.Contains("16ch")) return null;
            if (lower == "obs stream audio") return 0;
            if (lower.StartsWith("obs stream audio") && !lower.Contains("loopback")) return 10;
            if (lower.StartsWith("cable input")) return 20;
            return null;
        }

        // best active render endpoint for obs monitoring, before or after replaykits vb-cable rename.
        public static AudioDevice FindReplaykitMonitoringEndpoint()
        {
            var candidates = new List<(int Rank, string LowerName, AudioDevice Device)>();
            foreach (var device in ListRenderEndpoints())
            {
                var rank = ReplaykitMonitorRank(device.Name);
                if (rank.HasValue) candidates.Add((rank.Value, device.Name.ToLowerInvariant(), device));
            }
            if (candidates.Count == 0) return null;
            candidates.Sort((a, b) =>
            {
                int cmp = a.Rank.CompareTo(b.Rank);
                return cmp != 0 ? cmp : string.CompareOrdinal(a.LowerName, b.LowerName);
            });
            return candidates[0].Device;
        }

        // active capture devices known to windows, alphabetized.
        public static List<AudioDevice> ListMicrophones()
        {
            var devices = ActiveEndpoints(CaptureKey).Select(e => new AudioDevice(ToObsId(e.Endpoint), e.Name)).ToList();
            devices.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return devices;
        }
    }
}

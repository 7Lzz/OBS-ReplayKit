using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using ReplayKitShared;

namespace ReplayKitSetup
{
    // the windows audio endpoints as the registry lists them. obs stores endpoint ids in mmdevice format -- {0.0.1.00000000}.<endpoint-guid> for capture, {0.0.0.00000000}.<endpoint-guid> for render. friendly names live under each endpoints properties subkey.
    public static class Audio
    {
        // pkey_device_friendlyname in the mmdevices property store, as the registry names its value
        internal const string FriendlyNameValue = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";

        internal const string CaptureKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture";
        internal const string RenderKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";

        // devicestate bits. only the low nibble carries the documented mmdevice states; some virtual-audio drivers (voicemeeter etc.) set extra high bits.
        private const int StateActive = 0x1;

        // literal string obs uses for "follow the system defualt device".
        public const string DEFAULT_DEVICE_ID = SettingDefaults.MicDeviceId;
        public const string DEFAULT_DEVICE_NAME = "Default (system)";

        // the render endpoint of the share cable obs monitors to, before or after the rename VbCable gives it, as obs names it in basic.ini; null when the cable is not on the machine
        public static (string DeviceId, string Name)? FindShareCableEndpoint()
        {
            var candidates = new List<(int Rank, string DisplayName, string DeviceId)>();

            using (var root = Registry.LocalMachine.OpenSubKey(RenderKey))
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
                            if ((Convert.ToInt32(stateValue) & StateActive) == 0) continue;
                        }
                        using (var props = root.OpenSubKey(guid + @"\Properties"))
                        {
                            var name = props?.GetValue(FriendlyNameValue) as string;
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
    }
}

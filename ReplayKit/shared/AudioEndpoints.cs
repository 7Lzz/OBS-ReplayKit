using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ReplayKitShared
{
    // the windows audio endpoints as the registry lists them, and the one of them the share preview audio plays to. obs stores endpoint ids in mmdevice format -- {0.0.1.00000000}.<endpoint-guid> for capture, {0.0.0.00000000}.<endpoint-guid> for render. friendly names live under each endpoints properties subkey.
    internal static class AudioEndpoints
    {
        // pkey_device_friendlyname in the mmdevices property store, as the registry names its value
        public const string FriendlyNameValue = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";

        public const string CaptureKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture";
        public const string RenderKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";

        // devicestate bits. only the low nibble carries the documented mmdevice states; some virtual-audio drivers (voicemeeter etc.) set extra high bits.
        private const int StateActive = 0x1;

        // the render endpoint of the vb-audio cable as the driver names it, and the name the setup gives it
        public const string CableRenderName = "CABLE Input";
        public const string ShareCableName = "OBS Stream Audio";

        // the render endpoint of the share cable obs monitors to, before or after the rename, as obs names it in basic.ini; null when the cable is not on the machine. a registry that cannot be read throws
        public static (string DeviceId, string Name)? FindShareCable()
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
                            if (string.IsNullOrWhiteSpace(name)) continue;
                            displayName = name.Trim();
                        }
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException || ex is System.IO.IOException)
                    {
                        continue;
                    }

                    // some endpoint names carry a leading index (2 - Speakers); the surround and loopback siblings of the cable are never what obs monitors to
                    string canonical = Regex.Replace(displayName, @"^\s*\d+\s*-\s*", "").Trim();
                    if (Regex.IsMatch(canonical, "surround|16ch|loopback|do not select", RegexOptions.IgnoreCase)) continue;
                    int rank;
                    if (string.Equals(canonical, ShareCableName, StringComparison.OrdinalIgnoreCase)) rank = 0;
                    else if (canonical.StartsWith(ShareCableName, StringComparison.OrdinalIgnoreCase)) rank = 1;
                    else if (canonical.StartsWith(CableRenderName, StringComparison.OrdinalIgnoreCase)) rank = 2;
                    else continue;
                    candidates.Add((rank, displayName, "{0.0.0.00000000}." + guid));
                }
            }

            if (candidates.Count == 0) return null;
            var best = candidates.OrderBy(c => c.Rank).ThenBy(c => c.DisplayName.ToLowerInvariant(), StringComparer.Ordinal).First();
            return (best.DeviceId, best.DisplayName);
        }
    }
}

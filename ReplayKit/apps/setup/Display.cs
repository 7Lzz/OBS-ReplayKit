using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace ReplayKitSetup
{
    // detect windows displays in the format obss monitor_capture source uses (\\?\DISPLAY#<edid-model>#...#{class-guid}). produced by EnumDisplayDevicesW with EDD_GET_DEVICE_INTERFACE_NAME against the adapter device name from GetMonitorInfoExW.
    public sealed class DisplayInfo
    {
        public bool IsPrimary { get; set; }
        public string Adapter { get; set; } // e.g. \\.\display1 -- the gdi adapter device name
        public string DeviceId { get; set; } // e.g. \\?\display#msi3cd7#5&...#{guid} -- obss monitor_id
        public string FriendlyName { get; set; } // what windows tells us (often "generic pnp monitor")
        public int Width { get; set; }
        public int Height { get; set; }

        // edid model code from DeviceId (e.g. "MSI3CD7").
        public string EdidModel
        {
            get
            {
                var parts = (DeviceId ?? "").Split('#');
                return parts.Length > 1 ? parts[1] : "?";
            }
        }
    }

    public static class Display
    {
        private const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x00000001;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAY_DEVICEW
        {
            public uint cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplayDevicesW(string lpDevice, uint iDevNum, ref DISPLAY_DEVICEW lpDisplayDevice, uint dwFlags);

        internal static IEnumerable<string> AdapterRegistryPaths()
        {
            const string prefix = @"\Registry\Machine\";
            for (uint index = 0; ; index++)
            {
                var device = new DISPLAY_DEVICEW { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICEW>() };
                if (!EnumDisplayDevicesW(null, index, ref device, 0)) yield break;
                if ((device.StateFlags & 8) == 0 && device.DeviceKey != null &&
                    device.DeviceKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    yield return device.DeviceKey.Substring(prefix.Length);
            }
        }

        // every active display, primary first.
        public static List<DisplayInfo> ListDisplays()
        {
            var displays = new List<DisplayInfo>();
            foreach (var monitor in ReplayKitShared.Monitors.List(out _) ?? new List<ReplayKitShared.DisplayMonitor>())
            {
                var dd = new DISPLAY_DEVICEW { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICEW>() };
                bool identified = EnumDisplayDevicesW(monitor.Device, 0, ref dd, EDD_GET_DEVICE_INTERFACE_NAME);
                displays.Add(new DisplayInfo
                {
                    IsPrimary = monitor.Primary,
                    Adapter = monitor.Device,
                    DeviceId = identified && !string.IsNullOrWhiteSpace(dd.DeviceID) ? dd.DeviceID : monitor.Device,
                    FriendlyName = identified && !string.IsNullOrWhiteSpace(dd.DeviceString) ? dd.DeviceString : monitor.Device,
                    Width = monitor.Bounds.Width,
                    Height = monitor.Bounds.Height,
                });
            }

            return displays.OrderBy(d => !d.IsPrimary).ToList();
        }

        // the users primary monitor, or null if no display is reachable.
        public static DisplayInfo PrimaryDisplay()
        {
            return ListDisplays().FirstOrDefault(d => d.IsPrimary);
        }
    }
}

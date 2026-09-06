using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace ReplayKitSetup
{
    // detect windows displays in the format obss monitor_capture source uses (\\?\DISPLAY#<edid-model>#...#{class-guid}). produced by EnumDisplayDevicesW with EDD_GET_DEVICE_INTERFACE_NAME against the adapter device name from GetMonitorInfoExW. ported from obs_replaykit/display.py.
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
                var parts = DeviceId.Split('#');
                return parts.Length > 1 ? parts[1] : "?";
            }
        }
    }

    public static class Display
    {
        private const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x00000001;
        private const uint MONITORINFOF_PRIMARY = 0x00000001;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

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

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEXW
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEXW lpmi);

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
            var monitors = new List<MONITORINFOEXW>();

            bool Callback(IntPtr hMon, IntPtr hdc, ref RECT rect, IntPtr lparam)
            {
                var info = new MONITORINFOEXW { cbSize = (uint)Marshal.SizeOf<MONITORINFOEXW>() };
                if (GetMonitorInfoW(hMon, ref info)) monitors.Add(info);
                return true;
            }

            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);

            var displays = new List<DisplayInfo>();
            foreach (var info in monitors)
            {
                var dd = new DISPLAY_DEVICEW { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICEW>() };
                if (!EnumDisplayDevicesW(info.szDevice, 0, ref dd, EDD_GET_DEVICE_INTERFACE_NAME)) continue;
                displays.Add(new DisplayInfo
                {
                    IsPrimary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0,
                    Adapter = info.szDevice,
                    DeviceId = dd.DeviceID,
                    FriendlyName = dd.DeviceString,
                    Width = info.rcMonitor.Right - info.rcMonitor.Left,
                    Height = info.rcMonitor.Bottom - info.rcMonitor.Top,
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

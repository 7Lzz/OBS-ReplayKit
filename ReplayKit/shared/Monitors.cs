using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace ReplayKitShared
{
    public sealed class DisplayMonitor
    {
        // the gdi device name, \\.\DISPLAY1 and so on
        public string Device;
        public bool Primary;
        public Rectangle Bounds;
        public Rectangle WorkArea;
    }

    // the displays as windows reports them at the moment of the call, for every monitor read of the helper and the setup. nothing is kept between calls, since windows hands out new monitor handles whenever it rebuilds its displays (a monitor that sleeps, wakes or is plugged back in) and a list kept from before reads every work area as empty -- the winforms screen list did that and left share preview unparked
    public static class Monitors
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int Size;
            public RECT Monitor;
            public RECT Work;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
        }

        private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

        [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFOEX info);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const uint MONITORINFOF_PRIMARY = 1;

        // every display in the order windows lists them; null with the reason when windows could not list them
        public static List<DisplayMonitor> List(out string error)
        {
            error = "";
            var monitors = new List<DisplayMonitor>();
            MonitorEnumProc callback = (monitor, hdc, rect, data) =>
            {
                var found = Describe(monitor);
                if (found != null) monitors.Add(found);
                return true;
            };
            if (EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero)) return monitors;
            error = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;
            return null;
        }

        public static DisplayMonitor Primary() => List(out _)?.Find(m => m.Primary);

        // the display the window is on, or the nearest one when it is on none
        public static DisplayMonitor Nearest(IntPtr hwnd) => Describe(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST));

        public static DisplayMonitor Nearest(Point point) => Describe(MonitorFromPoint(new POINT { X = point.X, Y = point.Y }, MONITOR_DEFAULTTONEAREST));

        private static DisplayMonitor Describe(IntPtr monitor)
        {
            if (monitor == IntPtr.Zero) return null;
            var info = new MONITORINFOEX { Size = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfoW(monitor, ref info)) return null;
            return new DisplayMonitor
            {
                Device = info.Device ?? "",
                Primary = (info.Flags & MONITORINFOF_PRIMARY) != 0,
                Bounds = Rectangle.FromLTRB(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom),
                WorkArea = Rectangle.FromLTRB(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom),
            };
        }
    }
}

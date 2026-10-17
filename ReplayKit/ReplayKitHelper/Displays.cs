using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ReplayKitHelper
{
    // the active displays as obs display capture knows them: monitorDevicePath is the same \\?\DISPLAY#... path obs keeps as monitor_id, and the friendly name is the one its own list shows
    internal static class Displays
    {
        internal sealed class Entry
        {
            public string Id;
            public string Name;
            public int Width;
            public int Height;
            public bool Primary;
        }

        private const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
        private const int DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
        private const int DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE = 1;

        [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_SOURCE_INFO { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_RATIONAL { public uint Numerator; public uint Denominator; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology; public uint rotation; public uint scaling;
            public DISPLAYCONFIG_RATIONAL refreshRate; public uint scanLineOrdering; public int targetAvailable; public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_INFO { public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo; public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo; public uint flags; }

        [StructLayout(LayoutKind.Sequential)] private struct POINTL { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_SOURCE_MODE { public uint width; public uint height; public uint pixelFormat; public POINTL position; }

        // the mode union is 48 bytes; only the source mode is read, the target and desktop image modes share the same bytes
        [StructLayout(LayoutKind.Explicit, Size = 64)]
        private struct DISPLAYCONFIG_MODE_INFO
        {
            [FieldOffset(0)] public int infoType;
            [FieldOffset(4)] public uint id;
            [FieldOffset(8)] public LUID adapterId;
            [FieldOffset(16)] public DISPLAYCONFIG_SOURCE_MODE sourceMode;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public int type; public int size; public LUID adapterId; public uint id; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint flags; public uint outputTechnology; public ushort edidManufactureId; public ushort edidProductCodeId; public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
        }

        [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);
        [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray, ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);
        [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

        // empty when windows does not answer; the caller then offers only the primary choice
        public static List<Entry> Active()
        {
            var list = new List<Entry>();
            try
            {
                if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != 0) return list;
                var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
                var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
                if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return list;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < pathCount; i++)
                {
                    var path = paths[i];
                    var name = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                    name.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                    name.header.size = Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
                    name.header.adapterId = path.targetInfo.adapterId;
                    name.header.id = path.targetInfo.id;
                    if (DisplayConfigGetDeviceInfo(ref name) != 0 || string.IsNullOrWhiteSpace(name.monitorDevicePath)) continue;
                    // a mirrored display shows up once per path
                    if (!seen.Add(name.monitorDevicePath)) continue;
                    var entry = new Entry { Id = name.monitorDevicePath, Name = string.IsNullOrWhiteSpace(name.monitorFriendlyDeviceName) ? "Display " + (list.Count + 1) : name.monitorFriendlyDeviceName };
                    uint modeIndex = path.sourceInfo.modeInfoIdx;
                    if (modeIndex < modeCount && modes[modeIndex].infoType == DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
                    {
                        var mode = modes[modeIndex].sourceMode;
                        entry.Width = (int)mode.width;
                        entry.Height = (int)mode.height;
                        // the primary display is the one whose desktop starts at 0,0
                        entry.Primary = mode.position.x == 0 && mode.position.y == 0;
                    }
                    list.Add(entry);
                }
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException || ex is DllNotFoundException) { Log.Write("Displays.Active: " + ex.Message); }
            return list;
        }

        public static Entry Primary()
        {
            foreach (var entry in Active()) if (entry.Primary) return entry;
            return null;
        }
    }
}

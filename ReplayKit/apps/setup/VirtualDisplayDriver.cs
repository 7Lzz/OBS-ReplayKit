using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ReplayKitSetup
{
    // the optional virtual display driver older releases could install (device "Virtual Display Driver", driver package MttVDD.inf): its device and its driver package are removed through the setup api of windows, so nothing depends on the language of the output of a tool
    internal static class VirtualDisplayDriver
    {
        private const string DeviceName = "Virtual Display Driver";
        private const string DriverInf = "MttVDD.inf";
        private const string SampleFolder = @"C:\IddSampleDriver";

        [StructLayout(LayoutKind.Sequential)]
        private struct SpDevinfoData
        {
            public uint cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SpOriginalFileInfo
        {
            public uint cbSize;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string OriginalInfName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string OriginalCatalogName;
        }

        private const uint DIGCF_ALLCLASSES = 0x4;
        private const uint SPDRP_DEVICEDESC = 0x00;
        private const uint SPDRP_FRIENDLYNAME = 0x0C;
        private const uint INFINFO_INF_NAME_IS_ABSOLUTE = 2;
        private const uint SUOI_FORCEDELETE = 1;

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SpDevinfoData deviceInfoData);
        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr deviceInfoSet, ref SpDevinfoData deviceInfoData, uint property, out uint propertyRegDataType, byte[] propertyBuffer, uint propertyBufferSize, out uint requiredSize);
        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
        [DllImport("newdev.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DiUninstallDevice(IntPtr hwndParent, IntPtr deviceInfoSet, ref SpDevinfoData deviceInfoData, uint flags, out bool needReboot);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupGetInfInformationW(string infSpec, uint searchControl, IntPtr returnBuffer, uint returnBufferSize, out uint requiredSize);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupQueryInfOriginalFileInformationW(IntPtr infInformation, uint infIndex, IntPtr alternatePlatformInfo, ref SpOriginalFileInfo originalFileInfo);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupUninstallOEMInfW(string infFileName, uint flags, IntPtr reserved);

        public static bool Remove(Action<string> log)
        {
            bool ok = RemoveDevices(log, out int devices);
            ok &= RemoveDriverPackages(log, out int packages);
            if (Directory.Exists(SampleFolder))
            {
                try { Directory.Delete(SampleFolder, true); log?.Invoke("removed " + SampleFolder); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { log?.Invoke("warn: could not remove " + SampleFolder + ": " + ex.Message); ok = false; }
            }
            log?.Invoke($"virtual display driver: checked {devices} device(s) and {packages} driver package(s)");
            return ok;
        }

        private static bool RemoveDevices(Action<string> log, out int checkedCount)
        {
            checkedCount = 0;
            IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, DIGCF_ALLCLASSES);
            if (set == new IntPtr(-1))
            {
                log?.Invoke("warn: the device list could not be read (" + Marshal.GetLastWin32Error() + ")");
                return false;
            }
            bool ok = true;
            try
            {
                var buffer = new byte[1024];
                for (uint index = 0; ; index++)
                {
                    var device = new SpDevinfoData { cbSize = (uint)Marshal.SizeOf<SpDevinfoData>() };
                    if (!SetupDiEnumDeviceInfo(set, index, ref device)) break;
                    checkedCount++;
                    // the name device manager shows: the friendly name, or the description from the inf for a device that has none, which is how this driver names its device
                    string name = DeviceText(set, ref device, SPDRP_FRIENDLYNAME, buffer) ?? DeviceText(set, ref device, SPDRP_DEVICEDESC, buffer);
                    if (!string.Equals(name, DeviceName, StringComparison.Ordinal)) continue;
                    if (DiUninstallDevice(IntPtr.Zero, set, ref device, 0, out _)) log?.Invoke("removed device: " + DeviceName);
                    else { log?.Invoke("warn: could not remove device " + DeviceName + " (" + Marshal.GetLastWin32Error() + ")"); ok = false; }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return ok;
        }

        private static string DeviceText(IntPtr set, ref SpDevinfoData device, uint property, byte[] buffer)
        {
            if (!SetupDiGetDeviceRegistryPropertyW(set, ref device, property, out _, buffer, (uint)buffer.Length, out uint size) || size < 2) return null;
            return System.Text.Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0');
        }

        // the driver store keeps each third-party package as oemNN.inf; the name it was installed under is in its original file information
        private static bool RemoveDriverPackages(Action<string> log, out int checkedCount)
        {
            checkedCount = 0;
            string infDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
            string[] packages;
            try { packages = Directory.GetFiles(infDir, "oem*.inf"); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { log?.Invoke("warn: driver packages could not be listed: " + ex.Message); return false; }
            bool ok = true;
            foreach (string inf in packages)
            {
                checkedCount++;
                if (!string.Equals(OriginalName(inf), DriverInf, StringComparison.OrdinalIgnoreCase)) continue;
                string published = Path.GetFileName(inf);
                if (SetupUninstallOEMInfW(published, SUOI_FORCEDELETE, IntPtr.Zero)) log?.Invoke("removed driver package " + published + " (" + DriverInf + ")");
                else { log?.Invoke("warn: could not remove driver package " + published + " (" + Marshal.GetLastWin32Error() + ")"); ok = false; }
            }
            return ok;
        }

        private static string OriginalName(string infPath)
        {
            if (!SetupGetInfInformationW(infPath, INFINFO_INF_NAME_IS_ABSOLUTE, IntPtr.Zero, 0, out uint size) || size == 0) return null;
            IntPtr info = Marshal.AllocHGlobal((int)size);
            try
            {
                if (!SetupGetInfInformationW(infPath, INFINFO_INF_NAME_IS_ABSOLUTE, info, size, out _)) return null;
                var original = new SpOriginalFileInfo { cbSize = (uint)Marshal.SizeOf<SpOriginalFileInfo>() };
                return SetupQueryInfOriginalFileInformationW(info, 0, IntPtr.Zero, ref original) ? original.OriginalInfName : null;
            }
            finally { Marshal.FreeHGlobal(info); }
        }
    }
}

using System;
using System.Runtime.InteropServices;

namespace ReplayKitSetup
{
    // native port of the com-interop helper VbCable.cs used to shell out to a fresh powershell process for (Add-Type-compiling this exact same class at runtime). undocumented shell audio apis -- IPolicyConfigVistaClient/IMMDeviceEnumerator -- used to switch/rename default audio endpoints. same guids and interface layouts as the powershell version, just compiled in-process instead of spawned per call.
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumerator { }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, out IntPtr ppInterface);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IntPtr ppProperties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        [PreserveSig] int GetState(out int pdwState);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROPVARIANT
    {
        public ushort vt;
        public ushort r1;
        public ushort r2;
        public ushort r3;
        public IntPtr pszVal;
        public IntPtr padding;
    }

    [ComImport, Guid("294935CE-F637-4E7C-A41B-AB255460B862")]
    internal class _CPolicyConfigVistaClient { }

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    internal class _CPolicyConfigClient { }

    [Guid("568B9108-44BF-40B4-9006-86AFE5B5A620"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfigVistaClient
    {
        [PreserveSig] int GetMixFormat(string a, IntPtr b);
        [PreserveSig] int GetDeviceFormat(string a, bool b, IntPtr c);
        [PreserveSig] int SetDeviceFormat(string a, IntPtr b, IntPtr c);
        [PreserveSig] int GetProcessingPeriod(string a, bool b, IntPtr c, IntPtr d);
        [PreserveSig] int SetProcessingPeriod(string a, IntPtr b);
        [PreserveSig] int GetShareMode(string a, IntPtr b);
        [PreserveSig] int SetShareMode(string a, IntPtr b);
        [PreserveSig] int GetPropertyValue(string a, bool b, ref PROPERTYKEY key, IntPtr pv);
        [PreserveSig] int SetPropertyValue(string deviceId, bool bFxStore, ref PROPERTYKEY key, ref PROPVARIANT pv);
        [PreserveSig] int SetDefaultEndpoint(string deviceId, uint role);
        [PreserveSig] int SetEndpointVisibility(string a, bool b);
    }

    [Guid("F8679F50-850A-41CF-9C72-430F290290C8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfigClient
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string a, IntPtr b);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string a, int b, IntPtr c);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string a);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string a, IntPtr b, IntPtr c);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string a, IntPtr b);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string a, IntPtr b);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string a, IntPtr b);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string a, ref PROPERTYKEY key, IntPtr pv);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string a, ref PROPERTYKEY key, ref PROPVARIANT pv);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }

    internal static class AudioHelper
    {
        private static string GetDefault(int dataFlow)
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            IMMDevice dev;
            int rc = enumerator.GetDefaultAudioEndpoint(dataFlow, 0, out dev);
            if (rc != 0 || dev == null) return null;
            string id;
            return dev.GetId(out id) != 0 ? null : id;
        }

        private static int SetDefault(string id)
        {
            var client = (IPolicyConfigVistaClient)new _CPolicyConfigVistaClient();
            int rc0 = client.SetDefaultEndpoint(id, 0);
            int rc1 = client.SetDefaultEndpoint(id, 1);
            int rc2 = client.SetDefaultEndpoint(id, 2);
            return rc0 | rc1 | rc2;
        }

        public static string GetDefaultRender() => GetDefault(0);
        public static string GetDefaultCapture() => GetDefault(1);
        public static int SetDefaultRender(string id) => SetDefault(id);
        public static int SetDefaultCapture(string id) => SetDefault(id);

        public static int SetEndpointVisible(string id, bool visible)
        {
            var client = (IPolicyConfigClient)new _CPolicyConfigClient();
            return client.SetEndpointVisibility(id, visible ? 1 : 0);
        }

        public static int RenameEndpoint(string deviceId, string newName)
        {
            var client = (IPolicyConfigVistaClient)new _CPolicyConfigVistaClient();
            var key = new PROPERTYKEY
            {
                fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
                pid = 2,
            };
            IntPtr strPtr = Marshal.StringToCoTaskMemUni(newName);
            var pv = new PROPVARIANT { vt = 31, pszVal = strPtr };
            try
            {
                return client.SetPropertyValue(deviceId, false, ref key, ref pv);
            }
            finally
            {
                Marshal.FreeCoTaskMem(strPtr);
            }
        }
    }
}

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ReplayKitSetup
{
    // minimal .lnk COM interop for the clip-notification shortcut. windows toasts from an unpackaged app need SOME
    // start-menu shortcut carrying the app's AppUserModelID; rather than ship a separate "OBS ReplayKit" entry we stamp
    // that id onto OBS Studio's own shortcut (or, if that one is not writable, a same-named copy in the user scope so
    // the start menu still shows a single "OBS Studio"). mirrors the interop the helper's ToastNotify.cs uses.
    internal static class Shortcuts
    {
        // the id the helper registers with the toast API. keep in step with ReplayKitHelper.ToastNotify.Aumid.
        public const string ClipNotifyAumid = "OBS.ReplayKit.Clips";

        // IPersistFile.Load with STGM_READ is refused for .lnk property edits (STG_E_ACCESSDENIED even elevated); READWRITE works.
        private const uint STGM_READWRITE = 0x00000002;

        public static string AllUsersPrograms =>
            Path.Combine(Config.PROGRAMDATA, "Microsoft", "Windows", "Start Menu", "Programs");
        public static string UserPrograms =>
            Path.Combine(Config.APPDATA, "Microsoft", "Windows", "Start Menu", "Programs");

        // add the AppUserModelID property to an existing .lnk. returns false if the file is missing or not writable.
        public static bool StampAumid(string lnkPath, string aumid, Action<string> log = null)
        {
            if (!File.Exists(lnkPath)) return false;
            try
            {
                var link = (IShellLinkW)new CShellLink();
                ((IPersistFile)link).Load(lnkPath, STGM_READWRITE);
                SetAumidProp(link, aumid);
                ((IPersistFile)link).Save(lnkPath, true);
                return true;
            }
            catch (Exception ex) when (ex is COMException || ex is UnauthorizedAccessException || ex is IOException)
            {
                log?.Invoke("note: could not stamp notification id onto " + lnkPath + ": " + ex.Message);
                return false;
            }
        }

        // clear the AppUserModelID property from a .lnk (leave OBS's own shortcut pristine on a keep-OBS uninstall).
        public static void ClearAumid(string lnkPath, Action<string> log = null)
        {
            if (!File.Exists(lnkPath)) return;
            try
            {
                var link = (IShellLinkW)new CShellLink();
                ((IPersistFile)link).Load(lnkPath, STGM_READWRITE);
                var store = (IPropertyStore)link;
                var key = PKEY_AppUserModel_ID;
                var pv = new PropVariant { vt = 0 /* VT_EMPTY */ };
                store.SetValue(ref key, ref pv);
                store.Commit();
                ((IPersistFile)link).Save(lnkPath, true);
            }
            catch (Exception ex) when (ex is COMException || ex is UnauthorizedAccessException || ex is IOException)
            {
                log?.Invoke("note: could not clear notification id from " + lnkPath + ": " + ex.Message);
            }
        }

        // create a launcher .lnk (target obs64.exe, OBS's own icon) carrying the AppUserModelID.
        public static bool Write(string lnkPath, string target, string aumid, Action<string> log = null)
        {
            if (string.IsNullOrEmpty(target) || !File.Exists(target)) return false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(lnkPath));
                var link = (IShellLinkW)new CShellLink();
                link.SetPath(target);
                link.SetArguments("");
                link.SetWorkingDirectory(Path.GetDirectoryName(target) ?? "");
                link.SetIconLocation(target, 0);
                SetAumidProp(link, aumid);
                ((IPersistFile)link).Save(lnkPath, true);
                return true;
            }
            catch (Exception ex) when (ex is COMException || ex is UnauthorizedAccessException || ex is IOException)
            {
                log?.Invoke("note: could not write " + lnkPath + ": " + ex.Message);
                return false;
            }
        }

        public static bool HasAumid(string lnkPath, string aumid)
        {
            if (!File.Exists(lnkPath)) return false;
            try
            {
                var link = (IShellLinkW)new CShellLink();
                ((IPersistFile)link).Load(lnkPath, STGM_READWRITE);
                var store = (IPropertyStore)link;
                var key = PKEY_AppUserModel_ID;
                store.GetValue(ref key, out PropVariant pv);
                string val = pv.vt == 31 && pv.pointerValue != IntPtr.Zero ? Marshal.PtrToStringUni(pv.pointerValue) : null;
                return string.Equals(val, aumid, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is COMException || ex is UnauthorizedAccessException || ex is IOException)
            {
                return false;
            }
        }

        private static void SetAumidProp(IShellLinkW link, string aumid)
        {
            var store = (IPropertyStore)link;
            var key = PKEY_AppUserModel_ID;
            var pv = new PropVariant { vt = 31 /* VT_LPWSTR */, pointerValue = Marshal.StringToCoTaskMemUni(aumid) };
            store.SetValue(ref key, ref pv);
            store.Commit();
            Marshal.FreeCoTaskMem(pv.pointerValue);
        }

        private static readonly PropertyKey PKEY_AppUserModel_ID =
            new PropertyKey { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class CShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
        private interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            [PreserveSig] int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
        private interface IPropertyStore
        {
            void GetCount(out uint cProps);
            void GetAt(uint iProp, out PropertyKey pkey);
            void GetValue(ref PropertyKey key, out PropVariant pv);
            void SetValue(ref PropertyKey key, ref PropVariant pv);
            void Commit();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey { public Guid fmtid; public int pid; }

        [StructLayout(LayoutKind.Explicit)]
        private struct PropVariant
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public IntPtr pointerValue;
        }
    }
}

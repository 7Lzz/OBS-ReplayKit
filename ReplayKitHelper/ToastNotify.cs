using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ReplayKitHelper
{
    // real windows notifications (persist in the action center, carry an app name + icon) instead of a Shell_NotifyIcon balloon (transient on win11, generic (i) + exe name). identity is three cooperating pieces: a start-menu shortcut carrying an AppUserModelID (the documented way an unpackaged desktop app gets recognised -- this is what resolves the display name), an HKCU\...\AppUserModelId\<aumid> key with IconUri (what actually paints the tiny header icon -- the shortcut icon alone does not), and an appLogoOverride <image> baked into each toast (the big icon left of the text, the one thing that renders regardless). the toast itself is ToastNotificationManager, reached from net48 by CLR winrt projection (Type.GetType("... , ContentType=WindowsRuntime") + dynamic), no nuget/winmd. this is what medal / discord / obs itself do.
    internal static class ToastNotify
    {
        internal const string Aumid = "OBS.ReplayKit.Clips";
        private const string DisplayName = "OBS ReplayKit";
        private const string ShortcutName = "OBS ReplayKit.lnk";
        private static readonly string AppLogoPngPath = Path.Combine(Constants.REPLAYKIT_TEMP_ROOT, "toast-icon.png");

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string AppID);

        // pin the AUMID on this process, (re)write the start-menu shortcut, render the appLogo png, and write the HKCU AppUserModelId key. safe to call every start -- all four writes are cheap and idempotent-ish.
        internal static void EnsureRegistered(string iconPath)
        {
            try { SetCurrentProcessExplicitAppUserModelID(Aumid); } catch (Exception ex) { Log.Write("ToastNotify: SetAUMID: " + ex.Message, "upload"); }

            string logoPng = EnsureAppLogoPng(iconPath);

            try
            {
                string dir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                if (string.IsNullOrEmpty(dir)) return;
                string lnkPath = Path.Combine(dir, ShortcutName);
                string target = Constants.ResolveObsExe();
                if (string.IsNullOrEmpty(target) || !File.Exists(target)) target = Process_MainModulePath();
                if (string.IsNullOrEmpty(target)) return;

                var link = (IShellLinkW)new CShellLink();
                link.SetPath(target);
                link.SetArguments("");
                link.SetWorkingDirectory(Path.GetDirectoryName(target) ?? "");
                if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath)) link.SetIconLocation(iconPath, 0);
                else link.SetIconLocation(target, 0);

                var store = (IPropertyStore)link;
                var key = PKEY_AppUserModel_ID;
                var pv = new PropVariant { vt = 31 /* VT_LPWSTR */, pointerValue = Marshal.StringToCoTaskMemUni(Aumid) };
                store.SetValue(ref key, ref pv);
                store.Commit();
                Marshal.FreeCoTaskMem(pv.pointerValue);

                ((IPersistFile)link).Save(lnkPath, true);
            }
            catch (Exception ex) { Log.Write("ToastNotify: shortcut: " + ex.Message, "upload"); }

            // the header attribution icon on an unpackaged toast comes from IconUri here, not the shortcut icon. DisplayName matches the .lnk name so it is a no-op for the name, just belt-and-suspenders.
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + Aumid))
                {
                    if (k != null)
                    {
                        k.SetValue("DisplayName", DisplayName, RegistryValueKind.String);
                        if (!string.IsNullOrEmpty(logoPng) && File.Exists(logoPng)) k.SetValue("IconUri", logoPng, RegistryValueKind.String);
                        else k.DeleteValue("IconUri", false);
                    }
                }
            }
            catch (Exception ex) { Log.Write("ToastNotify: aumid key: " + ex.Message, "upload"); }
        }

        // render iconPath (a multi-res .ico, or any GDI+ image) to a 256px png for the toast appLogoOverride + the HKCU IconUri -- neither reliably accepts an .ico. rebuilt only when the source is newer. returns the png path, or null on any failure (caller then skips the image / IconUri).
        private static string EnsureAppLogoPng(string iconPath)
        {
            try
            {
                if (string.IsNullOrEmpty(iconPath) || !File.Exists(iconPath)) return null;
                Directory.CreateDirectory(Path.GetDirectoryName(AppLogoPngPath));
                if (File.Exists(AppLogoPngPath) && File.GetLastWriteTimeUtc(AppLogoPngPath) >= File.GetLastWriteTimeUtc(iconPath))
                    return AppLogoPngPath;

                using (var src = LoadIconImage(iconPath))
                {
                    if (src == null) return null;
                    using (var bmp = new Bitmap(256, 256, PixelFormat.Format32bppArgb))
                    {
                        using (var g = Graphics.FromImage(bmp))
                        {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            g.Clear(Color.Transparent);
                            g.DrawImage(src, new Rectangle(0, 0, 256, 256));
                        }
                        string tmp = AppLogoPngPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        bmp.Save(tmp, ImageFormat.Png);
                        Native.MoveFileReplace(tmp, AppLogoPngPath);
                    }
                }
                return AppLogoPngPath;
            }
            catch (Exception ex) { Log.Write("ToastNotify: logo png: " + ex.Message, "upload"); return null; }
        }

        // largest frame of an .ico, else the image itself. caller owns the returned image.
        private static Image LoadIconImage(string path)
        {
            if (path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
            {
                using (var ico = new Icon(path, 256, 256)) return ico.ToBitmap();
            }
            using (var img = Image.FromFile(path)) return new Bitmap(img);
        }

        private static string Process_MainModulePath()
        {
            try { return System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName; }
            catch { return null; }
        }

        // fire a persistent toast. iconPath (a .ico) becomes the appLogoOverride image left of the text. body may contain \n for a line break. returns true if it reached the notifier.
        internal static bool Show(string title, string body, string iconPath = null)
        {
            try
            {
                try { SetCurrentProcessExplicitAppUserModelID(Aumid); } catch { }

                string logo = EnsureAppLogoPng(iconPath);
                string logoNode = (!string.IsNullOrEmpty(logo) && File.Exists(logo))
                    ? "<image placement=\"appLogoOverride\" src=\"" + Esc(new Uri(logo).AbsoluteUri) + "\"/>"
                    : "";
                string xml =
                    "<toast><visual><binding template=\"ToastGeneric\">" +
                    logoNode +
                    "<text>" + Esc(title) + "</text>" +
                    "<text>" + Esc(body) + "</text>" +
                    "</binding></visual></toast>";

                Type xmlDocType = Type.GetType("Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom, ContentType=WindowsRuntime");
                Type mgrType = Type.GetType("Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType=WindowsRuntime");
                Type toastType = Type.GetType("Windows.UI.Notifications.ToastNotification, Windows.UI.Notifications, ContentType=WindowsRuntime");
                if (xmlDocType == null || mgrType == null || toastType == null) { Log.Write("ToastNotify: winrt types unavailable", "upload"); return false; }

                dynamic xmlDoc = Activator.CreateInstance(xmlDocType);
                xmlDoc.LoadXml(xml);
                dynamic notifier = mgrType.GetMethod("CreateToastNotifier", new[] { typeof(string) }).Invoke(null, new object[] { Aumid });
                dynamic toast = Activator.CreateInstance(toastType, new object[] { xmlDoc });
                notifier.Show(toast);
                return true;
            }
            catch (Exception ex) { Log.Write("ToastNotify.Show: " + ex.Message, "upload"); return false; }
        }

        private static string Esc(string s) => (s ?? "")
            .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

        // -- COM interop for the shortcut --

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

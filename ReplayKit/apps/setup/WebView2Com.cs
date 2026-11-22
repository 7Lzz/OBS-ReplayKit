using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace ReplayKitSetup
{
    // the part of webview2 the setup and uninstall windows use, called straight thru com since the managed Microsoft.Web.WebView2.Core wrapper would be a second assembly beside the exe or merged into it; declared from WebView2.h of sdk 1.0.3537.50, the package WebView2Loader.dll comes from, and com calls go by vtable position, so each interface lists its slots in header order up to the last one used and the unused ones are placeholders that are never called
    public static class WebView2Com
    {
        private const string LoaderResource = "ReplayKitSetup.WebView2Loader.dll";

        [StructLayout(LayoutKind.Sequential)]
        public struct EventRegistrationToken { public long Value; }

        [StructLayout(LayoutKind.Sequential)]
        public struct NativeRect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct NativeColor { public byte A, R, G, B; }

        [ComImport, Guid("b96d755e-0319-4e92-a296-23436f46a1fc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface ICoreWebView2Environment
        {
            void CreateCoreWebView2Controller(IntPtr parentWindow, ICoreWebView2CreateCoreWebView2ControllerCompletedHandler handler);
        }

        [ComImport, Guid("4d00c0d1-9434-4eb6-8078-8697a560334f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface ICoreWebView2Controller
        {
            void Slot00GetIsVisible();
            void put_IsVisible(int isVisible);
            void Slot02GetBounds();
            void put_Bounds(NativeRect bounds);
            void Slot04(); void Slot05(); void Slot06(); void Slot07(); void Slot08(); void Slot09();
            void Slot10(); void Slot11(); void Slot12(); void Slot13(); void Slot14(); void Slot15();
            void Slot16(); void Slot17(); void Slot18(); void Slot19(); void Slot20();
            void Close();
            ICoreWebView2 get_CoreWebView2();
        }

        [ComImport, Guid("c979903e-d4ca-4228-92eb-47ee3fa96eab"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface ICoreWebView2Controller2
        {
            void Slot00(); void Slot01(); void Slot02(); void Slot03(); void Slot04(); void Slot05(); void Slot06(); void Slot07();
            void Slot08(); void Slot09(); void Slot10(); void Slot11(); void Slot12(); void Slot13(); void Slot14(); void Slot15();
            void Slot16(); void Slot17(); void Slot18(); void Slot19(); void Slot20(); void Slot21(); void Slot22();
            void Slot23GetDefaultBackgroundColor();
            void put_DefaultBackgroundColor(NativeColor value);
        }

        [ComImport, Guid("76eceacb-0462-4d94-ac83-423a6793775e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface ICoreWebView2
        {
            ICoreWebView2Settings get_Settings();
            void Slot01GetSource();
            void Slot02Navigate();
            void NavigateToString([MarshalAs(UnmanagedType.LPWStr)] string htmlContent);
            EventRegistrationToken add_NavigationStarting(ICoreWebView2NavigationStartingEventHandler eventHandler);
            void Slot05(); void Slot06(); void Slot07(); void Slot08(); void Slot09(); void Slot10(); void Slot11(); void Slot12(); void Slot13();
            EventRegistrationToken add_FrameNavigationStarting(ICoreWebView2NavigationStartingEventHandler eventHandler);
            void Slot15(); void Slot16(); void Slot17(); void Slot18(); void Slot19();
            EventRegistrationToken add_PermissionRequested(ICoreWebView2PermissionRequestedEventHandler eventHandler);
            void Slot21(); void Slot22(); void Slot23(); void Slot24(); void Slot25(); void Slot26(); void Slot27(); void Slot28();
            void PostWebMessageAsJson([MarshalAs(UnmanagedType.LPWStr)] string webMessageAsJson);
            void Slot30PostWebMessageAsString();
            EventRegistrationToken add_WebMessageReceived(ICoreWebView2WebMessageReceivedEventHandler handler);
            void Slot32(); void Slot33(); void Slot34(); void Slot35(); void Slot36(); void Slot37(); void Slot38(); void Slot39(); void Slot40();
            EventRegistrationToken add_NewWindowRequested(ICoreWebView2NewWindowRequestedEventHandler eventHandler);
        }

        [ComImport, Guid("e562e4f0-d7fa-43ac-8d71-c05150499f00"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface ICoreWebView2Settings
        {
            void Slot00(); void Slot01(); void Slot02(); void Slot03(); void Slot04(); void Slot05(); void Slot06();
            void put_IsStatusBarEnabled(int isStatusBarEnabled);
            void Slot08();
            void put_AreDevToolsEnabled(int areDevToolsEnabled);
            void Slot10();
            void put_AreDefaultContextMenusEnabled(int enabled);
            void Slot12(); void Slot13(); void Slot14();
            void put_IsZoomControlEnabled(int enabled);
        }

        [ComImport, Guid("fdb5ab74-af33-4854-84f0-0a631deb5eba"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface ICoreWebView2Settings3
        {
            void Slot00(); void Slot01(); void Slot02(); void Slot03(); void Slot04(); void Slot05(); void Slot06(); void Slot07(); void Slot08(); void Slot09();
            void Slot10(); void Slot11(); void Slot12(); void Slot13(); void Slot14(); void Slot15(); void Slot16(); void Slot17(); void Slot18(); void Slot19();
            void Slot20GetAreBrowserAcceleratorKeysEnabled();
            void put_AreBrowserAcceleratorKeysEnabled(int value);
        }

        [ComImport, Guid("5b495469-e119-438a-9b18-7604f25f2e49"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface ICoreWebView2NavigationStartingEventArgs
        {
            [return: MarshalAs(UnmanagedType.LPWStr)] string get_Uri();
            void Slot01(); void Slot02(); void Slot03(); void Slot04();
            void put_Cancel(int cancel);
        }

        [ComImport, Guid("34acb11c-fc37-4418-9132-f9c21d1eafb9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface ICoreWebView2NewWindowRequestedEventArgs
        {
            void Slot00(); void Slot01(); void Slot02();
            void put_Handled(int handled);
        }

        [ComImport, Guid("973ae2ef-ff18-4894-8fb2-3c758f046810"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface ICoreWebView2PermissionRequestedEventArgs
        {
            void Slot00(); void Slot01(); void Slot02(); void Slot03();
            void put_State(int state);
        }

        [ComImport, Guid("0f99a40c-e962-4207-9e92-e3d542eff849"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface ICoreWebView2WebMessageReceivedEventArgs
        {
            [return: MarshalAs(UnmanagedType.LPWStr)] string get_Source();
            [return: MarshalAs(UnmanagedType.LPWStr)] string get_WebMessageAsJson();
            [return: MarshalAs(UnmanagedType.LPWStr)] string TryGetWebMessageAsString();
        }

        [ComImport, Guid("4e8a3389-c9d8-4bd2-b6b5-124fee6cc14d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler
        {
            void Invoke(int errorCode, ICoreWebView2Environment result);
        }

        [ComImport, Guid("6c4819f3-c9b7-4260-8127-c9f5bde7f68c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface ICoreWebView2CreateCoreWebView2ControllerCompletedHandler
        {
            void Invoke(int errorCode, ICoreWebView2Controller result);
        }

        [ComImport, Guid("9adbe429-f36d-432b-9ddc-f8881fbd76e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface ICoreWebView2NavigationStartingEventHandler
        {
            void Invoke(ICoreWebView2 sender, ICoreWebView2NavigationStartingEventArgs args);
        }

        [ComImport, Guid("d4c185fe-c81c-4989-97af-2d3fa7ab5651"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface ICoreWebView2NewWindowRequestedEventHandler
        {
            void Invoke(ICoreWebView2 sender, ICoreWebView2NewWindowRequestedEventArgs args);
        }

        [ComImport, Guid("15e1c6a3-c72a-4df3-91d7-d097fbec6bfd"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface ICoreWebView2PermissionRequestedEventHandler
        {
            void Invoke(ICoreWebView2 sender, ICoreWebView2PermissionRequestedEventArgs args);
        }

        [ComImport, Guid("57213f19-00e6-49fa-8e07-898ea01ecbd2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface ICoreWebView2WebMessageReceivedEventHandler
        {
            void Invoke(ICoreWebView2 sender, ICoreWebView2WebMessageReceivedEventArgs args);
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        public sealed class EnvironmentCompletedHandler : ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler
        {
            private readonly Action<int, ICoreWebView2Environment> _done;
            public EnvironmentCompletedHandler(Action<int, ICoreWebView2Environment> done) { _done = done; }
            public void Invoke(int errorCode, ICoreWebView2Environment result) => _done(errorCode, result);
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        public sealed class ControllerCompletedHandler : ICoreWebView2CreateCoreWebView2ControllerCompletedHandler
        {
            private readonly Action<int, ICoreWebView2Controller> _done;
            public ControllerCompletedHandler(Action<int, ICoreWebView2Controller> done) { _done = done; }
            public void Invoke(int errorCode, ICoreWebView2Controller result) => _done(errorCode, result);
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        public sealed class NavigationStartingHandler : ICoreWebView2NavigationStartingEventHandler
        {
            private readonly Action<ICoreWebView2NavigationStartingEventArgs> _on;
            public NavigationStartingHandler(Action<ICoreWebView2NavigationStartingEventArgs> on) { _on = on; }
            public void Invoke(ICoreWebView2 sender, ICoreWebView2NavigationStartingEventArgs args) => _on(args);
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        public sealed class NewWindowRequestedHandler : ICoreWebView2NewWindowRequestedEventHandler
        {
            private readonly Action<ICoreWebView2NewWindowRequestedEventArgs> _on;
            public NewWindowRequestedHandler(Action<ICoreWebView2NewWindowRequestedEventArgs> on) { _on = on; }
            public void Invoke(ICoreWebView2 sender, ICoreWebView2NewWindowRequestedEventArgs args) => _on(args);
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        public sealed class PermissionRequestedHandler : ICoreWebView2PermissionRequestedEventHandler
        {
            private readonly Action<ICoreWebView2PermissionRequestedEventArgs> _on;
            public PermissionRequestedHandler(Action<ICoreWebView2PermissionRequestedEventArgs> on) { _on = on; }
            public void Invoke(ICoreWebView2 sender, ICoreWebView2PermissionRequestedEventArgs args) => _on(args);
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        public sealed class WebMessageReceivedHandler : ICoreWebView2WebMessageReceivedEventHandler
        {
            private readonly Action<ICoreWebView2WebMessageReceivedEventArgs> _on;
            public WebMessageReceivedHandler(Action<ICoreWebView2WebMessageReceivedEventArgs> on) { _on = on; }
            public void Invoke(ICoreWebView2 sender, ICoreWebView2WebMessageReceivedEventArgs args) => _on(args);
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private delegate int CreateEnvironmentWithOptions(string browserExecutableFolder, string userDataFolder, IntPtr environmentOptions, ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler environmentCreatedHandler);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private delegate int GetBrowserVersionString(string browserExecutableFolder, out IntPtr versionInfo);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern IntPtr LoadLibraryW(string path);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string procName);

        private static readonly object LoaderGate = new object();
        private static CreateEnvironmentWithOptions createEnvironment;
        private static GetBrowserVersionString getBrowserVersion;

        // no options, which leaves every choice to the loader defaults of this sdk the way an empty CoreWebView2EnvironmentOptions did
        internal static int CreateEnvironment(string userDataFolder, ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler handler)
        {
            EnsureLoader();
            return createEnvironment(null, userDataFolder, IntPtr.Zero, handler);
        }

        // null when no runtime is installed
        internal static string AvailableBrowserVersion()
        {
            EnsureLoader();
            int hr = getBrowserVersion(null, out IntPtr version);
            try
            {
                Marshal.ThrowExceptionForHR(hr);
                return version == IntPtr.Zero ? null : Marshal.PtrToStringUni(version);
            }
            finally
            {
                if (version != IntPtr.Zero) Marshal.FreeCoTaskMem(version);
            }
        }

        // the loader is a resource of this exe; it is written to a folder named by its sha256 and loaded from there, read thru a handle that keeps it from being changed between the hash check and the load, and its two functions are taken from that loaded copy rather than looked up by name, which the setup limits to system32
        private static void EnsureLoader()
        {
            lock (LoaderGate)
            {
                if (createEnvironment != null) return;
                byte[] bytes;
                using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(LoaderResource))
                {
                    if (resource == null) throw new FileNotFoundException("WebView2Loader.dll is missing from the setup build.");
                    using var copy = new MemoryStream();
                    resource.CopyTo(copy);
                    bytes = copy.ToArray();
                }
                string hash = Sha256Hex(bytes);
                string folder = Path.Combine(ReplayKitShared.TempLayout.Root, "setup-webview2-loader", hash.Substring(0, 16));
                string path = Path.Combine(folder, "WebView2Loader.dll");
                Directory.CreateDirectory(folder);
                if (!File.Exists(path) || Sha256Hex(File.ReadAllBytes(path)) != hash)
                {
                    string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        File.WriteAllBytes(temporary, bytes);
                        if (File.Exists(path)) File.Delete(path);
                        File.Move(temporary, path);
                    }
                    finally
                    {
                        if (File.Exists(temporary)) File.Delete(temporary);
                    }
                }
                using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    using var read = new MemoryStream();
                    held.CopyTo(read);
                    if (Sha256Hex(read.ToArray()) != hash) throw new InvalidDataException("The unpacked WebView2Loader.dll does not match the one in the setup.");
                    IntPtr module = LoadLibraryW(path);
                    if (module == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                    getBrowserVersion = Export<GetBrowserVersionString>(module, "GetAvailableCoreWebView2BrowserVersionString");
                    createEnvironment = Export<CreateEnvironmentWithOptions>(module, "CreateCoreWebView2EnvironmentWithOptions");
                }
            }
        }

        private static T Export<T>(IntPtr module, string name) where T : class
        {
            IntPtr address = GetProcAddress(module, name);
            if (address == IntPtr.Zero) throw new EntryPointNotFoundException("WebView2Loader.dll has no " + name + ".");
            return Marshal.GetDelegateForFunctionPointer<T>(address);
        }

        private static string Sha256Hex(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
        }
    }

    internal enum CoreWebView2PermissionState
    {
        Default = 0,
        Allow = 1,
        Deny = 2,
    }

    internal sealed class CoreWebView2Environment
    {
        private readonly WebView2Com.ICoreWebView2Environment _raw;

        private CoreWebView2Environment(WebView2Com.ICoreWebView2Environment raw) { _raw = raw; }

        public static string GetAvailableBrowserVersionString() => WebView2Com.AvailableBrowserVersion();

        public static Task<CoreWebView2Environment> CreateAsync(string userDataFolder)
        {
            var done = new TaskCompletionSource<CoreWebView2Environment>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new WebView2Com.EnvironmentCompletedHandler((hr, env) =>
            {
                if (hr < 0) done.TrySetException(Marshal.GetExceptionForHR(hr));
                else if (env == null) done.TrySetException(new InvalidOperationException("WebView2 created no environment."));
                else done.TrySetResult(new CoreWebView2Environment(env));
            });
            int rc = WebView2Com.CreateEnvironment(userDataFolder, handler);
            if (rc < 0) done.TrySetException(Marshal.GetExceptionForHR(rc));
            return done.Task;
        }

        public Task<CoreWebView2Controller> CreateCoreWebView2ControllerAsync(IntPtr parentWindow)
        {
            var done = new TaskCompletionSource<CoreWebView2Controller>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new WebView2Com.ControllerCompletedHandler((hr, controller) =>
            {
                if (hr < 0) done.TrySetException(Marshal.GetExceptionForHR(hr));
                else if (controller == null) done.TrySetException(new InvalidOperationException("WebView2 created no controller."));
                else done.TrySetResult(new CoreWebView2Controller(controller));
            });
            _raw.CreateCoreWebView2Controller(parentWindow, handler);
            return done.Task;
        }
    }

    internal sealed class CoreWebView2Controller
    {
        private readonly WebView2Com.ICoreWebView2Controller _raw;

        internal CoreWebView2Controller(WebView2Com.ICoreWebView2Controller raw)
        {
            _raw = raw;
            CoreWebView2 = new CoreWebView2(raw.get_CoreWebView2());
        }

        public CoreWebView2 CoreWebView2 { get; }

        public bool IsVisible { set => _raw.put_IsVisible(value ? 1 : 0); }

        public Rectangle Bounds { set => _raw.put_Bounds(new WebView2Com.NativeRect { Left = value.Left, Top = value.Top, Right = value.Right, Bottom = value.Bottom }); }

        public Color DefaultBackgroundColor { set => ((WebView2Com.ICoreWebView2Controller2)_raw).put_DefaultBackgroundColor(new WebView2Com.NativeColor { A = value.A, R = value.R, G = value.G, B = value.B }); }

        public void Close() => _raw.Close();
    }

    internal sealed class CoreWebView2Settings
    {
        private readonly WebView2Com.ICoreWebView2Settings _raw;

        internal CoreWebView2Settings(WebView2Com.ICoreWebView2Settings raw) { _raw = raw; }

        public bool IsStatusBarEnabled { set => _raw.put_IsStatusBarEnabled(value ? 1 : 0); }
        public bool AreDevToolsEnabled { set => _raw.put_AreDevToolsEnabled(value ? 1 : 0); }
        public bool AreDefaultContextMenusEnabled { set => _raw.put_AreDefaultContextMenusEnabled(value ? 1 : 0); }
        public bool IsZoomControlEnabled { set => _raw.put_IsZoomControlEnabled(value ? 1 : 0); }
        public bool AreBrowserAcceleratorKeysEnabled { set => ((WebView2Com.ICoreWebView2Settings3)_raw).put_AreBrowserAcceleratorKeysEnabled(value ? 1 : 0); }
    }

    // the events only ever gain handlers here, so taking one off is refused rather than silently ignored
    internal sealed class CoreWebView2
    {
        private readonly WebView2Com.ICoreWebView2 _raw;

        internal CoreWebView2(WebView2Com.ICoreWebView2 raw)
        {
            _raw = raw;
            Settings = new CoreWebView2Settings(raw.get_Settings());
        }

        public CoreWebView2Settings Settings { get; }

        public void NavigateToString(string htmlContent) => _raw.NavigateToString(htmlContent);

        public void PostWebMessageAsJson(string webMessageAsJson) => _raw.PostWebMessageAsJson(webMessageAsJson);

        public event EventHandler<CoreWebView2NavigationStartingEventArgs> NavigationStarting
        {
            add => _raw.add_NavigationStarting(new WebView2Com.NavigationStartingHandler(a => value(this, new CoreWebView2NavigationStartingEventArgs(a))));
            remove => throw new NotSupportedException();
        }

        public event EventHandler<CoreWebView2NavigationStartingEventArgs> FrameNavigationStarting
        {
            add => _raw.add_FrameNavigationStarting(new WebView2Com.NavigationStartingHandler(a => value(this, new CoreWebView2NavigationStartingEventArgs(a))));
            remove => throw new NotSupportedException();
        }

        public event EventHandler<CoreWebView2NewWindowRequestedEventArgs> NewWindowRequested
        {
            add => _raw.add_NewWindowRequested(new WebView2Com.NewWindowRequestedHandler(a => value(this, new CoreWebView2NewWindowRequestedEventArgs(a))));
            remove => throw new NotSupportedException();
        }

        public event EventHandler<CoreWebView2PermissionRequestedEventArgs> PermissionRequested
        {
            add => _raw.add_PermissionRequested(new WebView2Com.PermissionRequestedHandler(a => value(this, new CoreWebView2PermissionRequestedEventArgs(a))));
            remove => throw new NotSupportedException();
        }

        public event EventHandler<CoreWebView2WebMessageReceivedEventArgs> WebMessageReceived
        {
            add => _raw.add_WebMessageReceived(new WebView2Com.WebMessageReceivedHandler(a => value(this, new CoreWebView2WebMessageReceivedEventArgs(a))));
            remove => throw new NotSupportedException();
        }
    }

    internal sealed class CoreWebView2NavigationStartingEventArgs : EventArgs
    {
        private readonly WebView2Com.ICoreWebView2NavigationStartingEventArgs _raw;
        internal CoreWebView2NavigationStartingEventArgs(WebView2Com.ICoreWebView2NavigationStartingEventArgs raw) { _raw = raw; }
        public string Uri => _raw.get_Uri();
        public bool Cancel { set => _raw.put_Cancel(value ? 1 : 0); }
    }

    internal sealed class CoreWebView2NewWindowRequestedEventArgs : EventArgs
    {
        private readonly WebView2Com.ICoreWebView2NewWindowRequestedEventArgs _raw;
        internal CoreWebView2NewWindowRequestedEventArgs(WebView2Com.ICoreWebView2NewWindowRequestedEventArgs raw) { _raw = raw; }
        public bool Handled { set => _raw.put_Handled(value ? 1 : 0); }
    }

    internal sealed class CoreWebView2PermissionRequestedEventArgs : EventArgs
    {
        private readonly WebView2Com.ICoreWebView2PermissionRequestedEventArgs _raw;
        internal CoreWebView2PermissionRequestedEventArgs(WebView2Com.ICoreWebView2PermissionRequestedEventArgs raw) { _raw = raw; }
        public CoreWebView2PermissionState State { set => _raw.put_State((int)value); }
    }

    internal sealed class CoreWebView2WebMessageReceivedEventArgs : EventArgs
    {
        private readonly WebView2Com.ICoreWebView2WebMessageReceivedEventArgs _raw;
        internal CoreWebView2WebMessageReceivedEventArgs(WebView2Com.ICoreWebView2WebMessageReceivedEventArgs raw) { _raw = raw; }
        public string Source => _raw.get_Source();
        public string WebMessageAsJson => _raw.get_WebMessageAsJson();
        public string TryGetWebMessageAsString() => _raw.TryGetWebMessageAsString();
    }
}

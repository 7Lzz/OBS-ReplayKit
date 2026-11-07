using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CSharp.RuntimeBinder;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // the gate every helper request passes (host and origin) and the shared helpers of the route classes, which hold the routes themselves, one feature each
    internal static class Routes
    {
        internal static readonly string[] AppIconExtensions = { ".ico", ".png", ".jpg", ".jpeg", ".bmp" };
        internal static readonly object RenameGate = new object();

        // "40-a1b2c3d4.ico" -> "40" -- drop the source-hash suffix ImportCustomIcon appends so the grid shows a clean label.
        internal static string UserIconLabel(string fileName) =>
            Regex.Replace(Path.GetFileNameWithoutExtension(fileName), "-[0-9a-f]{8}$", "");

        // the colours the dock draws a theme tile from.
        internal static JObject ThemeSwatch(Themes.Tokens t) => new JObject
        {
            ["bg"] = t.Bg, ["panel"] = t.Panel, ["accent"] = t.Accent, ["text"] = t.Text,
            ["background"] = Themes.BackgroundCss(t),
        };

        private sealed class OwnerWindow : System.Windows.Forms.IWin32Window
        {
            public OwnerWindow(IntPtr h) { Handle = h; }
            public IntPtr Handle { get; }
        }

        internal const string FolderPickerTitle = "Choose the recordings folder";
        internal const string FilePickerTitle = "Choose an icon image";

        // blocking modal picker on a one-shot STA thread (winforms dialogs need STA + a pump). parents to the ReplayKit Settings popup if its open. "" on cancel/error.
        internal static string PickPath(bool folder, string filter, string initial)
        {
            string result = "";
            var t = new Thread(() =>
            {
                try
                {
                    IntPtr ownerHwnd = Native.FindReplayKitSettingsWindow();
                    // show() below blocks this thread while the dialog is up, so bring it to the front from a watcher -- the helper is a separate process and the dialog otherwise opens behind OBS.
                    var raiser = new Thread(() => Native.RaiseOwnDialog(folder ? FolderPickerTitle : FilePickerTitle))
                    { IsBackground = true, Name = "picker-raise" };
                    raiser.Start();

                    if (folder)
                    {
                        result = PickFolderModern(ownerHwnd, initial);
                    }
                    else
                    {
                        System.Windows.Forms.IWin32Window owner = ownerHwnd == IntPtr.Zero ? null : new OwnerWindow(ownerHwnd);
                        using (var d = new System.Windows.Forms.OpenFileDialog { Filter = filter, CheckFileExists = true, Title = FilePickerTitle })
                        {
                            if (!string.IsNullOrEmpty(initial) && File.Exists(initial)) d.FileName = initial;
                            if (d.ShowDialog(owner) == System.Windows.Forms.DialogResult.OK) result = d.FileName;
                        }
                    }
                }
                catch (Exception ex) { Log.Write("PickPath: " + ex.Message); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            t.Join(180000);
            return result;
        }

        internal const string AudioPickerTitle = "Import audio";
        // the containers of video are listed too, since the sound of a video file can be taken out and used
        internal const string AudioFilter = "Audio files|*.mp3;*.wav;*.ogg;*.oga;*.opus;*.flac;*.m4a;*.aac;*.wma;*.aif;*.aiff;*.mka;*.mp2;*.ac3;*.amr;*.au;*.caf;*.webm;*.mp4;*.mov;*.mkv|All files (*.*)|*.*";

        internal const string MediaPickerTitle = "Add a GIF or image";
        internal const string MediaFilter = "GIFs and images|*.gif;*.png;*.jpg;*.jpeg|All files (*.*)|*.*";

        internal const string VideoPickerTitle = "Add a video";
        internal const string VideoFilter = "Videos|*.mp4;*.m4v;*.mov;*.mkv;*.webm|All files (*.*)|*.*";

        internal static string[] PickAudioFiles() => PickFiles(AudioPickerTitle, AudioFilter);

        // the picker of the editor: a modal dialog on a one-shot sta thread like PickPath, where several files can be chosen at once; an empty list when it is cancelled or fails
        internal static string[] PickFiles(string title, string filter)
        {
            string[] result = new string[0];
            var t = new Thread(() =>
            {
                try
                {
                    IntPtr ownerHwnd = Native.FindReplayKitSettingsWindow();
                    var raiser = new Thread(() => Native.RaiseOwnDialog(title)) { IsBackground = true, Name = "picker-raise" };
                    raiser.Start();
                    System.Windows.Forms.IWin32Window owner = ownerHwnd == IntPtr.Zero ? null : new OwnerWindow(ownerHwnd);
                    using (var d = new System.Windows.Forms.OpenFileDialog { Filter = filter, CheckFileExists = true, Multiselect = true, Title = title })
                    {
                        if (d.ShowDialog(owner) == System.Windows.Forms.DialogResult.OK) result = d.FileNames;
                    }
                }
                catch (Exception ex) { Log.Write("PickFiles: " + ex.Message); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            t.Join(180000);
            return result;
        }

        // modern folder picker -- IFileOpenDialog with FOS_PICKFOLDERS, the same Explorer-style chrome as the icon file picker. the net48 WinForms FolderBrowserDialog is the legacy tree-view control. STA-only; the caller already runs this on a one-shot STA thread. "" on cancel or any failure.
        internal static string PickFolderModern(IntPtr owner, string initial)
        {
            IFileOpenDialog dlg = null;
            try
            {
                dlg = (IFileOpenDialog)new FileOpenDialogClass();
                dlg.GetOptions(out uint opts);
                dlg.SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST | FOS_NOCHANGEDIR);
                dlg.SetTitle(FolderPickerTitle);
                if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial) &&
                    SHCreateItemFromParsingName(initial, IntPtr.Zero, typeof(IShellItem).GUID, out IShellItem seed) == 0 && seed != null)
                {
                    try { dlg.SetFolder(seed); } finally { Marshal.ReleaseComObject(seed); }
                }
                if (dlg.Show(owner) != 0) return ""; // ERROR_CANCELLED or any hr -- treat as cancel
                dlg.GetResult(out IShellItem item);
                try
                {
                    item.GetDisplayName(SIGDN_FILESYSPATH, out IntPtr pth);
                    string s = Marshal.PtrToStringUni(pth);
                    Marshal.FreeCoTaskMem(pth);
                    return s ?? "";
                }
                finally { Marshal.ReleaseComObject(item); }
            }
            catch (Exception ex) { Log.Write("PickFolderModern: " + ex.Message); return ""; }
            finally { if (dlg != null) Marshal.ReleaseComObject(dlg); }
        }

        internal const uint FOS_PICKFOLDERS = 0x20;
        internal const uint FOS_FORCEFILESYSTEM = 0x40;
        internal const uint FOS_NOCHANGEDIR = 0x8;
        internal const uint FOS_PATHMUSTEXIST = 0x800;
        internal const uint SIGDN_FILESYSPATH = 0x80058000;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem ppv);

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        private class FileOpenDialogClass { }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
            void AddPlace(IShellItem psi, int fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);
            void GetResults(out IntPtr ppenum);
            void GetSelectedItems(out IntPtr ppsai);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, out IntPtr ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }

        internal static Process[] GetProcessesByNames(params string[] names)
        {
            var list = new List<Process>();
            int currentPid = Process.GetCurrentProcess().Id;
            foreach (var name in names)
            {
                try
                {
                    foreach (var process in Process.GetProcessesByName(name))
                    {
                        if (process.Id == currentPid) process.Dispose();
                        else list.Add(process);
                    }
                }
                catch (InvalidOperationException) { }
            }
            return list.ToArray();
        }

        internal static string GetRunningObsPath()
        {
            var procs = GetProcessesByNames("obs64", "obs32", "obs");
            foreach (var p in procs)
            {
                try { if (!string.IsNullOrEmpty(p.MainModule?.FileName) && !Constants.IsRoleHost(p.MainModule.FileName)) return p.MainModule.FileName; }
                catch (Win32Exception) { } catch (InvalidOperationException) { }
            }
            foreach (var p in procs)
            {
                try
                {
                    using (var searcher = new ManagementObjectSearcher("SELECT ExecutablePath FROM Win32_Process WHERE ProcessId=" + p.Id))
                    {
                        foreach (ManagementObject mo in searcher.Get())
                        {
                            string exePath = mo["ExecutablePath"] as string;
                            if (!string.IsNullOrEmpty(exePath)) return exePath;
                        }
                    }
                }
                catch (ManagementException) { }
            }
            return null;
        }

        // a plain exit leaves obs and the helper running when a normal close cannot finish; a restart someone pressed and an update have to happen (the installer cannot replace the files of a running obs either), so for those obs is made to stop once the normal close has had its time, and the armed relauncher brings it back
        internal static void StopObsForRestart(string reason, bool mustStop)
        {
            Thread.Sleep(300);
            try
            {
                if (!PipeClient.SendAllowCloseAndWait(1000))
                    Log.Write(reason + ": native plugin did not acknowledge ALLOWCLOSE.");
            }
            catch (Exception ex) { Log.Write(reason + ": close acknowledgement failed: " + ex.Message); }
            var procs = GetProcessesByNames("obs64", "obs32", "obs");
            bool stopped = true;
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            foreach (var p in procs)
            {
                try
                {
                    if (Constants.IsRoleHost(p)) continue;
                    Native.CloseObsMainWindow((uint)p.Id);
                    int remaining = (int)Math.Max(0, (deadline - DateTime.UtcNow).TotalMilliseconds);
                    if (!p.HasExited && !p.WaitForExit(remaining)) stopped = false;
                }
                catch (InvalidOperationException) { }
                catch (Win32Exception ex)
                {
                    stopped = false;
                    Log.Write(reason + ": could not confirm OBS exit: " + ex.Message);
                }
                finally { p.Dispose(); }
            }
            if (!stopped && mustStop)
            {
                Log.Write(reason + ": OBS did not close within 15 s, stopping it");
                foreach (var p in GetProcessesByNames("obs64", "obs32", "obs"))
                {
                    try { if (!Constants.IsRoleHost(p) && !p.HasExited) { p.Kill(); p.WaitForExit(5000); } }
                    catch (InvalidOperationException) { }
                    catch (Win32Exception ex) { Log.Write(reason + ": could not stop OBS: " + ex.Message); }
                    finally { p.Dispose(); }
                }
                Server.State.Shutdown = true;
                return;
            }
            if (!stopped)
            {
                Server.State.RestartAfterCleanObsPath = null;
                const string message = "OBS did not close safely. ReplayKit left it running instead of forcing it to stop. Close OBS normally before trying again.";
                Log.Write(reason + ": " + message);
                Notifications.Add("warning", "obs-close-timeout", "OBS could not close safely", message);
                return;
            }
            Server.State.Shutdown = true;
        }

        // spawn restart_obs.ps1 outside our kill-on-close job so it lives past helper exit. an in-process relaunch is unreliable -- by the time obs is dead and any cleanup completes, this process has often already been torn down (parent-process watchdog exit, abandoned-mutex on takeover, etc.) and never reaches the launch line. doing the relaunch from a sibling process owned by the os instead of by us is the only way to make it survive every shutdown path.
        internal static bool StartDetachedObsRelauncher(string obsPath, int obsPid)
        {
            string scriptDir = AppConfig.GetScriptDir();
            if (string.IsNullOrWhiteSpace(scriptDir)) return false;
            string scriptPath = Path.Combine(scriptDir, "restart_obs.ps1");
            if (!File.Exists(scriptPath))
            {
                Log.Write("Start-DetachedObsRelauncher: missing " + scriptPath);
                return false;
            }
            string psExe = Path.Combine(Environment.GetEnvironmentVariable("WINDIR") ?? "", "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(psExe)) psExe = "powershell.exe";
            string cmd = ProcessArgs.Quote(psExe) +
                " -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File " + ProcessArgs.Quote(scriptPath) +
                " -ObsPath " + ProcessArgs.Quote(obsPath) +
                " -ObsPid " + obsPid;
            try
            {
                int relauncherPid = Native.SpawnDetached(cmd, scriptDir);
                if (relauncherPid <= 0)
                {
                    Log.Write("Start-DetachedObsRelauncher: SpawnDetached returned 0");
                    return false;
                }
                Log.Write("Start-DetachedObsRelauncher: PID " + relauncherPid + " will relaunch '" + obsPath + "' after this OBS exits.");
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Start-DetachedObsRelauncher threw: " + ex.Message);
                return false;
            }
        }

        // arms the detached relauncher (or the in-process path) before requesting a normal obs close. shared by /settings' restart-required branch, /restart-obs, and /restart-obs-clean.
        internal static void ArmRelaunchAndCloseObs(string obsPath, string routeName)
        {
            int obsTargetPid = 0;
            if (ParentWatchdog.ParentPid > 0)
            {
                obsTargetPid = ParentWatchdog.ParentPid;
            }
            else
            {
                var found = GetProcessesByNames("obs64", "obs32", "obs").Where(p => !Constants.IsRoleHost(p)).ToArray();
                if (found.Length > 0) obsTargetPid = found[0].Id;
            }
            bool detached = StartDetachedObsRelauncher(obsPath, obsTargetPid);
            if (!detached)
            {
                Server.State.RestartAfterCleanObsPath = obsPath;
                Log.Write(routeName + ": detached relauncher unavailable; queued legacy in-process restart for '" + obsPath + "'.");
            }
            else
            {
                Log.Write(routeName + ": detached relauncher armed for '" + obsPath + "' (target PID " + obsTargetPid + ").");
            }
        }

        // the body of a failed action; a problem with the setup file also carries its code, which the settings page keys on
        internal static JObject FailureJson(Exception ex)
        {
            var body = new JObject { ["ok"] = false, ["message"] = ex.Message };
            if (ex is SetupExeException setup) body["code"] = setup.Code;
            return body;
        }

        internal static bool TestSettingsOrigin(HttpRequest req)
        {
            if (req.Headers.TryGetValue("sec-fetch-site", out string site) &&
                !string.Equals(site, "same-origin", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(site, "same-site", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(site, "none", StringComparison.OrdinalIgnoreCase)) return false;
            if (!req.Headers.TryGetValue("origin", out string origin))
            {
                if (!req.Headers.TryGetValue("referer", out string referer)) return true;
                return Uri.TryCreate(referer, UriKind.Absolute, out var uri) && TrustedOrigin(uri.GetLeftPart(UriPartial.Authority));
            }
            return TrustedOrigin(origin);
        }

        internal static bool TrustedOrigin(string origin)
        {
            int port = ReplayKitShared.HelperEndpoint.Port;
            return string.Equals(origin, "http://" + Constants.HOST_ADDR + ":" + port, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(origin, "http://localhost:" + port, StringComparison.OrdinalIgnoreCase);
        }

        // normalize explorer paths before comparing them. file explorer may report the active tab thru locationurl or thru document.folder, depending on windows version and tab state.
        internal static string ConvertToExplorerComparePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return null; }
        }

        internal static string GetExplorerWindowPath(dynamic window)
        {
            try
            {
                string loc = (string)window.LocationURL;
                if (!string.IsNullOrEmpty(loc) && loc.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                    return new Uri(loc).LocalPath;
            }
            catch (COMException) { } catch (RuntimeBinderException) { }
            try
            {
                string path = (string)window.Document.Folder.Self.Path;
                if (!string.IsNullOrWhiteSpace(path)) return path;
            }
            catch (COMException) { } catch (RuntimeBinderException) { }
            return null;
        }

        // find an already-open file explorer window thats showing targetPath. returns the windows hwnd or IntPtr.Zero if no match. used by /open-clip-folder, /open-log-folder and /open-folder to raise an existing explorer window for the clip folder instead of spawning yet another explorer.exe -- windows otherwise stacks duplicate folder windows every time the user clicks the docks open folder button. uses Shell.Applications Windows() collection (the same one explorer itself uses); LocationURL on each item is a file:// url we can convert to a local path and compare case-insensitively to the target. ie/edge browser windows show up here too -- we skip anything whose LocationURL isnt file://.
        // shows a file in explorer: a window that already has its folder open is raised, else the folder opens with the file selected. we deliberately dont try to re-select the file in a window that is already open -- the com dance is fragile and the user is usually just trying to surface the folder. true when an open window was raised
        internal static bool RevealInExplorer(string full)
        {
            string parent = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(parent))
            {
                var existing = GetExplorerHwndForPath(parent);
                if (existing != IntPtr.Zero) return Native.FocusHwnd(existing);
            }
            // explorer.exe /select,"<path>" opens the containing folder with the file already highlighted. arguments must be one string becuase explorer parses /select,<rest> as a unit.
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = "/select," + ProcessArgs.Quote(full), UseShellExecute = false })?.Dispose();
            return false;
        }

        public static IntPtr GetExplorerHwndForPath(string targetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath)) return IntPtr.Zero;
            string full = ConvertToExplorerComparePath(targetPath);
            if (full == null) return IntPtr.Zero;

            dynamic shell = null;
            dynamic windows = null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                shell = Activator.CreateInstance(shellType);
                windows = shell.Windows();
                foreach (dynamic w in windows)
                {
                    try
                    {
                        string here = ConvertToExplorerComparePath(GetExplorerWindowPath(w));
                        if (string.Equals(here, full, StringComparison.OrdinalIgnoreCase))
                        {
                            long raw = (long)w.HWND;
                            if (raw != 0) return new IntPtr(raw);
                        }
                    }
                    catch (COMException) { } catch (RuntimeBinderException) { }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Get-ExplorerHwndForPath failed: " + ex.Message);
            }
            finally
            {
                if (windows != null) { try { Marshal.ReleaseComObject(windows); } catch (ArgumentException) { } }
                if (shell != null) { try { Marshal.ReleaseComObject(shell); } catch (ArgumentException) { } }
            }
            return IntPtr.Zero;
        }

        internal static JObject TrimResultToJson(TrimResult r)
        {
            var obj = new JObject { ["ok"] = r.Ok };
            if (!r.Ok) { obj["message"] = r.Message; if (r.Cancelled) obj["cancelled"] = true; return obj; }
            if (r.Name != null) obj["name"] = r.Name;
            if (r.SourceName != null)
            {
                obj["sourceName"] = r.SourceName;
                obj["mode"] = r.Mode;
                obj["precise"] = r.Precise;
                obj["removeAudio"] = r.RemoveAudio;
                obj["startSec"] = r.StartSec;
                obj["endSec"] = r.EndSec;
                obj["durationSec"] = r.DurationSec;
            }
            return obj;
        }

        internal static JObject KeyframeResultToJson(KeyframeScanResult r)
        {
            var obj = new JObject { ["ok"] = r.Ok };
            if (r.Pending) obj["pending"] = true;
            obj["message"] = r.Message ?? "";
            obj["keyframes"] = new JArray(r.Keyframes);
            if (r.Ok)
            {
                obj["name"] = r.Name;
                obj["count"] = r.Count;
                obj["cached"] = r.Cached;
                obj["probeMs"] = r.ProbeMs;
            }
            if (r.RetryMs != 0) obj["retryMs"] = r.RetryMs;
            return obj;
        }

        internal static JObject ObsResultToJson(ObsWebSocketResult r)
        {
            var obj = new JObject { ["ok"] = r.Ok };
            if (r.Unavailable) obj["unavailable"] = true;
            if (r.RequestType != null) obj["requestType"] = r.RequestType;
            if (!r.Ok) { obj["message"] = r.Message; if (r.Code != 0) obj["code"] = r.Code; }
            else if (r.Data != null) obj["data"] = r.Data;
            return obj;
        }

        // primary-desktop jpeg for the overlay-preview backdrop (setup wizard + settings overlay page), which asks for a fresh one every second while it is on screen; a grab is shared for a little less than that, so pages that ask in the same moment get one capture between them. never written to disk, and the response is no-store so cef cannot replay an old image.
        internal static byte[] _desktopPreviewJpeg;
        internal static readonly System.Diagnostics.Stopwatch _desktopPreviewAge = new System.Diagnostics.Stopwatch();
        internal const int DesktopPreviewMaxAgeMs = 800;
        internal static readonly object _desktopPreviewGate = new object();

        // capture + cache the primary screen as a downscaled jpeg; returns null when there is no usable screen, and a near-black grab (secure desktop up) is returned but not cached so the next call retries.
        internal static byte[] GetDesktopPreviewJpeg()
        {
            lock (_desktopPreviewGate)
            {
                if (_desktopPreviewJpeg != null && _desktopPreviewAge.ElapsedMilliseconds < DesktopPreviewMaxAgeMs) return _desktopPreviewJpeg;
                var bounds = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? System.Drawing.Rectangle.Empty;
                if (bounds.Width <= 0 || bounds.Height <= 0) return null;
                const int targetW = 1280;
                double scale = Math.Min(1.0, (double)targetW / bounds.Width);
                int outW = Math.Max(1, (int)Math.Round(bounds.Width * scale));
                int outH = Math.Max(1, (int)Math.Round(bounds.Height * scale));
                using (var full = new System.Drawing.Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var g = System.Drawing.Graphics.FromImage(full))
                        g.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);
                    byte[] jpeg = EncodeScaledJpeg(full, outW, outH);
                    if (!IsNearlyBlack(full))
                    {
                        _desktopPreviewJpeg = jpeg;
                        _desktopPreviewAge.Restart();
                    }
                    return jpeg;
                }
            }
        }

        // scale src into a w x h 24bpp jpeg (quality 72), same encode path as Native.SaveThumbnail minus the letterbox.
        internal static byte[] EncodeScaledJpeg(System.Drawing.Bitmap src, int w, int h)
        {
            using (var canvas = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
            using (var g = System.Drawing.Graphics.FromImage(canvas))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(src, 0, 0, w, h);
                var jpegCodec = Array.Find(System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders(), c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
                var eps = new System.Drawing.Imaging.EncoderParameters(1);
                eps.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 72L);
                using (var ms = new MemoryStream())
                {
                    canvas.Save(ms, jpegCodec, eps);
                    return ms.ToArray();
                }
            }
        }

        // true when a coarse pixel grid is all within a few levels of black -- cheap guard against caching a secure-desktop capture for the whole session.
        internal static bool IsNearlyBlack(System.Drawing.Bitmap bmp)
        {
            int stepX = Math.Max(1, bmp.Width / 16), stepY = Math.Max(1, bmp.Height / 16);
            for (int y = 0; y < bmp.Height; y += stepY)
                for (int x = 0; x < bmp.Width; x += stepX)
                {
                    var p = bmp.GetPixel(x, y);
                    if (p.R > 8 || p.G > 8 || p.B > 8) return false;
                }
            return true;
        }

        public static bool DispatchRequest(Stream stream, HttpRequest req)
        {
            AppConfig.LoadConfig();

            int port = ReplayKitShared.HelperEndpoint.Port;
            // every route is gated here on host and origin, so no route checks them again
            if (!req.Headers.TryGetValue("host", out string host) ||
                !(string.Equals(host, Constants.HOST_ADDR + ":" + port, StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(host, "localhost:" + port, StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(host, Constants.HOST_ADDR, StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) ||
                !TestSettingsOrigin(req))
            {
                HttpResponse.SendText(stream, 403, "Forbidden", "Untrusted request origin or host.");
                return false;
            }

            if (req.Method == "OPTIONS")
            {
                HttpResponse.SendBytes(stream, 204, "No Content", HttpResponse.GetNoStoreHeaders(), new byte[0]);
                return false;
            }

            var request = new RouteRequest(stream, req);
            foreach (var handle in Handlers)
            {
                bool? answered = handle(request);
                if (answered != null) return answered.Value;
            }
            HttpResponse.SendText(stream, 404, "Not Found", "Not found");
            return false;
        }

        // the route classes in the order they are asked; no two of them answer the same path
        internal static readonly Func<RouteRequest, bool?>[] Handlers =
        {
            GameRoutes.Handle,
            PageRoutes.Handle,
            RecordingRoutes.Handle,
            SettingsRoutes.Handle,
            SetupRoutes.Handle,
            OverlayRoutes.Handle,
            AudioRoutes.Handle,
            DiscordRoutes.Handle,
            WindowRoutes.Handle,
            NotificationRoutes.Handle,
            EditorRoutes.Handle,
            UpdateRoutes.Handle,
            AccountRoutes.Handle,
            AppearanceRoutes.Handle,
            StorageRoutes.Handle,
            AppRoutes.Handle,
            ClipRoutes.Handle,
            ShareRoutes.Handle,
            SdkRoutes.Handle,
        };
    }
}

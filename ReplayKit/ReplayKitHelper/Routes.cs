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
    // request dispatcher for every helper route
    internal static class Routes
    {
        private static readonly string[] AppIconExtensions = { ".ico", ".png", ".jpg", ".jpeg", ".bmp" };

        // "40-a1b2c3d4.ico" -> "40" -- drop the source-hash suffix ImportCustomIcon appends so the grid shows a clean label.
        private static string UserIconLabel(string fileName) =>
            Regex.Replace(Path.GetFileNameWithoutExtension(fileName), "-[0-9a-f]{8}$", "");

        // the colours the dock draws a theme tile from.
        private static JObject ThemeSwatch(Themes.Tokens t) => new JObject
        {
            ["bg"] = t.Bg, ["panel"] = t.Panel, ["accent"] = t.Accent, ["text"] = t.Text,
            ["background"] = Themes.BackgroundCss(t),
        };

        private sealed class OwnerWindow : System.Windows.Forms.IWin32Window
        {
            public OwnerWindow(IntPtr h) { Handle = h; }
            public IntPtr Handle { get; }
        }

        private const string FolderPickerTitle = "Choose the recordings folder";
        private const string FilePickerTitle = "Choose an icon image";

        // blocking modal picker on a one-shot STA thread (winforms dialogs need STA + a pump). parents to the ReplayKit Settings popup if its open. "" on cancel/error.
        private static string PickPath(bool folder, string filter, string initial)
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

        private const string AudioPickerTitle = "Import audio";
        // the containers of video are listed too, since the sound of a video file can be taken out and used
        private const string AudioFilter = "Audio files|*.mp3;*.wav;*.ogg;*.oga;*.opus;*.flac;*.m4a;*.aac;*.wma;*.aif;*.aiff;*.mka;*.mp2;*.ac3;*.amr;*.au;*.caf;*.webm;*.mp4;*.mov;*.mkv|All files (*.*)|*.*";

        private const string MediaPickerTitle = "Add a GIF or image";
        private const string MediaFilter = "GIFs and images|*.gif;*.png;*.jpg;*.jpeg|All files (*.*)|*.*";

        private static string[] PickAudioFiles() => PickFiles(AudioPickerTitle, AudioFilter);

        // the picker of the editor: a modal dialog on a one-shot sta thread like PickPath, where several files can be chosen at once; an empty list when it is cancelled or fails
        private static string[] PickFiles(string title, string filter)
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
        private static string PickFolderModern(IntPtr owner, string initial)
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

        private const uint FOS_PICKFOLDERS = 0x20;
        private const uint FOS_FORCEFILESYSTEM = 0x40;
        private const uint FOS_NOCHANGEDIR = 0x8;
        private const uint FOS_PATHMUSTEXIST = 0x800;
        private const uint SIGDN_FILESYSPATH = 0x80058000;

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

        private static Process[] GetProcessesByNames(params string[] names)
        {
            var list = new List<Process>();
            foreach (var name in names)
            {
                try { list.AddRange(Process.GetProcessesByName(name)); }
                catch (InvalidOperationException) { }
            }
            return list.ToArray();
        }

        private static string GetRunningObsPath()
        {
            var procs = GetProcessesByNames("obs64", "obs32", "obs");
            foreach (var p in procs)
            {
                try { if (!string.IsNullOrEmpty(p.MainModule?.FileName)) return p.MainModule.FileName; }
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

        // close the main window first so obss own closeEvent runs and saves window position to global.ini -- Process.Kill() skips that entirely, which is why obs kept reopening at a stale (sometimes wrong-monitor) position after every replaykit-triggered restart. uses Native.CloseObsMainWindow (hwnd published once per session by the native plugin) instead of MainWindowHandle/CloseMainWindow() -- .nets heuristic can grab a parked/hidden Discord-share projector instead of the real main window, which silently skips the graceful close and falls thru to the force-kill below every time. obs-browser-page helpers never own the published main-window hwnd so this is correctly a no-op for them.
        private static void StopObsForRestart(string reason)
        {
            Thread.Sleep(300);
            // tell the plugins close-to-tray filter this WM_CLOSE is a real restart/exit, not the user clicking X -- otherwise it swallows the close and obs never gets to save its geometry before the force-kill below. wait for the plugin to ack so the close cant beat the message; proceed anyway if it doesnt (older bundle without the pipe -- same as before this signal existed).
            try { if (!PipeClient.SendAllowCloseAndWait(1000)) Log.Write(reason + ": native plugin did not ack ALLOWCLOSE; proceeding."); } catch { }
            var procs = GetProcessesByNames("obs64", "obs32", "obs", "obs-browser-page");
            foreach (var p in procs)
            {
                try { Native.CloseObsMainWindow((uint)p.Id); } catch { }
            }
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(5000);
            foreach (var p in procs)
            {
                int remaining = (int)Math.Max(0, (deadline - DateTime.UtcNow).TotalMilliseconds);
                try { if (!p.HasExited) p.WaitForExit(remaining); } catch (InvalidOperationException) { } catch (Win32Exception) { }
            }
            foreach (var p in procs)
            {
                try { if (!p.HasExited) p.Kill(); }
                catch (Exception ex) { Log.Write(reason + ": Stop-Process PID " + p.Id + " failed: " + ex.Message); }
            }
            Server.State.Shutdown = true;
        }

        // spawn restart_obs.ps1 outside our kill-on-close job so it lives past helper exit. an in-process relaunch is unreliable -- by the time obs is dead and any cleanup completes, this process has often already been torn down (parent-process watchdog exit, abandoned-mutex on takeover, etc.) and never reaches the launch line. doing the relaunch from a sibling process owned by the os instead of by us is the only way to make it survive every shutdown path.
        private static bool StartDetachedObsRelauncher(string obsPath, int obsPid)
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

        // arms the detached relauncher (or falls back to the legacy in-process path), sends the response, then force-closes obs. shared by /settings' restart-required branch, /restart-obs, and /restart-obs-clean.
        private static void ArmRelaunchAndCloseObs(string obsPath, string routeName)
        {
            int obsTargetPid = 0;
            if (ParentWatchdog.ParentPid > 0)
            {
                obsTargetPid = ParentWatchdog.ParentPid;
            }
            else
            {
                var found = GetProcessesByNames("obs64", "obs32", "obs");
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
        private static JObject FailureJson(Exception ex)
        {
            var body = new JObject { ["ok"] = false, ["message"] = ex.Message };
            if (ex is SetupExeException setup) body["code"] = setup.Code;
            return body;
        }

        private static bool TestSettingsOrigin(HttpRequest req)
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

        private static bool TrustedOrigin(string origin)
        {
            int port = Server.State.Config?["port"]?.Value<int?>() ?? Constants.DEFAULT_PORT;
            return string.Equals(origin, "http://127.0.0.1:" + port, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(origin, "http://localhost:" + port, StringComparison.OrdinalIgnoreCase);
        }

        // normalize explorer paths before comparing them. file explorer may report the active tab thru locationurl or thru document.folder, depending on windows version and tab state.
        private static string ConvertToExplorerComparePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return null; }
        }

        private static string GetExplorerWindowPath(dynamic window)
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

        private static JObject TrimResultToJson(TrimResult r)
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

        private static JObject KeyframeResultToJson(KeyframeScanResult r)
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

        private static JObject ObsResultToJson(ObsWebSocketResult r)
        {
            var obj = new JObject { ["ok"] = r.Ok };
            if (r.Unavailable) obj["unavailable"] = true;
            if (r.RequestType != null) obj["requestType"] = r.RequestType;
            if (!r.Ok) { obj["message"] = r.Message; if (r.Code != 0) obj["code"] = r.Code; }
            else if (r.Data != null) obj["data"] = r.Data;
            return obj;
        }

        // primary-desktop jpeg for the overlay-preview backdrop (setup wizard + settings overlay page); captured the first time it is requested then held for the life of this process, which is the life of this obs session, so the backdrop is one stable shot of the users screen and not a per-frame capture. never written to disk -- a new obs session is a new process and captures fresh, and the response is no-store so cef cannot replay last sessions image.
        private static byte[] _desktopPreviewJpeg;
        private static readonly object _desktopPreviewGate = new object();

        // capture + cache the primary screen as a downscaled jpeg; returns null when there is no usable screen, and a near-black grab (secure desktop up) is returned but not cached so the next call retries.
        private static byte[] GetDesktopPreviewJpeg()
        {
            if (_desktopPreviewJpeg != null) return _desktopPreviewJpeg;
            lock (_desktopPreviewGate)
            {
                if (_desktopPreviewJpeg != null) return _desktopPreviewJpeg;
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
                    if (!IsNearlyBlack(full)) _desktopPreviewJpeg = jpeg;
                    return jpeg;
                }
            }
        }

        // scale src into a w x h 24bpp jpeg (quality 72), same encode path as Native.SaveThumbnail minus the letterbox.
        private static byte[] EncodeScaledJpeg(System.Drawing.Bitmap src, int w, int h)
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
        private static bool IsNearlyBlack(System.Drawing.Bitmap bmp)
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

            int port = Server.State.Config?["port"]?.Value<int?>() ?? Constants.DEFAULT_PORT;
            // every route is gated here on host and origin, so no route checks them again
            if (!req.Headers.TryGetValue("host", out string host) ||
                !(string.Equals(host, "127.0.0.1:" + port, StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(host, "localhost:" + port, StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
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

            string path = req.Path;
            var query = req.Query;
            string Q(string key, string def = "") => query.TryGetValue(key, out string v) ? v : def;
            bool QFlag(string key) => query.TryGetValue(key, out string v) && v == "1";

            if (Regex.IsMatch(path, @"^/(?:controls\.html)?$")) { HttpResponse.ServeHtml(stream, "controls.html"); return false; }
            if (Regex.IsMatch(path, @"^/controls_app\.html$")) { HttpResponse.ServeHtml(stream, "controls_app.html"); return false; }
            // the clean layout frames these inside its shell page (?embedded=1), which needs same-origin framing; the popup windows load them on their own and keep the DENY
            bool framed = QFlag("embedded");
            if (Regex.IsMatch(path, @"^/clips-view$|^/clips\.html$")) { HttpResponse.ServeHtml(stream, "clips.html", framed); return false; }
            if (Regex.IsMatch(path, @"^/settings-view$|^/settings\.html$")) { HttpResponse.ServeHtml(stream, "settings.html", framed); return false; }
            if (Regex.IsMatch(path, @"^/setup-view$|^/setup\.html$")) { HttpResponse.ServeHtml(stream, "setup.html", framed); return false; }
            if (Regex.IsMatch(path, @"^/shell-view$|^/shell\.html$")) { HttpResponse.ServeHtml(stream, "shell.html"); return false; }
            if (Regex.IsMatch(path, @"^/update-prompt$|^/update-prompt\.html$")) { HttpResponse.ServeHtml(stream, "update_prompt.html"); return false; }
            if (Regex.IsMatch(path, @"^/notifications-view$|^/notifications\.html$")) { HttpResponse.ServeHtml(stream, "notifications.html"); return false; }
            if (Regex.IsMatch(path, @"^/account-menu$|^/account-menu\.html$")) { HttpResponse.ServeHtml(stream, "account-menu.html"); return false; }
            if (Regex.IsMatch(path, @"^/hotkey-menu$|^/hotkey-menu\.html$")) { HttpResponse.ServeHtml(stream, "hotkey-menu.html"); return false; }
            if (Regex.IsMatch(path, @"^/storage-view$|^/storage\.html$")) { HttpResponse.ServeHtml(stream, "storage.html"); return false; }
            if (Regex.IsMatch(path, @"^/notification-reader-view$|^/notification-reader\.html$")) { HttpResponse.ServeHtml(stream, "notification-reader.html"); return false; }

            if (path == "/settings/native")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                try { HttpResponse.SendJson(stream, 200, ReplaykitSettings.GetNativeSettingsPayload()); }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/settings")
            {
                if (req.Method == "GET")
                {
                    try { HttpResponse.SendJson(stream, 200, ReplaykitSettings.GetSettingsPayload()); }
                    catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                    return false;
                }
                if (req.Method == "POST")
                {
                    try
                    {
                        // whether obs restarts follows from what changed, so the restart flag a page sends is not needed here
                        var result = ReplaykitSettings.SaveSettingsFromRequest(req.Body);
                        if (result["restartRequired"]?.Value<bool>() == true)
                        {
                            string obsPath = GetRunningObsPath();
                            if (obsPath == null)
                            {
                                HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = "Could not locate OBS executable path to relaunch from." });
                                return false;
                            }
                            ArmRelaunchAndCloseObs(obsPath, "/settings");
                            HttpResponse.SendJson(stream, 200, result);
                            StopObsForRestart("/settings");
                            return false;
                        }
                        HttpResponse.SendJson(stream, 200, result);
                        PipeClient.SendSettingsApplied();
                    }
                    catch (Exception ex) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                    return false;
                }
                HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET or POST required");
                return false;
            }

            if (path == "/setup/state")
            {
                // fail-closed to not-pending: never surface an error that would make the dock keep trying to open the wizard.
                try { HttpResponse.SendJson(stream, 200, ReplaykitSettings.GetSetupState()); }
                catch (Exception) { HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["pending"] = false }); }
                return false;
            }

            if (path == "/setup/complete")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try { HttpResponse.SendJson(stream, 200, ReplaykitSettings.SetSetupComplete()); }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            // primary-desktop jpeg for the overlay-preview backdrop; one capture per obs session (see GetDesktopPreviewJpeg), served no-store, and the dock cache-busts per page load so cef never shows a stale one.
            if (path == "/desktop-preview.jpg")
            {
                byte[] jpeg = null;
                try { jpeg = GetDesktopPreviewJpeg(); }
                catch (Exception ex) { Log.Write("desktop-preview capture failed: " + ex.Message); }
                if (jpeg == null || jpeg.Length == 0)
                {
                    HttpResponse.SendBytes(stream, 503, "Service Unavailable", HttpResponse.GetNoStoreHeaders(), new byte[0]);
                    return false;
                }
                var hj = HttpResponse.GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = "image/jpeg" });
                HttpResponse.SendBytes(stream, 200, "OK", hj, jpeg);
                return false;
            }

            if (path == "/settings/overlay-preview")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try
                {
                    string mode = Q("mode", "preview");
                    HttpResponse.SendJson(stream, 200, ReplaykitSettings.OverlayPreviewFromRequest(req.Body, mode));
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            // the overlay items live rect on the canvas, and a setter the Apply path calls once the user has dragged it in the in-page projector. obss own scene item stays the source of truth for position, so a drag done in the projector and one done in obs converge on the same value.
            if (path == "/overlay/position")
            {
                // read-only: a drag is saved as the overlayPosition setting and reaches obs through the normal settings apply, so it survives the scene rewrite + restart an overlay-style change forces
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                HttpResponse.SendJson(stream, 200, ReplaykitSettings.GetOverlayPositionLive());
                return false;
            }

            if (path == "/settings/hotkey-capture")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                bool active = QFlag("active");
                try { HttpResponse.SendJson(stream, 200, ReplaykitSettings.SetHotkeyCapture(active)); }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            // microphone settings: the device list for the combo, and the loopback mic test. the test opens the microphone and plays it back, so every route is origin-gated and the level poll doubles as the keep-alive.
            if (path == "/mic/devices")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                HttpResponse.SendJson(stream, 200, MicTest.GetDevicesPayload());
                return false;
            }

            if (path == "/mic/test/start")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                if (!int.TryParse(Q("volume"), out int volume)) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Volume must be a number." }); return false; }
                if (!int.TryParse(Q("gate"), out int gate)) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Input sensitivity must be a number." }); return false; }
                string noiseFlag = Q("ns");
                if (noiseFlag != "0" && noiseFlag != "1") { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Noise suppression must be 0 or 1." }); return false; }
                string playbackFlag = Q("playback", "1");
                if (playbackFlag != "0" && playbackFlag != "1") { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Playback must be 0 or 1." }); return false; }
                var started = MicTest.Start(Q("device"), volume, noiseFlag == "1", gate, playbackFlag == "1");
                HttpResponse.SendJson(stream, started["ok"]?.Value<bool>() == true ? 200 : 400, started);
                return false;
            }

            if (path == "/mic/test/params")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                if (!int.TryParse(Q("volume"), out int volume)) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Volume must be a number." }); return false; }
                if (!int.TryParse(Q("gate"), out int gate)) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Input sensitivity must be a number." }); return false; }
                string noiseFlag = Q("ns");
                if (noiseFlag != "0" && noiseFlag != "1") { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Noise suppression must be 0 or 1." }); return false; }
                var changed = MicTest.SetParams(volume, noiseFlag == "1", gate);
                HttpResponse.SendJson(stream, changed["ok"]?.Value<bool>() == true ? 200 : 400, changed);
                return false;
            }

            if (path == "/mic/test/level")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                HttpResponse.SendJson(stream, 200, MicTest.ReadLevel());
                return false;
            }

            if (path == "/mic/test/stop")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                HttpResponse.SendJson(stream, 200, MicTest.Stop());
                return false;
            }

            if (path == "/share-preview")
            {
                if (req.Method == "GET")
                {
                    try { HttpResponse.SendJson(stream, 200, ReplaykitSettings.GetSharePreviewState(true)); }
                    catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                    return false;
                }
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try
                {
                    if (string.IsNullOrWhiteSpace(req.Body)) throw new InvalidOperationException("Missing share preview body.");
                    var incoming = JObject.Parse(req.Body);
                    if (incoming["enabled"]?.Type != JTokenType.Boolean) throw new InvalidOperationException("Share preview enabled must be a JSON boolean.");
                    var result = ReplaykitSettings.SetSharePreviewEnabled(incoming["enabled"].Value<bool>());
                    int code = result["ok"]?.Value<bool>() == true ? 200 : 503;
                    HttpResponse.SendJson(stream, code, result);
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/projector-inspect")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try { HttpResponse.SendJson(stream, 200, DiscordProjector.Inspect()); }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/projector-repark")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try { HttpResponse.SendJson(stream, 200, DiscordProjector.Repark(force: true)); }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/uninstall")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try { HttpResponse.SendJson(stream, 200, Uninstall.StartCleanupFromSettings(req.Body)); }
                catch (Exception ex) { HttpResponse.SendJson(stream, 400, FailureJson(ex)); }
                return false;
            }

            // the cached setup exe the uninstall and the discord screenshare buttons run: what state it is in, and a download of it again from the latest release
            if (path == "/setup/status")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                HttpResponse.SendJson(stream, 200, SetupExe.GetStatus());
                return false;
            }

            if (path == "/setup/repair")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try { HttpResponse.SendJson(stream, 200, SetupExe.Repair()); }
                catch (Exception ex) { HttpResponse.SendJson(stream, 400, FailureJson(ex)); }
                return false;
            }

            if (path == "/install-discord-screenshare")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try { HttpResponse.SendJson(stream, 200, Uninstall.StartDiscordScreenshareInstall()); }
                catch (Exception ex) { HttpResponse.SendJson(stream, 400, FailureJson(ex)); }
                return false;
            }

            // dock -> plugin bridge: the dock cannot draw a modal past its own panel, so it asks the native plugin to show a themed confirm over the whole dimmed obs window. the plugin runs the matching action on ok.
            if (path == "/ui-confirm")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try
                {
                    string kind = string.IsNullOrWhiteSpace(req.Body) ? null : JObject.Parse(req.Body).Value<string>("kind");
                    if (kind != "sharepreview-setup" && kind != "streamable-relogin")
                        throw new InvalidOperationException("Unknown confirm kind.");
                    PipeClient.SendUiConfirm(kind);
                    HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true });
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/uninstall-discord-screenshare")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try { HttpResponse.SendJson(stream, 200, Uninstall.StartDiscordScreenshareRemoval()); }
                catch (Exception ex) { HttpResponse.SendJson(stream, 400, FailureJson(ex)); }
                return false;
            }

            // the bell in the clips / obs / settings title bars. read is what clears the badge; dismiss removes a row entirely.
            if (path == "/notifications")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                HttpResponse.SendJson(stream, 200, Notifications.Get());
                return false;
            }

            if (path == "/notifications/read" || path == "/notifications/unread" || path == "/notifications/read-all" ||
                path == "/notifications/dismiss" || path == "/notifications/clear")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string id = Q("id");
                if (path == "/notifications/read-all") HttpResponse.SendJson(stream, 200, Notifications.MarkAllRead());
                else if (path == "/notifications/clear") HttpResponse.SendJson(stream, 200, Notifications.Clear());
                else if (string.IsNullOrWhiteSpace(id)) HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Missing id." });
                else if (path == "/notifications/read") HttpResponse.SendJson(stream, 200, Notifications.MarkRead(id));
                else if (path == "/notifications/unread") HttpResponse.SendJson(stream, 200, Notifications.MarkUnread(id));
                else HttpResponse.SendJson(stream, 200, Notifications.Dismiss(id));
                return false;
            }

            // a notification too long for the panel: the plugin shows all of it in a card over the obs window
            if (path == "/notifications/reader")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string id = Q("id");
                if (string.IsNullOrEmpty(id) || !Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,64}$")) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Invalid id." }); return false; }
                PipeClient.SendNotificationReader(id);
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true });
                return false;
            }

            // auto subtitles: the one-time download of the speech engine and its model, and the jobs that turn a part of a clip into word timings
            if (path == "/speech/status" || path == "/speech/install" || path == "/speech/transcribe" || path == "/speech/job" || path == "/speech/cancel")
            {
                bool post = path != "/speech/status" && path != "/speech/job";
                if (req.Method != (post ? "POST" : "GET")) { HttpResponse.SendText(stream, 405, "Method Not Allowed", (post ? "POST" : "GET") + " required"); return false; }
                if (path == "/speech/status") HttpResponse.SendJson(stream, 200, Speech.Status());
                else if (path == "/speech/install") HttpResponse.SendJson(stream, 200, Speech.StartInstall());
                else if (path == "/speech/job") HttpResponse.SendJson(stream, 200, Speech.JobStatus(Q("id")));
                else if (path == "/speech/cancel") HttpResponse.SendJson(stream, 200, Speech.Cancel(Q("id")));
                else
                {
                    bool parsed = double.TryParse(Q("start"), NumberStyles.Float, CultureInfo.InvariantCulture, out double from) &
                        double.TryParse(Q("end"), NumberStyles.Float, CultureInfo.InvariantCulture, out double to) &
                        int.TryParse(Q("track"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int track);
                    if (!parsed) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Bad request" }); return false; }
                    var started = Speech.StartTranscribe(Q("file"), from, to, track, Q("language"));
                    HttpResponse.SendJson(stream, started["ok"]?.Value<bool>() == true ? 200 : 400, started);
                }
                return false;
            }

            // the record tab of the editor: a voiceover from the microphone of the settings or one picked in the tab, which becomes an import once it stops
            if (path == "/voiceover/start" || path == "/voiceover/level" || path == "/voiceover/stop")
            {
                bool post = path != "/voiceover/level";
                if (req.Method != (post ? "POST" : "GET")) { HttpResponse.SendText(stream, 405, "Method Not Allowed", (post ? "POST" : "GET") + " required"); return false; }
                JObject answer = path == "/voiceover/start" ? Voiceover.Start(Q("device")) : path == "/voiceover/level" ? Voiceover.Level() : Voiceover.Stop(Q("keep") == "1");
                HttpResponse.SendJson(stream, answer["ok"]?.Value<bool>() == true ? 200 : 400, answer);
                return false;
            }

            if (path == "/notifications/reader/close")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                PipeClient.SendNotificationReaderClose();
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true });
                return false;
            }

            if (path == "/notifications/open-link")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string url = Q("url");
                // scoped to github release pages on purpose -- this is not a general "open any url/file" primitive, only ever used for the link a notification itself carries
                bool validUrl = !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                    uri.Scheme == Uri.UriSchemeHttps && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase);
                if (!validUrl) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Only a github.com release link can be opened this way." }); return false; }
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true }); }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/update/check")
            {
                if (req.Method != "GET" && req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET or POST required"); return false; }
                HttpResponse.SendJson(stream, 200, Update.GetUpdateStatus(Q("manual") == "1"));
                return false;
            }

            if (path == "/update/notes")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                try
                {
                    string installed = Update.NormalizeVersion(Update.GetInstalledVersion());
                    var release = Update.GetReleaseForVersion(installed);
                    if (release == null) { HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false, ["message"] = "No release notes found for version " + installed + "." }); return false; }
                    // a real, persisted notification (not a throwaway client-side one) so it survives closing the panel or restarting obs same as any other -- keyed on the version so pressing the button again just refreshes the same row instead of stacking a duplicate. marked read immediately: the user asked to see this, it should not also bump the unread badge.
                    string title = (string.IsNullOrEmpty(release.Name) ? "ReplayKit " + installed : release.Name) + " (installed)";
                    var snapshot = Notifications.Add("info", "whats-new:" + installed, title, release.Body, release.HtmlUrl);
                    string addedId = (snapshot["items"] as JArray)?.OfType<JObject>()
                        .FirstOrDefault(i => i["key"]?.Value<string>() == "whats-new:" + installed)?["id"]?.Value<string>();
                    if (!string.IsNullOrEmpty(addedId)) snapshot = Notifications.MarkRead(addedId);
                    snapshot["id"] = addedId;
                    HttpResponse.SendJson(stream, 200, snapshot);
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/update/startup-check")
            {
                if (req.Method != "GET" && req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET or POST required"); return false; }
                HttpResponse.SendJson(stream, 200, Update.GetStartupUpdateStatus());
                return false;
            }

            if (path == "/update/auto-check")
            {
                if (req.Method != "GET" && req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET or POST required"); return false; }
                HttpResponse.SendJson(stream, 200, Update.GetAutoUpdateStatus());
                return false;
            }

            if (path == "/update/apply")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                HttpResponse.SendJson(stream, 200, Update.ApplyUpdate());
                return false;
            }

            // polled by the updater window while /update/apply is in flight, so its bar shows real bytes and speed
            if (path == "/update/progress")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                HttpResponse.SendJson(stream, 200, Update.GetProgress());
                return false;
            }

            if (path == "/update/install-result")
            {
                if (req.Method != "GET" && req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET or POST required"); return false; }
                HttpResponse.SendJson(stream, 200, Update.GetInstallResult());
                return false;
            }

            if (path == "/update/install-result/ack")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                HttpResponse.SendJson(stream, 200, Update.MarkInstallResultSeen());
                return false;
            }

            if (path == "/update/open-prompt")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                HttpResponse.SendJson(stream, 200, Update.OpenUpdatePromptWindow(Q("version")));
                return false;
            }

            if (path == "/update/later")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try
                {
                    string version = Q("version");
                    if (string.IsNullOrWhiteSpace(version) && !string.IsNullOrWhiteSpace(req.Body))
                    {
                        var body = JObject.Parse(req.Body);
                        if (body["version"] != null) version = body["version"].Value<string>();
                    }
                    HttpResponse.SendJson(stream, 200, Update.SetUpdatePromptDismissed(version));
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/signin-windows-status")
            {
                // returns visible sign-in popups owned by obss process family so the dock can update its status label. with overlay=1, the google oauth popup is moved onto the streamable login window so the flow presents as one window while preserving streamables popup-based oauth.
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                uint ownerPid = ParentWatchdog.ParentPid > 0 ? (uint)ParentWatchdog.ParentPid : 0;
                string jsonArr = "[]";
                try
                {
                    bool overlayGoogle = QFlag("overlay");
                    jsonArr = Native.ListSignInWindows(ownerPid, false, overlayGoogle);
                }
                catch (Exception ex) { Log.Write("/signin-windows-status: " + ex.Message); }
                // give the sign-in popups our icon + themed title bar -- obs-browser doesnt icon its own window.open popups. dedup is title-keyed inside StyleSignInWindows so this poll-driven call only paints when a new page title shows up.
                try
                {
                    string iconPath = Constants.OBS_ICON_PATH;
                    string resolved = ReplaykitSettings.ResolveAppIconPath(ReplaykitSettings.Normalize(ReplaykitSettings.ReadSettings()));
                    if (!string.IsNullOrEmpty(resolved)) iconPath = resolved;
                    Native.StyleSignInWindows(iconPath);
                }
                catch (Exception ex) { Log.Write("/signin-windows-status style: " + ex.Message); }
                var h = HttpResponse.GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = "application/json" });
                HttpResponse.SendBytes(stream, 200, "OK", h, Encoding.UTF8.GetBytes(jsonArr));
                return false;
            }

            if (path == "/close-signin-windows")
            {
                // post to nuke the stuck sign-in popups via win32 wm_close. the dock js calls this after /import-session succeeds becuase cef refuses window.close() on cross-origin popups but windows-level wm_close works fine. we match only windows owned by the parent obs process, by title.
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                uint ownerPid = ParentWatchdog.ParentPid > 0 ? (uint)ParentWatchdog.ParentPid : 0;
                // specific titles only. we deliberately do not include a bare "streamable" since that would also match the clip-viewer popup (clips.html) and any other dock title that contains "streamable".
                string[] titles = { "about:blank", "Sign In - Google Accounts", "Sign in - Google Accounts", "Signing in to Streamable", "Dashboard - Streamable", "Log in - Streamable", "Sign in to Streamable - Streamable", "Streamable | " };
                int n = 0;
                try
                {
                    n = Native.CloseWindowsByTitle(titles, ownerPid);
                    Native.ResetSignInWindowStyleCache(); // next attempt restyles from scratch
                    Log.Write("/close-signin-windows: closed " + n + " window(s) under OBS pid=" + ownerPid);
                }
                catch (Exception ex) { Log.Write("/close-signin-windows: " + ex.Message); }
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["closed"] = n });
                return false;
            }

            // same-origin splash the streamable sign-in popup navigates to (see the file for why); served through ServeHtml so it picks up the active theme + a real Content-Type instead of about:blank + document.write
            if (path == "/sign-in-loader") { HttpResponse.ServeHtml(stream, "sign-in-loader.html"); return false; }

            // static .svg files under icons/ -- one subfolder deep max, no traversal. used by the dock CSS (mask/background) where window.RK_ICONS (js-only) does not reach.
            var iconFile = Regex.Match(path, @"^/icons/([A-Za-z0-9_-]+/[A-Za-z0-9_-]+\.svg)$");
            if (iconFile.Success)
            {
                string full = Path.GetFullPath(Path.Combine(Constants.APP_ICONS_DIR, iconFile.Groups[1].Value));
                string root = Path.GetFullPath(Constants.APP_ICONS_DIR) + Path.DirectorySeparatorChar;
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
                {
                    var hi = HttpResponse.GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = "image/svg+xml" });
                    HttpResponse.SendBytes(stream, 200, "OK", hi, File.ReadAllBytes(full));
                }
                else HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false });
                return false;
            }

            // the bundled fonts by file name, for the subtitles the editor previews; no folders, no traversal
            var fontFile = Regex.Match(path, @"^/fonts/([A-Za-z0-9_-]+\.ttf)$");
            if (fontFile.Success)
            {
                string full = Path.Combine(Constants.APP_FONTS_DIR, fontFile.Groups[1].Value);
                if (File.Exists(full)) HttpResponse.SendBytes(stream, 200, "OK", new Dictionary<string, string> { ["Content-Type"] = "font/ttf", ["Cache-Control"] = "max-age=86400" }, File.ReadAllBytes(full));
                else HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false });
                return false;
            }

            // the Bango Cat folder of obs for the overlay preview: its modes with their configs and model bakes, and its pictures by path inside it
            if (path == "/bongo-cat/manifest")
            {
                HttpResponse.SendJson(stream, 200, BongoCatFiles.Manifest());
                return false;
            }
            if (path == "/bongo-cat/picture")
            {
                string picture = BongoCatFiles.Picture(Q("path", ""));
                if (picture == null) { HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false }); return false; }
                var hb = HttpResponse.GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = "image/png" });
                HttpResponse.SendBytes(stream, 200, "OK", hb, File.ReadAllBytes(picture));
                return false;
            }

            // static assets under obs-custom-dock/overlay-preview/ -- the sprite sheets + layout json of the wasd/mouse preview (wasd/, mouse/) and the baked bongo cat models (bongo-models/). two path segments, no traversal.
            var previewAsset = Regex.Match(path, @"^/overlay-preview/([a-z0-9_-]+/[a-z0-9_-]+\.(png|json))$");
            if (previewAsset.Success)
            {
                string baseDir = Path.Combine(AppConfig.GetDockDir(), "overlay-preview");
                string full = Path.GetFullPath(Path.Combine(baseDir, previewAsset.Groups[1].Value));
                string root = Path.GetFullPath(baseDir) + Path.DirectorySeparatorChar;
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
                {
                    string ext = previewAsset.Groups[2].Value;
                    string ctype = ext == "json" ? "application/json" : "image/png";
                    var hp = HttpResponse.GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = ctype });
                    HttpResponse.SendBytes(stream, 200, "OK", hp, File.ReadAllBytes(full));
                }
                else HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false });
                return false;
            }

            // artwork for the update window: obs-custom-dock/update-art.png (raster, preferred) or update-art.svg, served from whichever exists so the art can be swapped without touching the page.
            if (path == "/update-art")
            {
                string dockDir = AppConfig.GetDockDir();
                foreach (var candidate in new[] { "update-art.png", "update-art.svg" })
                {
                    string full = Path.Combine(dockDir, candidate);
                    if (!File.Exists(full)) continue;
                    string ext = Path.GetExtension(candidate);
                    string ctype = ext == ".svg" ? "image/svg+xml" : "image/png";
                    var ha = HttpResponse.GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = ctype });
                    HttpResponse.SendBytes(stream, 200, "OK", ha, File.ReadAllBytes(full));
                    return false;
                }
                HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false });
                return false;
            }

            if (Regex.IsMatch(path, @"^/obs-icon\.svg$|^/favicon\.ico$"))
            {
                var h = HttpResponse.GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = "image/svg+xml" });
                HttpResponse.SendBytes(stream, 200, "OK", h, Media.GetObsIconSvg());
                return false;
            }

            if (path == "/obs-icon.ico") { HttpResponse.ServeObsIcon(stream); return false; }

            if (path == "/version")
            {
                // local file read only, no github call -- for ui labels that just want "what am i running", not the update-check flow.
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["version"] = Update.GetInstalledVersion() });
                return false;
            }

            if (path == "/open-url")
            {
                // target=_blank inside these docks opens a bare cef popup (obs' own embedded chromium), not the users actual browser -- shelling out here hands it to whatever the os has registered for http/https instead.
                string target = Q("url");
                bool ok = false;
                if (Regex.IsMatch(target, "^https://[^\\s\"]+$"))
                {
                    try { Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true })?.Dispose(); ok = true; }
                    catch (Exception ex) { Log.Write("/open-url failed for '" + target + "': " + ex.Message); }
                }
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = ok });
                return false;
            }

            if (path == "/style-window")
            {
                string title = Q("title", "Clips");
                bool taskbar = QFlag("taskbar");
                int matched = -1;
                // the native plugin owns the icon (title bar + win11 taskbar button) for its own Clips / ReplayKit Settings windows -- style their chrome here but leave the icon to the plugin.
                bool pluginOwnsIcon = title.Equals("Clips", StringComparison.OrdinalIgnoreCase) ||
                    title.Equals("ReplayKit Settings", StringComparison.OrdinalIgnoreCase);
                // on the default app icon, leave the browser-hosted windows native OBS/SVG icon alone. Forcing the legacy ReplayKit ICO here replaces it with a distorted 16px caption image.
                string iconPath = "";
                if (!pluginOwnsIcon)
                {
                    try
                    {
                        string resolved = ReplaykitSettings.ResolveAppIconPath(ReplaykitSettings.Normalize(ReplaykitSettings.ReadSettings()));
                        if (!string.IsNullOrEmpty(resolved)) iconPath = resolved;
                    }
                    catch (Exception ex) { Log.Write("/style-window icon resolve: " + ex.Message); }
                }
                try { matched = Native.StyleWindow(title, iconPath, taskbar); }
                catch (Exception ex) { Log.Write("/style-window threw: " + ex.Message); }
                // logging only on misses keeps the log readable; success is the defualt, silent path.
                if (matched <= 0) Log.Write("/style-window title=" + title + " : 0 matching OBS-family windows found");
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["matched"] = matched });
                return false;
            }

            if (path == "/appearance/icons")
            {
                // the Appearance tabs picker grid: OBS Default + bundled presets + the users saved custom picks. replaykit.ico remains an internal fallback, not a selectable app icon.
                var icons = new JArray { new JObject { ["id"] = "default", ["label"] = "Default", ["url"] = "/obs-icon.svg?v=4" } };
                try
                {
                    if (Directory.Exists(Constants.APP_ICONS_DIR))
                    {
                        var files = Directory.GetFiles(Constants.APP_ICONS_DIR);
                        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                        foreach (var f in files)
                        {
                            if (Array.IndexOf(AppIconExtensions, Path.GetExtension(f).ToLowerInvariant()) < 0) continue;
                            string name = Path.GetFileName(f);
                            if (string.Equals(name, "replaykit.ico", StringComparison.OrdinalIgnoreCase)) continue;
                            icons.Add(new JObject { ["id"] = name, ["label"] = Path.GetFileNameWithoutExtension(name), ["url"] = "/appearance/icon?file=" + Uri.EscapeDataString(name) });
                        }
                    }
                    if (Directory.Exists(Constants.USER_ICONS_DIR))
                    {
                        var ufiles = Directory.GetFiles(Constants.USER_ICONS_DIR, "*.ico");
                        Array.Sort(ufiles, StringComparer.OrdinalIgnoreCase);
                        foreach (var f in ufiles)
                        {
                            string name = Path.GetFileName(f);
                            icons.Add(new JObject
                            {
                                ["id"] = "user/" + name,
                                ["label"] = UserIconLabel(name),
                                ["url"] = "/appearance/icon?file=" + Uri.EscapeDataString("user/" + name),
                                ["deletable"] = true,
                            });
                        }
                    }
                }
                catch (Exception ex) { Log.Write("/appearance/icons: " + ex.Message); }
                var s = ReplaykitSettings.Normalize(ReplaykitSettings.ReadSettings());
                HttpResponse.SendJson(stream, 200, new JObject
                {
                    ["ok"] = true, ["icons"] = icons,
                    ["selected"] = s["appIcon"]?.Value<string>() ?? "default",
                    ["customPath"] = s["appIconCustomPath"]?.Value<string>() ?? "",
                });
                return false;
            }

            if (path == "/appearance/icon")
            {
                string raw = Q("file", "");
                bool isUser = raw.StartsWith("user/", StringComparison.Ordinal);
                string file = Path.GetFileName(raw);
                string ext = Path.GetExtension(file).ToLowerInvariant();
                string dir = isUser ? Constants.USER_ICONS_DIR : Constants.APP_ICONS_DIR;
                string full = string.IsNullOrEmpty(file) ? null : Path.Combine(dir, file);
                if (full != null && Array.IndexOf(AppIconExtensions, ext) >= 0 && File.Exists(full))
                {
                    try
                    {
                        string ctype = ext == ".ico" ? "image/x-icon" : ext == ".png" ? "image/png" : ext == ".bmp" ? "image/bmp" : "image/jpeg";
                        var h = HttpResponse.GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = ctype });
                        HttpResponse.SendBytes(stream, 200, "OK", h, File.ReadAllBytes(full));
                        return false;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Log.Write("/appearance/icon read: " + ex.Message); }
                }
                HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false });
                return false;
            }

            if (path == "/appearance/delete-icon")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string raw = Q("file", "");
                if (!raw.StartsWith("user/", StringComparison.Ordinal))
                {
                    HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Only saved custom icons can be removed." });
                    return false;
                }
                string file = Path.GetFileName(raw);
                string full = Path.Combine(Constants.USER_ICONS_DIR, file);
                bool wasSelected = false;
                string selected = "default";
                try
                {
                    var s = ReplaykitSettings.Normalize(ReplaykitSettings.ReadSettings());
                    selected = s["appIcon"]?.Value<string>() ?? "default";
                    wasSelected = selected == "user/" + file;
                    if (File.Exists(full)) File.Delete(full);
                    if (wasSelected)
                    {
                        ReplaykitSettings.SetAppIconAndApply("default");
                        selected = "default";
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("/appearance/delete-icon: " + ex.Message);
                    HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message });
                    return false;
                }
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["wasSelected"] = wasSelected, ["selected"] = selected });
                return false;
            }

            if (path == "/appearance/themes")
            {
                var s = ReplaykitSettings.Normalize(ReplaykitSettings.ReadSettings());
                var list = new JArray();
                foreach (var kv in Themes.PresetOrder)
                {
                    var tk = Themes.Resolve(new JObject { ["theme"] = kv.Key });
                    list.Add(new JObject { ["id"] = kv.Key, ["label"] = kv.Value, ["swatch"] = ThemeSwatch(tk), ["seed"] = tk.ToSeedJson() });
                }
                try
                {
                    if (Directory.Exists(Constants.USER_THEMES_DIR))
                    {
                        var files = Directory.GetFiles(Constants.USER_THEMES_DIR, "*.json");
                        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                        foreach (var f in files)
                        {
                            string id = "user/" + Path.GetFileNameWithoutExtension(f);
                            var tk = Themes.Resolve(new JObject { ["theme"] = id });
                            list.Add(new JObject { ["id"] = id, ["label"] = Themes.UserThemeLabel(Path.GetFileName(f)), ["swatch"] = ThemeSwatch(tk), ["seed"] = tk.ToSeedJson(), ["deletable"] = true });
                        }
                    }
                }
                catch (Exception ex) { Log.Write("/appearance/themes user: " + ex.Message); }
                HttpResponse.SendJson(stream, 200, new JObject
                {
                    ["ok"] = true, ["themes"] = list,
                    ["selected"] = s["theme"]?.Value<string>() ?? "default",
                    ["custom"] = s["themeCustom"],
                });
                return false;
            }

            if (path == "/rk-theme.css")
            {
                // the committed theme stylesheet the dock pages <link> to. matches the physical rk-theme.css the helper writes for the file:// dock.
                string css;
                try
                {
                    var s = ReplaykitSettings.Normalize(ReplaykitSettings.ReadSettings());
                    css = (s["theme"]?.Value<string>() ?? "default") == "default" ? "/* default theme */" : Themes.DockCss(Themes.Resolve(s));
                }
                catch (Exception ex) { Log.Write("/rk-theme.css: " + ex.Message); css = ""; }
                var hh = HttpResponse.GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = "text/css; charset=utf-8" });
                HttpResponse.SendBytes(stream, 200, "OK", hh, Encoding.UTF8.GetBytes(css));
                return false;
            }

            if (path == "/appearance/theme.css")
            {
                // live-preview stylesheet -- ?theme=<id> or ?custom=<json> overrides the saved setting for the fetch.
                var s = ReplaykitSettings.Normalize(ReplaykitSettings.ReadSettings());
                string previewId = Q("theme", "");
                if (!string.IsNullOrEmpty(previewId)) s["theme"] = previewId;
                string rawCustom = Q("custom", "");
                if (!string.IsNullOrEmpty(rawCustom))
                {
                    try { s["themeCustom"] = JObject.Parse(rawCustom); s["theme"] = "custom"; } catch { }
                }
                // the settings tab asks with preview=1 while the theme is being edited, so the native chrome shows the same candidate
                if (Q("preview", "") == "1")
                {
                    try { ReplaykitSettings.SetThemePreview(s); }
                    catch (Exception ex) { Log.Write("/appearance/theme.css preview: " + ex.Message); }
                }
                string css;
                try { css = (s["theme"]?.Value<string>() ?? "default") == "default" ? Themes.DockCss(Themes.Resolve(new JObject { ["theme"] = "default" })) : Themes.DockCss(Themes.Resolve(s)); }
                catch (Exception ex) { Log.Write("/appearance/theme.css: " + ex.Message); css = ""; }
                var h = HttpResponse.GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = "text/css; charset=utf-8" });
                HttpResponse.SendBytes(stream, 200, "OK", h, Encoding.UTF8.GetBytes(css));
                return false;
            }

            if (path == "/appearance/save-theme")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string label = Q("label", "");
                string existingId = Q("id", "");
                JObject payload;
                try { payload = string.IsNullOrWhiteSpace(req.Body) ? null : JObject.Parse(req.Body); }
                catch { payload = null; }
                if (payload == null)
                {
                    HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Missing theme colours." });
                    return false;
                }
                string id;
                try { id = Themes.SaveUserTheme(label, payload, existingId); }
                catch (Exception ex) { Log.Write("/appearance/save-theme: " + ex.Message); id = null; }
                if (id == null) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = string.IsNullOrEmpty(existingId) ? "Could not save theme." : "That saved theme no longer exists." }); return false; }
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["id"] = id });
                return false;
            }

            if (path == "/appearance/pick-color")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try
                {
                    string color = DesktopColorPicker.Pick();
                    HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["cancelled"] = string.IsNullOrEmpty(color), ["color"] = color });
                }
                catch (Exception ex)
                {
                    Log.Write("/appearance/pick-color: " + ex.Message);
                    HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = "Desktop color picker failed: " + ex.Message });
                }
                return false;
            }

            if (path == "/appearance/delete-theme")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string raw = Q("id", "");
                if (!raw.StartsWith("user/", StringComparison.Ordinal))
                {
                    HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Only saved themes can be removed." });
                    return false;
                }
                string name = Path.GetFileNameWithoutExtension(Path.GetFileName(raw.Substring(5)));
                bool wasSelected = false;
                string selected = "default";
                try
                {
                    var s = ReplaykitSettings.Normalize(ReplaykitSettings.ReadSettings());
                    selected = s["theme"]?.Value<string>() ?? "default";
                    wasSelected = selected == "user/" + name;
                    string full = Path.Combine(Constants.USER_THEMES_DIR, name + ".json");
                    if (File.Exists(full)) File.Delete(full);
                    if (wasSelected)
                    {
                        var restart = ReplaykitSettings.SetThemeAndApply("default");
                        selected = "default";
                        HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["wasSelected"] = true, ["selected"] = "default", ["restartRequired"] = restart });
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("/appearance/delete-theme: " + ex.Message);
                    HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message });
                    return false;
                }
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["wasSelected"] = false, ["selected"] = selected });
                return false;
            }

            if (path == "/browse-file" || path == "/browse-folder")
            {
                bool folder = path == "/browse-folder";
                string picked = PickPath(folder, Q("filter", "Images (*.ico;*.png;*.jpg;*.bmp)|*.ico;*.png;*.jpg;*.jpeg;*.bmp|All files (*.*)|*.*"), Q("initial", ""));
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = !string.IsNullOrEmpty(picked), ["path"] = picked });
                return false;
            }

            if (path == "/focus-window")
            {
                // server-side foreground-raise for an existing dock-spawned popup (e.g. "view clips" clicked while the clips window is already open). the docks window.focus() is unreliable inside cef; this route uses win32 SetForegroundWindow + the AttachThreadInput dance which works regardless of who currently owns the foreground.
                string title = Q("title", "Clips");
                bool focused = false;
                try { focused = Native.FocusWindow(title); } catch { }
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["focused"] = focused });
                return false;
            }

            if (path == "/close-window")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string title = Q("title");
                if (title != "ReplayKit Settings" && title != "ReplayKit Update" && title != "ReplayKit Setup")
                {
                    HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Unsupported window title." });
                    return false;
                }
                // a page the clean layout embeds in the obs window has no window of its own to close; the plugin just puts the obs window back on its home view
                if (QFlag("embedded") && title != "ReplayKit Update")
                {
                    PipeClient.SendCloseView(title == "ReplayKit Setup" ? "setup" : "settings");
                    HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["closed"] = 1 });
                    return false;
                }
                // the update popup can be hosted by msedge.exe (--app launched from the bootstrap), which is not an obs-family process, so pass 0 for that title so CloseWindowsByTitle finds it by title alone; settings still goes thru the obs-family gate.
                uint ownerPid = title == "ReplayKit Update" ? 0 : (ParentWatchdog.ParentPid > 0 ? (uint)ParentWatchdog.ParentPid : 0);
                int closed = 0;
                // settings and setup are only hidden by the plugin and shown again later, so their taskbar tab has to survive the close; only the update popup really goes away
                try { closed = Native.CloseWindowsByTitle(new[] { title }, ownerPid, title == "ReplayKit Update"); } catch { }
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["closed"] = closed });
                return false;
            }

            if (path == "/window/maximize")
            {
                // server-side maximize for a dock-spawned popup. called from clips.html when the user enters fullscreen on a clip so the host window fills the screen. js in cef cant resize its own host, hence the win32 round-trip. the native side snapshots the original rect so /window/restore can put it back unless the user manually unmaximized or dragged the host while fullscreened.
                string title = Q("title", "Clips");
                bool maximized = false;
                try { maximized = Native.MaximizeObsWindow(title); } catch { }
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["maximized"] = maximized });
                return false;
            }

            if (path == "/window/fullscreen")
            {
                string title = Q("title", "Clips");
                bool fullscreen = false;
                // embedded in the obs window: the plugin widens the page over the whole window, there is no host window to make fullscreen
                if (QFlag("embedded") && title == "Clips")
                {
                    PipeClient.SendClipsFullscreen(true);
                    HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["fullscreen"] = true });
                    return false;
                }
                if (title == "Clips") PipeClient.SendClipsFullscreen(true);
                try { fullscreen = Native.EnterObsWindowFullscreen(title); } catch { }
                if (!fullscreen && title == "Clips") PipeClient.SendClipsFullscreen(false);
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["fullscreen"] = fullscreen });
                return false;
            }

            if (path == "/window/fullscreen/restore")
            {
                string title = Q("title", "Clips");
                bool restored = false;
                if (QFlag("embedded") && title == "Clips")
                {
                    PipeClient.SendClipsFullscreen(false);
                    HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["restored"] = true });
                    return false;
                }
                try { restored = Native.ExitObsWindowFullscreen(title); } catch { }
                if (title == "Clips") PipeClient.SendClipsFullscreen(false);
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["restored"] = restored });
                return false;
            }

            if (path == "/window/restore")
            {
                // counterpart to /window/maximize: restore the saved rect only if the Clips host is still maximized. if the user dragged or unmaximized it while fullscreened, their manual placement wins.
                string title = Q("title", "Clips");
                bool restored = false;
                try { restored = Native.RestoreObsWindow(title); } catch { }
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["restored"] = restored });
                return false;
            }

            if (path == "/window/resize")
            {
                // drag-to-resize for the borderless popups, started from the resize-grip dots the dock/clips pages draw in their bottom-right corner. BeginResizeWindow polls the mouse button instead of a message-based handoff -- see its comment in Native.cs. each window gets its own floor/ceiling since Settings is a fixed two-pane form while Clips is a fluid grid that tolerates a smaller floor; both axes always move together off the one drag.
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string title = Q("title");
                int minW, minH, maxW = 2400, maxH = 1800;
                // kept in sync with the native QWidget::setMinimumSize() calls in the native plugins CreateSettingsWindow/CreateClipsWindow (windows/settings_window.cpp, windows/clips_window.cpp) -- this custom corner-grip resize used to enforce different bounds than obss own native edge/corner resize on the same windows, so the two disagreed about how small each window could get depending on which resize handle you used.
                if (title == "ReplayKit Settings") { minW = 700; minH = 500; }
                else if (title == "Clips") { minW = 850; minH = 620; }
                else if (title == "ReplayKit Update") { minW = 480; minH = 400; }
                else if (title == "ReplayKit Setup") { minW = 640; minH = 520; }
                else { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Unsupported window title." }); return false; }
                bool resizing = false;
                try { resizing = Native.BeginResizeWindow(title, minW, minH, maxW, maxH); } catch { }
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["resizing"] = resizing });
                return false;
            }

            // a page of the clean shell grabbed at its right edge, bottom edge or the corner between them, which is where the window of obs is resized now that the pages reach those edges; the plugin runs the resize loop of windows inside obs, which repaints as it goes where moving the window from out here left white and stale areas
            if (path == "/shell/resize")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string edge = Q("edge");
                if (edge != "right" && edge != "bottom" && edge != "corner") { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Unknown edge." }); return false; }
                bool resizing = Native.ObsMainResizable() && PipeClient.SendShellResize(edge);
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["resizing"] = resizing });
                return false;
            }

            if (path == "/window/set-size")
            {
                // one-shot corrective resize for a window we just spawned via "chrome --app=" -- see SetWindowSizeCentereds comment for why the spawn-time --window-size flag alone is not reliable.
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string title = Q("title");
                if (!int.TryParse(Q("w"), out int w) || !int.TryParse(Q("h"), out int h) || w <= 0 || h <= 0)
                {
                    HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Missing or invalid w/h." });
                    return false;
                }
                bool applied = false;
                try { applied = Native.SetWindowSizeCentered(title, w, h); } catch { }
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["applied"] = applied });
                return false;
            }

            if (path == "/open_clips")
            {
                var h = HttpResponse.GetNoStoreHeaders(new Dictionary<string, string> { ["Location"] = "/clips-view" });
                HttpResponse.SendBytes(stream, 302, "Found", h, new byte[0]);
                return false;
            }

            // the storage card of the clean layout. the summary is polled by the plugin for the ring in the rail and read by the card itself; every route that changes something is a post from the dock origin
            if (path == "/storage/summary")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                try { HttpResponse.SendJson(stream, 200, Storage.GetSummaryPayload(QFlag("fresh"))); }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            // the temporary files are emptied from the card; the ones kept to open clips quickly are made again the next time a clip is opened
            if (path == "/storage/clear-temp")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try { HttpResponse.SendJson(stream, 200, Storage.ClearTemp()); }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/storage/trim-apply" || path == "/storage/open" || path == "/storage/close")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try
                {
                    if (path == "/storage/open" || path == "/storage/close")
                    {
                        if (path == "/storage/open") PipeClient.SendOpenStorage(); else PipeClient.SendCloseStorage();
                        HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true });
                    }
                    else
                    {
                        var applied = Storage.ApplyTrim(Q("token"));
                        HttpResponse.SendJson(stream, applied["ok"]?.Value<bool>() == true ? 200 : 409, applied);
                    }
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            // compressing clips from the card: what is offered, the status of the run, and the two posts that start and stop it
            if (path == "/storage/compress-candidates" || path == "/storage/compress-status")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                try { HttpResponse.SendJson(stream, 200, path == "/storage/compress-status" ? Storage.GetCompressPayload() : Storage.GetCompressCandidates(QFlag("favorites"), int.TryParse(Q("limit"), out int offerLimit) ? offerLimit : 0)); }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/storage/compress-start" || path == "/storage/compress-cancel")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try
                {
                    if (path == "/storage/compress-cancel") HttpResponse.SendJson(stream, 200, Storage.CancelCompress());
                    else
                    {
                        JArray names = string.IsNullOrWhiteSpace(req.Body) ? null : JObject.Parse(req.Body)["names"] as JArray;
                        var started = Storage.StartCompress(names);
                        HttpResponse.SendJson(stream, started["ok"]?.Value<bool>() == true ? 200 : 409, started);
                    }
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/storage/trim-plan")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                try { HttpResponse.SendJson(stream, 200, Storage.GetTrimPlanPayload()); }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/open-clips-window")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                PipeClient.SendOpenClips();
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true });
                return false;
            }

            if (path == "/open-settings-window")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                // the tab to land on; anything that is not a short lowercase word is dropped, since the plugin puts it into a script for the shell page
                string settingsPage = Q("page");
                if (!Regex.IsMatch(settingsPage, @"^[a-z]{1,20}$")) settingsPage = "";
                PipeClient.SendOpenSettings(settingsPage);
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true });
                return false;
            }

            if (path == "/open-setup-window")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                lock (Server.State.UpdateCheckLock) Server.State.SetupWizardOpened = true;
                PipeClient.SendOpenSetup();
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true });
                return false;
            }

            if (path == "/status") { HttpResponse.SendJson(stream, 200, UploadState.GetUploadStatusSnapshot()); return false; }

            if (path == "/clips/state")
            {
                try
                {
                    if (req.Method == "GET") { HttpResponse.SendJson(stream, 200, Clips.GetClipUiStatePayload()); return false; }
                    if (req.Method == "POST") { HttpResponse.SendJson(stream, 200, Clips.SaveClipUiStateFromJson(req.Body)); return false; }
                    HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET or POST required");
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            // the unsaved edits of the clip editor, one per clip: the list for the drafts tab, one draft for the editor to put back, and the two ways to change them
            if (path == "/drafts" || path.StartsWith("/drafts/"))
            {
                try
                {
                    if (path == "/drafts" && req.Method == "GET") { HttpResponse.SendJson(stream, 200, EditDrafts.List()); return false; }
                    if (path == "/drafts/get" && req.Method == "GET")
                    {
                        var got = EditDrafts.Get(Q("file"));
                        HttpResponse.SendJson(stream, got.Value<bool>("ok") ? 200 : 404, got);
                        return false;
                    }
                    if (path == "/drafts/save" && req.Method == "POST")
                    {
                        JObject edit;
                        try { edit = JObject.Parse(req.Body); }
                        catch (Newtonsoft.Json.JsonException) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Bad edit" }); return false; }
                        var saved = EditDrafts.Save(Q("file"), edit);
                        HttpResponse.SendJson(stream, saved.Value<bool>("ok") ? 200 : 400, saved);
                        return false;
                    }
                    if (path == "/drafts/delete" && req.Method == "POST")
                    {
                        var removed = EditDrafts.Delete(Q("file"));
                        HttpResponse.SendJson(stream, removed.Value<bool>("ok") ? 200 : 400, removed);
                        return false;
                    }
                    HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET or POST required");
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/clips/by-name")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try
                {
                    string sort = Q("sort", "newest");
                    var names = new List<string>();
                    if (!string.IsNullOrWhiteSpace(req.Body))
                    {
                        var incoming = JObject.Parse(req.Body);
                        if (incoming["names"] is JArray namesArr)
                            foreach (var n in namesArr) names.Add(n.Value<string>());
                    }
                    HttpResponse.SendText(stream, 200, "OK", Clips.GetClipsByNameJson(names, sort), "application/json; charset=utf-8");
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 400, new JObject { ["error"] = ex.Message }); }
                return false;
            }

            if (path == "/clips")
            {
                try
                {
                    if (QFlag("refresh")) AppConfig.ClearClipsCache();
                    if (query.ContainsKey("offset") || query.ContainsKey("limit"))
                    {
                        int offset = 0;
                        int limit = Constants.CLIPS_PAGE_LIMIT_MAX;
                        string sort = Q("sort", "newest");
                        if (query.ContainsKey("offset")) int.TryParse(Q("offset"), out offset);
                        if (query.ContainsKey("limit")) int.TryParse(Q("limit"), out limit);
                        HttpResponse.SendText(stream, 200, "OK", Clips.GetClipsPageJson(offset, limit, sort), "application/json; charset=utf-8");
                    }
                    else
                    {
                        HttpResponse.SendText(stream, 200, "OK", Clips.GetClipsListJson(), "application/json; charset=utf-8");
                    }
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["error"] = ex.Message }); }
                return false;
            }

            if (path == "/capabilities")
            {
                var caps = Compression.GetToolPaths();
                HttpResponse.SendJson(stream, 200, new JObject
                {
                    ["ok"] = true,
                    ["ffmpeg"] = !string.IsNullOrEmpty(caps["ffmpeg"]?.Value<string>()),
                    ["ffprobe"] = !string.IsNullOrEmpty(caps["ffprobe"]?.Value<string>()),
                    // the switch can change while the capabilities are kept, so it is read as it is now
                    ["logging"] = Server.State.LogEnabled,
                });
                return false;
            }

            if (path == "/upload-latest")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try
                {
                    var result = Upload.StartStreamableUpload(null);
                    Log.Write("/upload-latest -> " + result.ToString(Newtonsoft.Json.Formatting.None));
                    int code = result["ok"]?.Value<bool>() == true ? 200 : result["busy"]?.Value<bool>() == true ? 409 : 400;
                    HttpResponse.SendJson(stream, code, result);
                }
                catch (Exception ex)
                {
                    Log.Write("/upload-latest threw: " + ex.Message + "\n" + ex.StackTrace);
                    HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = "Helper error: " + ex.Message });
                }
                return false;
            }

            if (path == "/save-replay")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try
                {
                    var result = ObsWebSocket.SaveReplayBuffer();
                    int code = result.Ok ? 200 : result.Unavailable ? 503 : 409;
                    HttpResponse.SendJson(stream, code, ObsResultToJson(result));
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            // one poll for the merged controls dock: share-preview state (from the settings file, no monitor probe) plus the live replay-buffer / recording output state.
            if (path == "/controls-state")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                try
                {
                    var s = ReplaykitSettings.ReadSettings();
                    bool spAvailable = ReplaykitSettings.TestDiscordScreenshareEnabled(s);
                    bool spEnabled = spAvailable
                        && s["discord_output_mode"]?.Value<string>() == "projector"
                        && (s["discord_projector_enabled"]?.Value<bool>() ?? false);

                    var rbRes = ObsWebSocket.InvokeRequest("GetReplayBufferStatus");
                    var recRes = ObsWebSocket.InvokeRequest("GetRecordStatus");
                    // mute state of the three inputs the Settings > Audio switches drive; null when an input is missing from the scene
                    var micMute = ObsWebSocket.InvokeRequest("GetInputMute", new JObject { ["inputName"] = ReplaykitSettings.MicInputName }, 1500);
                    var desktopMute = ObsWebSocket.InvokeRequest("GetInputMute", new JObject { ["inputName"] = ReplaykitSettings.DesktopAudioInputName }, 1500);
                    var discordMute = ObsWebSocket.InvokeRequest("GetInputMute", new JObject { ["inputName"] = ReplaykitSettings.DiscordAudioInputName }, 1500);
                    HttpResponse.SendJson(stream, 200, new JObject
                    {
                        ["micMuted"] = micMute.Ok ? micMute.Data?["inputMuted"]?.Value<bool>() : (bool?)null,
                        ["desktopMuted"] = desktopMute.Ok ? desktopMute.Data?["inputMuted"]?.Value<bool>() : (bool?)null,
                        ["discordMuted"] = discordMute.Ok ? discordMute.Data?["inputMuted"]?.Value<bool>() : (bool?)null,
                        ["ok"] = true,
                        ["sharePreviewAvailable"] = spAvailable,
                        ["sharePreviewEnabled"] = spEnabled,
                        ["replayBufferActive"] = rbRes.Ok ? rbRes.Data?["outputActive"]?.Value<bool>() : (bool?)null,
                        ["recordActive"] = recRes.Ok ? recRes.Data?["outputActive"]?.Value<bool>() : (bool?)null,
                        ["recordMs"] = recRes.Ok ? recRes.Data?["outputDuration"]?.Value<long?>() : (long?)null,
                        ["obsConnected"] = rbRes.Ok || recRes.Ok,
                    });
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            // what the home page shows besides live state: clip count and size, this weeks per-day counts, and the room left on the clips drive.
            if (path == "/home/summary")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                try { HttpResponse.SendJson(stream, 200, Clips.GetHomeSummaryPayload()); }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            // the shell page (the one browser the clean layout keeps inside the obs window) says which view it now shows, so the plugin lifts it above the preview only once the right page is really on screen.
            if (path == "/shell/shown")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string view = Q("view");
                if (view != "home" && view != "clips" && view != "settings" && view != "setup") { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Unknown view." }); return false; }
                PipeClient.SendViewShown(view);
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true });
                return false;
            }

            // the shell page answering a ping of the plugin, with the number the ping carried; the plugin reloads a page that stays silent, since a page whose renderer died stays blank until the browser is reloaded
            if (path == "/shell/ping")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                if (!int.TryParse(Q("n"), NumberStyles.None, CultureInfo.InvariantCulture, out int pingNumber) || pingNumber < 1) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Expected n." }); return false; }
                PipeClient.SendShellPong(pingNumber);
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true });
                return false;
            }

            // the settings page reports unsaved edits, and later its answer to the unsaved-changes prompt (save or leave) with the view that was picked
            if (path == "/shell/settings-dirty" || path == "/shell/leave-settings")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                if (path == "/shell/settings-dirty")
                {
                    string dirtyFlag = Q("dirty");
                    if (dirtyFlag != "0" && dirtyFlag != "1") { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Expected dirty 0 or 1." }); return false; }
                    PipeClient.SendSettingsDirty(dirtyFlag == "1");
                }
                else
                {
                    string target = Q("to");
                    if (target != "home" && target != "clips" && target != "setup" && target != "projector") { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Unknown view." }); return false; }
                    PipeClient.SendSettingsLeave(target);
                }
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true });
                return false;
            }

            // a page of the shell saw alt + an arrow or a side button of the mouse; the plugin keeps the history of the views, so the page only says which way
            if (path == "/shell/nav")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string direction = Q("dir");
                if (direction != "back" && direction != "forward") { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Expected dir back or forward." }); return false; }
                PipeClient.SendShellNav(direction);
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true });
                return false;
            }

            // the clips page says a clip is playing, or that it stopped, so the display stays on for as long as one plays; PlaybackWake holds the windows request and lets a lease lapse on its own if the page dies
            if (path == "/power/playing")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string source = Q("src");
                string playing = Q("state");
                if (!Regex.IsMatch(source, "^[A-Za-z0-9_-]{1,40}$") || (playing != "1" && playing != "0"))
                {
                    HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Expected src and state (1 or 0)." });
                    return false;
                }
                bool holding = PlaybackWake.Report(source, playing == "1");
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["holding"] = holding });
                return false;
            }

            // start or stop clipping: toggles the obs replay buffer, or drives it to the state a state query names, which is what the queued clipping switch in Settings sends so that applying it twice, or after obs got there already, cannot undo it
            if (path == "/obs/replay-buffer")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string want = Q("state");
                if (want != "" && want != "on" && want != "off") { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "state must be on or off." }); return false; }
                try
                {
                    var status = ObsWebSocket.InvokeRequest("GetReplayBufferStatus");
                    bool active = status.Ok && (status.Data?["outputActive"]?.Value<bool>() ?? false);
                    if (want != "" && status.Ok && active == (want == "on"))
                    {
                        HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["active"] = active });
                        return false;
                    }
                    if (want == "on" || (want == "" && status.Ok && !active)) PipeClient.ReleaseObsOutputsAndWait(1500);
                    var res = ObsWebSocket.InvokeRequest(want == "on" ? "StartReplayBuffer" : want == "off" ? "StopReplayBuffer" : "ToggleReplayBuffer");
                    var body = ObsResultToJson(res);
                    if (res.Ok && want != "")
                    {
                        // the request only asks obs, which starts or stops its output a moment later: the answer waits for obs to get there and says what obs really did, so a page never shows a state that is still on its way
                        bool rbWantOn = want == "on";
                        bool rbObserved = rbWantOn;
                        bool rbReached = false;
                        var rbUntil = DateTime.UtcNow.AddMilliseconds(3000);
                        while (true)
                        {
                            var rbNow = ObsWebSocket.InvokeRequest("GetReplayBufferStatus");
                            if (rbNow.Ok) rbObserved = rbNow.Data?["outputActive"]?.Value<bool>() ?? rbObserved;
                            if (rbNow.Ok && rbObserved == rbWantOn) { rbReached = true; break; }
                            if (DateTime.UtcNow >= rbUntil) break;
                            Thread.Sleep(60);
                        }
                        body["active"] = rbObserved;
                        if (!rbReached)
                        {
                            body["ok"] = false;
                            body["message"] = "OBS did not " + (rbWantOn ? "start" : "stop") + " the replay buffer in time.";
                        }
                        HttpResponse.SendJson(stream, rbReached ? 200 : 409, body);
                        return false;
                    }
                    if (res.Ok) body["active"] = res.Data?["outputActive"]?.Value<bool>() ?? false;
                    HttpResponse.SendJson(stream, res.Ok ? 200 : res.Unavailable ? 503 : 409, body);
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            // "Start / Stop Recording" in the dock -- toggles OBS recording.
            if (path == "/obs/record")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try
                {
                    var recordStatus = ObsWebSocket.InvokeRequest("GetRecordStatus");
                    if (recordStatus.Ok && !(recordStatus.Data?["outputActive"]?.Value<bool>() ?? false)) PipeClient.ReleaseObsOutputsAndWait(1500);
                    var res = ObsWebSocket.InvokeRequest("ToggleRecord");
                    var body = ObsResultToJson(res);
                    if (res.Ok) body["active"] = res.Data?["outputActive"]?.Value<bool>() ?? false;
                    HttpResponse.SendJson(stream, res.Ok ? 200 : res.Unavailable ? 503 : 409, body);
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            // mute switches in Settings > Audio. the page names one of three fixed inputs, never an obs input name of its own, so it cannot mute arbitrary sources.
            if (path == "/audio-mute")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try
                {
                    var body = JObject.Parse(string.IsNullOrWhiteSpace(req.Body) ? "{}" : req.Body);
                    string which = body["input"]?.Value<string>();
                    string inputName = which == "mic" ? ReplaykitSettings.MicInputName : which == "desktop" ? ReplaykitSettings.DesktopAudioInputName : which == "discord" ? ReplaykitSettings.DiscordAudioInputName : null;
                    if (inputName == null || body["muted"]?.Type != JTokenType.Boolean)
                    {
                        HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Expected input (mic, desktop or discord) and muted (true or false)." });
                        return false;
                    }
                    var res = ObsWebSocket.InvokeRequest("SetInputMute", new JObject { ["inputName"] = inputName, ["inputMuted"] = body["muted"].Value<bool>() }, 3000);
                    HttpResponse.SendJson(stream, res.Ok ? 200 : res.Unavailable ? 503 : 409, ObsResultToJson(res));
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            // the programs that play sound, for the recording audio tab: the list with each choice, and the icon of one of them
            if (path == "/audio/apps")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                try
                {
                    var current = ReplaykitSettings.ReadSettings();
                    string mode = current["recordingAudioMode"]?.Value<string>() == "specific" ? "specific" : "all";
                    HttpResponse.SendJson(stream, 200, AudioApps.GetAppsPayload(mode, current["recordingAudioApps"] as JObject));
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/audio/app-icon")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                byte[] png = null;
                try { png = AudioApps.GetIconPng(Q("exe")); }
                catch (Exception ex) { Log.Write("/audio/app-icon: " + ex.Message); }
                if (png == null) { HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false }); return false; }
                var iconHeaders = HttpResponse.GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = "image/png" });
                HttpResponse.SendBytes(stream, 200, "OK", iconHeaders, png);
                return false;
            }

            // "OBS Studio Settings" in the dock -- the plugin triggers OBSs own Settings dialog.
            if (path == "/open-obs-settings")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                PipeClient.SendOpenObsSettings();
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true });
                return false;
            }

            // the query of the settings search, shared by the search box of the replaykit settings and the one of the obs settings
            if (path == "/settings/search")
            {
                if (req.Method == "POST") SettingsSearch.Set(Q("q"));
                else if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET or POST required"); return false; }
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["q"] = SettingsSearch.Get() });
                return false;
            }

            // the local command channel of sdk mode: the list of commands, running one, and the answer of the plugin to one it was sent. nothing of it answers while development mode and sdk mode are not both on
            if (path == "/sdk" || path == "/sdk/run" || path == "/sdk/reply")
            {
                if (!Sdk.Enabled()) { HttpResponse.SendJson(stream, 403, new JObject { ["ok"] = false, ["message"] = "SDK mode is off." }); return false; }
                if (path == "/sdk")
                {
                    if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                    HttpResponse.SendJson(stream, 200, Sdk.List());
                    return false;
                }
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                if (path == "/sdk/reply")
                {
                    bool taken = int.TryParse(Q("id"), out int replyId) && Sdk.Answer(replyId, req.Body);
                    HttpResponse.SendJson(stream, taken ? 200 : 404, new JObject { ["ok"] = taken });
                    return false;
                }
                JObject call;
                try { call = string.IsNullOrWhiteSpace(req.Body) ? new JObject() : JObject.Parse(req.Body); }
                catch (Newtonsoft.Json.JsonException) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Bad json." }); return false; }
                HttpResponse.SendJson(stream, 200, Sdk.Execute(call["command"]?.ToString(), call["args"] as JObject));
                return false;
            }

            // the menus of the obs menu bar, opened from the Advanced tab now that the title bar has no menu button
            if (path == "/obs-menu")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                if (!int.TryParse(Q("index", ""), out int menuIndex) || menuIndex < 0 || menuIndex > 15) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Bad menu." }); return false; }
                PipeClient.SendObsMenu(menuIndex);
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true });
                return false;
            }

            if (path == "/upload")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                var selected = Clips.GetSafeClipPath(query.ContainsKey("file") ? query["file"] : null);
                if (selected == null) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Bad filename" }); return false; }
                try
                {
                    // bulk uploads pass quiet=1 -- one balloon toast + one clipboard write per clip is noise when a whole selection goes at once.
                    var result = Upload.StartStreamableUpload(selected, quiet: QFlag("quiet"));
                    Log.Write("/upload(" + selected.Name + ") -> " + result.ToString(Newtonsoft.Json.Formatting.None));
                    int code = result["ok"]?.Value<bool>() == true ? 200 : result["busy"]?.Value<bool>() == true ? 409 : 400;
                    HttpResponse.SendJson(stream, code, result);
                }
                catch (Exception ex)
                {
                    Log.Write("/upload threw: " + ex.Message + "\n" + ex.StackTrace);
                    HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = "Helper error: " + ex.Message });
                }
                return false;
            }

            if (path == "/internal/clip-ready")
            {
                // the detached transcode poll worker calls this once streamable finishes encoding -- the helper shows the "clip ready" toast (its settings are loaded, so the replaykit/custom icon resolves; the poll workers arent). clipboard was already set by the worker.
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string readyUrl = Q("url");
                if (Regex.IsMatch(readyUrl, @"^https://streamable\.com/[A-Za-z0-9_-]+$"))
                {
                    try { Upload.ShowUploadToast(readyUrl, Q("name"), Q("copied") != "0"); } catch (Exception ex) { Log.Write("/internal/clip-ready toast: " + ex.Message, "upload"); }
                }
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true });
                return false;
            }

            if (path == "/remove-link")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                var selected = Clips.GetSafeClipPath(query.ContainsKey("file") ? query["file"] : null);
                if (selected == null) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Bad filename" }); return false; }
                bool removed = Clips.RemoveLink(selected.Name);
                HttpResponse.SendJson(stream, removed ? 200 : 404, new JObject { ["ok"] = removed });
                return false;
            }

            if (path == "/duplicate-clip")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                var selected = Clips.GetSafeClipPath(query.ContainsKey("file") ? query["file"] : null);
                if (selected == null) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Bad filename" }); return false; }
                var result = Clips.DuplicateClip(selected);
                HttpResponse.SendJson(stream, result["ok"]?.Value<bool>() == true ? 200 : 400, result);
                return false;
            }

            if (path == "/cancel-upload")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try
                {
                    var result = UploadState.CancelActiveUpload(Q("file"), Q("requestId"));
                    int code = result.Ok ? 200 : 409;
                    HttpResponse.SendJson(stream, code, new JObject { ["ok"] = result.Ok, ["message"] = result.Message });
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path.StartsWith("/file/"))
            {
                // only route allowed to keep the connection open past this request -- see HttpResponse.ServePreview.
                return HttpResponse.ServePreview(stream, req, path.Substring(6));
            }

            if (path == "/clip-audio")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                HttpResponse.SendJson(stream, 200, ClipAudioTracks.Describe(Q("name")));
                return false;
            }

            if (path == "/audio-track") return HttpResponse.ServeAudioTrack(stream, req, Q("key"), Q("i"));

            if (path == "/audio-peaks")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                string peaks = ClipAudioTracks.PeaksPathFor(Q("key"), Q("i"));
                if (peaks == null) { HttpResponse.SendText(stream, 404, "Not Found", "Peaks not found"); return false; }
                HttpResponse.SendBytes(stream, 200, "OK", HttpResponse.GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = "application/json; charset=utf-8" }), File.ReadAllBytes(peaks));
                return false;
            }

            // audio the editor brings in from outside the clip: the picker of the helper, the bytes of a drop, how an import stands, and the finished file with its waveform
            if (path == "/import-audio/pick")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                int.TryParse(Q("max"), NumberStyles.None, CultureInfo.InvariantCulture, out int room);
                if (room < 1 || room > 8) room = 8;
                string[] picked = PickAudioFiles();
                if (picked.Length == 0) { HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = false, ["cancelled"] = true }); return false; }
                var started = new JArray();
                foreach (string file in picked.Take(room)) started.Add(AudioImports.BeginFromPath(file));
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["files"] = started, ["skipped"] = picked.Length - started.Count });
                return false;
            }

            if (path == AudioImports.UploadPath)
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                var imported = AudioImports.BeginFromUpload(req, Q("name"), out int importStatus);
                HttpResponse.SendJson(stream, importStatus, imported);
                return false;
            }

            if (path == "/import-audio/status")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                HttpResponse.SendJson(stream, 200, AudioImports.Describe(Q("id")));
                return false;
            }

            if (path == "/import-audio/file") return HttpResponse.ServeAudioFile(stream, req, AudioImports.PathFor(Q("id")));

            if (path == "/import-audio/peaks")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                string importPeaks = AudioImports.PeaksPathFor(Q("id"));
                if (importPeaks == null) { HttpResponse.SendText(stream, 404, "Not Found", "Peaks not found"); return false; }
                HttpResponse.SendBytes(stream, 200, "OK", HttpResponse.GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = "application/json; charset=utf-8" }), File.ReadAllBytes(importPeaks));
                return false;
            }

            // the sound of another clip of the folder, which the library of the editor lays under the clip it put over the video
            if (path == "/import-audio/clip")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                var clip = Clips.GetSafeClipPath(Q("name"));
                if (clip == null || !File.Exists(clip.Full)) { HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false, ["message"] = "That clip was not found" }); return false; }
                HttpResponse.SendJson(stream, 200, AudioImports.BeginFromPath(clip.Full));
                return false;
            }

            // pictures and gifs the editor lays over the video: a shape or a sticker the page drew, files from the picker, and the file itself
            if (path == MediaImports.UploadPath)
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                var made = MediaImports.FromUpload(req, Q("name"), out int mediaStatus);
                HttpResponse.SendJson(stream, mediaStatus, made);
                return false;
            }

            if (path == "/import-media/pick")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string[] pickedMedia = PickFiles(MediaPickerTitle, MediaFilter);
                if (pickedMedia.Length == 0) { HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = false, ["cancelled"] = true }); return false; }
                var added = new JArray();
                foreach (string file in pickedMedia.Take(8)) added.Add(MediaImports.FromPath(file));
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["files"] = added, ["skipped"] = pickedMedia.Length - added.Count });
                return false;
            }

            if (path == "/import-media/file")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                string media = MediaImports.PathFor(Q("id"));
                if (media == null) { HttpResponse.SendText(stream, 404, "Not Found", "Media not found"); return false; }
                var mediaHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = MediaImports.ContentType(media), ["Cache-Control"] = "private, max-age=31536000, immutable" };
                HttpResponse.SendBytes(stream, 200, "OK", mediaHeaders, File.ReadAllBytes(media));
                return false;
            }

            // the film strip of the editor: what it asks for with the name of a clip, and the picture itself, which never changes under its key
            if (path == "/clip-filmstrip")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                HttpResponse.SendJson(stream, 200, ClipFilmstrip.Describe(Q("name")));
                return false;
            }

            if (path == "/film-strip")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                string strip = ClipFilmstrip.PathFor(Q("key"));
                if (strip == null) { HttpResponse.SendText(stream, 404, "Not Found", "Film strip not found"); return false; }
                try
                {
                    var stripHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Cache-Control"] = "public, max-age=31536000, immutable",
                        ["Content-Type"] = "image/jpeg",
                        ["Connection"] = "close",
                    };
                    HttpResponse.SendFile(stream, 200, "OK", stripHeaders, strip);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { HttpResponse.SendText(stream, 404, "Not Found", "Film strip not found"); }
                return false;
            }

            if (path.StartsWith("/thumb/")) { HttpResponse.ServeThumbnail(stream, path.Substring(7)); return false; }

            if (path == "/open-clip-folder")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string root = Path.GetFullPath(AppConfig.GetClipDir());
                if (!Directory.Exists(root)) { HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false, ["message"] = "Clip folder not found" }); return false; }
                try
                {
                    // dedupe: if explorer already has the clip folder open, raise it instead of spawning another window. falls thru to the normal launch path if the com probe fails or no match is found.
                    var existing = GetExplorerHwndForPath(root);
                    if (existing != IntPtr.Zero)
                    {
                        bool focused2 = Native.FocusHwnd(existing);
                        HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["focused"] = focused2 });
                        return false;
                    }
                    Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = ProcessArgs.Quote(root), UseShellExecute = false })?.Dispose();
                    HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["focused"] = false });
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/open-log-folder")
            {
                // for the Advanced tab "Open log folder" button -- same shape as /open-clip-folder above, just pointed at the log dir instead of the clip dir. created eagerly at helper startup, so this should only 404 if something deleted it out from under a running helper.
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string root = Path.GetFullPath(Constants.LOG_DIR);
                if (!Directory.Exists(root)) { HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false, ["message"] = "Log folder not found" }); return false; }
                try
                {
                    var existing = GetExplorerHwndForPath(root);
                    if (existing != IntPtr.Zero)
                    {
                        bool focused2 = Native.FocusHwnd(existing);
                        HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["focused"] = focused2 });
                        return false;
                    }
                    Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = ProcessArgs.Quote(root), UseShellExecute = false })?.Dispose();
                    HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["focused"] = false });
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/open-folder")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                var selected = Clips.GetSafeClipPath(query.ContainsKey("file") ? query["file"] : null);
                if (selected == null || !File.Exists(selected.Full)) { HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false, ["message"] = "Clip not found" }); return false; }
                try
                {
                    // if explorer already has the containing folder open, raise that window. we deliberately dont try to re-select the file via shell.application -- the com dance is fragile and the user clicking "open file location" from a clip card is usually just trying to surface the folder, not the specific row.
                    string parent = Path.GetDirectoryName(selected.Full);
                    if (!string.IsNullOrEmpty(parent))
                    {
                        var existing = GetExplorerHwndForPath(parent);
                        if (existing != IntPtr.Zero)
                        {
                            bool focused2 = Native.FocusHwnd(existing);
                            HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["focused"] = focused2 });
                            return false;
                        }
                    }
                    // explorer.exe /select,"<path>" opens the containing folder with the file already highlighted. arguments must be one string becuase explorer parses /select,<rest> as a unit.
                    Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = "/select," + ProcessArgs.Quote(selected.Full), UseShellExecute = false })?.Dispose();
                    HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["focused"] = false });
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/delete")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                var deleted = Clips.DeleteClip(query.ContainsKey("file") ? query["file"] : null);
                if (deleted.Status == 200) HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["name"] = deleted.Name });
                else HttpResponse.SendJson(stream, deleted.Status, new JObject { ["ok"] = false, ["message"] = deleted.Message });
                return false;
            }

            if (path == "/compress")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                var selected = Clips.GetSafeClipPath(query.ContainsKey("file") ? query["file"] : null);
                if (selected == null || !File.Exists(selected.Full)) { HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false, ["message"] = "Clip not found" }); return false; }
                try
                {
                    var result = Compression.StartCompressedStreamableUpload(selected);
                    Log.Write("/compress(" + selected.Name + ") -> " + result.ToString(Newtonsoft.Json.Formatting.None));
                    int code = result["ok"]?.Value<bool>() == true ? 200 : result["busy"]?.Value<bool>() == true ? 409 : 400;
                    HttpResponse.SendJson(stream, code, result);
                }
                catch (Exception ex)
                {
                    Log.Write("/compress threw: " + ex.Message + "\n" + ex.StackTrace);
                    HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message });
                }
                return false;
            }

            if (path == "/trim")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string file = Q("file");
                string mode = Q("mode", "copy");
                // precise=1 -> re-encode for frame-accurate cuts. precise=0 (default) -> stream-copy, keyframe-snapped (fast lossless).
                string preciseStr = Q("precise", "0");
                bool precise = preciseStr != "0" && preciseStr.ToLowerInvariant() != "false";
                string removeAudioStr = Q("removeAudio", "0");
                bool removeAudio = removeAudioStr != "0" && removeAudioStr.ToLowerInvariant() != "false";
                double.TryParse(Q("start"), NumberStyles.Float, CultureInfo.InvariantCulture, out double startSec);
                double.TryParse(Q("end"), NumberStyles.Float, CultureInfo.InvariantCulture, out double endSec);
                // the audio edits of the editor come as json in the body; no body means the audio is left as it is
                JToken audioEdit = null;
                JToken videoEdit = null;
                JToken subtitleEdit = null;
                JToken lookEdit = null;
                JToken layerEdit = null;
                if (!string.IsNullOrWhiteSpace(req.Body))
                {
                    try
                    {
                        var editBody = JObject.Parse(req.Body);
                        // a member that is null is the same as one that is not there
                        audioEdit = editBody["audio"] != null && editBody["audio"].Type != JTokenType.Null ? editBody["audio"] : null;
                        videoEdit = editBody["video"] != null && editBody["video"].Type != JTokenType.Null ? editBody["video"] : null;
                        subtitleEdit = editBody["subtitles"] != null && editBody["subtitles"].Type != JTokenType.Null ? editBody["subtitles"] : null;
                        lookEdit = editBody["look"] != null && editBody["look"].Type != JTokenType.Null ? editBody["look"] : null;
                        layerEdit = editBody["layers"] != null && editBody["layers"].Type != JTokenType.Null ? editBody["layers"] : null;
                    }
                    catch (Newtonsoft.Json.JsonException) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Bad audio edit" }); return false; }
                }
                try
                {
                    double videoSpeed = 1;
                    if (!string.IsNullOrEmpty(Q("speed")) && !double.TryParse(Q("speed"), NumberStyles.Float, CultureInfo.InvariantCulture, out videoSpeed))
                    {
                        HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Bad speed" });
                        return false;
                    }
                    var result = Trim.InvokeClipTrim(file, startSec, endSec, mode, precise, removeAudio, audioEdit, videoSpeed, videoEdit, subtitleEdit, lookEdit, layerEdit);
                    HttpResponse.SendJson(stream, result.Ok ? 200 : 400, TrimResultToJson(result));
                }
                catch (Exception ex)
                {
                    Log.Write("/trim threw: " + ex.Message + "\n" + ex.StackTrace);
                    HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message });
                }
                return false;
            }

            if (path == "/trim/keyframes")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                string file = Q("file");
                double.TryParse(Q("duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out double durationSec);
                try
                {
                    var result = Trim.GetClipKeyframeTimes(file, durationSec);
                    int code = result.Ok ? 200 : result.Pending ? 202 : 400;
                    HttpResponse.SendJson(stream, code, KeyframeResultToJson(result));
                }
                catch (Exception ex)
                {
                    Log.Write("/trim/keyframes threw: " + ex.Message + "\n" + ex.StackTrace);
                    HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message, ["keyframes"] = new JArray() });
                }
                return false;
            }

            if (path == "/clipboard")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string file = Q("file");
                try
                {
                    var result = Trim.SetFileClipboard(file);
                    HttpResponse.SendJson(stream, result.Ok ? 200 : 400, TrimResultToJson(result));
                }
                catch (Exception ex)
                {
                    Log.Write("/clipboard threw: " + ex.Message + "\n" + ex.StackTrace);
                    HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message });
                }
                return false;
            }

            if (path == "/compress-overwrite")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string file = Q("file");
                string mode = Q("mode", "smaller");
                int.TryParse(Q("scaleHeight"), out int scaleHeight);
                // compress-history rules (enforced server-side as defense in depth; the dock ui also disables the relevant card): clip already marked slow (libx265 medium) -> reject entirely, its the best mode and re-running anything would just degrade quality without size gain. clip already marked fast (libx264 fast) + fast again requested -> reject, re-fast-compressing produces no meaningful gain. re-running with smaller is allowed (fast -> slow is a valid upgrade path).
                var selected = Clips.GetSafeClipPath(file);
                if (selected == null || !File.Exists(selected.Full)) { HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false, ["message"] = "Clip not found" }); return false; }
                string existingMode = Compression.GetCompressMarker(selected.Full).Mode;
                if (existingMode == "slow")
                {
                    HttpResponse.SendJson(stream, 409, new JObject { ["ok"] = false, ["message"] = "Already at maximum compression (slow mode).", ["alreadyCompressed"] = true, ["existingMode"] = "slow" });
                    return false;
                }
                if (existingMode == "fast" && mode == "fast")
                {
                    HttpResponse.SendJson(stream, 409, new JObject { ["ok"] = false, ["message"] = "Already fast-compressed. Choose Smaller for further reduction.", ["alreadyCompressed"] = true, ["existingMode"] = "fast" });
                    return false;
                }
                try
                {
                    var result = CompressOverwrite.StartCompressOverwriteFile(selected, mode, scaleHeight);
                    Log.Write("/compress-overwrite(" + selected.Name + ", mode=" + mode + ", scaleHeight=" + scaleHeight + ") -> " + result.ToString(Newtonsoft.Json.Formatting.None));
                    int code = result["ok"]?.Value<bool>() == true ? 200 : result["busy"]?.Value<bool>() == true ? 409 : 400;
                    HttpResponse.SendJson(stream, code, result);
                }
                catch (Exception ex)
                {
                    Log.Write("/compress-overwrite threw: " + ex.Message + "\n" + ex.StackTrace);
                    HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message });
                }
                return false;
            }

            if (path == "/rename")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                var selected = Clips.GetSafeClipPath(query.ContainsKey("file") ? query["file"] : null);
                if (selected == null || !File.Exists(selected.Full)) { HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false, ["message"] = "Source clip not found" }); return false; }
                string newName = Clips.GetSafeFilename(query.ContainsKey("to") ? query["to"] : null);
                if (newName == null) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Bad new filename" }); return false; }
                if (newName == selected.Name) { HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["name"] = newName }); return false; }
                string newPath = Path.Combine(AppConfig.GetClipDir(), newName);
                if (File.Exists(newPath)) { HttpResponse.SendJson(stream, 409, new JObject { ["ok"] = false, ["message"] = "A file with that name already exists" }); return false; }
                string requestId = UploadState.NewRequestId();
                using var reservation = JobCoordinator.TryReserve(requestId, selected.Name, selected.Full, "rename", out string busy, newPath);
                if (reservation == null) { HttpResponse.SendJson(stream, 409, new JObject { ["ok"] = false, ["message"] = busy }); return false; }
                try
                {
                    JobCoordinator.Commit(requestId, () => File.Move(selected.Full, newPath));
                    // preserve the streamable url association across the rename so the clip card still shows "copy link" instead of silently going back to "create link".
                    Clips.UpdateDb(db =>
                    {
                        if (db.ContainsKey(selected.Name)) { db[newName] = db[selected.Name]; db.Remove(selected.Name); }
                        return true;
                    });
                    EditDrafts.Rename(selected.Name, newName);
                    HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["name"] = newName });
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/me")
            {
                var a = Server.State.Auth;
                string masked = Constants.GetMaskedIdentity(a.Username);
                HttpResponse.SendJson(stream, 200, new JObject
                {
                    ["signedIn"] = a.SignedIn,
                    ["username"] = "",
                    ["displayName"] = a.SignedIn ? "Signed in" : "",
                    ["maskedUsername"] = a.SignedIn ? masked : "",
                    ["plan"] = a.Plan,
                    ["sizeCap"] = a.SizeCap,
                    ["maxUploadBytes"] = Constants.GetEffectiveUploadCap(),
                    ["maxDurationSec"] = Upload.SubjectToStreamableDurationLimit() ? Constants.STREAMABLE_FREE_MAX_DURATION_SEC : 0,
                    ["retentionDays"] = a.RetentionDays,
                });
                return false;
            }

            if (path == "/logout")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try { AuthCore.StreamableBackgroundLogout(); } catch (Exception ex) { Log.Write("Background logout failed: " + ex.Message); }
                AuthCore.ClearAuth();
                StreamableSignIn.ClearProfile();
                AppConfig.ClearClipsCache();
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true });
                return false;
            }

            if (path == "/import-session")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                var status = QFlag("start") ? StreamableSignIn.OpenOrGetStatus() : StreamableSignIn.GetStatus();
                if (status.Value<bool?>("signedIn") == true) AppConfig.ClearClipsCache();
                status["maxUploadBytes"] = Constants.GetEffectiveUploadCap();
                status["maxDurationSec"] = Upload.SubjectToStreamableDurationLimit() ? Constants.STREAMABLE_FREE_MAX_DURATION_SEC : 0;
                HttpResponse.SendJson(stream, status.Value<bool?>("ok") == true ? 200 : 500, status);
                return false;
            }

            if (path == "/shutdown")
            {
                HttpResponse.SendText(stream, 200, "OK", "bye");
                AppConfig.StopClipFolderWatcher();
                MicTest.Stop();
                Server.State.Shutdown = true;
                return false;
            }

            if (path == "/restart-obs")
            {
                // plain restart -- close and reopen obs, no streamable sign-out, distinct from /restart-obs-clean below which forces a re-login; same relaunch mechanics as that route and the /settings restart-required branch: spawn the detached relauncher (survives this process dying, waits for the obs pid we hand it, then launches fresh), respond, then force-kill obs and set shutdown so our own accept loop exits and this port frees up for the new helper the relaunched obs will spawn.
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string obsPath = GetRunningObsPath();
                if (obsPath == null) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = "Could not locate OBS executable path to relaunch from." }); return false; }
                ArmRelaunchAndCloseObs(obsPath, "/restart-obs");
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["message"] = "OBS is restarting." });
                StopObsForRestart("/restart-obs");
                return false;
            }

            if (path == "/exit-obs")
            {
                // plain graceful exit, no relaunch -- reuses the same StopObsForRestart used by /restart-obs above, so the tray menus Exit button gets the same close-main-window-first-then-force-kill-if-needed behavior as a restart or clicking obss own X button, instead of skipping straight to a force-kill.
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["message"] = "OBS is closing." });
                StopObsForRestart("/exit-obs");
                return false;
            }

            if (path == "/restart-obs-clean")
            {
                // full-cycle "force re-login": note the obs executable path before we kill it, wipe the saved auth state immediately, send the response so the dock can react, then force-close obs64 + every obs-browser-page child. sets shutdown = true so our accept loop exits and the shutdown path runs the cookie cleanup (which needs the file unlocked, which happens once obs is dead), then relaunches obs via the path we saved.
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string obsPath = GetRunningObsPath();
                if (obsPath == null) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = "Could not locate OBS executable path to relaunch from." }); return false; }
                try { AuthCore.ClearAuthBlob(); } catch { }
                try { AuthCore.ClearAuth(); } catch { }
                StreamableSignIn.ClearProfile();
                Server.State.RestartAfterCleanObsPath = obsPath;
                Log.Write("/restart-obs-clean: cleared ReplayKit's Streamable profile and will relaunch from '" + obsPath + "'.");
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["message"] = "OBS will restart with a clean Streamable session." });

                // give the response a moment to flush before we kill obs (otherwise the docks fetch sees a torn connection).
                StopObsForRestart("/restart-obs-clean");
                return false;
            }

            HttpResponse.SendText(stream, 404, "Not Found", "Not found");
            return false;
        }
    }
}

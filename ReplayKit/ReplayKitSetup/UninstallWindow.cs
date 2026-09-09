using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitSetup
{
    // the modern uninstall window: the twin of SetupWindow, hosting uninstall_prompt.html. the removal runs on a
    // worker thread via Cleanup.RunCleanup; progress is pushed to the page as json messages. auto-runs on load
    // (the confirm already happened in the settings dialog), then relaunches OBS unless OBS itself was removed.
    internal sealed class UninstallWindow : IDisposable
    {
        private const string ClassName = "ReplayKitUninstallWindow";
        private const uint WM_APP_DRAIN = 0x8000 + 1;

        private static readonly uint ColBg = (uint)(0x1D | (0x1F << 8) | (0x26 << 16));

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int value, int size);
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33; // 2 = win11 rounded corners

        [StructLayout(LayoutKind.Sequential)] private struct MinMaxPoint { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO { public MinMaxPoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
        private int _outerW, _outerH;

        private readonly bool _keepUserSettings;
        private readonly bool _removeObs;

        private Native.WndProc _wndProcRef;
        private IntPtr _hwnd;
        private IntPtr _brushBg;
        private double _scale = 1.0;

        private CoreWebView2Controller _controller;
        private CoreWebView2 _web;
        private string _html;

        private readonly object _queueLock = new object();
        private readonly Queue<Action> _queue = new Queue<Action>();

        private Thread _worker;
        private volatile bool _running;
        private bool _done;
        public int ExitCode { get; private set; }

        public UninstallWindow(bool keepUserSettings, bool removeObs)
        {
            _keepUserSettings = keepUserSettings;
            _removeObs = removeObs;
        }

        public int RunModal()
        {
            _scale = SafeDpiScale();
            _html = LoadHtml();
            _brushBg = Native.CreateSolidBrush(ColBg);

            _wndProcRef = WndProc;
            IntPtr hInst = Native.GetModuleHandleW(null);
            var wc = new Native.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcRef),
                hInstance = hInst,
                hCursor = Native.LoadCursorW(IntPtr.Zero, (IntPtr)32512),
                hbrBackground = _brushBg,
                lpszClassName = ClassName,
            };
            Native.RegisterClassExW(ref wc);

            int w = S(560), h = S(370);
            _outerW = w; _outerH = h;
            int x = (Native.GetSystemMetrics(0) - w) / 2;
            int y = (Native.GetSystemMetrics(1) - h) / 2;
            // borderless like the update window: keep WS_THICKFRAME (dwm needs it to round the corners) + WS_SYSMENU +
            // WS_MINIMIZEBOX, drop WS_CAPTION, and eat the whole non-client area in WM_NCCALCSIZE so no title bar draws.
            const uint style = 0x00040000 | 0x00080000 | 0x00020000; // WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX
            _hwnd = Native.CreateWindowExW(0, ClassName, "OBS ReplayKit Uninstall", style, x, y, w, h,
                IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("Could not create the uninstall window.");

            ApplyBorderlessChrome();
            SetIconFromExe();
            Native.ShowWindow(_hwnd, 5);
            Native.UpdateWindow(_hwnd);

            SynchronizationContext.SetSynchronizationContext(new QueueSyncContext(this));
            InitWebViewAsync();

            while (Native.GetMessageW(out Native.MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessageW(ref msg);
            }
            return ExitCode;
        }

        private async void InitWebViewAsync()
        {
            try
            {
                string udf = Path.Combine(Path.GetTempPath(), "ReplayKit", "webview2-setup-udf");
                Directory.CreateDirectory(udf);
                var env = await CoreWebView2Environment.CreateAsync(null, udf, new CoreWebView2EnvironmentOptions());
                _controller = await env.CreateCoreWebView2ControllerAsync(_hwnd);
                _web = _controller.CoreWebView2;

                _controller.DefaultBackgroundColor = Color.FromArgb(0x1D, 0x1F, 0x26);
                LayoutController();

                var s = _web.Settings;
                s.AreDefaultContextMenusEnabled = false;
                s.IsZoomControlEnabled = false;
                s.AreDevToolsEnabled = false;
                s.IsStatusBarEnabled = false;
                s.AreBrowserAcceleratorKeysEnabled = false;

                _web.WebMessageReceived += OnWebMessage;
                _web.NavigateToString(_html);
                _controller.IsVisible = true;
            }
            catch (Exception ex)
            {
                WriteErrorLog(ex);
                InstallerApp.ShowFatalError("The uninstall window could not start.\n\n" + ex, null);
                Native.DestroyWindow(_hwnd);
            }
        }

        private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string json;
            try { json = e.TryGetWebMessageAsString(); }
            catch (Exception) { try { json = e.WebMessageAsJson; } catch (Exception) { return; } }
            if (string.IsNullOrEmpty(json)) return;

            // no title bar to grab -- the page posts the bare string "drag" on mousedown over the shell (same as update_prompt.html)
            if (json == "drag")
            {
                Native.ReleaseCapture();
                Native.SendMessageW(_hwnd, 0x00A1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
                return;
            }

            JObject o;
            try { o = JObject.Parse(json); }
            catch (JsonException) { return; }

            switch (o.Value<string>("cmd"))
            {
                case "start":
                    StartUninstall();
                    break;
                case "close":
                    if (!_running || _done) Native.DestroyWindow(_hwnd);
                    break;
            }
        }

        private void StartUninstall()
        {
            if (_running) return;
            _running = true;

            var progress = new BridgeProgress(this);
            _worker = new Thread(() =>
            {
                int code = 1;
                SetupLog.Line("=== uninstall flow start (keepSettings=" + _keepUserSettings + ", removeObs=" + _removeObs + ") ===");
                try
                {
                    Cleanup.RunCleanup(progress, _keepUserSettings, _removeObs);
                    // boot obs back up once everything is removed (nothing to boot if obs itself was uninstalled).
                    if (!_removeObs)
                    {
                        try { Obs.LaunchObs(progress.LogLine); }
                        catch (Exception ex) { progress.LogLine("warn: could not relaunch OBS: " + ex.Message); }
                    }
                    code = 0;
                    SetupLog.Line("=== uninstall flow complete ===");
                    Enqueue(OnFlowSucceeded);
                }
                catch (Exception ex)
                {
                    WriteErrorLog(ex);
                    SetupLog.Line("=== uninstall flow FAILED: " + ex.Message + " ===");
                    Enqueue(() => OnFlowFailed(ex.Message));
                }
                finally
                {
                    ExitCode = code;
                    _running = false;
                }
            })
            {
                IsBackground = true,
                Name = "uninstall-flow",
            };
            _worker.SetApartmentState(ApartmentState.STA); // some steps touch shell / COM apis
            _worker.Start();
        }

        private void OnFlowSucceeded()
        {
            _done = true;
            PostToPage(new JObject { ["type"] = "done", ["removeObs"] = _removeObs });
            // auto-close only when obs is coming back; if obs was removed, leave the "removed" state on screen with a Close button.
            if (!_removeObs) Native.SetTimer(_hwnd, (IntPtr)1, 2600, IntPtr.Zero);
        }

        private void OnFlowFailed(string message)
        {
            _done = true;
            string shown = (message ?? "unknown error") + "  (log: " + SetupLog.Path + ")";
            PostToPage(new JObject { ["type"] = "failed", ["message"] = shown });
        }

        // ---- page <-> host ----

        internal void PostToPage(JObject payload)
        {
            string json = payload.ToString(Formatting.None);
            Enqueue(() => { try { _web?.PostWebMessageAsJson(json); } catch (Exception) { } });
        }

        internal void Enqueue(Action a)
        {
            lock (_queueLock) _queue.Enqueue(a);
            Native.PostMessageW(_hwnd, WM_APP_DRAIN, IntPtr.Zero, IntPtr.Zero);
        }

        private void DrainQueue()
        {
            while (true)
            {
                Action a;
                lock (_queueLock)
                {
                    if (_queue.Count == 0) return;
                    a = _queue.Dequeue();
                }
                try { a(); } catch (Exception) { }
            }
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WM_APP_DRAIN:
                    DrainQueue();
                    return IntPtr.Zero;
                case 0x0083: // WM_NCCALCSIZE -- eat the whole non-client area so no title bar / frame is drawn
                    if (wParam != IntPtr.Zero) return IntPtr.Zero;
                    break;
                case 0x0024: // WM_GETMINMAXINFO -- pin the size so WS_THICKFRAME cannot maximize / aero-snap it
                    if (_outerW > 0)
                    {
                        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                        var p = new MinMaxPoint { X = _outerW, Y = _outerH };
                        mmi.MaxSize = p; mmi.MaxTrackSize = p; mmi.MinTrackSize = p;
                        Marshal.StructureToPtr(mmi, lParam, false);
                        return IntPtr.Zero;
                    }
                    break;
                case 0x0112: // WM_SYSCOMMAND -- swallow maximize / size
                {
                    long sc = wParam.ToInt64() & 0xFFF0;
                    if (sc == 0xF030 || sc == 0xF000) return IntPtr.Zero;
                    break;
                }
                case 0x0005: // WM_SIZE
                    LayoutController();
                    return IntPtr.Zero;
                case 0x0014: // WM_ERASEBKGND
                {
                    Native.GetClientRect(hWnd, out Native.RECT r);
                    Native.FillRect(wParam, ref r, _brushBg);
                    return (IntPtr)1;
                }
                case 0x0113: // WM_TIMER -- close after the "done" pause
                    Native.KillTimer(_hwnd, wParam);
                    Native.DestroyWindow(_hwnd);
                    return IntPtr.Zero;
                case 0x0010: // WM_CLOSE
                    if (_running && !_done) return IntPtr.Zero; // no closing mid-uninstall
                    Native.DestroyWindow(_hwnd);
                    return IntPtr.Zero;
                case 0x0002: // WM_DESTROY
                    try { _controller?.Close(); } catch (Exception) { }
                    Native.PostQuitMessage(ExitCode);
                    return IntPtr.Zero;
            }
            IntPtr res = Native.DefWindowProcW(hWnd, msg, wParam, lParam);
            // WM_NCHITTEST: turn every resize-border hit (HTLEFT..HTBOTTOMRIGHT = 10..17) into client so WS_THICKFRAME
            // cannot be used to edge-drag-resize the window.
            if (msg == 0x0084)
            {
                int hit = res.ToInt32();
                if (hit >= 10 && hit <= 17) return (IntPtr)1;
            }
            return res;
        }

        private void LayoutController()
        {
            if (_controller == null) return;
            try
            {
                Native.GetClientRect(_hwnd, out Native.RECT r);
                _controller.Bounds = new Rectangle(0, 0, Math.Max(0, r.right - r.left), Math.Max(0, r.bottom - r.top));
            }
            catch (Exception) { }
        }

        // ---- helpers ----

        private string LoadHtml()
        {
            var asm = Assembly.GetExecutingAssembly();
            string html;
            using (var stream = asm.GetManifestResourceStream("ReplayKitSetup.uninstall_prompt.html"))
            {
                if (stream == null) throw new InvalidOperationException("uninstall_prompt.html resource is missing from the build.");
                using var reader = new StreamReader(stream, Encoding.UTF8);
                html = reader.ReadToEnd();
            }
            html = html.Replace("{{HERO_ART}}", BundledDataUri("obs-studio/obs-replayKit/obs-custom-dock/update-art.png", "image/png"));
            html = html.Replace("{{BRAND_ICON}}", BundledDataUri("obs-studio/obs-replayKit/scripts/helper/obs-replaykit.ico", "image/x-icon"));
            html = html.Replace("{{UNINSTALL_STATE}}", new JObject { ["removeObs"] = _removeObs }.ToString(Formatting.None));
            return html;
        }

        private static string BundledDataUri(string entryPath, string mime)
        {
            try
            {
                using var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("ReplayKitSetup.assets.bundle.zip");
                if (res == null) return "";
                using var zip = new System.IO.Compression.ZipArchive(res, System.IO.Compression.ZipArchiveMode.Read);
                var entry = zip.GetEntry(entryPath);
                if (entry == null) return "";
                using var es = entry.Open();
                using var ms = new MemoryStream();
                es.CopyTo(ms);
                return "data:" + mime + ";base64," + Convert.ToBase64String(ms.ToArray());
            }
            catch (Exception)
            {
                return "";
            }
        }

        private int S(int px) => (int)Math.Round(px * _scale);

        private static double SafeDpiScale()
        {
            try
            {
                uint dpi = Native.GetDpiForSystem();
                if (dpi >= 72 && dpi <= 480) return dpi / 96.0;
            }
            catch (Exception) { }
            return 1.0;
        }

        // match UpdatePromptWindow.RoundedBorderlessForm: only round the corners via dwm. the immersive-dark-mode + border-colour calls that used to be here made dwm re-establish a standard frame (the title bar came back) with nothing re-running WM_NCCALCSIZE after -- so drop them and force one non-client recalc instead.
        private void ApplyBorderlessChrome()
        {
            try
            {
                int round = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
                // the winforms update window gets this free from the Size it sets in OnLoad; this raw window is created at final size, so ask for the frame recalc explicitly
                Native.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0237); // SWP_NOSIZE|NOMOVE|NOZORDER|NOACTIVATE|FRAMECHANGED|NOOWNERZORDER
            }
            catch (Exception) { }
        }

        private void SetIconFromExe()
        {
            try
            {
                string exe = Environment.ProcessPath ?? "";
                if (string.IsNullOrEmpty(exe)) return;
                IntPtr hIcon = Native.ExtractIconW(Native.GetModuleHandleW(null), exe, 0);
                if (hIcon != IntPtr.Zero && hIcon != (IntPtr)1)
                {
                    Native.SendMessageW(_hwnd, 0x0080, (IntPtr)1, hIcon);
                    Native.SendMessageW(_hwnd, 0x0080, IntPtr.Zero, hIcon);
                }
            }
            catch (Exception) { }
        }

        private static void WriteErrorLog(Exception ex)
        {
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "OBSReplayKit-error.log");
                File.WriteAllText(path, ex.ToString(), new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        public void Dispose()
        {
            if (_brushBg != IntPtr.Zero) Native.DeleteObject(_brushBg);
        }

        private sealed class QueueSyncContext : SynchronizationContext
        {
            private readonly UninstallWindow _w;
            public QueueSyncContext(UninstallWindow w) { _w = w; }
            public override void Post(SendOrPostCallback d, object state) => _w.Enqueue(() => d(state));
            public override void Send(SendOrPostCallback d, object state) => d(state);
            public override SynchronizationContext CreateCopy() => this;
        }

        // IInstallProgress that pushes every callback to uninstall_prompt.html as a json message. arrives on the
        // worker thread; PostToPage marshals to the message thread. same shape as SetupWindow.BridgeProgress.
        private sealed class BridgeProgress : IInstallProgress
        {
            private readonly UninstallWindow _w;
            private int _completed;

            public BridgeProgress(UninstallWindow w) { _w = w; }

            public int TotalSteps { get; set; }
            public List<string> Issues { get; } = new List<string>();

            public void Render(int completed, string title, string detail, string state)
            {
                _completed = completed;
                int total = Math.Max(TotalSteps, 1);
                double pct = 100.0 * Math.Min(completed, total) / total;
                var o = new JObject { ["type"] = "step", ["percent"] = pct, ["state"] = state ?? "", ["title"] = title };
                o["detail"] = detail ?? "";
                _w.PostToPage(o);
                SetupLog.Line($"[{completed}/{total}] {(string.IsNullOrEmpty(state) ? "working" : state)}: {title}"
                              + (string.IsNullOrEmpty(detail) ? "" : " - " + detail));
            }

            private static readonly string[] IssueWords = { "warn", "failed", "missing", "not found", "skipped", "timed out", "permission denied" };

            public void LogLine(string message)
            {
                string text = (message ?? "").Trim();
                if (text.Length == 0) return;
                SetupLog.Line(text);
                _w.PostToPage(new JObject { ["type"] = "log", ["line"] = text });
                string low = text.ToLowerInvariant();
                foreach (var word in IssueWords)
                {
                    if (low.Contains(word)) { AddIssue(text); break; }
                }
            }

            public void AddIssue(string message)
            {
                string cleaned = string.Join(" ", (message ?? "").Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries));
                if (cleaned.Length > 160) cleaned = cleaned.Substring(0, 157) + "...";
                if (cleaned.Length > 0 && !Issues.Contains(cleaned)) Issues.Add(cleaned);
            }

            public void SubProgress(double fraction, string detail)
            {
                int total = Math.Max(TotalSteps, 1);
                double frac = Math.Max(0.0, Math.Min(1.0, fraction));
                double pct = 100.0 * (_completed / (double)total + frac / total);
                var o = new JObject { ["type"] = "step", ["percent"] = pct, ["state"] = "" };
                if (!string.IsNullOrEmpty(detail)) o["detail"] = detail;
                _w.PostToPage(o);
            }
        }
    }
}

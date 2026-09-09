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
    // the modern setup window: a raw win32 window (no WinForms/WPF -- trimming forbids them) hosting a CoreWebView2
    // controller that renders setup_installer.html. the install itself still runs on a worker thread via
    // Apply.RunInstallFlow; progress is pushed to the page as json messages. reuses the Native p/invoke surface from InstallerWindow.cs.
    internal sealed class SetupWindow : IDisposable
    {
        private const string ClassName = "ReplayKitSetupWindow";
        private const uint WM_APP_DRAIN = 0x8000 + 1;

        private static readonly uint ColBg = (uint)(0x1D | (0x1F << 8) | (0x26 << 16));

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int value, int size);
        // 20 = immersive dark mode (19 on pre-20H1), 34 = border colour, 35 = caption colour. COLORREF is 0x00bbggrr.
        private const int DWMWA_DARK = 20, DWMWA_DARK_PRE20H1 = 19, DWMWA_BORDER_COLOR = 34, DWMWA_CAPTION_COLOR = 35;
        // the helper gives its own windows (Clips, Settings) a caption in the theme PANEL colour, a touch lighter than
        // the window body -- not a flat match. mirror that: panel #272A33, border #3C404D (--line).
        private const int CaptionColorRef = 0x00332A27; // #272A33 (--panel)
        private const int BorderColorRef = 0x004D403C;  // #3C404D (--line)

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
            int x = (Native.GetSystemMetrics(0) - w) / 2;
            int y = (Native.GetSystemMetrics(1) - h) / 2;
            // a plain titled window -- WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX, no thickframe/maximize so it stays a
            // fixed installer dialog. dwm themes + rounds the frame on its own.
            const uint style = 0x00C00000 | 0x00080000 | 0x00020000;
            _hwnd = Native.CreateWindowExW(0, ClassName, "OBS ReplayKit Setup", style, x, y, w, h,
                IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("Could not create the setup window.");

            ApplyDarkTitleBar();
            SetIconFromExe();
            Native.ShowWindow(_hwnd, 5);
            Native.UpdateWindow(_hwnd);

            // continuations from the async webview2 setup have to resume on this message thread
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
                InstallerApp.ShowFatalError("The setup window could not start.\n\n" + ex, null);
                Native.DestroyWindow(_hwnd);
            }
        }

        private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            // the page posts JSON.stringify(obj), i.e. a string -- read it as a string (WebMessageAsJson would hand back
            // the value re-quoted and JObject.Parse would throw). same read UpdatePromptWindow.cs uses.
            string json;
            try { json = e.TryGetWebMessageAsString(); }
            catch (Exception) { try { json = e.WebMessageAsJson; } catch (Exception) { return; } }
            if (string.IsNullOrEmpty(json)) return;

            JObject o;
            try { o = JObject.Parse(json); }
            catch (JsonException) { return; }

            switch (o.Value<string>("cmd"))
            {
                case "start":
                    // discord is only sent when the opt-in checkbox was on screen. absent -> leave the setting as loaded
                    // (the live file for an update, the default after a clean re-install wiped it).
                    bool? discord = o["discord"]?.Type == JTokenType.Boolean ? o.Value<bool>("discord") : (bool?)null;
                    StartInstall(discord, o.Value<string>("mode") == "reinstall");
                    break;
                case "close":
                    if (!_running || _done) Native.DestroyWindow(_hwnd);
                    break;
            }
        }

        private void StartInstall(bool? discord, bool cleanInstall)
        {
            if (_running) return;
            _running = true;

            var progress = new BridgeProgress(this);
            _worker = new Thread(() =>
            {
                int code = 1;
                SetupLog.Line("=== install flow start (v" + VersionInfo.Version + (cleanInstall ? ", clean re-install" : "") + ") ===");
                try
                {
                    var issues = Apply.RunInstallFlow(discord, progress, cleanInstall);
                    code = 0;
                    SetupLog.Line("=== install flow complete: " + issues.Count + " issue(s) ===");
                    Enqueue(() => OnFlowSucceeded(issues.Count));
                }
                catch (Exception ex)
                {
                    WriteErrorLog(ex);
                    SetupLog.Line("=== install flow FAILED: " + ex.Message + " ===");
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
                Name = "installer-flow",
            };
            _worker.SetApartmentState(ApartmentState.STA); // some steps touch shell / COM apis
            _worker.Start();
        }

        private void OnFlowSucceeded(int issueCount)
        {
            _done = true;
            PostToPage(new JObject { ["type"] = "done", ["issues"] = issueCount });
            Native.SetTimer(_hwnd, (IntPtr)1, 2400, IntPtr.Zero);
        }

        private void OnFlowFailed(string message)
        {
            _done = true;
            string shown = (message ?? "unknown error") + "  (log: " + SetupLog.Path + ")";
            PostToPage(new JObject { ["type"] = "failed", ["message"] = shown });
        }

        // ---- page <-> host ----

        // JObject, not an anonymous type: the app assembly is trimmed and Newtonsoft's reflection serializer cannot keep
        // an anon type's getters alive, so JsonConvert.SerializeObject(new { ... }) can come back as "{}".
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
                case 0x0005: // WM_SIZE
                    LayoutController();
                    return IntPtr.Zero;
                case 0x0014: // WM_ERASEBKGND -- paint our bg so there is no white flash before the webview shows
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
                    if (_running && !_done) return IntPtr.Zero; // no closing mid-install
                    Native.DestroyWindow(_hwnd);
                    return IntPtr.Zero;
                case 0x0002: // WM_DESTROY
                    try { _controller?.Close(); } catch (Exception) { }
                    Native.PostQuitMessage(ExitCode);
                    return IntPtr.Zero;
            }
            return Native.DefWindowProcW(hWnd, msg, wParam, lParam);
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

        private static string LoadHtml()
        {
            var asm = Assembly.GetExecutingAssembly();
            string html;
            using (var stream = asm.GetManifestResourceStream("ReplayKitSetup.setup_installer.html"))
            {
                if (stream == null) throw new InvalidOperationException("setup_installer.html resource is missing from the build.");
                using var reader = new StreamReader(stream, Encoding.UTF8);
                html = reader.ReadToEnd();
            }
            // the hero art + brand icon are the same files the update window fetches over http; there is no helper here,
            // so pull them straight out of the embedded asset bundle and inline them as data uris.
            html = html.Replace("{{HERO_ART}}", BundledDataUri("obs-studio/obs-replayKit/obs-custom-dock/update-art.png", "image/png"));
            html = html.Replace("{{BRAND_ICON}}", BundledDataUri("obs-studio/obs-replayKit/scripts/helper/obs-replaykit.ico", "image/x-icon"));
            html = html.Replace("{{SETUP_STATE}}", DetectSetupState());
            return html;
        }

        // {installed, cable, screenshare} -- lets the page show Update / Re-Install instead of Install and pre-check the
        // Discord opt-in when the audio cable is already on the machine (or the current settings file has it on).
        private static string DetectSetupState()
        {
            bool installed = false, screenshare = false;
            try { installed = Directory.Exists(Config.REPLAYKIT_CONFIG); } catch (Exception) { }
            bool cable = VbCable.IsVbcableInstalled();
            try
            {
                if (File.Exists(Prefs.RUNTIME_SETTINGS_FILE))
                    screenshare = JObject.Parse(File.ReadAllText(Prefs.RUNTIME_SETTINGS_FILE))
                        .Value<bool?>("discord_screenshare_enabled") ?? false;
            }
            catch (Exception) { }
            return new JObject { ["installed"] = installed, ["cable"] = cable, ["screenshare"] = screenshare }.ToString(Formatting.None);
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

        // dark caption + themed caption/border colour, same treatment the helper gives its own windows -- otherwise
        // win11 draws a plain light title bar on top of the dark webview content.
        private void ApplyDarkTitleBar()
        {
            try
            {
                int on = 1;
                DwmSetWindowAttribute(_hwnd, DWMWA_DARK, ref on, sizeof(int));
                DwmSetWindowAttribute(_hwnd, DWMWA_DARK_PRE20H1, ref on, sizeof(int));
                int caption = CaptionColorRef;
                DwmSetWindowAttribute(_hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
                int border = BorderColorRef;
                DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
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

        // resumes awaited continuations from InitWebViewAsync on the window message thread via the same queue the
        // worker->ui path uses.
        private sealed class QueueSyncContext : SynchronizationContext
        {
            private readonly SetupWindow _w;
            public QueueSyncContext(SetupWindow w) { _w = w; }
            public override void Post(SendOrPostCallback d, object state) => _w.Enqueue(() => d(state));
            public override void Send(SendOrPostCallback d, object state) => d(state);
            public override SynchronizationContext CreateCopy() => this;
        }

        // IInstallProgress that pushes every callback to setup_installer.html as a json message. arrives on the worker
        // thread; PostToPage marshals to the message thread.
        private sealed class BridgeProgress : IInstallProgress
        {
            private readonly SetupWindow _w;
            private int _completed;

            public BridgeProgress(SetupWindow w) { _w = w; }

            public int TotalSteps { get; set; }
            public List<string> Issues { get; } = new List<string>();

            public void Render(int completed, string title, string detail, string state)
            {
                _completed = completed;
                int total = Math.Max(TotalSteps, 1);
                double pct = 100.0 * Math.Min(completed, total) / total;
                string text = $"Step {Math.Min(completed + 1, total)} of {total}  -  {title}";
                var o = new JObject { ["type"] = "step", ["text"] = text, ["percent"] = pct, ["state"] = state ?? "", ["title"] = title };
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

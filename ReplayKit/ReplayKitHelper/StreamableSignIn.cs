using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // Owns the only browser profile ReplayKit may use for Streamable authentication.
    // It never inspects another application's cookies, encryption keys, or databases.
    internal static class StreamableSignIn
    {
        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

        // win10 1607+; ReplayKitHelper carries no dpi declaration in its manifest (app.manifest), so windows treats
        // it as system-dpi-aware and virtualises coordinates for every monitor that isnt running the system scale
        // factor -- the same mismatch already fixed for the desktop colour picker (DesktopColorPicker.cs). without
        // this, Screen.WorkingArea and the resulting Form.Location land in the wrong coordinate space on a mixed-dpi
        // multi-monitor rig, which is consistent with the window ending up wherever the user isnt looking.
        private static readonly IntPtr PerMonitorAwareV2 = new IntPtr(-4);

        private static readonly object Gate = new object();
        private static bool _windowOpen;
        private static string _lastError = "";
        private static IntPtr _formHandle = IntPtr.Zero;
        private static readonly string ProfileDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OBS ReplayKit", "StreamableProfile");

        public static JObject OpenOrGetStatus()
        {
            lock (Gate)
            {
                if (!_windowOpen)
                {
                    _windowOpen = true;
                    _lastError = "";
                    var thread = new Thread(RunWindow) { IsBackground = true, Name = "ReplayKit Streamable sign-in" };
                    thread.SetApartmentState(ApartmentState.STA);
                    thread.Start();
                }
                else
                {
                    // already open -- bring it forward instead of silently doing nothing, so clicking the dock
                    // button again when the window is just buried behind something actually does what it looks
                    // like it should. FocusHwnd is a plain hwnd operation (restore-if-minimized + the thread-input-
                    // attach dance for reliable foreground activation), safe to call from any thread or process.
                    IntPtr hWnd = _formHandle;
                    if (hWnd != IntPtr.Zero) { try { Native.FocusHwnd(hWnd); } catch (Exception ex) { Log.Write("focus existing sign-in window: " + ex.Message); } }
                }
                return StatusLocked();
            }
        }

        public static JObject GetStatus()
        {
            lock (Gate) { return StatusLocked(); }
        }

        public static void ClearProfile()
        {
            lock (Gate)
            {
                if (_windowOpen) return;
            }
            try { if (Directory.Exists(ProfileDirectory)) Directory.Delete(ProfileDirectory, true); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Log.Write("Clear Streamable profile: " + ex.Message); }
        }

        private static JObject StatusLocked()
        {
            lock (Server.State.AuthLock)
            {
                if (Server.State.Auth.SignedIn)
                {
                    return new JObject
                    {
                        ["ok"] = true,
                        ["signedIn"] = true,
                        ["username"] = "",
                        ["displayName"] = "Signed in",
                        ["maskedUsername"] = Constants.GetMaskedIdentity(Server.State.Auth.Username),
                        ["plan"] = Server.State.Auth.Plan,
                        ["sizeCap"] = Server.State.Auth.SizeCap,
                        ["retentionDays"] = Server.State.Auth.RetentionDays,
                    };
                }
            }
            return new JObject
            {
                ["ok"] = string.IsNullOrEmpty(_lastError),
                ["pending"] = _windowOpen,
                ["signedIn"] = false,
                ["message"] = _lastError,
            };
        }

        private static void RunWindow()
        {
            try
            {
                // set once for this threads whole lifetime -- the thread exists only to host this one window and
                // exits when it closes, so theres nothing afterward that needs the system-dpi behaviour restored.
                try { SetThreadDpiAwarenessContext(PerMonitorAwareV2); } catch (EntryPointNotFoundException) { } catch (DllNotFoundException) { }

                Directory.CreateDirectory(ProfileDirectory);
                Application.EnableVisualStyles();
                using (var form = new Form { Text = "Sign in to Streamable", Width = 560, Height = 760, StartPosition = FormStartPosition.Manual, TopMost = true })
                using (var browser = new WebView2 { Dock = DockStyle.Fill })
                // one-shot watchdog: if the form is somehow still not visible a moment after Shown, force it. this
                // is a self-healing backstop, not the primary fix -- it exists because a hidden top-level window with
                // no error logged anywhere is otherwise undiagnosable from the outside.
                using (var visibilityWatchdog = new System.Windows.Forms.Timer { Interval = 800 })
                // streamable's login does not reliably raise NavigationCompleted -- a normal email/password submit
                // is an xhr/fetch call that sets the session cookie, followed by a client-side route change
                // (history.pushState) to the dashboard, never a full page navigation. HistoryChanged below catches
                // that specific case, but rather than track every mechanism a frontend redesign might use next,
                // this timer just re-checks on a plain interval for as long as the window is open -- it is the one
                // check that cannot miss regardless of how the page gets the cookie set. CheckSession below is a
                // no-op when there is nothing new to capture, so polling it costs nothing extra.
                using (var sessionPoll = new System.Windows.Forms.Timer { Interval = 1500 })
                {
                    var workArea = ResolveTargetScreen().WorkingArea;
                    form.Location = new System.Drawing.Point(
                        workArea.Left + (workArea.Width - form.Width) / 2,
                        workArea.Top + (workArea.Height - form.Height) / 2);
                    form.Controls.Add(browser);

                    // last streamable-domain cookie jar seen, so the poll timer below can skip the network round
                    // trip (InvokeStreamableMe shells out to curl.exe) when nothing has actually changed since the
                    // previous check -- GetCookiesAsync itself is a cheap local call, so checking it every tick is
                    // fine, but hitting streamable's real api that often for the whole time the window sits open is
                    // not. a local function (not the previous static method) so it can close over this without a
                    // field that would need resetting between separate sign-in attempts.
                    string lastCheckedJar = null;
                    async System.Threading.Tasks.Task CheckSession()
                    {
                        try
                        {
                            var cookies = await browser.CoreWebView2.CookieManager.GetCookiesAsync("https://streamable.com");
                            if (cookies == null || cookies.Count == 0) return;
                            string jar = ToCookieJar(cookies);
                            if (string.IsNullOrEmpty(jar) || jar == lastCheckedJar) return;
                            lastCheckedJar = jar;
                            JObject user = AuthCore.InvokeStreamableMe(jar);
                            if (!IsAuthenticatedUser(user))
                            {
                                Log.Write("Streamable sign-in: cookie jar changed but /me is still not authenticated (" + cookies.Count + " streamable cookie(s)).");
                                return;
                            }
                            AuthCore.SaveAuthBlob(jar, user);
                            AuthCore.ApplyAuth(user, jar);
                            Log.Write("Saved Streamable session from ReplayKit's owned WebView2 profile.");
                            form.BeginInvoke((Action)(() => form.Close()));
                        }
                        catch (Exception ex)
                        {
                            Log.Write("Streamable sign-in session check: " + ex.Message);
                        }
                    }

                    form.Shown += async (sender, args) =>
                    {
                        try
                        {
                            form.Show();
                            _formHandle = form.Handle;
                            // plain SetForegroundWindow from here reliably lost to windows' anti-focus-stealing
                            // heuristic -- this thread never received the input event that triggered it (the click
                            // happened in obs's dock, over http, not in this process), which is exactly the case
                            // that heuristic exists to block. Native.FocusHwnd does the attach-thread-input dance
                            // that actually works around it; its the same call already used to bring an existing
                            // window forward on a repeat click, so both paths now behave the same way.
                            Native.FocusHwnd(form.Handle);
                            ApplyTheme(form.Handle);
                            var environment = await CoreWebView2Environment.CreateAsync(null, ProfileDirectory);
                            await browser.EnsureCoreWebView2Async(environment);
                            browser.CoreWebView2.NavigationCompleted += async (s, e) => await CheckSession();
                            // catches a client-side route change (history.pushState/replaceState) after login,
                            // which does not raise NavigationCompleted at all -- see the sessionPoll comment above
                            // for why the interval timer below is still kept as a backstop alongside this.
                            browser.CoreWebView2.HistoryChanged += async (s, e) => await CheckSession();
                            browser.CoreWebView2.Navigate("https://streamable.com/login");
                            sessionPoll.Start();
                        }
                        catch (Exception ex)
                        {
                            SetError("Could not open the secure Streamable sign-in window: " + ex.Message);
                            form.Close();
                        }
                    };
                    visibilityWatchdog.Tick += (sender, args) =>
                    {
                        visibilityWatchdog.Stop();
                        if (form.IsDisposed || form.Visible) return;
                        Log.Write("Streamable sign-in window was not visible after Shown; forcing it.");
                        form.Show();
                        form.WindowState = FormWindowState.Normal;
                        Native.FocusHwnd(form.Handle);
                    };
                    sessionPoll.Tick += async (sender, args) => await CheckSession();
                    form.Shown += (sender, args) => visibilityWatchdog.Start();
                    form.FormClosed += (sender, args) => { sessionPoll.Stop(); SetClosed(); };
                    Application.Run(form);
                }
            }
            catch (Exception ex)
            {
                SetError("Streamable sign-in failed: " + ex.Message);
                SetClosed();
            }
        }

        // the monitor to center the sign-in window on. GetForegroundWindow() used to drive this, but it reports
        // whatever window has os-level focus at the exact instant this stas thread runs -- often not obs at all
        // (any other app stealing focus around the click puts the window on a monitor the user isnt even looking
        // at), which is what earlier attempts misread as "no window pops up" and "closes itself". the tray plugin
        // publishes obs's own main-window hwnd over the ipc pipe specifically because its authoritative -- same
        // read pattern as Native.CloseObsMainWindow.
        private static Screen ResolveTargetScreen()
        {
            long hwndVal;
            lock (Server.State.IpcLock) hwndVal = Server.State.ObsMainWindowHwnd;
            if (hwndVal != 0)
            {
                IntPtr hWnd = new IntPtr(hwndVal);
                if (IsWindow(hWnd)) return Screen.FromHandle(hWnd);
            }
            // obs hasnt published its main window yet (helper started before the plugin connected) -- the cursor
            // is a better guess than the system foreground window, which could be any unrelated app.
            return Screen.FromPoint(Cursor.Position);
        }

        // dark title bar + the active themes caption/border/text colours + app icon, same treatment every other
        // replaykit-owned window gets. this form is created by the helper directly, so it goes straight through
        // Native.ApplyWindowTheme on its own hwnd rather than the needle-matched /style-window route, which only
        // ever finds obs-family windows and would never match this one (see Native.IsObsFamilyProcess).
        private static void ApplyTheme(IntPtr hWnd)
        {
            try
            {
                var settings = ReplaykitSettings.Normalize(ReplaykitSettings.ReadSettings());
                string iconPath = ReplaykitSettings.ResolveAppIconPath(settings);
                if (string.IsNullOrEmpty(iconPath)) iconPath = Constants.OBS_ICON_PATH;
                Native.ApplyWindowTheme(hWnd, iconPath, taskbar: false);
            }
            catch (Exception ex) { Log.Write("Streamable sign-in theme apply: " + ex.Message); }
        }

        private static string ToCookieJar(System.Collections.Generic.IList<CoreWebView2Cookie> cookies)
        {
            var text = new StringBuilder("# Netscape HTTP Cookie File\r\n");
            foreach (var cookie in cookies)
            {
                string domain = cookie.Domain ?? "";
                if (!domain.EndsWith("streamable.com", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrWhiteSpace(cookie.Name) || cookie.Value == null) continue;
                long expires = cookie.IsSession ? 0 : new DateTimeOffset(cookie.Expires).ToUnixTimeSeconds();
                text.Append(domain).Append('\t')
                    .Append(domain.StartsWith(".", StringComparison.Ordinal) ? "TRUE" : "FALSE").Append('\t')
                    .Append(string.IsNullOrEmpty(cookie.Path) ? "/" : cookie.Path).Append('\t')
                    .Append(cookie.IsSecure ? "TRUE" : "FALSE").Append('\t')
                    .Append(expires.ToString(CultureInfo.InvariantCulture)).Append('\t')
                    .Append(cookie.Name).Append('\t').Append(cookie.Value).Append("\r\n");
            }
            return text.ToString();
        }

        private static bool IsAuthenticatedUser(JObject user)
        {
            if (user == null) return false;
            return user["id"] != null ||
                   !string.IsNullOrWhiteSpace(user.Value<string>("user_name")) ||
                   !string.IsNullOrWhiteSpace(user.Value<string>("username")) ||
                   !string.IsNullOrWhiteSpace(user.Value<string>("email"));
        }

        private static void SetError(string message)
        {
            lock (Gate) { _lastError = message; }
            Log.Write(message);
        }

        private static void SetClosed()
        {
            lock (Gate) { _windowOpen = false; _formHandle = IntPtr.Zero; }
        }
    }
}

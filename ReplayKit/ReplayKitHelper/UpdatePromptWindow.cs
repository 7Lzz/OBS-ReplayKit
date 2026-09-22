using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ReplayKitHelper
{
    // the update window, run as its own process (OBSReplayKit.exe --update-window). it has to outlive the main
    // helper: an update closes obs, the helper follows obs down, and the installer only copies files once the helper
    // pid is gone -- a window hosted inside the helper would vanish mid-install. Update.OpenUpdatePromptWindow spawns
    // this detached and broken away from the helpers job so the kill-on-close chain never reaches it. a borderless
    // winforms form hosting webview2, pointed at the same /update-prompt page the helper already serves -- no
    // chrome --app titlebar to clip, the frameless look is native to the window.
    internal static class UpdatePromptWindow
    {
        // css pixels; scaled by the target monitors dpi below. matches update_prompt.html + Update.OpenUpdatePromptWindow.
        private const int ClientCssW = 560, ClientCssH = 336;

        // its own taskbar identity so windows does not group this window under obs64 / the helper and can show its
        // own icon on the button + jump list
        [DllImport("shell32.dll")] private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);

        // bgColor is "#rrggbb" from the active theme, resolved by Update.OpenUpdatePromptWindow (this process has no
        // settings loaded); empty falls back to the OBS-default window colour. it is only the pre-paint frame colour
        // -- the border is the dwm system default (see Native.EnableDwmRounding).
        private static readonly Color DefaultBg = ColorTranslator.FromHtml("#1D1F26");

        private static Color ParseColor(string hex, Color fallback)
        {
            try { if (!string.IsNullOrWhiteSpace(hex)) return ColorTranslator.FromHtml(hex.Trim()); }
            catch (Exception ex) { WriteDebug("color parse '" + hex + "': " + ex.Message); }
            return fallback;
        }

        // webview2 + winforms both need an sta thread. the helper exes Main runs mta (no [STAThread]), so host the
        // window on its own sta thread and block Run until it closes -- same shape StreamableSignIn uses.
        public static int Run(string version, int port, string bgColor)
        {
            int result = 1;
            var thread = new Thread(() => result = RunWindow(version, port, bgColor)) { Name = "ReplayKit update window" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            return result;
        }

        private static int RunWindow(string version, int port, string bgColor)
        {
            try { SetCurrentProcessExplicitAppUserModelID("OBSReplayKit.UpdateWindow"); }
            catch (EntryPointNotFoundException) { } catch (DllNotFoundException) { }

            try
            {
                string profileDir = Path.Combine(Constants.REPLAYKIT_TEMP_ROOT, "update-window-profile");
                Directory.CreateDirectory(profileDir);

                Application.EnableVisualStyles();

                using (var form = BuildForm(bgColor))
                using (var web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = ParseColor(bgColor, DefaultBg) })
                {
                    form.Controls.Add(web);
                    form.Shown += async (sender, args) =>
                    {
                        try
                        {
                            try { Native.FocusHwnd(form.Handle); } catch (Exception ex) { WriteDebug("focus: " + ex.Message); }

                            var environment = await CoreWebView2Environment.CreateAsync(null, profileDir);
                            await web.EnsureCoreWebView2Async(environment);
                            var core = web.CoreWebView2;
                            core.Settings.AreDefaultContextMenusEnabled = false;
                            core.Settings.IsZoomControlEnabled = false;
                            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                            core.Settings.IsStatusBarEnabled = false;

                            // window.close() from the page (closeWindow() in update_prompt.html) lands here
                            core.WindowCloseRequested += (s, e) => CloseForm(form);
                            // no -webkit-app-region in webview2: the page posts "drag" on mousedown over the shell
                            core.WebMessageReceived += (s, e) =>
                            {
                                string message = null;
                                try { message = e.TryGetWebMessageAsString(); } catch (Exception ex) { WriteDebug("web message: " + ex.Message); }
                                if (message == "drag") { try { Native.StartWindowDrag(form.Handle); } catch (Exception ex) { WriteDebug("drag: " + ex.Message); } }
                                else if (message == "close") CloseForm(form);
                            };

                            string url = "http://127.0.0.1:" + port + "/update-prompt";
                            if (!string.IsNullOrWhiteSpace(version)) url += "?version=" + Uri.EscapeDataString(version);
                            core.Navigate(url);
                        }
                        catch (Exception ex)
                        {
                            WriteDebug("webview init failed: " + ex.Message);
                            CloseForm(form);
                        }
                    };
                    Application.Run(form);
                }
                return 0;
            }
            catch (Exception ex)
            {
                WriteDebug("update window failed: " + ex.Message);
                return 1;
            }
        }

        private static Form BuildForm(string bgColor)
        {
            var form = new RoundedBorderlessForm(ClientCssW, ClientCssH)
            {
                // title kept exact -- WindowWithTitleExists / CloseWindowsByTitle / the /close-window route all match on it
                Text = "ReplayKit Update",
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                ShowInTaskbar = true,
                // pre-paint colour only -- dwm draws the border, the page paints the rest
                BackColor = ParseColor(bgColor, DefaultBg),
            };

            // bundled ico only -- this standalone process has no settings loaded, so the appearance-tab icon override
            // is not resolvable here; the taskbar button falls back to the exes own icon anyway.
            try
            {
                if (File.Exists(Constants.OBS_ICON_PATH)) form.Icon = new Icon(Constants.OBS_ICON_PATH);
            }
            catch (Exception ex) { WriteDebug("icon: " + ex.Message); }

            return form;
        }

        // FormBorderStyle.None strips WS_THICKFRAME, which makes the window unroundable-by-policy -- dwm will not give
        // it real win11 corners. so keep None (no caption painted) but add WS_THICKFRAME back in CreateParams and eat
        // the whole non-client area in WM_NCCALCSIZE: no title bar is drawn, but dwm now rounds + borders + shadows
        // the window itself, anti-aliased, and clips the webview2 surface to match (see Native.EnableDwmRounding).
        // WS_THICKFRAME also re-arms aero snap, so WS_MAXIMIZEBOX is dropped and WM_GETMINMAXINFO pins the size -- the
        // window cannot be drag-maximized or edge-snapped.
        private sealed class RoundedBorderlessForm : Form
        {
            private const int WS_THICKFRAME = 0x00040000, WS_MINIMIZEBOX = 0x00020000, WS_SYSMENU = 0x00080000, WS_MAXIMIZEBOX = 0x00010000;
            private const int WM_NCCALCSIZE = 0x0083, WM_NCHITTEST = 0x0084, WM_SYSCOMMAND = 0x0112, WM_GETMINMAXINFO = 0x0024;
            private readonly int _cssW, _cssH;
            private bool _sized;

            public RoundedBorderlessForm(int cssW, int cssH) { _cssW = cssW; _cssH = cssH; }

            [StructLayout(LayoutKind.Sequential)] private struct MinMaxPoint { public int X, Y; }
            [StructLayout(LayoutKind.Sequential)]
            private struct MINMAXINFO { public MinMaxPoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }

            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.Style |= WS_THICKFRAME | WS_MINIMIZEBOX | WS_SYSMENU;
                    cp.Style &= ~WS_MAXIMIZEBOX;
                    return cp;
                }
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                try { Native.EnableDwmRounding(Handle); } catch { }
            }

            protected override void OnLoad(EventArgs e)
            {
                base.OnLoad(e);
                float scale = DeviceDpi / 96f;
                // WM_NCCALCSIZE zeroes the non-client area, so outer size == client size -- set Size, not ClientSize
                Size = new Size((int)Math.Round(_cssW * scale), (int)Math.Round(_cssH * scale));
                _sized = true;
                var area = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero) { m.Result = IntPtr.Zero; return; }
                // pin the size once laid out: no maximize growth, no half-screen edge snap
                if (m.Msg == WM_GETMINMAXINFO && _sized)
                {
                    var mmi = (MINMAXINFO)Marshal.PtrToStructure(m.LParam, typeof(MINMAXINFO));
                    var p = new MinMaxPoint { X = Width, Y = Height };
                    mmi.MaxSize = p; mmi.MaxTrackSize = p; mmi.MinTrackSize = p;
                    Marshal.StructureToPtr(mmi, m.LParam, false);
                    m.Result = IntPtr.Zero;
                    return;
                }
                // swallow maximize / resize from the sys menu and the snap keyboard shortcuts
                if (m.Msg == WM_SYSCOMMAND)
                {
                    long sc = m.WParam.ToInt64() & 0xFFF0;
                    if (sc == 0xF030 || sc == 0xF000) { m.Result = IntPtr.Zero; return; }
                }
                base.WndProc(ref m);
                // turn every resize-border hit (HTLEFT..HTBOTTOMRIGHT) back into client so edge drags cannot resize
                if (m.Msg == WM_NCHITTEST)
                {
                    int hit = m.Result.ToInt32();
                    if (hit >= 10 && hit <= 17) m.Result = (IntPtr)1;
                }
            }
        }

        private static void CloseForm(Form form)
        {
            try
            {
                if (form.IsDisposed) return;
                if (form.InvokeRequired) form.BeginInvoke((Action)form.Close);
                else form.Close();
            }
            catch (Exception ex) { WriteDebug("close: " + ex.Message); }
        }

        // always-on, flag-independent -- same reasoning as Update.WriteUpdateDebug: a "window never showed" report
        // has to be diagnosable without turning logging on first. this process has no config loaded.
        private static void WriteDebug(string message)
        {
            try
            {
                Directory.CreateDirectory(Constants.LOG_DIR);
                string line = "[" + DateTime.Now.ToString("o") + "] update-window " + message + Environment.NewLine;
                File.AppendAllText(Path.Combine(Constants.LOG_DIR, "replaykit_update_debug.log"), line);
            }
            catch { }
        }
    }
}

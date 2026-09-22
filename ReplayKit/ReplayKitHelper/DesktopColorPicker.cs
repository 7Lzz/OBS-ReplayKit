using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ReplayKitHelper
{
    // the desktop eyedropper behind the color panel: a nearly invisible window over every monitor takes the mouse so nothing underneath can be pressed while picking, shows an eyedropper cursor, and returns the color of the pixel that was clicked. esc or a right click cancels, and it ends on its own after a minute.
    internal static class DesktopColorPicker
    {
        private const int PickTimeoutMs = 60000;
        private const int ExNoActivate = 0x08000000;
        private const int ExToolWindow = 0x00000080;
        private const int SrcCopy = 0x00CC0020;
        private const int CaptureBlt = 0x40000000;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoActivate = 0x0010;
        private const uint ModNoRepeat = 0x4000;
        private const uint VkEscape = 0x1B;
        private const int WmHotkey = 0x0312;
        private const int EscapeHotkeyId = 1;
        private static readonly IntPtr HwndTopmost = new IntPtr(-1);
        private const int CursorBaseSize = 32;
        // pixels per icon unit at the default cursor size, which makes the one unit line of the icon exactly one pixel thick
        private const float CursorScale = 1f;
        private const float CursorMargin = 2f;
        // in glyph units; the pen sits on the edge, so the half outside is as thick as the icon line and the half inside is hidden under it
        private const float HaloWidth = 2f;
        // where the drawn part of icons/action/color-picker.svg starts in its 24 x 24 viewBox (left and top edge) and where the tip of the dropper is, which is the point the cursor aims with
        private const float GlyphMin = 2.003f;
        private const float GlyphTipX = 2.458f;
        private const float GlyphTipY = 20.54f;
        private static readonly object Gate = new object();
        private static bool active;

        [StructLayout(LayoutKind.Sequential)]
        private struct IconInfo
        {
            public bool IsIcon;
            public int HotspotX;
            public int HotspotY;
            public IntPtr Mask;
            public IntPtr Color;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreateIconIndirect(ref IconInfo info);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr icon);

        [DllImport("user32.dll")]
        private static extern bool DestroyCursor(IntPtr cursor);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr handle);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int operation);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr window, IntPtr context);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr window, int id);

        // returns "#RRGGBB", or "" when the pick was cancelled
        public static string Pick()
        {
            lock (Gate)
            {
                if (active) throw new InvalidOperationException("A color picker is already active.");
                active = true;
            }

            string result = "";
            try
            {
                StaRunner.Run(() => result = RunPicker(SystemInformation.VirtualScreen));
                return result;
            }
            finally
            {
                lock (Gate) active = false;
            }
        }

        // the area and the cursor position are in real pixels because the helper manifest declares per-monitor dpi awareness, so the snapshot and the click line up on every monitor. the snapshot is taken before the window goes up, so the color read is the real pixel and not the pixel with the window on top of it.
        internal static string RunPicker(Rectangle area)
        {
            using (Bitmap snapshot = CaptureScreen(area))
            {
                IntPtr cursorHandle = CreateEyedropperCursor();
                try
                {
                    using (var overlay = new PickerOverlay(area, snapshot, cursorHandle))
                    using (var timeout = new Timer { Interval = PickTimeoutMs })
                    {
                        overlay.ReserveEscape();
                        timeout.Tick += (sender, args) => overlay.Cancel();
                        timeout.Start();
                        try { Application.Run(overlay); }
                        finally { timeout.Stop(); }
                        return overlay.Result;
                    }
                }
                finally { DestroyCursor(cursorHandle); }
            }
        }

        // the blit uses CAPTUREBLT to keep layered windows (menus, tooltips) in the picture, so they are sampled as they look and not as what sits behind them; Graphics.CopyFromScreen refuses that flag next to SRCCOPY, hence the direct call.
        private static Bitmap CaptureScreen(Rectangle area)
        {
            var bitmap = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb);
            try
            {
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    IntPtr screen = GetDC(IntPtr.Zero);
                    if (screen == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                    IntPtr target = graphics.GetHdc();
                    try
                    {
                        if (!BitBlt(target, 0, 0, area.Width, area.Height, screen, area.Left, area.Top, SrcCopy | CaptureBlt))
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not capture the screen for the color picker.");
                    }
                    finally
                    {
                        graphics.ReleaseHdc(target);
                        ReleaseDC(IntPtr.Zero, screen);
                    }
                }
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }

        // the eyedropper icon of the color panel button as a cursor: the icon lines in black over a see-through middle like the svg, with a white outline around the outside so it still reads on dark backgrounds, one icon unit per pixel at the default cursor size and scaled up with the system cursor size, with the hotspot on the tip; drawn a little inside the canvas so the outline is not clipped at the edges.
        private static IntPtr CreateEyedropperCursor()
        {
            int size = Math.Max(CursorBaseSize, SystemInformation.CursorSize.Width);
            float unit = size / (float)CursorBaseSize;
            float margin = CursorMargin * unit;
            float scale = CursorScale * unit;
            using (var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (var graphics = Graphics.FromImage(bitmap))
                using (var silhouette = BuildEyedropperPath(true))
                using (var lines = BuildEyedropperPath(false))
                using (var halo = new Pen(Color.White, HaloWidth) { LineJoin = LineJoin.Round })
                {
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.TranslateTransform(margin, margin);
                    graphics.ScaleTransform(scale, scale);
                    graphics.TranslateTransform(-GlyphMin, -GlyphMin);
                    graphics.DrawPath(halo, silhouette);
                    graphics.FillPath(Brushes.Black, lines);
                }

                IntPtr icon = bitmap.GetHicon();
                try
                {
                    if (!GetIconInfo(icon, out IconInfo info)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    try
                    {
                        info.IsIcon = false;
                        info.HotspotX = (int)Math.Round((GlyphTipX - GlyphMin) * scale + margin);
                        info.HotspotY = (int)Math.Round((GlyphTipY - GlyphMin) * scale + margin);
                        IntPtr cursor = CreateIconIndirect(ref info);
                        if (cursor == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                        return cursor;
                    }
                    finally
                    {
                        DeleteObject(info.Color);
                        DeleteObject(info.Mask);
                    }
                }
                finally { DestroyIcon(icon); }
            }
        }

        // the shape of icons/action/color-picker.svg (24 x 24 viewBox) with its relative path commands worked out to absolute points: the first figure is the silhouette and the other two are the openings the icon cuts out of it (even-odd fill), which is what leaves it a line drawing; silhouetteOnly stops after the first so the outline can be drawn around the outside only.
        private static GraphicsPath BuildEyedropperPath(bool silhouetteOnly)
        {
            var path = new GraphicsPath(FillMode.Alternate);
            // silhouette
            path.StartFigure();
            path.AddLine(16.525f, 2.7657f, 14.501f, 4.7907f);
            path.AddLine(14.501f, 4.7907f, 14.266f, 4.5577f);
            path.AddBezier(14.266f, 4.5577f, 13.546f, 3.8347f, 12.293f, 3.8297f, 11.559f, 4.5647f);
            path.AddBezier(11.559f, 4.5647f, 10.815f, 5.3077f, 10.815f, 6.5197f, 11.559f, 7.2647f);
            path.AddLine(11.559f, 7.2647f, 11.793f, 7.4987f);
            path.AddLine(11.793f, 7.4987f, 3.767f, 15.5237f);
            path.AddBezier(3.767f, 15.5237f, 3.185f, 16.1057f, 2.917f, 16.9397f, 3.035f, 17.7547f);
            path.AddLine(3.035f, 17.7547f, 2.458f, 18.3337f);
            path.AddBezier(2.458f, 18.3337f, 1.851f, 18.9137f, 1.853f, 19.9597f, 2.458f, 20.5397f);
            path.AddBezier(2.458f, 20.5397f, 3.036f, 21.1467f, 4.083f, 21.1467f, 4.665f, 20.5407f);
            path.AddLine(4.665f, 20.5407f, 5.242f, 19.9617f);
            path.AddBezier(5.242f, 19.9617f, 6.055f, 20.0817f, 6.892f, 19.8117f, 7.474f, 19.2307f);
            path.AddLine(7.474f, 19.2307f, 15.5f, 11.2057f);
            path.AddLine(15.5f, 11.2057f, 15.733f, 11.4387f);
            path.AddBezier(15.733f, 11.4387f, 16.478f, 12.1837f, 17.689f, 12.1837f, 18.44f, 11.4327f);
            path.AddBezier(18.44f, 11.4327f, 19.184f, 10.6877f, 19.184f, 9.4757f, 18.44f, 8.7327f);
            path.AddLine(18.44f, 8.7327f, 18.208f, 8.4977f);
            path.AddLine(18.208f, 8.4977f, 20.232f, 6.4737f);
            path.AddBezier(20.232f, 6.4737f, 21.251f, 5.4957f, 21.25f, 3.7417f, 20.23f, 2.7657f);
            path.AddBezier(20.23f, 2.7657f, 19.72f, 2.2557f, 19.049f, 1.9997f, 18.378f, 1.9997f);
            path.AddBezier(18.378f, 1.9997f, 17.707f, 1.9997f, 17.035f, 2.2557f, 16.525f, 2.7657f);
            path.CloseFigure();
            if (silhouetteOnly) return path;
            // opening around the bulb and collar
            path.StartFigure();
            path.AddLine(14.853f, 5.8527f, 17.232f, 3.4737f);
            path.AddBezier(17.232f, 3.4737f, 17.843f, 2.8617f, 18.912f, 2.8617f, 19.525f, 3.4757f);
            path.AddBezier(19.525f, 3.4757f, 20.153f, 4.0777f, 20.154f, 5.1627f, 19.525f, 5.7657f);
            path.AddLine(19.525f, 5.7657f, 17.146f, 8.1447f);
            path.AddBezier(17.146f, 8.1447f, 16.95f, 8.3387f, 16.95f, 8.6567f, 17.146f, 8.8517f);
            path.AddLine(17.146f, 8.8517f, 17.732f, 9.4387f);
            path.AddBezier(17.732f, 9.4387f, 18.087f, 9.7927f, 18.087f, 10.3707f, 17.727f, 10.7317f);
            path.AddBezier(17.727f, 10.7317f, 17.372f, 11.0867f, 16.796f, 11.0867f, 16.44f, 10.7317f);
            path.AddLine(16.44f, 10.7317f, 12.266f, 6.5577f);
            path.AddBezier(12.266f, 6.5577f, 11.912f, 6.2027f, 11.912f, 5.6257f, 12.272f, 5.2657f);
            path.AddBezier(12.272f, 5.2657f, 12.616f, 4.9177f, 13.215f, 4.9177f, 13.559f, 5.2657f);
            path.AddLine(13.559f, 5.2657f, 14.146f, 5.8527f);
            path.AddBezier(14.146f, 5.8527f, 14.244f, 5.9497f, 14.372f, 5.9977f, 14.5f, 5.9977f);
            path.AddBezier(14.5f, 5.9977f, 14.628f, 5.9977f, 14.756f, 5.9497f, 14.853f, 5.8527f);
            path.CloseFigure();
            // opening along the tube
            path.StartFigure();
            path.AddLine(3.165f, 19.0407f, 3.932f, 18.2727f);
            path.AddBezier(3.932f, 18.2727f, 4.058f, 18.1467f, 4.106f, 17.9637f, 4.062f, 17.7927f);
            path.AddBezier(4.062f, 17.7927f, 3.914f, 17.2217f, 4.067f, 16.6387f, 4.474f, 16.2307f);
            path.AddLine(4.474f, 16.2307f, 12.501f, 8.2057f);
            path.AddLine(12.501f, 8.2057f, 14.793f, 10.4977f);
            path.AddLine(14.793f, 10.4977f, 6.767f, 18.5237f);
            path.AddBezier(6.767f, 18.5237f, 6.361f, 18.9297f, 5.778f, 19.0837f, 5.206f, 18.9357f);
            path.AddBezier(5.206f, 18.9357f, 5.032f, 18.8917f, 4.85f, 18.9407f, 4.725f, 19.0657f);
            path.AddLine(4.725f, 19.0657f, 3.959f, 19.8337f);
            path.AddBezier(3.959f, 19.8337f, 3.844f, 19.9527f, 3.713f, 20.0027f, 3.585f, 20.0027f);
            path.AddBezier(3.585f, 20.0027f, 3.153f, 20.0027f, 2.76f, 19.4307f, 3.165f, 19.0407f);
            path.CloseFigure();
            return path;
        }

        private sealed class PickerOverlay : Form
        {
            private readonly Rectangle _area;
            private readonly Bitmap _snapshot;
            private MouseButtons _held = MouseButtons.None;
            private Point _pressed;
            private IntPtr _hotkeyWindow;
            private bool _done;

            public string Result { get; private set; } = "";

            public PickerOverlay(Rectangle area, Bitmap snapshot, IntPtr cursorHandle)
            {
                _area = area;
                _snapshot = snapshot;
                Text = "ReplayKit color picker";
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                Bounds = area;
                BackColor = Color.Black;
                // 1% opacity keeps the window invisible but still solid for the mouse, which a fully transparent one is not
                Opacity = 0.01;
                TopMost = true;
                Cursor = new Cursor(cursorHandle);
            }

            // never takes focus, so the window that was in front stays in front and the keys keep going to it
            protected override bool ShowWithoutActivation => true;

            protected override CreateParams CreateParams
            {
                get
                {
                    var parameters = base.CreateParams;
                    parameters.ExStyle |= ExNoActivate | ExToolWindow;
                    return parameters;
                }
            }

            // the topmost flag set at creation does not always survive on a window this large, and one that is not on top lets clicks fall through to the window under it, so it is put on top again once it is showing
            protected override void OnShown(EventArgs e)
            {
                base.OnShown(e);
                SetWindowPos(Handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            }

            // esc is reserved system-wide for as long as this window lives, so it stops the picker whatever has focus and never reaches the window underneath. a registered hotkey rather than a low level keyboard hook, which sees every key typed and is the shape antivirus heuristics tie to keyloggers; if another program already holds esc the pick refuses to start instead of running without its way out.
            public void ReserveEscape()
            {
                IntPtr window = Handle;
                if (!RegisterHotKey(window, EscapeHotkeyId, ModNoRepeat, VkEscape))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not reserve the Esc key for the color picker. Another program may be using it.");
                _hotkeyWindow = window;
            }

            // the hotkey is tied to the window it was registered on, so it is released against that same handle rather than asking the form for one while it is being torn down
            protected override void OnHandleDestroyed(EventArgs e)
            {
                if (_hotkeyWindow != IntPtr.Zero)
                {
                    UnregisterHotKey(_hotkeyWindow, EscapeHotkeyId);
                    _hotkeyWindow = IntPtr.Zero;
                }
                base.OnHandleDestroyed(e);
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WmHotkey && m.WParam.ToInt32() == EscapeHotkeyId)
                {
                    Cancel();
                    return;
                }
                base.WndProc(ref m);
            }

            public void Cancel() => Complete("");

            // one button at a time, and only left or right count. the color is read where the button went down and handed back when it comes up, so the release never reaches the window underneath.
            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (_held != MouseButtons.None) return;
                if (e.Button != MouseButtons.Left && e.Button != MouseButtons.Right) return;
                _held = e.Button;
                _pressed = PointToScreen(e.Location);
                Capture = true;
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (e.Button != _held) return;
                Complete(_held == MouseButtons.Left ? ColorAt(_pressed) : "");
            }

            private string ColorAt(Point screenPoint)
            {
                int x = Math.Max(0, Math.Min(_snapshot.Width - 1, screenPoint.X - _area.Left));
                int y = Math.Max(0, Math.Min(_snapshot.Height - 1, screenPoint.Y - _area.Top));
                Color color = _snapshot.GetPixel(x, y);
                return "#" + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");
            }

            private void Complete(string result)
            {
                if (_done) return;
                _done = true;
                Result = result;
                Capture = false;
                Close();
            }
        }
    }
}

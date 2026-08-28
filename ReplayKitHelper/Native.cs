using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ReplayKitHelper
{
    // consolidated win32/com interop for window management, job objects, thumbnail extraction, sqlite (browser cookie reads), and aes-gcm (cookie decryption).
    // ported from obs_replaykit helper modules/30_native.ps1, which itself compiled three separate overlapping Add-Type blocks (ReplayKitNative, a second copy inside modules/64_discord_projector.ps1, and a third inside trim_keyframes_worker.ps1-adjacent code) -- collapsed into one class here since a compiled assembly has no reason to duplicate P/Invoke declarations per file the way dot-sourced scripts did.
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor, rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFOW
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        // shell32
        [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            void GetImage(SIZE size, int flags, out IntPtr phbm);
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int cx, cy; }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);
        private static readonly Guid IID_IShellItemImageFactory = new Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B");
        private const int SIIGBF_BIGGERSIZEOK = 0x0001;

        // ITaskbarList (original 5-method interface, not ITaskbarList2/3)
        [ComImport, Guid("56FDF342-FD6D-11d0-958A-006097C9A090"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList
        {
            void HrInit();
            void AddTab(IntPtr hwnd);
            void DeleteTab(IntPtr hwnd);
            void ActivateTab(IntPtr hwnd);
            void SetActiveAlt(IntPtr hwnd);
        }
        [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
        private class CTaskbarList { }

        // gdi32
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(int crColor);

        // user32
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)] private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)] private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll", EntryPoint = "SetClassLongPtr", SetLastError = true)] private static extern IntPtr SetClassLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        // dwmapi
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int attrValue, int attrSize);

        // kernel32
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint dwAccess, bool inherit, uint pid);
        [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr hHandle, out uint exitCode);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr hHandle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);
        [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInformationClass, IntPtr lpJobObjectInformation, uint cbJobObjectInformationLength);
        [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool MoveFileEx(string lpExistingFileName, string lpNewFileName, uint dwFlags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcessW(string lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment,
            string lpCurrentDirectory, ref STARTUPINFOW lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        // constants -- exact values matter, these are standard documented win32 values.
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_PRE20H1 = 19;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;
        private const int CAPTION_COLOR = 0x00261F1D;
        private const int BORDER_COLOR = 0x004D403C;
        private const int TEXT_COLOR = 0x00FFFFFF;
        private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;
        private const int GWL_STYLE = -16, GWL_EXSTYLE = -20, GWLP_HWNDPARENT = -8, GCLP_HBRBACKGROUND = -10;
        private const int WS_EX_APPWINDOW = 0x00040000, WS_EX_TOOLWINDOW = 0x00000080;
        private const long WS_CAPTION = 0x00C00000L, WS_THICKFRAME = 0x00040000L, WS_MINIMIZEBOX = 0x00020000L, WS_MAXIMIZEBOX = 0x00010000L, WS_SYSMENU = 0x00080000L;
        private const uint RDW_INVALIDATE = 0x0001, RDW_UPDATENOW = 0x0100, RDW_FRAME = 0x0400, RDW_NOCHILDREN = 0x0040;
        private const uint WM_SETICON = 0x0080, WM_NCACTIVATE = 0x0086, WM_THEMECHANGED = 0x031A, WM_CLOSE = 0x0010;
        private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020, SWP_SHOWWINDOW = 0x0040;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
        private const int VK_LBUTTON = 0x01;
        private const int SW_HIDE = 0, SW_RESTORE = 9, SW_MAXIMIZE = 3;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint STILL_ACTIVE = 259;
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000, JOB_OBJECT_LIMIT_BREAKAWAY_OK = 0x0800;
        public const uint CREATE_NO_WINDOW = 0x08000000, CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
        private const int STARTF_USESHOWWINDOW = 0x00000001;

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        // is this pid part of the obs process family (used to scope window/close operations to obs-owned windows, not arbitrary chrome windows the user happens to have open).
        private static bool IsObsFamilyProcess(uint pid)
        {
            try
            {
                string name = Process.GetProcessById((int)pid).ProcessName;
                return string.Equals(name, "obs64", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "obs", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "chrome", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("obs-", StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        private static List<IntPtr> EnumerateTopLevelWindows()
        {
            var list = new List<IntPtr>();
            EnumWindows((hWnd, _) => { list.Add(hWnd); return true; }, IntPtr.Zero);
            return list;
        }

        private static string GetTitle(IntPtr hWnd)
        {
            var sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        // 480x270 jpeg (quality 86) via the shell's registered thumbnail codec, letterboxed on black. wrapped in an 8s timeout since damaged files have taken 13+s and would otherwise freeze the single accept thread.
        public static void SaveThumbnail(string src, string dst)
        {
            var task = Task.Run(() =>
            {
                Guid iid = IID_IShellItemImageFactory;
                SHCreateItemFromParsingName(src, IntPtr.Zero, ref iid, out IShellItemImageFactory factory);
                IntPtr hbmp = IntPtr.Zero;
                try
                {
                    factory.GetImage(new SIZE { cx = 480, cy = 270 }, SIIGBF_BIGGERSIZEOK, out hbmp);
                    using (var src2 = Image.FromHbitmap(hbmp))
                    using (var canvas = new Bitmap(480, 270))
                    using (var g = Graphics.FromImage(canvas))
                    {
                        g.Clear(Color.Black);
                        double scale = Math.Min(480.0 / src2.Width, 270.0 / src2.Height);
                        int w = Math.Max(1, (int)Math.Round(src2.Width * scale));
                        int h = Math.Max(1, (int)Math.Round(src2.Height * scale));
                        g.DrawImage(src2, (480 - w) / 2, (270 - h) / 2, w, h);
                        var jpegCodec = Array.Find(ImageCodecInfo.GetImageEncoders(), c => c.FormatID == ImageFormat.Jpeg.Guid);
                        var eps = new EncoderParameters(1);
                        eps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 86L);
                        canvas.Save(dst, jpegCodec, eps);
                    }
                }
                finally
                {
                    if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
                    Marshal.ReleaseComObject(factory);
                }
            });
            if (!task.Wait(8000)) throw new TimeoutException("SaveThumbnail timed out after 8s: " + src);
            if (task.IsFaulted && task.Exception != null) throw task.Exception.InnerException ?? task.Exception;
        }

        // enumwindows + exact/prefix/suffix (ordinalignorecase) title match, scoped to obs-family windows when requireOwnerPid != 0. returns count closed.
        public static int CloseWindowsByTitle(string[] titlePrefixes, uint requireOwnerPid)
        {
            int closed = 0;
            foreach (var hWnd in EnumerateTopLevelWindows())
            {
                if (!IsWindowVisible(hWnd)) continue;
                string title = GetTitle(hWnd);
                if (string.IsNullOrEmpty(title)) continue;
                bool matches = false;
                foreach (var prefix in titlePrefixes)
                {
                    if (title.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                        title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                        title.EndsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        matches = true;
                        break;
                    }
                }
                if (!matches) continue;
                if (requireOwnerPid != 0)
                {
                    GetWindowThreadProcessId(hWnd, out uint ownerPid);
                    if (ownerPid != requireOwnerPid && !IsObsFamilyProcess(ownerPid)) continue;
                }
                RemoveTaskbarTab(hWnd);
                PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                closed++;
            }
            return closed;
        }

        // the plugin (replaykit.cpp, out of scope here) hands obs's real main-window hwnd over the OBSReplayKitIpc pipe; PipeClient caches it in Server.State. window-title matching alone cant tell obs's own main window from a projector, so the plugin -- which has the authoritative Qt-side isOBSProjectorWindow check -- sends it directly. returns false (caller force-kills) if the pipe never delivered one.
        public static bool CloseObsMainWindow(uint requireOwnerPid)
        {
            long hwndVal;
            lock (Server.State.IpcLock) hwndVal = Server.State.ObsMainWindowHwnd;
            if (hwndVal == 0) return false;
            IntPtr hWnd = new IntPtr(hwndVal);
            if (!IsWindow(hWnd)) return false;
            if (requireOwnerPid != 0)
            {
                GetWindowThreadProcessId(hWnd, out uint ownerPid);
                if (ownerPid != requireOwnerPid) return false;
            }
            PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            return true;
        }

        // enumerates windows matching google/streamable/loader/about:blank title heuristics for the sign-in popup flow; hand-builds json (matches the ps original's manual-escape approach rather than pulling in a serializer for one caller).
        public static string ListSignInWindows(uint requireOwnerPid, bool hideGoogle, bool overlayGoogle)
        {
            IntPtr googleHwnd = IntPtr.Zero, streamableHwnd = IntPtr.Zero;
            var entries = new List<(string Title, long Hwnd, bool Google, bool Streamable)>();
            foreach (var hWnd in EnumerateTopLevelWindows())
            {
                if (!IsWindowVisible(hWnd)) continue;
                string title = GetTitle(hWnd);
                if (string.IsNullOrEmpty(title)) continue;
                if (requireOwnerPid != 0)
                {
                    GetWindowThreadProcessId(hWnd, out uint ownerPid);
                    if (ownerPid != requireOwnerPid && !IsObsFamilyProcess(ownerPid)) continue;
                }
                bool isGoogle = title.IndexOf("Sign in - Google Accounts", StringComparison.OrdinalIgnoreCase) >= 0
                    || title.IndexOf("accounts.google.com", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isStreamable = title.IndexOf("streamable.com", StringComparison.OrdinalIgnoreCase) >= 0
                    || title.Equals("about:blank", StringComparison.OrdinalIgnoreCase)
                    || title.IndexOf("Sign in", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!isGoogle && !isStreamable) continue;
                entries.Add((title, hWnd.ToInt64(), isGoogle, isStreamable));
                if (isGoogle && googleHwnd == IntPtr.Zero) googleHwnd = hWnd;
                if (isStreamable && streamableHwnd == IntPtr.Zero) streamableHwnd = hWnd;
            }

            if (hideGoogle && googleHwnd != IntPtr.Zero) ShowWindow(googleHwnd, SW_HIDE);
            if (overlayGoogle && googleHwnd != IntPtr.Zero && streamableHwnd != IntPtr.Zero)
            {
                GetWindowRect(streamableHwnd, out RECT target);
                int w = Math.Max(320, target.Right - target.Left);
                int h = Math.Max(320, target.Bottom - target.Top);
                SetWindowPos(googleHwnd, IntPtr.Zero, target.Left, target.Top, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
            }

            var sb = new StringBuilder("[");
            for (int i = 0; i < entries.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var e = entries[i];
                sb.Append("{\"title\":\"").Append(JsonEscape(e.Title)).Append("\",\"hwnd\":").Append(e.Hwnd)
                  .Append(",\"google\":").Append(e.Google ? "true" : "false")
                  .Append(",\"streamable\":").Append(e.Streamable ? "true" : "false").Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static string JsonEscape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n");

        // dark-mode dwm attrs + custom title/border/text colors + WM_SETICON, forcing a themechanged -> reapply dwm attrs -> ncactivate toggle -> setwindowpos(framechanged) -> redrawwindow sequence to defeat cef's non-client repaint race. returns match count.
        public static int StyleWindow(string needle, string iconPath, bool taskbar)
        {
            int matched = 0;
            IntPtr hIcon16 = IntPtr.Zero, hIcon32 = IntPtr.Zero;
            if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
            {
                try
                {
                    using (var ico = new Icon(iconPath, 16, 16)) hIcon16 = ico.Handle;
                    using (var ico = new Icon(iconPath, 32, 32)) hIcon32 = ico.Handle;
                }
                catch (Exception ex) when (ex is IOException || ex is ArgumentException) { }
            }

            foreach (var hWnd in EnumerateTopLevelWindows())
            {
                if (!IsWindowVisible(hWnd)) continue;
                GetWindowThreadProcessId(hWnd, out uint ownerPid);
                if (!IsObsFamilyProcess(ownerPid)) continue;
                string title = GetTitle(hWnd);
                if (string.IsNullOrEmpty(title) || title.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;

                int dark = 1;
                DwmSetWindowAttribute(hWnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
                DwmSetWindowAttribute(hWnd, DWMWA_USE_IMMERSIVE_DARK_MODE_PRE20H1, ref dark, sizeof(int));
                int caption = CAPTION_COLOR, border = BORDER_COLOR, text = TEXT_COLOR;
                DwmSetWindowAttribute(hWnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
                DwmSetWindowAttribute(hWnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
                DwmSetWindowAttribute(hWnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));

                if (hIcon16 != IntPtr.Zero) SendMessage(hWnd, WM_SETICON, new IntPtr(0), hIcon16);
                if (hIcon32 != IntPtr.Zero) SendMessage(hWnd, WM_SETICON, new IntPtr(1), hIcon32);

                if (taskbar) AddTaskbarTab(hWnd);

                SendMessage(hWnd, WM_THEMECHANGED, IntPtr.Zero, IntPtr.Zero);
                DwmSetWindowAttribute(hWnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
                DwmSetWindowAttribute(hWnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
                DwmSetWindowAttribute(hWnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
                DwmSetWindowAttribute(hWnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));
                SendMessage(hWnd, WM_NCACTIVATE, new IntPtr(0), IntPtr.Zero);
                SendMessage(hWnd, WM_NCACTIVATE, new IntPtr(1), IntPtr.Zero);
                SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
                RedrawWindow(hWnd, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_UPDATENOW | RDW_FRAME);
                matched++;
            }
            return matched;
        }

        // shows/restores + attachthreadinput anti-focus-steal dance + bringwindowtotop + topmost toggle + setforegroundwindow. returns whether it actually ended up foreground.
        public static bool FocusHwnd(IntPtr hWnd)
        {
            if (!IsWindow(hWnd)) return false;
            if (IsZoomed(hWnd)) ShowWindowAsync(hWnd, SW_RESTORE); else ShowWindow(hWnd, SW_RESTORE);

            uint targetThread = GetWindowThreadProcessId(hWnd, out _);
            uint currentThread = GetCurrentThreadId();
            IntPtr fgWnd = GetForegroundWindow();
            uint fgThread = fgWnd != IntPtr.Zero ? GetWindowThreadProcessId(fgWnd, out _) : 0;

            bool attached = fgThread != 0 && fgThread != currentThread && AttachThreadInput(currentThread, fgThread, true);
            try
            {
                BringWindowToTop(hWnd);
                SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
                SetWindowPos(hWnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
                SetForegroundWindow(hWnd);
            }
            finally
            {
                if (attached) AttachThreadInput(currentThread, fgThread, false);
            }
            return GetForegroundWindow() == hWnd;
        }

        private static IntPtr FindObsWindow(string needle)
        {
            foreach (var hWnd in EnumerateTopLevelWindows())
            {
                if (!IsWindowVisible(hWnd)) continue;
                GetWindowThreadProcessId(hWnd, out uint ownerPid);
                if (!IsObsFamilyProcess(ownerPid)) continue;
                string title = GetTitle(hWnd);
                if (string.IsNullOrEmpty(title)) continue;
                if (title.Equals(needle, StringComparison.OrdinalIgnoreCase) ||
                    title.StartsWith(needle + " ", StringComparison.OrdinalIgnoreCase) ||
                    title.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return hWnd;
                }
            }
            return IntPtr.Zero;
        }

        public static bool FocusWindow(string needle)
        {
            var hWnd = FindObsWindow(needle);
            return hWnd != IntPtr.Zero && FocusHwnd(hWnd);
        }

        private static readonly Dictionary<long, RECT> SavedRects = new Dictionary<long, RECT>();
        private static readonly object SavedRectsLock = new object();
        private sealed class FullscreenWindowState
        {
            public RECT Rect;
            public IntPtr Style, ExStyle;
            public bool WasMaximized;
        }
        private static readonly Dictionary<long, FullscreenWindowState> FullscreenWindows = new Dictionary<long, FullscreenWindowState>();
        private static readonly object FullscreenWindowsLock = new object();

        public static bool MaximizeObsWindow(string needle)
        {
            var hWnd = FindObsWindow(needle);
            if (hWnd == IntPtr.Zero) return false;
            lock (SavedRectsLock)
            {
                long key = hWnd.ToInt64();
                if (!SavedRects.ContainsKey(key) && !IsZoomed(hWnd) && GetWindowRect(hWnd, out RECT rect))
                {
                    SavedRects[key] = rect;
                }
            }
            ShowWindow(hWnd, SW_MAXIMIZE);
            return true;
        }

        public static bool RestoreObsWindow(string needle)
        {
            var hWnd = FindObsWindow(needle);
            if (hWnd == IntPtr.Zero) return false;
            lock (SavedRectsLock)
            {
                long key = hWnd.ToInt64();
                if (!SavedRects.TryGetValue(key, out RECT rect)) return false;
                SavedRects.Remove(key);
                if (!IsZoomed(hWnd)) return false;
                SetWindowPos(hWnd, IntPtr.Zero, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, SWP_NOZORDER | SWP_NOACTIVATE);
            }
            return true;
        }

        // borderless monitor-sized mode for CEF popups. ShowWindow(SW_MAXIMIZE) leaves the caption/taskbar visible, so player fullscreen uses this instead and restores the exact original frame/rect afterward.
        public static bool EnterObsWindowFullscreen(string needle)
        {
            var hWnd = FindObsWindow(needle);
            if (hWnd == IntPtr.Zero || !GetWindowRect(hWnd, out RECT rect)) return false;
            var monitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) return false;
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref info)) return false;
            lock (FullscreenWindowsLock)
            {
                long key = hWnd.ToInt64();
                if (!FullscreenWindows.ContainsKey(key))
                {
                    FullscreenWindows[key] = new FullscreenWindowState
                    {
                        Rect = rect,
                        Style = GetWindowLongPtr64(hWnd, GWL_STYLE),
                        ExStyle = GetWindowLongPtr64(hWnd, GWL_EXSTYLE),
                        WasMaximized = IsZoomed(hWnd)
                    };
                }
            }
            long style = GetWindowLongPtr64(hWnd, GWL_STYLE).ToInt64();
            style &= ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU);
            SetWindowLongPtr64(hWnd, GWL_STYLE, new IntPtr(style));
            return SetWindowPos(hWnd, IntPtr.Zero, info.rcMonitor.Left, info.rcMonitor.Top,
                info.rcMonitor.Right - info.rcMonitor.Left, info.rcMonitor.Bottom - info.rcMonitor.Top,
                SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        }

        public static bool ExitObsWindowFullscreen(string needle)
        {
            var hWnd = FindObsWindow(needle);
            if (hWnd == IntPtr.Zero) return false;
            FullscreenWindowState state;
            lock (FullscreenWindowsLock)
            {
                long key = hWnd.ToInt64();
                if (!FullscreenWindows.TryGetValue(key, out state)) return false;
                FullscreenWindows.Remove(key);
            }
            SetWindowLongPtr64(hWnd, GWL_STYLE, state.Style);
            SetWindowLongPtr64(hWnd, GWL_EXSTYLE, state.ExStyle);
            if (state.WasMaximized)
            {
                ShowWindow(hWnd, SW_MAXIMIZE);
                return true;
            }
            return SetWindowPos(hWnd, IntPtr.Zero, state.Rect.Left, state.Rect.Top,
                state.Rect.Right - state.Rect.Left, state.Rect.Bottom - state.Rect.Top,
                SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        }

        public static bool SetWindowSizeCentered(string needle, int width, int height)
        {
            var hWnd = FindObsWindow(needle);
            if (hWnd == IntPtr.Zero) return false;
            int screenW = GetSystemMetrics(SM_CXSCREEN), screenH = GetSystemMetrics(SM_CYSCREEN);
            int x = Math.Max(0, (screenW - width) / 2);
            int y = Math.Max(0, (screenH - height) / 2);
            return SetWindowPos(hWnd, IntPtr.Zero, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
        }

        private static readonly HashSet<long> ResizeTracking = new HashSet<long>();
        private static readonly object ResizeTrackingLock = new object();
        private static bool _resizeBackgroundPrimed;

        private static void PrimeResizeBackground(IntPtr hWnd)
        {
            if (_resizeBackgroundPrimed) return;
            IntPtr brush = CreateSolidBrush(CAPTION_COLOR);
            SetClassLongPtr64(hWnd, GCLP_HBRBACKGROUND, brush);
            _resizeBackgroundPrimed = true;
        }

        // spawns a background thread that polls GetAsyncKeyState(VK_LBUTTON) every 15ms (~66fps) and live-setwindowposs while the button stays down, clamped to [min,max]. deliberately not message-based (WM_NCLBUTTONDOWN) -- that was tried and found unreliable due to cross-process timing races.
        public static bool BeginResizeWindow(string needle, int minW, int minH, int maxW, int maxH)
        {
            var hWnd = FindObsWindow(needle);
            if (hWnd == IntPtr.Zero) return false;
            long key = hWnd.ToInt64();
            lock (ResizeTrackingLock)
            {
                if (ResizeTracking.Contains(key)) return true;
                ResizeTracking.Add(key);
            }
            PrimeResizeBackground(hWnd);

            var thread = new Thread(() =>
            {
                try
                {
                    while ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0)
                    {
                        if (!IsWindow(hWnd)) break;
                        GetCursorPos(out POINT cursor);
                        GetWindowRect(hWnd, out RECT rect);
                        int w = Math.Max(minW, Math.Min(maxW, cursor.X - rect.Left));
                        int h = Math.Max(minH, Math.Min(maxH, cursor.Y - rect.Top));
                        SetWindowPos(hWnd, IntPtr.Zero, 0, 0, w, h, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
                        Thread.Sleep(15);
                    }
                }
                finally
                {
                    lock (ResizeTrackingLock) ResizeTracking.Remove(key);
                }
            });
            thread.IsBackground = true;
            thread.Start();
            return true;
        }

        // creates a job object with kill-on-close, assigns the current process to it. called once at startup; every child process spawned afterward without CREATE_BREAKAWAY_FROM_JOB inherits the job automatically (win8+) and dies when this process's last job handle closes.
        public static IntPtr CreateKillOnCloseJob()
        {
            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_BREAKAWAY_OK
                }
            };
            int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
            AssignProcessToJobObject(job, GetCurrentProcess());
            return job;
        }

        // raw CreateProcessW with CREATE_NO_WINDOW | CREATE_BREAKAWAY_FROM_JOB -- escapes the kill-on-close job so the spawned process (relauncher/installer/uninstaller/transcode-poll) survives this process exiting. ProcessStartInfo has no equivalent to CREATE_BREAKAWAY_FROM_JOB, so this raw P/Invoke path is required. returns spawned pid, or 0 on failure.
        public static int SpawnDetached(string commandLine, string workingDirectory)
        {
            var si = new STARTUPINFOW { cb = Marshal.SizeOf<STARTUPINFOW>(), dwFlags = STARTF_USESHOWWINDOW, wShowWindow = SW_HIDE };
            var cmd = new StringBuilder(commandLine);
            bool ok = CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                CREATE_NO_WINDOW | CREATE_BREAKAWAY_FROM_JOB, IntPtr.Zero,
                string.IsNullOrEmpty(workingDirectory) ? null : workingDirectory, ref si, out PROCESS_INFORMATION pi);
            if (!ok) return 0;
            int pid = pi.dwProcessId;
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            return pid;
        }

        // PROCESS_QUERY_LIMITED_INFORMATION works cross-integrity-level (non-elevated helper watching an elevated obs), unlike SYNCHRONIZE access.
        public static IntPtr OpenParentForSync(uint pid) => OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);

        public static bool ParentExited(IntPtr handle)
        {
            if (!GetExitCodeProcess(handle, out uint exitCode)) return true;
            return exitCode != STILL_ACTIVE;
        }

        public static void CloseParentHandle(IntPtr handle)
        {
            try { CloseHandle(handle); } catch (Exception ex) when (ex is SEHException) { }
        }

        private const uint MOVEFILE_REPLACE_EXISTING = 0x1;

        // atomic same-volume rename-with-overwrite. net48s File.Move has no overwrite option (unlike .NET Core), and a manual delete-then-move reopens the crash/race window the atomic-write callers are specifically trying to close.
        public static void MoveFileReplace(string src, string dest)
        {
            if (!MoveFileEx(src, dest, MOVEFILE_REPLACE_EXISTING))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        // same-volume ntfs hard link -- shares the underlying file content, no extra disk, an in-place update to the target is picked up automatically. net48 has no managed api for this (unlike New-Item -ItemType HardLink in PS, which is really just this same syscall).
        public static void CreateHardLink(string newLinkPath, string existingFilePath)
        {
            if (!CreateHardLinkW(newLinkPath, existingFilePath, IntPtr.Zero))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        private static readonly object TaskbarLock = new object();
        private static ITaskbarList _taskbarList;

        private static void EnsureTaskbarList()
        {
            if (_taskbarList != null) return;
            _taskbarList = (ITaskbarList)new CTaskbarList();
            _taskbarList.HrInit();
        }

        private static void AddTaskbarTab(IntPtr hWnd)
        {
            lock (TaskbarLock)
            {
                try { EnsureTaskbarList(); _taskbarList.AddTab(hWnd); }
                catch (COMException) { }
            }
        }

        private static void RemoveTaskbarTab(IntPtr hWnd)
        {
            lock (TaskbarLock)
            {
                if (_taskbarList == null) return;
                try { _taskbarList.DeleteTab(hWnd); }
                catch (COMException) { }
            }
        }
    }
}

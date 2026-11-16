using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ReplayKitHelper
{
    // window-management primitives for the discord share-preview projector: find/open/close/park/style an obs windowed-projector hwnd. a few externs repeat ones in Native.cs on purpose so this class stays self-contained
    internal static class ProjectorNative
    {
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowText(IntPtr hWnd, string text);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(IntPtr hWnd, out uint key, out byte alpha, out uint flags);
        [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr hWnd, IntPtr region);
        [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
        private class CProjectorTaskbarList { }
        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("56FDF342-FD6D-11d0-958A-006097C9A090")]
        private interface IProjectorTaskbarList
        {
            void HrInit();
            void AddTab(IntPtr hwnd);
            void DeleteTab(IntPtr hwnd);
            void ActivateTab(IntPtr hwnd);
            void SetActiveAlt(IntPtr hwnd);
        }

        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const int GWLP_HWNDPARENT = -8;
        private const uint GW_OWNER = 4;
        private const int SW_SHOWNOACTIVATE = 4;
        private const int SW_RESTORE = 9;
        private const uint WM_CLOSE = 0x0010;
        private const long WS_CAPTION = 0x00C00000L;
        private const long WS_THICKFRAME = 0x00040000L;
        private const long WS_SYSMENU = 0x00080000L;
        private const long WS_MINIMIZEBOX = 0x00020000L;
        private const long WS_MAXIMIZEBOX = 0x00010000L;
        private const long WS_EX_TOOLWINDOW = 0x00000080L;
        private const long WS_EX_APPWINDOW = 0x00040000L;
        private const long WS_EX_LAYERED = 0x00080000L;
        private const long WS_EX_TRANSPARENT = 0x00000020L;
        private const long WS_EX_TOPMOST = 0x00000008L;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint LWA_ALPHA = 0x00000002;
        private const byte ParkedAlpha = 1;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        private static readonly IntPtr HWND_TOP = IntPtr.Zero;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        public static bool IsProjectorWindow(long hwndValue)
        {
            long main;
            lock (Server.State.IpcLock)
            {
                main = Server.State.ObsMainWindowHwnd;
                if (hwndValue == 0 || main == 0 || hwndValue == main || !Server.State.IpcClientConnected ||
                    Server.State.ProjectorHwnds == null || !Server.State.ProjectorHwnds.Contains(hwndValue) ||
                    (DateTime.UtcNow - Server.State.ProjectorHwndsAtUtc).TotalSeconds > 2) return false;
            }
            GetWindowThreadProcessId(new IntPtr(main), out uint mainProcess);
            GetWindowThreadProcessId(new IntPtr(hwndValue), out uint projectorProcess);
            return mainProcess != 0 && mainProcess == projectorProcess;
        }

        private static bool EnsureNormalAppWindow(IntPtr hWnd)
        {
            if (!IsProjectorWindow(hWnd.ToInt64())) return false;
            try { SetWindowLongPtr(hWnd, GWLP_HWNDPARENT, IntPtr.Zero); } catch (System.ComponentModel.Win32Exception) { }
            long style = GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();
            long next = (style | WS_EX_APPWINDOW) & ~WS_EX_TOOLWINDOW;
            if (next != style) SetWindowLongPtr(hWnd, GWL_EXSTYLE, new IntPtr(next));
            return SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }

        public static bool PreParkProjectorWindow(long hwndValue, string title)
        {
            return RunWithTimeout(cancel =>
            {
                IntPtr hWnd = new IntPtr(hwndValue);
                if (cancel.IsCancellationRequested || !IsProjectorWindow(hwndValue)) return false;
                if (!SetProjectorGhosted(hwndValue, true, 0)) return false;
                if (cancel.IsCancellationRequested) return false;
                if (!string.IsNullOrWhiteSpace(title) && !SetWindowText(hWnd, title)) return false;
                if (cancel.IsCancellationRequested) return false;
                return EnsureNormalAppWindow(hWnd);
            }, 1500);
        }

        private static bool IsObsFamilyProcess(uint pid, out string processName)
        {
            processName = "";
            if (pid == 0) return false;
            try
            {
                using (var p = Process.GetProcessById((int)pid))
                {
                    processName = p.ProcessName.ToLowerInvariant();
                    return processName == "obs64" || processName == "obs32" || processName == "obs" || processName.StartsWith("obs-");
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException) { return false; }
        }

        // only the projector carrying the exact share title counts: replaykit opens it and names it straight away, and obs is set not to bring projectors back on start (SaveProjectors=false in the user.ini replaykit ships), so any other projector is one the user opened (a source projector of Game Capture shows the game without the overlay), and a looser match parked it as the share or closed it on turning share preview off. no visibility filter on purpose -- a layered projector has stopped reporting itself as "visible" while still alive and still playing its own audio mix to the default device
        public static long FindProjectorWindow(string shareTitle)
        {
            if (string.IsNullOrWhiteSpace(shareTitle)) return 0;
            long found = 0;
            EnumWindows((hWnd, lParam) =>
            {
                if (!IsProjectorWindow(hWnd.ToInt64())) return true;
                var sb = new StringBuilder(512);
                GetWindowText(hWnd, sb, sb.Capacity);
                if (!string.Equals(sb.ToString(), shareTitle, StringComparison.Ordinal)) return true;
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (!IsObsFamilyProcess(pid, out _)) return true;
                found = hWnd.ToInt64();
                return false;
            }, IntPtr.Zero);
            return found;
        }

        // self-healing sweep for the accumulating-hidden-projectors bug: the keep-alive tick opens a replacement whenever it cant find the parked projector, but never closed the original if it was still alive elsewhere -- only matches windows carrying the exact branded title (set by this classs own SetWindowText calls), so it can never touch a projector the user opened themselves from OBSs own menu. keepHwndValue=0 closes every match (used when disabling share preview); nonzero keeps that hwnd and closes the rest (used right after find/open, so at most one of these is ever alive between keep-alive ticks).
        public static int CloseDuplicateProjectorWindows(string exactTitle, long keepHwndValue)
        {
            if (OperationPending() || string.IsNullOrWhiteSpace(exactTitle)) return 0;
            IntPtr keep = new IntPtr(keepHwndValue);
            int closed = 0;
            EnumWindows((hWnd, lParam) =>
            {
                if (keep != IntPtr.Zero && hWnd == keep) return true;
                if (!IsProjectorWindow(hWnd.ToInt64())) return true;
                var sb = new StringBuilder(512);
                GetWindowText(hWnd, sb, sb.Capacity);
                if (!string.Equals(sb.ToString(), exactTitle, StringComparison.Ordinal)) return true;
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (!IsObsFamilyProcess(pid, out _)) return true;
                PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                closed++;
                return true;
            }, IntPtr.Zero);
            return closed;
        }

        public static string GetWindowTitle(long hwndValue)
        {
            IntPtr hWnd = new IntPtr(hwndValue);
            if (hWnd == IntPtr.Zero) return "";
            var sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static bool SetProjectorChrome(long hwndValue, bool borderless)
        {
            IntPtr hWnd = new IntPtr(hwndValue);
            if (!IsProjectorWindow(hwndValue)) return false;
            long style = GetWindowLongPtr(hWnd, GWL_STYLE).ToInt64();
            long chromeMask = WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;
            long next = borderless ? (style & ~chromeMask) : (style | chromeMask);
            if (next == style) return true;
            IntPtr previous = SetWindowLongPtr(hWnd, GWL_STYLE, new IntPtr(next));
            if (previous == IntPtr.Zero) return false;
            return SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }

        private static readonly object OperationGate = new object();
        private static Task<bool> pendingOperation;

        public static bool OperationPending()
        {
            lock (OperationGate) return pendingOperation != null && !pendingOperation.IsCompleted;
        }

        // A timed-out Win32 call can still be running. Keep its slot until it finishes, and stop the remaining steps when it returns.
        private static bool RunWithTimeout(Func<CancellationToken, bool> action, int timeoutMs)
        {
            var cancellation = new CancellationTokenSource();
            Task<bool> task;
            lock (OperationGate)
            {
                if (pendingOperation != null && !pendingOperation.IsCompleted)
                {
                    cancellation.Dispose();
                    return false;
                }
                task = Task.Run(() => action(cancellation.Token));
                pendingOperation = task;
            }
            try
            {
                if (task.Wait(timeoutMs)) return task.Result;
                cancellation.Cancel();
                return false;
            }
            finally
            {
                task.ContinueWith(done =>
                {
                    if (done.IsFaulted) Log.Write("warn: Projector window operation failed: " + done.Exception.GetBaseException().Message);
                    cancellation.Dispose();
                }, TaskScheduler.Default);
            }
        }

        public static bool SetProjectorTaskbarHidden(long hwndValue, bool hidden)
        {
            return RunWithTimeout(cancel =>
            {
                if (cancel.IsCancellationRequested) return false;
                return SetProjectorTaskbarHiddenCore(hwndValue, hidden, cancel);
            }, 1500);
        }

        private static bool SetProjectorTaskbarHiddenCore(long hwndValue, bool hidden, CancellationToken cancel)
        {
            IntPtr hWnd = new IntPtr(hwndValue);
            if (!IsProjectorWindow(hwndValue)) return false;
            if (!EnsureNormalAppWindow(hWnd)) return false;
            if (cancel.IsCancellationRequested) return false;
            try
            {
                var taskbarList = (IProjectorTaskbarList)new CProjectorTaskbarList();
                taskbarList.HrInit();
                if (cancel.IsCancellationRequested) return false;
                if (hidden) taskbarList.DeleteTab(hWnd);
                else { taskbarList.AddTab(hWnd); taskbarList.SetActiveAlt(hWnd); }
                return !cancel.IsCancellationRequested;
            }
            catch (COMException) { return false; }
        }

        public static bool ClearProjectorVisibleRegion(long hwndValue)
        {
            IntPtr hWnd = new IntPtr(hwndValue);
            if (!IsProjectorWindow(hwndValue)) return false;
            return SetWindowRgn(hWnd, IntPtr.Zero, true) != 0;
        }

        public static bool SetProjectorVisibleRegion(long hwndValue, int x, int y, int width, int height)
        {
            IntPtr hWnd = new IntPtr(hwndValue);
            if (!IsProjectorWindow(hwndValue) || width < 1 || height < 1 || x < 0 || y < 0) return false;
            IntPtr region = CreateRectRgn(x, y, x + width, y + height);
            if (region == IntPtr.Zero) return false;
            if (SetWindowRgn(hWnd, region, true) == 0) { DeleteObject(region); return false; }
            return true;
        }

        public static bool SetProjectorGhosted(long hwndValue, bool ghosted, byte alpha)
        {
            IntPtr hWnd = new IntPtr(hwndValue);
            if (!IsProjectorWindow(hwndValue)) return false;
            long style = GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();
            const long ghostMask = WS_EX_LAYERED;
            // never click-through: discord left the projector out of its screen picker while it had WS_EX_TRANSPARENT
            long next = ghosted
                ? ((style | ghostMask | WS_EX_APPWINDOW) & ~(WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT))
                : ((style & ~ghostMask) | WS_EX_APPWINDOW) & ~(WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT);
            if (next != style) SetWindowLongPtr(hWnd, GWL_EXSTYLE, new IntPtr(next));
            if (ghosted)
            {
                if (!SetLayeredWindowAttributes(hWnd, 0, alpha, LWA_ALPHA)) return false;
            }
            else
            {
                SetLayeredWindowAttributes(hWnd, 0, 255, LWA_ALPHA);
            }
            return SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }

        public static bool RestoreResizeTitleAndChrome(long hwndValue, string title, int x, int y, int width, int height, bool borderless)
        {
            return RunWithTimeout(cancel => RestoreResizeTitleAndChromeCore(hwndValue, title, x, y, width, height, borderless, cancel), 1500);
        }

        private static bool RestoreResizeTitleAndChromeCore(long hwndValue, string title, int x, int y, int width, int height, bool borderless, CancellationToken cancel)
        {
            IntPtr hWnd = new IntPtr(hwndValue);
            if (cancel.IsCancellationRequested || !IsProjectorWindow(hwndValue) || width < 1 || height < 1) return false;
            if (!SetProjectorGhosted(hwndValue, false, 255) || cancel.IsCancellationRequested) return false;
            ShowWindow(hWnd, SW_RESTORE);
            if (cancel.IsCancellationRequested) return false;
            if (!string.IsNullOrWhiteSpace(title)) SetWindowText(hWnd, title);
            if (cancel.IsCancellationRequested || !SetProjectorChrome(hwndValue, borderless)) return false;
            if (cancel.IsCancellationRequested || !ClearProjectorVisibleRegion(hwndValue)) return false;
            if (cancel.IsCancellationRequested) return false;
            if (!SetWindowPos(hWnd, HWND_NOTOPMOST, x, y, width, height, SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW)) return false;
            if (cancel.IsCancellationRequested) return false;
            if (!SetWindowPos(hWnd, HWND_TOP, x, y, width, height, SWP_FRAMECHANGED | SWP_SHOWWINDOW)) return false;
            if (cancel.IsCancellationRequested) return false;
            BringWindowToTop(hWnd);
            SetForegroundWindow(hWnd);
            return IsWindowVisible(hWnd) && !IsIconic(hWnd) && IsWindowAtRectAndTitle(hwndValue, title, x, y, width, height);
        }

        public static bool RestoreResizeTitleChromeAndRegion(long hwndValue, string title, int x, int y, int width, int height, bool borderless, int regionX, int regionY, int regionWidth, int regionHeight)
        {
            IntPtr hWnd = new IntPtr(hwndValue);
            if (!IsProjectorWindow(hwndValue) || width < 1 || height < 1) return false;
            if (!SetProjectorVisibleRegion(hwndValue, regionX, regionY, regionWidth, regionHeight)) return false;
            ShowWindow(hWnd, SW_RESTORE);
            ShowWindowAsync(hWnd, SW_RESTORE);
            if (!string.IsNullOrWhiteSpace(title)) SetWindowText(hWnd, title);
            if (!SetProjectorChrome(hwndValue, borderless)) return false;
            if (!SetWindowPos(hWnd, HWND_NOTOPMOST, x, y, width, height, SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW)) return false;
            if (!SetWindowPos(hWnd, HWND_TOP, x, y, width, height, SWP_FRAMECHANGED | SWP_SHOWWINDOW)) return false;
            return SetProjectorVisibleRegion(hwndValue, regionX, regionY, regionWidth, regionHeight);
        }

        public static bool RestoreResizeTitleChromeGhosted(long hwndValue, string title, int x, int y, int width, int height, bool borderless)
        {
            return RunWithTimeout(cancel => RestoreResizeTitleChromeGhostedCore(hwndValue, title, x, y, width, height, borderless, cancel), 1500);
        }

        private static bool RestoreResizeTitleChromeGhostedCore(long hwndValue, string title, int x, int y, int width, int height, bool borderless, CancellationToken cancel)
        {
            IntPtr hWnd = new IntPtr(hwndValue);
            if (cancel.IsCancellationRequested || !IsProjectorWindow(hwndValue) || width < 1 || height < 1) return false;
            if (IsProjectorParked(hwndValue, title, x, y, width, height, borderless)) return true;
            if (!SetProjectorGhosted(hwndValue, true, 0) || cancel.IsCancellationRequested) return false;
            if (!string.IsNullOrWhiteSpace(title)) SetWindowText(hWnd, title);
            if (cancel.IsCancellationRequested || !SetProjectorChrome(hwndValue, borderless)) return false;
            if (cancel.IsCancellationRequested || !ClearProjectorVisibleRegion(hwndValue)) return false;
            if (cancel.IsCancellationRequested) return false;
            ShowWindow(hWnd, SW_SHOWNOACTIVATE);
            if (cancel.IsCancellationRequested) return false;
            // keep it near the top of the discord picker and at alpha zero until the move is confirmed
            if (!SetWindowPos(hWnd, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW)) return false;
            if (cancel.IsCancellationRequested || !IsWindowAtRectAndTitle(hwndValue, title, x, y, width, height)) return false;
            if (!SetProjectorGhosted(hwndValue, true, ParkedAlpha)) return false;
            return !cancel.IsCancellationRequested && IsProjectorParked(hwndValue, title, x, y, width, height, borderless);
        }

        public static bool IsProjectorParked(long hwndValue, string title, int x, int y, int width, int height, bool borderless)
        {
            IntPtr hWnd = new IntPtr(hwndValue);
            if (!IsWindowAtRectAndTitle(hwndValue, title, x, y, width, height) || !IsWindowVisible(hWnd) || IsIconic(hWnd)) return false;
            if (!GetClientRect(hWnd, out RECT client) || client.Right < 1 || client.Bottom < 1) return false;
            long style = GetWindowLongPtr(hWnd, GWL_STYLE).ToInt64();
            long chrome = WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;
            if (borderless && ((style & chrome) != 0 || client.Right != width || client.Bottom != height)) return false;
            long extended = GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();
            long required = WS_EX_LAYERED | WS_EX_APPWINDOW | WS_EX_TOPMOST;
            if ((extended & required) != required || (extended & (WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT)) != 0 || GetWindow(hWnd, GW_OWNER) != IntPtr.Zero) return false;
            if (!GetLayeredWindowAttributes(hWnd, out _, out byte alpha, out uint flags) || alpha != ParkedAlpha || (flags & LWA_ALPHA) == 0) return false;
            IntPtr region = CreateRectRgn(0, 0, 0, 0);
            if (region == IntPtr.Zero) return false;
            try { return GetWindowRgn(hWnd, region) == 0; }
            finally { DeleteObject(region); }
        }

        public static bool IsWindowAtRectAndTitle(long hwndValue, string title, int x, int y, int width, int height)
        {
            IntPtr hWnd = new IntPtr(hwndValue);
            if (!IsProjectorWindow(hwndValue) || width < 1 || height < 1) return false;
            if (!GetWindowRect(hWnd, out RECT rect)) return false;
            if (rect.Left != x || rect.Top != y || (rect.Right - rect.Left) != width || (rect.Bottom - rect.Top) != height) return false;
            if (!string.IsNullOrWhiteSpace(title) && !string.Equals(GetWindowTitle(hwndValue), title, StringComparison.Ordinal)) return false;
            return true;
        }

        public static bool CloseWindow(long hwndValue)
        {
            IntPtr hWnd = new IntPtr(hwndValue);
            if (OperationPending() || !IsProjectorWindow(hwndValue)) return false;
            return PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
    }
}

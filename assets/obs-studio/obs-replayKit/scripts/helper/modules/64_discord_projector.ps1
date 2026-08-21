if (-not ('ReplayKitProjectorNativeV2' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

public static class ReplayKitProjectorNativeV2 {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SetWindowText(IntPtr hWnd, string text);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);
    [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObject);
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtr", SetLastError=true)] static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint="SetWindowLongPtr", SetLastError=true)] static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll", EntryPoint="GetWindowLong", SetLastError=true)] static extern int GetWindowLong32(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint="SetWindowLong", SetLastError=true)] static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    public class ReplayKitProjectorTaskbarList { }
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("56FDF342-FD6D-11d0-958A-006097C9A090")]
    public interface IReplayKitProjectorTaskbarList {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
    }

    const int GWL_STYLE = -16;
    const int GWL_EXSTYLE = -20;
    const int GWLP_HWNDPARENT = -8;
    const uint GW_OWNER = 4;
    const int SW_HIDE = 0;
    const int SW_SHOWNOACTIVATE = 4;
    const int SW_RESTORE = 9;
    const uint WM_CLOSE = 0x0010;
    const long WS_CAPTION = 0x00C00000L;
    const long WS_THICKFRAME = 0x00040000L;
    const long WS_SYSMENU = 0x00080000L;
    const long WS_MINIMIZEBOX = 0x00020000L;
    const long WS_MAXIMIZEBOX = 0x00010000L;
    const long WS_EX_TOOLWINDOW = 0x00000080L;
    const long WS_EX_APPWINDOW = 0x00040000L;
    const long WS_EX_LAYERED = 0x00080000L;
    const uint SWP_NOSIZE = 0x0001;
    const uint SWP_NOMOVE = 0x0002;
    const uint SWP_NOZORDER = 0x0004;
    const uint SWP_NOACTIVATE = 0x0010;
    const uint SWP_FRAMECHANGED = 0x0020;
    const uint SWP_SHOWWINDOW = 0x0040;
    const uint LWA_ALPHA = 0x00000002;
    static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
    static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    static readonly IntPtr HWND_TOP = IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    static IntPtr GetWindowLongPtrCompat(IntPtr hWnd, int nIndex) {
        if (IntPtr.Size == 8) return GetWindowLongPtr64(hWnd, nIndex);
        return new IntPtr(GetWindowLong32(hWnd, nIndex));
    }

    static IntPtr SetWindowLongPtrCompat(IntPtr hWnd, int nIndex, IntPtr value) {
        if (IntPtr.Size == 8) return SetWindowLongPtr64(hWnd, nIndex, value);
        return new IntPtr(SetWindowLong32(hWnd, nIndex, unchecked((int)value.ToInt64())));
    }

    static bool EnsureNormalAppWindow(IntPtr hWnd) {
        if (hWnd == IntPtr.Zero) return false;
        try { SetWindowLongPtrCompat(hWnd, GWLP_HWNDPARENT, IntPtr.Zero); } catch {}
        long style = GetWindowLongPtrCompat(hWnd, GWL_EXSTYLE).ToInt64();
        long next = (style | WS_EX_APPWINDOW) & ~WS_EX_TOOLWINDOW;
        if (next != style) {
            SetWindowLongPtrCompat(hWnd, GWL_EXSTYLE, new IntPtr(next));
        }
        return SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    public static bool PreParkProjectorWindow(long hwndValue, string title) {
        IntPtr hWnd = new IntPtr(hwndValue);
        if (hWnd == IntPtr.Zero) return false;
        ShowWindow(hWnd, SW_HIDE);
        if (!String.IsNullOrWhiteSpace(title)) {
            SetWindowText(hWnd, title);
        }
        EnsureNormalAppWindow(hWnd);
        bool positioned = SetWindowPos(hWnd, IntPtr.Zero, -32000, -32000, 16, 16,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        ShowWindow(hWnd, SW_SHOWNOACTIVATE);
        return positioned;
    }

    static bool IsObsFamilyProcess(uint pid, out string processName) {
        processName = "";
        if (pid == 0) return false;
        try {
            using (Process p = Process.GetProcessById((int)pid)) {
                processName = p.ProcessName.ToLowerInvariant();
                return processName == "obs64" ||
                       processName == "obs32" ||
                       processName == "obs" ||
                       processName.StartsWith("obs-");
            }
        } catch {
            return false;
        }
    }

    static int ProjectorScore(string title, string processName, string titleHint) {
        if (String.IsNullOrWhiteSpace(title)) return 0;
        int score = 0;
        if (!String.IsNullOrWhiteSpace(titleHint) &&
            title.IndexOf(titleHint, StringComparison.OrdinalIgnoreCase) >= 0) {
            score = 100;
        } else if (title.IndexOf("Windowed Projector", StringComparison.OrdinalIgnoreCase) >= 0) {
            score = 80;
        } else if (title.IndexOf("Projector", StringComparison.OrdinalIgnoreCase) >= 0) {
            score = 60;
        }
        if (score > 0 && processName == "obs64") score += 10;
        return score;
    }

    public static long FindProjectorWindow(string titleHint) {
        long best = 0;
        int bestScore = 0;
        EnumWindows(delegate(IntPtr hWnd, IntPtr lParam) {
            // no IsWindowVisible filter here on purpose -- the parked/ghosted projector is a layered alpha-0 window that has, in the past, stopped reporting itself as "visible" while still alive and still playing its own audio mix to the default device; process+title scoring below is tight enough on its own.
            StringBuilder sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, sb.Capacity);
            string title = sb.ToString();
            uint pid = 0;
            GetWindowThreadProcessId(hWnd, out pid);
            string processName;
            bool isObs = IsObsFamilyProcess(pid, out processName);
            if (!isObs) return true;
            int score = ProjectorScore(title, processName, titleHint);
            if (score > bestScore) {
                bestScore = score;
                best = hWnd.ToInt64();
            }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    static bool IsObsMainProcess(uint pid, out string processName) {
        processName = "";
        if (pid == 0) return false;
        try {
            using (Process p = Process.GetProcessById((int)pid)) {
                processName = p.ProcessName.ToLowerInvariant();
                return processName == "obs64" || processName == "obs32" || processName == "obs";
            }
        } catch {
            return false;
        }
    }

    // qts windows backend registers a distinct window class per top-level window, shaped "Qt" + an internal version encoding + a suffix. a real application-level window (main window, a dialog, a projector) gets a QWindowIcon/QWindowToolSaveBits-style suffix containing "QWindow"; qts OWN internal plumbing -- the tray balloon message window, theme/screen-change observer windows, and the custom-titlebar strip every dock widget carries -- gets a differently-shaped class name that never contains that substring. confirmed empirically 2026-08-19 against a real running obs: real windows were "Qt6111QWindowIcon"/"Qt6111QWindowToolSaveBits", noise was "_q_titlebar" (dozens of these, one per dock), "Qt6111TrayIconMessageWindowClass", "Qt6111ThemeChangeObserverWindow", "Qt6111ScreenChangeObserverWindow" -- none of those four contain "QWindow". this is the filter FindAllObsWindows below actually needs: title text cant do it (obs localizes every real windows title, including "Windowed Projector", so a non-english install has no english substring to match -- confirmed 2026-08-18 this is why a non-english users projector could never be found and kept getting duplicated) and IsWindowVisible cant either (obss own main window reports not-visible when minimized to tray, same as this app runs by default, so a real window failing a visibility check is normal here, not a sign it isnt real).
    //
    // second, independent check on top of the class name: a genuinely freestanding top-level window (a projector, obss own main window) has no owner. every OTHER real dialog obs itself spawns (Stats, Output Timer, WebSocket Server Settings, input-overlays config window, ...) is owned by the main window -- confirmed empirically 2026-08-19 against all four. also confirmed a projector has owner=0 from the moment obs creates it, before our own code ever touches it (EnsureNormalAppWindow clears GWLP_HWNDPARENT later on, during parking, so that couldnt have been the reason -- checked by opening a projector directly over obs-websocket and reading its owner immediately, with none of our park/hide code in the loop at all). this catches what the class-name check alone cant: some other real obs dialog opening in the same narrow window we happen to be polling in.
    static bool IsRealQtTopLevelWindow(IntPtr hWnd) {
        StringBuilder sb = new StringBuilder(256);
        if (GetClassName(hWnd, sb, sb.Capacity) == 0) return false;
        string cls = sb.ToString();
        if (!cls.StartsWith("Qt", StringComparison.Ordinal) || cls.IndexOf("QWindow", StringComparison.Ordinal) < 0) return false;
        return GetWindow(hWnd, GW_OWNER) == IntPtr.Zero;
    }

    // every real top-level window belonging to obss own main process (not helper processes like obs-browser-page, which the obs- prefix match in IsObsFamilyProcess would also catch, and not the dozens of internal-only windows IsRealQtTopLevelWindow filters out) -- used to snapshot "what already exists" before asking obs to open a projector, so a slow-to-appear one can later be told apart from something that was already there. deliberately NOT title-filtered -- see IsRealQtTopLevelWindows comment for why. safety against touching something we shouldnt comes from the baseline diff in Open-ReplayKitDiscordProjectorIfMissing, not from filtering here: a window that already existed before we asked obs for one -- the users own projector, obss main window, an open Stats/Output-Timer dialog, anything else -- is never touched regardless of what this returns, only ones that appear afterward are ever treated as candidates.
    public static long[] FindAllObsWindows() {
        List<long> matches = new List<long>();
        EnumWindows(delegate(IntPtr hWnd, IntPtr lParam) {
            uint pid = 0;
            GetWindowThreadProcessId(hWnd, out pid);
            string processName;
            if (!IsObsMainProcess(pid, out processName)) return true;
            if (!IsRealQtTopLevelWindow(hWnd)) return true;
            matches.Add(hWnd.ToInt64());
            return true;
        }, IntPtr.Zero);
        return matches.ToArray();
    }

    // self-healing sweep for the accumulating-hidden-projectors bug: the keep-alive tick opens a replacement whenever it cant find our parked projector, but never closed the original if it was still alive elsewhere -- only matches windows carrying our EXACT branded title (set by our own SetWindowText calls in this class), so it can never touch a projector the user opened themselves from OBSs own menu. keepHwndValue=0 closes every match (used when disabling Share Preview); nonzero keeps that hwnd and closes the rest (used right after Find/open, so at most one of ours is ever alive between keep-alive ticks).
    public static int CloseDuplicateProjectorWindows(string exactTitle, long keepHwndValue) {
        if (String.IsNullOrWhiteSpace(exactTitle)) return 0;
        IntPtr keep = new IntPtr(keepHwndValue);
        int closed = 0;
        EnumWindows(delegate(IntPtr hWnd, IntPtr lParam) {
            if (keep != IntPtr.Zero && hWnd == keep) return true;
            StringBuilder sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, sb.Capacity);
            if (!String.Equals(sb.ToString(), exactTitle, StringComparison.Ordinal)) return true;
            uint pid = 0;
            GetWindowThreadProcessId(hWnd, out pid);
            string processName;
            if (!IsObsFamilyProcess(pid, out processName)) return true;
            PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            closed++;
            return true;
        }, IntPtr.Zero);
        return closed;
    }

    public static string GetWindowTitle(long hwndValue) {
        IntPtr hWnd = new IntPtr(hwndValue);
        if (hWnd == IntPtr.Zero) return "";
        StringBuilder sb = new StringBuilder(512);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static bool SetProjectorChrome(long hwndValue, bool borderless) {
        IntPtr hWnd = new IntPtr(hwndValue);
        if (hWnd == IntPtr.Zero) return false;
        long style = GetWindowLongPtrCompat(hWnd, GWL_STYLE).ToInt64();
        long chromeMask = WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;
        long next = borderless ? (style & ~chromeMask) : (style | chromeMask);
        if (next == style) return true;
        IntPtr previous = SetWindowLongPtrCompat(hWnd, GWL_STYLE, new IntPtr(next));
        if (previous == IntPtr.Zero) return false;
        return SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    // SetWindowPos/SetWindowLong/the shell taskbar-list com calls below all synchronously message the target windows owning thread (obss ui thread) and block until it services them, so a busy/slow obs ui thread used to freeze every dock over http, not just the projector. running the real work on a background task and giving up after timeoutMs bounds how long the CALLER waits -- but task.Wait() timing out does not cancel the task, so a win32 call blocked on a window that never answers keeps running forever and every timed-out firing abandons one more permanently-stuck threadpool thread; this fires at startup and every keep-alive tick for as long as discord projector mode is on, so left unguarded that leak is unbounded -- confirmed the hard way (see the inFlight guards below) after "fixed" turned into "fine for a few minutes, then the whole helper degrades" once enough of these had piled up.
    static bool RunWithTimeout(Func<bool> action, int timeoutMs) {
        var task = System.Threading.Tasks.Task.Run(action);
        return task.Wait(timeoutMs) && task.Result;
    }

    static volatile bool s_reparkInFlight = false;
    static volatile bool s_taskbarInFlight = false;

    public static bool SetProjectorTaskbarHidden(long hwndValue, bool hidden) {
        // an earlier call that timed out from the callers side may still be stuck on the real win32 work -- skip this tick rather than abandoning a second thread on top of it.
        if (s_taskbarInFlight) return false;
        s_taskbarInFlight = true;
        bool result = RunWithTimeout(() => {
            try { return SetProjectorTaskbarHiddenCore(hwndValue, hidden); }
            finally { s_taskbarInFlight = false; }
        }, 1500);
        return result;
    }

    static bool SetProjectorTaskbarHiddenCore(long hwndValue, bool hidden) {
        IntPtr hWnd = new IntPtr(hwndValue);
        if (hWnd == IntPtr.Zero) return false;
        bool styled = EnsureNormalAppWindow(hWnd);
        if (!styled) return false;
        try {
            IReplayKitProjectorTaskbarList taskbarList =
                (IReplayKitProjectorTaskbarList)new ReplayKitProjectorTaskbarList();
            taskbarList.HrInit();
            if (hidden) {
                taskbarList.DeleteTab(hWnd);
            } else {
                taskbarList.AddTab(hWnd);
                taskbarList.SetActiveAlt(hWnd);
            }
            return true;
        } catch {
            return hidden;
        }
    }

    public static bool ClearProjectorVisibleRegion(long hwndValue) {
        IntPtr hWnd = new IntPtr(hwndValue);
        if (hWnd == IntPtr.Zero) return false;
        return SetWindowRgn(hWnd, IntPtr.Zero, true) != 0;
    }

    public static bool SetProjectorVisibleRegion(long hwndValue, int x, int y, int width, int height) {
        IntPtr hWnd = new IntPtr(hwndValue);
        if (hWnd == IntPtr.Zero || width < 1 || height < 1 || x < 0 || y < 0) return false;
        IntPtr region = CreateRectRgn(x, y, x + width, y + height);
        if (region == IntPtr.Zero) return false;
        if (SetWindowRgn(hWnd, region, true) == 0) {
            DeleteObject(region);
            return false;
        }
        return true;
    }

    public static bool SetProjectorGhosted(long hwndValue, bool ghosted, byte alpha) {
        IntPtr hWnd = new IntPtr(hwndValue);
        if (hWnd == IntPtr.Zero) return false;
        long style = GetWindowLongPtrCompat(hWnd, GWL_EXSTYLE).ToInt64();
        long ghostMask = WS_EX_LAYERED;
        long next = ghosted
            ? ((style | ghostMask | WS_EX_APPWINDOW) & ~WS_EX_TOOLWINDOW)
            : ((style & ~ghostMask) | WS_EX_APPWINDOW) & ~WS_EX_TOOLWINDOW;
        if (next != style) {
            SetWindowLongPtrCompat(hWnd, GWL_EXSTYLE, new IntPtr(next));
        }
        if (ghosted) {
            if (!SetLayeredWindowAttributes(hWnd, 0, alpha, LWA_ALPHA)) return false;
        } else {
            SetLayeredWindowAttributes(hWnd, 0, 255, LWA_ALPHA);
        }
        return SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    public static bool RestoreResizeTitleAndChrome(long hwndValue, string title, int x, int y, int width, int height, bool borderless) {
        IntPtr hWnd = new IntPtr(hwndValue);
        if (hWnd == IntPtr.Zero || width < 1 || height < 1) return false;
        SetProjectorGhosted(hwndValue, false, 255);
        ShowWindow(hWnd, SW_RESTORE);
        ShowWindowAsync(hWnd, SW_RESTORE);
        if (!String.IsNullOrWhiteSpace(title)) {
            SetWindowText(hWnd, title);
        }
        if (!SetProjectorChrome(hwndValue, borderless)) return false;
        ClearProjectorVisibleRegion(hwndValue);
        if (!SetWindowPos(hWnd, HWND_NOTOPMOST, x, y, width, height,
            SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW)) {
            return false;
        }
        if (!SetWindowPos(hWnd, HWND_TOP, x, y, width, height,
            SWP_FRAMECHANGED | SWP_SHOWWINDOW)) {
            return false;
        }
        BringWindowToTop(hWnd);
        SetForegroundWindow(hWnd);
        return true;
    }

    public static bool RestoreResizeTitleChromeAndRegion(long hwndValue, string title, int x, int y, int width, int height, bool borderless, int regionX, int regionY, int regionWidth, int regionHeight) {
        IntPtr hWnd = new IntPtr(hwndValue);
        if (hWnd == IntPtr.Zero || width < 1 || height < 1) return false;
        if (!SetProjectorVisibleRegion(hwndValue, regionX, regionY, regionWidth, regionHeight)) return false;
        ShowWindow(hWnd, SW_RESTORE);
        ShowWindowAsync(hWnd, SW_RESTORE);
        if (!String.IsNullOrWhiteSpace(title)) {
            SetWindowText(hWnd, title);
        }
        if (!SetProjectorChrome(hwndValue, borderless)) return false;
        if (!SetWindowPos(hWnd, HWND_NOTOPMOST, x, y, width, height,
            SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW)) {
            return false;
        }
        if (!SetWindowPos(hWnd, HWND_TOP, x, y, width, height,
            SWP_FRAMECHANGED | SWP_SHOWWINDOW)) {
            return false;
        }
        return SetProjectorVisibleRegion(hwndValue, regionX, regionY, regionWidth, regionHeight);
    }

    public static bool RestoreResizeTitleChromeGhosted(long hwndValue, string title, int x, int y, int width, int height, bool borderless) {
        if (s_reparkInFlight) return false;
        s_reparkInFlight = true;
        return RunWithTimeout(() => {
            try { return RestoreResizeTitleChromeGhostedCore(hwndValue, title, x, y, width, height, borderless); }
            finally { s_reparkInFlight = false; }
        }, 1500);
    }

    static bool RestoreResizeTitleChromeGhostedCore(long hwndValue, string title, int x, int y, int width, int height, bool borderless) {
        IntPtr hWnd = new IntPtr(hwndValue);
        if (hWnd == IntPtr.Zero || width < 1 || height < 1) return false;
        if (!SetProjectorGhosted(hwndValue, true, 0)) return false;
        if (!String.IsNullOrWhiteSpace(title)) {
            SetWindowText(hWnd, title);
        }
        if (!SetProjectorChrome(hwndValue, borderless)) return false;
        // park topmost so discord lists the projector near the top of its picker -- safe becuase the window is alpha 0 layered, which is click-thru and invisible
        if (!SetWindowPos(hWnd, HWND_TOPMOST, x, y, width, height,
            SWP_NOACTIVATE | SWP_FRAMECHANGED)) {
            return false;
        }
        ClearProjectorVisibleRegion(hwndValue);
        ShowWindow(hWnd, SW_SHOWNOACTIVATE);
        ShowWindowAsync(hWnd, SW_SHOWNOACTIVATE);
        if (!SetWindowPos(hWnd, HWND_TOPMOST, x, y, width, height,
            SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW)) {
            return false;
        }
        return SetProjectorGhosted(hwndValue, true, 0);
    }

    public static bool IsWindowAtRectAndTitle(long hwndValue, string title, int x, int y, int width, int height) {
        IntPtr hWnd = new IntPtr(hwndValue);
        if (hWnd == IntPtr.Zero || width < 1 || height < 1) return false;
        RECT rect;
        if (!GetWindowRect(hWnd, out rect)) return false;
        if (rect.Left != x || rect.Top != y || (rect.Right - rect.Left) != width || (rect.Bottom - rect.Top) != height) {
            return false;
        }
        if (!String.IsNullOrWhiteSpace(title) && !String.Equals(GetWindowTitle(hwndValue), title, StringComparison.Ordinal)) {
            return false;
        }
        return true;
    }

    public static bool CloseWindow(long hwndValue) {
        IntPtr hWnd = new IntPtr(hwndValue);
        if (hWnd == IntPtr.Zero) return false;
        return PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }
}
'@
}

function Get-ReplayKitDiscordProjectorTitle([hashtable]$settings) {
    $default = 'OBS ReplayKit Discord Share'
    if ($null -eq $settings) { return $default }
    $value = ([string]$settings.discord_projector_title_hint).Trim()
    if ([string]::IsNullOrWhiteSpace($value) -or $value.Length -gt 128 -or $value -match '[\x00-\x1F]') {
        return $default
    }
    return $value
}

function Get-ReplayKitDiscordProjectorShareTitle([hashtable]$settings) {
    return (Get-ReplayKitDiscordProjectorTitle $settings) + ' (Projector - Desktop Audio)'
}

function Get-ReplayKitProjectorObjectInt($object, [string]$key) {
    if ($null -eq $object -or [string]::IsNullOrWhiteSpace($key)) { return 0 }
    if ($object -is [System.Collections.IDictionary] -and $object.Contains($key)) {
        $value = $object[$key]
    } else {
        $prop = $object.PSObject.Properties[$key]
        if (-not $prop) { return 0 }
        $value = $prop.Value
    }
    $n = 0
    if ([int]::TryParse(([string]$value), [ref]$n) -and $n -gt 0) { return $n }
    return 0
}

function Get-ReplayKitObsCanvasSize {
    $videoSettings = Invoke-ObsWebSocketRequest 'GetVideoSettings' $null 3000
    if (-not $videoSettings.ok) {
        return @{ ok = $false; message = "OBS video settings unavailable: $($videoSettings.message)" }
    }

    $baseWidth = Get-ReplayKitProjectorObjectInt $videoSettings.data 'baseWidth'
    $baseHeight = Get-ReplayKitProjectorObjectInt $videoSettings.data 'baseHeight'
    if ($baseWidth -gt 0 -and $baseHeight -gt 0) {
        return @{ ok = $true; width = $baseWidth; height = $baseHeight; source = 'obs_base_canvas' }
    }

    $outputWidth = Get-ReplayKitProjectorObjectInt $videoSettings.data 'outputWidth'
    $outputHeight = Get-ReplayKitProjectorObjectInt $videoSettings.data 'outputHeight'
    if ($outputWidth -gt 0 -and $outputHeight -gt 0) {
        return @{ ok = $true; width = $outputWidth; height = $outputHeight; source = 'obs_output' }
    }

    return @{ ok = $false; message = 'OBS video settings did not include a valid canvas or output size.' }
}

function Resolve-ReplayKitDiscordProjectorWindowSize([hashtable]$settings, [hashtable]$workArea, [bool]$preferSourceMonitor = $false) {
    $width = [int]$settings.discord_projector_width
    $height = [int]$settings.discord_projector_height
    $source = 'settings'
    if ($width -gt 0 -and $height -gt 0) {
        return @{ ok = $true; width = $width; height = $height; source = $source; warnings = @() }
    }

    $sourceWidth = if ($workArea.ContainsKey('source_bounds_width')) { [int]$workArea.source_bounds_width } else { 0 }
    $sourceHeight = if ($workArea.ContainsKey('source_bounds_height')) { [int]$workArea.source_bounds_height } else { 0 }
    if ($preferSourceMonitor -and $sourceWidth -gt 0 -and $sourceHeight -gt 0) {
        if ($width -le 0) { $width = $sourceWidth }
        if ($height -le 0) { $height = $sourceHeight }
        return @{ ok = $true; width = $width; height = $height; source = 'primary_source_monitor'; warnings = @() }
    }

    $canvas = Get-ReplayKitObsCanvasSize
    if ($canvas.ok) {
        if ($width -le 0) { $width = [int]$canvas.width }
        if ($height -le 0) { $height = [int]$canvas.height }
        return @{ ok = $true; width = $width; height = $height; source = [string]$canvas.source; warnings = @() }
    }

    $fallbackWarning = "$($canvas.message) Falling back to Windows primary monitor size."
    if ($width -le 0) { $width = $sourceWidth }
    if ($height -le 0) { $height = $sourceHeight }
    if ($width -lt 1 -or $height -lt 1) {
        return @{ ok = $false; message = 'Could not resolve a valid projector window size.' }
    }
    return @{ ok = $true; width = $width; height = $height; source = 'primary_monitor_fallback'; warnings = @($fallbackWarning) }
}

function Get-ReplayKitProjectorMonitorWorkArea([int]$monitorIndex) {
    try {
        Add-Type -AssemblyName System.Windows.Forms
        $screens = @([System.Windows.Forms.Screen]::AllScreens)
    } catch {
        return @{ ok = $false; message = "Could not read monitor work areas: $($_.Exception.Message)" }
    }
    if ($screens.Count -lt 1) {
        return @{ ok = $false; message = 'No real monitor work area was reported by Windows.' }
    }
    $idx = if ($monitorIndex -le 0) { $screens.Count - 1 } else { [Math]::Max(1, $monitorIndex) - 1 }
    if ($idx -ge $screens.Count) { $idx = $screens.Count - 1 }
    $area = $screens[$idx].WorkingArea
    $bounds = $screens[$idx].Bounds
    $sourceScreen = [System.Windows.Forms.Screen]::PrimaryScreen
    $sourceIdx = -1
    $sourceBounds = $null
    if ($sourceScreen -ne $null) {
        for ($i = 0; $i -lt $screens.Count; $i++) {
            if ($screens[$i].DeviceName -eq $sourceScreen.DeviceName) {
                $sourceIdx = $i
                break
            }
        }
        if ($sourceIdx -ge 0) { $sourceBounds = $sourceScreen.Bounds }
    }
    $result = @{
        ok = $true
        index = ($idx + 1)
        count = $screens.Count
        source_index = ($sourceIdx + 1)
        left = [int]$area.Left
        top = [int]$area.Top
        right = [int]$area.Right
        bottom = [int]$area.Bottom
        bounds_left = [int]$bounds.Left
        bounds_top = [int]$bounds.Top
        bounds_right = [int]$bounds.Right
        bounds_bottom = [int]$bounds.Bottom
        bounds_width = [int]$bounds.Width
        bounds_height = [int]$bounds.Height
    }
    if ($sourceBounds -ne $null) {
        $result['source_bounds_left'] = [int]$sourceBounds.Left
        $result['source_bounds_top'] = [int]$sourceBounds.Top
        $result['source_bounds_right'] = [int]$sourceBounds.Right
        $result['source_bounds_bottom'] = [int]$sourceBounds.Bottom
        $result['source_bounds_width'] = [int]$sourceBounds.Width
        $result['source_bounds_height'] = [int]$sourceBounds.Height
    }
    $allBounds = @()
    foreach ($screen in $screens) {
        $b = $screen.Bounds
        $allBounds += @{
            left = [int]$b.Left
            top = [int]$b.Top
            right = [int]$b.Right
            bottom = [int]$b.Bottom
        }
    }
    $result['all_screen_bounds'] = $allBounds
    return $result
}

function Get-ReplayKitProjectorRectInt($object, [string]$key) {
    if ($null -eq $object -or [string]::IsNullOrWhiteSpace($key)) { return 0 }
    if ($object -is [System.Collections.IDictionary] -and $object.Contains($key)) {
        $value = $object[$key]
    } else {
        $prop = $object.PSObject.Properties[$key]
        if (-not $prop) { return 0 }
        $value = $prop.Value
    }
    $n = 0
    if ([int]::TryParse(([string]$value), [ref]$n)) { return $n }
    return 0
}

function Get-ReplayKitProjectorScreenBoundsList([hashtable]$WorkArea, [int]$FallbackLeft, [int]$FallbackTop, [int]$FallbackRight, [int]$FallbackBottom) {
    $list = New-Object System.Collections.Generic.List[hashtable]
    if ($WorkArea.ContainsKey('all_screen_bounds')) {
        foreach ($entry in @($WorkArea.all_screen_bounds)) {
            $l = Get-ReplayKitProjectorRectInt $entry 'left'
            $t = Get-ReplayKitProjectorRectInt $entry 'top'
            $r = Get-ReplayKitProjectorRectInt $entry 'right'
            $b = Get-ReplayKitProjectorRectInt $entry 'bottom'
            if ($r -gt $l -and $b -gt $t) {
                [void]$list.Add(@{ left = $l; top = $t; right = $r; bottom = $b })
            }
        }
    }
    if ($list.Count -lt 1) {
        [void]$list.Add(@{ left = $FallbackLeft; top = $FallbackTop; right = $FallbackRight; bottom = $FallbackBottom })
    }
    return @($list)
}

function Get-ReplayKitRectIntersectionArea([hashtable]$A, [hashtable]$B) {
    $left = [Math]::Max([int]$A.left, [int]$B.left)
    $top = [Math]::Max([int]$A.top, [int]$B.top)
    $right = [Math]::Min([int]$A.right, [int]$B.right)
    $bottom = [Math]::Min([int]$A.bottom, [int]$B.bottom)
    if ($right -le $left -or $bottom -le $top) { return [int64]0 }
    return [int64](($right - $left) * ($bottom - $top))
}

function Get-ReplayKitProjectorCornerPreference([string]$Edge, [string]$Corner) {
    $edgeValue = ([string]$Edge).Trim().ToLowerInvariant()
    $orders = @{
        right = @('top_right', 'bottom_right', 'top_left', 'bottom_left')
        left = @('top_left', 'bottom_left', 'top_right', 'bottom_right')
        top = @('top_right', 'top_left', 'bottom_right', 'bottom_left')
        bottom = @('bottom_right', 'bottom_left', 'top_right', 'top_left')
    }
    $order = if ($orders.ContainsKey($edgeValue)) { $orders[$edgeValue] } else { $orders.bottom }
    for ($i = 0; $i -lt $order.Count; $i++) {
        if ($order[$i] -eq $Corner) { return $i }
    }
    return 99
}

function Get-ReplayKitDiscordProjectorParkedRect(
    [hashtable]$WorkArea,
    [int]$WindowWidth,
    [int]$WindowHeight,
    [string]$Edge,
    [int]$VisiblePixels
) {
    $left = [int]$WorkArea.left
    $top = [int]$WorkArea.top
    $right = [int]$WorkArea.right
    $bottom = [int]$WorkArea.bottom
    if ($right -le $left -or $bottom -le $top) {
        throw 'Monitor work area is invalid.'
    }

    $edgeValue = ([string]$Edge).Trim().ToLowerInvariant()
    if (@('right', 'left', 'top', 'bottom') -notcontains $edgeValue) {
        throw "Invalid projector edge: $Edge"
    }

    $workWidth = $right - $left
    $workHeight = $bottom - $top
    $screenLeft = if ($WorkArea.ContainsKey('bounds_left')) { [int]$WorkArea.bounds_left } else { $left }
    $screenTop = if ($WorkArea.ContainsKey('bounds_top')) { [int]$WorkArea.bounds_top } else { $top }
    $screenWidth = if ($WorkArea.ContainsKey('bounds_width')) { [int]$WorkArea.bounds_width } else { $workWidth }
    $screenHeight = if ($WorkArea.ContainsKey('bounds_height')) { [int]$WorkArea.bounds_height } else { $workHeight }
    $screenRight = if ($WorkArea.ContainsKey('bounds_right')) { [int]$WorkArea.bounds_right } else { $screenLeft + $screenWidth }
    $screenBottom = if ($WorkArea.ContainsKey('bounds_bottom')) { [int]$WorkArea.bounds_bottom } else { $screenTop + $screenHeight }
    if ($screenRight -le $screenLeft -or $screenBottom -le $screenTop) {
        $screenLeft = $left
        $screenTop = $top
        $screenRight = $right
        $screenBottom = $bottom
        $screenWidth = $workWidth
        $screenHeight = $workHeight
    }
    $sourceWidth = if ($WorkArea.ContainsKey('source_bounds_width')) { [int]$WorkArea.source_bounds_width } else { $screenWidth }
    $sourceHeight = if ($WorkArea.ContainsKey('source_bounds_height')) { [int]$WorkArea.source_bounds_height } else { $screenHeight }
    if ($sourceWidth -lt 1 -or $sourceHeight -lt 1) {
        throw 'Projector source monitor bounds are invalid.'
    }
    $autoWidth = [Math]::Max(1, $sourceWidth)
    $autoHeight = [Math]::Max(1, $sourceHeight)
    $width = if ($WindowWidth -le 0) { [Math]::Max(1, $autoWidth) } else { [Math]::Max(1, $WindowWidth) }
    $height = if ($WindowHeight -le 0) { [Math]::Max(1, $autoHeight) } else { [Math]::Max(1, $WindowHeight) }

    $visible = [Math]::Min([Math]::Max(1, $VisiblePixels), [Math]::Min($width, $height))
    $workRect = @{ left = $left; top = $top; right = $right; bottom = $bottom }
    $screenBounds = Get-ReplayKitProjectorScreenBoundsList $WorkArea $screenLeft $screenTop $screenRight $screenBottom
    $candidates = @(
        @{ corner = 'top_left'; x = ($left - $width + $visible); y = ($top - $height + $visible) },
        @{ corner = 'top_right'; x = ($right - $visible); y = ($top - $height + $visible) },
        @{ corner = 'bottom_left'; x = ($left - $width + $visible); y = ($bottom - $visible) },
        @{ corner = 'bottom_right'; x = ($right - $visible); y = ($bottom - $visible) }
    )
    $scored = foreach ($candidate in $candidates) {
        $rect = @{
            left = [int]$candidate.x
            top = [int]$candidate.y
            right = [int]$candidate.x + $width
            bottom = [int]$candidate.y + $height
        }
        $monitorArea = [int64]0
        foreach ($bounds in $screenBounds) {
            $monitorArea += Get-ReplayKitRectIntersectionArea $rect $bounds
        }
        [pscustomobject]@{
            corner = [string]$candidate.corner
            x = [int]$candidate.x
            y = [int]$candidate.y
            monitor_area = [int64]$monitorArea
            work_area = [int64](Get-ReplayKitRectIntersectionArea $rect $workRect)
            preference = [int](Get-ReplayKitProjectorCornerPreference $edgeValue ([string]$candidate.corner))
        }
    }
    $best = $scored |
        Sort-Object `
            @{ Expression = { [int64]$_.monitor_area } }, `
            @{ Expression = { [Math]::Abs([int64]$_.work_area - ([int64]$visible * [int64]$visible)) } }, `
            @{ Expression = { [int]$_.preference } }, `
            @{ Expression = { [string]$_.corner } } |
        Select-Object -First 1
    if ($null -eq $best) {
        throw 'Could not resolve a safe projector parking corner.'
    }

    $hostX = $screenLeft
    $hostY = $screenTop
    if ($width -le $screenWidth -and ([string]$best.corner).EndsWith('_right')) {
        $hostX = $screenRight - $width
    }
    if ($height -le $screenHeight -and ([string]$best.corner).StartsWith('bottom_')) {
        $hostY = $screenBottom - $height
    }

    $clipX = if (([string]$best.corner).EndsWith('_right')) { $right - $hostX - $visible } else { $left - $hostX }
    $clipY = if (([string]$best.corner).StartsWith('bottom_')) { $bottom - $hostY - $visible } else { $top - $hostY }
    $clipX = [Math]::Min([Math]::Max(0, [int]$clipX), [Math]::Max(0, $width - $visible))
    $clipY = [Math]::Min([Math]::Max(0, [int]$clipY), [Math]::Max(0, $height - $visible))

    return @{
        x = [int]$hostX
        y = [int]$hostY
        fallback_x = [int]$best.x
        fallback_y = [int]$best.y
        width = [int]$width
        height = [int]$height
        clip_x = [int]$clipX
        clip_y = [int]$clipY
        clip_width = [int]$visible
        clip_height = [int]$visible
        edge = $edgeValue
        corner = [string]$best.corner
        visible_pixels = [int]$visible
        visible_area = [int64]$best.monitor_area
        work_visible_area = [int64]$best.work_area
    }
}

function Get-ReplayKitDiscordProjectorInspectRect([hashtable]$WorkArea, [int]$WindowWidth, [int]$WindowHeight) {
    $left = [int]$WorkArea.left
    $top = [int]$WorkArea.top
    $right = [int]$WorkArea.right
    $bottom = [int]$WorkArea.bottom
    if ($right -le $left -or $bottom -le $top) {
        throw 'Monitor work area is invalid.'
    }

    $workWidth = [Math]::Max(1, $right - $left)
    $workHeight = [Math]::Max(1, $bottom - $top)
    $maxWidth = [Math]::Max(320, [int][Math]::Floor($workWidth * 0.72))
    $maxHeight = [Math]::Max(180, [int][Math]::Floor($workHeight * 0.72))
    $width = [Math]::Min([Math]::Max(320, $WindowWidth), $maxWidth)
    $height = [Math]::Min([Math]::Max(180, $WindowHeight), $maxHeight)

    $aspect = 16.0 / 9.0
    if ($width / [double]$height -gt $aspect) {
        $width = [int][Math]::Round($height * $aspect)
    } else {
        $height = [int][Math]::Round($width / $aspect)
    }

    $x = $left + [int][Math]::Floor(($workWidth - $width) / 2)
    $y = $top + [int][Math]::Floor(($workHeight - $height) / 2)
    return @{
        x = [int]$x
        y = [int]$y
        width = [int]$width
        height = [int]$height
    }
}

function Find-ReplayKitDiscordProjectorWindow([hashtable]$settings, [string]$titleOverride = '') {
    $title = if ([string]::IsNullOrWhiteSpace($titleOverride)) { Get-ReplayKitDiscordProjectorShareTitle $settings } else { [string]$titleOverride }
    $hwnd = [ReplayKitProjectorNativeV2]::FindProjectorWindow($title)
    if ($hwnd -eq 0) {
        Write-Log "Find projector: no match for title '$title'"
    } else {
        Write-Log "Find projector: matched hwnd=$hwnd for title '$title'"
    }
    return $hwnd
}

$script:ReplayKitProjectorHandoffPath = Join-Path $script:SCRATCH_DIR 'obsreplaykit_projector_windows.txt'
$script:ReplayKitProjectorHandoffMaxAgeSeconds = 2

# reads the tray plugins (replaykit-tray.cpp) published list of hwnds obs itself has marked with isOBSProjectorWindow -- the actual authoritative signal obs uses internally, republished every 250ms via an atomic QSaveFile write so this never sees a half-written file. returns $null (not an empty array) when the file is missing or older than a couple publish cycles, so the caller can tell "confirmed no projectors exist" apart from "the tray plugin isnt running or obs is mid-shutdown, i dont actually know" and fall back accordingly -- an empty array here would silently collapse those two very different situations into one.
function Get-ReplayKitProjectorHwndsFromTrayPlugin {
    try {
        $info = Get-Item -LiteralPath $script:ReplayKitProjectorHandoffPath -ErrorAction Stop
    } catch {
        return $null
    }
    if (([DateTime]::UtcNow - $info.LastWriteTimeUtc).TotalSeconds -gt $script:ReplayKitProjectorHandoffMaxAgeSeconds) {
        return $null
    }
    $hwnds = New-Object System.Collections.Generic.List[long]
    foreach ($line in @(Get-Content -LiteralPath $script:ReplayKitProjectorHandoffPath -ErrorAction SilentlyContinue)) {
        $n = [long]0
        if ([long]::TryParse($line.Trim(), [ref]$n) -and $n -ne 0) { [void]$hwnds.Add($n) }
    }
    return @($hwnds.ToArray())
}

# the tray-plugin signal above is authoritative when available -- prefer it. FindAllObsWindows (window class + no-owner heuristic) is the fallback for an older bundle without the tray plugin update, or the brief window before the tray plugins first publish after obs startup.
function Get-ReplayKitObsWindowCandidates {
    $fromTray = Get-ReplayKitProjectorHwndsFromTrayPlugin
    if ($null -ne $fromTray) { return $fromTray }
    return [ReplayKitProjectorNativeV2]::FindAllObsWindows()
}

# true only for a hwnd that appeared after $baseline was captured -- a window already present in $baseline is off limits no matter what it looks like, becuase it could be a projector the user opened themselves from OBSs own menu (same title pattern, no way to tell them apart except by "did it exist before we asked obs for one").
function Test-ReplayKitProjectorWindowIsNew([long]$hwnd, [long[]]$baseline) {
    return $baseline -notcontains $hwnd
}

function Get-ReplayKitFirstNewProjectorWindow([long[]]$baseline) {
    foreach ($candidate in Get-ReplayKitObsWindowCandidates) {
        if (Test-ReplayKitProjectorWindowIsNew $candidate $baseline) { return $candidate }
    }
    return 0
}

function Close-ReplayKitStrayProjectorWindows([long[]]$baseline, [long]$keepHwnd) {
    foreach ($candidate in Get-ReplayKitObsWindowCandidates) {
        if ($candidate -eq $keepHwnd) { continue }
        if (-not (Test-ReplayKitProjectorWindowIsNew $candidate $baseline)) { continue }
        [void][ReplayKitProjectorNativeV2]::CloseWindow($candidate)
        Write-Log "Closed stray Discord projector window hwnd=$candidate left over from an earlier slow-to-appear attempt"
    }
}

function Open-ReplayKitDiscordProjectorIfMissing([hashtable]$settings, [string]$titleOverride = '') {
    $title = if ([string]::IsNullOrWhiteSpace($titleOverride)) { Get-ReplayKitDiscordProjectorShareTitle $settings } else { [string]$titleOverride }
    $prePark = $true
    $hwnd = Find-ReplayKitDiscordProjectorWindow $settings $title
    if ($hwnd -ne 0) {
        $extra = [ReplayKitProjectorNativeV2]::CloseDuplicateProjectorWindows($title, [long]$hwnd)
        if ($extra -gt 0) { Write-Log "Closed $extra leaked Discord projector window(s)" }
        $script:State.ReplayKitDiscordProjectorPendingBaseline = $null
        return @{ ok = $true; hwnd = $hwnd; opened = $false }
    }

    # an earlier attempt may still be mid-create on obss side even though we gave up waiting for it last time -- if one is already pending, reuse that same baseline instead of re-snapshotting now, or wed wrongly treat its own not-yet-appeared window as "pre-existing, not ours" the moment it finally shows up. only take a fresh baseline when nothing is pending.
    if ($null -eq $script:State.ReplayKitDiscordProjectorPendingBaseline) {
        $script:State.ReplayKitDiscordProjectorPendingBaseline = Get-ReplayKitObsWindowCandidates
    }
    $baseline = @($script:State.ReplayKitDiscordProjectorPendingBaseline)

    Write-Log "Opening OBS Windowed Projector for title '$title'"
    $open = Invoke-ObsWebSocketRequest 'OpenVideoMixProjector' @{
        videoMixType = 'OBS_WEBSOCKET_VIDEO_MIX_TYPE_PROGRAM'
        monitorIndex = -1
    } 5000
    if (-not $open.ok) {
        Write-Log "warn: OpenVideoMixProjector request failed: $($open.message)"
        return @{ ok = $false; hwnd = 0; opened = $false; message = "Opening OBS Windowed Projector failed: $($open.message)" }
    }

    # 24x250ms=6s, up from the original 4s -- a slow PC that misses the shorter window used to report "not found" while OBS was still mid-create, leaving that projector un-renamed and un-hidden until some later cycle happened to trip over it. matching against $baseline (not title text) so this works regardless of OBSs UI language -- a non-english install localizes the windows own title, but never localizes what OBS process it belongs to or when it appeared, which is all this actually checks now.
    for ($i = 0; $i -lt 24; $i++) {
        Start-Sleep -Milliseconds 250
        $newHwnd = Get-ReplayKitFirstNewProjectorWindow $baseline
        if ($newHwnd -ne 0) {
            if ($prePark) {
                [void][ReplayKitProjectorNativeV2]::PreParkProjectorWindow([long]$newHwnd, $title)
                [void][ReplayKitProjectorNativeV2]::SetProjectorTaskbarHidden([long]$newHwnd, $true)
            }
            $extra = [ReplayKitProjectorNativeV2]::CloseDuplicateProjectorWindows($title, [long]$newHwnd)
            if ($extra -gt 0) { Write-Log "Closed $extra leaked Discord projector window(s)" }
            Close-ReplayKitStrayProjectorWindows $baseline $newHwnd
            $script:State.ReplayKitDiscordProjectorPendingBaseline = $null
            Write-Log "Opened OBS projector: hwnd=$newHwnd after $($i + 1) poll(s) ($(($i + 1) * 250)ms)"
            return @{ ok = $true; hwnd = $newHwnd; opened = $true }
        }
    }
    Write-Log "warn: OBS Windowed Projector opened but never appeared for title '$title' after 6s (still watching -- next check will catch it late instead of opening another)"
    return @{ ok = $false; hwnd = 0; opened = $true; message = 'OBS Windowed Projector was opened, but its window could not be found.' }
}

function Invoke-ReplayKitDiscordProjectorRepark([hashtable]$settings = $null, [string]$titleOverride = '', [bool]$Force = $false) {
    if ($null -eq $settings) { $settings = Read-ReplayKitSettings }
    $mode = [string]$settings.discord_output_mode
    if ($mode -ne 'projector') {
        Write-Log "Discord projector repark skipped: output mode is '$mode'"
        return @{ ok = $true; applied = @('Discord projector skipped: legacy output mode selected.'); warnings = @() }
    }
    if (-not [bool]$settings.discord_screenshare_enabled) {
        Write-Log 'Discord projector repark skipped: screenshare disabled in settings'
        return @{ ok = $true; applied = @('Discord screenshare support disabled.'); warnings = @() }
    }
    if ((-not [bool]$settings.discord_projector_enabled) -and -not $Force) {
        Write-Log 'Discord projector repark skipped: projector disabled in settings'
        return @{ ok = $true; applied = @('Discord projector disabled.'); warnings = @() }
    }
    # the whole find/open/park sequence below runs under one lock, not just the flag write that used to be here alone -- confirmed via a real user log (2026-08-17) that without this, a helper restart can fire this function and the keep-alive tick concurrently, both see no projector, and both open one. Invoke-ReplayKitDiscordProjectorInspect and -Disable take the same lock for their own find/open/close sequences so none of the three can interleave with each other either.
    [System.Threading.Monitor]::Enter($script:State.ProjectorLock)
    try {
    $script:State.ReplayKitDiscordProjectorInspectMode = $false

    $warnings = @()
    $title = if ([string]::IsNullOrWhiteSpace($titleOverride)) { Get-ReplayKitDiscordProjectorShareTitle $settings } else { [string]$titleOverride }
    $open = Open-ReplayKitDiscordProjectorIfMissing $settings $title
    if (-not $open.ok) {
        $warnings += $open.message
        return @{ ok = $false; message = $open.message; warnings = $warnings }
    }

    $workArea = Get-ReplayKitProjectorMonitorWorkArea ([int]$settings.discord_projector_monitor_index)
    if (-not $workArea.ok) {
        $warnings += $workArea.message
        return @{ ok = $false; message = $workArea.message; warnings = $warnings }
    }

    $preferSourceMonitor = $true
    $size = Resolve-ReplayKitDiscordProjectorWindowSize $settings $workArea $preferSourceMonitor
    if (-not $size.ok) {
        $warnings += $size.message
        return @{ ok = $false; message = $size.message; warnings = $warnings }
    }
    if ($size.warnings) { $warnings += @($size.warnings) }

    try {
        $visiblePixels = [int]$settings.discord_projector_visible_pixels
        $rect = Get-ReplayKitDiscordProjectorParkedRect `
            $workArea `
            ([int]$size.width) `
            ([int]$size.height) `
            ([string]$settings.discord_projector_edge) `
            ([int]$visiblePixels)
    } catch {
        $warnings += $_.Exception.Message
        return @{ ok = $false; message = $_.Exception.Message; warnings = $warnings }
    }

    Write-Log ("Parking Discord projector at corner={0}, visible_pixels={1}, visible_area={2}, window=({3},{4},{5},{6}), mode=hidden" -f $rect.corner, $rect.visible_pixels, $rect.visible_area, $rect.x, $rect.y, $rect.width, $rect.height)
    $alreadyPlaced = (-not [bool]$open.opened) -and [ReplayKitProjectorNativeV2]::IsWindowAtRectAndTitle(
        [long]$open.hwnd,
        $title,
        [int]$rect.x,
        [int]$rect.y,
        [int]$rect.width,
        [int]$rect.height
    )
    $parked = [ReplayKitProjectorNativeV2]::RestoreResizeTitleChromeGhosted(
        [long]$open.hwnd,
        $title,
        [int]$rect.x,
        [int]$rect.y,
        [int]$rect.width,
        [int]$rect.height,
        $true
    )
    if (-not $parked) {
        [void][ReplayKitProjectorNativeV2]::PreParkProjectorWindow([long]$open.hwnd, $title)
        $msg = 'OBS projector window was found, but Windows rejected the ghosted park request.'
        $warnings += $msg
        return @{ ok = $false; message = $msg; warnings = $warnings }
    }

    # the flag read-decide-write around each native call is kept together under one lock so two concurrent reparks cant both read a stale "not hidden yet" and both decide to call the native hide -- the native layer has its own s_taskbarInFlight guard too, but this keeps the powershell-side bookkeeping consistent with it rather than relying on it alone.
    $hideProjectorTaskbar = $true
    [System.Threading.Monitor]::Enter($script:State.ProjectorLock)
    try {
        if ($hideProjectorTaskbar) {
            if ($alreadyPlaced -and $script:State.ReplayKitDiscordProjectorTaskbarHiddenApplied) {
                $script:State.ReplayKitDiscordProjectorTaskbarHiddenApplied = $true
            } else {
                $hidden = [ReplayKitProjectorNativeV2]::SetProjectorTaskbarHidden([long]$open.hwnd, $true)
                if (-not $hidden) {
                    $script:State.ReplayKitDiscordProjectorTaskbarHiddenApplied = $false
                    $msg = 'OBS projector window was moved, but Windows rejected the taskbar-hide request.'
                    $warnings += $msg
                    Write-Log "warn: $msg"
                } else {
                    $script:State.ReplayKitDiscordProjectorTaskbarHiddenApplied = $true
                }
            }
        } elseif ($script:State.ReplayKitDiscordProjectorTaskbarHiddenApplied) {
            $shown = [ReplayKitProjectorNativeV2]::SetProjectorTaskbarHidden([long]$open.hwnd, $false)
            if ($shown) { $script:State.ReplayKitDiscordProjectorTaskbarHiddenApplied = $false }
        }
    } finally { [System.Threading.Monitor]::Exit($script:State.ProjectorLock) }

    Write-Log 'Discord projector ready'
    return @{
        ok = $true
        hwnd = [long]$open.hwnd
        opened = [bool]$open.opened
        title = $title
        rect = $rect
        monitor = $workArea
        size_source = [string]$size.source
        applied = @('Discord projector ready')
        warnings = $warnings
    }
    } finally { [System.Threading.Monitor]::Exit($script:State.ProjectorLock) }
}

function Invoke-ReplayKitDiscordProjectorInspect([hashtable]$settings = $null, [string]$titleOverride = '') {
    if ($null -eq $settings) { $settings = Read-ReplayKitSettings }
    $mode = [string]$settings.discord_output_mode
    if ($mode -ne 'projector') {
        return @{ ok = $false; message = 'Discord projector is not the active Share Preview mode.'; warnings = @() }
    }
    if (-not [bool]$settings.discord_screenshare_enabled) {
        return @{ ok = $false; message = 'Discord screenshare support is disabled.'; warnings = @() }
    }

    # same lock Repark and Disable take around their own find/open/manipulate sequences -- see the comment in Invoke-ReplayKitDiscordProjectorRepark.
    [System.Threading.Monitor]::Enter($script:State.ProjectorLock)
    try {
    $warnings = @()
    $title = if ([string]::IsNullOrWhiteSpace($titleOverride)) { Get-ReplayKitDiscordProjectorShareTitle $settings } else { [string]$titleOverride }
    $open = Open-ReplayKitDiscordProjectorIfMissing $settings $title
    if (-not $open.ok) {
        $warnings += $open.message
        return @{ ok = $false; message = $open.message; warnings = $warnings }
    }

    $workArea = Get-ReplayKitProjectorMonitorWorkArea ([int]$settings.discord_projector_monitor_index)
    if (-not $workArea.ok) {
        $warnings += $workArea.message
        return @{ ok = $false; message = $workArea.message; warnings = $warnings }
    }

    $preferSourceMonitor = $true
    $size = Resolve-ReplayKitDiscordProjectorWindowSize $settings $workArea $preferSourceMonitor
    if (-not $size.ok) {
        $warnings += $size.message
        return @{ ok = $false; message = $size.message; warnings = $warnings }
    }
    if ($size.warnings) { $warnings += @($size.warnings) }

    try {
        $rect = Get-ReplayKitDiscordProjectorInspectRect $workArea ([int]$size.width) ([int]$size.height)
    } catch {
        $warnings += $_.Exception.Message
        return @{ ok = $false; message = $_.Exception.Message; warnings = $warnings }
    }

    $moved = [ReplayKitProjectorNativeV2]::RestoreResizeTitleAndChrome(
        [long]$open.hwnd,
        $title,
        [int]$rect.x,
        [int]$rect.y,
        [int]$rect.width,
        [int]$rect.height,
        $false
    )
    if (-not $moved) {
        $msg = 'OBS projector window was found, but Windows rejected the move/resize request.'
        $warnings += $msg
        return @{ ok = $false; message = $msg; warnings = $warnings }
    }

    [void][ReplayKitProjectorNativeV2]::SetProjectorTaskbarHidden([long]$open.hwnd, $false)
    $script:State.ReplayKitDiscordProjectorTaskbarHiddenApplied = $false
    $script:State.ReplayKitDiscordProjectorInspectMode = $true
    return @{
        ok = $true
        hwnd = [long]$open.hwnd
        opened = [bool]$open.opened
        title = $title
        rect = $rect
        monitor = $workArea
        size_source = [string]$size.source
        applied = @('Projector shown for inspection')
        warnings = $warnings
    }
    } finally { [System.Threading.Monitor]::Exit($script:State.ProjectorLock) }
}

function Invoke-ReplayKitDiscordProjectorDisable([hashtable]$settings = $null, [string]$titleOverride = '') {
    if ($null -eq $settings) { $settings = Read-ReplayKitSettings }
    $warnings = @()
    $title = if ([string]::IsNullOrWhiteSpace($titleOverride)) { Get-ReplayKitDiscordProjectorShareTitle $settings } else { [string]$titleOverride }
    # same lock Repark and Inspect take around their own find/open/manipulate sequences -- see the comment in Invoke-ReplayKitDiscordProjectorRepark. without it, a concurrent Repark/Inspect could reopen the projector in the gap between our Find and our CloseWindow.
    [System.Threading.Monitor]::Enter($script:State.ProjectorLock)
    try {
    $script:State.ReplayKitDiscordProjectorInspectMode = $false
    # disabling ends any open attempt we were still tracking -- a stale baseline from before this disable shouldnt carry into whatever happens the next time its turned back on.
    $script:State.ReplayKitDiscordProjectorPendingBaseline = $null
    $hwnd = Find-ReplayKitDiscordProjectorWindow $settings $title
    if ($hwnd -eq 0) {
        return @{
            ok = $true
            title = $title
            applied = @('Share preview already disabled')
            warnings = $warnings
        }
    }

    # posting wm_close doesnt guarantee obs actually destroyed the projector -- verify it really went away instead of trusting the post, since a parked/ghosted projector left alive here keeps playing its own program audio mix to the default device long after this "succeeds". sweeps every exact-title match, not just the one Find picked as best, so a leaked duplicate from the keep-alive bug cant survive a disable.
    for ($attempt = 1; $attempt -le 6; $attempt++) {
        [void][ReplayKitProjectorNativeV2]::CloseWindow([long]$hwnd)
        [void][ReplayKitProjectorNativeV2]::CloseDuplicateProjectorWindows($title, 0)
        Start-Sleep -Milliseconds ([Math]::Min(500, 100 * $attempt))
        $hwnd = Find-ReplayKitDiscordProjectorWindow $settings $title
        if ($hwnd -eq 0) {
            return @{
                ok = $true
                title = $title
                applied = @('Share preview disabled')
                warnings = $warnings
            }
        }
    }

    $msg = 'OBS projector window was found, but it did not close after repeated attempts.'
    $warnings += $msg
    return @{ ok = $false; hwnd = [long]$hwnd; title = $title; message = $msg; warnings = $warnings }
    } finally { [System.Threading.Monitor]::Exit($script:State.ProjectorLock) }
}

function Stop-ReplayKitDiscordShareBridge {
    foreach ($proc in @(Get-Process -Name 'OBSReplayKitShareBridge' -ErrorAction SilentlyContinue)) {
        try {
            if ($proc.MainWindowHandle -ne [IntPtr]::Zero) {
                [void]$proc.CloseMainWindow()
                if ($proc.WaitForExit(1500)) { continue }
            }
        } catch {}
        try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch {}
    }
}

function Get-ReplayKitObsStreamAudioRenderDevice {
    $render = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render'
    $friendlyKey = '{a45c254e-df1c-4efd-8020-67d146a850e0},2'
    $activeFlag = 0x1
    $candidates = @()
    try {
        foreach ($child in @(Get-ChildItem -LiteralPath $render -ErrorAction Stop)) {
            $endpointPath = $child.PSPath
            $propsPath = Join-Path $endpointPath 'Properties'
            $state = (Get-ItemProperty -LiteralPath $endpointPath -Name 'DeviceState' -ErrorAction SilentlyContinue).DeviceState
            if ($null -eq $state -or (([int]$state -band $activeFlag) -eq 0)) { continue }
            $name = (Get-ItemProperty -LiteralPath $propsPath -Name $friendlyKey -ErrorAction SilentlyContinue).$friendlyKey
            if ([string]::IsNullOrWhiteSpace([string]$name)) { continue }
            $displayName = ([string]$name).Trim()
            $canonical = ($displayName -replace '^\s*\d+\s*-\s*', '').Trim()
            $lower = $canonical.ToLowerInvariant()
            if ($lower -match 'surround|16ch|loopback|do not select') { continue }

            $rank = 999
            if ($lower -eq 'obs stream audio') {
                $rank = 0
            } elseif ($lower.StartsWith('obs stream audio')) {
                $rank = 1
            } elseif ($lower.StartsWith('cable input')) {
                $rank = 2
            }
            if ($rank -eq 999) { continue }

            $candidates += [pscustomobject]@{
                id = "{0.0.0.00000000}.$($child.PSChildName)"
                name = $displayName
                rank = $rank
            }
        }
    } catch {
        return @{ ok = $false; message = "Could not inspect OBS Stream Audio playback endpoint: $($_.Exception.Message)" }
    }

    $selected = @($candidates | Sort-Object rank, name | Select-Object -First 1)
    if ($selected.Count -lt 1) {
        return @{ ok = $false; message = 'OBS Stream Audio playback endpoint is not active. Install or enable VB-Audio Cable, then re-run Share Preview.' }
    }
    return @{ ok = $true; id = [string]$selected[0].id; source = 'obs_stream_audio'; name = [string]$selected[0].name }
}

$script:ReplayKitDiscordProjectorKeepAliveAt = [DateTime]::MinValue
# this tick runs on the main accept-loop thread (see 90_runtime.ps1), never a pool thread, and must stay that way -- the win32 reposition call underneath it can block waiting on obss own ui thread if obs is busy, which on the pooled model only briefly delays the next new connection being accepted, not any in-flight request; 5s still made an OBS-side stall likely on a busy session, and a parked window doesnt need re-validating that often, so spacing it out cuts how often this tick and a live scene change can collide.
$script:ReplayKitDiscordProjectorKeepAliveSeconds = 30
$script:ReplayKitDiscordProjectorKeepAliveFailures = 0
# open-if-missing has no upper bound tighter than a 5s websocket call plus a 6s find-poll, and that whole sequence runs on this same main thread while holding ProjectorLock -- confirmed via a real user log (2026-08-18) that on a machine where the projector genuinely fails to appear, every single 30s tick re-attempts and re-blocks the entire helper for the full open+poll duration, which starved every other connection and showed up as the client-side "connection error: Write failed" spam thruout that log. capping the backoff here so a persistently-broken projector gets retried every few minutes instead of every 30s forever, once its clearly not a one-off miss.
$script:ReplayKitDiscordProjectorKeepAliveMaxBackoffSeconds = 300

function Invoke-ReplayKitDiscordProjectorKeepAlive {
    $now = [DateTime]::UtcNow
    $intervalSeconds = $script:ReplayKitDiscordProjectorKeepAliveSeconds
    if ($script:ReplayKitDiscordProjectorKeepAliveFailures -gt 1) {
        $backoff = $script:ReplayKitDiscordProjectorKeepAliveSeconds * [Math]::Pow(2, $script:ReplayKitDiscordProjectorKeepAliveFailures - 1)
        $intervalSeconds = [Math]::Min($backoff, $script:ReplayKitDiscordProjectorKeepAliveMaxBackoffSeconds)
    }
    if (($now - $script:ReplayKitDiscordProjectorKeepAliveAt).TotalSeconds -lt $intervalSeconds) {
        return
    }
    $script:ReplayKitDiscordProjectorKeepAliveAt = $now
    Write-Log 'Discord projector keep-alive tick'

    try {
        $settings = Read-ReplayKitSettings
    } catch {
        Write-Log "Discord projector keep-alive skipped: $($_.Exception.Message)"
        return
    }
    if (-not [bool]$settings.discord_projector_enabled) {
        Write-Log 'Discord projector keep-alive skipped: projector disabled in settings'
        return
    }
    if ([string]$settings.discord_output_mode -ne 'projector') {
        Write-Log "Discord projector keep-alive skipped: output mode is '$($settings.discord_output_mode)'"
        return
    }
    if (-not [bool]$settings.discord_screenshare_enabled) {
        Write-Log 'Discord projector keep-alive skipped: screenshare disabled in settings'
        return
    }
    if ($script:State.ReplayKitDiscordProjectorInspectMode) {
        Write-Log 'Discord projector keep-alive skipped: inspect mode active'
        return
    }
    Stop-ReplayKitDiscordShareBridge
    $result = Invoke-ReplayKitDiscordProjectorRepark $settings
    if (-not $result.ok) {
        $script:ReplayKitDiscordProjectorKeepAliveFailures++
        Write-Log "warn: Discord projector keep-alive failed ($($script:ReplayKitDiscordProjectorKeepAliveFailures) in a row, next retry in up to ${script:ReplayKitDiscordProjectorKeepAliveMaxBackoffSeconds}s): $($result.message)"
    } else {
        $script:ReplayKitDiscordProjectorKeepAliveFailures = 0
    }
}

function Start-ReplayKitDiscordProjectorAtStartup {
    try {
        $settings = Read-ReplayKitSettings
    } catch {
        Write-Log "Discord projector startup skipped: $($_.Exception.Message)"
        return
    }
    if ([string]$settings.discord_output_mode -eq 'projector' -and [bool]$settings.discord_projector_enabled) {
        Stop-ReplayKitDiscordShareBridge
        Write-Log 'Discord projector mode enabled'
        Write-Log 'Skipping OBS Virtual Camera for Discord output'
        $result = Invoke-ReplayKitDiscordProjectorRepark $settings
        if (-not $result.ok) {
            Write-Log "warn: Discord projector startup failed: $($result.message)"
        }
    } elseif ([string]$settings.discord_output_mode -eq 'projector') {
        Stop-ReplayKitDiscordShareBridge
        $result = Invoke-ReplayKitDiscordProjectorDisable $settings
        if (-not $result.ok) {
            Write-Log "warn: Discord projector disable failed: $($result.message)"
        }
    } elseif ([string]$settings.discord_output_mode -eq 'virtual_camera_legacy') {
        Stop-ReplayKitDiscordShareBridge
        Write-Log 'warn: Virtual Camera is legacy/deprecated for Discord output.'
    }
}

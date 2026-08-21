# native windows apis for thumbnails, obs icon extraction, and window styling.
Add-Type -ReferencedAssemblies @('System.Drawing') -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

[StructLayout(LayoutKind.Sequential)]
public struct SHELL_SIZE { public int cx; public int cy; }

[Flags]
public enum SIIGBF : uint {
    RESIZETOFIT   = 0x00,
    BIGGERSIZEOK  = 0x01,
    MEMORYONLY    = 0x02,
    ICONONLY      = 0x04,
    THUMBNAILONLY = 0x08,
    INCACHEONLY   = 0x10
}

[ComImport]
[Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItemImageFactory {
    void GetImage(SHELL_SIZE size, SIIGBF flags, out IntPtr phbm);
}

public static class ReplayKitNative {
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObject);

    // saves a 480x270 jpeg thumbnail for the given video file; the windows shell extracts the frame using whatever decoder is registered so mp4/mov/mkv all work without shipping ffmpeg -- wrapped in a timeout since a handful of files (bad codec, damaged header) took 13+ seconds on the helpers single request-handling thread, enough to freeze every dock over one problem clip.
    public static void SaveThumbnail(string src, string dst) {
        var task = System.Threading.Tasks.Task.Run(() => SaveThumbnailCore(src, dst));
        if (!task.Wait(8000)) {
            throw new TimeoutException("Thumbnail extraction timed out for " + src);
        }
    }

    static void SaveThumbnailCore(string src, string dst) {
        Guid iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
        IShellItemImageFactory factory;
        SHCreateItemFromParsingName(src, IntPtr.Zero, ref iid, out factory);
        IntPtr hbm;
        factory.GetImage(new SHELL_SIZE { cx = 480, cy = 270 }, SIIGBF.BIGGERSIZEOK, out hbm);
        try {
            using (Image source = Image.FromHbitmap(hbm))
            using (Bitmap canvas = new Bitmap(480, 270))
            using (Graphics g = Graphics.FromImage(canvas)) {
                g.Clear(Color.Black);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                double scale = Math.Min(480.0 / source.Width, 270.0 / source.Height);
                int w = Math.Max(1, (int)Math.Round(source.Width * scale));
                int h = Math.Max(1, (int)Math.Round(source.Height * scale));
                int x = (480 - w) / 2;
                int y = (270 - h) / 2;
                g.DrawImage(source, x, y, w, h);
                ImageCodecInfo jpg = null;
                foreach (ImageCodecInfo codec in ImageCodecInfo.GetImageEncoders())
                    if (codec.MimeType == "image/jpeg") { jpg = codec; break; }
                EncoderParameters ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 86L);
                canvas.Save(dst, jpg, ep);
            }
        } finally {
            DeleteObject(hbm);
        }
    }

    // dark win32 title bar for the popup window
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int attrValue, int attrSize);
    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtr", SetLastError=true)]
    static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint="SetWindowLongPtr", SetLastError=true)]
    static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll", EntryPoint="GetWindowLong", SetLastError=true)]
    static extern int GetWindowLong32(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint="SetWindowLong", SetLastError=true)]
    static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);
    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    public class CTaskbarList { }
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("56FDF342-FD6D-11d0-958A-006097C9A090")]
    public interface ITaskbarList {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT {
        public int X;
        public int Y;
    }
    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT lpPoint);
    // GetAsyncKeyState reads raw hardware button state directly from the os -- unlike a message-based handoff (wm_nclbuttondown etc), it doesnt depend on window message routing, capture ownership, or timing between our helper process and obss own process, which is what actualy made the message-based version unreliable here: by the time our http round trip + cross-process SendMessage landed, real users had often already released the button, so the native sizing loop saw "up" the instant it checked and did nothing. polling current button state instead just works regardless of when we happen to start looking.
    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);
    const int VK_LBUTTON = 0x01;
    [DllImport("gdi32.dll")]
    static extern IntPtr CreateSolidBrush(int crColor);
    [DllImport("user32.dll", EntryPoint="SetClassLongPtr", SetLastError=true)]
    static extern IntPtr SetClassLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll", EntryPoint="SetClassLong", SetLastError=true)]
    static extern int SetClassLong32(IntPtr hWnd, int nIndex, int dwNewLong);
    // redrawwindow is the forcing function stylewindow needs to make the dwmsetwindowattribute changes actualy paint on first creation. setwindowpos+swp_framechanged schedules a frame recompute, but cef popups frequently dont honor the async repaint for the non-client area -- you set the attributes, dwm accepts them, but the title bar stays painted in defualt chrome until something else triggers a frame redraw. rdw_frame|rdw_invalidate|rdw_updatenow is the synchronous version and resolves the race.
    [DllImport("user32.dll")]
    static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);
    const uint RDW_INVALIDATE   = 0x0001;
    const uint RDW_UPDATENOW    = 0x0100;
    const uint RDW_FRAME        = 0x0400;
    const uint RDW_NOCHILDREN   = 0x0040;
    const uint WM_SETICON       = 0x0080;
    const uint WM_NCACTIVATE    = 0x0086;
    const uint WM_THEMECHANGED  = 0x031A;
    const uint SWP_NOSIZE       = 0x0001;
    const uint SWP_NOMOVE       = 0x0002;
    const uint SWP_NOZORDER     = 0x0004;
    const uint SWP_NOACTIVATE   = 0x0010;
    const uint SWP_FRAMECHANGED = 0x0020;
    const uint SWP_SHOWWINDOW   = 0x0040;
    const int GWL_EXSTYLE       = -20;
    const int GWLP_HWNDPARENT   = -8;
    const int GCLP_HBRBACKGROUND = -10;
    const long WS_EX_APPWINDOW  = 0x00040000L;
    const long WS_EX_TOOLWINDOW = 0x00000080L;
    static readonly IntPtr HWND_TOPMOST    = new IntPtr(-1);
    static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    static object taskbarLock = new object();
    static System.Collections.Generic.HashSet<long> taskbarTabs =
        new System.Collections.Generic.HashSet<long>();
    static ITaskbarList taskbarList = null;
    static object resizeLock = new object();
    static System.Collections.Generic.HashSet<long> resizeTracking =
        new System.Collections.Generic.HashSet<long>();
    static IntPtr resizeBackgroundBrush = IntPtr.Zero;

    static IntPtr GetWindowLongPtrCompat(IntPtr hWnd, int nIndex) {
        if (IntPtr.Size == 8) return GetWindowLongPtr64(hWnd, nIndex);
        return new IntPtr(GetWindowLong32(hWnd, nIndex));
    }

    static IntPtr SetWindowLongPtrCompat(IntPtr hWnd, int nIndex, IntPtr value) {
        if (IntPtr.Size == 8) return SetWindowLongPtr64(hWnd, nIndex, value);
        return new IntPtr(SetWindowLong32(hWnd, nIndex, value.ToInt32()));
    }

    static IntPtr SetClassLongPtrCompat(IntPtr hWnd, int nIndex, IntPtr value) {
        if (IntPtr.Size == 8) return SetClassLongPtr64(hWnd, nIndex, value);
        return new IntPtr(SetClassLong32(hWnd, nIndex, value.ToInt32()));
    }

    // repaints the popups background before a live resize drag so the newly exposed edge shows the actual window color instead of a flash of default white -- borderless cef popups dont repaint their non-client-less background on their own the instant setwindowpos grows them.
    static void PrimeResizeBackground(IntPtr hWnd) {
        if (resizeBackgroundBrush == IntPtr.Zero) {
            resizeBackgroundBrush = CreateSolidBrush(0x00261F1D); // OBS Yami grey7, COLORREF 0x00BBGGRR.
        }
        if (resizeBackgroundBrush != IntPtr.Zero) {
            SetClassLongPtrCompat(hWnd, GCLP_HBRBACKGROUND, resizeBackgroundBrush);
            RedrawWindow(hWnd, IntPtr.Zero, IntPtr.Zero,
                RDW_INVALIDATE | RDW_UPDATENOW | RDW_NOCHILDREN);
        }
    }

    static void EnsureTaskbarWindow(IntPtr hWnd) {
        try { SetWindowLongPtrCompat(hWnd, GWLP_HWNDPARENT, IntPtr.Zero); } catch {}
        long ex = GetWindowLongPtrCompat(hWnd, GWL_EXSTYLE).ToInt64();
        long next = (ex | WS_EX_APPWINDOW) & ~WS_EX_TOOLWINDOW;
        if (next != ex) {
            SetWindowLongPtrCompat(hWnd, GWL_EXSTYLE, new IntPtr(next));
        }
        SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
    }

    static void AddTaskbarTab(IntPtr hWnd) {
        if (hWnd == IntPtr.Zero) return;
        EnsureTaskbarWindow(hWnd);
        lock (taskbarLock) {
            long key = hWnd.ToInt64();
            if (taskbarTabs.Contains(key)) return;
            try {
                if (taskbarList == null) {
                    taskbarList = (ITaskbarList)new CTaskbarList();
                    taskbarList.HrInit();
                }
                taskbarList.AddTab(hWnd);
                taskbarList.SetActiveAlt(hWnd);
                taskbarTabs.Add(key);
            } catch {}
        }
    }

    static void RemoveTaskbarTab(IntPtr hWnd) {
        if (hWnd == IntPtr.Zero) return;
        lock (taskbarLock) {
            long key = hWnd.ToInt64();
            try {
                if (taskbarList != null) taskbarList.DeleteTab(hWnd);
            } catch {}
            taskbarTabs.Remove(key);
        }
    }

    // win32 wm_close force-close for popups cef refuses to close. obss embedded cef blocks window.close() on popups that have navigated cross-origin (streamable.com, google oauth, etc.). postmessage(wm_close) bypasses cefs restriction entirely -- it is an os-level shutdown request that the window proc honors regardless of script policy.
    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    const uint WM_CLOSE = 0x0010;

    static bool IsObsFamilyProcess(uint pid) {
        if (pid == 0) return false;
        try {
            using (var p = System.Diagnostics.Process.GetProcessById((int)pid)) {
                string n = p.ProcessName.ToLowerInvariant();
                // obs64.exe, obs.exe, obs-browser-page.exe, obs-ffmpeg-mux, crashpad_handler, and other obs-* subprocesses.
                return n == "obs64" || n == "obs" || n.StartsWith("obs-");
            }
        } catch { return false; }
    }

    public static int CloseWindowsByTitle(string[] titlePrefixes, uint requireOwnerPid) {
        int closed = 0;
        EnumWindows(delegate(IntPtr hWnd, IntPtr lParam) {
            if (!IsWindowVisible(hWnd)) return true;
            uint pid = 0;
            GetWindowThreadProcessId(hWnd, out pid);
            // safety check: only touch windows in the obs process family (obs64.exe and any obs-* subprocesses like obs-browser-page). the original ownerpid-only check was too narrow -- cef popup windows are routinely owned by obs-browser-page renderer subprocesses, not by obs64 itself. pid==0 means caller didnt want a restriction at all.
            if (requireOwnerPid != 0 && !IsObsFamilyProcess(pid)) return true;
            StringBuilder sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, sb.Capacity);
            string text = sb.ToString();
            foreach (string prefix in titlePrefixes) {
                if (text == prefix ||
                    text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                    text.EndsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
                    RemoveTaskbarTab(hWnd);
                    PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    closed++;
                    break;
                }
            }
            return true;
        }, IntPtr.Zero);
        return closed;
    }

    // obss real main window title is a localized template ("OBS <version> - Profile: <name> - Scenes: <name>" in english) with no fixed substring a non-english build still renders, so matching it by text (the original approach) never found the window on a non-english obs. the tray plugin (replaykit-tray.cpp) runs inside obss own qt process and publishes the real main windows hwnd once per session via obs_frontend_get_main_window(), the same authoritative-signal pattern already used for projector windows -- read that instead. needed at all becuase .NETs Process.MainWindowHandle is unreliable here: confirmed 2026-08-19 it can grab a parked/hidden Discord-share projector instead of the real, visible main window, which silently defeats any CloseMainWindow()-based graceful shutdown -- the process just sits there ignoring the close request on the wrong window until something times out and force-kills it, exactly the "not graceful after all" failure this was supposed to prevent. falls back to false (caller then force-kills) if the tray plugin hasnt published yet, isnt loaded, or the owning pid no longer matches a stale file.
    public static bool CloseObsMainWindow(uint requireOwnerPid) {
        string path = Path.Combine(Environment.GetEnvironmentVariable("TEMP") ?? "", "ReplayKit", "scratch", "obsreplaykit_main_window.txt");
        long found = 0;
        try {
            if (File.Exists(path)) {
                long hwndValue;
                if (long.TryParse(File.ReadAllText(path).Trim(), out hwndValue) && hwndValue != 0) {
                    IntPtr hwnd = new IntPtr(hwndValue);
                    if (IsWindow(hwnd)) {
                        uint pid = 0;
                        GetWindowThreadProcessId(hwnd, out pid);
                        if (requireOwnerPid == 0 || pid == requireOwnerPid) found = hwndValue;
                    }
                }
            }
        } catch {}
        if (found == 0) return false;
        return PostMessage(new IntPtr(found), WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    // showwindow with sw_hide moves a window to the hidden state but does not close it -- the window proc keeps running, js keeps executing, and crucially postmessage cross-window communication still works. we use this on the google oauth popup so the user never sees its white-screening compositor race; oauth completes in the background and our /import-session poll picks up the resulting cookies, then we close the (still hidden) window. sw_restore brings a minimized window back to its previous size and is the right command for focuswindows "user clicked view clips again" path -- the popup might be minimized.
    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")]
    static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
    const int SW_HIDE    = 0;
    const int SW_RESTORE = 9;

    // foreground-window apis used by focuswindow -- the bare setforegroundwindow call is subject to windows anti-focus stealing protection, so we use the attachthreadinput trick to temporarily share the input queue with the current foreground thread, call setforegroundwindow, then detach, which works regardless of which obs window currently owns focus.
    [DllImport("user32.dll")] static extern bool   SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool   BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool   AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    // parent-process death detection -- we need to detect "did the parent process exit" reliably across integrity levels: the users obs64 can be elevated while the helper isnt (luas spawn_hidden doesnt propagate elevation when called from an elevated parent in some setups). that rules out waitforsingleobject becuase process_synchronize access is blocked low->high. process_query_limited_information is the access right microsoft added in vista specifically for this -- it lets a low-integrity process query basic state on a high integrity one. paired with getexitcodeprocess, this gives us a reliable poll-style "has the parent exited" check.
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern IntPtr OpenProcess(uint dwAccess, bool inherit, uint pid);
    [DllImport("kernel32.dll")]
    static extern bool GetExitCodeProcess(IntPtr hHandle, out uint exitCode);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr hHandle);
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x00001000;
    const uint STILL_ACTIVE = 259;

    // open a query-only handle on the parent. works across integrity levels (non-admin -> admin obs64), which the previous synchronize based variant didnt. returns intptr.zero on failure.
    public static IntPtr OpenParentForSync(uint pid) {
        if (pid == 0) return IntPtr.Zero;
        return OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
    }

    // has the process behind this handle exited, implemented as a getexitcodeprocess poll (handle doesnt need synchronize access) -- timeoutMs is currently ignored, callers that need to wait should start-sleep between calls.
    public static bool ParentExited(IntPtr handle, uint timeoutMs) {
        if (handle == IntPtr.Zero) return false;
        uint code;
        if (!GetExitCodeProcess(handle, out code)) return false;
        return code != STILL_ACTIVE;
    }

    public static void CloseParentHandle(IntPtr handle) {
        if (handle != IntPtr.Zero) { try { CloseHandle(handle); } catch {} }
    }

    // job objects workers (compress / trim / upload powershell child processes + their ffmpeg descendants) are bound to a windows job object with job_object_limit_kill_on_job_close so theyre auto-terminated when the helper exits -- whether obs closes cleanly, the user ends the task in task manager, or the helper crashes. without this, a long compress all session can orphan a dozen ffmpeg processes that keep encoding into files no ui is watching.
    [StructLayout(LayoutKind.Sequential)]
    public struct IO_COUNTERS {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_BASIC_LIMIT_INFORMATION {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);
    [DllImport("kernel32.dll")]
    static extern bool SetInformationJobObject(
        IntPtr hJob, int JobObjectInformationClass,
        IntPtr lpJobObjectInformation, uint cbJobObjectInformationLength);
    [DllImport("kernel32.dll")]
    static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();
    const int JobObjectExtendedLimitInformation = 9;
    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    // breakaway_ok lets a child opt out of the job via create_breakaway_from_job. existing workers (which dont pass that flag) still inherit the job and still get cleaned up on helper exit.
    const uint JOB_OBJECT_LIMIT_BREAKAWAY_OK = 0x0800;

    // create a job object, mark it kill-on-close + breakaway-allowed, and bind the current process to it. children created later via createprocess inherit the job automatically on windows 8+ (no breakaway flag required), so this single call covers every spawned worker. callers that need to outlive helper exit (e.g. the obs restart relauncher) opt out by passing create_breakaway_from_job in createprocess. returns the job handle; caller doesnt need to close it -- when the process exits, all handles release, the last reference to the job triggers kill_on_job_close.
    public static IntPtr CreateKillOnCloseJob() {
        IntPtr hJob = CreateJobObject(IntPtr.Zero, null);
        if (hJob == IntPtr.Zero) return IntPtr.Zero;
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_BREAKAWAY_OK;
        int size = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
        IntPtr p = Marshal.AllocHGlobal(size);
        try {
            Marshal.StructureToPtr(info, p, false);
            SetInformationJobObject(hJob, JobObjectExtendedLimitInformation, p, (uint)size);
            AssignProcessToJobObject(hJob, GetCurrentProcess());
        } finally {
            Marshal.FreeHGlobal(p);
        }
        return hJob;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOW {
        public uint cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string lpApplicationName,
        System.Text.StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);
    // closehandle + sw_hide are already declared earlier in this class. dont redeclare. createprocess flags are scoped here.
    private const uint CREATE_NO_WINDOW_SPAWN = 0x08000000;
    private const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
    private const uint STARTF_USESHOWWINDOW_SPAWN = 0x00000001;

    // spawn a child outside the helpers kill-on-close job so it survives helper exit. used for the obs restart relauncher: helper queues the relaunch, kills obs, then exits -- the relauncher needs to outlive the helper to actualy launch the new obs. create_no_window + startf_useshowwindow/sw_hide gets us zero console flash; create_breakaway_from_job is the actual escape hatch. deliberately not combining with detached_process -- powershell.exe queries its console handle at startup and refuses to run without one. returns the spawned pid, or 0 on failure.
    public static int SpawnDetached(string commandLine, string workingDirectory) {
        STARTUPINFOW si = new STARTUPINFOW();
        si.cb = (uint)Marshal.SizeOf(typeof(STARTUPINFOW));
        si.dwFlags = STARTF_USESHOWWINDOW_SPAWN;
        si.wShowWindow = (ushort)SW_HIDE;
        PROCESS_INFORMATION pi;
        System.Text.StringBuilder cmd = new System.Text.StringBuilder(commandLine);
        uint flags = CREATE_NO_WINDOW_SPAWN | CREATE_BREAKAWAY_FROM_JOB;
        bool ok = CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero, workingDirectory, ref si, out pi);
        if (!ok) return 0;
        int pid = (int)pi.dwProcessId;
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return pid;
    }

    // returns a json-style report of relevant sign-in popups so the dock can adjust its status label. [{title,hwnd,google,streamable}, ...] we hide google popups while reporting them so the user never sees a white google window.
    public static string ListSignInWindows(uint requireOwnerPid, bool hideGoogle, bool overlayGoogle) {
        StringBuilder report = new StringBuilder();
        report.Append("[");
        bool first = true;
        IntPtr googleHwnd = IntPtr.Zero;
        IntPtr streamableHwnd = IntPtr.Zero;
        EnumWindows(delegate(IntPtr hWnd, IntPtr lParam) {
            if (!IsWindowVisible(hWnd)) return true;
            uint pid = 0;
            GetWindowThreadProcessId(hWnd, out pid);
            if (requireOwnerPid != 0 && !IsObsFamilyProcess(pid)) return true;
            StringBuilder sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, sb.Capacity);
            string text = sb.ToString();
            bool isGoogle    = text.StartsWith("Sign In - Google Accounts", StringComparison.OrdinalIgnoreCase)
                            || text.StartsWith("Sign in - Google Accounts", StringComparison.OrdinalIgnoreCase);
            bool isStreamable= text.IndexOf("Streamable", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isLoader    = text.StartsWith("Signing in to Streamable", StringComparison.OrdinalIgnoreCase);
            bool isBlank     = text == "about:blank";
            if (!(isGoogle || isStreamable || isLoader || isBlank)) return true;
            AddTaskbarTab(hWnd);
            if (isGoogle && googleHwnd == IntPtr.Zero) googleHwnd = hWnd;
            if ((isStreamable || isLoader) && !isGoogle && streamableHwnd == IntPtr.Zero) streamableHwnd = hWnd;
            if (isGoogle && hideGoogle) {
                ShowWindow(hWnd, SW_HIDE);
            }
            if (!first) report.Append(",");
            first = false;
            string safeTitle = text.Replace("\\","\\\\").Replace("\"","\\\"");
            report.Append("{\"title\":\"").Append(safeTitle).Append("\",")
                  .Append("\"hwnd\":").Append(hWnd.ToInt64()).Append(",")
                  .Append("\"google\":").Append(isGoogle ? "true" : "false").Append(",")
                  .Append("\"streamable\":").Append(isStreamable ? "true" : "false")
                  .Append("}");
            return true;
        }, IntPtr.Zero);
        if (overlayGoogle && googleHwnd != IntPtr.Zero && streamableHwnd != IntPtr.Zero) {
            RECT r;
            if (GetWindowRect(streamableHwnd, out r)) {
                int w = Math.Max(320, r.Right - r.Left);
                int h = Math.Max(320, r.Bottom - r.Top);
                SetWindowPos(googleHwnd, IntPtr.Zero, r.Left, r.Top, w, h, SWP_NOZORDER);
            }
        }
        report.Append("]");
        return report.ToString();
    }

    // returns the number of windows whose title matched `needle` and were styled. 0 means we didnt find any -- the caller logs that so a stuck "white title bar" bug is debuggable from the helper log instead of being a silent no-op.
    public static int StyleWindow(string needle, string iconPath, bool taskbar) {
        Icon icon = null;
        int matched = 0;
        try { if (File.Exists(iconPath)) icon = Icon.ExtractAssociatedIcon(iconPath); } catch {}
        EnumWindows(delegate(IntPtr hWnd, IntPtr lParam) {
            if (!IsWindowVisible(hWnd)) return true;
            uint pid = 0;
            GetWindowThreadProcessId(hWnd, out pid);
            if (!IsObsFamilyProcess(pid)) return true;
            StringBuilder sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, sb.Capacity);
            string text = sb.ToString();
            if (text == needle || text.StartsWith(needle + " ") || text.Contains(needle)) {
                matched++;
                int enabled = 1;
                int caption = 0x00261F1D;  // Yami grey6 in 0x00BBGGRR
                int border  = 0x004D403C;
                int white   = 0x00FFFFFF;
                DwmSetWindowAttribute(hWnd, 20, ref enabled, 4);
                DwmSetWindowAttribute(hWnd, 19, ref enabled, 4);
                DwmSetWindowAttribute(hWnd, 35, ref caption, 4);
                DwmSetWindowAttribute(hWnd, 34, ref border,  4);
                DwmSetWindowAttribute(hWnd, 36, ref white,   4);
                if (icon != null) {
                    SendMessage(hWnd, WM_SETICON, IntPtr.Zero,     icon.Handle);
                    SendMessage(hWnd, WM_SETICON, new IntPtr(1),   icon.Handle);
                }
                if (taskbar) AddTaskbarTab(hWnd);
                // broadcast theme-changed first so cef refreshes any cached title-bar render state, then re-set dwm attributes (some chromium versions reset them in response to wm_themechanged -- we want our values to be the last write).
                SendMessage(hWnd, WM_THEMECHANGED, IntPtr.Zero, IntPtr.Zero);
                DwmSetWindowAttribute(hWnd, 20, ref enabled, 4);
                DwmSetWindowAttribute(hWnd, 35, ref caption, 4);
                DwmSetWindowAttribute(hWnd, 34, ref border,  4);
                DwmSetWindowAttribute(hWnd, 36, ref white,   4);
                SendMessage(hWnd, WM_NCACTIVATE, IntPtr.Zero,    IntPtr.Zero);
                SendMessage(hWnd, WM_NCACTIVATE, new IntPtr(1),  IntPtr.Zero);
                SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
                // force a synchronous non-client repaint. setwindowpos above schedules the recompute but cef popups frequently dont honor it for the title bar on first creation -- dwm accepts the attributes but the bar stays painted in defualt windows chrome ("white title bar, no icon" race). redrawwindow with these flags is the official workaround per microsofts dwm custom-frame guide.
                RedrawWindow(hWnd, IntPtr.Zero, IntPtr.Zero,
                    RDW_FRAME | RDW_INVALIDATE | RDW_UPDATENOW | RDW_NOCHILDREN);
            }
            return true;
        }, IntPtr.Zero);
        return matched;
    }

    // restore + raise an arbitrary hwnd. pulled out of focuswindow so other callers can reuse it -- in particular, the open-folder routes enumerate explorer.exe windows via shell.application com, find one already pointed at the clip folder, and pass its hwnd here to raise it instead of launching a new explorer instance.
    public static bool FocusHwnd(IntPtr hWnd) {
        if (hWnd == IntPtr.Zero) return false;
        // restore if minimized; sw_restore is a no-op for windows already in their normal state.
        ShowWindow(hWnd, SW_RESTORE);
        ShowWindowAsync(hWnd, SW_RESTORE);
        // attachthreadinput dance defeats anti-focus-stealing.
        IntPtr fg = GetForegroundWindow();
        uint fgPid = 0;
        uint fgThread = (fg == IntPtr.Zero) ? 0 : GetWindowThreadProcessId(fg, out fgPid);
        uint myThread = GetCurrentThreadId();
        bool attached = false;
        if (fgThread != 0 && fgThread != myThread) {
            attached = AttachThreadInput(myThread, fgThread, true);
        }
        try {
            BringWindowToTop(hWnd);
            SetForegroundWindow(hWnd);
            SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SetWindowPos(hWnd, HWND_NOTOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SetForegroundWindow(hWnd);
        } finally {
            if (attached) AttachThreadInput(myThread, fgThread, false);
        }
        return GetForegroundWindow() == hWnd;
    }

    // bring the first obs-family window whose title matches `needle` to the foreground. used by /focus-window so clicking "view clips" a second time while the popup is already open raises it instead of doing nothing -- cef inside obs makes the js-level window.focus() unreliable, so we do this server-side with win32 where it actualy works.
    public static bool FocusWindow(string needle) {
        bool focused = false;
        EnumWindows(delegate(IntPtr hWnd, IntPtr lParam) {
            if (!IsWindowVisible(hWnd)) return true;
            uint pid = 0;
            GetWindowThreadProcessId(hWnd, out pid);
            if (!IsObsFamilyProcess(pid)) return true;
            StringBuilder sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, sb.Capacity);
            string text = sb.ToString();
            if (text != needle && !text.StartsWith(needle + " ") && !text.Contains(needle)) {
                return true;
            }
            FocusHwnd(hWnd);
            focused = true;
            return false;   // stop enumerating, were done
        }, IntPtr.Zero);
        return focused;
    }

    // maximize / restore by title
    // the clips popup goes fullscreen when the user double-clicks (or presses f) on a clip preview. cef inside obs doesnt expose a js api for resizing its host window, so the dock posts to helper routes which call win32 directly.
    // maximizeobswindow: save the current window rect into a static dictionary keyed by hwnd, then showwindow sw_maximize. if the window is already maximized by hand, we skip it.
    // restoreobswindow: restore the saved rect only if the window is still maximized. if the user dragged or unmaximized the Clips host while fullscreened, windows has already moved it out of maximized state, so manual placement wins.
    [DllImport("user32.dll")]
    static extern bool IsZoomed(IntPtr hWnd);
    const int SW_MAXIMIZE = 3;
    static object windowSizeLock = new object();
    static System.Collections.Generic.Dictionary<long, RECT> savedRects =
        new System.Collections.Generic.Dictionary<long, RECT>();

    static IntPtr FindObsWindow(string needle) {
        IntPtr found = IntPtr.Zero;
        if (string.IsNullOrEmpty(needle)) return found;
        EnumWindows(delegate(IntPtr hWnd, IntPtr lParam) {
            if (!IsWindowVisible(hWnd)) return true;
            uint pid = 0;
            GetWindowThreadProcessId(hWnd, out pid);
            if (!IsObsFamilyProcess(pid)) return true;
            StringBuilder sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, sb.Capacity);
            string text = sb.ToString();
            if (text != needle && !text.StartsWith(needle + " ") && !text.Contains(needle)) {
                return true;
            }
            found = hWnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    public static bool MaximizeObsWindow(string needle) {
        IntPtr hwnd = FindObsWindow(needle);
        if (hwnd == IntPtr.Zero) return false;
        lock (windowSizeLock) {
            long key = hwnd.ToInt64();
            // already tracked or user already maximized by hand -- leave it alone so we dont clobber their geometry.
            if (savedRects.ContainsKey(key)) return true;
            if (IsZoomed(hwnd)) return true;
            RECT r;
            if (!GetWindowRect(hwnd, out r)) return false;
            savedRects[key] = r;
        }
        ShowWindow(hwnd, SW_MAXIMIZE);
        return true;
    }

    public static bool RestoreObsWindow(string needle) {
        IntPtr hwnd = FindObsWindow(needle);
        if (hwnd == IntPtr.Zero) return false;
        bool had;
        RECT r = new RECT();
        lock (windowSizeLock) {
            long key = hwnd.ToInt64();
            had = savedRects.TryGetValue(key, out r);
            if (had) savedRects.Remove(key);
        }
        if (!had) return false;
        if (!IsZoomed(hwnd)) return false;

        int w = Math.Max(100, r.Right - r.Left);
        int h = Math.Max(100, r.Bottom - r.Top);
        ShowWindow(hwnd, SW_RESTORE);
        SetWindowPos(hwnd, IntPtr.Zero, r.Left, r.Top, w, h, SWP_NOZORDER);
        return true;
    }

    // drag-to-resize for the borderless custom-chrome popups (Clips, ReplayKit Settings), which have no native resize border to grab. always moves both width and height off the one drag (see the grip css -- its one hit area, not per-edge zones), clamped to bounds the caller passes in per window. polls the left mouse button on a background thread instead of message-based approaches (eg wm_nclbuttondown) becuase GetAsyncKeyState reads live hardware state directly -- it doesnt care that were a different process than obs, or that our http round trip landed some milliseconds after the real mousedown; it just checks "is the button down right now" on every tick, so a late start still tracks correctly from that point on. throttled to ~66fps (15ms) rather than hammering setwindowpos as fast as possible -- the first version of this polled at 1ms and outran cefs own repaint, leaving a black unpainted strip behind the real content until the drag settled.
    public static bool BeginResizeWindow(string needle, int minW, int minH, int maxW, int maxH) {
        IntPtr hwnd = FindObsWindow(needle);
        if (hwnd == IntPtr.Zero) return false;
        RECT startRect;
        POINT startPoint;
        if (!GetWindowRect(hwnd, out startRect)) return false;
        if (!GetCursorPos(out startPoint)) return false;
        PrimeResizeBackground(hwnd);

        long key = hwnd.ToInt64();
        lock (resizeLock) {
            if (resizeTracking.Contains(key)) return true;
            resizeTracking.Add(key);
        }

        System.Threading.Thread t = new System.Threading.Thread(delegate() {
            try {
                int startW = Math.Max(minW, startRect.Right - startRect.Left);
                int startH = Math.Max(minH, startRect.Bottom - startRect.Top);
                int lastW = startW;
                int lastH = startH;
                while ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0) {
                    POINT pt;
                    if (!GetCursorPos(out pt)) break;
                    int w = Math.Max(minW, Math.Min(maxW, startW + (pt.X - startPoint.X)));
                    int h = Math.Max(minH, Math.Min(maxH, startH + (pt.Y - startPoint.Y)));
                    if (w != lastW || h != lastH) {
                        SetWindowPos(hwnd, IntPtr.Zero, startRect.Left, startRect.Top, w, h,
                            SWP_NOZORDER | SWP_NOACTIVATE);
                        RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero,
                            RDW_INVALIDATE | RDW_NOCHILDREN);
                        lastW = w;
                        lastH = h;
                    }
                    System.Threading.Thread.Sleep(15);
                }
            } finally {
                lock (resizeLock) {
                    resizeTracking.Remove(key);
                }
            }
        });
        t.IsBackground = true;
        t.Start();
        return true;
    }

}
'@

# normalize explorer paths before comparing them. file explorer may report the active tab thru locationurl or thru document.folder, depending on windows version and tab state.
function ConvertTo-ExplorerComparePath([string]$path) {
    if ([string]::IsNullOrWhiteSpace($path)) { return $null }
    try {
        return [System.IO.Path]::GetFullPath($path).TrimEnd('\','/')
    } catch {
        return $null
    }
}

function Get-ExplorerWindowPath($window) {
    try {
        $loc = [string]$window.LocationURL
        if (-not [string]::IsNullOrEmpty($loc) -and
            $loc.StartsWith('file:', [StringComparison]::OrdinalIgnoreCase)) {
            return ([System.Uri]::new($loc)).LocalPath
        }
    } catch {}

    try {
        $path = [string]$window.Document.Folder.Self.Path
        if (-not [string]::IsNullOrWhiteSpace($path)) { return $path }
    } catch {}

    return $null
}

# find an already-open file explorer window thats showing $targetpath. returns the windows hwnd (as intptr) or [intptr]::zero if no match. used by /open-clip-folder and /open-folder to raise an existing explorer window for the clip folder instead of spawning yet another explorer.exe -- windows otherwise stacks duplicate folder windows every time the user clicks the docks open folder button. we use shell.applications windows() collection (the same one explorer itself uses). locationurl on each item is a file:// url we can convert to a local path and compare case-insensitively to the target. ie/edge browser windows show up here too -- we skip anything whose locationurl isnt file://.
function Get-ExplorerHwndForPath([string]$targetPath) {
    if ([string]::IsNullOrWhiteSpace($targetPath)) { return [IntPtr]::Zero }
    $full = ConvertTo-ExplorerComparePath $targetPath
    if (-not $full) { return [IntPtr]::Zero }

    $shell   = $null
    $windows = $null
    try {
        $shell   = New-Object -ComObject Shell.Application
        $windows = $shell.Windows()
        foreach ($w in $windows) {
            try {
                $here = ConvertTo-ExplorerComparePath (Get-ExplorerWindowPath $w)
                if ([string]::Equals($here, $full, [StringComparison]::OrdinalIgnoreCase)) {
                    $raw = [int64]$w.HWND
                    if ($raw -ne 0) {
                        return [IntPtr]::new($raw)
                    }
                }
            } catch {
                # per-window failures (window dying mid-enumeration, com transient errors, etc.) shouldnt take out the whole search; just skip this entry.
            }
        }
    } catch {
        Write-Log "Get-ExplorerHwndForPath failed: $($_.Exception.Message)"
    } finally {
        if ($windows) { try { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($windows) } catch {} }
        if ($shell)   { try { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)   } catch {} }
    }
    return [IntPtr]::Zero
}

# chromium cookie extraction -- read obss own cef cookie jar, decrypt values, and write a curl-compatible jar. this is what makes "sign in with google" actualy work: the cef window we open lands the session cookie in obss cookie db, we yank it out, the upload script uses it. two native dependencies, both already on every windows 10/11 box (no dlls to ship): winsqlite3.dll -- the sqlite library windows itself uses for things like the search index. same c api as upstream sqlite. bcrypt.dll -- aes-gcm decryption for the cookie value (chromium 80+ encrypts cookies with aes-gcm, master key dpapi-encrypted inside local state).
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

// sqlite via winsqlite3.dll (ships with windows 10/11). cdecl, utf-8 strings. we open the cookies db read-only with immutable=1 so we never contend with obss cef holding the writer lock.
public static class WinSqlite {
    const string DLL = "winsqlite3.dll";
    public const int SQLITE_OK            = 0;
    public const int SQLITE_ROW           = 100;
    public const int SQLITE_DONE          = 101;
    public const int SQLITE_OPEN_READONLY = 1;
    public const int SQLITE_OPEN_URI      = 0x40;

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int sqlite3_open_v2(byte[] filename, out IntPtr ppDb, int flags, IntPtr zVfs);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_close_v2(IntPtr db);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int sqlite3_prepare_v2(IntPtr db, byte[] zSql, int nByte, out IntPtr ppStmt, IntPtr pzTail);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_step(IntPtr stmt);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_finalize(IntPtr stmt);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr sqlite3_column_blob(IntPtr stmt, int col);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_column_bytes(IntPtr stmt, int col);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    public static extern long sqlite3_column_int64(IntPtr stmt, int col);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr sqlite3_errmsg(IntPtr db);

    // helper: pull a text column out as a managed string (utf-8 -> .net).
    public static string ColumnText(IntPtr stmt, int col) {
        IntPtr p = sqlite3_column_text(stmt, col);
        if (p == IntPtr.Zero) return null;
        int n = sqlite3_column_bytes(stmt, col);
        if (n <= 0) return "";
        byte[] buf = new byte[n];
        Marshal.Copy(p, buf, 0, n);
        return Encoding.UTF8.GetString(buf);
    }

    // helper: pull a blob column out as a managed byte[].
    public static byte[] ColumnBlob(IntPtr stmt, int col) {
        IntPtr p = sqlite3_column_blob(stmt, col);
        int n = sqlite3_column_bytes(stmt, col);
        if (p == IntPtr.Zero || n <= 0) return new byte[0];
        byte[] buf = new byte[n];
        Marshal.Copy(p, buf, 0, n);
        return buf;
    }

    // encode a string the way sqlite expects: null-terminated utf-8.
    public static byte[] Utf8Z(string s) {
        if (s == null) s = "";
        byte[] body = Encoding.UTF8.GetBytes(s);
        byte[] z = new byte[body.Length + 1];
        Array.Copy(body, z, body.Length);
        return z;
    }
}

// aes-gcm via bcrypt.dll. ps 5.1 runs on .net framework 4.x which has no aesgcm class -- bcrypt is the os-provided way to get authenticated aes on this platform. chromium cookie payload: bytes 0..2 : "v10" or "v11" prefix bytes 3..14 : 12-byte nonce bytes 15..n-17 : ciphertext bytes n-16..n-1 : 16-byte auth tag
public static class BCryptAesGcm {
    [StructLayout(LayoutKind.Sequential)]
    struct BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO {
        public int cbSize;
        public int dwInfoVersion;
        public IntPtr pbNonce;
        public int cbNonce;
        public IntPtr pbAuthData;
        public int cbAuthData;
        public IntPtr pbTag;
        public int cbTag;
        public IntPtr pbMacContext;
        public int cbMacContext;
        public int cbAAD;
        public long cbData;
        public int dwFlags;
    }

    [DllImport("bcrypt.dll")]
    static extern uint BCryptOpenAlgorithmProvider(out IntPtr phAlgorithm,
        [MarshalAs(UnmanagedType.LPWStr)] string pszAlgId,
        [MarshalAs(UnmanagedType.LPWStr)] string pszImplementation,
        uint dwFlags);

    [DllImport("bcrypt.dll")]
    static extern uint BCryptCloseAlgorithmProvider(IntPtr hAlgorithm, uint dwFlags);

    [DllImport("bcrypt.dll")]
    static extern uint BCryptSetProperty(IntPtr hObject,
        [MarshalAs(UnmanagedType.LPWStr)] string pszProperty,
        byte[] pbInput, int cbInput, int dwFlags);

    [DllImport("bcrypt.dll")]
    static extern uint BCryptGenerateSymmetricKey(IntPtr hAlgorithm,
        out IntPtr phKey, IntPtr pbKeyObject, int cbKeyObject,
        byte[] pbSecret, int cbSecret, int dwFlags);

    [DllImport("bcrypt.dll")]
    static extern uint BCryptDestroyKey(IntPtr hKey);

    [DllImport("bcrypt.dll")]
    static extern uint BCryptDecrypt(IntPtr hKey,
        byte[] pbInput, int cbInput,
        ref BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO pPaddingInfo,
        byte[] pbIV, int cbIV,
        byte[] pbOutput, int cbOutput,
        out int pcbResult, int dwFlags);

    public static byte[] Decrypt(byte[] key, byte[] nonce, byte[] ciphertext, byte[] tag) {
        IntPtr hAlg = IntPtr.Zero;
        IntPtr hKey = IntPtr.Zero;
        try {
            uint s = BCryptOpenAlgorithmProvider(out hAlg, "AES", null, 0);
            if (s != 0) throw new Exception("BCryptOpenAlgorithmProvider 0x" + s.ToString("x8"));

            // chainingmodegcm is a wide-string property, no trailing null counted in length.
            byte[] gcm = Encoding.Unicode.GetBytes("ChainingModeGCM\0");
            s = BCryptSetProperty(hAlg, "ChainingMode", gcm, gcm.Length, 0);
            if (s != 0) throw new Exception("BCryptSetProperty 0x" + s.ToString("x8"));

            s = BCryptGenerateSymmetricKey(hAlg, out hKey, IntPtr.Zero, 0, key, key.Length, 0);
            if (s != 0) throw new Exception("BCryptGenerateSymmetricKey 0x" + s.ToString("x8"));

            GCHandle hNonce = GCHandle.Alloc(nonce, GCHandleType.Pinned);
            GCHandle hTag   = GCHandle.Alloc(tag,   GCHandleType.Pinned);
            try {
                BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO info = new BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO();
                info.cbSize        = Marshal.SizeOf(typeof(BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO));
                info.dwInfoVersion = 1;
                info.pbNonce       = hNonce.AddrOfPinnedObject();
                info.cbNonce       = nonce.Length;
                info.pbTag         = hTag.AddrOfPinnedObject();
                info.cbTag         = tag.Length;

                byte[] plain = new byte[ciphertext.Length];
                int outSize;
                s = BCryptDecrypt(hKey, ciphertext, ciphertext.Length, ref info,
                                  null, 0, plain, plain.Length, out outSize, 0);
                if (s != 0) throw new Exception("BCryptDecrypt 0x" + s.ToString("x8"));
                if (outSize != plain.Length) Array.Resize(ref plain, outSize);
                return plain;
            } finally {
                hNonce.Free();
                hTag.Free();
            }
        } finally {
            if (hKey != IntPtr.Zero) BCryptDestroyKey(hKey);
            if (hAlg != IntPtr.Zero) BCryptCloseAlgorithmProvider(hAlg, 0);
        }
    }
}
'@


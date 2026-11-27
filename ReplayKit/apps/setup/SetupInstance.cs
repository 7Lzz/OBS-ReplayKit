using System;
using System.Threading;

namespace ReplayKitSetup
{
    // one setup or uninstall window at a time: a second launch brings the open one forward instead of running a second install over it
    internal static class SetupInstance
    {
        private const string MutexName = @"Local\OBSReplayKitSetupWindow";
        private const int SW_SHOW = 5;
        private const int SW_RESTORE = 9;

        // null when another setup or uninstall window already holds it; the window that gets it keeps it until it closes
        public static IDisposable TryTake()
        {
            var mutex = new Mutex(false, MutexName, out bool created);
            if (created) return mutex;
            mutex.Dispose();
            return null;
        }

        // a launch moments after the first one can find no window yet, and then the first one shows itself anyway
        public static void ShowOpenWindow()
        {
            foreach (string className in new[] { SetupWindow.ClassName, UninstallWindow.ClassName })
            {
                IntPtr hwnd = Native.FindWindowW(className, null);
                if (hwnd == IntPtr.Zero) continue;
                Native.ShowWindow(hwnd, Native.IsIconic(hwnd) ? SW_RESTORE : SW_SHOW);
                Native.SetForegroundWindow(hwnd);
                SetupLog.Line("another setup window is already open; brought it forward instead of opening a second one");
                return;
            }
        }
    }
}

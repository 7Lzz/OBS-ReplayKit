using System;
using System.Runtime.InteropServices;

namespace ReplayKitSetup
{
    // fast process exit helpers for the console setup app. ported from obs_replaykit/fast_exit.py. the python original also hunts down and kills a "pyinstaller onefile bootstrap parent" process on exit -- that concept does not exist for a compiled .net exe (no separate bootstrap/child process split the way pyinstaller onefile has), so that step is simply gone here, not carried over as dead code.
    public static class FastExit
    {
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate handlerRoutine, bool add);

        private delegate bool ConsoleCtrlDelegate(uint ctrlType);

        private const int SW_HIDE = 0;

        private static ConsoleCtrlDelegate _ctrlHandlerRef;

        // hide this processs own console window if it has one. --update mode should never show a window regardless of how it was launched.
        public static void HideConsoleWindow()
        {
            IntPtr hwnd = GetConsoleWindow();
            if (hwnd != IntPtr.Zero) ShowWindow(hwnd, SW_HIDE);
        }

        // exit now -- no onefile parent to chase down in the .net build, see file header.
        public static void FastExitNow(int rc = 0)
        {
            try { TerminateProcess(GetCurrentProcess(), (uint)rc); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception) { }
            Environment.Exit(rc);
        }

        public static void InstallConsoleCloseHandler()
        {
            bool Handler(uint ctrlType)
            {
                FastExitNow(0);
                return true;
            }
            _ctrlHandlerRef = Handler;
            SetConsoleCtrlHandler(_ctrlHandlerRef, true);
        }
    }
}

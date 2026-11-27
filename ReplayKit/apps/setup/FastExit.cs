using System;
using System.Runtime.InteropServices;

namespace ReplayKitSetup
{
    // fast process exit helpers for the setup app
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
        private static extern bool FreeConsole();

        private const int SW_HIDE = 0;

        // hide this processs own console window if it has one. --update mode should never show a window regardless of how it was launched.
        private static void HideConsoleWindow()
        {
            IntPtr hwnd = GetConsoleWindow();
            if (hwnd != IntPtr.Zero) ShowWindow(hwnd, SW_HIDE);
        }

        // headless modes (--update, --cleanup, --uninstall-discord-screenshare) give up their console entirely rather than just hiding it. the helper that spawns us goes away moments after --update closes obs, and if we are still attached to a console that goes down with it, every attached process gets CTRL_CLOSE_EVENT, whose default handler terminates the process outright. that skips every finally block, so obs is already dead and nothing relaunches it. after detaching there is nowhere for console output to go, so it is pointed at a null writer instead of an invalid handle; the log file is the real record for these modes anyway.
        public static void DetachFromConsole()
        {
            HideConsoleWindow();
            FreeConsole();
            try
            {
                Console.SetOut(System.IO.TextWriter.Null);
                Console.SetError(System.IO.TextWriter.Null);
            }
            catch (System.IO.IOException) { }
        }

        // terminates at once, so nothing left running in the process can hold its exit
        public static void FastExitNow(int rc = 0)
        {
            try { TerminateProcess(GetCurrentProcess(), (uint)rc); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception) { }
            Environment.Exit(rc);
        }
    }
}

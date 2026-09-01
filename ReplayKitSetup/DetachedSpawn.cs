using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ReplayKitSetup
{
    // relaunches this exe as a process with no console and no job-object membership, so the install cannot be torn down by whatever happens to OBS. the helper that starts an update dies moments after the installer taskkills obs, and anything the installer inherited from it -- a console it shares, a job it was created inside -- takes the installer down with it. both of those are decided at CreateProcess time and cannot be shed afterwards, which is why this is a relaunch rather than a flag.
    internal static class DetachedSpawn
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved, lpDesktop, lpTitle;
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

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcessW(string applicationName, StringBuilder commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory,
            ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private const uint DETACHED_PROCESS = 0x00000008;
        private const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
        private const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;

        // the flag the relaunched copy carries so it knows it is already detached and does the work instead of relaunching again.
        public const string DetachedFlag = "--detached";

        // where the detached copy records its own pid. the helper watches THIS rather than the process it started,
        // because the process it started is only the launcher and exits within a second of handing off.
        public static string PidFilePath() => Path.Combine(Path.GetTempPath(), "ReplayKit", "logs", "update_pid");

        public static void WriteOwnPid()
        {
            try
            {
                string path = PidFilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, System.Diagnostics.Process.GetCurrentProcess().Id.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        // returns the new pid, or 0 when the relaunch could not be made. a breakaway is refused when the job we are
        // in does not permit it, so that failure is retried without the flag -- losing job independence but keeping
        // the detached console, which is still better than running inline.
        public static int Relaunch(string[] args)
        {
            string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
            var passthrough = new System.Collections.Generic.List<string>(args) { DetachedFlag };
            string commandLine = Win32Args.Build(exe) + " " + Win32Args.Build(passthrough.ToArray());

            int pid = Spawn(commandLine, exe, DETACHED_PROCESS | CREATE_BREAKAWAY_FROM_JOB | CREATE_NEW_PROCESS_GROUP);
            if (pid == 0) pid = Spawn(commandLine, exe, DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP);
            return pid;
        }

        private static int Spawn(string commandLine, string exePath, uint flags)
        {
            var si = new STARTUPINFO { cb = Marshal.SizeOf(typeof(STARTUPINFO)) };
            var cmd = new StringBuilder(commandLine);
            PROCESS_INFORMATION pi;
            if (!CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero,
                                Path.GetDirectoryName(exePath), ref si, out pi))
            {
                return 0;
            }
            int pid = pi.dwProcessId;
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            return pid;
        }
    }
}

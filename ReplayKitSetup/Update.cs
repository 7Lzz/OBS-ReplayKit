using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ReplayKitSetup
{
    // updater mode for OBS ReplayKit release installers. ported from obs_replaykit/update.py.
    public static class Update
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private const uint SYNCHRONIZE = 0x00100000;
        private const uint WAIT_OBJECT_0 = 0;

        private static string LogFile() => Path.Combine(Path.GetTempPath(), "OBSReplayKitUpdate.log");

        private static void Log(string message)
        {
            try
            {
                File.AppendAllText(LogFile(), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n", new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
        }

        private static bool WaitForPid(int pid, int timeoutS = 60)
        {
            if (pid <= 0) return true;
            IntPtr handle = OpenProcess(SYNCHRONIZE, false, pid);
            if (handle == IntPtr.Zero) return true;
            try
            {
                uint result = WaitForSingleObject(handle, (uint)(timeoutS * 1000));
                return result == WAIT_OBJECT_0;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private static string SafeCleanupDir(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\', '/');
            string target;
            try { target = Path.GetFullPath(path); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return null; }

            if (!string.Equals(new DirectoryInfo(target).Name, "ReplayKitUpdate", StringComparison.Ordinal)) return null;
            if (!target.StartsWith(tempRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(target, tempRoot, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return target;
        }

        private static string PsSingleQuote(string value) => "'" + value.Replace("'", "''") + "'";

        private static void ScheduleCleanup(string path)
        {
            string target = SafeCleanupDir(path);
            if (target == null) return;
            string command = "Start-Sleep -Seconds 3; Remove-Item -LiteralPath " + PsSingleQuote(target) + " -Recurse -Force -ErrorAction SilentlyContinue";
            try
            {
                var psi = new ProcessStartInfo("powershell.exe", Win32Args.Build("-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-Command", command))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                Process.Start(psi);
            }
            catch (Exception exc) when (exc is System.ComponentModel.Win32Exception || exc is InvalidOperationException)
            {
                Log("cleanup launch failed: " + exc.Message);
            }
        }

        private static bool LaunchObsPath(string obsPath, Action<string> log)
        {
            string obs = !string.IsNullOrEmpty(obsPath) ? obsPath : Obs.FindObsExe();
            if (obs == null || !File.Exists(obs))
            {
                log?.Invoke("OBS executable was not found for relaunch.");
                return false;
            }
            try
            {
                var psi = new ProcessStartInfo(obs, Obs.OBS_START_ARGS)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(obs),
                };
                Process.Start(psi);
                log?.Invoke("relaunched OBS: " + obs);
                return true;
            }
            catch (Exception exc) when (exc is System.ComponentModel.Win32Exception || exc is InvalidOperationException)
            {
                log?.Invoke("OBS relaunch failed: " + exc.Message);
                return false;
            }
        }

        // run a non-interactive repair/update install from a downloaded release exe.
        public static int RunUpdateMode(string cleanupDir = null, string relaunchObs = null, int waitPid = 0, int startDelayMs = 0, bool relaunch = true)
        {
            void LogBoth(string message)
            {
                Log(message);
                try { Console.WriteLine(message); } catch (IOException) { }
            }

            try
            {
                Log("update starting version=" + VersionInfo.Version);
                if (startDelayMs > 0) Thread.Sleep(Math.Min(startDelayMs, 5000));

                Obs.CloseObs(LogBoth);
                WaitForPid(waitPid > 0 ? waitPid : 0, 60);
                Obs.CleanupCrashFlags(LogBoth);
                int count = Installer.InstallReplaykitRuntimeUpdate(LogBoth);
                LogBoth($"runtime update copied {count} file(s)");
                if (relaunch) LaunchObsPath(relaunchObs, LogBoth);
                return 0;
            }
            catch (Exception exc)
            {
                Log("update failed: " + exc.Message);
                return 1;
            }
            finally
            {
                ScheduleCleanup(cleanupDir);
            }
        }

        // headless progress reporter for --cleanup mode (log lines only). Cli.InstallProgress is the rich ANSI equivalent used by the interactive menu; both implement IInstallProgress so Cleanup.RunCleanup works from either caller.
        public sealed class CleanupProgress : IInstallProgress
        {
            public int TotalSteps { get; set; }
            public List<string> Issues { get; } = new List<string>();

            public void Render(int completed, string title, string detail, string state = "working")
            {
                string status = state == "done" ? "done" : (state == "failed" ? "failed" : "working");
                string message = $"[{completed}/{Math.Max(1, TotalSteps)}] {status}: {title}";
                if (!string.IsNullOrEmpty(detail)) message += " - " + detail;
                Log(message);
                try { Console.WriteLine(message); } catch (IOException) { }
            }

            private static readonly string[] IssueWords = { "warn", "failed", "missing", "not found", "skipped", "timed out", "permission denied" };

            public void LogLine(string message)
            {
                string text = (message ?? "").Trim();
                if (text.Length == 0) return;
                Log(text);
                string lowered = text.ToLowerInvariant();
                if (IssueWords.Any(word => lowered.Contains(word))) AddIssue(text);
                try { Console.WriteLine(text); } catch (IOException) { }
            }

            public void AddIssue(string message)
            {
                string cleaned = string.Join(" ", (message ?? "").Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries));
                if (cleaned.Length > 118) cleaned = cleaned.Substring(0, 115) + "...";
                if (cleaned.Length > 0 && !Issues.Contains(cleaned)) Issues.Add(cleaned);
            }
        }

        public static int RunCleanupMode(int startDelayMs = 0, bool keepUserSettings = true)
        {
            try
            {
                Log("cleanup starting");
                if (startDelayMs > 0) Thread.Sleep(Math.Min(startDelayMs, 5000));
                var progress = new CleanupProgress();
                var issues = Cleanup.RunCleanup(progress, keepUserSettings);
                if (issues.Count > 0) Log($"cleanup complete with {issues.Count} warning(s)");
                else Log("cleanup complete");
                return 0;
            }
            catch (Exception exc)
            {
                Log("cleanup failed: " + exc.Message);
                return 1;
            }
        }

        // parses the subset of argv this app needs for --update/--cleanup mode. returns null if neither flag is present (normal interactive launch).
        public static int? TryRunUpdateFromArgv(string[] argv)
        {
            if (argv.Contains("--cleanup"))
            {
                bool removeUserSettings = argv.Contains("--remove-user-settings");
                int startDelay = IntArg(argv, "--start-delay-ms", 0);
                return RunCleanupMode(startDelay, !removeUserSettings);
            }
            if (!argv.Contains("--update")) return null;

            string cleanupDir = StringArg(argv, "--cleanup-dir", "");
            string relaunchObs = StringArg(argv, "--relaunch-obs", "");
            bool noRelaunch = argv.Contains("--no-relaunch-obs");
            int waitPid = IntArg(argv, "--wait-pid", 0);
            int startDelayMs = IntArg(argv, "--start-delay-ms", 0);
            return RunUpdateMode(cleanupDir, relaunchObs, waitPid, startDelayMs, !noRelaunch);
        }

        private static string StringArg(string[] argv, string name, string @default)
        {
            for (int i = 0; i < argv.Length - 1; i++)
            {
                if (argv[i] == name) return argv[i + 1];
            }
            foreach (var a in argv)
            {
                if (a.StartsWith(name + "=")) return a.Substring(name.Length + 1);
            }
            return @default;
        }

        private static int IntArg(string[] argv, string name, int @default)
        {
            string raw = StringArg(argv, name, null);
            return raw != null && int.TryParse(raw, out int value) ? value : @default;
        }
    }
}

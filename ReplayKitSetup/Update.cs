using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr handle, uint exitCode);

        private const uint SYNCHRONIZE = 0x00100000;
        private const uint PROCESS_TERMINATE = 0x0001;
        private const uint WAIT_OBJECT_0 = 0;
        private const string ReleasePage = "https://github.com/7Lzz/OBS-ReplayKit/releases/latest";

        private static string LogFile() => Path.Combine(Path.GetTempPath(), "OBSReplayKitUpdate.log");

        // the helper serves this back over /update/install-result so the update popup can say what actually happened. it lives beside the helper logs, not in the update temp dir, which ScheduleCleanup wipes right after this process exits.
        private static string ResultFile() => Path.Combine(Path.GetTempPath(), "ReplayKit", "logs", "update_result.json");

        private static void WriteResult(bool ok, string stage, string message, string releaseUrl = ReleasePage)
        {
            try
            {
                string path = ResultFile();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var payload = new JObject
                {
                    ["ok"] = ok,
                    ["stage"] = stage,
                    ["message"] = message ?? "",
                    ["version"] = VersionInfo.Version,
                    ["releaseUrl"] = string.IsNullOrWhiteSpace(releaseUrl) ? ReleasePage : releaseUrl,
                    ["finishedAt"] = DateTime.UtcNow.ToString("o"),
                };
                File.WriteAllText(path, payload.ToString(Formatting.Indented) + "\n", new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
        }

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

        private static bool WaitForPid(int pid, int timeoutS, Action<string> log)
        {
            if (pid <= 0) return true;
            IntPtr handle = OpenProcess(SYNCHRONIZE | PROCESS_TERMINATE, false, pid);
            if (handle == IntPtr.Zero) return true;
            try
            {
                uint result = WaitForSingleObject(handle, (uint)(timeoutS * 1000));
                if (result == WAIT_OBJECT_0) return true;
                log?.Invoke("old ReplayKit helper did not exit within " + timeoutS + "s; terminating it before the file transaction");
                if (!TerminateProcess(handle, 1)) return false;
                return WaitForSingleObject(handle, 10000) == WAIT_OBJECT_0;
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

            // the helper downloads into %temp%\ReplayKit\update; test_update_mode.bat still uses the flat %temp%\ReplayKitUpdate. only those two shapes are accepted, so a bad --cleanup-dir can never point a recursive delete at a real folder -- the nested one used to fall through this name check, which is why every update so far left its downloaded installer behind in %temp%.
            var dir = new DirectoryInfo(target);
            string name = dir.Name ?? "";
            bool uniqueUpdate = name.StartsWith("update-", StringComparison.OrdinalIgnoreCase) &&
                                Guid.TryParseExact(name.Substring(7), "N", out Guid ignored);
            bool named = string.Equals(name, "ReplayKitUpdate", StringComparison.Ordinal) ||
                         (uniqueUpdate && string.Equals(dir.Parent?.Name, "ReplayKit", StringComparison.Ordinal));
            if (!named) return null;
            if (!target.StartsWith(tempRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
            return target;
        }

        private static string PsSingleQuote(string value) => "'" + value.Replace("'", "''") + "'";

        // cant rmdir this update dir while this exe is still running from inside it, so hand the delete to a detached cmd that outlives us by a few seconds -- used to copy this exe under a new name and launch that copy with --cleanup-update-dir instead, but a self-duplicating renamed executable is exactly the shape av heuristics flag as a dropper, and it was doing that on every single update; target only ever comes from SafeCleanupDir, which limits it to our own temp-path shapes, so plain quoting is safe here since windows paths cannot contain a literal quote character.
        private static void ScheduleCleanup(string path)
        {
            string target = SafeCleanupDir(path);
            if (target == null) return;
            try
            {
                // ping as the delay instead of timeout.exe -- timeout refuses to run when stdin isnt a real console handle, which is exactly the case for a process launched with UseShellExecute=false from another non-interactive process.
                string cmdLine = "/c ping 127.0.0.1 -n 4 >nul & rmdir /s /q \"" + target + "\"";
                var psi = new ProcessStartInfo("cmd.exe", cmdLine)
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
        public static int RunUpdateMode(string cleanupDir = null, string relaunchObs = null, int waitPid = 0, int startDelayMs = 0, bool relaunch = true, string releaseUrl = ReleasePage)
        {
            void LogBoth(string message)
            {
                Log(message);
                try { Console.WriteLine(message); } catch (IOException) { }
            }

            bool obsClosed = false;
            bool installed = false;
            bool relaunched = false;
            Mutex updateGate = null;
            bool updateGateHeld = false;
            try
            {
                updateGate = new Mutex(false, @"Local\OBSReplayKit-runtime-update");
                try { updateGateHeld = updateGate.WaitOne(TimeSpan.FromSeconds(5)); }
                catch (AbandonedMutexException) { updateGateHeld = true; }
                if (!updateGateHeld)
                {
                    const string busy = "Another ReplayKit update is already running.";
                    LogBoth(busy);
                    WriteResult(false, "busy", busy, releaseUrl);
                    return 1;
                }
                Log("update starting version=" + VersionInfo.Version);
                if (startDelayMs > 0) Thread.Sleep(Math.Min(startDelayMs, 5000));

                // the payload is checked before obs is touched. a release exe built without its embedded assets used to get past here, kill obs, then discover it had nothing to install -- leaving no obs, no helper, and an update popup waiting on a restart that never came.
                string runtimeSrc = Installer.GetRuntimeAssetsDir();
                if (!Directory.Exists(runtimeSrc))
                {
                    string reason = AssetBundle.LastError ?? "This OBSReplayKit.exe was built without its bundled ReplayKit files.";
                    string message = "ReplayKit runtime assets not found: " + runtimeSrc + ". " + reason;
                    LogBoth("preflight failed, OBS was left running: " + message);
                    WriteResult(false, "preflight", message, releaseUrl);
                    return 1;
                }

                Obs.CloseObs(LogBoth);
                obsClosed = true;
                // stage markers: a process that is killed leaves no exception behind, so the last line in the log is
                // the only thing that says where it got to.
                Log("stage: obs closed, waiting for helper pid " + waitPid);
                if (!WaitForPid(waitPid > 0 ? waitPid : 0, 60, LogBoth))
                    throw new IOException("The old ReplayKit helper could not be stopped, so no files were changed.");
                Log("stage: helper gone, clearing crash flags");
                Obs.CleanupCrashFlags(LogBoth);
                Log("stage: copying runtime files");
                int count = Installer.InstallReplaykitRuntimeUpdate(LogBoth);
                LogBoth($"runtime update copied {count} file(s)");
                installed = true;

                if (relaunch)
                {
                    relaunched = LaunchObsPath(relaunchObs, LogBoth);
                    if (!relaunched)
                    {
                        WriteResult(false, "relaunch", $"ReplayKit {VersionInfo.Version} was installed but OBS could not be restarted. Start OBS manually.", releaseUrl);
                        return 1;
                    }
                }
                WriteResult(true, "done", $"ReplayKit {VersionInfo.Version} installed ({count} file(s)).", releaseUrl);
                return 0;
            }
            catch (Exception exc)
            {
                Log("update failed: " + exc.Message);
                WriteResult(false, obsClosed ? "install" : "startup", exc.Message, releaseUrl);
                return 1;
            }
            finally
            {
                // obs has to come back even when the install died partway thru the copy, otherwise a failed update costs the user their whole obs session. skipped once the install got far enough to report its own relaunch outcome.
                if (obsClosed && relaunch && !installed) LaunchObsPath(relaunchObs, LogBoth);
                if (updateGateHeld) updateGate.ReleaseMutex();
                updateGate?.Dispose();
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

        // headless counterpart to Cli.UninstallDiscordScreenshareOnly -- same two steps (close obs, remove the virtual audio cable), reported thru CleanupProgress instead of the interactive console since this runs detached from the settings docks uninstall box.
        public static int RunUninstallDiscordScreenshareMode(int startDelayMs = 0)
        {
            try
            {
                Log("discord screenshare removal starting");
                if (startDelayMs > 0) Thread.Sleep(Math.Min(startDelayMs, 5000));
                var progress = new CleanupProgress { TotalSteps = 2 };
                Cli.RunApplyStep(progress, 1, "Close OBS", "Stops OBS and ReplayKit helpers so the audio driver can be removed.", () => Cleanup.StopObsAndHelpers(progress.LogLine));
                Cli.RunApplyStep(progress, 2, "Remove OBS Stream Audio", "Uninstalls the ReplayKit virtual audio device.", () => VbCable.UninstallVbcable(progress.LogLine));
                var prefs = Prefs.LoadPrefs();
                prefs.DiscordScreenshareEnabled = false;
                prefs.DiscordProjectorEnabled = false;
                prefs.Save();
                if (progress.Issues.Count > 0) Log($"discord screenshare removal complete with {progress.Issues.Count} warning(s)");
                else Log("discord screenshare removal complete");
                return 0;
            }
            catch (Exception exc)
            {
                Log("discord screenshare removal failed: " + exc.Message);
                return 1;
            }
        }

        // parses the subset of argv this app needs for --update/--cleanup/--uninstall-discord-screenshare mode. returns null if none of those flags are present (normal interactive launch).
        public static int? TryRunUpdateFromArgv(string[] argv)
        {
            if (argv.Contains("--cleanup"))
            {
                bool removeUserSettings = argv.Contains("--remove-user-settings");
                int startDelay = IntArg(argv, "--start-delay-ms", 0);
                return RunCleanupMode(startDelay, !removeUserSettings);
            }
            if (argv.Contains("--uninstall-discord-screenshare"))
            {
                int startDelay = IntArg(argv, "--start-delay-ms", 0);
                return RunUninstallDiscordScreenshareMode(startDelay);
            }
            if (!argv.Contains("--update")) return null;

            // hand the whole job to a copy of ourselves that owns no console and belongs to no job object, then get
            // out of the way. everything this process inherited from the helper dies with OBS moments from now; the
            // detached copy inherits none of it, so killing OBS cannot reach it. see DetachedSpawn.
            if (!argv.Contains(DetachedSpawn.DetachedFlag))
            {
                int detachedPid = DetachedSpawn.Relaunch(argv);
                if (detachedPid > 0)
                {
                    Log("relaunched detached as pid " + detachedPid + "; this launcher is done");
                    return 0;
                }
                // could not detach -- carry on inline rather than skipping the update, and say so, so a failure right
                // after OBS closes is recognisable instead of looking like a silent death.
                Log("warn: could not relaunch detached, running inline (an OBS teardown may kill this process)");
            }
            else
            {
                DetachedSpawn.WriteOwnPid();
            }

            string cleanupDir = StringArg(argv, "--cleanup-dir", "");
            string relaunchObs = StringArg(argv, "--relaunch-obs", "");
            bool noRelaunch = argv.Contains("--no-relaunch-obs");
            int waitPid = IntArg(argv, "--wait-pid", 0);
            int startDelayMs = IntArg(argv, "--start-delay-ms", 0);
            string releaseUrl = StringArg(argv, "--release-url", ReleasePage);
            if (!Uri.TryCreate(releaseUrl, UriKind.Absolute, out Uri releaseUri) || releaseUri.Scheme != Uri.UriSchemeHttps)
                releaseUrl = ReleasePage;
            return RunUpdateMode(cleanupDir, relaunchObs, waitPid, startDelayMs, !noRelaunch, releaseUrl);
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

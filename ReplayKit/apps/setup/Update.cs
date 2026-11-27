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
using ReplayKitShared;

namespace ReplayKitSetup
{
    // the headless modes of the release installer: --update, --cleanup, --uninstall and the discord screenshare install/removal
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

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageNameW(IntPtr handle, uint flags, StringBuilder path, ref uint size);

        private const uint SYNCHRONIZE = 0x00100000;
        private const uint PROCESS_TERMINATE = 0x0001;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint WAIT_OBJECT_0 = 0;
        private const string ReleasePage = "https://github.com/7Lzz/OBS-ReplayKit/releases/latest";

        private static string LogFile() => Path.Combine(Path.GetTempPath(), "OBSReplayKitUpdate.log");

        // the helper serves this back over /update/install-result so the update popup can say what actually happened. it lives beside the helper logs, not in the update folder this exe runs from, which the helper clears once the update is over (ClearStaleUpdateFolders).
        private static string ResultFile(string attemptId) => Path.Combine(TempLayout.Logs, TempLayout.UpdateFile(TempLayout.UpdateResultFile, attemptId));

        private static void WriteProgress(string path, JObject step)
        {
            if (step.Value<string>("type") != "step") return;
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                using (var writer = new StreamWriter(file, new UTF8Encoding(false)))
                    writer.Write(step.ToString(Formatting.None));
                NetCompat.MoveFile(temporary, path, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log("could not publish update progress: " + ex.Message);
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }

        private static string PendingFile() => Path.Combine(Config.REPLAYKIT_CONFIG, StateLayout.UpdatePendingFile);

        internal static bool RecordSetupResult(bool complete)
        {
            if (complete) File.Delete(PendingFile());
            return WriteResult(complete, complete ? "done" : "incomplete",
                complete ? SetupLocale.Message("setup.replaykitSetupCompleted") : SetupLocale.Message("setup.replaykitSetupIsIncompleteRunThe"));
        }

        internal static void RecordSetupFailure(Exception error) => WriteResult(false, "install", error.Message);

        internal static void MarkUpdatePending()
        {
            string path = PendingFile();
            if (File.Exists(path)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    var payload = new JObject { ["version"] = VersionInfo.Version, ["startedAt"] = DateTime.UtcNow.ToString("o") };
                    byte[] bytes = new UTF8Encoding(false).GetBytes(payload.ToString(Formatting.None));
                    file.Write(bytes, 0, bytes.Length);
                    file.Flush(true);
                }
                NetCompat.MoveFile(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        internal static Exception RecoverInstall(RuntimeTransaction transaction, ConfigBackups.Snapshot snapshot,
            Exception error, Action<string> log, out bool consistent)
        {
            Exception runtimeError = null;
            try { transaction.Recover(log); }
            catch (Exception ex)
            {
                runtimeError = ex;
                log?.Invoke("ReplayKit runtime rollback failed: " + ex.Message);
            }

            var failure = ConfigBackups.Rollback(snapshot, error, log, out consistent);
            if (runtimeError == null) return failure;
            consistent = false;
            return new IOException(SetupLocale.Message("setup.theUpdateFailedAndCouldNotRollBack",
                "error", failure.Message, "rollbackError", runtimeError.Message), error);
        }

        private static bool WriteResult(bool ok, string stage, string message, string releaseUrl = ReleasePage, string attemptId = null)
        {
            try
            {
                string path = ResultFile(attemptId);
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
                string temporary = path + "." + Guid.NewGuid().ToString("N");
                try
                {
                    File.WriteAllText(temporary, payload.ToString(Formatting.Indented) + "\n", new UTF8Encoding(false));
                    NetCompat.MoveFile(temporary, path, true);
                }
                finally { File.Delete(temporary); }
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log("could not write setup result: " + ex.Message);
                return false;
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

        // the pid is checked against the image it runs: the close before this already waited for every owned process, so a pid that is still there can be one windows has handed to another program since
        private static bool WaitForPid(int pid, int timeoutS, Action<string> log)
        {
            if (pid <= 0) return true;
            IntPtr handle = OpenProcess(SYNCHRONIZE | PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return true;
            try
            {
                var image = new StringBuilder(32768);
                uint size = (uint)image.Capacity;
                if (!QueryFullProcessImageNameW(handle, 0, image, ref size))
                    return WaitForSingleObject(handle, 0) == WAIT_OBJECT_0;
                if (!Obs.IsOwnedImage(image.ToString(0, (int)size)))
                {
                    log?.Invoke("pid " + pid + " no longer belongs to the old ReplayKit helper; not waiting for it");
                    return true;
                }
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

        // the update runs elevated and can hand obs its rights, so the path the command line names is only a check against the obs this setup found, never a program it starts
        private static bool LaunchObsPath(string obsPath, Action<string> log)
        {
            string obs = Obs.FindObsExe();
            if (!string.IsNullOrEmpty(obsPath) && !SamePath(obsPath, obs))
                log?.Invoke("the OBS to relaunch is not the OBS this setup found, starting that one instead: " + obsPath);
            if (obs == null || !File.Exists(obs))
            {
                log?.Invoke("OBS executable was not found for relaunch.");
                return false;
            }
            bool started = Obs.LaunchObsAt(obs, log);
            log?.Invoke(started ? "relaunched OBS: " + obs : "OBS relaunch failed");
            return started;
        }

        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return false; }
        }

        public static int RunUpdateMode(string relaunchObs = null, int waitPid = 0, int startDelayMs = 0, bool relaunch = true,
            string releaseUrl = ReleasePage, string attemptId = null, IInstallProgress progress = null)
        {
            void LogBoth(string message)
            {
                Log(message);
                try { Console.WriteLine(message); } catch (IOException) { }
            }
            bool Report(bool ok, string stage, string message) => WriteResult(ok, stage, message, releaseUrl, attemptId);

            if (progress != null) progress.TotalSteps = relaunch ? 8 : 6;
            int completed = 0;
            void Step(SetupLocale.Text title, SetupLocale.Text detail = default) =>
                progress.Render(completed++, title, detail, "working");

            bool obsClosed = false;
            bool launchAttempted = false;
            bool relaunched = false;
            bool configWriting = false;
            bool configConsistent = true;
            bool externalRuntimeWriting = false;
            ConfigBackups.Snapshot configBackup = null;
            string failureStage = "startup";
            IDisposable updateGate = null;
            RuntimeTransaction transaction = null;
            try
            {
                updateGate = ObsExit.TryTakeOperation(ObsExit.SetupGateWaitMs);
                if (updateGate == null)
                {
                    string busy = SetupLocale.Message("setup.anotherReplaykitUpdateIsAlready");
                    LogBoth(busy);
                    if (attemptId != null) Report(false, "busy", busy);
                    return 1;
                }
                DetachedSpawn.WritePid(NetCompat.ProcessId, attemptId);
                Log("update starting version=" + VersionInfo.Version);
                if (startDelayMs > 0) Thread.Sleep(Math.Min(startDelayMs, 5000));

                // the payload is checked before obs is touched. a release exe built without its embedded assets used to get past here, kill obs, then discover it had nothing to install -- leaving no obs, no helper, and an update popup waiting on a restart that never came.
                if (progress != null) Step(SetupLocale.Pair("setup.verifyUpdateFiles"));
                try { Installer.ValidateRuntimeAssets(); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
                {
                    string message = ex.Message;
                    LogBoth("preflight failed, OBS was left running: " + message);
                    Report(false, "preflight", message);
                    return 1;
                }

                // an obs this release can never be put on stops the update before obs is closed or the update is marked pending, otherwise the startup check would close obs for it on every launch
                string unreachable = ObsDownloader.UnreachableObs();
                if (unreachable != null)
                {
                    LogBoth("preflight failed, OBS was left running: " + unreachable);
                    Report(false, "preflight", unreachable);
                    return 1;
                }

                if (progress != null) Step(SetupLocale.Pair("setup.closeObs"), SetupLocale.Pair("setup.stopsObsToWriteSettings"));
                transaction = RuntimeTransaction.Open();
                Obs.CloseObs(LogBoth);
                obsClosed = true;
                failureStage = "install";
                // stage markers: a process that is killed leaves no exception behind, so the last line in the log is the only thing that says where it got to.
                Log("stage: obs closed, waiting for helper pid " + waitPid);
                if (!WaitForPid(waitPid, 60, LogBoth))
                    throw new IOException(SetupLocale.Message("setup.theOldReplaykitHelperCouldNot"));
                if (progress != null) Step(SetupLocale.Pair("setup.backUpCurrentObsSettings"), SetupLocale.Pair("setup.savesARestoreCopyFirst"));
                try { configBackup = ConfigBackups.Create("app-update", LogBoth); }
                catch (Exception ex)
                {
                    LogBoth("preflight failed while backing up OBS config: " + ex.Message);
                    Report(false, "preflight", ex.Message);
                    return 1;
                }
                if (progress != null) Step(SetupLocale.Pair("setup.updateReplaykitFiles"), SetupLocale.Pair("setup.replacesTheReplaykitFilesAndKeeps"));
                transaction.Recover(LogBoth);
                MarkUpdatePending();
                configWriting = true;
                Log("stage: helper gone, clearing crash flags");
                Obs.CleanupCrashFlags(LogBoth);
                Log("stage: copying runtime files");
                var problems = new List<string>();
                externalRuntimeWriting = !ObsVersions.IsInstalledSupported();
                int count = Installer.InstallReplaykitRuntimeUpdate(transaction, LogBoth, problems);
                LogBoth($"runtime update copied {count} file(s)");
                if (problems.Count == 0)
                {
                    if (progress != null) Step(SetupLocale.Pair("setup.setWindowsStartup"));
                    Startup.RefreshObsStartup(LogBoth);
                    if (relaunch)
                    {
                        if (progress != null) Step(SetupLocale.Pair("setup.prepareObsTheme"), SetupLocale.Pair("setup.appliesYourThemeBeforeObsStarts"));
                        Installer.PrepareObsTheme(LogBoth);
                    }
                    if (progress != null) Step(SetupLocale.Pair("setup.refreshVideoToolsAndPlugins"));
                    externalRuntimeWriting = true;
                    if (!ShareWindowInstall.Install(LogBoth)) problems.Add(SetupLocale.Message("setup.theSharePreviewWindow"));
                    try { problems.AddRange(Installer.RunUpdateDriverRefresh(LogBoth)); }
                    catch (Exception ex)
                    {
                        LogBoth("tool and plugin refresh failed: " + ex.Message);
                        problems.Add(SetupLocale.Message("setup.theVideoToolsAndPlugins"));
                    }
                }
                if (problems.Count > 0)
                {
                    failureStage = "incomplete";
                    throw new IOException(SetupLocale.Message("setup.replaykitFilesWereInstalledButThe",
                        "version", VersionInfo.Version, "parts", string.Join(", ", problems)));
                }
                transaction.Complete(LogBoth);
                configWriting = false;
                externalRuntimeWriting = false;
                Installer.CleanupReplaykitLegacyFiles(LogBoth);

                if (relaunch)
                {
                    if (progress != null) Step(SetupLocale.Pair("setup.launchObs"), SetupLocale.Pair("setup.startsObs"));
                    failureStage = "relaunch";
                    launchAttempted = true;
                    relaunched = LaunchObsPath(relaunchObs, LogBoth);
                    if (!relaunched)
                        throw new IOException(SetupLocale.Message("setup.replaykitWasInstalledButObsCould", "version", VersionInfo.Version));
                }
                File.Delete(PendingFile());
                if (Report(true, "done", SetupLocale.Message("setup.replaykitVersionWasInstalled", "version", VersionInfo.Version, "count", count)) && relaunched)
                    ConfigBackups.StartCleanup();
                return 0;
            }
            catch (Exception exc)
            {
                var failure = configWriting ? RecoverInstall(transaction, configBackup, exc, LogBoth, out configConsistent) : exc;
                if (externalRuntimeWriting)
                {
                    failureStage = "incomplete";
                    Log("OBS was left closed because an external runtime change may require repair");
                }
                Log("update failed: " + failure.Message);
                Report(false, failureStage, failure.Message);
                return 1;
            }
            finally
            {
                // bring obs back once the runtime is consistent, including after a successful rollback.
                if (obsClosed && relaunch && !launchAttempted && configConsistent && !externalRuntimeWriting)
                {
                    try { if (!transaction.Pending) LaunchObsPath(relaunchObs, LogBoth); }
                    catch (Exception ex) { Log("OBS recovery launch failed: " + ex.Message); }
                }
                configBackup?.Dispose();
                transaction?.Dispose();
                updateGate?.Dispose();
            }
        }

        // headless progress reporter for --cleanup and the discord screenshare modes (log lines only); PageProgress is the one of the setup and uninstall windows
        public sealed class CleanupProgress : IInstallProgress
        {
            public int TotalSteps { get; set; }
            public List<string> Issues { get; } = new List<string>();

            public void Render(int completed, SetupLocale.Text title, SetupLocale.Text detail, string state = "working")
            {
                string status = state == "done" ? "done" : (state == "failed" ? "failed" : "working");
                string message = $"[{completed}/{Math.Max(1, TotalSteps)}] {status}: {title.English}";
                if (!string.IsNullOrEmpty(detail.English)) message += " - " + detail.English;
                Log(message);
                try { Console.WriteLine(message); } catch (IOException) { }
            }

            public void LogLine(string message)
            {
                string text = (message ?? "").Trim();
                if (text.Length == 0) return;
                Log(text);
                if (Apply.LooksLikeIssue(text)) AddIssue(new SetupLocale.Text(text, text));
                try { Console.WriteLine(text); } catch (IOException) { }
            }

            public void AddIssue(SetupLocale.Text message)
            {
                string cleaned = string.Join(" ", (message.Shown ?? "").Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries));
                if (cleaned.Length > 118) cleaned = cleaned.Substring(0, 115) + "...";
                if (cleaned.Length > 0 && !Issues.Contains(cleaned)) Issues.Add(cleaned);
            }

            // no download step runs in --cleanup mode, so intra-step progress has nothing to show.
            public void SubProgress(double fraction, string detail) { }
        }

        public static int RunCleanupMode(int startDelayMs = 0, bool keepUserSettings = true, bool removeObs = false)
        {
            try
            {
                Log("cleanup starting");
                if (startDelayMs > 0) Thread.Sleep(Math.Min(startDelayMs, 5000));
                var progress = new CleanupProgress();
                var issues = Cleanup.RunCleanup(progress, keepUserSettings, removeObs);
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

        // headless discord-screenshare install from the settings dock share preview audio row -- close obs, install the virtual audio cable, flip the prefs on. the helper already flipped replaykit_settings.json before spawning us.
        public static int RunInstallDiscordScreenshareMode(int startDelayMs = 0)
        {
            try
            {
                Log("discord screenshare install starting");
                if (startDelayMs > 0) Thread.Sleep(Math.Min(startDelayMs, 5000));
                // a detached restart would start obs again while the audio driver installs
                using var operation = ObsExit.TryTakeOperation(ObsExit.SetupGateWaitMs);
                if (operation == null) throw new IOException(SetupLocale.Message("setup.anotherReplaykitUpdateIsAlready"));
                var progress = new CleanupProgress { TotalSteps = 2 };
                Apply.RunApplyStep(progress, 1, SetupLocale.Pair("setup.closeObs"), SetupLocale.Pair("setup.stopsObsAndReplaykitHelpersSo"), () => Cleanup.StopObsAndHelpers(progress.LogLine));
                Apply.RunApplyStep(progress, 2, SetupLocale.Pair("setup.installObsStreamAudio"), SetupLocale.Pair("setup.installsTheReplaykitVirtualAudioDevice"), () => VbCable.EnsureVbcable(progress.LogLine));
                var prefs = Prefs.LoadPrefs();
                prefs.DiscordScreenshareEnabled = true;
                prefs.DiscordProjectorEnabled = true;
                prefs.Save();
                if (progress.Issues.Count > 0) Log($"discord screenshare install complete with {progress.Issues.Count} warning(s)");
                else Log("discord screenshare install complete");
                return 0;
            }
            catch (Exception exc)
            {
                Log("discord screenshare install failed: " + exc.Message);
                return 1;
            }
        }

        // headless discord-screenshare removal from the settings dock uninstall box -- same two steps (close obs, remove the virtual audio cable), reported thru CleanupProgress instead of a console.
        public static int RunUninstallDiscordScreenshareMode(int startDelayMs = 0)
        {
            try
            {
                Log("discord screenshare removal starting");
                if (startDelayMs > 0) Thread.Sleep(Math.Min(startDelayMs, 5000));
                // a detached restart would start obs again while the audio driver is removed
                using var operation = ObsExit.TryTakeOperation(ObsExit.SetupGateWaitMs);
                if (operation == null) throw new IOException(SetupLocale.Message("setup.anotherReplaykitUpdateIsAlready"));
                var progress = new CleanupProgress { TotalSteps = 2 };
                Apply.RunApplyStep(progress, 1, SetupLocale.Pair("setup.closeObs"), SetupLocale.Pair("setup.stopsObsAndReplaykitHelpersSo2"), () => Cleanup.StopObsAndHelpers(progress.LogLine));
                Apply.RunApplyStep(progress, 2, SetupLocale.Pair("setup.removeObsStreamAudio"), SetupLocale.Pair("setup.uninstallsTheReplaykitVirtualAudioDevice"), () => VbCable.UninstallVbcable(progress.LogLine));
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

        // return null for a normal interactive launch
        public static int? TryRunUpdateFromArgv(string[] argv)
        {
            if (argv.Contains(ConfigBackups.ClearFlag)) return ConfigBackups.ClearOld();
            // the windowed uninstall -- the twin of the setup window. --cleanup is the old headless path, kept as a fallback.
            if (argv.Contains("--uninstall"))
            {
                bool removeUserSettings = argv.Contains("--remove-user-settings");
                bool removeObs = argv.Contains("--remove-obs");
                // relaunch detached first: the "Close OBS" step kills the helper that started us and, with it, the job we were created inside -- both decided at CreateProcess time and unsheddable. same as --update.
                if (!argv.Contains(DetachedSpawn.DetachedFlag))
                {
                    int pid = DetachedSpawn.Relaunch(argv);
                    if (pid > 0) { Log("uninstall relaunched detached as pid " + pid + "; this launcher is done"); return 0; }
                    Log("warn: could not relaunch the uninstaller detached, running inline (an OBS teardown may kill it)");
                }
                // taken after the detached relaunch, so the launcher that hands over never holds it
                using var instance = SetupInstance.TryTake();
                if (instance == null)
                {
                    SetupInstance.ShowOpenWindow();
                    return 0;
                }
                if (!WebView2Runtime.Ensure(null))
                    return RunCleanupMode(0, !removeUserSettings, removeObs); // no webview2 -> fall back to headless
                using var win = new UninstallWindow(!removeUserSettings, removeObs);
                return win.RunModal();
            }
            if (argv.Contains("--cleanup"))
            {
                bool removeUserSettings = argv.Contains("--remove-user-settings");
                bool removeObs = argv.Contains("--remove-obs");
                int startDelay = IntArg(argv, "--start-delay-ms", 0);
                return RunCleanupMode(startDelay, !removeUserSettings, removeObs);
            }
            if (argv.Contains("--install-discord-screenshare"))
            {
                int startDelay = IntArg(argv, "--start-delay-ms", 0);
                return RunInstallDiscordScreenshareMode(startDelay);
            }
            if (argv.Contains("--uninstall-discord-screenshare"))
            {
                int startDelay = IntArg(argv, "--start-delay-ms", 0);
                return RunUninstallDiscordScreenshareMode(startDelay);
            }
            if (!argv.Contains("--update")) return null;
            string attemptId = StringArg(argv, "--attempt-id", null);
            if (attemptId != null && !TempLayout.ValidAttempt(attemptId)) return 1;

            // hand the whole job to a copy of ourselves that owns no console and belongs to no job object, then get out of the way. everything this process inherited from the helper dies with OBS moments from now; the detached copy inherits none of it, so killing OBS cannot reach it. see DetachedSpawn.
            if (!argv.Contains(DetachedSpawn.DetachedFlag))
            {
                int detachedPid = DetachedSpawn.Relaunch(argv);
                if (detachedPid > 0)
                {
                    Log("relaunched detached as pid " + detachedPid + "; this launcher is done");
                    return 0;
                }
                // could not detach -- carry on inline rather than skipping the update, and say so, so a failure right after OBS closes is recognisable instead of looking like a silent death.
                Log("warn: could not relaunch detached, running inline (an OBS teardown may kill this process)");
            }

            // the download folder this exe runs from is left to the helper that made it (Update.ClearStaleUpdateFolders); an older helper still passes --cleanup-dir, which is not read
            string relaunchObs = StringArg(argv, "--relaunch-obs", "");
            bool noRelaunch = argv.Contains("--no-relaunch-obs");
            int waitPid = IntArg(argv, "--wait-pid", 0);
            int startDelayMs = IntArg(argv, "--start-delay-ms", 0);
            string releaseUrl = StringArg(argv, "--release-url", ReleasePage);
            if (!Uri.TryCreate(releaseUrl, UriKind.Absolute, out Uri releaseUri) || releaseUri.Scheme != Uri.UriSchemeHttps)
                releaseUrl = ReleasePage;
            IInstallProgress reporter = null;
            if (attemptId != null && string.Equals(Path.GetDirectoryName(NetCompat.ProcessPath), TempLayout.UpdateDirectory(attemptId), StringComparison.OrdinalIgnoreCase))
            {
                string path = TempLayout.UpdateProgressPath(attemptId);
                reporter = new PageProgress(step => WriteProgress(path, step));
            }
            return RunUpdateMode(relaunchObs, waitPid, startDelayMs, !noRelaunch, releaseUrl, attemptId, reporter);
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

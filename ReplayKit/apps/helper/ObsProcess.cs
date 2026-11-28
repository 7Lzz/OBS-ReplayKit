using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using ReplayKitShared;

namespace ReplayKitHelper
{
    // closes obs for an exit or a restart, runs the detached restart that brings it back, and the --relaunch-obs mode the elevation module of lua starts
    internal static class ObsProcess
    {
        private static string CloseTicketName(Guid ticket) => @"Local\OBSReplayKit-close-ticket-" + ticket.ToString("N");

        private static string CloseTargetPath(Process process)
        {
            if (!Constants.IsObsProcessName(process.ProcessName)) return null;
            string path = process.MainModule?.FileName;
            return string.IsNullOrEmpty(path) || Constants.IsRoleHost(path) ? null : path;
        }

        internal sealed class CloseRequest : IDisposable
        {
            private readonly EventWaitHandle gate;
            private readonly EventWaitHandle ticketHandle;
            private readonly Guid ticket;
            private readonly IntPtr window;
            private readonly int pid;
            private readonly long started;

            internal static CloseRequest Begin(out string message)
            {
                message = Locales.Message("common.couldNotLocateObsExecutablePath");
                IntPtr window = Native.ObsMainWindow();
                int pid = Native.WindowOwnerPid(window);
                if (pid <= 0) return null;
                try
                {
                    using var process = Process.GetProcessById(pid);
                    if (CloseTargetPath(process) == null) return null;
                    long started = process.StartTime.ToFileTimeUtc();
                    var gate = new EventWaitHandle(false, EventResetMode.ManualReset, ObsExit.GateName(pid), out bool created);
                    if (!created)
                    {
                        gate.Dispose();
                        message = Locales.Message("helper.obsIsClosing");
                        return null;
                    }
                    var ticket = Guid.NewGuid();
                    try
                    {
                        var ticketHandle = new EventWaitHandle(true, EventResetMode.AutoReset, CloseTicketName(ticket));
                        return new CloseRequest(gate, ticketHandle, ticket, window, pid, started);
                    }
                    catch
                    {
                        gate.Dispose();
                        throw;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is Win32Exception || ex is IOException || ex is UnauthorizedAccessException)
                {
                    return null;
                }
            }

            private CloseRequest(EventWaitHandle gate, EventWaitHandle ticketHandle, Guid ticket, IntPtr window, int pid, long started)
            {
                this.gate = gate;
                this.ticketHandle = ticketHandle;
                this.ticket = ticket;
                this.window = window;
                this.pid = pid;
                this.started = started;
            }

            internal void Run(string reason, bool restart, bool notify = true)
            {
                try
                {
                    PrepareClose(reason);
                    bool closed = restart ? RestartDetached() : CloseOriginal();
                    if (closed)
                    {
                        Server.State.Shutdown = true;
                        return;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is Win32Exception || ex is IOException || ex is UnauthorizedAccessException)
                {
                    Log.Write(reason + ": could not close OBS: " + ex.Message);
                }
                ReportCloseFailure(reason, notify);
            }

            private bool CloseOriginal()
            {
                using var process = Process.GetProcessById(pid);
                return MatchesTarget(process, window, started) && CloseAndWait(process, window);
            }

            private bool RestartDetached()
            {
                string cmd = ProcessHost.CommandLine(new[] { RestartMode, "-ObsPid", pid.ToString(CultureInfo.InvariantCulture),
                    "-ObsWindow", window.ToInt64().ToString(CultureInfo.InvariantCulture), "-ObsStarted", started.ToString(CultureInfo.InvariantCulture),
                    "-CloseTicket", ticket.ToString("N") });
                return Native.RunDetached(cmd, Constants.HelperRoot, out uint exitCode) && exitCode == 0;
            }

            public void Dispose()
            {
                ticketHandle.Dispose();
                gate.Dispose();
            }
        }

        private static void PrepareClose(string reason)
        {
            // preserve the share card while obs is closing or being replaced
            ShareWindow.HoldForObsReturn(600, reason);
            Thread.Sleep(300);
            try
            {
                if (!PipeClient.SendAllowCloseAndWait(1000))
                    Log.Write(reason + ": native plugin did not acknowledge ALLOWCLOSE.");
            }
            catch (Exception ex) { Log.Write(reason + ": close acknowledgement failed: " + ex.Message); }
            ObsRequest.MarkObsClosing();
        }

        private static void ReportCloseFailure(string reason, bool notify)
        {
            ObsRequest.AllowRequests();
            string message = Locales.Message("helper.obsDidNotCloseSafelyReplaykit");
            Log.Write(reason + ": " + message);
            if (notify) Notifications.Add("warning", "obs-close-timeout", Locales.Message("helper.obsCouldNotCloseSafely"), message);
        }

        private static bool MatchesTarget(Process process, IntPtr window, long started) =>
            !process.SafeHandle.IsInvalid && process.StartTime.ToFileTimeUtc() == started && Native.WindowOwnerPid(window) == process.Id;

        private static bool CloseAndWait(Process process, IntPtr window) =>
            Native.CloseObsWindow(window, process.Id) && process.WaitForExit(ObsExit.GracefulWaitMs);

        internal const string RestartMode = "--restart-obs";

        // the detached child owns both close attempts and relaunches only after the original obs exits
        internal static int RunRestart(string pidText, string windowText, string startedText, string ticketText)
        {
            if (!int.TryParse(pidText, NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 0 ||
                !long.TryParse(windowText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long windowValue) || windowValue == 0 ||
                !long.TryParse(startedText, NumberStyles.None, CultureInfo.InvariantCulture, out long started) || started <= 0 ||
                !Guid.TryParseExact(ticketText, "N", out Guid ticket)) return 3;

            try
            {
                // keep the gate alive after the helper exits and reject a child left over from an earlier attempt
                using var gate = EventWaitHandle.OpenExisting(ObsExit.GateName(pid));
                using var ticketHandle = EventWaitHandle.OpenExisting(CloseTicketName(ticket));
                if (!ticketHandle.WaitOne(0)) return 3;
                using var operation = ObsExit.TryTakeOperation(0);
                if (operation == null)
                {
                    LogRestartStopped("ReplayKit is already changing the OBS installation.");
                    return 3;
                }
                using var process = Process.GetProcessById(pid);
                var window = new IntPtr(windowValue);
                string path = CloseTargetPath(process);
                if (path != null && MatchesTarget(process, window, started))
                {
                    if (!CloseAndWait(process, window))
                    {
                        Log.Write("OBS did not close normally. Forcing exit for the requested restart (PID " + pid + ").");
                        if (!ObsExit.ForceAndWait(process))
                        {
                            LogRestartStopped("OBS did not exit after forced termination.");
                            return 3;
                        }
                    }
                    if (Relaunch(path, DateTime.FromFileTimeUtc(started))) return 0;
                    LogRestartStopped("OBS could not be started from '" + path + "'.");
                    return 2;
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is Win32Exception || ex is WaitHandleCannotBeOpenedException || ex is UnauthorizedAccessException)
            {
                LogRestartStopped("OBS close failed: " + ex.Message);
                return 3;
            }
            LogRestartStopped("OBS did not exit safely.");
            return 3;
        }

        internal const string RelaunchMode = "--relaunch-obs";
        // give the detached relauncher time to see the requested graceful exit
        private const int RelaunchExitWaitMs = 40000;

        // the --relaunch-obs mode: waits for the obs that is closing, then puts the staged files in place and starts obs again. a relaunch never stops a process that still owns graphics or output resources, so an obs that does not exit leaves the restart undone
        internal static int RunRelauncher(string obsPath, string obsPidText)
        {
            if (!int.TryParse(obsPidText, NumberStyles.None, CultureInfo.InvariantCulture, out int obsPid) || obsPid <= 0 || !WaitForExit(obsPid, RelaunchExitWaitMs, out DateTime? obsStartedUtc))
            {
                LogRestartStopped("OBS did not exit safely.");
                return 3;
            }
            if (Relaunch(obsPath, obsStartedUtc)) return 0;
            LogRestartStopped("OBS could not be started from '" + obsPath + "'.");
            return 2;
        }

        // written whether or not logging is on, since nothing else records why a restart left obs closed
        private static void LogRestartStopped(string why) =>
            Log.AppendCapped(Path.Combine(Constants.LOG_DIR, "restart_obs.log"), "[" + DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + "] Restart stopped: " + why);

        // the start time of the obs that is closing is null when it was gone before it could be read
        private static bool WaitForExit(int pid, int timeoutMs, out DateTime? startedUtc)
        {
            startedUtc = null;
            Process process;
            try { process = Process.GetProcessById(pid); }
            catch (ArgumentException) { return true; }
            using (process)
            {
                try { startedUtc = process.StartTime.ToUniversalTime(); }
                catch (Exception ex) when (ex is InvalidOperationException || ex is Win32Exception || ex is NotSupportedException) { }
                try { return process.WaitForExit(timeoutMs); }
                catch (InvalidOperationException) { return true; }
                catch (Win32Exception) { return false; }
            }
        }

        // what has to happen between the exit of obs and its next start: the crash marker of the obs that was force-stopped goes, and the scene and theme edits staged for after its exit-save are put in place
        internal static bool Relaunch(string obsPath, DateTime? closedObsStartedUtc)
        {
            try { ClearCrashSentinel(); } catch (Exception ex) { Log.Write("ClearCrashSentinel: " + ex.Message); }
            try { ApplyPendingOverlayScene(closedObsStartedUtc); } catch (Exception ex) { Log.Write("ApplyPendingOverlayScene: " + ex.Message); }
            try { Themes.ApplyPendingTheme(); } catch (Exception ex) { Log.Write("Themes.ApplyPendingTheme: " + ex.Message); }
            if (string.IsNullOrEmpty(obsPath) || !File.Exists(obsPath))
            {
                Log.Write("Relaunch: OBS not found at '" + obsPath + "'");
                return false;
            }
            Log.Write("Relaunch: launching " + obsPath);
            try
            {
                // through the shell, which hands obs none of the handles of this short-lived process; obs keeps the rights this process has
                var psi = new ProcessStartInfo
                {
                    FileName = obsPath,
                    Arguments = ReplayKitShared.ObsLaunch.Arguments,
                    WorkingDirectory = Path.GetDirectoryName(obsPath),
                    UseShellExecute = true,
                };
                Process.Start(psi)?.Dispose();
                return true;
            }
            catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException)
            {
                Log.Write("Relaunch: launch failed: " + ex.Message);
                return false;
            }
        }

        // obs writes a run_<uuid> file under %appdata%\obs-studio\.sentinel on launch and deletes every lingering one during its own shutdown handler on a clean exit -- a killed/crashed obs never reaches that code, so the leftover file is what makes the next launch show the "crash detected" prompt
        internal static void ClearCrashSentinel()
        {
            string sentinelDir = Path.Combine(Environment.GetEnvironmentVariable("APPDATA") ?? "", "obs-studio", ".sentinel");
            if (!Directory.Exists(sentinelDir)) return;
            foreach (var file in Directory.GetFiles(sentinelDir, "run_*"))
            {
                try
                {
                    File.Delete(file);
                    Log.Write("Clear-ObsCrashSentinel: removed " + Path.GetFileName(file));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Log.Write("Clear-ObsCrashSentinel: could not remove " + Path.GetFileName(file) + ": " + ex.Message);
                }
            }
        }

        // move any scene staged by SetOverlaySceneFile onto the real scene collection, only once obs is gone since its own exit-save would write over it. a scene staged before the closing obs started is from a session that ended without this restart, whose exit-save kept everything changed since, so it is dropped instead of put back over that; with no start time known a staged scene is left for a later restart
        private static void ApplyPendingOverlayScene(DateTime? closedObsStartedUtc)
        {
            string scenesDir = Constants.OBS_SCENES_DIR;
            if (!Directory.Exists(scenesDir)) return;
            foreach (var staged in Directory.GetFiles(scenesDir, "*.json" + Constants.PENDING_SCENE_SUFFIX))
            {
                string target = staged.Substring(0, staged.Length - Constants.PENDING_SCENE_SUFFIX.Length);
                try
                {
                    if (closedObsStartedUtc == null)
                    {
                        Log.Write("ApplyPendingOverlayScene: left " + Path.GetFileName(staged) + ", the start of the closing OBS is not known");
                        continue;
                    }
                    if (File.GetLastWriteTimeUtc(staged) < closedObsStartedUtc.Value)
                    {
                        File.Delete(staged);
                        Log.Write("ApplyPendingOverlayScene: dropped " + Path.GetFileName(staged) + ", staged before the closing OBS started");
                        continue;
                    }
                    if (File.Exists(target)) File.Replace(staged, target, null);
                    else File.Move(staged, target);
                    Log.Write("ApplyPendingOverlayScene: applied " + Path.GetFileName(target));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Log.Write("ApplyPendingOverlayScene: could not apply " + Path.GetFileName(target) + ": " + ex.Message);
                }
            }
        }
    }
}

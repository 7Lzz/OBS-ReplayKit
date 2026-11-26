using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;
using ReplayKitShared;

namespace ReplayKitHelper
{
    // finds the running obs, closes it for a restart or an update and arms the relauncher that brings it back
    internal static class ObsProcess
    {
        private static readonly string[] ObsProcessNames = { "obs64", "obs32", "obs" };

        internal static Process[] GetProcessesByNames(params string[] names)
        {
            var list = new List<Process>();
            int currentPid = Process.GetCurrentProcess().Id;
            foreach (var name in names)
            {
                try
                {
                    foreach (var process in Process.GetProcessesByName(name))
                    {
                        if (process.Id == currentPid) process.Dispose();
                        else list.Add(process);
                    }
                }
                catch (InvalidOperationException) { }
            }
            return list.ToArray();
        }

        internal static string GetRunningObsPath()
        {
            var procs = GetProcessesByNames(ObsProcessNames);
            try
            {
                foreach (var p in procs)
                {
                    try { if (!string.IsNullOrEmpty(p.MainModule?.FileName) && !Constants.IsRoleHost(p.MainModule.FileName)) return p.MainModule.FileName; }
                    catch (Win32Exception) { } catch (InvalidOperationException) { }
                }
                foreach (var p in procs)
                {
                    try
                    {
                        using (var searcher = new ManagementObjectSearcher("SELECT ExecutablePath FROM Win32_Process WHERE ProcessId=" + p.Id))
                        {
                            foreach (ManagementObject mo in searcher.Get())
                            {
                                string exePath = mo["ExecutablePath"] as string;
                                if (!string.IsNullOrEmpty(exePath)) return exePath;
                            }
                        }
                    }
                    catch (ManagementException) { }
                }
                return null;
            }
            finally { foreach (var p in procs) p.Dispose(); }
        }

        private static string CloseTicketName(Guid ticket) => @"Local\OBSReplayKit-close-ticket-" + ticket.ToString("N");

        private static string CloseTargetPath(Process process)
        {
            if (!ObsProcessNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase)) return null;
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

            internal void Run(string reason, bool restart)
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
                ReportCloseFailure(reason);
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
            ObsWebSocket.DisconnectForObsClose();
        }

        private static void ReportCloseFailure(string reason)
        {
            ObsWebSocket.AllowConnect();
            string message = Locales.Message("helper.obsDidNotCloseSafelyReplaykit");
            Log.Write(reason + ": " + message);
            Notifications.Add("warning", "obs-close-timeout", Locales.Message("helper.obsCouldNotCloseSafely"), message);
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
                    if (Relaunch(path)) return 0;
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
            if (!int.TryParse(obsPidText, NumberStyles.None, CultureInfo.InvariantCulture, out int obsPid) || obsPid <= 0 || !WaitForExit(obsPid, RelaunchExitWaitMs))
            {
                LogRestartStopped("OBS did not exit safely.");
                return 3;
            }
            if (Relaunch(obsPath)) return 0;
            LogRestartStopped("OBS could not be started from '" + obsPath + "'.");
            return 2;
        }

        // written whether or not logging is on, since nothing else records why a restart left obs closed
        private static void LogRestartStopped(string why) =>
            Log.AppendCapped(Path.Combine(Constants.LOG_DIR, "restart_obs.log"), "[" + DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + "] Restart stopped: " + why);

        private static bool WaitForExit(int pid, int timeoutMs)
        {
            Process process;
            try { process = Process.GetProcessById(pid); }
            catch (ArgumentException) { return true; }
            using (process)
            {
                try { return process.WaitForExit(timeoutMs); }
                catch (InvalidOperationException) { return true; }
                catch (Win32Exception) { return false; }
            }
        }

        // what has to happen between the exit of obs and its next start: the crash marker of the obs that was force-stopped goes, and the scene and theme edits staged for after its exit-save are put in place
        internal static bool Relaunch(string obsPath)
        {
            try { ClearCrashSentinel(); } catch (Exception ex) { Log.Write("ClearCrashSentinel: " + ex.Message); }
            try { ApplyPendingOverlayScene(); } catch (Exception ex) { Log.Write("ApplyPendingOverlayScene: " + ex.Message); }
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

        // move any <name>.json.replaykit-pending staged by SetOverlaySceneFile onto the real scene collection. only safe once obs is gone (its own exit-save would clobber it otherwise)
        private static void ApplyPendingOverlayScene()
        {
            string scenesDir = Path.Combine(Environment.GetEnvironmentVariable("APPDATA") ?? "", "obs-studio", "basic", "scenes");
            if (!Directory.Exists(scenesDir)) return;
            foreach (var staged in Directory.GetFiles(scenesDir, "*.json.replaykit-pending"))
            {
                string target = staged.Substring(0, staged.Length - ".replaykit-pending".Length);
                try
                {
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

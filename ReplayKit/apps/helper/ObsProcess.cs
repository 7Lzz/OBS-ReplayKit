using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;

namespace ReplayKitHelper
{
    // finds the running obs, closes it for a restart or an update and arms the relauncher that brings it back
    internal static class ObsProcess
    {
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
            var procs = GetProcessesByNames("obs64", "obs32", "obs");
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

        // a plain exit leaves obs and the helper running when a normal close cannot finish; a restart someone pressed and an update have to happen (the installer cannot replace the files of a running obs either), so for those obs is made to stop once the normal close has had its time, and the armed relauncher brings it back
        internal static void StopObsForRestart(string reason, bool mustStop)
        {
            Thread.Sleep(300);
            try
            {
                if (!PipeClient.SendAllowCloseAndWait(1000))
                    Log.Write(reason + ": native plugin did not acknowledge ALLOWCLOSE.");
            }
            catch (Exception ex) { Log.Write(reason + ": close acknowledgement failed: " + ex.Message); }
            var procs = GetProcessesByNames("obs64", "obs32", "obs");
            bool stopped = true;
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            foreach (var p in procs)
            {
                try
                {
                    if (Constants.IsRoleHost(p)) continue;
                    Native.CloseObsMainWindow((uint)p.Id);
                    int remaining = (int)Math.Max(0, (deadline - DateTime.UtcNow).TotalMilliseconds);
                    if (!p.HasExited && !p.WaitForExit(remaining)) stopped = false;
                }
                catch (InvalidOperationException) { }
                catch (Win32Exception ex)
                {
                    stopped = false;
                    Log.Write(reason + ": could not confirm OBS exit: " + ex.Message);
                }
                finally { p.Dispose(); }
            }
            if (!stopped && mustStop)
            {
                Log.Write(reason + ": OBS did not close within 15 s, stopping it");
                foreach (var p in GetProcessesByNames("obs64", "obs32", "obs"))
                {
                    try { if (!Constants.IsRoleHost(p) && !p.HasExited) { p.Kill(); p.WaitForExit(5000); } }
                    catch (InvalidOperationException) { }
                    catch (Win32Exception ex) { Log.Write(reason + ": could not stop OBS: " + ex.Message); }
                    finally { p.Dispose(); }
                }
                Server.State.Shutdown = true;
                return;
            }
            if (!stopped)
            {
                Server.State.RestartAfterCleanObsPath = null;
                string message = Locales.T("OBS did not close safely. ReplayKit left it running instead of forcing it to stop. Close OBS normally before trying again.");
                Log.Write(reason + ": " + message);
                Notifications.Add("warning", "obs-close-timeout", Locales.T("OBS could not close safely"), message);
                return;
            }
            Server.State.Shutdown = true;
        }

        // starts this program again in its --relaunch-obs mode, outside our kill-on-close job so it lives past helper exit. an in-process relaunch is unreliable -- by the time obs is dead and any cleanup completes, this process has often already been torn down (parent-process watchdog exit, abandoned-mutex on takeover, etc.) and never reaches the launch line. doing the relaunch from a sibling process owned by the os instead of by us is the only way to make it survive every shutdown path.
        internal static bool StartDetachedObsRelauncher(string obsPath, int obsPid)
        {
            string cmd = ProcessHost.CommandLine(new[] { RelaunchMode, "-ObsPath", obsPath, "-ObsPid", obsPid.ToString(CultureInfo.InvariantCulture) });
            try
            {
                int relauncherPid = Native.SpawnDetached(cmd, Constants.HelperRoot);
                if (relauncherPid <= 0)
                {
                    Log.Write("Start-DetachedObsRelauncher: SpawnDetached returned 0");
                    return false;
                }
                Log.Write("Start-DetachedObsRelauncher: PID " + relauncherPid + " will relaunch '" + obsPath + "' after this OBS exits.");
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Start-DetachedObsRelauncher threw: " + ex.Message);
                return false;
            }
        }

        // arms the detached relauncher (or the in-process path) before requesting a normal obs close. shared by /settings' restart-required branch, /restart-obs, and /restart-obs-clean.
        internal static void ArmRelaunchAndCloseObs(string obsPath, string routeName)
        {
            int obsTargetPid = 0;
            if (ParentWatchdog.ParentPid > 0)
            {
                obsTargetPid = ParentWatchdog.ParentPid;
            }
            else
            {
                var running = GetProcessesByNames("obs64", "obs32", "obs");
                var found = running.Where(p => !Constants.IsRoleHost(p)).ToArray();
                if (found.Length > 0) obsTargetPid = found[0].Id;
                foreach (var p in running) p.Dispose();
            }
            bool detached = StartDetachedObsRelauncher(obsPath, obsTargetPid);
            if (!detached)
            {
                Server.State.RestartAfterCleanObsPath = obsPath;
                Log.Write(routeName + ": detached relauncher unavailable; queued legacy in-process restart for '" + obsPath + "'.");
            }
            else
            {
                Log.Write(routeName + ": detached relauncher armed for '" + obsPath + "' (target PID " + obsTargetPid + ").");
            }
        }

        internal const string RelaunchMode = "--relaunch-obs";
        // the close of obs is given its own 15 s and then the stop the helper makes after that for a restart, so the relauncher waits a little longer than both
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

        // what has to happen between the exit of obs and its next start: the crash marker of the obs that was force-stopped goes, and the scene, theme and language edits staged for after its exit-save are put in place
        internal static bool Relaunch(string obsPath)
        {
            try { ClearCrashSentinel(); } catch (Exception ex) { Log.Write("ClearCrashSentinel: " + ex.Message); }
            try { ApplyPendingOverlayScene(); } catch (Exception ex) { Log.Write("ApplyPendingOverlayScene: " + ex.Message); }
            try { Themes.ApplyPendingTheme(); } catch (Exception ex) { Log.Write("Themes.ApplyPendingTheme: " + ex.Message); }
            Locales.ApplyPending();
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
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(staged, target);
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

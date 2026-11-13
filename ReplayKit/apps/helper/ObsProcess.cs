using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
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
                const string message = "OBS did not close safely. ReplayKit left it running instead of forcing it to stop. Close OBS normally before trying again.";
                Log.Write(reason + ": " + message);
                Notifications.Add("warning", "obs-close-timeout", "OBS could not close safely", message);
                return;
            }
            Server.State.Shutdown = true;
        }

        // spawn restart_obs.ps1 outside our kill-on-close job so it lives past helper exit. an in-process relaunch is unreliable -- by the time obs is dead and any cleanup completes, this process has often already been torn down (parent-process watchdog exit, abandoned-mutex on takeover, etc.) and never reaches the launch line. doing the relaunch from a sibling process owned by the os instead of by us is the only way to make it survive every shutdown path.
        internal static bool StartDetachedObsRelauncher(string obsPath, int obsPid)
        {
            string scriptDir = AppConfig.GetScriptDir();
            if (string.IsNullOrWhiteSpace(scriptDir)) return false;
            string scriptPath = Path.Combine(scriptDir, "restart_obs.ps1");
            if (!File.Exists(scriptPath))
            {
                Log.Write("Start-DetachedObsRelauncher: missing " + scriptPath);
                return false;
            }
            string psExe = Path.Combine(Environment.GetEnvironmentVariable("WINDIR") ?? "", "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(psExe)) psExe = "powershell.exe";
            string cmd = ProcessArgs.Quote(psExe) +
                " -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File " + ProcessArgs.Quote(scriptPath) +
                " -ObsPath " + ProcessArgs.Quote(obsPath) +
                " -ObsPid " + obsPid;
            try
            {
                int relauncherPid = Native.SpawnDetached(cmd, scriptDir);
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
    }
}

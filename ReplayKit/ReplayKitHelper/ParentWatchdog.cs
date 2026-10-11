using System;
using System.Diagnostics;
using System.Management;

namespace ReplayKitHelper
{
    // tracks the obs process that spawned this helper (via lua) so the helper exits once that obs is gone instead of holding port 8767; pinned by pid and start time so pid reuse cannot fool it
    internal static class ParentWatchdog
    {
        public static int ParentPid { get; private set; }
        public static string ParentName { get; private set; } = "(unknown)";
        public static DateTime? ParentStartTime { get; private set; }
        public static bool ParentDied { get; private set; }

        private static IntPtr _parentHandle = IntPtr.Zero;
        private static DateTime _lastCheck = DateTime.MinValue;
        private static bool _resolved;

        // resolves parent pid/name/start time through wmi, since net48 has no api for the parent process; only the first call does anything
        public static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                int myPid = Process.GetCurrentProcess().Id;
                using (var searcher = new ManagementObjectSearcher("SELECT ParentProcessId FROM Win32_Process WHERE ProcessId=" + myPid))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject mo in results)
                    {
                        ParentPid = (int)(uint)mo["ParentProcessId"];
                        break;
                    }
                }
            }
            catch (ManagementException) { ParentPid = 0; }
            catch (UnauthorizedAccessException) { ParentPid = 0; }

            if (ParentPid <= 0) return;

            // opening the process fails when the parent runs at a higher integrity level (admin obs, non-admin helper), so wmi is the fallback for the name and start time
            try
            {
                using (var proc = Process.GetProcessById(ParentPid))
                {
                    ParentName = proc.ProcessName;
                    try { ParentStartTime = proc.StartTime; } catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) { }
                }
            }
            catch (ArgumentException)
            {
                ResolveParentViaWmi();
            }
            catch (InvalidOperationException)
            {
                ResolveParentViaWmi();
            }

            // lua can be hosted by a short-lived obs child process, so the running obs is watched instead and the helper does not exit as soon as that child does
            if (!IsMainObsProcess(ParentName)) ResolveRunningObs();
        }

        private static bool IsMainObsProcess(string name) =>
            string.Equals(name, "obs64", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "obs32", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "obs", StringComparison.OrdinalIgnoreCase);

        private static void ResolveRunningObs()
        {
            foreach (string name in new[] { "obs64", "obs32", "obs" })
            {
                var processes = Process.GetProcessesByName(name);
                try
                {
                    if (processes.Length == 0) continue;
                    using (var process = processes[0])
                    {
                        ParentPid = process.Id;
                        try { ParentStartTime = process.StartTime; } catch (System.ComponentModel.Win32Exception) { }
                        ParentName = process.ProcessName;
                        return;
                    }
                }
                finally
                {
                    for (int i = 1; i < processes.Length; i++) processes[i].Dispose();
                }
            }
        }

        private static void ResolveParentViaWmi()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Name, CreationDate FROM Win32_Process WHERE ProcessId=" + ParentPid))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject mo in results)
                    {
                        var name = mo["Name"] as string;
                        if (!string.IsNullOrEmpty(name))
                        {
                            // Win32_Process.Name -> "obs64.exe"; strip the extension to match Process.ProcessName semantics.
                            ParentName = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name;
                        }
                        var creationDate = mo["CreationDate"] as string;
                        if (!string.IsNullOrEmpty(creationDate))
                        {
                            try { ParentStartTime = ManagementDateTimeConverter.ToDateTime(creationDate); } catch (ArgumentException) { }
                        }
                        break;
                    }
                }
            }
            catch (ManagementException) { }
        }

        // opens a handle on the parent so CheckAlive can poll GetExitCodeProcess; PROCESS_QUERY_LIMITED_INFORMATION works across integrity levels. false when there is no known parent or it cannot be opened, which the caller only logs
        public static bool OpenHandle()
        {
            if (ParentPid <= 0) return true;
            _parentHandle = Native.OpenParentForSync((uint)ParentPid);
            return _parentHandle != IntPtr.Zero;
        }

        // kernel signal first, then a throttled lookup pinned by start time; a parent that was never known is never enforced
        public static bool CheckAlive()
        {
            if (_parentHandle != IntPtr.Zero && Native.ParentExited(_parentHandle))
            {
                ParentDied = true;
                return false;
            }

            var now = DateTime.UtcNow;
            if ((now - _lastCheck).TotalSeconds < 5) return true;
            _lastCheck = now;
            if (ParentPid <= 0) return true;

            Process alive = null;
            try { alive = Process.GetProcessById(ParentPid); }
            catch (ArgumentException) { alive = null; }

            if (alive != null && ParentStartTime.HasValue)
            {
                try
                {
                    if (alive.StartTime != ParentStartTime.Value) alive = null; // pid recycled by a diffrent process
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
                {
                    // cross-integrity / protected process: cant read start time, assume genuine match rather than false-positive exit.
                }
            }
            alive?.Dispose();
            if (alive != null) return true;

            ParentDied = true;
            return false;
        }

        // cheap immediate-return check for the accept loops idle branch -- catches "end task" within one poll interval instead of up to 5s.
        public static bool ExitedNow() => _parentHandle != IntPtr.Zero && Native.ParentExited(_parentHandle);

        public static int GetParentPid()
        {
            Resolve();
            return ParentPid;
        }
    }
}

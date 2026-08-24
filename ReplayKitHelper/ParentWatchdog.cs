using System;
using System.Diagnostics;
using System.Management;

namespace ReplayKitHelper
{
    // tracks the process that spawned this helper (obs64.exe, via lua spawn_hidden) so the helper can exit voluntarily once its specific parent is gone, instead of orphaning and jamming port 8767 forever. pinned by both pid and start time so pid reuse cant fool it. ported from obs_replaykit helper modules/90_runtime.ps1's parent-identity block + Check-ParentAlive.
    internal static class ParentWatchdog
    {
        public static int ParentPid { get; private set; }
        public static string ParentName { get; private set; } = "(unknown)";
        public static DateTime? ParentStartTime { get; private set; }
        public static bool ParentDied { get; private set; }

        private static IntPtr _parentHandle = IntPtr.Zero;
        private static DateTime _lastCheck = DateTime.MinValue;
        private static bool _resolved;

        // resolves parent pid/name/starttime via wmi (not Process, which has no "get my own parent" api on net48). safe to call more than once -- only the first call matters, since a processs parent never changes after launch.
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

            // Get-Process fails (access denied) when the parent runs at a higher integrity level than us -- e.g. admin obs64 launching a non-admin helper. wmi can often still see the name/start time in that case, so it is the fallback, not the primary source.
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

        // opens a real os handle on the parent so CheckAlive can poll GetExitCodeProcess instead of relying purely on Get-Process. PROCESS_QUERY_LIMITED_INFORMATION works cross-integrity-level. returns false if we have no known parent, or the parent is already gone / at a integrity level we cant even query -- either way the caller should exit rather than risk a non-admin orphan jamming the port while the correctly-elevated helper is waiting to bind it.
        public static bool OpenHandle()
        {
            if (ParentPid <= 0) return true;
            _parentHandle = Native.OpenParentForSync((uint)ParentPid);
            return _parentHandle != IntPtr.Zero;
        }

        // kernel signal first (instant, no polling interval), then a throttled Get-Process fallback pinned by start time. mirrors Check-ParentAlive exactly, including the 5s throttle and the "never knew parent, dont enforce" escape hatch.
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

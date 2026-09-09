using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // shared job-tracking primitives for uploads, compress-then-upload, and compress-overwrite: the job dictionary, concurrency-limiting start decision, cancellation, and stale-temp-file sweeping. ported from obs_replaykit helper modules/50_upload_state.ps1 -- the status-file plumbing from that file (Get-UploadStatusPath, Update-UploadStateFromStatusFile) has no equivalent here, since that existed only to bridge state back from a separate powershell.exe/runspace worker that could not see $script:State directly; the c# workers run as Tasks in this same process and call SetUploadState directly as they progress. Start-UploadResultWatcher/Start-CompressOverwriteResultWatcher (the completion handlers, "watcher" runspaces in the original) live in Upload.cs/CompressOverwrite.cs instead, next to the Task they continue from.
    internal static class UploadState
    {
        public static string NewRequestId() => Guid.NewGuid().ToString("N");

        private static UploadJobRecord NewUploadJobRecord(string requestId) => new UploadJobRecord { RequestId = requestId };

        public static JObject CopyUploadJobForJson(UploadJobRecord job)
        {
            if (job == null) return new JObject();
            return new JObject
            {
                ["state"] = job.State ?? "",
                ["active"] = job.Active,
                ["clipName"] = job.ClipName ?? "",
                ["startedAt"] = job.StartedAt,
                ["updatedAt"] = job.UpdatedAt,
                ["url"] = job.Url ?? "",
                ["error"] = job.Error ?? "",
                ["message"] = job.Message ?? "",
                ["phase"] = job.Phase ?? "",
                ["percent"] = job.Percent,
                ["requestId"] = job.RequestId ?? "",
                ["kind"] = job.Kind ?? "",
            };
        }

        // caller must already hold UploadLock.
        private static UploadJobRecord SelectCurrentUploadJobLocked()
        {
            UploadJobRecord chosen = null;
            foreach (var job in Server.State.Jobs.Values)
            {
                if (!job.Active) continue;
                if (chosen == null || job.UpdatedAt > chosen.UpdatedAt) chosen = job;
            }
            return chosen ?? Server.State.Upload;
        }

        // caller must already hold UploadLock.
        private static void RemoveOldUploadJobsLocked(long nowMs)
        {
            long cutoff = nowMs - (10 * 60 * 1000);
            var toRemove = new List<string>();
            foreach (var kv in Server.State.Jobs)
            {
                if (kv.Value.Active) continue;
                if (kv.Value.UpdatedAt > 0 && kv.Value.UpdatedAt < cutoff) toRemove.Add(kv.Key);
            }
            foreach (var key in toRemove) Server.State.Jobs.Remove(key);
        }

        // cached cpu percent sample. wmis PercentProcessorTime is an instantaneous reading -- cached for a few seconds so a burst of "can we start one more" checks from the bulk compress queue doesnt fire wmi queries back to back. returns -1 if sampling fails so callers fall back to the steady-state limit only (never the burst headroom). only ever called from GetUploadJobStartDecision, which already holds UploadLock for its whole body, so no dedicated lock here.
        private static double GetRecentCpuPercent()
        {
            var now = DateTime.UtcNow;
            if (Server.State.CpuSamplePercent >= 0 && (now - Server.State.CpuSampleAt).TotalSeconds < 4)
                return Server.State.CpuSamplePercent;
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name='_Total'"))
                {
                    foreach (ManagementObject row in searcher.Get())
                    {
                        if (row["PercentProcessorTime"] != null)
                        {
                            Server.State.CpuSamplePercent = Convert.ToInt32(row["PercentProcessorTime"]);
                            Server.State.CpuSampleAt = now;
                            return Server.State.CpuSamplePercent;
                        }
                    }
                }
            }
            catch (ManagementException)
            {
                // Win32_PerfFormattedData_* can flake on freshly-booted systems before the perflib counters are warm. -1 keeps the caller at the steady-state cap.
            }
            return -1;
        }

        public sealed class JobStartDecision
        {
            public bool Ok;
            public bool Busy;
            public bool Burst;
            public double Cpu;
            public string Message;
        }

        public static JobStartDecision GetUploadJobStartDecision(string clipName)
        {
            lock (Server.State.UploadLock)
            {
                int activeCount = 0;
                foreach (var job in Server.State.Jobs.Values)
                {
                    if (!job.Active) continue;
                    if (string.Equals(job.ClipName, clipName, StringComparison.OrdinalIgnoreCase))
                        return new JobStartDecision { Ok = false, Busy = true, Message = "That clip already has an operation running" };
                    activeCount++;
                }
                // below the steady-state cap -- always allowed.
                if (activeCount < Constants.MAX_CONCURRENT_VIDEO_JOBS) return new JobStartDecision { Ok = true };
                // between steady-state and burst -- only allowed if the host isnt already pegged. lets "compress all" auto-scale on underutilised machines without thrashing busy ones.
                if (activeCount < Constants.MAX_BURST_CONCURRENT_VIDEO_JOBS)
                {
                    double cpu = GetRecentCpuPercent();
                    if (cpu >= 0 && cpu < Constants.CPU_BURST_THRESHOLD_PCT) return new JobStartDecision { Ok = true, Burst = true, Cpu = cpu };
                }
                return new JobStartDecision { Ok = false, Busy = true, Message = "Already running " + activeCount + " video operations" };
            }
        }

        public static bool TestClipHasActiveUploadJob(string clipName)
        {
            if (string.IsNullOrWhiteSpace(clipName)) return false;
            lock (Server.State.UploadLock)
            {
                foreach (var job in Server.State.Jobs.Values)
                {
                    if (job.Active && string.Equals(job.ClipName, clipName, StringComparison.OrdinalIgnoreCase)) return true;
                }
                return false;
            }
        }

        // merges only the parameters actually passed (matches the ps originals hashtable-merge semantics: a key absent from @{...} leaves the field untouched). requestId null/blank falls back to the current Upload jobs id, same as the original.
        public static void SetUploadState(
            string requestId = null, string state = null, bool? active = null, string clipName = null,
            long? startedAt = null, string url = null, string error = null, string message = null,
            string phase = null, int? percent = null, string kind = null, bool? cancelRequested = null,
            CancellationTokenSource cts = null, Process encoderProcess = null, string tempPath = null)
        {
            void ApplyFields(UploadJobRecord target)
            {
                if (state != null) target.State = state;
                if (active.HasValue) target.Active = active.Value;
                if (clipName != null) target.ClipName = clipName;
                if (startedAt.HasValue) target.StartedAt = startedAt.Value;
                if (url != null) target.Url = url;
                if (error != null) target.Error = error;
                if (message != null) target.Message = message;
                if (phase != null) target.Phase = phase;
                if (percent.HasValue) target.Percent = percent.Value;
                if (kind != null) target.Kind = kind;
                if (cancelRequested.HasValue) target.CancelRequested = cancelRequested.Value;
                if (cts != null) target.Cts = cts;
                if (encoderProcess != null) target.EncoderProcess = encoderProcess;
                if (tempPath != null) target.TempPath = tempPath;
            }

            lock (Server.State.UploadLock)
            {
                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string effectiveRequestId = !string.IsNullOrWhiteSpace(requestId) ? requestId : Server.State.Upload.RequestId;

                UploadJobRecord job = null;
                if (!string.IsNullOrWhiteSpace(effectiveRequestId))
                {
                    if (!Server.State.Jobs.TryGetValue(effectiveRequestId, out job)) job = NewUploadJobRecord(effectiveRequestId);
                    ApplyFields(job);
                    job.RequestId = effectiveRequestId;
                    if (job.StartedAt == 0) job.StartedAt = nowMs;
                    job.UpdatedAt = nowMs;
                    Server.State.Jobs[effectiveRequestId] = job;
                }

                var u = job ?? Server.State.Upload;
                if (job == null)
                {
                    ApplyFields(u);
                    u.UpdatedAt = nowMs;
                }

                RemoveOldUploadJobsLocked(nowMs);
                Server.State.Upload = SelectCurrentUploadJobLocked();
                if (Server.State.Upload == null || !Server.State.Upload.Active) Server.State.Upload = u;
            }
        }

        public static JObject GetUploadStatusSnapshot()
        {
            lock (Server.State.UploadLock)
            {
                var jobsJson = new List<JObject>();
                int activeCount = 0;
                foreach (var job in Server.State.Jobs.Values)
                {
                    if (job.Active) activeCount++;
                    jobsJson.Add(CopyUploadJobForJson(job));
                }
                var current = CopyUploadJobForJson(Server.State.Upload);
                current["jobs"] = new JArray(jobsJson.OrderByDescending(j => j["updatedAt"].Value<long>()));
                current["activeJobs"] = activeCount;
                current["maxConcurrentJobs"] = Constants.MAX_CONCURRENT_VIDEO_JOBS;
                return current;
            }
        }

        public static void StopProcessTree(int processId)
        {
            if (processId <= 0) return;
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT ProcessId FROM Win32_Process WHERE ParentProcessId=" + processId))
                {
                    foreach (ManagementObject child in searcher.Get())
                    {
                        StopProcessTree((int)(uint)child["ProcessId"]);
                    }
                }
            }
            catch (ManagementException) { }
            try
            {
                using (var proc = Process.GetProcessById(processId)) { proc.Kill(); }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) { }
        }

        public sealed class CancelResult
        {
            public bool Ok;
            public string Message;
        }

        public static CancelResult CancelActiveUpload(string clipName = "", string requestId = "")
        {
            UploadJobRecord u = null;
            lock (Server.State.UploadLock)
            {
                if (!string.IsNullOrWhiteSpace(requestId) && Server.State.Jobs.TryGetValue(requestId, out var byId))
                {
                    u = byId;
                }
                else if (!string.IsNullOrWhiteSpace(clipName))
                {
                    foreach (var job in Server.State.Jobs.Values)
                    {
                        if (job.Active && string.Equals(job.ClipName, clipName, StringComparison.OrdinalIgnoreCase)) { u = job; break; }
                    }
                }
                else if (Server.State.Upload.Active)
                {
                    u = Server.State.Upload;
                }
            }

            if (u == null || !u.Active) return new CancelResult { Ok = false, Message = "No upload is running" };

            string activeRequestId = u.RequestId;
            var cts = u.Cts;
            var encoderProcess = u.EncoderProcess;
            string tempPath = u.TempPath;

            // mark cancelled before the kill so the tasks continuation (blocked awaiting the worker) sees the flag as soon as it wakes up and skips its generic "failed" overwrite.
            SetUploadState(requestId: activeRequestId, state: "error", active: false, error: "Cancelled", phase: "cancelled", percent: 0, cancelRequested: true);

            try { cts?.Cancel(); } catch (ObjectDisposedException) { }
            if (encoderProcess != null)
            {
                try { StopProcessTree(encoderProcess.Id); } catch (InvalidOperationException) { }
            }
            if (!string.IsNullOrEmpty(tempPath))
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
            return new CancelResult { Ok = true, Message = "Cancelled" };
        }

        private static IEnumerable<string> SafeEnumerateFiles(string dir, string pattern = "*")
        {
            try { return Directory.EnumerateFiles(dir, pattern).ToList(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return Enumerable.Empty<string>(); }
        }

        public static void ClearStaleCompressedTempFiles()
        {
            string root = Path.GetFullPath(Constants.COMPRESS_TMP_DIR);
            if (root.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) && Directory.Exists(root))
            {
                foreach (var file in SafeEnumerateFiles(root))
                {
                    try { File.Delete(file); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                }
            }
            // sweeps the current scratch dir plus bare %temp% root -- the latter only matters for leftovers from before everything moved under %temp%\ReplayKit (or from a pre-c# install that still wrote status files), and stays harmless once those are gone.
            foreach (var sweepRoot in new[] { Constants.SCRATCH_DIR, Path.GetTempPath() })
            {
                if (!Directory.Exists(sweepRoot)) continue;
                foreach (var file in SafeEnumerateFiles(sweepRoot))
                {
                    string name = Path.GetFileName(file);
                    if (name.StartsWith("streamable_upload_status_", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("replaykit_compress_", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("replaykit_trim_", StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(file); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                    }
                }
            }
            // every atomic-write .tmp sidecar (clips_db.json, clips_index.json, replaykit_settings.json, obss own ini/json, ...) lands in scratch, not next to its real file -- catch anything a crash left behind mid-write with one broad sweep, scoped to just this ReplayKit-owned dir.
            if (Directory.Exists(Constants.SCRATCH_DIR))
            {
                foreach (var file in SafeEnumerateFiles(Constants.SCRATCH_DIR, "*.tmp"))
                {
                    try { File.Delete(file); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                }
            }
            // sweep the clip folder for crashed-worker leftovers (sidecars from cross-volume finalize + older naming patterns).
            try
            {
                string clipDir = AppConfig.GetClipDir();
                if (!string.IsNullOrEmpty(clipDir) && Directory.Exists(clipDir))
                {
                    foreach (var file in SafeEnumerateFiles(clipDir))
                    {
                        string name = Path.GetFileName(file);
                        if (name.StartsWith("_replaykit_", StringComparison.OrdinalIgnoreCase) ||
                            name.StartsWith("_compress_tmp_", StringComparison.OrdinalIgnoreCase) ||
                            name.StartsWith("_trim_tmp_", StringComparison.OrdinalIgnoreCase))
                        {
                            try { File.Delete(file); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }
    }
}

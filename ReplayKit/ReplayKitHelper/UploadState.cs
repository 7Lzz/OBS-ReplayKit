using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // the job status records behind /status for uploads, compress-then-upload and compress-overwrite, the cancel entry point, and the sweep of temp files a stopped job left behind. the workers report progress here directly; their completion handlers live next to them in Upload.cs and CompressOverwrite.cs
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

        // merges only the parameters actually passed; a field left out keeps its value. a null or blank requestId falls back to the current job
        public static void SetUploadState(
            string requestId = null, string state = null, bool? active = null, string clipName = null,
            long? startedAt = null, string url = null, string error = null, string message = null,
            string phase = null, int? percent = null, string kind = null, bool? cancelRequested = null)
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
                if (!string.IsNullOrWhiteSpace(requestId))
                {
                    Server.State.Jobs.TryGetValue(requestId, out u);
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

            if (u == null || !JobCoordinator.Cancel(u.RequestId))
                return new CancelResult { Ok = false, Message = "Operation has already finished or committed" };
            return new CancelResult { Ok = true, Message = "Cancellation requested" };
        }

        private static IEnumerable<string> SafeEnumerateFiles(string dir, string pattern = "*")
        {
            try { return Directory.EnumerateFiles(dir, pattern).ToList(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return Enumerable.Empty<string>(); }
        }

        // the files a finished or crashed upload, trim or compression can leave behind. the startup sweep deletes them and the storage card counts them; the bare %temp% root only matters for leftovers from before everything moved under %temp%\ReplayKit, so only the sweep looks there
        public static List<string> EnumerateStaleTempFiles(bool includeLegacyTempRoot)
        {
            var found = new List<string>();
            string root = Path.GetFullPath(Constants.COMPRESS_TMP_DIR);
            if (root.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) && Directory.Exists(root))
                found.AddRange(SafeEnumerateFiles(root));
            // sweeps the current scratch dir plus bare %temp% root -- the latter only matters for leftovers from before everything moved under %temp%\ReplayKit (or from a pre-c# install that still wrote status files), and stays harmless once those are gone.
            var sweepRoots = includeLegacyTempRoot ? new[] { Constants.SCRATCH_DIR, Path.GetTempPath() } : new[] { Constants.SCRATCH_DIR };
            foreach (var sweepRoot in sweepRoots)
            {
                if (!Directory.Exists(sweepRoot)) continue;
                foreach (var file in SafeEnumerateFiles(sweepRoot))
                {
                    string name = Path.GetFileName(file);
                    if (name.StartsWith("streamable_upload_status_", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("replaykit_compress_", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("replaykit_trim_", StringComparison.OrdinalIgnoreCase))
                        found.Add(file);
                }
            }
            // every atomic-write .tmp sidecar (clips_db.json, clips_index.json, replaykit_settings.json, obss own ini/json, ...) lands in scratch, not next to its real file -- catch anything a crash left behind mid-write with one broad sweep, scoped to just this ReplayKit-owned dir.
            if (Directory.Exists(Constants.SCRATCH_DIR))
            {
                found.AddRange(SafeEnumerateFiles(Constants.SCRATCH_DIR, "*.tmp"));
                // A detached status poll lasts at most thirty minutes; older private cookie copies are abandoned.
                foreach (string file in SafeEnumerateFiles(Constants.SCRATCH_DIR, "streamable_poll_*.cookies.txt"))
                {
                    try { if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddHours(-1)) found.Add(file); }
                    catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
            // sweep the clip folder for crashed-worker leftovers (sidecars from cross-volume finalize + older naming patterns).
            try
            {
                string clipDir = AppConfig.GetClipDir();
                if (!string.IsNullOrEmpty(clipDir) && Directory.Exists(clipDir))
                {
                    foreach (var file in new[] { clipDir }.Concat(Clips.GameFolders(clipDir)).SelectMany(dir => SafeEnumerateFiles(dir)))
                    {
                        string name = Path.GetFileName(file);
                        if (name.StartsWith("_replaykit_", StringComparison.OrdinalIgnoreCase) ||
                            name.StartsWith("_compress_tmp_", StringComparison.OrdinalIgnoreCase) ||
                            name.StartsWith("_trim_tmp_", StringComparison.OrdinalIgnoreCase))
                            found.Add(file);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static void ClearStaleCompressedTempFiles()
        {
            foreach (var file in EnumerateStaleTempFiles(true))
            {
                try { File.Delete(file); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }
    }
}

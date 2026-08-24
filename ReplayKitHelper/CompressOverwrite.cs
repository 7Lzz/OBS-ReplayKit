using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // dispatches a local compress-overwrite job (CompressOverwriteWorker) and reacts once it finishes -- the "watcher" from the ps original, now a plain Task continuation instead of a second [powershell] runspace blocked on EndInvoke. ported from obs_replaykit helper modules/42_compress_overwrite.ps1s Start-CompressOverwriteFile / Start-CompressOverwriteResultWatcher.
    internal static class CompressOverwrite
    {
        public static JObject StartCompressOverwriteFile(Clips.SafeClipPath selected, string mode = "smaller")
        {
            AppConfig.LoadConfig();
            string requestId = UploadState.NewRequestId();
            mode = !string.IsNullOrEmpty(mode) ? mode.ToLowerInvariant() : "smaller";
            if (mode != "fast" && mode != "smaller") mode = "smaller";

            if (selected == null || !File.Exists(selected.Full))
                return new JObject { ["ok"] = false, ["message"] = "Clip not found" };

            var decision = UploadState.GetUploadJobStartDecision(selected.Name);
            if (!decision.Ok) return new JObject { ["ok"] = false, ["busy"] = decision.Busy, ["message"] = decision.Message };

            var caps = Compression.GetHelperCapabilities();
            string ffmpeg = caps["ffmpeg"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg))
                return new JObject { ["ok"] = false, ["message"] = "ffmpeg.exe not found in clip folder" };
            string ffprobe = caps["ffprobe"]?.Value<string>();

            var metadata = Compression.GetVideoMetadata(ffprobe, ffmpeg, selected.Full);
            if (metadata.Duration < 1)
                return new JObject { ["ok"] = false, ["message"] = "Could not read video duration" };
            double duration = metadata.Duration;

            string ext = Path.GetExtension(selected.Name);
            // encode into %temp% so the in-flight file never shows in the clip folder. the worker handles cross-volume safely with a sidecar copy + atomic rename on the source volume.
            string tempPath = Path.Combine(Constants.SCRATCH_DIR, "replaykit_compress_" + requestId + ext);
            long preBytes = 0;
            try { preBytes = new FileInfo(selected.Full).Length; } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }

            UploadState.SetUploadState(
                requestId: requestId, state: "compressing", active: true, clipName: selected.Name,
                startedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), url: "", error: "", phase: "compressing",
                percent: 1, kind: "compress-overwrite", cancelRequested: false, tempPath: tempPath);

            Log.Write("Start-CompressOverwriteFile clip=" + selected.Name + " mode=" + mode + " duration=" + duration, "compress", requestId);

            string fastEncoder = caps["fastEncoder"]?.Value<string>();
            string smallerEncoder = caps["smallerEncoder"]?.Value<string>();
            string sourceFull = selected.Full;
            string selectedName = selected.Name;

            var task = Task.Run(() => CompressOverwriteWorker.Run(requestId, ffmpeg, sourceFull, tempPath, duration, mode, preBytes, caps, fastEncoder, smallerEncoder));
            task.ContinueWith(t => OnCompressOverwriteComplete(t, requestId, tempPath));

            return new JObject { ["ok"] = true, ["state"] = "compressing", ["clip"] = selectedName, ["requestId"] = requestId };
        }

        // runs once the worker task finishes: reads its result, resolves the shared job state to idle/error, and cleans up the temp encode file. mirrors Start-CompressOverwriteResultWatcher, minus the clips-cache invalidation (CompressOverwriteWorker.Run already does that via Clips.MarkCompressed on success) and the status-file read (the worker reports thru UploadState.SetUploadState directly, so theres nothing left to read back).
        private static void OnCompressOverwriteComplete(Task<CompressOverwriteResult> task, string requestId, string tempPath)
        {
            bool workerThrew = task.IsFaulted;
            string workerThrewMessage = workerThrew ? (task.Exception?.InnerException?.Message ?? task.Exception?.Message) : null;

            bool cancelled = false;
            lock (Server.State.UploadLock)
            {
                var job = Server.State.Jobs.TryGetValue(requestId, out var j) ? j : Server.State.Upload;
                if (job.CancelRequested)
                {
                    job.CancelRequested = false;
                    cancelled = true;
                }
            }
            if (cancelled)
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                return;
            }

            bool success = !workerThrew && task.Result.Ok;
            if (success)
            {
                // no url to set -- the clip card just goes back to its normal state once /clips picks up the new mtime/size.
                UploadState.SetUploadState(requestId: requestId, state: "idle", active: false, error: "", phase: "", percent: 0, url: "", tempPath: "");
            }
            else
            {
                string msg = !workerThrew && !string.IsNullOrEmpty(task.Result.Message)
                    ? task.Result.Message
                    : workerThrew ? "Compress failed: " + workerThrewMessage : "Compress finished without success (worker exited)";
                UploadState.SetUploadState(requestId: requestId, state: "error", active: false, error: msg, phase: "error", percent: 0, tempPath: "");
            }

            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }
    }
}

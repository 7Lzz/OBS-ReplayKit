using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // starts a local compress-overwrite job (CompressOverwriteWorker) and resolves the job state once it finishes
    internal static class CompressOverwrite
    {
        public static JObject StartCompressOverwriteFile(Clips.SafeClipPath selected, string mode = "smaller", int scaleHeight = 0, long? expectedSize = null, DateTime? expectedWriteTime = null)
        {
            AppConfig.LoadConfig();
            string requestId = UploadState.NewRequestId();
            mode = !string.IsNullOrEmpty(mode) ? mode.ToLowerInvariant() : "smaller";
            if (mode != "fast" && mode != "smaller") mode = "smaller";
            if (scaleHeight < 120 || scaleHeight > 4320) scaleHeight = 0;

            if (selected == null || !File.Exists(selected.Full))
                return new JObject { ["ok"] = false, ["message"] = "Clip not found" };

            using (var reservation = JobCoordinator.TryReserve(requestId, selected.Name, selected.Full, "compress-overwrite", out string busy))
            {
                if (reservation == null) return new JObject { ["ok"] = false, ["busy"] = true, ["message"] = busy };

                var current = new FileInfo(selected.Full);
                long sourceSize = current.Length;
                DateTime sourceWriteTime = current.LastWriteTimeUtc;
                if ((expectedSize.HasValue && sourceSize != expectedSize.Value) ||
                    (expectedWriteTime.HasValue && sourceWriteTime != expectedWriteTime.Value))
                    return new JObject { ["ok"] = false, ["message"] = "The selected clip changed; select it again." };

                try { CompressionHistory.Prepare(); }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is Newtonsoft.Json.JsonException)
                {
                    Log.Write("Compression history: " + ex.Message);
                    return new JObject { ["ok"] = false, ["message"] = "Compression history could not be opened or saved. Check ReplayKit's saved state folder before compressing." };
                }

                var caps = Compression.GetHelperCapabilities();
                string fastEncoder = caps["fastEncoder"]?.Value<string>();
                string smallerEncoder = caps["smallerEncoder"]?.Value<string>();
                string ffmpeg = caps["ffmpeg"]?.Value<string>();
                if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg))
                    return new JObject { ["ok"] = false, ["message"] = "ffmpeg.exe not found in clip folder" };
                if (string.IsNullOrEmpty(mode == "fast" ? fastEncoder : smallerEncoder))
                    return new JObject { ["ok"] = false, ["message"] = "No compatible compression encoder is available on this PC." };
                string ffprobe = caps["ffprobe"]?.Value<string>();

                var metadata = Compression.GetVideoMetadata(ffprobe, ffmpeg, selected.Full);
                if (metadata.Duration < 1)
                    return new JObject { ["ok"] = false, ["message"] = "Could not read video duration" };
                double duration = metadata.Duration;

                string ext = Path.GetExtension(selected.Name);
                // encode into %temp% so the in-flight file never shows in the clip folder. the worker handles cross-volume safely with a sidecar copy + atomic rename on the source volume.
                string tempPath = Path.Combine(Constants.SCRATCH_DIR, "replaykit_compress_" + requestId + ext);
                long preBytes = sourceSize;

                UploadState.SetUploadState(
                    requestId: requestId, state: "compressing", active: true, clipName: selected.Name,
                    startedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), url: "", error: "", phase: "compressing",
                    percent: 1, kind: "compress-overwrite");

                Log.Write("Start-CompressOverwriteFile clip=" + selected.Name + " mode=" + mode + " duration=" + duration + " scaleHeight=" + scaleHeight, "compress", requestId);

                string sourceFull = selected.Full;
                string selectedName = selected.Name;
                int scale = scaleHeight;

                reservation.Start(() => CompressOverwriteWorker.Run(requestId, ffmpeg, sourceFull, tempPath, duration, mode, preBytes, caps, fastEncoder, smallerEncoder, scale, sourceWriteTime),
                    t => OnCompressOverwriteComplete(t, requestId, tempPath));

                return new JObject { ["ok"] = true, ["state"] = "compressing", ["clip"] = selectedName, ["requestId"] = requestId };
            }
        }

        // runs once the worker task finishes: resolves the job to idle or error and removes the temp encode; the worker already wrote the compress marker on success
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
                UploadState.SetUploadState(requestId: requestId, state: "idle", active: false, error: "", phase: "", percent: 0, url: "", message: task.Result.Message ?? "");
            }
            else
            {
                string msg = !workerThrew && !string.IsNullOrEmpty(task.Result.Message)
                    ? task.Result.Message
                    : workerThrew ? "Compress failed: " + workerThrewMessage : "Compress finished without success (worker exited)";
                UploadState.SetUploadState(requestId: requestId, state: "error", active: false, error: msg, phase: "error", percent: 0);
            }

            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    internal sealed class CompressOverwriteResult
    {
        public bool Ok;
        public string Message;
    }

    // compresses a clip locally with the best encoder the machine has and atomically replaces the original, reporting progress through UploadState and writing the compress marker through Clips.MarkCompressed
    internal static class CompressOverwriteWorker
    {
        private static readonly SemaphoreSlim SoftwareSlot = new SemaphoreSlim(1, 1);
        private static readonly SemaphoreSlim HardwareSlot = new SemaphoreSlim(1, 1);
        private static List<string> Combine(List<string> head, string[] mid, List<string> tail)
        {
            var result = new List<string>(head);
            result.AddRange(mid);
            result.AddRange(tail);
            return result;
        }

        public static CompressOverwriteResult Run(
            string requestId, string ffmpeg, string sourcePath, string tempPath,
            double durationSec, string mode, long preBytes, JObject caps, string fastEncoder, string smallerEncoder,
            int scaleHeight = 0, DateTime? expectedWriteTime = null)
        {
            var token = JobCoordinator.Token(requestId);
            token.ThrowIfCancellationRequested();
            string pickedEncoder = mode == "fast"
                ? fastEncoder : smallerEncoder;
            CompressionProfile.Arguments(pickedEncoder, mode);
            var slot = CompressionProfile.Software(pickedEncoder) ? SoftwareSlot : HardwareSlot;
            if (!slot.Wait(0))
            {
                UploadState.SetUploadState(requestId: requestId, state: "compressing", phase: "queued", percent: 0, message: "Waiting for another compression to finish");
                slot.Wait(token);
            }
            try
            {
                token.ThrowIfCancellationRequested();
                return Encode(requestId, ffmpeg, sourcePath, tempPath, durationSec, mode, preBytes, caps, pickedEncoder, scaleHeight, expectedWriteTime);
            }
            finally { slot.Release(); }
        }

        private static CompressOverwriteResult Encode(string requestId, string ffmpeg, string sourcePath, string tempPath,
            double durationSec, string mode, long preBytes, JObject caps, string pickedEncoder, int scaleHeight, DateTime? expectedWriteTime)
        {
            var token = JobCoordinator.Token(requestId);
            var resources = CompressionProfile.Limits(pickedEncoder == "libx265");

            // capture the source clips filesystem timestamps before the atomic replace. restoring them after the encode keeps the clip in its original sort position in the dock instead of jumping to the top of the list every time its compressed.
            DateTime? origLastWrite = null;
            DateTime? origCreation = null;
            try
            {
                var srcInfo = new FileInfo(sourcePath);
                if (!srcInfo.Exists || srcInfo.Length != preBytes ||
                    (expectedWriteTime.HasValue && srcInfo.LastWriteTimeUtc != expectedWriteTime.Value))
                    return new CompressOverwriteResult { Ok = false, Message = "The selected clip changed; select it again." };
                if (srcInfo.Exists) { origLastWrite = srcInfo.LastWriteTimeUtc; origCreation = srcInfo.CreationTimeUtc; }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }

            Log.Write("start mode=" + mode + " input=" + sourcePath + " temp=" + tempPath + " duration=" + durationSec, "compress", requestId);
            UploadState.SetUploadState(requestId: requestId, state: "compressing", phase: "compressing", percent: 1, message: "");

            // hidden marker embedded in the mp4s udta:comment atom so a later listing can identify files already compressed without a sidecar db. format obs-replaykit_compress_v2_<fast|slow>:<unix_ms>:<pre_compress_bytes>.
            string tagMode = mode == "fast" ? "fast" : "slow";
            long tagTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long preSize = 0;
            try { preSize = new FileInfo(sourcePath).Length; } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            string comment = "obs-replaykit_compress_v2_" + tagMode + ":" + tagTs + ":" + preSize;

            var commonHead = new List<string> { "-y", "-nostdin", "-xerror", "-hide_banner", "-loglevel", "error" };
            if (CompressionProfile.Software(pickedEncoder))
                commonHead.AddRange(new[] { "-threads", resources.DecoderThreads.ToString(CultureInfo.InvariantCulture), "-filter_threads", "2" });
            commonHead.AddRange(new[] { "-i", sourcePath, "-map", "0:v:0", "-map", "0:a?" });
            // optional resolution reduction folded into the same encode. -2 keeps aspect + even width; min(ih,H) never upscales, so this is a no-op if the source is already at/below the target.
            if (scaleHeight > 0)
                commonHead.AddRange(new[] { "-vf", "scale=-2:min(ih\\," + scaleHeight + ")" });
            var commonTail = new List<string> { "-c:a", "aac", "-b:a", "96k", "-metadata", "comment=" + comment, "-movflags", "+faststart", "-progress", "pipe:1", "-nostats", tempPath };
            var argv = Combine(commonHead, CompressionProfile.Arguments(pickedEncoder, mode, resources.Workers, resources.FrameThreads), commonTail);
            string resourceLog = CompressionProfile.Software(pickedEncoder)
                ? " workers=" + resources.Workers + " frameThreads=" + resources.FrameThreads : " hardware";
            Log.Write("encoder=" + pickedEncoder + " mode=" + mode + resourceLog, "compress", requestId);

            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = ProcessArgs.Join(argv.ToArray()),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            token.ThrowIfCancellationRequested();
            using var proc = Process.Start(psi);
            using var deadline = new System.Threading.CancellationTokenSource(TimeSpan.FromHours(2));
            using var stopping = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
            using var registration = stopping.Token.Register(() => { try { if (!proc.HasExited) proc.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } });

            // drop to belownormal so a running encode never starves the users foreground apps (game, browser, obs itself). best-effort: if PriorityClass fails (rare race / weird security context), the encode just runs at normal.
            try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is NotSupportedException) { }

            var stderrTask = CompressionOutput.Read(proc.StandardError, proc);

            // pull progress from stdout line-by-line. -progress pipe:1 makes ffmpeg flush each progress block to stdout immediately, so each readline returns within ~1s of the actual encode position -- the alternative (-progress <file>) goes thru the os page cache and can lag arbitrarily on windows. strictly matches out_time_us= (microseconds); out_time_ms= was changed from milliseconds to microseconds in ffmpeg 5.x and is inconsistent across builds, so its never trusted.
            double durationUs = Math.Max(1.0, durationSec * 1000000.0);
            int lastReportedPct = 1;
            int linesSeen = 0;
            try
            {
                string line;
                while ((line = proc.StandardOutput.ReadLine()) != null)
                {
                    linesSeen++;
                    if (line.StartsWith("out_time_us="))
                    {
                        string valStr = line.Substring(12);
                        if (double.TryParse(valStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double us))
                        {
                            int pct = (int)Math.Floor(2 + (94.0 * Math.Min(1.0, us / durationUs)));
                            if (pct != lastReportedPct)
                            {
                                lastReportedPct = pct;
                                UploadState.SetUploadState(requestId: requestId, state: "compressing", phase: "compressing", percent: pct);
                            }
                        }
                    }
                }
            }
            catch (IOException ex)
            {
                Log.Write("stdout read error: " + ex.Message, "compress", requestId);
            }

            proc.WaitForExit();
            var diagnostics = stderrTask.GetAwaiter().GetResult();
            string stderr = diagnostics.Text;
            token.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested) throw new TimeoutException("Compression exceeded its time limit.");
            if (!string.IsNullOrEmpty(stderr)) Log.Write("stderr: " + stderr, "compress", requestId);
            Log.Write("progress lines=" + linesSeen + " lastPct=" + lastReportedPct, "compress", requestId);

            if (proc.ExitCode != 0 || diagnostics.Exceeded)
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                string msg = diagnostics.Exceeded ? "Compression stopped because the encoder produced too many errors." : "ffmpeg failed (exit=" + proc.ExitCode + ")";
                UploadState.SetUploadState(requestId: requestId, state: "error", phase: "error", percent: 0, error: msg);
                return new CompressOverwriteResult { Ok = false, Message = msg };
            }
            if (!File.Exists(tempPath))
            {
                UploadState.SetUploadState(requestId: requestId, state: "error", phase: "error", percent: 0, error: "ffmpeg produced no output file");
                return new CompressOverwriteResult { Ok = false, Message = "ffmpeg produced no output file" };
            }

            // size guard. compression is supposed to shrink files; if it didnt, dont replace the original. this catches very short clips (mp4 header + first idr + aac priming overhead can exceed whatever the encode saved), sources already encoded more aggressively than the target, and any encoder misconfiguration -- the clip stays untouched, the marker is not written, and the next /clips poll shows the file in its original state.
            long outBytes = 0;
            try { outBytes = new FileInfo(tempPath).Length; } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            if (preBytes > 0 && outBytes > 0 && outBytes >= preBytes)
            {
                try { File.Delete(tempPath); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                double preMb = Math.Round(preBytes / 1024.0 / 1024.0, 1);
                double outMb = Math.Round(outBytes / 1024.0 / 1024.0, 1);
                Log.Write(string.Format("abort: would inflate ({0} MB >= {1} MB source); keeping original", outMb, preMb), "compress", requestId);
                string msg = string.Format("Already at minimum size ({0} MB would be larger than {1} MB)", outMb, preMb);
                UploadState.SetUploadState(requestId: requestId, state: "error", phase: "error", percent: 0, error: msg);
                return new CompressOverwriteResult { Ok = false, Message = msg };
            }

            UploadState.SetUploadState(requestId: requestId, state: "compressing", phase: "replacing", percent: 98);
            try
            {
                ClipFiles.Replace(requestId, tempPath, sourcePath, preBytes, origLastWrite.GetValueOrDefault());
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception)
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch (Exception ex2) when (ex2 is IOException || ex2 is UnauthorizedAccessException) { }
                string msg = "Could not replace original: " + ex.Message;
                UploadState.SetUploadState(requestId: requestId, state: "error", phase: "error", percent: 0, error: msg);
                return new CompressOverwriteResult { Ok = false, Message = msg };
            }

            // restore the original file timestamps onto the freshly-replaced file. creation time first so last-write "wins" any potential windows file-system-tunneling refresh.
            if (origLastWrite.HasValue)
            {
                try
                {
                    if (origCreation.HasValue) File.SetCreationTimeUtc(sourcePath, origCreation.Value);
                    File.SetLastWriteTimeUtc(sourcePath, origLastWrite.Value);
                    Log.Write("timestamps restored: ctime=" + origCreation.Value.ToString("o") + " mtime=" + origLastWrite.Value.ToString("o"), "compress", requestId);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Log.Write("timestamp restore failed: " + ex.Message, "compress", requestId);
                }
            }

            if (origLastWrite.HasValue) EditDrafts.Follow(sourcePath, preBytes, origLastWrite.Value.Ticks);

            // persist the compress marker so the clips listings cache check (cmp_mtime == file mtime) sees the new mode immediately -- without this the preserved mtime keeps the cache key matching, but the cached cmp_mode is stale (empty for a first-time compress), so the dock would keep showing the clip as compressable until something else invalidates the entry.
            string clipName = Path.GetFileName(sourcePath);
            long newMtimeTicks;
            if (origLastWrite.HasValue) newMtimeTicks = origLastWrite.Value.Ticks;
            else
            {
                newMtimeTicks = 0;
                try { newMtimeTicks = new FileInfo(sourcePath).LastWriteTimeUtc.Ticks; } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
            Clips.MarkCompressed(clipName, tagMode, newMtimeTicks, preBytes, sourcePath);
            Log.Write("marker written: name=" + clipName + " mode=" + tagMode + " mtime=" + newMtimeTicks, "compress", requestId);

            string historyWarning = null;
            try { CompressionHistory.Record(requestId, mode, preBytes, outBytes, scaleHeight); }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is Newtonsoft.Json.JsonException)
            {
                historyWarning = "The clip was compressed, but its result could not be saved to compression history.";
                Log.Write("Compression history: " + ex.Message, "compress", requestId);
                try { Notifications.Add("warning", "compression-history", "Compression history was not saved", historyWarning); }
                catch (Exception notice) when (notice is IOException || notice is UnauthorizedAccessException || notice is Newtonsoft.Json.JsonException)
                { Log.Write("Compression history notification: " + notice.Message, "compress", requestId); }
            }
            Log.Write("done", "compress", requestId);
            UploadState.SetUploadState(requestId: requestId, state: "done", phase: "done", percent: 100);
            return new CompressOverwriteResult { Ok = true, Message = historyWarning };
        }
    }
}

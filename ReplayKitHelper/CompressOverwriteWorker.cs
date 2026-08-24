using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    internal sealed class CompressOverwriteResult
    {
        public bool Ok;
        public string Message;
    }

    // compresses a clip locally with the machines best available encoder and atomically replaces the original -- no upload. runs as an in-process Task from CompressOverwrite.StartCompressOverwriteFile. ported from the embedded worker script inside obs_replaykit helper modules/42_compress_overwrite.ps1 -- that script already ran as an in-process runspace in the ps original (not a spawned powershell.exe), so this port only changes the invocation mechanism (Task instead of [powershell]::Create() on a RunspacePool), not the architecture. progress/cancellation/marker-writing that the ps worker had to duplicate inline (becuase a runspace cant see the main scripts function scope) call the real shared helpers directly here: UploadState.SetUploadState for progress, Clips.MarkCompressed for the clips_db marker.
    internal static class CompressOverwriteWorker
    {
        private static List<string> Combine(List<string> head, string[] mid, List<string> tail)
        {
            var result = new List<string>(head);
            result.AddRange(mid);
            result.AddRange(tail);
            return result;
        }

        public static CompressOverwriteResult Run(
            string requestId, string ffmpeg, string sourcePath, string tempPath,
            double durationSec, string mode, long preBytes, JObject caps, string fastEncoder, string smallerEncoder)
        {
            int cpuCount = caps?["cpuCount"]?.Value<int>() ?? Environment.ProcessorCount;
            // half-cores per encode for sw codecs. two parallel libx265 jobs split the cpu cleanly with this -- one job grabbing every core context-switches itself silly when a second job lands.
            int swPools = Math.Max(2, cpuCount / 2);
            string pickedEncoder = mode == "fast"
                ? (!string.IsNullOrEmpty(fastEncoder) ? fastEncoder : "libx264")
                : (!string.IsNullOrEmpty(smallerEncoder) ? smallerEncoder : "libx264");

            // capture the source clips filesystem timestamps before the atomic replace. restoring them after the encode keeps the clip in its original sort position in the dock instead of jumping to the top of the list every time its compressed.
            DateTime? origLastWrite = null;
            DateTime? origCreation = null;
            try
            {
                var srcInfo = new FileInfo(sourcePath);
                if (srcInfo.Exists) { origLastWrite = srcInfo.LastWriteTimeUtc; origCreation = srcInfo.CreationTimeUtc; }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }

            Log.Write("start mode=" + mode + " input=" + sourcePath + " temp=" + tempPath + " duration=" + durationSec, "compress", requestId);
            UploadState.SetUploadState(requestId: requestId, state: "compressing", phase: "compressing", percent: 1);

            // hidden marker embedded in the mp4s udta:comment atom so a later listing can identify files already compressed without a sidecar db. format obs-replaykit_compress_v2_<fast|slow>:<unix_ms>:<pre_compress_bytes>.
            string tagMode = mode == "fast" ? "fast" : "slow";
            long tagTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long preSize = 0;
            try { preSize = new FileInfo(sourcePath).Length; } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            string comment = "obs-replaykit_compress_v2_" + tagMode + ":" + tagTs + ":" + preSize;

            // build encoder-specific argv. goals: fast mode -> shortest wall time, ok quality. gpu encoders win here becuase they barely touch the cpu (so games keep running fine) and ffmpeg sustains 5-10x realtime on them. smaller mode -> smallest file at watch-anywhere quality. cpu sw encoders win here -- libx265 is ~2-3x more bit-efficient than nvenc/amf/qsv at matched perceptual quality. qp / crf targets are picked one or two notches below the obs source, so the output is guaranteed to be a step worse perceptually and a noticeable step smaller in size -- the size guard further down verifies that and reverts the replace if the output is somehow not smaller.
            var commonHead = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-i", sourcePath };
            var commonTail = new List<string> { "-c:a", "aac", "-b:a", "96k", "-metadata", "comment=" + comment, "-movflags", "+faststart", "-progress", "pipe:1", "-nostats", tempPath };
            List<string> argv;
            switch (pickedEncoder)
            {
                case "hevc_nvenc":
                    // nvidia hevc. constqp + spatial-aq + lookahead is the safe combo: spatial-aq redistributes the fixed qp across the frame without raising the bitrate ceiling, becuase there is no bitrate ceiling to raise in constqp mode. qp 28 -> ~50% of source at quality close to the hevc qp24 input.
                    argv = Combine(commonHead, new[] { "-c:v", "hevc_nvenc", "-preset", "p4", "-tune", "hq", "-rc", "constqp", "-qp", "28", "-bf", "2", "-spatial-aq", "1", "-rc-lookahead", "16", "-multipass", "disabled", "-profile:v", "main", "-tag:v", "hvc1", "-pix_fmt", "yuv420p" }, commonTail);
                    break;
                case "h264_nvenc":
                    // h264 needs ~30% more bits than hevc for the same quality, so the qp target sits a couple notches above hevc.
                    argv = Combine(commonHead, new[] { "-c:v", "h264_nvenc", "-preset", "p4", "-tune", "hq", "-rc", "constqp", "-qp", "28", "-bf", "2", "-spatial-aq", "1", "-rc-lookahead", "16", "-multipass", "disabled", "-profile:v", "high", "-pix_fmt", "yuv420p" }, commonTail);
                    break;
                case "hevc_amf":
                    // amd amf hevc. cqp mode + matching qp_i/qp_p/qp_b is the rate-control combination that honours the qp target without padding with filler bits. qp 28 mirrors the hevc_nvenc target for comparable compression on amd silicon.
                    argv = Combine(commonHead, new[] { "-c:v", "hevc_amf", "-usage", "transcoding", "-quality", "quality", "-rc", "cqp", "-qp_i", "28", "-qp_p", "28", "-qp_b", "28", "-profile:v", "main", "-tag:v", "hvc1" }, commonTail);
                    break;
                case "h264_amf":
                    argv = Combine(commonHead, new[] { "-c:v", "h264_amf", "-usage", "transcoding", "-quality", "quality", "-rc", "cqp", "-qp_i", "28", "-qp_p", "28", "-qp_b", "28", "-profile:v", "high" }, commonTail);
                    break;
                case "hevc_qsv":
                    // intel quick sync hevc. global_quality maps to icq on qsv -- the closest analogue to nvencs constqp. icq 26 puts us at roughly the same quality target as hevc_nvenc qp. look_ahead 0 keeps file size from creeping up on already-compressed sources.
                    argv = Combine(commonHead, new[] { "-c:v", "hevc_qsv", "-preset", "medium", "-global_quality", "26", "-look_ahead", "0", "-profile:v", "main", "-tag:v", "hvc1" }, commonTail);
                    break;
                case "h264_qsv":
                    argv = Combine(commonHead, new[] { "-c:v", "h264_qsv", "-preset", "medium", "-global_quality", "25", "-look_ahead", "0", "-profile:v", "high" }, commonTail);
                    break;
                case "libx265":
                    // libx265 medium crf 25 -- the textbook "compress with good quality" config, tuned a notch tighter than the original crf 28 defualt. pools=swPools + frame-threads=2 lets two encodes share one cpu cleanly; without the cap, each libx265 process grabs every core and contends.
                    argv = Combine(commonHead, new[] { "-c:v", "libx265", "-preset", "medium", "-crf", "25", "-tag:v", "hvc1", "-x265-params", "pools=" + swPools + ":frame-threads=2:log-level=error" }, commonTail);
                    break;
                default:
                    // libx264 -- universal fallback when nothing else linked. preset varies by mode so fast feels fast even on machines with no hw encoder at all.
                    string preset = mode == "fast" ? "veryfast" : "medium";
                    string crf = mode == "fast" ? "23" : "21";
                    argv = Combine(commonHead, new[] { "-c:v", "libx264", "-preset", preset, "-crf", crf, "-threads", swPools.ToString() }, commonTail);
                    break;
            }
            Log.Write("encoder=" + pickedEncoder + " mode=" + mode + " cpu=" + cpuCount + " swPools=" + swPools, "compress", requestId);

            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = ProcessArgs.Join(argv.ToArray()),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            var proc = Process.Start(psi);

            // drop to belownormal so a running encode never starves the users foreground apps (game, browser, obs itself). best-effort: if PriorityClass fails (rare race / weird security context), the encode just runs at normal.
            try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is NotSupportedException) { }

            // exposes the live ffmpeg process on the job record so UploadState.CancelActiveUpload can kill it directly -- replaces the ps originals $statusPath.pid sidecar file, which existed only becuase that worker ran in a seperate runspace unable to reach $script:State.
            UploadState.SetUploadState(requestId: requestId, encoderProcess: proc);

            // stderr is collected once at end for diagnostics only.
            var stderrTask = proc.StandardError.ReadToEndAsync();

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
            string stderr = stderrTask.Result;
            if (!string.IsNullOrEmpty(stderr)) Log.Write("stderr: " + stderr, "compress", requestId);
            Log.Write("progress lines=" + linesSeen + " lastPct=" + lastReportedPct, "compress", requestId);

            if (proc.ExitCode != 0)
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                string msg = "ffmpeg failed (exit=" + proc.ExitCode + ")";
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
                // same-volume move is an atomic rename. across volumes its copy-then-delete, which can leave the destination half-written on crash. detect the mismatch and route thru a sidecar on the sources volume so the final replace is always atomic. the sidecar carries the _replaykit_ prefix the clip listing filters out.
                string sourceVol = Path.GetPathRoot(sourcePath);
                string tempVol = Path.GetPathRoot(tempPath);
                if (string.Equals(sourceVol, tempVol, StringComparison.OrdinalIgnoreCase))
                {
                    Native.MoveFileReplace(tempPath, sourcePath);
                }
                else
                {
                    string extOut = Path.GetExtension(sourcePath);
                    string sideTemp = Path.Combine(Path.GetDirectoryName(sourcePath), "_replaykit_finalize_" + Guid.NewGuid().ToString("N") + extOut);
                    File.Copy(tempPath, sideTemp, true);
                    Native.MoveFileReplace(sideTemp, sourcePath);
                    try { File.Delete(tempPath); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                }
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

            // persist the compress marker so the clips listings cache check (cmp_mtime == file mtime) sees the new mode immediately -- without this the preserved mtime keeps the cache key matching, but the cached cmp_mode is stale (empty for a first-time compress), so the dock would keep showing the clip as compressable until something else invalidates the entry.
            string clipName = Path.GetFileName(sourcePath);
            long newMtimeTicks;
            if (origLastWrite.HasValue) newMtimeTicks = origLastWrite.Value.Ticks;
            else
            {
                newMtimeTicks = 0;
                try { newMtimeTicks = new FileInfo(sourcePath).LastWriteTimeUtc.Ticks; } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
            Clips.MarkCompressed(clipName, tagMode, newMtimeTicks, preBytes);
            Log.Write("marker written: name=" + clipName + " mode=" + tagMode + " mtime=" + newMtimeTicks, "compress", requestId);

            Log.Write("done", "compress", requestId);
            UploadState.SetUploadState(requestId: requestId, state: "done", phase: "done", percent: 100);
            return new CompressOverwriteResult { Ok = true };
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    public sealed class TrimResult
    {
        public bool Ok;
        public string Message;
        public string Name;
        public string SourceName;
        public string Mode;
        public bool Precise;
        public bool RemoveAudio;
        public double StartSec;
        public double EndSec;
        public double DurationSec;
    }

    // local file edits on clips: stream-copy trim and verbatim duplicate, plus the ffprobe keyframe scan the trim ui uses for snap points. both trim operations leave the source untouched by defualt; an explicit "overwrite" mode atomically replaces the source via a sibling temp file + atomic replace, so a crashed ffmpeg can never leave the original half-written. ported from obs_replaykit helper modules/41_trim.ps1. the keyframe scan (trim_keyframes_worker.ps1 in the original) is TrimKeyframesWorker.cs, run as an in-process Task -- see GetTrimKeyframeWorkerResult for how that replaces the ps originals spawned-process + status-file polling.
    internal static class Trim
    {
        private const int TrimKeyframeWorkerTimeoutMs = 300000;

        // probe the sources video bitrate in bits/sec. used by the precise trim branch to cap libx264s output so an already-compressed source cant inflate during re-encode (crf 14 alone faithfully reproduces blocking/banding from a low-bitrate input, costing more bits than the source carried -- the maxrate cap stops that). strategy: ask ffprobe for the v:0 streams bit_rate tag (cheapest, most mp4s from obs carry it). falls back to (filesize_bytes * 8) / duration_sec, knocking 10% off to ballpark the video portion (audio is typically a few hundred kbps of the total). returns 0 if both fail; callers should skip the cap in that case rather than guess.
        public static long GetVideoBitratePerSec(string path)
        {
            var caps = Compression.GetHelperCapabilities();
            string ffmpeg = caps["ffmpeg"]?.Value<string>() ?? "";
            string ffprobe = caps["ffprobe"]?.Value<string>() ?? "";

            if (!string.IsNullOrWhiteSpace(ffprobe) && File.Exists(ffprobe))
            {
                try
                {
                    var r = Compression.InvokeNativeCapture(ffprobe, new[]
                    {
                        "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=bit_rate",
                        "-of", "default=nokey=1:noprint_wrappers=1", path,
                    });
                    if (r.ExitCode == 0 && r.Output.Count > 0)
                    {
                        string raw = string.Join("", r.Output).Trim();
                        if (long.TryParse(raw, out long value) && value > 0) return value;
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception)
                {
                    Log.Write("Get-VideoBitratePerSec ffprobe failed: " + ex.Message);
                }
            }

            if (!string.IsNullOrWhiteSpace(ffmpeg) && File.Exists(ffmpeg))
            {
                try
                {
                    double duration = Compression.GetVideoDurationSec(ffmpeg, path);
                    if (duration > 0)
                    {
                        long size = new FileInfo(path).Length;
                        if (size > 0) return (long)Math.Floor((size * 8.0 / duration) * 0.9);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Log.Write("Get-VideoBitratePerSec fallback failed: " + ex.Message);
                }
            }
            return 0;
        }

        private static void SaveTrimKeyframeCache(string cacheKey, string cacheSig, KeyframeScanResult result)
        {
            lock (Server.State.TrimKeyframeCacheLock)
            {
                Server.State.TrimKeyframeCache[cacheKey] = new TrimKeyframeCacheEntry { Sig = cacheSig, At = DateTime.UtcNow, Result = result };
                if (Server.State.TrimKeyframeCache.Count > 64)
                {
                    int removeCount = Server.State.TrimKeyframeCache.Count - 64;
                    var oldest = Server.State.TrimKeyframeCache.OrderBy(kv => kv.Value.At).Take(removeCount).Select(kv => kv.Key).ToList();
                    foreach (var key in oldest) Server.State.TrimKeyframeCache.Remove(key);
                }
            }
        }

        private static KeyframeScanResult GetTrimKeyframeCache(string cacheKey, string cacheSig)
        {
            lock (Server.State.TrimKeyframeCacheLock)
            {
                if (Server.State.TrimKeyframeCache.TryGetValue(cacheKey, out var entry) && entry.Sig == cacheSig && entry.Result != null)
                {
                    var r = entry.Result;
                    return new KeyframeScanResult
                    {
                        Ok = r.Ok,
                        Name = r.Name,
                        Keyframes = new List<double>(r.Keyframes),
                        Count = r.Count,
                        Method = r.Method,
                        ProbeMs = r.ProbeMs,
                        Message = r.Message,
                        Cached = true,
                        Pending = false,
                    };
                }
            }
            return null;
        }

        // polls an in-flight keyframe-scan Task. mirrors Get-TrimKeyframeWorkerResult from the ps original, but reads Task state directly instead of a status file + pid liveness check -- theres no "worker process died without writing a result" case here since TrimKeyframesWorker.Run always returns a result object (its own outer catch guarantees that), so that third branch from the original has no equivalent to port.
        private static KeyframeScanResult GetTrimKeyframeWorkerResult(TrimKeyframeJob job, string cacheKey, string cacheSig, string clipName)
        {
            if (job.Task.IsCompleted)
            {
                KeyframeScanResult result;
                if (job.Task.IsFaulted)
                {
                    string msg = job.Task.Exception?.InnerException?.Message ?? job.Task.Exception?.Message ?? "unknown error";
                    result = new KeyframeScanResult { Ok = false, Message = "Keyframe worker crashed: " + msg };
                }
                else
                {
                    result = job.Task.Result;
                    if (string.IsNullOrWhiteSpace(result.Name)) result.Name = clipName;
                    if (result.Ok) SaveTrimKeyframeCache(cacheKey, cacheSig, result);
                }
                result.Pending = false;
                lock (Server.State.TrimKeyframeJobsLock) { Server.State.TrimKeyframeJobs.Remove(job.Key); }
                job.Cts.Dispose();
                return result;
            }

            double ageMs = (DateTime.UtcNow - job.StartedAt).TotalMilliseconds;
            if (ageMs > TrimKeyframeWorkerTimeoutMs)
            {
                try { job.Cts.Cancel(); } catch (ObjectDisposedException) { }
                lock (Server.State.TrimKeyframeJobsLock) { Server.State.TrimKeyframeJobs.Remove(job.Key); }
                return new KeyframeScanResult { Ok = false, Pending = false, Message = "Keyframe scan timed out; using normal fast trim" };
            }

            return new KeyframeScanResult { Ok = false, Pending = true, Message = "Keyframe snap points are still loading", RetryMs = 500 };
        }

        private static KeyframeScanResult StartTrimKeyframeWorker(Clips.SafeClipPath source, string ffprobe, string cacheKey, string cacheSig)
        {
            string jobKey = cacheKey + "|" + cacheSig;
            var cts = new CancellationTokenSource();
            var task = Task.Run(() => TrimKeyframesWorker.Run(ffprobe, source.Full, source.Name, cts.Token));

            var job = new TrimKeyframeJob { Key = jobKey, Sig = cacheSig, Task = task, StartedAt = DateTime.UtcNow, Cts = cts };
            lock (Server.State.TrimKeyframeJobsLock) { Server.State.TrimKeyframeJobs[jobKey] = job; }
            return new KeyframeScanResult { Ok = false, Pending = true, Message = "Keyframe snap points are loading", RetryMs = 500 };
        }

        public static KeyframeScanResult GetClipKeyframeTimes(string sourceName, double durationSec = 0.0)
        {
            var source = Clips.GetSafeClipPath(sourceName);
            if (source == null || !File.Exists(source.Full))
                return new KeyframeScanResult { Ok = false, Message = "Source clip not found" };
            var fi = new FileInfo(source.Full);
            if (!fi.Exists) return new KeyframeScanResult { Ok = false, Message = "Source clip not found" };

            string cacheKey = fi.FullName.ToLowerInvariant();
            string cacheSig = fi.Length + ":" + fi.LastWriteTimeUtc.Ticks;
            var cached = GetTrimKeyframeCache(cacheKey, cacheSig);
            if (cached != null) return cached;

            string jobKey = cacheKey + "|" + cacheSig;
            TrimKeyframeJob job;
            lock (Server.State.TrimKeyframeJobsLock) { Server.State.TrimKeyframeJobs.TryGetValue(jobKey, out job); }
            if (job != null) return GetTrimKeyframeWorkerResult(job, cacheKey, cacheSig, source.Name);

            string ffprobe = Compression.GetHelperCapabilities()["ffprobe"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(ffprobe) || !File.Exists(ffprobe))
                return new KeyframeScanResult { Ok = false, Message = "ffprobe.exe not found in clip folder" };

            return StartTrimKeyframeWorker(source, ffprobe, cacheKey, cacheSig);
        }

        // picks a non-colliding "<base> (<suffix>).<ext>" or "<base> (<suffix> n).<ext>" filename inside the clip folder. returns null if 99 candidates are taken.
        public static string GetSuffixedOutputName(string sourceName, string suffix)
        {
            string baseName = Path.GetFileNameWithoutExtension(sourceName);
            string ext = Path.GetExtension(sourceName);
            string clipDir = AppConfig.GetClipDir();
            string candidate = baseName + " (" + suffix + ")" + ext;
            if (!File.Exists(Path.Combine(clipDir, candidate))) return candidate;
            for (int i = 2; i <= 99; i++)
            {
                candidate = baseName + " (" + suffix + " " + i + ")" + ext;
                if (!File.Exists(Path.Combine(clipDir, candidate))) return candidate;
            }
            return null;
        }

        // trim a clip with ffmpeg. two precision modes: precise=true (defualt): decode-then-seek (-ss after -i) + libx264 crf 14 re-encode. cut lands frame-accurate at the picked time; encode takes roughly clip-length seconds at the veryfast preset. precise=false: stream-copy with input-side seek. lossless and near-instant, but the cut snaps to the previous keyframe (every 1-2s for obs replay-buffer recordings). mode is copy (defualt; new file) or overwrite (replaces the source in place via a temp sibling + atomic replace). removeAudio drops all audio streams from the edited output.
        public static TrimResult InvokeClipTrim(string sourceName, double startSec, double endSec, string mode = "copy", bool precise = true, bool removeAudio = false)
        {
            if (double.IsNaN(startSec) || double.IsNaN(endSec) || double.IsInfinity(startSec) || double.IsInfinity(endSec))
                return new TrimResult { Ok = false, Message = "Bad start/end" };
            if (startSec < 0) return new TrimResult { Ok = false, Message = "Start must be >= 0" };
            if (endSec <= startSec) return new TrimResult { Ok = false, Message = "End must be greater than start" };
            double duration = endSec - startSec;
            if (duration < 0.5) return new TrimResult { Ok = false, Message = "Trim must be at least 0.5s long" };

            mode = !string.IsNullOrEmpty(mode) ? mode.ToLowerInvariant() : "copy";
            if (mode != "copy" && mode != "overwrite") return new TrimResult { Ok = false, Message = "Unknown trim mode '" + mode + "'" };
            bool overwrite = mode == "overwrite";

            var source = Clips.GetSafeClipPath(sourceName);
            if (source == null || !File.Exists(source.Full)) return new TrimResult { Ok = false, Message = "Source clip not found" };

            string ffmpeg = Compression.GetHelperCapabilities()["ffmpeg"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg)) return new TrimResult { Ok = false, Message = "ffmpeg.exe not found in clip folder" };

            string ext = Path.GetExtension(source.Name);
            // for overwrite mode, snapshot the sources filesystem timestamps now (before the atomic replace destroys them) so they can be restored after the encode -- same rationale as the compress-overwrite path: keeps the clip in its original sort position rather than jumping to the top of the dock list every time its trimmed.
            DateTime? origLastWriteUtc = null;
            DateTime? origCreationUtc = null;
            if (overwrite)
            {
                try
                {
                    var srcInfo = new FileInfo(source.Full);
                    if (srcInfo.Exists)
                    {
                        origLastWriteUtc = srcInfo.LastWriteTimeUtc;
                        origCreationUtc = srcInfo.CreationTimeUtc;
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }

            string outName, tempPath, outPath, finalPath;
            if (overwrite)
            {
                // encode into %temp% so the in-flight file never shows in the clip folder. cross-volume finalize uses a _replaykit_ sidecar on the sources volume (filtered out of the clip listing).
                outName = source.Name;
                tempPath = Path.Combine(Constants.SCRATCH_DIR, "replaykit_trim_" + Guid.NewGuid().ToString("N") + ext);
                outPath = tempPath;
                finalPath = source.Full;
            }
            else
            {
                string suffix = precise ? "trimmed" : "trimmed (fast)";
                outName = GetSuffixedOutputName(source.Name, suffix);
                if (outName == null) return new TrimResult { Ok = false, Message = "Too many existing trim outputs" };
                finalPath = Path.Combine(AppConfig.GetClipDir(), outName);
                tempPath = Path.Combine(AppConfig.GetClipDir(), "_replaykit_trim_copy_" + Guid.NewGuid().ToString("N") + ext);
                outPath = tempPath;
            }

            // invariant-culture decimal formatting -- ffmpeg only accepts . as the decimal separator, never ,, regardless of system locale.
            string startStr = startSec.ToString("F3", CultureInfo.InvariantCulture);
            string durStr = duration.ToString("F3", CultureInfo.InvariantCulture);
            List<string> argv;

            if (precise)
            {
                // ss after -i is output-side / decode-then-seek: ffmpeg decodes frames from the start of the file but discards everything before startSec, then emits exactly the picked window -- the only way to land the cut on the exact frame chosen, at the cost of a full re-encode. crf 14 is the perceptual quality target -- visually indistinguishable from the source. on a fresh obs clip (30-60 mbps) crf 14 lands around 70-85% of source size; on a heavily compressed source it would happily exceed the source bitrate trying to preserve compression artifacts, so the sources video bitrate is probed and passed as -maxrate / -bufsize -- x264 in "capped crf" mode still targets crf, but rate-control caps the output when reaching it would spend more bits than the source had. trims of original clips look the same as before; trims of compressed clips no longer inflate. audio is stream-copied so the source aac bytes are preserved exactly, no second-generation transcoding loss. preset is "fast" rather than "veryfast" -- veryfast at low bitrates throws away bit-allocation efficiency the cap depends on, and the wall-clock difference for a typical trim is small enough to be invisible.
                argv = new List<string> { "-hide_banner", "-loglevel", "error", "-i", source.Full, "-ss", startStr, "-t", durStr, "-c:v", "libx264", "-preset", "fast", "-crf", "14" };
                long srcBps = GetVideoBitratePerSec(source.Full);
                if (srcBps > 0)
                {
                    // floor at 200 kbps so a near-empty clip cant drive the cap so low the encoder produces unwatchable output.
                    long kbps = Math.Max(200, (long)Math.Floor(srcBps / 1000.0));
                    argv.Add("-maxrate"); argv.Add(kbps + "k");
                    argv.Add("-bufsize"); argv.Add((kbps * 2) + "k");
                    Log.Write("Trim cap: source=" + srcBps + " bps, maxrate=" + kbps + "k");
                }
                else
                {
                    Log.Write("Trim cap: source bitrate unknown, leaving CRF unconstrained");
                }
                if (removeAudio) argv.Add("-an"); else { argv.Add("-c:a"); argv.Add("copy"); }
                argv.AddRange(new[] { "-avoid_negative_ts", "make_zero", "-movflags", "+faststart", "-y", outPath });
            }
            else
            {
                // ss before -i is input-side / fast-seek: ffmpeg jumps to the nearest keyframe at-or-before startSec without decoding the preceding frames. combined with -c copy this is a remux only -- lossless, finishes in well under a second, at the cost of snap-to-keyframe imprecision.
                argv = new List<string> { "-hide_banner", "-loglevel", "error", "-ss", startStr, "-i", source.Full, "-t", durStr, "-c:v", "copy" };
                if (removeAudio) argv.Add("-an"); else { argv.Add("-c:a"); argv.Add("copy"); }
                argv.AddRange(new[] { "-avoid_negative_ts", "make_zero", "-y", outPath });
            }

            Log.Write("Trim (" + mode + ", precise=" + precise + ", removeAudio=" + removeAudio + "): " + ffmpeg + " " + string.Join(" ", argv));
            try
            {
                var result = Compression.InvokeNativeCapture(ffmpeg, argv);
                if (result.ExitCode != 0)
                {
                    string combined = string.Join("\n", result.Output);
                    string msg = combined.Length > 400 ? combined.Substring(0, 400) + "..." : combined;
                    try { if (File.Exists(outPath)) File.Delete(outPath); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                    return new TrimResult { Ok = false, Message = "ffmpeg trim failed (exit=" + result.ExitCode + "): " + msg };
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception)
            {
                try { if (File.Exists(outPath)) File.Delete(outPath); } catch (Exception ex2) when (ex2 is IOException || ex2 is UnauthorizedAccessException) { }
                return new TrimResult { Ok = false, Message = "Trim failed: " + ex.Message };
            }

            if (!File.Exists(outPath)) return new TrimResult { Ok = false, Message = "ffmpeg reported success but the output file is missing" };

            if (!overwrite)
            {
                try
                {
                    if (File.Exists(finalPath))
                    {
                        try { File.Delete(tempPath); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                        return new TrimResult { Ok = false, Message = "A clip with that output name already exists" };
                    }
                    // plain File.Move, not an atomic replace -- a new output name colliding with an existing file is a genuine error above, not a case to silently overwrite.
                    File.Move(tempPath, finalPath);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch (Exception ex2) when (ex2 is IOException || ex2 is UnauthorizedAccessException) { }
                    return new TrimResult { Ok = false, Message = "Could not publish trimmed copy: " + ex.Message };
                }
            }
            else
            {
                // same-volume move is an atomic rename; across volumes its copy-then-delete, which can corrupt the destination on crash. detect and route thru a sidecar on the sources volume when needed, so the final replace is always atomic.
                try
                {
                    string sourceVol = Path.GetPathRoot(source.Full);
                    string tempVol = Path.GetPathRoot(tempPath);
                    if (string.Equals(sourceVol, tempVol, StringComparison.OrdinalIgnoreCase))
                    {
                        Native.MoveFileReplace(tempPath, source.Full);
                    }
                    else
                    {
                        string sideTemp = Path.Combine(Path.GetDirectoryName(source.Full), "_replaykit_finalize_" + Guid.NewGuid().ToString("N") + ext);
                        File.Copy(tempPath, sideTemp, true);
                        Native.MoveFileReplace(sideTemp, source.Full);
                        try { File.Delete(tempPath); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception)
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch (Exception ex2) when (ex2 is IOException || ex2 is UnauthorizedAccessException) { }
                    return new TrimResult { Ok = false, Message = "Could not replace original: " + ex.Message };
                }
                // restore the original mtime/ctime captured before the encode -- without this the clip would jump to the top of the date-sorted list every time its trimmed in place.
                if (origLastWriteUtc.HasValue)
                {
                    try
                    {
                        if (origCreationUtc.HasValue) File.SetCreationTimeUtc(source.Full, origCreationUtc.Value);
                        File.SetLastWriteTimeUtc(source.Full, origLastWriteUtc.Value);
                        Log.Write("Trim overwrite: timestamps restored on " + source.Name);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        Log.Write("Trim overwrite: timestamp restore failed: " + ex.Message);
                    }
                }
            }

            AppConfig.ClearClipsCache();
            return new TrimResult
            {
                Ok = true,
                Name = outName,
                SourceName = source.Name,
                Mode = mode,
                Precise = precise,
                RemoveAudio = removeAudio,
                StartSec = startSec,
                EndSec = endSec,
                DurationSec = duration,
            };
        }

        // puts the clips file path onto the windows clipboard as CF_HDROP so it can be pasted as an actual file into discord / explorer / etc -- not the files bytes, the file reference (same thing ctrl+c on a file in explorer produces). the OLE call must run STA; connections are handled on MTA thread-pool threads now, so hop to a one-shot STA thread via StaRunner.
        public static TrimResult SetFileClipboard(string sourceName)
        {
            var source = Clips.GetSafeClipPath(sourceName);
            if (source == null || !File.Exists(source.Full)) return new TrimResult { Ok = false, Message = "Source clip not found" };
            try
            {
                var col = new System.Collections.Specialized.StringCollection();
                col.Add(source.Full);
                StaRunner.Run(() => System.Windows.Forms.Clipboard.SetFileDropList(col));
                return new TrimResult { Ok = true, Name = source.Name };
            }
            catch (Exception ex)
            {
                return new TrimResult { Ok = false, Message = "Clipboard copy failed: " + ex.Message };
            }
        }
    }
}

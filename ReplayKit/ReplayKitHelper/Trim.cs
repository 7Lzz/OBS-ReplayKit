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
        public bool Cancelled;
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

    // local file edits on clips: stream-copy trim and verbatim duplicate, plus the ffprobe keyframe scan the trim ui uses for snap points. both trim operations leave the source untouched by defualt; an explicit "overwrite" mode atomically replaces the source via a sibling temp file + atomic replace, so a crashed ffmpeg can never leave the original half-written. the keyframe scan is TrimKeyframesWorker.cs, run as an in-process task (see GetTrimKeyframeWorkerResult).
    internal static class Trim
    {
        private const int TrimKeyframeWorkerTimeoutMs = 300000;

        // probe the sources video bitrate in bits/sec. used by the precise trim branch to cap libx264s output so an already-compressed source cant inflate during re-encode (crf 14 alone faithfully reproduces blocking/banding from a low-bitrate input, costing more bits than the source carried -- the maxrate cap stops that). strategy: ask ffprobe for the v:0 streams bit_rate tag (cheapest, most mp4s from obs carry it). falls back to (filesize_bytes * 8) / duration_sec, knocking 10% off to ballpark the video portion (audio is typically a few hundred kbps of the total). returns 0 if both fail; callers should skip the cap in that case rather than guess.
        public static long GetVideoBitratePerSec(string path)
        {
            var caps = Compression.GetToolPaths();
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
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception || ex is OperationCanceledException || ex is TimeoutException)
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

        // the keyframes of a clip are kept in a file of the cache as well as in memory, so a scan that takes seconds on a long clip is not made again after the helper was restarted
        private static string KeyframeFile(string cacheKey, string cacheSig)
        {
            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(cacheKey + "|" + cacheSig + "|keyframes"));
                return Path.Combine(ClipCache.KindDir("keyframes"), string.Concat(hash.Take(8).Select(b => b.ToString("x2"))) + ".json");
            }
        }

        private static void WriteKeyframeFile(string cacheKey, string cacheSig, KeyframeScanResult result)
        {
            if (result == null || !result.Ok || result.Keyframes == null || result.Keyframes.Count == 0) return;
            try
            {
                string path = KeyframeFile(cacheKey, cacheSig);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                ClipCache.Prune(path);
                // the times are written as they were read, since a cut that starts a hair before a keyframe lands on the one before it
                var json = new JObject { ["sig"] = cacheSig, ["name"] = result.Name, ["method"] = result.Method, ["probeMs"] = result.ProbeMs, ["keyframes"] = new JArray(result.Keyframes) };
                AppConfig.WriteUtf8(path, json.ToString(Newtonsoft.Json.Formatting.None));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Log.Write("keyframe cache not written: " + ex.Message); }
        }

        private static KeyframeScanResult ReadKeyframeFile(string cacheKey, string cacheSig)
        {
            try
            {
                string path = KeyframeFile(cacheKey, cacheSig);
                if (!File.Exists(path)) return null;
                var json = JObject.Parse(File.ReadAllText(path));
                if (json.Value<string>("sig") != cacheSig) return null;
                var list = (json["keyframes"] as JArray)?.Select(t => t.Value<double>()).ToList();
                if (list == null || list.Count == 0) return null;
                ClipCache.Touch(path);
                return new KeyframeScanResult { Ok = true, Name = json.Value<string>("name"), Keyframes = list, Count = list.Count, Method = json.Value<string>("method"), ProbeMs = json.Value<int?>("probeMs") ?? 0 };
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is Newtonsoft.Json.JsonException || ex is FormatException) { return null; }
        }

        private static void SaveTrimKeyframeCache(string cacheKey, string cacheSig, KeyframeScanResult result, bool persist = true)
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
            if (persist) WriteKeyframeFile(cacheKey, cacheSig, result);
        }

        private static KeyframeScanResult GetTrimKeyframeCache(string cacheKey, string cacheSig)
        {
            lock (Server.State.TrimKeyframeCacheLock)
            {
                if (Server.State.TrimKeyframeCache.TryGetValue(cacheKey, out var entry) && entry.Sig == cacheSig && entry.Result != null &&
                    (entry.Result.Ok || (DateTime.UtcNow - entry.At).TotalSeconds < 10))
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
            var stored = ReadKeyframeFile(cacheKey, cacheSig);
            if (stored == null) return null;
            SaveTrimKeyframeCache(cacheKey, cacheSig, stored, false);
            return GetTrimKeyframeCache(cacheKey, cacheSig);
        }

        // polls an in-flight keyframe scan; TrimKeyframesWorker.Run always returns a result object, so a finished task is either a result or a timeout
        private static KeyframeScanResult GetTrimKeyframeWorkerResult(TrimKeyframeJob job)
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
                }
                result.Pending = false;
                return result;
            }

            double ageMs = (DateTime.UtcNow - job.StartedAt).TotalMilliseconds;
            if (ageMs > TrimKeyframeWorkerTimeoutMs)
            {
                try { job.Cts.Cancel(); } catch (ObjectDisposedException) { }
                return new KeyframeScanResult { Ok = false, Pending = false, Message = "Keyframe scan timed out; using normal fast trim" };
            }

            return new KeyframeScanResult { Ok = false, Pending = true, Message = "Keyframe snap points are still loading", RetryMs = 500 };
        }

        // caller must already hold TrimKeyframeJobsLock -- keeps "is a job already running" and "register a new one" one atomic step, so a background pre-warm and a users own trim-modal open racing on the same clip can never both see "no job yet" and each start their own ffprobe for the same file.
        private static TrimKeyframeJob StartTrimKeyframeWorkerLocked(Clips.SafeClipPath source, string ffprobe, string jobKey, string cacheSig)
        {
            var cts = new CancellationTokenSource(TrimKeyframeWorkerTimeoutMs);
            var job = new TrimKeyframeJob { Key = jobKey, Sig = cacheSig, StartedAt = DateTime.UtcNow, Cts = cts };
            job.Task = Task.Run(() =>
            {
                var result = TrimKeyframesWorker.Run(ffprobe, source.Full, source.Name, cts.Token);
                if (string.IsNullOrWhiteSpace(result.Name)) result.Name = source.Name;
                SaveTrimKeyframeCache(source.Full.ToLowerInvariant(), cacheSig, result);
                return result;
            });
            Server.State.TrimKeyframeJobs[jobKey] = job;
            job.Task.ContinueWith(done =>
            {
                try
                {
                    if (done.IsFaulted) Program.WriteCrashReport("keyframe_worker", done.Exception, false);
                }
                finally
                {
                    lock (Server.State.TrimKeyframeJobsLock) { Server.State.TrimKeyframeJobs.Remove(jobKey); }
                    cts.Dispose();
                }
            }, TaskScheduler.Default);
            return job;
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
            bool startedNew = false;
            lock (Server.State.TrimKeyframeJobsLock)
            {
                if (!Server.State.TrimKeyframeJobs.TryGetValue(jobKey, out job))
                {
                    if (Server.State.TrimKeyframeJobs.Count >= 2)
                        return new KeyframeScanResult { Ok = false, Pending = true, Message = "Keyframe scans are busy", RetryMs = 500 };
                    string ffprobe = Compression.GetToolPaths()["ffprobe"]?.Value<string>();
                    if (string.IsNullOrWhiteSpace(ffprobe) || !File.Exists(ffprobe))
                        return new KeyframeScanResult { Ok = false, Message = "ffprobe.exe not found in clip folder" };
                    job = StartTrimKeyframeWorkerLocked(source, ffprobe, jobKey, cacheSig);
                    startedNew = true;
                }
            }
            if (startedNew) return new KeyframeScanResult { Ok = false, Pending = true, Message = "Keyframe snap points are loading", RetryMs = 500 };
            return GetTrimKeyframeWorkerResult(job);
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

        // the video encoder of a save that makes the video again: its ffmpeg name, the arguments that go with it and whether it runs on the graphics card
        private sealed class EditEncoder
        {
            public string Name;
            public List<string> Args;
            public bool Hardware;
        }

        // libx264 is what these saves always used, and it stays the answer whenever the card cannot do better
        private static EditEncoder SoftwareEditEncoder() => new EditEncoder { Name = "libx264", Args = new List<string> { "-c:v", "libx264", "-preset", "fast", "-crf", "14" } };

        // the codec comes from the recording codec set in replaykit, resolved by the same table the recorder uses; an nvidia card encodes it when the bundled ffmpeg has really started that encoder in the capability probe, and every other case is libx264 as before, so a pc without a usable nvidia card saves exactly as it always did
        private static EditEncoder PickEditEncoder(string ext)
        {
            if (!ReplaykitSettings.GetTrimGpuEncode())
            {
                Log.Write("Trim encoder: libx264, graphics card encoding is switched off");
                return SoftwareEditEncoder();
            }
            var gpu = Gpu.PrimaryGpu();
            var choice = Encoder.PickEncoder(gpu, ReplaykitSettings.GetCodecPreference(), 22);
            if (choice.Backend != "nvenc")
            {
                Log.Write("Trim encoder: libx264, the recording encoder is " + choice.Label + " and only nvenc is used for saves");
                return SoftwareEditEncoder();
            }
            string name = choice.Codec == "h265" ? "hevc_nvenc" : "h264_nvenc";
            if (Compression.GetHelperCapabilities()["encoders"]?[name]?.Value<bool>() != true)
            {
                Log.Write("Trim encoder: libx264, " + name + " did not start in the capability probe");
                return SoftwareEditEncoder();
            }
            var args = new List<string> { "-c:v", name, "-preset", "p4", "-tune", "hq", "-rc", "vbr", "-cq", "16", "-b:v", "0", "-spatial-aq", "1", "-pix_fmt", "yuv420p" };
            if (choice.Codec == "h265")
            {
                // apple players only accept hevc in mp4 and mov under the hvc1 tag while ffmpeg writes hev1 by default; matroska has no such tag
                if (ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase) || ext.Equals(".mov", StringComparison.OrdinalIgnoreCase)) args.AddRange(new[] { "-tag:v", "hvc1" });
            }
            else
            {
                args.AddRange(new[] { "-profile:v", "high" });
            }
            Log.Write("Trim encoder: " + name + " on " + (gpu?.Label ?? "the graphics card") + ", recording codec " + choice.Codec);
            return new EditEncoder { Name = name, Args = args, Hardware = true };
        }

        // the names of the audio tracks of a clip recorded with separate audio tracks: the mix first, then the tracks in the order obs writes them
        internal static readonly string[] AudioTrackLabels = { "All audio", "PC audio", "Discord", "Microphone" };

        internal static string AudioTrackLabel(int index) => index >= 0 && index < AudioTrackLabels.Length ? AudioTrackLabels[index] : "Track " + (index + 1);

        // the audio part of a trim command that has no plan: a clip with one audio track is left to ffmpeg as before and a clip with several keeps them all; a video at another speed takes its audio along, which cannot be a stream copy
        private static void AddAudioArgs(List<string> argv, int audioCount, bool removeAudio, double videoSpeed = 1)
        {
            if (removeAudio) { argv.Add("-an"); return; }
            if (audioCount > 1) argv.AddRange(new[] { "-map", "0:v:0", "-map", "0:a" });
            if (audioCount > 0 && Math.Abs(videoSpeed - 1) > 1e-6)
            {
                argv.AddRange(new[] { "-af", AudioPlan.TempoChain(videoSpeed).Substring(1), "-c:a", "aac", "-b:a", "160k" });
                return;
            }
            argv.AddRange(new[] { "-c:a", "copy" });
        }

        // what ffprobe says about the video of a clip, which the black parts of a saved copy have to match; null when it cannot be read
        internal static VideoPlan.SourceInfo ProbeVideo(string path)
        {
            string ffprobe = Compression.GetToolPaths()["ffprobe"]?.Value<string>() ?? "";
            if (string.IsNullOrWhiteSpace(ffprobe) || !File.Exists(ffprobe)) return null;
            try
            {
                var r = Compression.InvokeNativeCapture(ffprobe, new[]
                {
                    "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height,avg_frame_rate,r_frame_rate:format=duration",
                    "-of", "json", path,
                });
                if (r.ExitCode != 0) return null;
                var json = JObject.Parse(string.Join("\n", r.Output));
                var stream = (json["streams"] as JArray)?.FirstOrDefault() as JObject;
                if (stream == null) return null;
                int width = stream.Value<int?>("width") ?? 0;
                int height = stream.Value<int?>("height") ?? 0;
                string fps = ValidFrameRate(stream.Value<string>("avg_frame_rate")) ?? ValidFrameRate(stream.Value<string>("r_frame_rate"));
                double.TryParse(json["format"]?.Value<string>("duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out double duration);
                if (width < 16 || height < 16 || fps == null || !(duration > 0)) return null;
                return new VideoPlan.SourceInfo { Width = width, Height = height, Fps = fps, Duration = duration };
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception || ex is OperationCanceledException || ex is TimeoutException || ex is Newtonsoft.Json.JsonException)
            {
                Log.Write("ProbeVideo failed: " + ex.Message);
                return null;
            }
        }

        // a frame rate the way ffprobe writes it, a ratio of two whole numbers; null when it is not one or is not a rate a video has
        private static string ValidFrameRate(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(raw, @"^(\d{1,6})/(\d{1,6})$");
            if (!m.Success) return null;
            double num = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            double den = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (num <= 0 || den <= 0) return null;
            double rate = num / den;
            return rate >= 1 && rate <= 240 ? raw : null;
        }

        // the video of a saved copy that plays at another speed: every frame keeps its place relative to the start and the timestamps are stretched, which needs the video to be re-encoded
        private static string SpeedFilter(double videoSpeed) => "setpts=(PTS-STARTPTS)/" + videoSpeed.ToString("0.######", CultureInfo.InvariantCulture);

        // the audio part of a trim command that has a plan: the audio is cut together by the filter graph from a second read of the source, which is not seeked, so the plan can use any time of the source; the video comes from the first read, and when it plays at another speed its filter is part of the same graph
        private static void AddPlanArgs(List<string> argv, AudioPlan.Rendering rendering, double videoSpeed, string videoTail)
        {
            string chain = VideoChain(videoSpeed, videoTail);
            if (rendering == null)
            {
                argv.AddRange(new[] { "-map", "0:v:0" });
                if (chain != null) argv.AddRange(new[] { "-vf", chain });
                argv.Add("-an");
                return;
            }
            string graph = chain != null ? "[0:v:0]" + chain + "[vout];" + rendering.Graph : rendering.Graph;
            argv.AddRange(new[] { "-filter_complex", graph, "-map", chain != null ? "[vout]" : "0:v:0" });
            foreach (var map in rendering.Maps) argv.AddRange(new[] { "-map", map });
            argv.AddRange(new[] { "-c:a", "aac", "-b:a", "160k" });
            for (int k = 0; k < rendering.Names.Count; k++) argv.AddRange(new[] { "-metadata:s:a:" + k, "handler_name=" + rendering.Names[k] });
        }

        // the filters the video of a plain or planned save goes through: its speed first, then whatever is drawn over it; null when there are none
        private static string VideoChain(double videoSpeed, string videoTail)
        {
            var parts = new List<string>();
            if (Math.Abs(videoSpeed - 1) > 1e-6) parts.Add(SpeedFilter(videoSpeed));
            if (videoTail != null) parts.Add(videoTail);
            return parts.Count > 0 ? string.Join(",", parts) : null;
        }

        // the reads a command with a plan makes after the first read of the source: the source once more for its audio, unless the video graph reads it already or the clip has none, and then each file that was brought in from outside the clip
        private static List<string> PlanReads(bool composed, int audioCount, string sourcePath, List<string> importArgs)
        {
            var reads = new List<string>();
            if (!composed && audioCount > 0) reads.AddRange(new[] { "-i", sourcePath });
            reads.AddRange(importArgs);
            return reads;
        }

        // the parts of a trim command that has video pieces: the video and the audio are both put together by the filter graph from one read of the source, so a piece may start anywhere on the timeline, and nothing is written for a clip without audio
        // what follows the video as it was put together, in this order: the look, the layers over it, then the subtitles over everything; null with [vout] as the label when there is nothing
        private static string FinishGraph(string look, LayerPlan layers, int firstLayerInput, VideoPlan.SourceInfo info, string subtitles, out string label)
        {
            var steps = new List<string>();
            label = "[vout]";
            if (look != null) { steps.Add(label + look + "[vlook]"); label = "[vlook]"; }
            if (layers != null) { steps.Add(layers.Graph(firstLayerInput, label, "[vlayers]", info.Width, info.Height)); label = "[vlayers]"; }
            if (subtitles != null) { steps.Add(label + subtitles + "[vfinal]"); label = "[vfinal]"; }
            return steps.Count > 0 ? string.Join(";", steps) : null;
        }

        private static void AddComposedArgs(List<string> argv, string videoGraph, AudioPlan.Rendering rendering, string finish, string finalLabel)
        {
            if (finish != null) videoGraph += ";" + finish;
            string graph = rendering != null ? videoGraph + ";" + rendering.Graph : videoGraph;
            argv.AddRange(new[] { "-filter_complex", graph, "-map", finalLabel });
            if (rendering == null)
            {
                argv.Add("-an");
                return;
            }
            foreach (var map in rendering.Maps) argv.AddRange(new[] { "-map", map });
            argv.AddRange(new[] { "-c:a", "aac", "-b:a", "160k" });
            for (int k = 0; k < rendering.Names.Count; k++) argv.AddRange(new[] { "-metadata:s:a:" + k, "handler_name=" + rendering.Names[k] });
        }

        // trim a clip with ffmpeg. two precision modes: precise=true (defualt): decode-then-seek (-ss after -i) + libx264 crf 14 re-encode. cut lands frame-accurate at the picked time; encode takes roughly clip-length seconds at the veryfast preset. precise=false: stream-copy with input-side seek. lossless and near-instant, but the cut snaps to the previous keyframe (every 1-2s for obs replay-buffer recordings). mode is copy (defualt; new file) or overwrite (replaces the source in place via a temp sibling + atomic replace). removeAudio drops all audio streams from the edited output.
        public static TrimResult InvokeClipTrim(string sourceName, double startSec, double endSec, string mode = "copy", bool precise = true, bool removeAudio = false, JToken audioEdit = null, double videoSpeed = 1, JToken videoEdit = null, JToken subtitleEdit = null, JToken lookEdit = null, JToken layerEdit = null)
        {
            // the editor offers a tenth of normal speed to five times normal
            if (double.IsNaN(videoSpeed) || double.IsInfinity(videoSpeed) || videoSpeed < 0.1 || videoSpeed > 5)
                return new TrimResult { Ok = false, Message = "Bad speed" };
            // a video that plays at another speed is made again frame by frame, so it is never a stream copy
            bool retimed = Math.Abs(videoSpeed - 1) > 1e-6;
            // video pieces with black between them are put together frame by frame as well
            bool composed = videoEdit != null;
            // subtitles are drawn into the frames, so they are made again as well
            bool subtitled = subtitleEdit != null;
            // and so is a look, which changes every pixel
            bool looked = lookEdit != null;
            // and so are layers, which only the graph that puts the video together lays on
            bool layered = layerEdit != null;
            if (layered && !composed) return new TrimResult { Ok = false, Message = "Layers need the video of the edit to be put together" };
            if (retimed || composed || subtitled || looked || layered) precise = true;
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
            string requestId = UploadState.NewRequestId();
            using var reservation = JobCoordinator.TryReserve(requestId, source.Name, source.Full, "trim", out string busy);
            if (reservation == null) return new TrimResult { Ok = false, Message = busy };
            var token = JobCoordinator.Token(requestId);


            string ffmpeg = Compression.GetToolPaths()["ffmpeg"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg)) return new TrimResult { Ok = false, Message = "FFmpeg is missing or unavailable. Use Settings > Check for Updates or run the ReplayKit installer again to repair the video tools." };

            string ext = Path.GetExtension(source.Name);
            // for overwrite mode, snapshot the sources filesystem timestamps now (before the atomic replace destroys them) so they can be restored after the encode -- same rationale as the compress-overwrite path: keeps the clip in its original sort position rather than jumping to the top of the dock list every time its trimmed.
            long originalSize = 0;
            DateTime? origLastWriteUtc = null;
            DateTime? origCreationUtc = null;
            if (overwrite)
            {
                try
                {
                    var srcInfo = new FileInfo(source.Full);
                    if (srcInfo.Exists)
                    {
                        originalSize = srcInfo.Length;
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

            int audioCount = Mp4Tracks.CountAudio(source.Full);
            // the audio edits of the editor: a plan that leaves nothing audible writes no audio at all, and a plan that is out of range stops the trim before anything is made
            AudioPlan.Rendering planned = null;
            // a plan can also bring in audio files from outside the clip, which a clip without audio takes as well
            bool bringsIn = (((audioEdit as JObject)?["imports"] as JArray)?.Count ?? 0) > 0;
            // a clip whose boxes cannot be read may hold audio that is not seen, which a plan would leave out without a word
            if (bringsIn && !removeAudio && audioCount == 0 && !Mp4Tracks.IsReadable(source.Full)) return new TrimResult { Ok = false, Message = "Audio cannot be added to this kind of clip." };
            bool hasPlan = audioEdit != null && !removeAudio && (audioCount > 0 || bringsIn);
            var importArgs = new List<string>();
            // the saved clip is the part of the timeline between these two times; with video pieces the editor says which part, and it may reach past the video on either side
            double windowStart = startSec / videoSpeed;
            double windowEnd = endSec / videoSpeed;
            VideoPlan videoPlan = null;
            VideoPlan.SourceInfo sourceInfo = null;
            if (composed)
            {
                // the audio of a clip that has some is part of the same graph, so an edit that leaves it out would drop it without a word
                if (audioCount > 0 && !removeAudio && audioEdit == null) return new TrimResult { Ok = false, Message = "The audio of this clip was not prepared, so the video cannot be edited with it. Turn on Remove audio to edit the video alone." };
                sourceInfo = ProbeVideo(source.Full);
                if (sourceInfo == null) return new TrimResult { Ok = false, Message = "The video of the clip could not be read" };
                videoPlan = VideoPlan.Parse(videoEdit, videoSpeed, sourceInfo.Duration, out string videoError);
                if (videoPlan == null) return new TrimResult { Ok = false, Message = videoError };
                windowStart = videoPlan.Start;
                windowEnd = videoPlan.End;
            }
            if (hasPlan)
            {
                var plan = AudioPlan.Parse(audioEdit, audioCount, out string planError);
                if (plan == null) return new TrimResult { Ok = false, Message = planError };
                // each file brought in is a read of its own after the reads of the source, and a file that left the cache since it was added stops the trim before anything is made
                var importInputs = new Dictionary<int, int>();
                int nextInput = composed ? 1 : audioCount > 0 ? 2 : 1;
                foreach (var import in plan.Imports)
                {
                    string importFile = AudioImports.PathFor(import.Id);
                    if (importFile == null) return new TrimResult { Ok = false, Message = "An audio file added in the editor is no longer available. Add it again." };
                    importArgs.AddRange(new[] { "-i", importFile });
                    importInputs[import.Src] = nextInput++;
                }
                // the plan lives on the timeline of the saved clip, which is the source divided by the speed of the video
                planned = plan.Build(windowStart, windowEnd, composed ? 0 : 1, Mp4Tracks.AudioSampleRates(source.Full), importInputs);
            }
            // the subtitles live in seconds of the saved clip, and the filter sees the frames in the time of the path that made them: from 0 when the video is put together, retimed or read seeked, and in the time of the source on the plain cut, which seeks after reading (an input seek there would start the video a moment after the copied audio), so those cues move by the start of the cut; measured on rendered frames. an edit whose subtitles all fall outside the clip burns nothing
            string subtitleDir = null;
            string videoTail = null;
            string subtitleFilter = null;
            LayerPlan layerPlan = null;
            if (layered)
            {
                layerPlan = LayerPlan.Parse(layerEdit, windowEnd - windowStart, out string layerError);
                if (layerPlan == null) return new TrimResult { Ok = false, Message = layerError };
            }
            // the look goes on before the subtitles, which are drawn over the picture as it ends up
            string lookFilter = null;
            if (looked)
            {
                var lookPlan = VideoLook.Parse(lookEdit, out string lookError);
                if (lookPlan == null) return new TrimResult { Ok = false, Message = lookError };
                sourceInfo ??= ProbeVideo(source.Full);
                if (sourceInfo == null) return new TrimResult { Ok = false, Message = "The video of the clip could not be read" };
                lookFilter = lookPlan.Filter(sourceInfo.Width, sourceInfo.Height);
            }
            if (subtitled)
            {
                var burn = SubtitleBurn.Parse(subtitleEdit, composed ? windowEnd - windowStart : duration / videoSpeed, out string subtitleError);
                if (burn == null) return new TrimResult { Ok = false, Message = subtitleError };
                if (burn.Count > 0)
                {
                    var size = sourceInfo ?? ProbeVideo(source.Full);
                    subtitleDir = Path.Combine(Constants.SCRATCH_DIR, "subs-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(subtitleDir);
                    double sourceTimed = !composed && !hasPlan && !retimed ? startSec : 0;
                    // the folder is only cleaned by the finally further down, which a failed write never reaches
                    try { burn.Write(subtitleDir, size?.Width ?? 0, size?.Height ?? 0, sourceTimed); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
                    {
                        try { Directory.Delete(subtitleDir, true); } catch (Exception cleanup) when (cleanup is IOException || cleanup is UnauthorizedAccessException) { Log.Write("Trim subtitles cleanup: " + cleanup.Message); }
                        throw;
                    }
                    videoTail = burn.Filter;
                    subtitleFilter = burn.Filter;
                }
            }
            if (lookFilter != null) videoTail = videoTail != null ? lookFilter + "," + videoTail : lookFilter;
            try
            {
            // invariant-culture decimal formatting -- ffmpeg only accepts . as the decimal separator, never ,, regardless of system locale.
            string startStr = startSec.ToString("F3", CultureInfo.InvariantCulture);
            string durStr = duration.ToString("F3", CultureInfo.InvariantCulture);
            // how long the saved copy runs: the kept part of the source, stretched by the speed
            string outDurStr = (composed ? windowEnd - windowStart : duration / videoSpeed).ToString("F3", CultureInfo.InvariantCulture);
            List<string> argv;
            EditEncoder encoder = null;
            int firstLayerInput = 0;
            int firstOtherInput = 0;

            if (precise)
            {
                // ss after -i is output-side / decode-then-seek: ffmpeg decodes frames from the start of the file but discards everything before startSec, then emits exactly the picked window -- the only way to land the cut on the exact frame chosen, at the cost of a full re-encode. on the processor crf 14 is the perceptual quality target -- visually indistinguishable from the source; the graphics card encoder works to a constant quality of its own, picked by measuring against it. on a fresh obs clip (30-60 mbps) crf 14 lands around 70-85% of source size; on a heavily compressed source it would happily exceed the source bitrate trying to preserve compression artifacts, so the sources video bitrate is probed and passed as -maxrate / -bufsize -- x264 in "capped crf" mode still targets crf, but rate-control caps the output when reaching it would spend more bits than the source had. trims of original clips look the same as before; trims of compressed clips no longer inflate. audio is stream-copied so the source aac bytes are preserved exactly, no second-generation transcoding loss. preset is "fast" rather than "veryfast" -- veryfast at low bitrates throws away bit-allocation efficiency the cap depends on, and the wall-clock difference for a typical trim is small enough to be invisible. with video pieces nothing is seeked, since the graph cuts every piece out of the source by its own times; with a plan the video is read seeked and the audio graph reads the file again from its start, so the plan keeps working on the timeline of the source, and decoding a seeked input to a re-encode is exact to the frame as well; a video at another speed is read seeked as well, since the output side seek of ffmpeg counts the time after the filters have stretched it
                if (composed)
                {
                    argv = new List<string> { "-hide_banner", "-loglevel", "error", "-i", source.Full };
                    argv.AddRange(PlanReads(composed, audioCount, source.Full, importArgs));
                    if (layerPlan != null)
                    {
                        firstLayerInput = argv.Count(a => a == "-i");
                        argv.AddRange(layerPlan.Inputs());
                    }
                    // the other clips that pieces of the video come from, read once each
                    firstOtherInput = argv.Count(a => a == "-i");
                    foreach (string other in videoPlan.OtherClips) argv.AddRange(new[] { "-i", other });
                    argv.AddRange(new[] { "-t", outDurStr });
                }
                else if (hasPlan)
                {
                    argv = new List<string> { "-hide_banner", "-loglevel", "error", "-ss", startStr, "-i", source.Full };
                    argv.AddRange(PlanReads(composed, audioCount, source.Full, importArgs));
                    argv.AddRange(new[] { "-t", outDurStr });
                }
                else if (retimed)
                    argv = new List<string> { "-hide_banner", "-loglevel", "error", "-ss", startStr, "-i", source.Full, "-t", outDurStr };
                else
                    argv = new List<string> { "-hide_banner", "-loglevel", "error", "-i", source.Full, "-ss", startStr, "-t", durStr };
                encoder = PickEditEncoder(ext);
                argv.AddRange(encoder.Args);
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
                if (composed)
                {
                    AddComposedArgs(argv, videoPlan.BuildGraph(0, firstOtherInput, videoSpeed, sourceInfo), planned, FinishGraph(lookFilter, layerPlan, firstLayerInput, sourceInfo, subtitleFilter, out string finalLabel), finalLabel);
                }
                else if (hasPlan)
                {
                    AddPlanArgs(argv, planned, videoSpeed, videoTail);
                }
                else
                {
                    string chain = VideoChain(videoSpeed, videoTail);
                    if (chain != null) argv.AddRange(new[] { "-vf", chain });
                    AddAudioArgs(argv, audioCount, removeAudio, videoSpeed);
                }
                argv.AddRange(new[] { "-avoid_negative_ts", "make_zero", "-movflags", "+faststart", "-y", outPath });
            }
            else
            {
                // ss before -i is input-side / fast-seek: ffmpeg jumps to the nearest keyframe at-or-before startSec without decoding the preceding frames. combined with -c copy this is a remux only -- lossless, finishes in well under a second, at the cost of snap-to-keyframe imprecision.
                argv = new List<string> { "-hide_banner", "-loglevel", "error", "-ss", startStr, "-i", source.Full };
                if (hasPlan) argv.AddRange(PlanReads(composed, audioCount, source.Full, importArgs));
                argv.AddRange(new[] { "-t", durStr, "-c:v", "copy" });
                if (hasPlan) AddPlanArgs(argv, planned, videoSpeed, null); else AddAudioArgs(argv, audioCount, removeAudio);
                argv.AddRange(new[] { "-avoid_negative_ts", "make_zero", "-y", outPath });
            }

            // ffmpeg writes how far it has come as out_time_us; against the length of the saved clip that is the percent the editor shows while it waits
            double outputUs = Math.Max(1.0, (composed ? windowEnd - windowStart : duration / videoSpeed) * 1000000.0);
            int lastPercent = 0;
            argv.InsertRange(3, new[] { "-progress", "pipe:1", "-nostats" });
            UploadState.SetUploadState(requestId: requestId, phase: "encoding");
            Log.Write("Trim (" + mode + ", precise=" + precise + ", removeAudio=" + removeAudio + "): " + ffmpeg + " " + string.Join(" ", argv));
            try
            {
                var result = Compression.InvokeNativeProgress(ffmpeg, argv, line =>
                {
                    if (!line.StartsWith("out_time_us=", StringComparison.Ordinal)) return;
                    if (!double.TryParse(line.Substring(12), NumberStyles.Float, CultureInfo.InvariantCulture, out double us) || us <= 0) return;
                    int percent = (int)Math.Floor(2 + 94.0 * Math.Min(1.0, us / outputUs));
                    if (percent == lastPercent) return;
                    lastPercent = percent;
                    UploadState.SetUploadState(requestId: requestId, phase: "encoding", percent: percent);
                }, token, 2 * 60 * 60 * 1000, subtitleDir);
                if (result.ExitCode != 0)
                {
                    string combined = string.Join("\n", result.Output);
                    string msg = combined.Length > 400 ? combined.Substring(0, 400) + "..." : combined;
                    try { if (File.Exists(outPath)) File.Delete(outPath); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                    // a card that cannot run the encoder fails here with the message of ffmpeg, and the processor only takes over when the switch in the settings is turned off
                    string hint = encoder != null && encoder.Hardware ? " (encoded by " + encoder.Name + ", Settings > Video Editor can switch edits to the processor)" : "";
                    return new TrimResult { Ok = false, Message = "ffmpeg trim failed (exit=" + result.ExitCode + "): " + msg + hint };
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                try { if (File.Exists(outPath)) File.Delete(outPath); } catch (Exception ex2) when (ex2 is IOException || ex2 is UnauthorizedAccessException) { }
                return new TrimResult { Ok = false, Cancelled = true, Message = "Cancelled" };
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception || ex is OperationCanceledException || ex is TimeoutException)
            {
                try { if (File.Exists(outPath)) File.Delete(outPath); } catch (Exception ex2) when (ex2 is IOException || ex2 is UnauthorizedAccessException) { }
                return new TrimResult { Ok = false, Message = "Trim failed: " + ex.Message };
            }

            if (!File.Exists(outPath) || new FileInfo(outPath).Length == 0) return new TrimResult { Ok = false, Message = "ffmpeg reported success but the output file is missing or empty" };

            // from here the file is put in place, which the editor shows as the last step
            UploadState.SetUploadState(requestId: requestId, phase: "finishing", percent: 98);

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
                    JobCoordinator.Commit(requestId, () => File.Move(tempPath, finalPath));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch (Exception ex2) when (ex2 is IOException || ex2 is UnauthorizedAccessException) { }
                    return new TrimResult { Ok = false, Cancelled = true, Message = "Cancelled" };
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
                    ClipFiles.Replace(requestId, tempPath, source.Full, originalSize, origLastWriteUtc.GetValueOrDefault());
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch (Exception ex2) when (ex2 is IOException || ex2 is UnauthorizedAccessException) { }
                    return new TrimResult { Ok = false, Cancelled = true, Message = "Cancelled" };
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception || ex is OperationCanceledException || ex is TimeoutException)
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

            // the clip under this name is another file now, so an edit that was kept for the old one fits nothing any more
            if (overwrite) EditDrafts.Forget(source.Full);
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
                DurationSec = composed ? windowEnd - windowStart : duration,
            };
            }
            finally
            {
                if (subtitleDir != null)
                {
                    try { Directory.Delete(subtitleDir, true); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Log.Write("Trim subtitles cleanup: " + ex.Message); }
                }
            }
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

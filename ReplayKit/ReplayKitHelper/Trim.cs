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
        public static string GetSuffixedOutputName(string sourceName, string suffix, string folder = null)
        {
            string baseName = Path.GetFileNameWithoutExtension(sourceName);
            string ext = Path.GetExtension(sourceName);
            string clipDir = folder ?? AppConfig.GetClipDir();
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

            // a bitrate picked in the render settings replaces the constant quality the encoder works to by default with that rate, held there by the rate control
            public void SetBitrate(int kbps)
            {
                string rate = kbps + "k", buffer = (kbps * 2) + "k";
                int crf = Args.IndexOf("-crf");
                if (crf >= 0) Args.RemoveRange(crf, 2);
                int cq = Args.IndexOf("-cq");
                if (cq >= 0) Args.RemoveRange(cq, 2);
                int bv = Args.IndexOf("-b:v");
                if (bv >= 0) Args.RemoveRange(bv, 2);
                Args.AddRange(new[] { "-b:v", rate, "-maxrate", rate, "-bufsize", buffer });
            }
        }

        // the render settings of the editor: the short side of the picture in pixels, the frame rate and the video bitrate in kbps, each 0 to keep what the video has
        public sealed class RenderOptions
        {
            public int Height;
            public int Fps;
            public int BitrateKbps;
            public bool Any => Height > 0 || Fps > 0 || BitrateKbps > 0;

            private static readonly int[] Heights = { 0, 720, 1080, 1440, 2160 };
            private static readonly int[] Rates = { 0, 24, 30, 60, 120, 144 };

            public static bool Valid(RenderOptions o) => o != null && Array.IndexOf(Heights, o.Height) >= 0 && Array.IndexOf(Rates, o.Fps) >= 0 && o.BitrateKbps >= 0 && o.BitrateKbps <= 200000;

            // the size a frame of width x height comes out at: its short side at Height, the long side following the shape, both even
            public (int W, int H) Size(int width, int height)
            {
                if (Height <= 0 || width <= 0 || height <= 0) return (width, height);
                double k = (double)Height / Math.Min(width, height);
                return (Math.Max(2, (int)Math.Round(width * k / 2) * 2), Math.Max(2, (int)Math.Round(height * k / 2) * 2));
            }

            // the last step of the picture: scaled to its size and brought to its frame rate; null when both are kept
            public string Filter(int width, int height)
            {
                var steps = new List<string>();
                if (Height > 0 && width > 0 && height > 0)
                {
                    var size = Size(width, height);
                    if (size.W != width || size.H != height) steps.Add("scale=" + size.W + ":" + size.H + ":flags=lanczos,setsar=1");
                }
                if (Fps > 0) steps.Add("fps=" + Fps);
                return steps.Count > 0 ? string.Join(",", steps) : null;
            }
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
                    "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height,avg_frame_rate,r_frame_rate,pix_fmt,sample_aspect_ratio,bit_rate:format=duration",
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
                long.TryParse(stream.Value<string>("bit_rate"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long bitrate);
                return new VideoPlan.SourceInfo
                {
                    Width = width, Height = height, Fps = fps, Duration = duration,
                    PixelFormat = stream.Value<string>("pix_fmt"), SampleAspect = stream.Value<string>("sample_aspect_ratio"),
                    ConstantFrameRate = ValidFrameRate(stream.Value<string>("avg_frame_rate")) == ValidFrameRate(stream.Value<string>("r_frame_rate")),
                    Bitrate = bitrate,
                };
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
        // the picture is cut to its aspect first, so the look, the layers and the subtitles are all laid out on the frame that is saved, the way the editor shows them; width and height are that frame
        private static string FinishGraph(string look, LayerPlan layers, int firstLayerInput, int width, int height, string subtitles, string frame, out string label, string output = null)
        {
            var steps = new List<string>();
            label = "[vout]";
            if (frame != null) { steps.Add(label + frame + "[vframe]"); label = "[vframe]"; }
            if (look != null) { steps.Add(label + look + "[vlook]"); label = "[vlook]"; }
            if (layers != null) { steps.Add(layers.Graph(firstLayerInput, label, "[vlayers]", width, height)); label = "[vlayers]"; }
            if (subtitles != null) { steps.Add(label + subtitles + "[vfinal]"); label = "[vfinal]"; }
            if (output != null) { steps.Add(label + output + "[vrender]"); label = "[vrender]"; }
            return steps.Count > 0 ? string.Join(";", steps) : null;
        }

        // the one frame at frameTime of the saved clip, put together the way the full render puts it but without the rest of the clip: the piece under it is read from half a second before it, on the same grid of frames, sped up and fitted the same way and placed at its time on the timeline, so the look, the layers that show then and the subtitles land on it as they do in a saved copy. a frame no piece covers is black. the whole edit up to the frame used to be decoded and filtered for this, which grew with how far into the clip the frame was
        private static List<string> ScreenshotArgs(VideoPlan plan, VideoPlan.SourceInfo info, double speed, double frameTime, string sourcePath, string look, LayerPlan layers, string subtitles, string frame, string aspect)
        {
            string N(double x) => x.ToString("0.######", CultureInfo.InvariantCulture);
            string[] ratio = info.Fps.Split('/');
            double rate = double.Parse(ratio[0], CultureInfo.InvariantCulture) / double.Parse(ratio[1], CultureInfo.InvariantCulture);
            double t = frameTime - plan.Start;
            var argv = new List<string> { "-hide_banner", "-loglevel", "error" };
            var piece = plan.Pieces.FirstOrDefault(p => frameTime >= Math.Max(p.At, plan.Start) - 1e-6 && frameTime < p.At + p.Length(speed));
            string video;
            int inputs = 0;
            if (piece != null)
            {
                double a = Math.Max(piece.At, plan.Start);
                double srcA = piece.SrcStart + (a - piece.At) * speed;
                // a whole number of frames after the start of the piece, so the frames fall where the full render puts them
                double k = Math.Max(0, Math.Floor((frameTime - a) * rate + 1e-6) - Math.Ceiling(0.5 * rate));
                double seekAt = a + k / rate;
                double seekSrc = srcA + (seekAt - a) * speed;
                argv.AddRange(new[] { "-ss", N(seekSrc), "-i", piece.Path ?? sourcePath });
                inputs = 1;
                string stretch = Math.Abs(speed - 1) > 1e-6 ? "setpts=(PTS-STARTPTS)/" + N(speed) : "setpts=PTS-STARTPTS";
                video = "[0:v:0]" + stretch + (piece.Path == null ? "" : VideoPlan.Fit(info)) + ",fps=" + info.Fps + ",setsar=1,format=yuv420p,setpts=PTS+" + N(seekAt - plan.Start) + "/TB[vout]";
            }
            else
            {
                video = "color=c=black:s=" + info.Width + "x" + info.Height + ":r=" + info.Fps + ":d=" + N(2 / rate) + ",setsar=1,format=yuv420p,setpts=PTS+" + N(t) + "/TB[vout]";
            }
            LayerPlan shown = layers?.AtFrame(t);
            if (shown != null && shown.Count > 0) argv.AddRange(shown.Inputs());
            else shown = null;
            var size = frame != null ? EditorFrame.Size(aspect, info.Width, info.Height) : (info.Width, info.Height);
            string finish = FinishGraph(look, shown, inputs, size.Item1, size.Item2, subtitles, frame, out string label);
            // the frames that lead up to it only settle the decoder and the filters; the first one from the frame on is the picture
            string graph = video + (finish != null ? ";" + finish : "") + ";" + label + "trim=start=" + N(Math.Max(0, t - 0.5 / rate)) + "[shot]";
            argv.AddRange(new[] { "-filter_complex", graph, "-map", "[shot]", "-an" });
            return argv;
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
        public static TrimResult InvokeClipTrim(string sourceName, double startSec, double endSec, string mode = "copy", bool precise = true, bool removeAudio = false, JToken audioEdit = null, double videoSpeed = 1, JToken videoEdit = null, JToken subtitleEdit = null, JToken lookEdit = null, JToken layerEdit = null, string aspect = "original", double frameTime = -1, RenderOptions render = null)
        {
            render ??= new RenderOptions();
            if (!RenderOptions.Valid(render)) return new TrimResult { Ok = false, Message = "Bad render settings" };
            if (!EditorFrame.Valid(aspect)) return new TrimResult { Ok = false, Message = "Unknown aspect ratio" };
            bool screenshot = string.Equals(mode, "screenshot", StringComparison.OrdinalIgnoreCase);
            bool framed = aspect != "original";
            if (screenshot)
            {
                if (double.IsNaN(frameTime) || double.IsInfinity(frameTime) || frameTime < 0 || videoEdit == null)
                    return new TrimResult { Ok = false, Message = "Bad screenshot time or video edit" };
                removeAudio = true;
                audioEdit = null;
            }
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
            // a picture of another size, rate or bitrate is made again as well
            if (retimed || composed || subtitled || looked || layered || framed || screenshot || render.Any) precise = true;
            if (double.IsNaN(startSec) || double.IsNaN(endSec) || double.IsInfinity(startSec) || double.IsInfinity(endSec))
                return new TrimResult { Ok = false, Message = "Bad start/end" };
            if (startSec < 0) return new TrimResult { Ok = false, Message = "Start must be >= 0" };
            if (endSec <= startSec) return new TrimResult { Ok = false, Message = "End must be greater than start" };
            double duration = endSec - startSec;
            if (duration < 0.5) return new TrimResult { Ok = false, Message = "Trim must be at least 0.5s long" };

            mode = !string.IsNullOrEmpty(mode) ? mode.ToLowerInvariant() : "copy";
            if (mode != "copy" && mode != "overwrite" && mode != "screenshot") return new TrimResult { Ok = false, Message = "Unknown trim mode '" + mode + "'" };
            bool overwrite = mode == "overwrite";

            var source = Clips.GetSafeClipPath(sourceName);
            if (source == null || !File.Exists(source.Full)) return new TrimResult { Ok = false, Message = "Source clip not found" };
            string requestId = UploadState.NewRequestId();
            using var reservation = JobCoordinator.TryReserve(requestId, source.Name, source.Full, "trim", out string busy);
            if (reservation == null) return new TrimResult { Ok = false, Message = busy };
            var token = JobCoordinator.Token(requestId);


            string ffmpeg = Compression.GetToolPaths()["ffmpeg"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg)) return new TrimResult { Ok = false, Message = "FFmpeg is missing or unavailable. Use Settings > Check for Updates or run the ReplayKit installer again to repair the video tools." };

            string ext = screenshot ? ".png" : Path.GetExtension(source.Name);
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
                string outputFolder = screenshot ? Screenshots.Folder(Path.GetDirectoryName(source.Full), create: true) : Path.GetDirectoryName(source.Full);
                string suffix = screenshot ? "screenshot" : precise ? "trimmed" : "trimmed (fast)";
                outName = GetSuffixedOutputName(screenshot ? Path.ChangeExtension(source.Name, ".png") : source.Name, suffix, outputFolder);
                if (outName == null) return new TrimResult { Ok = false, Message = "Too many existing trim outputs" };
                finalPath = Path.Combine(outputFolder, outName);
                tempPath = Path.Combine(outputFolder, "_replaykit_trim_copy_" + Guid.NewGuid().ToString("N") + ext);
                outPath = tempPath;
            }

            // a screenshot has no audio, so the audio of the clip is never read for one
            int audioCount = screenshot ? 0 : Mp4Tracks.CountAudio(source.Full);
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
            bool directVideo = false;
            double directReadStart = 0;
            double directStart = 0, directEnd = 0;
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
                // A single unchanged CFR video range can use the existing accurate input seek; the audio keeps its original timeline on its own input.
                if (!layered && !screenshot && videoSpeed == 1 && sourceInfo.PixelFormat == "yuv420p" &&
                    sourceInfo.SampleAspect == "1:1" && sourceInfo.ConstantFrameRate && videoPlan.TryDirectRange(out directStart, out directEnd) &&
                    Math.Abs(directStart - startSec) <= 0.000501 && Math.Abs(directEnd - endSec) <= 0.000501)
                {
                    composed = false;
                    directVideo = true;
                    directReadStart = Math.Max(0, directStart - 1);
                }
            }
            if (screenshot && (frameTime < windowStart || frameTime >= windowEnd))
                return new TrimResult { Ok = false, Message = "The screenshot time is outside the saved part of the video" };
            if (screenshot)
            {
                string[] rate = sourceInfo.Fps.Split('/');
                double fps = double.Parse(rate[0], CultureInfo.InvariantCulture) / double.Parse(rate[1], CultureInfo.InvariantCulture);
                double last = Math.Max(0, Math.Ceiling((windowEnd - windowStart) * fps) - 1);
                frameTime = windowStart + Math.Min(last, Math.Floor((frameTime - windowStart) * fps)) / fps;
            }
            string frameFilter = null;
            // the frame that is saved, which everything drawn over the picture is laid out on; the size of the video until it is cut to an aspect
            int frameW = sourceInfo?.Width ?? 0, frameH = sourceInfo?.Height ?? 0;
            if (framed)
            {
                sourceInfo ??= ProbeVideo(source.Full);
                if (sourceInfo == null) return new TrimResult { Ok = false, Message = "The video of the clip could not be read" };
                try
                {
                    frameFilter = EditorFrame.Filter(aspect, sourceInfo.Width, sourceInfo.Height);
                    (frameW, frameH) = EditorFrame.Size(aspect, sourceInfo.Width, sourceInfo.Height);
                }
                catch (ArgumentException ex) { return new TrimResult { Ok = false, Message = ex.Message }; }
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
                if (!framed) (frameW, frameH) = (sourceInfo.Width, sourceInfo.Height);
                lookFilter = lookPlan.Filter(frameW, frameH);
            }
            if (subtitled)
            {
                var burn = SubtitleBurn.Parse(subtitleEdit, videoPlan != null ? windowEnd - windowStart : duration / videoSpeed, out string subtitleError);
                if (burn == null) return new TrimResult { Ok = false, Message = subtitleError };
                if (burn.Count > 0)
                {
                    var size = sourceInfo ?? ProbeVideo(source.Full);
                    if (!framed && size != null) (frameW, frameH) = (size.Width, size.Height);
                    subtitleDir = Path.Combine(Constants.SCRATCH_DIR, "subs-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(subtitleDir);
                    double sourceTimed = !composed && !directVideo && !hasPlan && !retimed ? startSec : 0;
                    // the folder is only cleaned by the finally further down, which a failed write never reaches
                    try { burn.Write(subtitleDir, frameW, frameH, sourceTimed); }
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
            // the cut to the aspect comes first, the same as in the graph of a put together video
            if (!composed && frameFilter != null) videoTail = videoTail != null ? frameFilter + "," + videoTail : frameFilter;
            // and the size and rate of the render come last, on the finished picture
            string renderFilter = null;
            if (render.Any && !screenshot)
            {
                if (frameW <= 0 || frameH <= 0)
                {
                    sourceInfo ??= ProbeVideo(source.Full);
                    if (sourceInfo == null) return new TrimResult { Ok = false, Message = "The video of the clip could not be read" };
                    (frameW, frameH) = (sourceInfo.Width, sourceInfo.Height);
                }
                renderFilter = render.Filter(frameW, frameH);
                if (!composed && renderFilter != null) videoTail = videoTail != null ? videoTail + "," + renderFilter : renderFilter;
            }
            if (directVideo)
            {
                string normalize = "trim=start=" + (directStart - directReadStart).ToString("0.######", CultureInfo.InvariantCulture) +
                    ":end=" + (directEnd - directReadStart).ToString("0.######", CultureInfo.InvariantCulture) +
                    ",setpts=PTS-STARTPTS,fps=" + sourceInfo.Fps + ",setsar=1,format=yuv420p";
                videoTail = normalize + (videoTail != null ? "," + videoTail : "");
            }
            try
            {
            // invariant-culture decimal formatting -- ffmpeg only accepts . as the decimal separator, never ,, regardless of system locale.
            string startStr = startSec.ToString("F3", CultureInfo.InvariantCulture);
            string durStr = duration.ToString("F3", CultureInfo.InvariantCulture);
            // how long the saved copy runs: the kept part of the source, stretched by the speed
            string outDurStr = (videoPlan != null ? windowEnd - windowStart : duration / videoSpeed).ToString("F3", CultureInfo.InvariantCulture);
            List<string> argv;
            EditEncoder encoder = null;
            int firstLayerInput = 0;
            int firstOtherInput = 0;

            if (screenshot)
            {
                argv = ScreenshotArgs(videoPlan, sourceInfo, videoSpeed, frameTime, source.Full, lookFilter, layerPlan, subtitleFilter, frameFilter, aspect);
                encoder = new EditEncoder { Name = "png", Args = new List<string> { "-c:v", "png" } };
                argv.AddRange(encoder.Args);
                argv.AddRange(new[] { "-frames:v", "1", "-update", "1", "-y", outPath });
            }
            else if (precise)
            {
                // Multi-piece edits retain source timestamps; simple ranges seek before decoding, with audio plans on a separate unseeked input.
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
                else if (hasPlan || directVideo)
                {
                    string readStart = directVideo ? directReadStart.ToString("0.######", CultureInfo.InvariantCulture) : startStr;
                    argv = new List<string> { "-hide_banner", "-loglevel", "error", "-ss", readStart, "-i", source.Full };
                    if (hasPlan) argv.AddRange(PlanReads(composed, audioCount, source.Full, importArgs));
                    argv.AddRange(new[] { "-t", outDurStr });
                }
                else if (retimed)
                    argv = new List<string> { "-hide_banner", "-loglevel", "error", "-ss", startStr, "-i", source.Full, "-t", outDurStr };
                else
                    argv = new List<string> { "-hide_banner", "-loglevel", "error", "-i", source.Full, "-ss", startStr, "-t", durStr };
                encoder = PickEditEncoder(ext);
                if (render.BitrateKbps > 0) encoder.SetBitrate(render.BitrateKbps);
                argv.AddRange(encoder.Args);
                // a bitrate picked in the render settings is the rate, so the cap that keeps a quality encode under the source is not added to it
                long srcBps = render.BitrateKbps > 0 ? 0 : sourceInfo?.Bitrate > 0 ? sourceInfo.Bitrate : GetVideoBitratePerSec(source.Full);
                if (render.BitrateKbps > 0)
                {
                    Log.Write("Trim rate: " + render.BitrateKbps + "k from the render settings");
                }
                else if (srcBps > 0)
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
                    AddComposedArgs(argv, videoPlan.BuildGraph(0, firstOtherInput, videoSpeed, sourceInfo), planned, FinishGraph(lookFilter, layerPlan, firstLayerInput, frameW > 0 ? frameW : sourceInfo.Width, frameH > 0 ? frameH : sourceInfo.Height, subtitleFilter, frameFilter, out string finalLabel, renderFilter), finalLabel);
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
                // a new encode is not the compressed file any more, so the hidden compress marker that ffmpeg would copy over from the source is left out
                argv.AddRange(new[] { "-metadata", "comment=", "-avoid_negative_ts", "make_zero", "-movflags", "+faststart", "-y", outPath });
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

            // ffmpeg writes how far it has come as out_time_us four times a second; against the length of the saved clip that is the percent the editor shows while it waits
            double outputUs = Math.Max(1.0, (screenshot ? 0 : videoPlan != null ? windowEnd - windowStart : duration / videoSpeed) * 1000000.0);
            int lastPercent = 0;
            argv.InsertRange(3, new[] { "-progress", "pipe:1", "-nostats", "-stats_period", "0.25" });
            UploadState.SetUploadState(requestId: requestId, phase: "starting");
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
                    JobCoordinator.Commit(requestId, () =>
                    {
                        if (screenshot) Screenshots.Folder(Path.GetDirectoryName(source.Full));
                        File.Move(tempPath, finalPath);
                    });
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
            if (overwrite && precise) Clips.ClearCompressed(source.Name, source.Full);
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
                DurationSec = videoPlan != null ? windowEnd - windowStart : duration,
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

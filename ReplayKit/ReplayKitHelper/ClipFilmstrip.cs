using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // the film strip of the editor made ahead of time: a row of small frames of the clip in one picture, kept in the cache, so the editor draws the video row from a file instead of seeking the video once for every frame, which takes seconds on a long clip every time it is opened
    internal static class ClipFilmstrip
    {
        private static readonly string Root = ClipCache.KindDir("film");
        private static readonly PreviewJobs Jobs = new PreviewJobs();
        private static readonly Regex KeyPattern = new Regex("^[0-9a-f]{16}$", RegexOptions.Compiled);
        private const string StripFile = "strip.jpg";
        private const string ManifestFile = "strip.json";
        // how many frames the strip holds; the timeline draws every cell of its video row from the nearest of them
        private const int Frames = 24;
        // the width of a frame in pixels, which is what the editor draws them at
        private const int FrameWidth = 160;
        // frames are cut out a few at a time, quickly when the editor waits for them and gently when it is only a warm up after a recording
        private const int ParallelFrames = 6;
        private const int ParallelFramesGentle = 2;

        private static JObject Fail(string message) => new JObject { ["ok"] = false, ["message"] = message };

        // what the editor asks for: the key of the strip, whether it is ready and the time of every frame in it; asking also starts making it when that has not been done
        public static JObject Describe(string clipName, bool lowPriority = false)
        {
            var clip = Clips.GetSafeClipPath(clipName);
            if (clip == null || !File.Exists(clip.Full)) return Fail("Clip not found");
            string key = ClipCache.KeyFor(clip.Name, new FileInfo(clip.Full), "film" + Frames);
            string dir = Path.Combine(Root, key);
            var result = new JObject { ["ok"] = true, ["key"] = key, ["ready"] = false, ["count"] = Frames };
            if (File.Exists(Path.Combine(dir, ClipCache.DoneMarker)))
            {
                try
                {
                    result["times"] = JObject.Parse(File.ReadAllText(Path.Combine(dir, ManifestFile)))["times"];
                    result["ready"] = true;
                    ClipCache.Touch(dir);
                }
                // an entry that cannot be read is made again below
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is Newtonsoft.Json.JsonException) { }
            }
            if (!result.Value<bool>("ready"))
            {
                string error = EnsureJob(key, clip.Full, dir, lowPriority);
                if (error != null) result["error"] = error;
            }
            return result;
        }

        // the picture of a finished strip, or null when the key is not one this helper made or the strip is not finished
        public static string PathFor(string key)
        {
            if (key == null || !KeyPattern.IsMatch(key)) return null;
            string dir = Path.Combine(Root, key);
            if (!File.Exists(Path.Combine(dir, ClipCache.DoneMarker))) return null;
            string path = Path.Combine(dir, StripFile);
            return File.Exists(path) ? path : null;
        }

        // returns the error of a strip that just failed, or null; starts making one when none is running
        private static string EnsureJob(string key, string source, string dir, bool lowPriority)
        {
            return Jobs.Ensure(key, () => Make(source, dir, lowPriority));
        }

        private static void Make(string source, string dir, bool gentle)
        {
            var caps = Compression.GetToolPaths();
            string ffmpeg = caps["ffmpeg"]?.Value<string>();
            string ffprobe = caps["ffprobe"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg)) throw new InvalidOperationException("ffmpeg.exe was not found");
            var meta = Compression.GetVideoMetadata(ffprobe, ffmpeg, source);
            if (meta == null || !meta.Ok || !(meta.Duration > 0)) throw new InvalidOperationException("the length of the clip could not be read");
            ClipCache.Prune(dir);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);

            // the middle of every one of equal parts of the clip, like the frames the editor used to capture itself
            var times = new double[Frames];
            for (int i = 0; i < Frames; i++) times[i] = Math.Max(0, Math.Min(meta.Duration - 0.04, meta.Duration * (i + 0.5) / Frames));
            var made = new bool[Frames];
            string pattern = Path.Combine(dir, "f%02d.jpg");
            Parallel.For(0, Frames, new ParallelOptions { MaxDegreeOfParallelism = gentle ? ParallelFramesGentle : ParallelFrames }, i =>
            {
                string frame = Path.Combine(dir, "f" + i.ToString("00", CultureInfo.InvariantCulture) + ".jpg");
                // the frame at the key frame before the time, since decoding on to the exact frame costs far more on a clip with long gaps between key frames and a strip does not need it
                var argv = new List<string>
                {
                    "-y", "-hide_banner", "-loglevel", "error",
                    "-ss", times[i].ToString("0.###", CultureInfo.InvariantCulture), "-noaccurate_seek", "-i", source,
                    "-frames:v", "1", "-vf", "scale=" + FrameWidth + ":-2:flags=fast_bilinear", "-q:v", "5", frame,
                };
                try { made[i] = Compression.InvokeNativeCapture(ffmpeg, argv, CancellationToken.None, 60 * 1000, gentle).ExitCode == 0 && File.Exists(frame); }
                catch (TimeoutException) { made[i] = false; }
            });
            if (!made.Any(m => m)) throw new InvalidOperationException("no frame could be read from the clip");
            // a frame that could not be read is the one next to it, so the strip never has a hole
            for (int i = 0; i < Frames; i++)
            {
                if (made[i]) continue;
                int from = Enumerable.Range(0, Frames).Where(j => made[j]).OrderBy(j => Math.Abs(j - i)).First();
                File.Copy(Path.Combine(dir, "f" + from.ToString("00", CultureInfo.InvariantCulture) + ".jpg"), Path.Combine(dir, "f" + i.ToString("00", CultureInfo.InvariantCulture) + ".jpg"), true);
            }

            string strip = Path.Combine(dir, StripFile);
            var join = Compression.InvokeNativeCapture(ffmpeg, new[] { "-y", "-hide_banner", "-loglevel", "error", "-framerate", "1", "-i", pattern, "-vf", "tile=" + Frames + "x1", "-frames:v", "1", "-q:v", "4", strip }, CancellationToken.None, 60 * 1000, gentle);
            if (join.ExitCode != 0 || !File.Exists(strip))
            {
                string text = string.Join(" ", join.Output);
                throw new InvalidOperationException("ffmpeg exit " + join.ExitCode + ": " + (text.Length > 300 ? text.Substring(0, 300) : text));
            }
            for (int i = 0; i < Frames; i++) File.Delete(Path.Combine(dir, "f" + i.ToString("00", CultureInfo.InvariantCulture) + ".jpg"));
            var manifest = new JObject { ["count"] = Frames, ["times"] = new JArray(times.Select(t => Math.Round(t, 3))) };
            File.WriteAllText(Path.Combine(dir, ManifestFile), manifest.ToString(Newtonsoft.Json.Formatting.None), new System.Text.UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, ClipCache.DoneMarker), "ok");
        }
    }
}

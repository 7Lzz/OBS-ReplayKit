using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // the audio tracks of a clip as the editor uses them: which tracks the clip has, and every track the editor lays out cut into a small audio file that the page plays next to the muted video, with the waveform of each one
    internal static class ClipAudioTracks
    {
        private static readonly string Root = ClipCache.KindDir("tracks");
        private static readonly PreviewJobs Jobs = new PreviewJobs();
        private static readonly Regex KeyPattern = new Regex("^[0-9a-f]{16}$", RegexOptions.Compiled);
        private const string DoneMarker = ClipCache.DoneMarker;
        // the waveform is read from the track at a low sample rate and kept as the loudest sample of every 40 ms
        private const int PeakSampleRate = 8000;
        private const int PeakRate = 25;

        private static JObject Fail(string message) => new JObject { ["ok"] = false, ["message"] = message };

        // one folder per state of a clip, so an edited or replaced clip never plays the audio of its old self
        private static string KeyFor(string name, FileInfo info) => ClipCache.KeyFor(name, info, "peaks");

        // what the editor asks for: the tracks it lays out and whether their audio files and waveforms are ready; asking also starts cutting them out when that has not been done. a clip with separate tracks lists the ones after the mix, since the mix is what they add up to, and a clip with one audio stream lists that stream. the warm up after a recording asks the same way at a lower priority, so a game running meanwhile does not notice
        public static JObject Describe(string clipName, bool lowPriority = false)
        {
            var clip = Clips.GetSafeClipPath(clipName);
            if (clip == null || !File.Exists(clip.Full)) return Fail("Clip not found");
            int count = Mp4Tracks.CountAudio(clip.Full);
            int first = count >= 2 ? 1 : 0;
            var tracks = new JArray();
            for (int i = first; i < count; i++) tracks.Add(new JObject { ["index"] = i, ["label"] = count >= 2 ? Trim.AudioTrackLabel(i) : "Audio" });
            var result = new JObject { ["ok"] = true, ["audioTracks"] = count, ["tracks"] = tracks };
            if (count < 1)
            {
                // a clip with no audio at all takes audio from outside, but only when its boxes could be read; one that could not may hold audio that is not seen
                result["readable"] = Mp4Tracks.IsReadable(clip.Full);
                return result;
            }

            string key = KeyFor(clip.Name, new FileInfo(clip.Full));
            string dir = Path.Combine(Root, key);
            bool ready = File.Exists(Path.Combine(dir, DoneMarker));
            result["key"] = key;
            result["ready"] = ready;
            if (ready) ClipCache.Touch(dir);
            else
            {
                string error = EnsureJob(key, clip.Full, first, count, dir, lowPriority);
                if (error != null) result["error"] = error;
            }
            return result;
        }

        // the audio file of one track, or null when the key or the number is not one this helper made or the file is not finished
        public static string PathFor(string key, string indexText)
        {
            if (key == null || !KeyPattern.IsMatch(key)) return null;
            if (!int.TryParse(indexText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index) || index < 0 || index > 15) return null;
            string dir = Path.Combine(Root, key);
            if (!File.Exists(Path.Combine(dir, DoneMarker))) return null;
            string path = Path.Combine(dir, "t" + index + ".m4a");
            return File.Exists(path) ? path : null;
        }

        // the waveform file of one track, under the same rules as the audio file
        public static string PeaksPathFor(string key, string indexText)
        {
            string audio = PathFor(key, indexText);
            if (audio == null) return null;
            string peaks = Path.ChangeExtension(audio, ".peaks.json");
            return File.Exists(peaks) ? peaks : null;
        }

        // returns the error of a cut that just failed, or null; starts a cut when none is running
        private static string EnsureJob(string key, string source, int first, int count, string dir, bool lowPriority)
        {
            return Jobs.Ensure(key, () => Extract(source, first, count, dir, lowPriority));
        }

        private static void Extract(string source, int first, int count, string dir, bool lowPriority)
        {
            string ffmpeg = Compression.GetHelperCapabilities()["ffmpeg"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg)) throw new InvalidOperationException("ffmpeg.exe was not found");
            ClipCache.Prune(dir);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            // a track that is aac already is copied as it is, which takes a fraction of a second where encoding it again takes seconds on a long clip; any other codec is encoded to aac for the page
            var codecs = Mp4Tracks.AudioSampleEntries(source);
            var run = RunExtract(ffmpeg, source, first, count, dir, codecs, lowPriority);
            if (run.ExitCode != 0 && codecs.Any(c => c == "mp4a"))
            {
                // something the copy did not take, so every track is encoded this time
                Directory.Delete(dir, true);
                Directory.CreateDirectory(dir);
                run = RunExtract(ffmpeg, source, first, count, dir, new List<string>(), lowPriority);
            }
            if (run.ExitCode != 0)
            {
                string text = string.Join(" ", run.Output);
                throw new InvalidOperationException("ffmpeg exit " + run.ExitCode + ": " + (text.Length > 300 ? text.Substring(0, 300) : text));
            }
            for (int i = first; i < count; i++)
            {
                string raw = Path.Combine(dir, "t" + i + ".raw");
                WritePeaks(raw, Path.Combine(dir, "t" + i + ".peaks.json"));
                File.Delete(raw);
            }
            File.WriteAllText(Path.Combine(dir, DoneMarker), "ok");
        }

        private static Compression.NativeCaptureResult RunExtract(string ffmpeg, string source, int first, int count, string dir, List<string> codecs, bool lowPriority)
        {
            var argv = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-i", source };
            for (int i = first; i < count; i++)
            {
                bool copy = i < codecs.Count && codecs[i] == "mp4a";
                argv.AddRange(new[] { "-map", "0:a:" + i });
                argv.AddRange(copy ? new[] { "-c:a", "copy" } : new[] { "-c:a", "aac", "-b:a", "128k" });
                argv.AddRange(new[] { "-movflags", "+faststart", Path.Combine(dir, "t" + i + ".m4a") });
                // the same stream once more as raw samples for the waveform, in the same pass over the file
                argv.AddRange(new[] { "-map", "0:a:" + i, "-ac", "1", "-ar", PeakSampleRate.ToString(CultureInfo.InvariantCulture), "-f", "s16le", Path.Combine(dir, "t" + i + ".raw") });
            }
            return Compression.InvokeNativeCapture(ffmpeg, argv, CancellationToken.None, 20 * 60 * 1000, lowPriority);
        }

        // the loudest sample of every 40 ms of a raw 16 bit mono file, scaled to 0-255, as json the page can draw from; gives back how long the samples run in seconds
        internal static double WritePeaks(string rawPath, string jsonPath)
        {
            int bucket = PeakSampleRate / PeakRate;
            var peaks = new List<int>();
            long samples = 0;
            int max = 0;
            int inBucket = 0;
            using (var fs = new FileStream(rawPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var buf = new byte[1 << 16];
                while (true)
                {
                    // a read can come back short, and a sample is two bytes, so the buffer is filled as far as the file goes
                    int got = 0;
                    int n;
                    while (got < buf.Length && (n = fs.Read(buf, got, buf.Length - got)) > 0) got += n;
                    if (got < 2) break;
                    for (int i = 0; i + 1 < got; i += 2)
                    {
                        int sample = (short)(buf[i] | (buf[i + 1] << 8));
                        if (sample < 0) sample = -sample;
                        if (sample > max) max = sample;
                        samples++;
                        if (++inBucket == bucket)
                        {
                            peaks.Add(Math.Min(255, (int)(max * 255L / 32767)));
                            max = 0;
                            inBucket = 0;
                        }
                    }
                    if (got < buf.Length) break;
                }
            }
            if (inBucket > 0) peaks.Add(Math.Min(255, (int)(max * 255L / 32767)));
            var json = new StringBuilder();
            json.Append("{\"rate\":").Append(PeakRate).Append(",\"duration\":").Append((samples / (double)PeakSampleRate).ToString("0.###", CultureInfo.InvariantCulture)).Append(",\"peaks\":[");
            json.Append(string.Join(",", peaks));
            json.Append("]}");
            File.WriteAllText(jsonPath, json.ToString(), new UTF8Encoding(false));
            return samples / (double)PeakSampleRate;
        }

        // the track boxes of a clip as they are in the file, for whoever needs to see why a player picks the track it does
        public static JObject Inspect(string clipName)
        {
            var clip = Clips.GetSafeClipPath(clipName);
            if (clip == null || !File.Exists(clip.Full)) return Fail("Clip not found");
            var list = new JArray();
            try
            {
                using (var fs = new FileStream(clip.Full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var tracks = Mp4Tracks.Read(fs);
                    if (tracks == null) return Fail("Not an mp4 with a readable moov box");
                    foreach (var t in tracks)
                        list.Add(new JObject { ["id"] = t.Id, ["kind"] = t.Audio ? "audio" : t.Video ? "video" : "other", ["enabled"] = (t.Flags & 1) != 0, ["flags"] = t.Flags, ["alternateGroup"] = t.AlternateGroup });
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return Fail(ex.Message);
            }
            return new JObject { ["ok"] = true, ["name"] = clip.Name, ["tracks"] = list };
        }
    }
}

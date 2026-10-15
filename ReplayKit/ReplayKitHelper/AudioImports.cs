using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // audio files the editor brings in from outside the clip: each one is turned into a small aac file that the page plays next to the clip and the saved copy mixes in, with a waveform of it; a file comes from the picker the helper opens or as the bytes of a drop
    internal static class AudioImports
    {
        private sealed class Job
        {
            public Task Task;
            public string Name;
            public string Error;
            public DateTime FinishedAt;
        }

        // an error with a message meant for the person using the editor
        private sealed class ImportException : Exception
        {
            public ImportException(string message) : base(message) { }
        }

        public const string UploadPath = "/import-audio/upload";
        public const long MaxUploadBytes = 512L * 1024 * 1024;
        // every import is written at this rate, so the graph of a saved copy never has to line one up with the rest
        public const int SampleRate = 48000;
        // a longer file is refused, since cutting it off without a word would hand over less than was asked for
        private const double MaxSeconds = 3600;
        private const double MinSeconds = 0.1;
        private const string AudioName = "audio.m4a";
        private const string PeaksName = "peaks.json";
        private const string InfoName = "info.json";

        private static readonly string Root = ClipCache.KindDir("imports");
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Job> Jobs = new Dictionary<string, Job>();
        private static readonly Regex IdPattern = new Regex("^[0-9a-f]{16}$", RegexOptions.Compiled);
        private static readonly Regex NoAudio = new Regex("matches no streams|does not contain any stream", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // a handful of files dropped together are decoded two at a time
        private static readonly SemaphoreSlim Slots = new SemaphoreSlim(2);

        public static bool IsId(string id) => id != null && IdPattern.IsMatch(id);

        private static JObject Fail(string message) => new JObject { ["ok"] = false, ["message"] = message };

        private static JObject State(string id, string name, string state) => new JObject { ["ok"] = true, ["id"] = id, ["name"] = name, ["state"] = state };

        // how an import stands: working while the file is turned into audio, ready once it can be played and mixed, failed with the reason when it cannot be
        public static JObject Describe(string id)
        {
            if (!IsId(id)) return Fail("Unknown audio import");
            string dir = Path.Combine(Root, id);
            lock (Gate)
            {
                if (Jobs.TryGetValue(id, out Job job))
                {
                    if (!job.Task.IsCompleted) return State(id, job.Name, "working");
                    if (job.Error != null)
                    {
                        var failed = State(id, job.Name, "failed");
                        failed["message"] = job.Error;
                        return failed;
                    }
                    Jobs.Remove(id);
                }
            }
            if (!File.Exists(Path.Combine(dir, ClipCache.DoneMarker))) return Fail("This audio is no longer available. Add it again.");
            Used(dir);
            var info = ReadInfo(dir);
            var ready = State(id, info.Value<string>("name"), "ready");
            ready["duration"] = info.Value<double?>("duration") ?? 0;
            return ready;
        }

        // the audio file of an import, or null when the id is not one this helper made or the file is not finished
        public static string PathFor(string id)
        {
            if (!IsId(id)) return null;
            string dir = Path.Combine(Root, id);
            if (!File.Exists(Path.Combine(dir, ClipCache.DoneMarker))) return null;
            string path = Path.Combine(dir, AudioName);
            if (!File.Exists(path)) return null;
            Used(dir);
            return path;
        }

        public static string PeaksPathFor(string id)
        {
            string audio = PathFor(id);
            if (audio == null) return null;
            string peaks = Path.Combine(Path.GetDirectoryName(audio), PeaksName);
            return File.Exists(peaks) ? peaks : null;
        }

        // an import that is in use is kept from going first when the cache is pruned; the stamp only moves when it is a while old, since the page asks for ranges all the time while it plays
        private static void Used(string dir)
        {
            try { if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir) > TimeSpan.FromMinutes(1)) ClipCache.Touch(dir); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        private static JObject ReadInfo(string dir)
        {
            try { return JObject.Parse(File.ReadAllText(Path.Combine(dir, InfoName), Encoding.UTF8)); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is Newtonsoft.Json.JsonException) { return new JObject(); }
        }

        // starts the import of a file the picker returned; a file that was imported before is ready at once
        public static JObject BeginFromPath(string path)
        {
            string name = NameFrom(path);
            try
            {
                // the path comes from the dialog of this helper, and anything that is not a plain file on a drive or a share is turned away all the same
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || Path.GetPathRoot(path).Length < 3 || path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.StartsWith(@"\\?\", StringComparison.Ordinal))
                    return Skipped(name, "That file cannot be used");
                var info = new FileInfo(path);
                if (!info.Exists) return Skipped(name, "That file was not found");
                if (info.Length <= 0) return Skipped(name, "That file is empty");
                return Begin(ClipCache.KeyFor(info.FullName, info, "import"), name, info.FullName, null);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                Log.Write("AudioImports: " + name + ": " + ex.Message);
                return Skipped(name, "That file could not be opened");
            }
        }

        private static JObject Skipped(string name, string message) => new JObject { ["ok"] = false, ["name"] = name, ["message"] = message };

        // starts the import of the bytes a page sent as the body of a request; the body is taken off the wire here, in the order it comes, into a file that is gone again once the import is made
        public static JObject BeginFromUpload(HttpRequest req, string fileName, out int status)
        {
            status = 200;
            string name = NameFrom(fileName);
            if (req.UploadReader == null || req.UploadLength <= 0) { status = 400; return Fail("That file is empty"); }
            if (req.UploadLength > MaxUploadBytes) { status = 413; return Fail("That file is larger than 512 MB"); }
            string staging = Path.Combine(Root, "u" + Guid.NewGuid().ToString("N"));
            string file = Path.Combine(staging, "upload" + ExtensionFrom(fileName));
            string id;
            try
            {
                Directory.CreateDirectory(staging);
                using (var sha = SHA1.Create())
                {
                    using (var fs = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        Connection.CopyUploadBody(req, fs, sha);
                    id = string.Concat(sha.Hash.Take(8).Select(b => b.ToString("x2")));
                }
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
            {
                DeleteStaging(staging);
                Log.Write("AudioImports: upload of " + name + " failed: " + ex.Message);
                status = 400;
                return Fail("The file could not be received");
            }
            return Begin(id, name, file, staging);
        }

        // the same file twice is one import, so a copy that arrives while the first is ready or being made is dropped
        private static JObject Begin(string id, string name, string source, string staging)
        {
            lock (Gate)
            {
                foreach (var old in Jobs.Where(p => p.Value.Task.IsCompleted && p.Value.Error == null).Select(p => p.Key).ToList())
                    Jobs.Remove(old);
                foreach (var old in Jobs.Where(p => p.Value.Task.IsCompleted).OrderBy(p => p.Value.FinishedAt).Take(Math.Max(0, Jobs.Count - 63)).Select(p => p.Key).ToList())
                    Jobs.Remove(old);
                string dir = Path.Combine(Root, id);
                bool done = File.Exists(Path.Combine(dir, ClipCache.DoneMarker));
                bool running = Jobs.TryGetValue(id, out Job existing) && !existing.Task.IsCompleted;
                if (done || running)
                {
                    DeleteStaging(staging);
                }
                else
                {
                    var job = new Job { Name = name };
                    job.Task = Task.Run(() => Run(job, id, source, staging));
                    Jobs[id] = job;
                }
            }
            return Describe(id);
        }

        private static async Task Run(Job job, string id, string source, string staging)
        {
            string dir = Path.Combine(Root, id);
            await Slots.WaitAsync().ConfigureAwait(false);
            try
            {
                Extract(job, source, dir);
            }
            // the end of a background task: whatever went wrong is kept as the answer for the page instead of being lost with the task
            catch (Exception ex)
            {
                job.Error = ex is ImportException ? ex.Message : "That audio could not be imported";
                Log.Write("AudioImports: " + job.Name + ": " + ex.Message);
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
                catch (Exception ex2) when (ex2 is IOException || ex2 is UnauthorizedAccessException) { }
            }
            finally
            {
                DeleteStaging(staging);
                job.FinishedAt = DateTime.UtcNow;
                Slots.Release();
            }
        }

        private static void Extract(Job job, string source, string dir)
        {
            string ffmpeg = Compression.GetHelperCapabilities()["ffmpeg"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg)) throw new ImportException("ffmpeg.exe was not found");
            ClipCache.Prune(dir);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            string audio = Path.Combine(dir, AudioName);
            string raw = Path.Combine(dir, "audio.raw");
            string limit = (MaxSeconds + 1).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
            // only the first audio stream is used and only local files may be opened, so a playlist cannot send the read anywhere else; the same pass writes the file the page plays and the samples the waveform is drawn from
            var argv = new List<string>
            {
                "-y", "-hide_banner", "-loglevel", "error", "-nostdin", "-protocol_whitelist", "file", "-i", source,
                "-map", "0:a:0", "-t", limit, "-c:a", "aac", "-b:a", "192k", "-ar", SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture), "-ac", "2", "-movflags", "+faststart", audio,
                "-map", "0:a:0", "-t", limit, "-ac", "1", "-ar", "8000", "-f", "s16le", raw,
            };
            var run = Compression.InvokeNativeCapture(ffmpeg, argv, CancellationToken.None, 10 * 60 * 1000, true);
            if (run.ExitCode != 0)
            {
                string text = string.Join(" ", run.Output).Trim();
                Log.Write("AudioImports: ffmpeg exit " + run.ExitCode + " for " + job.Name + ": " + (text.Length > 300 ? text.Substring(0, 300) : text));
                throw new ImportException(NoAudio.IsMatch(text) ? "That file has no audio" : "That file could not be read as audio");
            }
            if (!File.Exists(audio) || new FileInfo(audio).Length == 0 || !File.Exists(raw)) throw new ImportException("That file has no audio");
            double duration = ClipAudioTracks.WritePeaks(raw, Path.Combine(dir, PeaksName));
            File.Delete(raw);
            if (duration < MinSeconds) throw new ImportException("That audio is too short to use");
            if (duration > MaxSeconds) throw new ImportException("That audio is longer than an hour");
            var info = new JObject { ["name"] = job.Name, ["duration"] = Math.Round(duration, 3) };
            File.WriteAllText(Path.Combine(dir, InfoName), info.ToString(Newtonsoft.Json.Formatting.None), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, ClipCache.DoneMarker), "ok");
        }

        private static void DeleteStaging(string staging)
        {
            if (staging == null) return;
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        // the last part of a path or a file name as the page wrote it, split by hand since a name from a request can hold characters the path functions refuse
        private static string LastPart(string raw)
        {
            raw = raw ?? "";
            return raw.Substring(Math.Max(raw.LastIndexOf('\\'), raw.LastIndexOf('/')) + 1);
        }

        // the name a row carries: the file name without its folder and extension, free of control characters and not too long
        private static string NameFrom(string raw)
        {
            string name = LastPart(raw);
            int dot = name.LastIndexOf('.');
            if (dot > 0) name = name.Substring(0, dot);
            name = Regex.Replace(Regex.Replace(name, @"\p{C}+", " "), @"\s+", " ").Trim();
            if (name.Length > 60) name = name.Substring(0, 60).Trim();
            return name.Length == 0 ? "Audio" : name;
        }

        // the extension helps ffmpeg tell formats apart that look alike, so it is kept when it is plain letters and digits
        private static string ExtensionFrom(string raw)
        {
            var m = Regex.Match(LastPart(raw), @"\.([A-Za-z0-9]{1,5})$");
            return m.Success ? "." + m.Groups[1].Value.ToLowerInvariant() : "";
        }
    }
}

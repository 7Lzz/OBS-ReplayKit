using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // auto subtitles: whisper.cpp (the cpu build, which picks the fastest code its processor runs at start) and the multilingual base model, fetched once on first use into the user state folder and checked against pinned sha256 hashes; a clip is turned into word timings that the editor groups into subtitles itself
    internal static class Speech
    {
        // pinned so a changed or swapped file is refused instead of run; moving to a newer engine or model is a change of these lines
        private const string EngineTag = "b5454";
        private const string EngineUrl = "https://github.com/ggml-org/whisper.cpp/releases/download/b5454/whisper-bin-x64.zip";
        private const string EngineSha256 = "6ba69e3482d7826214f90a6a9c84ca07782aec1e1d0c6a7c30c994fd5d816ccb";
        private const long EngineMaxBytes = 64L * 1024 * 1024;
        private const string ModelName = "ggml-base-q5_1.bin";
        private const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base-q5_1.bin";
        private const string ModelSha256 = "422f1ae452ade6f30a004d7e5c6a43195e4433bc370bf23fac9cc591f01a8898";
        private const long ModelBytes = 59707625;
        // what the download adds up to, shown before it starts
        public const long DownloadBytes = 8L * 1024 * 1024 + ModelBytes;

        private static readonly string[] EngineFiles = { "whisper-cli.exe", "whisper.dll", "ggml.dll", "ggml-base.dll" };
        private const string EngineCpuPrefix = "ggml-cpu-";

        // the languages offered in the editor; auto lets the model tell from the first seconds
        private static readonly HashSet<string> Languages = new HashSet<string>(StringComparer.Ordinal)
        {
            "auto", "en", "es", "pt", "fr", "de", "it", "nl", "pl", "ru", "uk", "tr", "ar", "hi", "ja", "ko", "zh", "vi", "id", "th", "sv",
        };

        private static readonly HttpClient Http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        private static readonly object Gate = new object();
        private static readonly SemaphoreSlim TranscribeSlot = new SemaphoreSlim(1, 1);

        private static Task _install;
        private static long _installDone;
        private static string _installError;

        private sealed class Job
        {
            public string Id;
            public string State = "queued";
            public int Percent;
            public string Error;
            public string Language;
            public JArray Words;
            public CancellationTokenSource Cancel = new CancellationTokenSource();
            public DateTime FinishedAt = DateTime.MaxValue;
        }

        private static readonly Dictionary<string, Job> Jobs = new Dictionary<string, Job>();

        private static string Root => Path.Combine(AppConfig.GetReplayKitUserStateDir(), "speech");
        private static string EngineDir => Path.Combine(Root, "whisper-" + EngineTag);
        private static string ModelPath => Path.Combine(Root, ModelName);
        private static string EngineExe => Path.Combine(EngineDir, "whisper-cli.exe");

        private static bool Installed() => File.Exists(EngineExe) && File.Exists(ModelPath) && new FileInfo(ModelPath).Length == ModelBytes;

        public static JObject Status()
        {
            lock (Gate)
            {
                bool running = _install != null && !_install.IsCompleted;
                return new JObject
                {
                    ["ok"] = true,
                    ["installed"] = Installed(),
                    ["installing"] = running,
                    ["percent"] = running ? (int)Math.Min(99, Interlocked.Read(ref _installDone) * 100 / DownloadBytes) : 0,
                    ["error"] = running ? null : _installError,
                    ["downloadMb"] = (int)Math.Round(DownloadBytes / 1048576.0),
                };
            }
        }

        // one install at a time; asking again while it runs only reports on it
        public static JObject StartInstall()
        {
            lock (Gate)
            {
                if (Installed()) return Status();
                if (_install == null || _install.IsCompleted)
                {
                    _installError = null;
                    Interlocked.Exchange(ref _installDone, 0);
                    _install = Task.Run(() => Install());
                }
            }
            return Status();
        }

        private static void Install()
        {
            string staging = Path.Combine(Root, "staging-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(staging);
                if (!File.Exists(EngineExe))
                {
                    string zip = Path.Combine(staging, "engine.zip");
                    Download(EngineUrl, zip, EngineMaxBytes, EngineSha256);
                    string unpacked = Path.Combine(staging, "engine");
                    Directory.CreateDirectory(unpacked);
                    using (var archive = ZipFile.OpenRead(zip))
                    {
                        foreach (var entry in archive.Entries)
                        {
                            string name = Path.GetFileName(entry.FullName);
                            bool wanted = EngineFiles.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                                (name.StartsWith(EngineCpuPrefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
                            if (!wanted || entry.FullName.Contains("..")) continue;
                            entry.ExtractToFile(Path.Combine(unpacked, name), true);
                        }
                    }
                    if (EngineFiles.Any(f => !File.Exists(Path.Combine(unpacked, f)))) throw new InvalidDataException("The speech engine download is missing files.");
                    if (Directory.Exists(EngineDir)) Directory.Delete(EngineDir, true);
                    Directory.Move(unpacked, EngineDir);
                }
                else
                {
                    Interlocked.Add(ref _installDone, 8L * 1024 * 1024);
                }
                if (!File.Exists(ModelPath) || new FileInfo(ModelPath).Length != ModelBytes)
                {
                    string model = Path.Combine(staging, ModelName);
                    Download(ModelUrl, model, ModelBytes, ModelSha256);
                    if (File.Exists(ModelPath)) File.Delete(ModelPath);
                    File.Move(model, ModelPath);
                }
                Log.Write("Speech: engine " + EngineTag + " and " + ModelName + " installed");
            }
            catch (Exception ex)
            {
                lock (Gate) _installError = ex is HttpRequestException || ex is TaskCanceledException || ex is OperationCanceledException
                    ? "The download did not finish. Check the internet connection and try again."
                    : ex.Message;
                Log.Write("Speech install failed: " + ex.Message);
            }
            finally
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }

        // https only, never more than the cap, and a download that stalls for half a minute is given up; the hash is what makes the file trusted, wherever the redirects led
        private static void Download(string url, string path, long maxBytes, string sha256)
        {
            using (var overall = new CancellationTokenSource(TimeSpan.FromMinutes(30)))
            using (var response = Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, overall.Token).GetAwaiter().GetResult())
            {
                response.EnsureSuccessStatusCode();
                if (response.RequestMessage.RequestUri.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("The download left https.");
                if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("The download is larger than expected.");
                using (var stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var sha = SHA256.Create())
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    while (true)
                    {
                        int count;
                        using (var stall = CancellationTokenSource.CreateLinkedTokenSource(overall.Token))
                        {
                            stall.CancelAfter(TimeSpan.FromSeconds(30));
                            count = stream.ReadAsync(buffer, 0, buffer.Length, stall.Token).GetAwaiter().GetResult();
                        }
                        if (count <= 0) break;
                        total += count;
                        if (total > maxBytes) throw new InvalidDataException("The download is larger than expected.");
                        file.Write(buffer, 0, count);
                        sha.TransformBlock(buffer, 0, count, null, 0);
                        Interlocked.Add(ref _installDone, count);
                    }
                    sha.TransformFinalBlock(new byte[0], 0, 0);
                    string actual = BitConverter.ToString(sha.Hash).Replace("-", "");
                    if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The downloaded file did not match its expected hash.");
                    file.Flush(true);
                }
            }
        }

        // the audio of a clip from start to end (seconds of the clip), track an audio stream of it; the words come back in seconds of the clip
        public static JObject StartTranscribe(string clipName, double start, double end, int track, string language)
        {
            if (!Installed()) return new JObject { ["ok"] = false, ["message"] = "Auto subtitles are not set up yet." };
            if (!Languages.Contains(language ?? "")) return new JObject { ["ok"] = false, ["message"] = "Unknown language." };
            var selected = Clips.GetSafeClipPath(clipName);
            if (selected == null || !File.Exists(selected.Full)) return new JObject { ["ok"] = false, ["message"] = "The clip was not found." };
            if (!(end > start) || start < 0 || end - start > 3 * 60 * 60 || track < 0 || track > 31) return new JObject { ["ok"] = false, ["message"] = "Nothing to listen to in that part of the clip." };

            var job = new Job { Id = Guid.NewGuid().ToString("N") };
            lock (Gate)
            {
                foreach (var old in Jobs.Values.Where(j => DateTime.UtcNow - j.FinishedAt > TimeSpan.FromMinutes(10)).ToList())
                {
                    Jobs.Remove(old.Id);
                    old.Cancel.Dispose();
                }
                Jobs[job.Id] = job;
            }
            Task.Run(() => Run(job, selected.Full, start, end, track, language));
            return new JObject { ["ok"] = true, ["id"] = job.Id };
        }

        public static JObject JobStatus(string id)
        {
            lock (Gate)
            {
                if (id == null || !Jobs.TryGetValue(id, out Job job)) return new JObject { ["ok"] = false, ["message"] = "Unknown job." };
                return new JObject { ["ok"] = true, ["state"] = job.State, ["percent"] = job.Percent, ["message"] = job.Error, ["language"] = job.Language, ["words"] = job.State == "done" ? job.Words : null };
            }
        }

        public static JObject Cancel(string id)
        {
            lock (Gate)
            {
                if (id != null && Jobs.TryGetValue(id, out Job job) && job.State != "done" && job.State != "error")
                {
                    try { job.Cancel.Cancel(); } catch (ObjectDisposedException) { }
                }
            }
            return new JObject { ["ok"] = true };
        }

        private static void Finish(Job job, string state, string error = null)
        {
            lock (Gate)
            {
                job.State = state;
                job.Error = error;
                job.FinishedAt = DateTime.UtcNow;
            }
        }

        private static void Run(Job job, string clipPath, double start, double end, int track, string language)
        {
            string work = Path.Combine(Constants.SCRATCH_DIR, "speech-" + job.Id);
            bool slot = false;
            try
            {
                // one at a time: two at once would only share the processor and both take twice as long
                TranscribeSlot.Wait(job.Cancel.Token);
                slot = true;
                Directory.CreateDirectory(work);
                lock (Gate) job.State = "preparing";
                string wav = Path.Combine(work, "audio.wav");
                string ffmpeg = Compression.FindCompressionFfmpeg();
                if (string.IsNullOrEmpty(ffmpeg)) throw new InvalidOperationException("The video tools are missing. Run the ReplayKit setup again.");
                var extract = new List<string>
                {
                    "-hide_banner", "-loglevel", "error", "-ss", start.ToString("0.###", CultureInfo.InvariantCulture), "-i", clipPath,
                    "-t", (end - start).ToString("0.###", CultureInfo.InvariantCulture), "-map", "0:a:" + track.ToString(CultureInfo.InvariantCulture),
                    "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "-y", wav,
                };
                var extracted = Compression.InvokeNativeCapture(ffmpeg, extract, job.Cancel.Token, 10 * 60 * 1000, true);
                if (extracted.ExitCode != 0 || !File.Exists(wav)) throw new InvalidOperationException("The audio of the clip could not be read.");

                lock (Gate) job.State = "listening";
                string outBase = Path.Combine(work, "words");
                int threads = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));
                var args = new List<string>
                {
                    "-m", ModelPath, "-f", wav, "-l", language, "-t", threads.ToString(CultureInfo.InvariantCulture),
                    "-ml", "1", "-sow", "-oj", "-of", outBase, "-np", "-pp",
                };
                RunEngine(job, args);
                string json = outBase + ".json";
                if (!File.Exists(json)) throw new InvalidOperationException("The speech engine gave no result.");
                var data = JObject.Parse(File.ReadAllText(json, Encoding.UTF8));
                var words = new JArray();
                foreach (var seg in data["transcription"] as JArray ?? new JArray())
                {
                    string text = (seg["text"]?.Value<string>() ?? "").Trim();
                    if (text.Length == 0) continue;
                    double from = (seg["offsets"]?["from"]?.Value<double>() ?? 0) / 1000.0 + start;
                    double to = (seg["offsets"]?["to"]?.Value<double>() ?? 0) / 1000.0 + start;
                    words.Add(new JArray(Math.Round(from, 3), Math.Round(Math.Max(from, to), 3), text));
                }
                lock (Gate)
                {
                    job.Words = words;
                    job.Language = data["result"]?["language"]?.Value<string>();
                    job.Percent = 100;
                }
                Finish(job, "done");
            }
            catch (OperationCanceledException)
            {
                Finish(job, "error", "Cancelled");
            }
            catch (Exception ex)
            {
                Log.Write("Speech transcribe failed: " + ex.Message);
                Finish(job, "error", ex.Message);
            }
            finally
            {
                if (slot) TranscribeSlot.Release();
                try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }

        // below normal priority so a game running beside it keeps the processor; a cancel or the time limit ends the engine, and its progress lines are what the editor shows
        private static void RunEngine(Job job, List<string> args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = EngineExe,
                Arguments = ProcessArgs.Join(args.ToArray()),
                WorkingDirectory = EngineDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var proc = Process.Start(psi))
            using (var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30)))
            using (var stopping = CancellationTokenSource.CreateLinkedTokenSource(job.Cancel.Token, deadline.Token))
            using (stopping.Token.Register(() => { try { if (!proc.HasExited) proc.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } }))
            {
                try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; }
                catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) { }
                var drain = proc.StandardOutput.ReadToEndAsync();
                var tail = new StringBuilder();
                string line;
                while ((line = proc.StandardError.ReadLine()) != null)
                {
                    int at = line.IndexOf("progress =", StringComparison.Ordinal);
                    if (at >= 0 && int.TryParse(line.Substring(at + 10).Trim().TrimEnd('%'), out int pct)) { lock (Gate) job.Percent = Math.Max(0, Math.Min(99, pct)); }
                    else if (tail.Length < 2000) tail.AppendLine(line);
                }
                proc.WaitForExit();
                drain.GetAwaiter().GetResult();
                job.Cancel.Token.ThrowIfCancellationRequested();
                if (deadline.IsCancellationRequested) throw new TimeoutException("The speech engine took too long.");
                if (proc.ExitCode != 0)
                {
                    Log.Write("Speech engine exit " + proc.ExitCode + ": " + tail);
                    throw new InvalidOperationException("The speech engine failed (exit " + proc.ExitCode + ").");
                }
            }
        }
    }
}

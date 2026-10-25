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
    // auto subtitles: whisper.cpp, a multilingual model picked by the accuracy setting and Silero speech detection, downloaded with pinned hashes; source-timed words are grouped into captions by the editor
    internal static class Speech
    {
        // pinned so a changed or swapped file is refused instead of run; moving to a newer engine or model is a change of these lines
        private const string EngineTag = "b5454";
        private const string EngineUrl = "https://github.com/ggml-org/whisper.cpp/releases/download/b5454/whisper-bin-x64.zip";
        private const string EngineSha256 = "6ba69e3482d7826214f90a6a9c84ca07782aec1e1d0c6a7c30c994fd5d816ccb";
        private const long EngineMaxBytes = 64L * 1024 * 1024;
        private sealed class Model
        {
            // Dtw names the alignment heads whisper.cpp times the words with
            public string Id, Name, Sha256, Dtw;
            public long Bytes;
            public string Url => "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + Name;
        }

        // fast is whisper small, accurate is large-v3-turbo; each downloads only when it is first used
        private static readonly Model[] Models =
        {
            new Model { Id = "fast", Name = "ggml-small-q5_1.bin", Sha256 = "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb", Bytes = 190085487, Dtw = "small" },
            new Model { Id = "accurate", Name = "ggml-large-v3-turbo-q5_0.bin", Sha256 = "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2", Bytes = 574041195, Dtw = "large.v3.turbo" },
        };
        // the base model used before the accuracy setting existed, removed once a model of the setting is in place
        private const string RetiredModelName = "ggml-base-q5_1.bin";
        private const long EngineBytes = 8L * 1024 * 1024;
        private const string VadName = "ggml-silero-v5.1.2.bin";
        private const string VadUrl = "https://huggingface.co/ggml-org/whisper-vad/resolve/main/ggml-silero-v5.1.2.bin";
        private const string VadSha256 = "29940d98d42b91fbd05ce489f3ecf7c72f0a42f027e4875919a28fb4c04ea2cf";
        private const long VadBytes = 885098;

        private static readonly string[] EngineFiles = { "whisper-cli.exe", "whisper-vad-speech-segments.exe", "whisper.dll", "ggml.dll", "ggml-base.dll" };
        private const string EngineCpuPrefix = "ggml-cpu-";

        // auto detects the language after non-speech audio has been removed
        private static readonly HashSet<string> Languages = new HashSet<string>(StringComparer.Ordinal)
        {
            "auto", "en", "es", "pt", "fr", "de", "it", "nl", "pl", "ru", "uk", "tr", "ar", "hi", "ja", "ko", "zh", "vi", "id", "th", "sv",
        };

        private static readonly HttpClient Http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            SslProtocols = System.Security.Authentication.SslProtocols.Tls12,
        }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        private static readonly object Gate = new object();
        private static readonly SemaphoreSlim TranscribeSlot = new SemaphoreSlim(1, 1);

        // installs run one after another, so asking for the other model while one downloads queues it behind
        private static Task _install = Task.CompletedTask;
        private static readonly HashSet<string> Queued = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> InstallErrors = new Dictionary<string, string>(StringComparer.Ordinal);
        private static string _installing;
        private static long _installDone;
        private static long _installTotal;

        private sealed class Job
        {
            public string Id;
            public string State = "queued";
            public int Percent;
            public string Error;
            public string Language;
            public string Warning;
            public JArray Words;
            public JObject Alternatives;
            public CancellationTokenSource Cancel = new CancellationTokenSource();
            public DateTime FinishedAt = DateTime.MaxValue;
        }

        private static readonly Dictionary<string, Job> Jobs = new Dictionary<string, Job>();

        private static string Root => Path.Combine(AppConfig.GetReplayKitUserStateDir(), "speech");
        private static string EngineDir => Path.Combine(Root, "whisper-" + EngineTag);
        private static string ModelPath(Model model) => Path.Combine(Root, model.Name);
        private static string VadPath => Path.Combine(Root, VadName);
        private static string EngineExe => Path.Combine(EngineDir, "whisper-cli.exe");
        private static string DetectorExe => Path.Combine(EngineDir, "whisper-vad-speech-segments.exe");

        private static bool VadInstalled()
        {
            if (!File.Exists(VadPath) || new FileInfo(VadPath).Length != VadBytes) return false;
            using (var file = File.OpenRead(VadPath))
            using (var sha = SHA256.Create())
                return string.Equals(BitConverter.ToString(sha.ComputeHash(file)).Replace("-", ""), VadSha256, StringComparison.OrdinalIgnoreCase);
        }

        private static Model FindModel(string id) => Models.FirstOrDefault(m => m.Id == id);

        private static bool ModelInstalled(Model model) => File.Exists(ModelPath(model)) && new FileInfo(ModelPath(model)).Length == model.Bytes;

        // an engine folder from before the speech detector was used lacks it, and is fetched again
        private static bool EngineInstalled() => EngineFiles.All(f => File.Exists(Path.Combine(EngineDir, f)));

        private static bool Installed(Model model) => EngineInstalled() && ModelInstalled(model) && VadInstalled();

        // what is still missing for this model, so the download size shown is the one about to happen
        private static long MissingBytes(Model model) => (EngineInstalled() ? 0 : EngineBytes) + (ModelInstalled(model) ? 0 : model.Bytes) + (VadInstalled() ? 0 : VadBytes);

        private static JObject UnknownModel() => new JObject { ["ok"] = false, ["message"] = "Unknown speech model." };

        public static JObject Status(string modelId)
        {
            var model = FindModel(modelId);
            if (model == null) return UnknownModel();
            lock (Gate)
            {
                bool queued = Queued.Contains(model.Id);
                bool installed = Installed(model);
                long total = Interlocked.Read(ref _installTotal);
                bool active = _installing == model.Id && total > 0;
                InstallErrors.TryGetValue(model.Id, out string error);
                return new JObject
                {
                    ["ok"] = true,
                    ["installed"] = installed,
                    ["installing"] = queued,
                    ["percent"] = active ? (int)Math.Min(99, Interlocked.Read(ref _installDone) * 100 / total) : 0,
                    ["error"] = queued || installed ? null : error,
                    ["downloadMb"] = (int)Math.Round(MissingBytes(model) / 1048576.0),
                };
            }
        }

        // asking again for a model that is downloading or waiting its turn only reports on it
        public static JObject StartInstall(string modelId)
        {
            var model = FindModel(modelId);
            if (model == null) return UnknownModel();
            lock (Gate)
            {
                if (Installed(model)) return Status(modelId);
                if (Queued.Add(model.Id))
                {
                    InstallErrors.Remove(model.Id);
                    _install = _install.ContinueWith(_ => Install(model), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                }
            }
            return Status(modelId);
        }

        private static void Install(Model model)
        {
            string staging = Path.Combine(Root, "staging-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Root);
                lock (Gate)
                {
                    _installing = model.Id;
                    Interlocked.Exchange(ref _installDone, 0);
                    Interlocked.Exchange(ref _installTotal, MissingBytes(model));
                }
                Directory.CreateDirectory(staging);
                if (!EngineInstalled())
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
                if (!ModelInstalled(model))
                {
                    string downloaded = Path.Combine(staging, model.Name);
                    Download(model.Url, downloaded, model.Bytes, model.Sha256);
                    if (File.Exists(ModelPath(model))) File.Delete(ModelPath(model));
                    File.Move(downloaded, ModelPath(model));
                }
                if (!VadInstalled())
                {
                    string vad = Path.Combine(staging, VadName);
                    Download(VadUrl, vad, VadBytes, VadSha256);
                    if (File.Exists(VadPath)) File.Delete(VadPath);
                    File.Move(vad, VadPath);
                }
                string retired = Path.Combine(Root, RetiredModelName);
                if (File.Exists(retired)) File.Delete(retired);
                Log.Write("Speech: engine " + EngineTag + " and " + model.Name + " installed");
            }
            catch (Exception ex)
            {
                string error = ex.Message;
                if (ex is HttpRequestException)
                {
                    var network = ex.InnerException as System.Net.WebException;
                    error = network != null && (network.Status == System.Net.WebExceptionStatus.SecureChannelFailure || network.Status == System.Net.WebExceptionStatus.TrustFailure)
                        ? "The speech-model download could not establish a secure HTTPS connection."
                        : "The speech-model download could not connect to the download server over HTTPS.";
                }
                else if (ex is OperationCanceledException) error = "The speech-model download timed out before it finished.";
                lock (Gate) InstallErrors[model.Id] = error;
                Log.Write("Speech install of " + model.Name + " failed: " + ex.Message);
            }
            finally
            {
                lock (Gate)
                {
                    Queued.Remove(model.Id);
                    _installing = null;
                    Interlocked.Exchange(ref _installTotal, 0);
                }
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }

        // https only, never more than the cap, and a download that stalls for half a minute is given up; the hash is what makes the file trusted, wherever the redirects led
        private static void Download(string url, string path, long maxBytes, string sha256)
        {
            using (var overall = new CancellationTokenSource(TimeSpan.FromMinutes(30)))
            using (var response = Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, overall.Token).GetAwaiter().GetResult())
            {
                if (!response.IsSuccessStatusCode) throw new InvalidDataException("The speech-model download server returned HTTP " + (int)response.StatusCode + ".");
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

        // the audio of a clip from start to end (seconds of the clip), tracks the audio streams of it to listen to together; the words come back in seconds of the clip
        public static JObject StartTranscribe(string clipName, double start, double end, List<int> tracks, string language, string modelId)
        {
            var model = FindModel(modelId);
            if (model == null) return UnknownModel();
            if (!Installed(model)) return new JObject { ["ok"] = false, ["message"] = "Auto subtitles are not set up yet." };
            if (!Languages.Contains(language ?? "")) return new JObject { ["ok"] = false, ["message"] = "Unknown language." };
            var selected = Clips.GetSafeClipPath(clipName);
            if (selected == null || !File.Exists(selected.Full)) return new JObject { ["ok"] = false, ["message"] = "The clip was not found." };
            if (!(end > start) || start < 0 || end - start > 3 * 60 * 60) return new JObject { ["ok"] = false, ["message"] = "Nothing to listen to in that part of the clip." };
            // the first stream is the mix of all the others, so it is only ever listened to alone
            if (tracks == null || tracks.Count == 0 || tracks.Count > 8 || tracks.Any(t => t < 0 || t > 31) || tracks.Distinct().Count() != tracks.Count || (tracks.Contains(0) && tracks.Count > 1))
                return new JObject { ["ok"] = false, ["message"] = "Those audio tracks cannot be listened to." };

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
            var picked = tracks.ToArray();
            Task.Run(() => Run(job, selected.Full, start, end, picked, language, model));
            return new JObject { ["ok"] = true, ["id"] = job.Id };
        }

        public static JObject JobStatus(string id)
        {
            lock (Gate)
            {
                if (id == null || !Jobs.TryGetValue(id, out Job job)) return new JObject { ["ok"] = false, ["message"] = "Unknown job." };
                return new JObject { ["ok"] = true, ["state"] = job.State, ["percent"] = job.Percent, ["message"] = job.Error, ["warning"] = job.Warning, ["language"] = job.Language, ["words"] = job.State == "done" ? job.Words : null, ["alternatives"] = job.State == "done" ? job.Alternatives : null };
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

        private static void Run(Job job, string clipPath, double start, double end, int[] tracks, string language, Model model)
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
                    "-t", (end - start).ToString("0.###", CultureInfo.InvariantCulture),
                };
                // several tracks are summed rather than averaged, so a voice on one keeps its level
                if (tracks.Length == 1) extract.AddRange(new[] { "-map", "0:a:" + tracks[0].ToString(CultureInfo.InvariantCulture) });
                else extract.AddRange(new[] { "-filter_complex", string.Concat(tracks.Select(t => "[0:a:" + t.ToString(CultureInfo.InvariantCulture) + "]")) + "amix=inputs=" + tracks.Length + ":duration=longest:normalize=0[voices]", "-map", "[voices]" });
                extract.AddRange(new[] { "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "-y", wav });
                var extracted = Compression.InvokeNativeCapture(ffmpeg, extract, job.Cancel.Token, 10 * 60 * 1000, true);
                if (extracted.ExitCode != 0 || !File.Exists(wav)) throw new InvalidOperationException("The audio of the clip could not be read.");

                lock (Gate) job.State = "listening";
                // about one thread per physical core; the engine runs below normal priority so a game keeps its share
                int threads = Math.Max(1, Math.Min(8, Environment.ProcessorCount / 2));
                string joined = Path.Combine(work, "speech.wav");
                var timings = JoinSpeech(job, wav, joined, threads);
                if (timings.Count == 0)
                {
                    lock (Gate) { job.Words = new JArray(); job.Percent = 100; }
                    Finish(job, "done");
                    return;
                }
                string outBase = Path.Combine(work, "words");
                var args = new List<string>
                {
                    // whole segments rather than one word each, since the edges of a segment are what hold each word to its piece of speech
                    "-m", ModelPath(model), "-f", joined, "-l", language, "-t", threads.ToString(CultureInfo.InvariantCulture),
                    "-ojf", "-of", outBase, "-pp", "-sns", "-mc", "0", "-bs", "5", "-bo", "5",
                    // the dtw alignment of the attention gives each token a time of its own; it needs flash attention off
                    "-nfa", "-dtw", model.Dtw,
                };
                RunTool(job, EngineExe, args, end - start);
                string json = outBase + ".json";
                if (!File.Exists(json)) throw new InvalidOperationException("The speech engine gave no result.");
                if (new FileInfo(json).Length > 32 * 1024 * 1024) throw new InvalidDataException("The speech engine result is too large.");
                var data = JObject.Parse(File.ReadAllText(json, Encoding.UTF8));
                var transcript = SpeechTranscript.Parse(data, start, end, timings);
                string heard = data["result"]?["language"]?.Value<string>();
                JObject alternatives = null;
                if (transcript.UnsureText.Count > 0)
                {
                    lock (Gate) job.State = "checking";
                    alternatives = Alternatives(job, wav, Languages.Contains(heard ?? "") ? heard : language, model, timings, transcript.UnsureText, work, threads);
                }
                lock (Gate)
                {
                    job.Words = transcript.Words;
                    job.Alternatives = alternatives;
                    job.Warning = transcript.Warning;
                    job.Language = heard;
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

        private const int MaxAlternativeRanges = 8;
        private const int MaxAlternatives = 3;
        // silence between the stretches put together for the second listen, long enough that no word runs from one into the next
        private const double AlternativeGap = 1.0;
        private const double AlternativePad = 0.2;

        // a second listen to the stretches the engine was unsure of, put together in one file so the model loads once: the same model sampled more loosely, then the other model when it is installed; what comes back different from the first listen is what the editor offers in its place
        private static JObject Alternatives(Job job, string wav, string language, Model model, SpeechTimings timings, SortedDictionary<int, string> unsure, string work, int threads)
        {
            var stretches = unsure.Keys.Take(MaxAlternativeRanges).ToList();
            var asked = new List<double[]>();
            foreach (int index in stretches)
            {
                timings.SourceRange(index, out double from, out double to);
                asked.Add(new[] { from - AlternativePad, to + AlternativePad });
            }
            var pieces = WritePieces(wav, Path.Combine(work, "unsure.wav"), asked, AlternativeGap);
            if (pieces.All(piece => piece == null)) return null;
            var heard = stretches.ToDictionary(index => index, index => new List<string>());
            var passes = new List<KeyValuePair<Model, string[]>> { new KeyValuePair<Model, string[]>(model, new[] { "-tp", "0.4", "-bo", "5" }) };
            var other = Models.FirstOrDefault(m => m != model && ModelInstalled(m));
            if (other != null) passes.Add(new KeyValuePair<Model, string[]>(other, new[] { "-bs", "5", "-bo", "5" }));
            for (int p = 0; p < passes.Count; p++)
            {
                string outBase = Path.Combine(work, "unsure" + p);
                var args = new List<string> { "-m", ModelPath(passes[p].Key), "-f", Path.Combine(work, "unsure.wav"), "-l", language, "-t", threads.ToString(CultureInfo.InvariantCulture), "-ml", "1", "-sow", "-ojf", "-of", outBase, "-sns", "-mc", "0" };
                args.AddRange(passes[p].Value);
                RunTool(job, EngineExe, args, 0);
                string json = outBase + ".json";
                if (!File.Exists(json) || new FileInfo(json).Length > 4 * 1024 * 1024) throw new InvalidOperationException("The speech engine gave no result on the second listen.");
                var texts = new string[stretches.Count];
                foreach (var segment in JObject.Parse(File.ReadAllText(json, Encoding.UTF8))["transcription"] as JArray ?? new JArray())
                {
                    string text = segment["text"]?.Type == JTokenType.String ? segment.Value<string>("text").Trim() : "";
                    double from = (segment["offsets"]?["from"]?.Value<double>() ?? -1) / 1000, to = (segment["offsets"]?["to"]?.Value<double>() ?? -1) / 1000;
                    if (text.Length == 0 || from < 0 || to < from || (text.StartsWith("[") && text.EndsWith("]"))) continue;
                    double middle = (from + to) / 2;
                    for (int i = 0; i < pieces.Count; i++)
                        if (pieces[i] != null && middle >= pieces[i][2] && middle < pieces[i][3]) texts[i] = texts[i] == null ? text : texts[i] + " " + text;
                }
                for (int i = 0; i < stretches.Count; i++) if (!string.IsNullOrWhiteSpace(texts[i])) heard[stretches[i]].Add(texts[i]);
            }
            var result = new JObject();
            foreach (int index in stretches)
            {
                var seen = new HashSet<string> { Plain(unsure[index]) };
                var offers = new JArray();
                foreach (string text in heard[index])
                    if (offers.Count < MaxAlternatives && Plain(text).Length > 0 && seen.Add(Plain(text))) offers.Add(text.Length > 300 ? text.Substring(0, 300) : text);
                result[(index + 1).ToString(CultureInfo.InvariantCulture)] = offers;
            }
            return result;
        }

        // pieces of speech closer together than this are one piece, so a short breath does not split a sentence
        private const double SpeechJoinGap = 0.5;
        // the silence put between pieces of speech in the audio the engine hears, long enough that the timing of no word reaches across it
        private const double SpeechGap = 2.0;
        private static readonly System.Text.RegularExpressions.Regex DetectedSpeech = new System.Text.RegularExpressions.Regex(@"^Speech segment \d+: start = ([0-9]+(?:\.[0-9]+)?), end = ([0-9]+(?:\.[0-9]+)?)$");

        // where the speech is, from the silero detector run on the whole clip audio, and the speech alone written to joined with silence between the pieces -- the way whisperx and faster-whisper cut long audio, so each word can be put back in the one piece it was heard in
        private static SpeechTimings JoinSpeech(Job job, string wav, string joined, int threads)
        {
            string found = RunTool(job, DetectorExe, new List<string> { "-f", wav, "-vm", VadPath, "-t", threads.ToString(CultureInfo.InvariantCulture), "-vsd", "300", "-vp", "200", "-vmsd", "30", "-np" }, 0);
            double length;
            using (var file = File.OpenRead(wav))
            {
                FindPcm(file, out long _, out long bytes);
                length = bytes / 2.0 / PcmRate;
            }
            var pieces = new List<double[]>();
            foreach (string line in found.Split('\n'))
            {
                var match = DetectedSpeech.Match(line.Trim());
                if (!match.Success) continue;
                // the detector writes centiseconds
                double from = Math.Max(0, double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) / 100);
                double to = Math.Min(length, double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) / 100);
                if (to <= from) continue;
                if (pieces.Count > 0 && from < pieces[pieces.Count - 1][0]) throw new InvalidDataException("The speech detector returned speech out of order.");
                if (pieces.Count > 0 && from - pieces[pieces.Count - 1][1] < SpeechJoinGap) pieces[pieces.Count - 1][1] = Math.Max(pieces[pieces.Count - 1][1], to);
                else pieces.Add(new[] { from, to });
            }
            var timings = new SpeechTimings();
            if (pieces.Count == 0) return timings;
            foreach (var piece in WritePieces(wav, joined, pieces, SpeechGap))
                if (piece != null) timings.Add(piece[0], piece[1], piece[2], piece[3]);
            return timings;
        }

        private const int PcmRate = 16000;

        // where the samples of the 16 kHz mono 16-bit file the clip audio was written to start, and how many bytes they take
        private static void FindPcm(FileStream file, out long offset, out long length)
        {
            var reader = new BinaryReader(file);
            file.Position = 0;
            if (file.Length < 12 || Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF") throw new InvalidDataException("The clip audio could not be read again.");
            reader.ReadInt32();
            if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE") throw new InvalidDataException("The clip audio could not be read again.");
            bool format = false;
            while (file.Position + 8 <= file.Length)
            {
                string id = Encoding.ASCII.GetString(reader.ReadBytes(4));
                long size = reader.ReadUInt32();
                long body = file.Position;
                if (body + size > file.Length) size = file.Length - body;
                if (id == "fmt " && size >= 16)
                {
                    short kind = reader.ReadInt16(), channels = reader.ReadInt16();
                    int rate = reader.ReadInt32();
                    reader.ReadInt32();
                    reader.ReadInt16();
                    format = kind == 1 && channels == 1 && rate == PcmRate && reader.ReadInt16() == 16;
                }
                if (id == "data")
                {
                    if (!format) break;
                    offset = body;
                    length = size & ~1L;
                    return;
                }
                file.Position = body + size + (size & 1);
            }
            throw new InvalidDataException("The clip audio could not be read again.");
        }

        // the pieces of the clip audio (start and end in seconds) copied one after another to target with gap seconds of silence between them, streamed so a long clip is never held in memory whole; each piece comes back as its start and end in the clip audio and in target, or null when nothing of it was there
        private static List<double[]> WritePieces(string source, string target, List<double[]> pieces, double gap)
        {
            var placed = new List<double[]>();
            var buffer = new byte[64 * 1024];
            var silence = new byte[(int)(gap * PcmRate) * 2];
            using (var input = File.OpenRead(source))
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                FindPcm(input, out long offset, out long length);
                var writer = new BinaryWriter(output);
                // the sizes are filled in once the samples are written
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(0);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(PcmRate);
                writer.Write(PcmRate * 2);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(0);
                writer.Flush();
                long written = 0;
                foreach (var piece in pieces)
                {
                    long a = Math.Max(0, (long)(piece[0] * PcmRate)) * 2, b = Math.Min(length, (long)(piece[1] * PcmRate) * 2);
                    if (b <= a) { placed.Add(null); continue; }
                    if (written > 0) { output.Write(silence, 0, silence.Length); written += silence.Length; }
                    double at = written / 2.0 / PcmRate;
                    input.Position = offset + a;
                    for (long left = b - a; left > 0;)
                    {
                        int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                        if (read <= 0) throw new InvalidDataException("The clip audio ended early.");
                        output.Write(buffer, 0, read);
                        left -= read;
                        written += read;
                    }
                    placed.Add(new[] { a / 2.0 / PcmRate, b / 2.0 / PcmRate, at, written / 2.0 / PcmRate });
                }
                if (written > int.MaxValue - 36) throw new InvalidDataException("The audio is too long.");
                output.Position = 4;
                writer.Write((int)(36 + written));
                output.Position = 40;
                writer.Write((int)written);
            }
            return placed;
        }

        // two guesses count as the same when they differ only in case, spacing and punctuation
        private static string Plain(string text) => new string((text ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        // below normal priority so a game running beside it keeps the processor; a cancel or the time limit ends the engine, and its progress lines are what the editor shows. the limit is half an hour plus the length of the audio, so a long clip on a slow processor is not cut off while it is still working
        private static string RunTool(Job job, string exe, List<string> args, double audioSeconds)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = ProcessArgs.Join(args.ToArray()),
                WorkingDirectory = EngineDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var proc = Process.Start(psi))
            using (var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30) + TimeSpan.FromSeconds(Math.Max(0, audioSeconds))))
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
                string output = drain.GetAwaiter().GetResult();
                job.Cancel.Token.ThrowIfCancellationRequested();
                if (deadline.IsCancellationRequested) throw new TimeoutException("The speech engine took too long.");
                if (proc.ExitCode != 0)
                {
                    Log.Write("Speech engine exit " + proc.ExitCode + ": " + tail);
                    throw new InvalidOperationException("The speech engine failed (exit " + proc.ExitCode + ").");
                }
                return output;
            }
        }
    }
}

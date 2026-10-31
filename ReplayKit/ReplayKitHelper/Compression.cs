using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // ffmpeg/ffprobe discovery, hardware/software encoder capability probing, video metadata reads, and the compress-then-upload entry point
    internal static class Compression
    {
        private static string FindOnPath(string exeName)
        {
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string candidate;
                try { candidate = Path.Combine(dir, exeName); }
                catch (ArgumentException) { continue; }
                if (File.Exists(candidate)) return candidate;
            }
            return "";
        }

        // search order, highest priority first: the helper directory where Apply installs ffmpeg/ffprobe, then the configured/default clip folders, then PATH for users who manage their own ffmpeg install.
        public static string FindToolInClipDirs(string exeName)
        {
            var candidates = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void AddCandidate(string c) { if (seen.Add(c)) candidates.Add(c); }

            if (!string.IsNullOrEmpty(Constants.HelperRoot)) AddCandidate(Path.Combine(Constants.HelperRoot, exeName));
            foreach (var dir in new[] { AppConfig.GetClipDir(), AppConfig.GetDefaultClipDir() })
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                AddCandidate(Path.Combine(dir, exeName));
            }
            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            try
            {
                string found = FindOnPath(exeName);
                if (!string.IsNullOrEmpty(found)) return Path.GetFullPath(found);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            return "";
        }

        private static readonly object FfmpegAliasLock = new object();

        // a copy of ffmpeg.exe under a replaykit name with the replaykit icon stamped into it, so the encodes read as replaykit in task manager; a hard link would share the blank icon of ffmpeg, so this costs a second copy of it on disk. the copy carries the write time of the ffmpeg it was made from and is always larger than it, which tells it apart from a stale copy and from the hard link older versions made. ffmpeg.exe itself is never touched, and the plain ffmpeg path is used whenever the copy cannot be made
        public static string EnsureFfmpegAlias(string ffmpegPath)
        {
            if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath)) return ffmpegPath;
            string aliasPath = Path.Combine(Path.GetDirectoryName(ffmpegPath), "OBSReplayKit-Encoder.exe");
            string tempPath = aliasPath + ".tmp";
            lock (FfmpegAliasLock)
            {
                try
                {
                    var source = new FileInfo(ffmpegPath);
                    var alias = new FileInfo(aliasPath);
                    if (alias.Exists && alias.LastWriteTimeUtc == source.LastWriteTimeUtc && alias.Length > source.Length) return alias.FullName;
                    byte[] icon = File.ReadAllBytes(Constants.OBS_ICON_PATH);
                    File.Copy(ffmpegPath, tempPath, true);
                    Native.StampIcon(tempPath, icon);
                    File.SetLastWriteTimeUtc(tempPath, source.LastWriteTimeUtc);
                    // a running encode keeps the old alias open, so it cannot be replaced until that encode is done
                    if (alias.Exists) File.Delete(aliasPath);
                    File.Move(tempPath, aliasPath);
                    Log.Write("ffmpeg alias created: " + aliasPath + " (copy of " + ffmpegPath + " with the replaykit icon)");
                    return Path.GetFullPath(aliasPath);
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception)
                {
                    try { File.Delete(tempPath); } catch (Exception cleanup) when (cleanup is IOException || cleanup is UnauthorizedAccessException) { }
                    Log.Write("ffmpeg alias failed: " + ex.Message + " (falling back to ffmpeg.exe)");
                    return ffmpegPath;
                }
            }
        }

        private static bool ToolPathsFresh(JObject paths, DateTime savedAt, DateTime now)
        {
            if (paths == null) return false;
            bool found = !string.IsNullOrEmpty(paths["ffmpeg"]?.Value<string>());
            if ((now - savedAt).TotalMinutes >= (found ? 360 : 10)) return false;
            foreach (string name in new[] { "ffmpeg", "ffprobe" })
            {
                string path = paths[name]?.Value<string>();
                if (string.IsNullOrEmpty(path))
                {
                    if (!string.IsNullOrEmpty(FindToolInClipDirs(name + ".exe"))) return false;
                }
                else if (!File.Exists(path)) return false;
            }
            return true;
        }

        private static bool CapabilitiesFresh(DateTime now) => ToolPathsFresh(Server.State.Capabilities, Server.State.CapabilitiesAt, now);

        private static readonly object ToolPathsLock = new object();
        private static JObject _toolPaths;
        private static DateTime _toolPathsAt;

        // where ffmpeg and ffprobe are, which most routes need and which costs nothing next to the probe of the encoders, so a route that only runs the tools never waits behind that probe; taken from the capabilities once they are made, and kept the same way they are
        public static JObject GetToolPaths()
        {
            DateTime now = DateTime.UtcNow;
            if (CapabilitiesFresh(now)) return Server.State.Capabilities;
            lock (ToolPathsLock)
            {
                if (ToolPathsFresh(_toolPaths, _toolPathsAt, now)) return _toolPaths;
                AppConfig.LoadConfig();
                _toolPaths = new JObject
                {
                    ["ffmpeg"] = EnsureFfmpegAlias(FindToolInClipDirs("ffmpeg.exe")),
                    ["ffprobe"] = FindToolInClipDirs("ffprobe.exe"),
                };
                _toolPathsAt = now;
                return _toolPaths;
            }
        }

        public static JObject GetHelperCapabilities(bool refresh = false)
        {
            DateTime now = DateTime.UtcNow;
            if (!refresh && CapabilitiesFresh(now))
                return Server.State.Capabilities;

            // CapabilitiesLock nests outside ConfigLock/ClipsMetaLock (LoadConfig below acquires those internally), never the reverse, so this cant deadlock -- double-checked since a concurrent cache-miss caller may have already refreshed by the time this gets the lock.
            lock (Server.State.CapabilitiesLock)
            {
                if (!refresh && CapabilitiesFresh(now))
                    return Server.State.Capabilities;

                AppConfig.LoadConfig();
                string ffmpegRaw = FindToolInClipDirs("ffmpeg.exe");
                string ffmpeg = EnsureFfmpegAlias(ffmpegRaw);

                // detect everything the local ffmpeg can actualy drive -- hw encoders are probed with a real tiny encode since compiled-in does not mean works-at-runtime, sw encoders are checked against the ffmpeg encoder list. av1 is deliberately not probed since most iphones and plenty of android devices have no av1 decoder at all, so it never belongs in either priority chain below.
                var encoders = new JObject
                {
                    ["hevc_nvenc"] = TestHwEncoderAvailable(ffmpeg, "hevc_nvenc"),
                    ["h264_nvenc"] = TestHwEncoderAvailable(ffmpeg, "h264_nvenc"),
                    ["hevc_amf"] = TestHwEncoderAvailable(ffmpeg, "hevc_amf"),
                    ["h264_amf"] = TestHwEncoderAvailable(ffmpeg, "h264_amf"),
                    ["hevc_qsv"] = TestHwEncoderAvailable(ffmpeg, "hevc_qsv"),
                    ["h264_qsv"] = TestHwEncoderAvailable(ffmpeg, "h264_qsv"),
                    ["libx265"] = TestSwEncoderLinked(ffmpeg, "libx265"),
                    ["libx264"] = TestSwEncoderLinked(ffmpeg, "libx264"),
                };
                // pick the best fast and smaller encoders for this machine. fast is gpu-first (hevc > h264, nvidia > amd > intel) so games keep the cpu free; falls thru to libx264 veryfast if no hw encoder works at all. smaller is cpu-first by design -- sw encoders are ~2-3x more bit-efficient than hw at the same perceived quality, and the user trades wall time for file size when they pick this mode. libx265 wins when present; libx264 is the last resort if the build was stripped down.
                string fast = "";
                foreach (var cand in new[] { "hevc_nvenc", "hevc_amf", "hevc_qsv", "h264_nvenc", "h264_amf", "h264_qsv", "libx264" })
                {
                    if (encoders[cand].Value<bool>()) { fast = cand; break; }
                }
                string smaller = "";
                foreach (var cand in new[] { "libx265", "libx264" })
                {
                    if (encoders[cand].Value<bool>()) { smaller = cand; break; }
                }

                var caps = new JObject
                {
                    ["ffmpeg"] = ffmpeg,
                    ["ffprobe"] = FindToolInClipDirs("ffprobe.exe"),
                    ["logging"] = Server.State.LogEnabled,
                    ["compressTmp"] = Constants.COMPRESS_TMP_DIR,
                    ["authDir"] = Constants.AUTH_DIR,
                    ["encoders"] = encoders,
                    ["fastEncoder"] = fast,
                    ["smallerEncoder"] = smaller,
                    ["cpuCount"] = Environment.ProcessorCount,
                };
                Log.Write(string.Format("capabilities: fast={0} smaller={1} hevcn={2} h264n={3} hevca={4} h264a={5} hevcq={6} h264q={7} x265={8}",
                    fast, smaller,
                    encoders["hevc_nvenc"].Value<bool>() ? 1 : 0, encoders["h264_nvenc"].Value<bool>() ? 1 : 0,
                    encoders["hevc_amf"].Value<bool>() ? 1 : 0, encoders["h264_amf"].Value<bool>() ? 1 : 0,
                    encoders["hevc_qsv"].Value<bool>() ? 1 : 0, encoders["h264_qsv"].Value<bool>() ? 1 : 0,
                    encoders["libx265"].Value<bool>() ? 1 : 0));
                Server.State.Capabilities = caps;
                Server.State.CapabilitiesAt = now;
                return caps;
            }
        }

        // Exercise the same options as Fast, including enough frames to initialize B-frames and lookahead.
        public static bool TestHwEncoderAvailable(string ffmpegPath, string encoder)
        {
            if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath)) return false;
            try
            {
                var arguments = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=black:s=256x256:r=30:d=1", "-pix_fmt", "yuv420p" };
                arguments.AddRange(CompressionProfile.Arguments(encoder, "fast"));
                arguments.AddRange(new[] { "-frames:v", "30", "-f", "null", "-" });
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = ProcessArgs.Join(arguments.ToArray()),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                };
                using (var proc = Process.Start(psi))
                {
                    try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; }
                    catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) { }
                    var stdout = CompressionOutput.Read(proc.StandardOutput, proc);
                    var stderr = CompressionOutput.Read(proc.StandardError, proc);
                    if (!proc.WaitForExit(5000))
                    {
                        try { proc.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
                        Log.Write("Compression encoder probe timed out: " + encoder);
                        return false;
                    }
                    var output = stdout.GetAwaiter().GetResult();
                    var error = stderr.GetAwaiter().GetResult();
                    bool available = proc.ExitCode == 0 && !output.Exceeded && !error.Exceeded;
                    if (!available) Log.Write("Compression encoder unavailable: " + encoder + " exit=" + proc.ExitCode + " " + error.Text);
                    return available;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            {
                Log.Write("Compression encoder probe failed: " + encoder + " " + ex.Message);
                return false;
            }
        }

        // cheap sw-encoder availability check -- if ffmpeg lists it as a known encoder, itll run. no real test encode here becuase libx264 / libx265 have no runtime dependencies that could make them fail per-machine the way hw encoders do.
        public static bool TestSwEncoderLinked(string ffmpegPath, string encoder)
        {
            if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath)) return false;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = "-hide_banner -loglevel quiet -encoders",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                };
                using (var proc = Process.Start(psi))
                {
                    var stdout = proc.StandardOutput.ReadToEndAsync();
                    var stderr = proc.StandardError.ReadToEndAsync();
                    if (!proc.WaitForExit(5000))
                    {
                        try { proc.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
                        return false;
                    }
                    if (proc.ExitCode != 0) return false;
                    string outText = stdout.GetAwaiter().GetResult();
                    stderr.GetAwaiter().GetResult();
                    return Regex.IsMatch(outText, @"(?m)^\s*[VAS][^\s]*\s+" + Regex.Escape(encoder) + @"\s");
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        public static string FindCompressionFfmpeg() => GetToolPaths()["ffmpeg"]?.Value<string>() ?? "";
        public static string FindCompressionFfprobe() => GetToolPaths()["ffprobe"]?.Value<string>() ?? "";

        public sealed class NativeCaptureResult
        {
            public int ExitCode;
            public List<string> Output = new List<string>();
        }

        // reads both streams concurrently before WaitForExit so a chatty child cant deadlock on a full pipe buffer, then joins stdout and stderr for the line scans the callers do; the optional token kills the child outright
        public static NativeCaptureResult InvokeNativeCapture(string exe, IEnumerable<string> arguments, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken), int timeoutMs = 30000, bool lowPriority = false)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = ProcessArgs.Join(arguments.ToArray()),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            cancellationToken.ThrowIfCancellationRequested();
            using (var proc = Process.Start(psi))
            using (var deadline = new System.Threading.CancellationTokenSource(timeoutMs))
            using (var stopping = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token))
            using (stopping.Token.Register(() =>
            {
                try { if (!proc.HasExited) proc.Kill(); }
                catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            }))
            {
                // work done ahead of time runs behind whatever else the machine is doing, a game most of all
                if (lowPriority)
                {
                    try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; }
                    catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) { }
                }
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();
                proc.WaitForExit();
                string combined = stdoutTask.Result + stderrTask.Result;
                cancellationToken.ThrowIfCancellationRequested();
                if (deadline.IsCancellationRequested) throw new TimeoutException("External tool exceeded its time limit.");
                return new NativeCaptureResult { ExitCode = proc.ExitCode, Output = combined.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList() };
            }
        }

        // like InvokeNativeCapture, but stdout is read line by line while the child runs so a caller can follow the -progress output of ffmpeg as it is written; only stderr comes back in Output, since the progress block is no part of an error message
        public static NativeCaptureResult InvokeNativeProgress(string exe, IEnumerable<string> arguments, Action<string> onStdoutLine, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken), int timeoutMs = 30000, string workingDirectory = null)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = workingDirectory ?? "",
                Arguments = ProcessArgs.Join(arguments.ToArray()),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            cancellationToken.ThrowIfCancellationRequested();
            using (var proc = Process.Start(psi))
            using (var deadline = new System.Threading.CancellationTokenSource(timeoutMs))
            using (var stopping = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token))
            using (stopping.Token.Register(() =>
            {
                try { if (!proc.HasExited) proc.Kill(); }
                catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            }))
            {
                var stderrTask = proc.StandardError.ReadToEndAsync();
                try
                {
                    string line;
                    while ((line = proc.StandardOutput.ReadLine()) != null)
                    {
                        // a callback that throws must not leave the encode running with nobody reading it
                        try { onStdoutLine(line); }
                        catch (Exception ex) { Log.Write("progress callback failed: " + ex.Message); }
                    }
                }
                catch (IOException ex) { Log.Write("progress read ended: " + ex.Message); }
                proc.WaitForExit();
                string errText = stderrTask.Result;
                cancellationToken.ThrowIfCancellationRequested();
                if (deadline.IsCancellationRequested) throw new TimeoutException("External tool exceeded its time limit.");
                return new NativeCaptureResult { ExitCode = proc.ExitCode, Output = errText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList() };
            }
        }

        public static double GetVideoDurationSec(string ffmpeg, string path)
        {
            try
            {
                var probe = InvokeNativeCapture(ffmpeg, new[] { "-hide_banner", "-i", path });
                string text = string.Join("\n", probe.Output);
                var m = Regex.Match(text, @"Duration:\s*(\d+):(\d{2}):(\d{2}(?:\.\d+)?)");
                if (!m.Success)
                {
                    string trimmed = Regex.Replace(text, @"\s+", " ");
                    Log.Write("Get-VideoDurationSec: no Duration line from ffmpeg exit=" + probe.ExitCode + " output=" + trimmed.Substring(0, Math.Min(220, trimmed.Length)));
                    return 0;
                }
                int hours = int.Parse(m.Groups[1].Value);
                int mins = int.Parse(m.Groups[2].Value);
                double secs = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                return (hours * 3600.0) + (mins * 60.0) + secs;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception)
            {
                Log.Write("Get-VideoDurationSec failed: " + ex.Message);
                return 0;
            }
        }

        public sealed class VideoMetadata
        {
            public bool Ok;
            public double Duration;
            public string Source;
        }

        public static VideoMetadata GetVideoMetadata(string ffprobe, string ffmpeg, string path)
        {
            if (!string.IsNullOrWhiteSpace(ffprobe) && File.Exists(ffprobe))
            {
                try
                {
                    var probe = InvokeNativeCapture(ffprobe, new[] { "-v", "error", "-show_entries", "format=duration", "-of", "json", path });
                    if (probe.ExitCode == 0 && probe.Output.Count > 0)
                    {
                        var obj = JObject.Parse(string.Join("\n", probe.Output));
                        string durationStr = obj["format"]?["duration"]?.Value<string>();
                        if (!string.IsNullOrEmpty(durationStr))
                        {
                            double duration = double.Parse(durationStr, CultureInfo.InvariantCulture);
                            if (duration > 0) return new VideoMetadata { Ok = true, Duration = duration, Source = "ffprobe" };
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is FormatException)
                {
                    Log.Write("ffprobe metadata failed: " + ex.Message);
                }
            }
            double durationFallback = GetVideoDurationSec(ffmpeg, path);
            return new VideoMetadata { Ok = durationFallback > 0, Duration = durationFallback, Source = "ffmpeg" };
        }

        public sealed class CompressMarker
        {
            public bool Ok;
            public string Mode = "";
            public long Ts;
            public long Pre;
        }

        private static string ReadCompressComment(string ffprobe, string path)
        {
            var info = new ProcessStartInfo
            {
                FileName = ffprobe,
                Arguments = ProcessArgs.Join(new[] { "-v", "error", "-show_entries", "format_tags=comment", "-of", "default=nokey=1:noprint_wrappers=1", path }),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(info);
            using var deadline = new System.Threading.CancellationTokenSource(30000);
            using var registration = deadline.Token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            });
            var stdout = CompressionOutput.Read(process.StandardOutput, process);
            var stderr = CompressionOutput.Read(process.StandardError, process);
            process.WaitForExit();
            var output = stdout.GetAwaiter().GetResult();
            var error = stderr.GetAwaiter().GetResult();
            if (deadline.IsCancellationRequested || process.ExitCode != 0 || output.Exceeded || output.Truncated || error.Exceeded) return null;
            return output.Text.Trim();
        }

        // reads the hidden compress-history marker that CompressOverwrite writes into the mp4s comment metadata atom. empty Mode = no marker found / not one of ours. cheap: ffprobe with -show_entries format_tags=comment touches only the header atoms, no decode -- about 30-60ms per file on a local ssd. the marker prefix is "obs-replaykit_compress_v2_" -- the v2 bump invalidates every clip processed by the old (broken) pipeline so it becomes eligible for re-compression under the dynamic/av1-aware pipeline; v1 markers are intentionally treated as "no marker" so those clips show as compressable again.
        public static CompressMarker GetCompressMarker(string path)
        {
            var result = new CompressMarker();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return result;
            string ffprobe = GetToolPaths()["ffprobe"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(ffprobe) || !File.Exists(ffprobe)) return result;
            try
            {
                string raw = ReadCompressComment(ffprobe, path);
                if (raw == null) return result;
                result.Ok = true;
                const string prefix = "obs-replaykit_compress_v2_";
                if (!raw.StartsWith(prefix)) return result;
                var parts = raw.Substring(prefix.Length).Split(new[] { ':' }, 3);
                if (parts.Length < 1) return result;
                string mode = parts[0].Trim().ToLowerInvariant();
                if (mode != "fast" && mode != "slow") return result;
                result.Mode = mode;
                if (parts.Length >= 2 && long.TryParse(parts[1].Trim(), out long tsVal) && tsVal > 0) result.Ts = tsVal;
                if (parts.Length >= 3 && long.TryParse(parts[2].Trim(), out long preVal) && preVal > 0) result.Pre = preVal;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception)
            {
                Log.Write("Get-CompressMarker failed for " + path + ": " + ex.Message);
            }
            return result;
        }

        // compresses a temp copy down to a size that fits the upload cap, then uploads that copy; the original is never touched, and the finish goes through Upload.HandleUploadCompletion like a plain upload
        public static JObject StartCompressedStreamableUpload(Clips.SafeClipPath selected)
        {
            AppConfig.LoadConfig();
            string requestId = UploadState.NewRequestId();

            if (selected == null || !File.Exists(selected.Full))
                return new JObject { ["ok"] = false, ["message"] = "Clip not found" };

            var auth = Upload.CaptureAuth();
            long effCap = auth.SizeCap;
            if (effCap <= 0) return Upload.StartStreamableUpload(selected, auth: auth);

            using (var reservation = JobCoordinator.TryReserve(requestId, selected.Name, selected.Full, "compress-upload", out string busy))
            {
                if (reservation == null) return new JObject { ["ok"] = false, ["busy"] = true, ["message"] = busy };

                var caps = GetToolPaths();
                string ffmpeg = caps["ffmpeg"]?.Value<string>();
                if (string.IsNullOrWhiteSpace(ffmpeg))
                {
                    string msg = "ffmpeg.exe not found in your configured clip folder.";
                    UploadState.SetUploadState(requestId: requestId, state: "error", active: false, clipName: selected.Name, error: msg);
                    return new JObject { ["ok"] = false, ["message"] = msg };
                }
                string ffprobe = caps["ffprobe"]?.Value<string>();

                var metadata = GetVideoMetadata(ffprobe, ffmpeg, selected.Full);
                double duration = metadata.Duration;
                if (duration < 1)
                {
                    string msg = "Could not read video duration for compression.";
                    UploadState.SetUploadState(requestId: requestId, state: "error", active: false, clipName: selected.Name, error: msg);
                    return new JObject { ["ok"] = false, ["message"] = msg };
                }

                // compression shrinks bytes, not runtime -- a clip over streamables 10-min free limit still fails transcode, so stop it here too.
                if (auth.DurationLimited && duration > Constants.STREAMABLE_FREE_MAX_DURATION_SEC)
                {
                    int total = (int)Math.Round(duration);
                    string msg = "Clip is " + (total / 60) + ":" + (total % 60).ToString("D2") + " long. Streamable's limit is 10 minutes -- trim it shorter first.";
                    UploadState.SetUploadState(requestId: requestId, state: "error", active: false, clipName: selected.Name, error: msg);
                    return new JObject { ["ok"] = false, ["message"] = msg, ["tooLong"] = true, ["durationSec"] = total };
                }

                long targetBytes = (long)Math.Floor(effCap * 0.88);
                if (targetBytes < 5L * 1024 * 1024)
                {
                    string msg = "Upload size limit is too small for automatic compression.";
                    UploadState.SetUploadState(requestId: requestId, state: "error", active: false, clipName: selected.Name, error: msg);
                    return new JObject { ["ok"] = false, ["message"] = msg };
                }

                int totalKbps = (int)Math.Floor((targetBytes * 8.0 / duration) / 1000.0);
                int audioKbps = totalKbps < 400 ? 48 : totalKbps < 900 ? 64 : 96;
                int videoKbps = totalKbps - audioKbps;
                if (videoKbps < 120)
                {
                    long capMb = effCap / 1024 / 1024;
                    long sizeMb = (long)Math.Ceiling(new FileInfo(selected.Full).Length / 1024.0 / 1024.0);
                    string msg = "Clip is too long to compress under " + capMb + " MB without going below a safe video bitrate";
                    UploadState.SetUploadState(requestId: requestId, state: "error", active: false, clipName: selected.Name, error: msg);
                    return new JObject { ["ok"] = false, ["message"] = msg, ["tooBig"] = true, ["sizeMb"] = sizeMb, ["capMb"] = capMb };
                }

                Directory.CreateDirectory(Constants.COMPRESS_TMP_DIR);
                string safeBase = Regex.Replace(Path.GetFileNameWithoutExtension(selected.Name), "[^A-Za-z0-9_.-]+", "_");
                if (string.IsNullOrWhiteSpace(safeBase)) safeBase = "clip";
                string tempOut = Path.Combine(Constants.COMPRESS_TMP_DIR, safeBase + "_" + requestId + ".mp4");
                string passLog = Path.Combine(Constants.COMPRESS_TMP_DIR, "ffmpeg-pass-" + requestId);

                UploadState.SetUploadState(
                    requestId: requestId, state: "compressing", active: true, clipName: selected.Name,
                    startedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), url: "", error: "", phase: "analyzing",
                    percent: 1, kind: "compress-upload");

                Log.Write("Start-CompressedStreamableUpload clip=" + selected.Name + " ffmpeg=" + ffmpeg + " ffprobe=" + ffprobe +
                    " metadata=" + metadata.Source + " targetKbps=" + totalKbps + " videoKbps=" + videoKbps + " audioKbps=" + audioKbps, "compress", requestId);

                string selectedName = selected.Name;
                string selectedFull = selected.Full;

                reservation.Start(() => CompressedUploadWorker.Run(requestId, ffmpeg, selectedFull, tempOut, passLog, effCap, videoKbps, audioKbps, duration, auth.Cookies, auth.Required), t =>
                {
                    try { if (File.Exists(tempOut)) File.Delete(tempOut); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                    Upload.HandleUploadCompletion(t, requestId, selectedName, sourcePath: selectedFull, auth: auth);
                });

                return new JObject { ["ok"] = true, ["state"] = "compressing", ["clip"] = selectedName, ["requestId"] = requestId, ["message"] = "Compressing temp copy and uploading" };
            }
        }
    }
}

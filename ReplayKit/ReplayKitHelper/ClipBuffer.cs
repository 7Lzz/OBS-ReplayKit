using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // where the last stretch of gameplay a clip is cut from is kept: obs holds it in memory as its replay buffer, or the plugin records it to disk in short pieces and this joins the newest of them into the clip
    internal static class ClipBuffer
    {
        public const string Disk = "disk";
        public const string Memory = "memory";
        private const int MaxSegments = 500;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalKilobytes);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetLongPathName(string shortPath, StringBuilder longPath, uint bufferLength);

        private static readonly Lazy<int> InstalledGb = new Lazy<int>(ReadInstalledGb);
        private static readonly object StateGate = new object();
        private static readonly object SaveGate = new object();
        private static bool _diskActive;
        // the save that runs or the last one that ended, so a clips page that opens or comes back while a clip is saved still shows its card, and learns the name of the clip it waits for; the plugin numbers its saves, so a start that arrives after its save already ended cannot open it again
        private static long _saveId = -1;
        private static long _endedSaveId = -1;
        private static long _saveStartedAt;
        private static long _saveEndedAt;
        private static string _savedName = "";
        // a save nobody ever ends stops counting as running, the longest a join may take and then some
        private const long SaveForgetMs = 150000;

        public static string DiskDir => Path.Combine(Constants.SCRATCH_DIR, "clip-buffer");

        // the memory sticks that are in the machine, not what windows has left after reserving some, so a 16 gb machine reads as 16
        private static int ReadInstalledGb()
        {
            try
            {
                if (!GetPhysicallyInstalledSystemMemory(out ulong kb) || kb == 0) return 0;
                return (int)Math.Round(kb / (1024.0 * 1024.0));
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException) { return 0; }
        }

        // 0 when windows does not say
        public static int SystemMemoryGb => InstalledGb.Value;

        // disk unless memory was picked; the recommended choice of older builds now reads as disk
        public static string EffectiveMode(JObject settings) => settings?["recordingBuffer"]?.Value<string>() == Memory ? Memory : Disk;

        public static JArray Options()
        {
            return new JArray(
                new JObject { ["value"] = Disk, ["label"] = "Disk", ["blurb"] = "Keeps the buffer on your drive. Uses very little RAM, but writes to the drive while clipping is on." },
                new JObject { ["value"] = Memory, ["label"] = "Memory", ["blurb"] = "Keeps the buffer in RAM. Saves clips fastest but uses the most RAM. If Disk causes stutters, slow saves or clips that fail to save, Memory can help." });
        }

        // the plugin says whenever its disk buffer starts or stops, and again on every settings poll
        public static void ReportDiskState(bool active)
        {
            lock (StateGate) _diskActive = active;
        }

        // the replay buffer requests the helper sends to obs-websocket; null when obs should answer them itself
        public static ObsWebSocketResult TryInvoke(string requestType)
        {
            if (requestType != "GetReplayBufferStatus" && requestType != "StartReplayBuffer" && requestType != "StopReplayBuffer" && requestType != "ToggleReplayBuffer" && requestType != "SaveReplayBuffer")
                return null;
            if (EffectiveMode(ReplaykitSettings.ReadSettings()) != Disk) return null;
            bool active;
            lock (StateGate) active = _diskActive;
            switch (requestType)
            {
                case "GetReplayBufferStatus":
                    return Answer(requestType, active);
                case "StartReplayBuffer":
                    return PipeClient.SendClipBuffer("on") ? Answer(requestType, true) : PluginGone(requestType);
                case "StopReplayBuffer":
                    return PipeClient.SendClipBuffer("off") ? Answer(requestType, false) : PluginGone(requestType);
                case "ToggleReplayBuffer":
                    return PipeClient.SendClipBuffer("toggle") ? Answer(requestType, !active) : PluginGone(requestType);
                default:
                    if (!active) return new ObsWebSocketResult { Ok = false, RequestType = requestType, Code = 501, Message = "Clipping is off." };
                    return PipeClient.SendClipSave() ? Answer(requestType, true) : PluginGone(requestType);
            }
        }

        private static ObsWebSocketResult Answer(string requestType, bool active) =>
            new ObsWebSocketResult { Ok = true, RequestType = requestType, Code = 100, Data = new JObject { ["outputActive"] = active } };

        private static ObsWebSocketResult PluginGone(string requestType) =>
            new ObsWebSocketResult { Ok = false, Unavailable = true, RequestType = requestType, Message = "ReplayKit's OBS plugin is not connected." };

        private static JObject Fail(string message) => new JObject { ["ok"] = false, ["message"] = message };

        public static JObject SaveStatus()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (StateGate)
                return new JObject { ["active"] = _saveStartedAt > 0 && _saveEndedAt == 0 && now - _saveStartedAt < SaveForgetMs, ["startedAt"] = _saveStartedAt, ["endedAt"] = _saveEndedAt, ["name"] = _savedName };
        }

        // the plugin says so on the clip press, before it has the pieces together
        public static void MarkSaveStarted(long id)
        {
            lock (StateGate)
            {
                if (id == _endedSaveId || (id == _saveId && _saveEndedAt == 0)) return;
                _saveId = id;
                _saveStartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _saveEndedAt = 0;
                _savedName = "";
            }
        }

        public static void MarkSaveEnded(long id, string name)
        {
            lock (StateGate)
            {
                if (id == _endedSaveId || id != _saveId) return;
                _endedSaveId = id;
                _saveEndedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _savedName = name ?? "";
            }
        }

        public static JObject SaveSegments(JObject body)
        {
            long id = body?["id"]?.Value<long?>() ?? 0;
            MarkSaveStarted(id);
            var result = SaveSegmentsCore(body, id);
            // a save that failed ends here; one that worked already ended the moment its file was in place
            MarkSaveEnded(id, "");
            return result;
        }

        // joins the newest pieces the plugin recorded into one clip of the asked length, in the clip folder under the name obs would have given it; the pieces stay where they are, since they are still the buffer
        private static JObject SaveSegmentsCore(JObject body, long id)
        {
            var segments = body?["segments"] as JArray;
            int seconds = body?["seconds"]?.Value<int?>() ?? 0;
            string name = body?["name"]?.Value<string>() ?? "";
            if (segments == null || segments.Count == 0 || segments.Count > MaxSegments) return Fail("There is no buffered video to save yet.");
            if (seconds < 1 || seconds > 3600) return Fail("The clip length is not valid.");
            string ext = Path.GetExtension(name).ToLowerInvariant();
            if (name.Length == 0 || name.Length > 200 || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || (ext != ".mp4" && ext != ".mkv" && ext != ".mov"))
                return Fail("The clip name is not valid.");

            Directory.CreateDirectory(DiskDir);
            string root = LongPath(Path.GetFullPath(DiskDir)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var files = new List<string>();
            foreach (var item in segments)
            {
                string path;
                try { path = LongPath(Path.GetFullPath(item?.Value<string>() ?? "")); }
                catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return Fail("A buffered piece has a bad path."); }
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !string.Equals(Path.GetExtension(path), ".mp4", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                    return Fail("A buffered piece is missing.");
                files.Add(path);
            }

            string clipDir = AppConfig.GetClipDir();
            if (string.IsNullOrWhiteSpace(clipDir) || !Directory.Exists(clipDir)) return Fail("The clip folder was not found.");
            string ffmpeg = Compression.FindCompressionFfmpeg();
            string ffprobe = Compression.FindCompressionFfprobe();
            if (!File.Exists(ffmpeg) || !File.Exists(ffprobe)) return Fail("ffmpeg is missing, so the clip could not be saved.");

            if (!Monitor.TryEnter(SaveGate)) return Fail("A clip is already being saved.");
            string list = null;
            string part = null;
            try
            {
                // the newest piece was closed a moment ago, and its process may still be writing the end of the file
                WaitUntilClosed(files[files.Count - 1], 5000);
                var lengths = new List<double>();
                foreach (string file in files)
                {
                    double length = ProbeSeconds(ffprobe, file);
                    if (length < 0) return Fail("A buffered piece could not be read.");
                    lengths.Add(length);
                }
                string codec = ProbeVideoCodec(ffprobe, files[files.Count - 1]);

                // the clip starts the clip length before the end: whole pieces in front of that are left out, and the first piece kept is entered part way. a seek on the joined input is ignored, so the start is put on the piece itself
                double start = Math.Max(0, lengths.Sum() - seconds);
                int first = 0;
                while (first < files.Count - 1 && start >= lengths[first])
                {
                    start -= lengths[first];
                    first++;
                }
                var entries = new StringBuilder();
                for (int i = first; i < files.Count; i++)
                {
                    entries.Append("file '").Append(files[i].Replace('\\', '/').Replace("'", "'\\''")).Append("'\n");
                    if (i == first && start > 0.05) entries.Append("inpoint ").Append(start.ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');
                }
                list = Path.Combine(Constants.SCRATCH_DIR, "clip-buffer-" + Guid.NewGuid().ToString("N") + ".txt");
                File.WriteAllText(list, entries.ToString(), new UTF8Encoding(false));
                string target = UniquePath(Path.Combine(clipDir, name));
                part = target + ".rkpart";
                bool mp4Family = ext == ".mp4" || ext == ".mov";
                var argv = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-f", "concat", "-safe", "0", "-i", list, "-map", "0", "-c", "copy", "-avoid_negative_ts", "make_zero" };
                // the same tag the hevc fix-up gives a saved replay, so phones and editors that only read hvc1 play it
                if (mp4Family && codec == "hevc") argv.AddRange(new[] { "-tag:v", "hvc1" });
                if (mp4Family) argv.AddRange(new[] { "-movflags", "+faststart" });
                argv.AddRange(new[] { "-f", ext == ".mkv" ? "matroska" : ext == ".mov" ? "mov" : "mp4", part });
                var run = Compression.InvokeNativeCapture(ffmpeg, argv, CancellationToken.None, 120000);
                if (run.ExitCode != 0 || !File.Exists(part))
                {
                    Log.Write("clip buffer save: ffmpeg exit " + run.ExitCode + ": " + string.Join(" | ", run.Output.Take(5)));
                    return Fail("The clip could not be written.");
                }
                File.Move(part, target);
                part = null;
                // said as soon as the file is in the folder, so a page that already lists the clip also reads that its save ended
                MarkSaveEnded(id, Path.GetFileName(target));
                Log.Write("clip buffer save: " + (files.Count - first) + " of " + files.Count + " piece(s), " + lengths.Sum().ToString("0.0", CultureInfo.InvariantCulture) + " s buffered -> " + target);
                return new JObject { ["ok"] = true, ["path"] = target };
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Write("clip buffer save: " + ex.Message);
                return Fail("The clip could not be written: " + ex.Message);
            }
            finally
            {
                if (list != null) TryDelete(list);
                if (part != null) TryDelete(part);
                Monitor.Exit(SaveGate);
            }
        }

        private static void WaitUntilClosed(string path, int timeoutMs)
        {
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                try
                {
                    using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) return;
                }
                catch (IOException)
                {
                    if (DateTime.UtcNow >= until) return;
                    Thread.Sleep(100);
                }
            }
        }

        private static double ProbeSeconds(string ffprobe, string file)
        {
            var run = Compression.InvokeNativeCapture(ffprobe, new[] { "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", file }, CancellationToken.None, 15000);
            string line = run.Output.LastOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim();
            return run.ExitCode == 0 && double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds >= 0 ? seconds : -1;
        }

        private static string ProbeVideoCodec(string ffprobe, string file)
        {
            var run = Compression.InvokeNativeCapture(ffprobe, new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name", "-of", "default=nw=1:nk=1", file }, CancellationToken.None, 15000);
            return run.ExitCode == 0 ? (run.Output.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? "") : "";
        }

        // the temp folder can be named in its short 8.3 form on one side and its long form on the other
        private static string LongPath(string path)
        {
            var buffer = new StringBuilder(1024);
            uint length = GetLongPathName(path, buffer, (uint)buffer.Capacity);
            return length > 0 && length < buffer.Capacity ? buffer.ToString() : path;
        }

        private static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path), stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            for (int i = 2; i < 1000; i++)
            {
                string next = Path.Combine(dir, stem + " (" + i + ")" + ext);
                if (!File.Exists(next)) return next;
            }
            throw new IOException("No free clip name was found.");
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Log.Write("clip buffer: could not delete " + path + ": " + ex.Message); }
        }
    }
}

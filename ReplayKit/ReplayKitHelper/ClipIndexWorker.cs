using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // rebuilds clips_index.json (per-clip ffprobe metadata cache: duration/width/height/fps/codec) for whatever in the clip folder isnt already indexed at its current size+mtime. runs as an in-process background task kicked off by Clips.StartClipIndexRepairIfNeeded; it stays self-contained (no Server.State access) so the slow ffprobe work needs no locking of its own
    internal static class ClipIndexWorker
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private static double ConvertRateToDouble(string rate)
        {
            if (string.IsNullOrWhiteSpace(rate) || rate == "0/0") return 0.0;
            if (rate.Contains("/"))
            {
                var parts = rate.Split('/');
                if (parts.Length != 2) return 0.0;
                if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double num)) return 0.0;
                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double den)) return 0.0;
                if (den <= 0) return 0.0;
                return num / den;
            }
            return double.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out double plain) ? plain : 0.0;
        }

        private static JObject ReadExistingIndex(string path)
        {
            var entries = new JObject();
            if (!File.Exists(path)) return entries;
            try
            {
                string raw = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(raw)) return entries;
                var parsed = JObject.Parse(raw);
                if (parsed["clips"] is JObject clips)
                {
                    foreach (var prop in clips.Properties()) entries[prop.Name] = prop.Value;
                }
            }
            catch (JsonException) { }
            return entries;
        }

        private static bool TestEntryCurrent(JToken entry, FileInfo file)
        {
            if (entry == null) return false;
            if (!string.Equals(entry["path"]?.Value<string>(), file.FullName, StringComparison.OrdinalIgnoreCase)) return false;
            var size = entry["size"];
            var mtimeTicks = entry["mtimeTicks"];
            if (size == null || mtimeTicks == null) return false;
            return size.Value<long>() == file.Length && mtimeTicks.Value<long>() == file.LastWriteTimeUtc.Ticks;
        }

        private static bool TestEntryReusable(JToken entry, FileInfo file)
        {
            if (!TestEntryCurrent(entry, file)) return false;
            var duration = entry["duration"];
            string codec = entry["codec"]?.Value<string>();
            if (duration != null && duration.Value<double>() > 0 && !string.IsNullOrWhiteSpace(codec)) return true;
            var failedAt = entry["failedAt"];
            if (failedAt == null) return false;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return (now - failedAt.Value<long>()) < 21600;
        }

        private static JObject ReadFfprobeMetadata(string ffprobe, string path)
        {
            if (string.IsNullOrWhiteSpace(ffprobe) || !File.Exists(ffprobe)) return null;
            var psi = new ProcessStartInfo
            {
                FileName = ffprobe,
                Arguments = ProcessArgs.Join(
                    "-v", "error",
                    "-select_streams", "v:0",
                    "-show_entries", "stream=width,height,r_frame_rate,avg_frame_rate,codec_name,codec_tag_string:format=duration",
                    "-of", "json=compact=1",
                    path),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using (var proc = Process.Start(psi))
            {
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();
                if (!proc.WaitForExit(15000))
                {
                    try { proc.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
                    return null;
                }
                string stdout = stdoutTask.GetAwaiter().GetResult();
                stderrTask.GetAwaiter().GetResult();
                if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout)) return null;
                try { return JObject.Parse(stdout); } catch (JsonException) { return null; }
            }
        }

        private static JObject NewIndexEntry(FileInfo file, JObject metadata)
        {
            double duration = 0.0;
            int width = 0, height = 0;
            double fps = 0.0;
            string codec = "", tag = "";
            if (metadata != null)
            {
                try
                {
                    var durToken = metadata["format"]?["duration"];
                    if (durToken != null) duration = double.Parse(durToken.ToString(), CultureInfo.InvariantCulture);
                }
                catch (Exception ex) when (ex is FormatException || ex is OverflowException) { duration = 0.0; }
                try
                {
                    var stream = (metadata["streams"] as JArray)?.FirstOrDefault();
                    if (stream != null)
                    {
                        if (stream["width"] != null) width = stream["width"].Value<int>();
                        if (stream["height"] != null) height = stream["height"].Value<int>();
                        string rate = stream["avg_frame_rate"]?.Value<string>();
                        if (string.IsNullOrEmpty(rate)) rate = stream["r_frame_rate"]?.Value<string>();
                        fps = ConvertRateToDouble(rate);
                        if (fps <= 0) fps = ConvertRateToDouble(stream["r_frame_rate"]?.Value<string>());
                        if (stream["codec_name"] != null) codec = stream["codec_name"].Value<string>();
                        if (stream["codec_tag_string"] != null) tag = stream["codec_tag_string"].Value<string>();
                    }
                }
                catch (Exception ex) when (ex is FormatException || ex is OverflowException) { }
            }
            var entry = new JObject
            {
                ["name"] = file.Name,
                ["path"] = file.FullName,
                ["size"] = file.Length,
                ["mtime"] = new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeSeconds(),
                ["mtimeTicks"] = file.LastWriteTimeUtc.Ticks,
                ["duration"] = Math.Round(Math.Max(0.0, duration), 3),
                ["width"] = width,
                ["height"] = height,
                ["fps"] = Math.Round(Math.Max(0.0, fps), 3),
                ["codec"] = codec,
                ["tag"] = tag,
                ["indexedAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            if (duration <= 0) entry["failedAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return entry;
        }

        // tmp lands in the shared scratch dir, not next to $path, so a crash mid-write never leaves a stray .tmp beside clips_index.json; the atomic MoveFileReplace closes the crash window a delete-then-move would reopen.
        private static void WriteIndexFile(string path, JObject entries)
        {
            string parent = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(parent)) throw new InvalidOperationException("Invalid index path.");
            Directory.CreateDirectory(parent);
            var payload = new JObject
            {
                ["version"] = 1,
                ["updatedAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["clips"] = entries,
            };
            AppConfig.WriteUtf8(path, payload.ToString(Formatting.Indented));
        }

        // returns the names actually (re)probed this pass (not reused from the existing index) -- newest-first, same order as the enumeration below -- so a caller can proactively warm up just-created clips without also rescanning every clip already sitting in the folder.
        public static List<string> Run(string clipDir, string indexPath, string ffprobe, IEnumerable<string> allowedExts, int maxFiles)
        {
            var freshlyIndexed = new List<string>();
            try
            {
                if (string.IsNullOrWhiteSpace(clipDir) || string.IsNullOrWhiteSpace(indexPath)) return freshlyIndexed;
                string root = Path.GetFullPath(clipDir);
                if (!Directory.Exists(root)) return freshlyIndexed;
                string indexFull = Path.GetFullPath(indexPath);
                var allowed = new HashSet<string>(allowedExts, StringComparer.OrdinalIgnoreCase);

                var existing = ReadExistingIndex(indexFull);
                var entries = new JObject();
                int count = 0;

                var files = Clips.LibraryFiles(root)
                    .Where(f => allowed.Contains(f.Extension) &&
                                !f.Name.StartsWith("_replaykit_", StringComparison.OrdinalIgnoreCase) &&
                                !f.Name.StartsWith("_compress_tmp_", StringComparison.OrdinalIgnoreCase) &&
                                !f.Name.StartsWith("_trim_tmp_", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => f.LastWriteTimeUtc);

                foreach (var file in files)
                {
                    if (maxFiles > 0 && count >= maxFiles) break;
                    count++;
                    var old = existing[file.Name];
                    if (TestEntryReusable(old, file))
                    {
                        entries[file.Name] = old;
                        continue;
                    }
                    // a clip with several audio tracks gets its track flags put right before anything reads it; a file obs still holds open stays out of the index so the next pass looks at it again
                    if (Mp4Tracks.NormalizeAudio(file.FullName) == Mp4Tracks.Fix.Busy) continue;
                    file.Refresh();
                    var metadata = ReadFfprobeMetadata(ffprobe, file.FullName);
                    entries[file.Name] = NewIndexEntry(file, metadata);
                    freshlyIndexed.Add(file.Name);
                }
                WriteIndexFile(indexFull, entries);
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(indexPath + ".error.txt", ex.Message, Utf8NoBom); }
                catch (Exception ex2) when (ex2 is IOException || ex2 is UnauthorizedAccessException) { }
            }
            return freshlyIndexed;
        }
    }
}

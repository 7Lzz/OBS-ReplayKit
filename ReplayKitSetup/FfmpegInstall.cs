using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace ReplayKitSetup
{
    // install ffmpeg.exe + ffprobe.exe into the helper dir. ported from obs_replaykit/ffmpeg_install.py.
    public static class FfmpegInstall
    {
        // next to OBSReplayKit.exe (the ReplayKitHelper server) under the consolidated obs-replayKit/ tree; Compression.FindToolInClipDirs probes Constants.HelperRoot first.
        private static readonly string HelperDir = Path.Combine(Config.OBS_CONFIG, "obs-replayKit", "scripts", "helper");
        private static readonly string FfmpegDst = Path.Combine(HelperDir, "ffmpeg.exe");
        private static readonly string FfprobeDst = Path.Combine(HelperDir, "ffprobe.exe");

        private static readonly string[] ArchiveUrls =
        {
            "https://github.com/7Lzz/OBS-ReplayKit/raw/refs/heads/main/utils/downloads/ffmpeg-tools.zip",
        };

        private static readonly (string Name, string Dst, string ExpectedSha256)[] Tools =
        {
            ("ffmpeg.exe", FfmpegDst, "228d7a8556258de907fdb55f36850078ebc7680b84ec30d84ea02e99bec1d1eb"),
            ("ffprobe.exe", FfprobeDst, "0fde260f5abd35c9cafd96f594cc76365a780c1b73a90e35b6a3409ea1db1bf0"),
        };

        // size caps so a hijacked url/archive cant fill the users disk.
        private const long MaxArchiveBytes = 130L * 1024 * 1024;
        private const long MaxToolBytes = 110L * 1024 * 1024;
        private const long ProgressIntervalBytes = 5L * 1024 * 1024;
        private const double ProgressIntervalSeconds = 0.5;

        private static bool AlreadyInstalled() => Tools.All(t => ValidTool(t.Dst, t.ExpectedSha256));

        private static string FileSha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var handle = File.OpenRead(path))
            {
                return BitConverter.ToString(sha.ComputeHash(handle)).Replace("-", "").ToLowerInvariant();
            }
        }

        private static bool ValidTool(string path, string expectedSha256)
        {
            return File.Exists(path) && FileSha256(path) == expectedSha256.ToLowerInvariant();
        }

        private static string FormatSize(long numBytes)
        {
            double value = Math.Max(0, numBytes);
            foreach (var unit in new[] { "B", "KB", "MB" })
            {
                if (value < 1024.0)
                {
                    return unit == "B" ? $"{(int)value} {unit}" : $"{value:F1} {unit}";
                }
                value /= 1024.0;
            }
            return $"{value:F1} GB";
        }

        private static string FormatDuration(double seconds)
        {
            if (seconds < 0 || double.IsInfinity(seconds)) return "--:--";
            int total = (int)seconds;
            int minutes = total / 60, secs = total % 60;
            int hours = minutes / 60;
            minutes %= 60;
            return hours > 0 ? $"{hours:D}:{minutes:D2}:{secs:D2}" : $"{minutes:D2}:{secs:D2}";
        }

        private static string DownloadProgressLine(long total, long expectedLen, DateTime startedAt)
        {
            double elapsed = Math.Max(0.001, (DateTime.UtcNow - startedAt).TotalSeconds);
            double rate = total / elapsed;
            if (expectedLen > 0)
            {
                double pct = Math.Max(0.0, Math.Min(100.0, (double)total / expectedLen * 100.0));
                int filled = (int)Math.Round(pct / 100.0 * 24);
                string bar = new string('#', filled) + new string('-', 24 - filled);
                long remaining = Math.Max(0, expectedLen - total);
                double eta = rate > 0 ? remaining / rate : double.PositiveInfinity;
                return $"Downloading ffmpeg tools [{bar}] {pct,5:F1}% {FormatSize(total)} / {FormatSize(expectedLen)} @ {FormatSize((long)rate)}/s ETA {FormatDuration(eta)}";
            }
            return $"Downloading ffmpeg tools {FormatSize(total)} @ {FormatSize((long)rate)}/s";
        }

        // stream a replaykit-hosted ffmpeg archive with live progress.
        private static string DownloadArchive(string url, Action<string> log = null)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            string tmpPath = null;
            try
            {
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(90) })
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "OBSReplayKit/1.0 (+https://github.com/7Lzz/OBS-ReplayKit)");
                    var response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                    if (!response.IsSuccessStatusCode)
                    {
                        log?.Invoke($"warn: FFmpeg archive download returned HTTP {(int)response.StatusCode}: {url}");
                        return null;
                    }
                    long expectedLen = response.Content.Headers.ContentLength ?? 0;
                    if (expectedLen > MaxArchiveBytes)
                    {
                        log?.Invoke($"warn: FFmpeg archive is larger than {MaxArchiveBytes / (1024 * 1024)} MB cap");
                        return null;
                    }

                    Directory.CreateDirectory(HelperDir);
                    tmpPath = Path.Combine(HelperDir, "ffmpeg-tools." + Guid.NewGuid().ToString("N") + ".zip.download");
                    long total = 0;
                    long nextLog = ProgressIntervalBytes;
                    DateTime lastLogAt = DateTime.UtcNow;
                    DateTime startedAt = lastLogAt;

                    using (var stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                    using (var outFile = File.Open(tmpPath, FileMode.Create, FileAccess.Write))
                    {
                        var buffer = new byte[1024 * 1024];
                        int read;
                        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            total += read;
                            if (total > MaxArchiveBytes)
                            {
                                log?.Invoke($"warn: FFmpeg archive exceeded {MaxArchiveBytes / (1024 * 1024)} MB cap, aborting");
                                return null;
                            }
                            outFile.Write(buffer, 0, read);
                            DateTime now = DateTime.UtcNow;
                            if (log != null && (total >= nextLog || (now - lastLogAt).TotalSeconds >= ProgressIntervalSeconds))
                            {
                                log(DownloadProgressLine(total, expectedLen, startedAt));
                                nextLog += ProgressIntervalBytes;
                                lastLogAt = now;
                            }
                        }
                    }

                    log?.Invoke(DownloadProgressLine(total, expectedLen, startedAt));
                    return tmpPath;
                }
            }
            catch (Exception exc) when (exc is HttpRequestException || exc is TaskCanceledException || exc is IOException)
            {
                log?.Invoke($"warn: FFmpeg archive download error: {exc.Message}: {url}");
                if (tmpPath != null && File.Exists(tmpPath))
                {
                    try { File.Delete(tmpPath); } catch (IOException) { }
                }
                return null;
            }
        }

        private static bool ExtractArchive(string archive, Action<string> log = null)
        {
            var tmpPaths = new List<string>();
            try
            {
                using (var zf = ZipFile.OpenRead(archive))
                {
                    var infos = new Dictionary<string, ZipArchiveEntry>();
                    foreach (var e in zf.Entries) infos[Path.GetFileName(e.FullName).ToLowerInvariant()] = e;
                    foreach (var (name, dst, expectedSha256) in Tools)
                    {
                        if (!infos.TryGetValue(name.ToLowerInvariant(), out var info))
                        {
                            log?.Invoke("warn: " + name + " missing from FFmpeg archive");
                            return false;
                        }
                        if (info.Length <= 0 || info.Length > MaxToolBytes)
                        {
                            log?.Invoke("warn: " + name + " has an unexpected archive size");
                            return false;
                        }

                        string tmp = Path.Combine(HelperDir, name + "." + Guid.NewGuid().ToString("N") + ".extract");
                        tmpPaths.Add(tmp);
                        long total = 0;
                        byte[] hash;
                        using (var sha = SHA256.Create())
                        {
                            using (var src = info.Open())
                            using (var outFile = File.Open(tmp, FileMode.Create, FileAccess.Write))
                            {
                                var buffer = new byte[1024 * 1024];
                                int read;
                                while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                                {
                                    total += read;
                                    if (total > MaxToolBytes)
                                    {
                                        log?.Invoke($"warn: {name} exceeded {MaxToolBytes / (1024 * 1024)} MB cap while extracting");
                                        return false;
                                    }
                                    sha.TransformBlock(buffer, 0, read, null, 0);
                                    outFile.Write(buffer, 0, read);
                                }
                                sha.TransformFinalBlock(new byte[0], 0, 0);
                                hash = sha.Hash;
                            }
                        }
                        string actual = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                        if (actual != expectedSha256.ToLowerInvariant())
                        {
                            log?.Invoke("warn: " + name + " hash mismatch after extract: " + actual);
                            return false;
                        }

                        // python's Path.replace() atomically overwrites the destination; File.Move needs it gone first.
                        if (File.Exists(dst)) File.Delete(dst);
                        File.Move(tmp, dst);
                        tmpPaths.Remove(tmp);
                        log?.Invoke("installed -> " + dst);
                    }
                }
                return AlreadyInstalled();
            }
            catch (Exception exc) when (exc is IOException || exc is InvalidDataException)
            {
                log?.Invoke("warn: FFmpeg archive extract failed: " + exc.Message);
                return false;
            }
            finally
            {
                foreach (var tmp in tmpPaths)
                {
                    if (File.Exists(tmp))
                    {
                        try { File.Delete(tmp); } catch (IOException) { }
                    }
                }
            }
        }

        private static bool DownloadAndExtractTools(Action<string> log = null)
        {
            foreach (var url in ArchiveUrls)
            {
                log?.Invoke("  " + url);
                string archive = DownloadArchive(url, log);
                if (archive == null) continue;
                try
                {
                    if (ExtractArchive(archive, log)) return true;
                }
                finally
                {
                    if (File.Exists(archive))
                    {
                        try { File.Delete(archive); } catch (IOException) { }
                    }
                }
            }
            log?.Invoke("warn: ReplayKit ffmpeg-tools.zip was unavailable or invalid");
            log?.Invoke("Upload ffmpeg-tools.zip to utils/downloads on the public main branch, then re-run Apply.");
            return false;
        }

        private static void LogExistingTools(Action<string> log = null)
        {
            if (log == null) return;
            foreach (var (name, dst, expectedSha256) in Tools)
            {
                if (ValidTool(dst, expectedSha256)) log(name + " already present at " + dst);
                else if (File.Exists(dst)) log("warn: " + name + " hash mismatch; replacing it");
            }
        }

        private static bool InstallTools(Action<string> log = null)
        {
            if (AlreadyInstalled())
            {
                LogExistingTools(log);
                return true;
            }
            return DownloadAndExtractTools(log);
        }

        // install ffmpeg.exe + ffprobe.exe next to the helper.
        public static bool InstallFfmpeg(Action<string> log = null)
        {
            if (AlreadyInstalled())
            {
                LogExistingTools(log);
                return true;
            }

            Directory.CreateDirectory(HelperDir);
            log?.Invoke("installing ffmpeg tools...");

            bool ok = InstallTools(log);
            if (!ok)
            {
                log?.Invoke("ffmpeg install SKIPPED -- compress / trim will be unavailable");
                log?.Invoke("Make the GitHub ZIP URL public, then re-run Apply.");
            }
            return ok;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using ReplayKitShared;

namespace ReplayKitSetup
{
    // install ffmpeg.exe + ffprobe.exe into the helper dir
    public static class FfmpegInstall
    {
        // next to OBSReplayKit.exe (the ReplayKitHelper server) under the consolidated obs-replayKit/ tree; Compression.FindToolInClipDirs probes Constants.HelperRoot first.
        private static readonly string HelperDir = Path.Combine(Config.OBS_CONFIG, "obs-replayKit", "scripts", "helper");
        private static readonly string FfmpegDst = Path.Combine(HelperDir, "ffmpeg.exe");
        private static readonly string FfprobeDst = Path.Combine(HelperDir, "ffprobe.exe");

        private static readonly string[] ArchiveUrls =
        {
            VideoTools.ArchiveUrl,
        };

        private static readonly (string Name, string Dst, string ExpectedSha256)[] Tools =
        {
            ("ffmpeg.exe", FfmpegDst, VideoTools.FfmpegSha256),
            ("ffprobe.exe", FfprobeDst, VideoTools.FfprobeSha256),
        };

        // size caps so a hijacked url/archive cant fill the users disk.
        private const long MaxArchiveBytes = 130L * 1024 * 1024;
        private const long MaxToolBytes = 110L * 1024 * 1024;
        private const long ProgressIntervalBytes = 5L * 1024 * 1024;
        private const double ProgressIntervalSeconds = 0.5;

        private static bool AlreadyInstalled() => VideoTools.IsInstalled(HelperDir);

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
                    Directory.CreateDirectory(HelperDir);
                    tmpPath = Path.Combine(HelperDir, "ffmpeg-tools." + Guid.NewGuid().ToString("N") + ".zip.download");
                    long nextLog = ProgressIntervalBytes;
                    DateTime lastLogAt = DateTime.UtcNow;
                    DateTime startedAt = lastLogAt;
                    Download.Save(client, url, tmpPath, MaxArchiveBytes, TimeSpan.FromSeconds(90), progress: (total, expectedLen) =>
                    {
                        DateTime now = DateTime.UtcNow;
                        if (log != null && (total >= nextLog || (now - lastLogAt).TotalSeconds >= ProgressIntervalSeconds))
                        {
                            log(DownloadProgressLine(total, expectedLen, startedAt));
                            nextLog = total + ProgressIntervalBytes;
                            lastLogAt = now;
                        }
                    });
                    long size = new FileInfo(tmpPath).Length;
                    log?.Invoke(DownloadProgressLine(size, size, startedAt));
                    return tmpPath;
                }
            }
            catch (Exception exc) when (exc is HttpRequestException || exc is InvalidDataException || exc is OperationCanceledException || exc is IOException || exc is UnauthorizedAccessException)
            {
                log?.Invoke($"warn: FFmpeg archive download error: {exc.Message}: {url}");
                if (tmpPath != null && File.Exists(tmpPath))
                {
                    try { File.Delete(tmpPath); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
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
                        if (VideoTools.IsValid(dst, expectedSha256)) continue;
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

                        if (name == "ffmpeg.exe") File.Delete(Path.Combine(HelperDir, "OBSReplayKit-Encoder.exe"));
                        File.Move(tmp, dst, true);
                        tmpPaths.Remove(tmp);
                        log?.Invoke("installed -> " + dst);
                    }
                }
                return AlreadyInstalled();
            }
            catch (Exception exc) when (exc is IOException || exc is InvalidDataException || exc is UnauthorizedAccessException)
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
                        try { File.Delete(tmp); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
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
                        try { File.Delete(archive); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                    }
                }
            }
            log?.Invoke("warn: ReplayKit ffmpeg-tools.zip was unavailable or invalid");
            log?.Invoke("Check your connection and run the installer again to repair the video tools.");
            return false;
        }

        private static void LogExistingTools(Action<string> log = null)
        {
            if (log == null) return;
            foreach (var (name, dst, expectedSha256) in Tools)
            {
                if (VideoTools.IsValid(dst, expectedSha256)) log(name + " already present at " + dst);
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
                log?.Invoke("FFmpeg and FFprobe could not be installed or verified; trimming and compression are unavailable.");
            }
            return ok;
        }
    }
}

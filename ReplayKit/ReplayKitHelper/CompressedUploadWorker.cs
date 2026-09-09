using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace ReplayKitHelper
{
    // two-pass, bitrate-targeted libx264 encode of a temp copy (the original clip is never touched), then hands the compressed copy straight to UploadWorker -- the size-constrained sibling of CompressOverwriteWorker (which is quality-targeted and replaces the original in place). runs as an in-process Task from Compression.StartCompressedStreamableUpload. ported from the embedded worker script inside obs_replaykit helper modules/52_compression.ps1s Start-CompressedStreamableUpload -- that script spawned a seperate "powershell.exe -EncodedCommand" process which itself spawned upload_worker.ps1 on success; here the encode and the upload are just two steps of the same Task, so theres no second process to hand off to. progress is read via -progress pipe:1 (stdout), not the embedded scripts original -progress <file> polling -- the same pipe-based approach CompressOverwriteWorker already uses, for the same documented reason (a file goes thru the os page cache and can lag arbitrarily on windows; a pipe doesnt).
    internal static class CompressedUploadWorker
    {
        private static void RunFfmpegPass(string ffmpeg, List<string> argv, string phase, int startPercent, int endPercent, double durationSec, string requestId)
        {
            var withProgress = new List<string>(argv) { "-progress", "pipe:1", "-nostats" };
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = ProcessArgs.Join(withProgress.ToArray()),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var proc = Process.Start(psi))
            {
                // exposes the live ffmpeg process on the job record so UploadState.CancelActiveUpload can kill it directly.
                UploadState.SetUploadState(requestId: requestId, encoderProcess: proc);
                var stderrTask = proc.StandardError.ReadToEndAsync();
                double durationUs = Math.Max(1.0, durationSec * 1000000.0);
                int lastPct = -1;
                string line;
                while ((line = proc.StandardOutput.ReadLine()) != null)
                {
                    if (line.StartsWith("out_time_us="))
                    {
                        if (double.TryParse(line.Substring(12), NumberStyles.Float, CultureInfo.InvariantCulture, out double us))
                        {
                            int pct = (int)Math.Floor(startPercent + (endPercent - startPercent) * Math.Min(1.0, us / durationUs));
                            if (pct != lastPct)
                            {
                                lastPct = pct;
                                UploadState.SetUploadState(requestId: requestId, state: "compressing", phase: phase, percent: pct);
                            }
                        }
                    }
                }
                proc.WaitForExit();
                string stderr = stderrTask.Result;
                if (!string.IsNullOrEmpty(stderr)) Log.Write("compress-upload " + phase + " stderr: " + stderr, "compress", requestId);
                UploadState.SetUploadState(requestId: requestId, state: "compressing", phase: phase, percent: endPercent);
                if (proc.ExitCode != 0) throw new InvalidOperationException(phase + " failed (exit=" + proc.ExitCode + ")");
            }
        }

        public static UploadOutcome Run(
            string requestId, string ffmpeg, string inputPath, string tempPath, string passLog,
            long capBytes, int videoKbps, int audioKbps, double durationSec,
            string authJar, bool requireAuth)
        {
            try
            {
                UploadState.SetUploadState(requestId: requestId, state: "compressing", phase: "analyzing", percent: 1);
                Log.Write("Compressing temp copy: video=" + videoKbps + "k audio=" + audioKbps + "k", "compress", requestId);

                var pass1 = new List<string> { "-y", "-hide_banner", "-i", inputPath, "-map", "0:v:0", "-c:v", "libx264", "-preset", "veryfast", "-b:v", videoKbps + "k", "-pass", "1", "-passlogfile", passLog, "-an", "-f", "null", "NUL" };
                RunFfmpegPass(ffmpeg, pass1, "compress pass 1", 2, 49, durationSec, requestId);

                var pass2 = new List<string> { "-y", "-hide_banner", "-i", inputPath, "-map", "0:v:0", "-map", "0:a:0?", "-c:v", "libx264", "-preset", "veryfast", "-b:v", videoKbps + "k", "-pass", "2", "-passlogfile", passLog, "-c:a", "aac", "-b:a", audioKbps + "k", "-movflags", "+faststart", tempPath };
                RunFfmpegPass(ffmpeg, pass2, "compress pass 2", 50, 94, durationSec, requestId);

                if (!File.Exists(tempPath)) throw new InvalidOperationException("ffmpeg did not create a compressed file");
                var fi = new FileInfo(tempPath);
                if (fi.Length <= 0) throw new InvalidOperationException("Compressed file is empty");
                if (capBytes > 0 && fi.Length > capBytes)
                {
                    long mb = (long)Math.Ceiling(fi.Length / 1024.0 / 1024.0);
                    long capMb = capBytes / 1024 / 1024;
                    throw new InvalidOperationException("Compressed file is still too big: " + mb + " MB / " + capMb + " MB limit");
                }

                Log.Write("Uploading compressed temp copy (" + Math.Round(fi.Length / 1024.0 / 1024.0, 2) + " MB)", "compress", requestId);
                UploadState.SetUploadState(requestId: requestId, state: "uploading", phase: "uploading", percent: 95);

                return UploadWorker.Run(requestId, tempPath, authJar, requireAuth, 95, 4);
            }
            catch (Exception ex)
            {
                Log.Write("ERROR: " + ex.Message, "compress", requestId);
                UploadState.SetUploadState(requestId: requestId, state: "error", phase: "error", percent: 0, error: ex.Message);
                return new UploadOutcome { Ok = false, Message = ex.Message };
            }
            finally
            {
                try
                {
                    string passLogDir = Path.GetDirectoryName(passLog);
                    string passLogName = Path.GetFileName(passLog);
                    foreach (var f in Directory.EnumerateFiles(string.IsNullOrEmpty(passLogDir) ? Constants.COMPRESS_TMP_DIR : passLogDir, passLogName + "*"))
                    {
                        try { File.Delete(f); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }
    }
}

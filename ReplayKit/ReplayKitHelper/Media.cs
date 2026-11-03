using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;

namespace ReplayKitHelper
{
    // thumbnail work is deduplicated by file generation and bounded even when a shell codec hangs.
    internal static class Media
    {
        private static string GetThumbnailName(Clips.SafeClipPath selected, FileInfo fi)
        {
            string key = Clips.CacheIdentity(selected.Full) + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
            using (var sha = SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString() + ".jpg";
            }
        }

        private static readonly Dictionary<string, Task<string>> ThumbnailJobs = new Dictionary<string, Task<string>>();
        private static readonly Dictionary<string, DateTime> ThumbnailFailures = new Dictionary<string, DateTime>();

        public static string GetCachedThumbnail(Clips.SafeClipPath selected, FileInfo fi)
        {
            Directory.CreateDirectory(Constants.THUMB_DIR);
            string outPath = Path.Combine(Constants.THUMB_DIR, GetThumbnailName(selected, fi));
            if (File.Exists(outPath)) return outPath;
            var wait = Stopwatch.StartNew();
            Task<string> task;
            lock (Server.State.ThumbQueueLock)
            {
                if (File.Exists(outPath)) return outPath;
                // the picture of a clip that was only just written can fail while the file is still being finished, so a recent clip is tried again a few seconds after a failure; the failure of an older clip stays
                if (ThumbnailFailures.TryGetValue(outPath, out DateTime failedAt))
                {
                    if (DateTime.UtcNow - fi.LastWriteTimeUtc > TimeSpan.FromMinutes(5) || (DateTime.UtcNow - failedAt).TotalSeconds < 8) return null;
                    ThumbnailFailures.Remove(outPath);
                }
                if (!ThumbnailJobs.TryGetValue(outPath, out task))
                {
                    // A timed-out shell call still owns a slot until it actually returns.
                    while (ThumbnailJobs.Count >= 2)
                    {
                        int remaining = (int)Math.Max(0, 8000 - wait.ElapsedMilliseconds);
                        if (remaining == 0 || !Monitor.Wait(Server.State.ThumbQueueLock, remaining)) return null;
                        if (File.Exists(outPath)) return outPath;
                        if (ThumbnailFailures.ContainsKey(outPath)) return null;
                        if (ThumbnailJobs.TryGetValue(outPath, out task)) break;
                    }
                    if (task == null)
                    {
                        task = Task.Run(() => CreateThumbnail(selected.Full, outPath));
                        ThumbnailJobs.Add(outPath, task);
                        task.ContinueWith(done =>
                        {
                            lock (Server.State.ThumbQueueLock)
                            {
                                ThumbnailJobs.Remove(outPath);
                                Monitor.PulseAll(Server.State.ThumbQueueLock);
                                if (done.IsFaulted || done.Result == null)
                                {
                                    if (ThumbnailFailures.Count < 1024) ThumbnailFailures[outPath] = DateTime.UtcNow;
                                    if (done.IsFaulted) Program.WriteCrashReport("thumbnail", done.Exception, false);
                                }
                            }
                        }, TaskScheduler.Default);
                    }
                }
            }
            try { return task.Wait((int)Math.Max(0, 8000 - wait.ElapsedMilliseconds)) ? task.GetAwaiter().GetResult() : null; }
            catch (Exception ex) { Log.Write("Thumbnail failed: " + ex.Message); return null; }
        }

        private static string CreateThumbnail(string source, string output)
        {
            string temp = output + "." + Guid.NewGuid().ToString("N") + ".tmp.jpg";
            try
            {
                using var reader = JobCoordinator.ReadSource(source);
                Native.SaveThumbnail(source, temp);
                File.Move(temp, output);
                return output;
            }
            catch (COMException ex)
            {
                // the shell has no picture of the file: still being written, or a codec it cannot read. not a crash; the caller gets null and the page asks again or shows the placeholder
                Log.Write("Thumbnail: no picture from the shell for " + Path.GetFileName(source) + " (0x" + ex.ErrorCode.ToString("X8") + ")");
                return null;
            }
            catch (Exception ex) { Program.WriteCrashReport("thumbnail", ex, false); return null; }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }

        private static byte[] FallbackSvg(string name)
        {
            try { return File.ReadAllBytes(Path.Combine(Constants.APP_ICONS_DIR, "fallback", name)); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Write("Media.FallbackSvg " + name + ": " + ex.Message);
                using var source = typeof(Media).Assembly.GetManifestResourceStream("ReplayKitHelper.icons." + name);
                using var output = new MemoryStream();
                source.CopyTo(output);
                return output.ToArray();
            }
        }

        public static byte[] GetPlaceholderThumbnail() => FallbackSvg("placeholder-thumbnail.svg");
        public static byte[] GetObsIconSvg() => FallbackSvg("obs-icon.svg");

        // follows the Appearance-tab choice so the dock favicon / served /obs-icon.ico matches everything else.
        public static string GetObsIconIco() => ReplaykitSettings.EffectiveReplayKitIconPath();
    }
}

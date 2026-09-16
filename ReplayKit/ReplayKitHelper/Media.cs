using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;

namespace ReplayKitHelper
{
    // Thumbnail work is deduplicated by file generation and bounded even when a shell codec hangs.
    internal static class Media
    {
        private static string GetThumbnailName(Clips.SafeClipPath selected, FileInfo fi)
        {
            string key = selected.Full + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
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
                if (ThumbnailFailures.ContainsKey(outPath)) return null;
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
                Native.SaveThumbnail(source, temp);
                File.Move(temp, output);
                return output;
            }
            catch (Exception ex) { Program.WriteCrashReport("thumbnail", ex, false); return null; }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }

        // the two fallback images live as files under icons/fallback/ -- that copy is the source of truth; the inline string is only a net for a broken deploy where the file is missing, not a silent behaviour change
        private static byte[] FallbackSvg(string name, string inlineNet)
        {
            try
            {
                string f = Path.Combine(Constants.APP_ICONS_DIR, "fallback", name);
                if (File.Exists(f)) return File.ReadAllBytes(f);
                Log.Write("Media.FallbackSvg: missing " + f + " -- using inline copy");
            }
            catch (Exception ex) { Log.Write("Media.FallbackSvg " + name + ": " + ex.Message); }
            return Encoding.UTF8.GetBytes(inlineNet);
        }

        public static byte[] GetPlaceholderThumbnail() => FallbackSvg("placeholder-thumbnail.svg",
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 480 270\">\n" +
            "  <rect width=\"480\" height=\"270\" fill=\"#13141A\"/>\n" +
            "  <circle cx=\"240\" cy=\"135\" r=\"44\" fill=\"#272A33\" stroke=\"#3C404D\" stroke-width=\"2\"/>\n" +
            "  <path d=\"M229 110v50l43-25z\" fill=\"#969696\"/>\n" +
            "</svg>\n");

        // net for the degenerate case where BOTH the real .ico and icons/fallback/obs-icon.svg are gone -- a plain dark tile with a ring, deliberately minimal (the real OBS mark is the .svg file)
        public static byte[] GetObsIconSvg() => FallbackSvg("obs-icon.svg",
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 64 64\">\n" +
            "  <rect width=\"64\" height=\"64\" rx=\"12\" fill=\"#111217\"/>\n" +
            "  <circle cx=\"32\" cy=\"32\" r=\"19\" fill=\"none\" stroke=\"#5B6273\" stroke-width=\"3\"/>\n" +
            "</svg>\n");

        // follows the Appearance-tab choice so the dock favicon / served /obs-icon.ico matches everything else.
        public static string GetObsIconIco() => ReplaykitSettings.EffectiveReplayKitIconPath();
    }
}

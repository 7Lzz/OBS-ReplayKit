using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ReplayKitHelper
{
    // thumbnails -- cached on disk by sha1(path + size + mtime), serialised via ThumbQueueLock so the popup opening doesnt spawn dozens of simultaneous shell-thumbnail extractors -- plus the dock's placeholder/obs-icon svgs. ported from obs_replaykit helper modules/60_media.ps1.
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

        public static string GetCachedThumbnail(Clips.SafeClipPath selected, FileInfo fi)
        {
            Directory.CreateDirectory(Constants.THUMB_DIR);
            string outPath = Path.Combine(Constants.THUMB_DIR, GetThumbnailName(selected, fi));
            if (File.Exists(outPath)) return outPath;

            lock (Server.State.ThumbQueueLock)
            {
                // re-check after acquiring the lock in case another waiter just made it.
                if (File.Exists(outPath)) return outPath;
                string tmp = outPath + "." + Process.GetCurrentProcess().Id + ".tmp.jpg";
                try
                {
                    Native.SaveThumbnail(selected.Full, tmp);
                    File.Move(tmp, outPath);
                    return outPath;
                }
                catch (Exception ex)
                {
                    // thumbnail generation can fail in plenty of ways depending on the codec/shell extension state (com errors, gdi+ errors, timeout) -- best effort, log and report no thumbnail rather than enumerate every possible cause.
                    Log.Write("thumb failed for " + selected.Name + ": " + ex.Message);
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception ex2) when (ex2 is IOException || ex2 is UnauthorizedAccessException) { }
                    return null;
                }
            }
        }

        public static byte[] GetPlaceholderThumbnail()
        {
            const string svg =
                "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 480 270\">\n" +
                "  <rect width=\"480\" height=\"270\" fill=\"#13141A\"/>\n" +
                "  <circle cx=\"240\" cy=\"135\" r=\"44\" fill=\"#272A33\" stroke=\"#3C404D\" stroke-width=\"2\"/>\n" +
                "  <path d=\"M229 110v50l43-25z\" fill=\"#969696\"/>\n" +
                "</svg>\n";
            return Encoding.UTF8.GetBytes(svg);
        }

        public static byte[] GetObsIconSvg()
        {
            const string svg =
                "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 64 64\">\n" +
                "  <rect width=\"64\" height=\"64\" rx=\"12\" fill=\"#111217\"/>\n" +
                "  <circle cx=\"32\" cy=\"32\" r=\"27\" fill=\"#1D1F26\" stroke=\"#5B6273\" stroke-width=\"2\"/>\n" +
                "  <path fill=\"#F2F2F2\" d=\"M31.3 8.8c7.3.2 13.5 4.6 16.3 10.9-6.7-3.1-14.8-.3-18.1 6.4-2.6-2.6-4.2-6.2-4.2-10.2 0-2.5.7-4.9 1.9-7 1.3-.1 2.7-.2 4.1-.1Z\"/>\n" +
                "</svg>\n";
            return Encoding.UTF8.GetBytes(svg);
        }

        public static string GetObsIconIco() => File.Exists(Constants.OBS_ICON_PATH) ? Constants.OBS_ICON_PATH : null;
    }
}

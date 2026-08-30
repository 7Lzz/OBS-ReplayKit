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

        // the two fallback images live as files under icons/fallback/ -- that copy is the source of truth. the
        // inline string is only a net for a broken deploy where the file is missing; not a silent behaviour change.
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

        // net for the degenerate case where BOTH the real .ico and icons/fallback/obs-icon.svg are gone -- a plain
        // dark tile with a ring, deliberately minimal (the real OBS mark is the .svg file).
        public static byte[] GetObsIconSvg() => FallbackSvg("obs-icon.svg",
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 64 64\">\n" +
            "  <rect width=\"64\" height=\"64\" rx=\"12\" fill=\"#111217\"/>\n" +
            "  <circle cx=\"32\" cy=\"32\" r=\"19\" fill=\"none\" stroke=\"#5B6273\" stroke-width=\"3\"/>\n" +
            "</svg>\n");

        // follows the Appearance-tab choice so the dock favicon / served /obs-icon.ico matches everything else.
        public static string GetObsIconIco() => ReplaykitSettings.EffectiveReplayKitIconPath();
    }
}

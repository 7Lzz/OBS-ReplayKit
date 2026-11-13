using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // obs sometimes muxes hevc under the hev1 sample entry instead of hvc1, which ios and avfoundation (and so the iphone app of discord) will not decode; the fix is a lossless retag of the container, never a re-encode
    internal static class HevcTag
    {
        // mp4 and mov are the containers with sample entries; matroska names the codec another way and has nothing to retag
        private static readonly HashSet<string> RetagExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov" };

        internal static bool Enabled() => ReplaykitSettings.ReadSettings()["hevcFixEnabled"]?.Value<bool>() == true;

        // true when the first video track is hevc under the hev1 name; read from the header of the file, so no tool runs for a clip that needs nothing
        internal static bool NeedsFix(string path)
        {
            if (!RetagExtensions.Contains(Path.GetExtension(path))) return false;
            var video = Mp4Tracks.ReadFile(path)?.FirstOrDefault(track => track.Video);
            return video != null && video.SampleEntry == "hev1";
        }

        // remuxes the clip with the video tagged hvc1 and puts the copy in its place; false when the clip stays as it was
        internal static bool Fix(string path)
        {
            // only the copy replaykit installed and verified, never one found in the clip folder or on the path
            string ffmpeg = Path.Combine(Constants.HelperRoot, "ffmpeg.exe");
            if (!File.Exists(ffmpeg)) return false;
            var original = new FileInfo(path);
            if (!original.Exists || (original.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            var tracks = Mp4Tracks.ReadFile(path);
            if (tracks == null) return false;

            // beside the clip, so the replace is one rename on the same volume, and under a name the library never lists
            string temporary = Path.Combine(original.DirectoryName, "_replaykit_finalize_" + Guid.NewGuid().ToString("N") + ".rkpart");
            try
            {
                string format = string.Equals(original.Extension, ".mov", StringComparison.OrdinalIgnoreCase) ? "mov" : "mp4";
                // a recording of many gigabytes takes longer to copy than a clip, so the time allowed grows with the file
                int allowedMs = (int)Math.Min(int.MaxValue, Math.Max(120000L, original.Length / 20000));
                var remux = Compression.InvokeNativeCapture(ffmpeg, new[]
                {
                    "-nostdin", "-hide_banner", "-loglevel", "error", "-xerror", "-copyts", "-i", path,
                    "-map", "0", "-c", "copy", "-tag:v:0", "hvc1", "-movflags", "+faststart", "-f", format, temporary,
                }, CancellationToken.None, allowedMs, true);
                var result = remux.ExitCode == 0 ? Mp4Tracks.ReadFile(temporary) : null;
                // every track came across, the audio kept its names and the video now carries the hvc1 name
                if (result == null || result.Count != tracks.Count ||
                    !result.Where(t => t.Audio).Select(t => t.HandlerName).SequenceEqual(tracks.Where(t => t.Audio).Select(t => t.HandlerName)) ||
                    result.FirstOrDefault(t => t.Video)?.SampleEntry != "hvc1")
                {
                    Log.Write("HEVC tag fix: the remux of " + original.Name + " could not be verified, the clip was left as it was");
                    return false;
                }
                var normalized = Mp4Tracks.NormalizeAudio(temporary);
                if (normalized == Mp4Tracks.Fix.Busy || normalized == Mp4Tracks.Fix.Failed) return false;
                if (!ClipFiles.ReplaceKeepingDates(path, temporary, original)) return false;
                Log.Write("HEVC tag fix: hev1 -> hvc1 for " + original.Name);
                ClipGames.RefreshedRetag(path, original.Length, original.LastWriteTimeUtc.Ticks, original.CreationTimeUtc.Ticks);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is Win32Exception || ex is TimeoutException)
            {
                Log.Write("HEVC tag fix: " + original.Name + " was left as it was: " + ex.Message);
                return false;
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Log.Write("HEVC tag fix scratch cleanup: " + ex.GetType().Name); }
            }
        }
    }
}

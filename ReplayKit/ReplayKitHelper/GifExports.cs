using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // gifs made from the clip menu: they live in the GIFs folder of the clip folder and the clips page shows them the way it shows screenshots
    internal static class GifExports
    {
        private const double MinSeconds = .5, MaxSeconds = 15;

        public static string Folder(bool create = false)
        {
            string root = Path.GetFullPath(AppConfig.GetClipDir());
            string folder = Path.Combine(root, "GIFs");
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Clip folder not found");
            if (File.Exists(folder)) throw new IOException("The GIF folder is occupied by a file");
            if (create) Directory.CreateDirectory(folder);
            if (Directory.Exists(folder) && (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The GIF folder cannot be a link");
            return folder;
        }

        // a plain gif file name, not one of the temporary files of an export
        internal static bool ValidName(string name) =>
            !string.IsNullOrWhiteSpace(name) && name.Length <= 200 && name.Trim() == name &&
            name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name == Path.GetFileName(name) &&
            !name.StartsWith("_replaykit_", StringComparison.OrdinalIgnoreCase) &&
            Path.GetExtension(name).Equals(".gif", StringComparison.OrdinalIgnoreCase);

        internal static JArray List()
        {
            string folder = Folder();
            if (!Directory.Exists(folder)) return new JArray();
            return new JArray(new DirectoryInfo(folder).EnumerateFiles("*.gif", SearchOption.TopDirectoryOnly)
                .Where(file => ValidName(file.Name) && (file.Attributes & FileAttributes.ReparsePoint) == 0)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => new JObject
                {
                    ["name"] = file.Name,
                    ["size"] = file.Length,
                    ["mtime"] = new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeSeconds(),
                }));
        }

        internal static string Resolve(string name)
        {
            if (!ValidName(name)) return null;
            string path = Path.Combine(Folder(), name);
            return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 ? path : null;
        }

        // the cards show the first frame as a small jpeg and only play the gif itself while hovered, so a grid of gifs does not decode every animation at once
        internal static string Thumb(string path) => Screenshots.Thumb(path, "gifs", "GIF");

        // a gif goes into deleted history like a clip or a screenshot while that is on, and is deleted for good when it is off; true when it was kept
        internal static bool Delete(string path)
        {
            bool kept = DeletedHistory.Keep(path, "gif", Path.GetDirectoryName(Path.GetDirectoryName(path)), null);
            if (!kept) File.Delete(path);
            return kept;
        }

        internal static string Rename(string path, string to)
        {
            to = (to ?? "").Trim();
            if (!Path.GetExtension(to).Equals(".gif", StringComparison.OrdinalIgnoreCase)) to += ".gif";
            if (!ValidName(to)) throw new ArgumentException("That name cannot be used");
            string target = Path.Combine(Path.GetDirectoryName(path), to);
            if (string.Equals(target, path, StringComparison.Ordinal)) return to;
            // a change of case only is the same file to windows, so it is moved on its own
            if (File.Exists(target) && !string.Equals(target, path, StringComparison.OrdinalIgnoreCase)) throw new IOException("A GIF with that name already exists");
            ClipFiles.MoveWhenFree(path, target);
            return to;
        }

        // only the file goes on the clipboard: a picture on the clipboard is a single frame, while chat apps take the copied file and keep it animated
        internal static void Copy(string path)
        {
            StaRunner.Run(() =>
            {
                var files = new System.Collections.Specialized.StringCollection();
                files.Add(path);
                var data = new System.Windows.Forms.DataObject();
                data.SetFileDropList(files);
                System.Windows.Forms.Clipboard.SetDataObject(data, true);
            });
        }

        internal static string Export(string name, double start, double end)
        {
            double duration = end - start;
            if (double.IsNaN(start) || double.IsInfinity(start) || double.IsNaN(duration) || double.IsInfinity(duration) ||
                start < 0 || duration < MinSeconds || duration > MaxSeconds)
                throw new ArgumentException("Select between 0.5 and 15 seconds for a GIF.");
            var source = Clips.GetSafeClipPath(name);
            if (source == null || !File.Exists(source.Full)) throw new ArgumentException("Clip not found.");
            string id = UploadState.NewRequestId();
            using var reservation = JobCoordinator.TryReserve(id, source.Name, source.Full, "gif", out string busy);
            if (reservation == null) throw new InvalidOperationException(busy);
            string ffmpeg = Compression.GetToolPaths()["ffmpeg"]?.Value<string>();
            if (!File.Exists(ffmpeg)) throw new IOException("FFmpeg is unavailable.");
            string folder = Folder(true);
            string temporary = Path.Combine(folder, "_replaykit_" + Guid.NewGuid().ToString("N") + ".gif");
            string stem = Path.GetFileNameWithoutExtension(source.Name);
            if (stem.Length > 150) stem = stem.Substring(0, 150);
            try
            {
                // the frame rate is 100 over the frame time from the settings, so every frame lands on a whole hundredth of a second, the unit a gif stores, and plays at an even pace (15 fps would mix 6 and 7 hundredths and stutter)
                int delay = ReplaykitSettings.GetGifFrameDelay();
                // one pass: fps and scale come first so every later step works on the small frames. the palette is read from 5 frames a second instead of all of them (its colour count is the slow part, and the picked colours come out the same), and paletteuse only redraws the part of a frame that changed (diff_mode=rectangle) with an ordered dither, which keeps gameplay gifs smaller than error diffusion and without its crawling noise. on a detailed 15 s 1080p60 clip this took the export from 40 s to 23 s and the file from 22.5 MB to 17.7 MB at the same quality
                var args = new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-ss",
                    start.ToString("0.###", CultureInfo.InvariantCulture), "-t",
                    duration.ToString("0.###", CultureInfo.InvariantCulture), "-i", source.Full,
                    "-an", "-sn", "-dn", "-filter_complex",
                    "[0:v:0]fps=100/" + delay.ToString(CultureInfo.InvariantCulture) + ",scale=640:640:force_original_aspect_ratio=decrease:flags=lanczos,split[a][b];" +
                    "[a]fps=5,palettegen[p];[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle",
                    "-loop", "0", "-f", "gif", temporary };
                // 2 minutes up to 20 fps; a higher rate has more frames to make and gets more time (3 min 20 s at 33 fps, 5 minutes at 50 fps)
                var result = Compression.InvokeNativeProgress(ffmpeg, args, _ => {}, JobCoordinator.Token(id), Math.Max(120000, 600000 / delay));
                if (result.ExitCode != 0 || !File.Exists(temporary) || new FileInfo(temporary).Length == 0)
                {
                    Log.Write("GIF export failed (exit " + result.ExitCode + "): " + string.Join(" ", result.Output.Where(line => line.Length > 0).Take(3)));
                    throw new IOException("The selected video could not be exported as a GIF.");
                }
                for (int suffix = 1; suffix <= 9999; suffix++)
                {
                    string output = stem + (suffix == 1 ? "" : " (" + suffix + ")") + ".gif";
                    string target = Path.Combine(folder, output);
                    if (File.Exists(target)) continue;
                    try { File.Move(temporary, target); return output; }
                    catch (IOException) when (File.Exists(target)) { }
                }
                throw new IOException("No available GIF filename.");
            }
            catch (TimeoutException) { throw new IOException("The GIF took too long to export."); }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Log.Write("GIF export cleanup: " + ex.Message); }
            }
        }
    }
}

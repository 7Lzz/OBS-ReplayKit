using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    internal static class GifExports
    {
        private static string Folder(bool create = false)
        {
            string folder = Path.Combine(AppConfig.GetClipDir(), "GIFs");
            if (create) Directory.CreateDirectory(folder);
            if (Directory.Exists(folder) && (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The GIF folder cannot be a link.");
            return folder;
        }

        private static bool ValidName(string name) =>
            !string.IsNullOrWhiteSpace(name) && name.Length <= 200 &&
            name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name == Path.GetFileName(name) &&
            !name.StartsWith("_replaykit_", StringComparison.OrdinalIgnoreCase) &&
            Path.GetExtension(name).Equals(".gif", StringComparison.OrdinalIgnoreCase);

        internal static JArray List()
        {
            string folder = Folder();
            if (!Directory.Exists(folder)) return new JArray();
            return new JArray(new DirectoryInfo(folder).EnumerateFiles("*.gif")
                .Where(file => ValidName(file.Name) && (file.Attributes & FileAttributes.ReparsePoint) == 0)
                .OrderByDescending(file => file.LastWriteTimeUtc).Select(file => file.Name));
        }

        internal static string Resolve(string name)
        {
            if (!ValidName(name)) return null;
            string path = Path.Combine(Folder(), name);
            return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 ? path : null;
        }

        internal static string Export(string name, double start, double end)
        {
            double duration = end - start;
            if (double.IsNaN(start) || double.IsInfinity(start) || double.IsNaN(duration) ||
                start < 0 || duration < .5 || duration > 15)
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
                var args = new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-ss",
                    start.ToString("0.###", CultureInfo.InvariantCulture), "-t",
                    duration.ToString("0.###", CultureInfo.InvariantCulture), "-i", source.Full,
                    "-an", "-filter_complex_threads", "1", "-filter_complex",
                    "[0:v:0]fps=15,scale=640:640:force_original_aspect_ratio=decrease:flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse",
                    "-loop", "0", temporary };
                var result = Compression.InvokeNativeProgress(ffmpeg, args, _ => {}, JobCoordinator.Token(id), 60000);
                if (result.ExitCode != 0 || !File.Exists(temporary) || new FileInfo(temporary).Length == 0)
                    throw new IOException("The selected video could not be exported as a GIF.");
                for (int suffix = 1; suffix <= 9999; suffix++)
                {
                    string output = stem + (suffix == 1 ? "" : " (" + suffix + ")") + ".gif";
                    string target = Path.Combine(folder, output);
                    try { File.Move(temporary, target); return output; }
                    catch (IOException) when (File.Exists(target)) { }
                }
                throw new IOException("No available GIF filename.");
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}

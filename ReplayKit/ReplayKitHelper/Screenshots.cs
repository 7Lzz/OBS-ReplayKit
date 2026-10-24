using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    internal static class Screenshots
    {
        private const int ThumbWidth = 480;
        private static readonly object ThumbGate = new object();
        public static string Folder(string clipFolder = null, bool create = false)
        {
            string root = Path.GetFullPath(clipFolder ?? AppConfig.GetClipDir());
            string folder = Path.Combine(root, "Screenshots");
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Clip folder not found");
            if (File.Exists(folder)) throw new IOException("The screenshots folder is occupied by a file");
            if (Directory.Exists(folder) && (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The screenshots folder cannot be a link");
            if (create) Directory.CreateDirectory(folder);
            if (Directory.Exists(folder) && (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The screenshots folder cannot be a link");
            return folder;
        }

        // a name a screenshot can have: a plain png file name, not one of the temporary files of a save
        private static bool ValidName(string name) =>
            !string.IsNullOrWhiteSpace(name) && name.Length <= 200 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name == Path.GetFileName(name) && name.Trim() == name &&
            string.Equals(Path.GetExtension(name), ".png", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("_replaykit_", StringComparison.OrdinalIgnoreCase);

        public static string Resolve(string name)
        {
            if (!ValidName(name)) return null;
            string path = Path.Combine(Folder(), name);
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
            return path;
        }

        // a small jpeg of the screenshot for the cards of the grid, made once for each state of the file and kept in the clip cache, so the grid never decodes the full png; the viewer still shows the png itself
        public static string Thumb(string path)
        {
            var info = new FileInfo(path);
            string dir = ClipCache.KindDir("shots");
            string thumb = Path.Combine(dir, ClipCache.KeyFor(info.Name, info, "thumb" + ThumbWidth) + ".jpg");
            lock (ThumbGate)
            {
                if (File.Exists(thumb))
                {
                    ClipCache.Touch(thumb);
                    return thumb;
                }
                Directory.CreateDirectory(dir);
                ClipCache.Prune(thumb);
                string temp = thumb + ".tmp";
                try
                {
                    // read through a stream that is closed right away, since an image opened from a path keeps the png locked for as long as it lives
                    using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var source = Image.FromStream(input, false, false))
                    {
                        int width = Math.Min(ThumbWidth, source.Width);
                        int height = Math.Max(1, (int)Math.Round(source.Height * (double)width / source.Width));
                        using (var scaled = new Bitmap(width, height, PixelFormat.Format24bppRgb))
                        {
                            using (var g = Graphics.FromImage(scaled))
                            {
                                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                                g.CompositingQuality = CompositingQuality.HighQuality;
                                g.DrawImage(source, 0, 0, width, height);
                            }
                            var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                            using (var quality = new EncoderParameters(1))
                            {
                                quality.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
                                scaled.Save(temp, jpeg, quality);
                            }
                        }
                    }
                    File.Move(temp, thumb);
                }
                // gdi+ reports a file it cannot read as a picture this way, not as a bad format
                catch (OutOfMemoryException) { throw new IOException("The screenshot could not be read as a picture"); }
                finally
                {
                    try { if (File.Exists(temp)) File.Delete(temp); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Log.Write("Screenshots thumb cleanup: " + ex.Message); }
                }
            }
            return thumb;
        }

        public static void Delete(string path) => File.Delete(path);

        // a screenshot that is renamed keeps its .png and stays in the screenshots folder; the new name of the file, or an exception that says why it could not be
        public static string Rename(string path, string to)
        {
            to = (to ?? "").Trim();
            if (!string.Equals(Path.GetExtension(to), ".png", StringComparison.OrdinalIgnoreCase)) to += ".png";
            if (!ValidName(to)) throw new ArgumentException("That name cannot be used");
            string target = Path.Combine(Path.GetDirectoryName(path), to);
            if (string.Equals(target, path, StringComparison.Ordinal)) return to;
            // a change of case only is the same file to windows, so it is moved on its own
            if (File.Exists(target) && !string.Equals(target, path, StringComparison.OrdinalIgnoreCase)) throw new IOException("A screenshot with that name already exists");
            File.Move(path, target);
            return to;
        }

        // the file and the picture both go on the clipboard: chat apps take the file the way they take a copied clip, and image editors take the picture
        public static void Copy(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            StaRunner.Run(() =>
            {
                using (var stream = new MemoryStream(bytes))
                using (var image = new Bitmap(stream))
                {
                    var data = new System.Windows.Forms.DataObject();
                    var files = new System.Collections.Specialized.StringCollection();
                    files.Add(path);
                    data.SetFileDropList(files);
                    data.SetImage(image);
                    System.Windows.Forms.Clipboard.SetDataObject(data, true);
                }
            });
        }

        public static JArray List()
        {
            string folder = Folder();
            if (!Directory.Exists(folder)) return new JArray();
            return new JArray(new DirectoryInfo(folder).EnumerateFiles("*.png", SearchOption.TopDirectoryOnly)
                .Where(file => (file.Attributes & FileAttributes.ReparsePoint) == 0 && !file.Name.StartsWith("_replaykit_", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => new JObject
                {
                    ["name"] = file.Name,
                    ["size"] = file.Length,
                    ["mtime"] = new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeSeconds(),
                }));
        }
    }
}

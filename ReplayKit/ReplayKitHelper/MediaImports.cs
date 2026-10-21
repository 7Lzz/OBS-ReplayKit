using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // pictures and gifs the editor lays over the video: a shape or a sticker the page drew and sent as png, or a gif, png or jpeg from the picker. each is kept as it came, by a hash of its bytes, in the clip cache where the saved copy reads it; only those three formats are taken, told apart by their first bytes rather than their names
    internal static class MediaImports
    {
        public const string UploadPath = "/import-media/upload";
        private const long MaxBytes = 50L * 1024 * 1024;
        private static readonly string Root = ClipCache.KindDir("media");
        private static readonly Regex IdPattern = new Regex("^[0-9a-f]{16}$", RegexOptions.Compiled);
        private static readonly string[] Extensions = { ".png", ".gif", ".jpg" };

        public static bool IsId(string id) => id != null && IdPattern.IsMatch(id);

        private static JObject Fail(string name, string message) => new JObject { ["ok"] = false, ["name"] = name, ["message"] = message };

        // the file of an import, or null when the id is not one this helper made or the cache let it go
        public static string PathFor(string id)
        {
            if (!IsId(id)) return null;
            string dir = Path.Combine(Root, id);
            if (!File.Exists(Path.Combine(dir, ClipCache.DoneMarker))) return null;
            foreach (string ext in Extensions)
            {
                string file = Path.Combine(dir, "media" + ext);
                if (!File.Exists(file)) continue;
                try { ClipCache.Touch(dir); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                return file;
            }
            return null;
        }

        public static string ContentType(string path)
        {
            switch (Path.GetExtension(path))
            {
                case ".gif": return "image/gif";
                case ".jpg": return "image/jpeg";
                default: return "image/png";
            }
        }

        public static bool IsGif(string path) => Path.GetExtension(path) == ".gif";

        // the format by its first bytes, or null when it is none of the ones taken
        private static string Sniff(byte[] head, int count)
        {
            if (count >= 8 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47 && head[4] == 0x0D && head[5] == 0x0A && head[6] == 0x1A && head[7] == 0x0A) return ".png";
            if (count >= 6 && head[0] == 0x47 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x38 && (head[4] == 0x37 || head[4] == 0x39) && head[5] == 0x61) return ".gif";
            if (count >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return ".jpg";
            return null;
        }

        public static JObject FromPath(string path)
        {
            string name = NameFrom(path);
            try
            {
                // the path comes from the dialog of this helper, and anything that is not a plain file on a drive or a share is turned away all the same
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || Path.GetPathRoot(path).Length < 3 || path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.StartsWith(@"\\?\", StringComparison.Ordinal))
                    return Fail(name, "That file cannot be used");
                var info = new FileInfo(path);
                if (!info.Exists) return Fail(name, "That file was not found");
                if (info.Length <= 0) return Fail(name, "That file is empty");
                if (info.Length > MaxBytes) return Fail(name, "That file is larger than 50 MB");
                return Store(File.ReadAllBytes(info.FullName), name);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                Log.Write("MediaImports: " + name + ": " + ex.Message);
                return Fail(name, "That file could not be opened");
            }
        }

        public static JObject FromUpload(HttpRequest req, string fileName, out int status)
        {
            status = 200;
            string name = NameFrom(fileName);
            if (req.UploadReader == null || req.UploadLength <= 0) { status = 400; return Fail(name, "That file is empty"); }
            if (req.UploadLength > MaxBytes) { status = 413; return Fail(name, "That file is larger than 50 MB"); }
            try
            {
                using (var ms = new MemoryStream())
                using (var sha = SHA1.Create())
                {
                    Connection.CopyUploadBody(req, ms, sha);
                    var made = Store(ms.ToArray(), name);
                    if (made.Value<bool>("ok") != true) status = 400;
                    return made;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
            {
                Log.Write("MediaImports: upload of " + name + " failed: " + ex.Message);
                status = 400;
                return Fail(name, "The file could not be received");
            }
        }

        // the same bytes twice are one import
        private static JObject Store(byte[] bytes, string name)
        {
            string ext = Sniff(bytes, bytes.Length);
            if (ext == null) return Fail(name, "Only GIF, PNG and JPEG files can be added");
            string id;
            using (var sha = SHA1.Create()) id = string.Concat(sha.ComputeHash(bytes).Take(8).Select(b => b.ToString("x2")));
            string dir = Path.Combine(Root, id);
            if (!File.Exists(Path.Combine(dir, ClipCache.DoneMarker)))
            {
                ClipCache.Prune(dir);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, "media" + ext), bytes);
                File.WriteAllText(Path.Combine(dir, ClipCache.DoneMarker), "ok");
            }
            return new JObject { ["ok"] = true, ["id"] = id, ["name"] = name, ["gif"] = ext == ".gif" };
        }

        // the name a layer carries: the file name without its folder and extension, free of control characters and not too long
        private static string NameFrom(string raw)
        {
            raw = raw ?? "";
            string name = raw.Substring(Math.Max(raw.LastIndexOf('\\'), raw.LastIndexOf('/')) + 1);
            int dot = name.LastIndexOf('.');
            if (dot > 0) name = name.Substring(0, dot);
            name = Regex.Replace(Regex.Replace(name, @"\p{C}+", " "), @"\s+", " ").Trim();
            if (name.Length > 60) name = name.Substring(0, 60).Trim();
            return name.Length == 0 ? "Image" : name;
        }
    }
}

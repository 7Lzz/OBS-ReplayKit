using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // videos from outside the clip folder that the library of the editor lays over a clip. the file is read where it lies and never copied or changed: the cache keeps a small entry per file with its path and the size and time it had when picked, and a ref of the form rkvideo-<16 hex> stands for it in the editor, its drafts and the saved copy. only the read-only routes of the editor resolve a ref, so nothing that deletes or renames clips can reach the file
    internal static class VideoImports
    {
        private const string Prefix = "rkvideo-";
        private const string SourceFile = "source.json";
        private static readonly string Root = ClipCache.KindDir("videos");
        private static readonly string[] Extensions = { ".mp4", ".m4v", ".mov", ".mkv", ".webm" };
        private static readonly object StoreGate = new object();

        public static bool IsRef(string reference) =>
            reference != null && reference.StartsWith(Prefix, StringComparison.Ordinal) && ClipCache.IsValidKey(reference.Substring(Prefix.Length));

        public static string DirFor(string reference) => Path.Combine(Root, reference.Substring(Prefix.Length));

        private static JObject Fail(string name, string message) => new JObject { ["ok"] = false, ["name"] = name, ["message"] = message };

        // the file a ref stands for, or null when the ref is not one this helper made, the cache let it go, or the file was moved or changed since it was picked
        public static Clips.SafeClipPath Resolve(string reference)
        {
            if (!IsRef(reference)) return null;
            string dir = DirFor(reference);
            try
            {
                string record = Path.Combine(dir, SourceFile);
                if (!File.Exists(Path.Combine(dir, ClipCache.DoneMarker)) || !File.Exists(record)) return null;
                var saved = JObject.Parse(File.ReadAllText(record));
                string path = saved.Value<string>("path");
                if (!AudioImports.IsUsableLocalPath(path)) return null;
                var info = new FileInfo(path);
                if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0) return null;
                if (saved.Value<long?>("size") != info.Length || saved.Value<long?>("mtimeTicks") != info.LastWriteTimeUtc.Ticks) return null;
                ClipCache.Touch(dir);
                return new Clips.SafeClipPath { Name = reference, Full = info.FullName };
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is ArgumentException || ex is NotSupportedException || ex is FormatException || ex is InvalidCastException)
            {
                Log.Write("VideoImports: " + reference + ": " + ex.Message);
                return null;
            }
        }

        public static JObject FromPath(string path)
        {
            string name = AudioImports.CleanImportName(path, Locales.Message("editor.video"), 80);
            try
            {
                if (!AudioImports.IsUsableLocalPath(path)) return Fail(name, Locales.Message("editor.fileCannotBeUsed"));
                var info = new FileInfo(Path.GetFullPath(path));
                if (!info.Exists) return Fail(name, Locales.Message("editor.fileWasNotFound"));
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return Fail(name, Locales.Message("editor.fileCannotBeUsed"));
                if (!Extensions.Contains(info.Extension.ToLowerInvariant())) return Fail(name, Locales.Message("editor.onlyMp4MovMkvWebm"));
                if (info.Length <= 0) return Fail(name, Locales.Message("editor.thatFileIsEmpty"));
                var probe = Trim.ProbeVideo(info.FullName);
                if (probe == null || probe.Duration <= 0 || probe.Width <= 0 || probe.Height <= 0) return Fail(name, Locales.Message("editor.videoCouldNotBeRead"));
                string id;
                using (var sha = SHA1.Create())
                {
                    byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(info.FullName.ToUpperInvariant() + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks));
                    id = string.Concat(hash.Take(8).Select(b => b.ToString("x2")));
                }
                string reference = Prefix + id;
                string dir = DirFor(reference);
                lock (StoreGate)
                {
                    if (Resolve(reference) == null)
                    {
                        ClipCache.Prune(dir);
                        Directory.CreateDirectory(dir);
                        var record = new JObject
                        {
                            ["path"] = info.FullName,
                            ["size"] = info.Length,
                            ["mtimeTicks"] = info.LastWriteTimeUtc.Ticks,
                        };
                        File.WriteAllText(Path.Combine(dir, SourceFile), record.ToString(Formatting.None));
                        File.WriteAllText(Path.Combine(dir, ClipCache.DoneMarker), "");
                    }
                }
                return new JObject
                {
                    ["ok"] = true,
                    ["ref"] = reference,
                    ["name"] = name,
                    ["duration"] = probe.Duration,
                    ["width"] = probe.Width,
                    ["height"] = probe.Height,
                };
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                Log.Write("VideoImports: " + name + ": " + ex.Message);
                return Fail(name, Locales.Message("editor.fileCouldNotBeOpened"));
            }
        }
    }
}

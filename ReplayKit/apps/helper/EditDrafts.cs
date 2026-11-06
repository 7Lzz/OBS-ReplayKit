using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // Drafts are tied to a source file and keep their imported assets in the cache.
    internal static class EditDrafts
    {
        public const int MaxEditBytes = 512 * 1024;
        private const string FileName = "edit_drafts.json";

        private static string StorePath() => Path.Combine(AppConfig.GetReplayKitRoot(), FileName);

        private static JObject Fail(string message) => new JObject { ["ok"] = false, ["message"] = message };

        private static string FullPath(string name) => Path.GetFullPath(Path.IsPathRooted(name) ? name : Clips.LocateClip(name));
        private static string Key(string path) => FullPath(path).ToUpperInvariant();

        private static bool Matches(JObject entry, string path)
        {
            if (entry == null) return false;
            try
            {
                string source = entry["source"]?.Value<string>();
                if (source != null && Key(source) != Key(path)) return false;
                var file = new FileInfo(path);
                return file.Exists && (file.Attributes & FileAttributes.ReparsePoint) == 0 && entry["size"]?.Value<long>() == file.Length &&
                    (entry["mtimeTicks"] != null ? entry["mtimeTicks"].Value<long>() == file.LastWriteTimeUtc.Ticks :
                        entry["mtime"]?.Value<long>() == new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeSeconds());
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is FormatException || ex is InvalidCastException) { return false; }
        }

        private static JObject Entry(JObject db, Clips.SafeClipPath clip, out bool migrated)
        {
            migrated = false;
            string key = Key(clip.Full);
            if (db[key] is JObject scoped) return Matches(scoped, clip.Full) ? scoped : null;
            if (!(db[clip.Name] is JObject legacy) || !Matches(legacy, clip.Full)) return null;
            var entry = (JObject)legacy.DeepClone();
            entry["source"] = clip.Full;
            entry["mtimeTicks"] = File.GetLastWriteTimeUtc(clip.Full).Ticks;
            db[key] = entry;
            db.Remove(clip.Name);
            migrated = true;
            return entry;
        }

        // Show only drafts of the current folder. Switching folders never deletes another folder's edits.
        public static JObject List()
        {
            var rows = new List<JObject>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string folder = Path.GetFullPath(AppConfig.GetClipDir());
            try
            {
                ClipStore.Update(StorePath(), db =>
                {
                    bool changed = false;
                    foreach (var prop in db.Properties().ToList())
                    {
                        if (!(prop.Value is JObject saved)) continue;
                        if (saved["source"] != null && saved["source"].Type != JTokenType.String) continue;
                        string source = saved["source"]?.Value<string>();
                        string name = source == null ? prop.Name : Path.GetFileName(source);
                        var clip = Clips.GetSafeClipPath(name);
                        if (clip == null || !string.Equals(Clips.LibraryRoot(Path.GetDirectoryName(clip.Full)), folder, StringComparison.OrdinalIgnoreCase) ||
                            (source != null && Key(source) != Key(clip.Full)) || !seen.Add(clip.Name)) continue;
                        var entry = Entry(db, clip, out bool migrated);
                        changed |= migrated;
                        if (entry == null) continue;
                        rows.Add(new JObject
                        {
                            ["name"] = clip.Name,
                            ["savedAt"] = entry["savedAt"]?.Value<long>() ?? 0,
                            ["duration"] = entry["duration"]?.Value<double>() ?? 0,
                        });
                    }
                    return changed;
                });
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
            {
                Log.Write("EditDrafts.List: " + ex.Message);
            }
            return new JObject { ["ok"] = true, ["drafts"] = new JArray(rows.OrderByDescending(r => r.Value<long>("savedAt"))) };
        }

        public static JObject Get(string name)
        {
            var clip = Clips.GetSafeClipPath(name);
            if (clip == null) return Fail("Bad filename");
            JObject entry = null;
            try { ClipStore.Update(StorePath(), db => { entry = Entry(db, clip, out bool migrated); return migrated; }); }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
            {
                Log.Write("EditDrafts.Get: " + ex.Message);
                return Fail("The drafts could not be read");
            }
            if (entry == null || !(entry["edit"] is JObject)) return Fail("There is no draft for this clip");
            return new JObject
            {
                ["ok"] = true,
                ["name"] = clip.Name,
                ["savedAt"] = entry["savedAt"]?.Value<long>() ?? 0,
                ["duration"] = entry["duration"]?.Value<double>() ?? 0,
                ["edit"] = entry["edit"],
            };
        }

        public static JObject Save(string name, JObject edit)
        {
            var clip = Clips.GetSafeClipPath(name);
            if (clip == null || !File.Exists(clip.Full)) return Fail("Clip not found");
            if (edit == null) return Fail("Bad edit");
            if (Encoding.UTF8.GetByteCount(edit.ToString(Formatting.None)) > MaxEditBytes) return Fail("The edit is too large to keep");
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            double duration = edit["duration"]?.Type == JTokenType.Float || edit["duration"]?.Type == JTokenType.Integer ? edit["duration"].Value<double>() : 0;
            if (double.IsNaN(duration) || double.IsInfinity(duration) || duration < 0) return Fail("Invalid draft duration");
            var info = new FileInfo(clip.Full);
            try
            {
                ClipStore.Update(StorePath(), db =>
                {
                    if (Matches(db[clip.Name] as JObject, clip.Full)) db.Remove(clip.Name);
                    db[Key(clip.Full)] = new JObject
                    {
                        ["source"] = clip.Full,
                        ["savedAt"] = now,
                        ["size"] = info.Length,
                        ["mtime"] = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds(),
                        ["mtimeTicks"] = info.LastWriteTimeUtc.Ticks,
                        ["duration"] = duration,
                        ["edit"] = edit,
                    };
                    return true;
                });
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
            {
                Log.Write("EditDrafts.Save: " + ex.Message);
                return Fail("The draft could not be kept");
            }
            // the audio files a draft brought in live in a cache that is pruned by use, so each save counts as a use
            if (edit["imports"] is JArray imports)
            {
                foreach (var import in imports.OfType<JObject>().Take(16))
                {
                    string id = import["id"]?.Value<string>();
                    if (AudioImports.IsId(id)) AudioImports.PathFor(id);
                }
            }
            return new JObject { ["ok"] = true, ["savedAt"] = now };
        }

        public static JObject Delete(string name)
        {
            var clip = Clips.GetSafeClipPath(name);
            if (clip == null) return Fail("Bad filename");
            try { ClipStore.Update(StorePath(), db => Remove(db, clip.Full)); }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
            {
                Log.Write("EditDrafts.Delete: " + ex.Message);
                return Fail("The draft could not be removed");
            }
            return new JObject { ["ok"] = true };
        }

        // a clip that was renamed keeps its draft under the new name; one that was deleted loses it. both are best effort, since the clip itself is already done with
        public static void Rename(string from, string to)
        {
            try
            {
                ClipStore.Update(StorePath(), db =>
                {
                    string source = FullPath(from), target = FullPath(to), key = Key(source);
                    var entry = db[key] as JObject;
                    if (entry == null && Matches(db[Path.GetFileName(source)] as JObject, target))
                    {
                        key = Path.GetFileName(source);
                        entry = db[key] as JObject;
                    }
                    if (entry == null) return false;
                    var moved = (JObject)entry.DeepClone();
                    moved["source"] = target;
                    if (key != Key(target)) db.Remove(key);
                    db[Key(target)] = moved;
                    return true;
                });
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException) { Log.Write("EditDrafts.Rename: " + ex.Message); }
        }

        // a clip compressed in place is the same edit on a smaller file, so the draft of the version it replaced moves to the new one; a draft saved for the new file in the meantime is left as it is
        public static void Follow(string path, long oldSize, long oldMtimeTicks)
        {
            try
            {
                ClipStore.Update(StorePath(), db =>
                {
                    var file = new FileInfo(path);
                    if (!file.Exists) return false;
                    long oldSeconds = new DateTimeOffset(new DateTime(oldMtimeTicks, DateTimeKind.Utc)).ToUnixTimeSeconds();
                    bool changed = false;
                    foreach (string key in new[] { Key(path), Path.GetFileName(path) })
                    {
                        if (!(db[key] is JObject entry)) continue;
                        if (key != Key(path) && entry["source"] != null) continue;
                        if (entry["size"]?.Value<long>() != oldSize) continue;
                        if (entry["mtimeTicks"] != null ? entry["mtimeTicks"].Value<long>() != oldMtimeTicks : entry["mtime"]?.Value<long>() != oldSeconds) continue;
                        entry["size"] = file.Length;
                        entry["mtime"] = new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeSeconds();
                        entry["mtimeTicks"] = file.LastWriteTimeUtc.Ticks;
                        changed = true;
                    }
                    return changed;
                });
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is FormatException || ex is InvalidCastException)
            {
                Log.Write("EditDrafts.Follow: " + ex.Message);
            }
        }

        public static void Forget(string name)
        {
            try { ClipStore.Update(StorePath(), db => Remove(db, FullPath(name))); }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException) { Log.Write("EditDrafts.Forget: " + ex.Message); }
        }

        private static bool Remove(JObject db, string path)
        {
            bool changed = db.Remove(Key(path));
            string name = Path.GetFileName(path);
            if (Matches(db[name] as JObject, path)) changed |= db.Remove(name);
            return changed;
        }

        public static HashSet<string> ReferencedAssets()
        {
            try
            {
                return ClipStore.Read(StorePath(), db =>
                {
                    var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var prop in db.Properties())
                    {
                        if (!(prop.Value is JObject entry) || !(entry["edit"] is JObject edit)) continue;
                        if (entry["source"] != null && entry["source"].Type != JTokenType.String) continue;
                        string source = entry["source"]?.Value<string>();
                        if (source == null)
                        {
                            var clip = Clips.GetSafeClipPath(prop.Name);
                            if (clip == null) continue;
                            source = clip.Full;
                        }
                        if (!Matches(entry, source)) continue;
                        foreach (var field in edit.Descendants().OfType<JProperty>())
                        {
                            if ((field.Name != "id" && field.Name != "ref") || field.Value.Type != JTokenType.String) continue;
                            string id = field.Value.Value<string>();
                            if (AudioImports.IsId(id)) paths.Add(Path.Combine(ClipCache.KindDir("imports"), id));
                            if (MediaImports.IsId(id)) paths.Add(Path.Combine(ClipCache.KindDir("media"), id));
                            if (VideoImports.IsRef(id)) paths.Add(VideoImports.DirFor(id));
                        }
                    }
                    return paths;
                });
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
            {
                Log.Write("EditDrafts.ReferencedAssets: " + ex.Message);
                return null;
            }
        }
    }
}

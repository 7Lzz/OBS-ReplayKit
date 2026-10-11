using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // the unsaved edits of the clip editor, one per clip, kept in edit_drafts.json beside the clip database so an obs that was closed, or another view that was opened, does not take them away. the page sends the whole edit as json and gets the same json back; the helper only checks that the clip is there and that the edit is a sane size, and never reads into it
    internal static class EditDrafts
    {
        public const int MaxEditBytes = 512 * 1024;
        private const string FileName = "edit_drafts.json";

        private static string StorePath() => Path.Combine(AppConfig.GetReplayKitRoot(), FileName);

        private static JObject Fail(string message) => new JObject { ["ok"] = false, ["message"] = message };

        // every draft in short form, newest first. the draft of a clip that is no longer in the folder is dropped on the way, since there is nothing left to open it on
        public static JObject List()
        {
            var rows = new List<JObject>();
            var gone = new List<string>();
            try
            {
                ClipStore.Read(StorePath(), db =>
                {
                    foreach (var prop in db.Properties())
                    {
                        if (!(prop.Value is JObject entry)) continue;
                        var clip = Clips.GetSafeClipPath(prop.Name);
                        if (clip == null || !File.Exists(clip.Full))
                        {
                            gone.Add(prop.Name);
                            continue;
                        }
                        rows.Add(new JObject
                        {
                            ["name"] = prop.Name,
                            ["savedAt"] = entry["savedAt"]?.Value<long>() ?? 0,
                            ["duration"] = entry["duration"]?.Value<double>() ?? 0,
                        });
                    }
                    return true;
                });
                if (gone.Count > 0)
                    ClipStore.Update(StorePath(), db => gone.Aggregate(false, (changed, name) => db.Remove(name) || changed));
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
            try { entry = ClipStore.Read(StorePath(), db => db[clip.Name] as JObject); }
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
            if (edit.ToString(Formatting.None).Length > MaxEditBytes) return Fail("The edit is too large to keep");
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            double duration = edit["duration"]?.Type == JTokenType.Float || edit["duration"]?.Type == JTokenType.Integer ? edit["duration"].Value<double>() : 0;
            var info = new FileInfo(clip.Full);
            try
            {
                ClipStore.Update(StorePath(), db =>
                {
                    db[clip.Name] = new JObject
                    {
                        ["savedAt"] = now,
                        ["size"] = info.Length,
                        ["mtime"] = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds(),
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
            try { ClipStore.Update(StorePath(), db => db.Remove(clip.Name)); }
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
                    if (!db.ContainsKey(from)) return false;
                    db[to] = db[from];
                    db.Remove(from);
                    return true;
                });
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException) { Log.Write("EditDrafts.Rename: " + ex.Message); }
        }

        public static void Forget(string name)
        {
            try { ClipStore.Update(StorePath(), db => db.Remove(name)); }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException) { Log.Write("EditDrafts.Forget: " + ex.Message); }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // looks saved in the clip editor apart from any clip, the style of a subtitle row and the look of the video, each a list the user names and orders; the editor checks a look again before using it, so this keeps only the shape and size of each in bounds
    internal static class EditorPresets
    {
        private const string FileName = "editor_presets.json";
        private const int MaxPresets = 100;
        private const int MaxNameLength = 40;
        private const int MaxDataBytes = 16 * 1024;
        private static readonly string[] Kinds = { "subtitle", "video" };
        private static readonly Regex Id = new Regex("^[A-Za-z0-9_-]{1,40}$", RegexOptions.CultureInvariant);

        private static string StorePath() => Path.Combine(AppConfig.GetReplayKitRoot(), FileName);

        private static JObject Fail(string message) => new JObject { ["ok"] = false, ["message"] = message };

        // a preset as it is kept, or null when it is not one
        private static JObject Clean(JToken token, HashSet<string> ids)
        {
            if (!(token is JObject p) || !(p["data"] is JObject data)) return null;
            string id = p["id"]?.Type == JTokenType.String ? p.Value<string>("id") : null;
            string name = p["name"]?.Type == JTokenType.String ? new string(p.Value<string>("name").Where(c => !char.IsControl(c)).ToArray()).Trim() : null;
            if (id == null || !Id.IsMatch(id) || !ids.Add(id)) return null;
            if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength) return null;
            if (Encoding.UTF8.GetByteCount(data.ToString(Formatting.None)) > MaxDataBytes) return null;
            return new JObject { ["id"] = id, ["name"] = name, ["data"] = data };
        }

        private static JArray CleanList(JToken list)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var kept = new JArray();
            if (list is JArray items)
                foreach (var item in items.Take(MaxPresets))
                {
                    var p = Clean(item, ids);
                    if (p != null) kept.Add(p);
                }
            return kept;
        }

        public static JObject List()
        {
            try
            {
                return ClipStore.Read(StorePath(), db =>
                {
                    var result = new JObject { ["ok"] = true };
                    foreach (string kind in Kinds) result[kind] = CleanList(db[kind]);
                    return result;
                });
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
            {
                Log.Write("EditorPresets.List: " + ex.Message);
                return Fail("The presets could not be read");
            }
        }

        // the whole list of one kind as the editor has it now; a list with anything in it that is not a preset is refused rather than trimmed, so nothing is lost without a word
        public static JObject Save(string kind, string body)
        {
            if (Array.IndexOf(Kinds, kind) < 0) return Fail("Unknown kind of preset");
            JArray list;
            try { list = JObject.Parse(body ?? "")["presets"] as JArray; }
            catch (JsonException) { return Fail("Bad presets"); }
            if (list == null || list.Count > MaxPresets) return Fail(list == null ? "Bad presets" : "There are too many presets. Delete some first.");
            var kept = CleanList(list);
            if (kept.Count != list.Count) return Fail("Bad presets");
            try
            {
                ClipStore.Update(StorePath(), db =>
                {
                    db[kind] = kept;
                    return true;
                });
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
            {
                Log.Write("EditorPresets.Save: " + ex.Message);
                return Fail("The presets could not be saved");
            }
            return List();
        }
    }
}

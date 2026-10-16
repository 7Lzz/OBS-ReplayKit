using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // the Bango Cat folder beside obs64.exe as the overlay preview needs it: every readable mode with its config and the bake of each of its models, and its pictures one by one
    internal static class BongoCatFiles
    {
        private static string Root() => Path.Combine(Path.GetDirectoryName(Constants.ResolveObsExe()) ?? "", "Bango Cat");

        private static JObject ReadJson(string path)
        {
            try { return File.Exists(path) && new FileInfo(path).Length <= 1024 * 1024 ? JObject.Parse(File.ReadAllText(path)) : null; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is Newtonsoft.Json.JsonException) { return null; }
        }

        private static string Relative(string root, string full) => full.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');

        // a model folder like the plugin reads it (its first *.model3.json); the bake is named by the md5 of the moc3, the same name replaykit.dll looks for
        private static JObject Model(string root, string folder)
        {
            if (!Directory.Exists(folder)) return null;
            string setting = Directory.EnumerateFiles(folder, "*.model3.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            var json = setting == null ? null : ReadJson(setting);
            string moc = json?["FileReferences"]?["Moc"]?.Value<string>();
            string texture = (json?["FileReferences"]?["Textures"] as JArray)?.FirstOrDefault()?.Value<string>();
            if (string.IsNullOrEmpty(moc) || string.IsNullOrEmpty(texture)) return null;
            string mocPath = Path.Combine(folder, moc);
            if (!File.Exists(mocPath)) return null;
            string md5;
            using (var hash = MD5.Create())
            using (var stream = File.OpenRead(mocPath))
                md5 = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            var blink = (json["Groups"] as JArray ?? new JArray()).OfType<JObject>()
                .FirstOrDefault(g => g["Target"]?.Value<string>() == "Parameter" && g["Name"]?.Value<string>() == "EyeBlink")?["Ids"] as JArray;
            return new JObject { ["bake"] = md5, ["texture"] = Relative(root, Path.Combine(folder, texture)), ["blinkIds"] = blink ?? new JArray() };
        }

        public static JObject Manifest()
        {
            string root = Root();
            var modes = new JArray();
            foreach (var name in (ReadJson(Path.Combine(root, "mode", "config.json"))?["ModelPath"] as JArray ?? new JArray()).Select(t => t.Value<string>()))
            {
                if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;
                string dir = Path.Combine(root, "mode", name);
                var config = ReadJson(Path.Combine(dir, "config.json"));
                if (config == null) continue;
                JObject ModelAt(string key) { string rel = config[key]?.Value<string>(); return string.IsNullOrEmpty(rel) ? null : Model(root, Path.Combine(dir, rel)); }
                modes.Add(new JObject
                {
                    ["name"] = name, ["config"] = config,
                    ["cat"] = ModelAt("CatModelPath"), ["left"] = ModelAt("ModelLeftHandModelPath"), ["right"] = ModelAt("ModelRightHandModelPath"),
                });
            }
            var face = ReadJson(Path.Combine(root, "face", "config.json"));
            return new JObject { ["ok"] = modes.Count > 0, ["modes"] = modes, ["face"] = face ?? new JObject() };
        }

        // a png inside the folder, or null; anything that resolves outside it is refused
        public static string Picture(string relative)
        {
            if (string.IsNullOrEmpty(relative) || !relative.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return null;
            string root = Path.GetFullPath(Root()).TrimEnd('\\') + "\\";
            string full;
            try { full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', '\\'))); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return null; }
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? full : null;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using ReplayKitShared;

namespace ReplayKitSetup
{
    // the language of the setup windows: the language of obs (or the windows one) read from the translation files inside the exe, never from the network or the helper; any problem leaves the table empty so the windows stay english
    internal static class SetupLocale
    {
        private const string BundleResource = "ReplayKitSetup.assets.bundle.zip";
        private const string RuntimeResource = "ReplayKitSetup._i18n.html";
        private const string LocaleDir = "obs-studio/obs-replayKit/locale/";
        private const long MaxLocaleBytes = 4 * 1024 * 1024;
        private static readonly Regex Placeholder = new Regex(@"\{([A-Za-z0-9_]+)\}", RegexOptions.CultureInvariant);
        private static readonly Regex HeadOpen = new Regex(@"<head(\s[^>]*)?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly object Gate = new object();
        private static bool loaded;
        private static string code = LanguageMatch.English;
        private static Dictionary<string, string> table = new Dictionary<string, string>(StringComparer.Ordinal);
        private static JObject messages = new JObject();

        internal static string Message(string id, params object[] pairs)
        {
            Ensure();
            var value = messages[id];
            return MessageCatalog.Format(value?.Type == JTokenType.String ? value.Value<string>() : id, code, pairs);
        }

        // the english text in the current language, with {name} placeholders filled from the name/value pairs; an unknown text stays english
        public static string T(string english, params object[] pairs)
        {
            if (english == null) return "";
            Ensure();
            string text = table.TryGetValue(english, out string translated) ? translated : english;
            if (pairs == null || pairs.Length < 2) return text;
            var vars = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i + 1 < pairs.Length; i += 2) vars[Convert.ToString(pairs[i])] = Convert.ToString(pairs[i + 1]);
            return Placeholder.Replace(text, m => vars.TryGetValue(m.Groups[1].Value, out string v) ? v : m.Value);
        }

        // puts the table and the page runtime right after <head> so the first text the parser adds is already translated
        public static string Inject(string html)
        {
            try
            {
                Ensure();
                Match head = HeadOpen.Match(html);
                if (!head.Success) return html;
                string runtime = ReadRuntime();
                if (runtime == null) return html;
                string block = "<script>window.RK_LANG=" + JsonString(code) + ";window.RK_STRINGS=" + TableJson() +
                    ";window.RK_MESSAGES=" + Newtonsoft.Json.JsonConvert.SerializeObject(messages,
                        new Newtonsoft.Json.JsonSerializerSettings { StringEscapeHandling = Newtonsoft.Json.StringEscapeHandling.EscapeHtml }) +
                    ";</script>" + runtime;
                return html.Insert(head.Index + head.Length, block);
            }
            catch (Exception ex)
            {
                SetupLog.Line("page translation skipped: " + ex.Message);
                return html;
            }
        }

        private static void Ensure()
        {
            lock (Gate)
            {
                if (loaded) return;
                loaded = true;
                try { Load(); }
                catch (Exception ex)
                {
                    code = LanguageMatch.English;
                    table = new Dictionary<string, string>(StringComparer.Ordinal);
                    SetupLog.Line("translations not loaded, using english: " + ex.Message);
                }
            }
        }

        private static void Load()
        {
            using Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(BundleResource);
            if (resource == null) return;
            using var zip = new ZipArchive(resource, ZipArchiveMode.Read);
            string messagePath = LocaleDir + MessageCatalog.Folder + "/";
            ReadMessages(zip, messagePath, LanguageMatch.English);
            var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (!entry.FullName.StartsWith(LocaleDir, StringComparison.Ordinal) || !entry.FullName.EndsWith(".json", StringComparison.Ordinal)) continue;
                string name = entry.FullName.Substring(LocaleDir.Length, entry.FullName.Length - LocaleDir.Length - ".json".Length);
                if (LanguageMatch.IsSupported(name)) files[name] = entry;
            }
            string chosen = ObsLanguage();
            if (chosen == null)
            {
                var codes = files.Keys.ToList();
                if (!codes.Contains(LanguageMatch.English)) codes.Add(LanguageMatch.English);
                string exe = Obs.FindObsExe();
                if (exe != null)
                {
                    string ini = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(exe), "..", "..", "data", "obs-studio", "locale.ini"));
                    if (File.Exists(ini)) codes = LanguageMatch.ReadObsLanguages(ini).Select(item => item.Key).ToList();
                }
                chosen = LanguageMatch.WindowsDefault(codes);
            }
            if (LanguageMatch.IsSupported(chosen) && chosen != LanguageMatch.English)
                ReadMessages(zip, messagePath, chosen);
            if (!files.TryGetValue(chosen, out ZipArchiveEntry file) || string.Equals(chosen, LanguageMatch.English, StringComparison.OrdinalIgnoreCase)) return;
            if (file.Length > MaxLocaleBytes) throw new InvalidDataException("translation file is too large");
            using Stream stream = file.Open();
            using JsonDocument doc = JsonDocument.Parse(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("translation file is not an object");
            var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty property in doc.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String) continue;
                string value = property.Value.GetString();
                if (string.IsNullOrEmpty(value) || !SamePlaceholders(property.Name, value)) continue;
                parsed[property.Name] = value;
            }
            table = parsed;
            code = chosen;
        }

        private static void ReadMessages(ZipArchive zip, string path, string language)
        {
            var entry = zip.GetEntry(path + language + ".json");
            if (entry == null || entry.Length > MaxLocaleBytes) return;
            using Stream stream = entry.Open();
            var catalog = MessageCatalog.Read(stream);
            if (language == LanguageMatch.English) messages = catalog;
            else MessageCatalog.Overlay(messages, catalog);
        }

        private static bool SamePlaceholders(string english, string translated)
        {
            var a = Placeholder.Matches(english).Cast<Match>().Select(m => m.Value).OrderBy(s => s, StringComparer.Ordinal);
            var b = Placeholder.Matches(translated).Cast<Match>().Select(m => m.Value).OrderBy(s => s, StringComparer.Ordinal);
            return a.SequenceEqual(b);
        }

        // [General] Language of the obs user.ini, read only; null when it is missing or not a language code
        private static string ObsLanguage()
        {
            try
            {
                string path = Path.Combine(Config.OBS_CONFIG, "user.ini");
                if (!File.Exists(path)) return null;
                string text;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(fs, Encoding.UTF8, true))
                    text = reader.ReadToEnd();
                bool general = false;
                foreach (string raw in text.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("[", StringComparison.Ordinal)) { general = string.Equals(line, "[General]", StringComparison.OrdinalIgnoreCase); continue; }
                    if (!general) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0 || !string.Equals(line.Substring(0, eq).Trim(), "Language", StringComparison.OrdinalIgnoreCase)) continue;
                    string value = line.Substring(eq + 1).Trim();
                    return LanguageMatch.IsCode(value) ? value : null;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            return null;
        }

        private static string ReadRuntime()
        {
            using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(RuntimeResource);
            if (stream == null) return null;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        private static string TableJson()
        {
            var sb = new StringBuilder("{");
            bool first = true;
            foreach (KeyValuePair<string, string> pair in table)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(JsonString(pair.Key)).Append(':').Append(JsonString(pair.Value));
            }
            return sb.Append('}').ToString();
        }

        // json string safe inside a script element: angle brackets, ampersands and the line separators are escaped so nothing can close the script
        private static string JsonString(string s)
        {
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '<': case '>': case '&': case (char)0x2028: case (char)0x2029:
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                        break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}

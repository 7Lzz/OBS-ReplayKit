using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using ReplayKitShared;

namespace ReplayKitSetup
{
    // the language of the setup windows: the language of obs (or the windows one) read from the message catalogs inside the exe, never from the network or the helper; any problem leaves the windows english
    public static class SetupLocale
    {
        private const string BundleResource = "ReplayKitSetup.assets.bundle.zip";
        private const string RuntimeResource = "ReplayKitSetup._i18n.html";
        private const string LocaleDir = "obs-studio/" + ReplayKitFolder.Name + "/locale/";
        private const long MaxLocaleBytes = 4 * 1024 * 1024;
        private static readonly Regex HeadOpen = new Regex(@"<head(\s[^>]*)?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly object Gate = new object();
        private static bool loaded;
        private static string code = LanguageMatch.English;
        private static JObject messages = new JObject();
        private static JObject english = new JObject();
        private static Dictionary<string, string> englishOfShown;

        // a text as the windows show it and as the setup log writes it, so the log a user sends stays english whatever language the installer ran in
        public readonly struct Text
        {
            public readonly string Shown;
            public readonly string English;
            public Text(string shown, string english) { Shown = shown; English = english; }
        }

        // a reason an exception carries in the language of the installer, with its english kept beside it so the setup log stays english
        internal static T WithEnglish<T>(this T ex, Text text) where T : Exception
        {
            ex.Data["english"] = text.English;
            return ex;
        }

        // an exception that says message id in the language of the installer and keeps its english for the log; make builds it from the shown text, and the id comes first so the message checker finds the call
        internal static T Error<T>(string id, Func<string, T> make, params object[] pairs) where T : Exception
        {
            var text = Pair(id, pairs);
            return make(text.Shown).WithEnglish(text);
        }

        internal static string EnglishOf(Exception ex) => ex.Data["english"] as string ?? ToEnglish(ex.Message);

        internal static string Message(string id, params object[] pairs)
        {
            Ensure();
            return MessageCatalog.Text(messages[id], id, code, pairs);
        }

        internal static Text Pair(string id, params object[] pairs)
        {
            Ensure();
            return new Text(MessageCatalog.Text(messages[id], id, code, pairs), MessageCatalog.Text(english[id], id, LanguageMatch.English, pairs));
        }

        // the english of a whole message the windows showed, such as an error a step threw; text that is no single message comes back as it is
        internal static string ToEnglish(string shown)
        {
            Ensure();
            if (string.IsNullOrEmpty(shown) || code == LanguageMatch.English) return shown;
            lock (Gate)
            {
                if (englishOfShown == null)
                {
                    englishOfShown = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var entry in messages.Properties())
                        if (entry.Value.Type == JTokenType.String && english[entry.Name]?.Type == JTokenType.String && !englishOfShown.ContainsKey(entry.Value.Value<string>()))
                            englishOfShown[entry.Value.Value<string>()] = english[entry.Name].Value<string>();
                }
                return englishOfShown.TryGetValue(shown, out string text) ? text : shown;
            }
        }

        // puts the messages and the page runtime right after <head> so the first bound text the parser adds renders as it arrives
        public static string Inject(string html)
        {
            try
            {
                Ensure();
                Match head = HeadOpen.Match(html);
                if (!head.Success) return html;
                string runtime = ReadRuntime();
                if (runtime == null) return html;
                string block = "<script>window.RK_LANG=" + JsonString(code) + ";window.RK_MESSAGES=" + Newtonsoft.Json.JsonConvert.SerializeObject(messages,
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
            string chosen = ObsLanguage();
            if (chosen == null)
            {
                var codes = zip.Entries
                    .Where(entry => entry.FullName.StartsWith(messagePath, StringComparison.Ordinal) && entry.FullName.EndsWith(".json", StringComparison.Ordinal))
                    .Select(entry => entry.FullName.Substring(messagePath.Length, entry.FullName.Length - messagePath.Length - ".json".Length))
                    .Where(LanguageMatch.IsSupported).ToList();
                if (!codes.Contains(LanguageMatch.English)) codes.Add(LanguageMatch.English);
                string exe = Obs.FindObsExe();
                if (exe != null)
                {
                    string ini = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(exe), "..", "..", "data", "obs-studio", "locale.ini"));
                    if (File.Exists(ini)) codes = LanguageMatch.ReadObsLanguages(ini).Select(item => item.Key).ToList();
                }
                chosen = LanguageMatch.WindowsDefault(codes);
            }
            if (!LanguageMatch.IsSupported(chosen) || chosen == LanguageMatch.English) return;
            if (ReadMessages(zip, messagePath, chosen)) code = chosen;
        }

        private static bool ReadMessages(ZipArchive zip, string path, string language)
        {
            var entry = zip.GetEntry(path + language + ".json");
            if (entry == null || entry.Length > MaxLocaleBytes) return false;
            using Stream stream = entry.Open();
            var catalog = MessageCatalog.Read(stream);
            if (language == LanguageMatch.English) { messages = catalog; english = (JObject)catalog.DeepClone(); }
            else MessageCatalog.Overlay(messages, catalog);
            return true;
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
                string value = ObsIni.Get(text, "General", "Language")?.Trim();
                return value != null && LanguageMatch.IsCode(value) ? value : null;
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ReplayKitShared;

namespace ReplayKitHelper
{
    // the language of obs and replaykit, one setting: obs keeps it in user.ini [General] Language and with none set takes the first windows display language it ships, the same rule this class follows; replaykit translates from obs-replayKit\locale\<code>.json, a json object of english text to its translation, and keeps the english text for any string a file does not have
    internal static class Locales
    {
        public const string English = ReplayKitShared.LanguageMatch.English;
        private const string Section = "General";
        private const string Key = "Language";
        // where a picked language waits between the save and the relaunch, since obs writes the user.ini it holds in memory back over the file as it exits
        public static readonly string PendingMarker = Path.Combine(Constants.OBS_CONFIG_DIR, ".replaykit-language-pending");

        public sealed class Language
        {
            public string Code;
            public string Name;
            public string EnglishName;
        }

        private static readonly object Gate = new object();
        private static string _listStamp;
        private static List<Language> _list;
        private static List<string> _obsCodes;
        private static readonly Dictionary<string, CachedStrings> _strings = new Dictionary<string, CachedStrings>(StringComparer.Ordinal);

        private sealed class CachedStrings
        {
            public DateTime Stamp;
            public Dictionary<string, string> Table;
            public string Version;
        }

        // locale selection applies on restart, so the message assets can be cached for this helper session
        private static readonly Dictionary<string, JObject> MessagesByCode = new Dictionary<string, JObject>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> MessageVersions = new Dictionary<string, string>(StringComparer.Ordinal);

        internal static JObject Messages(string code)
        {
            if (!LanguageMatch.IsSupported(code)) code = English;
            lock (Gate)
            {
                if (MessagesByCode.TryGetValue(code, out JObject messages)) return messages;
                messages = code == English ? new JObject() : (JObject)Messages(English).DeepClone();
                try
                {
                    string path = Path.Combine(Constants.APP_LOCALE_DIR, MessageCatalog.Folder, code + ".json");
                    if (File.Exists(path) && new FileInfo(path).Length <= 4 * 1024 * 1024)
                        using (var file = File.OpenRead(path))
                        {
                            var catalog = MessageCatalog.Read(file);
                            if (code == English) messages = catalog;
                            else MessageCatalog.Overlay(messages, catalog);
                        }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
                { Log.Write("Could not load message catalog " + code + ": " + ex.Message); }
                using (var sha = SHA256.Create())
                    MessageVersions[code] = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(messages.ToString(Formatting.None))), 0, 8).Replace("-", "").ToLowerInvariant();
                return MessagesByCode[code] = messages;
            }
        }

        internal static string BundleVersion(string code, bool explicitPage)
        {
            Messages(code);
            lock (Gate)
                return MessageVersions[code] + (explicitPage ? "" : "-" + StringsVersion(code));
        }

        internal static string Message(string id, params object[] pairs)
        {
            string code = Current();
            var value = Messages(code)[id];
            return MessageCatalog.Format(value?.Type == JTokenType.String ? value.Value<string>() : id, code, pairs);
        }

        // replaykit's supported languages with the names from the installed obs locale list
        public static List<Language> Available()
        {
            string ini = Path.Combine(SettingsSceneJson.GetObsInstallRoot(), "data", "obs-studio", "locale.ini");
            string stamp;
            try { stamp = File.Exists(ini) ? File.GetLastWriteTimeUtc(ini).Ticks.ToString() : "none"; }
            catch (IOException) { stamp = "none"; }
            lock (Gate)
            {
                if (_list != null && _listStamp == stamp) return _list;
                var list = new List<Language>();
                var obsCodes = new List<string>();
                if (stamp != "none")
                {
                    try
                    {
                        foreach (var item in ReplayKitShared.LanguageMatch.ReadObsLanguages(ini))
                        {
                            obsCodes.Add(item.Key);
                            if (ReplayKitShared.LanguageMatch.IsSupported(item.Key))
                                list.Add(new Language { Code = item.Key, Name = item.Value, EnglishName = EnglishNameOf(item.Key) });
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Log.Write("Locales.Available: " + ex.Message); list.Clear(); obsCodes.Clear(); }
                }
                if (!list.Any(l => l.Code == English)) list.Insert(0, new Language { Code = English, Name = "English", EnglishName = "English" });
                if (!obsCodes.Contains(English)) obsCodes.Insert(0, English);
                _obsCodes = obsCodes;
                _list = list;
                _listStamp = stamp;
                return list;
            }
        }

        public static bool IsAvailable(string code) => code != null && Available().Any(l => l.Code == code);

        // follow the obs language when replaykit supports it, otherwise use english
        public static string Current()
        {
            string saved = null;
            try { saved = ObsUserIni.Read(Section, Key); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Log.Write("Locales.Current: " + ex.Message); }
            if (string.IsNullOrEmpty(saved)) saved = WindowsDefault();
            return IsAvailable(saved) ? saved : English;
        }

        // the windows default, matched against the languages obs ships by the same rule obs uses (shared/LanguageMatch.cs)
        private static string WindowsDefault()
        {
            Available();
            return ReplayKitShared.LanguageMatch.WindowsDefault(_obsCodes);
        }

        // the table of a language: english text to its translation; empty for english and for a language without a file, so every string falls back to the english text
        public static Dictionary<string, string> Strings(string code) => Load(code).Table;

        // changes whenever the file of the language changes, so a page can keep the served strings cached until then
        public static string StringsVersion(string code) => Load(code).Version;

        // a text the helper makes itself, in the current language
        public static string T(string english, params object[] vars)
        {
            string text = english;
            if (Strings(Current()).TryGetValue(english, out string translated) && !string.IsNullOrEmpty(translated)) text = translated;
            for (int i = 0; i + 1 < vars.Length; i += 2) text = text.Replace("{" + vars[i] + "}", Convert.ToString(vars[i + 1], System.Globalization.CultureInfo.CurrentCulture));
            return text;
        }

        public static JArray ToJson() => new JArray(Available().Select(l => new JObject { ["code"] = l.Code, ["name"] = l.Name, ["englishName"] = l.EnglishName }));

        // writes the picked language into user.ini now and stages it for the relaunch, which puts it back after the exit-save of obs; obs reads it only as it starts, so a change needs a restart. picking the windows language clears the key instead, so obs and replaykit keep following windows the way a fresh obs does
        public static void ApplyToObs(string code)
        {
            if (!IsAvailable(code)) throw new InvalidOperationException("Unknown language: " + code);
            string value = code == WindowsDefault() ? "" : code;
            ObsUserIni.Set(Section, Key, value);
            File.WriteAllText(PendingMarker, value, new UTF8Encoding(false));
        }

        // the staged language back into user.ini, once obs is gone; called by ObsProcess.Relaunch and before the installer starts obs
        public static void ApplyPending()
        {
            try
            {
                if (!File.Exists(PendingMarker)) return;
                string want = File.ReadAllText(PendingMarker).Trim();
                // empty means follow windows, anything else has to be a language code
                if (want.Length == 0 || ReplayKitShared.LanguageMatch.IsSupported(want))
                {
                    ObsUserIni.Set(Section, Key, want);
                    Log.Write("Locales.ApplyPending: user.ini [General] Language=" + (want.Length == 0 ? "(cleared, follows windows)" : want));
                }
                else Log.Write("Locales.ApplyPending: ignored a staged language that is not a language code");
                File.Delete(PendingMarker);
            }
            catch (Exception ex) { Log.Write("Locales.ApplyPending: " + ex.Message); }
        }

        private static CachedStrings Load(string code)
        {
            var empty = new CachedStrings { Table = new Dictionary<string, string>(StringComparer.Ordinal), Version = "0" };
            if (code == English || !ReplayKitShared.LanguageMatch.IsSupported(code)) return empty;
            string file = Path.Combine(Constants.APP_LOCALE_DIR, code + ".json");
            DateTime stamp;
            try { stamp = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue; }
            catch (IOException) { stamp = DateTime.MinValue; }
            lock (Gate)
            {
                if (_strings.TryGetValue(code, out var cached) && cached.Stamp == stamp) return cached;
                var loaded = new CachedStrings { Stamp = stamp, Table = new Dictionary<string, string>(StringComparer.Ordinal), Version = "0" };
                if (stamp != DateTime.MinValue)
                {
                    try
                    {
                        byte[] bytes = File.ReadAllBytes(file);
                        var obj = JObject.Parse(Encoding.UTF8.GetString(bytes));
                        foreach (var prop in obj.Properties())
                        {
                            if (prop.Value.Type != JTokenType.String) continue;
                            string value = prop.Value.Value<string>();
                            // a translation that lost or renamed a placeholder would drop a value from the sentence, and a | it added would split a file dialog filter, so the english text stays instead
                            if (value.Length > 0 && SamePlaceholders(prop.Name, value) && (value.IndexOf('|') < 0 || prop.Name.IndexOf('|') >= 0)) loaded.Table[prop.Name] = value;
                        }
                        using (var sha = SHA256.Create()) loaded.Version = BitConverter.ToString(sha.ComputeHash(bytes), 0, 8).Replace("-", "").ToLowerInvariant();
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
                    {
                        // a broken file leaves the language in english rather than half translated
                        Log.Write("Locales.Load " + code + ": " + ex.Message);
                        loaded.Table.Clear();
                        loaded.Version = "0";
                    }
                }
                _strings[code] = loaded;
                return loaded;
            }
        }

        private static readonly System.Text.RegularExpressions.Regex Placeholder = new System.Text.RegularExpressions.Regex(@"\{[A-Za-z0-9_]+\}", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        private static bool SamePlaceholders(string english, string translated)
        {
            var a = Placeholder.Matches(english).Cast<System.Text.RegularExpressions.Match>().Select(m => m.Value).OrderBy(v => v, StringComparer.Ordinal);
            var b = Placeholder.Matches(translated).Cast<System.Text.RegularExpressions.Match>().Select(m => m.Value).OrderBy(v => v, StringComparer.Ordinal);
            return a.SequenceEqual(b);
        }

        private static string EnglishNameOf(string code)
        {
            try
            {
                var culture = System.Globalization.CultureInfo.GetCultureInfo(code);
                string name = culture.EnglishName;
                return string.IsNullOrEmpty(name) || name.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase) ? "" : name;
            }
            catch (System.Globalization.CultureNotFoundException) { return ""; }
        }
    }
}

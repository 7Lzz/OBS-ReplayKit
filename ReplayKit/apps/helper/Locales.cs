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
    // the language of obs and replaykit, one setting: obs keeps it in user.ini [General] Language and with none set takes the first windows display language it ships, the same rule this class follows; replaykit takes its text from the message catalogs in obs-replayKit\locale\messages\<code>.json, and a message a catalog lacks stays english
    internal static class Locales
    {
        public const string English = ReplayKitShared.LanguageMatch.English;
        private const string Section = "General";
        private const string Key = "Language";

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

        internal static string BundleVersion(string code)
        {
            Messages(code);
            lock (Gate) return MessageVersions[code];
        }

        internal static string Message(string id, params object[] pairs)
        {
            string code = Current();
            return MessageCatalog.Text(Messages(code)[id], id, code, pairs);
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

        // the language obs runs in when replaykit supports it, otherwise english; a pick only takes over once obs restarts, so the pages keep the language of the native chrome until then
        public static string Current()
        {
            string running = PipeClient.ObsLocale;
            if (running == null) return Picked();
            return IsAvailable(running) ? running : English;
        }

        // the language obs starts in next: the user.ini key, or the windows language when there is none
        public static string Picked()
        {
            string saved = null;
            try { saved = ObsUserIni.Read(Section, Key); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Log.Write("Locales.Picked: " + ex.Message); }
            if (string.IsNullOrEmpty(saved)) saved = WindowsDefault();
            return IsAvailable(saved) ? saved : English;
        }

        // the windows default, matched against the languages obs ships by the same rule obs uses (shared/LanguageMatch.cs)
        private static string WindowsDefault()
        {
            Available();
            return ReplayKitShared.LanguageMatch.WindowsDefault(_obsCodes);
        }

        public static JArray ToJson() => new JArray(Available().Select(l => new JObject { ["code"] = l.Code, ["name"] = l.Name, ["englishName"] = l.EnglishName }));

        // the plugin puts the pick into the user config obs holds and saves it, the way the language picker of obs does, so every later save and exit of obs keeps it; obs reads it only as it starts, so a change needs a restart. picking the windows language clears the key instead, so obs and replaykit keep following windows the way a fresh obs does. false when the plugin did not confirm
        public static bool ApplyToObs(string code)
        {
            if (!IsAvailable(code)) throw new InvalidOperationException("Unknown language: " + code);
            string value = code == WindowsDefault() ? "-" : code;
            bool applied = PipeClient.SetObsLanguageAndWait(value, 3000);
            Log.Write("Locales.ApplyToObs: " + (value == "-" ? "follow windows" : value) + (applied ? "" : ", not confirmed by the plugin"));
            return applied;
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

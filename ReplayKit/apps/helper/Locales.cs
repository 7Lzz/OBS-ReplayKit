using System;
using System.Collections.Generic;
using System.Globalization;
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
        internal const string FollowWindows = "-";
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
        private static readonly Dictionary<string, string> InlineMessages = new Dictionary<string, string>(StringComparer.Ordinal);
        private static bool languageUnconfirmed;

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

        internal static string InlineCatalog(string code)
        {
            lock (Gate)
            {
                if (!InlineMessages.TryGetValue(code, out string script))
                {
                    var escape = new JsonSerializerSettings { StringEscapeHandling = StringEscapeHandling.EscapeHtml };
                    script = JsonConvert.SerializeObject(Messages(code), escape);
                    InlineMessages[code] = script;
                }
                return script;
            }
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
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { stamp = "none"; }
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
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Log.Write("Locales.Available: " + ex.Message); list.Clear(); obsCodes.Clear(); stamp = null; }
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
            if (running == null) running = Effective(Picked());
            return IsAvailable(running) ? running : English;
        }

        // the number format of the language replaykit shows, so a size reads 1,5 GB where the language writes it that way
        public static CultureInfo Culture()
        {
            try { return CultureInfo.GetCultureInfo(Current()); }
            catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
        }

        // the language obs starts in next: the user.ini key, or the windows language when there is none
        public static string Picked()
        {
            string saved = null;
            try { saved = ObsUserIni.Read(Section, Key); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Log.Write("Locales.Picked: " + ex.Message); }
            return LanguageMatch.IsCode(saved) ? saved : FollowWindows;
        }

        // the windows default, matched against the languages obs ships by the same rule obs uses (shared/LanguageMatch.cs)
        private static string WindowsDefault()
        {
            Available();
            return ReplayKitShared.LanguageMatch.WindowsDefault(_obsCodes);
        }

        private static string Effective(string choice) => choice == FollowWindows ? WindowsDefault() : choice;

        internal static bool CanSelect(string choice) => choice == FollowWindows || IsAvailable(choice) || (LanguageMatch.IsCode(choice) && choice == Picked());

        public static JArray ToJson()
        {
            var options = new JArray(Available().Select(l => new JObject { ["code"] = l.Code, ["name"] = l.Name, ["englishName"] = l.EnglishName, ["preview"] = l.Code }));
            string windows = WindowsDefault();
            options.Insert(0, new JObject { ["code"] = FollowWindows, ["name"] = Locales.Message("settings.followWindowsLanguage"), ["preview"] = IsAvailable(windows) ? windows : English });
            string saved = Picked();
            if (saved != FollowWindows && !IsAvailable(saved))
                options.Add(new JObject { ["code"] = saved, ["name"] = EnglishNameOf(saved) is string name && name.Length > 0 ? name : saved, ["preview"] = English });
            return options;
        }

        internal static bool ApplyToObs(string choice, out bool restart)
        {
            restart = false;
            if (!CanSelect(choice)) return false;
            string saved = ObsUserIni.Read(Section, Key);
            string desired = choice == FollowWindows ? null : choice;
            if (languageUnconfirmed || !string.Equals(saved ?? "", desired ?? "", StringComparison.Ordinal))
            {
                languageUnconfirmed = !PipeClient.ApplyObsConfig("LANGUAGE", choice, 3000);
                if (languageUnconfirmed) return false;
            }
            string running = PipeClient.ObsLocale;
            restart = running != null && !string.Equals(Effective(choice), running, StringComparison.OrdinalIgnoreCase);
            return true;
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

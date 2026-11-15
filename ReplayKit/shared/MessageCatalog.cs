using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitShared
{
    internal static class MessageCatalog
    {
        internal const string Folder = "messages";
        private static readonly Regex Placeholder = new Regex(@"\{([A-Za-z0-9_]+)\}", RegexOptions.CultureInvariant);

        internal static JObject Read(Stream stream)
        {
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            using (var json = new JsonTextReader(reader) { DateParseHandling = DateParseHandling.None, MaxDepth = 8 })
                return JObject.Load(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        }

        // a resources/locale table of english text to its translation; a translation that lost or renamed a placeholder would drop a value from the sentence, and a | it added would split a file dialog filter, so those keep the english text
        internal static Dictionary<string, string> ReadTable(Stream stream)
        {
            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in Read(stream).Properties())
            {
                if (prop.Value.Type != JTokenType.String) continue;
                string value = prop.Value.Value<string>();
                if (value.Length > 0 && SamePlaceholders(prop.Name, value) && (value.IndexOf('|') < 0 || prop.Name.IndexOf('|') >= 0)) table[prop.Name] = value;
            }
            return table;
        }

        private static bool SamePlaceholders(string english, string translated)
        {
            var a = Placeholder.Matches(english).Cast<Match>().Select(m => m.Value).OrderBy(v => v, StringComparer.Ordinal);
            var b = Placeholder.Matches(translated).Cast<Match>().Select(m => m.Value).OrderBy(v => v, StringComparer.Ordinal);
            return a.SequenceEqual(b);
        }

        internal static void Overlay(JObject english, JObject translations)
        {
            foreach (var entry in english.Properties())
            {
                var translated = translations[entry.Name];
                if (entry.Value.Type == JTokenType.String && translated?.Type == JTokenType.String &&
                    !string.IsNullOrWhiteSpace(translated.Value<string>()))
                    entry.Value = translated.DeepClone();
                else if (entry.Value is JObject && translated is JObject plural && plural["other"]?.Type == JTokenType.String)
                    entry.Value = plural.DeepClone();
            }
        }

        // a plural message takes the form its whole-number count needs in the language, the way Intl.PluralRules picks it on the pages, and the count is written the way the language writes numbers
        internal static string Text(JToken message, string id, string locale, object[] pairs)
        {
            if (!(message is JObject forms)) return Format(message?.Type == JTokenType.String ? message.Value<string>() : id, locale, pairs);
            var values = (object[])pairs.Clone();
            string form = null;
            for (int i = 0; i + 1 < values.Length; i += 2)
            {
                if (Convert.ToString(values[i], CultureInfo.InvariantCulture) != "count" || !(values[i + 1] is int || values[i + 1] is long)) continue;
                long count = Convert.ToInt64(values[i + 1], CultureInfo.InvariantCulture);
                form = forms[PluralForm(locale, count)]?.Value<string>();
                values[i + 1] = count.ToString("N0", CultureInfo.GetCultureInfo(locale));
            }
            return Format(form ?? forms["other"]?.Value<string>() ?? id, locale, values);
        }

        // the cldr cardinal rules of the supported languages for whole numbers, the categories tools/i18n/messages.py asks each translation for
        private static string PluralForm(string locale, long n)
        {
            n = Math.Abs(n);
            switch (locale.Length >= 2 ? locale.Substring(0, 2) : "")
            {
                case "ja":
                case "zh":
                    return "other";
                case "ru":
                    if (n % 10 == 1 && n % 100 != 11) return "one";
                    return n % 10 >= 2 && n % 10 <= 4 && (n % 100 < 12 || n % 100 > 14) ? "few" : "many";
                case "fr":
                case "pt":
                    return n <= 1 ? "one" : n % 1000000 == 0 ? "many" : "other";
                case "es":
                    return n == 1 ? "one" : n != 0 && n % 1000000 == 0 ? "many" : "other";
                default:
                    return n == 1 ? "one" : "other";
            }
        }

        internal static string Format(string text, string locale, object[] pairs)
        {
            if (pairs.Length == 0) return text;
            var values = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int i = 0; i + 1 < pairs.Length; i += 2)
                values[Convert.ToString(pairs[i], CultureInfo.InvariantCulture)] = pairs[i + 1];
            CultureInfo culture = CultureInfo.GetCultureInfo(locale);
            return Placeholder.Replace(text, match =>
                values.TryGetValue(match.Groups[1].Value, out object value) && value != null
                    ? Convert.ToString(value, culture)
                    : match.Value);
        }
    }
}

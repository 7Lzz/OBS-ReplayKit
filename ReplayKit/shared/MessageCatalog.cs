using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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

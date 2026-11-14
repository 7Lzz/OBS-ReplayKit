using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ReplayKitShared
{
    // picks the language obs starts in when user.ini names none, the way obs picks it: the windows display languages in their order of preference, each matched to the languages on offer (the whole code first, else the first code with the same two letters), the first match winning; the helper and the installer both read it from here
    public static class LanguageMatch
    {
        public const string English = "en-US";
        public const string SupportedCodes = "en-US,de-DE,es-ES,fr-FR,ja-JP,pt-BR,ru-RU,zh-CN";
        private static readonly HashSet<string> Supported = new HashSet<string>(SupportedCodes.Split(','), StringComparer.Ordinal);
        // what a language code of obs looks like; a code names a translation file, so nothing else is ever read as one
        public static readonly Regex CodePattern = new Regex("^[a-z]{2,3}-[A-Za-z]{2,4}$", RegexOptions.CultureInvariant);

        public static bool IsCode(string code) => code != null && CodePattern.IsMatch(code);
        public static bool IsSupported(string code) => Supported.Contains(code);

        public static IEnumerable<KeyValuePair<string, string>> ReadObsLanguages(string path)
        {
            string code = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string raw in File.ReadLines(path))
            {
                string line = raw.Trim();
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    code = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }
                if (line.StartsWith("Name=", StringComparison.Ordinal) && IsCode(code) && seen.Add(code))
                    yield return new KeyValuePair<string, string>(code, line.Substring(5).Trim());
            }
        }

        public static string WindowsDefault(IList<string> available)
        {
            foreach (string windows in PreferredUiLanguages())
            {
                string languageMatch = null;
                foreach (string code in available)
                {
                    if (windows.Length >= code.Length && string.Equals(windows.Substring(0, code.Length), code, StringComparison.OrdinalIgnoreCase)) return code;
                    if (languageMatch == null && windows.Length >= 2 && code.Length >= 2 && string.Equals(windows.Substring(0, 2), code.Substring(0, 2), StringComparison.OrdinalIgnoreCase)) languageMatch = code;
                }
                if (languageMatch != null) return languageMatch;
            }
            return English;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetUserPreferredUILanguages(uint flags, out uint count, char[] buffer, ref uint bufferLength);
        private const uint MUI_LANGUAGE_NAME = 0x8;

        public static string[] PreferredUiLanguages()
        {
            uint length = 0;
            if (!GetUserPreferredUILanguages(MUI_LANGUAGE_NAME, out uint _, null, ref length) || length == 0) return new[] { System.Globalization.CultureInfo.CurrentUICulture.Name };
            var buffer = new char[length];
            if (!GetUserPreferredUILanguages(MUI_LANGUAGE_NAME, out uint _, buffer, ref length)) return new[] { System.Globalization.CultureInfo.CurrentUICulture.Name };
            return new string(buffer).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}

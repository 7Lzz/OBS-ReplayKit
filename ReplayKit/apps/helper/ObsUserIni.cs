using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ReplayKitHelper
{
    // reads and writes single keys of the user.ini of obs (%APPDATA%\obs-studio\user.ini), keeping every other line as obs wrote it
    internal static class ObsUserIni
    {
        public static string Path => System.IO.Path.Combine(Constants.OBS_CONFIG_DIR, "user.ini");

        // the value of section/key, or null when the file, the section or the key is missing
        public static string Read(string section, string key)
        {
            string ini = Path;
            if (!File.Exists(ini)) return null;
            string header = "[" + section + "]";
            string prefix = key + "=";
            bool inSection = false;
            foreach (var raw in File.ReadAllLines(ini))
            {
                string s = raw.Trim();
                if (s.StartsWith("[") && s.EndsWith("]")) { inSection = s.Equals(header, StringComparison.OrdinalIgnoreCase); continue; }
                if (inSection && s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return s.Substring(prefix.Length).Trim();
            }
            return null;
        }

        // sets section/key, or removes the key when value is empty; a missing section is added at the end
        public static void Set(string section, string key, string value)
        {
            string ini = Path;
            string header = "[" + section + "]";
            string prefix = key + "=";
            var lines = File.Exists(ini) ? new List<string>(File.ReadAllLines(ini)) : new List<string>();
            int sectionIdx = -1, keyIdx = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                string s = lines[i].Trim();
                if (s.StartsWith("[") && s.EndsWith("]"))
                {
                    if (sectionIdx >= 0) break;
                    if (s.Equals(header, StringComparison.OrdinalIgnoreCase)) sectionIdx = i;
                    continue;
                }
                if (sectionIdx >= 0 && keyIdx < 0 && s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) keyIdx = i;
            }

            if (string.IsNullOrEmpty(value))
            {
                if (keyIdx >= 0) lines.RemoveAt(keyIdx);
            }
            else if (keyIdx >= 0)
            {
                lines[keyIdx] = prefix + value;
            }
            else if (sectionIdx >= 0)
            {
                lines.Insert(sectionIdx + 1, prefix + value);
            }
            else
            {
                if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0) lines.Add("");
                lines.Add(header);
                lines.Add(prefix + value);
            }
            File.WriteAllLines(ini, lines, new UTF8Encoding(false));
        }
    }
}

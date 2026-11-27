using System;
using System.Collections.Generic;
using System.Linq;

namespace ReplayKitShared
{
    // reads and edits the ini files of obs (user.ini, global.ini, basic.ini) line by line and keeps every other line as obs wrote it; it reads them the way libobs does: a leading utf-8 bom is skipped, and of a section or a key that is there twice the last one counts, so a doubled section is first merged into its first place with the later values winning, which also repairs a file an older setup wrote a second [General] into. a value is never parsed, since a hotkey value is json with [ in it
    public static class ObsIni
    {
        // [section] key value, or null
        public static string Get(string text, string section, string key)
        {
            var lines = Lines(text);
            int start = FindSection(lines, section);
            if (start < 0) return null;
            int at = FindKey(lines, start, key);
            return at < 0 ? null : Body(lines[at]).Substring(key.Length + 1);
        }

        // sets [section] key=value, adding the key at the end of its section or the section at the end of the file; the line ending of the file is kept
        public static string Set(string text, string section, string key, string value)
        {
            var lines = Lines(text);
            int start = FindSection(lines, section);
            if (start < 0)
            {
                string joined = string.Join("\n", lines);
                string sep = joined.Length == 0 || joined.EndsWith("\n", StringComparison.Ordinal) ? "" : "\n";
                return $"{joined}{sep}\n[{section}]\n{key}={value}\n";
            }
            string ending = lines[start].EndsWith("\r", StringComparison.Ordinal) ? "\r" : "";
            int at = FindKey(lines, start, key);
            if (at >= 0)
            {
                lines[at] = key + "=" + value + (lines[at].EndsWith("\r", StringComparison.Ordinal) ? "\r" : "");
                return string.Join("\n", lines);
            }
            // after the last line of the section, before the blank lines that lead to the next header
            int end = SectionEnd(lines, start);
            while (end > start + 1 && Body(lines[end - 1]).Trim().Length == 0) end--;
            lines.Insert(end, key + "=" + value + ending);
            return string.Join("\n", lines);
        }

        // takes [section] key out; text without it comes back merged and otherwise as it was
        public static string Remove(string text, string section, string key)
        {
            var lines = Lines(text);
            int start = FindSection(lines, section);
            int at = start < 0 ? -1 : FindKey(lines, start, key);
            if (at >= 0) lines.RemoveAt(at);
            return string.Join("\n", lines);
        }

        // the lines of the text with the bom gone and every doubled section merged
        private static List<string> Lines(string text)
        {
            text = text ?? "";
            if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);
            // what comes before the first header, then each section with its header line first
            var blocks = new List<List<string>> { new List<string>() };
            foreach (string line in text.Split('\n'))
            {
                if (SectionName(line) != null) blocks.Add(new List<string>());
                blocks[blocks.Count - 1].Add(line);
            }
            var merged = new List<List<string>> { blocks[0] };
            var byName = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var block in blocks.Skip(1))
            {
                string name = SectionName(block[0]);
                if (!byName.TryGetValue(name, out var target))
                {
                    byName[name] = block;
                    merged.Add(block);
                    continue;
                }
                foreach (string line in block.Skip(1))
                {
                    string key = KeyOf(line);
                    if (key == null) continue;
                    int existing = FindKey(target, 0, key);
                    if (existing >= 0)
                    {
                        target[existing] = line;
                        continue;
                    }
                    int insert = target.Count;
                    while (insert > 1 && Body(target[insert - 1]).Trim().Length == 0) insert--;
                    target.Insert(insert, line);
                }
            }
            return merged.SelectMany(block => block).ToList();
        }

        private static int FindSection(List<string> lines, string section)
        {
            for (int i = 0; i < lines.Count; i++)
                if (SectionName(lines[i]) == section) return i;
            return -1;
        }

        // the last line of the key in its section, since that is the one obs reads
        private static int FindKey(List<string> lines, int start, string key)
        {
            int found = -1;
            int end = SectionEnd(lines, start);
            for (int i = start + 1; i < end; i++)
                if (KeyOf(lines[i]) == key) found = i;
            return found;
        }

        private static int SectionEnd(List<string> lines, int start)
        {
            int end = start + 1;
            while (end < lines.Count && SectionName(lines[end]) == null) end++;
            return end;
        }

        private static string SectionName(string line)
        {
            string trimmed = Body(line).Trim();
            return trimmed.Length > 1 && trimmed[0] == '[' && trimmed[trimmed.Length - 1] == ']' ? trimmed.Substring(1, trimmed.Length - 2) : null;
        }

        private static string KeyOf(string line)
        {
            string body = Body(line);
            int equals = body.IndexOf('=');
            if (equals <= 0 || body.TrimStart().StartsWith("#", StringComparison.Ordinal)) return null;
            return body.Substring(0, equals);
        }

        private static string Body(string line) => line.EndsWith("\r", StringComparison.Ordinal) ? line.Substring(0, line.Length - 1) : line;
    }
}

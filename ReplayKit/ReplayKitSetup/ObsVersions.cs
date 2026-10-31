using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitSetup
{
    // the obs versions replaykit supports, read from assets/obs-versions.json bundled in this exe: per line of obs releases (from its first version up to where the next line starts) the build it was tested on and where it loads plugins from. every bundled plugin is one build for all lines, and verify_obs_versions.ps1 checks that at build time, so a new obs release is supported by adding its line there, never by code here
    public static class ObsVersions
    {
        public const string ManifestName = "obs-versions.json";

        public sealed class Download
        {
            public string Url;
            public string Sha256;
        }

        public sealed class Line
        {
            public Version From;
            public Version Tested;
            public Download Installer;
            public Download Portable;
            // legacy obs loads a plugin from plugins\<name>\bin\64bit, obs 33 first from plugins\<name> itself and marks the other one legacy
            public bool ModernPlugins;
        }

        private static readonly object Gate = new object();
        private static List<Line> lines;
        private static Line install;

        // every line, oldest first
        public static IReadOnlyList<Line> Lines { get { Load(); return lines; } }

        // the newest line, whose tested version a new install gets and an older obs is updated to
        public static Line Install { get { Load(); return install; } }

        // the line the installed obs belongs to, null when there is no obs; an obs older than every line counts as the oldest, since the install flow brings it up to Install first
        public static Line Current
        {
            get
            {
                var version = InstalledVersion();
                return version == null ? null : For(version);
            }
        }

        public static Line For(Version version)
        {
            Load();
            Line found = lines[0];
            foreach (var line in lines)
                if (version >= line.From) found = line;
            return found;
        }

        // every plugin and the process host are laid out and built for this one version, so nothing of replaykit is installed into any other
        public static bool IsInstalledSupported() => InstalledVersion() == Install.Tested;

        // the version of the obs64.exe of the obs install, as major.minor.patch
        public static Version InstalledVersion()
        {
            string exe = Path.Combine(Config.PROGRAMFILES_OBS_DIR, "bin", "64bit", "obs64.exe");
            if (!File.Exists(exe)) return null;
            try { return Parse(FileVersionInfo.GetVersionInfo(exe).FileVersion, allowRevision: true); }
            catch (Exception ex) when (ex is IOException || ex is FormatException) { return null; }
        }

        private static Version Parse(string text, bool allowRevision = false)
        {
            var match = Regex.Match(text ?? "", allowRevision ? @"^(\d{1,4})\.(\d{1,4})\.(\d{1,4})(?:\.\d{1,6})?$" : @"^(\d{1,4})\.(\d{1,4})\.(\d{1,4})$");
            if (!match.Success) throw new FormatException("Not an OBS version: " + text);
            return new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value));
        }

        private static void Load()
        {
            lock (Gate)
            {
                if (lines != null) return;
                string path = Path.Combine(Config.ASSETS_DIR, ManifestName);
                JObject root;
                try { root = JObject.Parse(File.ReadAllText(path)); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
                {
                    throw new InvalidDataException($"The bundled {ManifestName} could not be read: {ex.Message}");
                }

                try { (lines, install) = Read(root); }
                catch (FormatException ex) { throw Bad(ex.Message.TrimEnd('.')); }
            }
        }

        private static (List<Line>, Line) Read(JObject root)
        {
            var parsed = new List<Line>();
            foreach (var token in root["lines"] as JArray ?? throw Bad("it lists no lines"))
            {
                if (!(token is JObject entry)) throw Bad("a line is not an object");
                var line = new Line
                {
                    From = Parse(Text(entry, "from")),
                    Tested = Parse(Text(entry, "tested")),
                    Installer = ReadDownload(entry, "installer"),
                    Portable = ReadDownload(entry, "portable"),
                    ModernPlugins = Layout(entry["pluginLayout"]?.Value<string>()),
                };
                if (line.Tested < line.From) throw Bad("line " + line.From + " was tested on an older version");
                if (parsed.Count > 0 && line.From <= parsed[parsed.Count - 1].From) throw Bad("lines are not in order");
                if (parsed.Count > 0 && line.From <= parsed[parsed.Count - 1].Tested) throw Bad("line " + line.From + " starts before the previous line was tested");
                parsed.Add(line);
            }
            if (parsed.Count == 0) throw Bad("it lists no lines");
            return (parsed, parsed[parsed.Count - 1]);
        }

        private static InvalidDataException Bad(string why) => new InvalidDataException($"The bundled {ManifestName} is invalid: {why}.");

        private static string Text(JObject entry, string name)
        {
            string value = entry[name]?.Type == JTokenType.String ? entry[name].Value<string>() : null;
            return string.IsNullOrWhiteSpace(value) ? throw Bad(name + " is missing") : value;
        }

        private static string Sha256Of(JObject entry)
        {
            string value = Text(entry, "sha256");
            return Regex.IsMatch(value, "^[0-9a-f]{64}$") ? value : throw Bad("a sha256 is not 64 lowercase hex digits");
        }

        private static bool Layout(string value) => value == "modern" ? true : value == "legacy" ? false : throw Bad("a layout is neither legacy nor modern");

        // only official obs release downloads are allowed, so the manifest can never point the elevated installer at another host
        private static Download ReadDownload(JObject line, string name)
        {
            if (!(line[name] is JObject entry)) throw Bad(name + " is missing");
            string url = Text(entry, "url");
            if (!Regex.IsMatch(url, @"^https://github\.com/obsproject/obs-studio/releases/download/[0-9A-Za-z.\-]+/[0-9A-Za-z.\-]+\.(exe|zip)$"))
                throw Bad(name + " is not an obs release download");
            return new Download { Url = url, Sha256 = Sha256Of(entry) };
        }
    }
}

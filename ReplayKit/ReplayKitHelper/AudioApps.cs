using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // the programs that play sound while obs runs, for the recording audio list of the audio tab: in the specific apps mode which of them are in the recorded desktop sound is a choice per program, kept in the settings as recordingAudioApps and applied to the capture source of the desktop audio
    internal static class AudioApps
    {
        // the property of the application audio capture source that lists the sessions playing right now, the same list its own properties window offers under "add from currently active sessions"
        private const string ActiveSessionProperty = "active_session_list";
        // an include list that is empty could mean "nothing" or "everything" to the capture source, so it is never left empty: this names a program that does not exist
        internal const string NoAppExe = "replaykit-no-app.exe";
        private const int MaxApps = 200;
        private const int MaxSeen = 120;
        private static readonly TimeSpan ForgetAfter = TimeSpan.FromDays(120);

        private static readonly object Gate = new object();
        private static Dictionary<string, SeenApp> _seen;

        private sealed class SeenApp
        {
            public string Exe;
            public string Path = "";
            public DateTime LastSeenUtc;
        }

        // a program name as the capture source knows it: a bare file name ending in .exe, printable, nothing that could pass for a path
        public static string CleanExe(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string name = raw.Trim();
            // the list of the source may prefix the process id, "[1234] brave.exe"
            if (name.StartsWith("[", StringComparison.Ordinal))
            {
                int close = name.IndexOf(']');
                if (close > 0) name = name.Substring(close + 1).Trim();
            }
            if (name.Length < 5 || name.Length > 64) return null;
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
            foreach (char c in name)
            {
                if (c < 32 || c == '\\' || c == '/' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|') return null;
            }
            return name;
        }

        // the programs the desktop capture never takes, because they have a source of their own or would record clips played back by obs itself; they are not offered in the list either
        public static bool IsBuiltinExcluded(string exe)
        {
            return ReplaykitSettings.DesktopAudioExcludeList.Any(e => string.Equals(e, exe, StringComparison.OrdinalIgnoreCase));
        }

        // the choices the settings carry: valid program names with true or false, nothing else
        public static JObject NormalizeChoices(JToken raw)
        {
            var result = new JObject();
            var source = raw as JObject;
            if (source == null) return result;
            foreach (var prop in source.Properties())
            {
                if (result.Count >= MaxApps) break;
                string exe = CleanExe(prop.Name);
                if (exe == null || prop.Value.Type != JTokenType.Boolean) continue;
                if (result.Properties().Any(p => string.Equals(p.Name, exe, StringComparison.OrdinalIgnoreCase))) continue;
                result[exe] = prop.Value.Value<bool>();
            }
            return result;
        }

        // whether a program is in the recorded sound: every program is in the all apps mode, and in the specific mode only the ones ticked
        public static bool IsEnabled(string mode, JObject choices, string exe)
        {
            if (mode != "specific") return true;
            var own = choices?.Properties().FirstOrDefault(p => string.Equals(p.Name, exe, StringComparison.OrdinalIgnoreCase));
            return own != null && own.Value.Type == JTokenType.Boolean && own.Value.Value<bool>();
        }

        // the executable list and the include or exclude switch of the desktop capture. "all": everything but the built in programs, the choices are not looked at because that mode lists no programs. "specific": only the ones switched on
        public static JArray BuildCaptureList(string mode, JObject choices, out bool exclude)
        {
            var entries = new JArray();
            if (mode == "specific")
            {
                exclude = false;
                foreach (var prop in (choices ?? new JObject()).Properties())
                {
                    if (prop.Value.Type != JTokenType.Boolean || !prop.Value.Value<bool>()) continue;
                    if (IsBuiltinExcluded(prop.Name)) continue;
                    entries.Add(new JObject { ["value"] = prop.Name });
                }
                if (entries.Count == 0) entries.Add(new JObject { ["value"] = NoAppExe });
                return entries;
            }
            exclude = true;
            foreach (string name in ReplaykitSettings.DesktopAudioExcludeList) entries.Add(new JObject { ["value"] = name });
            return entries;
        }

        private static string SeenPath() => System.IO.Path.Combine(Constants.OBS_CONFIG_DIR, "obs-replayKit", "cache", "audio_apps_seen.json");

        private static Dictionary<string, SeenApp> LoadSeenLocked()
        {
            if (_seen != null) return _seen;
            _seen = new Dictionary<string, SeenApp>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string path = SeenPath();
                if (File.Exists(path) && new FileInfo(path).Length < 262144)
                {
                    var root = JObject.Parse(File.ReadAllText(path));
                    foreach (var item in root["apps"] as JArray ?? new JArray())
                    {
                        string exe = CleanExe(item["exe"]?.Value<string>());
                        if (exe == null) continue;
                        long ticks = item["seen"]?.Value<long>() ?? 0;
                        _seen[exe] = new SeenApp { Exe = exe, Path = item["path"]?.Value<string>() ?? "", LastSeenUtc = ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : DateTime.UtcNow };
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException) { }
            return _seen;
        }

        private static void SaveSeenLocked()
        {
            try
            {
                string path = SeenPath();
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                var apps = new JArray();
                foreach (var app in _seen.Values.OrderByDescending(a => a.LastSeenUtc).Take(MaxSeen))
                    apps.Add(new JObject { ["exe"] = app.Exe, ["path"] = app.Path ?? "", ["seen"] = app.LastSeenUtc.Ticks });
                AppConfig.WriteUtf8(path, new JObject { ["apps"] = apps }.ToString(Formatting.None));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        // the sessions playing now, straight from the capture source, which is the same list the audio capture itself can pick from. empty when obs or the source is not there
        private static List<string> ReadActiveSessions(out bool obsAnswered)
        {
            var found = new List<string>();
            obsAnswered = false;
            var reply = ObsWebSocket.InvokeRequest("GetInputPropertiesListPropertyItems", new JObject { ["inputName"] = ReplaykitSettings.DesktopAudioInputName, ["propertyName"] = ActiveSessionProperty }, 2500);
            if (!reply.Ok) return found;
            obsAnswered = true;
            foreach (var item in reply.Data?["propertyItems"] as JArray ?? new JArray())
            {
                string exe = CleanExe(item["itemValue"]?.ToString()) ?? CleanExe(item["itemName"]?.ToString());
                if (exe != null && !found.Contains(exe, StringComparer.OrdinalIgnoreCase)) found.Add(exe);
            }
            return found;
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref uint size);
        private const uint ProcessQueryLimitedInformation = 0x1000;

        // the file behind a running program, asked with the limited access that works across integrity levels, since obs and this helper can run at different ones
        private static string ImagePathOfRunning(string exe)
        {
            try
            {
                foreach (var process in Process.GetProcessesByName(System.IO.Path.GetFileNameWithoutExtension(exe)))
                {
                    using (process)
                    {
                        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)process.Id);
                        if (handle == IntPtr.Zero) continue;
                        try
                        {
                            var name = new StringBuilder(1024);
                            uint size = (uint)name.Capacity;
                            if (QueryFullProcessImageNameW(handle, 0, name, ref size) && size > 0) return name.ToString();
                        }
                        finally { CloseHandle(handle); }
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) { }
            return null;
        }

        // one call per look at the list: the sessions of the moment are added to the record of programs that were ever seen, and the list the page shows is that record plus whatever has a choice attached
        public static JObject GetAppsPayload(string mode, JObject choices)
        {
            var active = ReadActiveSessions(out bool obsAnswered);
            var apps = new List<JObject>();
            lock (Gate)
            {
                var seen = LoadSeenLocked();
                bool changed = false;
                foreach (string exe in active)
                {
                    if (IsBuiltinExcluded(exe)) continue;
                    string path = ImagePathOfRunning(exe) ?? "";
                    if (!seen.TryGetValue(exe, out var record))
                    {
                        record = new SeenApp { Exe = exe };
                        seen[exe] = record;
                        changed = true;
                    }
                    if (!string.IsNullOrEmpty(path) && record.Path != path) { record.Path = path; changed = true; }
                    // the time only moves once in a while, so listing does not rewrite the file every few seconds
                    if ((DateTime.UtcNow - record.LastSeenUtc).TotalHours > 1) { record.LastSeenUtc = DateTime.UtcNow; changed = true; }
                }
                var cutoff = DateTime.UtcNow - ForgetAfter;
                foreach (var stale in seen.Values.Where(a => a.LastSeenUtc < cutoff && !(choices?.Properties().Any(p => string.Equals(p.Name, a.Exe, StringComparison.OrdinalIgnoreCase)) ?? false)).ToList())
                {
                    seen.Remove(stale.Exe);
                    changed = true;
                }
                if (changed) SaveSeenLocked();

                var names = new List<string>(seen.Keys);
                foreach (var prop in (choices ?? new JObject()).Properties())
                    if (!names.Contains(prop.Name, StringComparer.OrdinalIgnoreCase)) names.Add(prop.Name);
                foreach (string exe in names)
                {
                    if (IsBuiltinExcluded(exe)) continue;
                    apps.Add(new JObject
                    {
                        ["exe"] = exe,
                        ["name"] = System.IO.Path.GetFileNameWithoutExtension(exe),
                        ["active"] = active.Contains(exe, StringComparer.OrdinalIgnoreCase),
                        ["enabled"] = IsEnabled(mode, choices, exe),
                    });
                }
            }
            // playing programs first, then the rest by name
            var ordered = apps.OrderByDescending(a => a["active"].Value<bool>()).ThenBy(a => a["name"].Value<string>(), StringComparer.OrdinalIgnoreCase);
            return new JObject { ["ok"] = true, ["obs"] = obsAnswered, ["apps"] = new JArray(ordered) };
        }

        // the icon of a program the list shows, as a small png. the file it is read from is only ever one that this helper saw a running program of that name at, never a path from the request
        public static byte[] GetIconPng(string rawExe)
        {
            string exe = CleanExe(rawExe);
            if (exe == null) return null;
            string path = null;
            lock (Gate)
            {
                var seen = LoadSeenLocked();
                bool known = seen.TryGetValue(exe, out var record);
                if (!known || string.IsNullOrEmpty(record.Path))
                {
                    string running = ImagePathOfRunning(exe);
                    if (running != null) path = running;
                    else if (known) path = record.Path;
                }
                else path = record.Path;
            }
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                using (var icon = Icon.ExtractAssociatedIcon(path))
                {
                    if (icon == null) return null;
                    using (var bitmap = icon.ToBitmap())
                    using (var stream = new MemoryStream())
                    {
                        bitmap.Save(stream, ImageFormat.Png);
                        return stream.ToArray();
                    }
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception) { return null; }
        }
    }
}

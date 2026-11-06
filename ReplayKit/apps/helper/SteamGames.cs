using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    internal static class SteamGames
    {
        internal sealed class Library
        {
            internal readonly Dictionary<string, GameCatalog.Game> Folders = new Dictionary<string, GameCatalog.Game>(StringComparer.OrdinalIgnoreCase);
            // the steamapps folders the read looked in, so the next refresh can tell whether anything was installed or removed
            internal readonly List<string> Scanned = new List<string>();
            internal GameCatalog.Game Find(string file)
            {
                string path = LocalPath(file);
                if (path == null || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !GameCatalog.Playable(Path.GetFileName(path))) return null;
                var matches = Folders.Where(p => path.StartsWith(p.Key, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count != 1 || !File.Exists(path)) return null;
                string current = path;
                while (current != null)
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return null;
                    current = Path.GetDirectoryName(current);
                }
                return matches[0].Value;
            }
        }

        private static readonly object Gate = new object();
        private static Library library = new Library();
        private static long refreshAt;
        private static bool pending;
        private static string status = "not-checked";
        private static string readSignature = "";

        // what a read of the library depends on: the library list, the app cache and every steamapps folder, whose write time changes when a game is installed or removed
        private static string Signature(string root, Library last)
        {
            var parts = new List<string>();
            foreach (string file in new[] { Path.Combine(root, "steamapps", "libraryfolders.vdf"), Path.Combine(root, "appcache", "appinfo.vdf") })
            {
                var info = new FileInfo(file);
                parts.Add(info.Exists ? info.Length + ":" + info.LastWriteTimeUtc.Ticks : "none");
            }
            foreach (string apps in last.Scanned) parts.Add(Directory.Exists(apps) ? Directory.GetLastWriteTimeUtc(apps).Ticks.ToString() : "none");
            return root + "|" + string.Join("|", parts);
        }

        private static string LocalPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length < 3 || path.Length > 32767 || !char.IsLetter(path[0]) || path[1] != ':' || (path[2] != '\\' && path[2] != '/') || path.IndexOf(':', 2) >= 0) return null;
            return Path.GetFullPath(path).TrimEnd('\\', '/');
        }

        private static string Root()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            {
                string root = LocalPath(key?.GetValue("SteamPath") as string);
                if (root != null) return root;
            }
            using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
            using (var key = machine.OpenSubKey(@"Software\Valve\Steam")) return LocalPath(key?.GetValue("InstallPath") as string);
        }

        internal static Library Read(string root)
        {
            root = LocalPath(root);
            if (root == null) throw new InvalidDataException("Invalid Steam installation.");
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };
            string file = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (File.Exists(file))
            {
                var entries = SteamData.Text(file)["libraryfolders"] as JObject;
                if (entries == null || entries.Count > 64) throw new InvalidDataException("Invalid Steam library list.");
                foreach (var entry in entries.Properties())
                {
                    string path = LocalPath(entry.Value.Type == JTokenType.String ? entry.Value.Value<string>() : entry.Value["path"]?.Value<string>());
                    if (path != null) folders.Add(path);
                }
            }
            var installed = new List<Tuple<uint, string, string>>();
            var result = new Library();
            foreach (string folder in folders)
            {
                string apps = Path.Combine(folder, "steamapps");
                result.Scanned.Add(apps);
                if (!Directory.Exists(apps)) continue;
                foreach (string manifest in Directory.EnumerateFiles(apps, "appmanifest_*.acf").Take(10001))
                {
                    if (installed.Count >= 10000) throw new InvalidDataException("Too many installed Steam apps.");
                    var app = SteamData.Text(manifest)["AppState"] as JObject;
                    if (app == null || !uint.TryParse(app["appid"]?.Value<string>(), out uint id) || id == 0 || !uint.TryParse(app["StateFlags"]?.Value<string>(), out uint flags) || (flags & 4) == 0) continue;
                    if (!string.Equals(Path.GetFileName(manifest), "appmanifest_" + id + ".acf", StringComparison.OrdinalIgnoreCase)) continue;
                    string name = app["name"]?.Value<string>(), directory = app["installdir"]?.Value<string>();
                    if (string.IsNullOrWhiteSpace(name) || name.Length > 160 || name.Any(char.IsControl) || string.IsNullOrWhiteSpace(directory) || directory == "." || directory == ".." || directory.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;
                    string path = Path.GetFullPath(Path.Combine(apps, "common", directory));
                    if (Directory.Exists(path)) installed.Add(Tuple.Create(id, name, path + "\\"));
                }
            }
            var info = SteamData.Apps(Path.Combine(root, "appcache", "appinfo.vdf"), new HashSet<uint>(installed.Select(p => p.Item1)));
            foreach (var app in installed)
            {
                if (!info.TryGetValue(app.Item1, out var details)) continue;
                string type = details["common"]?["type"]?.Value<string>();
                if (!string.Equals(type, "game", StringComparison.OrdinalIgnoreCase) && !string.Equals(type, "demo", StringComparison.OrdinalIgnoreCase)) continue;
                if (result.Folders.ContainsKey(app.Item3)) throw new InvalidDataException("Ambiguous Steam installation.");
                result.Folders[app.Item3] = GameCatalog.Known(app.Item2) ?? new GameCatalog.Game { Id = "steam-" + app.Item1, Name = app.Item2 };
            }
            return result;
        }

        internal static void Refresh()
        {
            lock (Gate)
            {
                long now = DateTime.UtcNow.Ticks;
                if (pending || now - refreshAt < TimeSpan.TicksPerMinute) return;
                pending = true;
                refreshAt = now;
            }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var next = new Library();
                string message = "unavailable";
                try
                {
                    string root = Root();
                    if (root == null) message = "not-installed";
                    else
                    {
                        // nothing steam keeps about installed games changed since the last read, so that read still stands
                        Library last;
                        string lastSignature, lastStatus;
                        lock (Gate) { last = library; lastSignature = readSignature; lastStatus = status; }
                        string signature = Signature(root, last);
                        if (lastStatus == "ready" && signature == lastSignature) next = last;
                        else { next = Read(root); signature = Signature(root, next); }
                        lock (Gate) readSignature = signature;
                        message = "ready";
                    }
                }
                // the readers refuse a file they do not understand with InvalidDataException; nothing may escape this pool thread, or the helper process ends
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is SecurityException || ex is Newtonsoft.Json.JsonException || ex is InvalidOperationException || ex is FormatException || ex is OverflowException || ex is EndOfStreamException)
                { Log.Write("Local Steam metadata unavailable: " + ex.GetType().Name + ": " + ex.Message); next = new Library(); }
                finally { lock (Gate) { library = next; status = message; pending = false; } }
            });
        }

        internal static GameCatalog.Game Find(string path)
        {
            Refresh();
            Library snapshot;
            lock (Gate) snapshot = library;
            return snapshot.Find(path);
        }

        internal static IEnumerable<GameCatalog.Game> Games()
        {
            Refresh();
            lock (Gate) return library.Folders.Values.ToArray();
        }

        // the first read of the steam library is done, so a game it did not find is not about to show up
        internal static bool Settled { get { lock (Gate) return status != "not-checked" && !pending; } }

        internal static JObject Status()
        {
            lock (Gate) return new JObject { ["status"] = status, ["refreshing"] = pending, ["games"] = library.Folders.Count };
        }
    }
}

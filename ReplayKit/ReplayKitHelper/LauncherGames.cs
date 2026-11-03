using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // the games other launchers installed, found the way the steam library is: each launcher keeps a record of what it installed and where, and a captured program inside one of those folders is that game. a game the bundled catalog knows by the same name keeps its catalog entry (and its artwork); any other one gets a saved game of its own name, the same one a name typed in the game picker makes
    internal static class LauncherGames
    {
        internal sealed class Entry
        {
            internal string Folder;
            internal string Name;
            internal string Source;
            // a folder from the windows list of installed programs, which holds programs of every kind; it only counts for a capture that is plainly a game (see Find)
            internal bool AnyProgram;
        }

        private static readonly object Gate = new object();
        private static List<Entry> entries = new List<Entry>();
        private static long refreshAt;
        private static bool pending;
        private static string status = "not-checked";

        // the publishers whose entries in the windows list are games (battle.net, the riot client, the ea app and the rest install their games there); their own launchers are left out by name
        private static readonly string[] GamePublishers = { "Blizzard Entertainment", "Riot Games", "Electronic Arts", "Rockstar Games", "Ubisoft", "Bethesda Softworks", "Epic Games, Inc.", "GOG.com", "CD PROJEKT RED" };
        private static readonly Regex LauncherNames = new Regex(@"^(?:Battle\.net|EA app|EA Desktop|Origin|Riot Client|Riot Vanguard|Rockstar Games Launcher|Rockstar Games Social Club|Social Club|Ubisoft Connect|Uplay|Epic Games Launcher|Epic Online Services|GOG GALAXY|GOG Galaxy|Amazon Games|itch|Steam|Xbox|Bethesda\.net Launcher|Unreal Engine)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static string LocalPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            path = path.Trim().Trim('"').Replace('/', '\\');
            if (path.Length < 4 || path.Length > 1000 || !char.IsLetter(path[0]) || path[1] != ':' || path[2] != '\\' || path.IndexOf(':', 2) >= 0) return null;
            try { return Path.GetFullPath(path).TrimEnd('\\'); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is SecurityException) { return null; }
        }

        // a folder that holds far more than one game is never a game folder: a drive, the program folders, windows, the folders of a user
        private static readonly Lazy<HashSet<string>> BroadFolders = new Lazy<HashSet<string>>(() =>
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Windows, Environment.SpecialFolder.CommonApplicationData,
                Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.Desktop })
            {
                string path = LocalPath(Environment.GetFolderPath(folder));
                if (path != null) set.Add(path);
            }
            string profile = LocalPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            if (profile != null)
            {
                set.Add(Path.GetDirectoryName(profile));
                foreach (string sub in new[] { "Downloads", "AppData", @"AppData\LocalLow", "Games" }) set.Add(Path.Combine(profile, sub));
            }
            foreach (string sub in new[] { @"Program Files\Common Files", @"Program Files (x86)\Common Files", "Games", "XboxGames", "Riot Games", @"Program Files\Epic Games", @"Program Files (x86)\Epic Games", @"Program Files (x86)\GOG Galaxy\Games", @"Program Files\EA Games", @"Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games", @"Program Files\Rockstar Games" })
                foreach (var drive in SafeDrives()) set.Add(Path.Combine(drive, sub));
            return set;
        });

        private static IEnumerable<string> SafeDrives()
        {
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { yield break; }
            foreach (var drive in drives)
            {
                bool ready;
                try { ready = drive.DriveType == DriveType.Fixed && drive.IsReady; }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { ready = false; }
                if (ready) yield return drive.RootDirectory.FullName.TrimEnd('\\');
            }
        }

        private static string CleanName(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || name.Length > 100 || name.Any(char.IsControl)) return null;
            return name;
        }

        private static void Add(List<Entry> list, string folder, string name, string source, bool anyProgram = false)
        {
            folder = LocalPath(folder);
            name = CleanName(name);
            if (folder == null || name == null || LauncherNames.IsMatch(name)) return;
            // a drive root, or a folder one step under a drive, is too broad unless it is the game's own folder name (d:\Fortnite is fine, d:\Games is not)
            if (Path.GetPathRoot(folder).TrimEnd('\\').Length == folder.Length || BroadFolders.Value.Contains(folder)) return;
            if (list.Count >= 5000 || !Directory.Exists(folder)) return;
            list.Add(new Entry { Folder = folder + "\\", Name = name, Source = source, AnyProgram = anyProgram });
        }

        private static RegistryKey Open(RegistryHive hive, RegistryView view, string path)
        {
            try { return RegistryKey.OpenBaseKey(hive, view).OpenSubKey(path); }
            catch (Exception ex) when (ex is SecurityException || ex is UnauthorizedAccessException || ex is IOException) { return null; }
        }

        private static string Value(RegistryKey key, string name)
        {
            try { return key?.GetValue(name) as string; }
            catch (Exception ex) when (ex is SecurityException || ex is UnauthorizedAccessException || ex is IOException) { return null; }
        }

        // every subkey of a registry folder, in both views of the machine part where a launcher may have written it
        private static IEnumerable<RegistryKey> SubKeys(RegistryHive hive, string path)
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using (var root = Open(hive, view, path))
                {
                    if (root == null) continue;
                    string[] names;
                    try { names = root.GetSubKeyNames(); }
                    catch (Exception ex) when (ex is SecurityException || ex is UnauthorizedAccessException || ex is IOException) { continue; }
                    foreach (string name in names.Take(5000))
                    {
                        RegistryKey sub = null;
                        try { sub = root.OpenSubKey(name); }
                        catch (Exception ex) when (ex is SecurityException || ex is UnauthorizedAccessException || ex is IOException) { }
                        if (sub == null) continue;
                        using (sub) yield return sub;
                    }
                }
            }
        }

        private static string KeyName(RegistryKey key) => key.Name.Substring(key.Name.LastIndexOf('\\') + 1);

        // epic games: one json manifest for each install, with its name, its folder and what kind of app it is (the unreal engine is not a game)
        private static void ReadEpic(List<Entry> list)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
            if (!Directory.Exists(dir)) return;
            foreach (string file in Directory.EnumerateFiles(dir, "*.item").Take(2000))
            {
                try
                {
                    if (new FileInfo(file).Length > 1024 * 1024) continue;
                    var item = JObject.Parse(File.ReadAllText(file));
                    if (item["bIsIncompleteInstall"]?.Type == JTokenType.Boolean && item["bIsIncompleteInstall"].Value<bool>()) continue;
                    var categories = (item["AppCategories"] as JArray)?.Values<string>().ToList();
                    if (categories != null && categories.Count > 0 && !categories.Any(c => string.Equals(c, "games", StringComparison.OrdinalIgnoreCase))) continue;
                    Add(list, item["InstallLocation"]?.Value<string>(), item["DisplayName"]?.Value<string>(), "epic");
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is Newtonsoft.Json.JsonException || ex is InvalidCastException || ex is FormatException) { }
            }
        }

        // gog galaxy: a registry key for each game with its name and folder
        private static void ReadGog(List<Entry> list)
        {
            foreach (var key in SubKeys(RegistryHive.LocalMachine, @"SOFTWARE\GOG.com\Games"))
                Add(list, Value(key, "path"), Value(key, "gameName") ?? Value(key, "GAMENAME"), "gog");
        }

        // ubisoft connect: the folder of each install by its id, and the name the uninstaller entry of that id carries
        private static void ReadUbisoft(List<Entry> list, Dictionary<string, string> uninstallNames)
        {
            foreach (var key in SubKeys(RegistryHive.LocalMachine, @"SOFTWARE\Ubisoft\Launcher\Installs"))
            {
                string folder = Value(key, "InstallDir");
                string id = KeyName(key);
                uninstallNames.TryGetValue("Uplay Install " + id, out string name);
                Add(list, folder, name ?? Path.GetFileName((LocalPath(folder) ?? "").TrimEnd('\\')), "ubisoft");
            }
        }

        // the ea app and origin, and rockstar: a key for each game under the publisher, with its install folder
        private static void ReadPublisherKeys(List<Entry> list)
        {
            foreach (string root in new[] { @"SOFTWARE\EA Games", @"SOFTWARE\Electronic Arts", @"SOFTWARE\Electronic Arts\EA Games" })
                foreach (var key in SubKeys(RegistryHive.LocalMachine, root))
                    Add(list, Value(key, "Install Dir") ?? Value(key, "InstallDir"), Value(key, "DisplayName") ?? KeyName(key), "ea");
            foreach (var key in SubKeys(RegistryHive.LocalMachine, @"SOFTWARE\Rockstar Games"))
                Add(list, Value(key, "InstallFolder") ?? Value(key, "Install Folder"), KeyName(key), "rockstar");
        }

        // riot games: a settings file for each product with the folder it is installed in; the folder carries the name (VALORANT, League of Legends)
        private static readonly Regex RiotPath = new Regex(@"^\s*product_install_full_path:\s*""?(?<path>[^""\r\n]+)""?\s*$", RegexOptions.Multiline | RegexOptions.Compiled);
        private static void ReadRiot(List<Entry> list)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Riot Games", "Metadata");
            if (!Directory.Exists(dir)) return;
            foreach (string file in Directory.EnumerateFiles(dir, "*.product_settings.yaml", SearchOption.AllDirectories).Take(200))
            {
                try
                {
                    if (new FileInfo(file).Length > 256 * 1024) continue;
                    var match = RiotPath.Match(File.ReadAllText(file));
                    if (!match.Success) continue;
                    string folder = LocalPath(match.Groups["path"].Value);
                    if (folder != null && !Path.GetFileName(folder).StartsWith("Riot Client", StringComparison.OrdinalIgnoreCase))
                        Add(list, folder, Path.GetFileName(folder), "riot");
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }

        // itch.io: each game is a folder of the itch apps folder, and its receipt has the title
        private static void ReadItch(List<Entry> list)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "itch", "apps");
            if (!Directory.Exists(dir)) return;
            foreach (string folder in Directory.EnumerateDirectories(dir).Take(2000))
            {
                string name = null;
                string receipt = Path.Combine(folder, ".itch", "receipt.json.gz");
                try
                {
                    if (File.Exists(receipt) && new FileInfo(receipt).Length < 1024 * 1024)
                        using (var file = File.OpenRead(receipt))
                        using (var gzip = new GZipStream(file, CompressionMode.Decompress))
                        using (var reader = new StreamReader(gzip))
                        {
                            char[] buffer = new char[512 * 1024];
                            int read = reader.ReadBlock(buffer, 0, buffer.Length);
                            name = JObject.Parse(new string(buffer, 0, read))["game"]?["title"]?.Value<string>();
                        }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is Newtonsoft.Json.JsonException) { }
                Add(list, folder, name ?? Path.GetFileName(folder), "itch");
            }
        }

        // the xbox app and the microsoft store put pc games in an XboxGames folder at the root of a drive, one folder for each game under its title
        private static void ReadXbox(List<Entry> list)
        {
            foreach (string drive in SafeDrives())
            {
                string dir = Path.Combine(drive + "\\", "XboxGames");
                if (!Directory.Exists(dir)) continue;
                try { foreach (string folder in Directory.EnumerateDirectories(dir).Take(500)) if (!Path.GetFileName(folder).StartsWith(".", StringComparison.Ordinal)) Add(list, folder, Path.GetFileName(folder), "xbox"); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }

        // the windows list of installed programs: games from the publishers above (battle.net, the riot client and the ea app install there), and every other program too, which only counts for a capture that is plainly a game
        private static void ReadInstalledPrograms(List<Entry> list, Dictionary<string, string> names)
        {
            const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
                foreach (var key in SubKeys(hive, uninstall))
                {
                    string name = Value(key, "DisplayName");
                    if (name == null) continue;
                    names[KeyName(key)] = name;
                    object component = null;
                    try { component = key.GetValue("SystemComponent"); } catch (Exception ex) when (ex is SecurityException || ex is UnauthorizedAccessException || ex is IOException) { }
                    if (component is int c && c == 1 || Value(key, "ParentKeyName") != null || string.Equals(Value(key, "ReleaseType"), "Update", StringComparison.OrdinalIgnoreCase)) continue;
                    string publisher = Value(key, "Publisher") ?? "";
                    bool gamePublisher = GamePublishers.Any(p => publisher.StartsWith(p, StringComparison.OrdinalIgnoreCase));
                    Add(list, Value(key, "InstallLocation"), name, gamePublisher ? "publisher" : "installed", anyProgram: !gamePublisher);
                }
        }

        private static List<Entry> Read()
        {
            var list = new List<Entry>();
            var uninstallNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // each reader stands on its own, so one launcher with a record it cannot read leaves the others
            foreach (Action read in new Action[] { () => ReadInstalledPrograms(list, uninstallNames), () => ReadEpic(list), () => ReadGog(list), () => ReadUbisoft(list, uninstallNames), () => ReadPublisherKeys(list), () => ReadRiot(list), () => ReadItch(list), () => ReadXbox(list) })
            {
                try { read(); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException || ex is ArgumentException || ex is InvalidOperationException) { Log.Write("Launcher games: a launcher could not be read: " + ex.GetType().Name + ": " + ex.Message); }
            }
            return list;
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
                var next = new List<Entry>();
                try { next = Read(); }
                // nothing may escape this pool thread, or the helper process ends
                catch (Exception ex) { Log.Write("Launcher games unavailable: " + ex.GetType().Name + ": " + ex.Message); }
                finally { lock (Gate) { entries = next; status = "ready"; pending = false; } }
            });
        }

        // the game a captured program belongs to: the launcher folder that holds it, the deepest one when folders nest; null when it is in none, or in two of the same depth that name different games. anyProgram lets the folders of the windows list of every program count, for a game capture that hooked the program or a program that fills its display
        internal static GameCatalog.Game Find(string file, bool anyProgram)
        {
            Refresh();
            string path = LocalPath(file);
            if (path == null || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !GameCatalog.Playable(Path.GetFileName(path))) return null;
            List<Entry> snapshot;
            lock (Gate) snapshot = entries;
            var matches = snapshot.Where(e => (anyProgram || !e.AnyProgram) && path.StartsWith(e.Folder, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0) return null;
            int deepest = matches.Max(e => e.Folder.Length);
            // the same folder is often in the record of its launcher and in the windows list under a slightly other name (a ™ more); the launcher's own record wins, then a publisher's entry, then the list of every program
            var level = matches.Where(e => e.Folder.Length == deepest).GroupBy(e => e.Source == "installed" ? 2 : e.Source == "publisher" ? 1 : 0).OrderBy(g => g.Key).First();
            var names = level.Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (names.Count != 1) return null;
            return GameCatalog.Known(names[0]) ?? GameCatalog.CustomGame(names[0]);
        }

        // the first read of the launchers is done, so a game they did not name is not about to show up
        internal static bool Settled { get { lock (Gate) return status != "not-checked" && !pending; } }

        internal static JObject Status()
        {
            lock (Gate) return new JObject { ["status"] = status, ["refreshing"] = pending, ["folders"] = entries.Count,
                ["sources"] = new JObject(entries.GroupBy(e => e.Source).Select(g => new JProperty(g.Key, g.Count()))) };
        }
    }
}

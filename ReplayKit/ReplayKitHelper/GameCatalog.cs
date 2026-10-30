using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    internal static class GameCatalog
    {
        internal sealed class Game
        {
            internal string Id, Name;
            internal List<string> Rules = new List<string>();
            internal JObject Json() => new JObject { ["id"] = Id, ["name"] = Name };
        }

        private sealed class Index
        {
            internal readonly Dictionary<string, Game> Games = new Dictionary<string, Game>(StringComparer.Ordinal);
            internal readonly Dictionary<string, List<Game>> Exes = new Dictionary<string, List<Game>>(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string, List<Game>> Names = new Dictionary<string, List<Game>>(StringComparer.OrdinalIgnoreCase);
        }

        // a catalog that failed to load is tried again on the next use instead of failing for the whole session
        private static readonly Lazy<Index> Catalog = new Lazy<Index>(Load, LazyThreadSafetyMode.PublicationOnly);
        private static readonly object Gate = new object();
        private static int iconJobs;
        internal static string CustomFile => Path.Combine(AppConfig.GetReplayKitRoot(), "game_mappings.json");
        internal static string IconDir => Path.Combine(AppConfig.GetReplayKitRoot(), "game-icons");
        internal static bool ValidId(string id) => id != null && Regex.IsMatch(id, "^(?:[0-9]{1,24}|steam-[0-9]{1,10}|custom-[a-f0-9]{32})$");
        // the icon of a roblox experience sits beside the game icons, though an experience is no game of its own
        internal static bool ValidIconId(string id) => ValidId(id) || id != null && Regex.IsMatch(id, "^(?:[0-9]{1,24}|steam-[0-9]{1,10}|custom-[a-f0-9]{32}|roblox-[0-9]{1,20})-[a-f0-9]{16}$");
        internal static bool Playable(string exe)
        {
            string name = (exe ?? "").ToLowerInvariant();
            return !name.Contains("launcher") && !name.Contains("installer") && !name.Contains("updater") && !name.Contains("crashreport") && !name.Contains("crashhandler") && !name.Contains("crashpad") && !name.Contains("anticheat") && name != "setup.exe" && !name.Contains("uninstall");
        }

        private static Index Load()
        {
            var index = new Index();
            string file = Path.Combine(AppConfig.GetReplayKitRoot(), "data", "games.json");
            if (!File.Exists(file) || new FileInfo(file).Length > 4 * 1024 * 1024) throw new InvalidDataException("The bundled game catalog is missing or invalid.");
            var games = JObject.Parse(File.ReadAllText(file))["games"] as JArray;
            if (games == null || games.Count > 30000) throw new InvalidDataException("The bundled game catalog is invalid.");
            foreach (var raw in games)
            {
                string id = raw["id"]?.Value<string>(), name = raw["name"]?.Value<string>();
                if (!ValidId(id) || string.IsNullOrWhiteSpace(name) || name.Length > 160 || name.Any(char.IsControl)) continue;
                var game = new Game { Id = id, Name = name == "ROBLOX" ? "Roblox" : name };
                foreach (string rule in (raw["executables"] as JArray ?? new JArray()).Values<string>())
                {
                    string clean = (rule ?? "").Replace('\\', '/').ToLowerInvariant();
                    if (clean.Length > 400 || clean.Contains("..") || clean.Any(c => char.IsControl(c) || "*?:\"<>|".Contains(c)) || !clean.EndsWith(".exe", StringComparison.Ordinal)) continue;
                    game.Rules.Add(clean.TrimStart('/'));
                }
                if (game.Rules.Count == 0) continue;
                index.Games[id] = game;
                if (!index.Names.TryGetValue(game.Name, out var names)) index.Names[game.Name] = names = new List<Game>();
                names.Add(game);
                foreach (string rule in game.Rules)
                {
                    string exe = rule.Substring(rule.LastIndexOf('/') + 1);
                    if (!index.Exes.TryGetValue(exe, out var list)) index.Exes[exe] = list = new List<Game>();
                    if (!list.Contains(game)) list.Add(game);
                }
            }
            return index;
        }

        internal static Game Find(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > 32768) return null;
            string normalized = path.Replace('\\', '/').ToLowerInvariant(), exe = normalized.Substring(normalized.LastIndexOf('/') + 1);
            if (!Playable(exe)) return null;
            if (!Catalog.Value.Exes.TryGetValue(exe, out var candidates)) candidates = new List<Game>();
            var matches = candidates.Where(g => g.Rules.Any(r => normalized.EndsWith("/" + r, StringComparison.Ordinal))).ToList();
            if (matches.Count == 1 && (!(exe == "java.exe" || exe == "javaw.exe" || exe == "electron.exe") || matches[0].Rules.Any(r => r.Contains("/") && normalized.EndsWith("/" + r, StringComparison.Ordinal)))) return matches[0];
            var explicitGames = ReplaykitSettings.ReadSettings()["screenshareGameOverrides"] as JArray;
            if (explicitGames != null && explicitGames.Any(g => string.Equals(g["exeName"]?.Value<string>(), exe, StringComparison.OrdinalIgnoreCase)))
                return Custom(Path.GetFileNameWithoutExtension(exe));
            var installed = SteamGames.Find(path);
            if (installed != null) Remember(installed);
            return installed;
        }

        // minecraft java edition runs as the plain java runtime, so only java with a minecraft window title is it: the vanilla, forge, fabric and prism launchers title it "Minecraft ...", and the lunar, badlion and feather clients carry their own names
        private static readonly Regex MinecraftTitle = new Regex(@"^(?:Minecraft\b|Lunar Client\b|Feather Client\b|Badlion Minecraft Client\b)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        internal static Game MinecraftJava(string path, string title)
        {
            string exe = Path.GetFileName(path ?? "");
            if (!exe.Equals("javaw.exe", StringComparison.OrdinalIgnoreCase) && !exe.Equals("java.exe", StringComparison.OrdinalIgnoreCase)) return null;
            return MinecraftTitle.IsMatch(title ?? "") ? Known("Minecraft") : null;
        }

        // where the minecraft icon comes from, since the java runtime carries only the java cup: the launcher from the xbox app or from the classic installer
        internal static string MinecraftLauncher()
        {
            foreach (string file in new[]
            {
                Path.Combine(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\", "XboxGames", "Minecraft Launcher", "Content", "Minecraft.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Minecraft Launcher", "MinecraftLauncher.exe"),
            })
                if (File.Exists(file)) return file;
            return null;
        }

        internal static Game Known(string name) => Catalog.Value.Names.TryGetValue(name, out var games) && games.Count == 1 ? games[0] : null;

        internal static Game Get(string id)
        {
            if (!ValidId(id)) return null;
            if (Catalog.Value.Games.TryGetValue(id, out var game)) return game;
            game = SteamGames.Games().FirstOrDefault(g => g.Id == id);
            if (game != null) return game;
            return ClipStore.Read(CustomFile, db => db[id] is JObject row ? new Game { Id = id, Name = row["name"]?.Value<string>() ?? "Game" } : null, 1024 * 1024);
        }

        internal static Game Custom(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || name.Length > 100 || name.Any(char.IsControl)) throw new ArgumentException("Enter a game name of 1–100 characters.");
            string id;
            using (var hash = SHA256.Create()) id = "custom-" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(name.ToUpperInvariant()))).Replace("-", "").ToLowerInvariant().Substring(0, 32);
            var game = new Game { Id = id, Name = name };
            Remember(game);
            return game;
        }

        private static void Remember(Game game) => ClipStore.Update(CustomFile, db =>
        {
            if (db[game.Id]?["name"]?.Value<string>() == game.Name) return false;
            if (db.Count >= 1000 && db[game.Id] == null) throw new InvalidDataException("Too many saved games.");
            // the name is all that changes, so a game that was played stays marked as played
            var row = db[game.Id] as JObject ?? new JObject();
            row["name"] = game.Name;
            db[game.Id] = row;
            return true;
        }, 1024 * 1024);

        // every game a clip or screenshot was ever tagged with stays one of the users games, so it is still offered after those clips are deleted; returns all of them
        internal static List<Game> Played(IEnumerable<Game> seen)
        {
            var fresh = seen.Where(g => g != null && ValidId(g.Id) && !string.IsNullOrWhiteSpace(g.Name) && g.Name.Length <= 160).GroupBy(g => g.Id).Select(g => g.First()).ToList();
            var played = new List<Game>();
            ClipStore.Update(CustomFile, db =>
            {
                bool changed = false;
                foreach (var game in fresh)
                {
                    var row = db[game.Id] as JObject;
                    if (row?["played"]?.Type == JTokenType.Boolean && row["played"].Value<bool>()) continue;
                    if (row == null && db.Count >= 1000) continue;
                    row = row ?? new JObject { ["name"] = game.Name };
                    row["played"] = true;
                    db[game.Id] = row;
                    changed = true;
                }
                played = db.Properties().Where(p => ValidId(p.Name) && p.Value is JObject row && row["played"]?.Type == JTokenType.Boolean && row["played"].Value<bool>() && row["name"]?.Type == JTokenType.String)
                    .Select(p => new Game { Id = p.Name, Name = p.Value["name"].Value<string>() }).ToList();
                return changed;
            }, 1024 * 1024);
            return played;
        }

        internal static JArray Search(string query)
        {
            string text = (query ?? "").Trim();
            if (text.Length > 100) throw new ArgumentException("Search is too long.");
            var custom = ClipStore.Read(CustomFile, db => db.Properties().Where(p => ValidId(p.Name)).Select(p => new Game { Id = p.Name, Name = p.Value["name"]?.Value<string>() ?? "Game" }).ToList(), 1024 * 1024);
            return new JArray(Catalog.Value.Games.Values.Concat(SteamGames.Games()).Concat(custom).GroupBy(g => g.Id).Select(g => g.First()).Where(g => g.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(g => g.Name).Take(60).Select(g => g.Json()));
        }

        // a game icon is read from the exe of the game, again whenever the game is seen after an hour, and kept under a hash of its pixels: an unchanged icon reuses its file, a changed one becomes a new file, and each clip keeps the version it was made with
        private const string IconsKey = "@icons";
        private static readonly TimeSpan IconRefresh = TimeSpan.FromHours(1);
        private static readonly Dictionary<string, DateTime> IconChecked = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private static Dictionary<string, string> currentIcons;

        private static Dictionary<string, string> CurrentIcons()
        {
            lock (Gate) if (currentIcons != null) return currentIcons;
            var loaded = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var stored = ClipStore.Read(CustomFile, db => (db[IconsKey] as JObject)?.DeepClone() as JObject, 1024 * 1024);
                foreach (var row in stored?.Properties() ?? Enumerable.Empty<JProperty>())
                    if (ValidId(row.Name) && row.Value.Type == JTokenType.String && IconVersion.IsMatch(row.Value.Value<string>())) loaded[row.Name] = row.Value.Value<string>();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is Newtonsoft.Json.JsonException) { Log.Write("Game icon versions unreadable: " + ex.GetType().Name); }
            lock (Gate) return currentIcons = currentIcons ?? loaded;
        }

        private static readonly Regex IconVersion = new Regex("^[a-f0-9]{16}$", RegexOptions.Compiled);

        // the icon version a new clip of this game gets: the newest one read from its exe, or none yet
        internal static string CurrentIcon(string id)
        {
            if (!ValidId(id)) return null;
            var icons = CurrentIcons();
            lock (Gate) return icons.TryGetValue(id, out string version) ? version : null;
        }

        internal static void CacheIcon(Game game, string path)
        {
            if (game == null || !ValidId(game.Id)) return;
            var icons = CurrentIcons();
            lock (Gate)
            {
                if (iconJobs >= 2 || icons.Count >= 512 && !icons.ContainsKey(game.Id)) return;
                if (IconChecked.TryGetValue(game.Id, out DateTime at) && DateTime.UtcNow - at < IconRefresh) return;
                IconChecked[game.Id] = DateTime.UtcNow;
                iconJobs++;
            }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool saved = false;
                try
                {
                    if (!File.Exists(path)) return;
                    Directory.CreateDirectory(IconDir);
                    if ((File.GetAttributes(IconDir) & FileAttributes.ReparsePoint) != 0) return;
                    byte[] png;
                    using (var icon = Icon.ExtractAssociatedIcon(path))
                    {
                        if (icon == null) throw new IOException("no icon");
                        using (var bitmap = new Bitmap(32, 32))
                        using (var draw = Graphics.FromImage(bitmap))
                        using (var output = new MemoryStream())
                        {
                            draw.DrawIcon(icon, new Rectangle(0, 0, 32, 32));
                            bitmap.Save(output, ImageFormat.Png);
                            png = output.ToArray();
                        }
                    }
                    string version;
                    using (var sha = SHA256.Create()) version = string.Concat(sha.ComputeHash(png).Take(8).Select(x => x.ToString("x2")));
                    string target = Path.Combine(IconDir, game.Id + "-" + version + ".png");
                    if (!File.Exists(target))
                    {
                        if (Directory.EnumerateFiles(IconDir, "*.png").Take(2048).Count() >= 2048) return;
                        // written beside the target and moved in, so a failed save never leaves a broken icon behind
                        string part = target + "." + Guid.NewGuid().ToString("N") + ".part";
                        try { File.WriteAllBytes(part, png); File.Move(part, target); }
                        finally { if (File.Exists(part)) File.Delete(part); }
                    }
                    saved = true;
                    bool changed;
                    lock (Gate) { changed = !icons.TryGetValue(game.Id, out string old) || old != version; icons[game.Id] = version; }
                    if (changed)
                    {
                        ClipStore.Update(CustomFile, db =>
                        {
                            var stored = db[IconsKey] as JObject ?? new JObject();
                            stored[game.Id] = version;
                            db[IconsKey] = stored;
                            return true;
                        }, 1024 * 1024);
                        AppConfig.ClearClipsCache();
                        Log.Write("Game icon for " + game.Name + " is version " + version);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is System.ComponentModel.Win32Exception || ex is System.Runtime.InteropServices.ExternalException || ex is Newtonsoft.Json.JsonException) { Log.Write("Game icon could not be cached: " + ex.GetType().Name); }
                finally
                {
                    // a game whose icon could not be made is tried again the next time it is seen
                    lock (Gate) { iconJobs--; if (!saved) IconChecked.Remove(game.Id); }
                }
            });
        }

        // a versioned id names one icon file; a plain game id is the newest version, or the single icon file the first version of this feature kept
        internal static byte[] IconBytes(string id)
        {
            if (!ValidIconId(id) || !Directory.Exists(IconDir) || (File.GetAttributes(IconDir) & FileAttributes.ReparsePoint) != 0) return null;
            string file = Path.Combine(IconDir, id + ".png");
            if (ValidId(id) && CurrentIcon(id) is string current && File.Exists(Path.Combine(IconDir, id + "-" + current + ".png"))) file = Path.Combine(IconDir, id + "-" + current + ".png");
            if (!File.Exists(file)) return null;
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > 65536) return null;
            return File.ReadAllBytes(file);
        }
    }
}

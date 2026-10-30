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
        private static readonly HashSet<string> Icons = new HashSet<string>();
        private static int iconJobs;
        internal static string CustomFile => Path.Combine(AppConfig.GetReplayKitRoot(), "game_mappings.json");
        internal static string IconDir => Path.Combine(AppConfig.GetReplayKitRoot(), "game-icons");
        internal static bool ValidId(string id) => id != null && Regex.IsMatch(id, "^(?:[0-9]{1,24}|steam-[0-9]{1,10}|custom-[a-f0-9]{32})$");
        // the icon of a roblox experience sits beside the game icons, though an experience is no game of its own
        internal static bool ValidIconId(string id) => ValidId(id) || id != null && Regex.IsMatch(id, "^roblox-[0-9]{1,20}-[a-f0-9]{16}$");
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

        internal static void CacheIcon(Game game, string path)
        {
            if (game == null) return;
            lock (Gate)
            {
                if (iconJobs >= 2 || Icons.Count >= 512 || !Icons.Add(game.Id)) return;
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
                    string target = Path.Combine(IconDir, game.Id + ".png");
                    if (File.Exists(target)) { saved = true; return; }
                    if (Directory.EnumerateFiles(IconDir, "*.png").Take(512).Count() >= 512) return;
                    // written beside the target and moved in, so a failed save never leaves a broken icon that later jobs would take as done
                    string part = target + "." + Guid.NewGuid().ToString("N") + ".part";
                    try
                    {
                        using (var icon = Icon.ExtractAssociatedIcon(path))
                        {
                            if (icon == null) throw new IOException("no icon");
                            using (var bitmap = new Bitmap(32, 32))
                            using (var draw = Graphics.FromImage(bitmap))
                            { draw.DrawIcon(icon, new Rectangle(0, 0, 32, 32)); bitmap.Save(part, ImageFormat.Png); }
                        }
                        File.Move(part, target);
                        saved = true;
                    }
                    finally { if (File.Exists(part)) File.Delete(part); }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is System.ComponentModel.Win32Exception || ex is System.Runtime.InteropServices.ExternalException) { Log.Write("Game icon could not be cached: " + ex.GetType().Name); }
                finally
                {
                    // a game whose icon could not be made is tried again the next time it is seen
                    lock (Gate) { iconJobs--; if (!saved) Icons.Remove(game.Id); }
                }
            });
        }

        internal static byte[] IconBytes(string id)
        {
            if (!ValidIconId(id)) return null;
            string file = Path.Combine(IconDir, id + ".png");
            if (!Directory.Exists(IconDir) || (File.GetAttributes(IconDir) & FileAttributes.ReparsePoint) != 0 || !File.Exists(file)) return null;
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > 65536) return null;
            return File.ReadAllBytes(file);
        }
    }
}

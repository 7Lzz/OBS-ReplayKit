using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    internal static class ClipGames
    {
        [DllImport("kernel32.dll")] private static extern ulong GetTickCount64();
        private static readonly HashSet<string> DesktopApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "explorer.exe", "obs64.exe", "obs32.exe", "obsreplaykit.exe", "discord.exe", "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "code.exe", "notepad.exe", "taskmgr.exe", "systemsettings.exe", "applicationframehost.exe", "searchhost.exe", "steam.exe", "epicgameslauncher.exe", "vlc.exe", "codex.exe", "chatgpt.exe", "powershell.exe", "pwsh.exe", "windowsterminal.exe", "cmd.exe", "spotify.exe", "devenv.exe" };
        private static readonly object Gate = new object();
        private static string lastPath = "";
        private static long resolvedAt;
        private static GameCatalog.Game lastGame;
        private static int pending;
        private static long acceptedAt, acceptedSamples;
        private static string acceptedKind = "unknown";
        private sealed class SaveBounds { internal long At; internal JArray Pieces; }
        private static readonly Dictionary<long, SaveBounds> Saves = new Dictionary<long, SaveBounds>();
        // the recognition switch of the settings, read from disk once and then kept in step by every settings write instead of read again for each sample; -1 until it is known
        private static int recognition = -1;

        internal static void SetRecognition(bool on) => Volatile.Write(ref recognition, on ? 1 : 0);

        internal static bool RecognitionOn()
        {
            int known = Volatile.Read(ref recognition);
            if (known >= 0) return known == 1;
            bool on;
            try { on = ReplaykitSettings.ReadSettings()["gameRecognition"]?.Value<bool>() ?? true; }
            catch (InvalidOperationException) { return false; }
            Interlocked.CompareExchange(ref recognition, on ? 1 : 0, -1);
            return Volatile.Read(ref recognition) == 1;
        }

        internal static void Observe(string json)
        {
            if (json.Length > 4000 || !RecognitionOn()) return;
            long at = 0;
            string session = "";
            bool valid = false;
            try
            {
                var raw = JObject.Parse(json);
                at = raw["at"]?.Value<long>() ?? 0;
                session = raw["session"]?.Value<string>() ?? "";
                if (at < 0 || Math.Abs((long)GetTickCount64() - at) > 5000 || session.Length == 0 || session.Length > 80) return;
                valid = true;
                string kind = raw["kind"]?.Value<string>() ?? "unknown", path = raw["path"]?.Value<string>() ?? "";
                // the plugin sends a window title and whether it was launched as minecraft only for java, where they are what tell minecraft from other java programs
                string title = raw["title"]?.Type == JTokenType.String ? raw["title"].Value<string>() : "";
                if (title.Length > 256) title = "";
                bool launched = raw["minecraft"]?.Type == JTokenType.Boolean && raw["minecraft"].Value<bool>();
                GameCatalog.Game game = null;
                bool fullscreen = raw["fullscreen"]?.Type == JTokenType.Boolean && raw["fullscreen"].Value<bool>();
                if ((kind == "game" || kind == "window" || kind == "display") && path.Length > 0 && path.Length < 32768)
                {
                    lock (Gate)
                    {
                        // an app that is no game is looked up again only once a minute, as the steam library is read again; until that first read is done a fresh game may still turn up, so it is asked every two seconds
                        string lookup = title.Length > 0 || launched ? path + "|" + title + (launched ? "|minecraft" : "") : path;
                        if (!string.Equals(lookup, lastPath, StringComparison.OrdinalIgnoreCase) || lastGame == null && at - resolvedAt >= (SteamGames.Settled && LauncherGames.Settled ? 60000 : 2000))
                        {
                            lastGame = null;
                            lastPath = lookup;
                            resolvedAt = at;
                            var minecraft = GameCatalog.MinecraftJava(path, title, launched);
                            lastGame = minecraft ?? (DesktopApps.Contains(Path.GetFileName(path)) ? null : GameCatalog.Find(path));
                            if (minecraft != null) { if (GameCatalog.MinecraftIcon() is string icon) GameCatalog.CacheIcon(minecraft, icon); }
                            else if (lastGame != null) GameCatalog.CacheIcon(lastGame, path);
                        }
                        game = lastGame;
                    }
                }
                // a display capture of a known desktop app or an ordinary window is desktop; an unidentified fullscreen app stays unassigned
                string state = game != null ? "game" : kind == "display" && (path.Length == 0 || DesktopApps.Contains(Path.GetFileName(path)) || !fullscreen) ? "desktop" : "unknown";
                // the plugin says why a sample shows no capture; a capture of a program this catalog does not know is not recognised
                string reason = raw["reason"]?.Type == JTokenType.String ? raw["reason"].Value<string>() : "";
                DiscordPresence.Seen(game, path, reason != "inactive");
                string why = state != "unknown" ? "" : kind == "unknown" ? reason : "not-recognised";
                CaptureTimeline.Observe(at, session, state, game?.Id, game?.Name, why, why == "not-recognised" ? path : "");
                long start = raw["start"]?.Value<long>() ?? 0, buffer = raw["buffer"]?.Value<long>() ?? 0;
                int seconds = raw["seconds"]?.Value<int>() ?? 0;
                string name = buffer > 0 && start >= buffer && start < at && seconds > 0 && seconds <= 3600 && at - start <= seconds * 1000L
                    ? CaptureTimeline.GameName(CaptureTimeline.Between(start, at)) : "";
                PipeClient.SendCaptureName(at, session, buffer, start, seconds, name);
                lock (Gate) { acceptedAt = at; acceptedSamples++; acceptedKind = state; }
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is ArgumentException || ex is Newtonsoft.Json.JsonException || ex is InvalidOperationException || ex is FormatException || ex is OverflowException || ex is UnauthorizedAccessException)
            {
                if (valid)
                {
                    CaptureTimeline.Observe(at, session, "unknown", "", "");
                    lock (Gate) { lastGame = null; acceptedAt = at; acceptedSamples++; acceptedKind = "unknown"; }
                }
                Log.Write("Capture identity unavailable: " + ex.GetType().Name);
            }
        }

        internal static JObject Status()
        {
            lock (Gate) return new JObject { ["ok"] = true, ["peer"] = PipeClient.CapturePeer, ["samples"] = acceptedSamples, ["kind"] = acceptedKind,
                ["ageMs"] = acceptedAt > 0 ? (JToken)((long)GetTickCount64() - acceptedAt) : JValue.CreateNull(), ["historyEntries"] = CaptureTimeline.Count, ["pendingSaves"] = Saves.Count, ["steam"] = SteamGames.Status(), ["launchers"] = LauncherGames.Status() };
        }

        private static bool InScreenshots(string file) => string.Equals(Path.GetFileName(Path.GetDirectoryName(file)), "Screenshots", StringComparison.OrdinalIgnoreCase);
        private static string Owner(string file) => InScreenshots(file) ? Path.GetDirectoryName(Path.GetDirectoryName(file)) : Clips.LibraryRoot(Path.GetDirectoryName(file));
        private static string Key(string file) => InScreenshots(file) ? "@screenshots/" + Path.GetFileName(file) : Path.GetFileName(file);

        internal static JObject Read(string file)
        {
            var capture = ClipStore.ReadFolder(AppConfig.GetDbPath(), Owner(file), db => (db[Key(file)]?["capture"] as JObject)?.DeepClone() as JObject);
            return Current(capture, new FileInfo(file)) ? capture : null;
        }

        internal static bool Current(JObject capture, FileInfo file)
        {
            if (!file.Exists || !(capture?["file"] is JObject stamp)) return false;
            return stamp["bytes"]?.Type == JTokenType.Integer && stamp["written"]?.Type == JTokenType.Integer && stamp["created"]?.Type == JTokenType.Integer &&
                stamp["bytes"].Value<long>() == file.Length && stamp["written"].Value<long>() == file.LastWriteTimeUtc.Ticks && stamp["created"].Value<long>() == file.CreationTimeUtc.Ticks;
        }

        internal static void Stamp(JObject capture, string file)
        {
            var info = new FileInfo(file);
            capture["file"] = new JObject { ["bytes"] = info.Length, ["written"] = info.LastWriteTimeUtc.Ticks, ["created"] = info.CreationTimeUtc.Ticks };
        }

        internal static bool PriorIdentity(JObject capture, long bytes, long written, long created)
        {
            var stamp = capture?["file"] as JObject;
            return stamp?["bytes"]?.Type == JTokenType.Integer && stamp["written"]?.Type == JTokenType.Integer && stamp["created"]?.Type == JTokenType.Integer &&
                stamp["bytes"].Value<long>() == bytes && stamp["written"].Value<long>() == written && stamp["created"].Value<long>() == created;
        }

        internal static void RefreshedRetag(string file, long bytes, long written, long created)
        {
            file = Path.GetFullPath(file);
            if (!Constants.ALLOWED_EXTS.Contains(Path.GetExtension(file)) || !File.Exists(file) || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) return;
            var info = new FileInfo(file);
            if (info.LastWriteTimeUtc.Ticks != written || info.CreationTimeUtc.Ticks != created) return;
            Clips.UpdateDb(db =>
            {
                if (!(db[Key(file)]?["capture"] is JObject capture) || !PriorIdentity(capture, bytes, written, created)) return false;
                Stamp(capture, file);
                return true;
            }, Owner(file));
        }

        internal static JObject Public(JObject capture, FileInfo file)
        {
            var current = Current(capture, file) ? capture : null;
            var summary = CaptureTimeline.Describe(current);
            summary["canReset"] = current?["original"] is JObject;
            // the name and icon of a roblox experience may have to be fetched, which waits while recognition is off
            var experience = RecognitionOn() ? RobloxExperiences.Describe(current, summary) : null;
            if (experience != null) summary["experience"] = experience;
            // the icon version this clip was made with, when it is of the game the clip goes by
            var pinned = current?["gameIcon"] as JObject;
            string game = MainGame(summary), version = pinned?["version"]?.Type == JTokenType.String ? pinned["version"].Value<string>() : null;
            if (game != null && pinned?["id"]?.ToString() == game && version != null && GameCatalog.ValidIconId(game + "-" + version)) summary["icon"] = game + "-" + version;
            return summary;
        }

        internal static void Forget(string file)
        {
            Clips.UpdateDb(db => db.Remove(Key(file)), Owner(file));
        }

        internal static void Write(string file, JObject capture, bool automatic = false)
        {
            if (capture == null || !File.Exists(file)) return;
            var summary = CaptureTimeline.Describe(capture);
            // the clip keeps the icon its game has now, unless it already carries the icon of that same game from the clip it was made from
            string game = MainGame(summary);
            string version = game != null && (capture["gameIcon"] as JObject)?["id"]?.ToString() != game ? GameCatalog.CurrentIcon(game) : null;
            Clips.UpdateDb(db =>
            {
                string key = Key(file);
                var entry = db[key] as JObject ?? new JObject();
                var saved = (JObject)capture.DeepClone();
                if (version != null) saved["gameIcon"] = new JObject { ["id"] = game, ["version"] = version };
                Stamp(saved, file);
                if (entry["capture"] is JObject existing && Current(existing, new FileInfo(file)))
                {
                    bool wasCorrected = existing["manual"]?.Value<bool>() == true;
                    if (automatic && wasCorrected)
                    {
                        if (existing["original"] is JObject) return false;
                        existing["original"] = saved;
                        return true;
                    }
                    if (saved["manual"]?.Value<bool>() == true && saved["original"] == null)
                        saved["original"] = wasCorrected ? existing["original"]?.DeepClone() : existing.DeepClone();
                }
                entry["capture"] = saved;
                db[key] = entry;
                return true;
            }, Owner(file));
            try { GameCatalog.Played(((JArray)summary["games"]).Select(g => new GameCatalog.Game { Id = g["id"].Value<string>(), Name = g["name"].Value<string>() })); }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is Newtonsoft.Json.JsonException) { Log.Write("Played games not updated: " + ex.GetType().Name); }
        }

        internal static void Copy(string source, string target) => Write(target, Read(source));

        // the game a clip goes by: its main game, or the one game it holds part of
        private static string MainGame(JObject summary) => summary["kind"]?.Value<string>() == "game" ? summary["id"]?.Value<string>() : summary["partial"]?["id"]?.Value<string>();

        internal static bool Matches(JObject row, string game)
        {
            if (string.IsNullOrEmpty(game) || game == "all") return true;
            var capture = row["capture"] as JObject;
            if (game == "desktop" || game == "unknown" || game == "mixed") return (capture?["kind"]?.Value<string>() ?? "unknown") == game;
            return (capture?["games"] as JArray ?? new JArray()).Any(g => g["id"]?.Value<string>() == game);
        }

        internal static JObject Library()
        {
            var clips = Clips.GetClipsList();
            var shots = Screenshots.List().OfType<JObject>().ToList();
            var files = clips.Concat(shots).ToList();
            var games = new Dictionary<string, JObject>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                var capture = file["capture"] as JObject;
                string kind = capture?["kind"]?.Value<string>() ?? "unknown";
                bool screenshot = Path.GetExtension(file["name"]?.Value<string>() ?? "").Equals(".png", StringComparison.OrdinalIgnoreCase);
                var choices = (capture?["games"] as JArray ?? new JArray()).OfType<JObject>().Select(g => (JObject)g.DeepClone()).ToList();
                if (kind != "game") choices.Add(new JObject { ["id"] = kind, ["name"] = kind == "desktop" ? Locales.Message("clips.desktop") : kind == "mixed" ? Locales.Message("clips.mixed") : Locales.Message("clips.unassigned") });
                foreach (var choice in choices)
                {
                    string id = choice["id"].Value<string>();
                    if (!games.TryGetValue(id, out var entry)) games[id] = entry = new JObject { ["id"] = id, ["name"] = choice["name"], ["count"] = 0, ["clips"] = 0, ["screenshots"] = 0 };
                    entry["count"] = entry["count"].Value<int>() + 1;
                    string type = screenshot ? "screenshots" : "clips";
                    entry[type] = entry[type].Value<int>() + 1;
                }
            }
            // the games of the clips there are now join the played ones, which the game picker keeps offering after their clips are gone
            foreach (var entry in games.Values)
                entry["steamAppId"] = GameCatalog.SteamAppIdFor(entry["id"].Value<string>());
            var played = GameCatalog.Played(games.Values.Where(g => GameCatalog.ValidId(g["id"].Value<string>())).Select(g => new GameCatalog.Game { Id = g["id"].Value<string>(), Name = g["name"].Value<string>() }));
            return new JObject { ["ok"] = true, ["total"] = files.Count, ["clips"] = clips.Count, ["screenshots"] = shots.Count, ["games"] = new JArray(games.Values.OrderBy(g => g["name"].Value<string>())),
                ["played"] = new JArray(played.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.Json())),
                ["roblox"] = new JObject { ["id"] = RobloxExperiences.RobloxId, ["name"] = "Roblox", ["experiences"] = RobloxExperiences.Library() },
                ["hidden"] = new JArray(GameCatalog.Hidden()) };
        }

        internal static void Saved(string file, long end, double duration = 0)
        {
            if (!File.Exists(file) || end <= 0) return;
            if (duration <= 0) duration = (Trim.ProbeVideo(file)?.Duration ?? 0);
            if (!CaptureTimeline.Number(duration) || duration <= 0) return;
            var capture = CaptureTimeline.Between(end - (long)Math.Round(duration * 1000), end);
            RobloxExperiences.Attach(capture, end);
            Write(file, capture, automatic: true);
        }

        internal static void BindSave(string json)
        {
            if (json.Length > 400000 || !RecognitionOn()) return;
            try
            {
                var raw = JObject.Parse(json);
                if (raw["id"]?.Type != JTokenType.Integer || !(raw["pieces"] is JArray pieces) || pieces.Count == 0 || pieces.Count > 500) return;
                long id = raw["id"].Value<long>(), now = (long)GetTickCount64();
                if (id <= 0 || pieces.Any(p => !(p is JObject) || p["path"]?.Type != JTokenType.String || p["s"]?.Type != JTokenType.Integer || p["e"]?.Type != JTokenType.Integer)) return;
                lock (Gate)
                {
                    foreach (long old in Saves.Where(p => now - p.Value.At > 600000).Select(p => p.Key).ToList()) Saves.Remove(old);
                    if (Saves.Count >= 32) return;
                    Saves[id] = new SaveBounds { At = now, Pieces = (JArray)pieces.DeepClone() };
                }
            }
            catch (Exception ex) when (ex is Newtonsoft.Json.JsonException || ex is ArgumentException || ex is FormatException || ex is OverflowException)
            { Log.Write("Invalid capture save boundary rejected."); }
        }

        internal static void SavedDisk(string file, long id, IList<string> files, IList<double> lengths, double duration)
        {
            if (!RecognitionOn()) return;
            SaveBounds saved = null;
            for (int attempt = 0; attempt < 10; attempt++)
            {
                lock (Gate) { if (Saves.TryGetValue(id, out saved)) { Saves.Remove(id); break; } }
                Thread.Sleep(20);
            }
            if (saved == null || (long)GetTickCount64() - saved.At > 600000 || saved.Pieces.Count != files.Count)
            { Log.Write("Clip saved without capture evidence: no matching native boundary."); return; }
            for (int i = 0; i < files.Count; i++)
                if (!string.Equals(Path.GetFullPath(saved.Pieces[i]["path"].Value<string>()), Path.GetFullPath(files[i]), StringComparison.OrdinalIgnoreCase))
                { Log.Write("Clip saved without capture evidence: native piece list differs."); return; }
            var capture = CaptureTimeline.Pieces(saved.Pieces, lengths, duration);
            var last = saved.Pieces.Last;
            RobloxExperiences.Attach(capture, last?["e"]?.Value<long>() ?? 0);
            Write(file, capture, automatic: true);
        }

        internal static void NativeFile(string json)
        {
            // with recognition off a new file gets no tag, so it is neither probed nor written to the db
            if (json.Length > 400000 || !RecognitionOn()) return;
            if (Interlocked.Increment(ref pending) > 16) { Interlocked.Decrement(ref pending); return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var raw = JObject.Parse(json);
                    string path = raw["path"]?.Value<string>() ?? "";
                    long end = raw["end"]?.Value<long>() ?? 0;
                    string root = Path.GetFullPath(AppConfig.GetClipDir()).TrimEnd('\\') + "\\";
                    string full = Path.GetFullPath(path);
                    if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) return;
                    string extension = Path.GetExtension(full).ToLowerInvariant();
                    if (extension == ".png" && string.Equals(Path.GetDirectoryName(full), Screenshots.Folder(), StringComparison.OrdinalIgnoreCase))
                        Saved(full, end, .001);
                    else if (Constants.ALLOWED_EXTS.Contains(extension))
                    {
                        Clips.RegisterGameFolder(full);
                        if (!string.Equals(Path.GetDirectoryName(full), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) && !Clips.IsGameFolder(Path.GetDirectoryName(full))) return;
                        // a memory replay whose press the plugin did not see ends where obs started writing it, which is when the file was created; a file older than two minutes is not that save
                        if (end <= 0 && raw["replay"]?.Type == JTokenType.Boolean && raw["replay"].Value<bool>())
                        {
                            double age = (DateTime.UtcNow - File.GetCreationTimeUtc(full)).TotalMilliseconds;
                            if (age >= 0 && age < 120000) end = (long)GetTickCount64() - (long)age;
                        }
                        if (raw["ranges"] is JArray ranges)
                        {
                            if (ranges.Count > 4096) return;
                            var intervals = new JArray();
                            double position = 0, duration = Trim.ProbeVideo(full)?.Duration ?? 0;
                            foreach (var range in ranges)
                            {
                                long start = range["s"]?.Value<long>() ?? 0, stop = range["e"]?.Value<long>() ?? 0;
                                if (start < 0 || stop <= start || stop > end || position >= duration) continue;
                                var capture = CaptureTimeline.Between(start, Math.Min(stop, start + (long)((duration - position) * 1000)));
                                foreach (var interval in (JArray)capture["intervals"])
                                    CaptureTimeline.Add(intervals, position + interval["s"].Value<double>(), position + interval["e"].Value<double>(), interval);
                                position += capture["duration"].Value<double>();
                            }
                            var recorded = CaptureTimeline.Pack(duration, intervals);
                            RobloxExperiences.Attach(recorded, end);
                            Write(full, recorded, automatic: true);
                        }
                        else Saved(full, end);
                    }
                }
                catch (Exception ex) { Log.Write("Capture tag was not saved: " + ex.GetType().Name); }
                finally { Interlocked.Decrement(ref pending); }
            });
        }

        internal static void Correct(string file, string id, string customName, double duration, string universe = "")
        {
            if (!File.Exists(file)) throw new FileNotFoundException(Locales.Message("common.clipNotFound2"));
            GameCatalog.Game game = null;
            if (!string.IsNullOrWhiteSpace(customName)) game = GameCatalog.Custom(customName);
            else if (id != "desktop" && id != "unknown") game = GameCatalog.Get(id) ?? throw new ArgumentException(Locales.Message("clips.unknownGame2"));
            if (universe.Length > 0 && game?.Id != RobloxExperiences.RobloxId) throw new ArgumentException(Locales.Message("clips.anExperienceMustBelongToRoblox"));
            var experience = universe.Length > 0 ? RobloxExperiences.Select(universe) : null;
            duration = CorrectionDuration(file, duration);
            var rows = new JArray();
            CaptureTimeline.Add(rows, 0, duration, game != null ? "game" : id, game?.Id ?? "", game?.Name ?? "");
            var corrected = Correction(duration, rows, Read(file));
            if (experience != null) corrected["experience"] = experience;
            Write(file, corrected);
            if (game != null) GameCatalog.Played(new[] { game }, restore: true);
        }

        private static double CorrectionDuration(string file, double duration)
        {
            if (duration <= 0) duration = Path.GetExtension(file).Equals(".png", StringComparison.OrdinalIgnoreCase) ? .001 : (Trim.ProbeVideo(file)?.Duration ?? 0);
            if (!CaptureTimeline.Number(duration) || duration <= 0) throw new ArgumentException(Locales.Message("clips.theClipLengthIsUnavailable"));
            return duration;
        }

        private static JObject Correction(double duration, JArray rows, JObject previous)
        {
            var result = CaptureTimeline.Pack(duration, rows, true);
            var original = previous?["original"] as JObject ?? (previous?["manual"]?.Value<bool>() != true ? previous : null);
            if (original != null) result["original"] = original.DeepClone();
            return result;
        }

        internal static JObject EditMix(string file, string version, string action, string id, string name)
        {
            bool add = action == "add";
            if (!add && (action != "remove" || !GameCatalog.ValidId(id))) throw new ArgumentException();
            using var pin = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                (!string.IsNullOrEmpty(version) && ClipRenames.Version(info) != version)) throw new FileNotFoundException();
            var game = add ? (string.IsNullOrEmpty(id) ? GameCatalog.Custom(name) :
                GameCatalog.Get(id) ?? throw new ArgumentException(Locales.Message("clips.unknownGame2"))) : null;
            double duration = Read(file)?["duration"]?.Value<double>() ?? 0;
            if (add) duration = CorrectionDuration(file, duration);
            JObject result = null;
            bool changed = Clips.UpdateDb(db =>
            {
                string key = Key(file);
                var entry = db[key] as JObject ?? new JObject();
                var saved = entry["capture"] as JObject;
                var previous = Current(saved, info) ? saved : null;
                result = ChangeMembership(previous, previous?["duration"]?.Value<double>() ?? duration, game, id);
                if (ReferenceEquals(result, previous)) return false;
                Stamp(result, file);
                entry["capture"] = result;
                db[key] = entry;
                return true;
            }, Owner(file));
            if (changed && add)
            {
                try { GameCatalog.Played(new[] { game }, restore: true); }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is Newtonsoft.Json.JsonException)
                { Log.Write("Played games not updated: " + ex.GetType().Name); }
            }
            return Public(result, info);
        }

        private static JObject ChangeMembership(JObject previous, double duration, GameCatalog.Game added, string removed)
        {
            var summary = CaptureTimeline.Describe(previous);
            string primary = MainGame(summary);
            var games = (JArray)summary["games"];
            var members = games.OfType<JObject>().Where(g => summary["kind"].Value<string>() == "mixed" || g["id"].Value<string>() == primary)
                .Select(g => new GameCatalog.Game { Id = g["id"].Value<string>(), Name = g["name"].Value<string>() }).ToList();
            if (added != null)
            {
                if (members.Any(g => g.Id == added.Id)) return previous;
                if (members.Count >= 128) throw new ArgumentException();
                members.Add(added);
            }
            else if (members.RemoveAll(g => g.Id == removed) == 0) return previous;

            var rows = new JArray();
            var single = members.Count == 1 ? members[0] : null;
            if (members.Count < 2)
                CaptureTimeline.Add(rows, 0, duration, single != null ? "game" : "unknown", single?.Id ?? "", single?.Name ?? "");
            var result = Correction(duration, rows, previous);
            if (members.Count > 1)
            {
                var kept = new JArray();
                if (previous != null)
                    CaptureTimeline.PlaceVideoGames(kept, CaptureTimeline.Cut(previous, 0, duration), summary, 0);
                var evidence = new JArray();
                foreach (var member in members)
                {
                    var held = kept.Where(row => row["id"]?.Value<string>() == member.Id);
                    if (!held.Any()) CaptureTimeline.Add(evidence, 0, duration, "game", member.Id, member.Name);
                    else foreach (var row in held) CaptureTimeline.Add(evidence, row["s"].Value<double>(), row["e"].Value<double>(), row);
                }
                CaptureTimeline.SetVideoGames(result, evidence, keepOrder: true);
            }
            else if (single != null)
            {
                var earlier = result["original"] as JObject;
                var details = primary == single.Id ? previous : MainGame(CaptureTimeline.Describe(earlier)) == single.Id ? earlier : null;
                foreach (string key in new[] { "experience", "gameIcon" })
                    if (details?[key] is JObject detail) result[key] = detail.DeepClone();
            }
            return result;
        }

        internal static void Reset(string file)
        {
            var capture = Read(file);
            if (!(capture?["original"] is JObject original)) throw new ArgumentException(Locales.Message("clips.theOriginalGameAssignmentIsUnavailable"));
            Write(file, original);
        }

        internal static void Rendered(string source, string target, VideoPlan plan, double start, double end, double speed, bool screenshot, double frame, JObject sourceCapture, LayerPlan layers)
        {
            JObject result;
            if (plan == null)
            {
                result = CaptureTimeline.Cut(sourceCapture, screenshot ? frame * speed : start, screenshot ? frame * speed + .001 : end, screenshot ? 1 : speed);
                CarryDetails(result, new[] { PieceDetails(CaptureTimeline.Describe(result), sourceCapture) });
            }
            else
            {
                var rows = new JArray();
                var originalRows = new JArray();
                var games = new JArray();
                var originalGames = new JArray();
                var renderedDetails = new List<JObject>();
                var originalDetails = new List<JObject>();
                var captures = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase) { [source] = sourceCapture };
                bool corrected = false;
                bool library = false;
                double left = screenshot ? frame : plan.Start, right = screenshot ? frame + .001 : plan.End;

                void AddFootage(string path, double at, double from, double to, double rate, bool mainRow)
                {
                    string file = path ?? source;
                    if (!captures.TryGetValue(file, out var original)) captures[file] = original = Read(file);
                    var cut = CaptureTimeline.Cut(original, from, to, rate);
                    corrected |= cut["original"] is JObject;
                    var baseline = cut["original"] as JObject ?? cut;
                    var recorded = original?["original"] as JObject ?? original;
                    var summary = CaptureTimeline.Describe(cut);
                    var baselineSummary = baseline == cut ? summary : CaptureTimeline.Describe(baseline);
                    library |= !mainRow || !string.Equals(file, source, StringComparison.OrdinalIgnoreCase) ||
                        CaptureTimeline.HasVideoGames(cut) || CaptureTimeline.HasVideoGames(baseline);
                    CaptureTimeline.PlaceVideoGames(games, cut, summary, at);
                    CaptureTimeline.PlaceVideoGames(originalGames, baseline, baselineSummary, at);
                    renderedDetails.Add(PieceDetails(summary, original));
                    originalDetails.Add(PieceDetails(baselineSummary, recorded));
                    if (mainRow)
                    {
                        foreach (var row in (JArray)cut["intervals"])
                            CaptureTimeline.Add(rows, at + row["s"].Value<double>(), at + row["e"].Value<double>(), row);
                        foreach (var row in (JArray)baseline["intervals"])
                            CaptureTimeline.Add(originalRows, at + row["s"].Value<double>(), at + row["e"].Value<double>(), row);
                    }
                }

                if (screenshot)
                {
                    var piece = plan.PieceAt(frame, speed);
                    if (piece != null)
                    {
                        double rate = piece.Rate(speed);
                        double from = piece.SrcStart + Math.Max(0, frame - piece.At) * rate;
                        AddFootage(piece.Path, 0, from, Math.Min(piece.SrcEnd, from + (right - left) * rate), rate, true);
                    }
                }
                else
                    foreach (var part in plan.Parts(speed))
                        AddFootage(part.Piece.Path, part.A - left, part.SrcA, part.SrcB, part.Piece.Rate(speed), true);

                if (layers != null)
                    foreach (var layer in layers.ClipSpans(left - plan.Start, right - plan.Start, screenshot))
                        AddFootage(layer.Path, layer.At + plan.Start - left, layer.Src, layer.Src + (layer.End - layer.At) * layer.Speed, layer.Speed, false);

                result = CaptureTimeline.Pack(right - left, rows);
                if (library) CaptureTimeline.SetVideoGames(result, games);
                CarryDetails(result, renderedDetails);
                if (corrected)
                {
                    result["manual"] = true;
                    var baseline = CaptureTimeline.Pack(right - left, originalRows);
                    if (library) CaptureTimeline.SetVideoGames(baseline, originalGames);
                    CarryDetails(baseline, originalDetails);
                    result["original"] = baseline;
                }
            }
            Write(target, result);
        }

        private static JObject PieceDetails(JObject summary, JObject source)
        {
            string game = MainGame(summary);
            bool matches = game != null && game == MainGame(CaptureTimeline.Describe(source));
            return new JObject
            {
                ["id"] = game,
                ["experience"] = matches ? source?["experience"]?.DeepClone() : null,
                ["gameIcon"] = matches ? source?["gameIcon"]?.DeepClone() : null,
            };
        }

        private static void CarryDetails(JObject target, IEnumerable<JObject> pieces)
        {
            string game = MainGame(CaptureTimeline.Describe(target));
            if (game == null) return;
            var matching = pieces.Where(piece => piece["id"]?.Value<string>() == game).ToList();
            if (matching.Count == 0) return;
            if (matching[0]["experience"] is JObject experience &&
                experience["universe"]?.Value<string>() is string universe &&
                matching.All(piece => (piece["experience"] as JObject)?["universe"]?.Value<string>() == universe))
                target["experience"] = experience.DeepClone();

            if (matching[0]["gameIcon"] is JObject icon && matching.All(piece => JToken.DeepEquals(piece["gameIcon"], icon)))
            {
                target["gameIcon"] = icon.DeepClone();
            }
        }
    }
}

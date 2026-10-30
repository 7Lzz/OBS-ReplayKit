using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // the roblox experience a roblox clip was made in: the roblox player logs every join with its universe and every return to the app or shutdown; the name and icon of an experience come from the public roblox web api, kept in game_mappings.json and game-icons, and each clip keeps the icon and name it was made with
    internal static class RobloxExperiences
    {
        [DllImport("kernel32.dll")] private static extern ulong GetTickCount64();

        internal const string RobloxId = "363445589247131668";
        private const string CacheKey = "@roblox";
        private const int MaxLogBytes = 64 * 1024 * 1024;
        private static readonly Regex JoinLine = new Regex(@"^(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d{1,7})?Z),[^\[]*\[FLog::GameJoinLoadTime\] Report game_join_loadtime: placeid:(\d{1,20}),.*?universeid:(\d{1,20})", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex LeaveLine = new Regex(@"^(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d{1,7})?Z),[^\[]*\[FLog::SessionTransitionFSM\] (?:Entered app session|Tearing down)\.", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex LogName = new Regex(@"_(\d{8}T\d{6}Z)_Player_[0-9A-Fa-f]{1,16}(?:_last)?\.log$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex UniverseId = new Regex(@"^[0-9]{1,20}$", RegexOptions.Compiled);
        private static readonly HttpClient Http = CreateHttpClient();
        private static readonly object Gate = new object();

        private sealed class Visit
        {
            internal DateTime At;
            internal string Universe, Place;
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, SslProtocols = System.Security.Authentication.SslProtocols.Tls12 }) { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("OBSReplayKit");
            return client;
        }

        internal static bool ValidUniverse(string id) => id != null && UniverseId.IsMatch(id);

        // the joins and leaves of the newest player logs, oldest first; roblox keeps its log open, so the times in the file names are read instead of the write times the files show
        private static List<Visit> Visits(DateTime until)
        {
            var visits = new List<Visit>();
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            // the roblox installer and launchers like bloxstrap log under Roblox\logs; the microsoft store app logs inside its package
            var dirs = new List<string> { Path.Combine(local, "Roblox", "logs") };
            string packages = Path.Combine(local, "Packages");
            if (Directory.Exists(packages)) dirs.AddRange(Directory.EnumerateDirectories(packages, "ROBLOXCORPORATION.ROBLOX_*").Take(4).Select(d => Path.Combine(d, "LocalState", "logs")));
            var logs = new List<Tuple<DateTime, string>>();
            foreach (string dir in dirs)
            {
                if (!Directory.Exists(dir) || (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
                foreach (string file in Directory.EnumerateFiles(dir, "*_Player_*.log").Take(2000))
                {
                    var match = LogName.Match(Path.GetFileName(file));
                    if (match.Success && DateTime.TryParseExact(match.Groups[1].Value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime started) && started <= until)
                        logs.Add(Tuple.Create(started, file));
                }
            }
            foreach (var log in logs.OrderByDescending(l => l.Item1).Take(4).OrderBy(l => l.Item1))
            {
                using (var stream = new FileStream(log.Item2, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length > MaxLogBytes) continue;
                    using (var reader = new StreamReader(stream))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (line.IndexOf("game_join_loadtime", StringComparison.Ordinal) < 0 && line.IndexOf("SessionTransitionFSM", StringComparison.Ordinal) < 0) continue;
                            var join = JoinLine.Match(line);
                            var leave = join.Success ? null : LeaveLine.Match(line);
                            var at = join.Success ? join.Groups[1].Value : leave.Success ? leave.Groups[1].Value : null;
                            if (at == null || !DateTime.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime when)) continue;
                            visits.Add(new Visit { At = when, Universe = join.Success ? join.Groups[3].Value : null, Place = join.Success ? join.Groups[2].Value : null });
                        }
                    }
                }
            }
            return visits.OrderBy(v => v.At).ToList();
        }

        // the experience joined last before the clip ended that the clip still overlaps; null when the player was in the app menu or roblox was closed the whole time
        private static Visit During(DateTime start, DateTime end)
        {
            Visit current = null;
            DateTime? left = null;
            foreach (var visit in Visits(end))
            {
                if (visit.At > end) break;
                if (visit.Universe != null) { current = visit; left = null; }
                else if (current != null && left == null) left = visit.At;
            }
            return current != null && (left == null || left.Value > start) ? current : null;
        }

        // a roblox clip just tagged from the live timeline gets the experience it was made in, with the icon and name the experience has now when they were looked up within the hour; otherwise the worker looks them up and pins them to the clip afterwards
        internal static void Attach(JObject capture, long endTick)
        {
            try
            {
                var summary = CaptureTimeline.Describe(capture);
                string game = summary["kind"].Value<string>() == "game" ? summary["id"]?.Value<string>() : summary["partial"]?["id"]?.Value<string>();
                if (game != RobloxId || endTick <= 0) return;
                double duration = capture["duration"]?.Value<double>() ?? 0;
                DateTime end = DateTime.UtcNow - TimeSpan.FromMilliseconds(Math.Max(0, (long)GetTickCount64() - endTick));
                var visit = During(end - TimeSpan.FromSeconds(Math.Max(0, duration)), end);
                if (visit == null) return;
                var experience = new JObject { ["universe"] = visit.Universe, ["place"] = visit.Place };
                Load();
                lock (Gate)
                {
                    if (Cache.TryGetValue(visit.Universe, out var known) && known.Icon != null && DateTime.UtcNow - known.Checked < Refresh)
                    {
                        experience["icon"] = known.Icon;
                        experience["name"] = known.Name;
                    }
                    else Want(visit.Universe, true);
                }
                capture["experience"] = experience;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is FormatException || ex is OverflowException)
            { Log.Write("Roblox experience unavailable: " + ex.GetType().Name); }
        }

        // what the clips page shows of a roblox clip: the icon and name pinned to it, or while those are being looked up the newest known ones
        internal static JObject Describe(JObject capture, JObject summary)
        {
            string game = summary["kind"]?.Value<string>() == "game" ? summary["id"]?.Value<string>() : summary["partial"]?["id"]?.Value<string>();
            var experience = capture?["experience"] as JObject;
            string universe = experience?["universe"]?.Type == JTokenType.String ? experience["universe"].Value<string>() : null;
            if (game != RobloxId || !ValidUniverse(universe)) return null;
            string icon = experience["icon"]?.Type == JTokenType.String && ValidVersion(experience["icon"].Value<string>()) ? experience["icon"].Value<string>() : null;
            string name = experience["name"]?.Type == JTokenType.String ? experience["name"].Value<string>() : null;
            if (icon == null || name == null)
            {
                Load();
                lock (Gate)
                {
                    if (Cache.TryGetValue(universe, out var known)) { icon = icon ?? known.Icon; name = name ?? known.Name; }
                    Want(universe, false);
                }
            }
            return new JObject { ["id"] = icon != null ? IconId(universe, icon) : "", ["name"] = name ?? "" };
        }

        internal static string IconId(string universe, string version) => "roblox-" + universe + "-" + version;
        private static bool ValidVersion(string version) => version != null && Regex.IsMatch(version, "^[a-f0-9]{16}$");

        // what is known of an experience: its newest name and icon, the image address that icon came from, when roblox was last asked, and when it may be asked again after a failure
        private sealed class Entry
        {
            internal string Name, Icon, Url;
            internal DateTime Checked, RetryAt;
            internal int Misses;
        }

        // a new clip is given the icon the experience has now when roblox was asked within this time; so an experience is asked about at most once an hour, however much it is clipped
        private static readonly TimeSpan Refresh = TimeSpan.FromHours(1);
        // every request to roblox, across all experiences, stays under this many a minute; the limits roblox publishes for its thumbnail service are a hundred and more a minute
        private const int RequestsPerMinute = 30;
        // how many experiences one request asks about, the batch size the roblox games and thumbnails apis take
        private const int Batch = 50;
        private static readonly Dictionary<string, Entry> Cache = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static readonly HashSet<string> Due = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Queue<DateTime> Sent = new Queue<DateTime>();
        private static readonly AutoResetEvent Wake = new AutoResetEvent(false);
        private static bool loaded, started;
        private static DateTime blockedUntil;
        private static int failures;

        // the file is read before Gate is taken, since writes of the same file take Gate inside its file lock
        private static void Load()
        {
            lock (Gate) if (loaded) return;
            JObject stored = null;
            try { stored = ClipStore.Read(GameCatalog.CustomFile, db => (db[CacheKey] as JObject)?.DeepClone() as JObject, 1024 * 1024); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is Newtonsoft.Json.JsonException) { Log.Write("Roblox experiences unreadable: " + ex.GetType().Name); }
            lock (Gate)
            {
                if (loaded) return;
                loaded = true;
                foreach (var row in stored?.Properties() ?? Enumerable.Empty<JProperty>())
                {
                    if (!ValidUniverse(row.Name) || !(row.Value is JObject value)) continue;
                    Cache[row.Name] = new Entry
                    {
                        Name = value["name"]?.Type == JTokenType.String ? value["name"].Value<string>() : null,
                        Icon = value["icon"]?.Type == JTokenType.String && ValidVersion(value["icon"].Value<string>()) ? value["icon"].Value<string>() : null,
                        Url = value["url"]?.Type == JTokenType.String ? value["url"].Value<string>() : null,
                        Checked = value["checked"]?.Type == JTokenType.Date ? value["checked"].Value<DateTime>().ToUniversalTime() : DateTime.MinValue,
                    };
                }
            }
        }

        // asks the worker about an experience: a new clip wants the current icon, so it counts as due once the last answer is older than Refresh; a clip still waiting for its icon only when nothing is known yet
        private static void Want(string universe, bool newClip)
        {
            bool known = Cache.TryGetValue(universe, out var entry) && entry.Icon != null;
            if (known && !(newClip && DateTime.UtcNow - entry.Checked >= Refresh)) { if (!newClip) PinLater(universe); return; }
            if (Due.Count >= 1000 || !Due.Add(universe)) return;
            if (!started) { started = true; new Thread(Work) { Name = "roblox-experiences", IsBackground = true }.Start(); }
            Wake.Set();
        }

        // a clip tagged before its icon was known gets the known one pinned by the next pass of the worker
        private static readonly HashSet<string> Unpinned = new HashSet<string>(StringComparer.Ordinal);
        private static void PinLater(string universe)
        {
            if (Unpinned.Count < 1000 && Unpinned.Add(universe)) { if (!started) { started = true; new Thread(Work) { Name = "roblox-experiences", IsBackground = true }.Start(); } Wake.Set(); }
        }

        private static void Work()
        {
            while (!Server.State.Shutdown)
            {
                Wake.WaitOne(TimeSpan.FromMinutes(1));
                List<string> pins;
                lock (Gate) { pins = Unpinned.ToList(); Unpinned.Clear(); }
                if (pins.Count > 0) Pin(pins);
                List<string> batch;
                lock (Gate)
                {
                    DateTime now = DateTime.UtcNow;
                    if (now < blockedUntil || Due.Count == 0) continue;
                    batch = Due.Where(u => !Cache.TryGetValue(u, out var e) || e.RetryAt <= now).Take(Batch).ToList();
                    while (Sent.Count > 0 && now - Sent.Peek() > TimeSpan.FromMinutes(1)) Sent.Dequeue();
                    // a pass sends two api requests and at most one image per experience, so it waits until the minute has room for that
                    if (batch.Count == 0 || Sent.Count + 2 + batch.Count > RequestsPerMinute) { batch = batch.Take(Math.Max(0, RequestsPerMinute - Sent.Count - 2)).ToList(); if (batch.Count == 0) continue; }
                }
                try
                {
                    Fetch(batch);
                    // an experience whose icon is still in moderation stays queued for its retry
                    lock (Gate) { failures = 0; foreach (string u in batch) if (!(Cache.TryGetValue(u, out var e) && e.RetryAt > DateTime.UtcNow)) Due.Remove(u); }
                    Pin(batch);
                    AppConfig.ClearClipsCache();
                }
                catch (RateLimited ex)
                {
                    lock (Gate) blockedUntil = DateTime.UtcNow + ex.Wait;
                    Log.Write("Roblox asked to wait " + (int)ex.Wait.TotalSeconds + " s before more experience lookups");
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is Newtonsoft.Json.JsonException || ex is ArgumentException || ex is ExternalException || ex is InvalidOperationException || ex is AggregateException)
                {
                    // anything else, a block in front of the api or roblox being down, waits a minute and twice as long after every failure in a row, up to an hour
                    lock (Gate) { failures = Math.Min(failures + 1, 7); blockedUntil = DateTime.UtcNow + TimeSpan.FromMinutes(Math.Min(60, 1 << (failures - 1))); }
                    Log.Write("Roblox experience lookup failed (" + ex.GetType().Name + "), next try in " + Math.Min(60, 1 << (failures - 1)) + " min");
                }
            }
        }

        private sealed class RateLimited : Exception
        {
            internal readonly TimeSpan Wait;
            internal RateLimited(TimeSpan wait) { Wait = wait; }
        }

        private static HttpResponseMessage Send(Uri url)
        {
            lock (Gate) Sent.Enqueue(DateTime.UtcNow);
            var response = Http.GetAsync(url).GetAwaiter().GetResult();
            if ((int)response.StatusCode != 429) return response;
            // roblox says how long to wait in retry-after, or in x-ratelimit-reset as seconds; without either a minute, and never more than an hour
            TimeSpan wait = TimeSpan.FromMinutes(1);
            var retry = response.Headers.RetryAfter;
            if (retry?.Delta != null) wait = retry.Delta.Value;
            else if (retry?.Date != null) wait = retry.Date.Value - DateTimeOffset.UtcNow;
            else if (response.Headers.TryGetValues("x-ratelimit-reset", out var reset) && double.TryParse(reset.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)) wait = TimeSpan.FromSeconds(seconds);
            response.Dispose();
            throw new RateLimited(TimeSpan.FromSeconds(Math.Max(10, Math.Min(3600, wait.TotalSeconds))));
        }

        private static JObject GetJson(string url)
        {
            using (var response = Send(new Uri(url)))
            {
                if (!response.IsSuccessStatusCode) throw new HttpRequestException("HTTP " + (int)response.StatusCode);
                string text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (text.Length > 1024 * 1024) throw new InvalidDataException("Answer too large.");
                return JObject.Parse(text);
            }
        }

        // asks roblox about a batch of experiences: one request for the names, one for the icon addresses, and an image only for an icon whose address changed, which is how roblox shows a new icon; an old icon stays on disk for the clips made with it
        private static void Fetch(List<string> batch)
        {
            string ids = string.Join(",", batch);
            var games = (GetJson("https://games.roblox.com/v1/games?universeIds=" + ids)["data"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            var thumbs = (GetJson("https://thumbnails.roblox.com/v1/games/icons?universeIds=" + ids + "&returnPolicy=PlaceHolder&size=150x150&format=Png&isCircular=false")["data"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            DateTime now = DateTime.UtcNow;
            foreach (string universe in batch)
            {
                var game = games.FirstOrDefault(g => g["id"]?.ToString() == universe);
                var thumb = thumbs.FirstOrDefault(t => t["targetId"]?.ToString() == universe);
                string name = (game?["name"]?.Type == JTokenType.String ? game["name"].Value<string>() : "").Trim();
                string url = thumb?["state"]?.ToString() == "Completed" ? thumb["imageUrl"]?.ToString() : null;
                Entry entry;
                lock (Gate) { if (!Cache.TryGetValue(universe, out entry)) Cache[universe] = entry = new Entry(); }
                if (name.Length > 0 && name.Length <= 160 && !name.Any(char.IsControl)) entry.Name = name;
                // an icon still in moderation, or an experience made private or deleted, keeps the last icon; it is asked about again after ten minutes, then twice as long each time it still has none, up to a day
                if (url == null)
                {
                    entry.Misses = Math.Min(entry.Misses + 1, 8);
                    entry.RetryAt = now + TimeSpan.FromMinutes(Math.Min(24 * 60, 10 << (entry.Misses - 1)));
                    lock (Gate) Due.Add(universe);
                    entry.Checked = now;
                    continue;
                }
                entry.Misses = 0;
                if (url != entry.Url || entry.Icon == null || !File.Exists(Path.Combine(GameCatalog.IconDir, IconId(universe, entry.Icon) + ".png")))
                {
                    entry.Icon = SaveIcon(universe, url);
                    entry.Url = url;
                }
                entry.Checked = now;
            }
            ClipStore.Update(GameCatalog.CustomFile, db =>
            {
                var cache = db[CacheKey] as JObject ?? new JObject();
                lock (Gate)
                    foreach (string universe in batch)
                    {
                        if (!Cache.TryGetValue(universe, out var entry) || cache.Count >= 1000 && cache[universe] == null) continue;
                        cache[universe] = new JObject { ["name"] = entry.Name, ["icon"] = entry.Icon, ["url"] = entry.Url, ["checked"] = entry.Checked };
                    }
                db[CacheKey] = cache;
                return true;
            }, 1024 * 1024);
        }

        // the picture only from the roblox image servers over https, decoded and drawn again so nothing but pixels is kept; named after a hash of its address, so a new icon is a new file and never replaces the one older clips show
        private static string SaveIcon(string universe, string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri source) || source.Scheme != Uri.UriSchemeHttps || !source.Host.EndsWith(".rbxcdn.com", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("No usable experience icon.");
            string version;
            using (var sha = System.Security.Cryptography.SHA256.Create())
                version = string.Concat(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(url)).Take(8).Select(b => b.ToString("x2")));
            Directory.CreateDirectory(GameCatalog.IconDir);
            if ((File.GetAttributes(GameCatalog.IconDir) & FileAttributes.ReparsePoint) != 0) throw new IOException("The icon folder is a link.");
            string target = Path.Combine(GameCatalog.IconDir, IconId(universe, version) + ".png");
            if (File.Exists(target)) return version;
            byte[] bytes;
            using (var response = Send(source))
            {
                if (!response.IsSuccessStatusCode || (response.Content.Headers.ContentLength ?? 0) > 1024 * 1024) throw new HttpRequestException("Icon unavailable.");
                bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                if (bytes.Length == 0 || bytes.Length > 1024 * 1024) throw new InvalidDataException("Icon too large.");
            }
            string part = target + "." + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                using (var input = new MemoryStream(bytes))
                using (var picture = Image.FromStream(input, false, true))
                using (var bitmap = new Bitmap(64, 64))
                using (var draw = Graphics.FromImage(bitmap))
                {
                    draw.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    draw.DrawImage(picture, new Rectangle(0, 0, 64, 64));
                    bitmap.Save(part, ImageFormat.Png);
                }
                File.Move(part, target);
            }
            finally { if (File.Exists(part)) File.Delete(part); }
            // the single icon file of the first version of this feature is replaced by the versioned ones
            string legacy = Path.Combine(GameCatalog.IconDir, "roblox-" + universe + ".png");
            if (File.Exists(legacy)) File.Delete(legacy);
            Log.Write("Roblox experience " + universe + " icon " + version + " saved");
            return version;
        }

        // clips of these experiences that have no icon of their own yet get the current one and the current name written into their tag, after which they keep them
        private static void Pin(List<string> universes)
        {
            var known = new Dictionary<string, Entry>(StringComparer.Ordinal);
            lock (Gate) foreach (string u in universes) if (Cache.TryGetValue(u, out var e) && e.Icon != null) known[u] = e;
            if (known.Count == 0) return;
            try
            {
                Clips.UpdateDb(db =>
                {
                    bool changed = false;
                    foreach (var row in db.Properties())
                    {
                        if (!(row.Value is JObject entry) || !(entry["capture"]?["experience"] is JObject experience) || experience["icon"] != null) continue;
                        string universe = experience["universe"]?.Type == JTokenType.String ? experience["universe"].Value<string>() : null;
                        if (universe == null || !known.TryGetValue(universe, out var e)) continue;
                        experience["icon"] = e.Icon;
                        experience["name"] = e.Name ?? "";
                        changed = true;
                    }
                    return changed;
                });
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is Newtonsoft.Json.JsonException) { Log.Write("Roblox icons not pinned: " + ex.GetType().Name); }
        }

        // what the sdk shows: the experience the logs say is open now, what is known, and whether lookups are waiting on roblox
        internal static JObject State()
        {
            var now = DateTime.UtcNow;
            var visit = During(now, now);
            Load();
            lock (Gate)
            {
                return new JObject
                {
                    ["ok"] = true,
                    ["current"] = visit == null ? (JToken)JValue.CreateNull() : new JObject { ["universe"] = visit.Universe, ["place"] = visit.Place, ["joinedUtc"] = visit.At.ToString("o") },
                    ["known"] = new JArray(Cache.Select(p => new JObject { ["universe"] = p.Key, ["name"] = p.Value.Name, ["icon"] = p.Value.Icon, ["checkedUtc"] = p.Value.Checked.ToString("o") })),
                    ["due"] = Due.Count,
                    ["blockedForSeconds"] = Math.Max(0, (int)(blockedUntil - now).TotalSeconds),
                    ["requestsLastMinute"] = Sent.Count(t => now - t <= TimeSpan.FromMinutes(1)),
                };
            }
        }
    }
}

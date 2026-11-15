using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ReplayKitHelper
{
    internal static class CatalogArtwork
    {
        private const int CacheLimit = 96;
        private const int MaxBytes = 192 * 1024;
        private const int DiskLimit = 256;
        private static readonly HttpClient Http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            SslProtocols = System.Security.Authentication.SslProtocols.Tls12
        })
        {
            Timeout = TimeSpan.FromSeconds(3)
        };
        private static readonly SemaphoreSlim Downloads = new SemaphoreSlim(4);
        private static readonly Provider Steam = new Provider();
        private static readonly Provider SteamStore = new Provider(500);
        private static readonly Provider SteamCommunity = new Provider(500);
        private static readonly Provider Discord = new Provider();
        private static readonly ConcurrentDictionary<string, Lazy<byte[]>> Pending =
            new ConcurrentDictionary<string, Lazy<byte[]>>(StringComparer.Ordinal);
        private static readonly object DiskGate = new object();
        private static readonly Regex CacheName = new Regex(
            @"\A(?:discord-[0-9]{1,24}-[a-f0-9]{32}\.png|steam-(?:square-)?[0-9]{1,10}\.jpg)\z", RegexOptions.Compiled);
        private static readonly Regex SteamIcon = new Regex(
            "class=\"apphub_AppIcon\"[^>]*>\\s*<img\\s+src=\"https://shared\\.(?:fastly|akamai)\\.steamstatic\\.com/community_assets/images/apps/([0-9]{1,10})/([a-f0-9]{40})\\.jpg\"",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Dictionary<string, (byte[] Bytes, DateTime Expires)> Cache =
            new Dictionary<string, (byte[], DateTime)>(StringComparer.Ordinal);

        internal static byte[] Get(string id, out string contentType)
        {
            contentType = null;
            if (!GameCatalog.ValidId(id)) return null;
            var game = GameCatalog.Get(id);
            if (game == null) return null;
            uint steam = game.SteamAppId;
            if (steam == 0 && id.StartsWith("steam-", StringComparison.Ordinal))
                uint.TryParse(id.Substring(6), out steam);
            var source = id.StartsWith("steam-", StringComparison.Ordinal) && steam != 0
                ? GameCatalog.BySteamId(steam) ?? game : game;
            if (source.IconHash == null && steam == 0) return null;
            byte[] bytes = null;
            if (steam != 0)
            {
                contentType = "image/jpeg";
                bytes = Artwork("steam-square-" + steam + ".jpg", contentType, () => FetchSteam(steam, source.SteamIconHash));
            }
            if (bytes == null && source.IconHash != null)
            {
                contentType = "image/png";
                string url = DiscordIconUrl(source.Id, source.IconHash, 64);
                bytes = Artwork("discord-" + source.Id + "-" + source.IconHash + ".png", contentType,
                    () => Fetch(url, "image/png", Discord));
            }
            return bytes;
        }

        private static string DiscordIconUrl(string id, string hash, int size) =>
            "https://cdn.discordapp.com/app-icons/" + Uri.EscapeDataString(id) + "/" + Uri.EscapeDataString(hash) + ".png?size=" + size;

        private static string SteamIconUrl(uint appId, string hash) =>
            "https://shared.fastly.steamstatic.com/community_assets/images/apps/" + appId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + hash + ".jpg";

        // where the art of a game can be seen on the web, for the discord presence, which shows images by address only: the icon discord keeps for a catalog game, else the steam icon, read from the store page when the catalog has no hash for it; null when the art exists only on this pc. it may wait on the network, so only the presence thread calls it
        internal static string PublicUrl(GameCatalog.Game game)
        {
            if (game == null) return null;
            if (game.IconHash != null) return DiscordIconUrl(game.Id, game.IconHash, 512);
            uint steam = game.SteamAppId;
            if (steam == 0 && game.Id.StartsWith("steam-", StringComparison.Ordinal)) uint.TryParse(game.Id.Substring(6), out steam);
            var source = steam != 0 ? GameCatalog.BySteamId(steam) ?? game : game;
            // a steam library game is a steam- id of its own, while the catalog entry with the same app id holds the discord icon
            if (source.IconHash != null) return DiscordIconUrl(source.Id, source.IconHash, 512);
            // a game a launcher named a little differently from the catalog, or one the catalog only knows by name
            var twin = source == game ? GameCatalog.ArtTwin(game.Name) : null;
            if (twin?.IconHash != null) return DiscordIconUrl(twin.Id, twin.IconHash, 512);
            if (steam == 0) return null;
            string hash = source.SteamIconHash ?? ScrapedSteamIconHash(steam);
            return hash != null ? SteamIconUrl(steam, hash) : null;
        }

        private static byte[] Artwork(string name, string type, Func<byte[]> download)
        {
            if (!CacheName.IsMatch(name)) return null;
            var work = Pending.GetOrAdd(name, _ => new Lazy<byte[]>(() =>
            {
                lock (Cache)
                    if (Cache.TryGetValue(name, out var cached) && cached.Expires > DateTime.UtcNow) return cached.Bytes;
                byte[] bytes = ReadOrDownload(name, type, download);
                lock (Cache)
                {
                    if (!Cache.ContainsKey(name) && Cache.Count >= CacheLimit)
                        Cache.Remove(Cache.OrderBy(entry => entry.Value.Expires).First().Key);
                    Cache[name] = (bytes, DateTime.UtcNow.AddMinutes(bytes == null ? 2 : 720));
                }
                return bytes;
            }));
            try { return work.Value; }
            finally
            {
                ((ICollection<KeyValuePair<string, Lazy<byte[]>>>)Pending)
                    .Remove(new KeyValuePair<string, Lazy<byte[]>>(name, work));
            }
        }

        private static byte[] ReadOrDownload(string name, string type, Func<byte[]> download)
        {
            string directory = Path.Combine(GameCatalog.IconDir, "catalog");
            string path = Path.Combine(directory, name);
            try
            {
                lock (DiskGate)
                {
                    if (PlainDirectory(GameCatalog.IconDir) && PlainDirectory(directory) && File.Exists(path))
                    {
                        var file = new FileInfo(path);
                        if ((file.Attributes & FileAttributes.ReparsePoint) == 0 && file.Length <= MaxBytes &&
                            file.LastWriteTimeUtc >= DateTime.UtcNow.AddDays(-7))
                        {
                            byte[] cached = File.ReadAllBytes(path);
                            if (IsImage(cached, type)) return cached;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            { Log.Write("Catalog artwork cache read failed: " + ex.GetType().Name); }

            if (!Downloads.Wait(750)) return null;
            byte[] bytes;
            try { bytes = download(); }
            finally { Downloads.Release(); }
            if (bytes == null) return null;
            try
            {
                lock (DiskGate)
                {
                    Directory.CreateDirectory(GameCatalog.IconDir);
                    if (!PlainDirectory(GameCatalog.IconDir)) return bytes;
                    Directory.CreateDirectory(directory);
                    if (!PlainDirectory(directory)) return bytes;
                    bool exists = File.Exists(path);
                    if (exists && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return bytes;
                    var files = new DirectoryInfo(directory).EnumerateFiles()
                        .Where(file => CacheName.IsMatch(file.Name) && (file.Attributes & FileAttributes.ReparsePoint) == 0)
                        .OrderBy(file => file.LastWriteTimeUtc).ToArray();
                    foreach (var file in files.Where(file => file.Name != name)
                        .Take(Math.Max(0, files.Length - DiskLimit + (exists ? 0 : 1)))) file.Delete();
                    string temporary = path + "." + Guid.NewGuid().ToString("N") + ".part";
                    try
                    {
                        File.WriteAllBytes(temporary, bytes);
                        if (exists) File.Replace(temporary, path, null);
                        else File.Move(temporary, path);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            { Log.Write("Catalog artwork cache write failed: " + ex.GetType().Name); }
            return bytes;
        }

        private static bool PlainDirectory(string directory) =>
            Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0;

        private static bool IsImage(byte[] bytes, string type)
        {
            if (type == "image/png")
                return bytes.Length >= 24 && bytes.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            return bytes.Length >= 4 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255;
        }

        private static string SteamIconHash(string html, uint appId)
        {
            var match = SteamIcon.Match(html);
            return match.Success && match.Groups[1].Value == appId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ? match.Groups[2].Value : null;
        }

        private static byte[] FetchSteam(uint appId, string hash)
        {
            hash = hash ?? ScrapedSteamIconHash(appId);
            if (hash == null) return null;
            return Fetch(SteamIconUrl(appId, hash), "image/jpeg", Steam);
        }

        private static readonly TimeSpan ScrapeRetry = TimeSpan.FromMinutes(30);
        private static readonly ConcurrentDictionary<uint, (string Hash, DateTime At)> Scraped = new ConcurrentDictionary<uint, (string, DateTime)>();

        // the icon hash of a steam game the catalog has none for, read from its store page and then its community page; a found hash is kept for the session and a miss is asked again after a while
        private static string ScrapedSteamIconHash(uint appId)
        {
            if (Scraped.TryGetValue(appId, out var known) && (known.Hash != null || DateTime.UtcNow - known.At < ScrapeRetry)) return known.Hash;
            string id = appId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string hash = null;
            byte[] page = Fetch("https://store.steampowered.com/app/" + id + "/", "text/html", SteamStore, prefixBytes: 128 * 1024);
            if (page != null) hash = SteamIconHash(Encoding.UTF8.GetString(page), appId);
            if (hash == null)
            {
                page = Fetch("https://steamcommunity.com/app/" + id, "text/html", SteamCommunity, prefixBytes: 64 * 1024);
                if (page != null) hash = SteamIconHash(Encoding.UTF8.GetString(page), appId);
            }
            Scraped[appId] = (hash, DateTime.UtcNow);
            return hash;
        }

        private static byte[] Fetch(string url, string type, Provider provider, int prefixBytes = 0)
        {
            if (!provider.Reserve()) return null;
            try
            {
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                using (var response = Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                    .GetAwaiter().GetResult())
                {
                    if ((int)response.StatusCode == 429 || response.StatusCode == HttpStatusCode.ServiceUnavailable)
                    {
                        provider.CoolDown(response.Headers.RetryAfter);
                        Log.Write("Catalog artwork cooling down " + new Uri(url).Host + " after HTTP " + (int)response.StatusCode);
                    }
                    if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != type ||
                        prefixBytes == 0 && response.Content.Headers.ContentLength > MaxBytes) return null;

                    // the app icon is near the top of the page, so skip the rest
                    var bytes = new byte[prefixBytes == 0 ? MaxBytes + 1 : prefixBytes];
                    using (timeout.Token.Register(response.Dispose))
                    using (var stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                    {
                        int total = 0;
                        while (total < bytes.Length)
                        {
                            int count = stream.ReadAsync(bytes, total, bytes.Length - total, timeout.Token)
                                .GetAwaiter().GetResult();
                            if (count == 0) break;
                            total += count;
                        }
                        if (prefixBytes == 0 && total > MaxBytes) return null;
                        Array.Resize(ref bytes, total);
                        return prefixBytes != 0 || IsImage(bytes, type) ? bytes : null;
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException ||
                ex is ObjectDisposedException || ex is IOException)
            {
                return null;
            }
        }

        private sealed class Provider
        {
            private readonly object gate = new object();
            private readonly TimeSpan spacing;
            private DateTime next, blockedUntil;

            internal Provider(int spacingMs = 250)
            {
                spacing = TimeSpan.FromMilliseconds(spacingMs);
            }

            internal bool Reserve()
            {
                TimeSpan delay;
                lock (gate)
                {
                    DateTime now = DateTime.UtcNow;
                    if (blockedUntil > now) return false;
                    delay = next > now ? next - now : TimeSpan.Zero;
                    if (delay > TimeSpan.FromMilliseconds(750)) return false;
                    next = now + delay + spacing;
                }
                if (delay > TimeSpan.Zero) Thread.Sleep(delay);
                lock (gate) return blockedUntil <= DateTime.UtcNow;
            }

            internal void CoolDown(System.Net.Http.Headers.RetryConditionHeaderValue retry)
            {
                lock (gate)
                {
                    DateTime now = DateTime.UtcNow;
                    TimeSpan wait = retry?.Delta ?? (retry?.Date?.UtcDateTime - now) ?? TimeSpan.FromMinutes(1);
                    if (wait < TimeSpan.FromSeconds(1)) wait = TimeSpan.FromSeconds(1);
                    DateTime until = wait >= DateTime.MaxValue - now ? DateTime.MaxValue : now + wait;
                    if (until > blockedUntil) blockedUntil = until;
                }
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // clip listing, the clips db (streamable url + compression history per filename), the ffprobe metadata index, favorites/sort ui state, and filename/path safety checks. ported from obs_replaykit helper modules/40_clips.ps1. clip_index_worker.ps1's repair pass is ClipIndexWorker.cs, run as an in-process Task from StartClipIndexRepairIfNeeded below instead of a spawned child process -- the Task gives a real completion callback, so the ps originals poll-based Update-ClipIndexRepairState (checking a childs pid on every call) has no equivalent here; ContinueWith replaces it directly.
    internal static class Clips
    {
        public static string GetSafeFilename(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string decoded = Uri.UnescapeDataString(raw);
            if (decoded.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            string name = Path.GetFileName(decoded).Trim();
            if (string.IsNullOrEmpty(name)) return null;
            string ext = Path.GetExtension(name).ToLowerInvariant();
            if (!Constants.ALLOWED_EXTS.Contains(ext)) return null;
            return name;
        }

        public sealed class SafeClipPath
        {
            public string Name;
            public string Full;
        }

        public static SafeClipPath GetSafeClipPath(string raw)
        {
            string name = GetSafeFilename(raw);
            if (name == null) return null;
            string root = Path.GetFullPath(AppConfig.GetClipDir());
            string full = Path.GetFullPath(Path.Combine(root, name));
            string prefix = root.EndsWith(Path.DirectorySeparatorChar.ToString()) ? root : root + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) return null;
            return new SafeClipPath { Name = name, Full = full };
        }

        // clips db (filename -> {url, uploaded_at, ...}) with retention expiry.
        public static string GetClipsDbCacheSignature()
        {
            var parts = new List<string> { "minute:" + (long)Math.Floor(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60.0) };
            string p = AppConfig.GetDbPath();
            try
            {
                var fi = new FileInfo(p);
                parts.Add(fi.Exists ? string.Format("{0}:{1}:{2}", p, fi.Length, fi.LastWriteTimeUtc.Ticks) : p + ":missing");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException)
            {
                parts.Add(p + ":error");
            }
            return string.Join("|", parts);
        }

        // Read-ClipsDb/Save-ClipsDb lock their whole body, not just the file i/o -- a cache-hit read returns the live cached JObject, so two callers touching it at once would be concurrent unsynchronized mutation, not just a stale-read risk.
        public static JObject ReadClipsDb()
        {
            lock (Server.State.ClipsMetaLock)
            {
                string sig = GetClipsDbCacheSignature();
                if (Server.State.ClipsDbCache != null && Server.State.ClipsDbCacheSig == sig) return Server.State.ClipsDbCache;

                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var db = new JObject();
                string path = AppConfig.GetDbPath();
                if (File.Exists(path))
                {
                    try
                    {
                        var parsed = JObject.Parse(File.ReadAllText(path));
                        foreach (var prop in parsed.Properties())
                        {
                            if (!(prop.Value is JObject v)) continue;

                            // compress-history cache is independent of streamable upload state -- a clip might be marked as compressed without ever having been uploaded. read those fields first so they survive even when theres no url entry.
                            JObject cmpEntry = null;
                            if (v["cmp_mode"] != null)
                            {
                                cmpEntry = new JObject
                                {
                                    ["cmp_mode"] = v["cmp_mode"].Value<string>(),
                                    ["cmp_mtime"] = v["cmp_mtime"]?.Value<long>() ?? 0L,
                                    ["cmp_ts"] = v["cmp_ts"]?.Value<long>() ?? 0L,
                                    ["cmp_pre"] = v["cmp_pre"]?.Value<long>() ?? 0L,
                                    // cmp_ver = 2 marks entries written by the current compression pipeline.
                                    ["cmp_ver"] = v["cmp_ver"]?.Value<int>() ?? 0,
                                };
                            }

                            if (v["url"] != null && v["uploaded_at"] != null)
                            {
                                long retSec = v["retention_days"] != null ? v["retention_days"].Value<int>() * 86400L : Constants.ANON_RETENTION_DAYS * 86400L;
                                if ((now - v["uploaded_at"].Value<long>()) < retSec)
                                {
                                    var entry = new JObject
                                    {
                                        ["url"] = v["url"].Value<string>(),
                                        ["uploaded_at"] = v["uploaded_at"].Value<long>(),
                                    };
                                    if (v["retention_days"] != null) entry["retention_days"] = v["retention_days"].Value<int>();
                                    // preserve transcode-state fields the background poller writes. without this theyd be dropped on read and the dock would never show the "processing on streamable" badge.
                                    if (v["shortcode"] != null) entry["shortcode"] = v["shortcode"].Value<string>();
                                    if (v["ready"] != null) entry["ready"] = v["ready"].Value<bool>();
                                    if (v["transcode_status"] != null) entry["transcode_status"] = v["transcode_status"].Value<int>();
                                    if (v["transcode_percent"] != null) entry["transcode_percent"] = v["transcode_percent"].Value<int>();
                                    if (v["failed"] != null) entry["failed"] = v["failed"].Value<bool>();
                                    if (cmpEntry != null)
                                    {
                                        entry["cmp_mode"] = cmpEntry["cmp_mode"];
                                        entry["cmp_mtime"] = cmpEntry["cmp_mtime"];
                                        entry["cmp_ts"] = cmpEntry["cmp_ts"];
                                        entry["cmp_pre"] = cmpEntry["cmp_pre"];
                                        entry["cmp_ver"] = cmpEntry["cmp_ver"];
                                    }
                                    db[prop.Name] = entry;
                                }
                            }
                            else if (cmpEntry != null)
                            {
                                // compress-only entry: clip has never been uploaded but its compress history is known. keep it.
                                db[prop.Name] = cmpEntry;
                            }
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
                    {
                        Log.Write("Read-ClipsDb error: " + ex.Message);
                    }
                }
                Server.State.ClipsDbCache = db;
                Server.State.ClipsDbCacheSig = sig;
                return db;
            }
        }

        public static void SaveClipsDb(JObject db)
        {
            lock (Server.State.ClipsMetaLock)
            {
                AppConfig.WriteUtf8(AppConfig.GetDbPath(), db.ToString(Formatting.Indented));
                Server.State.ClipsDbCache = db;
                Server.State.ClipsDbCacheSig = GetClipsDbCacheSignature();
            }
        }

        // shortcode/ready/transcodeStatus/transcodePercent are optional so the upload watcher can stamp the initial "streamable is still processing this" state in the same write that records the url -- the ps original had two separate code paths for this (Mark-Uploaded here, plus a second inline reimplementation in Start-UploadResultWatcher that forgot to preserve the cmp_* fields below); consolidating onto this one, correct path is a real fix, not just a style choice.
        public static void MarkUploaded(string name, string url, string shortcode = null, bool? ready = null, int? transcodeStatus = null, int? transcodePercent = null)
        {
            // holds the lock across the whole read-mutate-save so a concurrent MarkUploaded/MarkCompressed cant interleave between the read and the save -- ReadClipsDb/SaveClipsDb re-enter the same lock internally, which is safe since a c# lock is reentrant per-thread.
            lock (Server.State.ClipsMetaLock)
            {
                var db = ReadClipsDb();
                // tag entries with the retention that applied at upload time so filtering doesnt change retroactively when signing in/out later. anonymous uploads stay short-retention even after signing in.
                var entry = new JObject
                {
                    ["url"] = url,
                    ["uploaded_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ["retention_days"] = Constants.GetEffectiveRetentionDays(),
                };
                if (shortcode != null) entry["shortcode"] = shortcode;
                if (ready.HasValue) entry["ready"] = ready.Value;
                if (transcodeStatus.HasValue) entry["transcode_status"] = transcodeStatus.Value;
                if (transcodePercent.HasValue) entry["transcode_percent"] = transcodePercent.Value;
                // preserve the compress-history cache fields if they were already there. otherwise an upload would wipe the fact that this file had already been ffprobed for its compress marker.
                if (db[name] is JObject prev)
                {
                    if (prev["cmp_mode"] != null) entry["cmp_mode"] = prev["cmp_mode"].Value<string>();
                    if (prev["cmp_mtime"] != null) entry["cmp_mtime"] = prev["cmp_mtime"].Value<long>();
                    if (prev["cmp_ts"] != null) entry["cmp_ts"] = prev["cmp_ts"].Value<long>();
                    if (prev["cmp_pre"] != null) entry["cmp_pre"] = prev["cmp_pre"].Value<long>();
                    if (prev["cmp_ver"] != null) entry["cmp_ver"] = prev["cmp_ver"].Value<int>();
                }
                db[name] = entry;
                SaveClipsDb(db);
                AppConfig.ClearClipsCache();
            }
        }

        // called once the compress-overwrite encode + atomic replace succeeds. stores the new mode + the freshly-written files mtime as the cache key, plus the timestamp + pre-compress size for ui purposes, so later /clips polls dont need to re-probe the file.
        public static void MarkCompressed(string name, string mode, long mtimeTicks, long preBytes)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            if (mode != "fast" && mode != "slow") return;
            lock (Server.State.ClipsMetaLock)
            {
                var db = ReadClipsDb();
                var entry = db[name] as JObject ?? new JObject();
                entry["cmp_mode"] = mode;
                entry["cmp_mtime"] = mtimeTicks;
                entry["cmp_ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                entry["cmp_pre"] = preBytes;
                // cmp_ver = 2 -- written by the v2 (dynamic encoder / size-guarded) compress pipeline. a cache hit is refused without this field, which forces v1-era entries to re-probe their mp4 atom.
                entry["cmp_ver"] = 2;
                db[name] = entry;
                SaveClipsDb(db);
                AppConfig.ClearClipsCache();
            }
        }

        // drop the streamable link + its transcode state from a clips_db entry (the "remove link" action on a failed / unwanted upload). keeps the cmp_* compress-history fields; removes the whole entry if nothing else was on it. returns false when there was no entry to touch.
        public static bool RemoveLink(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            lock (Server.State.ClipsMetaLock)
            {
                var db = ReadClipsDb();
                if (!(db[name] is JObject entry)) return false;
                foreach (var k in new[] { "url", "uploaded_at", "retention_days", "shortcode", "ready", "transcode_status", "transcode_percent", "failed", "transcode_error" })
                    entry.Remove(k);
                if (entry.Count > 0) db[name] = entry;
                else db.Remove(name);
                SaveClipsDb(db);
                AppConfig.ClearClipsCache();
                return true;
            }
        }

        // copy a clip file to "<name> (copy).<ext>" (or " (copy 2)", " (copy 3)" ...) in the same folder, keeping the source mtime so it sorts right next to the original. the copy starts fresh -- no clips_db entry is carried over, so it has no link / favorite / compress marker.
        public static JObject DuplicateClip(SafeClipPath src)
        {
            if (src == null || !File.Exists(src.Full))
                return new JObject { ["ok"] = false, ["message"] = "Clip not found" };
            try
            {
                string dir = Path.GetDirectoryName(src.Full);
                string baseName = Path.GetFileNameWithoutExtension(src.Full);
                string ext = Path.GetExtension(src.Full);
                string dest = Path.Combine(dir, baseName + " (copy)" + ext);
                for (int i = 2; File.Exists(dest); i++) dest = Path.Combine(dir, baseName + " (copy " + i + ")" + ext);
                File.Copy(src.Full, dest);
                try { File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(src.Full)); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                AppConfig.ClearClipsCache();
                return new JObject { ["ok"] = true, ["name"] = Path.GetFileName(dest) };
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new JObject { ["ok"] = false, ["message"] = ex.Message };
            }
        }

        public static string GetClipIndexSignature()
        {
            string path = AppConfig.GetClipIndexPath();
            try
            {
                var fi = new FileInfo(path);
                return fi.Exists ? string.Format("{0}:{1}:{2}", path, fi.Length, fi.LastWriteTimeUtc.Ticks) : path + ":missing";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException)
            {
                return path + ":error";
            }
        }

        public static JObject ReadClipIndex()
        {
            lock (Server.State.ClipsMetaLock)
            {
                string sig = GetClipIndexSignature();
                if (Server.State.ClipIndexCache != null && Server.State.ClipIndexCacheSig == sig) return Server.State.ClipIndexCache;

                var map = new JObject();
                string path = AppConfig.GetClipIndexPath();
                if (File.Exists(path))
                {
                    try
                    {
                        string raw = File.ReadAllText(path);
                        if (!string.IsNullOrWhiteSpace(raw) && JObject.Parse(raw)["clips"] is JObject clips)
                        {
                            foreach (var prop in clips.Properties()) map[prop.Name] = prop.Value;
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
                    {
                        Log.Write("Read-ClipIndex error: " + ex.Message);
                    }
                }
                Server.State.ClipIndexCache = map;
                Server.State.ClipIndexCacheSig = sig;
                return map;
            }
        }

        private static bool TestClipIndexEntryCurrent(JToken entry, FileInfo file)
        {
            if (entry == null) return false;
            var size = entry["size"];
            var mtimeTicks = entry["mtimeTicks"];
            if (size == null || mtimeTicks == null) return false;
            return size.Value<long>() == file.Length && mtimeTicks.Value<long>() == file.LastWriteTimeUtc.Ticks;
        }

        private static JObject GetCurrentClipIndexEntry(FileInfo file, JObject index)
        {
            if (file == null || index == null) return null;
            var entry = index[file.Name] as JObject;
            return TestClipIndexEntryCurrent(entry, file) ? entry : null;
        }

        private static void AddClipIndexFields(JObject item, JObject entry)
        {
            if (item == null || entry == null) return;
            var duration = entry["duration"];
            if (duration != null && duration.Value<double>() > 0) item["duration"] = duration.Value<double>();
            foreach (var field in new[] { "width", "height" })
            {
                var value = entry[field];
                if (value != null && value.Value<int>() > 0) item[field] = value.Value<int>();
            }
            var fps = entry["fps"];
            if (fps != null && fps.Value<double>() > 0) item["fps"] = fps.Value<double>();
            string codec = entry["codec"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(codec)) item["codec"] = codec;
            string tag = entry["tag"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(tag)) item["tag"] = tag;
            item["indexed"] = true;
        }

        private static bool TestClipNeedsIndexRepair(FileInfo file, JObject index)
        {
            if (file == null) return false;
            var entry = GetCurrentClipIndexEntry(file, index);
            if (entry == null) return true;
            var duration = entry["duration"];
            string codec = entry["codec"]?.Value<string>();
            if (duration != null && duration.Value<double>() > 0 && !string.IsNullOrWhiteSpace(codec)) return false;
            var failedAt = entry["failedAt"];
            if (failedAt != null && (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - failedAt.Value<long>()) < 21600) return false;
            return true;
        }

        private static string ResolveClipIndexFfprobe()
        {
            try
            {
                string found = Compression.FindToolInClipDirs("ffprobe.exe");
                if (!string.IsNullOrEmpty(found)) return found;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            try
            {
                string ffprobe = Compression.GetHelperCapabilities()?["ffprobe"]?.Value<string>();
                if (!string.IsNullOrEmpty(ffprobe)) return ffprobe;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            return "";
        }

        // claims ClipIndexRepairRunning under the lock before doing any slow work (ffprobe resolution, which can hit Get-HelperCapabilitiess multi-second cold-cache probe), then does that slow work lock-free so it cant hold up every other ClipsMetaLock user. the Task.ContinueWith below resets the claim on completion, so a bail-out before the task actually starts must reset it directly.
        public static void StartClipIndexRepairIfNeeded(List<FileInfo> files, JObject index)
        {
            DateTime now = DateTime.UtcNow;
            bool claimed = false;
            lock (Server.State.ClipsMetaLock)
            {
                if (Server.State.ClipIndexRepairRunning) return;

                bool needsRepair = Server.State.ClipIndexRepairQueued;
                if (!needsRepair)
                {
                    foreach (var file in files)
                    {
                        if (TestClipNeedsIndexRepair(file, index)) { needsRepair = true; break; }
                    }
                }
                if (!needsRepair) return;

                if (!Server.State.ClipIndexRepairQueued && (now - Server.State.ClipIndexRepairAt).TotalSeconds < 30) return;

                Server.State.ClipIndexRepairRunning = true;
                Server.State.ClipIndexRepairAt = now;
                Server.State.ClipIndexRepairQueued = false;
                claimed = true;
            }
            if (!claimed) return;

            string ffprobe = ResolveClipIndexFfprobe();
            if (string.IsNullOrWhiteSpace(ffprobe) || !File.Exists(ffprobe))
            {
                lock (Server.State.ClipsMetaLock) { Server.State.ClipIndexRepairRunning = false; }
                return;
            }

            string clipDir = AppConfig.GetClipDir();
            string indexPath = AppConfig.GetClipIndexPath();
            int maxFiles = Constants.MAX_CLIPS > 0 ? Math.Max(Constants.MAX_CLIPS, 1) : 20000;

            Task.Run(() => ClipIndexWorker.Run(clipDir, indexPath, ffprobe, Constants.ALLOWED_EXTS, maxFiles))
                .ContinueWith(_ =>
                {
                    lock (Server.State.ClipsMetaLock)
                    {
                        Server.State.ClipIndexRepairRunning = false;
                        Server.State.ClipIndexCache = null;
                        Server.State.ClipIndexCacheSig = "";
                        AppConfig.ClearClipsCache();
                    }
                });
        }

        public static string NormalizeClipSort(string sort)
        {
            switch ((sort ?? "").ToLowerInvariant())
            {
                case "oldest": return "oldest";
                case "shortest": return "shortest";
                case "longest": return "longest";
                case "smallest": return "smallest";
                case "biggest": return "biggest";
                default: return "newest";
            }
        }

        private static string NormalizeFavoriteClipName(JToken value)
        {
            if (value == null) return "";
            string text = value.ToString().Trim();
            if (string.IsNullOrWhiteSpace(text)) return "";
            string name = Path.GetFileName(text).Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 260) return "";
            foreach (char ch in Path.GetInvalidFileNameChars())
            {
                if (name.IndexOf(ch) >= 0) return "";
            }
            string ext = Path.GetExtension(name).ToLowerInvariant();
            return Constants.ALLOWED_EXTS.Contains(ext) ? name : "";
        }

        private static List<string> NormalizeFavoriteClipNames(JToken names)
        {
            var outList = new List<string>();
            var seen = new HashSet<string>();
            IEnumerable<JToken> items = names as JArray ?? (names != null ? new[] { names } : Enumerable.Empty<JToken>());
            foreach (var value in items)
            {
                string name = NormalizeFavoriteClipName(value);
                if (string.IsNullOrWhiteSpace(name)) continue;
                string key = name.ToLowerInvariant();
                if (!seen.Add(key)) continue;
                outList.Add(name);
                if (outList.Count >= 10000) break;
            }
            return outList;
        }

        public static JObject ReadClipUiState()
        {
            var state = new JObject { ["favorites"] = new JArray(), ["sort"] = "newest" };
            string path = AppConfig.GetClipUiStatePath();
            if (!File.Exists(path)) return state;
            try
            {
                string raw = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(raw)) return state;
                var parsed = JObject.Parse(raw);
                state["favorites"] = new JArray(NormalizeFavoriteClipNames(parsed["favorites"]));
                state["sort"] = NormalizeClipSort(parsed["sort"]?.ToString());
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
            {
                Log.Write("Read-ClipUiState error: " + ex.Message);
            }
            return state;
        }

        public static JObject SaveClipUiState(JObject state)
        {
            var safe = new JObject
            {
                ["favorites"] = new JArray(NormalizeFavoriteClipNames(state?["favorites"])),
                ["sort"] = NormalizeClipSort(state?["sort"]?.ToString()),
            };
            string path = AppConfig.GetClipUiStatePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            AppConfig.WriteUtf8(path, safe.ToString(Formatting.Indented));
            return safe;
        }

        public static JObject GetClipUiStatePayload()
        {
            string path = AppConfig.GetClipUiStatePath();
            return new JObject { ["ok"] = true, ["exists"] = File.Exists(path), ["state"] = ReadClipUiState() };
        }

        public static JObject SaveClipUiStateFromJson(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) throw new InvalidOperationException("Clip state body is required.");
            JObject incoming;
            try { incoming = JObject.Parse(body); }
            catch (JsonException) { throw new InvalidOperationException("Invalid clip state."); }
            return new JObject { ["ok"] = true, ["state"] = SaveClipUiState(incoming) };
        }

        private static double GetDurationSortValue(JObject clip)
        {
            var duration = clip["duration"];
            return (duration != null && duration.Value<double>() > 0) ? duration.Value<double>() : double.PositiveInfinity;
        }
        private static int GetDurationKnownRank(JObject clip)
        {
            var duration = clip["duration"];
            return (duration != null && duration.Value<double>() > 0) ? 0 : 1;
        }
        private static double GetDurationDescendingValue(JObject clip)
        {
            var duration = clip["duration"];
            return (duration != null && duration.Value<double>() > 0) ? duration.Value<double>() : 0.0;
        }

        // matches powershells default Sort-Object string comparison for the name tie-breaker: culture-aware, case-insensitive.
        private static readonly StringComparer NameComparer = StringComparer.CurrentCultureIgnoreCase;

        public static List<JObject> SortClipsForPage(List<JObject> items, string sort)
        {
            string safeSort = NormalizeClipSort(sort);
            switch (safeSort)
            {
                case "oldest":
                    return items.OrderBy(i => i["mtime"].Value<long>()).ThenBy(i => i["name"].Value<string>(), NameComparer).ToList();
                case "smallest":
                    return items.OrderBy(i => i["size"].Value<long>()).ThenByDescending(i => i["mtime"].Value<long>()).ThenBy(i => i["name"].Value<string>(), NameComparer).ToList();
                case "biggest":
                    return items.OrderByDescending(i => i["size"].Value<long>()).ThenByDescending(i => i["mtime"].Value<long>()).ThenBy(i => i["name"].Value<string>(), NameComparer).ToList();
                case "shortest":
                    return items.OrderBy(GetDurationKnownRank).ThenBy(GetDurationSortValue).ThenByDescending(i => i["mtime"].Value<long>()).ThenBy(i => i["name"].Value<string>(), NameComparer).ToList();
                case "longest":
                    return items.OrderBy(GetDurationKnownRank).ThenByDescending(GetDurationDescendingValue).ThenByDescending(i => i["mtime"].Value<long>()).ThenBy(i => i["name"].Value<string>(), NameComparer).ToList();
                default:
                    return items.OrderByDescending(i => i["mtime"].Value<long>()).ThenBy(i => i["name"].Value<string>(), NameComparer).ToList();
            }
        }

        // clip listing -- cached by directory/db signature so unchanged popup polls dont re-enumerate the folder or re-serialize a large json list.
        public static string GetClipsCacheSignature(string root)
        {
            var parts = new List<string>();
            try
            {
                var dir = new DirectoryInfo(root);
                parts.Add(dir.Exists ? "dir:" + dir.LastWriteTimeUtc.Ticks : "dir:missing");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException)
            {
                parts.Add("dir:error");
            }
            foreach (var p in new[] { AppConfig.GetDbPath(), AppConfig.GetClipIndexPath() })
            {
                try
                {
                    var fi = new FileInfo(p);
                    parts.Add(fi.Exists ? string.Format("{0}:{1}:{2}", p, fi.Length, fi.LastWriteTimeUtc.Ticks) : p + ":missing");
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException)
                {
                    parts.Add(p + ":error");
                }
            }
            return string.Join("|", parts);
        }

        // holds ClipsMetaLock across the whole enumeration, not just the ReadClipsDb/ReadClipIndex calls -- both return live cached objects and this walks the folder doing many scattered reads into them, so a concurrent MarkUploaded/SaveClipsDb writing to that same live object mid-enumeration would be real corruption, not just staleness. only runs on a clips-cache miss, so its not the hot per-poll path.
        public static List<JObject> GetClipsListUncached()
        {
            List<JObject> items;
            List<FileInfo> eligibleFiles;
            JObject index;
            lock (Server.State.ClipsMetaLock)
            {
                string root = AppConfig.GetClipDir();
                if (!Directory.Exists(root)) return new List<JObject>();
                var db = ReadClipsDb();
                index = ReadClipIndex();
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                items = new List<JObject>();
                eligibleFiles = new List<FileInfo>();

                IEnumerable<FileInfo> files;
                try { files = new DirectoryInfo(root).EnumerateFiles(); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return new List<JObject>(); }

                foreach (var fi in files)
                {
                    string ext = fi.Extension.ToLowerInvariant();
                    if (!Constants.ALLOWED_EXTS.Contains(ext)) continue;
                    // skip in-flight worker temp files. trim/compress workers write under %temp%, but cross-volume finalizes briefly stage a _replaykit_finalize_ sidecar in the clip folder. older _compress_tmp_ / _trim_tmp_ names are filtered too in case a previous version left one behind.
                    if (fi.Name.StartsWith("_replaykit_", StringComparison.OrdinalIgnoreCase) ||
                        fi.Name.StartsWith("_compress_tmp_", StringComparison.OrdinalIgnoreCase) ||
                        fi.Name.StartsWith("_trim_tmp_", StringComparison.OrdinalIgnoreCase)) continue;

                    eligibleFiles.Add(fi);
                    var item = new JObject
                    {
                        ["name"] = fi.Name,
                        ["size"] = fi.Length,
                        ["mtime"] = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds(),
                    };
                    var indexEntry = GetCurrentClipIndexEntry(fi, index);
                    if (indexEntry != null) AddClipIndexFields(item, indexEntry);

                    // resolve compression state only from the helper-maintained cache during normal listing. probing every clip with ffprobe makes the clips popup wait seconds on large folders; compression routes still inspect the selected file server-side before modifying it.
                    long fileMtimeTicks = fi.LastWriteTimeUtc.Ticks;
                    string cmpMode = "";
                    long cmpTs = 0, cmpPre = 0;
                    var entry = db[fi.Name] as JObject;
                    // cache hit requires both the mtime match and a cmp_ver=2 stamp. without the version check, clips compressed by the v1 pipeline would still report "already compressed" forever.
                    int entryVer = entry?["cmp_ver"]?.Value<int>() ?? 0;
                    if (entry != null && entry["cmp_mtime"] != null && entry["cmp_mtime"].Value<long>() == fileMtimeTicks && entryVer == 2)
                    {
                        cmpMode = entry["cmp_mode"]?.Value<string>() ?? "";
                        if (entry["cmp_ts"] != null) cmpTs = entry["cmp_ts"].Value<long>();
                        if (entry["cmp_pre"] != null) cmpPre = entry["cmp_pre"].Value<long>();
                    }
                    if (cmpMode == "fast" || cmpMode == "slow")
                    {
                        item["cmp_mode"] = cmpMode;
                        if (cmpTs > 0) item["cmp_ts"] = cmpTs;
                        if (cmpPre > 0) item["cmp_pre"] = cmpPre;
                    }

                    if (entry != null && entry["url"] != null && entry["uploaded_at"] != null)
                    {
                        if ((now - entry["uploaded_at"].Value<long>()) < Constants.GetEntryRetentionSec(entry))
                        {
                            item["streamable_url"] = entry["url"].Value<string>();
                            item["uploaded_at"] = entry["uploaded_at"].Value<long>();
                            item["retention_days"] = entry["retention_days"] != null ? entry["retention_days"].Value<int>() : Constants.ANON_RETENTION_DAYS;
                            // missing transcode-state fields read as ready -- these fields are never written as null, so absent reliably means absent.
                            if (entry["ready"] != null) item["ready"] = entry["ready"].Value<bool>();
                            if (entry["transcode_status"] != null) item["transcode_status"] = entry["transcode_status"].Value<int>();
                            if (entry["transcode_percent"] != null) item["transcode_percent"] = entry["transcode_percent"].Value<int>();
                        }
                    }
                    items.Add(item);
                }
            } // ClipsMetaLock released here

            // deliberately called after ClipsMetaLock is released -- a c# lock is reentrant per-thread, so nesting the call inside the block above would not free the lock for other threads while this claims-under-lock-then-spawns-outside-it function does its slow work. doing that once in the ps original silently pinned a whole ffprobe.exe launch behind ClipsMetaLock, blocking every other pool thread that needed it for as long as the spawn took.
            StartClipIndexRepairIfNeeded(eligibleFiles, index);

            var sorted = items.OrderByDescending(i => i["mtime"].Value<long>()).ThenBy(i => i["name"].Value<string>(), NameComparer).ToList();
            if (Constants.MAX_CLIPS > 0 && sorted.Count > Constants.MAX_CLIPS) sorted = sorted.Take(Constants.MAX_CLIPS).ToList();
            return sorted;
        }

        public static List<JObject> GetClipsList()
        {
            DateTime now = DateTime.UtcNow;
            string root = AppConfig.GetClipDir();
            string sig = GetClipsCacheSignature(root);
            if (Server.State.ClipsCacheBody != null && Server.State.ClipsCacheSig == sig &&
                (now - Server.State.ClipsCacheAt).TotalMilliseconds < Constants.CLIPS_CACHE_MAX_AGE_MS)
            {
                return Server.State.ClipsCacheBody;
            }
            var body = GetClipsListUncached();
            // the fields below are meant to be seen together as one cache "generation" -- lock the writes so a concurrent fast-path reader (deliberately lock-free above since its the hot per-poll path) never sees, say, a fresh body paired with a stale sig.
            lock (Server.State.ClipsMetaLock)
            {
                Server.State.ClipsCacheBody = body;
                string json = JsonConvert.SerializeObject(body, Formatting.None);
                Server.State.ClipsCacheJson = string.IsNullOrWhiteSpace(json) ? "[]" : json;
                Server.State.ClipsCacheSig = sig;
                Server.State.ClipsCacheVersion = sig;
                Server.State.ClipsCacheAt = now;
            }
            return body;
        }

        public static string GetClipsListJson()
        {
            GetClipsList();
            return string.IsNullOrWhiteSpace(Server.State.ClipsCacheJson) ? "[]" : Server.State.ClipsCacheJson;
        }

        public static string GetClipsPageJson(int offset, int limit, string sort = "newest")
        {
            string safeSort = NormalizeClipSort(sort);
            var items = SortClipsForPage(GetClipsList(), safeSort);
            int total = items.Count;
            int safeOffset = Math.Max(0, Math.Min(offset, total));
            int safeLimit = limit > 0 ? Math.Min(limit, Constants.CLIPS_PAGE_LIMIT_MAX) : Math.Min(Math.Max(total, 1), Constants.CLIPS_PAGE_LIMIT_MAX);
            var page = new List<JObject>();
            if (total > 0 && safeOffset < total)
            {
                int end = Math.Min(total - 1, safeOffset + safeLimit - 1);
                page = items.GetRange(safeOffset, end - safeOffset + 1);
            }
            int linked = items.Count(c => !string.IsNullOrEmpty(c["streamable_url"]?.Value<string>()));
            var payload = new JObject
            {
                ["version"] = Server.State.ClipsCacheVersion + "|sort:" + safeSort,
                ["total"] = total,
                ["linked"] = linked,
                ["offset"] = safeOffset,
                ["limit"] = safeLimit,
                ["sort"] = safeSort,
                ["indexing"] = new JObject
                {
                    ["running"] = Server.State.ClipIndexRepairRunning,
                    ["queued"] = Server.State.ClipIndexRepairQueued,
                },
                ["clips"] = new JArray(page),
            };
            return payload.ToString(Formatting.None);
        }

        public static string GetClipsByNameJson(IEnumerable<string> names, string sort = "newest")
        {
            string safeSort = NormalizeClipSort(sort);
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in names ?? Enumerable.Empty<string>())
            {
                if (wanted.Count >= 500) break;
                string decoded;
                try { decoded = Uri.UnescapeDataString(raw ?? ""); }
                catch (Exception ex) when (ex is FormatException || ex is ArgumentException) { continue; }
                if (string.IsNullOrWhiteSpace(decoded) || decoded.IndexOfAny(new[] { '\\', '/' }) >= 0) continue;
                string name = GetSafeFilename(decoded);
                if (!string.IsNullOrEmpty(name)) wanted.Add(name);
            }
            var items = new List<JObject>();
            if (wanted.Count > 0)
            {
                foreach (var clip in GetClipsList())
                {
                    string clipName = clip["name"]?.Value<string>();
                    if (!string.IsNullOrEmpty(clipName) && wanted.Contains(clipName)) items.Add(clip);
                }
            }
            var sorted = SortClipsForPage(items, safeSort);
            var payload = new JObject
            {
                ["version"] = Server.State.ClipsCacheVersion + "|sort:" + safeSort + "|names",
                ["total"] = sorted.Count,
                ["sort"] = safeSort,
                ["clips"] = new JArray(sorted),
            };
            return payload.ToString(Formatting.None);
        }

        public sealed class LatestClip
        {
            public string Name;
            public string Full;
            public long Size;
        }

        public static LatestClip FindLatestClip()
        {
            var clips = GetClipsListUncached();
            if (clips == null || clips.Count == 0) return null;
            var selected = GetSafeClipPath(clips[0]["name"]?.Value<string>());
            if (selected == null) return null;
            var fi = new FileInfo(selected.Full);
            return new LatestClip { Name = selected.Name, Full = selected.Full, Size = fi.Length };
        }
    }
}

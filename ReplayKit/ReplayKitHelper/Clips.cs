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
    // clip listing, the clips db (streamable url + compression history per filename), the ffprobe metadata index, favorites/sort ui state, and filename/path safety checks; the index repair runs as an in-process task (ClipIndexWorker)
    internal static class Clips
    {
        public static string GetSafeFilename(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            if (raw.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            string name = Path.GetFileName(raw).Trim();
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

        public static SafeClipPath GetSafeClipPath(string raw, string folder = null)
        {
            string name = GetSafeFilename(raw);
            if (name == null) return null;
            string root = Path.GetFullPath(folder ?? AppConfig.GetClipDir());
            string full = Path.GetFullPath(Path.Combine(root, name));
            string prefix = root.EndsWith(Path.DirectorySeparatorChar.ToString()) ? root : root + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) return null;
            return new SafeClipPath { Name = name, Full = full };
        }

        // what the editor reads a clip by: a clip of the folder, or a video picked from outside it; only routes that read go through this, never the ones that delete or rename
        public static SafeClipPath GetEditorSource(string raw) => VideoImports.IsRef(raw) ? VideoImports.Resolve(raw) : GetSafeClipPath(raw);

        public sealed class DeleteResult
        {
            // the http status the delete route answers with: 200 deleted, 404 not there, 409 another operation holds the clip, 500 the delete failed
            public int Status;
            public string Name;
            public string Message = "";
        }

        // deletes one clip for good, the way the clips page does, so the storage card can delete a list of them with the same checks: the name has to resolve inside the clip folder, and a clip that an upload, trim or compression holds is left alone
        public static DeleteResult DeleteClip(string rawName)
        {
            return DeleteClip(GetSafeClipPath(rawName));
        }

        internal static DeleteResult DeleteClip(SafeClipPath selected, long? size = null, DateTime? writeTime = null)
        {
            if (selected == null || !File.Exists(selected.Full)) return new DeleteResult { Status = 404, Message = "Clip not found" };
            string requestId = UploadState.NewRequestId();
            using (var reservation = JobCoordinator.TryReserve(requestId, selected.Name, selected.Full, "delete", out string busy))
            {
                if (reservation == null) return new DeleteResult { Status = 409, Name = selected.Name, Message = busy };
                try
                {
                    JobCoordinator.Commit(requestId, () =>
                    {
                        var safe = GetSafeClipPath(selected.Name, Path.GetDirectoryName(selected.Full));
                        var current = new FileInfo(selected.Full);
                        if (safe == null || !string.Equals(safe.Full, selected.Full, StringComparison.OrdinalIgnoreCase) ||
                            !current.Exists || (size.HasValue && current.Length != size.Value) ||
                            (writeTime.HasValue && current.LastWriteTimeUtc != writeTime.Value))
                            throw new IOException("The selected clip changed; review the library again.");
                        File.Delete(selected.Full);
                    });
                    UpdateDb(db => db.Remove(selected.Name), Path.GetDirectoryName(selected.Full));
                    EditDrafts.Forget(selected.Full);
                    return new DeleteResult { Status = 200, Name = selected.Name };
                }
                catch (Exception ex) { return new DeleteResult { Status = 500, Name = selected.Name, Message = ex.Message }; }
            }
        }

        // clips db (filename -> {url, uploaded_at, ...}) with retention expiry.
        public static string GetClipsDbCacheSignature()
        {
            var parts = new List<string> { "folder:" + AppConfig.GetClipDir().ToUpperInvariant(), "minute:" + (long)Math.Floor(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60.0) };
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

        // read-ClipsDb/Save-ClipsDb lock their whole body, not just the file i/o -- a cache-hit read returns the live cached JObject, so two callers touching it at once would be concurrent unsynchronized mutation, not just a stale-read risk.
        public static JObject ReadClipsDb()
        {
            lock (Server.State.ClipsMetaLock)
            {
                string sig = GetClipsDbCacheSignature();
                if (Server.State.ClipsDbCache != null && Server.State.ClipsDbCacheSig == sig) return (JObject)Server.State.ClipsDbCache.DeepClone();

                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                string path = AppConfig.GetDbPath();
                var db = new JObject();
                try
                {
                    var parsed = ClipStore.ReadFolder(path, AppConfig.GetClipDir(), data => data);
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
                            else if (cmpEntry != null) db[prop.Name] = cmpEntry;
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
                Server.State.ClipsDbCache = db;
                Server.State.ClipsDbCacheSig = sig;
                return (JObject)db.DeepClone();
            }
        }

        public static bool UpdateDb(Func<JObject, bool> change, string folder = null)
        {
            lock (Server.State.ClipsMetaLock)
            {
                bool result = ClipStore.UpdateFolder(AppConfig.GetDbPath(), folder ?? AppConfig.GetClipDir(), change);
                Server.State.ClipsDbCache = null;
                Server.State.ClipsDbCacheSig = "";
                AppConfig.ClearClipsCache();
                return result;
            }
        }

        // the transcode fields are optional so an upload records its url and the initial still-processing state in one write, keeping the cmp_* fields
        public static void MarkUploaded(string name, string url, string shortcode = null, bool? ready = null, int? transcodeStatus = null, int? transcodePercent = null, string sourcePath = null, int? retentionDays = null)
        {
            // UpdateDb coordinates this mutation with detached pollers as well as helper requests.
            UpdateDb(db =>
            {
                // tag entries with the retention that applied at upload time so filtering doesnt change retroactively when signing in/out later. anonymous uploads stay short-retention even after signing in.
                var entry = new JObject
                {
                    ["url"] = url,
                    ["uploaded_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ["retention_days"] = retentionDays ?? Constants.GetEffectiveRetentionDays(),
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

                return true;
            }, sourcePath == null ? null : Path.GetDirectoryName(sourcePath));
        }

        // called once the compress-overwrite encode + atomic replace succeeds. stores the new mode + the freshly-written files mtime as the cache key, plus the timestamp + pre-compress size for ui purposes, so later /clips polls dont need to re-probe the file.
        public static void MarkCompressed(string name, string mode, long mtimeTicks, long preBytes, string sourcePath = null)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            if (mode != "fast" && mode != "slow") return;
            UpdateDb(db =>
            {
                var entry = db[name] as JObject ?? new JObject();
                entry["cmp_mode"] = mode;
                entry["cmp_mtime"] = mtimeTicks;
                entry["cmp_ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                entry["cmp_pre"] = preBytes;
                // cmp_ver = 2 -- written by the v2 (dynamic encoder / size-guarded) compress pipeline. a cache hit is refused without this field, which forces v1-era entries to re-probe their mp4 atom.
                entry["cmp_ver"] = 2;
                db[name] = entry;

                return true;
            }, sourcePath == null ? null : Path.GetDirectoryName(sourcePath));
        }

        // a clip replaced by a fresh encode of an edit is no longer the compressed file, though its restored mtime would still match the cached marker
        public static void ClearCompressed(string name, string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            UpdateDb(db =>
            {
                if (!(db[name] is JObject entry)) return false;
                bool changed = false;
                foreach (var k in new[] { "cmp_mode", "cmp_mtime", "cmp_ts", "cmp_pre", "cmp_ver" })
                    changed |= entry.Remove(k);
                if (entry.Count == 0) db.Remove(name);
                return changed;
            }, Path.GetDirectoryName(sourcePath));
        }

        // drop the streamable link + its transcode state from a clips_db entry (the "remove link" action on a failed / unwanted upload). keeps the cmp_* compress-history fields; removes the whole entry if nothing else was on it. returns false when there was no entry to touch.
        public static bool RemoveLink(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            return UpdateDb(db =>
            {
                if (!(db[name] is JObject entry)) return false;
                foreach (var k in new[] { "url", "uploaded_at", "retention_days", "shortcode", "ready", "transcode_status", "transcode_percent", "failed", "transcode_error" })
                    entry.Remove(k);
                if (entry.Count > 0) db[name] = entry;
                else db.Remove(name);

                return true;
            });
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
                string requestId = UploadState.NewRequestId();
                using var reservation = JobCoordinator.TryReserve(requestId, src.Name, src.Full, "duplicate", out string busy, dest);
                if (reservation == null) return new JObject { ["ok"] = false, ["busy"] = true, ["message"] = busy };
                JobCoordinator.Commit(requestId, () => File.Copy(src.Full, dest));
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
            if (!string.Equals(entry["path"]?.Value<string>(), file.FullName, StringComparison.OrdinalIgnoreCase)) return false;
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
                string ffprobe = Compression.GetToolPaths()?["ffprobe"]?.Value<string>();
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
                .ContinueWith(t =>
                {
                    lock (Server.State.ClipsMetaLock)
                    {
                        Server.State.ClipIndexRepairRunning = false;
                        Server.State.ClipIndexCache = null;
                        Server.State.ClipIndexCacheSig = "";
                        AppConfig.ClearClipsCache();
                    }
                    // only the freshest few -- an old backlog doesnt need proactive warming (nobody is about to edit a clip from last week the moment it gets reindexed), and capping this keeps a first-ever big clip folder from firing a burst of ffprobe/shell-thumbnail work all at once.
                    if (t.Status == TaskStatus.RanToCompletion)
                        foreach (var name in t.Result.Take(3)) WarmClipMetadata(name);
                });
        }

        // fires the keyframe scan and grid thumbnail for a clip in the background, before the user ever opens it -- GetClipKeyframeTimes and GetCachedThumbnail both dedupe/cache by the files own path+size+mtime, so this can never race a live trim-modal open for the same clip: whichever call gets there first starts the one shared job/file, the other just sees it already in flight (or already on disk) and uses that.
        private static void WarmClipMetadata(string name)
        {
            var source = GetSafeClipPath(name);
            if (source == null || !File.Exists(source.Full)) return;
            try { Trim.GetClipKeyframeTimes(name); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            Task.Run(() =>
            {
                try
                {
                    var fi = new FileInfo(source.Full);
                    if (fi.Exists) Media.GetCachedThumbnail(source, fi);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            });
            // the film strip and the audio tracks of the editor as well, at a low priority so a game running meanwhile does not notice; a clip written to a moment ago is still being recorded, and what is made of it now would be of no use once it is finished
            try
            {
                if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(source.Full)).TotalSeconds > 30)
                {
                    ClipFilmstrip.Describe(name, true);
                    ClipAudioTracks.Describe(name, true);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
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

        public static JObject ReadClipUiState(string folder = null)
        {
            var state = new JObject { ["favorites"] = new JArray(), ["sort"] = "newest" };
            string path = AppConfig.GetClipUiStatePath();
            if (!File.Exists(path)) return state;
            try
            {
                var parsed = ClipStore.ReadFolder(path, folder ?? AppConfig.GetClipDir(), data => data);
                state["favorites"] = new JArray(NormalizeFavoriteClipNames(parsed["favorites"]));
                state["sort"] = NormalizeClipSort(parsed["sort"]?.ToString());
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
            {
                Log.Write("Read-ClipUiState error: " + ex.Message);
            }
            return state;
        }

        public static JObject SaveClipUiState(JObject state, string folderKey)
        {
            lock (SettingsStore.Gate)
            {
                string folder = AppConfig.GetClipDir();
                if (!string.Equals(folderKey, ClipStore.FolderKey(folder), StringComparison.Ordinal))
                    throw new InvalidOperationException("The clip folder changed. Reload the favorites before saving.");
                var safe = new JObject
                {
                    ["favorites"] = new JArray(NormalizeFavoriteClipNames(state?["favorites"])),
                    ["sort"] = NormalizeClipSort(state?["sort"]?.ToString()),
                };
                string path = AppConfig.GetClipUiStatePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                ClipStore.UpdateFolder(path, folder, data =>
                {
                    if (!string.Equals(state?.Value<string>("revision"), ClipUiRevision(data), StringComparison.Ordinal))
                        throw new InvalidOperationException("Favorites changed in another view. Reload them before saving.");
                    data["favorites"] = safe["favorites"].DeepClone();
                    data["sort"] = safe["sort"].DeepClone();
                    return true;
                });
                return safe;
            }
        }

        private static string ClipUiRevision(JObject state)
        {
            var normalized = new JObject
            {
                ["favorites"] = new JArray(NormalizeFavoriteClipNames(state?["favorites"])),
                ["sort"] = NormalizeClipSort(state?["sort"]?.ToString()),
            };
            using (var hash = System.Security.Cryptography.SHA256.Create())
                return Convert.ToBase64String(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(normalized.ToString(Formatting.None))));
        }

        // the numbers the home page shows next to its recent clips row: how many clips there are, how much space they take, which days this week they were saved on, and how much room is left on the drive they live on. built from the same cached snapshot the clips list serves, so polling it is cheap.
        public static JObject GetHomeSummaryPayload()
        {
            var clips = GetClipsList();
            long bytes = 0;
            DateTime today = DateTime.Now.Date;
            int todayIndex = ((int)today.DayOfWeek + 6) % 7;
            DateTime monday = today.AddDays(-todayIndex);
            var week = new int[7];
            foreach (var clip in clips)
            {
                bytes += clip["size"]?.Value<long>() ?? 0;
                long mtime = clip["mtime"]?.Value<long>() ?? 0;
                if (mtime <= 0) continue;
                int dayOffset = (int)(DateTimeOffset.FromUnixTimeSeconds(mtime).LocalDateTime.Date - monday).TotalDays;
                if (dayOffset >= 0 && dayOffset < 7) week[dayOffset]++;
            }
            string root = AppConfig.GetClipDir();
            // -1 = unknown (a network path, or a drive that is not ready); the page hides the free-space line instead of showing a made-up number
            long freeBytes = -1;
            string drive = "";
            try
            {
                var info = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root)));
                freeBytes = info.AvailableFreeSpace;
                drive = info.Name.TrimEnd('\\');
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException) { }
            return new JObject
            {
                ["ok"] = true,
                ["clipCount"] = clips.Count,
                ["clipBytes"] = bytes,
                ["freeBytes"] = freeBytes,
                ["drive"] = drive,
                ["clipDirExists"] = Directory.Exists(root),
                ["week"] = new JArray(week),
                ["todayIndex"] = todayIndex,
            };
        }

        public static JObject GetClipUiStatePayload()
        {
            lock (SettingsStore.Gate)
            {
                string path = AppConfig.GetClipUiStatePath();
                string folder = AppConfig.GetClipDir();
                bool exists = File.Exists(path);
                var state = ReadClipUiState(folder);
                return new JObject { ["ok"] = true, ["exists"] = exists, ["folderKey"] = ClipStore.FolderKey(folder), ["revision"] = ClipUiRevision(state), ["state"] = state };
            }
        }

        public static JObject SaveClipUiStateFromJson(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) throw new InvalidOperationException("Clip state body is required.");
            JObject incoming;
            try { incoming = JObject.Parse(body); }
            catch (JsonException) { throw new InvalidOperationException("Invalid clip state."); }
            var state = SaveClipUiState(incoming, incoming.Value<string>("folderKey"));
            return new JObject { ["ok"] = true, ["revision"] = ClipUiRevision(state), ["state"] = state };
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

        // culture-aware, case-insensitive name tie-breaker
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
            // the folder itself is part of it, so a list read from one folder can never be served for another whose timestamp happens to match
            var parts = new List<string> { "path:" + (root ?? "").Trim().ToLowerInvariant() };
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

            // called after ClipsMetaLock is released -- the lock is reentrant, so calling this inside the block above would keep every other thread waiting while it resolves ffprobe and starts the repair
            StartClipIndexRepairIfNeeded(eligibleFiles, index);

            var sorted = items.OrderByDescending(i => i["mtime"].Value<long>()).ThenBy(i => i["name"].Value<string>(), NameComparer).ToList();
            if (Constants.MAX_CLIPS > 0 && sorted.Count > Constants.MAX_CLIPS) sorted = sorted.Take(Constants.MAX_CLIPS).ToList();
            return sorted;
        }

        private sealed class Snapshot
        {
            internal readonly List<JObject> Body;
            internal readonly string Json, Signature, Version, Folder;
            internal readonly DateTime Created = DateTime.UtcNow;
            internal readonly int Linked;
            private readonly Dictionary<string, List<JObject>> sorted = new Dictionary<string, List<JObject>>();

            internal Snapshot(List<JObject> body, string signature, string folder)
            {
                Body = body;
                Signature = signature;
                Folder = folder;
                Json = JsonConvert.SerializeObject(body, Formatting.None);
                // the same list has the same version whenever it was rebuilt, so a page that keeps what it read does not take a rebuild for a change and start over
                using (var sha = System.Security.Cryptography.SHA1.Create())
                    Version = BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(Json))).Replace("-", "").ToLowerInvariant();
                Linked = body.Count(c => !string.IsNullOrEmpty(c["streamable_url"]?.Value<string>()));
            }

            internal List<JObject> Sorted(string sort)
            {
                lock (sorted)
                {
                    if (!sorted.TryGetValue(sort, out var result)) sorted[sort] = result = SortClipsForPage(Body, sort);
                    return result;
                }
            }
        }

        private static Snapshot cached;
        private static long generation;

        internal static void InvalidateCache()
        {
            lock (Server.State.ClipsMetaLock) { generation++; cached = null; }
        }

        private static Snapshot GetSnapshot()
        {
            string clipDir = AppConfig.GetClipDir();
            string signature = GetClipsCacheSignature(clipDir);
            long observed;
            lock (Server.State.ClipsMetaLock)
            {
                if (cached != null && cached.Signature == signature &&
                    (DateTime.UtcNow - cached.Created).TotalMilliseconds < Constants.CLIPS_CACHE_MAX_AGE_MS) return cached;
                observed = generation;
            }
            var snapshot = new Snapshot(GetClipsListUncached(), signature, FolderId(clipDir));
            lock (Server.State.ClipsMetaLock)
            {
                if (observed == generation) cached = snapshot;
            }
            return snapshot;
        }

        // which clip folder a list was read from, so a page that keeps clips between polls drops all of them (favorites, drafts, counts) when the folder is switched; a hash, so the path itself is not handed out
        private static string FolderId(string dir)
        {
            string normalized = (dir ?? "").Trim().TrimEnd('\\', '/').ToLowerInvariant();
            using (var sha = System.Security.Cryptography.SHA1.Create())
                return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(normalized))).Replace("-", "").Substring(0, 12).ToLowerInvariant();
        }

        public static List<JObject> GetClipsList() => GetSnapshot().Body;
        public static string GetClipsListJson() => GetSnapshot().Json;

        public static string GetClipsPageJson(int offset, int limit, string sort = "newest")
        {
            string safeSort = NormalizeClipSort(sort);
            var snapshot = GetSnapshot();
            var items = snapshot.Sorted(safeSort);
            int total = items.Count;
            int safeOffset = Math.Max(0, Math.Min(offset, total));
            int safeLimit = limit > 0 ? Math.Min(limit, Constants.CLIPS_PAGE_LIMIT_MAX) : Math.Min(Math.Max(total, 1), Constants.CLIPS_PAGE_LIMIT_MAX);
            var page = new List<JObject>();
            if (total > 0 && safeOffset < total)
            {
                int end = Math.Min(total - 1, safeOffset + safeLimit - 1);
                page = items.GetRange(safeOffset, end - safeOffset + 1);
            }
            int linked = snapshot.Linked;
            var payload = new JObject
            {
                ["version"] = snapshot.Version + "|sort:" + safeSort,
                ["folder"] = snapshot.Folder,
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
                ["clips"] = SerializeShared(page),
            };
            return payload.ToString(Formatting.None);
        }

        // the clip objects belong to the shared snapshot that other requests read at the same time; putting them into a JArray would re-parent them, so they are written out as json instead
        private static JRaw SerializeShared(List<JObject> clips) => new JRaw(JsonConvert.SerializeObject(clips, Formatting.None));

        public static string GetClipsByNameJson(IEnumerable<string> names, string sort = "newest")
        {
            var snapshot = GetSnapshot();
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
                foreach (var clip in snapshot.Body)
                {
                    string clipName = clip["name"]?.Value<string>();
                    if (!string.IsNullOrEmpty(clipName) && wanted.Contains(clipName)) items.Add(clip);
                }
            }
            var sorted = SortClipsForPage(items, safeSort);
            var payload = new JObject
            {
                ["version"] = snapshot.Version + "|sort:" + safeSort + "|names",
                ["folder"] = snapshot.Folder,
                ["total"] = sorted.Count,
                ["sort"] = safeSort,
                ["clips"] = SerializeShared(sorted),
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

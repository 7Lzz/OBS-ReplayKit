using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // the numbers and actions behind the storage card of the clean layout: what the clips take, what is only temporary, the limit that was set, and the two ways to get space back (compressing the biggest clips, deleting the oldest ones after a review)
    internal static class Storage
    {
        // the same line the low disk space banner of the home page draws
        private const long LowSpaceBytes = 5L * 1024 * 1024 * 1024;
        private const long BytesPerGb = 1024L * 1024 * 1024;

        // the folder scans behind the temporary and thumbnail numbers are not free, and the plugin polls the summary every few seconds
        private static readonly object CacheLock = new object();
        private static DateTime _cacheAt = DateTime.MinValue;
        private static long _tempBytes;
        private static long _thumbBytes;

        // 1024-based like the size column of explorer: "352 MB", "99.7 GB", "1.25 TB"
        public static string FormatBytes(long bytes)
        {
            if (bytes < 0) return "";
            double v = bytes;
            if (v < 1024) return ((long)v).ToString(CultureInfo.InvariantCulture) + " B";
            v /= 1024;
            if (v < 1024) return Math.Round(v).ToString("0", CultureInfo.InvariantCulture) + " KB";
            v /= 1024;
            if (v < 1024) return (v >= 100 ? Math.Round(v).ToString("0", CultureInfo.InvariantCulture) : v.ToString("0.#", CultureInfo.InvariantCulture)) + " MB";
            v /= 1024;
            if (v < 1024) return v.ToString("0.#", CultureInfo.InvariantCulture) + " GB";
            v /= 1024;
            return v.ToString("0.##", CultureInfo.InvariantCulture) + " TB";
        }

        // the leftovers the temporary files number counts: never a file touched a moment ago, since an atomic write in progress leaves its sidecar there for a few milliseconds
        private static readonly TimeSpan MinLeftoverAge = TimeSpan.FromMinutes(10);

        private static List<string> LeftoverFiles()
        {
            DateTime cutoff = DateTime.UtcNow - MinLeftoverAge;
            return UploadState.EnumerateStaleTempFiles(false).Where(file =>
            {
                try { return File.GetLastWriteTimeUtc(file) < cutoff; }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return false; }
            }).ToList();
        }

        private static long SizeOfFiles(IEnumerable<string> files)
        {
            long total = 0;
            foreach (var file in files)
            {
                try { total += new FileInfo(file).Length; }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
            return total;
        }

        private static long SizeOfFolder(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return 0;
                return SizeOfFiles(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList());
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return 0; }
        }

        private static void RefreshFolderSizes(bool fresh)
        {
            lock (CacheLock)
            {
                if (!fresh && (DateTime.UtcNow - _cacheAt).TotalSeconds < 60) return;
                // the files kept to open clips quickly in the editor are temporary files too, next to what a stopped job left behind
                _tempBytes = SizeOfFiles(LeftoverFiles()) + ClipCache.TotalBytes();
                _thumbBytes = SizeOfFolder(Constants.THUMB_DIR);
                _cacheAt = DateTime.UtcNow;
            }
        }

        private static bool HasActiveJob()
        {
            return (UploadState.GetUploadStatusSnapshot()["activeJobs"]?.Value<int>() ?? 0) > 0;
        }

        // everything the rail widget and the card draw; fresh scans the temporary and thumbnail folders again instead of trusting the last scan
        public static JObject GetSummaryPayload(bool fresh)
        {
            var clips = Clips.GetClipsList();
            long clipBytes = 0;
            foreach (var clip in clips) clipBytes += clip["size"]?.Value<long>() ?? 0;
            RefreshFolderSizes(fresh);
            long tempBytes, thumbBytes;
            lock (CacheLock) { tempBytes = _tempBytes; thumbBytes = _thumbBytes; }

            // -1 = unknown (a network path, or a drive that is not ready); the card hides the line instead of showing a made-up number
            long freeBytes = -1, totalBytes = -1;
            string drive = "";
            string root = AppConfig.GetClipDir();
            try
            {
                var info = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root)));
                freeBytes = info.AvailableFreeSpace;
                totalBytes = info.TotalSize;
                drive = info.Name.TrimEnd('\\');
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException) { }

            var compress = CompressSnapshot();
            long limitBytes = ReplaykitSettings.GetStorageLimitGb() * BytesPerGb;
            long overBytes = limitBytes > 0 && clipBytes > limitBytes ? clipBytes - limitBytes : 0;
            bool low = freeBytes >= 0 && freeBytes < LowSpaceBytes;
            // the ring: how much of the limit the clips fill, or how much of the drive they fill when there is no limit
            double fraction = limitBytes > 0 ? (double)clipBytes / limitBytes : totalBytes > 0 ? (double)clipBytes / totalBytes : 0;
            fraction = Math.Max(0, Math.Min(1, fraction));

            return new JObject
            {
                ["ok"] = true,
                ["clipCount"] = clips.Count,
                ["clipBytes"] = clipBytes,
                ["tempBytes"] = tempBytes,
                ["thumbBytes"] = thumbBytes,
                ["freeBytes"] = freeBytes,
                ["totalBytes"] = totalBytes,
                ["drive"] = drive,
                ["clipDirExists"] = Directory.Exists(root),
                ["limitGb"] = limitBytes / BytesPerGb,
                ["limitBytes"] = limitBytes,
                ["overBytes"] = overBytes,
                ["state"] = overBytes > 0 ? "over" : low ? "low" : "ok",
                ["fraction"] = fraction,
                ["usedText"] = FormatBytes(clipBytes),
                ["limitText"] = limitBytes > 0 ? "of " + FormatBytes(limitBytes) : "No Limit",
                ["freeText"] = FormatBytes(freeBytes),
                ["tempText"] = FormatBytes(tempBytes),
                ["thumbText"] = FormatBytes(thumbBytes),
                ["overText"] = FormatBytes(overBytes),
                ["busy"] = HasActiveJob(),
                // what the compress list of the card starts out as every time it opens
                ["skipFavorites"] = ReplaykitSettings.GetCompressSkipFavorites(),
                // the ring in the rail shows a compression as it runs
                ["compressing"] = compress.Value<bool>("running"),
                ["compressPercent"] = compress.Value<int?>("percent") ?? 0,
                ["compressText"] = compress.Value<bool>("running") ? Math.Min(compress.Value<int>("total"), compress.Value<int>("done") + compress.Value<int>("failed") + 1) + "/" + compress.Value<int>("total") : "",
            };
        }

        // empties the temporary files: the ones kept to open clips quickly in the editor, which are made again from the clips so nothing is lost but a little time on the next open, and what a stopped upload or compression left behind. while a job is running its files are in use, so only the editor files go then
        public static JObject ClearTemp()
        {
            long freed = ClipCache.Clear(out int removed);
            if (!HasActiveJob())
            {
                foreach (var file in LeftoverFiles())
                {
                    try
                    {
                        long length = new FileInfo(file).Length;
                        File.Delete(file);
                        freed += length;
                        removed++;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                }
            }
            RefreshFolderSizes(true);
            return new JObject { ["ok"] = true, ["removed"] = removed, ["freedBytes"] = freed, ["freedText"] = FormatBytes(freed) };
        }

        private sealed class SelectedClip
        {
            public Clips.SafeClipPath Path;
            public long Size;
            public DateTime WriteTime;
        }

        private static SelectedClip SelectClip(string name, string folder, long size)
        {
            var selected = Clips.GetSafeClipPath(name, folder);
            if (selected == null) throw new IOException("The selected clip is no longer available.");
            var file = new FileInfo(selected.Full);
            if (!file.Exists || file.Length != size) throw new IOException("The selected clip changed; review the library again.");
            return new SelectedClip { Path = selected, Size = size, WriteTime = file.LastWriteTimeUtc };
        }

        private sealed class TrimPlan
        {
            public readonly List<SelectedClip> Victims = new List<SelectedClip>();
            public long Bytes;
            public long Over;
            // the favourites hold the rest: even with every other clip gone the library would still be over the limit
            public bool Insufficient;
            public string Token = "";
        }

        // the oldest clips first, favourites never, until the library is back under the limit
        private static TrimPlan BuildTrimPlan()
        {
            var plan = new TrimPlan();
            string folder = Path.GetFullPath(AppConfig.GetClipDir());
            long limitBytes = ReplaykitSettings.GetStorageLimitGb() * BytesPerGb;
            if (limitBytes <= 0) return plan;
            var clips = Clips.GetClipsList();
            long total = 0;
            foreach (var clip in clips) total += clip["size"]?.Value<long>() ?? 0;
            plan.Over = Math.Max(0, total - limitBytes);
            if (plan.Over == 0) return plan;

            var favorites = FavoriteNames();

            var candidates = clips
                .Where(c => !string.IsNullOrEmpty(c["name"]?.Value<string>()) && !favorites.Contains(c["name"].Value<string>()))
                .OrderBy(c => c["mtime"]?.Value<long>() ?? 0)
                .ThenBy(c => c["name"].Value<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var clip in candidates)
            {
                if (plan.Bytes >= plan.Over) break;
                long size = clip["size"]?.Value<long>() ?? 0;
                plan.Victims.Add(SelectClip(clip["name"].Value<string>(), folder, size));
                plan.Bytes += size;
            }
            plan.Insufficient = plan.Bytes < plan.Over;
            if (!string.Equals(folder, Path.GetFullPath(AppConfig.GetClipDir()), StringComparison.OrdinalIgnoreCase))
                throw new IOException("The clip folder changed; review the library again.");
            using (var sha = SHA256.Create())
            {
                var text = folder.ToUpperInvariant() + "|" + limitBytes.ToString(CultureInfo.InvariantCulture) + "\n" +
                    string.Join("\n", plan.Victims.Select(v => v.Path.Full.ToUpperInvariant() + "|" + v.Size.ToString(CultureInfo.InvariantCulture) + "|" + v.WriteTime.Ticks.ToString(CultureInfo.InvariantCulture)));
                plan.Token = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
            }
            return plan;
        }

        public static JObject GetTrimPlanPayload()
        {
            var plan = BuildTrimPlan();
            return new JObject
            {
                ["ok"] = true,
                ["count"] = plan.Victims.Count,
                ["bytes"] = plan.Bytes,
                ["bytesText"] = FormatBytes(plan.Bytes),
                ["overBytes"] = plan.Over,
                ["insufficient"] = plan.Insufficient,
                ["token"] = plan.Token,
            };
        }

        // deletes what the review showed and nothing else: the plan is built again, and a library that changed since the review (a new clip, a favourite, another limit) is refused so the user reviews the new list
        public static JObject ApplyTrim(string token)
        {
            if (string.IsNullOrEmpty(token))
                return new JObject { ["ok"] = false, ["message"] = "Review the clips to delete first." };
            var plan = BuildTrimPlan();
            if (plan.Victims.Count == 0 || !string.Equals(plan.Token, token, StringComparison.Ordinal))
                return new JObject { ["ok"] = false, ["changed"] = true, ["message"] = "Your library changed while you were looking. Review it again." };
            int deleted = 0, failed = 0;
            long freed = 0;
            foreach (var victim in plan.Victims)
            {
                var result = Clips.DeleteClip(victim.Path, victim.Size, victim.WriteTime);
                if (result.Status == 200) { deleted++; freed += victim.Size; }
                else failed++;
            }
            RefreshFolderSizes(true);
            return new JObject { ["ok"] = true, ["deleted"] = deleted, ["failed"] = failed, ["freedBytes"] = freed, ["freedText"] = FormatBytes(freed) };
        }

        private static HashSet<string> FavoriteNames()
        {
            var favorites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var favoriteList = Clips.GetClipUiStatePayload()["state"]?["favorites"] as JArray;
            if (favoriteList != null) foreach (var f in favoriteList) favorites.Add(f.Value<string>() ?? "");
            return favorites;
        }

        // -- compressing clips from the card: the biggest clips are offered, and the ones ticked are compressed one after the other with the worker of the clips page

        // nothing smaller than this is offered, the saving would not be worth an encode
        private const long MinCompressBytes = 50L * 1024 * 1024;
        private const int OfferedClips = 8;
        private const int MaxBatchClips = 50;
        // what a compression is expected to save when the machine has none behind it yet to go by: about half, which is the low end of what the encoders of the worker do on game footage
        private const double TypicalSavedFraction = 0.5;

        private sealed class CompressBatch
        {
            public readonly List<SelectedClip> Clips = new List<SelectedClip>();
            public long TotalBytes;
            // the sizes the finished clips had before they were compressed, so the share done is by bytes and one long clip is not counted like a short one
            public long FinishedBytes;
            public int Done, Failed;
            public long SavedBytes;
            public string Current = "";
            public long CurrentBytes;
            public int CurrentPercent;
            public bool Running, Cancelled;
            public string Error = "";
            // tells the card one run from another, and how long ago this one ended, so a result that is days old is not shown as news
            public readonly long Id = DateTime.UtcNow.Ticks;
            public DateTime FinishedAt = DateTime.MinValue;
        }

        private static readonly object CompressGate = new object();
        private static CompressBatch _compress;

        // the biggest clips that compressing still helps: not the ones already at the smallest size, nothing tiny, and favourites only when the card asks for them what the compressions done here already saved, as the share of the size the clips had before: the best guess for the next ones. only clips that carry the size they had then count, and the runs at the smallest setting are what is offered now, so they are what is looked at when there are enough of them
        private static double SavedFractionFromHistory(List<JObject> clips, out int samples, out bool fromHistory)
        {
            var smaller = new List<double>();
            var any = new List<double>();
            foreach (var c in clips)
            {
                long pre = c["cmp_pre"]?.Value<long>() ?? 0;
                long now = c["size"]?.Value<long>() ?? 0;
                if (pre <= 0 || now <= 0 || now >= pre) continue;
                double saved = 1.0 - (double)now / pre;
                any.Add(saved);
                if ((c["cmp_mode"]?.Value<string>() ?? "") == "slow") smaller.Add(saved);
            }
            var basis = smaller.Count >= 3 ? smaller : any;
            samples = basis.Count;
            fromHistory = samples > 0;
            if (!fromHistory) return TypicalSavedFraction;
            basis.Sort();
            return Math.Max(0.1, Math.Min(0.9, basis[basis.Count / 2]));
        }

        public static JObject GetCompressCandidates(bool includeFavorites, int limit)
        {
            if (limit <= 0) limit = OfferedClips;
            limit = Math.Min(limit, MaxBatchClips);
            var everything = Clips.GetClipsList();
            double savedFraction = SavedFractionFromHistory(everything, out int samples, out bool fromHistory);
            var favorites = includeFavorites ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : FavoriteNames();
            var eligible = everything.Where(c =>
            {
                string name = c["name"]?.Value<string>();
                if (string.IsNullOrEmpty(name) || favorites.Contains(name)) return false;
                if ((c["cmp_mode"]?.Value<string>() ?? "") == "slow") return false;
                return (c["size"]?.Value<long>() ?? 0) >= MinCompressBytes;
            }).OrderByDescending(c => c["size"]?.Value<long>() ?? 0).ToList();
            long eligibleBytes = 0;
            foreach (var c in eligible) eligibleBytes += c["size"]?.Value<long>() ?? 0;
            var offered = new JArray();
            foreach (var c in eligible.Take(limit))
            {
                long size = c["size"]?.Value<long>() ?? 0;
                string compressedMode = c["cmp_mode"]?.Value<string>() ?? "";
                // a clip that was compressed once at the faster setting has already given up part of what it can, so half as much is expected of it
                long estimate = (long)(size * savedFraction * (compressedMode == "fast" ? 0.5 : 1.0));
                offered.Add(new JObject { ["name"] = c["name"], ["size"] = size, ["sizeText"] = FormatBytes(size), ["compressed"] = compressedMode, ["estimateBytes"] = estimate });
            }
            return new JObject
            {
                ["ok"] = true, ["clips"] = offered, ["eligible"] = eligible.Count, ["eligibleBytes"] = eligibleBytes,
                ["eligibleText"] = FormatBytes(eligibleBytes), ["favoritesIncluded"] = includeFavorites,
                ["limit"] = limit, ["savedFraction"] = savedFraction, ["estimateSamples"] = samples, ["estimateFromHistory"] = fromHistory,
            };
        }

        public static JObject StartCompress(JArray names)
        {
            if (names == null || names.Count == 0)
                return new JObject { ["ok"] = false, ["message"] = "Pick at least one clip." };
            string folder = Path.GetFullPath(AppConfig.GetClipDir());
            var known = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var clip in Clips.GetClipsList())
            {
                string name = clip["name"]?.Value<string>();
                if (!string.IsNullOrEmpty(name)) known[name] = clip;
            }
            var batch = new CompressBatch();
            foreach (var token in names.Take(MaxBatchClips))
            {
                string name = token?.Type == JTokenType.String ? token.Value<string>() : null;
                if (string.IsNullOrEmpty(name) || !known.TryGetValue(name, out var clip)) continue;
                if ((clip["cmp_mode"]?.Value<string>() ?? "") == "slow") continue;
                if (batch.Clips.Any(c => string.Equals(c.Path.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
                long size = clip["size"]?.Value<long>() ?? 0;
                batch.Clips.Add(SelectClip(clip["name"].Value<string>(), folder, size));
                batch.TotalBytes += size;
            }
            if (batch.Clips.Count == 0)
                return new JObject { ["ok"] = false, ["message"] = "None of those clips can be compressed any further." };
            if (!string.Equals(folder, Path.GetFullPath(AppConfig.GetClipDir()), StringComparison.OrdinalIgnoreCase))
                return new JObject { ["ok"] = false, ["message"] = "The clip folder changed; select the clips again." };
            lock (CompressGate)
            {
                if (_compress != null && _compress.Running)
                    return new JObject { ["ok"] = false, ["message"] = "A compression is already running." };
                if (HasActiveJob())
                    return new JObject { ["ok"] = false, ["message"] = "Wait for the running upload, trim or compression to finish, or cancel it first." };
                batch.Running = true;
                _compress = batch;
            }
            Task.Run(() => RunCompress(batch));
            return new JObject { ["ok"] = true, ["count"] = batch.Clips.Count };
        }

        // stops after the clip that is being encoded: that clip is cancelled the way the clips page cancels one, and the rest are left alone
        public static JObject CancelCompress()
        {
            lock (CompressGate)
            {
                if (_compress == null || !_compress.Running)
                    return new JObject { ["ok"] = false, ["message"] = "Nothing is being compressed." };
                _compress.Cancelled = true;
            }
            return new JObject { ["ok"] = true };
        }

        private static JObject CompressSnapshot()
        {
            lock (CompressGate)
            {
                var b = _compress;
                if (b == null) return new JObject { ["started"] = false, ["running"] = false, ["percent"] = 0, ["total"] = 0, ["done"] = 0, ["failed"] = 0 };
                double fraction = b.TotalBytes > 0 ? (b.FinishedBytes + b.CurrentBytes * (b.CurrentPercent / 100.0)) / b.TotalBytes : 0;
                if (!b.Running && !b.Cancelled && b.Done + b.Failed >= b.Clips.Count) fraction = 1.0;
                return new JObject
                {
                    ["started"] = true, ["running"] = b.Running, ["cancelled"] = b.Cancelled, ["id"] = b.Id,
                    ["finishedAgo"] = b.Running || b.FinishedAt == DateTime.MinValue ? -1 : (int)(DateTime.UtcNow - b.FinishedAt).TotalSeconds,
                    ["total"] = b.Clips.Count, ["done"] = b.Done, ["failed"] = b.Failed, ["current"] = b.Current,
                    ["percent"] = (int)Math.Round(Math.Max(0, Math.Min(1, fraction)) * 100),
                    ["savedBytes"] = b.SavedBytes, ["savedText"] = FormatBytes(b.SavedBytes), ["error"] = b.Error ?? "",
                };
            }
        }

        public static JObject GetCompressPayload()
        {
            var payload = CompressSnapshot();
            payload["ok"] = true;
            return payload;
        }

        private static void RunCompress(CompressBatch batch)
        {
            try
            {
                foreach (var entry in batch.Clips)
                {
                    lock (CompressGate)
                    {
                        if (batch.Cancelled) break;
                        batch.Current = entry.Path.Name;
                        batch.CurrentBytes = entry.Size;
                        batch.CurrentPercent = 0;
                    }
                    bool ok = CompressOne(batch, entry);
                    lock (CompressGate)
                    {
                        batch.FinishedBytes += entry.Size;
                        if (ok) batch.Done++;
                        else if (!batch.Cancelled) batch.Failed++;
                        batch.Current = "";
                        batch.CurrentPercent = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Storage.RunCompress: " + ex.Message);
                lock (CompressGate) batch.Error = ex.Message;
            }
            finally
            {
                lock (CompressGate)
                {
                    batch.Running = false;
                    batch.FinishedAt = DateTime.UtcNow;
                }
                Clips.InvalidateCache();
                try { RefreshFolderSizes(true); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                AnnounceCompress(batch);
            }
        }

        // one clip through the worker of the clips page, waiting for its job to end. true when it was compressed
        private static bool CompressOne(CompressBatch batch, SelectedClip entry)
        {
            var selected = Clips.GetSafeClipPath(entry.Path.Name, Path.GetDirectoryName(entry.Path.Full));
            if (selected == null || !File.Exists(selected.Full)) return false;
            JObject started = null;
            // another job can hold a slot for a moment, so the clip waits for it a little instead of failing
            for (int attempt = 0; attempt < 30; attempt++)
            {
                lock (CompressGate) { if (batch.Cancelled) return false; }
                started = CompressOverwrite.StartCompressOverwriteFile(selected, "smaller", 0, entry.Size, entry.WriteTime);
                if (started["ok"]?.Value<bool>() == true || started["busy"]?.Value<bool>() != true) break;
                lock (CompressGate) { if (batch.Cancelled) return false; }
                Thread.Sleep(2000);
            }
            if (started == null || started["ok"]?.Value<bool>() != true)
            {
                lock (CompressGate) batch.Error = started?["message"]?.Value<string>() ?? "Could not start.";
                return false;
            }
            string requestId = started["requestId"]?.Value<string>() ?? "";
            string state = "", error = "";
            bool cancelSent = false;
            while (true)
            {
                bool active = false;
                int percent = 0;
                lock (Server.State.UploadLock)
                {
                    if (Server.State.Jobs.TryGetValue(requestId, out var job))
                    {
                        active = job.Active;
                        percent = job.Percent;
                        state = job.State ?? "";
                        error = job.Error ?? "";
                    }
                }
                bool cancel;
                lock (CompressGate)
                {
                    batch.CurrentPercent = Math.Max(0, Math.Min(100, percent));
                    cancel = batch.Cancelled;
                }
                if (!active) break;
                if (cancel && !cancelSent)
                {
                    cancelSent = true;
                    UploadState.CancelActiveUpload("", requestId);
                }
                Thread.Sleep(500);
            }
            bool cancelled;
            lock (CompressGate) cancelled = batch.Cancelled;
            if (cancelled || state == "error" || !File.Exists(selected.Full))
            {
                if (state == "error") lock (CompressGate) batch.Error = error;
                return false;
            }
            long after = entry.Size;
            try { after = new FileInfo(selected.Full).Length; } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            lock (CompressGate) batch.SavedBytes += Math.Max(0, entry.Size - after);
            return true;
        }

        // a compression is often started and then left running with the card closed, so the end of it is a note in the bell
        private static void AnnounceCompress(CompressBatch batch)
        {
            try
            {
                int done, failed;
                long saved;
                bool cancelled;
                string error;
                lock (CompressGate) { done = batch.Done; failed = batch.Failed; saved = batch.SavedBytes; cancelled = batch.Cancelled; error = batch.Error; }
                string clipsText = done + " clip" + (done == 1 ? "" : "s");
                if (done == 0 && failed > 0)
                    Notifications.Add("warning", "compress-batch", "Compression did not finish", string.IsNullOrEmpty(error) ? "The clips could not be compressed." : error);
                else if (cancelled)
                    Notifications.Add("info", "compress-batch", "Compression stopped", "Compressed " + clipsText + " and freed " + FormatBytes(saved) + " before it was stopped.");
                else
                    Notifications.Add("info", "compress-batch", "Compression finished", "Compressed " + clipsText + " and freed " + FormatBytes(saved) + "." + (failed > 0 ? " " + failed + " could not be compressed." : ""));
            }
            catch (Exception ex) { Log.Write("Storage.AnnounceCompress: " + ex.Message); }
        }
    }
}

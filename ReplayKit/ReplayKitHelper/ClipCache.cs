using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ReplayKitHelper
{
    // the temporary files that make a clip quick to open in the editor the next time: its audio tracks, its film strip and its keyframes. they live under the temp folder of replaykit, the storage card counts them and can empty them, and once they pass the budget the entries used longest ago go first
    internal static class ClipCache
    {
        // what a folder carries once everything in it is made, so one that is still being filled is never taken for an old one
        public const string DoneMarker = "done";
        // all kinds together; ten minutes of a clip take about ten megabytes of tracks and a few hundred kilobytes of strip
        private const long BudgetBytes = 400L * 1024 * 1024;
        // nothing is kept for longer than this without being used, whatever room is left
        private static readonly TimeSpan MaxIdle = TimeSpan.FromDays(30);
        // an unfinished entry younger than this belongs to a job that is still running
        private static readonly TimeSpan UnfinishedGrace = TimeSpan.FromMinutes(10);
        private static readonly object Gate = new object();
        private static bool _oldLocationRetired;

        private sealed class Entry
        {
            public string Path;
            public bool IsDir;
            public long Bytes;
            public DateTime LastUse;
            public bool Busy;
        }

        public static string KindDir(string kind) => Path.Combine(Constants.CACHE_DIR, kind);

        // one entry per state of a clip, so an edited or replaced clip never shows what was made from its old self
        public static string KeyFor(string name, FileInfo info, string tag)
        {
            using (var sha = SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(info.FullName.ToUpperInvariant() + "|" + name + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks + "|" + tag));
                return string.Concat(hash.Take(8).Select(b => b.ToString("x2")));
            }
        }

        // marks an entry as used just now, so it is the last one to go
        public static void Touch(string entry)
        {
            try
            {
                if (Directory.Exists(entry)) Directory.SetLastWriteTimeUtc(entry, DateTime.UtcNow);
                else if (File.Exists(entry)) File.SetLastWriteTimeUtc(entry, DateTime.UtcNow);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        private static IEnumerable<string> SafeDirs(string dir)
        {
            try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : new string[0]; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return new string[0]; }
        }

        private static IEnumerable<string> SafeFiles(string dir, SearchOption option = SearchOption.TopDirectoryOnly)
        {
            try { return Directory.Exists(dir) ? Directory.GetFiles(dir, "*", option) : new string[0]; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return new string[0]; }
        }

        private static long BytesOf(IEnumerable<string> files)
        {
            long total = 0;
            foreach (var file in files)
            {
                try { total += new FileInfo(file).Length; }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
            return total;
        }

        private static List<Entry> Entries()
        {
            var list = new List<Entry>();
            DateTime now = DateTime.UtcNow;
            foreach (var kind in SafeDirs(Constants.CACHE_DIR))
            {
                foreach (var dir in SafeDirs(kind))
                {
                    DateTime last = Directory.GetLastWriteTimeUtc(dir);
                    bool done = File.Exists(Path.Combine(dir, DoneMarker));
                    list.Add(new Entry { Path = dir, IsDir = true, Bytes = BytesOf(SafeFiles(dir, SearchOption.AllDirectories)), LastUse = last, Busy = !done && now - last < UnfinishedGrace });
                }
                foreach (var file in SafeFiles(kind))
                {
                    DateTime last = File.GetLastWriteTimeUtc(file);
                    list.Add(new Entry { Path = file, IsDir = false, Bytes = BytesOf(new[] { file }), LastUse = last, Busy = file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) && now - last < UnfinishedGrace });
                }
            }
            return list;
        }

        private static bool Remove(Entry entry)
        {
            try
            {
                if (entry.IsDir) Directory.Delete(entry.Path, true);
                else File.Delete(entry.Path);
                return true;
            }
            // a file that is being played or read right now cannot go, and is left for the next pass
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return false; }
        }

        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        // the audio tracks used to live in the scratch folder; that copy is of no use to anything now
        private static void RetireOldLocation()
        {
            if (_oldLocationRetired) return;
            _oldLocationRetired = true;
            try
            {
                string old = Path.Combine(Constants.SCRATCH_DIR, "tracks");
                if (Directory.Exists(old)) Directory.Delete(old, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        // makes room before a new entry is made: what has been idle for too long goes, and then the least recently used until the cache is under its budget. the entry about to be made is never touched
        public static void Prune(string keep)
        {
            lock (Gate)
            {
                RetireOldLocation();
                var entries = Entries();
                DateTime now = DateTime.UtcNow;
                long total = entries.Sum(e => e.Bytes);
                foreach (var entry in entries.Where(e => !e.Busy && !SamePath(e.Path, keep) && now - e.LastUse > MaxIdle).ToList())
                {
                    if (Remove(entry)) { total -= entry.Bytes; entries.Remove(entry); }
                }
                if (total <= BudgetBytes) return;
                foreach (var entry in entries.Where(e => !e.Busy && !SamePath(e.Path, keep)).OrderBy(e => e.LastUse))
                {
                    if (total <= BudgetBytes) break;
                    if (Remove(entry)) total -= entry.Bytes;
                }
            }
        }

        // how much room the cache takes now, for the storage card
        public static long TotalBytes()
        {
            lock (Gate) return Entries().Sum(e => e.Bytes);
        }

        // empties the cache for the storage card; what a job is still making is left alone. returns the bytes given back
        public static long Clear(out int removed)
        {
            removed = 0;
            long freed = 0;
            lock (Gate)
            {
                foreach (var entry in Entries().Where(e => !e.Busy))
                {
                    if (Remove(entry)) { removed++; freed += entry.Bytes; }
                }
            }
            return freed;
        }
    }
}

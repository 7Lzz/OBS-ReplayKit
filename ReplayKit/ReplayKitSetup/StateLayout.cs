using System;
using System.IO;
using System.Threading;

namespace ReplayKitShared
{
    // where the settings and the saved state of replaykit live: plain files straight in the obs-replayKit folder, next to version.json. installs from before that kept them under scripts\ and scripts\helper\; Run moves those over once, and until a file has been moved Resolve keeps pointing at the old copy, so real data is never shadowed by an empty file in the new place. this file is compiled into both the setup and the helper, which is why it has a namespace of its own
    public static class StateLayout
    {
        public const string SettingsFile = "replaykit_settings.json";
        public const string HotkeySignalFile = "hotkey_capture_signal.json";
        public const string HelperConfigFile = "helper_config.json";
        public const string ClipsDbFile = "clips_db.json";
        public const string ClipsIndexFile = "clips_index.json";
        public const string UpdatePendingFile = "update_pending.json";

        private sealed class Entry
        {
            public string File;
            public string OldFolder;
            // the owner writes the file again from scratch whenever it starts, so an old copy that is left over is deleted and not kept
            public bool Regenerated;
        }

        private static readonly Entry[] Entries =
        {
            new Entry { File = SettingsFile, OldFolder = "scripts" },
            new Entry { File = HotkeySignalFile, OldFolder = "scripts", Regenerated = true },
            new Entry { File = HelperConfigFile, OldFolder = Path.Combine("scripts", "helper"), Regenerated = true },
            new Entry { File = ClipsDbFile, OldFolder = Path.Combine("scripts", "helper") },
            new Entry { File = ClipsIndexFile, OldFolder = Path.Combine("scripts", "helper") },
        };

        // written by the helper for diagnosis only and not read by anything; it lives with the logs now
        private static readonly string[] RetiredRels =
        {
            Path.Combine("scripts", "helper", "helper_startup_status.json"),
        };

        public sealed class Result
        {
            public int Moved;
            public int Removed;
            public int Left;
        }

        public static string PathFor(string root, string file) => Path.Combine(root, file);

        // the place the file lived before the move, or null for a file that never lived anywhere else
        public static string LegacyPath(string root, string file)
        {
            foreach (var entry in Entries)
                if (string.Equals(entry.File, file, StringComparison.OrdinalIgnoreCase)) return Path.Combine(root, entry.OldFolder, entry.File);
            return null;
        }

        // the path to read and write right now: the new place, or the old one only while the file exists there and has not been moved yet. nothing creates the file in the new place while the old copy is still the real one
        public static string Resolve(string root, string file)
        {
            string current = Path.Combine(root, file);
            if (File.Exists(current)) return current;
            string legacy = LegacyPath(root, file);
            return legacy != null && File.Exists(legacy) ? legacy : current;
        }

        // moves what is still in the old places into the folder root; safe to run any number of times and never overwrites a file that is already in the new place
        public static Result Run(string root, Action<string> log = null)
        {
            var result = new Result();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return result;

            foreach (var entry in Entries)
            {
                string old = Path.Combine(root, entry.OldFolder, entry.File);
                string current = Path.Combine(root, entry.File);
                if (!File.Exists(old)) continue;

                if (File.Exists(current))
                {
                    if (entry.Regenerated && TryDelete(old))
                    {
                        result.Removed++;
                        log?.Invoke("removed the old copy of " + entry.File + " (" + entry.OldFolder.Replace('\\', '/') + ")");
                    }
                    else
                    {
                        result.Left++;
                        log?.Invoke("warn: " + entry.File + " exists in the old and in the new place; the new one is used and the old one stays at " + old);
                    }
                    continue;
                }

                string failure = TryMove(old, current);
                if (failure == null)
                {
                    result.Moved++;
                    log?.Invoke("moved " + entry.OldFolder.Replace('\\', '/') + "/" + entry.File + " to the replaykit folder");
                }
                else
                {
                    result.Left++;
                    log?.Invoke("warn: could not move " + entry.File + " yet, the old copy keeps being used: " + failure);
                }
            }

            foreach (var rel in RetiredRels)
            {
                string path = Path.Combine(root, rel);
                if (File.Exists(path) && TryDelete(path)) result.Removed++;
            }
            result.Removed += SweepReplaceLeftovers(Path.Combine(root, "scripts", "helper"), log);
            result.Removed += SweepReplaceLeftovers(root, log);
            return result;
        }

        // file.Replace makes a ~RF<hex>.TMP backup beside the file it replaces and leaves it behind when another process still holds the old file; the clip database was saved that way in scripts\helper and then in the root. only old ones go, so a replace that is running now is left alone
        private static int SweepReplaceLeftovers(string folder, Action<string> log)
        {
            if (!Directory.Exists(folder)) return 0;
            int removed = 0;
            try
            {
                foreach (string path in Directory.GetFiles(folder, "*~RF*.TMP"))
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromHours(1)) continue;
                    if (!TryDelete(path)) continue;
                    removed++;
                    log?.Invoke("removed leftover " + Path.GetFileName(path));
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log?.Invoke("warn: could not clean up leftover temp files: " + ex.Message);
            }
            return removed;
        }

        private static bool TryDelete(string path)
        {
            try { File.Delete(path); return !File.Exists(path); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return false; }
        }

        // a rename inside one folder tree, so it is instant and a file is either in the old place or in the new one, never in between. a file that is open somewhere for a moment is tried again a few times
        private static string TryMove(string from, string to)
        {
            string last = "";
            for (int attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    File.Move(from, to);
                    return null;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    last = ex.Message;
                    if (File.Exists(to)) return "the new place was filled in the meantime";
                    Thread.Sleep(300);
                }
            }
            return last;
        }
    }
}

using System;
using System.IO;
using System.Threading;

namespace ReplayKitShared
{
    // where the settings and the saved state of replaykit live: the user-data folder inside obs-replayKit, so the files of the user are apart from what replaykit ships. older installs kept them straight in obs-replayKit, and before that under scripts\ and scripts\helper\; Run moves them over once, and until a file has been moved Resolve keeps pointing at the old copy, so real data is never shadowed by an empty file in the new place. compiled into both the setup and the helper, and scripts/sync_shared.ps1 writes Folder out for the plugin and the lua scripts
    public static class StateLayout
    {
        public const string Folder = "user-data";

        public const string SettingsFile = "replaykit_settings.json";
        public const string HotkeySignalFile = "hotkey_capture_signal.json";
        public const string HelperConfigFile = "helper_config.json";
        public const string ClipsDbFile = "clips_db.json";
        public const string ClipsIndexFile = "clips_index.json";
        public const string ClipsIndexErrorFile = "clips_index.json.error.txt";
        public const string CompressionHistoryFile = "compression_history.json";
        public const string GameMappingsFile = "game_mappings.json";
        public const string ProfilePictureFile = "profile_picture.png";
        public const string DeletedHistoryFile = "deleted_history.json";
        public const string EditDraftsFile = "edit_drafts.json";
        public const string EditorPresetsFile = "editor_presets.json";
        public const string NotificationsFile = "notifications.json";
        public const string DefaultsAppliedFile = "defaults_applied.json";
        public const string WindowStateFile = "window_state.ini";
        // the hand-off between the installer and a helper that can still be the one of the release before, so it keeps its place in the folder root
        public const string UpdatePendingFile = "update_pending.json";

        private sealed class Entry
        {
            public string File;
            // the places the file lived before, newest first; the root of obs-replayKit is the one every install from before the move has
            public string[] OldFolders;
            // the owner writes the file again from scratch whenever it starts, so an old copy that is left over is deleted and not kept
            public bool Regenerated;
        }

        // plain literals, since scripts/sync_shared.ps1 reads them and the table below to give lua and the plugin the same old places
        private static readonly string Root = "";
        private static readonly string Scripts = "scripts";
        private static readonly string ScriptsHelper = @"scripts\helper";

        private static readonly Entry[] Entries =
        {
            new Entry { File = SettingsFile, OldFolders = new[] { Root, Scripts } },
            new Entry { File = HotkeySignalFile, OldFolders = new[] { Root, Scripts }, Regenerated = true },
            new Entry { File = HelperConfigFile, OldFolders = new[] { Root, ScriptsHelper }, Regenerated = true },
            new Entry { File = ClipsDbFile, OldFolders = new[] { Root, ScriptsHelper } },
            new Entry { File = ClipsIndexFile, OldFolders = new[] { Root, ScriptsHelper } },
            new Entry { File = ClipsIndexErrorFile, OldFolders = new[] { Root }, Regenerated = true },
            new Entry { File = CompressionHistoryFile, OldFolders = new[] { Root } },
            new Entry { File = GameMappingsFile, OldFolders = new[] { Root } },
            new Entry { File = ProfilePictureFile, OldFolders = new[] { Root } },
            new Entry { File = DeletedHistoryFile, OldFolders = new[] { Root } },
            new Entry { File = EditDraftsFile, OldFolders = new[] { Root } },
            new Entry { File = EditorPresetsFile, OldFolders = new[] { Root } },
            new Entry { File = NotificationsFile, OldFolders = new[] { Root } },
            new Entry { File = DefaultsAppliedFile, OldFolders = new[] { Root } },
            new Entry { File = WindowStateFile, OldFolders = new[] { Root } },
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

        public static string FolderPath(string root) => Path.Combine(root, Folder);

        // where the file belongs; files that are not part of the move (the update hand-off) stay in the root
        public static string PathFor(string root, string file)
        {
            return Find(file) == null ? Path.Combine(root, file) : Path.Combine(root, Folder, file);
        }

        // the path to read and write right now: the new place, or an old one only while the file exists there and has not been moved yet. nothing creates the file in the new place while an old copy is still the real one
        public static string Resolve(string root, string file)
        {
            string current = PathFor(root, file);
            if (File.Exists(current)) return current;
            var entry = Find(file);
            if (entry == null) return current;
            foreach (var old in entry.OldFolders)
            {
                string legacy = Path.Combine(root, old, file);
                if (File.Exists(legacy)) return legacy;
            }
            return current;
        }

        // moves what is still in the old places into the user-data folder; safe to run any number of times and never overwrites a file that is already in the new place
        public static Result Run(string root, Action<string> log = null)
        {
            var result = new Result();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return result;
            // made even when there is nothing to move, so every writer finds its folder, the plugin included
            Directory.CreateDirectory(FolderPath(root));

            foreach (var entry in Entries)
            {
                string current = Path.Combine(root, Folder, entry.File);
                foreach (var oldFolder in entry.OldFolders)
                {
                    string old = Path.Combine(root, oldFolder, entry.File);
                    string where = oldFolder.Length == 0 ? "the replaykit folder" : oldFolder.Replace('\\', '/');
                    if (!File.Exists(old)) continue;

                    if (File.Exists(current))
                    {
                        if (entry.Regenerated && TryDelete(old))
                        {
                            result.Removed++;
                            log?.Invoke("removed the old copy of " + entry.File + " (" + where + ")");
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
                        log?.Invoke("moved " + entry.File + " from " + where + " to " + Folder);
                    }
                    else
                    {
                        result.Left++;
                        log?.Invoke("warn: could not move " + entry.File + " yet, the old copy keeps being used: " + failure);
                    }
                }
            }

            foreach (var rel in RetiredRels)
            {
                string path = Path.Combine(root, rel);
                if (File.Exists(path) && TryDelete(path)) result.Removed++;
            }
            result.Removed += SweepReplaceLeftovers(Path.Combine(root, "scripts", "helper"), log);
            result.Removed += SweepReplaceLeftovers(root, log);
            result.Removed += SweepReplaceLeftovers(FolderPath(root), log);
            return result;
        }

        private static Entry Find(string file)
        {
            foreach (var entry in Entries)
                if (string.Equals(entry.File, file, StringComparison.OrdinalIgnoreCase)) return entry;
            return null;
        }

        // file.Replace makes a ~RF<hex>.TMP backup beside the file it replaces and leaves it behind when another process still holds the old file; the clip database was saved that way in scripts\helper, then in the root, now in user-data. only old ones go, so a replace that is running now is left alone
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // files every clip of the library in the folder of its game, or in Unassigned, Desktop or Mixed; the folder a clip was filed in is kept with it in the clips db, so a clip the user moved by hand in explorer stays where it was put and only a changed game files it again
    internal static class ClipFolderWorker
    {
        // a clip this new may still be written by obs, the hevc fix or a save of the helper, and its game tag may not be written yet
        private static readonly TimeSpan Settle = TimeSpan.FromSeconds(45);
        private const int PassMs = 30000;
        private const int DbBatch = 50;
        private static readonly object Gate = new object();
        private static readonly AutoResetEvent Wake = new AutoResetEvent(false);
        private static readonly HashSet<string> Reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool started, running;
        private static int total, done, moved;
        private static long pass;

        private sealed class Move
        {
            public FileInfo File;
            public string Folder;
            public string Group;
            public string Current;
        }

        public static void Start()
        {
            lock (Gate) { if (started) return; started = true; }
            new Thread(Loop) { Name = "clip-folders", IsBackground = true }.Start();
        }

        // a pass soon instead of on the clock: after a changed game, or a file or folder that showed up in the clip folder
        public static void Poke() => Wake.Set();

        public static JObject Status()
        {
            lock (Gate) return new JObject { ["active"] = running && total > 0, ["done"] = done, ["total"] = total, ["moved"] = moved, ["pass"] = pass };
        }

        private static void Loop()
        {
            // the startup chores and the first pages go first
            Wake.WaitOne(10000);
            while (!Server.State.Shutdown)
            {
                try { Pass(); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is InvalidOperationException || ex is ArgumentException)
                { Log.Write("Clip folders: pass stopped: " + ex.Message); }
                finally { lock (Gate) running = false; }
                Wake.WaitOne(PassMs);
                // a burst of explorer events comes in as one pass
                Thread.Sleep(1500);
            }
        }

        private static bool IsTempName(string name) =>
            name.StartsWith("_replaykit_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("_compress_tmp_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("_trim_tmp_", StringComparison.OrdinalIgnoreCase);

        // the section the clips page sorts a clip into and the folder that section is, from the public summary of its tag
        private static (string Group, string Folder) GroupOf(JObject summary)
        {
            string kind = summary?["kind"]?.Value<string>() ?? "unknown";
            var game = kind == "game" ? summary : kind == "unknown" ? summary?["partial"] as JObject : null;
            string id = game?["id"]?.Value<string>();
            string folder = Clips.GameFolderFor(game?["name"]?.Value<string>());
            if (!string.IsNullOrEmpty(id) && folder.Length > 0) return ("game:" + id, folder);
            if (kind == "desktop") return ("desktop", "Desktop");
            if (kind == "mixed") return ("mixed", "Mixed");
            return ("unknown", "Unassigned");
        }

        private static JObject Record(string folder, string group) => new JObject { ["folder"] = folder, ["group"] = group };

        // a folder of the clip folder joins the library once it holds a clip the db knows that the library has lost: a folder renamed in explorer, or one the user moved clips into; a copy of a clip that is still in the library never pulls its folder in
        private static void Adopt(string root, JObject entries)
        {
            var known = new HashSet<string>(Clips.GameFolders(root), StringComparer.OrdinalIgnoreCase);
            // the library is only listed once a folder holds a clip the db knows, which is rare
            HashSet<string> inLibrary = null;
            List<DirectoryInfo> dirs;
            try { dirs = new DirectoryInfo(root).EnumerateDirectories().ToList(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return; }
            foreach (var dir in dirs)
            {
                if (known.Contains(dir.FullName) || (dir.Attributes & FileAttributes.ReparsePoint) != 0 || !Clips.IsGameFolderName(dir.Name)) continue;
                try
                {
                    var knownHere = dir.EnumerateFiles().Take(500).Where(f => Constants.ALLOWED_EXTS.Contains(f.Extension.ToLowerInvariant()) &&
                        entries[f.Name] is JObject entry && (entry["filed"] != null || entry["capture"] != null)).Select(f => f.Name).ToList();
                    if (knownHere.Count == 0) continue;
                    inLibrary = inLibrary ?? new HashSet<string>(Clips.LibraryFiles(root).Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
                    bool holdsClips = knownHere.Any(name => !inLibrary.Contains(name));
                    if (holdsClips) Clips.RegisterFolder(root, dir.Name);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }

        // a folder of the library left with nothing in it, once its last clip was deleted or moved, goes too; one that changed in the last moments may be about to receive a save, and a folder with anything in it is never touched
        private static bool RemoveEmptyFolders(string root)
        {
            DateTime settled = DateTime.UtcNow - Settle;
            bool waiting = false;
            foreach (string dir in Clips.GameFolders(root))
            {
                try
                {
                    if (Directory.EnumerateFileSystemEntries(dir).Any()) continue;
                    if (Directory.GetLastWriteTimeUtc(dir) > settled || Directory.GetCreationTimeUtc(dir) > settled) { waiting = true; continue; }
                    Directory.Delete(dir, false);
                    Log.Write("Clip folders: removed the empty folder " + Path.GetFileName(dir));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
            Clips.ForgetFolderCache();
            return waiting;
        }

        // what a pass looks at: the clip folder, its game folders and the db (GetClipsCacheSignature), and the folders straight inside the clip folder that may be adopted
        private static string PassSignature(string root)
        {
            var parts = new List<string> { Clips.GetClipsCacheSignature(root) };
            try { parts.AddRange(new DirectoryInfo(root).EnumerateDirectories().Select(d => d.Name + ":" + d.LastWriteTimeUtc.Ticks)); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { parts.Add("dirs:error"); }
            return string.Join("|", parts);
        }
        private static string lastSignature = "";
        private static bool waiting;

        private static void Flush(string root, Dictionary<string, JObject> records)
        {
            if (records.Count == 0) return;
            var batch = new Dictionary<string, JObject>(records, StringComparer.OrdinalIgnoreCase);
            records.Clear();
            Clips.UpdateDb(db =>
            {
                foreach (var pair in batch)
                {
                    var entry = db[pair.Key] as JObject ?? new JObject();
                    entry["filed"] = pair.Value;
                    db[pair.Key] = entry;
                }
                return true;
            }, root);
        }

        private static void Pass()
        {
            // with game recognition off clips stay where they are saved, and the game folders that are there are left alone
            if (!ClipGames.RecognitionOn()) return;
            string root = Path.GetFullPath(AppConfig.GetClipDir()).TrimEnd(Path.DirectorySeparatorChar);
            if (!Directory.Exists(root)) return;
            // nothing changed since the last pass and nothing was waiting to settle: the pass would find what it found then
            string signature = PassSignature(root);
            if (signature == lastSignature && !waiting) return;
            waiting = RemoveEmptyFolders(root);
            Clips.PruneFolders(root);
            var entries = ClipStore.ReadFolder(AppConfig.GetDbPath(), root, data => (JObject)data.DeepClone());
            Adopt(root, entries);

            var records = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            var moves = new List<Move>();
            DateTime settled = DateTime.UtcNow - Settle;
            foreach (var file in Clips.LibraryFiles(root))
            {
                if (!Constants.ALLOWED_EXTS.Contains(file.Extension.ToLowerInvariant()) || IsTempName(file.Name)) continue;
                var entry = entries[file.Name] as JObject;
                var capture = entry?["capture"] as JObject;
                var filed = entry?["filed"] as JObject;
                string filedFolder = filed?["folder"]?.Type == JTokenType.String ? filed["folder"].Value<string>() : null;
                string current = string.Equals(file.DirectoryName, root, StringComparison.OrdinalIgnoreCase) ? "" : Path.GetFileName(file.DirectoryName);
                bool fresh = file.CreationTimeUtc > settled || file.LastWriteTimeUtc > settled;
                waiting |= fresh;
                if (filedFolder == null)
                {
                    if (fresh) continue;
                    var (group, target) = GroupOf(ClipGames.Public(capture, file));
                    // a clip that was never filed is filed from the clip folder or from the game folder the plugin saved it into; one that came into some other folder stays there
                    bool savedHere = current.Length == 0 || Clips.NamedAfterFolder(file.Name, current);
                    if (!savedHere || string.Equals(current, target, StringComparison.OrdinalIgnoreCase)) records[file.Name] = Record(current, group);
                    else moves.Add(new Move { File = file, Folder = target, Group = group, Current = current });
                    continue;
                }
                // moved by hand in explorer: the clip stays where the user put it
                if (!string.Equals(filedFolder, current, StringComparison.OrdinalIgnoreCase))
                {
                    records[file.Name] = Record(current, GroupOf(ClipGames.Public(capture, file)).Group);
                    continue;
                }
                // a tag that no longer fits the file is being renewed (the hevc fix, an edit saved over the clip), so a filed clip waits for it instead of moving by it
                if (capture != null && !ClipGames.Current(capture, file)) { waiting = true; continue; }
                var (newGroup, newTarget) = GroupOf(ClipGames.Public(capture, file));
                if (newGroup == (filed["group"]?.Value<string>() ?? "")) continue;
                if (string.Equals(current, newTarget, StringComparison.OrdinalIgnoreCase)) records[file.Name] = Record(current, newGroup);
                else if (!fresh) moves.Add(new Move { File = file, Folder = newTarget, Group = newGroup, Current = current });
            }
            Flush(root, records);
            if (moves.Count == 0) { lastSignature = PassSignature(root); return; }

            lock (Gate) { running = true; total = moves.Count; done = 0; moved = 0; pass++; }
            int movedNow = 0;
            foreach (var move in moves)
            {
                if (Server.State.Shutdown || !string.Equals(Path.GetFullPath(AppConfig.GetClipDir()).TrimEnd(Path.DirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase)) break;
                string dir = Path.Combine(root, move.Folder);
                string dest = Path.Combine(dir, move.File.Name);
                try
                {
                    Directory.CreateDirectory(dir);
                    if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) throw new IOException("the folder is a link");
                    Clips.RegisterFolder(root, move.Folder);
                    if (File.Exists(dest))
                    {
                        // two files of one name: the one in place keeps it, and this one is left where it is for good
                        Log.Write("Clip folders: " + move.Folder + " already has " + move.File.Name + ", left in place");
                        records[move.File.Name] = Record(move.Current, move.Group);
                    }
                    else if (JobCoordinator.TryMove(move.File.FullName, dest))
                    {
                        EditDrafts.Rename(move.File.FullName, dest);
                        records[move.File.Name] = Record(move.Folder, move.Group);
                        movedNow++;
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // open in a player, an editor or another program: the next pass tries it again
                    lock (Gate) if (Reported.Add(move.File.Name)) Log.Write("Clip folders: " + move.File.Name + " not moved yet: " + ex.Message);
                }
                lock (Gate) { done++; moved = movedNow; }
                if (records.Count >= DbBatch) Flush(root, records);
            }
            Flush(root, records);
            Clips.ForgetFolderCache();
            AppConfig.ClearClipsCache();
            // a clip that could not be moved yet is tried again by the next pass
            waiting |= movedNow < moves.Count;
            lastSignature = PassSignature(root);
            Log.Write("Clip folders: filed " + movedNow + " of " + moves.Count + " clip(s)");
        }
    }
}

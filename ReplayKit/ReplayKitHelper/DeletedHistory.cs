using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // deleted clips and screenshots wait here for the days the storage settings keep them, so a delete can be taken back; the file moves into a hidden folder of its own clip folder (the same drive, so the move is instant) under an id, and where it came from, with its row of the clips db, lives in deleted_history.json
    internal static class DeletedHistory
    {
        private const string FileName = "deleted_history.json";
        // a name no game folder can be adopted by, since the files in it go by ids the clips db does not know
        private const string FolderName = ".replaykit-deleted";
        private const int PurgeMs = 60 * 60 * 1000;
        private static readonly object Gate = new object();
        private static bool started;

        private static string StorePath() => Path.Combine(AppConfig.GetReplayKitRoot(), FileName);
        private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        private static JObject Fail(string message) => new JObject { ["ok"] = false, ["message"] = message };

        // a record that cannot be read turned every delete away; it is kept aside, and the files of the hidden folder come back as recovered items, so they can still be restored and still expire. runs under Gate
        private static void Heal()
        {
            try { ClipStore.Read(StorePath(), db => true); return; }
            catch (JsonException) { }
            string store = StorePath();
            string aside = store + ".broken-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            File.Move(store, aside);
            string root = Path.GetFullPath(AppConfig.GetClipDir());
            string dir = Path.Combine(root, FolderName);
            var recovered = new JObject();
            if (Directory.Exists(dir) && (File.GetAttributes(dir) & FileAttributes.ReparsePoint) == 0)
            {
                int days = Math.Max(1, ReplaykitSettings.GetDeletedHistoryDays());
                foreach (var file in new DirectoryInfo(dir).EnumerateFiles().OrderBy(f => f.Name, StringComparer.Ordinal))
                {
                    string id = Path.GetFileNameWithoutExtension(file.Name);
                    if (!ValidId(id) || (file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    bool shot = file.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase);
                    recovered[id] = new JObject
                    {
                        ["kind"] = shot ? "screenshot" : "clip",
                        ["name"] = "Recovered " + (recovered.Count + 1) + file.Extension.ToLowerInvariant(),
                        ["folder"] = Path.Combine(root, shot ? "Screenshots" : "Unassigned"),
                        ["stored"] = file.FullName,
                        ["size"] = file.Length,
                        ["deletedAt"] = Now(),
                        ["keepDays"] = days,
                    };
                }
            }
            ClipStore.Update(store, db => { foreach (var prop in recovered.Properties()) db[prop.Name] = prop.Value; return true; });
            Log.Write("Deleted history: the record could not be read, kept it as " + Path.GetFileName(aside) + " and recovered " + recovered.Count + " item(s) from the hidden folder");
        }

        // moves the file into the history and says so; false when deleted history is off, and the caller then deletes it for good
        public static bool Keep(string path, string kind, string libraryRoot, JObject record)
        {
            int days = ReplaykitSettings.GetDeletedHistoryDays();
            if (days <= 0) return false;
            string dir = Path.Combine(Path.GetFullPath(libraryRoot), FolderName);
            Directory.CreateDirectory(dir);
            var info = new DirectoryInfo(dir);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("The deleted history folder cannot be a link.");
            if ((info.Attributes & FileAttributes.Hidden) == 0) info.Attributes |= FileAttributes.Hidden;
            var file = new FileInfo(path);
            string id = Guid.NewGuid().ToString("N");
            string stored = Path.Combine(dir, id + file.Extension.ToLowerInvariant());
            var entry = new JObject
            {
                ["kind"] = kind,
                ["name"] = file.Name,
                ["folder"] = file.DirectoryName,
                ["stored"] = stored,
                ["size"] = file.Length,
                ["deletedAt"] = Now(),
                ["keepDays"] = days,
            };
            if (record != null) entry["record"] = record;
            lock (Gate)
            {
                Heal();
                File.Move(path, stored);
                try { ClipStore.Update(StorePath(), db => { db[id] = entry; return true; }); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
                {
                    // a file the history has no record of could never be restored, so the delete is undone and fails the way it would have
                    File.Move(stored, path);
                    throw;
                }
            }
            return true;
        }

        // the days an item is kept: the setting now, or the days it was deleted with once history was turned off, so turning it off never removes what is already there
        private static int DaysFor(JObject entry, int days) => days > 0 ? days : Math.Max(1, entry["keepDays"]?.Value<int>() ?? 1);

        public static JObject List()
        {
            Purge();
            int days = ReplaykitSettings.GetDeletedHistoryDays();
            var items = new JArray();
            long total = 0;
            JObject db;
            lock (Gate)
            {
                Heal();
                db = ClipStore.Read(StorePath(), data => (JObject)data.DeepClone());
            }
            foreach (var prop in db.Properties().OrderByDescending(p => (p.Value as JObject)?["deletedAt"]?.Value<long>() ?? 0))
            {
                if (!(prop.Value is JObject entry)) continue;
                string stored = entry["stored"]?.Value<string>();
                if (string.IsNullOrEmpty(stored) || !File.Exists(stored)) continue;
                long deletedAt = entry["deletedAt"]?.Value<long>() ?? 0;
                long size = entry["size"]?.Value<long>() ?? 0;
                total += size;
                string kind = entry["kind"]?.Value<string>() == "screenshot" ? "screenshot" : "clip";
                var record = entry["record"] as JObject;
                var capture = kind == "clip" ? record?["capture"] as JObject : record;
                items.Add(new JObject
                {
                    ["id"] = prop.Name,
                    ["kind"] = kind,
                    ["name"] = entry["name"]?.Value<string>() ?? "",
                    ["size"] = size,
                    ["mtime"] = deletedAt,
                    ["deletedAt"] = deletedAt,
                    ["expiresAt"] = deletedAt + DaysFor(entry, days) * 86400L,
                    ["capture"] = ClipGames.Public(capture, new FileInfo(stored)),
                });
            }
            return new JObject { ["ok"] = true, ["days"] = days, ["bytes"] = total, ["items"] = items };
        }

        // the file of an item for its picture; null for an id the history does not hold
        public static string StoredFile(string id, out string kind)
        {
            kind = null;
            if (!ValidId(id)) return null;
            JObject entry;
            lock (Gate) entry = ClipStore.Read(StorePath(), db => (db[id] as JObject)?.DeepClone() as JObject);
            string stored = entry?["stored"]?.Value<string>();
            if (string.IsNullOrEmpty(stored) || !File.Exists(stored)) return null;
            kind = entry["kind"]?.Value<string>() == "screenshot" ? "screenshot" : "clip";
            return stored;
        }

        private static bool ValidId(string id) => !string.IsNullOrEmpty(id) && id.Length == 32 && id.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));

        // puts the file back where it was deleted from, under its own name unless another file took it since, with its row of the clips db
        public static JObject Restore(string id)
        {
            if (!ValidId(id)) return Fail("That deleted item was not found.");
            lock (Gate)
            {
                Heal();
                var entry = ClipStore.Read(StorePath(), db => (db[id] as JObject)?.DeepClone() as JObject);
                string stored = entry?["stored"]?.Value<string>();
                if (string.IsNullOrEmpty(stored) || !File.Exists(stored))
                {
                    if (entry != null) Forget(id);
                    return Fail("That deleted item is no longer there.");
                }
                bool screenshot = entry["kind"]?.Value<string>() == "screenshot";
                string folder = entry["folder"]?.Value<string>() ?? "";
                string name = entry["name"]?.Value<string>() ?? "";
                if (!Path.IsPathRooted(folder) || (screenshot ? !Screenshots.ValidName(name) : Clips.GetSafeFilename(name) == null)) return Fail("Where that item came from is unknown.");
                Directory.CreateDirectory(folder);
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) return Fail("The folder it came from is a link now.");
                string root = screenshot ? Path.GetDirectoryName(folder) : Clips.LibraryRoot(folder);
                // a game folder that was removed once it was empty joins the library again
                if (!screenshot && !string.Equals(Path.GetFullPath(folder).TrimEnd('\\'), Path.GetFullPath(root).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    Clips.RegisterFolder(root, Path.GetFileName(folder.TrimEnd('\\')));
                string restored = FreeName(folder, name, screenshot);
                if (restored == null) return Fail("There is no free name to restore that item under.");
                string target = Path.Combine(folder, restored);
                // a rename or a save can take the name between the check and the move
                try { File.Move(stored, target); }
                catch (IOException) when (File.Exists(target)) { return Fail("Another clip just took that name. Try restoring it again."); }
                Forget(id);
                var record = entry["record"] as JObject;
                if (screenshot)
                {
                    if (record != null) ClipGames.Write(target, record);
                }
                else if (record != null) Clips.UpdateDb(db => { db[restored] = record; return true; }, folder);
                else AppConfig.ClearClipsCache();
                return new JObject { ["ok"] = true, ["name"] = restored, ["kind"] = screenshot ? "screenshot" : "clip" };
            }
        }

        // the name the item had, or the first "(restored)" one that no clip of the library and no file of the folder has
        private static string FreeName(string folder, string name, bool screenshot)
        {
            string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
            for (int n = 1; n <= 100; n++)
            {
                string candidate = n == 1 ? name : stem + (n == 2 ? " (restored)" : " (restored " + (n - 1) + ")") + ext;
                if (Path.Combine(folder, candidate).Length >= 260) return null;
                if (File.Exists(Path.Combine(folder, candidate))) continue;
                if (!screenshot && Clips.NameTaken(candidate)) continue;
                return candidate;
            }
            return null;
        }

        public static JObject Remove(string id)
        {
            if (!ValidId(id)) return Fail("That deleted item was not found.");
            lock (Gate)
            {
                Heal();
                var entry = ClipStore.Read(StorePath(), db => (db[id] as JObject)?.DeepClone() as JObject);
                if (entry == null) return Fail("That deleted item was not found.");
                DeleteStored(entry);
                Forget(id);
                return new JObject { ["ok"] = true };
            }
        }

        public static JObject Empty()
        {
            int removed = 0;
            lock (Gate)
            {
                Heal();
                var db = ClipStore.Read(StorePath(), data => (JObject)data.DeepClone());
                foreach (var prop in db.Properties().ToList())
                {
                    if (prop.Value is JObject entry) DeleteStored(entry);
                    removed++;
                }
                ClipStore.Update(StorePath(), data => { bool any = data.HasValues; data.RemoveAll(); return any; });
            }
            return new JObject { ["ok"] = true, ["removed"] = removed };
        }

        // a stored file that cannot be deleted right now (open in another program) throws, and its record stays for the next try
        private static void DeleteStored(JObject entry)
        {
            string stored = entry["stored"]?.Value<string>();
            if (string.IsNullOrEmpty(stored) || !File.Exists(stored)) return;
            if ((File.GetAttributes(stored) & FileAttributes.ReparsePoint) != 0) throw new IOException("A deleted item is a link.");
            File.Delete(stored);
        }

        private static void Forget(string id) => ClipStore.Update(StorePath(), db => db.Remove(id));

        // deletes for good what has been kept its days, and drops records whose file is gone
        public static void Purge()
        {
            int days = ReplaykitSettings.GetDeletedHistoryDays();
            long now = Now();
            lock (Gate)
            {
                JObject db;
                try { Heal(); db = ClipStore.Read(StorePath(), data => (JObject)data.DeepClone()); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException) { Log.Write("Deleted history: could not be read: " + ex.Message); return; }
                var gone = new List<string>();
                foreach (var prop in db.Properties())
                {
                    if (!(prop.Value is JObject entry)) { gone.Add(prop.Name); continue; }
                    string stored = entry["stored"]?.Value<string>();
                    bool missing = string.IsNullOrEmpty(stored) || !File.Exists(stored);
                    bool expired = now - (entry["deletedAt"]?.Value<long>() ?? 0) >= DaysFor(entry, days) * 86400L;
                    if (!missing && !expired) continue;
                    try
                    {
                        if (!missing) DeleteStored(entry);
                        gone.Add(prop.Name);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Log.Write("Deleted history: " + entry["name"] + " not removed yet: " + ex.Message); }
                }
                if (gone.Count == 0) return;
                ClipStore.Update(StorePath(), data => { bool changed = false; foreach (string id in gone) changed |= data.Remove(id); return changed; });
                Log.Write("Deleted history: removed " + gone.Count + " item(s) for good");
            }
        }

        public static void Start()
        {
            lock (Gate) { if (started) return; started = true; }
            new Thread(() =>
            {
                // after the startup chores, then once an hour
                Thread.Sleep(60000);
                while (!Server.State.Shutdown)
                {
                    try { Purge(); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is InvalidOperationException) { Log.Write("Deleted history purge: " + ex.Message); }
                    Thread.Sleep(PurgeMs);
                }
            }) { Name = "deleted-history", IsBackground = true }.Start();
        }
    }
}

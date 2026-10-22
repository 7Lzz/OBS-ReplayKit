using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    internal static class ClipStore
    {
        private static T WithLock<T>(string path, Func<string, T> action)
        {
            string full = Path.GetFullPath(path);
            string name;
            using (var hash = SHA256.Create())
                name = "Local\\ReplayKit.Clips." + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(full.ToUpperInvariant()))).Replace("-", "");
            using (var mutex = new Mutex(false, name))
            {
                bool held = false;
                try
                {
                    try { held = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
                    catch (AbandonedMutexException) { held = true; }
                    if (!held) throw new IOException("Clip database is busy.");
                    return action(full);
                }
                finally { if (held) mutex.ReleaseMutex(); }
            }
        }

        public static T Read<T>(string path, Func<JObject, T> read)
        {
            return WithLock(path, full =>
            {
                var db = File.Exists(full) ? JObject.Parse(File.ReadAllText(full)) : new JObject();
                return read(db);
            });
        }

        public static bool Update(string path, Func<JObject, bool> change)
        {
            return WithLock(path, full =>
            {
                var db = File.Exists(full) ? JObject.Parse(File.ReadAllText(full)) : new JObject();
                if (!change(db)) return false;
                Write(full, db);
                return true;
            });
        }

        private static void Write(string path, JObject db)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(db.ToString(Newtonsoft.Json.Formatting.Indented));
                    file.Write(bytes, 0, bytes.Length);
                    file.Flush(true);
                }
                Native.MoveFileReplace(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        internal static string FolderKey(string folder)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(folder).TrimEnd('\\', '/').ToUpperInvariant()))).Replace("-", "");
        }

        private static JObject Folder(JObject db, string folder, out bool created)
        {
            string key = FolderKey(folder);
            created = false;
            if (db["_folders"] != null && !(db["_folders"] is JObject)) throw new Newtonsoft.Json.JsonException("Invalid clip folder metadata.");
            var folders = db["_folders"] as JObject;
            if (folders == null)
            {
                // Bind legacy filename-only state once; retain the original fields for recovery.
                var legacy = (JObject)db.DeepClone();
                folders = new JObject { [key] = legacy };
                db["_folders"] = folders;
                created = true;
            }
            if (folders[key] != null && !(folders[key] is JObject)) throw new Newtonsoft.Json.JsonException("Invalid clip folder metadata.");
            if (folders[key] == null)
            {
                folders[key] = new JObject();
                created = true;
            }
            return (JObject)folders[key];
        }

        public static T ReadFolder<T>(string path, string folder, Func<JObject, T> read)
        {
            return WithLock(path, full =>
            {
                var db = File.Exists(full) ? JObject.Parse(File.ReadAllText(full)) : new JObject();
                var scoped = Folder(db, folder, out bool created);
                if (created) Write(full, db);
                return read(scoped);
            });
        }

        public static bool UpdateFolder(string path, string folder, Func<JObject, bool> change)
        {
            return Update(path, db => { var scoped = Folder(db, folder, out bool created); return change(scoped) || created; });
        }

        public static bool SetTranscode(string path, string clip, string shortcode, int status, int percent)
        {
            return Update(path, db =>
            {
                bool changed = false;
                var folders = (db["_folders"] as JObject)?.Properties().Select(p => p.Value).OfType<JObject>() ?? Enumerable.Empty<JObject>();
                foreach (var owner in folders.Concat(new[] { db }))
                {
                    if (!(owner[clip] is JObject entry) || !string.Equals(entry["shortcode"]?.Value<string>(), shortcode, StringComparison.Ordinal)) continue;
                    int currentStatus = entry["transcode_status"]?.Value<int>() ?? 0;
                    if (currentStatus >= 2 && (status < 2 || status == 4)) continue;
                    entry["transcode_status"] = status;
                    entry["transcode_percent"] = percent;
                    entry["ready"] = status == 2;
                    entry["failed"] = status == 3;
                    if (status == 4) entry["transcode_error"] = "Streamable status check timed out after 30 minutes.";
                    else entry.Remove("transcode_error");
                    changed = true;
                }
                return changed;
            });
        }
    }
}

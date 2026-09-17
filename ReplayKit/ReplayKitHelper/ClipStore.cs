using System;
using System.IO;
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
                    string temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                        {
                            byte[] bytes = new UTF8Encoding(false).GetBytes(db.ToString(Newtonsoft.Json.Formatting.Indented));
                            file.Write(bytes, 0, bytes.Length);
                            file.Flush(true);
                        }
                        if (File.Exists(full)) File.Replace(temporary, full, null);
                        else File.Move(temporary, full);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                return true;
            });
        }

        public static bool SetTranscode(string path, string clip, string shortcode, int status, int percent)
        {
            return Update(path, db =>
            {
                if (!(db[clip] is JObject entry) || !string.Equals(entry["shortcode"]?.Value<string>(), shortcode, StringComparison.Ordinal)) return false;
                int currentStatus = entry["transcode_status"]?.Value<int>() ?? 0;
                if (currentStatus >= 2 && (status < 2 || status == 4)) return false;
                entry["transcode_status"] = status;
                entry["transcode_percent"] = percent;
                entry["ready"] = status == 2;
                entry["failed"] = status == 3;
                if (status == 4) entry["transcode_error"] = "Streamable status check timed out after 30 minutes.";
                else entry.Remove("transcode_error");
                return true;
            });
        }
    }
}

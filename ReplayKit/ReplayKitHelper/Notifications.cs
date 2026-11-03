using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // the notification list behind the bell in the clips / obs / settings title bars. one flat newest-first list, persisted so an unread item survives an obs restart -- which matters most for the update notice, since the update itself restarts obs. items carry a stable key so a repeated check (every startup) updates the existing entry instead of stacking a duplicate for the same release.
    internal static class Notifications
    {
        private const int MaxStored = 50;
        private static readonly object Gate = new object();

        // kept next to the other per-user replaykit state rather than under %TEMP%, which gets swept
        private static string StorePath() =>
            Path.Combine(Constants.OBS_CONFIG_DIR, "obs-replayKit", "notifications.json");

        private static JArray ReadItems()
        {
            try
            {
                string path = StorePath();
                if (!File.Exists(path)) return new JArray();
                var parsed = JToken.Parse(File.ReadAllText(path));
                if (parsed is JArray direct) return direct;
                return (parsed as JObject)?["items"] as JArray ?? new JArray();
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
            {
                // unreadable or corrupt: start clean rather than wedging the bell forever
                Log.Write("Notifications store could not be read: " + ex.Message);
                return new JArray();
            }
        }

        private static void WriteItems(JArray items)
        {
            string path = StorePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            AppConfig.WriteUtf8(path, new JObject { ["items"] = items }.ToString(Formatting.Indented));
        }

        private static JObject Snapshot(JArray items)
        {
            int unread = items.Count(i => !(i["read"]?.Value<bool>() ?? false));
            return new JObject { ["ok"] = true, ["unread"] = unread, ["items"] = items };
        }

        public static JObject Get()
        {
            lock (Gate) return Snapshot(ReadItems());
        }

        // key identifies the thing being announced (an update version, say) so re-announcing it refreshes the existing row. a refresh deliberately leaves `read` alone -- re-checking for an update the user already dismissed must not light the badge up again. url is optional (a github release page, say) and only used to offer an "open release page" action -- items without one just skip that menu entry.
        public static JObject Add(string kind, string key, string title, string body, string url = "", string linkLabel = null)
        {
            if (string.IsNullOrWhiteSpace(title)) return new JObject { ["ok"] = false, ["message"] = "Notification needs a title." };
            lock (Gate)
            {
                var items = ReadItems();
                if (!string.IsNullOrEmpty(key))
                {
                    foreach (var existing in items.OfType<JObject>())
                    {
                        if (existing["key"]?.Value<string>() != key) continue;
                        existing["title"] = title;
                        existing["body"] = body ?? "";
                        existing["url"] = url ?? "";
                        if (!string.IsNullOrEmpty(linkLabel)) existing["linkLabel"] = linkLabel;
                        WriteItems(items);
                        return Snapshot(items);
                    }
                }
                items.Insert(0, NewItem(kind, key, title, body, url, linkLabel));
                while (items.Count > MaxStored) items.RemoveAt(items.Count - 1);
                WriteItems(items);
                return Snapshot(items);
            }
        }

        private static JObject NewItem(string kind, string key, string title, string body, string url, string linkLabel)
        {
            var item = new JObject
            {
                ["id"] = Guid.NewGuid().ToString("N"),
                ["kind"] = kind ?? "info",
                ["key"] = key ?? "",
                ["title"] = title,
                ["body"] = body ?? "",
                ["url"] = url ?? "",
                ["createdUtc"] = DateTime.UtcNow.ToString("o"),
                ["read"] = false,
            };
            // the words of the link button, for a link that is not a release page
            if (!string.IsNullOrEmpty(linkLabel)) item["linkLabel"] = linkLabel;
            return item;
        }

        // a notice that is both in the bell and shown at once in the popup over the obs window (a driver to update); a fresh row at the top of the list replaces an older one of the same key. the popup itself is not part of the list (see DonePopups)
        public static bool Announce(string kind, string key, string title, string body, JObject popup, string url = "", string linkLabel = null)
        {
            if (string.IsNullOrWhiteSpace(title)) return false;
            lock (Gate)
            {
                var items = ReadItems();
                var keep = new JArray(items.OfType<JObject>().Where(i => string.IsNullOrEmpty(key) || i["key"]?.Value<string>() != key));
                keep.Insert(0, NewItem(kind, key, title, body, url, linkLabel));
                while (keep.Count > MaxStored) keep.RemoveAt(keep.Count - 1);
                WriteItems(keep);
            }
            return DonePopups.Show(kind, title, popup);
        }

        // takes away the row of a key, for a notice that only meant something until the next start
        public static void Forget(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (Gate)
            {
                var items = ReadItems();
                var keep = new JArray(items.OfType<JObject>().Where(i => i["key"]?.Value<string>() != key));
                if (keep.Count != items.Count) WriteItems(keep);
            }
        }

        public static JObject MarkRead(string id)
        {
            lock (Gate)
            {
                var items = ReadItems();
                foreach (var item in items.OfType<JObject>())
                {
                    if (item["id"]?.Value<string>() != id) continue;
                    item["read"] = true;
                    WriteItems(items);
                    break;
                }
                return Snapshot(items);
            }
        }

        public static JObject MarkUnread(string id)
        {
            lock (Gate)
            {
                var items = ReadItems();
                foreach (var item in items.OfType<JObject>())
                {
                    if (item["id"]?.Value<string>() != id) continue;
                    item["read"] = false;
                    WriteItems(items);
                    break;
                }
                return Snapshot(items);
            }
        }

        public static JObject MarkAllRead()
        {
            lock (Gate)
            {
                var items = ReadItems();
                foreach (var item in items.OfType<JObject>()) item["read"] = true;
                WriteItems(items);
                return Snapshot(items);
            }
        }

        public static JObject Dismiss(string id)
        {
            lock (Gate)
            {
                var items = ReadItems();
                var keep = new JArray(items.OfType<JObject>().Where(i => i["id"]?.Value<string>() != id));
                WriteItems(keep);
                return Snapshot(keep);
            }
        }

        // drops the row a keyed announcement made, for when the thing it announced is fine again. no-op when there is no such row
        public static void DismissKey(string key)
        {
            lock (Gate)
            {
                var items = ReadItems();
                var keep = new JArray(items.OfType<JObject>().Where(i => i["key"]?.Value<string>() != key));
                if (keep.Count != items.Count) WriteItems(keep);
            }
        }

        public static JObject Clear()
        {
            lock (Gate)
            {
                var empty = new JArray();
                WriteItems(empty);
                return Snapshot(empty);
            }
        }

        public static int UnreadCount()
        {
            try { lock (Gate) return ReadItems().Count(i => !(i["read"]?.Value<bool>() ?? false)); }
            catch (Exception ex) { Log.Write("Notifications unread count: " + ex.Message); return 0; }
        }

        // called by every update check that finds a newer release. the release notes become the body, so opening the notification is what shows the patch notes.
        public static void AnnounceUpdate(string version, string notes, string releaseUrl = "")
        {
            if (string.IsNullOrWhiteSpace(version)) return;
            Add("update", "update:" + version, "Update ReplayKit " + version, notes ?? "", releaseUrl ?? "");
        }
    }
}

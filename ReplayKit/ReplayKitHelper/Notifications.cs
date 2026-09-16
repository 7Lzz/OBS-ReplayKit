using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // the notification list behind the bell in the clips / obs / settings title bars. one flat newest-first list,
    // persisted so an unread item survives an obs restart -- which matters most for the update notice, since the
    // update itself restarts obs. items carry a stable key so a repeated check (every startup) updates the existing
    // entry instead of stacking a duplicate for the same release.
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

        // key identifies the thing being announced (an update version, say) so re-announcing it refreshes the existing
        // row. a refresh deliberately leaves `read` alone -- re-checking for an update the user already dismissed must
        // not light the badge up again. url is optional (a github release page, say) and only used to offer an "open
        // release page" action -- items without one just skip that menu entry.
        public static JObject Add(string kind, string key, string title, string body, string url = "")
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
                        WriteItems(items);
                        return Snapshot(items);
                    }
                }
                items.Insert(0, new JObject
                {
                    ["id"] = Guid.NewGuid().ToString("N"),
                    ["kind"] = kind ?? "info",
                    ["key"] = key ?? "",
                    ["title"] = title,
                    ["body"] = body ?? "",
                    ["url"] = url ?? "",
                    ["createdUtc"] = DateTime.UtcNow.ToString("o"),
                    ["read"] = false,
                });
                while (items.Count > MaxStored) items.RemoveAt(items.Count - 1);
                WriteItems(items);
                return Snapshot(items);
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

        // called by every update check that finds a newer release. the release notes become the body, so opening the
        // notification is what shows the patch notes.
        public static void AnnounceUpdate(string version, string notes, string releaseUrl = "")
        {
            if (string.IsNullOrWhiteSpace(version)) return;
            Add("update", "update:" + version, "Update ReplayKit " + version, notes ?? "", releaseUrl ?? "");
        }
    }
}

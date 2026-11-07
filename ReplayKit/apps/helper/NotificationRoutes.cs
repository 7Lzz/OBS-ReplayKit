using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CSharp.RuntimeBinder;
using Newtonsoft.Json.Linq;
using static ReplayKitHelper.Routes;

namespace ReplayKitHelper
{
    // the routes of notifications, the notification reader and the done popup
    internal static class NotificationRoutes
    {
        // answers the request when its path is one of these routes, with what DispatchRequest returns; null when it is not
        public static bool? Handle(RouteRequest r)
        {
            Stream stream = r.Stream;
            HttpRequest req = r.Req;
            string path = r.Path;
            string Q(string key, string def = "") => r.Q(key, def);


            // the bell in the clips / obs / settings title bars. read is what clears the badge; dismiss removes a row entirely.
            if (path == "/notifications")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                HttpResponse.SendJson(stream, 200, Notifications.Get());
                return false;
            }

            if (path == "/notifications/read" || path == "/notifications/unread" || path == "/notifications/read-all" ||
                path == "/notifications/dismiss" || path == "/notifications/clear")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string id = Q("id");
                if (path == "/notifications/read-all") HttpResponse.SendJson(stream, 200, Notifications.MarkAllRead());
                else if (path == "/notifications/clear") HttpResponse.SendJson(stream, 200, Notifications.Clear());
                else if (string.IsNullOrWhiteSpace(id)) HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Missing id." });
                else if (path == "/notifications/read") HttpResponse.SendJson(stream, 200, Notifications.MarkRead(id));
                else if (path == "/notifications/unread") HttpResponse.SendJson(stream, 200, Notifications.MarkUnread(id));
                else HttpResponse.SendJson(stream, 200, Notifications.Dismiss(id));
                return false;
            }

            // a notification too long for the panel: the plugin shows all of it in a card over the obs window
            if (path == "/notifications/reader")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string id = Q("id");
                if (string.IsNullOrEmpty(id) || !Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,64}$")) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Invalid id." }); return false; }
                PipeClient.SendNotificationReader(id);
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true });
                return false;
            }

            if (path == "/done-popup/item")
            {
                var item = DonePopups.Get(Q("id"));
                HttpResponse.SendJson(stream, item == null ? 404 : 200, item == null ? new JObject { ["ok"] = false } : new JObject { ["ok"] = true, ["item"] = item });
                return false;
            }

            if (path == "/done-popup/close")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                PipeClient.SendDonePopupClose();
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true });
                return false;
            }

            if (path == "/notifications/reader/close")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                PipeClient.SendNotificationReaderClose();
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true });
                return false;
            }

            if (path == "/notifications/open-link")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string url = Q("url");
                // scoped to github release pages and the driver pages of the three gpu makers on purpose -- this is not a general "open any url/file" primitive, only ever used for the link a notification itself carries
                bool validUrl = !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                    uri.Scheme == Uri.UriSchemeHttps && (string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) || GpuDrivers.IsDriverPage(uri));
                if (!validUrl) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Only a release page or a graphics driver page can be opened this way." }); return false; }
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true }); }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            return null;
        }
    }
}

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
    // the routes of the update check, notes, apply and the version
    internal static class UpdateRoutes
    {
        // answers the request when its path is one of these routes, with what DispatchRequest returns; null when it is not
        public static bool? Handle(RouteRequest r)
        {
            Stream stream = r.Stream;
            HttpRequest req = r.Req;
            string path = r.Path;
            string Q(string key, string def = "") => r.Q(key, def);


            if (path == "/update/check")
            {
                if (req.Method != "GET" && req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET or POST required"); return false; }
                var status = Update.GetUpdateStatus(Q("manual") == "1");
                if (Q("manual") == "1") Update.ShowBadge(status);
                HttpResponse.SendJson(stream, 200, status);
                return false;
            }

            if (path == "/update/notes")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                try
                {
                    string installed = Update.NormalizeVersion(Update.GetInstalledVersion());
                    var release = Update.GetReleaseForVersion(installed);
                    if (release == null) { HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false, ["message"] = "No release notes found for version " + installed + "." }); return false; }
                    // a real, persisted notification (not a throwaway client-side one) so it survives closing the panel or restarting obs same as any other -- keyed on the version so pressing the button again just refreshes the same row instead of stacking a duplicate. marked read immediately: the user asked to see this, it should not also bump the unread badge.
                    string title = (string.IsNullOrEmpty(release.Name) ? "ReplayKit " + installed : release.Name) + " (installed)";
                    var snapshot = Notifications.Add("info", "whats-new:" + installed, title, release.Body, release.HtmlUrl);
                    string addedId = (snapshot["items"] as JArray)?.OfType<JObject>()
                        .FirstOrDefault(i => i["key"]?.Value<string>() == "whats-new:" + installed)?["id"]?.Value<string>();
                    if (!string.IsNullOrEmpty(addedId)) snapshot = Notifications.MarkRead(addedId);
                    snapshot["id"] = addedId;
                    HttpResponse.SendJson(stream, 200, snapshot);
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/update/startup-check")
            {
                if (req.Method != "GET" && req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET or POST required"); return false; }
                HttpResponse.SendJson(stream, 200, Update.GetStartupUpdateStatus());
                return false;
            }

            if (path == "/update/auto-check")
            {
                if (req.Method != "GET" && req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET or POST required"); return false; }
                HttpResponse.SendJson(stream, 200, Update.GetAutoUpdateStatus());
                return false;
            }

            if (path == "/update/apply")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                HttpResponse.SendJson(stream, 200, Update.ApplyUpdate());
                return false;
            }

            // polled by the updater window while /update/apply is in flight, so its bar shows real bytes and speed
            if (path == "/update/progress")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                HttpResponse.SendJson(stream, 200, Update.GetProgress());
                return false;
            }

            if (path == "/update/install-result")
            {
                if (req.Method != "GET" && req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET or POST required"); return false; }
                HttpResponse.SendJson(stream, 200, Update.GetInstallResult());
                return false;
            }

            if (path == "/update/install-result/ack")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                HttpResponse.SendJson(stream, 200, Update.MarkInstallResultSeen());
                return false;
            }

            if (path == "/update/open-prompt")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                HttpResponse.SendJson(stream, 200, Update.OpenUpdatePromptWindow(Q("version")));
                return false;
            }

            if (path == "/update/later")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try
                {
                    string version = Q("version");
                    if (string.IsNullOrWhiteSpace(version) && !string.IsNullOrWhiteSpace(req.Body))
                    {
                        var body = JObject.Parse(req.Body);
                        if (body["version"] != null) version = body["version"].Value<string>();
                    }
                    HttpResponse.SendJson(stream, 200, Update.SetUpdatePromptDismissed(version));
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/version")
            {
                // local file read only, no github call -- for ui labels that just want "what am i running", not the update-check flow.
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["version"] = Update.GetInstalledVersion(), ["obsVersion"] = ObsUpdate.InstalledVersion()?.ToString() ?? "" });
                return false;
            }

            return null;
        }
    }
}

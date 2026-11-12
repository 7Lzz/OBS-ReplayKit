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
    // the routes of the discord share preview, projector and screenshare install
    internal static class DiscordRoutes
    {
        // answers the request when its path is one of these routes, with what DispatchRequest returns; null when it is not
        public static bool? Handle(RouteRequest r)
        {
            Stream stream = r.Stream;
            HttpRequest req = r.Req;
            string path = r.Path;
            string Q(string key, string def = "") => r.Q(key, def);


            if (path == "/share-preview")
            {
                if (req.Method == "GET")
                {
                    try
                    {
                        JObject state;
                        if (Q("quick") == "1") state = SettingsLiveApply.GetSharePreviewFlags();
                        else lock (SettingsStore.Gate) state = SettingsLiveApply.GetSharePreviewState(true);
                        HttpResponse.SendJson(stream, 200, state);
                    }
                    catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                    return false;
                }
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try
                {
                    if (string.IsNullOrWhiteSpace(req.Body)) throw new InvalidOperationException("Missing share preview body.");
                    var incoming = JObject.Parse(req.Body);
                    if (incoming["enabled"]?.Type != JTokenType.Boolean) throw new InvalidOperationException("Share preview enabled must be a JSON boolean.");
                    var result = SettingsLiveApply.SetSharePreviewEnabled(incoming["enabled"].Value<bool>());
                    int code = result["ok"]?.Value<bool>() == true ? 200 : 503;
                    HttpResponse.SendJson(stream, code, result);
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/projector-inspect")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try { HttpResponse.SendJson(stream, 200, DiscordProjector.Inspect()); }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/projector-repark")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try { HttpResponse.SendJson(stream, 200, DiscordProjector.Repark(force: true)); }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/install-discord-screenshare")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try { HttpResponse.SendJson(stream, 200, Uninstall.StartDiscordScreenshareInstall()); }
                catch (Exception ex) { HttpResponse.SendJson(stream, 400, FailureJson(ex)); }
                return false;
            }

            if (path == "/uninstall-discord-screenshare")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try { HttpResponse.SendJson(stream, 200, Uninstall.StartDiscordScreenshareRemoval()); }
                catch (Exception ex) { HttpResponse.SendJson(stream, 400, FailureJson(ex)); }
                return false;
            }

            return null;
        }
    }
}

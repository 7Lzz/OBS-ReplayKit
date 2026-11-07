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
    // the routes of the sdk command channel
    internal static class SdkRoutes
    {
        // answers the request when its path is one of these routes, with what DispatchRequest returns; null when it is not
        public static bool? Handle(RouteRequest r)
        {
            Stream stream = r.Stream;
            HttpRequest req = r.Req;
            string path = r.Path;
            string Q(string key, string def = "") => r.Q(key, def);


            // the local command channel of sdk mode: the list of commands, running one, and the answer of the plugin to one it was sent. nothing of it answers while development mode and sdk mode are not both on
            if (path == "/sdk" || path == "/sdk/run" || path == "/sdk/reply")
            {
                if (!Sdk.Enabled()) { HttpResponse.SendJson(stream, 403, new JObject { ["ok"] = false, ["message"] = "SDK mode is off." }); return false; }
                if (path == "/sdk")
                {
                    if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                    HttpResponse.SendJson(stream, 200, Sdk.List());
                    return false;
                }
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                if (path == "/sdk/reply")
                {
                    bool taken = int.TryParse(Q("id"), out int replyId) && Sdk.Answer(replyId, req.Body);
                    HttpResponse.SendJson(stream, taken ? 200 : 404, new JObject { ["ok"] = taken });
                    return false;
                }
                JObject call;
                try { call = string.IsNullOrWhiteSpace(req.Body) ? new JObject() : JObject.Parse(req.Body); }
                catch (Newtonsoft.Json.JsonException) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Bad json." }); return false; }
                HttpResponse.SendJson(stream, 200, Sdk.Execute(call["command"]?.ToString(), call["args"] as JObject));
                return false;
            }

            return null;
        }
    }
}

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
    // the routes of the storage card: the summary, temp cleanup, trims and compression of old clips
    internal static class StorageRoutes
    {
        // answers the request when its path is one of these routes, with what DispatchRequest returns; null when it is not
        public static bool? Handle(RouteRequest r)
        {
            Stream stream = r.Stream;
            HttpRequest req = r.Req;
            string path = r.Path;
            string Q(string key, string def = "") => r.Q(key, def);
            bool QFlag(string key) => r.QFlag(key);


            // the storage card of the clean layout. the summary is polled by the plugin for the ring in the rail and read by the card itself; every route that changes something is a post from the dock origin
            if (path == "/storage/summary")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                try { HttpResponse.SendJson(stream, 200, Storage.GetSummaryPayload(QFlag("fresh"))); }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            // the temporary files are emptied from the card; the ones kept to open clips quickly are made again the next time a clip is opened
            if (path == "/storage/clear-temp")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try { HttpResponse.SendJson(stream, 200, Storage.ClearTemp()); }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/storage/trim-apply" || path == "/storage/open" || path == "/storage/close")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try
                {
                    if (path == "/storage/open" || path == "/storage/close")
                    {
                        if (path == "/storage/open") PipeClient.SendOpenStorage(); else PipeClient.SendCloseStorage();
                        HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true });
                    }
                    else
                    {
                        var applied = Storage.ApplyTrim(Q("token"));
                        HttpResponse.SendJson(stream, applied["ok"]?.Value<bool>() == true ? 200 : 409, applied);
                    }
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            // compressing clips from the card: what is offered, the status of the run, and the two posts that start and stop it
            if (path == "/storage/compress-candidates" || path == "/storage/compress-status")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                try { HttpResponse.SendJson(stream, 200, path == "/storage/compress-status" ? Storage.GetCompressPayload() : Storage.GetCompressCandidates(QFlag("favorites"), int.TryParse(Q("limit"), out int offerLimit) ? offerLimit : 0)); }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/storage/compress-start" || path == "/storage/compress-cancel")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try
                {
                    if (path == "/storage/compress-cancel") HttpResponse.SendJson(stream, 200, Storage.CancelCompress());
                    else
                    {
                        var body = string.IsNullOrWhiteSpace(req.Body) ? new JObject() : JObject.Parse(req.Body);
                        if (body["mode"] != null && body["mode"].Type != JTokenType.String)
                        {
                            HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Compression mode must be a string." });
                            return false;
                        }
                        var started = Storage.StartCompress(body["names"] as JArray, body["mode"]?.Value<string>());
                        HttpResponse.SendJson(stream, started["ok"]?.Value<bool>() == true ? 200 : 409, started);
                    }
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/storage/trim-plan")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                try { HttpResponse.SendJson(stream, 200, Storage.GetTrimPlanPayload()); }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            return null;
        }
    }
}

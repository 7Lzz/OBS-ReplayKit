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
    // the routes of uploading clips to streamable, their links and compressed uploads
    internal static class ShareRoutes
    {
        // answers the request when its path is one of these routes, with what DispatchRequest returns; null when it is not
        public static bool? Handle(RouteRequest r)
        {
            Stream stream = r.Stream;
            HttpRequest req = r.Req;
            string path = r.Path;
            var query = r.Query;
            string Q(string key, string def = "") => r.Q(key, def);
            bool QFlag(string key) => r.QFlag(key);


            if (path == "/upload-latest")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try
                {
                    var result = Upload.StartStreamableUpload(null);
                    Log.Write("/upload-latest -> " + result.ToString(Newtonsoft.Json.Formatting.None));
                    int code = result["ok"]?.Value<bool>() == true ? 200 : result["busy"]?.Value<bool>() == true ? 409 : 400;
                    HttpResponse.SendJson(stream, code, result);
                }
                catch (Exception ex)
                {
                    Log.Write("/upload-latest threw: " + ex.Message + "\n" + ex.StackTrace);
                    HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = "Helper error: " + ex.Message });
                }
                return false;
            }

            if (path == "/upload")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                var selected = Clips.GetSafeClipPath(query.ContainsKey("file") ? query["file"] : null);
                if (selected == null) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Bad filename" }); return false; }
                try
                {
                    // bulk uploads pass quiet=1 -- one balloon toast + one clipboard write per clip is noise when a whole selection goes at once.
                    var result = Upload.StartStreamableUpload(selected, quiet: QFlag("quiet"));
                    Log.Write("/upload(" + selected.Name + ") -> " + result.ToString(Newtonsoft.Json.Formatting.None));
                    int code = result["ok"]?.Value<bool>() == true ? 200 : result["busy"]?.Value<bool>() == true ? 409 : 400;
                    HttpResponse.SendJson(stream, code, result);
                }
                catch (Exception ex)
                {
                    Log.Write("/upload threw: " + ex.Message + "\n" + ex.StackTrace);
                    HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = "Helper error: " + ex.Message });
                }
                return false;
            }

            if (path == "/internal/clip-ready")
            {
                // the detached transcode poll worker calls this once streamable finishes encoding -- the helper shows the "clip ready" toast (its settings are loaded, so the replaykit/custom icon resolves; the poll workers arent). clipboard was already set by the worker.
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string readyUrl = Q("url");
                if (Regex.IsMatch(readyUrl, @"^https://streamable\.com/[A-Za-z0-9_-]+$"))
                {
                    try { Upload.ShowUploadToast(readyUrl, Q("name"), Q("copied") != "0"); } catch (Exception ex) { Log.Write("/internal/clip-ready toast: " + ex.Message, "upload"); }
                }
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true });
                return false;
            }

            if (path == "/remove-link")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                var selected = Clips.GetSafeClipPath(query.ContainsKey("file") ? query["file"] : null);
                if (selected == null) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Bad filename" }); return false; }
                bool removed = Clips.RemoveLink(selected.Name);
                HttpResponse.SendJson(stream, removed ? 200 : 404, new JObject { ["ok"] = removed });
                return false;
            }

            if (path == "/cancel-upload")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try
                {
                    var result = UploadState.CancelActiveUpload(Q("file"), Q("requestId"));
                    int code = result.Ok ? 200 : 409;
                    HttpResponse.SendJson(stream, code, new JObject { ["ok"] = result.Ok, ["message"] = result.Message });
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/compress")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                var selected = Clips.GetSafeClipPath(query.ContainsKey("file") ? query["file"] : null);
                if (selected == null || !File.Exists(selected.Full)) { HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false, ["message"] = "Clip not found" }); return false; }
                try
                {
                    var result = Compression.StartCompressedStreamableUpload(selected);
                    Log.Write("/compress(" + selected.Name + ") -> " + result.ToString(Newtonsoft.Json.Formatting.None));
                    int code = result["ok"]?.Value<bool>() == true ? 200 : result["busy"]?.Value<bool>() == true ? 409 : 400;
                    HttpResponse.SendJson(stream, code, result);
                }
                catch (Exception ex)
                {
                    Log.Write("/compress threw: " + ex.Message + "\n" + ex.StackTrace);
                    HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message });
                }
                return false;
            }

            return null;
        }
    }
}

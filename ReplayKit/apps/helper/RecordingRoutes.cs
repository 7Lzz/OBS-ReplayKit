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
    // the routes of the clip buffer the plugin reports and saves, the replay buffer and recording, and the encoders obs loaded
    internal static class RecordingRoutes
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


            // the plugin, which owns the disk clip buffer, says whether it runs; once on every change and again with each settings poll, so a helper that restarted learns it within a second
            if (path == "/clip-buffer/state")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                ClipBuffer.ReportDiskState(QFlag("active"));
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true });
                return false;
            }

            // the plugin hands over the pieces of the disk buffer that cover the clip, and they are joined into the clip itself
            // the plugin says a clip press started a save, or that one ended before it reached the helper
            if (path == "/clip-buffer/saving")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try
                {
                    var body = JObject.Parse(string.IsNullOrWhiteSpace(req.Body) ? "{}" : req.Body);
                    long id = body["id"]?.Value<long?>() ?? -1;
                    string state = body["state"]?.Value<string>() ?? "";
                    if (id < 0 || (state != "start" && state != "failed")) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "The save notice was not valid." }); return false; }
                    if (state == "start") ClipBuffer.MarkSaveStarted(id); else ClipBuffer.MarkSaveEnded(id, "");
                    HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true });
                }
                catch (Exception ex) when (ex is Newtonsoft.Json.JsonException || ex is FormatException || ex is OverflowException || ex is ArgumentException) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "The save notice was not valid." }); }
                return false;
            }

            if (path == "/clip-buffer/save")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try
                {
                    var result = ClipBuffer.SaveSegments(JObject.Parse(string.IsNullOrWhiteSpace(req.Body) ? "{}" : req.Body));
                    HttpResponse.SendJson(stream, result["ok"]?.Value<bool>() == true ? 200 : 409, result);
                }
                catch (Newtonsoft.Json.JsonException) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "The save request was not valid." }); }
                return false;
            }

            // the plugin says which video encoders obs loaded, and which one of the profile it had to replace
            if (path == "/encoders/report")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try { HttpResponse.SendJson(stream, 200, GpuDrivers.Report(JObject.Parse(string.IsNullOrWhiteSpace(req.Body) ? "{}" : req.Body))); }
                catch (Newtonsoft.Json.JsonException) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Bad report" }); }
                return false;
            }

            if (path == "/save-replay")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try
                {
                    var result = ObsWebSocket.SaveReplayBuffer();
                    int code = result.Ok ? 200 : result.Unavailable ? 503 : 409;
                    HttpResponse.SendJson(stream, code, ObsResultToJson(result));
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            // start or stop clipping: toggles the obs replay buffer, or drives it to the state a state query names, which is what the queued clipping switch in Settings sends so that applying it twice, or after obs got there already, cannot undo it
            if (path == "/obs/replay-buffer")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string want = Q("state");
                if (want != "" && want != "on" && want != "off") { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "state must be on or off." }); return false; }
                try
                {
                    var status = ObsWebSocket.InvokeRequest("GetReplayBufferStatus");
                    bool active = status.Ok && (status.Data?["outputActive"]?.Value<bool>() ?? false);
                    if (want != "" && status.Ok && active == (want == "on"))
                    {
                        HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["active"] = active });
                        return false;
                    }
                    if (want == "on" || (want == "" && status.Ok && !active)) PipeClient.ReleaseObsOutputsAndWait(1500);
                    var res = ObsWebSocket.InvokeRequest(want == "on" ? "StartReplayBuffer" : want == "off" ? "StopReplayBuffer" : "ToggleReplayBuffer");
                    var body = ObsResultToJson(res);
                    if (res.Ok && want != "")
                    {
                        // the request only asks obs, which starts or stops its output a moment later: the answer waits for obs to get there and says what obs really did, so a page never shows a state that is still on its way
                        bool rbWantOn = want == "on";
                        bool rbObserved = rbWantOn;
                        bool rbReached = false;
                        var rbUntil = DateTime.UtcNow.AddMilliseconds(3000);
                        while (true)
                        {
                            var rbNow = ObsWebSocket.InvokeRequest("GetReplayBufferStatus");
                            if (rbNow.Ok) rbObserved = rbNow.Data?["outputActive"]?.Value<bool>() ?? rbObserved;
                            if (rbNow.Ok && rbObserved == rbWantOn) { rbReached = true; break; }
                            if (DateTime.UtcNow >= rbUntil) break;
                            Thread.Sleep(60);
                        }
                        body["active"] = rbObserved;
                        if (!rbReached)
                        {
                            body["ok"] = false;
                            body["message"] = "OBS did not " + (rbWantOn ? "start" : "stop") + " the replay buffer in time.";
                        }
                        HttpResponse.SendJson(stream, rbReached ? 200 : 409, body);
                        return false;
                    }
                    if (res.Ok) body["active"] = res.Data?["outputActive"]?.Value<bool>() ?? false;
                    HttpResponse.SendJson(stream, res.Ok ? 200 : res.Unavailable ? 503 : 409, body);
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            // "Start / Stop Recording" in the dock -- toggles OBS recording.
            if (path == "/obs/record")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try
                {
                    var recordStatus = ObsWebSocket.InvokeRequest("GetRecordStatus");
                    if (recordStatus.Ok && !(recordStatus.Data?["outputActive"]?.Value<bool>() ?? false)) PipeClient.ReleaseObsOutputsAndWait(1500);
                    var res = ObsWebSocket.InvokeRequest("ToggleRecord");
                    var body = ObsResultToJson(res);
                    if (res.Ok) body["active"] = res.Data?["outputActive"]?.Value<bool>() ?? false;
                    HttpResponse.SendJson(stream, res.Ok ? 200 : res.Unavailable ? 503 : 409, body);
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            return null;
        }
    }
}

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
    // the routes of reading and saving the settings, hotkey capture and settings search
    internal static class SettingsRoutes
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


            if (path == "/settings/native")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                if (query.ContainsKey("clip")) ClipBuffer.ReportDiskState(QFlag("clip"));
                try { HttpResponse.SendJson(stream, 200, SettingsHttp.GetNativeSettingsPayload()); }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/settings")
            {
                if (req.Method == "GET")
                {
                    try { HttpResponse.SendJson(stream, 200, SettingsHttp.GetSettingsPayload()); }
                    catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                    return false;
                }
                if (req.Method == "POST")
                {
                    try
                    {
                        // whether obs restarts follows from what changed, so the restart flag a page sends is not needed here
                        var result = SettingsHttp.SaveSettingsFromRequest(req.Body);
                        if (result["restartRequired"]?.Value<bool>() == true)
                        {
                            string obsPath = GetRunningObsPath();
                            if (obsPath == null)
                            {
                                HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = "Could not locate OBS executable path to relaunch from." });
                                return false;
                            }
                            ArmRelaunchAndCloseObs(obsPath, "/settings");
                            HttpResponse.SendJson(stream, 200, result);
                            StopObsForRestart("/settings", true);
                            return false;
                        }
                        HttpResponse.SendJson(stream, 200, result);
                        PipeClient.SendSettingsApplied();
                    }
                    catch (Exception ex) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                    return false;
                }
                HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET or POST required");
                return false;
            }

            if (path == "/settings/hotkey-capture")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                bool active = QFlag("active");
                try { HttpResponse.SendJson(stream, 200, SettingsHotkeys.SetHotkeyCapture(active)); }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            // the query of the settings search, shared by the search box of the replaykit settings and the one of the obs settings
            if (path == "/settings/search")
            {
                if (req.Method == "POST") SettingsSearch.Set(Q("q"));
                else if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET or POST required"); return false; }
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["q"] = SettingsSearch.Get() });
                return false;
            }

            return null;
        }
    }
}

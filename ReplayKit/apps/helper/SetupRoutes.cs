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
    // the routes of the first run setup, repair and uninstall
    internal static class SetupRoutes
    {
        // answers the request when its path is one of these routes, with what DispatchRequest returns; null when it is not
        public static bool? Handle(RouteRequest r)
        {
            Stream stream = r.Stream;
            HttpRequest req = r.Req;
            string path = r.Path;


            if (path == "/setup/state")
            {
                // fail-closed to not-pending: never surface an error that would make the dock keep trying to open the wizard.
                try { HttpResponse.SendJson(stream, 200, ReplaykitSettings.GetSetupState()); }
                catch (Exception) { HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["pending"] = false }); }
                return false;
            }

            if (path == "/setup/complete")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try { HttpResponse.SendJson(stream, 200, ReplaykitSettings.SetSetupComplete()); }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/uninstall")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try { HttpResponse.SendJson(stream, 200, Uninstall.StartCleanupFromSettings(req.Body)); }
                catch (Exception ex) { HttpResponse.SendJson(stream, 400, FailureJson(ex)); }
                return false;
            }

            // the cached setup exe the uninstall and the discord screenshare buttons run: what state it is in, and a download of it again from the latest release
            if (path == "/setup/status")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                HttpResponse.SendJson(stream, 200, SetupExe.GetStatus());
                return false;
            }

            if (path == "/setup/repair")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try { HttpResponse.SendJson(stream, 200, SetupExe.Repair()); }
                catch (Exception ex) { HttpResponse.SendJson(stream, 400, FailureJson(ex)); }
                return false;
            }

            return null;
        }
    }
}

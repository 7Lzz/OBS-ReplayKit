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
    // the routes of the helper and obs themselves: status, logs, shutdown and restarting or closing obs
    internal static class AppRoutes
    {
        // answers the request when its path is one of these routes, with what DispatchRequest returns; null when it is not
        public static bool? Handle(RouteRequest r)
        {
            Stream stream = r.Stream;
            HttpRequest req = r.Req;
            string path = r.Path;
            string Q(string key, string def = "") => r.Q(key, def);


            if (path == "/status")
            {
                var status = UploadState.GetUploadStatusSnapshot();
                status["clipSave"] = ClipBuffer.SaveStatus();
                status["clipFolders"] = ClipFolderWorker.Status();
                HttpResponse.SendJson(stream, 200, status);
                return false;
            }

            if (path == "/open-log-folder")
            {
                // for the Advanced tab "Open log folder" button -- same shape as /open-clip-folder above, just pointed at the log dir instead of the clip dir. created eagerly at helper startup, so this should only 404 if something deleted it out from under a running helper.
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string root = Path.GetFullPath(Constants.LOG_DIR);
                if (!Directory.Exists(root)) { HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false, ["message"] = "Log folder not found" }); return false; }
                try
                {
                    var existing = GetExplorerHwndForPath(root);
                    if (existing != IntPtr.Zero)
                    {
                        bool focused2 = Native.FocusHwnd(existing);
                        HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["focused"] = focused2 });
                        return false;
                    }
                    Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = ProcessArgs.Quote(root), UseShellExecute = false })?.Dispose();
                    HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["focused"] = false });
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/shutdown")
            {
                HttpResponse.SendText(stream, 200, "OK", "bye");
                AppConfig.StopClipFolderWatcher();
                MicTest.Stop();
                Server.State.Shutdown = true;
                return false;
            }

            if (path == "/restart-obs")
            {
                // the detached relauncher starts obs only after the original process exits normally
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string obsPath = GetRunningObsPath();
                if (obsPath == null) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = "Could not locate OBS executable path to relaunch from." }); return false; }
                ArmRelaunchAndCloseObs(obsPath, "/restart-obs");
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["message"] = "OBS is restarting." });
                StopObsForRestart("/restart-obs", true);
                return false;
            }

            if (path == "/exit-obs/safety")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["gracefulOnly"] = true });
                return false;
            }

            if (path == "/exit-obs")
            {
                // requests a normal exit; a stalled shutdown is reported without killing obs
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["message"] = "OBS is closing." });
                // the installer says when the close is for an update
                StopObsForRestart("/exit-obs", Q("reason") == "update" || Update.InstallerRunning());
                return false;
            }

            if (path == "/restart-obs-clean")
            {
                // clears the sign-in state and requests a normal restart before the unlocked profile is cleaned
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string obsPath = GetRunningObsPath();
                if (obsPath == null) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = "Could not locate OBS executable path to relaunch from." }); return false; }
                try { AuthCore.ClearAuthBlob(); } catch { }
                try { AuthCore.ClearAuth(); } catch { }
                StreamableSignIn.ClearProfile();
                Server.State.RestartAfterCleanObsPath = obsPath;
                Log.Write("/restart-obs-clean: cleared ReplayKit's Streamable profile and will relaunch from '" + obsPath + "'.");
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["message"] = "OBS will restart with a clean Streamable session." });

                // allow the response to flush before closing obs
                StopObsForRestart("/restart-obs-clean", true);
                return false;
            }

            return null;
        }
    }
}

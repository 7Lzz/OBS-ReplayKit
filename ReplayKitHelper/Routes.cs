using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CSharp.RuntimeBinder;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // request dispatcher. ported from obs_replaykit helper modules/71_routes.ps1.
    internal static class Routes
    {
        private static Process[] GetProcessesByNames(params string[] names)
        {
            var list = new List<Process>();
            foreach (var name in names)
            {
                try { list.AddRange(Process.GetProcessesByName(name)); }
                catch (InvalidOperationException) { }
            }
            return list.ToArray();
        }

        private static string GetRunningObsPath()
        {
            var procs = GetProcessesByNames("obs64", "obs32", "obs");
            foreach (var p in procs)
            {
                try { if (!string.IsNullOrEmpty(p.MainModule?.FileName)) return p.MainModule.FileName; }
                catch (Win32Exception) { } catch (InvalidOperationException) { }
            }
            foreach (var p in procs)
            {
                try
                {
                    using (var searcher = new ManagementObjectSearcher("SELECT ExecutablePath FROM Win32_Process WHERE ProcessId=" + p.Id))
                    {
                        foreach (ManagementObject mo in searcher.Get())
                        {
                            string exePath = mo["ExecutablePath"] as string;
                            if (!string.IsNullOrEmpty(exePath)) return exePath;
                        }
                    }
                }
                catch (ManagementException) { }
            }
            return null;
        }

        // close the main window first so obs's own closeEvent runs and saves window position to global.ini -- Process.Kill() skips that entirely, which is why obs kept reopening at a stale (sometimes wrong-monitor) position after every replaykit-triggered restart. uses Native.CloseObsMainWindow (hwnd published once per session by the tray plugin) instead of MainWindowHandle/CloseMainWindow() -- .net's heuristic can grab a parked/hidden Discord-share projector instead of the real main window, which silently skips the graceful close and falls thru to the force-kill below every time. obs-browser-page helpers never own the published main-window hwnd so this is correctly a no-op for them.
        private static void StopObsForRestart(string reason)
        {
            Thread.Sleep(300);
            // tell the plugin's close-to-tray filter this WM_CLOSE is a real restart/exit, not the user clicking X -- otherwise it swallows the close and obs never gets to save its geometry before the force-kill below. wait for the plugin to ack so the close can't beat the message; proceed anyway if it doesn't (older bundle without the pipe -- same as before this signal existed).
            try { if (!PipeClient.SendAllowCloseAndWait(1000)) Log.Write(reason + ": close-to-tray plugin did not ack ALLOWCLOSE; proceeding."); } catch { }
            var procs = GetProcessesByNames("obs64", "obs32", "obs", "obs-browser-page");
            foreach (var p in procs)
            {
                try { Native.CloseObsMainWindow((uint)p.Id); } catch { }
            }
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(5000);
            foreach (var p in procs)
            {
                int remaining = (int)Math.Max(0, (deadline - DateTime.UtcNow).TotalMilliseconds);
                try { if (!p.HasExited) p.WaitForExit(remaining); } catch (InvalidOperationException) { } catch (Win32Exception) { }
            }
            foreach (var p in procs)
            {
                try { if (!p.HasExited) p.Kill(); }
                catch (Exception ex) { Log.Write(reason + ": Stop-Process PID " + p.Id + " failed: " + ex.Message); }
            }
            Server.State.Shutdown = true;
        }

        // spawn restart_obs.ps1 outside our kill-on-close job so it lives past helper exit. an in-process relaunch is unreliable -- by the time obs is dead and any cleanup completes, this process has often already been torn down (parent-process watchdog exit, abandoned-mutex on takeover, etc.) and never reaches the launch line. doing the relaunch from a sibling process owned by the os instead of by us is the only way to make it survive every shutdown path.
        private static bool StartDetachedObsRelauncher(string obsPath, int obsPid)
        {
            string scriptDir = AppConfig.GetScriptDir();
            if (string.IsNullOrWhiteSpace(scriptDir)) return false;
            string scriptPath = Path.Combine(scriptDir, "restart_obs.ps1");
            if (!File.Exists(scriptPath))
            {
                Log.Write("Start-DetachedObsRelauncher: missing " + scriptPath);
                return false;
            }
            string psExe = Path.Combine(Environment.GetEnvironmentVariable("WINDIR") ?? "", "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(psExe)) psExe = "powershell.exe";
            string cmd = ProcessArgs.Quote(psExe) +
                " -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File " + ProcessArgs.Quote(scriptPath) +
                " -ObsPath " + ProcessArgs.Quote(obsPath) +
                " -ObsPid " + obsPid;
            try
            {
                int relauncherPid = Native.SpawnDetached(cmd, scriptDir);
                if (relauncherPid <= 0)
                {
                    Log.Write("Start-DetachedObsRelauncher: SpawnDetached returned 0");
                    return false;
                }
                Log.Write("Start-DetachedObsRelauncher: PID " + relauncherPid + " will relaunch '" + obsPath + "' after this OBS exits.");
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Start-DetachedObsRelauncher threw: " + ex.Message);
                return false;
            }
        }

        // arms the detached relauncher (or falls back to the legacy in-process path), sends the response, then force-closes obs. shared by /settings' restart-required branch, /restart-obs, and /restart-obs-clean.
        private static void ArmRelaunchAndCloseObs(string obsPath, string routeName)
        {
            int obsTargetPid = 0;
            if (ParentWatchdog.ParentPid > 0)
            {
                obsTargetPid = ParentWatchdog.ParentPid;
            }
            else
            {
                var found = GetProcessesByNames("obs64", "obs32", "obs");
                if (found.Length > 0) obsTargetPid = found[0].Id;
            }
            bool detached = StartDetachedObsRelauncher(obsPath, obsTargetPid);
            if (!detached)
            {
                Server.State.RestartAfterCleanObsPath = obsPath;
                Log.Write(routeName + ": detached relauncher unavailable; queued legacy in-process restart for '" + obsPath + "'.");
            }
            else
            {
                Log.Write(routeName + ": detached relauncher armed for '" + obsPath + "' (target PID " + obsTargetPid + ").");
            }
        }

        // an explicit origin/referer/user-agent check since the dock pages are loaded from a file:// or loopback-http origin the browser doesnt sandbox the way it would a remote site -- these routes can change settings or trigger an obs restart, so anything that isnt recognizably "our own dock" is rejected.
        private static bool TestSettingsOrigin(HttpRequest req)
        {
            if (!req.Headers.TryGetValue("origin", out string origin)) return true;
            origin = origin.Trim().ToLowerInvariant();
            int port = Server.State.Config?["port"]?.Value<int?>() ?? Constants.DEFAULT_PORT;
            if (origin == "http://127.0.0.1:" + port || origin == "http://localhost:" + port) return true;
            if (origin == "null") return TestInstalledDockReferer(req) || TestObsBrowserUserAgent(req);
            return false;
        }

        private static bool TestObsBrowserUserAgent(HttpRequest req) =>
            req.Headers.TryGetValue("user-agent", out string ua) && Regex.IsMatch(ua, @"\bOBS/", RegexOptions.IgnoreCase);

        private static bool TestInstalledDockReferer(HttpRequest req)
        {
            if (!req.Headers.TryGetValue("referer", out string referer)) return false;
            try
            {
                var uri = new Uri(referer);
                if (!uri.IsFile) return false;
                string path = Path.GetFullPath(uri.LocalPath);
                string dockRoot = Path.GetFullPath(AppConfig.GetDockDir()).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                string defaultRoot = Path.GetFullPath(AppConfig.GetDefaultDockDir()).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                return path.StartsWith(dockRoot, StringComparison.OrdinalIgnoreCase) || path.StartsWith(defaultRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch (UriFormatException) { return false; }
        }

        // normalize explorer paths before comparing them. file explorer may report the active tab thru locationurl or thru document.folder, depending on windows version and tab state.
        private static string ConvertToExplorerComparePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return null; }
        }

        private static string GetExplorerWindowPath(dynamic window)
        {
            try
            {
                string loc = (string)window.LocationURL;
                if (!string.IsNullOrEmpty(loc) && loc.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                    return new Uri(loc).LocalPath;
            }
            catch (COMException) { } catch (RuntimeBinderException) { }
            try
            {
                string path = (string)window.Document.Folder.Self.Path;
                if (!string.IsNullOrWhiteSpace(path)) return path;
            }
            catch (COMException) { } catch (RuntimeBinderException) { }
            return null;
        }

        // find an already-open file explorer window thats showing targetPath. returns the window's hwnd or IntPtr.Zero if no match. used by /open-clip-folder, /open-log-folder and /open-folder to raise an existing explorer window for the clip folder instead of spawning yet another explorer.exe -- windows otherwise stacks duplicate folder windows every time the user clicks the dock's open folder button. uses Shell.Application's Windows() collection (the same one explorer itself uses); LocationURL on each item is a file:// url we can convert to a local path and compare case-insensitively to the target. ie/edge browser windows show up here too -- we skip anything whose LocationURL isnt file://.
        public static IntPtr GetExplorerHwndForPath(string targetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath)) return IntPtr.Zero;
            string full = ConvertToExplorerComparePath(targetPath);
            if (full == null) return IntPtr.Zero;

            dynamic shell = null;
            dynamic windows = null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                shell = Activator.CreateInstance(shellType);
                windows = shell.Windows();
                foreach (dynamic w in windows)
                {
                    try
                    {
                        string here = ConvertToExplorerComparePath(GetExplorerWindowPath(w));
                        if (string.Equals(here, full, StringComparison.OrdinalIgnoreCase))
                        {
                            long raw = (long)w.HWND;
                            if (raw != 0) return new IntPtr(raw);
                        }
                    }
                    catch (COMException) { } catch (RuntimeBinderException) { }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Get-ExplorerHwndForPath failed: " + ex.Message);
            }
            finally
            {
                if (windows != null) { try { Marshal.ReleaseComObject(windows); } catch (ArgumentException) { } }
                if (shell != null) { try { Marshal.ReleaseComObject(shell); } catch (ArgumentException) { } }
            }
            return IntPtr.Zero;
        }

        private static JObject TrimResultToJson(TrimResult r)
        {
            var obj = new JObject { ["ok"] = r.Ok };
            if (!r.Ok) { obj["message"] = r.Message; return obj; }
            if (r.Name != null) obj["name"] = r.Name;
            if (r.SourceName != null)
            {
                obj["sourceName"] = r.SourceName;
                obj["mode"] = r.Mode;
                obj["precise"] = r.Precise;
                obj["removeAudio"] = r.RemoveAudio;
                obj["startSec"] = r.StartSec;
                obj["endSec"] = r.EndSec;
                obj["durationSec"] = r.DurationSec;
            }
            return obj;
        }

        private static JObject KeyframeResultToJson(KeyframeScanResult r)
        {
            var obj = new JObject { ["ok"] = r.Ok };
            if (r.Pending) obj["pending"] = true;
            obj["message"] = r.Message ?? "";
            obj["keyframes"] = new JArray(r.Keyframes);
            if (r.Ok)
            {
                obj["name"] = r.Name;
                obj["count"] = r.Count;
                obj["cached"] = r.Cached;
                obj["probeMs"] = r.ProbeMs;
            }
            if (r.RetryMs != 0) obj["retryMs"] = r.RetryMs;
            return obj;
        }

        private static JObject ObsResultToJson(ObsWebSocketResult r)
        {
            var obj = new JObject { ["ok"] = r.Ok };
            if (r.Unavailable) obj["unavailable"] = true;
            if (r.RequestType != null) obj["requestType"] = r.RequestType;
            if (!r.Ok) { obj["message"] = r.Message; if (r.Code != 0) obj["code"] = r.Code; }
            else if (r.Data != null) obj["data"] = r.Data;
            return obj;
        }

        public static bool DispatchRequest(Stream stream, HttpRequest req)
        {
            AppConfig.LoadConfig();

            if (req.Method == "OPTIONS")
            {
                HttpResponse.SendBytes(stream, 204, "No Content", HttpResponse.GetNoStoreHeaders(), new byte[0]);
                return false;
            }

            string path = req.Path;
            var query = req.Query;
            string Q(string key, string def = "") => query.TryGetValue(key, out string v) ? v : def;
            bool QFlag(string key) => query.TryGetValue(key, out string v) && v == "1";

            if (Regex.IsMatch(path, @"^/(?:controls\.html)?$")) { HttpResponse.ServeHtml(stream, "controls.html"); return false; }
            if (Regex.IsMatch(path, @"^/controls_app\.html$")) { HttpResponse.ServeHtml(stream, "controls_app.html"); return false; }
            if (Regex.IsMatch(path, @"^/clips-view$|^/clips\.html$")) { HttpResponse.ServeHtml(stream, "clips.html"); return false; }
            if (Regex.IsMatch(path, @"^/settings-view$|^/settings\.html$")) { HttpResponse.ServeHtml(stream, "settings.html"); return false; }
            if (Regex.IsMatch(path, @"^/update-prompt$|^/update-prompt\.html$")) { HttpResponse.ServeHtml(stream, "update_prompt.html"); return false; }

            if (path == "/settings")
            {
                if (!TestSettingsOrigin(req)) { HttpResponse.SendJson(stream, 403, new JObject { ["ok"] = false, ["message"] = "Untrusted origin." }); return false; }
                if (req.Method == "GET")
                {
                    try { HttpResponse.SendJson(stream, 200, ReplaykitSettings.GetSettingsPayload()); }
                    catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                    return false;
                }
                if (req.Method == "POST")
                {
                    try
                    {
                        bool restartRequested = QFlag("restart");
                        var result = ReplaykitSettings.SaveSettingsFromRequest(req.Body, restartRequested);
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
                            StopObsForRestart("/settings");
                            return false;
                        }
                        HttpResponse.SendJson(stream, 200, result);
                    }
                    catch (Exception ex) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                    return false;
                }
                HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET or POST required");
                return false;
            }

            if (path == "/settings/overlay-preview")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                if (!TestSettingsOrigin(req)) { HttpResponse.SendJson(stream, 403, new JObject { ["ok"] = false, ["message"] = "Untrusted origin." }); return false; }
                try
                {
                    string mode = Q("mode", "preview");
                    HttpResponse.SendJson(stream, 200, ReplaykitSettings.OverlayPreviewFromRequest(req.Body, mode));
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/settings/hotkey-capture")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                if (!TestSettingsOrigin(req)) { HttpResponse.SendJson(stream, 403, new JObject { ["ok"] = false, ["message"] = "Untrusted origin." }); return false; }
                bool active = QFlag("active");
                try { HttpResponse.SendJson(stream, 200, ReplaykitSettings.SetHotkeyCapture(active)); }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/share-preview")
            {
                if (!TestSettingsOrigin(req)) { HttpResponse.SendJson(stream, 403, new JObject { ["ok"] = false, ["message"] = "Untrusted origin." }); return false; }
                if (req.Method == "GET")
                {
                    try { HttpResponse.SendJson(stream, 200, ReplaykitSettings.GetSharePreviewState(true)); }
                    catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                    return false;
                }
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try
                {
                    if (string.IsNullOrWhiteSpace(req.Body)) throw new InvalidOperationException("Missing share preview body.");
                    var incoming = JObject.Parse(req.Body);
                    if (incoming["enabled"]?.Type != JTokenType.Boolean) throw new InvalidOperationException("Share preview enabled must be a JSON boolean.");
                    var result = ReplaykitSettings.SetSharePreviewEnabled(incoming["enabled"].Value<bool>());
                    int code = result["ok"]?.Value<bool>() == true ? 200 : 503;
                    HttpResponse.SendJson(stream, code, result);
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/projector-inspect")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                if (!TestSettingsOrigin(req)) { HttpResponse.SendJson(stream, 403, new JObject { ["ok"] = false, ["message"] = "Untrusted origin." }); return false; }
                try { HttpResponse.SendJson(stream, 200, DiscordProjector.Inspect()); }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/projector-repark")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                if (!TestSettingsOrigin(req)) { HttpResponse.SendJson(stream, 403, new JObject { ["ok"] = false, ["message"] = "Untrusted origin." }); return false; }
                try { HttpResponse.SendJson(stream, 200, DiscordProjector.Repark(force: true)); }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/uninstall")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                if (!TestSettingsOrigin(req)) { HttpResponse.SendJson(stream, 403, new JObject { ["ok"] = false, ["message"] = "Untrusted origin." }); return false; }
                try { HttpResponse.SendJson(stream, 200, Uninstall.StartCleanupFromSettings(req.Body)); }
                catch (Exception ex) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/uninstall-discord-screenshare")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                if (!TestSettingsOrigin(req)) { HttpResponse.SendJson(stream, 403, new JObject { ["ok"] = false, ["message"] = "Untrusted origin." }); return false; }
                try { HttpResponse.SendJson(stream, 200, Uninstall.StartDiscordScreenshareRemoval()); }
                catch (Exception ex) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/update/check")
            {
                if (req.Method != "GET" && req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET or POST required"); return false; }
                if (!TestSettingsOrigin(req)) { HttpResponse.SendJson(stream, 403, new JObject { ["ok"] = false, ["message"] = "Untrusted origin." }); return false; }
                HttpResponse.SendJson(stream, 200, Update.GetUpdateStatus());
                return false;
            }

            if (path == "/update/startup-check")
            {
                if (req.Method != "GET" && req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET or POST required"); return false; }
                if (!TestSettingsOrigin(req)) { HttpResponse.SendJson(stream, 403, new JObject { ["ok"] = false, ["message"] = "Untrusted origin." }); return false; }
                HttpResponse.SendJson(stream, 200, Update.GetStartupUpdateStatus());
                return false;
            }

            if (path == "/update/auto-check")
            {
                if (req.Method != "GET" && req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET or POST required"); return false; }
                if (!TestSettingsOrigin(req)) { HttpResponse.SendJson(stream, 403, new JObject { ["ok"] = false, ["message"] = "Untrusted origin." }); return false; }
                HttpResponse.SendJson(stream, 200, Update.GetAutoUpdateStatus());
                return false;
            }

            if (path == "/update/apply")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                if (!TestSettingsOrigin(req)) { HttpResponse.SendJson(stream, 403, new JObject { ["ok"] = false, ["message"] = "Untrusted origin." }); return false; }
                HttpResponse.SendJson(stream, 200, Update.ApplyUpdate());
                return false;
            }

            if (path == "/update/later")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                if (!TestSettingsOrigin(req)) { HttpResponse.SendJson(stream, 403, new JObject { ["ok"] = false, ["message"] = "Untrusted origin." }); return false; }
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

            if (path == "/signin-windows-status")
            {
                // returns visible sign-in popups owned by obs's process family so the dock can update its status label. with overlay=1, the google oauth popup is moved onto the streamable login window so the flow presents as one window while preserving streamable's popup-based oauth.
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                uint ownerPid = ParentWatchdog.ParentPid > 0 ? (uint)ParentWatchdog.ParentPid : 0;
                string jsonArr = "[]";
                try
                {
                    bool overlayGoogle = QFlag("overlay");
                    jsonArr = Native.ListSignInWindows(ownerPid, false, overlayGoogle);
                }
                catch (Exception ex) { Log.Write("/signin-windows-status: " + ex.Message); }
                var h = HttpResponse.GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = "application/json" });
                HttpResponse.SendBytes(stream, 200, "OK", h, Encoding.UTF8.GetBytes(jsonArr));
                return false;
            }

            if (path == "/close-signin-windows")
            {
                // post to nuke the stuck sign-in popups via win32 wm_close. the dock js calls this after /import-session succeeds becuase cef refuses window.close() on cross-origin popups but windows-level wm_close works fine. we match only windows owned by the parent obs process, by title.
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                uint ownerPid = ParentWatchdog.ParentPid > 0 ? (uint)ParentWatchdog.ParentPid : 0;
                // specific titles only. we deliberately do not include a bare "streamable" since that would also match the clip-viewer popup (clips.html) and any other dock title that contains "streamable".
                string[] titles = { "about:blank", "Sign In - Google Accounts", "Sign in - Google Accounts", "Signing in to Streamable", "Dashboard - Streamable", "Log in - Streamable", "Sign in to Streamable - Streamable", "Streamable | " };
                int n = 0;
                try
                {
                    n = Native.CloseWindowsByTitle(titles, ownerPid);
                    Log.Write("/close-signin-windows: closed " + n + " window(s) under OBS pid=" + ownerPid);
                }
                catch (Exception ex) { Log.Write("/close-signin-windows: " + ex.Message); }
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["closed"] = n });
                return false;
            }

            if (path == "/sign-in-loader")
            {
                // same-origin loading page that the dock's popup navigates to instead of about:blank + document.write. removing the document.write step dodges a cef bug where the popup window's document isnt yet writable at the time window.open returns -- the popup just sits on about:blank forever. by having the helper serve this page directly, the popup gets a proper navigation from the start and paints reliably. the meta-refresh then takes it to streamable.com once the spinner has had a moment to show.
                var h = HttpResponse.GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = "text/html; charset=utf-8" });
                long cb = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string body = "<!doctype html><html><head>\n" +
                    "<meta charset=\"utf-8\">\n" +
                    "<meta http-equiv=\"refresh\" content=\"0.4;url=https://streamable.com/login?_cb=" + cb + "\">\n" +
                    "<title>Signing in to Streamable</title>\n" +
                    "<style>\n" +
                    "html,body{margin:0;height:100%;background:#1D1F26;\n" +
                    "color:#FFFFFF;font:13px \"Segoe UI\",system-ui,sans-serif}\n" +
                    ".wrap{display:flex;flex-direction:column;align-items:center;\n" +
                    "justify-content:center;height:100%;gap:18px}\n" +
                    ".spinner{width:34px;height:34px;border-radius:50%;\n" +
                    "border:3px solid #3C404D;border-top-color:#476BD7;\n" +
                    "animation:r 0.8s linear infinite}\n" +
                    "@keyframes r{to{transform:rotate(360deg)}}\n" +
                    ".label{color:#FFFFFF;font-size:14px;font-weight:500}\n" +
                    ".sub{color:#969696;font-size:12px}\n" +
                    "</style></head><body>\n" +
                    "<div class=\"wrap\">\n" +
                    "<div class=\"spinner\"></div>\n" +
                    "<div class=\"label\">Opening Streamable&hellip;</div>\n" +
                    "<div class=\"sub\">Sign in with Google, Facebook, or email</div>\n" +
                    "</div></body></html>";
                HttpResponse.SendBytes(stream, 200, "OK", h, Encoding.UTF8.GetBytes(body));
                return false;
            }

            if (Regex.IsMatch(path, @"^/obs-icon\.svg$|^/favicon\.ico$"))
            {
                var h = HttpResponse.GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = "image/svg+xml" });
                HttpResponse.SendBytes(stream, 200, "OK", h, Media.GetObsIconSvg());
                return false;
            }

            if (path == "/obs-icon.ico") { HttpResponse.ServeObsIcon(stream); return false; }

            if (path == "/version")
            {
                // local file read only, no github call -- for ui labels that just want "what am i running", not the update-check flow.
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["version"] = Update.GetInstalledVersion() });
                return false;
            }

            if (path == "/open-url")
            {
                // target=_blank inside these docks opens a bare cef popup (obs' own embedded chromium), not the user's actual browser -- shelling out here hands it to whatever the os has registered for http/https instead.
                string target = Q("url");
                bool ok = false;
                if (Regex.IsMatch(target, "^https://[^\\s\"]+$"))
                {
                    try { Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true }); ok = true; }
                    catch (Exception ex) { Log.Write("/open-url failed for '" + target + "': " + ex.Message); }
                }
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = ok });
                return false;
            }

            if (path == "/style-window")
            {
                string title = Q("title", "Clips");
                bool taskbar = QFlag("taskbar");
                int matched = -1;
                try { matched = Native.StyleWindow(title, Constants.OBS_ICON_PATH, taskbar); }
                catch (Exception ex) { Log.Write("/style-window threw: " + ex.Message); }
                // logging only on misses keeps the log readable; success is the defualt, silent path.
                if (matched <= 0) Log.Write("/style-window title=" + title + " : 0 matching OBS-family windows found");
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["matched"] = matched });
                return false;
            }

            if (path == "/focus-window")
            {
                // server-side foreground-raise for an existing dock-spawned popup (e.g. "view clips" clicked while the clips window is already open). the dock's window.focus() is unreliable inside cef; this route uses win32 SetForegroundWindow + the AttachThreadInput dance which works regardless of who currently owns the foreground.
                string title = Q("title", "Clips");
                bool focused = false;
                try { focused = Native.FocusWindow(title); } catch { }
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["focused"] = focused });
                return false;
            }

            if (path == "/close-window")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string title = Q("title");
                if (title != "ReplayKit Settings" && title != "ReplayKit Update")
                {
                    HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Unsupported window title." });
                    return false;
                }
                // the update popup can be hosted by msedge.exe (--app launched from the bootstrap), which is not an obs-family process, so pass 0 for that title so CloseWindowsByTitle finds it by title alone; settings still goes thru the obs-family gate.
                uint ownerPid = title == "ReplayKit Update" ? 0 : (ParentWatchdog.ParentPid > 0 ? (uint)ParentWatchdog.ParentPid : 0);
                int closed = 0;
                try { closed = Native.CloseWindowsByTitle(new[] { title }, ownerPid); } catch { }
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["closed"] = closed });
                return false;
            }

            if (path == "/window/maximize")
            {
                // server-side maximize for a dock-spawned popup. called from clips.html when the user enters fullscreen on a clip so the host window fills the screen. js in cef cant resize its own host, hence the win32 round-trip. the native side snapshots the original rect so /window/restore can put it back unless the user manually unmaximized or dragged the host while fullscreened.
                string title = Q("title", "Clips");
                bool maximized = false;
                try { maximized = Native.MaximizeObsWindow(title); } catch { }
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["maximized"] = maximized });
                return false;
            }

            if (path == "/window/fullscreen")
            {
                string title = Q("title", "Clips");
                bool fullscreen = false;
                try { fullscreen = Native.EnterObsWindowFullscreen(title); } catch { }
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["fullscreen"] = fullscreen });
                return false;
            }

            if (path == "/window/fullscreen/restore")
            {
                string title = Q("title", "Clips");
                bool restored = false;
                try { restored = Native.ExitObsWindowFullscreen(title); } catch { }
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["restored"] = restored });
                return false;
            }

            if (path == "/window/restore")
            {
                // counterpart to /window/maximize: restore the saved rect only if the Clips host is still maximized. if the user dragged or unmaximized it while fullscreened, their manual placement wins.
                string title = Q("title", "Clips");
                bool restored = false;
                try { restored = Native.RestoreObsWindow(title); } catch { }
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["restored"] = restored });
                return false;
            }

            if (path == "/window/resize")
            {
                // drag-to-resize for the borderless popups, started from the resize-grip dots the dock/clips pages draw in their bottom-right corner. BeginResizeWindow polls the mouse button instead of a message-based handoff -- see its comment in Native.cs. each window gets its own floor/ceiling since Settings is a fixed two-pane form while Clips is a fluid grid that tolerates a smaller floor; both axes always move together off the one drag.
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string title = Q("title");
                int minW, minH, maxW = 2400, maxH = 1800;
                // kept in sync with the native QWidget::setMinimumSize() calls in replaykit.cpp's CreateSettingsWindow/CreateClipsWindow -- this custom corner-grip resize used to enforce different bounds than obs's own native edge/corner resize on the same windows, so the two disagreed about how small each window could get depending on which resize handle you used.
                if (title == "ReplayKit Settings") { minW = 700; minH = 500; }
                else if (title == "Clips") { minW = 850; minH = 620; }
                else if (title == "ReplayKit Update") { minW = 480; minH = 400; }
                else { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Unsupported window title." }); return false; }
                bool resizing = false;
                try { resizing = Native.BeginResizeWindow(title, minW, minH, maxW, maxH); } catch { }
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["resizing"] = resizing });
                return false;
            }

            if (path == "/window/set-size")
            {
                // one-shot corrective resize for a window we just spawned via "chrome --app=" -- see SetWindowSizeCentered's comment for why the spawn-time --window-size flag alone is not reliable.
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string title = Q("title");
                if (!int.TryParse(Q("w"), out int w) || !int.TryParse(Q("h"), out int h) || w <= 0 || h <= 0)
                {
                    HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Missing or invalid w/h." });
                    return false;
                }
                bool applied = false;
                try { applied = Native.SetWindowSizeCentered(title, w, h); } catch { }
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["applied"] = applied });
                return false;
            }

            if (path == "/open_clips")
            {
                var h = HttpResponse.GetNoStoreHeaders(new Dictionary<string, string> { ["Location"] = "/clips-view" });
                HttpResponse.SendBytes(stream, 302, "Found", h, new byte[0]);
                return false;
            }

            if (path == "/open-clips-window")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                if (!TestSettingsOrigin(req)) { HttpResponse.SendJson(stream, 403, new JObject { ["ok"] = false, ["message"] = "Untrusted origin." }); return false; }
                PipeClient.SendOpenClips();
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true });
                return false;
            }

            if (path == "/status") { HttpResponse.SendJson(stream, 200, UploadState.GetUploadStatusSnapshot()); return false; }

            if (path == "/clips/state")
            {
                if (!TestSettingsOrigin(req)) { HttpResponse.SendJson(stream, 403, new JObject { ["ok"] = false, ["message"] = "Untrusted origin." }); return false; }
                try
                {
                    if (req.Method == "GET") { HttpResponse.SendJson(stream, 200, Clips.GetClipUiStatePayload()); return false; }
                    if (req.Method == "POST") { HttpResponse.SendJson(stream, 200, Clips.SaveClipUiStateFromJson(req.Body)); return false; }
                    HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET or POST required");
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/clips/by-name")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try
                {
                    string sort = Q("sort", "newest");
                    var names = new List<string>();
                    if (!string.IsNullOrWhiteSpace(req.Body))
                    {
                        var incoming = JObject.Parse(req.Body);
                        if (incoming["names"] is JArray namesArr)
                            foreach (var n in namesArr) names.Add(n.Value<string>());
                    }
                    HttpResponse.SendText(stream, 200, "OK", Clips.GetClipsByNameJson(names, sort), "application/json; charset=utf-8");
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 400, new JObject { ["error"] = ex.Message }); }
                return false;
            }

            if (path == "/clips")
            {
                try
                {
                    if (QFlag("refresh")) AppConfig.ClearClipsCache();
                    if (query.ContainsKey("offset") || query.ContainsKey("limit"))
                    {
                        int offset = 0;
                        int limit = Constants.CLIPS_PAGE_LIMIT_MAX;
                        string sort = Q("sort", "newest");
                        if (query.ContainsKey("offset")) int.TryParse(Q("offset"), out offset);
                        if (query.ContainsKey("limit")) int.TryParse(Q("limit"), out limit);
                        HttpResponse.SendText(stream, 200, "OK", Clips.GetClipsPageJson(offset, limit, sort), "application/json; charset=utf-8");
                    }
                    else
                    {
                        HttpResponse.SendText(stream, 200, "OK", Clips.GetClipsListJson(), "application/json; charset=utf-8");
                    }
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["error"] = ex.Message }); }
                return false;
            }

            if (path == "/capabilities")
            {
                var caps = Compression.GetHelperCapabilities();
                HttpResponse.SendJson(stream, 200, new JObject
                {
                    ["ok"] = true,
                    ["ffmpeg"] = !string.IsNullOrEmpty(caps["ffmpeg"]?.Value<string>()),
                    ["ffprobe"] = !string.IsNullOrEmpty(caps["ffprobe"]?.Value<string>()),
                    ["logging"] = caps["logging"]?.Value<bool>() ?? false,
                });
                return false;
            }

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

            if (path.StartsWith("/file/"))
            {
                // only route allowed to keep the connection open past this request -- see HttpResponse.ServePreview.
                return HttpResponse.ServePreview(stream, req, path.Substring(6));
            }

            if (path.StartsWith("/thumb/")) { HttpResponse.ServeThumbnail(stream, path.Substring(7)); return false; }

            if (path == "/open-clip-folder")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string root = Path.GetFullPath(AppConfig.GetClipDir());
                if (!Directory.Exists(root)) { HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false, ["message"] = "Clip folder not found" }); return false; }
                try
                {
                    // dedupe: if explorer already has the clip folder open, raise it instead of spawning another window. falls thru to the normal launch path if the com probe fails or no match is found.
                    var existing = GetExplorerHwndForPath(root);
                    if (existing != IntPtr.Zero)
                    {
                        bool focused2 = Native.FocusHwnd(existing);
                        HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["focused"] = focused2 });
                        return false;
                    }
                    Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = ProcessArgs.Quote(root), UseShellExecute = false });
                    HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["focused"] = false });
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
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
                    Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = ProcessArgs.Quote(root), UseShellExecute = false });
                    HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["focused"] = false });
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/open-folder")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                var selected = Clips.GetSafeClipPath(query.ContainsKey("file") ? query["file"] : null);
                if (selected == null || !File.Exists(selected.Full)) { HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false, ["message"] = "Clip not found" }); return false; }
                try
                {
                    // if explorer already has the containing folder open, raise that window. we deliberately dont try to re-select the file via shell.application -- the com dance is fragile and the user clicking "open file location" from a clip card is usually just trying to surface the folder, not the specific row.
                    string parent = Path.GetDirectoryName(selected.Full);
                    if (!string.IsNullOrEmpty(parent))
                    {
                        var existing = GetExplorerHwndForPath(parent);
                        if (existing != IntPtr.Zero)
                        {
                            bool focused2 = Native.FocusHwnd(existing);
                            HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["focused"] = focused2 });
                            return false;
                        }
                    }
                    // explorer.exe /select,"<path>" opens the containing folder with the file already highlighted. arguments must be one string becuase explorer parses /select,<rest> as a unit.
                    Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = "/select," + ProcessArgs.Quote(selected.Full), UseShellExecute = false });
                    HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["focused"] = false });
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/delete")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                var selected = Clips.GetSafeClipPath(query.ContainsKey("file") ? query["file"] : null);
                if (selected == null || !File.Exists(selected.Full)) { HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false, ["message"] = "Clip not found" }); return false; }
                if (UploadState.TestClipHasActiveUploadJob(selected.Name)) { HttpResponse.SendJson(stream, 409, new JObject { ["ok"] = false, ["message"] = "Clip is currently being processed" }); return false; }
                try
                {
                    File.Delete(selected.Full);
                    lock (Server.State.ClipsMetaLock)
                    {
                        var db = Clips.ReadClipsDb();
                        if (db.ContainsKey(selected.Name)) { db.Remove(selected.Name); Clips.SaveClipsDb(db); }
                        AppConfig.ClearClipsCache();
                    }
                    HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["name"] = selected.Name });
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

            if (path == "/trim")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string file = Q("file");
                string mode = Q("mode", "copy");
                // precise=1 -> re-encode for frame-accurate cuts. precise=0 (default) -> stream-copy, keyframe-snapped (fast lossless).
                string preciseStr = Q("precise", "0");
                bool precise = preciseStr != "0" && preciseStr.ToLowerInvariant() != "false";
                string removeAudioStr = Q("removeAudio", "0");
                bool removeAudio = removeAudioStr != "0" && removeAudioStr.ToLowerInvariant() != "false";
                double.TryParse(Q("start"), NumberStyles.Float, CultureInfo.InvariantCulture, out double startSec);
                double.TryParse(Q("end"), NumberStyles.Float, CultureInfo.InvariantCulture, out double endSec);
                try
                {
                    var result = Trim.InvokeClipTrim(file, startSec, endSec, mode, precise, removeAudio);
                    HttpResponse.SendJson(stream, result.Ok ? 200 : 400, TrimResultToJson(result));
                }
                catch (Exception ex)
                {
                    Log.Write("/trim threw: " + ex.Message + "\n" + ex.StackTrace);
                    HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message });
                }
                return false;
            }

            if (path == "/trim/keyframes")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                string file = Q("file");
                double.TryParse(Q("duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out double durationSec);
                try
                {
                    var result = Trim.GetClipKeyframeTimes(file, durationSec);
                    int code = result.Ok ? 200 : result.Pending ? 202 : 400;
                    HttpResponse.SendJson(stream, code, KeyframeResultToJson(result));
                }
                catch (Exception ex)
                {
                    Log.Write("/trim/keyframes threw: " + ex.Message + "\n" + ex.StackTrace);
                    HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message, ["keyframes"] = new JArray() });
                }
                return false;
            }

            if (path == "/clipboard")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string file = Q("file");
                try
                {
                    var result = Trim.SetFileClipboard(file);
                    HttpResponse.SendJson(stream, result.Ok ? 200 : 400, TrimResultToJson(result));
                }
                catch (Exception ex)
                {
                    Log.Write("/clipboard threw: " + ex.Message + "\n" + ex.StackTrace);
                    HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message });
                }
                return false;
            }

            if (path == "/compress-overwrite")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string file = Q("file");
                string mode = Q("mode", "smaller");
                int.TryParse(Q("scaleHeight"), out int scaleHeight);
                // compress-history rules (enforced server-side as defense in depth; the dock ui also disables the relevant card): clip already marked slow (libx265 medium) -> reject entirely, its the best mode and re-running anything would just degrade quality without size gain. clip already marked fast (libx264 fast) + fast again requested -> reject, re-fast-compressing produces no meaningful gain. re-running with smaller is allowed (fast -> slow is a valid upgrade path).
                var checkSel = Clips.GetSafeClipPath(file);
                if (checkSel != null && File.Exists(checkSel.Full))
                {
                    var existingMarker = Compression.GetCompressMarker(checkSel.Full);
                    string existingMode = existingMarker.Mode;
                    if (existingMode == "slow")
                    {
                        HttpResponse.SendJson(stream, 409, new JObject { ["ok"] = false, ["message"] = "Already at maximum compression (slow mode).", ["alreadyCompressed"] = true, ["existingMode"] = "slow" });
                        return false;
                    }
                    if (existingMode == "fast" && mode == "fast")
                    {
                        HttpResponse.SendJson(stream, 409, new JObject { ["ok"] = false, ["message"] = "Already fast-compressed. Choose Smaller for further reduction.", ["alreadyCompressed"] = true, ["existingMode"] = "fast" });
                        return false;
                    }
                }
                var selected = Clips.GetSafeClipPath(file);
                if (selected == null || !File.Exists(selected.Full)) { HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false, ["message"] = "Clip not found" }); return false; }
                try
                {
                    var result = CompressOverwrite.StartCompressOverwriteFile(selected, mode, scaleHeight);
                    Log.Write("/compress-overwrite(" + selected.Name + ", mode=" + mode + ", scaleHeight=" + scaleHeight + ") -> " + result.ToString(Newtonsoft.Json.Formatting.None));
                    int code = result["ok"]?.Value<bool>() == true ? 200 : result["busy"]?.Value<bool>() == true ? 409 : 400;
                    HttpResponse.SendJson(stream, code, result);
                }
                catch (Exception ex)
                {
                    Log.Write("/compress-overwrite threw: " + ex.Message + "\n" + ex.StackTrace);
                    HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message });
                }
                return false;
            }

            if (path == "/rename")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                var selected = Clips.GetSafeClipPath(query.ContainsKey("file") ? query["file"] : null);
                if (selected == null || !File.Exists(selected.Full)) { HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false, ["message"] = "Source clip not found" }); return false; }
                string newName = Clips.GetSafeFilename(query.ContainsKey("to") ? query["to"] : null);
                if (newName == null) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Bad new filename" }); return false; }
                if (newName == selected.Name) { HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["name"] = newName }); return false; }
                string newPath = Path.Combine(AppConfig.GetClipDir(), newName);
                if (File.Exists(newPath)) { HttpResponse.SendJson(stream, 409, new JObject { ["ok"] = false, ["message"] = "A file with that name already exists" }); return false; }
                try
                {
                    File.Move(selected.Full, newPath);
                    // preserve the streamable url association across the rename so the clip card still shows "copy link" instead of silently going back to "create link".
                    lock (Server.State.ClipsMetaLock)
                    {
                        var db = Clips.ReadClipsDb();
                        if (db.ContainsKey(selected.Name)) { db[newName] = db[selected.Name]; db.Remove(selected.Name); Clips.SaveClipsDb(db); }
                        AppConfig.ClearClipsCache();
                    }
                    HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["name"] = newName });
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/me")
            {
                var a = Server.State.Auth;
                string masked = Constants.GetMaskedIdentity(a.Username);
                HttpResponse.SendJson(stream, 200, new JObject
                {
                    ["signedIn"] = a.SignedIn,
                    ["username"] = "",
                    ["displayName"] = a.SignedIn ? "Signed in" : "",
                    ["maskedUsername"] = a.SignedIn ? masked : "",
                    ["plan"] = a.Plan,
                    ["sizeCap"] = a.SizeCap,
                    ["retentionDays"] = a.RetentionDays,
                });
                return false;
            }

            if (path == "/logout")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try { AuthCore.StreamableBackgroundLogout(); } catch (Exception ex) { Log.Write("Background logout failed: " + ex.Message); }
                AuthCore.ClearAuth();
                AppConfig.ClearClipsCache();
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true });
                return false;
            }

            if (path == "/import-session")
            {
                // find a chromium-based browser on the box that has a signed-in streamable.com session (edge, chrome, brave, vivaldi, opera, or obs's own cef), decrypt the cookies, validate against /me, and adopt them as our auth state. the find-and-validate logic is shared with startup restore so we dont pick up stale logged-out cookies and clobber working auth.
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try
                {
                    string sourceParam = Q("source");
                    bool obsOnly = sourceParam == "obs";
                    Log.Write("/import-session source='" + sourceParam + "' obsOnly=" + obsOnly);
                    var result = BrowserCookies.FindWorkingStreamableSession(obsOnly);
                    if (!result.Ok)
                    {
                        HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false, ["message"] = result.Error });
                        return false;
                    }
                    AuthCore.SaveAuthBlob(result.Jar, result.User);
                    AuthCore.ApplyAuth(result.User, result.Jar);
                    AppConfig.ClearClipsCache();
                    var a = Server.State.Auth;
                    string masked = Constants.GetMaskedIdentity(a.Username);
                    Log.Write("Imported session from " + result.SourceName + " for " + a.Username + " (plan=" + a.Plan + ")");
                    HttpResponse.SendJson(stream, 200, new JObject
                    {
                        ["ok"] = true,
                        ["source"] = result.SourceName,
                        ["signedIn"] = a.SignedIn,
                        ["username"] = "",
                        ["displayName"] = a.SignedIn ? "Signed in" : "",
                        ["maskedUsername"] = a.SignedIn ? masked : "",
                        ["plan"] = a.Plan,
                        ["sizeCap"] = a.SizeCap,
                        ["retentionDays"] = a.RetentionDays,
                    });
                }
                catch (Exception ex)
                {
                    Log.Write("/import-session threw: " + ex.Message + "\n" + ex.StackTrace);
                    HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message });
                }
                return false;
            }

            if (path == "/shutdown")
            {
                HttpResponse.SendText(stream, 200, "OK", "bye");
                AppConfig.StopClipFolderWatcher();
                Server.State.Shutdown = true;
                return false;
            }

            if (path == "/restart-obs")
            {
                // plain restart -- close and reopen obs, no streamable sign-out, distinct from /restart-obs-clean below which forces a re-login; same relaunch mechanics as that route and the /settings restart-required branch: spawn the detached relauncher (survives this process dying, waits for the obs pid we hand it, then launches fresh), respond, then force-kill obs and set shutdown so our own accept loop exits and this port frees up for the new helper the relaunched obs will spawn.
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string obsPath = GetRunningObsPath();
                if (obsPath == null) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = "Could not locate OBS executable path to relaunch from." }); return false; }
                ArmRelaunchAndCloseObs(obsPath, "/restart-obs");
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["message"] = "OBS is restarting." });
                StopObsForRestart("/restart-obs");
                return false;
            }

            if (path == "/exit-obs")
            {
                // plain graceful exit, no relaunch -- reuses the same StopObsForRestart used by /restart-obs above, so the tray menu's Exit button gets the same close-main-window-first-then-force-kill-if-needed behavior as a restart or clicking obs's own X button, instead of skipping straight to a force-kill.
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["message"] = "OBS is closing." });
                StopObsForRestart("/exit-obs");
                return false;
            }

            if (path == "/restart-obs-clean")
            {
                // full-cycle "force re-login": note the obs executable path before we kill it, wipe the saved auth state immediately, send the response so the dock can react, then force-close obs64 + every obs-browser-page child. sets shutdown = true so our accept loop exits and the shutdown path runs the cookie cleanup (which needs the file unlocked, which happens once obs is dead), then relaunches obs via the path we saved.
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string obsPath = GetRunningObsPath();
                if (obsPath == null) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = "Could not locate OBS executable path to relaunch from." }); return false; }
                try { AuthCore.ClearAuthBlob(); } catch { }
                try { AuthCore.ClearAuth(); } catch { }
                Server.State.ClearStreamableOnExit = true;
                Server.State.RestartAfterCleanObsPath = obsPath;
                Log.Write("/restart-obs-clean: queued. Will kill OBS, wipe streamable cookies, relaunch from '" + obsPath + "'.");
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["message"] = "OBS will restart with a clean Streamable session." });

                // give the response a moment to flush before we kill obs (otherwise the dock's fetch sees a torn connection).
                StopObsForRestart("/restart-obs-clean");
                return false;
            }

            HttpResponse.SendText(stream, 404, "Not Found", "Not found");
            return false;
        }
    }
}

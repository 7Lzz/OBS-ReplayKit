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
    // the routes of the obs window, the shell, file pickers, confirmations and the windows of the plugin
    internal static class WindowRoutes
    {
        // answers the request when its path is one of these routes, with what DispatchRequest returns; null when it is not
        public static bool? Handle(RouteRequest r)
        {
            Stream stream = r.Stream;
            HttpRequest req = r.Req;
            string path = r.Path;
            string Q(string key, string def = "") => r.Q(key, def);
            bool QFlag(string key) => r.QFlag(key);


            // dock -> plugin bridge: the dock cannot draw a modal past its own panel, so it asks the native plugin to show a themed confirm over the whole dimmed obs window. the plugin runs the matching action on ok.
            if (path == "/ui-confirm")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try
                {
                    string kind = string.IsNullOrWhiteSpace(req.Body) ? null : JObject.Parse(req.Body).Value<string>("kind");
                    if (kind != "sharepreview-setup" && kind != "streamable-relogin")
                        throw new InvalidOperationException("Unknown confirm kind.");
                    PipeClient.SendUiConfirm(kind);
                    HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true });
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/open-url")
            {
                // target=_blank inside these docks opens a bare cef popup (obs' own embedded chromium), not the users actual browser -- shelling out here hands it to whatever the os has registered for http/https instead.
                string target = Q("url");
                bool ok = false;
                if (Regex.IsMatch(target, "^https://[^\\s\"]+$"))
                {
                    try { Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true })?.Dispose(); ok = true; }
                    catch (Exception ex) { Log.Write("/open-url failed for '" + target + "': " + ex.Message); }
                }
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = ok });
                return false;
            }

            if (path == "/browse-file" || path == "/browse-folder")
            {
                bool folder = path == "/browse-folder";
                string picked = FilePickers.PickPath(folder, Q("filter", "Images (*.ico;*.png;*.jpg;*.bmp)|*.ico;*.png;*.jpg;*.jpeg;*.bmp|All files (*.*)|*.*"), Q("initial", ""));
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = !string.IsNullOrEmpty(picked), ["path"] = picked });
                return false;
            }

            if (path == "/focus-window")
            {
                // server-side foreground-raise for an existing dock-spawned popup (e.g. "view clips" clicked while the clips window is already open). the docks window.focus() is unreliable inside cef; this route uses win32 SetForegroundWindow + the AttachThreadInput dance which works regardless of who currently owns the foreground.
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
                if (title != "ReplayKit Settings" && title != "ReplayKit Update" && title != "ReplayKit Setup")
                {
                    HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Unsupported window title." });
                    return false;
                }
                // a page the clean layout embeds in the obs window has no window of its own to close; the plugin just puts the obs window back on its home view
                if (QFlag("embedded") && title != "ReplayKit Update")
                {
                    PipeClient.SendCloseView(title == "ReplayKit Setup" ? "setup" : "settings");
                    HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["closed"] = 1 });
                    return false;
                }
                // the update popup can be hosted by msedge.exe (--app launched from the bootstrap), which is not an obs-family process, so pass 0 for that title so CloseWindowsByTitle finds it by title alone; settings still goes thru the obs-family gate.
                uint ownerPid = title == "ReplayKit Update" ? 0 : (ParentWatchdog.ParentPid > 0 ? (uint)ParentWatchdog.ParentPid : 0);
                int closed = 0;
                // settings and setup are only hidden by the plugin and shown again later, so their taskbar tab has to survive the close; only the update popup really goes away
                try { closed = Native.CloseWindowsByTitle(new[] { title }, ownerPid, title == "ReplayKit Update"); } catch { }
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
                // embedded in the obs window: the plugin widens the page over the whole window, there is no host window to make fullscreen
                if (QFlag("embedded") && title == "Clips")
                {
                    PipeClient.SendClipsFullscreen(true);
                    HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["fullscreen"] = true });
                    return false;
                }
                if (title == "Clips") PipeClient.SendClipsFullscreen(true);
                try { fullscreen = Native.EnterObsWindowFullscreen(title); } catch { }
                if (!fullscreen && title == "Clips") PipeClient.SendClipsFullscreen(false);
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["fullscreen"] = fullscreen });
                return false;
            }

            if (path == "/window/fullscreen/restore")
            {
                string title = Q("title", "Clips");
                bool restored = false;
                if (QFlag("embedded") && title == "Clips")
                {
                    PipeClient.SendClipsFullscreen(false);
                    HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["restored"] = true });
                    return false;
                }
                try { restored = Native.ExitObsWindowFullscreen(title); } catch { }
                if (title == "Clips") PipeClient.SendClipsFullscreen(false);
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
                // kept in sync with the native QWidget::setMinimumSize() calls in the native plugins CreateSettingsWindow/CreateClipsWindow (windows/settings_window.cpp, windows/clips_window.cpp) -- this custom corner-grip resize used to enforce different bounds than obss own native edge/corner resize on the same windows, so the two disagreed about how small each window could get depending on which resize handle you used.
                if (title == "ReplayKit Settings") { minW = 700; minH = 500; }
                else if (title == "Clips") { minW = 850; minH = 620; }
                else if (title == "ReplayKit Update") { minW = 480; minH = 400; }
                else if (title == "ReplayKit Setup") { minW = 640; minH = 520; }
                else { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Unsupported window title." }); return false; }
                bool resizing = false;
                try { resizing = Native.BeginResizeWindow(title, minW, minH, maxW, maxH); } catch { }
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["resizing"] = resizing });
                return false;
            }

            // a page of the clean shell grabbed at its right edge, bottom edge or the corner between them, which is where the window of obs is resized now that the pages reach those edges; the plugin runs the resize loop of windows inside obs, which repaints as it goes where moving the window from out here left white and stale areas
            if (path == "/shell/resize")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string edge = Q("edge");
                if (edge != "right" && edge != "bottom" && edge != "corner") { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Unknown edge." }); return false; }
                bool resizing = Native.ObsMainResizable() && PipeClient.SendShellResize(edge);
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["resizing"] = resizing });
                return false;
            }

            if (path == "/window/set-size")
            {
                // one-shot corrective resize for a window we just spawned via "chrome --app=" -- see SetWindowSizeCentereds comment for why the spawn-time --window-size flag alone is not reliable.
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
                PipeClient.SendOpenClips();
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true });
                return false;
            }

            if (path == "/open-settings-window")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                // the tab to land on; anything that is not a short lowercase word is dropped, since the plugin puts it into a script for the shell page
                string settingsPage = Q("page");
                if (!Regex.IsMatch(settingsPage, @"^[a-z]{1,20}$")) settingsPage = "";
                PipeClient.SendOpenSettings(settingsPage);
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true });
                return false;
            }

            if (path == "/open-setup-window")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                lock (Server.State.UpdateCheckLock) Server.State.SetupWizardOpened = true;
                PipeClient.SendOpenSetup();
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true });
                return false;
            }

            // the shell page (the one browser the clean layout keeps inside the obs window) says which view it now shows, so the plugin lifts it above the preview only once the right page is really on screen.
            if (path == "/shell/shown")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string view = Q("view");
                if (view != "home" && view != "clips" && view != "settings" && view != "setup") { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Unknown view." }); return false; }
                PipeClient.SendViewShown(view);
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true });
                return false;
            }

            // the shell page answering a ping of the plugin, with the number the ping carried; the plugin reloads a page that stays silent, since a page whose renderer died stays blank until the browser is reloaded
            if (path == "/shell/ping")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                if (!int.TryParse(Q("n"), NumberStyles.None, CultureInfo.InvariantCulture, out int pingNumber) || pingNumber < 1) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Expected n." }); return false; }
                PipeClient.SendShellPong(pingNumber);
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true });
                return false;
            }

            // the settings page reports unsaved edits, and later its answer to the unsaved-changes prompt (save or leave) with the view that was picked
            if (path == "/shell/settings-dirty" || path == "/shell/leave-settings")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                if (path == "/shell/settings-dirty")
                {
                    string dirtyFlag = Q("dirty");
                    if (dirtyFlag != "0" && dirtyFlag != "1") { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Expected dirty 0 or 1." }); return false; }
                    PipeClient.SendSettingsDirty(dirtyFlag == "1");
                }
                else
                {
                    string target = Q("to");
                    if (target != "home" && target != "clips" && target != "setup" && target != "projector") { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Unknown view." }); return false; }
                    PipeClient.SendSettingsLeave(target);
                }
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true });
                return false;
            }

            // a page of the shell saw alt + an arrow or a side button of the mouse; the plugin keeps the history of the views, so the page only says which way
            if (path == "/shell/nav")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string direction = Q("dir");
                if (direction != "back" && direction != "forward") { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Expected dir back or forward." }); return false; }
                PipeClient.SendShellNav(direction);
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true });
                return false;
            }

            // the clips page says a clip is playing, or that it stopped, so the display stays on for as long as one plays; PlaybackWake holds the windows request and lets a lease lapse on its own if the page dies
            if (path == "/power/playing")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string source = Q("src");
                string playing = Q("state");
                if (!Regex.IsMatch(source, "^[A-Za-z0-9_-]{1,40}$") || (playing != "1" && playing != "0"))
                {
                    HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Expected src and state (1 or 0)." });
                    return false;
                }
                bool holding = PlaybackWake.Report(source, playing == "1");
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["holding"] = holding });
                return false;
            }

            // "OBS Studio Settings" in the dock -- the plugin triggers OBSs own Settings dialog.
            if (path == "/open-obs-settings")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                PipeClient.SendOpenObsSettings();
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true });
                return false;
            }
            if (path == "/quick-actions/state")
            {
                if (req.Method == "POST") { var set = QuickActions.Set(Q("view"), req.Body); HttpResponse.SendJson(stream, set.Value<bool>("ok") ? 200 : 400, set); return false; }
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET or POST required"); return false; }
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["actions"] = QuickActions.Get(Q("view")) });
                return false;
            }
            if (path == "/quick-actions/run")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string view = Q("view"), id = Q("id");
                bool sent = QuickActions.IsView(view) && QuickActions.IsId(id.StartsWith("page-", StringComparison.Ordinal) ? id.Substring(5) : id) && PipeClient.SendQuickAction(view, id);
                HttpResponse.SendJson(stream, sent ? 200 : 400, new JObject { ["ok"] = sent });
                return false;
            }

            if (path == "/obs-menu")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                if (!int.TryParse(Q("index", ""), out int menuIndex) || menuIndex < 0 || menuIndex > 15) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Bad menu." }); return false; }
                PipeClient.SendObsMenu(menuIndex);
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true });
                return false;
            }

            return null;
        }
    }
}

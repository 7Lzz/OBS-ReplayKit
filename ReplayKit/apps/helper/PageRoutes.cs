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
    // the routes of the pages, the dock script and the static art and fonts the pages load
    internal static class PageRoutes
    {
        // answers the request when its path is one of these routes, with what DispatchRequest returns; null when it is not
        public static bool? Handle(RouteRequest r)
        {
            Stream stream = r.Stream;
            HttpRequest req = r.Req;
            string path = r.Path;
            bool QFlag(string key) => r.QFlag(key);


            if (Regex.IsMatch(path, @"^/(?:controls\.html)?$")) { HttpResponse.ServeHtml(stream, "controls.html"); return false; }
            if (Regex.IsMatch(path, @"^/controls_app\.html$")) { HttpResponse.ServeHtml(stream, "controls_app.html"); return false; }
            // the clean layout frames these inside its shell page and popup.html frames the menus and cards (?embedded=1), which needs same-origin framing; a page loaded on its own keeps the DENY
            bool framed = QFlag("embedded");
            if (path == "/clip-app.js") { HttpResponse.ServeDockScript(stream); return false; }
            if (path == "/home-view") { HttpResponse.ServeHtml(stream, "home.html", framed); return false; }
            if (Regex.IsMatch(path, @"^/clips-view$|^/clips\.html$")) { HttpResponse.ServeHtml(stream, QFlag("home") ? "home.html" : "clips.html", framed); return false; }
            if (path == "/popup-view") { HttpResponse.ServeHtml(stream, "popup.html"); return false; }
            if (path == "/editor-view") { HttpResponse.ServeHtml(stream, "editor.html"); return false; }
            if (Regex.IsMatch(path, @"^/settings-view$|^/settings\.html$")) { HttpResponse.ServeHtml(stream, "settings.html", framed); return false; }
            if (Regex.IsMatch(path, @"^/setup-view$|^/setup\.html$")) { HttpResponse.ServeHtml(stream, "setup.html", framed); return false; }
            if (Regex.IsMatch(path, @"^/shell-view$|^/shell\.html$")) { HttpResponse.ServeHtml(stream, "shell.html"); return false; }
            if (Regex.IsMatch(path, @"^/update-prompt$|^/update-prompt\.html$")) { HttpResponse.ServeHtml(stream, "update_prompt.html"); return false; }
            if (Regex.IsMatch(path, @"^/notifications-view$|^/notifications\.html$")) { HttpResponse.ServeHtml(stream, "notifications.html", framed); return false; }
            if (Regex.IsMatch(path, @"^/account-menu$|^/account-menu\.html$")) { HttpResponse.ServeHtml(stream, "account-menu.html", framed); return false; }
            if (Regex.IsMatch(path, @"^/quick-actions-view$|^/quick-actions\.html$")) { HttpResponse.ServeHtml(stream, "quick-actions.html", framed); return false; }
            if (Regex.IsMatch(path, @"^/hotkey-menu$|^/hotkey-menu\.html$")) { HttpResponse.ServeHtml(stream, "hotkey-menu.html", framed); return false; }
            if (Regex.IsMatch(path, @"^/storage-view$|^/storage\.html$")) { HttpResponse.ServeHtml(stream, "storage.html", framed); return false; }
            if (Regex.IsMatch(path, @"^/notification-reader-view$|^/notification-reader\.html$")) { HttpResponse.ServeHtml(stream, "notification-reader.html", framed); return false; }
            if (Regex.IsMatch(path, @"^/done-popup-view$|^/done-popup\.html$")) { HttpResponse.ServeHtml(stream, "done-popup.html", framed); return false; }

            // same-origin splash the streamable sign-in popup navigates to (see the file for why); served through ServeHtml so it picks up the active theme + a real Content-Type instead of about:blank + document.write
            if (path == "/sign-in-loader") { HttpResponse.ServeHtml(stream, "sign-in-loader.html"); return false; }

            // static .svg files under icons/ -- one subfolder deep max, no traversal. used by the dock CSS (mask/background) where window.RK_ICONS (js-only) does not reach.
            var iconFile = Regex.Match(path, @"^/icons/([A-Za-z0-9_-]+/[A-Za-z0-9_-]+\.svg)$");
            if (iconFile.Success)
            {
                string full = Path.GetFullPath(Path.Combine(Constants.APP_ICONS_DIR, iconFile.Groups[1].Value));
                string root = Path.GetFullPath(Constants.APP_ICONS_DIR) + Path.DirectorySeparatorChar;
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
                {
                    var hi = HttpResponse.GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = "image/svg+xml" });
                    HttpResponse.SendBytes(stream, 200, "OK", hi, File.ReadAllBytes(full));
                }
                else HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false });
                return false;
            }

            // the bundled fonts by file name, for the subtitles the editor previews; no folders, no traversal
            var fontFile = Regex.Match(path, @"^/fonts/([A-Za-z0-9_-]+\.ttf)$");
            if (fontFile.Success)
            {
                string full = Path.Combine(Constants.APP_FONTS_DIR, fontFile.Groups[1].Value);
                if (File.Exists(full)) HttpResponse.SendBytes(stream, 200, "OK", new Dictionary<string, string> { ["Content-Type"] = "font/ttf", ["Cache-Control"] = "max-age=86400" }, File.ReadAllBytes(full));
                else HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false });
                return false;
            }

            // artwork for the update window: obs-custom-dock/update-art.png (raster, preferred) or update-art.svg, served from whichever exists so the art can be swapped without touching the page.
            if (path == "/update-art")
            {
                string dockDir = AppConfig.GetDockDir();
                foreach (var candidate in new[] { "update-art.png", "update-art.svg" })
                {
                    string full = Path.Combine(dockDir, candidate);
                    if (!File.Exists(full)) continue;
                    string ext = Path.GetExtension(candidate);
                    string ctype = ext == ".svg" ? "image/svg+xml" : "image/png";
                    var ha = HttpResponse.GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = ctype });
                    HttpResponse.SendBytes(stream, 200, "OK", ha, File.ReadAllBytes(full));
                    return false;
                }
                HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false });
                return false;
            }

            if (Regex.IsMatch(path, @"^/obs-icon\.svg$|^/favicon\.ico$"))
            {
                var h = HttpResponse.GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = "image/svg+xml" });
                HttpResponse.SendBytes(stream, 200, "OK", h, Media.GetObsIconSvg());
                return false;
            }

            if (path == "/obs-icon.ico") { HttpResponse.ServeObsIcon(stream); return false; }

            return null;
        }
    }
}

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
    // the routes of the overlay preview, its position and the bongo cat and input overlay files it draws
    internal static class OverlayRoutes
    {
        // answers the request when its path is one of these routes, with what DispatchRequest returns; null when it is not
        public static bool? Handle(RouteRequest r)
        {
            Stream stream = r.Stream;
            HttpRequest req = r.Req;
            string path = r.Path;
            string Q(string key, string def = "") => r.Q(key, def);


            // primary-desktop jpeg for the overlay-preview backdrop; a grab is shared for a moment (see GetDesktopPreviewJpeg), served no-store, and the page cache-busts each fetch so cef never shows a stale one.
            if (path == "/desktop-preview.jpg")
            {
                byte[] jpeg = null;
                try { jpeg = GetDesktopPreviewJpeg(); }
                catch (Exception ex) { Log.Write("desktop-preview capture failed: " + ex.Message); }
                if (jpeg == null || jpeg.Length == 0)
                {
                    HttpResponse.SendBytes(stream, 503, "Service Unavailable", HttpResponse.GetNoStoreHeaders(), new byte[0]);
                    return false;
                }
                var hj = HttpResponse.GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = "image/jpeg" });
                HttpResponse.SendBytes(stream, 200, "OK", hj, jpeg);
                return false;
            }

            if (path == "/settings/overlay-preview")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try
                {
                    string mode = Q("mode", "preview");
                    HttpResponse.SendJson(stream, 200, SettingsOverlayLive.OverlayPreviewFromRequest(req.Body, mode));
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            // the overlay items live rect on the canvas, and a setter the Apply path calls once the user has dragged it in the in-page projector. obss own scene item stays the source of truth for position, so a drag done in the projector and one done in obs converge on the same value.
            if (path == "/overlay/position")
            {
                // read-only: a drag is saved as the overlayPosition setting and reaches obs through the normal settings apply, so it survives the scene rewrite + restart an overlay-style change forces
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                HttpResponse.SendJson(stream, 200, ReplaykitSettings.GetOverlayPositionLive());
                return false;
            }

            // the Bango Cat folder of obs for the overlay preview: its modes with their configs and model bakes, and its pictures by path inside it
            if (path == "/bongo-cat/manifest")
            {
                HttpResponse.SendJson(stream, 200, BongoCatFiles.Manifest());
                return false;
            }
            if (path == "/bongo-cat/picture")
            {
                string picture = BongoCatFiles.Picture(Q("path", ""));
                if (picture == null) { HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false }); return false; }
                var hb = HttpResponse.GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = "image/png" });
                HttpResponse.SendBytes(stream, 200, "OK", hb, File.ReadAllBytes(picture));
                return false;
            }

            // the wasd overlay preview draws the layouts obs gets: a file of the input-overlay presets, or the layout of the keys picked for the custom keyboard
            if (path == "/input-overlay/file")
            {
                string file = InputOverlayLayouts.ServedFile(Q("path", ""));
                if (file == null) { HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false }); return false; }
                string ctype = file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "application/json" : "image/png";
                HttpResponse.SendBytes(stream, 200, "OK", HttpResponse.GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = ctype }), File.ReadAllBytes(file));
                return false;
            }
            if (path == "/input-overlay/custom-sheet")
            {
                string sheet = InputOverlayLayouts.CustomSheet();
                if (sheet == null) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false }); return false; }
                HttpResponse.SendBytes(stream, 200, "OK", HttpResponse.GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = "image/png" }), File.ReadAllBytes(sheet));
                return false;
            }
            if (path == "/input-overlay/custom")
            {
                HttpResponse.SendJson(stream, 200, InputOverlayLayouts.CustomLayout(new JArray((Q("keys", "") ?? "").Split(',').Where(k => k.Length > 0 && k.Length <= 8).Take(64))));
                return false;
            }

            // static assets under obs-custom-dock/overlay-preview/ -- the sprite sheets + layout json of the wasd/mouse preview (wasd/, mouse/) and the baked bongo cat models (bongo-models/). two path segments, no traversal.
            var previewAsset = Regex.Match(path, @"^/overlay-preview/([a-z0-9_-]+/[a-z0-9_-]+\.(png|json))$");
            if (previewAsset.Success)
            {
                string baseDir = Path.Combine(AppConfig.GetDockDir(), "overlay-preview");
                string full = Path.GetFullPath(Path.Combine(baseDir, previewAsset.Groups[1].Value));
                string root = Path.GetFullPath(baseDir) + Path.DirectorySeparatorChar;
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
                {
                    string ext = previewAsset.Groups[2].Value;
                    string ctype = ext == "json" ? "application/json" : "image/png";
                    var hp = HttpResponse.GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = ctype });
                    HttpResponse.SendBytes(stream, 200, "OK", hp, File.ReadAllBytes(full));
                }
                else HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false });
                return false;
            }

            return null;
        }
    }
}

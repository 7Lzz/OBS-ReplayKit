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
    // the routes of themes, app icons and the window style
    internal static class AppearanceRoutes
    {
        // answers the request when its path is one of these routes, with what DispatchRequest returns; null when it is not
        public static bool? Handle(RouteRequest r)
        {
            Stream stream = r.Stream;
            HttpRequest req = r.Req;
            string path = r.Path;
            string Q(string key, string def = "") => r.Q(key, def);
            bool QFlag(string key) => r.QFlag(key);


            if (path == "/style-window")
            {
                string title = Q("title", "Clips");
                bool taskbar = QFlag("taskbar");
                int matched = -1;
                // the native plugin owns the icon (title bar + win11 taskbar button) for its own Clips / ReplayKit Settings windows -- style their chrome here but leave the icon to the plugin.
                bool pluginOwnsIcon = title.Equals("Clips", StringComparison.OrdinalIgnoreCase) ||
                    title.Equals("ReplayKit Settings", StringComparison.OrdinalIgnoreCase);
                // on the default app icon, leave the browser-hosted windows native OBS/SVG icon alone. Forcing the legacy ReplayKit ICO here replaces it with a distorted 16px caption image.
                string iconPath = "";
                if (!pluginOwnsIcon)
                {
                    try
                    {
                        string resolved = ReplaykitSettings.ResolveAppIconPath(ReplaykitSettings.Normalize(ReplaykitSettings.ReadSettings()));
                        if (!string.IsNullOrEmpty(resolved)) iconPath = resolved;
                    }
                    catch (Exception ex) { Log.Write("/style-window icon resolve: " + ex.Message); }
                }
                try { matched = Native.StyleWindow(title, iconPath, taskbar); }
                catch (Exception ex) { Log.Write("/style-window threw: " + ex.Message); }
                // logging only on misses keeps the log readable; success is the defualt, silent path.
                if (matched <= 0) Log.Write("/style-window title=" + title + " : 0 matching OBS-family windows found");
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["matched"] = matched });
                return false;
            }

            if (path == "/appearance/icons")
            {
                // the Appearance tabs picker grid: OBS Default + bundled presets + the users saved custom picks. replaykit.ico remains an internal fallback, not a selectable app icon.
                var icons = new JArray { new JObject { ["id"] = "default", ["label"] = "Default", ["url"] = "/obs-icon.svg?v=4" } };
                try
                {
                    if (Directory.Exists(Constants.APP_ICONS_DIR))
                    {
                        var files = Directory.GetFiles(Constants.APP_ICONS_DIR);
                        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                        foreach (var f in files)
                        {
                            if (Array.IndexOf(AppIconExtensions, Path.GetExtension(f).ToLowerInvariant()) < 0) continue;
                            string name = Path.GetFileName(f);
                            if (string.Equals(name, "replaykit.ico", StringComparison.OrdinalIgnoreCase)) continue;
                            icons.Add(new JObject { ["id"] = name, ["label"] = Path.GetFileNameWithoutExtension(name), ["url"] = "/appearance/icon?file=" + Uri.EscapeDataString(name) });
                        }
                    }
                    if (Directory.Exists(Constants.USER_ICONS_DIR))
                    {
                        var ufiles = Directory.GetFiles(Constants.USER_ICONS_DIR, "*.ico");
                        Array.Sort(ufiles, StringComparer.OrdinalIgnoreCase);
                        foreach (var f in ufiles)
                        {
                            string name = Path.GetFileName(f);
                            icons.Add(new JObject
                            {
                                ["id"] = "user/" + name,
                                ["label"] = UserIconLabel(name),
                                ["url"] = "/appearance/icon?file=" + Uri.EscapeDataString("user/" + name),
                                ["deletable"] = true,
                            });
                        }
                    }
                }
                catch (Exception ex) { Log.Write("/appearance/icons: " + ex.Message); }
                var s = ReplaykitSettings.Normalize(ReplaykitSettings.ReadSettings());
                HttpResponse.SendJson(stream, 200, new JObject
                {
                    ["ok"] = true, ["icons"] = icons,
                    ["selected"] = s["appIcon"]?.Value<string>() ?? "default",
                    ["customPath"] = s["appIconCustomPath"]?.Value<string>() ?? "",
                });
                return false;
            }

            if (path == "/appearance/icon")
            {
                string raw = Q("file", "");
                bool isUser = raw.StartsWith("user/", StringComparison.Ordinal);
                string file = Path.GetFileName(raw);
                string ext = Path.GetExtension(file).ToLowerInvariant();
                string dir = isUser ? Constants.USER_ICONS_DIR : Constants.APP_ICONS_DIR;
                string full = string.IsNullOrEmpty(file) ? null : Path.Combine(dir, file);
                if (full != null && Array.IndexOf(AppIconExtensions, ext) >= 0 && File.Exists(full))
                {
                    try
                    {
                        string ctype = ext == ".ico" ? "image/x-icon" : ext == ".png" ? "image/png" : ext == ".bmp" ? "image/bmp" : "image/jpeg";
                        var h = HttpResponse.GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = ctype });
                        HttpResponse.SendBytes(stream, 200, "OK", h, File.ReadAllBytes(full));
                        return false;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Log.Write("/appearance/icon read: " + ex.Message); }
                }
                HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false });
                return false;
            }

            if (path == "/appearance/delete-icon")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string raw = Q("file", "");
                if (!raw.StartsWith("user/", StringComparison.Ordinal))
                {
                    HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Only saved custom icons can be removed." });
                    return false;
                }
                string file = Path.GetFileName(raw);
                string full = Path.Combine(Constants.USER_ICONS_DIR, file);
                bool wasSelected = false;
                string selected = "default";
                try
                {
                    var s = ReplaykitSettings.Normalize(ReplaykitSettings.ReadSettings());
                    selected = s["appIcon"]?.Value<string>() ?? "default";
                    wasSelected = selected == "user/" + file;
                    if (File.Exists(full)) File.Delete(full);
                    if (wasSelected)
                    {
                        ReplaykitSettings.SetAppIconAndApply("default");
                        selected = "default";
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("/appearance/delete-icon: " + ex.Message);
                    HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message });
                    return false;
                }
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["wasSelected"] = wasSelected, ["selected"] = selected });
                return false;
            }

            if (path == "/appearance/themes")
            {
                var s = ReplaykitSettings.Normalize(ReplaykitSettings.ReadSettings());
                var list = new JArray();
                foreach (var kv in Themes.PresetOrder)
                {
                    var tk = Themes.Resolve(new JObject { ["theme"] = kv.Key });
                    list.Add(new JObject { ["id"] = kv.Key, ["label"] = kv.Value, ["swatch"] = ThemeSwatch(tk), ["seed"] = tk.ToSeedJson() });
                }
                try
                {
                    if (Directory.Exists(Constants.USER_THEMES_DIR))
                    {
                        var files = Directory.GetFiles(Constants.USER_THEMES_DIR, "*.json");
                        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                        foreach (var f in files)
                        {
                            string id = "user/" + Path.GetFileNameWithoutExtension(f);
                            var tk = Themes.Resolve(new JObject { ["theme"] = id });
                            list.Add(new JObject { ["id"] = id, ["label"] = Themes.UserThemeLabel(Path.GetFileName(f)), ["swatch"] = ThemeSwatch(tk), ["seed"] = tk.ToSeedJson(), ["deletable"] = true });
                        }
                    }
                }
                catch (Exception ex) { Log.Write("/appearance/themes user: " + ex.Message); }
                HttpResponse.SendJson(stream, 200, new JObject
                {
                    ["ok"] = true, ["themes"] = list,
                    ["selected"] = s["theme"]?.Value<string>() ?? "default",
                    ["custom"] = s["themeCustom"],
                });
                return false;
            }

            if (path == "/rk-theme.css")
            {
                // the committed theme stylesheet the dock pages <link> to. matches the physical rk-theme.css the helper writes for the file:// dock.
                string css;
                try
                {
                    var s = ReplaykitSettings.Normalize(ReplaykitSettings.ReadSettings());
                    css = (s["theme"]?.Value<string>() ?? "default") == "default" ? "/* default theme */" : Themes.DockCss(Themes.Resolve(s));
                }
                catch (Exception ex) { Log.Write("/rk-theme.css: " + ex.Message); css = ""; }
                var hh = HttpResponse.GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = "text/css; charset=utf-8" });
                HttpResponse.SendBytes(stream, 200, "OK", hh, Encoding.UTF8.GetBytes(css));
                return false;
            }

            if (path == "/appearance/theme.css")
            {
                // live-preview stylesheet -- ?theme=<id> or ?custom=<json> overrides the saved setting for the fetch.
                var s = ReplaykitSettings.Normalize(ReplaykitSettings.ReadSettings());
                string previewId = Q("theme", "");
                if (!string.IsNullOrEmpty(previewId)) s["theme"] = previewId;
                string rawCustom = Q("custom", "");
                if (!string.IsNullOrEmpty(rawCustom))
                {
                    try { s["themeCustom"] = JObject.Parse(rawCustom); s["theme"] = "custom"; } catch { }
                }
                // the settings tab asks with preview=1 while the theme is being edited, so the native chrome shows the same candidate
                if (Q("preview", "") == "1")
                {
                    try { SettingsHttp.SetThemePreview(s); }
                    catch (Exception ex) { Log.Write("/appearance/theme.css preview: " + ex.Message); }
                }
                string css;
                try { css = (s["theme"]?.Value<string>() ?? "default") == "default" ? Themes.DockCss(Themes.Resolve(new JObject { ["theme"] = "default" })) : Themes.DockCss(Themes.Resolve(s)); }
                catch (Exception ex) { Log.Write("/appearance/theme.css: " + ex.Message); css = ""; }
                var h = HttpResponse.GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = "text/css; charset=utf-8" });
                HttpResponse.SendBytes(stream, 200, "OK", h, Encoding.UTF8.GetBytes(css));
                return false;
            }

            if (path == "/appearance/save-theme")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string label = Q("label", "");
                string existingId = Q("id", "");
                JObject payload;
                try { payload = string.IsNullOrWhiteSpace(req.Body) ? null : JObject.Parse(req.Body); }
                catch { payload = null; }
                if (payload == null)
                {
                    HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Missing theme colours." });
                    return false;
                }
                string id;
                try { id = Themes.SaveUserTheme(label, payload, existingId); }
                catch (Exception ex) { Log.Write("/appearance/save-theme: " + ex.Message); id = null; }
                if (id == null) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = string.IsNullOrEmpty(existingId) ? "Could not save theme." : "That saved theme no longer exists." }); return false; }
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["id"] = id });
                return false;
            }

            if (path == "/appearance/pick-color")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try
                {
                    string color = DesktopColorPicker.Pick();
                    HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["cancelled"] = string.IsNullOrEmpty(color), ["color"] = color });
                }
                catch (Exception ex)
                {
                    Log.Write("/appearance/pick-color: " + ex.Message);
                    HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = "Desktop color picker failed: " + ex.Message });
                }
                return false;
            }

            if (path == "/appearance/delete-theme")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string raw = Q("id", "");
                if (!raw.StartsWith("user/", StringComparison.Ordinal))
                {
                    HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Only saved themes can be removed." });
                    return false;
                }
                string name = Path.GetFileNameWithoutExtension(Path.GetFileName(raw.Substring(5)));
                bool wasSelected = false;
                string selected = "default";
                try
                {
                    var s = ReplaykitSettings.Normalize(ReplaykitSettings.ReadSettings());
                    selected = s["theme"]?.Value<string>() ?? "default";
                    wasSelected = selected == "user/" + name;
                    string full = Path.Combine(Constants.USER_THEMES_DIR, name + ".json");
                    if (File.Exists(full)) File.Delete(full);
                    if (wasSelected)
                    {
                        var restart = ReplaykitSettings.SetThemeAndApply("default");
                        selected = "default";
                        HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["wasSelected"] = true, ["selected"] = "default", ["restartRequired"] = restart });
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("/appearance/delete-theme: " + ex.Message);
                    HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message });
                    return false;
                }
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["wasSelected"] = false, ["selected"] = selected });
                return false;
            }

            return null;
        }
    }
}

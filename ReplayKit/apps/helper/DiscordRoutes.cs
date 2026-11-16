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
    // the routes of the discord share preview, projector and screenshare install
    internal static class DiscordRoutes
    {
        // answers the request when its path is one of these routes, with what DispatchRequest returns; null when it is not
        public static bool? Handle(RouteRequest r)
        {
            Stream stream = r.Stream;
            HttpRequest req = r.Req;
            string path = r.Path;
            string Q(string key, string def = "") => r.Q(key, def);

            if (path.StartsWith("/share/cover", StringComparison.Ordinal)) return HandleCover(r);

            if (path == "/share-preview")
            {
                if (req.Method == "GET")
                {
                    try
                    {
                        JObject state;
                        if (Q("quick") == "1") state = SettingsLiveApply.GetSharePreviewFlags();
                        else lock (SettingsStore.Gate) state = SettingsLiveApply.GetSharePreviewState(true);
                        HttpResponse.SendJson(stream, 200, state);
                    }
                    catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                    return false;
                }
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try
                {
                    if (string.IsNullOrWhiteSpace(req.Body)) throw new InvalidOperationException(Locales.Message("screenshare.missingSharePreviewBody"));
                    var incoming = JObject.Parse(req.Body);
                    if (incoming["enabled"]?.Type != JTokenType.Boolean) throw new InvalidOperationException(Locales.Message("screenshare.sharePreviewEnabledMustBeA"));
                    var result = SettingsLiveApply.SetSharePreviewEnabled(incoming["enabled"].Value<bool>());
                    int code = result["ok"]?.Value<bool>() == true ? 200 : 503;
                    HttpResponse.SendJson(stream, code, result);
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/projector-inspect")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try { HttpResponse.SendJson(stream, 200, DiscordProjector.Inspect()); }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/projector-repark")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try { HttpResponse.SendJson(stream, 200, DiscordProjector.Repark(force: true)); }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/install-discord-screenshare")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try { HttpResponse.SendJson(stream, 200, Uninstall.StartDiscordScreenshareInstall()); }
                catch (Exception ex) { HttpResponse.SendJson(stream, 400, FailureJson(ex)); }
                return false;
            }

            if (path == "/uninstall-discord-screenshare")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try { HttpResponse.SendJson(stream, 200, Uninstall.StartDiscordScreenshareRemoval()); }
                catch (Exception ex) { HttpResponse.SendJson(stream, 400, FailureJson(ex)); }
                return false;
            }

            return null;
        }

        // the card of the share window: what it can use, the image and the sound picked for it, the picked image for the card editor and a showing of the card now
        private static bool? HandleCover(RouteRequest r)
        {
            Stream stream = r.Stream;
            HttpRequest req = r.Req;
            string path = r.Path;
            if (path == "/share/cover/state" && req.Method == "GET")
            {
                HttpResponse.SendJson(stream, 200, ShareCover.Status());
                return false;
            }
            if (path == "/share/cover/image.png" && req.Method == "GET")
            {
                if (!File.Exists(ShareCover.ImagePath)) { HttpResponse.SendText(stream, 404, "Not Found", "no image picked"); return false; }
                HttpResponse.SendFile(stream, 200, "OK", new Dictionary<string, string> { ["Content-Type"] = "image/png", ["Cache-Control"] = "no-store" }, ShareCover.ImagePath, FileShare.ReadWrite | FileShare.Delete);
                return false;
            }
            if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
            try
            {
                JObject result;
                switch (path)
                {
                    case "/share/cover/image":
                    {
                        string picked = FilePickers.PickPath(false, FilePickers.ImagesFilter, "");
                        if (string.IsNullOrEmpty(picked)) { HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["changed"] = false }); return false; }
                        result = ShareCover.ImportImage(picked);
                        if (result["ok"]?.Value<bool>() == true) result["settings"] = SetCoverSetting("share_cover_background", "image");
                        break;
                    }
                    case "/share/cover/image/remove":
                        ShareCover.Remove(ShareCover.ImagePath);
                        result = new JObject { ["ok"] = true, ["settings"] = SetCoverSetting("share_cover_background", "theme", onlyFrom: "image") };
                        break;
                    case "/share/cover/sound":
                    {
                        string picked = FilePickers.PickPath(false, FilePickers.AudioFilter, "");
                        if (string.IsNullOrEmpty(picked)) { HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["changed"] = false }); return false; }
                        result = ShareCover.ImportSound(picked);
                        if (result["ok"]?.Value<bool>() == true) result["settings"] = SetCoverSetting("share_cover_sound", "custom");
                        break;
                    }
                    case "/share/cover/sound/remove":
                        ShareCover.Remove(ShareCover.SoundPath);
                        result = new JObject { ["ok"] = true, ["settings"] = SetCoverSetting("share_cover_sound", "default", onlyFrom: "custom") };
                        break;
                    case "/share/cover/show":
                        if (SettingsLiveApply.GetSharePreviewFlags()["enabled"]?.Value<bool>() != true)
                            result = new JObject { ["ok"] = false, ["message"] = Locales.Message("screenshare.turnOnSharePreviewFirst") };
                        else
                        {
                            ShareCover.Apply(ReplaykitSettings.ReadSettings(), false);
                            result = ShareWindow.Send("SHOWCARD 10");
                        }
                        break;
                    default:
                        return null;
                }
                result["cover"] = ShareCover.Status();
                HttpResponse.SendJson(stream, 200, result);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message });
            }
            return false;
        }

        // saves one card setting the way any saved setting is merged, then draws the card again for a running share window
        private static JObject SetCoverSetting(string key, string value, string onlyFrom = null)
        {
            JObject settings;
            lock (SettingsStore.Gate)
            {
                settings = ReplaykitSettings.ReadSettings();
                if (onlyFrom == null || settings[key]?.Value<string>() == onlyFrom)
                {
                    settings[key] = value;
                    settings = ReplaykitSettings.Normalize(settings);
                    ReplaykitSettings.WriteSettings(settings);
                }
            }
            ShareCover.Apply(settings, true);
            return settings;
        }
    }
}

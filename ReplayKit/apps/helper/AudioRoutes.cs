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
    // the routes of the microphone test, recorded audio apps and mutes
    internal static class AudioRoutes
    {
        // answers the request when its path is one of these routes, with what DispatchRequest returns; null when it is not
        public static bool? Handle(RouteRequest r)
        {
            Stream stream = r.Stream;
            HttpRequest req = r.Req;
            string path = r.Path;
            string Q(string key, string def = "") => r.Q(key, def);


            // microphone settings: the device list for the combo, and the loopback mic test. the test opens the microphone and plays it back, so every route is origin-gated and the level poll doubles as the keep-alive.
            if (path == "/mic/devices")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                HttpResponse.SendJson(stream, 200, MicTest.GetDevicesPayload());
                return false;
            }

            if (path == "/mic/test/start")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                if (!int.TryParse(Q("volume"), out int volume)) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Volume must be a number." }); return false; }
                if (!int.TryParse(Q("gate"), out int gate)) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Input sensitivity must be a number." }); return false; }
                string noiseFlag = Q("ns");
                if (noiseFlag != "0" && noiseFlag != "1") { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Noise suppression must be 0 or 1." }); return false; }
                string playbackFlag = Q("playback", "1");
                if (playbackFlag != "0" && playbackFlag != "1") { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Playback must be 0 or 1." }); return false; }
                var started = MicTest.Start(Q("device"), volume, noiseFlag == "1", gate, playbackFlag == "1");
                HttpResponse.SendJson(stream, started["ok"]?.Value<bool>() == true ? 200 : 400, started);
                return false;
            }

            if (path == "/mic/test/params")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                if (!int.TryParse(Q("volume"), out int volume)) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Volume must be a number." }); return false; }
                if (!int.TryParse(Q("gate"), out int gate)) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Input sensitivity must be a number." }); return false; }
                string noiseFlag = Q("ns");
                if (noiseFlag != "0" && noiseFlag != "1") { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Noise suppression must be 0 or 1." }); return false; }
                var changed = MicTest.SetParams(volume, noiseFlag == "1", gate);
                HttpResponse.SendJson(stream, changed["ok"]?.Value<bool>() == true ? 200 : 400, changed);
                return false;
            }

            if (path == "/mic/test/level")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                HttpResponse.SendJson(stream, 200, MicTest.ReadLevel());
                return false;
            }

            if (path == "/mic/test/stop")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                HttpResponse.SendJson(stream, 200, MicTest.Stop());
                return false;
            }

            // mute switches in Settings > Audio. the page names one of three fixed inputs, never an obs input name of its own, so it cannot mute arbitrary sources.
            if (path == "/audio-mute")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try
                {
                    var body = JObject.Parse(string.IsNullOrWhiteSpace(req.Body) ? "{}" : req.Body);
                    string which = body["input"]?.Value<string>();
                    string inputName = which == "mic" ? SettingsLiveApply.MicInputName : which == "desktop" ? SettingsLiveApply.DesktopAudioInputName : which == "discord" ? SettingsLiveApply.DiscordAudioInputName : null;
                    if (inputName == null || body["muted"]?.Type != JTokenType.Boolean)
                    {
                        HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Expected input (mic, desktop or discord) and muted (true or false)." });
                        return false;
                    }
                    var res = ObsWebSocket.InvokeRequest("SetInputMute", new JObject { ["inputName"] = inputName, ["inputMuted"] = body["muted"].Value<bool>() }, 3000);
                    HttpResponse.SendJson(stream, res.Ok ? 200 : res.Unavailable ? 503 : 409, ObsResultToJson(res));
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            // the programs that play sound, for the recording audio tab: the list with each choice, and the icon of one of them
            if (path == "/audio/apps")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                try
                {
                    var current = ReplaykitSettings.ReadSettings();
                    string mode = current["recordingAudioMode"]?.Value<string>() == "specific" ? "specific" : "all";
                    HttpResponse.SendJson(stream, 200, AudioApps.GetAppsPayload(mode, current["recordingAudioApps"] as JObject));
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/audio/app-icon")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                byte[] png = null;
                try { png = AudioApps.GetIconPng(Q("exe")); }
                catch (Exception ex) { Log.Write("/audio/app-icon: " + ex.Message); }
                if (png == null) { HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false }); return false; }
                var iconHeaders = HttpResponse.GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = "image/png" });
                HttpResponse.SendBytes(stream, 200, "OK", iconHeaders, png);
                return false;
            }

            return null;
        }
    }
}

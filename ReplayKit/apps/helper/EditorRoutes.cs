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
    // the routes of the clip editor: presets, drafts, its session, imported media, trims, renders and subtitles
    internal static class EditorRoutes
    {
        // answers the request when its path is one of these routes, with what DispatchRequest returns; null when it is not
        public static bool? Handle(RouteRequest r)
        {
            Stream stream = r.Stream;
            HttpRequest req = r.Req;
            string path = r.Path;
            string Q(string key, string def = "") => r.Q(key, def);


            // auto subtitles: the one-time download of the speech engine and its model, and the jobs that turn a part of a clip into word timings
            if (path == "/speech/status" || path == "/speech/install" || path == "/speech/transcribe" || path == "/speech/job" || path == "/speech/cancel")
            {
                bool post = path != "/speech/status" && path != "/speech/job";
                if (req.Method != (post ? "POST" : "GET")) { HttpResponse.SendText(stream, 405, "Method Not Allowed", (post ? "POST" : "GET") + " required"); return false; }
                if (path == "/speech/status") HttpResponse.SendJson(stream, 200, Speech.Status(Q("model")));
                else if (path == "/speech/install") HttpResponse.SendJson(stream, 200, Speech.StartInstall(Q("model")));
                else if (path == "/speech/job") HttpResponse.SendJson(stream, 200, Speech.JobStatus(Q("id")));
                else if (path == "/speech/cancel") HttpResponse.SendJson(stream, 200, Speech.Cancel(Q("id")));
                else
                {
                    // tracks is a comma list of the audio streams to listen to together
                    var trackParts = (Q("tracks") ?? "").Split(',');
                    var tracks = new List<int>();
                    bool parsed = double.TryParse(Q("start"), NumberStyles.Float, CultureInfo.InvariantCulture, out double from) &
                        double.TryParse(Q("end"), NumberStyles.Float, CultureInfo.InvariantCulture, out double to);
                    foreach (string part in trackParts)
                    {
                        if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int one)) tracks.Add(one);
                        else parsed = false;
                    }
                    if (!parsed) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Bad request" }); return false; }
                    var started = Speech.StartTranscribe(Q("file"), from, to, tracks, Q("language"), Q("model"));
                    HttpResponse.SendJson(stream, started["ok"]?.Value<bool>() == true ? 200 : 400, started);
                }
                return false;
            }

            // the record tab of the editor: a voiceover from the microphone of the settings or one picked in the tab, which becomes an import once it stops
            if (path == "/voiceover/start" || path == "/voiceover/level" || path == "/voiceover/stop")
            {
                bool post = path != "/voiceover/level";
                if (req.Method != (post ? "POST" : "GET")) { HttpResponse.SendText(stream, 405, "Method Not Allowed", (post ? "POST" : "GET") + " required"); return false; }
                JObject answer = path == "/voiceover/start" ? Voiceover.Start(Q("device")) : path == "/voiceover/level" ? Voiceover.Level() : Voiceover.Stop(Q("keep") == "1");
                HttpResponse.SendJson(stream, answer["ok"]?.Value<bool>() == true ? 200 : 400, answer);
                return false;
            }

            // the presets of the clip editor: the lists of subtitle styles and video looks, and one list saved back whole
            if (path == "/editor-presets" || path == "/editor-presets/save")
            {
                try
                {
                    if (path == "/editor-presets" && req.Method == "GET") { var listed = EditorPresets.List(); HttpResponse.SendJson(stream, listed.Value<bool>("ok") ? 200 : 500, listed); return false; }
                    if (path == "/editor-presets/save" && req.Method == "POST") { var saved = EditorPresets.Save(Q("kind"), req.Body); HttpResponse.SendJson(stream, saved.Value<bool>("ok") ? 200 : 400, saved); return false; }
                    HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET or POST required");
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            // the unsaved edits of the clip editor, one per clip: the list for the drafts tab, one draft for the editor to put back, and the two ways to change them
            if (path == "/drafts" || path.StartsWith("/drafts/"))
            {
                try
                {
                    if (path == "/drafts" && req.Method == "GET") { HttpResponse.SendJson(stream, 200, EditDrafts.List()); return false; }
                    if (path == "/drafts/get" && req.Method == "GET")
                    {
                        var got = EditDrafts.Get(Q("file"));
                        HttpResponse.SendJson(stream, got.Value<bool>("ok") ? 200 : 404, got);
                        return false;
                    }
                    if (path == "/drafts/save" && req.Method == "POST")
                    {
                        JObject edit;
                        try { edit = JObject.Parse(req.Body); }
                        catch (Newtonsoft.Json.JsonException) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Bad edit" }); return false; }
                        var saved = EditDrafts.Save(Q("file"), edit);
                        HttpResponse.SendJson(stream, saved.Value<bool>("ok") ? 200 : 400, saved);
                        return false;
                    }
                    if (path == "/drafts/delete" && req.Method == "POST")
                    {
                        var removed = EditDrafts.Delete(Q("file"));
                        HttpResponse.SendJson(stream, removed.Value<bool>("ok") ? 200 : 400, removed);
                        return false;
                    }
                    HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET or POST required");
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            // the menus of the obs menu bar, opened from the Advanced tab now that the title bar has no menu button
            // the quick actions under the logo of the title bar: what a page offers, read back by the plugin, and a pick sent on to the plugin
            if (path == "/editor/session")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                JObject body;
                try { body = JObject.Parse(req.Body ?? ""); }
                catch (Newtonsoft.Json.JsonException) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Bad editor session." }); return false; }
                string view = body["view"]?.Type == JTokenType.String ? body.Value<string>("view") : null;
                string id = body["id"]?.Type == JTokenType.String ? body.Value<string>("id") : null;
                string action = body["action"]?.Type == JTokenType.String ? body.Value<string>("action") : null;
                if ((view != "clips" && view != "home") || !Guid.TryParseExact(id, "D", out _))
                { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Bad editor session." }); return false; }
                if (action == "register")
                {
                    JobCoordinator.RegisterEditor(view, id);
                    HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true });
                    return false;
                }
                if (body["revision"]?.Type != JTokenType.Integer || !long.TryParse(body["revision"].ToString(), out long revision) || revision <= 0 ||
                    (action != "add" && action != "clear"))
                { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Bad editor session." }); return false; }
                var paths = new List<string>();
                if (action == "add")
                {
                    if (!(body["names"] is JArray names) || names.Count == 0 || names.Count > 128)
                    { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Bad editor sources." }); return false; }
                    foreach (var name in names)
                    {
                        var selected = name.Type == JTokenType.String ? Clips.GetEditorSource(name.Value<string>()) : null;
                        if (selected == null || !File.Exists(selected.Full))
                        { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "An editor source is unavailable." }); return false; }
                        paths.Add(selected.Full);
                    }
                }
                bool ok = JobCoordinator.UpdateEditor(view, id, revision, action == "clear" ? null : paths.ToArray(), out string error);
                HttpResponse.SendJson(stream, ok ? 200 : 409, new JObject { ["ok"] = ok, ["message"] = error });
                return false;
            }

            if (path == "/audio-track") return HttpResponse.ServeAudioTrack(stream, req, Q("key"), Q("i"));

            if (path == "/audio-peaks")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                string peaks = ClipAudioTracks.PeaksPathFor(Q("key"), Q("i"));
                if (peaks == null) { HttpResponse.SendText(stream, 404, "Not Found", "Peaks not found"); return false; }
                HttpResponse.SendBytes(stream, 200, "OK", HttpResponse.GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = "application/json; charset=utf-8" }), File.ReadAllBytes(peaks));
                return false;
            }

            // audio the editor brings in from outside the clip: the picker of the helper, the bytes of a drop, how an import stands, and the finished file with its waveform
            if (path == "/import-audio/pick")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                int.TryParse(Q("max"), NumberStyles.None, CultureInfo.InvariantCulture, out int room);
                if (room < 1 || room > 8) room = 8;
                string[] picked = PickAudioFiles();
                if (picked.Length == 0) { HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = false, ["cancelled"] = true }); return false; }
                var started = new JArray();
                foreach (string file in picked.Take(room)) started.Add(AudioImports.BeginFromPath(file));
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["files"] = started, ["skipped"] = picked.Length - started.Count });
                return false;
            }

            if (path == AudioImports.UploadPath)
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                var imported = AudioImports.BeginFromUpload(req, Q("name"), out int importStatus);
                HttpResponse.SendJson(stream, importStatus, imported);
                return false;
            }

            if (path == "/import-audio/status")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                HttpResponse.SendJson(stream, 200, AudioImports.Describe(Q("id")));
                return false;
            }

            if (path == "/import-audio/file") return HttpResponse.ServeAudioFile(stream, req, AudioImports.PathFor(Q("id")));

            if (path == "/import-audio/peaks")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                string importPeaks = AudioImports.PeaksPathFor(Q("id"));
                if (importPeaks == null) { HttpResponse.SendText(stream, 404, "Not Found", "Peaks not found"); return false; }
                HttpResponse.SendBytes(stream, 200, "OK", HttpResponse.GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = "application/json; charset=utf-8" }), File.ReadAllBytes(importPeaks));
                return false;
            }

            // the sound of another clip of the folder, which the library of the editor lays under the clip it put over the video
            if (path == "/import-audio/clip")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                var clip = Clips.GetEditorSource(Q("name"));
                if (clip == null || !File.Exists(clip.Full)) { HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false, ["message"] = "That clip was not found" }); return false; }
                HttpResponse.SendJson(stream, 200, AudioImports.BeginFromPath(clip.Full));
                return false;
            }

            // a video from outside the clip folder for the library of the editor, read where it lies
            if (path == "/import-video/pick")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string[] pickedVideo = PickFiles(VideoPickerTitle, VideoFilter);
                if (pickedVideo.Length == 0) { HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = false, ["cancelled"] = true }); return false; }
                HttpResponse.SendJson(stream, 200, VideoImports.FromPath(pickedVideo[0]));
                return false;
            }

            // pictures and gifs the editor lays over the video: a shape or a sticker the page drew, files from the picker, and the file itself
            if (path == MediaImports.UploadPath)
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                var made = MediaImports.FromUpload(req, Q("name"), out int mediaStatus);
                HttpResponse.SendJson(stream, mediaStatus, made);
                return false;
            }

            if (path == "/import-media/pick")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string[] pickedMedia = PickFiles(MediaPickerTitle, MediaFilter);
                if (pickedMedia.Length == 0) { HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = false, ["cancelled"] = true }); return false; }
                var added = new JArray();
                foreach (string file in pickedMedia.Take(8)) added.Add(MediaImports.FromPath(file));
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["files"] = added, ["skipped"] = pickedMedia.Length - added.Count });
                return false;
            }

            if (path == "/import-media/file")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                string media = MediaImports.PathFor(Q("id"));
                if (media == null) { HttpResponse.SendText(stream, 404, "Not Found", "Media not found"); return false; }
                var mediaHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = MediaImports.ContentType(media), ["Cache-Control"] = "private, max-age=31536000, immutable" };
                HttpResponse.SendFile(stream, 200, "OK", mediaHeaders, media);
                return false;
            }

            // the film strip of the editor: what it asks for with the name of a clip, and the picture itself, which never changes under its key
            if (path == "/clip-filmstrip")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                HttpResponse.SendJson(stream, 200, ClipFilmstrip.Describe(Q("name")));
                return false;
            }

            if (path == "/film-strip")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                string strip = ClipFilmstrip.PathFor(Q("key"));
                if (strip == null) { HttpResponse.SendText(stream, 404, "Not Found", "Film strip not found"); return false; }
                try
                {
                    var stripHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Cache-Control"] = "public, max-age=31536000, immutable",
                        ["Content-Type"] = "image/jpeg",
                        ["Connection"] = "close",
                    };
                    HttpResponse.SendFile(stream, 200, "OK", stripHeaders, strip);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { HttpResponse.SendText(stream, 404, "Not Found", "Film strip not found"); }
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
                // the audio edits of the editor come as json in the body; no body means the audio is left as it is
                JToken audioEdit = null;
                JToken videoEdit = null;
                JToken subtitleEdit = null;
                JToken lookEdit = null;
                JToken layerEdit = null;
                if (!string.IsNullOrWhiteSpace(req.Body))
                {
                    try
                    {
                        var editBody = JObject.Parse(req.Body);
                        // a member that is null is the same as one that is not there
                        audioEdit = editBody["audio"] != null && editBody["audio"].Type != JTokenType.Null ? editBody["audio"] : null;
                        videoEdit = editBody["video"] != null && editBody["video"].Type != JTokenType.Null ? editBody["video"] : null;
                        subtitleEdit = editBody["subtitles"] != null && editBody["subtitles"].Type != JTokenType.Null ? editBody["subtitles"] : null;
                        lookEdit = editBody["look"] != null && editBody["look"].Type != JTokenType.Null ? editBody["look"] : null;
                        layerEdit = editBody["layers"] != null && editBody["layers"].Type != JTokenType.Null ? editBody["layers"] : null;
                    }
                    catch (Newtonsoft.Json.JsonException) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Bad audio edit" }); return false; }
                }
                try
                {
                    double videoSpeed = 1;
                    if (!string.IsNullOrEmpty(Q("speed")) && !double.TryParse(Q("speed"), NumberStyles.Float, CultureInfo.InvariantCulture, out videoSpeed))
                    {
                        HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Bad speed" });
                        return false;
                    }
                    double frameTime = -1;
                    if (mode == "screenshot" && !double.TryParse(Q("time"), NumberStyles.Float, CultureInfo.InvariantCulture, out frameTime))
                    {
                        HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Bad screenshot time" });
                        return false;
                    }
                    // the render settings of the editor, each left out to keep what the video has
                    int.TryParse(Q("height", "0"), NumberStyles.None, CultureInfo.InvariantCulture, out int renderHeight);
                    int.TryParse(Q("fps", "0"), NumberStyles.None, CultureInfo.InvariantCulture, out int renderFps);
                    int.TryParse(Q("bitrate", "0"), NumberStyles.None, CultureInfo.InvariantCulture, out int renderKbps);
                    var render = new Trim.RenderOptions { Height = renderHeight, Fps = renderFps, BitrateKbps = renderKbps };
                    if (!double.TryParse(Q("pictureScale", "1"), NumberStyles.Float, CultureInfo.InvariantCulture, out double pictureScale) ||
                        !double.TryParse(Q("pictureRotation", "0"), NumberStyles.Float, CultureInfo.InvariantCulture, out double pictureRotation))
                        throw new ArgumentException("Invalid video transform.");
                    var result = Trim.InvokeClipTrim(file, startSec, endSec, mode, precise, removeAudio, audioEdit, videoSpeed, videoEdit, subtitleEdit, lookEdit, layerEdit, Q("aspect", "original"), frameTime, render, pictureScale, pictureRotation);
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

            if (path == "/compress-overwrite")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                string file = Q("file");
                string mode = Q("mode", "smaller");
                int.TryParse(Q("scaleHeight"), out int scaleHeight);
                // compress-history rules (enforced server-side as defense in depth; the dock ui also disables the relevant card): clip already marked slow (libx265 medium) -> reject entirely, its the best mode and re-running anything would just degrade quality without size gain. clip already marked fast (libx264 fast) + fast again requested -> reject, re-fast-compressing produces no meaningful gain. re-running with smaller is allowed (fast -> slow is a valid upgrade path).
                var selected = Clips.GetSafeClipPath(file);
                if (selected == null || !File.Exists(selected.Full)) { HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false, ["message"] = "Clip not found" }); return false; }
                string existingMode = Compression.GetCompressMarker(selected.Full).Mode;
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

            return null;
        }
    }
}

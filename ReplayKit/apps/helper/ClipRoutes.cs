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
    // the routes of the clips, screenshots, gifs and deleted history: listing, files, thumbnails, renames and deletes
    internal static class ClipRoutes
    {
        // answers the request when its path is one of these routes, with what DispatchRequest returns; null when it is not
        public static bool? Handle(RouteRequest r)
        {
            Stream stream = r.Stream;
            HttpRequest req = r.Req;
            string path = r.Path;
            var query = r.Query;
            string Q(string key, string def = "") => r.Q(key, def);
            bool QFlag(string key) => r.QFlag(key);


            if (path == "/clips/state")
            {
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
                        string game = Q("game", "all");
                        if (game != "all" && game != "desktop" && game != "mixed" && game != "unknown" && !GameCatalog.ValidId(game)) throw new ArgumentException("Invalid game filter.");
                        HttpResponse.SendText(stream, 200, "OK", Clips.GetClipsPageJson(offset, limit, sort, game, QFlag("linked")), "application/json; charset=utf-8");
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
                var caps = Compression.GetToolPaths();
                HttpResponse.SendJson(stream, 200, new JObject
                {
                    ["ok"] = true,
                    ["ffmpeg"] = !string.IsNullOrEmpty(caps["ffmpeg"]?.Value<string>()),
                    ["ffprobe"] = !string.IsNullOrEmpty(caps["ffprobe"]?.Value<string>()),
                    // the switch can change while the capabilities are kept, so it is read as it is now
                    ["logging"] = Server.State.LogEnabled,
                });
                return false;
            }

            // one poll for the merged controls dock: share-preview state (from the settings file, no monitor probe) plus the live replay-buffer / recording output state.
            if (path == "/controls-state")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                try
                {
                    var s = ReplaykitSettings.ReadSettings();
                    bool spAvailable = ReplaykitSettings.TestDiscordScreenshareEnabled(s);
                    bool spEnabled = spAvailable
                        && s["discord_output_mode"]?.Value<string>() == "projector"
                        && (s["discord_projector_enabled"]?.Value<bool>() ?? false);

                    var rbRes = ObsWebSocket.InvokeRequest("GetReplayBufferStatus");
                    var recRes = ObsWebSocket.InvokeRequest("GetRecordStatus");
                    // mute state of the three inputs the Settings > Audio switches drive; null when an input is missing from the scene
                    var micMute = ObsWebSocket.InvokeRequest("GetInputMute", new JObject { ["inputName"] = SettingsLiveApply.MicInputName }, 1500);
                    var desktopMute = ObsWebSocket.InvokeRequest("GetInputMute", new JObject { ["inputName"] = SettingsLiveApply.DesktopAudioInputName }, 1500);
                    var discordMute = ObsWebSocket.InvokeRequest("GetInputMute", new JObject { ["inputName"] = SettingsLiveApply.DiscordAudioInputName }, 1500);
                    HttpResponse.SendJson(stream, 200, new JObject
                    {
                        ["micMuted"] = micMute.Ok ? micMute.Data?["inputMuted"]?.Value<bool>() : (bool?)null,
                        ["desktopMuted"] = desktopMute.Ok ? desktopMute.Data?["inputMuted"]?.Value<bool>() : (bool?)null,
                        ["discordMuted"] = discordMute.Ok ? discordMute.Data?["inputMuted"]?.Value<bool>() : (bool?)null,
                        ["ok"] = true,
                        ["sharePreviewAvailable"] = spAvailable,
                        ["sharePreviewEnabled"] = spEnabled,
                        ["replayBufferActive"] = rbRes.Ok ? rbRes.Data?["outputActive"]?.Value<bool>() : (bool?)null,
                        ["recordActive"] = recRes.Ok ? recRes.Data?["outputActive"]?.Value<bool>() : (bool?)null,
                        ["recordMs"] = recRes.Ok ? recRes.Data?["outputDuration"]?.Value<long?>() : (long?)null,
                        ["obsConnected"] = rbRes.Ok || recRes.Ok,
                    });
                }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            // what the home page shows besides live state: clip count and size, this weeks per-day counts, and the room left on the clips drive.
            if (path == "/home/summary")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                try { HttpResponse.SendJson(stream, 200, Clips.GetHomeSummaryPayload()); }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            if (path == "/duplicate-clip")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                var selected = Clips.GetSafeClipPath(query.ContainsKey("file") ? query["file"] : null);
                if (selected == null) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Bad filename" }); return false; }
                var result = Clips.DuplicateClip(selected);
                HttpResponse.SendJson(stream, result["ok"]?.Value<bool>() == true ? 200 : 400, result);
                return false;
            }

            if (path.StartsWith("/file/"))
            {
                // only route allowed to keep the connection open past this request -- see HttpResponse.ServePreview.
                return HttpResponse.ServePreview(stream, req, Uri.UnescapeDataString(path.Substring(6)));
            }

            if (path == "/clip-audio")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                HttpResponse.SendJson(stream, 200, ClipAudioTracks.Describe(Q("name")));
                return false;
            }

            if (path.StartsWith("/thumb/")) { HttpResponse.ServeThumbnail(stream, Uri.UnescapeDataString(path.Substring(7))); return false; }

            if (path == "/screenshots" || path == "/screenshots/file" || path == "/screenshots/thumb")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                try
                {
                    if (path == "/screenshots") HttpResponse.SendJson(stream, 200, new JObject { ["screenshots"] = Screenshots.List() });
                    else
                    {
                        string file = Screenshots.Resolve(query.ContainsKey("file") ? query["file"] : null);
                        if (file == null) { HttpResponse.SendText(stream, 404, "Not Found", "Screenshot not found"); return false; }
                        bool thumb = path == "/screenshots/thumb";
                        // the page asks with the size and time of the file in the address, so a picture under one address never changes and the browser keeps it instead of reading it again on every paint of the grid
                        HttpResponse.SendFile(stream, 200, "OK", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["Content-Type"] = thumb ? "image/jpeg" : "image/png",
                            ["Cache-Control"] = "private, max-age=31536000, immutable",
                            ["X-Content-Type-Options"] = "nosniff",
                        }, thumb ? Screenshots.Thumb(file) : file);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is System.Runtime.InteropServices.ExternalException)
                {
                    HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = "The screenshots could not be read" });
                }
                return false;
            }

            if (path == "/open-clip-folder" || path == "/open-screenshot-folder" || path == "/open-gif-folder")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try
                {
                    string root = path == "/open-screenshot-folder" ? Screenshots.Folder(create: true) : path == "/open-gif-folder" ? GifExports.Folder(true) : Path.GetFullPath(AppConfig.GetClipDir());
                    if (!Directory.Exists(root)) { HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false, ["message"] = "Clip folder not found" }); return false; }
                    // dedupe: if explorer already has the clip folder open, raise it instead of spawning another window. falls thru to the normal launch path if the com probe fails or no match is found.
                    var existing = Explorer.GetExplorerHwndForPath(root);
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

            if (path == "/open-folder")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                var selected = Clips.GetSafeClipPath(query.ContainsKey("file") ? query["file"] : null);
                if (selected == null || !File.Exists(selected.Full)) { HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false, ["message"] = "Clip not found" }); return false; }
                try { HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["focused"] = Explorer.RevealInExplorer(selected.Full) }); }
                catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                return false;
            }

            // what the menu of a screenshot card does: delete it, rename it, put it on the clipboard and show it in its folder
            if (path == "/screenshots/delete" || path == "/screenshots/rename" || path == "/screenshots/copy" || path == "/screenshots/reveal")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try
                {
                    string shot = Screenshots.Resolve(query.ContainsKey("file") ? query["file"] : null);
                    if (shot == null) { HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false, ["message"] = "Screenshot not found" }); return false; }
                    var answer = new JObject { ["ok"] = true };
                    if (path == "/screenshots/delete") answer["kept"] = Screenshots.Delete(shot);
                    else if (path == "/screenshots/rename") answer["name"] = Screenshots.Rename(shot, query.ContainsKey("to") ? query["to"] : null);
                    else if (path == "/screenshots/copy") Screenshots.Copy(shot);
                    else answer["focused"] = Explorer.RevealInExplorer(shot);
                    HttpResponse.SendJson(stream, 200, answer);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is System.Runtime.InteropServices.ExternalException || ex is Newtonsoft.Json.JsonException)
                {
                    HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = ex.Message });
                }
                return false;
            }

            if (path == "/deleted/media")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                Clips.SafeClipPath selected;
                try { selected = DeletedHistory.PreviewFile(Q("id")); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is Newtonsoft.Json.JsonException)
                {
                    selected = null;
                }
                if (selected == null) { HttpResponse.SendText(stream, 404, "Not Found", "Deleted item not found"); return false; }
                if (Path.GetExtension(selected.Name) != ".gif") return HttpResponse.ServePreview(stream, req, selected);
                HttpResponse.SendFile(stream, 200, "OK", HttpResponse.GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = "image/gif" }), selected.Full, FileShare.Read | FileShare.Delete);
                return false;
            }

            // what the deleted view of the clips page lists and does: restore an item, delete one for good, or empty the whole history
            if (path == "/deleted" || path == "/deleted/thumb" || path == "/deleted/restore" || path == "/deleted/remove" || path == "/deleted/empty")
            {
                bool read = path == "/deleted" || path == "/deleted/thumb";
                if (req.Method != (read ? "GET" : "POST")) { HttpResponse.SendText(stream, 405, "Method Not Allowed", read ? "GET required" : "POST required"); return false; }
                try
                {
                    if (path == "/deleted") HttpResponse.SendJson(stream, 200, DeletedHistory.List());
                    else if (path == "/deleted/thumb")
                    {
                        string stored = DeletedHistory.StoredFile(Q("id"), out string kind);
                        string thumb = stored == null ? null : kind == "screenshot" ? Screenshots.Thumb(stored) : kind == "gif" ? GifExports.Thumb(stored) : Media.GetCachedThumbnail(new Clips.SafeClipPath { Name = Path.GetFileName(stored), Full = stored }, new FileInfo(stored));
                        if (thumb == null) HttpResponse.SendBytes(stream, 200, "OK", HttpResponse.GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = "image/svg+xml" }), Media.GetPlaceholderThumbnail());
                        else HttpResponse.SendFile(stream, 200, "OK", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = "image/jpeg", ["Cache-Control"] = "private, max-age=31536000, immutable", ["X-Content-Type-Options"] = "nosniff" }, thumb);
                    }
                    else
                    {
                        var done = path == "/deleted/restore" ? DeletedHistory.Restore(Q("id")) : path == "/deleted/remove" ? DeletedHistory.Remove(Q("id")) : DeletedHistory.Empty();
                        HttpResponse.SendJson(stream, done.Value<bool>("ok") ? 200 : 409, done);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is Newtonsoft.Json.JsonException || ex is System.Runtime.InteropServices.ExternalException)
                {
                    HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message });
                }
                return false;
            }

            if (path == "/delete")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                var deleted = Clips.DeleteClip(query.ContainsKey("file") ? query["file"] : null);
                if (deleted.Status == 200) HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["name"] = deleted.Name, ["kept"] = deleted.Kept });
                else HttpResponse.SendJson(stream, deleted.Status, new JObject { ["ok"] = false, ["message"] = deleted.Message });
                return false;
            }

            // the gifs of the clips page: the list, the file and the first-frame thumb of each card, an export from the clip menu, and what the menu of a gif card does
            if (path == "/gifs" || path == "/gifs/file" || path == "/gifs/thumb")
            {
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                try
                {
                    if (path == "/gifs") HttpResponse.SendJson(stream, 200, new JObject { ["gifs"] = GifExports.List() });
                    else
                    {
                        string file = GifExports.Resolve(Q("file"));
                        if (file == null) { HttpResponse.SendText(stream, 404, "Not Found", "GIF not found"); return false; }
                        bool thumb = path == "/gifs/thumb";
                        // the address carries the size and time of the file, as for screenshots, so the browser keeps each picture instead of reading it again on every paint of the grid
                        HttpResponse.SendFile(stream, 200, "OK", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["Content-Type"] = thumb ? "image/jpeg" : "image/gif",
                            ["Cache-Control"] = "private, max-age=31536000, immutable",
                            ["X-Content-Type-Options"] = "nosniff",
                        }, thumb ? GifExports.Thumb(file) : file);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is System.Runtime.InteropServices.ExternalException)
                {
                    HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = "The GIFs could not be read" });
                }
                return false;
            }

            if (path == "/gifs/export" || path == "/gifs/delete" || path == "/gifs/rename" || path == "/gifs/copy" || path == "/gifs/reveal")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try
                {
                    var answer = new JObject { ["ok"] = true };
                    if (path == "/gifs/export")
                    {
                        if (!double.TryParse(Q("start"), NumberStyles.Float, CultureInfo.InvariantCulture, out double start) ||
                            !double.TryParse(Q("end"), NumberStyles.Float, CultureInfo.InvariantCulture, out double end))
                            throw new ArgumentException("Invalid GIF range.");
                        string made = GifExports.Export(Q("file"), start, end);
                        long size = new FileInfo(Path.Combine(GifExports.Folder(), made)).Length;
                        answer["file"] = made;
                        answer["size"] = size;
                        // the popup over the obs window, so it is seen from any view; the clips page shows its own toast only when that popup could not be shown
                        answer["popup"] = DonePopups.Show("done", "GIF exported",
                            new JObject
                            {
                                ["icon"] = "gif",
                                ["media"] = "/gifs/file?file=" + Uri.EscapeDataString(made) + "&v=" + size,
                                ["text"] = Path.GetFileNameWithoutExtension(made),
                                ["detail"] = Storage.FormatBytes(size) + " · Saved to the GIFs section",
                                ["action"] = new JObject { ["label"] = "Go to GIF", ["kind"] = "reveal-gif", ["file"] = made },
                            });
                    }
                    else
                    {
                        string gif = GifExports.Resolve(Q("file"));
                        if (gif == null) { HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false, ["message"] = "GIF not found" }); return false; }
                        if (path == "/gifs/delete") answer["kept"] = GifExports.Delete(gif);
                        else if (path == "/gifs/rename") answer["name"] = GifExports.Rename(gif, Q("to"));
                        else if (path == "/gifs/copy") GifExports.Copy(gif);
                        else answer["focused"] = Explorer.RevealInExplorer(gif);
                    }
                    HttpResponse.SendJson(stream, 200, answer);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException ||
                    ex is InvalidOperationException || ex is OperationCanceledException || ex is System.ComponentModel.Win32Exception ||
                    ex is System.Runtime.InteropServices.ExternalException)
                { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
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

            if (path == "/rename")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                lock (RenameGate)
                {
                    var selected = Clips.GetSafeClipPath(query.ContainsKey("file") ? query["file"] : null);
                    if (selected == null || !File.Exists(selected.Full)) { HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false, ["message"] = "Source clip not found" }); return false; }
                    string newName = Clips.GetSafeFilename(query.ContainsKey("to") ? query["to"] : null);
                    if (newName == null) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "Bad new filename" }); return false; }
                    if (newName == selected.Name) { HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["name"] = newName }); return false; }
                    // the name is all a rename changes; the pages keep the extension, and a clip under another one would be read as another kind of file
                    if (!string.Equals(Path.GetExtension(newName), Path.GetExtension(selected.Name), StringComparison.OrdinalIgnoreCase)) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "A rename keeps the file type of the clip" }); return false; }
                    // a renamed clip stays in its own folder, and the name stays unique across the clip folder and its game folders
                    try
                    {
                        string requestedName = newName;
                        newName = ClipFiles.AvailableName(newName, candidate =>
                            !(string.Equals(candidate, selected.Name, StringComparison.OrdinalIgnoreCase) && string.Equals(candidate, requestedName, StringComparison.OrdinalIgnoreCase)) &&
                            (Clips.NameTaken(candidate) || File.Exists(Path.Combine(Path.GetDirectoryName(selected.Full), candidate)) ||
                             Directory.Exists(Path.Combine(Path.GetDirectoryName(selected.Full), candidate))));
                    }
                    catch (ArgumentException ex) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = ex.Message }); return false; }
                    if (newName == selected.Name) { HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["name"] = newName }); return false; }
                    string newPath = Path.Combine(Path.GetDirectoryName(selected.Full), newName);
                    if (newPath.Length >= 260) { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = "That name is too long" }); return false; }
                    // a change of capitals only is the same file to windows, so it is not a name another clip already has
                    bool caseOnly = string.Equals(newName, selected.Name, StringComparison.OrdinalIgnoreCase);
                    string editorView = query.ContainsKey("view") && (query["view"] == "clips" || query["view"] == "home") ? query["view"] : null;
                    string requestId = UploadState.NewRequestId();
                    using var reservation = JobCoordinator.TryReserve(requestId, selected.Name, selected.Full, "rename", out string busy, newPath, editorView);
                    if (reservation == null) { HttpResponse.SendJson(stream, 409, new JObject { ["ok"] = false, ["message"] = busy }); return false; }
                    try
                    {
                        string fileId = null;
                        JobCoordinator.Commit(requestId, () => JobCoordinator.ChangeSource(selected.Full, () =>
                        {
                            // a restore from deleted history or a save can take the name between the check above and here
                            if (!caseOnly && (File.Exists(newPath) || Directory.Exists(newPath) || Clips.NameTaken(newName))) throw new IOException("A file with that name already exists");
                            fileId = ClipRenames.Identity(new FileInfo(selected.Full));
                            ClipFiles.MoveWhenFree(selected.Full, newPath);
                            JobCoordinator.RenameEditorSource(selected.Full, newPath, editorView);
                        }));
                        if (!ClipRenames.MoveMetadata(Clips.LibraryRoot(Path.GetDirectoryName(newPath)), selected.Full, newPath, fileId))
                            ClipFolderWorker.Poke();
                        AppConfig.ClearClipsCache();
                        HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["name"] = newName });
                    }
                    catch (IOException ex) when (ClipFiles.InUse(ex)) { HttpResponse.SendJson(stream, 409, new JObject { ["ok"] = false, ["message"] = ClipFiles.InUseMessage }); }
                    catch (Exception ex) { HttpResponse.SendJson(stream, 500, new JObject { ["ok"] = false, ["message"] = ex.Message }); }
                    return false;
                }
            }

            return null;
        }
    }
}

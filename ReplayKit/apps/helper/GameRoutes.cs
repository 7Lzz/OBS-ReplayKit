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
    // the routes of the game library, search, icons and the game a clip is filed under
    internal static class GameRoutes
    {
        // answers the request when its path is one of these routes, with what DispatchRequest returns; null when it is not
        public static bool? Handle(RouteRequest r)
        {
            Stream stream = r.Stream;
            HttpRequest req = r.Req;
            string path = r.Path;
            string Q(string key, string def = "") => r.Q(key, def);
            bool QFlag(string key) => r.QFlag(key);

            if (path == "/games/library" || path == "/games/search" || path == "/games/icon" || path == "/games/set" || path == "/games/reset" || path == "/games/mix" || path == "/games/remove")
            {
                try
                {
                    if (path == "/games/remove")
                    {
                        if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                        GameCatalog.Remove(Q("id"));
                        HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true });
                    }
                    else if (path == "/games/set" || path == "/games/reset" || path == "/games/mix")
                    {
                        if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                        bool screenshot = QFlag("screenshot");
                        string version = Q("v");
                        string file = screenshot ? Screenshots.Resolve(Q("file"), version) : Clips.GetSafeClipPath(Q("file"))?.Full;
                        if (file == null || !File.Exists(file)) throw new ArgumentException(Locales.Message("common.clipNotFound2"));
                        var result = new JObject { ["ok"] = true };
                        if (path == "/games/reset") ClipGames.Reset(file);
                        else if (path == "/games/mix") result["capture"] = Mix(file, version, req.Body);
                        else ClipGames.Correct(file, Q("id"), Q("name"), 0, Q("universe"));
                        // a clip given another game is filed in the folder of that game
                        if (!screenshot) ClipFolderWorker.Poke();
                        HttpResponse.SendJson(stream, 200, result);
                    }
                    else if (req.Method != "GET") HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required");
                    else if (path == "/games/library") HttpResponse.SendJson(stream, 200, ClipGames.Library());
                    else if (path == "/games/search") HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["games"] = GameCatalog.Search(Q("q")) });
                    else
                    {
                        byte[] icon = GameCatalog.IconBytes(Q("id"));
                        string contentType = "image/png";
                        if (icon == null && QFlag("catalog"))
                            icon = CatalogArtwork.Get(Q("id"), out contentType);
                        if (icon == null) HttpResponse.SendText(stream, 404, "Not Found", "Game icon unavailable.");
                        else HttpResponse.SendBytes(stream, 200, "OK", HttpResponse.GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = contentType }), icon);
                    }
                }
                catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is Newtonsoft.Json.JsonException)
                { HttpResponse.SendJson(stream, 400, new JObject { ["ok"] = false, ["message"] = Locales.Message("clips.gameDetailsCouldNotBeRead") }); }
                return false;
            }

            return null;
        }

        private static JObject Mix(string file, string version, string body)
        {
            if (string.IsNullOrEmpty(body) || body.Length > 2048) throw new ArgumentException();
            var edit = JObject.Parse(body, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (edit["action"]?.Type != JTokenType.String || !OptionalString(edit["id"]) || !OptionalString(edit["name"]))
                throw new ArgumentException();
            return ClipGames.EditMix(file, version, edit.Value<string>("action"), edit.Value<string>("id"), edit.Value<string>("name"));
        }

        private static bool OptionalString(JToken value) => value == null || value.Type == JTokenType.String || value.Type == JTokenType.Null;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static ReplayKitHelper.Routes;

namespace ReplayKitHelper
{
    // the routes of the translations: the messages of a language as a script every page loads before its body, and as json for a page that switches language without loading again
    internal static class LocaleRoutes
    {
        // answers the request when its path is one of these routes, with what DispatchRequest returns; null when it is not
        public static bool? Handle(RouteRequest r)
        {
            Stream stream = r.Stream;
            HttpRequest req = r.Req;
            string path = r.Path;
            if (path != "/locale/messages.js" && path != "/locale/messages.json") return null;
            if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
            string code = r.Q("lang", "");
            if (!Locales.IsAvailable(code)) { HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false, ["message"] = Locales.Message("common.unknownLanguage") }); return false; }
            var messages = Locales.Messages(code);
            bool script = path == "/locale/messages.js";
            // escape < > so no string can close the script it is served in
            var jsonSettings = new JsonSerializerSettings { StringEscapeHandling = StringEscapeHandling.EscapeHtml };
            string json = JsonConvert.SerializeObject(messages, jsonSettings);
            byte[] body = Encoding.UTF8.GetBytes(script ? "window.RK_MESSAGES=" + json + ";" : json);
            var headers = HttpResponse.GetNoStoreHeaders(new Dictionary<string, string>
            {
                ["Content-Type"] = script ? "application/javascript; charset=utf-8" : "application/json; charset=utf-8",
                ["Cache-Control"] = script && r.Q("v", "") == Locales.BundleVersion(code)
                    ? "private, max-age=31536000, immutable" : "no-store",
            });
            HttpResponse.SendBytes(stream, 200, "OK", headers, body);
            return false;
        }
    }
}

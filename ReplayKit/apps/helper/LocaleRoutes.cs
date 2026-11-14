using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static ReplayKitHelper.Routes;

namespace ReplayKitHelper
{
    // the routes of the translations: the strings of a language as a script every page loads before its body, and as json for a page that switches language without loading again
    internal static class LocaleRoutes
    {
        // answers the request when its path is one of these routes, with what DispatchRequest returns; null when it is not
        public static bool? Handle(RouteRequest r)
        {
            Stream stream = r.Stream;
            HttpRequest req = r.Req;
            string path = r.Path;
            if (path != "/locale/strings.js" && path != "/locale/strings.json" && path != "/locale/messages.json") return null;
            if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
            string code = r.Q("lang", "");
            if (!Locales.IsAvailable(code)) { HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false, ["message"] = "Unknown language." }); return false; }
            // escape < > so no string can close the script it is served in
            var messages = Locales.Messages(code);
            bool script = path == "/locale/strings.js";
            bool explicitPage = script && r.Q("explicit", "") == "1";
            var legacy = explicitPage ? null : Locales.Strings(code);
            var jsonSettings = new JsonSerializerSettings { StringEscapeHandling = StringEscapeHandling.EscapeHtml };
            byte[] body = Encoding.UTF8.GetBytes(script
                ? "window.RK_MESSAGES=" + JsonConvert.SerializeObject(messages, jsonSettings) + ";" +
                  (explicitPage || code == Locales.English ? "" : "window.RK_STRINGS=" + JsonConvert.SerializeObject(legacy, jsonSettings) + ";")
                : JsonConvert.SerializeObject(path == "/locale/strings.json" ? (object)legacy : new { legacy, messages }, jsonSettings));
            var headers = HttpResponse.GetNoStoreHeaders(new Dictionary<string, string>
            {
                ["Content-Type"] = script ? "application/javascript; charset=utf-8" : "application/json; charset=utf-8",
                ["Cache-Control"] = script && r.Q("v", "") == Locales.BundleVersion(code, explicitPage)
                    ? "private, max-age=31536000, immutable" : "no-store",
            });
            HttpResponse.SendBytes(stream, 200, "OK", headers, body);
            return false;
        }
    }
}

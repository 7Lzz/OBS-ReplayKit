using System.IO;
using Newtonsoft.Json.Linq;
using static ReplayKitHelper.Routes;

namespace ReplayKitHelper
{
    // the route the plugin answers the requests of ObsRequest on
    internal static class ObsRoutes
    {
        // answers the request when its path is one of these routes, with what DispatchRequest returns; null when it is not
        public static bool? Handle(RouteRequest r)
        {
            if (r.Path != "/obs/reply") return null;
            Stream stream = r.Stream;
            if (r.Req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
            bool taken = ObsRequest.Answer(r.Q("id"), r.Req.Body);
            HttpResponse.SendJson(stream, taken ? 200 : 404, new JObject { ["ok"] = taken });
            return false;
        }
    }
}

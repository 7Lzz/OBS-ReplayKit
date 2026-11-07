using System.Collections.Generic;
using System.IO;

namespace ReplayKitHelper
{
    // one request as the route classes see it: the stream to answer on, the request and its query
    internal sealed class RouteRequest
    {
        public readonly Stream Stream;
        public readonly HttpRequest Req;
        public readonly string Path;
        public readonly Dictionary<string, string> Query;

        public RouteRequest(Stream stream, HttpRequest req)
        {
            Stream = stream;
            Req = req;
            Path = req.Path;
            Query = req.Query;
        }

        public string Q(string key, string def = "") => Query.TryGetValue(key, out string v) ? v : def;

        public bool QFlag(string key) => Query.TryGetValue(key, out string v) && v == "1";
    }
}

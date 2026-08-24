using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace ReplayKitHelper
{
    internal sealed class HttpRequest
    {
        public string Method;
        public string Path;
        public Dictionary<string, string> Query = new Dictionary<string, string>();
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Body = "";
    }

    // per-connection http/1.1 request parsing and the accept-loop's per-socket handler: read one request, dispatch it, and (for /file/ range requests only) keep the socket open for a short idle window so a video element's next range request reuses it instead of paying a fresh tcp handshake per chunk. ported from obs_replaykit helper modules/80_connection.ps1.
    internal static class Connection
    {
        // returns null on a closed connection or a request line too broken to route, which the caller treats as "stop reading this connection".
        public static HttpRequest ReadHttpRequest(Stream stream, StreamReader reader)
        {
            string requestLine = reader.ReadLine();
            if (string.IsNullOrEmpty(requestLine)) return null;
            var parts = requestLine.Split(' ');
            if (parts.Length < 2) return null;
            string method = parts[0];
            string rawPath = parts[1];

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string line;
            while (!string.IsNullOrEmpty(line = reader.ReadLine()))
            {
                int idx = line.IndexOf(':');
                if (idx > 0)
                {
                    string k = line.Substring(0, idx).Trim().ToLowerInvariant();
                    string v = line.Substring(idx + 1).Trim();
                    headers[k] = v;
                }
            }

            string body = "";
            int bodyLen = 0;
            if (headers.TryGetValue("content-length", out string clStr))
            {
                if (!int.TryParse(clStr, out bodyLen) || bodyLen < 0)
                {
                    HttpResponse.SendText(stream, 400, "Bad Request", "Invalid Content-Length");
                    return null;
                }
                if (bodyLen > 1024 * 1024)
                {
                    HttpResponse.SendText(stream, 413, "Payload Too Large", "Request body too large");
                    return null;
                }
            }
            if (bodyLen > 0)
            {
                var buf = new char[bodyLen];
                int read = 0;
                while (read < bodyLen)
                {
                    int n = reader.Read(buf, read, bodyLen - read);
                    if (n <= 0) break;
                    read += n;
                }
                body = new string(buf, 0, read);
            }

            int qIdx = rawPath.IndexOf('?');
            string path = qIdx >= 0 ? rawPath.Substring(0, qIdx) : rawPath;
            string queryString = qIdx >= 0 ? rawPath.Substring(qIdx + 1) : "";
            var query = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(queryString))
            {
                foreach (var pair in queryString.Split('&'))
                {
                    if (string.IsNullOrEmpty(pair)) continue;
                    int eq = pair.IndexOf('=');
                    if (eq >= 0)
                        query[Uri.UnescapeDataString(pair.Substring(0, eq))] = Uri.UnescapeDataString(pair.Substring(eq + 1));
                    else
                        query[Uri.UnescapeDataString(pair)] = "";
                }
            }

            return new HttpRequest
            {
                Method = method.ToUpperInvariant(),
                Path = path,
                Query = query,
                Headers = headers,
                Body = body,
            };
        }

        public static void HandleConnection(TcpClient client)
        {
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = 5000;
                // a write that stalls (client not draining its receive window) blocks this connection's pool thread for the full timeout before .net aborts it -- nothing on localhost legitimately needs anywhere near that long, and a stuck client shouldnt tie up a pool slot repeatedly.
                client.SendTimeout = 3000;
                var stream = client.GetStream();
                using (var reader = new StreamReader(stream, Encoding.ASCII, false, 8192, true))
                {
                    // every route answers once and closes (Connection: close from GetNoStoreHeaders) except /file/, which can ask to keep going so a video element's next range request reuses this socket instead of paying a fresh tcp handshake per chunk.
                    while (true)
                    {
                        var req = ReadHttpRequest(stream, reader);
                        if (req == null) break;
                        bool keepAlive = Routes.DispatchRequest(stream, req);
                        if (!keepAlive) break;

                        // a kept-alive /file/ socket is betting the same video element's next range request follows within tens of ms, so it gets a short idle timeout instead of the fresh-connection 5s one. sacrificing this slot the instant a new connection shows up elsewhere in the pool once broke ordinary mid-playback traffic, so this waits out its own budget regardless of what else the listener is doing.
                        int waitedMs = 0;
                        bool gotNextRequest = false;
                        while (waitedMs < 500)
                        {
                            if (client.Client.Poll(20000, SelectMode.SelectRead)) { gotNextRequest = true; break; }
                            waitedMs += 20;
                        }
                        if (!gotNextRequest)
                        {
                            Log.Write("Handle-Connection: keep-alive idle-timeout path=" + req.Path + " waitedMs=" + waitedMs);
                            break;
                        }
                        client.ReceiveTimeout = 500;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("connection error: " + ex.Message);
            }
            finally
            {
                try { client.Close(); } catch (Exception ex) when (ex is IOException || ex is SocketException) { }
            }
        }
    }
}

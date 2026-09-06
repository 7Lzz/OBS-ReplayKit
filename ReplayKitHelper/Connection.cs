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
        internal static readonly Encoding WireEncoding = Encoding.GetEncoding(28591);
        private static readonly Encoding BodyEncoding = new UTF8Encoding(false, true);

        private static string ReadLine(StreamReader reader)
        {
            var line = new StringBuilder();
            while (line.Length <= 8192)
            {
                int value = reader.Read();
                if (value < 0)
                {
                    if (line.Length == 0) return null;
                    throw new InvalidDataException("Incomplete HTTP header.");
                }
                if (value == '\r')
                {
                    if (reader.Read() != '\n') throw new InvalidDataException("Invalid HTTP line ending.");
                    return line.ToString();
                }
                if (value < 32 && value != '\t' || value > 126)
                    throw new InvalidDataException("Invalid HTTP header character.");
                line.Append((char)value);
            }
            throw new InvalidDataException("HTTP header line too long.");
        }

        // returns null on a closed connection or a request line too broken to route, which the caller treats as "stop reading this connection".
        public static HttpRequest ReadHttpRequest(Stream stream, StreamReader reader)
        {
            string requestLine = ReadLine(reader);
            if (string.IsNullOrEmpty(requestLine)) return null;
            var parts = requestLine.Split(' ');
            if (parts.Length != 3 || (parts[2] != "HTTP/1.1" && parts[2] != "HTTP/1.0") || !parts[1].StartsWith("/", StringComparison.Ordinal))
                throw new InvalidDataException("Invalid HTTP request line.");
            string method = parts[0];
            string rawPath = parts[1];

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string line;
            int headerBytes = requestLine.Length + 2;
            while ((line = ReadLine(reader)) != "")
            {
                if (line == null) throw new InvalidDataException("Incomplete HTTP headers.");
                headerBytes += line.Length + 2;
                if (headerBytes > 32768) throw new InvalidDataException("HTTP headers too large.");
                int idx = line.IndexOf(':');
                if (idx <= 0) throw new InvalidDataException("Invalid HTTP header.");
                {
                    string k = line.Substring(0, idx).Trim().ToLowerInvariant();
                    string v = line.Substring(idx + 1).Trim();
                    if (headers.ContainsKey(k)) throw new InvalidDataException("Duplicate HTTP header.");
                    headers.Add(k, v);
                }
            }

            if (headers.ContainsKey("transfer-encoding"))
                throw new InvalidDataException("Transfer-Encoding is not supported.");

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
                    if (n <= 0) throw new InvalidDataException("Incomplete HTTP body.");
                    read += n;
                }
                // Content-Length counts bytes; the wire reader maps each byte to one character.
                body = BodyEncoding.GetString(WireEncoding.GetBytes(buf, 0, read));
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
                using (var reader = new StreamReader(stream, WireEncoding, false, 8192, true))
                {
                    // every route answers once and closes (Connection: close from GetNoStoreHeaders) except /file/, which can ask to keep going so a video element's next range request reuses this socket instead of paying a fresh tcp handshake per chunk.
                    while (true)
                    {
                        var req = ReadHttpRequest(stream, reader);
                        if (req == null) break;
                        bool keepAlive = Routes.DispatchRequest(stream, req);
                        if (!keepAlive) break;

                        // Read buffered requests first; polling the socket misses StreamReader's read-ahead.
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

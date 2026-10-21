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
        // an upload keeps its body on the wire: the number of bytes it announced and the reader to take them from, which is for the route to read
        public long UploadLength = -1;
        public StreamReader UploadReader;
    }

    // per-connection http/1.1 request parsing and the per-socket handler of the accept loop: read one request, dispatch it, and keep the socket open only for /file/ range requests so a video elements next range request reuses it instead of paying a fresh tcp handshake per chunk
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

            int qIdx = rawPath.IndexOf('?');
            string path = qIdx >= 0 ? rawPath.Substring(0, qIdx) : rawPath;
            // a file sent by the editor is far past what a body may hold in memory, so its route reads it off the wire itself and sets its own limit
            bool upload = method == "POST" && (path == AudioImports.UploadPath || path == MediaImports.UploadPath);

            string body = "";
            int bodyLen = 0;
            if (headers.TryGetValue("content-length", out string clStr))
            {
                if (!int.TryParse(clStr, out bodyLen) || bodyLen < 0)
                {
                    HttpResponse.SendText(stream, 400, "Bad Request", "Invalid Content-Length");
                    return null;
                }
                if (bodyLen > 1024 * 1024 && !upload)
                {
                    HttpResponse.SendText(stream, 413, "Payload Too Large", "Request body too large");
                    return null;
                }
            }
            if (bodyLen > 0 && !upload)
            {
                var buf = new char[bodyLen];
                int read = 0;
                while (read < bodyLen)
                {
                    int n = reader.Read(buf, read, bodyLen - read);
                    if (n <= 0) throw new InvalidDataException("Incomplete HTTP body.");
                    read += n;
                }
                // content-Length counts bytes; the wire reader maps each byte to one character.
                body = BodyEncoding.GetString(WireEncoding.GetBytes(buf, 0, read));
            }

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
                UploadLength = upload ? bodyLen : -1,
                UploadReader = upload ? reader : null,
            };
        }

        // moves the body of an upload from the wire into a stream, as bytes, while a hash reads them too; the length it announced is what is taken, and a body that ends short is an error
        public static void CopyUploadBody(HttpRequest req, Stream destination, System.Security.Cryptography.HashAlgorithm hash)
        {
            var chars = new char[65536];
            var bytes = new byte[65536];
            long left = req.UploadLength;
            while (left > 0)
            {
                int n = req.UploadReader.Read(chars, 0, (int)Math.Min(chars.Length, left));
                if (n <= 0) throw new InvalidDataException("Incomplete upload.");
                // the wire reader maps each byte to one character, so this gives the bytes back as they came
                int count = WireEncoding.GetBytes(chars, 0, n, bytes, 0);
                destination.Write(bytes, 0, count);
                hash.TransformBlock(bytes, 0, count, null, 0);
                left -= n;
            }
            hash.TransformFinalBlock(new byte[0], 0, 0);
        }

        public static void HandleConnection(TcpClient client)
        {
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = 5000;
                // a write that stalls (client not draining its receive window) blocks this connections pool thread for the full timeout before .net aborts it -- nothing on localhost legitimately needs anywhere near that long, and a stuck client shouldnt tie up a pool slot repeatedly.
                client.SendTimeout = 3000;
                var stream = client.GetStream();
                using (var reader = new StreamReader(stream, WireEncoding, false, 8192, true))
                {
                    // every route answers once and closes (Connection: close from GetNoStoreHeaders) except /file/, which can ask to keep going so a video elements next range request reuses this socket instead of paying a fresh tcp handshake per chunk.
                    bool keptAlive = false;
                    while (true)
                    {
                        HttpRequest req;
                        using (var deadline = new System.Threading.Timer(_ => client.Close(), null, 10000, System.Threading.Timeout.Infinite))
                        {
                            try { req = ReadHttpRequest(stream, reader); }
                            // a kept-alive socket that stays quiet past its short read timeout is the normal end of it, not an error
                            catch (IOException) when (keptAlive) { req = null; }
                            using (var drained = new System.Threading.ManualResetEvent(false))
                            {
                                if (deadline.Dispose(drained)) drained.WaitOne();
                            }
                        }
                        if (req == null) break;
                        bool keepAlive = Routes.DispatchRequest(stream, req);
                        if (!keepAlive) break;

                        // the next request is read through the same reader, so one already held by its read-ahead is not missed
                        keptAlive = true;
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

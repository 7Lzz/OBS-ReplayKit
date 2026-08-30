using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // http response writers. everything goes to a NetworkStream held open until the underlying TcpClient is closed (see Connection.cs). ported from obs_replaykit helper modules/70_http_response.ps1.
    internal static class HttpResponse
    {
        public static Dictionary<string, string> GetNoStoreHeaders(Dictionary<string, string> extra = null)
        {
            var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Access-Control-Allow-Origin"] = "*",
                ["Access-Control-Allow-Methods"] = "GET,POST,OPTIONS",
                ["Access-Control-Allow-Headers"] = "Content-Type,Range",
                ["Cache-Control"] = "no-store",
                ["Connection"] = "close",
            };
            if (extra != null) foreach (var kv in extra) h[kv.Key] = kv.Value;
            return h;
        }

        public static byte[] FormatHttpResponse(int status, string statusText, Dictionary<string, string> headers, long bodyLength)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(statusText).Append("\r\n");
            if (!headers.ContainsKey("Content-Length")) headers["Content-Length"] = bodyLength.ToString();
            foreach (var kv in headers) sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
            sb.Append("\r\n");
            return Encoding.ASCII.GetBytes(sb.ToString());
        }

        public static void SendBytes(Stream stream, int status, string statusText, Dictionary<string, string> headers, byte[] body)
        {
            var head = FormatHttpResponse(status, statusText, headers, body.Length);
            stream.Write(head, 0, head.Length);
            if (body.Length > 0) stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        public static void SendFile(Stream stream, int status, string statusText, Dictionary<string, string> headers, string path)
        {
            var fi = new FileInfo(path);
            var head = FormatHttpResponse(status, statusText, headers, fi.Length);
            stream.Write(head, 0, head.Length);
            using (var fs = File.OpenRead(path))
            {
                var buf = new byte[65536];
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0) stream.Write(buf, 0, n);
                stream.Flush();
            }
        }

        public static void SendText(Stream stream, int status, string statusText, string text, string ctype = "text/plain; charset=utf-8")
        {
            byte[] body = Encoding.UTF8.GetBytes(text ?? "");
            var h = GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = ctype });
            SendBytes(stream, status, statusText, h, body);
        }

        public static void SendJson(Stream stream, int status, JToken data)
        {
            string json = data?.ToString(Formatting.None) ?? "null";
            SendText(stream, status, GetStatusText(status), json, "application/json; charset=utf-8");
        }

        public static string GetStatusText(int code)
        {
            switch (code)
            {
                case 200: return "OK";
                case 202: return "Accepted";
                case 204: return "No Content";
                case 206: return "Partial Content";
                case 302: return "Found";
                case 400: return "Bad Request";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 409: return "Conflict";
                case 413: return "Payload Too Large";
                case 416: return "Range Not Satisfiable";
                case 500: return "Internal Server Error";
                case 503: return "Service Unavailable";
                default: return "OK";
            }
        }

        // splice the active theme's :root override in right before </head> so every dock page loads themed. no-op on
        // the default theme and if the markup has no </head>.
        private static byte[] InjectThemeStyle(byte[] html)
        {
            try
            {
                string tag = Themes.DockStyleTag(ReplaykitSettings.Normalize(ReplaykitSettings.ReadSettings()));
                if (string.IsNullOrEmpty(tag)) return html;
                string s = System.Text.Encoding.UTF8.GetString(html);
                int i = s.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
                if (i < 0) return html;
                return new UTF8Encoding(false).GetBytes(s.Substring(0, i) + tag + s.Substring(i));
            }
            catch { return html; }
        }

        public static void ServeHtml(Stream stream, string filename)
        {
            var candidates = new[]
            {
                Path.Combine(AppConfig.GetDockDir(), filename),
                Path.Combine(AppConfig.GetDefaultDockDir(), filename),
                Path.Combine(AppConfig.GetScriptDir() ?? "", filename),
            };
            foreach (var f in candidates)
            {
                if (File.Exists(f))
                {
                    try
                    {
                        byte[] bytes = File.ReadAllBytes(f);
                        bytes = InjectThemeStyle(bytes);
                        var h = GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = "text/html; charset=utf-8" });
                        SendBytes(stream, 200, "OK", h, bytes);
                        return;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                }
            }
            SendText(stream, 404, "Not Found", filename + " not found");
        }

        // serves one range chunk of a clip and reports whether the connection is worth keeping open -- playback at high speed multipliers needs a new chunk far more often than realtime, and a fresh tcp handshake per chunk (plain connection:close) couldnt keep up and showed up as buffering; returning true lets Connection.HandleConnection reuse the socket for the next range request.
        public static bool ServePreview(Stream stream, HttpRequest req, string rawName)
        {
            // reqId lets start/end/error log lines for the same call be matched up -- this function otherwise writes nothing on a fast/happy path, so a stuck or failed preview would leave no trace at all.
            string reqId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var sw = Stopwatch.StartNew();
            if (Server.State.ActivePreviews >= Constants.MAX_PREVIEW_STREAM)
            {
                // scrubbing/seeking fires a burst of overlapping range requests that each abort the last -- normal <video> behavior, not a bug, so briefly overlapping here is routine, not the sustained overload this cap exists for. a slot typically frees in single-digit ms, so a short bounded wait clears almost every one of these instead of hard-failing a request a video element cant retry on its own.
                int busyWaitedMs = 0;
                while (Server.State.ActivePreviews >= Constants.MAX_PREVIEW_STREAM && busyWaitedMs < 300)
                {
                    Thread.Sleep(15);
                    busyWaitedMs += 15;
                }
                if (Server.State.ActivePreviews >= Constants.MAX_PREVIEW_STREAM)
                {
                    Log.Write("Serve-Preview[" + reqId + "] BUSY name=" + rawName + " active=" + Server.State.ActivePreviews + " cap=" + Constants.MAX_PREVIEW_STREAM + " waitedMs=" + busyWaitedMs);
                    SendText(stream, 503, "Service Unavailable", "Preview busy");
                    return false;
                }
            }
            var selected = Clips.GetSafeClipPath(rawName);
            if (selected == null)
            {
                Log.Write("Serve-Preview[" + reqId + "] BAD-FILENAME raw=" + rawName);
                SendText(stream, 400, "Bad Request", "Bad filename");
                return false;
            }
            if (!File.Exists(selected.Full))
            {
                Log.Write("Serve-Preview[" + reqId + "] NOT-FOUND name=" + selected.Name);
                SendText(stream, 404, "Not Found", "Clip not found");
                return false;
            }
            var fi = new FileInfo(selected.Full);
            long fileSize = fi.Length;
            long start = 0;
            long end = fileSize - 1;

            req.Headers.TryGetValue("range", out string range);
            if (!string.IsNullOrEmpty(range))
            {
                var m = Regex.Match(range, @"^bytes=(\d*)-(\d*)$");
                if (m.Success)
                {
                    string lo = m.Groups[1].Value;
                    string hi = m.Groups[2].Value;
                    if (lo == "")
                    {
                        long suffix = string.IsNullOrEmpty(hi) ? 0 : long.Parse(hi);
                        if (suffix <= 0) { SendText(stream, 416, "Range Not Satisfiable", ""); return false; }
                        start = Math.Max(fileSize - suffix, 0);
                    }
                    else
                    {
                        start = long.Parse(lo);
                        if (hi != "") end = long.Parse(hi);
                    }
                }
            }
            if (start < 0 || start >= fileSize || end < start)
            {
                SendText(stream, 416, "Range Not Satisfiable", "");
                return false;
            }
            end = Math.Min(end, fileSize - 1);
            end = Math.Min(end, start + Constants.PREVIEW_CHUNK - 1);
            long length = end - start + 1;
            // honor an explicit client close request even on a successful chunk -- no point offering keep-alive to a socket the caller already said it was done with.
            req.Headers.TryGetValue("connection", out string connHeader);
            bool clientWantsClose = (connHeader ?? "").Trim().ToLowerInvariant() == "close";

            lock (Server.State.PreviewLock) { Server.State.ActivePreviews++; }
            Log.Write("Serve-Preview[" + reqId + "] START name=" + selected.Name + " range=" + range + " start=" + start + " end=" + end + " length=" + length + " keepAliveReq=" + !clientWantsClose + " active=" + Server.State.ActivePreviews);
            bool sentFully = false;
            try
            {
                string ext = Path.GetExtension(selected.Name).ToLowerInvariant();
                string ctype = Constants.CONTENT_TYPES.TryGetValue(ext, out string ct) ? ct : "application/octet-stream";
                var h = GetNoStoreHeaders(new Dictionary<string, string>
                {
                    ["Content-Type"] = ctype,
                    ["Accept-Ranges"] = "bytes",
                    ["Content-Range"] = "bytes " + start + "-" + end + "/" + fileSize,
                    ["Connection"] = clientWantsClose ? "close" : "keep-alive",
                });
                var head = FormatHttpResponse(206, "Partial Content", h, length);
                stream.Write(head, 0, head.Length);

                // explicit FileShare.Delete -- FileStream's defualt share mode (read only) blocks any concurrent delete/rename/replace-on-top of this file for as long as this handle is open, and /delete can land on a different pool thread while this one is mid-stream. Delete share doesnt affect what gets read here: windows keeps this handles view of the data valid even if the file gets unlinked out from under it mid-read.
                using (var fs = new FileStream(selected.Full, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                {
                    fs.Seek(start, SeekOrigin.Begin);
                    var buf = new byte[65536];
                    long remaining = length;
                    while (remaining > 0)
                    {
                        int read = (int)Math.Min(buf.Length, remaining);
                        int n = fs.Read(buf, 0, read);
                        if (n <= 0) break;
                        stream.Write(buf, 0, n);
                        remaining -= n;
                    }
                    sentFully = remaining == 0;
                    stream.Flush();
                }
            }
            catch (Exception ex)
            {
                Log.Write("Serve-Preview[" + reqId + "] EXCEPTION after " + sw.ElapsedMilliseconds + "ms name=" + selected.Name + ": " + ex.GetType().Name + ": " + ex.Message);
                throw;
            }
            finally
            {
                lock (Server.State.PreviewLock) { Server.State.ActivePreviews--; }
            }
            Log.Write("Serve-Preview[" + reqId + "] DONE name=" + selected.Name + " sentFully=" + sentFully + " elapsedMs=" + sw.ElapsedMilliseconds + " keepAliveResp=" + (sentFully && !clientWantsClose));
            // a short read already broke the promised content-length, so the stream is out of sync for whatever request would come next on this connection -- close instead of pretending its reusable.
            return sentFully && !clientWantsClose;
        }

        public static void ServeThumbnail(Stream stream, string rawName)
        {
            var selected = Clips.GetSafeClipPath(rawName);
            if (selected == null || !File.Exists(selected.Full))
            {
                var hh = GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = "image/svg+xml" });
                SendBytes(stream, 200, "OK", hh, Media.GetPlaceholderThumbnail());
                return;
            }
            var fi = new FileInfo(selected.Full);
            string thumb = Media.GetCachedThumbnail(selected, fi);
            if (thumb != null)
            {
                try
                {
                    var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Access-Control-Allow-Origin"] = "*",
                        ["Cache-Control"] = "public, max-age=31536000, immutable",
                        ["Content-Type"] = "image/jpeg",
                        ["Connection"] = "close",
                    };
                    SendFile(stream, 200, "OK", h, thumb);
                    return;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
            var h2 = GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = "image/svg+xml" });
            SendBytes(stream, 200, "OK", h2, Media.GetPlaceholderThumbnail());
        }

        public static void ServeObsIcon(Stream stream)
        {
            string iconPath = Media.GetObsIconIco();
            if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(iconPath);
                    var h = GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = "image/x-icon" });
                    SendBytes(stream, 200, "OK", h, bytes);
                    return;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
            var h2 = GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = "image/svg+xml" });
            SendBytes(stream, 200, "OK", h2, Media.GetObsIconSvg());
        }
    }
}

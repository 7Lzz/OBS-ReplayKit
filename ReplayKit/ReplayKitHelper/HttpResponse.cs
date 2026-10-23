using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // http response writers; everything goes to the NetworkStream of a connection that Connection.cs closes
    internal static class HttpResponse
    {
        public static Dictionary<string, string> GetNoStoreHeaders(Dictionary<string, string> extra = null)
        {
            var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Cache-Control"] = "no-store",
                ["X-Frame-Options"] = "DENY",
                ["Cross-Origin-Resource-Policy"] = "same-origin",
                ["X-Content-Type-Options"] = "nosniff",
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
            using (var fs = File.OpenRead(path))
            {
                var head = FormatHttpResponse(status, statusText, headers, fs.Length);
                stream.Write(head, 0, head.Length);
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

        // splice the active themes :root override in right before </head> so every dock page loads themed. no-op on the default theme and if the markup has no </head>.
        private static string InjectThemeStyle(string html)
        {
            try
            {
                string tag = Themes.DockStyleTag(ReplaykitSettings.Normalize(ReplaykitSettings.ReadSettings()));
                if (string.IsNullOrEmpty(tag)) return html;
                int i = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
                if (i < 0) return html;
                return html.Insert(i, tag);
            }
            catch { return html; }
        }

        private static readonly object _dockIconLock = new object();
        private static string _dockIconStamp;
        private static string _dockIconTag;

        // splice the dock icon set in as window.RK_ICONS before </head>, same shape as InjectThemeStyle -- icons live as .svg files in subfolders of icons/ (player/, action/, nav/, ...) and the dock html pulls their inner markup from this map keyed by "<folder>/<name>" (see the ICON block in clips.html and the svg[data-i] init in settings.html); rebuilt only when a file mtime changes; icons/user/ holds .ico only
        private static string DockIconTag()
        {
            string dir = Constants.APP_ICONS_DIR;
            if (!Directory.Exists(dir)) return "";
            var files = Directory.GetFiles(dir, "*.svg", SearchOption.AllDirectories);
            if (files.Length == 0) return "";
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            var sb = new StringBuilder(files.Length.ToString());
            foreach (var f in files) sb.Append('|').Append(f).Append(':').Append(File.GetLastWriteTimeUtc(f).Ticks);
            string stamp = sb.ToString();
            lock (_dockIconLock)
            {
                if (stamp == _dockIconStamp && _dockIconTag != null) return _dockIconTag;
                var map = new JObject();
                foreach (var f in files)
                {
                    string key = f.Substring(dir.Length).TrimStart('\\', '/').Replace('\\', '/');
                    if (key.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) key = key.Substring(0, key.Length - 4);
                    if (key.StartsWith("ui/", StringComparison.Ordinal)) continue; // css-only masks, served by /icons/, no js consumer
                    map[key] = File.ReadAllText(f).Trim();
                }
                // escape < > so nothing in an svg can break out of the <script> block or be read as a tag
                string json = map.ToString(Formatting.None).Replace("<", "\\u003c").Replace(">", "\\u003e");
                _dockIconStamp = stamp;
                _dockIconTag = "<script>window.RK_ICONS=" + json + ";</script>";
                return _dockIconTag;
            }
        }

        private static readonly Regex _partialRe = new Regex(@"<!--#include ([a-z][a-z0-9-]{0,40})-->", RegexOptions.Compiled);

        // splice shared dock fragments in: every "<!--#include NAME-->" becomes the contents of obs-custom-dock/_NAME.html. runs before InjectThemeStyle/InjectDockIcons so a partials own data-i / RK_ICONS references still get picked up.
        private static string InjectPartials(string html)
        {
            try
            {
                if (html.IndexOf("<!--#include ", StringComparison.Ordinal) < 0) return html;
                string[] roots = { AppConfig.GetDockDir(), AppConfig.GetDefaultDockDir() };
                return _partialRe.Replace(html, m =>
                {
                    string name = m.Groups[1].Value;
                    foreach (var root in roots)
                    {
                        if (string.IsNullOrEmpty(root)) continue;
                        string p = Path.GetFullPath(Path.Combine(root, "_" + name + ".html"));
                        string rootFull = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                        if (p.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) && File.Exists(p)) return File.ReadAllText(p);
                    }
                    return ""; // unknown partial -> drop the placeholder rather than ship a literal comment
                });
            }
            catch { return html; }
        }

        private static string InjectDockIcons(string html)
        {
            try
            {
                // only pages that actually read the map (clips.html, settings.html) -- skips update_prompt / sign-in-loader
                if (html.IndexOf("RK_ICONS", StringComparison.Ordinal) < 0 && html.IndexOf("data-i=", StringComparison.Ordinal) < 0) return html;
                string tag = DockIconTag();
                if (string.IsNullOrEmpty(tag)) return html;
                int i = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
                if (i < 0) return html;
                return html.Insert(i, tag);
            }
            catch { return html; }
        }

        public static void ServeHtml(Stream stream, string filename, bool sameOriginFrame = false)
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
                        string html = InjectDockIcons(InjectThemeStyle(InjectPartials(File.ReadAllText(f))));
                        if (html.Contains("__RK_CLIP_APP_VERSION__"))
                        {
                            using (var input = File.OpenRead(DockScriptPath()))
                            using (var hash = SHA256.Create())
                                html = html.Replace("__RK_CLIP_APP_VERSION__", BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "").ToLowerInvariant());
                        }
                        byte[] bytes = new UTF8Encoding(false).GetBytes(html);
                        var h = GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = "text/html; charset=utf-8" });
                        // only the pages the obs window frames inside its own shell page get this, and it still refuses every other origin; everything else keeps the DENY
                        if (sameOriginFrame) h["X-Frame-Options"] = "SAMEORIGIN";
                        SendBytes(stream, 200, "OK", h, bytes);
                        return;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                }
            }
            SendText(stream, 404, "Not Found", filename + " not found");
        }

        private static string DockScriptPath()
        {
            foreach (string root in new[] { AppConfig.GetDockDir(), AppConfig.GetDefaultDockDir() })
            {
                if (string.IsNullOrEmpty(root)) continue;
                string path = Path.Combine(root, "clip-app.js");
                if (File.Exists(path)) return path;
            }
            throw new FileNotFoundException("Clip browser script was not found.");
        }

        public static void ServeDockScript(Stream stream)
        {
            try
            {
                SendFile(stream, 200, "OK", GetNoStoreHeaders(new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/javascript; charset=utf-8",
                    ["Cache-Control"] = "private, max-age=31536000, immutable"
                }), DockScriptPath());
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { SendText(stream, 404, "Not Found", "Script not found"); }
        }

        // scrubbing fires a burst of overlapping range requests that each abort the last, so a slot usually frees within milliseconds and a short wait clears almost all of them; the check and the claim happen under one lock so a burst can never pass the cap together
        private static bool TryTakePreviewSlot(out int waitedMs, out int active)
        {
            waitedMs = 0;
            while (true)
            {
                lock (Server.State.PreviewLock)
                {
                    active = Server.State.ActivePreviews;
                    if (active < Constants.MAX_PREVIEW_STREAM)
                    {
                        active = ++Server.State.ActivePreviews;
                        return true;
                    }
                }
                if (waitedMs >= 300) return false;
                Thread.Sleep(15);
                waitedMs += 15;
            }
        }

        // serves one range chunk of a clip and reports whether the connection is worth keeping open -- playback at high speed multipliers needs a new chunk far more often than realtime, and a fresh tcp handshake per chunk (plain connection:close) couldnt keep up and showed up as buffering; returning true lets Connection.HandleConnection reuse the socket for the next range request.
        public static bool ServePreview(Stream stream, HttpRequest req, string rawName)
        {
            // reqId lets start/end/error log lines for the same call be matched up -- this function otherwise writes nothing on a fast/happy path, so a stuck or failed preview would leave no trace at all.
            string reqId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var sw = Stopwatch.StartNew();
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
                    bool parsed;
                    if (lo == "")
                    {
                        parsed = long.TryParse(hi, out long suffix) && suffix > 0;
                        if (parsed) start = Math.Max(fileSize - suffix, 0);
                    }
                    else
                    {
                        parsed = long.TryParse(lo, out start) && (hi == "" || long.TryParse(hi, out end));
                    }
                    if (!parsed) { SendText(stream, 416, "Range Not Satisfiable", ""); return false; }
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

            if (!TryTakePreviewSlot(out int busyWaitedMs, out int active))
            {
                Log.Write("Serve-Preview[" + reqId + "] BUSY name=" + selected.Name + " active=" + active + " cap=" + Constants.MAX_PREVIEW_STREAM + " waitedMs=" + busyWaitedMs);
                SendText(stream, 503, "Service Unavailable", "Preview busy");
                return false;
            }
            Log.Write("Serve-Preview[" + reqId + "] START name=" + selected.Name + " range=" + range + " start=" + start + " end=" + end + " length=" + length + " keepAliveReq=" + !clientWantsClose + " active=" + active);
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

                // explicit FileShare.Delete -- FileStreams defualt share mode (read only) blocks any concurrent delete/rename/replace-on-top of this file for as long as this handle is open, and /delete can land on a different pool thread while this one is mid-stream. Delete share doesnt affect what gets read here: windows keeps this handles view of the data valid even if the file gets unlinked out from under it mid-read.
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

        // one cut out audio track for the editor, with byte ranges so the audio element can seek; the files are small and made by this helper
        public static bool ServeAudioTrack(Stream stream, HttpRequest req, string key, string index) => ServeAudioFile(stream, req, ClipAudioTracks.PathFor(key, index));

        // any audio file the cache made, an imported one included; null is a file that is not there
        public static bool ServeAudioFile(Stream stream, HttpRequest req, string path)
        {
            if (path == null) { SendText(stream, 404, "Not Found", "Track not found"); return false; }
            long size = new FileInfo(path).Length;
            long start = 0;
            long end = size - 1;
            bool partial = false;
            req.Headers.TryGetValue("range", out string range);
            if (!string.IsNullOrEmpty(range))
            {
                var m = Regex.Match(range, @"^bytes=(\d*)-(\d*)$");
                if (m.Success && (m.Groups[1].Value != "" || m.Groups[2].Value != ""))
                {
                    partial = true;
                    bool parsed;
                    if (m.Groups[1].Value == "")
                    {
                        parsed = long.TryParse(m.Groups[2].Value, out long suffix) && suffix > 0;
                        start = Math.Max(size - (parsed ? suffix : 0), 0);
                    }
                    else
                    {
                        parsed = long.TryParse(m.Groups[1].Value, out start);
                        if (parsed && m.Groups[2].Value != "") parsed = long.TryParse(m.Groups[2].Value, out end);
                    }
                    if (!parsed) { SendText(stream, 416, "Range Not Satisfiable", ""); return false; }
                }
            }
            if (start < 0 || start >= size || end < start) { SendText(stream, 416, "Range Not Satisfiable", ""); return false; }
            end = Math.Min(end, size - 1);
            long length = end - start + 1;
            var headers = GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = "audio/mp4", ["Accept-Ranges"] = "bytes", ["Connection"] = "close" });
            if (partial) headers["Content-Range"] = "bytes " + start + "-" + end + "/" + size;
            var head = FormatHttpResponse(partial ? 206 : 200, partial ? "Partial Content" : "OK", headers, length);
            stream.Write(head, 0, head.Length);
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                fs.Seek(start, SeekOrigin.Begin);
                var buf = new byte[65536];
                long remaining = length;
                while (remaining > 0)
                {
                    int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, remaining));
                    if (n <= 0) break;
                    stream.Write(buf, 0, n);
                    remaining -= n;
                }
            }
            stream.Flush();
            return false;
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
                        ["Cache-Control"] = "public, max-age=31536000, immutable",
                        ["Content-Type"] = "image/jpeg",
                        ["Connection"] = "close",
                    };
                    SendFile(stream, 200, "OK", h, thumb);
                    return;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
            // a clip that was written a moment ago may still be being finished by OBS and has no picture yet: the page is told to ask again, and shows the clip as processing, instead of getting the grey tile
            if (DateTime.UtcNow - fi.LastWriteTimeUtc < TimeSpan.FromMinutes(3))
            {
                SendText(stream, 404, "Not Found", "Thumbnail not ready");
                return;
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

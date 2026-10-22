using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    internal sealed class UploadOutcome
    {
        public bool Ok;
        public string Url;
        public string Message;
    }

    // uploads a clip to streamable the way streamable.com does it: get a shortcode and a presigned s3 form (cookie jar), post the file to s3, then trigger the transcode (cookie jar)
    internal static class UploadWorker
    {
        private static string[] TransportArgs() => new[]
        {
            "--http1.1", "--tlsv1.2", "--ssl-revoke-best-effort",
            "--connect-timeout", "15", "--retry", "2", "--retry-delay", "1", "--retry-all-errors",
        };

        // percent is this workers own 0-100 progress, remapped into the sub-range the caller reserved (compress-then-upload hands it 95-99); error forces 0, done forces 100 and everything else clamps to 99 so a rounding blip never reads as done. the s3 byte transfer owns 8..100 and the steps after it hold at the top while the phase tells the page to show finalizing
        private static void WriteStatus(string requestId, int progressBase, int progressSpan, string state, string phase, int percent, string message = "", string url = null)
        {
            int finalPercent;
            if (state == "error") finalPercent = 0;
            else if (state == "done") finalPercent = 100;
            else
            {
                finalPercent = (int)Math.Round(progressBase + (progressSpan * (double)percent) / 100.0);
                finalPercent = Math.Max(0, Math.Min(99, finalPercent));
            }
            UploadState.SetUploadState(requestId: requestId, state: state, phase: phase, percent: finalPercent, message: message, url: url);
        }

        // streamed multipart POST of the clip to the presigned s3 url, counting real bytes-on-the-wire for progress instead of scraping curls ~1hz stderr meter. returns the response body; sets statusCode. throws WebException with no .Response for a pure connection failure (caller retries); returns the body + a 4xx/5xx statusCode for a real s3 rejection (caller turns that into an error). settings mirror the two curl steps: direct connection (no wpad proxy auto-detect), tls 1.2+, no auto-redirect, explicit Content-Length.
        private static string UploadFileToS3WithProgress(string url, JObject fields, string filePath, string requestId,
            int progressBase, int progressSpan, int startPercent, int endPercent,
            CancellationToken cancelToken, out int statusCode)
        {
            statusCode = 0;
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri destination) || destination.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("Upload destination must use HTTPS.");
            string boundary = "----ReplayKit" + Guid.NewGuid().ToString("N");
            string fileName = Path.GetFileName(filePath).Replace("\"", "").Replace("\r", "").Replace("\n", "");
            long fileLen = new FileInfo(filePath).Length;

            var head = new StringBuilder();
            foreach (var prop in fields.Properties())
            {
                head.Append("--").Append(boundary).Append("\r\n");
                head.Append("Content-Disposition: form-data; name=\"").Append(prop.Name).Append("\"\r\n\r\n");
                head.Append(prop.Value.ToString()).Append("\r\n");
            }
            head.Append("--").Append(boundary).Append("\r\n");
            head.Append("Content-Disposition: form-data; name=\"file\"; filename=\"").Append(fileName).Append("\"\r\n");
            head.Append("Content-Type: application/octet-stream\r\n\r\n");
            byte[] headBytes = Encoding.UTF8.GetBytes(head.ToString());
            byte[] tailBytes = Encoding.UTF8.GetBytes("\r\n--" + boundary + "--\r\n");

            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "multipart/form-data; boundary=" + boundary;
            request.ContentLength = headBytes.Length + fileLen + tailBytes.Length;
            request.AllowWriteStreamBuffering = false;
            request.AllowAutoRedirect = false;
            request.SendChunked = false;
            request.KeepAlive = true;
            request.Proxy = null;
            request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
            // AllowWriteStreamBuffering is off, so Timeout bounds the whole request -- the body upload included -- and a flat 120s aborted (RequestCanceled, mid-transfer, no retry) any clip that took longer than that to send. scale it to the file size at a 64 KB/s worst-case floor (min 15 min) so a real upload is never cut short; ReadWriteTimeout below stays the per-write stall guard that actually fails a dead connection.
            request.Timeout = (int)Math.Min(int.MaxValue, Math.Max(15L * 60 * 1000, fileLen / (64 * 1024) * 1000));
            request.ReadWriteTimeout = 120000;
            request.AutomaticDecompression = DecompressionMethods.None;
            // one big streamed POST: skip the expect/100-continue round-trip before the body, and turn nagle off so the last sub-mss buffer isnt held back. scoped to the s3 endpoints ServicePoint, nothing else in the helper hits s3.amazonaws.com.
            request.ServicePoint.Expect100Continue = false;
            request.ServicePoint.UseNagleAlgorithm = false;

            var reg = cancelToken.Register(() => { try { request.Abort(); } catch { } });
            try
            {
                using (var reqStream = request.GetRequestStream())
                {
                    reqStream.Write(headBytes, 0, headBytes.Length);
                    long sent = 0;
                    int lastPct = -1;
                    var lastAt = DateTime.UtcNow;
                    // 1 MiB blocks -- fewer read/write syscalls over a multi-GB body
                    var buf = new byte[1024 * 1024];
                    using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, buf.Length, FileOptions.SequentialScan))
                    {
                        int n;
                        while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                        {
                            if (cancelToken.IsCancellationRequested) { try { request.Abort(); } catch { } throw new OperationCanceledException(); }
                            reqStream.Write(buf, 0, n);
                            sent += n;
                            int mapped = fileLen > 0
                                ? startPercent + (int)((endPercent - startPercent) * (sent / (double)fileLen))
                                : endPercent;
                            var now = DateTime.UtcNow;
                            if (mapped > lastPct && (now - lastAt).TotalMilliseconds >= 40)
                            {
                                lastPct = mapped;
                                lastAt = now;
                                WriteStatus(requestId, progressBase, progressSpan, "uploading", "uploading", mapped);
                            }
                        }
                    }
                    reqStream.Write(tailBytes, 0, tailBytes.Length);
                }

                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    statusCode = (int)response.StatusCode;
                    return ReadResponseBody(response);
                }
            }
            catch (WebException wex)
            {
                if (cancelToken.IsCancellationRequested) throw new OperationCanceledException();
                var er = wex.Response as HttpWebResponse;
                if (er == null) throw;
                statusCode = (int)er.StatusCode;
                string b = ReadResponseBody(er);
                er.Close();
                return b;
            }
            finally
            {
                reg.Dispose();
            }
        }

        private static string ReadResponseBody(HttpWebResponse resp)
        {
            try
            {
                using (var rs = resp.GetResponseStream())
                {
                    if (rs == null) return "";
                    using (var sr = new StreamReader(rs)) return sr.ReadToEnd();
                }
            }
            catch { return ""; }
        }

        private static bool IsTransientWebError(WebException wex)
        {
            switch (wex.Status)
            {
                case WebExceptionStatus.ConnectFailure:
                case WebExceptionStatus.SendFailure:
                case WebExceptionStatus.ReceiveFailure:
                case WebExceptionStatus.Timeout:
                case WebExceptionStatus.KeepAliveFailure:
                case WebExceptionStatus.PipelineFailure:
                case WebExceptionStatus.ConnectionClosed:
                case WebExceptionStatus.NameResolutionFailure:
                    return true;
                default:
                    return false;
            }
        }

        public static UploadOutcome Run(string requestId, string clipPath, string authCookies, bool requireAuth, int progressBase, int progressSpan, bool quiet = false, CancellationToken cancelToken = default(CancellationToken))
        {
            string tempPrefix = Path.Combine(Constants.SCRATCH_DIR, "streamable_" + requestId);
            string jar = tempPrefix + ".cookies.txt";
            string transcodeBodyPath = tempPrefix + ".transcode_body.json";
            var tempFiles = new[] { jar, transcodeBodyPath };
            string mixOnlyDir = null;

            WriteStatus(requestId, progressBase, progressSpan, "uploading", "preparing", 8);
            try
            {
                if (!File.Exists(clipPath)) throw new InvalidOperationException("Clip not found: " + clipPath);
                var clipItem = new FileInfo(clipPath);
                // a clip with separate audio tracks goes up as a copy with the mix only, so the site gets exactly one audio track whatever it does with extra ones; the copy is a stream copy and takes seconds
                if (Mp4Tracks.CountAudio(clipPath) > 1)
                {
                    string ffmpeg = Compression.GetToolPaths()["ffmpeg"]?.Value<string>();
                    if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg)) throw new InvalidOperationException("ffmpeg.exe was not found, so the clip could not be prepared for upload.");
                    // the copy keeps the name of the clip, in a folder of its own, since the name of the file is what the site receives
                    mixOnlyDir = tempPrefix + ".mix";
                    Directory.CreateDirectory(mixOnlyDir);
                    string mixOnlyCopy = Path.Combine(mixOnlyDir, Path.GetFileName(clipPath));
                    WriteStatus(requestId, progressBase, progressSpan, "uploading", "preparing", 8);
                    var copy = Compression.InvokeNativeCapture(ffmpeg, new[] { "-y", "-hide_banner", "-loglevel", "error", "-i", clipPath, "-map", "0:v:0", "-map", "0:a:0", "-c", "copy", "-movflags", "+faststart", mixOnlyCopy }, cancelToken, 30 * 60 * 1000);
                    if (copy.ExitCode != 0 || !File.Exists(mixOnlyCopy)) throw new InvalidOperationException("The clip could not be prepared for upload (ffmpeg exit " + copy.ExitCode + ").");
                    clipItem = new FileInfo(mixOnlyCopy);
                }
                long size = clipItem.Length;
                Log.Write("Uploading " + clipItem.Name + " (" + Math.Round(size / 1024.0 / 1024.0, 2) + " MB)", "upload", requestId);
                WriteStatus(requestId, progressBase, progressSpan, "uploading", "preparing", 8);

                // cookie jar shared between step 1 (which sets the session cookie) and step 3 (which needs it -- transcode returns 403 without it).
                try { if (File.Exists(jar)) File.Delete(jar); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }

                // The job owns the starting session, even if the account changes during compression.
                bool signedIn = false;
                if (requireAuth && string.IsNullOrWhiteSpace(authCookies))
                    throw new InvalidOperationException("Signed-in Streamable session is unavailable. Sign out and sign in again.");
                if (!string.IsNullOrWhiteSpace(authCookies))
                {
                    try
                    {
                        File.WriteAllText(jar, authCookies, Encoding.ASCII);
                        signedIn = true;
                        Log.Write("Auth: using the upload's starting session", "upload", requestId);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        throw new InvalidOperationException("Could not prepare signed-in Streamable session: " + ex.Message);
                    }
                }

                // step 1: shortcode + presigned s3 form.
                Log.Write("Step 1: requesting upload shortcode...", "upload", requestId);
                WriteStatus(requestId, progressBase, progressSpan, "uploading", "requesting upload", 8);
                string url1 = "https://api-f.streamable.com/api/v1/uploads/shortcode?size=" + size + "&version=unknown";
                var step1Args = new List<string>(TransportArgs())
                {
                    "-s", "-S", "-m", "30", "-c", jar,
                    "-H", "Origin: https://streamable.com",
                    "-H", "Referer: https://streamable.com/",
                    "-H", "Pragma: no-cache",
                    "-H", "Cache-Control: no-cache",
                    "-A", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)",
                };
                if (signedIn) { step1Args.Add("-b"); step1Args.Add(jar); }
                step1Args.Add(url1);
                var r1 = Curl.Run(step1Args.ToArray(), cancelToken);
                if (r1.ExitCode != 0 || string.IsNullOrWhiteSpace(r1.Stdout))
                {
                    throw new InvalidOperationException("Step 1 curl failed (exit=" + r1.ExitCode + ").");
                }
                var data = JObject.Parse(r1.Stdout);
                string shortcode = data["shortcode"]?.Value<string>();
                if (string.IsNullOrEmpty(shortcode))
                    throw new InvalidOperationException("Step 1 response missing shortcode.");
                Log.Write("shortcode: " + shortcode, "upload", requestId);

                if (cancelToken.IsCancellationRequested) return new UploadOutcome { Ok = false, Message = "Cancelled" };

                // step 2: streamed multipart POST of the clip to the presigned s3 url. field order matters -- every policy field first, "file" last -- and the json property order already matches what streamable.com sends. HttpWebRequest, not curl, so progress is real bytes-on-the-wire (see UploadFileToS3WithProgress).
                Log.Write("Step 2: uploading to S3...", "upload", requestId);
                WriteStatus(requestId, progressBase, progressSpan, "uploading", "uploading", 8);
                string s3Url = data["url"]?.Value<string>();
                if (string.IsNullOrWhiteSpace(s3Url)) throw new InvalidOperationException("Step 1 response missing the S3 upload url.");
                var s3Fields = data["fields"] as JObject ?? new JObject();

                int s3Status = 0;
                string s3Body = "";
                for (int attempt = 1; ; attempt++)
                {
                    if (cancelToken.IsCancellationRequested) return new UploadOutcome { Ok = false, Message = "Cancelled" };
                    try
                    {
                        s3Body = UploadFileToS3WithProgress(s3Url, s3Fields, clipItem.FullName, requestId, progressBase, progressSpan, 8, 100, cancelToken, out s3Status);
                        break;
                    }
                    catch (OperationCanceledException) { return new UploadOutcome { Ok = false, Message = "Cancelled" }; }
                    catch (WebException wex) when (attempt < 3 && IsTransientWebError(wex) && !cancelToken.IsCancellationRequested)
                    {
                        Log.Write("Step 2: attempt " + attempt + " failed (" + wex.Status + "), retrying in 3s...", "upload", requestId);
                        Thread.Sleep(3000);
                    }
                    // a long upload spends minutes on the wire, so a mid-transfer socket drop (or ReadWriteTimeout surfacing as IOException rather than WebException) is a normal transient -- retry the same as a WebException instead of hard-failing.
                    catch (IOException ioex) when (attempt < 3 && !cancelToken.IsCancellationRequested)
                    {
                        Log.Write("Step 2: attempt " + attempt + " failed (io: " + ioex.Message + "), retrying in 3s...", "upload", requestId);
                        Thread.Sleep(3000);
                    }
                }
                if (s3Status < 200 || s3Status >= 300)
                    throw new InvalidOperationException("S3 upload failed: HTTP " + s3Status + ".");
                Log.Write("S3: HTTP " + s3Status, "upload", requestId);
                WriteStatus(requestId, progressBase, progressSpan, "uploading", "finalizing", 100);

                if (cancelToken.IsCancellationRequested) return new UploadOutcome { Ok = false, Message = "Cancelled" };

                // step 3: trigger transcoding.
                Log.Write("Step 3: triggering transcode...", "upload", requestId);
                WriteStatus(requestId, progressBase, progressSpan, "uploading", "finalizing", 100);
                string transcoderOptionsJson = (data["transcoder_options"] ?? new JObject()).ToString(Formatting.None);
                File.WriteAllText(transcodeBodyPath, transcoderOptionsJson, Encoding.ASCII);

                var step3Args = new List<string>(TransportArgs())
                {
                    "-s", "-S", "-m", "30",
                    "-b", jar,
                    "-w", "\n%{http_code}",
                    "-X", "POST",
                    "-H", "Origin: https://streamable.com",
                    "-H", "Referer: https://streamable.com/",
                    "-H", "Content-Type: application/json",
                    "-A", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)",
                    "--data", "@" + transcodeBodyPath,
                    "https://api-f.streamable.com/api/v1/transcode/" + shortcode,
                };
                var r3 = Curl.Run(step3Args.ToArray(), cancelToken);
                if (cancelToken.IsCancellationRequested) return new UploadOutcome { Ok = false, Message = "Cancelled" };
                if (r3.ExitCode != 0 || string.IsNullOrWhiteSpace(r3.Stdout))
                {
                    throw new InvalidOperationException("Transcode trigger curl failed (exit=" + r3.ExitCode + ").");
                }
                var tcLines = r3.Stdout.Split('\n');
                if (!int.TryParse(tcLines[tcLines.Length - 1].Trim(), out int tcStatus))
                    throw new InvalidOperationException("Transcode trigger returned an unreadable status.");
                if (tcStatus < 200 || tcStatus >= 300)
                {
                    throw new InvalidOperationException("Transcode trigger failed: HTTP " + tcStatus + ".");
                }
                Log.Write("transcode: HTTP " + tcStatus, "upload", requestId);
                WriteStatus(requestId, progressBase, progressSpan, "uploading", "copying link", 100);

                if (cancelToken.IsCancellationRequested) return new UploadOutcome { Ok = false, Message = "Cancelled" };

                string finalUrl = "https://streamable.com/" + shortcode;
                Log.Write("DONE: " + finalUrl, "upload", requestId);
                WriteStatus(requestId, progressBase, progressSpan, "done", "done", 100, url: finalUrl);

                // clipboard copy + the "link copied" toast now wait for streamables transcode to actually finish -- the transcode poll worker (TranscodePollWorker) does both when status reaches 2, so a link that isnt watchable yet never lands on the clipboard.
                return new UploadOutcome { Ok = true, Url = finalUrl };
            }
            catch (Exception ex)
            {
                // a cancel mid-curl surfaces here as a curl-failed exception -- report it as a clean cancellation instead of an error toast, and leave the "cancelled" state /cancel-upload already wrote.
                if (cancelToken.IsCancellationRequested)
                {
                    Log.Write("upload cancelled during step: " + ex.Message, "upload", requestId);
                    return new UploadOutcome { Ok = false, Message = "Cancelled" };
                }
                Log.Write("ERROR: " + ex.Message, "upload", requestId);
                WriteStatus(requestId, progressBase, progressSpan, "error", "error", 0, ex.Message);
                return new UploadOutcome { Ok = false, Message = ex.Message };
            }
            finally
            {
                foreach (var f in tempFiles)
                {
                    try { if (File.Exists(f)) File.Delete(f); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                }
                try { if (mixOnlyDir != null && Directory.Exists(mixOnlyDir)) Directory.Delete(mixOnlyDir, true); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }
    }
}

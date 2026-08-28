using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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

    // uploads a clip to streamable using the same anonymous flow streamable.com itself uses (no account needed): curl get /api/v1/uploads/shortcode?size=n (cookie jar) -> curl post <s3 url> multipart-form with the aws fields + file -> curl post /api/v1/transcode/<shortcode> (cookie jar). runs as an in-process Task from Upload.StartStreamableUpload instead of a spawned "powershell.exe -File upload_worker.ps1" child -- progress/status that worker wrote to a json file for the main helper to poll now go straight into UploadState.SetUploadState, since theres no process boundary left to cross. ported from obs_replaykit helper upload_worker.ps1.
    internal static class UploadWorker
    {
        private static string[] TransportArgs() => new[]
        {
            "--http1.1", "--tlsv1.2", "--ssl-revoke-best-effort",
            "--connect-timeout", "15", "--retry", "2", "--retry-delay", "1", "--retry-all-errors",
        };

        // mirrors upload_worker.ps1s Write-Status: percent is this workers own 0-100 local progress; progressBase/progressSpan remap that into whatever sub-range the caller reserved (compress-then-upload hands this the tail of its own job, e.g. 95-99). state=error forces 0, state=done forces 100 -- everything else clamps to [0,99] so a rounding blip can never read as "done" early. the local scale is deliberately transfer-dominated: prep/step1 pin at 8, the s3 byte transfer owns 8..100, and the post-transfer steps (phase "finalizing"/"copying link") hold at the top -- so the bar tracks bytes-on-the-wire and the dock swaps to a "Finalizing..." text state once phase leaves "uploading".
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

        // streamed multipart POST of the clip to the presigned s3 url, counting real bytes-on-the-wire for progress
        // instead of scraping curl's ~1hz stderr meter. returns the response body; sets statusCode. throws
        // WebException with no .Response for a pure connection failure (caller retries); returns the body + a 4xx/5xx
        // statusCode for a real s3 rejection (caller turns that into an error). settings mirror the two curl steps:
        // direct connection (no wpad proxy auto-detect), tls 1.2+, no auto-redirect, explicit Content-Length.
        private static string UploadFileToS3WithProgress(string url, JObject fields, string filePath, string requestId,
            int progressBase, int progressSpan, int startPercent, int endPercent,
            CancellationToken cancelToken, out int statusCode)
        {
            statusCode = 0;
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

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
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
            request.Timeout = 120000;
            request.ReadWriteTimeout = 120000;
            request.AutomaticDecompression = DecompressionMethods.None;

            var reg = cancelToken.Register(() => { try { request.Abort(); } catch { } });
            try
            {
                using (var reqStream = request.GetRequestStream())
                {
                    reqStream.Write(headBytes, 0, headBytes.Length);
                    long sent = 0;
                    int lastPct = -1;
                    var lastAt = DateTime.UtcNow;
                    var buf = new byte[128 * 1024];
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

        public static UploadOutcome Run(string requestId, string clipPath, string authJar, bool requireAuth, int progressBase, int progressSpan, bool quiet = false, CancellationToken cancelToken = default(CancellationToken))
        {
            string tempPrefix = Path.Combine(Constants.SCRATCH_DIR, "streamable_" + requestId);
            string jar = tempPrefix + ".cookies.txt";
            string transcodeBodyPath = tempPrefix + ".transcode_body.json";
            var tempFiles = new[] { jar, transcodeBodyPath };

            WriteStatus(requestId, progressBase, progressSpan, "uploading", "preparing", 8);
            try
            {
                if (!File.Exists(clipPath)) throw new InvalidOperationException("Clip not found: " + clipPath);
                var clipItem = new FileInfo(clipPath);
                long size = clipItem.Length;
                Log.Write("Uploading " + clipItem.Name + " (" + Math.Round(size / 1024.0 / 1024.0, 2) + " MB)", "upload", requestId);
                WriteStatus(requestId, progressBase, progressSpan, "uploading", "preparing", 8);

                // cookie jar shared between step 1 (which sets the session cookie) and step 3 (which needs it -- transcode returns 403 without it).
                try { if (File.Exists(jar)) File.Delete(jar); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }

                // if given a signed-in cookie jar, seed the session jar with its contents so step 1 goes out authenticated. its copied rather than just -b'd so curl can still -c into it (streamable issues additional session cookies during upload).
                bool signedIn = false;
                if (requireAuth && (string.IsNullOrWhiteSpace(authJar) || !File.Exists(authJar)))
                    throw new InvalidOperationException("Signed-in Streamable session is unavailable. Sign out and sign in again.");
                if (!string.IsNullOrWhiteSpace(authJar))
                {
                    if (!File.Exists(authJar)) throw new InvalidOperationException("Signed-in Streamable session file is missing. Sign out and sign in again.");
                    try
                    {
                        File.Copy(authJar, jar, true);
                        signedIn = true;
                        Log.Write("Auth: using signed-in session cookies from " + authJar, "upload", requestId);
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
                var r1 = Curl.Run(step1Args.ToArray(), proc => UploadState.SetUploadState(requestId: requestId, encoderProcess: proc));
                if (r1.ExitCode != 0 || string.IsNullOrWhiteSpace(r1.Stdout))
                {
                    string detail = string.Join(" ", new[] { r1.Stderr, r1.Stdout }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
                    if (string.IsNullOrWhiteSpace(detail)) detail = "no curl output";
                    throw new InvalidOperationException("Step 1 curl failed (exit=" + r1.ExitCode + "). Output: " + detail);
                }
                var data = JObject.Parse(r1.Stdout);
                string shortcode = data["shortcode"]?.Value<string>();
                if (string.IsNullOrEmpty(shortcode))
                    throw new InvalidOperationException("Step 1 response missing shortcode. Body: " + r1.Stdout.Substring(0, Math.Min(400, r1.Stdout.Length)));
                Log.Write("shortcode: " + shortcode, "upload", requestId);

                if (cancelToken.IsCancellationRequested) return new UploadOutcome { Ok = false, Message = "Cancelled" };

                // step 2: streamed multipart POST of the clip to the presigned s3 url. field order matters -- every
                // policy field first, "file" last -- and the json property order already matches what streamable.com
                // sends. HttpWebRequest, not curl, so progress is real bytes-on-the-wire (see UploadFileToS3WithProgress).
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
                        Log.Write("Step 2: attempt " + attempt + " failed (" + wex.Status + "), retrying in 1s...", "upload", requestId);
                        Thread.Sleep(1000);
                    }
                }
                if (s3Status < 200 || s3Status >= 300)
                    throw new InvalidOperationException("S3 upload failed: HTTP " + s3Status + ". Body: " + (s3Body ?? "").Trim());
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
                var r3 = Curl.Run(step3Args.ToArray(), proc =>
                {
                    UploadState.SetUploadState(requestId: requestId, encoderProcess: proc);
                    if (cancelToken.IsCancellationRequested) { try { proc.Kill(); } catch { } }
                });
                if (cancelToken.IsCancellationRequested) return new UploadOutcome { Ok = false, Message = "Cancelled" };
                if (r3.ExitCode != 0 || string.IsNullOrWhiteSpace(r3.Stdout))
                {
                    string detail = string.Join(" ", new[] { r3.Stderr, r3.Stdout }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
                    if (string.IsNullOrWhiteSpace(detail)) detail = "no curl output";
                    throw new InvalidOperationException("Transcode trigger curl failed (exit=" + r3.ExitCode + "). Output: " + detail);
                }
                var tcLines = r3.Stdout.Split('\n');
                if (!int.TryParse(tcLines[tcLines.Length - 1].Trim(), out int tcStatus))
                    throw new InvalidOperationException("Transcode trigger returned an unreadable status. Body: " + r3.Stdout.Substring(0, Math.Min(400, r3.Stdout.Length)));
                if (tcStatus < 200 || tcStatus >= 300)
                {
                    string tcBody = string.Join("\n", tcLines, 0, tcLines.Length - 1);
                    throw new InvalidOperationException("Transcode trigger failed: HTTP " + tcStatus + ". Body: " + tcBody);
                }
                Log.Write("transcode: HTTP " + tcStatus, "upload", requestId);
                WriteStatus(requestId, progressBase, progressSpan, "uploading", "copying link", 100);

                if (cancelToken.IsCancellationRequested) return new UploadOutcome { Ok = false, Message = "Cancelled" };

                string finalUrl = "https://streamable.com/" + shortcode;
                Log.Write("DONE: " + finalUrl, "upload", requestId);
                WriteStatus(requestId, progressBase, progressSpan, "done", "done", 100, url: finalUrl);

                // push to clipboard so the link can be pasted anywhere immediately without looking at the dock. runs on a one-shot STA thread (via StaRunner) since this worker is an MTA thread-pool task and Clipboard is an OLE call. wrapped seperately (unlike the ps original) so a clipboard hiccup -- another app briefly holding an exclusive lock, a real and observed windows quirk -- cant flip an already-successful upload into a reported failure. skipped for quiet (bulk) uploads: N copies in a row just leave the last one, useless.
                if (!quiet)
                {
                    try { StaRunner.Run(() => System.Windows.Forms.Clipboard.SetText(finalUrl)); }
                    catch (Exception ex) { Log.Write("clipboard copy failed (non-fatal): " + ex.Message, "upload", requestId); }
                }

                return new UploadOutcome { Ok = true, Url = finalUrl };
            }
            catch (Exception ex)
            {
                // a cancel mid-curl surfaces here as a curl-failed exception -- report it as a clean cancellation
                // instead of an error toast, and leave the "cancelled" state /cancel-upload already wrote.
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
            }
        }
    }
}

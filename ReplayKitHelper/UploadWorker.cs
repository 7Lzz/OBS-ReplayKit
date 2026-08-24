using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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

    // uploads a clip to streamable using the same anonymous flow streamable.com itself uses (no account needed): curl get /api/v1/uploads/shortcode?size=n (cookie jar) -> curl post <s3 url> multipart-form with the aws fields + file -> curl post /api/v1/transcode/<shortcode> (cookie jar). runs as an in-process Task from Upload.StartStreamableUpload instead of a spawned "powershell.exe -File upload_worker.ps1" child -- progress/status that worker wrote to a json file for the main helper to poll now go straight into UploadState.SetUploadState, since theres no process boundary left to cross. ported from obs_replaykit helper upload_worker.ps1.
    internal static class UploadWorker
    {
        private static string[] TransportArgs() => new[]
        {
            "--http1.1", "--tlsv1.2", "--ssl-revoke-best-effort",
            "--connect-timeout", "15", "--retry", "2", "--retry-delay", "1", "--retry-all-errors",
        };

        // mirrors upload_worker.ps1s Write-Status: percent is this workers own 0-100 local progress; progressBase/progressSpan remap that into whatever sub-range the caller reserved (compress-then-upload hands this the tail of its own job, e.g. 95-99). state=error forces 0, state=done forces 100 -- everything else clamps to [0,99] so a rounding blip can never read as "done" early.
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

        private static Curl.Result RunCurlUploadWithProgress(string requestId, int progressBase, int progressSpan, string[] args, int startPercent, int endPercent, string progressFile)
        {
            try { if (File.Exists(progressFile)) File.Delete(progressFile); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            var psi = new ProcessStartInfo
            {
                FileName = "curl.exe",
                Arguments = ProcessArgs.Join(args),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var proc = Process.Start(psi))
            {
                UploadState.SetUploadState(requestId: requestId, encoderProcess: proc);
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();
                int lastPct = -1;
                while (!proc.HasExited)
                {
                    try
                    {
                        if (File.Exists(progressFile))
                        {
                            string raw = File.ReadAllText(progressFile);
                            var matches = Regex.Matches(raw, @"(\d{1,3}(?:\.\d+)?)%");
                            if (matches.Count > 0)
                            {
                                double curlPct = double.Parse(matches[matches.Count - 1].Groups[1].Value, CultureInfo.InvariantCulture);
                                curlPct = Math.Max(0.0, Math.Min(100.0, curlPct));
                                int mapped = (int)Math.Floor(startPercent + (endPercent - startPercent) * curlPct / 100.0);
                                if (mapped > lastPct)
                                {
                                    lastPct = mapped;
                                    WriteStatus(requestId, progressBase, progressSpan, "uploading", "uploading", mapped);
                                }
                            }
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                    Thread.Sleep(500);
                }
                proc.WaitForExit();
                string stdout = stdoutTask.Result;
                string stderr = stderrTask.Result;
                string progressText = "";
                try { if (File.Exists(progressFile)) progressText = File.ReadAllText(progressFile); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                var errParts = new[] { stderr, progressText }.Where(s => !string.IsNullOrEmpty(s));
                return new Curl.Result { ExitCode = proc.ExitCode, Stdout = stdout, Stderr = string.Join("\n", errParts) };
            }
        }

        public static UploadOutcome Run(string requestId, string clipPath, string authJar, bool requireAuth, int progressBase, int progressSpan)
        {
            string tempPrefix = Path.Combine(Constants.SCRATCH_DIR, "streamable_" + requestId);
            string jar = tempPrefix + ".cookies.txt";
            string s3RespPath = tempPrefix + ".s3_resp.txt";
            string transcodeBodyPath = tempPrefix + ".transcode_body.json";
            string curlProgressPath = tempPrefix + ".curl_progress.txt";
            var tempFiles = new[] { jar, s3RespPath, transcodeBodyPath, curlProgressPath };

            WriteStatus(requestId, progressBase, progressSpan, "uploading", "preparing", 1);
            try
            {
                if (!File.Exists(clipPath)) throw new InvalidOperationException("Clip not found: " + clipPath);
                var clipItem = new FileInfo(clipPath);
                long size = clipItem.Length;
                Log.Write("Uploading " + clipItem.Name + " (" + Math.Round(size / 1024.0 / 1024.0, 2) + " MB)", "upload", requestId);
                WriteStatus(requestId, progressBase, progressSpan, "uploading", "preparing", 5);

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
                WriteStatus(requestId, progressBase, progressSpan, "uploading", "requesting upload", 10);
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

                // step 2: multipart post to s3. field order matters: every aws form field first, then "file" last -- json object-property enumeration order already matches what streamable.com itself sends.
                Log.Write("Step 2: uploading to S3...", "upload", requestId);
                WriteStatus(requestId, progressBase, progressSpan, "uploading", "uploading", 35);
                var forms = new List<string>();
                if (data["fields"] is JObject fields)
                {
                    foreach (var prop in fields.Properties())
                    {
                        forms.Add("-F");
                        forms.Add(prop.Name + "=" + prop.Value);
                    }
                }
                forms.Add("-F");
                forms.Add("file=@" + clipItem.FullName);

                var step2Args = new List<string>(TransportArgs())
                {
                    "--progress-bar", "--stderr", curlProgressPath,
                    "-m", "900",
                    "-o", s3RespPath,
                    "-w", "%{http_code}",
                    "-X", "POST",
                    data["url"]?.Value<string>(),
                };
                step2Args.AddRange(forms);
                var r2 = RunCurlUploadWithProgress(requestId, progressBase, progressSpan, step2Args.ToArray(), 35, 84, curlProgressPath);
                if (r2.ExitCode != 0) throw new InvalidOperationException("S3 upload curl failed (exit=" + r2.ExitCode + "). Output: " + r2.Stderr);
                if (!int.TryParse(r2.Stdout.Trim(), out int s3Status))
                    throw new InvalidOperationException("S3 upload returned an unreadable status. Output: " + r2.Stdout);
                if (s3Status < 200 || s3Status >= 300)
                {
                    string errBody = "";
                    try { if (File.Exists(s3RespPath)) errBody = File.ReadAllText(s3RespPath); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                    if (string.IsNullOrWhiteSpace(errBody) && !string.IsNullOrEmpty(r2.Stderr)) errBody = r2.Stderr;
                    throw new InvalidOperationException("S3 upload failed: HTTP " + s3Status + ". Body: " + errBody);
                }
                Log.Write("S3: HTTP " + s3Status, "upload", requestId);
                WriteStatus(requestId, progressBase, progressSpan, "uploading", "finalizing", 85);

                // step 3: trigger transcoding.
                Log.Write("Step 3: triggering transcode...", "upload", requestId);
                WriteStatus(requestId, progressBase, progressSpan, "uploading", "finalizing", 90);
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
                var r3 = Curl.Run(step3Args.ToArray(), proc => UploadState.SetUploadState(requestId: requestId, encoderProcess: proc));
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
                WriteStatus(requestId, progressBase, progressSpan, "uploading", "copying link", 98);

                string finalUrl = "https://streamable.com/" + shortcode;
                Log.Write("DONE: " + finalUrl, "upload", requestId);
                WriteStatus(requestId, progressBase, progressSpan, "done", "done", 100, url: finalUrl);

                // push to clipboard so the link can be pasted anywhere immediately without looking at the dock. wrapped seperately (unlike the ps original) so a clipboard hiccup -- another app briefly holding an exclusive lock, a real and observed windows quirk -- cant flip an already-successful upload into a reported failure.
                try { System.Windows.Forms.Clipboard.SetText(finalUrl); }
                catch (Exception ex) { Log.Write("clipboard copy failed (non-fatal): " + ex.Message, "upload", requestId); }

                return new UploadOutcome { Ok = true, Url = finalUrl };
            }
            catch (Exception ex)
            {
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // polls streamables /api/v1/videos/<shortcode> until status reaches 2 (ready) or 30 minutes pass, writing transcode_status/transcode_percent/ready into clips_db.json as they change. spawned DETACHED (Native.SpawnDetached, CREATE_BREAKAWAY_FROM_JOB) from Upload.cs specifically so this survives an obs/helper restart mid-poll -- a normal child process dies with the helper the instant obs closes, which is what used to leave clips permanently stuck showing "processing" even after streamable had long since finished, since nothing ever resumed a killed poll. runs as a hidden CLI mode of this same exe (Program.cs --transcode-poll) rather than a Task, since outliving the helper process itself is the entire point -- unlike every other worker in this port, this one genuinely cannot run in-process. self-contained: never touches Server.State, since a truly detached process cant share it. ported from obs_replaykit helper transcode_poll_worker.ps1.
    internal static class TranscodePollWorker
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public static int Run(string shortcode, string clipName, string dbPath, string api, string logPath, string cookieJar, bool quiet = false)
        {
            void L(string m)
            {
                if (string.IsNullOrEmpty(logPath)) return;
                try
                {
                    string line = string.Format("[{0}] area=transcode shortcode={1} {2}", DateTime.Now.ToString("o"), shortcode, m);
                    string dir = Path.GetDirectoryName(logPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.AppendAllText(logPath, line + Environment.NewLine);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }

            L("start clip='" + clipName + "' quiet=" + quiet);

            bool readyNotified = false;
            // fires once when streamable reports the video watchable (status 2): copies the link + shows the "clip ready" balloon. this is where the copy/toast live now -- the upload flow no longer does it, so a not-yet-playable link never lands on the clipboard. skipped for quiet (bulk / resumed-after-restart) polls.
            void NotifyReady()
            {
                if (quiet || readyNotified) return;
                readyNotified = true;
                string url = "https://streamable.com/" + shortcode;
                try { StaRunner.Run(() => System.Windows.Forms.Clipboard.SetText(url)); }
                catch (Exception ex) { L("clipboard copy failed: " + ex.Message); }
                // hand the toast to the running helper -- it has the settings loaded so the replaykit / custom icon resolves, and a real message pump. only fall back to a self-hosted balloon if the helper is gone (obs closed mid-transcode).
                bool handed = false;
                try
                {
                    var r = Curl.Run("-s", "-S", "--max-time", "5", "-X", "POST",
                        "http://127.0.0.1:8767/internal/clip-ready?url=" + Uri.EscapeDataString(url) + "&name=" + Uri.EscapeDataString(clipName ?? ""));
                    handed = r.ExitCode == 0 && (r.Stdout + r.Stderr).IndexOf("\"ok\":true", StringComparison.Ordinal) >= 0;
                }
                catch (Exception ex) { L("clip-ready POST failed: " + ex.Message); }
                if (!handed)
                {
                    try { Upload.ShowUploadToast(url, clipName); }
                    catch (Exception ex) { L("fallback toast failed: " + ex.Message); }
                }
                L("ready -> link copied, toast handed=" + handed);
            }

            DateTime deadline = DateTime.Now.AddMinutes(30);
            string scratchDir = Path.Combine(Path.GetTempPath(), "ReplayKit", "scratch");
            try { Directory.CreateDirectory(scratchDir); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }

            string lastWritten = "";
            while (DateTime.Now < deadline)
            {
                Thread.Sleep(5000);
                string respPath = Path.Combine(scratchDir, "strmbl_st_" + Guid.NewGuid().ToString("N") + ".txt");
                int? status = null;
                int percent = 0;
                try
                {
                    string url = api + "/api/v1/videos/" + shortcode;
                    var cargs = new List<string>
                    {
                        "-s", "-S", "--max-time", "8",
                        "-H", "Origin: https://streamable.com",
                        "-H", "Referer: https://streamable.com/",
                        "-H", "Accept: application/json",
                        "-A", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)",
                        "-o", respPath, "-w", "%{http_code}",
                    };
                    if (!string.IsNullOrEmpty(cookieJar) && File.Exists(cookieJar)) { cargs.Add("-b"); cargs.Add(cookieJar); }
                    cargs.Add(url);

                    var r = Curl.Run(cargs.ToArray());
                    int.TryParse((r.Stdout + r.Stderr).Trim(), out int code);
                    if (code >= 200 && code < 300 && File.Exists(respPath))
                    {
                        string body = File.ReadAllText(respPath);
                        try
                        {
                            var obj = JObject.Parse(body);
                            if (obj["status"] != null) status = obj["status"].Value<int>();
                            // streamables field is "percent" (0-100) -- the old "percentage_complete" never existed on this response, so the dock only ever saw 0 and showed "Processing..." with no number.
                            if (obj["percent"] != null) percent = obj["percent"].Value<int>();
                        }
                        catch (JsonException) { }
                    }
                    else
                    {
                        L("HTTP " + code);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception)
                {
                    L("exception: " + ex.Message);
                }
                finally
                {
                    try { if (File.Exists(respPath)) File.Delete(respPath); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                }

                if (!status.HasValue) continue;
                // skip the db read/write when nothing observable changed.
                string stateKey = status.Value + "|" + percent;
                if (stateKey == lastWritten)
                {
                    if (status.Value >= 2) { if (status.Value == 2) NotifyReady(); break; }
                    continue;
                }

                try
                {
                    if (!ClipStore.SetTranscode(dbPath, clipName, shortcode, status.Value, percent))
                    {
                        L("upload entry removed or replaced");
                        return 0;
                    }
                    lastWritten = stateKey;
                    L("wrote status=" + status.Value + " percent=" + percent + " ready=" + (status.Value == 2));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
                {
                    L("db update failed: " + ex.Message);
                    Program.WriteCrashReport("transcode_persistence", ex, false);
                    return 1;
                }

                if (status.Value >= 2) { if (status.Value == 2) NotifyReady(); break; }
            }

            if (DateTime.Now >= deadline)
            {
                try
                {
                    ClipStore.SetTranscode(dbPath, clipName, shortcode, 4, 0);
                    L("marked status check timed out");
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
                {
                    L("timeout update failed: " + ex.Message);
                    Program.WriteCrashReport("transcode_persistence", ex, false);
                    return 1;
                }
            }
            L("exit");
            return 0;
        }
    }
}

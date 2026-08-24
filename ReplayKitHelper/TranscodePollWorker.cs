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

        public static int Run(string shortcode, string clipName, string dbPath, string api, string logPath, string cookieJar)
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

            L("start clip='" + clipName + "'");

            DateTime deadline = DateTime.Now.AddMinutes(30);
            string scratchDir = Path.Combine(Path.GetTempPath(), "ReplayKit", "scratch");
            try { Directory.CreateDirectory(scratchDir); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }

            // tmp lands in scratchDir, not next to dbPath, so a crash between the write and the move never leaves a stray .tmp sitting beside clips_db.json. this poller is a genuinely seperate detached process and cant take the helpers in-process ClipsMetaLock -- atomic replace at least keeps a concurrent reader from ever seeing a torn file, even without full mutual exclusion against the helpers other clips_db.json writers.
            void SaveClipsDbAtomic(JObject db)
            {
                string tmp = Path.Combine(scratchDir, Path.GetFileName(dbPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(tmp, db.ToString(Formatting.Indented), Utf8NoBom);
                Native.MoveFileReplace(tmp, dbPath);
            }

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
                            if (obj["percentage_complete"] != null) percent = obj["percentage_complete"].Value<int>();
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
                    if (status.Value >= 2) break;
                    continue;
                }
                lastWritten = stateKey;

                try
                {
                    if (!File.Exists(dbPath)) continue;
                    var db = JObject.Parse(File.ReadAllText(dbPath));
                    // user may have deleted the clip while polling -- dont silently resurrect its entry.
                    if (!(db[clipName] is JObject entry)) { L("entry gone"); break; }
                    entry["transcode_status"] = status.Value;
                    entry["transcode_percent"] = percent;
                    // status: 0/1 = queued/processing, 2 = ready, 3 = failed.
                    entry["ready"] = status.Value == 2;
                    entry["failed"] = status.Value == 3;
                    db[clipName] = entry;
                    SaveClipsDbAtomic(db);
                    L("wrote status=" + status.Value + " percent=" + percent + " ready=" + (status.Value == 2));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
                {
                    L("db update failed: " + ex.Message);
                }

                if (status.Value >= 2) break;
            }

            if (DateTime.Now >= deadline)
            {
                try
                {
                    if (File.Exists(dbPath))
                    {
                        var db = JObject.Parse(File.ReadAllText(dbPath));
                        if (db[clipName] is JObject entry)
                        {
                            int curStatus = entry["transcode_status"]?.Value<int>() ?? 0;
                            if (curStatus < 2)
                            {
                                entry["transcode_status"] = 4;
                                entry["ready"] = false;
                                entry["failed"] = false;
                                entry["transcode_error"] = "Streamable status check timed out after 30 minutes.";
                                db[clipName] = entry;
                                SaveClipsDbAtomic(db);
                                L("marked status check timed out");
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
                {
                    L("timeout update failed: " + ex.Message);
                }
            }
            L("exit");
            return 0;
        }
    }
}

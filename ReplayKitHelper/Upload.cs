using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // dispatches a plain streamable upload (UploadWorker) and reacts once it finishes: writes the clips_db entry, shows a toast, and kicks off the background transcode poller. the "watcher" from the ps original is now a plain Task continuation instead of a second runspace blocked on the spawned processs exit + a status-file read. ported from obs_replaykit helper modules/51_upload.ps1 (Start-StreamableUpload / Resolve-UploadAuthJar) and the plain-upload half of modules/50_upload_state.ps1s Start-UploadResultWatcher.
    internal static class Upload
    {
        public static JObject ResolveUploadAuthJar()
        {
            if (!Server.State.Auth.SignedIn) return new JObject { ["ok"] = true, ["required"] = false, ["path"] = "" };
            string authJar = AuthCore.GetAuthCookieJarPath();
            if (!File.Exists(authJar))
                return new JObject { ["ok"] = false, ["required"] = true, ["path"] = "", ["message"] = "Signed-in Streamable session is unavailable. Sign out and sign in again." };
            return new JObject { ["ok"] = true, ["required"] = true, ["path"] = authJar };
        }

        // the 10-minute cap is a free/anon-tier limit -- any recognised paid plan uploads clips of any length.
        public static bool SubjectToStreamableDurationLimit()
        {
            var a = Server.State.Auth;
            return !a.SignedIn || string.IsNullOrEmpty(a.Plan) || string.Equals(a.Plan, "free", StringComparison.OrdinalIgnoreCase);
        }

        // pre-flight against streamables 10-min free-tier limit: probe the clips duration and reject before the (multi-minute) upload instead of letting it fail transcode with "Video too long". returns the error JObject to send back, or null to proceed. fails open -- a probe failure just lets the upload run.
        public static JObject CheckStreamableDuration(string path)
        {
            if (!SubjectToStreamableDurationLimit()) return null;
            try
            {
                var meta = Compression.GetVideoMetadata(Compression.FindCompressionFfprobe(), Compression.FindCompressionFfmpeg(), path);
                if (!meta.Ok || meta.Duration <= Constants.STREAMABLE_FREE_MAX_DURATION_SEC) return null;
                int total = (int)Math.Round(meta.Duration);
                string mmss = (total / 60) + ":" + (total % 60).ToString("D2");
                return new JObject
                {
                    ["ok"] = false,
                    ["tooLong"] = true,
                    ["durationSec"] = total,
                    ["message"] = "Clip is " + mmss + " long. Streamable's limit is 10 minutes -- trim it shorter first.",
                };
            }
            catch (Exception ex)
            {
                Log.Write("CheckStreamableDuration probe failed (allowing upload): " + ex.Message, "upload");
                return null;
            }
        }

        public static JObject StartStreamableUpload(Clips.SafeClipPath selected, string uploadPath = "", string displayName = "", bool quiet = false)
        {
            AppConfig.LoadConfig();
            string requestId = UploadState.NewRequestId();

            var clip = selected;
            if (clip == null)
            {
                var latest = Clips.FindLatestClip();
                if (latest != null) clip = new Clips.SafeClipPath { Name = latest.Name, Full = latest.Full };
            }
            if (clip == null)
            {
                UploadState.SetUploadState(requestId: requestId, state: "error", active: false, error: "No mp4/mkv/mov clip found");
                return new JObject { ["ok"] = false, ["message"] = "No mp4/mkv/mov clip found" };
            }

            string clipName = !string.IsNullOrWhiteSpace(displayName) ? displayName : clip.Name;
            var decision = UploadState.GetUploadJobStartDecision(clipName);
            if (!decision.Ok) return new JObject { ["ok"] = false, ["busy"] = decision.Busy, ["message"] = decision.Message };

            string uploadFull = !string.IsNullOrWhiteSpace(uploadPath) ? Path.GetFullPath(uploadPath) : clip.Full;
            if (!File.Exists(uploadFull))
            {
                string msg = "Upload file not found: " + uploadFull;
                UploadState.SetUploadState(requestId: requestId, state: "error", active: false, clipName: clipName, error: msg);
                return new JObject { ["ok"] = false, ["message"] = msg };
            }

            var tooLong = CheckStreamableDuration(uploadFull);
            if (tooLong != null)
            {
                UploadState.SetUploadState(requestId: requestId, state: "error", active: false, clipName: clipName, error: tooLong["message"].Value<string>());
                return tooLong;
            }

            var fi = new FileInfo(uploadFull);
            long effCap = Constants.GetEffectiveUploadCap();
            if (effCap > 0 && fi.Length > effCap)
            {
                // keep this short -- the dock button caps display at 60 chars and the popup buttons cap at 28. full context goes via toast in the popup.
                long mb = (long)Math.Ceiling(fi.Length / 1024.0 / 1024.0);
                long cap = effCap / 1024 / 1024;
                string msg = "Too big: " + mb + " MB / " + cap + " MB limit";
                UploadState.SetUploadState(requestId: requestId, state: "error", active: false, clipName: clipName, error: msg);
                return new JObject { ["ok"] = false, ["message"] = msg, ["tooBig"] = true, ["sizeMb"] = mb, ["capMb"] = cap };
            }

            var auth = ResolveUploadAuthJar();
            if (!auth["ok"].Value<bool>())
            {
                string msg = auth["message"]?.Value<string>() ?? "";
                UploadState.SetUploadState(requestId: requestId, state: "error", active: false, clipName: clipName, error: msg);
                return new JObject { ["ok"] = false, ["message"] = msg };
            }

            UploadState.SetUploadState(
                requestId: requestId, state: "uploading", active: true, clipName: clipName,
                startedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), url: "", error: "", phase: "preparing",
                percent: 1, kind: "upload", tempPath: "");

            bool authRequired = auth["required"]?.Value<bool>() ?? false;
            string authJarPath = authRequired ? auth["path"]?.Value<string>() : null;

            Log.Write("Start-StreamableUpload spawning in-process upload task", "upload", requestId);

            // token so /cancel-upload can abort between steps -- killing the curl process only covers a cancel that
            // lands while curl is actually running; steps 1/3 and the gaps had no way to stop, so a cancel at (say)
            // 70% could still finish the S3 upload + trigger the transcode.
            var cts = new CancellationTokenSource();
            UploadState.SetUploadState(requestId: requestId, cts: cts);

            var task = Task.Run(() => UploadWorker.Run(requestId, uploadFull, authJarPath, authRequired, 0, 100, quiet, cts.Token));
            task.ContinueWith(t => HandleUploadCompletion(t, requestId, clipName, quiet));

            return new JObject { ["ok"] = true, ["state"] = "uploading", ["clip"] = clipName, ["requestId"] = requestId };
        }

        // runs once an upload task finishes -- whether it was a plain upload or the tail of a compress-then-upload chain (Compression.StartCompressedStreamableUpload calls this too, since the ps original routed both thru this same Start-UploadResultWatcher). on success, records the clips_db entry (url + initial "still processing" transcode state), shows a toast, and starts the background transcode poller; on failure or cancellation, resolves the job to error/idle. mirrors Start-UploadResultWatcher, minus the process-wait + status-file read (UploadWorker.Run reports thru UploadState.SetUploadState directly and returns its outcome, so theres nothing left to poll for).
        public static void HandleUploadCompletion(Task<UploadOutcome> task, string requestId, string clipName, bool quiet = false)
        {
            bool workerThrew = task.IsFaulted;
            string workerThrewMessage = workerThrew ? (task.Exception?.InnerException?.Message ?? task.Exception?.Message) : null;

            bool cancelled = false;
            lock (Server.State.UploadLock)
            {
                var job = Server.State.Jobs.TryGetValue(requestId, out var j) ? j : Server.State.Upload;
                if (job.CancelRequested)
                {
                    job.CancelRequested = false;
                    cancelled = true;
                }
            }
            // cancel-activeupload has already written the final "cancelled" state -- dont overwrite it with a generic failure built from whatever the worker happened to return after being killed mid-flight.
            if (cancelled) return;

            if (workerThrew)
            {
                UploadState.SetUploadState(requestId: requestId, state: "error", active: false, error: "Upload failed: " + workerThrewMessage, phase: "error", tempPath: "");
                return;
            }

            var outcome = task.Result;
            if (!outcome.Ok || string.IsNullOrEmpty(outcome.Url))
            {
                string msg = !string.IsNullOrEmpty(outcome.Message) ? outcome.Message : "Upload finished without a Streamable result";
                UploadState.SetUploadState(requestId: requestId, state: "error", active: false, error: msg, phase: "error", tempPath: "");
                return;
            }

            string result = outcome.Url;
            string shortcode = "";
            var match = Regex.Match(result, @"^https://streamable\.com/([A-Za-z0-9_-]+)$");
            if (match.Success) shortcode = match.Groups[1].Value;

            // the transcode step just triggered streamables encode; the video isnt watchable until streamable finishes processing it. mark ready=false and let the background poller flip it once streamables api reports status=2.
            Clips.MarkUploaded(clipName, result, shortcode, ready: false, transcodeStatus: 1, transcodePercent: 0);

            UploadState.SetUploadState(requestId: requestId, state: "done", active: false, url: result, error: "", phase: "done", percent: 100, tempPath: "");

            // the "link copied" toast + the actual clipboard write are deferred to TranscodePollWorker (fires on streamable status 2) so the link isnt handed over until the video is watchable. quiet (bulk) still suppresses both.
            if (!string.IsNullOrEmpty(shortcode)) StartTranscodePoll(shortcode, clipName, quiet);
        }

        // resolve a real .ico for the toast -- the appearance-tab custom/preset icon, else the bundled replaykit .ico. tries a couple of fixed locations so it still works from the detached poll process where the settings path may not resolve. never returns the system info glyph.
        internal static string ResolveToastIconPath()
        {
            try
            {
                string p = ReplaykitSettings.EffectiveReplayKitIconPath();
                if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
            }
            catch { }
            foreach (var cand in new[]
            {
                Constants.OBS_ICON_PATH,
                Path.Combine(Constants.APP_ICONS_DIR, "replaykit.ico"),
                Path.Combine(Constants.HelperRoot, "obs-replaykit.ico"),
            })
            {
                try { if (!string.IsNullOrEmpty(cand) && File.Exists(cand)) return cand; }
                catch { }
            }
            return null;
        }

        // "Clip Uploaded" toast once streamable finishes. delegates to ToastNotify (real action-center notification via the start-menu shortcut + ToastNotificationManager) -- the shell falls back to a transient Shell_NotifyIcon balloon otherwise, which win11 shows with a generic (i) and doesnt keep in the notification list. link is NOT in the body -- its already on the clipboard.
        internal static void ShowUploadToast(string url, string clipName = "")
        {
            string shownName = string.IsNullOrEmpty(clipName) ? "" : Path.GetFileNameWithoutExtension(clipName);
            string body = string.IsNullOrEmpty(shownName)
                ? "Your clip is ready and the link is on your clipboard"
                : "Your \"" + shownName + "\" is ready, link copied to clipboard";
            ToastNotify.Show("Clip Uploaded", body, ResolveToastIconPath());
        }

        // background transcode poller. streamables /transcode call only queues encoding; the video isnt watchable until the status field on /api/v1/videos/<shortcode> reaches 2. spawned detached (CREATE_BREAKAWAY_FROM_JOB) as this same exe running its hidden --transcode-poll mode, rather than a plain Task, becuase a plain in-process worker dies with the helper the instant obs/the helper exits -- confirmed 2026-08-19 (in the ps original) this was leaving clips permanently stuck showing "processing" even long after streamable had actually finished, since nothing ever resumed a poll that got killed mid-flight.
        private static void StartTranscodePoll(string shortcode, string clipName, bool quiet = false)
        {
            try
            {
                string dbPath = AppConfig.GetDbPath();
                string logPath = Server.State.LogEnabled ? Path.Combine(Constants.LOG_DIR, "transcode.log") : "";
                string cookieJar = "";
                if (Server.State.Auth.SignedIn)
                {
                    string candidate = AuthCore.GetAuthCookieJarPath();
                    if (File.Exists(candidate)) cookieJar = candidate;
                }
                string exePath = Process.GetCurrentProcess().MainModule.FileName;
                string cmdLine = ProcessArgs.Quote(exePath) +
                    " --transcode-poll" +
                    " -Shortcode " + ProcessArgs.Quote(shortcode) +
                    " -ClipName " + ProcessArgs.Quote(clipName) +
                    " -DbPath " + ProcessArgs.Quote(dbPath) +
                    " -Api " + ProcessArgs.Quote(Constants.STREAMABLE_API) +
                    " -LogPath " + ProcessArgs.Quote(logPath) +
                    " -CookieJar " + ProcessArgs.Quote(cookieJar) +
                    " -Quiet " + (quiet ? "1" : "0");
                Native.SpawnDetached(cmdLine, Constants.HelperRoot);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
            {
                Log.Write("Could not start transcode poll worker: " + ex.Message, "upload");
            }
        }

        // re-arm transcode polls at helper start for clips still mid-processing. the detached poll worker normally outlives a graceful restart, but a kill / crash / reboot leaves the clip frozen on "Processing" with nothing to update transcode_percent -- this catches those. a redundant poll when one did survive is harmless (both write the same values, atomic replace, both self-terminate at status>=2 or the 30-min deadline). bounded at 90 min since upload -- 3x the worker deadline, past which the clip is done or dead either way.
        public static void ResumeTranscodePollsAtStartup()
        {
            JObject db;
            try { db = Clips.ReadClipsDb(); }
            catch (Exception ex) { Log.Write("ResumeTranscodePollsAtStartup: read db: " + ex.Message, "upload"); return; }
            if (db == null) return;

            long nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int armed = 0;
            foreach (var prop in db.Properties())
            {
                if (!(prop.Value is JObject e)) continue;
                // clips_db stores the raw "shortcode" + "url"; the /clips response renames url -> streamable_url, so read the db field names here.
                string shortcode = e["shortcode"]?.Value<string>();
                if (string.IsNullOrEmpty(shortcode))
                {
                    var m = Regex.Match(e["url"]?.Value<string>() ?? "", @"^https://streamable\.com/([A-Za-z0-9_-]+)$");
                    if (m.Success) shortcode = m.Groups[1].Value;
                }
                if (string.IsNullOrEmpty(shortcode)) continue;
                bool ready = e["ready"]?.Value<bool>() ?? false;
                int? ts = e["transcode_status"]?.Value<int?>();
                // 2 = ready, 3 = failed, 4 = timed out -- only null/0/1 are still worth polling.
                if (ready || ts == 2 || ts == 3 || ts == 4) continue;
                long uploadedAt = e["uploaded_at"]?.Value<long>() ?? 0;
                if (uploadedAt <= 0 || nowSec - uploadedAt > 90 * 60) continue;
                // resumed after a restart -- the user has moved on, so no toast / clipboard-hijack when it finishes; the dock card still flips to "Copy Link".
                StartTranscodePoll(shortcode, prop.Name, quiet: true);
                armed++;
            }
            if (armed > 0) Log.Write("ResumeTranscodePollsAtStartup: re-armed " + armed + " transcode poll(s)", "upload");
        }
    }
}

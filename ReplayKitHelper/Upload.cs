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

            // quiet = part of a bulk selection; the dock shows per-card "Copy Link" + a summary toast, so skip the windows balloon.
            if (!quiet) ShowUploadToast(result);

            if (!string.IsNullOrEmpty(shortcode)) StartTranscodePoll(shortcode, clipName);
        }

        // windows balloon-tip notification: the upload finished and the link is on the clipboard. runs on its own one-shot sta thread with a short DoEvents pump -- NotifyIcon/ShowBalloonTip needs a message loop to actually paint and auto-dismiss, which this Task continuation (a plain threadpool thread) doesnt have. the ps original spawned a whole seperate powershell.exe for this since its watcher runspace was mta and had no easy way to get an sta thread with a pump; a background thread is the direct equivalent here and skips a process spawn entirely.
        // the Appearance-tab icon for the balloon tip, or null to fall back to the system info glyph. a fresh Icon per toast (disposed with the NotifyIcon) -- Icon(path) loads the closest frame to the default small size.
        private static System.Drawing.Icon LoadReplayKitToastIcon()
        {
            string path = ReplaykitSettings.EffectiveReplayKitIconPath();
            if (string.IsNullOrEmpty(path)) return null;
            try { return new System.Drawing.Icon(path, 32, 32); }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException) { return null; }
        }

        private static void ShowUploadToast(string url)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    using (var icon = new System.Windows.Forms.NotifyIcon())
                    {
                        icon.Icon = LoadReplayKitToastIcon() ?? System.Drawing.SystemIcons.Information;
                        icon.Visible = true;
                        icon.ShowBalloonTip(5000, "OBS clip uploaded", "Link copied to clipboard\n" + url, System.Windows.Forms.ToolTipIcon.Info);
                        var deadline = DateTime.UtcNow.AddSeconds(6);
                        while (DateTime.UtcNow < deadline)
                        {
                            System.Windows.Forms.Application.DoEvents();
                            Thread.Sleep(100);
                        }
                        icon.Visible = false;
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("toast notification failed: " + ex.Message, "upload");
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
        }

        // background transcode poller. streamables /transcode call only queues encoding; the video isnt watchable until the status field on /api/v1/videos/<shortcode> reaches 2. spawned detached (CREATE_BREAKAWAY_FROM_JOB) as this same exe running its hidden --transcode-poll mode, rather than a plain Task, becuase a plain in-process worker dies with the helper the instant obs/the helper exits -- confirmed 2026-08-19 (in the ps original) this was leaving clips permanently stuck showing "processing" even long after streamable had actually finished, since nothing ever resumed a poll that got killed mid-flight.
        private static void StartTranscodePoll(string shortcode, string clipName)
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
                    " -CookieJar " + ProcessArgs.Quote(cookieJar);
                Native.SpawnDetached(cmdLine, Constants.HelperRoot);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
            {
                Log.Write("Could not start transcode poll worker: " + ex.Message, "upload");
            }
        }
    }
}

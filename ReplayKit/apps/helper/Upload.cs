using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // starts a plain streamable upload (UploadWorker) and reacts once it finishes: writes the clips_db entry and starts the background transcode poller
    internal static class Upload
    {
        internal sealed class UploadAuth
        {
            public string Cookies { get; }
            public bool Required { get; }
            public long SizeCap { get; }
            public bool DurationLimited { get; }
            public int RetentionDays { get; }

            public UploadAuth(string cookies, bool required, long sizeCap, bool durationLimited, int retentionDays)
            {
                Cookies = cookies; Required = required; SizeCap = sizeCap;
                DurationLimited = durationLimited; RetentionDays = retentionDays;
            }
        }

        internal static UploadAuth CaptureAuth()
        {
            lock (Server.State.AuthLock)
            {
                bool required = Server.State.Auth.SignedIn;
                string cookies = required ? AuthCore.CaptureCookies() : "";
                if (required && string.IsNullOrWhiteSpace(cookies))
                    throw new InvalidOperationException(Locales.Message("share.signedInStreamableSessionIsUnavailable"));
                return new UploadAuth(cookies, required, Constants.GetEffectiveUploadCap(), SubjectToStreamableDurationLimit(), Constants.GetEffectiveRetentionDays());
            }
        }

        // the 10-minute cap is a free/anon-tier limit -- any recognised paid plan uploads clips of any length.
        public static bool SubjectToStreamableDurationLimit()
        {
            var a = Server.State.Auth;
            return !a.SignedIn || string.IsNullOrEmpty(a.Plan) || string.Equals(a.Plan, "free", StringComparison.OrdinalIgnoreCase);
        }

        // pre-flight against streamables 10-min free-tier limit: probe the clips duration and reject before the (multi-minute) upload instead of letting it fail transcode with "Video too long". returns the error JObject to send back, or null to proceed. fails open -- a probe failure just lets the upload run.
        public static JObject CheckStreamableDuration(string path, bool? limited = null)
        {
            if (!(limited ?? SubjectToStreamableDurationLimit())) return null;
            try
            {
                using var reader = JobCoordinator.ReadSource(path);
                var meta = Compression.GetVideoMetadata(Compression.FindCompressionFfprobe(), Compression.FindCompressionFfmpeg(), path);
                if (!meta.Ok || meta.Duration <= Constants.STREAMABLE_FREE_MAX_DURATION_SEC) return null;
                int total = (int)Math.Round(meta.Duration);
                string mmss = (total / 60) + ":" + (total % 60).ToString("D2");
                return new JObject
                {
                    ["ok"] = false,
                    ["tooLong"] = true,
                    ["durationSec"] = total,
                    ["message"] = Locales.Message("common.clipIsLengthLongStreamableS", "length", mmss),
                };
            }
            catch (Exception ex)
            {
                Log.Write("CheckStreamableDuration probe failed (allowing upload): " + ex.Message, "upload");
                return null;
            }
        }

        public static JObject StartStreamableUpload(Clips.SafeClipPath selected, string uploadPath = "", string displayName = "", bool quiet = false, UploadAuth auth = null)
        {
            AppConfig.LoadConfig();
            string requestId = UploadState.NewRequestId();

            var clip = selected ?? Clips.FindLatestClip();
            if (clip == null)
            {
                UploadState.SetUploadState(requestId: requestId, state: "error", active: false, error: Locales.Message("share.noMp4MkvMovClipFound"));
                return new JObject { ["ok"] = false, ["message"] = Locales.Message("share.noMp4MkvMovClipFound") };
            }

            string clipName = !string.IsNullOrWhiteSpace(displayName) ? displayName : clip.Name;
            using (var reservation = JobCoordinator.TryReserve(requestId, clipName, clip.Full, "upload", out string busy))
            {
                if (reservation == null) return new JObject { ["ok"] = false, ["busy"] = true, ["message"] = busy };

                string uploadFull = !string.IsNullOrWhiteSpace(uploadPath) ? Path.GetFullPath(uploadPath) : clip.Full;
                if (!File.Exists(uploadFull))
                {
                    string msg = Locales.Message("share.uploadFileNotFoundPath", "path", uploadFull);
                    UploadState.SetUploadState(requestId: requestId, state: "error", active: false, clipName: clipName, error: msg);
                    return new JObject { ["ok"] = false, ["message"] = msg };
                }

                auth = auth ?? CaptureAuth();
                var tooLong = CheckStreamableDuration(uploadFull, auth.DurationLimited);
                if (tooLong != null)
                {
                    UploadState.SetUploadState(requestId: requestId, state: "error", active: false, clipName: clipName, error: tooLong["message"].Value<string>());
                    return tooLong;
                }

                var fi = new FileInfo(uploadFull);
                string fileIdentity = ClipRenames.Identity(new FileInfo(clip.Full));
                if (fileIdentity == null) return new JObject { ["ok"] = false, ["message"] = Locales.Message("storage.theSelectedClipChangedSelectIt") };
                long effCap = auth.SizeCap;
                if (effCap > 0 && fi.Length > effCap)
                {
                    // keep this short -- the dock button caps display at 60 chars and the popup buttons cap at 28. full context goes via toast in the popup.
                    long mb = (long)Math.Ceiling(fi.Length / 1024.0 / 1024.0);
                    long cap = effCap / 1024 / 1024;
                    string msg = Locales.Message("share.tooBigSizeMbCapMb", "size", mb, "cap", cap);
                    UploadState.SetUploadState(requestId: requestId, state: "error", active: false, clipName: clipName, error: msg);
                    return new JObject { ["ok"] = false, ["message"] = msg, ["tooBig"] = true, ["sizeMb"] = mb, ["capMb"] = cap };
                }

                UploadState.SetUploadState(
                    requestId: requestId, state: "uploading", active: true, clipName: clipName,
                    startedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), url: "", error: "", phase: "preparing",
                    percent: 1, kind: "upload");

                Log.Write("Start-StreamableUpload spawning in-process upload task", "upload", requestId);

                // token so /cancel-upload can abort between steps -- killing the curl process only covers a cancel that lands while curl is actually running; steps 1/3 and the gaps had no way to stop, so a cancel at (say) 70% could still finish the S3 upload + trigger the transcode.
                var token = JobCoordinator.Token(requestId);

                reservation.Start(() => UploadWorker.Run(requestId, uploadFull, auth.Cookies, auth.Required, 0, 100, quiet, token),
                    t => HandleUploadCompletion(t, requestId, clipName, clip.Root, fileIdentity, quiet, auth));

                return new JObject { ["ok"] = true, ["state"] = "uploading", ["clip"] = clipName, ["requestId"] = requestId };
            }
        }

        // runs once an upload task finishes, for a plain upload and for the tail of a compress-then-upload: on success it records the clips_db entry (url + still-processing transcode state) and starts the transcode poller, on failure it resolves the job to error
        public static void HandleUploadCompletion(Task<UploadOutcome> task, string requestId, string clipName, string libraryRoot, string fileIdentity, bool quiet = false, UploadAuth auth = null)
        {
            bool workerThrew = task.IsFaulted;
            string workerThrewMessage = workerThrew ? (task.Exception?.InnerException?.Message ?? task.Exception?.Message) : null;

            bool cancelled = false;
            lock (Server.State.UploadLock)
            {
                var job = Server.State.Jobs.TryGetValue(requestId, out var j) ? j : Server.State.Upload;
                if (job.CancelRequested)
                {
                    cancelled = true;
                }
            }
            // the coordinator publishes final cancellation only after the worker and completion cleanup have stopped.
            if (cancelled) return;

            if (workerThrew)
            {
                UploadState.SetUploadState(requestId: requestId, state: "error", active: false, error: "Upload failed: " + workerThrewMessage, phase: "error");
                return;
            }

            var outcome = task.Result;
            if (!outcome.Ok || string.IsNullOrEmpty(outcome.Url))
            {
                string msg = !string.IsNullOrEmpty(outcome.Message) ? outcome.Message : Locales.Message("clips.uploadFinishedWithoutResult");
                UploadState.SetUploadState(requestId: requestId, state: "error", active: false, error: msg, phase: "error");
                return;
            }

            string result = outcome.Url;
            string shortcode = "";
            var match = Regex.Match(result, @"^https://streamable\.com/([A-Za-z0-9_-]+)$");
            if (match.Success) shortcode = match.Groups[1].Value;

            // the transcode step just triggered streamables encode; the video isnt watchable until streamable finishes processing it. mark ready=false and let the background poller flip it once streamables api reports status=2.
            JobCoordinator.Commit(requestId, () =>
            {
                Clips.MarkUploaded(libraryRoot, clipName, result, fileIdentity, shortcode, ready: false, transcodeStatus: 1, transcodePercent: 0, retentionDays: auth?.RetentionDays);

                UploadState.SetUploadState(requestId: requestId, state: "done", active: false, url: result, error: "", phase: "done", percent: 100);
            });

            // the "link copied" toast + the actual clipboard write are deferred to TranscodePollWorker (fires on streamable status 2) so the link isnt handed over until the video is watchable. quiet (bulk) still suppresses both.
            if (!string.IsNullOrEmpty(shortcode)) StartTranscodePoll(shortcode, clipName, quiet, auth);
        }

        // resolve a real .ico for the toast -- the appearance-tab custom/preset icon, else the bundled replaykit .ico. tries a couple of fixed locations so it still works from the detached poll process where the settings path may not resolve. never returns the system info glyph.
        internal static string ResolveToastIconPath()
        {
            try
            {
                string p = ReplaykitSettings.EffectiveReplayKitIconPath();
                if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
            }
            catch (Exception ex) { Log.Write("ResolveToastIconPath: " + ex.Message); }
            foreach (var cand in new[]
            {
                Constants.OBS_ICON_PATH,
                Path.Combine(Constants.APP_ICONS_DIR, "replaykit.ico"),
                Path.Combine(Constants.HelperRoot, "obs-replaykit.ico"),
            })
            {
                if (!string.IsNullOrEmpty(cand) && File.Exists(cand)) return cand;
            }
            return null;
        }

        // "Clip Uploaded" toast once streamable finishes. delegates to ToastNotify (real action-center notification via the start-menu shortcut + ToastNotificationManager) -- the shell falls back to a transient Shell_NotifyIcon balloon otherwise, which win11 shows with a generic (i) and doesnt keep in the notification list. link is NOT in the body -- its already on the clipboard.
        internal static void ShowUploadToast(string url, string clipName = "", bool copied = true)
        {
            string shownName = string.IsNullOrEmpty(clipName) ? "" : Path.GetFileNameWithoutExtension(clipName);
            string body = string.IsNullOrEmpty(shownName)
                ? (copied ? Locales.Message("share.yourClipIsReadyAndThe") : Locales.Message("share.yourClipIsReady"))
                : (copied ? Locales.Message("share.yourNameIsReadyLinkCopied", "name", shownName) : Locales.Message("share.yourNameIsReady", "name", shownName));
            ToastNotify.Show(Locales.Message("share.clipUploaded"), body, ResolveToastIconPath());
        }

        // background transcode poller: streamables /transcode only queues the encode, and the video is watchable once /api/v1/videos/<shortcode> reports status 2. it runs as this exe in --transcode-poll mode outside the kill-on-close job, since a poll inside the helper died with it and left clips stuck on processing
        private static void StartTranscodePoll(string shortcode, string clipName, bool quiet = false, UploadAuth auth = null)
        {
            string cookieJar = "";
            bool launched = false;
            try
            {
                string dbPath = AppConfig.GetDbPath();
                string logPath = Server.State.LogEnabled ? Path.Combine(Constants.LOG_DIR, "transcode.log") : "";
                auth = auth ?? CaptureAuth();
                if (auth.Required)
                {
                    Directory.CreateDirectory(Constants.SCRATCH_DIR);
                    cookieJar = Path.Combine(Constants.SCRATCH_DIR, "streamable_poll_" + Guid.NewGuid().ToString("N") + ".cookies.txt");
                    File.WriteAllText(cookieJar, auth.Cookies, System.Text.Encoding.ASCII);
                }
                // what the settings say about the link is read now, since the poll runs on its own from here and cannot look it up later
                bool copyLink = true;
                bool notify = true;
                try
                {
                    var settings = ReplaykitSettings.ReadSettings();
                    copyLink = settings["autoCopyLink"]?.Value<bool>() ?? true;
                    notify = settings["linkNotificationEnabled"]?.Value<bool>() ?? true;
                }
                catch (InvalidOperationException) { }
                string cmdLine = ProcessArgs.Quote(ProcessHost.HelperExecutable) + " --transcode-poll" +
                    " -Shortcode " + ProcessArgs.Quote(shortcode) +
                    " -ClipName " + ProcessArgs.Quote(clipName) +
                    " -DbPath " + ProcessArgs.Quote(dbPath) +
                    " -Api " + ProcessArgs.Quote(Constants.STREAMABLE_API) +
                    " -LogPath " + ProcessArgs.Quote(logPath) +
                    " -CookieJar " + ProcessArgs.Quote(cookieJar) +
                    " -DeleteCookieJar 1" +
                    " -Quiet " + (quiet ? "1" : "0") +
                    " -CopyLink " + (copyLink ? "1" : "0") +
                    " -Notify " + (notify ? "1" : "0");
                if (Native.SpawnDetached(cmdLine, Constants.HelperRoot) <= 0)
                    throw new InvalidOperationException(Locales.Message("share.couldNotLaunchTheTranscodeStatus"));
                launched = true;
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
            {
                Log.Write("Could not start transcode poll worker: " + ex.Message, "upload");
            }
            finally
            {
                if (!launched && !string.IsNullOrEmpty(cookieJar))
                    try { File.Delete(cookieJar); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        // re-arm transcode polls at helper start for clips still mid-processing. the detached poll worker normally outlives a graceful restart, but a kill / crash / reboot leaves the clip frozen on "Processing" with nothing to update transcode_percent -- this catches those. a redundant poll when one did survive is harmless (both write the same values, atomic replace, both self-terminate at status>=2 or the 30-min deadline). bounded at 90 min since upload -- 3x the worker deadline, past which the clip is done or dead either way.
        public static void ResumeTranscodePollsAtStartup()
        {
            JObject db;
            try { db = Clips.ReadClipsDb(); }
            catch (Exception ex) { Log.Write("ResumeTranscodePollsAtStartup: read db: " + ex.Message, "upload"); return; }
            if (db == null) return;

            int armed = 0;
            foreach (var prop in db.Properties())
                if (prop.Value is JObject entry && ResumeTranscodePoll(prop.Name, entry, recentOnly: true)) armed++;
            if (armed > 0) Log.Write("ResumeTranscodePollsAtStartup: re-armed " + armed + " transcode poll(s)", "upload");
        }

        internal static bool ResumeTranscodePoll(string name, JObject entry, bool recentOnly = false)
        {
            bool? ready = entry["ready"]?.Value<bool?>();
            int? status = entry["transcode_status"]?.Value<int?>();
            if (ready == true || status >= 2 || (ready == null && status == null)) return false;
            long uploadedAt = entry["uploaded_at"]?.Value<long>() ?? 0;
            long age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - uploadedAt;
            if (uploadedAt <= 0 || age >= Constants.GetEntryRetentionSec(entry) || (recentOnly && age > 90 * 60)) return false;
            var link = Regex.Match(entry["url"]?.Value<string>() ?? "", @"\Ahttps://streamable\.com/([A-Za-z0-9_-]+)\z");
            if (!link.Success) return false;
            StartTranscodePoll(link.Groups[1].Value, name, quiet: true);
            return true;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Threading.Tasks;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // constants and the shared mutable server state that every connection handler and background task reaches through Server.State
    internal static class Constants
    {
        public const string HOST_ADDR = "127.0.0.1";
        public const int DEFAULT_PORT = 8767;
        public const int CLIPS_CACHE_MAX_AGE_MS = 60000;
        public const int CLIPS_PAGE_LIMIT_MAX = 500;
        public const int MAX_CLIPS = 0; // 0 = unlimited
        public const int MAX_CONCURRENT_VIDEO_JOBS = 2;
        public const long PREVIEW_CHUNK = 4 * 1024 * 1024;
        public const int MAX_PREVIEW_STREAM = 2;
        public const long ANON_SIZE_CAP = 250L * 1024 * 1024;
        public const int ANON_RETENTION_DAYS = 1;
        public const int SIGNED_IN_DEFAULT_RETENTION = 90;
        // streamable free / anon tier caps a video at 10 minutes -- longer uploads fine then fail transcode with "Video too long".
        public const int STREAMABLE_FREE_MAX_DURATION_SEC = 600;
        public const string STREAMABLE_API = "https://api-f.streamable.com";
        public const bool DEFAULT_LOG_ENABLED = false;

        public static readonly int MAX_CONNECTION_THREADS = Math.Max(6, Environment.ProcessorCount);

        // the folder the running exe lives in, where the setup puts ffmpeg/ffprobe next to the helper
        public static readonly string HelperRoot = Path.GetDirectoryName(typeof(Program).Assembly.Location);
        // bundled next to the helper exe by the setup wizard, not extracted from obs64.exe at runtime -- Icon.ExtractAssociatedIcon only ever returns a single low-res frame, so dock/toast/window icons would look blurry next to the real multi-resolution icon the setup exe uses.
        public static readonly string OBS_ICON_PATH = Path.Combine(HelperRoot, "obs-replaykit.ico");
        // bundled preset icons for the Appearance tab -- HelperRoot is ...\obs-replayKit\scripts\helper, so this is ...\obs-replayKit\icons, mirrored from assets/ by the installer. drop-in extensible: any .ico/.png here shows in the picker.
        public static readonly string APP_ICONS_DIR = Path.GetFullPath(Path.Combine(HelperRoot, "..", "..", "icons"));
        // fonts the subtitles of the editor can use that windows does not have, mirrored from assets/ like the icons
        public static readonly string APP_FONTS_DIR = Path.GetFullPath(Path.Combine(HelperRoot, "..", "..", "fonts"));
        // custom icons the user has picked -- converted to .ico and saved here so they become deletable presets. kept out of APP_ICONS_DIR so the installers assets/ mirror never touches them and so "delete" can be gated to this dir only.
        public static readonly string USER_ICONS_DIR = Path.Combine(APP_ICONS_DIR, "user");
        // %APPDATA%\obs-studio -- APP_ICONS_DIR is ...\obs-replayKit\icons, so up two.
        public static readonly string OBS_CONFIG_DIR = Path.GetFullPath(Path.Combine(APP_ICONS_DIR, "..", ".."));
        // custom themes the user saved from the Appearance tab, one <name>.json of colour tokens each. per-user, gitignored.
        public static readonly string USER_THEMES_DIR = Path.GetFullPath(Path.Combine(APP_ICONS_DIR, "..", "themes-user"));

        public static readonly string REPLAYKIT_TEMP_ROOT = Path.Combine(Path.GetTempPath(), "ReplayKit");
        public static readonly string THUMB_DIR = Path.Combine(REPLAYKIT_TEMP_ROOT, "thumbs");
        public static readonly string LOG_DIR = Path.Combine(REPLAYKIT_TEMP_ROOT, "logs");
        public static readonly string HELPER_LOG_PATH = Path.Combine(LOG_DIR, "helper.log");
        public static readonly string UPLOAD_LOG_PATH = Path.Combine(LOG_DIR, "upload.log");
        public static readonly string COMPRESS_LOG_PATH = Path.Combine(LOG_DIR, "compress.log");
        public static readonly string COMPRESS_TMP_DIR = Path.Combine(REPLAYKIT_TEMP_ROOT, "compressed");
        public static readonly string SCRATCH_DIR = Path.Combine(REPLAYKIT_TEMP_ROOT, "scratch");
        // what makes a clip quick to open in the editor again, see ClipCache
        public static readonly string CACHE_DIR = Path.Combine(REPLAYKIT_TEMP_ROOT, "cache");

        public static readonly string AUTH_DIR = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OBS Streamable Helper");
        public static readonly string AUTH_FILE = Path.Combine(AUTH_DIR, "auth.dat");

        public static readonly HashSet<string> ALLOWED_EXTS = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".mov" };
        public static readonly Dictionary<string, string> CONTENT_TYPES = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".mp4"] = "video/mp4",
            [".mkv"] = "video/x-matroska",
            [".mov"] = "video/quicktime",
        };

        // env vars -> parent process -> running obs64/obs32/obs processes -> registry app paths -> default program files locations.
        public static string ResolveObsExe()
        {
            string envPath = Environment.GetEnvironmentVariable("OBS_REPLAYKIT_OBS_EXE");
            if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath)) return envPath;

            try
            {
                int parentPid = ParentWatchdog.GetParentPid();
                if (parentPid > 0)
                {
                    var proc = Process.GetProcessById(parentPid);
                    if (IsObsProcessName(proc.ProcessName) && proc.MainModule != null && !IsRoleHost(proc.MainModule.FileName))
                    {
                        return proc.MainModule.FileName;
                    }
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) { }

            foreach (var name in new[] { "obs64", "obs32", "obs" })
            {
                try
                {
                    foreach (var proc in Process.GetProcessesByName(name))
                    {
                        try { if (proc.MainModule != null && !IsRoleHost(proc.MainModule.FileName)) return proc.MainModule.FileName; }
                        catch (System.ComponentModel.Win32Exception) { }
                        finally { proc.Dispose(); }
                    }
                }
                catch (InvalidOperationException) { }
            }

            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\obs64.exe"))
                    {
                        var path = key?.GetValue(null) as string;
                        if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
                    }
                }
                catch (Exception ex) when (ex is System.Security.SecurityException || ex is IOException) { }
            }

            foreach (var candidate in new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "obs-studio", "bin", "64bit", "obs64.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "obs-studio", "bin", "64bit", "obs64.exe"),
            })
            {
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        // the obs64.exe copy in bin\64bit\replaykit-host runs this helper and the browser pages so they show as obs, but it is never obs itself
        internal static bool IsRoleHost(string path) =>
            string.Equals(Path.GetFileName(Path.GetDirectoryName(path ?? "") ?? ""), "replaykit-host", StringComparison.OrdinalIgnoreCase);

        internal static bool IsRoleHost(Process process)
        {
            try { return IsRoleHost(process.MainModule?.FileName); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException) { return false; }
        }

        private static bool IsObsProcessName(string name) =>
            string.Equals(name, "obs64", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "obs", StringComparison.OrdinalIgnoreCase);

        // hard ceiling on any upload from the free / anon tier. 0 = unlimited only applies to a recognised paid plan.
        public const long HARD_UPLOAD_CAP = 1024L * 1024 * 1024;

        // size limit for the current auth state. 0 means unlimited.
        public static long GetEffectiveUploadCap()
        {
            long cap = Server.State.Auth.SizeCap;
            if (cap > 0) return cap;                                        // basic / plus -- the plans own cap
            return Upload.SubjectToStreamableDurationLimit() ? HARD_UPLOAD_CAP : 0;  // free/anon -> 1 GB, recognised pro -> unlimited
        }

        // retention to tag new uploads with. clips_db filtering still uses each entrys own stored retention_days, so an old anonymous link doesnt suddenly extend after signing in.
        public static int GetEffectiveRetentionDays()
        {
            int d = Server.State.Auth.RetentionDays;
            return d <= 0 ? ANON_RETENTION_DAYS : d;
        }

        public static string GetMaskedIdentity(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "Signed in";
            string v = value.Trim();
            int at = v.IndexOf('@');
            if (at > 0)
            {
                string local = v.Substring(0, at);
                string domain = v.Substring(at + 1);
                string first = local.Substring(0, 1);
                var domParts = domain.Split('.');
                string domainHint = domParts.Length > 1 ? domParts[domParts.Length - 1] : "";
                string suffix = !string.IsNullOrEmpty(domainHint) ? "." + domainHint : "";
                return first + "***@***" + suffix;
            }
            if (v.Length <= 2) return "**";
            return v.Substring(0, 1) + new string('*', Math.Min(6, v.Length - 1));
        }

        // per-entry retention. entries without retention_days use anonymous retention.
        public static long GetEntryRetentionSec(JObject entry)
        {
            var retDays = entry?["retention_days"];
            if (retDays != null && retDays.Value<int>() > 0) return retDays.Value<int>() * 86400L;
            return ANON_RETENTION_DAYS * 86400L;
        }
    }

    // process-wide access point for the one ServerState instance
    internal static class Server
    {
        public static readonly ServerState State = new ServerState();
    }

    // one shared mutable-state instance; fields are grouped by the lock that protects them, and ConfigLock is always taken before ClipsMetaLock, never the reverse
    internal sealed class ServerState
    {
        // -- ConfigLock --
        public readonly object ConfigLock = new object();
        public JObject Config;
        public DateTime ConfigMTime;
        public string ConfigPath;

        // -- LogLock --
        public readonly object LogLock = new object();
        public bool LogEnabled = Constants.DEFAULT_LOG_ENABLED;

        // -- ClipsMetaLock -- snapshot publication, database/index caches, repair state and the folder watcher.
        public readonly object ClipsMetaLock = new object();
        public JObject ClipsDbCache;
        public string ClipsDbCacheSig;
        public JObject ClipIndexCache;
        public string ClipIndexCacheSig;
        public bool ClipIndexRepairRunning;
        public bool ClipIndexRepairQueued;
        public DateTime ClipIndexRepairAt;
        public FileSystemWatcher ClipWatcher;
        public string ClipWatcherPath;

        // -- TrimKeyframeCacheLock / TrimKeyframeJobsLock --
        public readonly object TrimKeyframeCacheLock = new object();
        public readonly Dictionary<string, TrimKeyframeCacheEntry> TrimKeyframeCache = new Dictionary<string, TrimKeyframeCacheEntry>();
        public readonly object TrimKeyframeJobsLock = new object();
        public readonly Dictionary<string, TrimKeyframeJob> TrimKeyframeJobs = new Dictionary<string, TrimKeyframeJob>();

        // -- PreviewLock --
        public readonly object PreviewLock = new object();
        public int ActivePreviews;

        // -- UploadLock -- job status records; operation ownership lives in JobCoordinator.
        public readonly object UploadLock = new object();
        public UploadJobRecord Upload = new UploadJobRecord { RequestId = "" };
        public readonly Dictionary<string, UploadJobRecord> Jobs = new Dictionary<string, UploadJobRecord>();

        // -- ThumbQueueLock --
        public readonly object ThumbQueueLock = new object();

        // written from both the accept loop and route handlers; read by the accept loop every iteration.
        public volatile bool Shutdown;

        // written by route handlers and read once by the shutdown path, so it needs no lock of its own null means no restart pending; a non-null value is the obs executable path to relaunch once this process exits (the legacy fallback path used only when the detached relauncher in Routes.cs cant be started).
        public volatile string RestartAfterCleanObsPath;

        // -- AuthLock --
        public readonly object AuthLock = new object();
        public AuthState Auth = new AuthState();

        // -- CapabilitiesLock --
        public readonly object CapabilitiesLock = new object();
        public JObject Capabilities;
        public DateTime CapabilitiesAt;

        // -- OverlayPreviewLock --
        public readonly object OverlayPreviewLock = new object();
        public JObject ReplaykitOverlayPreviewState;
        public long ReplaykitOverlayPreviewRevision;

        // -- HotkeyCaptureLock --
        public readonly object HotkeyCaptureLock = new object();
        public bool ReplaykitHotkeyCaptureActive;

        // -- ProjectorLock --
        public readonly object ProjectorLock = new object();
        public bool ReplaykitDiscordProjectorInspectMode;
        public bool ReplaykitDiscordProjectorTaskbarHiddenApplied;
        // null means "no open attempt pending"; non-null (even an empty list) is the hwnd snapshot taken before the current pending OpenVideoMixProjector call, reused across retries so a slow-to-appear window isnt wrongly read as pre-existing.
        public List<long> ReplaykitDiscordProjectorPendingBaseline;

        // -- VideoApplyLock -- serializes the stop-outputs / SetVideoSettings (obs_reset_video) / restart-outputs cycle. concurrent obs_reset_video calls deadlock obss video graph -- 2026-08-28 a user hard-froze obs by rapidly re-applying the downscale resolution.
        public readonly object VideoApplyLock = new object();
        public DateTime LastVideoApplyDoneUtc = DateTime.MinValue;

        // -- IpcLock -- streamed in from the native plugin (core/ipc_pipe.cpp) over the OBSReplayKitIpc named pipe, replacing the old scratch-file handoff.
        public readonly object IpcLock = new object();
        public bool IpcClientConnected;
        public long ObsMainWindowHwnd;           // 0 until the plugin sends MAINWIN
        public List<long> ProjectorHwnds;        // null = pipe down / no snapshot yet; non-null (even empty) = authoritative
        public DateTime ProjectorHwndsAtUtc;

        // -- ObsWebSocketLock --
        public readonly object ObsWebSocketLock = new object();
        public ClientWebSocket ObsWebSocket;
        public int ObsWebSocketPort;

        // -- UpdateCheckLock --
        public readonly object UpdateCheckLock = new object();
        public bool ReplaykitStartupUpdateChecked;
        // set by the startup check when a newer release is waiting. the first-run wizard defers to it -- setting obs up against a build that is about to be replaced wastes the users time, and the install restarts obs out from under the wizard anyway.
        public bool StartupUpdatePending;
        // set the moment anything opens the first-run wizard. the helper opens it on its first pipe connect (which covers a launch straight to the tray) and the dock asks again once obs is on screen -- without this the second ask re-raises the window and reads as the wizard popping up a second time.
        public bool SetupWizardOpened;
        public bool UpdateApplyInProgress;

    }

    // one upload/compress/trim-overwrite job as the /status json shows it; StartedAt/UpdatedAt are unix milliseconds, the shape the pages expect. cancellation lives in JobCoordinator, not here
    internal sealed class UploadJobRecord
    {
        public string State = "idle";
        public bool Active;
        public string ClipName = "";
        public long StartedAt;
        public long UpdatedAt;
        public string Url = "";
        public string Error = "";
        public string Message = "";
        public string Phase = "";
        public int Percent;
        public string RequestId = "";
        public string Kind = "";
        public bool CancelRequested;
    }

    internal sealed class AuthState
    {
        public bool SignedIn;
        public string Username = "";
        public string Plan = "";
        public long SizeCap;
        public int RetentionDays = Constants.ANON_RETENTION_DAYS;
    }

    // an in-flight keyframe scan; Cts is the kill switch for a hung scan (see TrimKeyframeWorkerTimeoutMs in Trim.cs)
    internal sealed class TrimKeyframeJob
    {
        public string Key;
        public string Sig;
        public Task<KeyframeScanResult> Task;
        public DateTime StartedAt;
        public System.Threading.CancellationTokenSource Cts;
    }

    internal sealed class TrimKeyframeCacheEntry
    {
        public string Sig;
        public DateTime At;
        public KeyframeScanResult Result;
    }

    internal sealed class KeyframeScanResult
    {
        public bool Ok;
        public string Name;
        public List<double> Keyframes = new List<double>();
        public int Count;
        public bool Cached;
        public bool Pending;
        public string Method;
        public int ProbeMs;
        public string Message = "";
        public int RetryMs;
    }
}

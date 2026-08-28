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
    // constants + shared mutable server state, ported from obs_replaykit helper modules/00_state.ps1. the ps original seeds these into every pooled runspace via a hand-maintained allowlist (PooledConstantNames) since dot-sourced script state doesn't otherwise cross runspace boundaries -- that whole mechanism is a workaround for powershell's runspace model and has no equivalent here: a compiled assembly's static fields and a shared State instance are already visible from every thread by construction.
    internal static class Constants
    {
        public const string HOST_ADDR = "127.0.0.1";
        public const int DEFAULT_PORT = 8767;
        public const int CLIPS_CACHE_MAX_AGE_MS = 60000;
        public const int CLIPS_PAGE_LIMIT_MAX = 500;
        public const int MAX_CLIPS = 0; // 0 = unlimited
        public const int MAX_CONCURRENT_VIDEO_JOBS = 2;
        public const int CPU_BURST_THRESHOLD_PCT = 40;
        public const long PREVIEW_CHUNK = 4 * 1024 * 1024;
        public const int MAX_PREVIEW_STREAM = 2;
        public const long ANON_SIZE_CAP = 250L * 1024 * 1024;
        public const int ANON_RETENTION_DAYS = 1;
        public const int SIGNED_IN_DEFAULT_RETENTION = 90;
        public const string STREAMABLE_API = "https://api-f.streamable.com";
        public const bool DEFAULT_LOG_ENABLED = false;

        public static readonly int MAX_BURST_CONCURRENT_VIDEO_JOBS = Math.Max(2, Math.Min(4, Environment.ProcessorCount / 4));
        public static readonly int MAX_CONNECTION_THREADS = Math.Max(6, Environment.ProcessorCount);

        // directory the running exe lives in -- the compiled equivalent of the ps originals $PSScriptRoot ($script:HelperRoot in local_helper_server.ps1), used to find ffmpeg/ffprobe installed alongside the helper by the setup wizard.
        public static readonly string HelperRoot = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
        // bundled next to the helper exe by the setup wizard, not extracted from obs64.exe at runtime -- Icon.ExtractAssociatedIcon only ever returns a single low-res frame, so dock/toast/window icons would look blurry next to the real multi-resolution icon the setup exe uses.
        public static readonly string OBS_ICON_PATH = Path.Combine(HelperRoot, "obs-replaykit.ico");

        public static readonly string REPLAYKIT_TEMP_ROOT = Path.Combine(Path.GetTempPath(), "ReplayKit");
        public static readonly string THUMB_DIR = Path.Combine(REPLAYKIT_TEMP_ROOT, "thumbs");
        public static readonly string LOG_DIR = Path.Combine(REPLAYKIT_TEMP_ROOT, "logs");
        public static readonly string HELPER_LOG_PATH = Path.Combine(LOG_DIR, "helper.log");
        public static readonly string UPLOAD_LOG_PATH = Path.Combine(LOG_DIR, "upload.log");
        public static readonly string COMPRESS_LOG_PATH = Path.Combine(LOG_DIR, "compress.log");
        public static readonly string COMPRESS_TMP_DIR = Path.Combine(REPLAYKIT_TEMP_ROOT, "compressed");
        public static readonly string SCRATCH_DIR = Path.Combine(REPLAYKIT_TEMP_ROOT, "scratch");
        public static readonly string UPDATE_DIR = Path.Combine(REPLAYKIT_TEMP_ROOT, "update");

        public static readonly string AUTH_DIR = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OBS Streamable Helper");
        public static readonly string AUTH_FILE = Path.Combine(AUTH_DIR, "auth.dat");

        public static readonly HashSet<string> ALLOWED_EXTS = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".mov" };
        public static readonly Dictionary<string, string> CONTENT_TYPES = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".mp4"] = "video/mp4",
            [".mkv"] = "video/x-matroska",
            [".mov"] = "video/quicktime",
        };

        // eagerly create the temp tree at process start, matching the ps original.
        public static void EnsureTempDirs()
        {
            foreach (var dir in new[] { REPLAYKIT_TEMP_ROOT, THUMB_DIR, LOG_DIR, COMPRESS_TMP_DIR, SCRATCH_DIR, UPDATE_DIR })
            {
                Directory.CreateDirectory(dir);
            }
        }

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
                    if (IsObsProcessName(proc.ProcessName) && proc.MainModule != null)
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
                        try { if (proc.MainModule != null) return proc.MainModule.FileName; }
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

        private static bool IsObsProcessName(string name) =>
            string.Equals(name, "obs64", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "obs", StringComparison.OrdinalIgnoreCase);

        // size limit for the current auth state. 0 means unlimited.
        public static long GetEffectiveUploadCap()
        {
            long cap = Server.State.Auth.SizeCap;
            return cap <= 0 ? 0 : cap;
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

    // process-wide access point for the one ServerState instance, mirroring the ps originals $script:State -- every module reaches shared state through Server.State the same way the scripts reached it through $script:State.
    internal static class Server
    {
        public static readonly ServerState State = new ServerState();
    }

    // one shared mutable-state instance, referenced by every connection handler and background task -- the c# equivalent of the ps original's single [hashtable]::Synchronized() seeded once into every pooled runspace. fields are grouped by the lock that protects them, matching the ps original's granularity (and its documented lock-ordering rule: ConfigLock before ClipsMetaLock, never the reverse) rather than redesigning the concurrency model, since a wrong redesign is a worse risk than keeping the same shape.
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

        // -- ClipsMetaLock -- a db write and a cache invalidation must be seen as one unit by concurrent readers, so this lock covers all of: cache body/sig/json/version, db cache, index cache, repair state, and the live folder watcher handle.
        public readonly object ClipsMetaLock = new object();
        public DateTime ClipsCacheAt;
        public List<JObject> ClipsCacheBody;
        public string ClipsCacheJson;
        public string ClipsCacheSig;
        public string ClipsCacheVersion = ""; // a signature string when populated, "" when cleared -- never numeric despite the name
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

        // -- UploadLock -- CpuSamplePercent/At are cache fields only ever touched while UploadLock is already held, no dedicated lock.
        public readonly object UploadLock = new object();
        public UploadJobRecord Upload = new UploadJobRecord { RequestId = "" };
        public readonly Dictionary<string, UploadJobRecord> Jobs = new Dictionary<string, UploadJobRecord>();
        public double CpuSamplePercent;
        public DateTime CpuSampleAt;

        // -- ThumbQueueLock --
        public readonly object ThumbQueueLock = new object();

        // written from both the accept loop and route handlers; read by the accept loop every iteration.
        public volatile bool Shutdown;

        // written by route handlers, read once by the shutdown path -- single write-then-read-once, no dedicated lock (matches the ps original's reliance on happens-before at process-exit time).
        public volatile bool ClearStreamableOnExit;
        // null means no restart pending; a non-null value is the obs executable path to relaunch once this process exits (the legacy fallback path used only when the detached relauncher in Routes.cs cant be started).
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

        // -- VideoApplyLock -- serializes the stop-outputs / SetVideoSettings (obs_reset_video) / restart-outputs
        // cycle. concurrent obs_reset_video calls deadlock obs's video graph -- 2026-08-28 a user hard-froze obs by
        // rapidly re-applying the downscale resolution.
        public readonly object VideoApplyLock = new object();
        public DateTime LastVideoApplyDoneUtc = DateTime.MinValue;

        // -- IpcLock -- streamed in from the native plugin (replaykit.cpp) over the OBSReplayKitIpc named pipe, replacing the old scratch-file handoff.
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
        public bool UpdateApplyInProgress;

        public string ObsExe;
    }

    // one upload/compress/trim-overwrite job. StartedAt/UpdatedAt are unix milliseconds (the exact shape the frontends /status json expects), not DateTime -- matches New-UploadJobRecord/Copy-UploadJobForJson in the ps original exactly. Cts/EncoderProcess/TempPath are internal-only (never serialized to the frontend, same as the ps originals now-eliminated processId/statusPath were) -- they replace those cross-process coordination handles with direct in-process ones now that upload/compress/trim run as Tasks in this same process instead of spawned children a status file had to bridge to.
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

        public System.Threading.CancellationTokenSource Cts;
        public Process EncoderProcess;
        public string TempPath;
    }

    internal sealed class AuthState
    {
        public bool SignedIn;
        public string Username = "";
        public string Plan = "";
        public long SizeCap;
        public int RetentionDays;
    }

    // an in-flight keyframe scan. the ps original tracked a spawned processs pid + a json status file it polled for; running the scan as an in-process Task replaces both with a direct Task<KeyframeScanResult> and a CancellationTokenSource that reproduces the same 5-minute hung-worker kill switch (see TrimKeyframeWorkerTimeoutMs in Trim.cs).
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

using System;
using System.IO;
using System.Security;
using System.Text;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // logging -- disabled by defualt for production, driven by the loggingEnabled key in the shared helper config so the lua entrypoint, this helper, and the upload/compress work it does in-process all agree on one setting. ported from obs_replaykit helper modules/20_config.ps1.
    internal static class Log
    {
        private static string GetLogPath(string area)
        {
            switch (area)
            {
                case "upload": return Constants.UPLOAD_LOG_PATH;
                case "compress": return Constants.COMPRESS_LOG_PATH;
                default: return Constants.HELPER_LOG_PATH;
            }
        }

        public static void Write(string msg, string area = "helper", string requestId = "")
        {
            if (!Server.State.LogEnabled) return;
            try
            {
                Directory.CreateDirectory(Constants.LOG_DIR);
                string rid = string.IsNullOrEmpty(requestId) ? "" : " request=" + requestId;
                string line = string.Format("[{0}] area={1}{2} {3}", DateTime.Now.ToString("o"), area, rid, msg);
                // add-content opens/closes the file per call -- concurrent writers from pool threads can hit a sharing violation without this, and this is the single hottest call site in the helper.
                lock (Server.State.LogLock)
                {
                    try { File.AppendAllText(GetLogPath(area), line + Environment.NewLine); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                }
            }
            catch { }
        }
    }

    internal static class AppConfig
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        // runs once at helper startup, before this session has written a line -- clears *.log files left from prior sessions so debug logging left on overnight doesnt slowly fill %TEMP%. gated on its own setting rather than LogEnabled, since old logs from a session where logging WAS on still need cleaning up after the user turns it back off.
        public static void ClearLogsAtStartup()
        {
            try
            {
                var settings = ReplaykitSettings.ReadSettings();
                var flag = settings?["autoDeleteLogsOnLaunch"];
                if (flag == null || !flag.Value<bool>()) return;
                if (!Directory.Exists(Constants.LOG_DIR)) return;
                foreach (var file in Directory.GetFiles(Constants.LOG_DIR, "*.log"))
                {
                    try { File.Delete(file); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                }
            }
            catch { }
        }

        // utf-8 (no bom) text writer, thru a per-call-unique temp file in the scratch dir then an atomic replace, so a concurrent reader never sees a half-written file and a crash between the write and the rename never leaves a stray .tmp sitting next to a real file. same pattern the upload/compress status files use.
        public static void WriteUtf8(string path, string text)
        {
            Directory.CreateDirectory(Constants.SCRATCH_DIR);
            string tmp = Path.Combine(Constants.SCRATCH_DIR, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(tmp, text, Utf8NoBom);
            string destVol = Path.GetPathRoot(path);
            string tmpVol = Path.GetPathRoot(tmp);
            if (string.Equals(destVol, tmpVol, StringComparison.OrdinalIgnoreCase))
            {
                Native.MoveFileReplace(tmp, path);
            }
            else
            {
                // different volume -- stage on the destinations own volume first so the final replace is still a same-volume atomic rename instead of a copy+delete that can leave $path half-written on crash.
                string sideTemp = Path.Combine(Path.GetDirectoryName(path), "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
                File.Copy(tmp, sideTemp, true);
                Native.MoveFileReplace(sideTemp, path);
                try { File.Delete(tmp); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }

        public static string GetUserProfile()
        {
            string v = Environment.GetEnvironmentVariable("USERPROFILE");
            return string.IsNullOrEmpty(v) ? @"C:\Users\Default" : v;
        }

        public static string GetDefaultClipDir() => Path.Combine(GetUserProfile(), "Pictures", "Videos");

        // dock html lives under obs replaykits consolidated runtime tree inside obs-studios config root. prefer %appdata% becuase roaming profiles can redirect it to a non-default location; fall back to the userprofile default for shells where the env var isnt set.
        public static string GetDefaultDockDir()
        {
            string appData = Environment.GetEnvironmentVariable("APPDATA");
            if (!string.IsNullOrEmpty(appData)) return Path.Combine(appData, "obs-studio", "obs-replayKit", "obs-custom-dock");
            return Path.Combine(GetUserProfile(), "AppData", "Roaming", "obs-studio", "obs-replayKit", "obs-custom-dock");
        }

        public static string ResolveDir(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            try { return Path.GetFullPath(value); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is SecurityException) { return fallback; }
        }

        public static string GetScriptDir() => Server.State.Config?["scriptDir"]?.Value<string>();
        public static string GetClipDir() => ResolveDir(Server.State.Config?["clipDir"]?.Value<string>(), GetDefaultClipDir());
        public static string GetDockDir() => GetDefaultDockDir();
        public static string GetDbPath() => Path.Combine(GetScriptDir(), "clips_db.json");
        public static string GetClipIndexPath() => Path.Combine(GetScriptDir(), "clips_index.json");

        public static string GetReplayKitUserStateDir()
        {
            string local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (!string.IsNullOrEmpty(local)) return Path.Combine(local, "OBS ReplayKit");
            return Path.Combine(GetUserProfile(), "AppData", "Local", "OBS ReplayKit");
        }
        public static string GetClipUiStatePath() => Path.Combine(GetReplayKitUserStateDir(), "clips_state.json");

        public static void ClearClipsCache()
        {
            lock (Server.State.ClipsMetaLock)
            {
                Server.State.ClipsCacheAt = DateTime.MinValue;
                Server.State.ClipsCacheSig = "";
                Server.State.ClipsCacheVersion = "";
                Server.State.ClipsCacheJson = "";
            }
        }

        public static void StopClipFolderWatcher()
        {
            lock (Server.State.ClipsMetaLock)
            {
                if (Server.State.ClipWatcher != null)
                {
                    try
                    {
                        Server.State.ClipWatcher.EnableRaisingEvents = false;
                        Server.State.ClipWatcher.Dispose();
                    }
                    catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException) { }
                }
                Server.State.ClipWatcher = null;
                Server.State.ClipWatcherPath = "";
            }
        }

        public static void StartClipFolderWatcher()
        {
            string root = ResolveDir(GetClipDir(), GetDefaultClipDir());
            try { root = Path.GetFullPath(root); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is SecurityException) { return; }

            // Stop-ClipFolderWatcher below re-enters the same lock -- Monitor (what `lock` compiles to) is reentrant per-thread so this nests safely.
            lock (Server.State.ClipsMetaLock)
            {
                if (Server.State.ClipWatcher != null && Server.State.ClipWatcherPath == root) return;
                StopClipFolderWatcher();
                if (!Directory.Exists(root)) return;
                try
                {
                    var watcher = new FileSystemWatcher
                    {
                        Path = root,
                        Filter = "*.*",
                        IncludeSubdirectories = false,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime
                    };
                    watcher.Created += OnClipFolderChanged;
                    watcher.Changed += OnClipFolderChanged;
                    watcher.Deleted += OnClipFolderChanged;
                    watcher.Renamed += OnClipFolderRenamed;
                    watcher.EnableRaisingEvents = true;
                    Server.State.ClipWatcher = watcher;
                    Server.State.ClipWatcherPath = root;
                }
                catch (Exception ex)
                {
                    StopClipFolderWatcher();
                    Log.Write("Clip watcher disabled for '" + root + "': " + ex.Message);
                }
            }
        }

        // runs on a .net threadpool thread, a real 3rd execution context beyond the accept loop and the connection pool -- needs the lock same as everything else.
        private static void OnClipFolderChanged(object sender, FileSystemEventArgs e) => InvalidateClipsCacheFromWatcher();
        private static void OnClipFolderRenamed(object sender, RenamedEventArgs e) => InvalidateClipsCacheFromWatcher();

        private static void InvalidateClipsCacheFromWatcher()
        {
            lock (Server.State.ClipsMetaLock)
            {
                Server.State.ClipsCacheAt = DateTime.MinValue;
                Server.State.ClipsCacheSig = "";
                Server.State.ClipsCacheVersion = "";
                Server.State.ClipsCacheJson = "";
                Server.State.ClipIndexRepairQueued = true;
                Server.State.ClipIndexRepairAt = DateTime.MinValue;
            }
        }

        // re-reads when mtime changes so a script_update in obs is picked up without restarting the helper. the plain (non-locked) check above the lock is the common case cost: one file-info compare, nothing more, since this runs at the top of every request.
        public static void LoadConfig()
        {
            try
            {
                var fi = new FileInfo(Server.State.ConfigPath);
                if (!fi.Exists) return;
                if (fi.LastWriteTimeUtc == Server.State.ConfigMTime) return;
                lock (Server.State.ConfigLock)
                {
                    // re-check now that we hold the lock -- another thread may have already reloaded while we were waiting to get in here.
                    if (fi.LastWriteTimeUtc == Server.State.ConfigMTime) return;
                    string text = File.ReadAllText(Server.State.ConfigPath);
                    if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
                    var obj = JObject.Parse(text);
                    var loggingToken = obj["loggingEnabled"];
                    Server.State.LogEnabled = loggingToken != null ? loggingToken.Value<bool>() : Constants.DEFAULT_LOG_ENABLED;
                    Server.State.Config = obj;
                    Server.State.ConfigMTime = fi.LastWriteTimeUtc;
                    // ConfigLock is always taken before ClipsMetaLock, never the reverse -- see the comment on ServerState in State.cs.
                    lock (Server.State.ClipsMetaLock)
                    {
                        ClearClipsCache();
                        Server.State.ClipsDbCache = null;
                        Server.State.ClipsDbCacheSig = "";
                        Server.State.ClipIndexCache = null;
                        Server.State.ClipIndexCacheSig = "";
                        Server.State.ClipIndexRepairQueued = true;
                        StartClipFolderWatcher();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Load-Config failed: " + ex.Message);
            }
        }
    }
}

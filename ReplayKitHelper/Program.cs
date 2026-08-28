using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // entry point: normal launch binds the loopback listener and runs the accept loop; a hidden --transcode-poll
    // mode instead runs a single detached poll worker (see TranscodePollWorker) and exits. ported from
    // obs_replaykit helper local_helper_server.ps1 + modules/90_runtime.ps1. the ps original's elaborate
    // runspace-pool/InitialSessionState setup (register every dot-sourced function + read-only constant into a
    // pool so a brand-new runspace can call Handle-Connection without re-dot-sourcing 21 files per connection) has
    // no equivalent here -- this process is already a real multithreaded .net app, so each accepted connection is
    // simply Task.Run onto the thread pool.
    internal static class Program
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);
        private const uint HANDLE_FLAG_INHERIT = 0x1;

        private static Mutex _singletonMutex;
        private static TcpListener _listener;
        private static readonly object CrashLogLock = new object();

        private static int Main(string[] args)
        {
            InstallCrashReporter();
            if (args.Length > 0 && string.Equals(args[0], "--transcode-poll", StringComparison.OrdinalIgnoreCase))
                return RunTranscodePoll(args);
            return RunServer(args);
        }

        private static void InstallCrashReporter()
        {
            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
                WriteCrashReport("helper_unhandled_exception", e.ExceptionObject, e.IsTerminating);
            TaskScheduler.UnobservedTaskException += (sender, e) =>
            {
                WriteCrashReport("helper_unobserved_task_exception", e.Exception, false);
                e.SetObserved();
            };
        }

        private static void WriteCrashReport(string kind, object exception, bool terminating)
        {
            try
            {
                string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (string.IsNullOrWhiteSpace(root)) return;
                string directory = Path.Combine(root, "obs-studio", "crashes", "replaykit");
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "replaykit-helper.log");
                string message = exception == null ? "(no exception object)" : exception.ToString();
                lock (CrashLogLock)
                {
                    File.AppendAllText(path, "[" + DateTime.UtcNow.ToString("o") + "] kind=" + kind +
                        " terminating=" + terminating + Environment.NewLine + message + Environment.NewLine);
                }
            }
            catch { }
        }

        private static string GetNamedArg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            }
            return "";
        }

        private static int RunTranscodePoll(string[] args)
        {
            string shortcode = GetNamedArg(args, "-Shortcode");
            string clipName = GetNamedArg(args, "-ClipName");
            string dbPath = GetNamedArg(args, "-DbPath");
            string api = GetNamedArg(args, "-Api");
            string logPath = GetNamedArg(args, "-LogPath");
            string cookieJar = GetNamedArg(args, "-CookieJar");
            return TranscodePollWorker.Run(shortcode, clipName, dbPath, api, logPath, cookieJar);
        }

        private static int RunServer(string[] args)
        {
            string configPath = GetNamedArg(args, "-ConfigPath");
            if (string.IsNullOrWhiteSpace(configPath))
            {
                Console.Error.WriteLine("Missing required -ConfigPath argument.");
                return 1;
            }
            Server.State.ConfigPath = configPath;
            AppConfig.LoadConfig();
            AppConfig.ClearLogsAtStartup();

            if (!AcquireSingleton()) return 0;

            try { UploadState.ClearStaleCompressedTempFiles(); } catch (Exception ex) { Log.Write("ClearStaleCompressedTempFiles: " + ex.Message); }
            try { Compression.GetHelperCapabilities(refresh: true); } catch (Exception ex) { Log.Write("GetHelperCapabilities: " + ex.Message); }

            int port = Server.State.Config?["port"]?.Value<int?>() ?? Constants.DEFAULT_PORT;
            _listener = BindListener(port);
            if (_listener == null)
            {
                Log.Write("Could not bind 127.0.0.1:" + port + " after takeover attempt -- giving up.");
                ReleaseSingleton();
                return 0;
            }
            Log.Write("ReplayKit helper listening on http://" + Constants.HOST_ADDR + ":" + port);

            // client half of the ipc pipe with the native plugin -- main-window + projector hwnds in, open-clips + allow-close out. connects when the plugin's server is up and reconnects on its own; every consumer degrades gracefully while it isn't.
            try { PipeClient.Start(); } catch (Exception ex) { Log.Write("PipeClient.Start: " + ex.Message); }

            // surface admin / parent-process info at startup so it's trivial to diagnose "vss not admin" later. with an elevation script that auto-relaunches obs, the lua can spawn the helper twice -- once under the pre-elevation non-admin obs (which then exits), and once under the post-elevation admin obs. the non-admin one can win port races and stick around as a useless orphan; the parent-process watchdog below kills the orphan when its obs goes away.
            bool isAdmin = false;
            try { isAdmin = BrowserCookies.TestIsAdmin(); } catch (Exception ex) { Log.Write("TestIsAdmin: " + ex.Message); }
            ParentWatchdog.Resolve();
            Log.Write("Helper PID=" + Process.GetCurrentProcess().Id + " admin=" + isAdmin + " parent=" + ParentWatchdog.ParentPid +
                " (" + ParentWatchdog.ParentName + ", started " + ParentWatchdog.ParentStartTime + ")");

            // open a real os handle on the parent so the watchdog can wait on a kernel signal instead of polling Get-Process. if this fails while we do have a known parent, we are at the wrong integrity level for it (or it is already dead) -- either way the right move is to exit now so the correctly-elevated helper can bind the port without us blocking it.
            if (!ParentWatchdog.OpenHandle())
            {
                Log.Write("OpenParentForSync(" + ParentWatchdog.ParentPid + ") returned NULL -- this helper has the wrong integrity level for its parent (or parent is dead). Exiting so the correctly-elevated helper can bind the port.");
                ReleaseSingleton();
                return 0;
            }

            try { DiscordProjector.StartAtStartup(); } catch (Exception ex) { Log.Write("warn: Discord projector startup threw: " + ex.Message); }

            // bind this process to a windows job object with kill-on-close. any worker child we spawn (compress/trim ffmpeg, etc) inherits the job automatically on windows 8+; when this process exits for any reason the kernel terminates every process still in the job, so orphaned ffmpeg workers cannot keep encoding into clip files no ui is watching.
            try
            {
                IntPtr jobHandle = Native.CreateKillOnCloseJob();
                Log.Write(jobHandle != IntPtr.Zero
                    ? "Job object created (kill-on-close); workers will be auto-terminated on helper exit."
                    : "warn: CreateJobObject returned NULL -- worker cleanup will rely on parent-PID watchdog only.");
            }
            catch (Exception ex) { Log.Write("warn: Job object setup threw: " + ex.Message); }

            try { AuthCore.RestoreAuthAtStartup(); } catch (Exception ex) { Log.Write("RestoreAuthAtStartup: " + ex.Message); }
            try { ReplaykitSettings.ResetHotkeyCaptureSignalAtStartup(); } catch (Exception ex) { Log.Write("ResetHotkeyCaptureSignalAtStartup: " + ex.Message); }

            RunAcceptLoop();

            Shutdown();
            return 0;
        }

        // named system mutex held for this process's lifetime so a second helper bails out immediately instead of
        // fighting over the port -- Tools -> Scripts reload occasionally leaves the old helper alive (blocked on a
        // slow operation when /shutdown arrives, or com-apartment cleanup keeps the launcher process around after
        // the hosted process already exited). the os releases the mutex automatically on process exit either way.
        private static bool AcquireSingleton()
        {
            try
            {
                _singletonMutex = new Mutex(false, "Local\\OBSReplayKit_Helper_Singleton");
                bool owned;
                try { owned = _singletonMutex.WaitOne(0); }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned)
                {
                    Log.Write("Singleton mutex held by another helper; asking it to /shutdown then retrying.");
                    TryPostShutdown(Server.State.Config?["port"]?.Value<int?>() ?? Constants.DEFAULT_PORT);
                    try { owned = _singletonMutex.WaitOne(3000); }
                    catch (AbandonedMutexException) { owned = true; }
                    if (!owned)
                    {
                        Log.Write("Singleton mutex still held after 3s; exiting to avoid a second OBS ReplayKit instance.");
                        return false;
                    }
                    Log.Write("Acquired singleton mutex after the prior helper released it.");
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Singleton mutex setup failed: " + ex.Message + ". Continuing without singleton protection.");
                return true;
            }
        }

        private static void ReleaseSingleton()
        {
            if (_singletonMutex == null) return;
            try { _singletonMutex.ReleaseMutex(); } catch (Exception ex) when (ex is ApplicationException || ex is ObjectDisposedException) { }
            try { _singletonMutex.Close(); } catch (ObjectDisposedException) { }
        }

        private static void TryPostShutdown(int port)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/shutdown");
                req.Method = "POST";
                req.Timeout = 1500;
                using (var resp = req.GetResponse()) { }
            }
            catch (WebException) { } catch (IOException) { }
        }

        // try to bind the listener. if another helper is already on the port (for example, a previous helper that
        // did not see /shutdown), post /shutdown to it, wait briefly, then retry. this is what makes a script
        // reload actually pick up new helper code instead of silently leaving the old one in place.
        private static TcpListener NewListener(int port)
        {
            try
            {
                var l = new TcpListener(IPAddress.Loopback, port);
                // ReuseAddress lets us bind a port lingering in TIME_WAIT after a clean restart.
                l.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                l.Start();
                // clear HANDLE_FLAG_INHERIT on the underlying socket handle. without this, any child process this helper spawns (or any process that inherits our handle table via CreateProcess with bInheritHandles=true) duplicates this handle. when the helper dies abruptly, the kernel cannot gc the listening socket until every duplicate is closed -- and if those duplicates are inside long-lived obs cef children, the listener stays parked under the dead pid forever. clearing inherit at bind time is the only documented prevention.
                try { SetHandleInformation(l.Server.Handle, HANDLE_FLAG_INHERIT, 0); }
                catch (Exception ex) { Log.Write("New-Listener: SetHandleInformation failed: " + ex.Message); }
                return l;
            }
            catch (SocketException) { return null; }
        }

        // finds the pid currently bound to the loopback port, if any -- used both to address the /shutdown
        // post-mortem (so a wedged helper cannot keep us locked out) and to log who we were fighting with.
        private static int GetPortListenerPid(int port)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netstat.exe",
                    Arguments = "-ano -p tcp",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };
                using (var proc = Process.Start(psi))
                {
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(3000);
                    foreach (var line in output.Split('\n'))
                    {
                        var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 5) continue;
                        if (!string.Equals(parts[0], "TCP", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!string.Equals(parts[3], "LISTENING", StringComparison.OrdinalIgnoreCase)) continue;
                        int colonIdx = parts[1].LastIndexOf(':');
                        if (colonIdx < 0 || !int.TryParse(parts[1].Substring(colonIdx + 1), out int localPort)) continue;
                        if (localPort != port) continue;
                        if (int.TryParse(parts[4], out int pid)) return pid;
                    }
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            {
                Log.Write("Get-PortListenerPid: " + ex.Message);
            }
            return 0;
        }

        private static TcpListener BindListener(int port)
        {
            var listener = NewListener(port);
            if (listener != null) return listener;

            int stalePid = GetPortListenerPid(port);
            Log.Write("Port " + port + " busy (PID " + stalePid + ") -- asking the existing helper to shut down.");
            TryPostShutdown(port);
            for (int i = 0; i < 8 && listener == null; i++)
            {
                Thread.Sleep(250);
                listener = NewListener(port);
            }
            if (listener == null && stalePid > 0 && stalePid != Process.GetCurrentProcess().Id)
            {
                // shutdown did not unstick it. force-kill the wedged listener -- we run under the same obs process tree, so we have rights to kill our predecessor.
                Log.Write("Force-killing wedged helper PID " + stalePid + ".");
                try { Process.GetProcessById(stalePid).Kill(); }
                catch (Exception ex) { Log.Write("Stop-Process PID " + stalePid + " failed: " + ex.Message); }
                for (int i = 0; i < 16 && listener == null; i++)
                {
                    Thread.Sleep(250);
                    listener = NewListener(port);
                }
            }
            if (listener != null) Log.Write("Took over port " + port + " from the previous helper (PID " + stalePid + ").");
            return listener;
        }

        // single-threaded accept loop; Pending() lets us cooperatively check the shutdown flag every ~50ms instead
        // of blocking in AcceptTcpClient forever. also runs the parent-process watchdog: with the user's
        // auto-elevation flow we can end up spawned by a non-admin obs that then exits when its elevated
        // replacement takes over -- without this, the orphan helper happily holds the port forever.
        private static void RunAcceptLoop()
        {
            try
            {
                while (!Server.State.Shutdown)
                {
                    if (!ParentWatchdog.CheckAlive()) break;
                    if (!_listener.Pending())
                    {
                        if (ParentWatchdog.ExitedNow())
                        {
                            Log.Write("Parent terminated during idle (GetExitCodeProcess). Exiting.");
                            break;
                        }
                        try { DiscordProjector.KeepAlive(); } catch (Exception ex) { Log.Write("warn: Discord projector keep-alive threw: " + ex.Message); }
                        Thread.Sleep(50);
                        continue;
                    }
                    var client = _listener.AcceptTcpClient();
                    // hand the connection off to the thread pool instead of handling it inline -- this is the one line that makes the server multithreaded. Connection.HandleConnection already wraps its own body in try/catch/finally (logs handler errors, always closes the client), so nothing further is needed here to keep one bad connection from taking down the accept loop.
                    Task.Run(() => Connection.HandleConnection(client));
                }
            }
            catch (Exception ex)
            {
                Log.Write("accept loop error: " + ex.Message);
            }
        }

        // obs writes a run_<uuid> file under %appdata%\obs-studio\.sentinel on launch and deletes every lingering
        // one during its own shutdown handler on a clean exit -- a killed/crashed obs never reaches that code, so
        // the leftover file is what makes the next launch show the "crash detected" prompt. clearing it ourselves
        // the moment we know obs is actually gone heads that off regardless of how obs went down, including a raw
        // task manager "end task" that our own restart flow never gets a chance to see.
        private static void ClearObsCrashSentinel()
        {
            string sentinelDir = Path.Combine(Environment.GetEnvironmentVariable("APPDATA") ?? "", "obs-studio", ".sentinel");
            if (!Directory.Exists(sentinelDir)) return;
            foreach (var file in Directory.GetFiles(sentinelDir, "run_*"))
            {
                try
                {
                    File.Delete(file);
                    Log.Write("Clear-ObsCrashSentinel: removed " + Path.GetFileName(file));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Log.Write("Clear-ObsCrashSentinel: could not remove " + Path.GetFileName(file) + ": " + ex.Message);
                }
            }
        }

        // if /restart-obs-clean queued a cookie wipe, run it now while obs is exiting: poll for the cookies file
        // to become writable (obs-cef releases its exclusive lock as the obs-browser plugin shuts down) for up to
        // 20 seconds, then delete every streamable/google/facebook row so a lingering google session cannot
        // silently re-mint a streamable session on the next sign-in attempt.
        private static void ClearStreamableCookiesOnExit()
        {
            string obsCookies = Path.Combine(Environment.GetEnvironmentVariable("APPDATA") ?? "", "obs-studio", "plugin_config", "obs-browser", "Network", "Cookies");
            Log.Write("ClearStreamableOnExit: waiting for OBS to release " + obsCookies);
            bool unlocked = false;
            for (int i = 0; i < 80; i++)
            {
                try
                {
                    using (var fs = new FileStream(obsCookies, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                    unlocked = true;
                    break;
                }
                catch (IOException) { Thread.Sleep(250); }
                catch (UnauthorizedAccessException) { Thread.Sleep(250); }
            }
            if (!unlocked)
            {
                Log.Write("ClearStreamableOnExit: timed out waiting for cookies file unlock; skipping.");
                return;
            }

            try
            {
                IntPtr db = NativeSqlite.OpenReadWrite(obsCookies);
                try
                {
                    IntPtr stmt = NativeSqlite.Prepare(db,
                        "DELETE FROM cookies WHERE " +
                        "host_key LIKE '%streamable.com' OR " +
                        "host_key LIKE '%google.com' OR " +
                        "host_key LIKE '%googleapis.com' OR " +
                        "host_key LIKE '%facebook.com' OR " +
                        "host_key LIKE '%facebook.net'");
                    int sr = NativeSqlite.Step(stmt);
                    NativeSqlite.Finalize(stmt);

                    long changed = 0;
                    IntPtr countStmt = NativeSqlite.Prepare(db, "SELECT changes()");
                    if (NativeSqlite.Step(countStmt) == NativeSqlite.SQLITE_ROW) changed = NativeSqlite.ColumnInt64(countStmt, 0);
                    NativeSqlite.Finalize(countStmt);
                    Log.Write("ClearStreamableOnExit: DELETE step rc=" + sr + " rows=" + changed + " (101=DONE expected).");
                }
                finally
                {
                    NativeSqlite.Close(db);
                }

                // also wipe local storage + indexeddb for those origins so any cached oauth state cannot replay either -- deleting the per-origin subdirectories under the cef profile is enough, since the next visit recreates fresh empty storage automatically.
                string idxDir = Path.Combine(Environment.GetEnvironmentVariable("APPDATA") ?? "", "obs-studio", "plugin_config", "obs-browser", "IndexedDB");
                if (Directory.Exists(idxDir))
                {
                    var pattern = new System.Text.RegularExpressions.Regex(@"^https?_(streamable\.com|accounts\.google\.com|google\.com|googleapis\.com|facebook\.com)_", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    foreach (var dir in Directory.GetDirectories(idxDir))
                    {
                        string name = Path.GetFileName(dir);
                        if (!pattern.IsMatch(name)) continue;
                        try
                        {
                            Directory.Delete(dir, true);
                            Log.Write("ClearStreamableOnExit: removed IndexedDB " + name);
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        {
                            Log.Write("ClearStreamableOnExit: IndexedDB " + name + ": " + ex.Message);
                        }
                    }
                }
                // local storage uses a single leveldb shared by all origins -- we cant surgically delete per-origin keys without a leveldb library. the cookies wipe is the critical part anyway; local storage doesnt carry oauth re-auth state on its own.
            }
            catch (Exception ex)
            {
                Log.Write("ClearStreamableOnExit: " + ex.Message);
            }
        }

        // restart broker: if /restart-obs-clean was the reason we are exiting, relaunch obs now that the
        // streamable cookies are gone. we are admin (restart-obs-clean requires it), so the new obs inherits our
        // admin token with no further uac prompt.
        private static void RelaunchObsAfterClean(string obsPath)
        {
            // same leftover-sentinel problem ClearObsCrashSentinel exists for -- we are about to relaunch right after our own force-kill, which skips obs's graceful-shutdown cleanup same as any other force-kill.
            try { ClearObsCrashSentinel(); } catch (Exception ex) { Log.Write("ClearObsCrashSentinel: " + ex.Message); }

            Log.Write("RestartAfterClean: launching " + obsPath);
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = obsPath,
                    Arguments = "--background-color=ff272a33 --default-background-color=ff272a33 --disable-direct-composition-video-overlays",
                    WorkingDirectory = Path.GetDirectoryName(obsPath),
                    UseShellExecute = true,
                };
                Process.Start(psi);
                Log.Write("RestartAfterClean: launch issued.");
            }
            catch (Exception ex)
            {
                Log.Write("RestartAfterClean: launch failed: " + ex.Message);
            }
        }

        private static void Shutdown()
        {
            try { PipeClient.Stop(); } catch { }
            try { AppConfig.StopClipFolderWatcher(); } catch (Exception ex) { Log.Write("StopClipFolderWatcher: " + ex.Message); }
            try { _listener?.Stop(); } catch (SocketException) { }
            if (ParentWatchdog.ParentDied)
            {
                try { ClearObsCrashSentinel(); } catch (Exception ex) { Log.Write("ClearObsCrashSentinel: " + ex.Message); }
            }

            if (Server.State.ClearStreamableOnExit)
            {
                try { ClearStreamableCookiesOnExit(); } catch (Exception ex) { Log.Write("ClearStreamableCookiesOnExit: " + ex.Message); }
            }

            string restartObsPath = Server.State.RestartAfterCleanObsPath;
            if (!string.IsNullOrEmpty(restartObsPath))
            {
                RelaunchObsAfterClean(restartObsPath);
            }

            ReleaseSingleton();
        }
    }
}

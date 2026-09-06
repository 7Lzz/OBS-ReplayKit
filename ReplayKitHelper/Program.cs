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
        private static Timer _crashSentinelTimer;

        private static int Main(string[] args)
        {
            InstallCrashReporter();
            if (args.Length > 0 && string.Equals(args[0], "--transcode-poll", StringComparison.OrdinalIgnoreCase))
                return RunTranscodePoll(args);
            if (args.Length > 0 && string.Equals(args[0], "--update-watchdog", StringComparison.OrdinalIgnoreCase))
                return UpdateWatchdog.Run(args);
            if (args.Length > 0 && string.Equals(args[0], "--update-startup-check", StringComparison.OrdinalIgnoreCase))
                return RunUpdateStartupCheck(args);
            try { return RunServer(args); }
            catch (Exception ex)
            {
                WriteStartupStatus(GetNamedArg(args, "-ConfigPath"), "failed", ex.Message);
                WriteCrashReport("helper_startup", ex, true);
                return 1;
            }
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
            bool quiet = GetNamedArg(args, "-Quiet") == "1";
            return TranscodePollWorker.Run(shortcode, clipName, dbPath, api, logPath, cookieJar, quiet);
        }

        private static int RunServer(string[] args)
        {
            string configPath = GetNamedArg(args, "-ConfigPath");
            if (string.IsNullOrWhiteSpace(configPath))
            {
                Console.Error.WriteLine("Missing required -ConfigPath argument.");
                return 1;
            }
            WriteStartupStatus(configPath, "starting", "");
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
                WriteStartupStatus(configPath, "failed", "Could not bind 127.0.0.1:" + port);
                Log.Write("Could not bind 127.0.0.1:" + port + " after takeover attempt -- giving up.");
                ReleaseSingleton();
                return 0;
            }
            WriteStartupStatus(configPath, "ready", "");
            Log.Write("ReplayKit helper listening on http://" + Constants.HOST_ADDR + ":" + port);

            // client half of the ipc pipe with the native plugin -- main-window + projector hwnds in, open-clips + allow-close out. connects when the plugin's server is up and reconnects on its own; every consumer degrades gracefully while it isn't.
            try { PipeClient.Start(); } catch (Exception ex) { Log.Write("PipeClient.Start: " + ex.Message); }

            // surface admin / parent-process info at startup so it's trivial to diagnose "vss not admin" later. with an elevation script that auto-relaunches obs, the lua can spawn the helper twice -- once under the pre-elevation non-admin obs (which then exits), and once under the post-elevation admin obs. the non-admin one can win port races and stick around as a useless orphan; the parent-process watchdog below kills the orphan when its obs goes away.
            bool isAdmin = false;
            try { isAdmin = BrowserCookies.TestIsAdmin(); } catch (Exception ex) { Log.Write("TestIsAdmin: " + ex.Message); }
            ParentWatchdog.Resolve();
            Log.Write("Helper PID=" + Process.GetCurrentProcess().Id + " admin=" + isAdmin + " parent=" + ParentWatchdog.ParentPid +
                " (" + ParentWatchdog.ParentName + ", started " + ParentWatchdog.ParentStartTime + ")");

            // Parent identity is diagnostic only. OBS can launch scripts through a
            // short-lived child process, so treating a parent-handle failure as a
            // reason to exit causes the local server to disappear after launch.
            ParentWatchdog.OpenHandle();

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
            try { ReplaykitSettings.RevertAbandonedOverlayPreviewAtStartup(); } catch (Exception ex) { Log.Write("RevertAbandonedOverlayPreviewAtStartup: " + ex.Message); }
            try { ReplaykitSettings.ApplyAppIconAtStartup(); } catch (Exception ex) { Log.Write("ApplyAppIconAtStartup: " + ex.Message); }
            try { ReplaykitSettings.EnsureObsRecordingFolderAtStartup(); } catch (Exception ex) { Log.Write("EnsureObsRecordingFolderAtStartup: " + ex.Message); }
            try { Upload.ResumeTranscodePollsAtStartup(); } catch (Exception ex) { Log.Write("ResumeTranscodePollsAtStartup: " + ex.Message); }
            try { ToastNotify.EnsureRegistered(Upload.ResolveToastIconPath()); } catch (Exception ex) { Log.Write("ToastNotify.EnsureRegistered: " + ex.Message); }
            try { Themes.EnsureObsInSync(ReplaykitSettings.Normalize(ReplaykitSettings.ReadSettings())); } catch (Exception ex) { Log.Write("Themes.EnsureObsInSync: " + ex.Message); }
            try { StartCrashSentinelSweep(); } catch (Exception ex) { Log.Write("StartCrashSentinelSweep: " + ex.Message); }

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
                l.ExclusiveAddressUse = true;
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
                    var stdout = proc.StandardOutput.ReadToEndAsync();
                    if (!proc.WaitForExit(3000))
                    {
                        try { proc.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
                        return 0;
                    }
                    string output = stdout.GetAwaiter().GetResult();
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

        // true unless the user has explicitly turned the setting off -- read fresh so a toggle takes effect within one sweep interval, no restart. fail-safe to true (suppress) if the settings file is momentarily unreadable.
        private static bool CrashPopupSuppressed()
        {
            try { return ReplaykitSettings.ReadSettings()["disableObsCrashPopup"]?.Value<bool>() ?? true; }
            catch { return true; }
        }

        // obs's "did not properly shut down" prompt fires from a leftover .sentinel\run_* before any module or this helper loads, so the current launch cant be caught -- but sweeping the file while we run means a hard crash / power loss leaves nothing behind and the NEXT launch is clean. obs writes its sentinel once at startup and never recreates it, so the 30s repeat is just insurance for a helper that started before obs wrote the file.
        private static void StartCrashSentinelSweep()
        {
            try { if (CrashPopupSuppressed()) ClearObsCrashSentinel(); }
            catch (Exception ex) { Log.Write("ClearObsCrashSentinel(startup): " + ex.Message); }
            _crashSentinelTimer = new Timer(_ =>
            {
                try { if (CrashPopupSuppressed()) ClearObsCrashSentinel(); }
                catch (Exception ex) { Log.Write("ClearObsCrashSentinel(sweep): " + ex.Message); }
            }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
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

        private static void WriteStartupStatus(string configPath, string state, string message)
        {
            if (string.IsNullOrWhiteSpace(configPath)) return;
            try
            {
                string path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath)), "helper_startup_status.json");
                var data = new JObject
                {
                    ["state"] = state,
                    ["message"] = message ?? "",
                    ["pid"] = Process.GetCurrentProcess().Id,
                    ["parentPid"] = ParentWatchdog.ParentPid,
                    ["parentName"] = ParentWatchdog.ParentName,
                    ["at"] = DateTime.UtcNow.ToString("o"),
                };
                File.WriteAllText(path, data.ToString());
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        private static int RunUpdateStartupCheck(string[] args)
        {
            int port = int.TryParse(GetNamedArg(args, "-Port"), out int value) ? value : Constants.DEFAULT_PORT;
            string origin = "http://127.0.0.1:" + port;
            for (int attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    var request = (HttpWebRequest)WebRequest.Create(origin + "/update/startup-check");
                    request.Method = "GET";
                    request.Timeout = 30000;
                    request.Headers["Origin"] = origin;
                    using (var response = (HttpWebResponse)request.GetResponse())
                    using (var reader = new StreamReader(response.GetResponseStream()))
                    {
                        var data = JObject.Parse(reader.ReadToEnd());
                        if (data.Value<bool?>("ok") != true || data.Value<bool?>("prompt") != true) return 0;
                        Update.OpenUpdatePromptWindow(data.Value<string>("latestVersion"));
                        return 0;
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("Startup update check attempt " + (attempt + 1) + " failed: " + ex.Message);
                    Thread.Sleep(3000);
                }
            }
            return 1;
        }

        // move any <name>.json.replaykit-pending staged by SetOverlaySceneFile onto the real scene collection. only safe once obs is gone (its own exit-save would clobber it otherwise) -- callers must run this post-exit, pre-relaunch.
        private static void ApplyPendingOverlayScene()
        {
            string scenesDir = Path.Combine(Environment.GetEnvironmentVariable("APPDATA") ?? "", "obs-studio", "basic", "scenes");
            if (!Directory.Exists(scenesDir)) return;
            foreach (var staged in Directory.GetFiles(scenesDir, "*.json.replaykit-pending"))
            {
                string target = staged.Substring(0, staged.Length - ".replaykit-pending".Length);
                try
                {
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(staged, target);
                    Log.Write("ApplyPendingOverlayScene: applied " + Path.GetFileName(target));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Log.Write("ApplyPendingOverlayScene: could not apply " + Path.GetFileName(target) + ": " + ex.Message);
                }
            }
        }

        // restart broker: if /restart-obs-clean was the reason we are exiting, relaunch obs now that the
        // streamable cookies are gone. we are admin (restart-obs-clean requires it), so the new obs inherits our
        // admin token with no further uac prompt.
        private static void RelaunchObsAfterClean(string obsPath)
        {
            // same leftover-sentinel problem ClearObsCrashSentinel exists for -- we are about to relaunch right after our own force-kill, which skips obs's graceful-shutdown cleanup same as any other force-kill.
            try { ClearObsCrashSentinel(); } catch (Exception ex) { Log.Write("ClearObsCrashSentinel: " + ex.Message); }
            // legacy-fallback twin of the move in restart_obs.ps1 -- apply a staged overlay scene edit now that obs is gone.
            try { ApplyPendingOverlayScene(); } catch (Exception ex) { Log.Write("ApplyPendingOverlayScene: " + ex.Message); }
            // same, for a staged theme change -- obs clobbered user.ini [Appearance] Theme= on its exit-save.
            try { Themes.ApplyPendingTheme(); } catch (Exception ex) { Log.Write("Themes.ApplyPendingTheme: " + ex.Message); }

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
            if (ParentWatchdog.ParentDied && CrashPopupSuppressed())
            {
                try { ClearObsCrashSentinel(); } catch (Exception ex) { Log.Write("ClearObsCrashSentinel: " + ex.Message); }
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

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using ReplayKitShared;

namespace ReplayKitHelper
{
    // entry point: a normal launch binds the loopback listener and runs the accept loop, each connection on the thread pool; the hidden --transcode-poll, --update-watchdog, --update-startup-check and --update-window modes run one job and exit
    internal static class Program
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);
        private const uint HANDLE_FLAG_INHERIT = 0x1;

        private static Mutex _singletonMutex;
        private static TcpListener _listener;
        private static readonly object CrashLogLock = new object();
        private static Timer _crashSentinelTimer;

        internal static int Main(string[] args)
        {
            System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.ThrowException);
            InstallCrashReporter();
            if (args.Length > 0 && args[0] == "--refresh-game-tag")
            {
                try
                {
                    Server.State.Config = new JObject { ["scriptDir"] = Constants.HelperRoot };
                    if (!long.TryParse(GetNamedArg(args, "-Bytes"), out long bytes) || !long.TryParse(GetNamedArg(args, "-Written"), out long written) || !long.TryParse(GetNamedArg(args, "-Created"), out long created)) return 1;
                    ClipGames.RefreshedRetag(GetNamedArg(args, "-File"), bytes, written, created);
                    return 0;
                }
                catch (Exception ex) { Console.Error.WriteLine("Game tag refresh failed: " + ex.GetType().Name); return 1; }
            }
            if (args.Length == 1 && args[0] == "--host-info")
            {
                Console.WriteLine(new JObject { ["hosted"] = ProcessHost.Hosted, ["helperRoot"] = Constants.HelperRoot,
                    ["assembly"] = ProcessHost.HelperExecutable, ["command"] = ProcessHost.CommandLine(new[] { "--host-info" }) }.ToString(Newtonsoft.Json.Formatting.None));
                return 0;
            }
            if (args.Length == 1 && string.Equals(args[0], "--prepare-theme", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    Server.State.Config = new JObject { ["scriptDir"] = Constants.HelperRoot };
                    Themes.PrepareForLaunch(ReplaykitSettings.ReadSettings());
                    return 0;
                }
                catch (Exception ex) { Console.Error.WriteLine("Theme preparation failed: " + ex.Message); return 1; }
            }
            if (args.Length > 0 && string.Equals(args[0], "--transcode-poll", StringComparison.OrdinalIgnoreCase))
                return RunTranscodePoll(args);
            if (args.Length > 0 && string.Equals(args[0], "--update-watchdog", StringComparison.OrdinalIgnoreCase))
                return UpdateWatchdog.Run(args);
            if (args.Length > 0 && string.Equals(args[0], "--update-startup-check", StringComparison.OrdinalIgnoreCase))
                return RunUpdateStartupCheck(args);
            if (args.Length > 0 && string.Equals(args[0], "--update-window", StringComparison.OrdinalIgnoreCase))
            {
                int windowPort = int.TryParse(GetNamedArg(args, "-Port"), out int wp) ? wp : Constants.DEFAULT_PORT;
                return UpdatePromptWindow.Run(GetNamedArg(args, "-Version"), windowPort, GetNamedArg(args, "-BgColor"));
            }
            try { return RunServer(args); }
            catch (Exception ex)
            {
                WriteStartupStatus(GetNamedArg(args, "-ConfigPath"), "failed", ex.Message);
                WriteCrashReport("helper_failure", ex, true);
                return 1;
            }
            finally { Shutdown(); }
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

        internal static void WriteCrashReport(string kind, object exception, bool terminating)
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
            // a poll started by an older helper knows nothing of these two, and does what it always did
            bool copyLink = GetNamedArg(args, "-CopyLink") != "0";
            bool notify = GetNamedArg(args, "-Notify") != "0";
            try { return TranscodePollWorker.Run(shortcode, clipName, dbPath, api, logPath, cookieJar, quiet, copyLink, notify); }
            finally
            {
                if (GetNamedArg(args, "-DeleteCookieJar") == "1" &&
                    string.Equals(Path.GetDirectoryName(cookieJar), Constants.SCRATCH_DIR, StringComparison.OrdinalIgnoreCase) &&
                    System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(cookieJar), @"^streamable_poll_[a-f0-9]{32}\.cookies\.txt$"))
                    try { File.Delete(cookieJar); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private static int RunServer(string[] args)
        {
            string configPath = GetNamedArg(args, "-ConfigPath");
            if (string.IsNullOrWhiteSpace(configPath))
            {
                Console.Error.WriteLine("Missing required -ConfigPath argument.");
                return 1;
            }
            if (!AcquireSingleton()) return 0;
            WriteStartupStatus(configPath, "starting", "");
            Server.State.ConfigPath = configPath;
            AppConfig.LoadConfig();
            AppConfig.ClearLogsAtStartup();

            // the settings and the saved state moved out of scripts\ into the replaykit folder itself; an install that still has them in the old places gets them moved before anything reads them, and until then the old copies keep being used
            try { StateLayout.Run(AppConfig.GetReplayKitRoot(), message => Log.Write(message)); }
            catch (Exception ex) { Log.Write("StateLayout.Run: " + ex.Message); }

            try { UploadState.ClearStaleCompressedTempFiles(); } catch (Exception ex) { Log.Write("ClearStaleCompressedTempFiles: " + ex.Message); }
            try { Update.ClearStaleUpdateFolders(); } catch (Exception ex) { Log.Write("ClearStaleUpdateFolders: " + ex.Message); }

            int port = Server.State.Config?["port"]?.Value<int?>() ?? Constants.DEFAULT_PORT;
            _listener = BindListener(port);
            WriteStartupStatus(configPath, "ready", "");
            Log.Write("ReplayKit helper listening on http://" + Constants.HOST_ADDR + ":" + port);

            // client half of the ipc pipe with the native plugin -- main-window + projector hwnds in, open-clips + allow-close out. connects when the plugins server is up and reconnects on its own; every consumer degrades gracefully while it isnt.
            try { PipeClient.Start(); } catch (Exception ex) { Log.Write("PipeClient.Start: " + ex.Message); }
            SteamGames.Refresh();

            // surface admin / parent-process info at startup so its trivial to diagnose "vss not admin" later. with an elevation script that auto-relaunches obs, the lua can spawn the helper twice -- once under the pre-elevation non-admin obs (which then exits), and once under the post-elevation admin obs. the non-admin one can win port races and stick around as a useless orphan; the parent-process watchdog below kills the orphan when its obs goes away.
            bool isAdmin = false;
            try { isAdmin = Elevation.IsAdmin(); } catch (Exception ex) { Log.Write("Elevation.IsAdmin: " + ex.Message); }
            ParentWatchdog.Resolve();
            Log.Write("Helper PID=" + Process.GetCurrentProcess().Id + " admin=" + isAdmin + " parent=" + ParentWatchdog.ParentPid +
                " (" + ParentWatchdog.ParentName + ", started " + ParentWatchdog.ParentStartTime + ")");

            // the parent handle is diagnostic only -- obs can host the lua through a short-lived child process, so a failed open must not make the helper exit
            ParentWatchdog.OpenHandle();

            // bind this process to a windows job object with kill-on-close. any worker child we spawn (compress/trim ffmpeg, etc) inherits the job automatically on windows 8+; when this process exits for any reason the kernel terminates every process still in the job, so orphaned ffmpeg workers cannot keep encoding into clip files no ui is watching.
            try
            {
                IntPtr jobHandle = Native.CreateKillOnCloseJob();
                Log.Write(jobHandle != IntPtr.Zero
                    ? "Job object created (kill-on-close); workers will be auto-terminated on helper exit."
                    : "warn: CreateJobObject returned NULL -- worker cleanup will rely on parent-PID watchdog only.");
            }
            catch (Exception ex) { Log.Write("warn: Job object setup threw: " + ex.Message); }

            // requests are answered from here on while the chores below run, since obs builds its pages only once the helper answers and the probe of the encoders alone (a test encode per graphics card encoder) held every page back for seconds
            Exception acceptFailure = null;
            var acceptThread = new Thread(() =>
            {
                try { RunAcceptLoop(); }
                catch (Exception ex) { acceptFailure = ex; Server.State.Shutdown = true; }
            }) { Name = "accept", IsBackground = true };
            acceptThread.Start();

            try { DiscordProjector.StartAtStartup(); } catch (Exception ex) { Log.Write("warn: Discord projector startup threw: " + ex.Message); }
            try { AuthCore.RestoreAuthAtStartup(); } catch (Exception ex) { Log.Write("RestoreAuthAtStartup: " + ex.Message); }
            try { ReplaykitSettings.ResetHotkeyCaptureSignalAtStartup(); } catch (Exception ex) { Log.Write("ResetHotkeyCaptureSignalAtStartup: " + ex.Message); }
            try { ReplaykitSettings.RevertAbandonedOverlayPreviewAtStartup(); } catch (Exception ex) { Log.Write("RevertAbandonedOverlayPreviewAtStartup: " + ex.Message); }
            try { ReplaykitSettings.ApplyAppIconAtStartup(); } catch (Exception ex) { Log.Write("ApplyAppIconAtStartup: " + ex.Message); }
            try { ReplaykitSettings.EnsureObsRecordingFolderAtStartup(); } catch (Exception ex) { Log.Write("EnsureObsRecordingFolderAtStartup: " + ex.Message); }
            try { ReplaykitSettings.EnsureObsVideoMatchesSettingsAtStartup(); } catch (Exception ex) { Log.Write("EnsureObsVideoMatchesSettingsAtStartup: " + ex.Message); }
            try { ReplaykitSettings.EnsureRecordingAudioProgramsAtStartup(); } catch (Exception ex) { Log.Write("EnsureRecordingAudioProgramsAtStartup: " + ex.Message); }
            try { ReplaykitSettings.CheckBongoCatAtStartup(); } catch (Exception ex) { Log.Write("CheckBongoCatAtStartup: " + ex.Message); }
            try { Upload.ResumeTranscodePollsAtStartup(); } catch (Exception ex) { Log.Write("ResumeTranscodePollsAtStartup: " + ex.Message); }
            try { ToastNotify.EnsureRegistered(Upload.ResolveToastIconPath()); } catch (Exception ex) { Log.Write("ToastNotify.EnsureRegistered: " + ex.Message); }
            try { Themes.EnsureObsInSync(ReplaykitSettings.Normalize(ReplaykitSettings.ReadSettings())); } catch (Exception ex) { Log.Write("Themes.EnsureObsInSync: " + ex.Message); }
            try { StartCrashSentinelSweep(); } catch (Exception ex) { Log.Write("StartCrashSentinelSweep: " + ex.Message); }
            try { IdleMemory.Start(); } catch (Exception ex) { Log.Write("IdleMemory.Start: " + ex.Message); }
            try { SetupExe.CheckAtStartup(); } catch (Exception ex) { Log.Write("SetupExe.CheckAtStartup: " + ex.Message); }
            try { Update.StartPolling(); } catch (Exception ex) { Log.Write("Update.StartPolling: " + ex.Message); }
            try { ClipFolderWorker.Start(); } catch (Exception ex) { Log.Write("ClipFolderWorker.Start: " + ex.Message); }
            // the probe of the encoders waits until obs and its pages have started, since its test encodes compete with them for the machine and anything that runs the tools only needs their paths (GetToolPaths); a compress or a save that needs the encoders before then runs the probe itself
            var probe = new Thread(() =>
            {
                Thread.Sleep(15000);
                if (Server.State.Shutdown) return;
                try { Compression.GetHelperCapabilities(); } catch (Exception ex) { Log.Write("GetHelperCapabilities: " + ex.Message); }
            }) { Name = "encoder-probe", IsBackground = true };
            probe.Start();

            acceptThread.Join();
            if (acceptFailure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(acceptFailure).Throw();
            return 0;
        }

        // named system mutex held for this processs lifetime so a second helper bails out immediately instead of fighting over the port -- Tools -> Scripts reload occasionally leaves the old helper alive (blocked on a slow operation when /shutdown arrives, or com-apartment cleanup keeps the launcher process around after the hosted process already exited). the os releases the mutex automatically on process exit either way.
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
                    Log.Write("Another helper owns the singleton; leaving that instance running.");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Cannot establish exclusive helper ownership.", ex);
            }
        }

        private static void ReleaseSingleton()
        {
            if (_singletonMutex == null) return;
            try { _singletonMutex.ReleaseMutex(); } catch (Exception ex) when (ex is ApplicationException || ex is ObjectDisposedException) { }
            try { _singletonMutex.Close(); } catch (ObjectDisposedException) { }
        }

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

        private static TcpListener BindListener(int port)
        {
            var listener = NewListener(port);
            if (listener == null) throw new IOException("Cannot bind 127.0.0.1:" + port + ". Close the application using that port, then restart ReplayKit.");
            return listener;
        }

        private static readonly SemaphoreSlim ConnectionSlots = new SemaphoreSlim(Constants.MAX_CONNECTION_THREADS);

        // single-threaded accept loop. the accept waits on its handle with a 50ms cap, so a connection is taken the moment it arrives (a Pending() check followed by a 50ms sleep added 25ms on average to every request) and the shutdown flag and the parent-process watchdog still get looked at every ~50ms: with the users auto-elevation flow we can end up spawned by a non-admin obs that then exits when its elevated replacement takes over -- without this, the orphan helper happily holds the port forever.
        private static void RunAcceptLoop()
        {
            try
            {
                IAsyncResult accept = null;
                var idleWork = Stopwatch.StartNew();
                while (!Server.State.Shutdown)
                {
                    if (!ParentWatchdog.CheckAlive()) break;
                    // on a clock rather than on idle passes, so steady traffic cannot hold them off
                    if (idleWork.ElapsedMilliseconds >= 50)
                    {
                        idleWork.Restart();
                        if (ParentWatchdog.ExitedNow())
                        {
                            Log.Write("Parent terminated during idle (GetExitCodeProcess). Exiting.");
                            break;
                        }
                        try { DiscordProjector.KeepAlive(); } catch (Exception ex) { Log.Write("warn: Discord projector keep-alive threw: " + ex.Message); }
                    }
                    if (accept == null) accept = _listener.BeginAcceptTcpClient(null, null);
                    if (!accept.AsyncWaitHandle.WaitOne(50)) continue;
                    var client = _listener.EndAcceptTcpClient(accept);
                    accept = null;
                    if (!ConnectionSlots.Wait(0)) { client.Close(); continue; }
                    Task.Run(() =>
                    {
                        try { Connection.HandleConnection(client); }
                        finally { ConnectionSlots.Release(); }
                    });
                }
            }
            catch (Exception ex)
            {
                throw new IOException("Helper accept loop failed.", ex);
            }
        }

        // true unless the user has explicitly turned the setting off -- read fresh so a toggle takes effect within one sweep interval, no restart. fail-safe to true (suppress) if the settings file is momentarily unreadable.
        private static bool CrashPopupSuppressed()
        {
            try { return ReplaykitSettings.ReadSettings()["disableObsCrashPopup"]?.Value<bool>() ?? true; }
            catch { return true; }
        }

        // obss "did not properly shut down" prompt fires from a leftover .sentinel\run_* before any module or this helper loads, so the current launch cant be caught -- but sweeping the file while we run means a hard crash / power loss leaves nothing behind and the NEXT launch is clean. obs writes its sentinel once at startup and never recreates it, so the 30s repeat is just insurance for a helper that started before obs wrote the file.
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

        // obs writes a run_<uuid> file under %appdata%\obs-studio\.sentinel on launch and deletes every lingering one during its own shutdown handler on a clean exit -- a killed/crashed obs never reaches that code, so the leftover file is what makes the next launch show the "crash detected" prompt. clearing it ourselves the moment we know obs is actually gone heads that off regardless of how obs went down, including a raw task manager "end task" that our own restart flow never gets a chance to see.
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
                // diagnosis only and read by nothing, so it lives with the logs and not in the install folder
                Directory.CreateDirectory(Constants.LOG_DIR);
                string path = Path.Combine(Constants.LOG_DIR, "helper_startup_status.json");
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
                        // The update route retains the download-size, GitHub HTTPS, and SHA-256 verification gates;
                        // the startup worker only removes the old Install/Later prompt before invoking that route.
                        var apply = (HttpWebRequest)WebRequest.Create(origin + "/update/apply");
                        apply.Method = "POST";
                        apply.Timeout = 180000;
                        apply.Headers["Origin"] = origin;
                        using (var applyResponse = (HttpWebResponse)apply.GetResponse())
                        using (var applyReader = new StreamReader(applyResponse.GetResponseStream()))
                        {
                            var result = JObject.Parse(applyReader.ReadToEnd());
                            if (result.Value<bool?>("ok") != true) Log.Write("Automatic update did not start: " + (result.Value<string>("message") ?? "unknown error"));
                        }
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

        // restart broker: if /restart-obs-clean was the reason we are exiting, relaunch obs now that the streamable cookies are gone. we are admin (restart-obs-clean requires it), so the new obs inherits our admin token with no further uac prompt.
        private static void RelaunchObsAfterClean(string obsPath)
        {
            // same leftover-sentinel problem ClearObsCrashSentinel exists for -- we are about to relaunch right after our own force-kill, which skips obss graceful-shutdown cleanup same as any other force-kill.
            try { ClearObsCrashSentinel(); } catch (Exception ex) { Log.Write("ClearObsCrashSentinel: " + ex.Message); }
            // the same move restart_obs.ps1 does on the detached path: apply a staged overlay scene edit now that obs is gone
            try { ApplyPendingOverlayScene(); } catch (Exception ex) { Log.Write("ApplyPendingOverlayScene: " + ex.Message); }
            // same, for a staged theme change -- obs clobbered user.ini [Appearance] Theme= on its exit-save.
            try { Themes.ApplyPendingTheme(); } catch (Exception ex) { Log.Write("Themes.ApplyPendingTheme: " + ex.Message); }

            Log.Write("RestartAfterClean: launching " + obsPath);
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = obsPath,
                    Arguments = "--background-color=ff272a33 --default-background-color=ff272a33 --disable-direct-composition-video-overlays --process-per-site --js-flags=--optimize-for-size",
                    WorkingDirectory = Path.GetDirectoryName(obsPath),
                    UseShellExecute = true,
                };
                Process.Start(psi)?.Dispose();
                Log.Write("RestartAfterClean: launch issued.");
            }
            catch (Exception ex)
            {
                Log.Write("RestartAfterClean: launch failed: " + ex.Message);
            }
        }

        private static void Shutdown()
        {
            Update.StopPolling();
            _crashSentinelTimer?.Dispose();
            IdleMemory.Stop();
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

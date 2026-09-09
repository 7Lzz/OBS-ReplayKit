using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // client half of the ipc pipe with the native plugin (replaykit.cpp), which is the server. replaces the old
    // scratch-file handoff (obsreplaykit_main_window.txt / obsreplaykit_projector_windows.txt / open_clips.command
    // / obs-allow-close). the plugin is the server because it outlives helper hot-swaps; this side just reconnects.
    // inbound (plugin -> helper): MAINWIN <hwnd>, PROJECTORS <csv>. outbound: OPENCLIPS, OPENSETTINGS, OPENOBSSETTINGS, OPENSETUP / OPENSETUPQUIET,
        // ALLOWCLOSE (+ ALLOWCLOSE_ACK back), SETICON <path|->, SETICONDOT <0|1>, RKICON <path>, CLIPSFULLSCREEN <0|1>, CONFIRM <kind>. newline-delimited utf8 text, one message per line.
    internal static class PipeClient
    {
        private const string PipeName = "OBSReplayKitIpc";

        private static Thread _thread;
        private static volatile bool _stop;
        private static readonly object WriteLock = new object();
        private static NamedPipeClientStream _stream;
        private static StreamWriter _writer;
        private static bool _setupWizardKicked;
        private static readonly ManualResetEventSlim AllowCloseAck = new ManualResetEventSlim(false);

        public static void Start()
        {
            if (_thread != null) return;
            _thread = new Thread(Loop) { IsBackground = true, Name = "ipc-pipe-client" };
            _thread.Start();
        }

        public static void Stop()
        {
            _stop = true;
            try { _stream?.Dispose(); } catch { }
        }

        private static void Loop()
        {
            var utf8 = new UTF8Encoding(false);
            while (!_stop && !Server.State.Shutdown)
            {
                try
                {
                    using (var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                    {
                        pipe.Connect(2000);
                        var reader = new StreamReader(pipe, utf8);
                        var writer = new StreamWriter(pipe, utf8) { AutoFlush = true, NewLine = "\n" };
                        lock (WriteLock) { _stream = pipe; _writer = writer; }
                        lock (Server.State.IpcLock) Server.State.IpcClientConnected = true;
                        Log.Write("IPC pipe connected to the tray plugin.");
                        // sync the appearance-tab icon + recording-dot toggle + branded fallback to a plugin that may have just (re)loaded
                        try
                        {
                            var norm = ReplaykitSettings.Normalize(ReplaykitSettings.ReadSettings());
                            SendRkIcon(File.Exists(Constants.OBS_ICON_PATH) ? Constants.OBS_ICON_PATH : "");
                            SendSetIcon(ReplaykitSettings.ResolveAppIconPath(norm));
                            SendSetIconDot(norm["appIconRecordingDot"]?.Value<bool>() ?? true);
                        }
                        catch (Exception ex) { Log.Write("IPC SETICON on connect: " + ex.Message); }

                        // first connect of this helper: run the update check, then open the first-run wizard if nothing is waiting to install. the dock-side triggers only fire once the obs window is on screen, so a launch straight to the tray would otherwise show neither.
                        if (!_setupWizardKicked)
                        {
                            _setupWizardKicked = true;
                            // off the pipe thread -- the check hits github, and this loop still has to get to its read
                            ThreadPool.QueueUserWorkItem(_ => KickFirstRunFlow());
                        }

                        string line;
                        while (!_stop && (line = reader.ReadLine()) != null)
                            Dispatch(line.Trim());
                    }
                }
                catch (Exception ex) when (ex is TimeoutException || ex is IOException || ex is UnauthorizedAccessException || ex is ObjectDisposedException)
                {
                    // plugin not up yet, or the connection dropped -- fall through to the retry sleep.
                }
                catch (Exception ex)
                {
                    Log.Write("IPC pipe client error: " + ex.Message);
                }
                finally
                {
                    lock (WriteLock) { _stream = null; _writer = null; }
                    lock (Server.State.IpcLock)
                    {
                        Server.State.IpcClientConnected = false;
                        Server.State.ProjectorHwnds = null;
                    }
                }
                if (!_stop && !Server.State.Shutdown) Thread.Sleep(1000);
            }
        }

        // an update outranks the wizard: setting obs up against a build that is about to be replaced wastes the users
        // time, and the install restarts obs out from under the wizard. the wizard flag stays set in settings either
        // way, so it opens on the next launch once the install has landed.
        private static void KickFirstRunFlow()
        {
            try
            {
                var status = Update.GetStartupUpdateStatus();
                if (status["prompt"]?.Value<bool>() ?? false)
                {
                    string version = status["latestVersion"]?.Value<string>() ?? "";
                    Log.Write("Startup update " + version + " is waiting; opening the updater instead of the setup wizard.");
                    Update.OpenUpdatePromptWindow(version);
                    return;
                }
            }
            catch (Exception ex) { Log.Write("Startup update check before setup: " + ex.Message); }

            try
            {
                if (!(ReplaykitSettings.GetSetupState()["pending"]?.Value<bool>() ?? false)) return;
                // quiet: obs is still coming up, so the wizard appears without yanking focus off the main window
                lock (Server.State.UpdateCheckLock) Server.State.SetupWizardOpened = true;
                TryWrite("OPENSETUPQUIET");
                Log.Write("First-run setup wizard is pending; asked the tray plugin to open it without focus.");
            }
            catch (Exception ex) { Log.Write("IPC OPENSETUP on connect: " + ex.Message); }
        }

        private static void Dispatch(string line)
        {
            if (line.Length == 0) return;
            int sp = line.IndexOf(' ');
            string verb = sp < 0 ? line : line.Substring(0, sp);
            string payload = sp < 0 ? "" : line.Substring(sp + 1);
            switch (verb)
            {
                case "MAINWIN":
                    if (long.TryParse(payload.Trim(), out long hwnd))
                        lock (Server.State.IpcLock) Server.State.ObsMainWindowHwnd = hwnd;
                    break;
                case "PROJECTORS":
                    var list = new List<long>();
                    foreach (var tok in payload.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                        if (long.TryParse(tok.Trim(), out long n) && n != 0) list.Add(n);
                    lock (Server.State.IpcLock)
                    {
                        Server.State.ProjectorHwnds = list;
                        Server.State.ProjectorHwndsAtUtc = DateTime.UtcNow;
                    }
                    break;
                case "ALLOWCLOSE_ACK":
                    AllowCloseAck.Set();
                    break;
            }
        }

        // tells the plugin's close-to-tray filter the next OBS WM_CLOSE is a real restart/exit. waits for the ack
        // so the close can't beat the message; returns false if the plugin isn't connected or never acks, in which
        // case the caller proceeds anyway (older bundle without the pipe -- same as before this signal existed).
        public static bool SendAllowCloseAndWait(int timeoutMs)
        {
            AllowCloseAck.Reset();
            if (!TryWrite("ALLOWCLOSE")) return false;
            return AllowCloseAck.Wait(timeoutMs);
        }

        public static void SendOpenClips() => TryWrite("OPENCLIPS");

        // opens the tray-plugin-owned Settings window. the docks own settings button routes thru here so it takes the same path as the tray row instead of a window.open popup that cef hands to the real browser.
        public static void SendOpenSettings() => TryWrite("OPENSETTINGS");

        // opens OBS's own Settings dialog (the merged controls dock's "OBS Studio Settings" button) -- only the plugin, inside obs's process, can trigger it.
        public static void SendOpenObsSettings() => TryWrite("OPENOBSSETTINGS");

        // opens the tray-plugin-owned first-run setup wizard window. fired once per helper process on the first pipe connect when setupWizardPending is set (works even if obs launched to the tray), and again by the dock once the obs window is on screen -- ShowSetup is a singleton so the second call just focuses it.
        public static void SendOpenSetup() => TryWrite("OPENSETUP");

        // tells the plugin which icon to put on obs's main window + taskbar + system tray. "-" restores obs's own icon.
        // sent on every appearance-tab change and once on each (re)connect so a freshly-loaded plugin syncs.
        public static void SendSetIcon(string iconPath) => TryWrite("SETICON " + (string.IsNullOrEmpty(iconPath) ? "-" : iconPath));

        // whether the plugin overlays the red recording dot on a custom icon while recording / replay buffer is active.
        public static void SendSetIconDot(bool on) => TryWrite("SETICONDOT " + (on ? "1" : "0"));

        // the replaykit-branded .ico the plugin puts on our own windows (Clips, Settings) when appIcon is "default".
        public static void SendRkIcon(string icoPath) { if (!string.IsNullOrEmpty(icoPath)) TryWrite("RKICON " + icoPath); }

        // Prevent the tray plugin from persisting the temporary monitor-sized Clips geometry while video fullscreen is active.
        public static void SendClipsFullscreen(bool active) => TryWrite("CLIPSFULLSCREEN " + (active ? "1" : "0"));

        // asks the plugin to draw a themed confirm over the whole dimmed obs window (a cef dock cannot paint past its own panel). the plugin runs the follow-up action itself on ok, so nothing comes back. kind is one short token, letters and dashes only.
        public static void SendUiConfirm(string kind) { if (!string.IsNullOrEmpty(kind)) TryWrite("CONFIRM " + kind); }

        private static bool TryWrite(string line)
        {
            lock (WriteLock)
            {
                if (_writer == null) return false;
                try { _writer.WriteLine(line); return true; }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException) { return false; }
            }
        }
    }
}

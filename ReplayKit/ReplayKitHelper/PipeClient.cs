using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // client half of the ipc pipe with the native plugin (core/ipc_pipe.cpp), which is the server. replaces the old scratch-file handoff (obsreplaykit_main_window.txt / obsreplaykit_projector_windows.txt / open_clips.command / obs-allow-close). the plugin is the server because it outlives helper hot-swaps; this side just reconnects. inbound (plugin -> helper): MAINWIN <hwnd>, PROJECTORS <csv>. outbound: OPENCLIPS, OPENSETTINGS [page], OPENSTORAGE, CLOSESTORAGE, OPENOBSSETTINGS, OPENSETUP / OPENSETUPQUIET,
        // ALLOWCLOSE (+ ALLOWCLOSE_ACK back), SETICON <path|->, SETICONDOT <0|1>, RKICON <path>, CLIPSFULLSCREEN <0|1>, CLOSEVIEW <settings|setup|clips>, VIEWSHOWN <home|clips|settings|setup>, SHELLNAV <back|forward>, SHELLPONG <n>, CONFIRM <kind>. newline-delimited utf8 text, one message per line.
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
        private static readonly ManualResetEventSlim OutputsReleasedAck = new ManualResetEventSlim(false);
        private static readonly object ReleaseOutputsLock = new object();

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
                        Log.Write("IPC pipe connected to the native plugin.");
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

        // an update outranks the wizard: setting obs up against a build that is about to be replaced wastes the users time, and the install restarts obs out from under the wizard. the wizard flag stays set in settings either way, so it opens on the next launch once the install has landed.
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
                lock (Server.State.UpdateCheckLock) Server.State.SetupWizardOpened = true;
                SendOpenSetup();
                Log.Write("First-run setup wizard is pending; asked the native plugin to open it and bring OBS forward.");
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
                case "RELEASEOUTPUTS_ACK":
                    OutputsReleasedAck.Set();
                    break;
            }
        }

        // tells the plugins close-to-tray filter the next OBS WM_CLOSE is a real restart/exit. waits for the ack so the close cant beat the message; returns false if the plugin isnt connected or never acks, in which case the caller proceeds anyway (older bundle without the pipe -- same as before this signal existed).
        public static bool SendAllowCloseAndWait(int timeoutMs)
        {
            AllowCloseAck.Reset();
            if (!TryWrite("ALLOWCLOSE")) return false;
            return AllowCloseAck.Wait(timeoutMs);
        }

        // obs refuses to start an output, without an error, while its settings dialog exists, and the plugin keeps that dialog built (parked) after the obs settings view is left; this has the plugin drop a parked one and waits until obs takes outputs again. false when the plugin is not connected or never answers, and the start then goes ahead as it would have
        public static bool ReleaseObsOutputsAndWait(int timeoutMs)
        {
            lock (ReleaseOutputsLock)
            {
                OutputsReleasedAck.Reset();
                if (!TryWrite("RELEASEOUTPUTS")) return false;
                bool acked = OutputsReleasedAck.Wait(timeoutMs);
                if (!acked) Log.Write("RELEASEOUTPUTS: no answer from the plugin in " + timeoutMs + " ms");
                return acked;
            }
        }

        public static void SendOpenClips() => TryWrite("OPENCLIPS");

        // opens the plugin-owned Settings window. the docks own settings button routes thru here so it takes the same path as the tray row instead of a window.open popup that cef hands to the real browser. page is a tab of the settings (lowercase letters only), or empty to open it where it was left.
        public static void SendOpenSettings(string page = null) => TryWrite(string.IsNullOrEmpty(page) ? "OPENSETTINGS" : "OPENSETTINGS " + page);

        // the storage card of the clean layout: the plugin owns its window and the dim behind it, so opening and closing it are messages
        public static void SendOpenStorage() => TryWrite("OPENSTORAGE");

        public static void SendCloseStorage() => TryWrite("CLOSESTORAGE");

        // opens OBSs own Settings dialog (the merged controls docks "OBS Studio Settings" button) -- only the plugin, inside obss process, can trigger it.
        public static void SendOpenObsSettings() => TryWrite("OPENOBSSETTINGS");

        // a command of the local command channel for the plugin: an id to match its answer to, the name without the plugin prefix, and the arguments as one line of json
        public static bool SendSdk(int id, string command, string argsJson) => TryWrite("SDK " + id + " " + command + " " + argsJson);

        // one of the obs menu bar menus (File, Edit, ...) by its position, dropped open at the pointer
        public static void SendObsMenu(int index) { if (index >= 0 && index < 20) TryWrite("OBSMENU " + index); }

        // the plugin groups WASD Overlay + Mouse Overlay of the current scene into Group, which obs-websocket cannot do
        public static void SendGroupInputOverlay() => TryWrite("GROUPINPUTOVERLAY");

        // opens the plugin-owned first-run setup wizard window. fired once per helper process on the first pipe connect when setupWizardPending is set (works even if obs launched to the tray), and again by the dock once the obs window is on screen -- ShowSetup is a singleton so the second call just focuses it.
        public static void SendOpenSetup() => TryWrite("OPENSETUP");

        // tells the plugin which icon to put on obss main window + taskbar + system tray. "-" restores obss own icon. sent on every appearance-tab change and once on each (re)connect so a freshly-loaded plugin syncs.
        public static void SendSetIcon(string iconPath) => TryWrite("SETICON " + (string.IsNullOrEmpty(iconPath) ? "-" : iconPath));

        // whether the plugin overlays the red recording dot on a custom icon while recording / replay buffer is active.
        public static void SendSetIconDot(bool on) => TryWrite("SETICONDOT " + (on ? "1" : "0"));

        // the replaykit-branded .ico the plugin puts on our own windows (Clips, Settings) when appIcon is "default".
        public static void SendRkIcon(string icoPath) { if (!string.IsNullOrEmpty(icoPath)) TryWrite("RKICON " + icoPath); }

        // prevent the native plugin from persisting the temporary monitor-sized Clips geometry while video fullscreen is active.
        public static void SendClipsFullscreen(bool active) => TryWrite("CLIPSFULLSCREEN " + (active ? "1" : "0"));

        // a page the clean layout embeds in the obs window asks to close itself; view is one of settings / setup / clips
        public static void SendCloseView(string view) { if (!string.IsNullOrEmpty(view)) TryWrite("CLOSEVIEW " + view); }

        // the shell page reports which view it now shows, so the plugin can lift the browser above the obs preview at the right moment
        public static void SendViewShown(string view) { if (!string.IsNullOrEmpty(view)) TryWrite("VIEWSHOWN " + view); }

        // a page of the shell saw alt + an arrow or a side button of the mouse: the plugin keeps the history of the views and takes the step
        public static void SendShellNav(string direction) { if (direction == "back" || direction == "forward") TryWrite("SHELLNAV " + direction); }

        // the settings page has unsaved edits or not, and its answer to the prompt the plugin raised when another view was picked meanwhile
        // the id is checked by the route already, the plugin checks it again before it reaches a script
        public static void SendNotificationReader(string id) => TryWrite("NOTIFYREADER " + id);
        public static void SendNotificationReaderClose() => TryWrite("NOTIFYREADERCLOSE");

        public static void SendSettingsDirty(bool dirty) => TryWrite("SETTINGSDIRTY " + (dirty ? "1" : "0"));
        public static void SendSettingsLeave(string view) { if (view == "home" || view == "clips" || view == "setup" || view == "projector") TryWrite("SETTINGSLEAVE " + view); }

        // the shell page answers a ping of the plugin, so the plugin learns the page still runs; a page whose renderer died says nothing
        public static void SendShellPong(int n) { if (n > 0) TryWrite("SHELLPONG " + n.ToString(System.Globalization.CultureInfo.InvariantCulture)); }

        // the replaykit settings were just saved: an obs settings dialog the plugin has kept built and parked holds what obs held before, so the plugin drops it
        public static void SendSettingsApplied() => TryWrite("SETTINGSAPPLIED");

        // the plugin reads the theme colours on a one second poll; this makes it read them now, for a theme being previewed
        public static void SendPollSettings() => TryWrite("POLLSETTINGS");

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

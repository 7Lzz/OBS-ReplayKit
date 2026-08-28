using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace ReplayKitHelper
{
    // client half of the ipc pipe with the native plugin (replaykit.cpp), which is the server. replaces the old
    // scratch-file handoff (obsreplaykit_main_window.txt / obsreplaykit_projector_windows.txt / open_clips.command
    // / obs-allow-close). the plugin is the server because it outlives helper hot-swaps; this side just reconnects.
    // inbound (plugin -> helper): MAINWIN <hwnd>, PROJECTORS <csv>. outbound: OPENCLIPS, ALLOWCLOSE (+ ALLOWCLOSE_ACK
    // back). newline-delimited utf8 text, one message per line.
    internal static class PipeClient
    {
        private const string PipeName = "OBSReplayKitIpc";

        private static Thread _thread;
        private static volatile bool _stop;
        private static readonly object WriteLock = new object();
        private static NamedPipeClientStream _stream;
        private static StreamWriter _writer;
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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Newtonsoft.Json.Linq;
using ReplayKitShared;

namespace ReplayKitHelper
{
    // whether discord streams something other than the share window with its sound: a whole screen or another window already carries the sound obs monitors for the share window, so viewers would hear it twice. discord has no api for its own go live, so this reads the voice engine log every discord client keeps in its logs folder, which writes each stream start and stop, the process whose sound the stream takes (pid 1 for a whole screen) and the stats of the stream every 10 s; while that holds, the plugin keeps desktop audio unmonitored. anything this cannot read or no longer sees holds nothing, which keeps share preview as it was
    internal static class DiscordStreamWorker
    {
        private const int PollMs = 2000;
        // the current log of a session and the one before it; discord moves the current one aside at 5 mb and starts a new one, and names the logs of the last session discord-last-webrtc
        private const string CurrentLog = "discord-webrtc_0";
        private const string PreviousLog = "discord-webrtc_1";
        private const string StreamMarker = "SetScreenshare: ";
        private const string AudioStartMarker = "Application loopback capture started for pid ";
        private const string AudioStopMarker = "Application loopback capture stopped for pid ";
        // written every 10 s while the user streams, so a stream that ended without a stop line (a dropped call, a frozen discord) lets go once these stop
        private const string StatsMarker = "[stream] Outbound screen share stats";
        private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(30);
        private const string TimeFormat = "yyyy-MM-dd HH:mm:ss.fff";
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Tail> Tails = new Dictionary<string, Tail>(StringComparer.OrdinalIgnoreCase);
        // the read errors already written to the log, so one that keeps happening is written once
        private static readonly HashSet<string> Reported = new HashSet<string>(StringComparer.Ordinal);
        private static bool started, held;
        private static string reason = "";

        private sealed class Tail
        {
            public string File;
            public long Offset;
            public bool Streaming;
            public int AudioPid;
            // the local time discord wrote the last stream start or stats line at
            public DateTime LastSeen;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
            public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);

        public static void Start()
        {
            lock (Gate) { if (started) return; started = true; }
            new Thread(Loop) { Name = "discord-stream", IsBackground = true }.Start();
        }

        // whether the share audio is held now, read by the monitoring the helper repairs so it agrees with the plugin
        public static bool Held
        {
            get { lock (Gate) return held; }
        }

        public static JObject State()
        {
            lock (Gate)
            {
                var clients = new JArray();
                foreach (var pair in Tails)
                    clients.Add(new JObject { ["client"] = pair.Key, ["streaming"] = pair.Value.Streaming, ["audioPid"] = pair.Value.AudioPid, ["lastSeen"] = pair.Value.LastSeen == DateTime.MinValue ? null : pair.Value.LastSeen.ToString("o", CultureInfo.InvariantCulture) });
                return new JObject { ["ok"] = true, ["held"] = held, ["reason"] = reason, ["shareWindowPid"] = ShareWindow.KnownPid, ["clients"] = clients };
            }
        }

        private static void Loop()
        {
            while (!Server.State.Shutdown)
            {
                string why;
                try { why = Poll(); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
                {
                    ReportOnce("round", ex.Message);
                    why = "";
                }
                Publish(why);
                Thread.Sleep(PollMs);
            }
        }

        // why the share audio is held this round, empty when it is not
        private static string Poll()
        {
            bool enabled;
            try { enabled = SettingsLiveApply.GetSharePreviewFlags()["enabled"]?.Value<bool>() == true; }
            catch (InvalidOperationException) { enabled = false; }
            if (!enabled)
            {
                lock (Gate) Tails.Clear();
                return "";
            }
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            int sharePid = ShareWindow.KnownPid;
            DateTime now = DateTime.Now;
            string why = "";
            // each discord build keeps its logs in a folder of its own name; a program of the list without one, like the system helper, writes no stream log
            foreach (string program in DesktopAudioExclusions.DiscordPrograms)
            {
                string process = Path.GetFileNameWithoutExtension(program);
                string client = process.ToLowerInvariant();
                Tail tail;
                try
                {
                    tail = Read(Path.Combine(appData, client, "logs"), client);
                    lock (Gate) Reported.RemoveWhere(r => r.StartsWith(client + ":", StringComparison.Ordinal));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // a log that cannot be read says nothing about a stream
                    ReportOnce(client, ex.Message);
                    lock (Gate) Tails.Remove(client);
                    continue;
                }
                if (why.Length > 0 || tail == null || !tail.Streaming || tail.AudioPid == 0 || tail.AudioPid == sharePid || now - tail.LastSeen > Quiet) continue;
                // a client that crashed mid stream wrote no stop, so its last stream only counts while it runs
                if (!Running(process)) continue;
                why = client + " streams the sound of " + (tail.AudioPid == 1 ? "the whole screen" : "pid " + tail.AudioPid);
            }
            return why;
        }

        private static void Publish(string why)
        {
            bool now = why.Length > 0;
            bool changed;
            lock (Gate)
            {
                changed = now != held;
                held = now;
                reason = why;
            }
            if (changed) Log.Write(now ? "Discord " + why + ", so the share audio is held and Desktop Audio is not monitored" : "Discord no longer streams another window with sound, so the share audio is back");
            // sent every round: a plugin that started again with obs learns it, and the plugin lets go of a hold this stops repeating
            PipeClient.SendShareAudioHold(now);
        }

        private static void ReportOnce(string where, string message)
        {
            lock (Gate) if (!Reported.Add(where + ":" + message)) return;
            Log.Write("Discord stream check, " + where + ": " + message);
        }

        // the stream state of one client from its log, reading only what was written since the last round; a log that is another file than last round (discord moved it aside or started a new session) or got shorter is read again from the start, after the one before it
        private static Tail Read(string folder, string client)
        {
            string current = Path.Combine(folder, CurrentLog);
            if (!File.Exists(current))
            {
                lock (Gate) Tails.Remove(client);
                return null;
            }
            using (var stream = Open(current))
            {
                if (!GetFileInformationByHandle(stream.SafeFileHandle, out var information)) throw new IOException("could not identify " + CurrentLog, Marshal.GetHRForLastWin32Error());
                string identity = information.VolumeSerial.ToString("x8", CultureInfo.InvariantCulture) + ":" + information.IndexHigh.ToString("x8", CultureInfo.InvariantCulture) + information.IndexLow.ToString("x8", CultureInfo.InvariantCulture);
                Tail tail;
                lock (Gate) Tails.TryGetValue(client, out tail);
                if (tail == null || tail.File != identity || stream.Length < tail.Offset)
                {
                    tail = new Tail { File = identity, LastSeen = DateTime.MinValue };
                    string previous = Path.Combine(folder, PreviousLog);
                    if (File.Exists(previous))
                        using (var older = Open(previous)) ReadLines(older, 0, tail);
                }
                tail.Offset = ReadLines(stream, tail.Offset, tail);
                lock (Gate) Tails[client] = tail;
                return tail;
            }
        }

        private static FileStream Open(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        // reads the whole lines after offset into the state and returns where the last whole line ended, so a line discord is still writing is read next round
        private static long ReadLines(FileStream stream, long offset, Tail tail)
        {
            stream.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[64 * 1024];
            var line = new List<byte>(512);
            long position = offset, lineEnd = offset;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    position++;
                    if (buffer[i] != (byte)'\n')
                    {
                        // the lines this reads are short, so a long one is only ever skipped
                        if (line.Count < 2048) line.Add(buffer[i]);
                        continue;
                    }
                    Apply(Encoding.UTF8.GetString(line.ToArray()), tail);
                    line.Clear();
                    lineEnd = position;
                }
            }
            return lineEnd;
        }

        private static void Apply(string line, Tail tail)
        {
            int at = line.IndexOf(StreamMarker, StringComparison.Ordinal);
            if (at >= 0)
            {
                // the address of the stream, all zeros once it stopped
                string value = line.Substring(at + StreamMarker.Length).Trim();
                tail.Streaming = value.Length > 0 && value.TrimStart('0').Length > 0;
                if (tail.Streaming) Seen(line, tail);
                else tail.AudioPid = 0;
                return;
            }
            if (line.IndexOf(StatsMarker, StringComparison.Ordinal) >= 0)
            {
                Seen(line, tail);
                return;
            }
            if (TryPid(line, AudioStartMarker, out int started))
            {
                tail.AudioPid = started;
                return;
            }
            if (TryPid(line, AudioStopMarker, out int stopped) && stopped == tail.AudioPid) tail.AudioPid = 0;
        }

        // the local time a line starts with, [2026-11-19 19:54:08.647]; a line without one leaves the time as it was
        private static void Seen(string line, Tail tail)
        {
            if (line.Length > TimeFormat.Length + 1 && line[0] == '[' &&
                DateTime.TryParseExact(line.Substring(1, TimeFormat.Length), TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime when))
                tail.LastSeen = when;
        }

        private static bool TryPid(string line, string marker, out int pid)
        {
            pid = 0;
            int at = line.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) return false;
            int start = at + marker.Length, end = start;
            while (end < line.Length && char.IsDigit(line[end])) end++;
            return end > start && int.TryParse(line.Substring(start, end - start), out pid) && pid > 0;
        }

        private static bool Running(string process)
        {
            var found = Process.GetProcessesByName(process);
            foreach (var p in found) p.Dispose();
            return found.Length > 0;
        }
    }
}

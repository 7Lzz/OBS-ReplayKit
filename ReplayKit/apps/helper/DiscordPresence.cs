using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // the game being played on the discord profile of the user, the way medal shows it: the game and its art, how long it has run and a link to get replaykit, set over the local rpc pipe of the discord app; only while the setting and game recognition are on and a recognised game is running
    internal static class DiscordPresence
    {
        // the discord application the presence belongs to: its name is the activity discord shows and its art assets hold the logo; an application id is public by design, unlike its secret, which nothing here needs
        private const string ClientId = "1571337149347790899";
        private const string LogoAsset = "replaykit";
        private const int TickMs = 2000;
        // discord takes five activity updates every twenty seconds
        private const int MinSendMs = 4000;
        private const int RetrySeconds = 15;
        private const int ReplyMs = 3000;
        private const int MaxFrameBytes = 64 * 1024;
        private const int RobloxCheckSeconds = 15;

        private sealed class Sighting
        {
            internal GameCatalog.Game Game;
            internal string Process;
            internal long StartUnix;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool PeekNamedPipe(SafePipeHandle pipe, IntPtr buffer, uint size, IntPtr read, out uint available, IntPtr left);

        private static readonly AutoResetEvent Wake = new AutoResetEvent(false);
        // the recognised game seen last, with the program it runs as and when it was first seen
        private static volatile Sighting seen;
        // the setting, read once and then kept in step by every settings write; -1 until it is known
        private static int enabled = -1;
        private static volatile NamedPipeClientStream pipe;
        private static volatile string sentKey = "";
        private static volatile string lastError = "";
        private static DateTime sentAt = DateTime.MinValue, retryAt = DateTime.MinValue, robloxCheckedAt = DateTime.MinValue;
        private static RobloxExperiences.Experience roblox;
        // discord ties the activity to this process and takes it away when it ends
        private static readonly int OwnPid = Process.GetCurrentProcess().Id;

        internal static void Start() => new Thread(Run) { Name = "discord-presence", IsBackground = true }.Start();

        internal static void SetEnabled(bool on)
        {
            Volatile.Write(ref enabled, on ? 1 : 0);
            Wake.Set();
        }

        // every capture sample that recognised a game, with the path of the program it captured
        internal static void Seen(GameCatalog.Game game, string path)
        {
            if (game == null || seen?.Game.Id == game.Id) return;
            string process;
            try { process = Path.GetFileNameWithoutExtension(path); }
            catch (ArgumentException) { return; }
            if (string.IsNullOrEmpty(process)) return;
            seen = new Sighting { Game = game, Process = process, StartUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
            Wake.Set();
        }

        internal static JObject State()
        {
            string sent = sentKey;
            return new JObject
            {
                ["ok"] = true,
                ["applicationSet"] = ClientId.Length > 0,
                ["enabled"] = Enabled(),
                ["gameRecognition"] = ClipGames.RecognitionOn(),
                ["game"] = seen?.Game.Name,
                ["connected"] = pipe != null,
                ["activity"] = sent.Length > 0 ? JObject.Parse(sent) : null,
                ["lastError"] = lastError,
            };
        }

        private static bool Enabled()
        {
            int known = Volatile.Read(ref enabled);
            if (known >= 0) return known == 1;
            bool on;
            try { on = ReplaykitSettings.ReadSettings()["discordRichPresence"]?.Value<bool>() ?? false; }
            catch (InvalidOperationException) { return false; }
            Interlocked.CompareExchange(ref enabled, on ? 1 : 0, -1);
            return Volatile.Read(ref enabled) == 1;
        }

        private static void Run()
        {
            while (!Server.State.Shutdown)
            {
                try { Tick(); }
                catch (Exception ex) when (ex is IOException || ex is TimeoutException || ex is AggregateException || ex is ObjectDisposedException || ex is InvalidOperationException || ex is JsonException || ex is InvalidDataException || ex is UnauthorizedAccessException)
                {
                    lastError = ex.GetBaseException().Message;
                    Log.Write("Discord presence: " + lastError);
                    Close();
                    retryAt = DateTime.UtcNow.AddSeconds(RetrySeconds);
                }
                Wake.WaitOne(TickMs);
            }
            Close();
        }

        private static void Tick()
        {
            Drain();
            var want = Desired();
            if (want == null)
            {
                // discord takes the activity away the moment its pipe closes
                if (pipe != null)
                {
                    Close();
                    Log.Write("Discord presence cleared");
                }
                return;
            }
            string key = want.ToString(Formatting.None);
            if (key == sentKey && pipe != null) return;
            var now = DateTime.UtcNow;
            if ((now - sentAt).TotalMilliseconds < MinSendMs) return;
            if (pipe == null)
            {
                if (now < retryAt) return;
                if (!Connect())
                {
                    retryAt = now.AddSeconds(RetrySeconds);
                    return;
                }
            }
            sentAt = now;
            if (SetActivity(want))
            {
                sentKey = key;
                lastError = "";
                Log.Write("Discord presence shows " + want["details"]);
            }
            else
            {
                Close();
                retryAt = now.AddSeconds(RetrySeconds);
            }
        }

        // the activity the profile should show now, or null for none
        private static JObject Desired()
        {
            if (ClientId.Length == 0 || !Enabled() || !ClipGames.RecognitionOn()) return null;
            var sighting = seen;
            if (sighting == null) return null;
            if (!Running(sighting.Process))
            {
                if (seen == sighting) seen = null;
                return null;
            }
            string name = sighting.Game.Name, details = name;
            string art = CatalogArtwork.PublicUrl(sighting.Game);
            if (sighting.Game.Id == RobloxExperiences.RobloxId)
            {
                var now = DateTime.UtcNow;
                if ((now - robloxCheckedAt).TotalSeconds >= RobloxCheckSeconds)
                {
                    robloxCheckedAt = now;
                    try { roblox = RobloxExperiences.Playing(); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Log.Write("Discord presence: roblox logs unreadable: " + ex.GetType().Name); }
                }
                if (!string.IsNullOrWhiteSpace(roblox?.Name))
                {
                    details = roblox.Name;
                    if (roblox.ImageUrl != null && roblox.ImageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) art = roblox.ImageUrl;
                }
            }
            var assets = new JObject { ["large_image"] = art ?? LogoAsset, ["large_text"] = Fit(name, 128) };
            if (art != null)
            {
                assets["small_image"] = LogoAsset;
                assets["small_text"] = "ReplayKit";
            }
            return new JObject
            {
                ["type"] = 0,
                ["details"] = Fit(details, 128),
                ["state"] = Fit(Locales.Message("presence.clipping"), 128),
                ["timestamps"] = new JObject { ["start"] = sighting.StartUnix },
                ["assets"] = assets,
                ["buttons"] = new JArray(new JObject { ["label"] = Fit(Locales.Message("presence.getReplayKit"), 32), ["url"] = Update.RepoPage }),
                ["instance"] = false,
            };
        }

        // discord refuses a text line shorter than two characters or longer than its limit
        private static string Fit(string text, int max)
        {
            text = (text ?? "").Trim();
            if (text.Length > max) text = text.Substring(0, max - 1) + "…";
            return text.Length < 2 ? text.PadRight(2) : text;
        }

        private static bool Running(string process)
        {
            var found = Process.GetProcessesByName(process);
            foreach (var p in found) p.Dispose();
            return found.Length > 0;
        }

        // the discord app listens on the first free one of discord-ipc-0 to 9
        private static bool Connect()
        {
            for (int i = 0; i < 10; i++)
            {
                var candidate = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut, PipeOptions.Asynchronous);
                try { candidate.Connect(50); }
                catch (Exception ex) when (ex is TimeoutException || ex is IOException || ex is UnauthorizedAccessException)
                {
                    candidate.Dispose();
                    continue;
                }
                pipe = candidate;
                Write(0, new JObject { ["v"] = 1, ["client_id"] = ClientId });
                var answer = ReadFrame(out int op);
                if (op == 1 && answer["evt"]?.Value<string>() == "READY")
                {
                    Log.Write("Discord presence connected on discord-ipc-" + i);
                    return true;
                }
                lastError = op == 2 ? "Discord refused the connection: " + answer["message"] : "Discord did not answer the handshake.";
                Log.Write("Discord presence: " + lastError);
                Close();
                return false;
            }
            lastError = "Discord is not running.";
            return false;
        }

        private static bool SetActivity(JObject activity)
        {
            string nonce = Guid.NewGuid().ToString("N");
            Write(1, new JObject
            {
                ["cmd"] = "SET_ACTIVITY",
                ["args"] = new JObject { ["pid"] = OwnPid, ["activity"] = activity },
                ["nonce"] = nonce,
            });
            for (int frames = 0; frames < 16; frames++)
            {
                var answer = ReadFrame(out int op);
                if (!Answer(op, answer)) return false;
                if (op != 1 || answer["nonce"]?.Value<string>() != nonce) continue;
                if (answer["evt"]?.Value<string>() != "ERROR") return true;
                lastError = "Discord rejected the activity: " + answer["data"]?["message"];
                Log.Write("Discord presence: " + lastError);
                return false;
            }
            lastError = "Discord did not answer the activity.";
            return false;
        }

        // answers a ping and notices a close; false once the connection is over
        private static bool Answer(int op, JObject frame)
        {
            if (op == 3) Write(4, frame);
            if (op != 2) return true;
            lastError = "Discord closed the connection: " + frame["message"];
            return false;
        }

        // what discord sent while nothing was asked, read without waiting: pings to answer, a close, or a pipe that is gone
        private static void Drain()
        {
            var current = pipe;
            if (current == null) return;
            while (true)
            {
                if (!PeekNamedPipe(current.SafePipeHandle, IntPtr.Zero, 0, IntPtr.Zero, out uint available, IntPtr.Zero))
                {
                    Close();
                    return;
                }
                if (available < 8) return;
                var frame = ReadFrame(out int op);
                if (!Answer(op, frame))
                {
                    Close();
                    return;
                }
            }
        }

        private static void Write(int op, JObject payload)
        {
            byte[] body = Encoding.UTF8.GetBytes(payload.ToString(Formatting.None));
            var frame = new byte[8 + body.Length];
            BitConverter.GetBytes(op).CopyTo(frame, 0);
            BitConverter.GetBytes(body.Length).CopyTo(frame, 4);
            body.CopyTo(frame, 8);
            if (!pipe.WriteAsync(frame, 0, frame.Length).Wait(ReplyMs)) throw new TimeoutException("Discord did not take the update.");
        }

        private static JObject ReadFrame(out int op)
        {
            byte[] header = ReadExactly(8);
            op = BitConverter.ToInt32(header, 0);
            int length = BitConverter.ToInt32(header, 4);
            if (length < 0 || length > MaxFrameBytes) throw new InvalidDataException("Discord sent a frame of " + length + " bytes.");
            return JObject.Parse(Encoding.UTF8.GetString(ReadExactly(length)));
        }

        private static byte[] ReadExactly(int count)
        {
            var buffer = new byte[count];
            int read = 0;
            while (read < count)
            {
                var task = pipe.ReadAsync(buffer, read, count - read);
                if (!task.Wait(ReplyMs)) throw new TimeoutException("Discord did not answer.");
                if (task.Result == 0) throw new EndOfStreamException("Discord closed the pipe.");
                read += task.Result;
            }
            return buffer;
        }

        private static void Close()
        {
            var current = pipe;
            pipe = null;
            sentKey = "";
            try { current?.Dispose(); }
            catch (IOException) { }
        }
    }
}

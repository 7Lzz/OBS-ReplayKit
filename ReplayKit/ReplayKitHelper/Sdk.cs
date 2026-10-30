using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // a way to communicate with replaykit while it runs: a local command channel that is on while development mode and sdk mode are both on. a command is a name, a line of description and a function from json arguments to a json answer. the helper answers the commands it owns, and a name that begins with "plugin." is sent over the pipe to the native plugin, which runs it on the interface thread and answers over http. a new command is one Register call, here or in the plugin
    internal static class Sdk
    {
        private sealed class Command
        {
            public string Description;
            public Func<JObject, JObject> Run;
        }

        private sealed class Waiting
        {
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public string Body = "";
        }

        private const int MaxTailLines = 500;
        // the tail of a log is read from the end of the file, so a log that has grown large is not read whole
        private const int TailWindowBytes = 256 * 1024;
        private const string PluginPrefix = "plugin.";

        private static readonly object Gate = new object();
        private static readonly SortedDictionary<string, Command> Commands = new SortedDictionary<string, Command>(StringComparer.Ordinal);
        private static readonly Dictionary<int, Waiting> Pending = new Dictionary<int, Waiting>();
        private static readonly Regex NamePattern = new Regex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.Compiled);
        private static int _lastId;

        static Sdk()
        {
            Register("update.badge", "shows the update button of the title bar for args.version (9.9.9 when left out) as if the update check had found it, or takes it away with args.clear; the next update check, every 30 minutes, puts the real state back", args => Update.PreviewBadge(args["clear"]?.Value<bool>() == true ? "" : args["version"]?.ToString() ?? "9.9.9"));
            Register("helper.info", "version, process id, uptime and the switches that matter of the helper", HelperInfo);
            Register("capture.state", "reports accepted native capture evidence without exposing process paths", args => ClipGames.Status());
            Register("roblox.state", "the roblox experience the roblox logs say is open now, and how many experiences have a name and icon", args => RobloxExperiences.State());
            Register("settings.get", "the settings, or only the keys named in args.keys", SettingsGet);
            Register("settings.set", "saves the settings in args.values the way the settings page does", SettingsSet);
            Register("log.tail", "the last args.lines lines of a helper log; args.name is helper, upload or compress", LogTail);
            Register("obs.log.tail", "the last args.lines lines of the newest obs log; args.filter keeps only the lines that contain it", ObsLogTail);
            Register("clip.audio", "the tracks of the clip args.name as its mp4 holds them: kind, enabled flag and alternate group", args => ClipAudioTracks.Inspect(args["name"]?.ToString()));
        }

        public static bool Enabled()
        {
            try { return ReplaykitSettings.DevelopmentFlag(ReplaykitSettings.ReadSettings(), "sdkMode"); }
            catch (InvalidOperationException) { return false; }
        }

        public static void Register(string name, string description, Func<JObject, JObject> run)
        {
            lock (Gate) Commands[name] = new Command { Description = description, Run = run };
        }

        private static JObject Fail(string message) => new JObject { ["ok"] = false, ["message"] = message };

        public static JObject List()
        {
            var commands = new JArray();
            lock (Gate)
            {
                foreach (var pair in Commands)
                    commands.Add(new JObject { ["name"] = pair.Key, ["description"] = pair.Value.Description, ["runsIn"] = "helper" });
            }
            commands.Add(new JObject { ["name"] = PluginPrefix + "commands", ["description"] = "the commands of the native plugin, all named plugin.<name>", ["runsIn"] = "plugin" });
            return new JObject { ["ok"] = true, ["commands"] = commands };
        }

        public static JObject Execute(string name, JObject args)
        {
            if (name == null || !NamePattern.IsMatch(name)) return Fail("Bad command name.");
            Command command;
            lock (Gate) Commands.TryGetValue(name, out command);
            if (command != null)
            {
                try { return command.Run(args ?? new JObject()); }
                catch (Exception ex)
                {
                    Log.Write("sdk " + name + ": " + ex.Message);
                    return Fail(ex.Message);
                }
            }
            if (name.StartsWith(PluginPrefix, StringComparison.Ordinal)) return RunInPlugin(name.Substring(PluginPrefix.Length), args ?? new JObject());
            return Fail("Unknown command: " + name);
        }

        private static JObject RunInPlugin(string name, JObject args)
        {
            int id = Interlocked.Increment(ref _lastId);
            var waiting = new Waiting();
            lock (Gate) Pending[id] = waiting;
            try
            {
                // ascii only, so nothing in the line can be read by the pipe as the end of it
                string json = JsonConvert.SerializeObject(args, Formatting.None, new JsonSerializerSettings { StringEscapeHandling = StringEscapeHandling.EscapeNonAscii });
                if (!PipeClient.SendSdk(id, name, json)) return Fail("The plugin is not connected.");
                if (!waiting.Done.Wait(8000)) return Fail("The plugin did not answer in time.");
                try { return JObject.Parse(waiting.Body); }
                catch (JsonException) { return Fail("The plugin answered with something that is not json."); }
            }
            finally
            {
                lock (Gate) Pending.Remove(id);
            }
        }

        // the plugin calling back over http with the answer to a command it was sent; false when nothing is waiting for that id any more
        public static bool Answer(int id, string body)
        {
            Waiting waiting;
            lock (Gate) Pending.TryGetValue(id, out waiting);
            if (waiting == null) return false;
            waiting.Body = body ?? "";
            waiting.Done.Set();
            return true;
        }

        private static JObject HelperInfo(JObject args)
        {
            var settings = ReplaykitSettings.ReadSettings();
            var process = Process.GetCurrentProcess();
            bool pluginConnected;
            lock (Server.State.IpcLock) pluginConnected = Server.State.IpcClientConnected;
            return new JObject
            {
                ["ok"] = true,
                ["version"] = Update.GetInstalledVersion(),
                ["pid"] = process.Id,
                ["uptimeSeconds"] = (long)(DateTime.Now - process.StartTime).TotalSeconds,
                ["pluginConnected"] = pluginConnected,
                ["developmentMode"] = settings["developmentMode"]?.Value<bool>() ?? false,
                ["sdkMode"] = settings["sdkMode"]?.Value<bool>() ?? false,
                ["logEnabled"] = Server.State.LogEnabled,
                ["logDir"] = Constants.LOG_DIR,
            };
        }

        private static JObject SettingsGet(JObject args)
        {
            var settings = ReplaykitSettings.ReadSettings();
            var keys = args["keys"] as JArray;
            if (keys == null || keys.Count == 0) return new JObject { ["ok"] = true, ["settings"] = settings };
            var picked = new JObject();
            foreach (var key in keys)
            {
                string name = key.ToString();
                if (settings[name] != null) picked[name] = settings[name];
            }
            return new JObject { ["ok"] = true, ["settings"] = picked };
        }

        private static JObject SettingsSet(JObject args)
        {
            var values = args["values"] as JObject;
            if (values == null || !values.HasValues) return Fail("args.values is required.");
            return ReplaykitSettings.SaveSettingsFromRequest(values.ToString(Formatting.None));
        }

        private static int LinesOf(JObject args, int fallback)
        {
            int lines = args["lines"]?.Value<int?>() ?? fallback;
            return Math.Max(1, Math.Min(MaxTailLines, lines));
        }

        private static JObject Tail(string path, int lines, string filter)
        {
            if (!File.Exists(path)) return new JObject { ["ok"] = true, ["path"] = path, ["lines"] = new JArray() };
            string text;
            // the file is still being written to, so it is opened the way that lets its writer go on
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long from = Math.Max(0, stream.Length - TailWindowBytes);
                stream.Seek(from, SeekOrigin.Begin);
                using (var reader = new StreamReader(stream, Encoding.UTF8)) text = reader.ReadToEnd();
            }
            IEnumerable<string> all = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0);
            if (!string.IsNullOrEmpty(filter)) all = all.Where(l => l.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
            var picked = all.Reverse().Take(lines).Reverse().ToList();
            return new JObject { ["ok"] = true, ["path"] = path, ["lines"] = new JArray(picked) };
        }

        private static JObject LogTail(JObject args)
        {
            string name = args["name"]?.ToString() ?? "helper";
            string path = name == "upload" ? Constants.UPLOAD_LOG_PATH : name == "compress" ? Constants.COMPRESS_LOG_PATH : name == "helper" ? Constants.HELPER_LOG_PATH : null;
            if (path == null) return Fail("args.name is helper, upload or compress.");
            return Tail(path, LinesOf(args, 50), null);
        }

        private static JObject ObsLogTail(JObject args)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio", "logs");
            if (!Directory.Exists(dir)) return Fail("The obs log folder was not found.");
            var newest = new DirectoryInfo(dir).GetFiles("*.txt").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (newest == null) return Fail("There is no obs log yet.");
            return Tail(newest.FullName, LinesOf(args, 80), args["filter"]?.ToString());
        }
    }
}

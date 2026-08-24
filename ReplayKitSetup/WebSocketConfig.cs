using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitSetup
{
    // enable obs-websocket on loopback so the docks save-replay button can drive obs. plugin ships with obs since 28; just flip server_enabled=true, auth off. windows/wsl hyper-v port exclusion ranges are recomputed every reboot from whatever the machines dynamic port range happens to be, so no single hardcoded port stays safe forever on every pc -- 4455 then 6455 both eventually got swallowed (WSAEACCES on listen) on different machines. this verifies the configured port actually binds and auto-repicks from a spread of fallbacks when it does not, instead of hardcoding a third number and hoping. ported from obs_replaykit/websocket.py.
    public static class WebSocketConfig
    {
        public static readonly string WEBSOCKET_CONFIG_PATH = Path.Combine(Config.OBS_CONFIG, "plugin_config", "obs-websocket", "config.json");

        // shape obs writes on first run; used as the seed when no config exists yet.
        private static JObject DefaultConfig() => new JObject
        {
            ["alerts_enabled"] = false,
            ["auth_required"] = false,
            ["first_load"] = false,
            ["server_enabled"] = true,
            ["server_password"] = "",
            ["server_port"] = 6455,
        };

        // tried in order after the currently-configured port fails; spread 10000 apart so one exclusion block (seen up to ~100 ports wide in practice) cant catch more than one candidate.
        private static readonly int[] PortFallbacks = { 6455, 16455, 26455, 36455, 46455, 12455, 22455 };

        // true if a loopback listener can actually take this port right now.
        private static bool PortIsBindable(int port)
        {
            try
            {
                using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    s.Bind(new IPEndPoint(IPAddress.Loopback, port));
                }
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        // preferred if it binds, else the first fallback that does. null if every candidate is blocked.
        private static int? PickWorkingPort(int preferred)
        {
            if (PortIsBindable(preferred)) return preferred;
            foreach (var port in PortFallbacks)
            {
                if (port == preferred) continue;
                if (PortIsBindable(port)) return port;
            }
            return null;
        }

        // current config or {} (also {} on parse failure so a corrupted file doesnt block install).
        private static JObject LoadExisting()
        {
            if (!File.Exists(WEBSOCKET_CONFIG_PATH)) return new JObject();
            try
            {
                return JObject.Parse(File.ReadAllText(WEBSOCKET_CONFIG_PATH, System.Text.Encoding.UTF8));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                return new JObject();
            }
        }

        // ensure obs-websocket is enabled on a port that actually binds on this pc. preserves auth_required if the user has already turned it on. runs on every apply (not just fresh installs), so a port thats become blocked since the last run gets caught and repicked automatically -- called with obs already closed, so this is testing the same bind obs-websocket itself will attempt.
        public static bool InstallWebsocketConfig(Action<string> log = null)
        {
            var existing = LoadExisting();
            var merged = DefaultConfig();
            foreach (var prop in existing.Properties()) merged[prop.Name] = prop.Value;

            // force only what the helper depends on; everything else (alerts, etc.) keeps the users value.
            merged["server_enabled"] = true;
            // if auth is already on, leave it on. only defualt to off for fresh configs.
            if (existing.Property("auth_required") == null) merged["auth_required"] = false;

            int wantedPort = merged.Value<int?>("server_port") ?? 6455;
            if (!PortIsBindable(wantedPort))
            {
                var found = PickWorkingPort(wantedPort);
                if (found.HasValue && found.Value != wantedPort)
                {
                    log?.Invoke($"port {wantedPort} is blocked on this pc (windows port reservation) - switching to {found.Value}");
                    merged["server_port"] = found.Value;
                }
                else if (!found.HasValue)
                {
                    log?.Invoke($"warn: every candidate port is blocked on this pc; leaving server_port={wantedPort} (obs-websocket will fail to start)");
                }
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(WEBSOCKET_CONFIG_PATH));
                File.WriteAllText(WEBSOCKET_CONFIG_PATH, merged.ToString(Formatting.Indented), new System.Text.UTF8Encoding(false));
            }
            catch (Exception exc) when (exc is IOException || exc is UnauthorizedAccessException)
            {
                log?.Invoke("warn: could not write obs-websocket config: " + exc.Message);
                return false;
            }

            bool authRequired = merged.Value<bool?>("auth_required") ?? false;
            string authNote = authRequired ? " (auth required - helper will need the password)" : "";
            log?.Invoke($"server_enabled=true port={merged["server_port"]}{authNote}");
            return true;
        }
    }
}

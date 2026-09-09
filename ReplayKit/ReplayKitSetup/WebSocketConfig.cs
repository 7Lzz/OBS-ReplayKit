using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitSetup
{
    // Configure authenticated OBS WebSocket access for the local helper.
    public static class WebSocketConfig
    {
        public static readonly string WEBSOCKET_CONFIG_PATH = Path.Combine(Config.OBS_CONFIG, "plugin_config", "obs-websocket", "config.json");

        // shape obs writes on first run; used as the seed when no config exists yet.
        private static JObject DefaultConfig() => new JObject
        {
            ["alerts_enabled"] = false,
            ["auth_required"] = true,
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
            if (port < 1 || port > 65535) return false;
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

        // Preserve existing settings; unreadable configuration must not be overwritten.
        private static JObject LoadExisting()
        {
            if (!File.Exists(WEBSOCKET_CONFIG_PATH)) return new JObject();
            try
            {
                return JObject.Parse(File.ReadAllText(WEBSOCKET_CONFIG_PATH, System.Text.Encoding.UTF8));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                throw new InvalidDataException("Existing OBS websocket configuration could not be read; it was not changed.", ex);
            }
        }

        // Called with OBS closed so port availability can be checked before configuration is written.
        public static bool InstallWebsocketConfig(Action<string> log = null)
        {
            var existing = LoadExisting();
            var merged = DefaultConfig();
            foreach (var prop in existing.Properties()) merged[prop.Name] = prop.Value;

            // force only what the helper depends on; everything else (alerts, etc.) keeps the users value.
            merged["server_enabled"] = true;
            merged["auth_required"] = true;
            if (string.IsNullOrWhiteSpace(merged.Value<string>("server_password")))
                merged["server_password"] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

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
                    log?.Invoke("warn: every candidate OBS websocket port is blocked; configuration was not changed.");
                    return false;
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

            string authNote = " (authentication enabled)";
            log?.Invoke($"server_enabled=true port={merged["server_port"]}{authNote}");
            return true;
        }
    }
}

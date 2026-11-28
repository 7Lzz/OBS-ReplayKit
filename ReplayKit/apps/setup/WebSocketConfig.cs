using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitSetup
{
    // replaykit asks obs through its own plugin, so the obs-websocket server it used to turn on is turned off, which closes the port it listened on for the whole network
    public static class WebSocketConfig
    {
        public static readonly string WEBSOCKET_CONFIG_PATH = Path.Combine(Config.OBS_CONFIG, "plugin_config", "obs-websocket", "config.json");

        // keeps every other value of the config; no config means obs never ran obs-websocket, whose server is off until someone turns it on
        public static bool TurnServerOff(Action<string> log = null)
        {
            if (!File.Exists(WEBSOCKET_CONFIG_PATH)) return true;
            // a config that cannot be read is left as it is, and the step is tried again on the next update
            JObject config = JObject.Parse(File.ReadAllText(WEBSOCKET_CONFIG_PATH, System.Text.Encoding.UTF8));
            if (config["server_enabled"]?.Type == JTokenType.Boolean && !config["server_enabled"].Value<bool>()) return true;
            config["server_enabled"] = false;
            File.WriteAllText(WEBSOCKET_CONFIG_PATH, config.ToString(Formatting.Indented), new System.Text.UTF8Encoding(false));
            log?.Invoke("obs-websocket server turned off");
            return true;
        }
    }
}

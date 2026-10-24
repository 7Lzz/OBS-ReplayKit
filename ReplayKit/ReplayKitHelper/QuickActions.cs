using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // the quick actions of the menu under the logo of the title bar: each page of the shell says here what it offers right now (the editor its exit, screenshot, render and import, settings its save and cancel), the plugin reads the list of the page that shows when the menu opens and adds what it does itself, and a pick goes back to the plugin over the pipe
    internal static class QuickActions
    {
        private const int MaxActions = 8;
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, JArray> Lists = new Dictionary<string, JArray>();
        private static readonly Regex IdPattern = new Regex("^[a-z][a-z-]{0,23}$", RegexOptions.Compiled);
        private static readonly Regex IconPattern = new Regex("^[a-z]+/[a-z0-9-]{1,40}$", RegexOptions.Compiled);

        // the pages that run in the shell browser, the only ones that keep a list here
        public static bool IsPageView(string view) => view == "home" || view == "clips" || view == "settings" || view == "setup";

        // the native views of the plugin can be picked from too, but their actions are the plugins own
        public static bool IsView(string view) => IsPageView(view) || view == "projector" || view == "obs-settings";

        public static bool IsId(string id) => id != null && IdPattern.IsMatch(id);

        // what a page offers, checked field by field; null and a message when any of it is not what a page sends
        public static JObject Set(string view, string body)
        {
            if (!IsPageView(view)) return new JObject { ["ok"] = false, ["message"] = "Unknown view" };
            JArray clean = new JArray();
            try
            {
                var actions = (JObject.Parse(body ?? "{}")["actions"] as JArray) ?? new JArray();
                if (actions.Count > MaxActions) return new JObject { ["ok"] = false, ["message"] = "Too many actions" };
                foreach (var token in actions)
                {
                    if (!(token is JObject a)) return new JObject { ["ok"] = false, ["message"] = "Bad action" };
                    string id = a["id"]?.Type == JTokenType.String ? a.Value<string>("id") : null;
                    string label = a["label"]?.Type == JTokenType.String ? a.Value<string>("label").Trim() : null;
                    string icon = a["icon"]?.Type == JTokenType.String ? a.Value<string>("icon") : "";
                    if (!IsId(id) || string.IsNullOrEmpty(label) || label.Length > 40 || (icon.Length > 0 && !IconPattern.IsMatch(icon)))
                        return new JObject { ["ok"] = false, ["message"] = "Bad action" };
                    clean.Add(new JObject { ["id"] = id, ["label"] = label, ["icon"] = icon, ["disabled"] = a["disabled"]?.Type == JTokenType.Boolean && a.Value<bool>("disabled") });
                }
            }
            catch (Newtonsoft.Json.JsonException) { return new JObject { ["ok"] = false, ["message"] = "Bad json" }; }
            lock (Gate) Lists[view] = clean;
            return new JObject { ["ok"] = true };
        }

        public static JArray Get(string view)
        {
            lock (Gate) return Lists.TryGetValue(view ?? "", out var list) ? (JArray)list.DeepClone() : new JArray();
        }
    }
}

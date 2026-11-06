using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // the encoders obs could really load this session, and what to tell someone whose gpu encoder did not load. obs leaves a gpu encoder out when the driver is too old for it (nvenc says "outdated_driver" in the log), and a profile that still names it fails every recording with obs's own missing-encoder and failed-to-start dialogs. the plugin catches that before obs builds its outputs, moves the profile onto an encoder that did load, and reports here; this keeps the settings from naming the missing one again and tells the person how to get it back
    internal static class GpuDrivers
    {
        private static readonly object Gate = new object();
        private static HashSet<string> available;
        private static int reportedObsPid;
        private static string announcedFor = "";

        // the pages that install or update the driver of each maker, in the apps people are meant to use for it
        private sealed class Maker
        {
            public string Name, App, Url;
        }
        private static readonly Maker Nvidia = new Maker { Name = "NVIDIA", App = "NVIDIA App", Url = "https://www.nvidia.com/en-us/software/nvidia-app/" };
        private static readonly Maker Amd = new Maker { Name = "AMD", App = "AMD Software: Adrenalin Edition", Url = "https://www.amd.com/en/support/download/drivers.html" };
        private static readonly Maker Intel = new Maker { Name = "Intel", App = "Intel Driver & Support Assistant", Url = "https://www.intel.com/content/www/us/en/support/detect.html" };

        private static Maker MakerOf(string encoderId)
        {
            string id = (encoderId ?? "").ToLowerInvariant();
            if (id.Contains("nvenc")) return Nvidia;
            if (id.Contains("amf")) return Amd;
            if (id.Contains("qsv")) return Intel;
            return null;
        }

        // the links a notification may open besides a release page
        public static bool IsDriverPage(Uri uri) =>
            uri != null && new[] { Nvidia, Amd, Intel }.Any(m => Uri.TryCreate(m.Url, UriKind.Absolute, out var known) &&
                string.Equals(known.Host, uri.Host, StringComparison.OrdinalIgnoreCase) && string.Equals(known.AbsolutePath, uri.AbsolutePath, StringComparison.OrdinalIgnoreCase));

        // the video encoders obs registered this session, or null before the plugin said (an older plugin, obs still starting), in which case nothing is filtered
        public static HashSet<string> Available()
        {
            lock (Gate)
            {
                if (available == null) return null;
                // a report from an obs that has since closed says nothing about this one
                if (reportedObsPid != 0 && ParentWatchdog.ParentPid != 0 && reportedObsPid != ParentWatchdog.ParentPid) return null;
                return new HashSet<string>(available, StringComparer.OrdinalIgnoreCase);
            }
        }

        public static bool IsAvailable(string encoderId)
        {
            var set = Available();
            return set == null || string.IsNullOrEmpty(encoderId) || set.Contains(encoderId);
        }

        // the report of the plugin: { obsPid, available: [ids], missing: id or "", fallback: id or "" }
        public static JObject Report(JObject body)
        {
            var ids = (body?["available"] as JArray)?.Select(t => t?.Value<string>()).Where(s => !string.IsNullOrWhiteSpace(s) && s.Length < 80).ToList();
            if (ids == null || ids.Count == 0) return new JObject { ["ok"] = false, ["message"] = "No encoders were reported." };
            int pid = body["obsPid"]?.Type == JTokenType.Integer ? body["obsPid"].Value<int>() : 0;
            string missing = body["missing"]?.Value<string>() ?? "";
            string fallback = body["fallback"]?.Value<string>() ?? "";
            bool announce;
            lock (Gate)
            {
                available = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
                reportedObsPid = pid;
                string session = pid + "|" + missing;
                announce = missing.Length > 0 && session != announcedFor;
                if (announce) announcedFor = session;
            }
            if (missing.Length == 0)
            {
                // the encoder loads again (the driver was updated), so an old notice of it goes
                Notifications.Forget("encoder-driver");
                return new JObject { ["ok"] = true };
            }
            Log.Write("Encoder " + missing + " did not load in OBS; the plugin moved recording to " + (fallback.Length > 0 ? fallback : "nothing"));
            if (announce) AnnounceMissing(missing, fallback);
            return new JObject { ["ok"] = true, ["announced"] = announce };
        }

        private static string Describe(string encoderId)
        {
            string id = (encoderId ?? "").ToLowerInvariant();
            if (id.Contains("x264")) return "your processor (x264)";
            var maker = MakerOf(id);
            string codec = id.Contains("hevc") || id.Contains("h265") ? "H.265" : "H.264";
            return maker == null ? encoderId : maker.Name + " " + codec + " (" + (maker == Nvidia ? "NVENC" : maker == Amd ? "AMF" : "Quick Sync") + ")";
        }

        private static void AnnounceMissing(string missing, string fallback)
        {
            var maker = MakerOf(missing);
            string gpu = maker?.Name ?? "graphics";
            string now = string.IsNullOrEmpty(fallback)
                ? "ReplayKit could not find another encoder to switch to, so clips cannot be recorded until the driver is fixed."
                : "To keep clipping working, ReplayKit switched recording to " + Describe(fallback) + " for now." +
                  (fallback.IndexOf("x264", StringComparison.OrdinalIgnoreCase) >= 0 ? " Encoding on the processor can lower your frame rate in games." : "");
            string how = maker == null
                ? "Update your graphics driver from the website of your graphics card maker, then restart OBS."
                : "Install or open **" + maker.App + "**, update the " + maker.Name + " graphics driver, then restart your PC. ReplayKit switches back to the " + maker.Name + " encoder by itself once it loads.";
            string body = "OBS could not load the " + Describe(missing) + " encoder. This almost always means the " + gpu + " graphics driver is out of date.\n\n" + now + "\n\n" + how;
            Notifications.Announce("warning", "encoder-driver", "Update your " + gpu + " graphics driver", body,
                new JObject
                {
                    ["icon"] = "warning",
                    ["text"] = "OBS could not load the " + Describe(missing) + " encoder. Your " + gpu + " graphics driver is most likely out of date.",
                    ["detail"] = now,
                    ["action"] = maker == null ? null : new JObject { ["label"] = "Get " + maker.App, ["kind"] = "link", ["url"] = maker.Url },
                },
                maker?.Url ?? "", maker == null ? null : "Get " + maker.App);
        }
    }
}

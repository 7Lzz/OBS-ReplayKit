using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // the development switch that looks for a newer obs. the installer turned the updater of obs off, so this is how a developer learns that an obs release is out which replaykit has to be made ready for; the version goes on a grey badge of the title bar, left of the replaykit one, and nothing is fetched while the switch is off
    internal static class ObsUpdate
    {
        private const string LatestRelease = "https://api.github.com/repos/obsproject/obs-studio/releases/latest";
        private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);
        private static readonly HttpClient Http = CreateHttpClient();
        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("OBSReplayKit-ObsCheck");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        private static Timer timer;
        private static int running;
        private static volatile bool enabled;
        // the newest obs release the last check found and when, so the badge follows an obs that was updated without asking github again
        private static volatile string latest = "";
        private static DateTime checkedAt = DateTime.MinValue;
        private static volatile string readyVersion = "";

        public static string ReadyVersion => readyVersion;

        // the 30 minute tick only goes to github when the switch is on and the last answer is 6 hours old
        public static void Start()
        {
            SetEnabled(ReplaykitSettings.DevelopmentFlag(ReplaykitSettings.ReadSettings(), "checkObsUpdates"));
            if (timer == null) timer = new Timer(_ => Check(), null, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
        }

        // follows the settings: turned on it checks right away, turned off the badge goes
        public static void SetEnabled(bool on)
        {
            if (enabled == on) return;
            enabled = on;
            if (!on)
            {
                readyVersion = "";
                return;
            }
            checkedAt = DateTime.MinValue;
            ThreadPool.QueueUserWorkItem(_ => Check());
        }

        // sdk mode shows the badge for a version of its own choosing, to see how it looks, or takes it away with an empty one; the next check puts the real state back
        public static JObject PreviewBadge(string version)
        {
            version = (version ?? "").Trim();
            if (version.Length > 0 && !Regex.IsMatch(version, @"^\d{1,4}\.\d{1,4}\.\d{1,4}$")) return new JObject { ["ok"] = false, ["message"] = "Bad version." };
            readyVersion = version;
            return new JObject { ["ok"] = true, ["obsUpdateReady"] = version };
        }

        private static void Check()
        {
            if (!enabled || Interlocked.Exchange(ref running, 1) == 1) return;
            try
            {
                if (DateTime.UtcNow - checkedAt >= CheckEvery)
                {
                    string body = Http.GetStringAsync(LatestRelease).GetAwaiter().GetResult();
                    string tag = JObject.Parse(body)["tag_name"]?.Value<string>() ?? "";
                    if (!Regex.IsMatch(tag, @"^\d{1,4}\.\d{1,4}\.\d{1,4}$")) throw new InvalidDataException("GitHub named the latest OBS release " + tag);
                    latest = tag;
                    checkedAt = DateTime.UtcNow;
                }
                var installed = InstalledVersion();
                string ready = enabled && latest.Length > 0 && installed != null && Version.Parse(latest) > installed ? latest : "";
                if (ready != readyVersion) Log.Write("obs update check: " + (ready.Length > 0 ? "OBS " + ready + " is out, installed " + installed : "OBS is up to date"));
                readyVersion = ready;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException || ex is JsonException || ex is InvalidDataException || ex is IOException)
            {
                Log.Write("obs update check failed: " + ex.Message);
            }
            finally { Interlocked.Exchange(ref running, 0); }
        }

        internal static Version InstalledVersion()
        {
            string exe = Constants.ResolveObsExe();
            if (string.IsNullOrEmpty(exe)) return null;
            var match = Regex.Match(FileVersionInfo.GetVersionInfo(exe).FileVersion ?? "", @"^(\d{1,4})\.(\d{1,4})\.(\d{1,4})");
            return match.Success ? new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value)) : null;
        }
    }
}

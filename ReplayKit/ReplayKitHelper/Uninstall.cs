using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // triggers the setup exe's windowed --uninstall mode from the settings dock uninstall button -- a progress window
    // (twin of the setup window) removes everything, then boots OBS back up. ported from obs_replaykit helper modules/65_uninstall.ps1.
    internal static class Uninstall
    {
        public static string GetSetupCacheDir()
        {
            string localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (!string.IsNullOrEmpty(localAppData)) return Path.Combine(localAppData, "OBS ReplayKit");
            return Path.Combine(AppConfig.GetUserProfile(), "AppData", "Local", "OBS ReplayKit");
        }

        public static string GetSetupExecutable() => Path.Combine(GetSetupCacheDir(), "OBSReplayKitSetup.exe");

        public static JObject StartCleanupFromSettings(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) throw new InvalidOperationException("Missing uninstall confirmation.");
            var incoming = JObject.Parse(body);
            if (incoming["confirm"]?.Value<string>() != "confirm") throw new InvalidOperationException("Invalid uninstall confirmation.");
            bool keepUserSettings = true;
            var keepToken = incoming["keepUserSettings"];
            if (keepToken != null)
            {
                if (keepToken.Type != JTokenType.Boolean) throw new InvalidOperationException("Keep user settings must be a JSON boolean.");
                keepUserSettings = keepToken.Value<bool>();
            }

            bool removeObs = false;
            var removeObsToken = incoming["removeObs"];
            if (removeObsToken != null)
            {
                if (removeObsToken.Type != JTokenType.Boolean) throw new InvalidOperationException("Remove OBS must be a JSON boolean.");
                removeObs = removeObsToken.Value<bool>();
            }

            string cacheDir = Path.GetFullPath(GetSetupCacheDir());
            string setupExe = Path.GetFullPath(GetSetupExecutable());
            string cachePrefix = cacheDir.TrimEnd('\\') + "\\";
            if (!setupExe.StartsWith(cachePrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Invalid setup executable path.");
            if (!File.Exists(setupExe))
                throw new InvalidOperationException("ReplayKit setup executable is missing. Re-run the installer once, then uninstall from Advanced.");

            var argList = new List<string> { "--uninstall" };
            if (!keepUserSettings) argList.Add("--remove-user-settings");
            if (removeObs) argList.Add("--remove-obs");

            if (BrowserCookies.TestIsAdmin())
            {
                string cmdLine = ProcessArgs.Quote(setupExe) + " " + ProcessArgs.Join(argList.ToArray());
                int uninstallPid = Native.SpawnDetached(cmdLine, cacheDir);
                if (uninstallPid <= 0) throw new InvalidOperationException("Could not start ReplayKit uninstall.");
                Log.Write("ReplayKit uninstall started detached as PID " + uninstallPid + ".");
                return new JObject { ["ok"] = true, ["processId"] = uninstallPid, ["message"] = "Uninstall started. Follow the progress window." };
            }

            var psi = new ProcessStartInfo
            {
                FileName = setupExe,
                Arguments = ProcessArgs.Join(argList.ToArray()),
                WorkingDirectory = cacheDir,
                UseShellExecute = true,
                Verb = "runas",
            };
            var proc = Process.Start(psi);
            if (proc == null) throw new InvalidOperationException("Could not start ReplayKit uninstall.");
            Log.Write("ReplayKit uninstall started elevated as PID " + proc.Id + ".");
            return new JObject { ["ok"] = true, ["processId"] = proc.Id, ["message"] = "Uninstall started. Follow the progress window." };
        }

        // narrower siblings of StartCleanupFromSettings -- install / remove just the OBS Stream Audio virtual cable
        // instead of touching the whole ReplayKit install, for the Share Preview audio row next to the main uninstall.
        public static JObject StartDiscordScreenshareInstall() => RunDiscordScreenshareMode(true);

        public static JObject StartDiscordScreenshareRemoval() => RunDiscordScreenshareMode(false);

        private static JObject RunDiscordScreenshareMode(bool install)
        {
            string cacheDir = Path.GetFullPath(GetSetupCacheDir());
            string setupExe = Path.GetFullPath(GetSetupExecutable());
            string cachePrefix = cacheDir.TrimEnd('\\') + "\\";
            if (!setupExe.StartsWith(cachePrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Invalid setup executable path.");
            if (!File.Exists(setupExe))
                throw new InvalidOperationException("ReplayKit setup executable is missing. Re-run the installer once, then try again.");

            // flip the runtime setting now so Share Preview is really on/off after the OBS relaunch -- the setup exe only
            // writes its own prefs.json, and the helper reads discord_screenshare_enabled from replaykit_settings.json.
            try
            {
                var settings = ReplaykitSettings.ReadSettings();
                settings["discord_screenshare_enabled"] = install;
                settings["discord_projector_enabled"] = install;
                ReplaykitSettings.WriteSettings(settings);
            }
            catch (Exception ex) { Log.Write("Discord screenshare settings flip failed: " + ex.Message); }

            string verb = install ? "--install-discord-screenshare" : "--uninstall-discord-screenshare";
            string what = install ? "install" : "removal";
            string message = install ? "Installing Discord screenshare support. OBS will close." : "Removing Discord screenshare support. OBS will close.";
            var argList = new List<string> { verb, "--start-delay-ms", "900" };

            if (BrowserCookies.TestIsAdmin())
            {
                string cmdLine = ProcessArgs.Quote(setupExe) + " " + ProcessArgs.Join(argList.ToArray());
                int pid = Native.SpawnDetached(cmdLine, cacheDir);
                if (pid <= 0) throw new InvalidOperationException("Could not start Discord screenshare " + what + ".");
                Log.Write("Discord screenshare " + what + " started detached as PID " + pid + ".");
                return new JObject { ["ok"] = true, ["processId"] = pid, ["message"] = message };
            }

            var psi = new ProcessStartInfo
            {
                FileName = setupExe,
                Arguments = ProcessArgs.Join(argList.ToArray()),
                WorkingDirectory = cacheDir,
                UseShellExecute = true,
                Verb = "runas",
            };
            var proc = Process.Start(psi);
            if (proc == null) throw new InvalidOperationException("Could not start Discord screenshare " + what + ".");
            Log.Write("Discord screenshare " + what + " started elevated as PID " + proc.Id + ".");
            return new JObject { ["ok"] = true, ["processId"] = proc.Id, ["message"] = message };
        }
    }
}

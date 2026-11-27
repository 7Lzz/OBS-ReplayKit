using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // starts the windowed --uninstall mode of the setup exe from the settings uninstall button; that window (twin of the setup window) removes everything, then boots OBS back up
    internal static class Uninstall
    {
        public static string GetSetupCacheDir() => AppConfig.GetReplayKitUserStateDir();

        public static string GetSetupExecutable() => Path.Combine(GetSetupCacheDir(), ReplayKitShared.ReplayKitFolder.SetupExe);

        public static JObject StartCleanupFromSettings(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) throw new InvalidOperationException(Locales.Message("update.missingUninstallConfirmation"));
            var incoming = JObject.Parse(body);
            if (incoming["confirm"]?.Value<string>() != "confirm") throw new InvalidOperationException(Locales.Message("update.invalidUninstallConfirmation"));
            bool keepUserSettings = true;
            var keepToken = incoming["keepUserSettings"];
            if (keepToken != null)
            {
                if (keepToken.Type != JTokenType.Boolean) throw new InvalidOperationException(Locales.Message("update.keepUserSettingsMustBeA"));
                keepUserSettings = keepToken.Value<bool>();
            }

            bool removeObs = false;
            var removeObsToken = incoming["removeObs"];
            if (removeObsToken != null)
            {
                if (removeObsToken.Type != JTokenType.Boolean) throw new InvalidOperationException(Locales.Message("update.removeObsMustBeAJson"));
                removeObs = removeObsToken.Value<bool>();
            }

            var argList = new List<string> { "--uninstall" };
            if (!keepUserSettings) argList.Add("--remove-user-settings");
            if (removeObs) argList.Add("--remove-obs");
            int pid = StartSetup(argList, Locales.Message("update.couldNotStartReplaykitUninstall"), "ReplayKit uninstall");
            return new JObject { ["ok"] = true, ["processId"] = pid, ["message"] = Locales.Message("update.uninstallStartedFollowTheProgressWindow") };
        }

        // narrower siblings of StartCleanupFromSettings -- install / remove just the OBS Stream Audio virtual cable instead of touching the whole ReplayKit install, for the Share Preview audio row next to the main uninstall.
        public static JObject StartDiscordScreenshareInstall() => RunDiscordScreenshareMode(true);

        public static JObject StartDiscordScreenshareRemoval() => RunDiscordScreenshareMode(false);

        private static JObject RunDiscordScreenshareMode(bool install)
        {
            string verb = install ? "--install-discord-screenshare" : "--uninstall-discord-screenshare";
            string failed = install ? Locales.Message("update.couldNotStartDiscordScreenshareInstall") : Locales.Message("update.couldNotStartDiscordScreenshareRemoval");
            int pid = StartSetup(new List<string> { verb, "--start-delay-ms", "900" }, failed, "Discord screenshare " + (install ? "install" : "removal"));

            // the setup only writes its own prefs.json and the helper reads discord_screenshare_enabled from the settings, so the setting follows here; only once the setup runs, since a setup that never started (the uac prompt turned down) changes nothing, and well inside its start delay, before it closes obs
            try
            {
                SettingsStore.Update(settings =>
                {
                    settings["discord_screenshare_enabled"] = install;
                    settings["discord_projector_enabled"] = install;
                });
            }
            catch (Exception ex) { Log.Write("Discord screenshare settings flip failed: " + ex.Message); }

            string message = install ? Locales.Message("common.installingDiscordScreenshareSupportObsWill") : Locales.Message("common.removingDiscordScreenshareSupportObsWill");
            return new JObject { ["ok"] = true, ["processId"] = pid, ["message"] = message };
        }

        // runs the cached setup exe with these arguments, detached when the helper is elevated already and through the uac prompt otherwise, and returns its pid; failed is the message when it does not start
        private static int StartSetup(List<string> argList, string failed, string what)
        {
            string cacheDir = Path.GetFullPath(GetSetupCacheDir());
            string setupExe = Path.GetFullPath(GetSetupExecutable());
            string cachePrefix = cacheDir.TrimEnd('\\') + "\\";
            if (!setupExe.StartsWith(cachePrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(Locales.Message("update.invalidSetupExecutablePath"));
            // a missing or damaged copy is downloaded again, checked against the release hash, before anything is started
            SetupExe.Ensure(true);

            if (Elevation.IsAdmin())
            {
                string cmdLine = ProcessArgs.Quote(setupExe) + " " + ProcessArgs.Join(argList.ToArray());
                int pid = Native.SpawnDetached(cmdLine, cacheDir);
                if (pid <= 0) throw new InvalidOperationException(failed);
                Log.Write(what + " started detached as PID " + pid + ".");
                return pid;
            }

            var psi = new ProcessStartInfo
            {
                FileName = setupExe,
                Arguments = ProcessArgs.Join(argList.ToArray()),
                WorkingDirectory = cacheDir,
                UseShellExecute = true,
                Verb = "runas",
            };
            using (var proc = Process.Start(psi))
            {
                if (proc == null) throw new InvalidOperationException(failed);
                Log.Write(what + " started elevated as PID " + proc.Id + ".");
                return proc.Id;
            }
        }
    }
}

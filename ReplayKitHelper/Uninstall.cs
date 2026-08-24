using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // triggers the setup exes --cleanup mode from the settings docks uninstall button. ported from obs_replaykit helper modules/65_uninstall.ps1.
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

            string cacheDir = Path.GetFullPath(GetSetupCacheDir());
            string setupExe = Path.GetFullPath(GetSetupExecutable());
            string cachePrefix = cacheDir.TrimEnd('\\') + "\\";
            if (!setupExe.StartsWith(cachePrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Invalid setup executable path.");
            if (!File.Exists(setupExe))
                throw new InvalidOperationException("ReplayKit setup executable is missing. Re-run the installer once, then uninstall from Advanced.");

            var argList = new List<string> { "--cleanup", "--start-delay-ms", "900" };
            if (!keepUserSettings) argList.Add("--remove-user-settings");

            if (BrowserCookies.TestIsAdmin())
            {
                string cmdLine = ProcessArgs.Quote(setupExe) + " " + ProcessArgs.Join(argList.ToArray());
                int cleanupPid = Native.SpawnDetached(cmdLine, cacheDir);
                if (cleanupPid <= 0) throw new InvalidOperationException("Could not start ReplayKit cleanup.");
                Log.Write("ReplayKit cleanup started detached as PID " + cleanupPid + ".");
                return new JObject { ["ok"] = true, ["processId"] = cleanupPid, ["message"] = "Uninstall started. OBS will close." };
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
            if (proc == null) throw new InvalidOperationException("Could not start ReplayKit cleanup.");
            Log.Write("ReplayKit cleanup started elevated as PID " + proc.Id + ".");
            return new JObject { ["ok"] = true, ["processId"] = proc.Id, ["message"] = "Uninstall started. OBS will close." };
        }
    }
}

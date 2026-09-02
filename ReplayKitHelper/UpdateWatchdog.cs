using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    internal static class UpdateWatchdog
    {
        public static int Run(string[] args)
        {
            int launcherPid = IntArg(args, "-LauncherPid");
            string pidFile = StringArg(args, "-PidFile");
            string resultPath = StringArg(args, "-Result");
            string obsPath = StringArg(args, "-ObsPath");
            string targetVersion = StringArg(args, "-TargetVersion");
            string releaseUrl = StringArg(args, "-ReleaseUrl");
            string versionPath = StringArg(args, "-VersionPath");
            if (launcherPid <= 0 || string.IsNullOrWhiteSpace(resultPath)) return 1;

            int installerPid = 0;
            DateTime handoff = DateTime.UtcNow.AddSeconds(45);
            while (DateTime.UtcNow < handoff && !File.Exists(resultPath))
            {
                installerPid = ReadPid(pidFile);
                if (installerPid > 0) break;
                Thread.Sleep(IsRunning(launcherPid) ? 1000 : 3000);
            }
            if (installerPid <= 0) installerPid = launcherPid;

            DateTime deadline = DateTime.UtcNow.AddMinutes(15);
            while (DateTime.UtcNow < deadline && IsRunning(installerPid)) Thread.Sleep(3000);
            if (File.Exists(resultPath)) return 0;

            string installedVersion = ReadVersion(versionPath);
            if (!IsObsRunning() && File.Exists(obsPath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(obsPath)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(obsPath),
                    });
                }
                catch (Exception ex) { Log.Write("Update watchdog could not relaunch OBS: " + ex.Message); }
            }

            bool installed = string.Equals(installedVersion, targetVersion, StringComparison.OrdinalIgnoreCase);
            var result = new JObject
            {
                ["ok"] = installed,
                ["stage"] = installed ? "done" : "aborted",
                ["message"] = installed
                    ? "ReplayKit " + targetVersion + " installed; OBS was restarted by the update watchdog."
                    : "The installer stopped before it finished. OBS was restarted; the update was not applied.",
                ["version"] = targetVersion ?? "",
                ["releaseUrl"] = releaseUrl ?? "",
                ["finishedAt"] = DateTime.UtcNow.ToString("o"),
            };
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(resultPath));
                File.WriteAllText(resultPath, result.ToString(Formatting.None));
            }
            catch (Exception ex) { Log.Write("Update watchdog could not write result: " + ex.Message); }
            return installed ? 0 : 1;
        }

        private static bool IsRunning(int pid)
        {
            try { using (var process = Process.GetProcessById(pid)) return !process.HasExited; }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        private static bool IsObsRunning()
        {
            foreach (var name in new[] { "obs64", "obs32", "obs" })
            {
                var processes = Process.GetProcessesByName(name);
                try { if (processes.Length > 0) return true; }
                finally { foreach (var process in processes) process.Dispose(); }
            }
            return false;
        }

        private static int ReadPid(string path)
        {
            try { return int.TryParse(File.ReadAllText(path).Trim(), out int pid) ? pid : 0; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return 0; }
        }

        private static string ReadVersion(string path)
        {
            try { return JObject.Parse(File.ReadAllText(path))["version"]?.Value<string>() ?? ""; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException) { return ""; }
        }

        private static string StringArg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++) if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return "";
        }

        private static int IntArg(string[] args, string name) => int.TryParse(StringArg(args, name), out int value) ? value : 0;
    }
}

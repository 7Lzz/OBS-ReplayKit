using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
            string legacyPidFile = StringArg(args, "-LegacyPidFile");
            string legacyResultPath = StringArg(args, "-LegacyResult");
            string obsPath = StringArg(args, "-ObsPath");
            string targetVersion = StringArg(args, "-TargetVersion");
            string releaseUrl = StringArg(args, "-ReleaseUrl");
            if (launcherPid <= 0 || string.IsNullOrWhiteSpace(resultPath)) return 1;

            int installerPid = 0;
            DateTime handoff = DateTime.UtcNow.AddSeconds(45);
            while (DateTime.UtcNow < handoff && !File.Exists(resultPath))
            {
                installerPid = ReadPid(pidFile);
                if (installerPid <= 0) installerPid = ReadPid(legacyPidFile);
                if (installerPid > 0) break;
                Thread.Sleep(IsRunning(launcherPid) ? 1000 : 3000);
            }
            if (installerPid <= 0) installerPid = launcherPid;

            while (!File.Exists(resultPath) && IsRunning(installerPid)) Thread.Sleep(3000);
            if (File.Exists(resultPath)) return 0;
            if (!File.Exists(pidFile) && CopyLegacyResult(legacyResultPath, resultPath, targetVersion, releaseUrl)) return 0;

            // A version file cannot confirm completion, especially during a same-version repair.
            var result = new JObject
            {
                ["ok"] = false,
                ["stage"] = "aborted",
                ["message"] = Locales.Message("update.theInstallerStoppedWithoutConfirmingCompletion"),
                ["version"] = targetVersion ?? "",
                ["releaseUrl"] = releaseUrl ?? "",
                ["finishedAt"] = DateTime.UtcNow.ToString("o"),
            };
            try
            {
                PublishResult(resultPath, result.ToString(Formatting.None));
            }
            catch (Exception ex) { Log.Write("Update watchdog could not write result: " + ex.Message); }
            if (!IsObsRunning() && File.Exists(obsPath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(obsPath)
                    {
                        Arguments = ReplayKitShared.ObsLaunch.Arguments,
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(obsPath),
                    })?.Dispose();
                }
                catch (Exception ex) { Log.Write("Update watchdog could not relaunch OBS: " + ex.Message); }
            }
            return 1;
        }

        private static bool CopyLegacyResult(string source, string target, string version, string releaseUrl)
        {
            if (string.IsNullOrEmpty(source) || !File.Exists(source)) return false;
            try
            {
                string text = File.ReadAllText(source);
                var result = JObject.Parse(text);
                if (result["ok"]?.Type != JTokenType.Boolean || result.Value<string>("stage") == "busy" ||
                    Update.NormalizeVersion(result.Value<string>("version")) != Update.NormalizeVersion(version) ||
                    !string.Equals(result.Value<string>("releaseUrl"), releaseUrl, StringComparison.OrdinalIgnoreCase))
                    return false;
                PublishResult(target, text);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                Log.Write("Update watchdog could not read the previous installer protocol: " + ex.Message);
                return false;
            }
        }

        private static void PublishResult(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, text);
                File.Move(temporary, path);
            }
            finally { File.Delete(temporary); }
        }

        // the installer runs elevated, and Process.HasExited asks for rights a process of the user never gets on it, which threw and ended the watchdog before it could write a result; limited query rights work across integrity levels
        internal static bool IsRunning(int pid)
        {
            IntPtr handle = Native.OpenParentForSync((uint)pid);
            if (handle == IntPtr.Zero) return System.Runtime.InteropServices.Marshal.GetLastWin32Error() == 5;
            try { return !Native.ParentExited(handle); }
            finally { Native.CloseParentHandle(handle); }
        }

        private static bool IsObsRunning()
        {
            foreach (var name in Constants.ObsProcessNames)
            {
                var processes = Process.GetProcessesByName(name);
                try { if (processes.Any(process => !Constants.IsRoleHost(process))) return true; }
                finally { foreach (var process in processes) process.Dispose(); }
            }
            return false;
        }

        internal static int ReadPid(string path)
        {
            if (string.IsNullOrEmpty(path)) return 0;
            try { return int.TryParse(File.ReadAllText(path).Trim(), out int pid) ? pid : 0; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return 0; }
        }

        private static string StringArg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++) if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return "";
        }

        private static int IntArg(string[] args, string name) => int.TryParse(StringArg(args, name), out int value) ? value : 0;
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;

namespace ReplayKitSetup
{
    // in-menu cleanup for OBS ReplayKit installs. ported from obs_replaykit/cleanup.py.
    public static class Cleanup
    {
        private static readonly string[] ReplaykitRuntimeStateRels =
        {
            "obs-replayKit/scripts/helper/clips_db.json",
            "obs-replayKit/scripts/helper/clips_index.json",
        };

        private static (int ExitCode, string Stdout, string Stderr) RunHidden(string fileName, string arguments, int timeoutMs = 30000)
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using (var proc = Process.Start(psi))
            {
                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                if (!proc.WaitForExit(timeoutMs))
                {
                    try { proc.Kill(); } catch (InvalidOperationException) { }
                    throw new TimeoutException(fileName + " timed out");
                }
                return (proc.ExitCode, stdout, stderr);
            }
        }

        private static int? CurrentParentPid()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher($"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId={Process.GetCurrentProcess().Id}"))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject row in results)
                    {
                        return Convert.ToInt32(row["ParentProcessId"]);
                    }
                }
                return null;
            }
            catch (ManagementException)
            {
                return null;
            }
        }

        // close obs and replaykit helper processes while keeping this setup process alive.
        public static bool StopObsAndHelpers(Action<string> log = null)
        {
            Obs.CloseObs(log);
            var keep = new HashSet<int> { Process.GetCurrentProcess().Id };
            var parent = CurrentParentPid();
            if (parent.HasValue) keep.Add(parent.Value);
            string keepCsv = string.Join(",", keep);
            string script =
                $"$keep = @({keepCsv})\r\n" +
                "Get-CimInstance Win32_Process |\r\n" +
                "  Where-Object { $_.Name -in @('OBSReplayKit.exe','OBSReplayKit-Encoder.exe') -and $keep -notcontains [int]$_.ProcessId } |\r\n" +
                "  ForEach-Object {\r\n" +
                "    try { Stop-Process -Id $_.ProcessId -Force -ErrorAction Stop } catch { }\r\n" +
                "  }\r\n";
            try
            {
                RunHidden("powershell.exe", Win32Args.Build("-NoProfile", "-NonInteractive", "-Command", script));
            }
            catch (Exception exc) when (exc is System.ComponentModel.Win32Exception || exc is TimeoutException)
            {
                log?.Invoke("warn: helper stop failed: " + exc.Message);
                return false;
            }
            return true;
        }

        public static bool RemoveReplaykitPlugins(Action<string> log = null)
        {
            string obsRoot = Config.PROGRAMFILES_OBS_DIR;
            var targets = new[]
            {
                Path.Combine(obsRoot, "obs-plugins", "64bit", "win-capture-audio.dll"),
                Path.Combine(obsRoot, "obs-plugins", "64bit", "win-capture-audio.pdb"),
                Path.Combine(obsRoot, "data", "obs-plugins", "win-capture-audio"),
                Path.Combine(obsRoot, "obs-plugins", "64bit", "input-overlay.dll"),
                Path.Combine(obsRoot, "obs-plugins", "64bit", "SDL2.dll"),
                Path.Combine(obsRoot, "data", "obs-plugins", "input-overlay"),
                Path.Combine(obsRoot, "obs-plugins", "64bit", "bongobs-cat.dll"),
                Path.Combine(obsRoot, "bin", "64bit", "Bango Cat"),
                Path.Combine(obsRoot, "data", "obs-plugins", "bongobs-cat"),
                Path.Combine(obsRoot, "obs-plugins", "64bit", "obs-composite-blur.dll"),
                Path.Combine(obsRoot, "obs-plugins", "64bit", "obs-composite-blur.pdb"),
                Path.Combine(obsRoot, "data", "obs-plugins", "obs-composite-blur"),
                Path.Combine(Config.PROGRAMDATA, "obs-studio", "plugins", "obs-composite-blur"),
                Path.Combine(obsRoot, "obs-plugins", "64bit", "obs-shaderfilter.dll"),
                Path.Combine(obsRoot, "obs-plugins", "64bit", "obs-shaderfilter.pdb"),
                Path.Combine(obsRoot, "data", "obs-plugins", "obs-shaderfilter"),
                Config.REPLAYKIT_TRAY_PLUGIN_DIR,
            };
            bool ok = true;
            foreach (var target in targets)
            {
                bool isDir = Directory.Exists(target);
                bool isFile = File.Exists(target);
                if (!isDir && !isFile) continue;
                try
                {
                    if (isDir) Directory.Delete(target, true);
                    else File.Delete(target);
                }
                catch (Exception exc) when (exc is IOException || exc is UnauthorizedAccessException)
                {
                    ok = false;
                    log?.Invoke("warn: could not remove " + target + ": " + exc.Message);
                }
            }
            return ok;
        }

        public static bool RemoveVirtualDisplayDriver(Action<string> log = null)
        {
            const string script = @"
$ErrorActionPreference = 'Continue'
$vddDevice = Get-PnpDevice -ErrorAction SilentlyContinue |
    Where-Object { $_.FriendlyName -eq 'Virtual Display Driver' } |
    Select-Object -First 1
if ($vddDevice) {
    Disable-PnpDevice -InstanceId $vddDevice.InstanceId -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
    pnputil.exe /remove-device $vddDevice.InstanceId | Out-Null
}
$drivers = pnputil.exe /enum-drivers | Out-String
$matches = [regex]::Matches($drivers, ""Published Name:\s+(oem\d+\.inf)\s+Original Name:\s+MttVDD\.inf"", ""IgnoreCase"")
foreach ($m in $matches) {
    pnputil.exe /delete-driver $m.Groups[1].Value /uninstall /force | Out-Null
}
if (Test-Path ""C:\IddSampleDriver"") {
    Remove-Item -Recurse -Force ""C:\IddSampleDriver"" -ErrorAction SilentlyContinue
}
";
            (int ExitCode, string Stdout, string Stderr) result;
            try
            {
                result = RunHidden("powershell.exe", Win32Args.Build("-NoProfile", "-NonInteractive", "-Command", script), 60000);
            }
            catch (Exception exc) when (exc is System.ComponentModel.Win32Exception || exc is TimeoutException)
            {
                log?.Invoke("warn: virtual display cleanup failed: " + exc.Message);
                return false;
            }
            if (result.ExitCode != 0)
            {
                string message = !string.IsNullOrEmpty(result.Stderr) ? result.Stderr : result.Stdout;
                string firstLine = string.IsNullOrWhiteSpace(message) ? "" : message.Trim().Split('\n')[0];
                log?.Invoke("warn: virtual display cleanup returned " + result.ExitCode + ": " + firstLine);
            }
            return result.ExitCode == 0;
        }

        public static bool WipeObsConfig(Action<string> log = null)
        {
            if (!Directory.Exists(Config.REPLAYKIT_CONFIG)) return true;
            try
            {
                Directory.Delete(Config.REPLAYKIT_CONFIG, true);
                return true;
            }
            catch (Exception exc) when (exc is IOException || exc is UnauthorizedAccessException)
            {
                log?.Invoke("warn: could not wipe " + Config.REPLAYKIT_CONFIG + ": " + exc.Message);
                return false;
            }
        }

        public static bool SaveUserSettings(Action<string> log = null)
        {
            bool ok = true;
            try
            {
                var prefs = Prefs.LoadPrefs();
                prefs.Save();
                log?.Invoke("ReplayKit user settings kept -> " + Prefs.PREFS_FILE);
            }
            catch (Exception exc)
            {
                ok = false;
                log?.Invoke("warn: could not keep ReplayKit user settings: " + exc.Message);
            }

            try
            {
                Directory.CreateDirectory(Config.REPLAYKIT_USER_STATE_CACHE);
                int kept = 0;
                foreach (var rel in ReplaykitRuntimeStateRels)
                {
                    string source = Path.Combine(Config.OBS_CONFIG, rel.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(source)) continue;
                    string target = Path.Combine(Config.REPLAYKIT_USER_STATE_CACHE, Path.GetFileName(rel));
                    File.Copy(source, target, true);
                    kept++;
                }
                if (kept > 0) log?.Invoke("ReplayKit clip state kept -> " + Config.REPLAYKIT_USER_STATE_CACHE);
            }
            catch (Exception exc) when (exc is IOException || exc is UnauthorizedAccessException)
            {
                ok = false;
                log?.Invoke("warn: could not keep ReplayKit clip state: " + exc.Message);
            }
            return ok;
        }

        public static bool RemoveUserSettings(Action<string> log = null)
        {
            var targets = new List<string>
            {
                Prefs.PREFS_FILE,
                Path.Combine(Config.REPLAYKIT_SETUP_CACHE, "prefs.json"),
                Path.Combine(Config.REPLAYKIT_SETUP_CACHE, "clips_state.json"),
            };

            bool ok = true;
            var seen = new HashSet<string>();
            foreach (var target in targets)
            {
                string key;
                try { key = Path.GetFullPath(target).ToLowerInvariant(); }
                catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException) { continue; }
                if (!seen.Add(key)) continue;
                if (!File.Exists(target)) continue;
                try
                {
                    File.Delete(target);
                }
                catch (Exception exc) when (exc is IOException || exc is UnauthorizedAccessException)
                {
                    ok = false;
                    log?.Invoke("warn: could not remove " + target + ": " + exc.Message);
                }
            }
            if (Directory.Exists(Config.REPLAYKIT_USER_STATE_CACHE))
            {
                try
                {
                    Directory.Delete(Config.REPLAYKIT_USER_STATE_CACHE, true);
                }
                catch (Exception exc) when (exc is IOException || exc is UnauthorizedAccessException)
                {
                    ok = false;
                    log?.Invoke("warn: could not remove " + Config.REPLAYKIT_USER_STATE_CACHE + ": " + exc.Message);
                }
            }
            return ok;
        }

        public static List<string> RunCleanup(IInstallProgress progress, bool keepUserSettings = true)
        {
            var steps = new List<(string Title, string Detail, Func<object> Action)>
            {
                ("Close OBS", "Stops OBS and ReplayKit helpers, but keeps this setup window alive.", () => StopObsAndHelpers(progress.LogLine)),
                ("Remove launch permission", "Deletes the ReplayKit scheduled task.", () => ScheduledTask.DeleteElevationTask(progress.LogLine)),
                ("Remove Windows startup", "Stops ReplayKit from launching OBS when Windows signs in.", () => Startup.ConfigureObsStartup(false, progress.LogLine)),
                ("Remove Windows sleep override", "Restores default Windows sleep behavior for OBS.", () => SleepOverride.RemoveSleepOverride(progress.LogLine)),
                ("Remove OBS plugins", "Deletes ReplayKit OBS plugins from the OBS install folder.", () => RemoveReplaykitPlugins(progress.LogLine)),
                ("Remove OBS Stream Audio", "Uninstalls the ReplayKit virtual audio device.", () => VbCable.UninstallVbcable(progress.LogLine)),
                ("Remove virtual display driver", "Deletes the optional virtual display driver if it exists.", () => RemoveVirtualDisplayDriver(progress.LogLine)),
            };
            if (keepUserSettings)
                steps.Add(("Keep ReplayKit settings", "Saves current ReplayKit settings for the next install.", () => SaveUserSettings(progress.LogLine)));
            else
                steps.Add(("Remove ReplayKit settings", "Deletes saved ReplayKit preferences.", () => RemoveUserSettings(progress.LogLine)));
            steps.Add(("Wipe OBS ReplayKit config", "Deletes ReplayKit's OBS config folder while preserving OBS scenes and profiles.", () => WipeObsConfig(progress.LogLine)));

            progress.TotalSteps = steps.Count;
            for (int i = 0; i < steps.Count; i++)
            {
                Cli.RunApplyStep(progress, i + 1, steps[i].Title, steps[i].Detail, steps[i].Action);
            }
            progress.Render(progress.TotalSteps, "Cleanup complete", "OBS ReplayKit changes were removed.", "done");
            return progress.Issues;
        }
    }
}

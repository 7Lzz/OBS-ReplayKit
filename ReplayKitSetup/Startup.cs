using System;
using System.Diagnostics;
using System.IO;

namespace ReplayKitSetup
{
    // windows startup integration for launching obs with replaykit. ported from obs_replaykit/startup.py (the already-fixed version -- plain temp .ps1 invoked with -File, not the base64-encoded -EncodedCommand pattern that AV heuristics flag).
    public static class Startup
    {
        private const string ValueName = "OBS ReplayKit";
        private const string ShortcutName = "OBS ReplayKit.lnk";

        private static string StartupFolder()
        {
            string appdata = Config.APPDATA;
            return appdata == null ? null : Path.Combine(appdata, "Microsoft", "Windows", "Start Menu", "Programs", "Startup");
        }

        private static string StartupShortcutPath()
        {
            string folder = StartupFolder();
            return folder == null ? null : Path.Combine(folder, ShortcutName);
        }

        private static bool RemoveLegacyRunValue(Action<string> log)
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true))
                {
                    if (key?.GetValue(ValueName) != null)
                    {
                        key.DeleteValue(ValueName, throwOnMissingValue: false);
                        log?.Invoke("Windows startup: removed legacy registry entry");
                    }
                }
                return true;
            }
            catch (Exception exc) when (exc is System.Security.SecurityException || exc is UnauthorizedAccessException || exc is IOException)
            {
                log?.Invoke("warn: could not remove legacy Windows startup registry entry: " + exc.Message);
                return false;
            }
        }

        private static readonly string ShortcutScript =
            "param([string]$ShortcutPath, [string]$TargetPath, [string]$Arguments, [string]$WorkingDirectory)\r\n" +
            "$ErrorActionPreference = 'Stop'\r\n" +
            "$shell = New-Object -ComObject WScript.Shell\r\n" +
            "$shortcut = $shell.CreateShortcut($ShortcutPath)\r\n" +
            "$shortcut.TargetPath = $TargetPath\r\n" +
            "$shortcut.Arguments = $Arguments\r\n" +
            "$shortcut.WorkingDirectory = $WorkingDirectory\r\n" +
            "$shortcut.IconLocation = \"$TargetPath,0\"\r\n" +
            "$shortcut.Description = 'Start OBS ReplayKit when Windows signs in.'\r\n" +
            "$shortcut.Save()\r\n";

        private static bool CreateStartupShortcut(Action<string> log)
        {
            string obsExe = Obs.FindObsExe();
            string shortcutPath = StartupShortcutPath();
            if (obsExe == null)
            {
                log?.Invoke("warn: OBS install not found - Windows startup was not changed");
                return false;
            }
            if (shortcutPath == null)
            {
                log?.Invoke("warn: Windows Startup folder was not found");
                return false;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath));
            }
            catch (Exception exc) when (exc is IOException || exc is UnauthorizedAccessException)
            {
                log?.Invoke("warn: could not create Windows Startup folder: " + exc.Message);
                return false;
            }

            string scriptPath = Path.Combine(Path.GetTempPath(), "obsreplaykit_startup_" + Guid.NewGuid().ToString("N") + ".ps1");
            try
            {
                File.WriteAllText(scriptPath, ShortcutScript, new System.Text.UTF8Encoding(false));
                Process proc;
                try
                {
                    string args = Win32Args.Build(
                        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath,
                        "-ShortcutPath", shortcutPath, "-TargetPath", obsExe,
                        "-Arguments", Obs.OBS_START_ARGS, "-WorkingDirectory", Path.GetDirectoryName(obsExe));
                    var psi = new ProcessStartInfo("powershell.exe", args)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    proc = Process.Start(psi);
                }
                catch (Exception exc) when (exc is System.ComponentModel.Win32Exception || exc is InvalidOperationException)
                {
                    log?.Invoke("warn: could not create Windows startup shortcut: " + exc.Message);
                    return false;
                }

                string stderr = proc.StandardError.ReadToEnd();
                string stdout = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(20000);

                if (proc.ExitCode != 0)
                {
                    string message = !string.IsNullOrEmpty(stderr) ? stderr : stdout;
                    string firstLine = string.IsNullOrWhiteSpace(message) ? proc.ExitCode.ToString() : message.Trim().Split('\n')[0];
                    log?.Invoke("warn: could not create Windows startup shortcut: " + firstLine);
                    return false;
                }
            }
            finally
            {
                try { File.Delete(scriptPath); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }

            log?.Invoke("Windows startup: created shortcut at " + shortcutPath);
            return true;
        }

        private static bool DeleteStartupShortcut(Action<string> log)
        {
            string shortcutPath = StartupShortcutPath();
            if (shortcutPath == null) return true;
            try
            {
                if (File.Exists(shortcutPath)) File.Delete(shortcutPath);
                log?.Invoke("Windows startup: removed Startup folder shortcut");
                return true;
            }
            catch (Exception exc) when (exc is IOException || exc is UnauthorizedAccessException)
            {
                log?.Invoke("warn: could not remove Windows startup shortcut: " + exc.Message);
                return false;
            }
        }

        // add or remove obs replaykit from the current users windows startup apps.
        public static bool ConfigureObsStartup(bool enabled, Action<string> log = null)
        {
            if (enabled)
            {
                bool registryRemoved = RemoveLegacyRunValue(log);
                bool created = CreateStartupShortcut(log);
                if (created)
                {
                    registryRemoved = RemoveLegacyRunValue(log) && registryRemoved;
                    log?.Invoke("Windows startup: OBS will start when you sign in");
                }
                return created && registryRemoved;
            }

            bool shortcutRemoved = DeleteStartupShortcut(log);
            bool regRemoved = RemoveLegacyRunValue(log);
            log?.Invoke("Windows startup: off");
            return shortcutRemoved && regRemoved;
        }
    }
}

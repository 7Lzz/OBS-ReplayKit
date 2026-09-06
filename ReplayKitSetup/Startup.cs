using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

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

        // wscript.shell is late-bound com; InvokeMember routes through IDispatch at runtime and needs no managed metadata, so the trimmer's warnings here are moot -- it never removes anything this path depends on.
        [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "com idispatch dispatch, no trimmable managed members")]
        [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "com idispatch dispatch, no trimmable managed members")]
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

            // builds the .lnk directly thru the same WScript.Shell COM object powershell used underneath -- in-process, no powershell.exe subprocess, no -ExecutionPolicy Bypass, and no temp script dropped to disk first. writing a script to temp then running it with the execution policy forced off is exactly the shape av heuristics flag; this does the identical shortcut creation without any of that.
            object shell = null;
            object shortcut = null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                shell = Activator.CreateInstance(shellType);
                shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
                Type shortcutType = shortcut.GetType();
                shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { obsExe });
                shortcutType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { Obs.OBS_START_ARGS });
                shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(obsExe) });
                shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { obsExe + ",0" });
                shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "Start OBS ReplayKit when Windows signs in." });
                shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            }
            catch (Exception exc) when (exc is COMException || exc is TargetInvocationException || exc is MissingMemberException)
            {
                log?.Invoke("warn: could not create Windows startup shortcut: " + exc.Message);
                return false;
            }
            finally
            {
                if (shortcut != null) Marshal.ReleaseComObject(shortcut);
                if (shell != null) Marshal.ReleaseComObject(shell);
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

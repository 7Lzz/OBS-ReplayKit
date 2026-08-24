using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace ReplayKitSetup
{
    // obs process management: locate obs64.exe, close running instances, clear crash flags, relaunch. ported from obs_replaykit/obs.py.
    public static class Obs
    {
        public const string OBS_START_ARGS =
            "--background-color=ff272a33 " +
            "--default-background-color=ff272a33 " +
            "--disable-direct-composition-video-overlays";

        // best detected obs executable path, or null.
        public static string FindObsExe() => Config.FindObsExeCandidate();

        // taskkill any running obs process. returns the kill count.
        public static int CloseObs(Action<string> log = null)
        {
            int killed = 0;
            foreach (var proc in Config.OBS_PROCESSES)
            {
                // no /T: this runs from the update flow, where this very process can be a descendant of obs64.exe (helper -> updater) -- /T walks the whole tree by parent pid regardless of job/breakaway state, so it would take the updater down with obs mid-copy. obs-browser-page.exe is reaped separately below instead.
                var result = RunTaskkill(proc);
                if (result == 0)
                {
                    log?.Invoke("killed " + proc);
                    killed++;
                }
            }
            // cef renderer children outlive obs64.exe otherwise and keep plugin_config/obs-browser locked, which stalls the backup step right after this.
            RunTaskkill("obs-browser-page.exe");
            if (killed > 0) Thread.Sleep(1500); // give the process tree time to actualy exit
            return killed;
        }

        private static int RunTaskkill(string imageName)
        {
            try
            {
                var psi = new ProcessStartInfo("taskkill", $"/F /IM {imageName}")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using (var proc = Process.Start(psi))
                {
                    proc.WaitForExit();
                    return proc.ExitCode;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            {
                return -1;
            }
        }

        // remove safe_mode + .sentinel/* so obs doesnt show its crash-recovery prompt on the next launch.
        public static void CleanupCrashFlags(Action<string> log = null)
        {
            string safeMode = Path.Combine(Config.OBS_CONFIG, "safe_mode");
            if (File.Exists(safeMode) || Directory.Exists(safeMode))
            {
                try
                {
                    if (File.Exists(safeMode)) File.Delete(safeMode);
                    else Directory.Delete(safeMode, true);
                    log?.Invoke("removed safe_mode flag");
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                }
            }

            string sentinel = Path.Combine(Config.OBS_CONFIG, ".sentinel");
            if (Directory.Exists(sentinel))
            {
                int cleared = 0;
                foreach (var item in Directory.EnumerateFileSystemEntries(sentinel))
                {
                    try
                    {
                        if (File.Exists(item)) File.Delete(item);
                        else Directory.Delete(item, true);
                        cleared++;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                    }
                }
                if (cleared > 0) log?.Invoke($"cleared .sentinel ({cleared} item(s))");
            }
        }

        private static string VbsLiteral(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

        // start obs from a temporary gui-script host so native plugins cannot write to our console.
        private static bool LaunchObsViaWscript(string obsExe, Action<string> log)
        {
            string command = $"\"{obsExe}\" {OBS_START_ARGS}";
            string obsDir = Path.GetDirectoryName(obsExe);
            string script =
                "Option Explicit\r\n" +
                "On Error Resume Next\r\n\r\n" +
                "Dim shell, fso, scriptPath, rc\r\n" +
                "Set shell = CreateObject(\"WScript.Shell\")\r\n" +
                "shell.CurrentDirectory = " + VbsLiteral(obsDir) + "\r\n" +
                "rc = 0\r\n" +
                "shell.Run " + VbsLiteral(command) + ", 1, False\r\n" +
                "If Err.Number <> 0 Then rc = 1\r\n\r\n" +
                "scriptPath = WScript.ScriptFullName\r\n" +
                "Set fso = CreateObject(\"Scripting.FileSystemObject\")\r\n" +
                "fso.DeleteFile scriptPath, True\r\n\r\n" +
                "WScript.Quit rc\r\n";

            string scriptPath = Path.Combine(Path.GetTempPath(), "obsreplaykit_launch_" + Guid.NewGuid().ToString("N") + ".vbs");
            try
            {
                File.WriteAllText(scriptPath, script, Encoding.Unicode);
            }
            catch (Exception exc) when (exc is IOException || exc is UnauthorizedAccessException)
            {
                log?.Invoke("failed to prepare OBS launcher: " + exc.Message);
                return false;
            }

            try
            {
                var psi = new ProcessStartInfo("wscript.exe", Win32Args.Build("//B", scriptPath))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetTempPath(),
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    RedirectStandardInput = false,
                };
                Process.Start(psi);
                return true;
            }
            catch (Exception exc) when (exc is System.ComponentModel.Win32Exception || exc is InvalidOperationException)
            {
                try { File.Delete(scriptPath); }
                catch (Exception ex2) when (ex2 is IOException || ex2 is UnauthorizedAccessException) { }
                log?.Invoke("failed to launch OBS through detached helper: " + exc.Message);
                return false;
            }
        }

        // launch obs without attaching it to the setup console.
        public static bool LaunchObs(Action<string> log = null)
        {
            string obsExe = FindObsExe();
            if (obsExe == null)
            {
                log?.Invoke("OBS install was not found - install OBS, set OBS_REPLAYKIT_OBS_EXE, or re-run from an active OBS install.");
                return false;
            }

            log?.Invoke("starting " + obsExe);
            return LaunchObsViaWscript(obsExe, log);
        }
    }
}

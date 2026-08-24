using System;
using System.Diagnostics;
using System.IO;

namespace ReplayKitSetup
{
    // register the OBSReplayKit-Elevate task scheduler entry so the in-obs lua can relaunch obs elevated without a uac popup on every launch. ported from obs_replaykit/scheduled_task.py.
    public static class ScheduledTask
    {
        // public so the lua script + any later uninstaller share the exact name. renaming this orphans existing installs.
        public const string TASK_NAME = "OBSReplayKit-Elevate";

        // where the relauncher vbs lives after install_obs_config runs. the task action points here; the vbs reads its runtime args from %temp%\obsreplaykit_elevate.txt when invoked without arguments.
        private static readonly string RelauncherVbs = Path.Combine(Config.OBS_CONFIG, "obs-replayKit", "scripts", "obs_elevation", "hidden_relauncher.vbs");

        // task xml notes: runlevel=highestavailable elevates only for users already in administrators (no fake elevation for standard users); empty <triggers/> + allowstartondemand makes it on-demand only; hidden=true keeps it out of the defualt task scheduler view; //b suppresses wscripts error popups; the xml must be utf-16 with bom or schtasks /xml rejects it with a misleading "incorrectly formatted or out of range" error.
        private static string TaskXml(string vbsPath) => "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
            "<Task version=\"1.4\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
            "  <RegistrationInfo>\r\n" +
            "    <Description>OBS ReplayKit -- elevated OBS relaunch helper (UAC-free per-launch)</Description>\r\n" +
            "    <Author>OBS ReplayKit</Author>\r\n" +
            "  </RegistrationInfo>\r\n" +
            "  <Principals>\r\n" +
            "    <Principal id=\"Author\">\r\n" +
            "      <LogonType>InteractiveToken</LogonType>\r\n" +
            "      <RunLevel>HighestAvailable</RunLevel>\r\n" +
            "    </Principal>\r\n" +
            "  </Principals>\r\n" +
            "  <Settings>\r\n" +
            "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
            "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
            "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
            "    <AllowHardTerminate>true</AllowHardTerminate>\r\n" +
            "    <StartWhenAvailable>false</StartWhenAvailable>\r\n" +
            "    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\r\n" +
            "    <IdleSettings>\r\n" +
            "      <StopOnIdleEnd>false</StopOnIdleEnd>\r\n" +
            "      <RestartOnIdle>false</RestartOnIdle>\r\n" +
            "    </IdleSettings>\r\n" +
            "    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n" +
            "    <Enabled>true</Enabled>\r\n" +
            "    <Hidden>true</Hidden>\r\n" +
            "    <RunOnlyIfIdle>false</RunOnlyIfIdle>\r\n" +
            "    <WakeToRun>false</WakeToRun>\r\n" +
            "    <ExecutionTimeLimit>PT5M</ExecutionTimeLimit>\r\n" +
            "    <Priority>4</Priority>\r\n" +
            "  </Settings>\r\n" +
            "  <Triggers/>\r\n" +
            "  <Actions Context=\"Author\">\r\n" +
            "    <Exec>\r\n" +
            "      <Command>wscript.exe</Command>\r\n" +
            "      <Arguments>//B \"" + vbsPath + "\"</Arguments>\r\n" +
            "    </Exec>\r\n" +
            "  </Actions>\r\n" +
            "</Task>\r\n";

        // create or overwrite the OBSReplayKit-Elevate scheduled task. on any failure the lua falls back to shellexecuteex+uac, so a false here just means the user keeps seeing a per-launch popup.
        public static bool InstallElevationTask(Action<string> log = null)
        {
            if (!File.Exists(RelauncherVbs))
            {
                log?.Invoke($"warn: {Path.GetFileName(RelauncherVbs)} not present at {RelauncherVbs}; elevation task install skipped (Lua will fall back to per-launch UAC)");
                return false;
            }

            string xml = TaskXml(RelauncherVbs);
            string xmlPath = Path.Combine(Path.GetTempPath(), "obsreplaykit_task_" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                // utf-16 (not utf-16-le) so the bom schtasks looks for is included.
                File.WriteAllText(xmlPath, xml, System.Text.Encoding.Unicode);

                int exitCode;
                string output;
                // /f overwrites an existing task so re-apply is idempotent. create_no_window suppresses the schtasks console flash.
                try
                {
                    var result = RunSchtasksWithOutput(Win32Args.Build("/Create", "/TN", TASK_NAME, "/XML", xmlPath, "/F"));
                    exitCode = result.ExitCode;
                    output = result.Output;
                }
                catch (Exception exc) when (exc is System.ComponentModel.Win32Exception || exc is TimeoutException)
                {
                    log?.Invoke("warn: schtasks /Create failed: " + exc.Message);
                    return false;
                }

                if (exitCode != 0)
                {
                    string firstLine = FirstLine(output) ?? $"exit {exitCode}";
                    log?.Invoke("warn: schtasks /Create returned " + exitCode + ": " + firstLine);
                    return false;
                }

                log?.Invoke($"installed scheduled task '{TASK_NAME}' (no per-launch UAC popup)");
                return true;
            }
            finally
            {
                try { File.Delete(xmlPath); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }

        private static (int ExitCode, string Output) RunSchtasksWithOutput(string arguments)
        {
            var psi = new ProcessStartInfo("schtasks.exe", arguments)
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
                if (!proc.WaitForExit(20000))
                {
                    try { proc.Kill(); } catch (InvalidOperationException) { }
                    throw new TimeoutException("schtasks timed out");
                }
                return (proc.ExitCode, string.IsNullOrEmpty(stderr) ? stdout : stderr);
            }
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var trimmed = text.Trim();
            int idx = trimmed.IndexOfAny(new[] { '\r', '\n' });
            return idx >= 0 ? trimmed.Substring(0, idx) : trimmed;
        }

        // cheap pre-check so an idempotent re-apply can skip the install. any schtasks failure is treated as "not installed".
        public static bool IsElevationTaskInstalled()
        {
            try
            {
                var result = RunSchtasksWithOutput($"/Query /TN {TASK_NAME}");
                return result.ExitCode == 0;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is TimeoutException)
            {
                return false;
            }
        }

        // delete the ReplayKit scheduled task. safe when the task is not present.
        public static bool DeleteElevationTask(Action<string> log = null)
        {
            (int ExitCode, string Output) result;
            try
            {
                result = RunSchtasksWithOutput($"/Delete /TN {TASK_NAME} /F");
            }
            catch (Exception exc) when (exc is System.ComponentModel.Win32Exception || exc is TimeoutException)
            {
                log?.Invoke("warn: schtasks /Delete failed: " + exc.Message);
                return false;
            }
            if (result.ExitCode == 0)
            {
                log?.Invoke($"removed scheduled task '{TASK_NAME}'");
                return true;
            }
            string lowerText = (result.Output ?? "").ToLowerInvariant();
            if (lowerText.Contains("cannot find") || lowerText.Contains("does not exist")) return true;
            log?.Invoke("warn: schtasks /Delete returned " + result.ExitCode + ": " + (FirstLine(result.Output) ?? ""));
            return false;
        }
    }
}

using System;
using System.Diagnostics;

namespace ReplayKitSetup
{
    // used to register the OBSReplayKit-Elevate task so the in-obs lua could relaunch obs elevated without a uac popup on every launch -- removed becuase a hidden, highest-privilege, unconditionally-installed scheduled task is exactly the shape av heuristics flag as a persistence mechanism, and it was doing that on every install regardless of whether run-as-admin was even turned on. run-as-admin now always goes thru a normal UAC prompt instead; this class only remains to delete the task from installs that already have one.
    public static class ScheduledTask
    {
        // public so the uninstaller and DeleteElevationTask share the exact name of whatever older releases may have registered.
        public const string TASK_NAME = "OBSReplayKit-Elevate";

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
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();
                if (!proc.WaitForExit(20000))
                {
                    try { proc.Kill(); } catch (InvalidOperationException) { }
                    throw new TimeoutException("schtasks timed out");
                }
                string stdout = stdoutTask.GetAwaiter().GetResult();
                string stderr = stderrTask.GetAwaiter().GetResult();
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

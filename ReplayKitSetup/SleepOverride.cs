using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReplayKitSetup
{
    // drop obs power requests so windows idle monitor/system sleep can still run. ported from obs_replaykit/sleep_override.py.
    public static class SleepOverride
    {
        private const string ObsExeName = "obs64.exe";
        private static readonly string[] RequestFlags = { "DISPLAY", "SYSTEM", "AWAYMODE" };

        // subprocess.run wrapper with our standard timeout + create_no_window flags. null on win32exception/timeout.
        private static (int ExitCode, string Stdout, string Stderr)? RunPowercfg(string arguments, Action<string> log = null)
        {
            try
            {
                var psi = new ProcessStartInfo("powercfg.exe", arguments)
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
                    if (!proc.WaitForExit(10000))
                    {
                        try { proc.Kill(); } catch (InvalidOperationException) { }
                        log?.Invoke("warn: powercfg " + arguments + " failed: timed out");
                        return null;
                    }
                    return (proc.ExitCode, stdout, stderr);
                }
            }
            catch (System.ComponentModel.Win32Exception exc)
            {
                log?.Invoke("warn: powercfg " + arguments + " failed: " + exc.Message);
                return null;
            }
        }

        private static HashSet<string> InstalledRequestFlags()
        {
            var flags = new HashSet<string>();
            var result = RunPowercfg("/requestsoverride");
            if (result == null || result.Value.ExitCode != 0) return flags;
            foreach (var rawLine in result.Value.Stdout.Split('\n'))
            {
                string stripped = rawLine.Trim();
                if (stripped.Length == 0) continue;
                if (stripped.ToLowerInvariant().StartsWith(ObsExeName.ToLowerInvariant()))
                {
                    string tail = stripped.Substring(ObsExeName.Length);
                    foreach (var flag in RequestFlags)
                    {
                        if (Regex.IsMatch(tail, @"\b" + Regex.Escape(flag) + @"\b", RegexOptions.IgnoreCase)) flags.Add(flag);
                    }
                }
            }
            return flags;
        }

        // true iff all replaykit-managed obs power request overrides are already in place.
        public static bool IsSleepOverrideInstalled()
        {
            var installed = InstalledRequestFlags();
            return RequestFlags.All(installed.Contains);
        }

        // ignore obs display/system/awaymode requests so windows sleep timers still work.
        public static bool InstallSleepOverride(Action<string> log = null)
        {
            if (IsSleepOverrideInstalled())
            {
                log?.Invoke($"sleep override already in place ({ObsExeName} -> {string.Join("/", RequestFlags)})");
                return true;
            }

            var result = RunPowercfg(Win32Args.Build(new[] { "/requestsoverride", "PROCESS", ObsExeName }.Concat(RequestFlags).ToArray()), log);
            if (result == null) return false;
            if (result.Value.ExitCode != 0)
            {
                string message = !string.IsNullOrEmpty(result.Value.Stderr) ? result.Value.Stderr : result.Value.Stdout;
                string errLine = string.IsNullOrWhiteSpace(message) ? "exit " + result.Value.ExitCode : message.Trim().Split('\n')[0];
                log?.Invoke("warn: powercfg /requestsoverride failed: " + errLine);
                return false;
            }

            log?.Invoke($"sleep override installed ({ObsExeName} -> {string.Join("/", RequestFlags)} requests ignored)");
            log?.Invoke("Windows monitor and PC sleep timers can now run while OBS/ReplayKit is active.");
            return true;
        }

        // drop the override entry. powercfg /requestsoverride process <exe> with no request-type flags after the exe name removes the entry entirely. safe to call when no override exists (powercfg exits 0).
        public static bool RemoveSleepOverride(Action<string> log = null)
        {
            var result = RunPowercfg(Win32Args.Build("/requestsoverride", "PROCESS", ObsExeName), log);
            if (result == null) return false;
            if (result.Value.ExitCode != 0)
            {
                string message = !string.IsNullOrEmpty(result.Value.Stderr) ? result.Value.Stderr : result.Value.Stdout;
                string errLine = string.IsNullOrWhiteSpace(message) ? "exit " + result.Value.ExitCode : message.Trim().Split('\n')[0];
                log?.Invoke("warn: powercfg remove override failed: " + errLine);
                return false;
            }
            log?.Invoke("sleep override removed for " + ObsExeName);
            return true;
        }
    }
}

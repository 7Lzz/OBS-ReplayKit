using System;
using System.Diagnostics;

namespace ReplayKitHelper
{
    // shared curl.exe subprocess runner. every streamable api call in this port goes thru curl.exe rather than HttpClient, matching the ps originals own choice (documented in upload_worker.ps1: PowerShell 5.1s Invoke-WebRequest is unreliable when winhttp proxy auto-detect is slow or misconfigured, hanging silently before hitting the wire -- curl.exe, built into windows 10+, sidesteps that). the ps original duplicated this spawn logic separately in 10_auth_core.ps1 / upload_worker.ps1 / transcode_poll_worker.ps1 becuase the latter two ran as seperate processes with no access to the formers functions; that constraint doesnt apply here, so its one shared implementation.
    internal static class Curl
    {
        public struct Result
        {
            public int ExitCode;
            public string Stdout;
            public string Stderr;
        }

        // onStarted fires right after the process launches, letting a caller (Upload.cs) publish the live Process onto the current job record so a cancel request can kill whichever curl step happens to be running -- the ps original got this for free since its cancel killed the whole upload_worker.ps1 wrapper process tree; there is no equivalent single wrapper process here to kill instead.
        public static Result Run(string[] args, Action<Process> onStarted)
        {
            var psi = new ProcessStartInfo
            {
                FileName = System.IO.Path.Combine(Environment.SystemDirectory, "curl.exe"),
                Arguments = ProcessArgs.Join(args),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var proc = Process.Start(psi))
            {
                onStarted?.Invoke(proc);
                var stdout = proc.StandardOutput.ReadToEndAsync();
                var stderr = proc.StandardError.ReadToEndAsync();
                proc.WaitForExit();
                return new Result { ExitCode = proc.ExitCode, Stdout = stdout.GetAwaiter().GetResult(), Stderr = stderr.GetAwaiter().GetResult() };
            }
        }

        public static Result Run(params string[] args) => Run(args, null);
    }
}

using System;
using System.Diagnostics;
using System.Threading;

namespace ReplayKitHelper
{
    // shared curl.exe runner for the streamable api calls. curl ships with windows 10+ and does not stall on a slow or misconfigured proxy auto-detect, so it is used instead of an in-process http client
    internal static class Curl
    {
        public struct Result
        {
            public int ExitCode;
            public string Stdout;
            public string Stderr;
        }

        // a cancel or the two minute deadline kills whichever curl step is running
        public static Result Run(string[] args, CancellationToken cancellationToken)
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
            cancellationToken.ThrowIfCancellationRequested();
            using (var proc = Process.Start(psi))
            using (var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2)))
            using (var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token))
            using (stopping.Token.Register(() => { try { if (!proc.HasExited) proc.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } }))
            {
                var stdout = proc.StandardOutput.ReadToEndAsync();
                var stderr = proc.StandardError.ReadToEndAsync();
                proc.WaitForExit();
                cancellationToken.ThrowIfCancellationRequested();
                if (deadline.IsCancellationRequested) throw new TimeoutException("Network worker exceeded its time limit.");
                return new Result { ExitCode = proc.ExitCode, Stdout = stdout.GetAwaiter().GetResult(), Stderr = stderr.GetAwaiter().GetResult() };
            }
        }

        public static Result Run(params string[] args) => Run(args, CancellationToken.None);
    }
}

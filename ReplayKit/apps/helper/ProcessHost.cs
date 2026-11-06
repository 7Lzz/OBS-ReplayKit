using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace ReplayKitHelper
{
    internal static class ProcessHost
    {
        internal static bool Hosted;
        internal static string HelperExecutable => typeof(Program).Assembly.Location;

        internal static string CommandLine(IEnumerable<string> arguments)
        {
            var args = arguments.ToList();
            if (Hosted) args.Insert(0, "--replaykit-helper");
            return ProcessArgs.Quote(Process.GetCurrentProcess().MainModule.FileName) + " " + ProcessArgs.Join(args.ToArray());
        }
    }

    public static class HostEntry
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        public static int Run(string commandLine)
        {
            IntPtr parsed = IntPtr.Zero;
            try
            {
                if (commandLine == null || commandLine.Length > 32767) throw new ArgumentException("Invalid host command line.");
                parsed = CommandLineToArgvW(commandLine, out int count);
                if (parsed == IntPtr.Zero || count < 2) throw new ArgumentException("Missing helper role.");
                var args = new string[count];
                for (int i = 0; i < count; i++) args[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(parsed, i * IntPtr.Size));
                if (args[1] != "--replaykit-helper" || !string.Equals(Path.GetFullPath(args[0]),
                    Path.GetFullPath(Process.GetCurrentProcess().MainModule.FileName), StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Invalid helper host.");
                ProcessHost.Hosted = true;
                // a runtime started by a native host has no entry assembly to read the target framework from, so .net 4.8 keeps its legacy tls defaults (ssl3 and tls 1.0) that github and streamable refuse; these must be set before anything touches the network
                AppContext.SetSwitch("Switch.System.Net.DontEnableSystemDefaultTlsVersions", false);
                AppContext.SetSwitch("Switch.System.Net.DontEnableSchUseStrongCrypto", false);
                return Program.Main(args.Skip(2).ToArray());
            }
            catch (Exception ex)
            {
                Program.WriteCrashReport("host_entry", ex, true);
                return 1;
            }
            finally { if (parsed != IntPtr.Zero) LocalFree(parsed); }
        }
    }
}

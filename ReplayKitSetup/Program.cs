using System;
using System.IO;
using System.Linq;
using System.Text;

namespace ReplayKitSetup
{
    // OBS ReplayKit setup entry point. ported from main.py.
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Contains("--update")) FastExit.HideConsoleWindow();
            FastExit.InstallConsoleCloseHandler();

            int rc = Run(args);
            FastExit.FastExitNow(rc);
            return rc; // unreachable -- FastExitNow terminates the process directly, matching pythons fast_exit(main()) at module scope.
        }

        // pythons KeyboardInterrupt/SystemExit catches have no equivalent here: InstallConsoleCloseHandler already intercepts ctrl+c/window-close at the native handler level above, and FastExitNow terminates without raising a catchable exception, so neither branch has anything to adapt.
        private static int Run(string[] args)
        {
            try
            {
                int? updateRc = Update.TryRunUpdateFromArgv(args);
                if (updateRc.HasValue) return updateRc.Value;
                return Cli.RunCli();
            }
            catch (Exception ex)
            {
                string tb = ex.ToString();
                string logPath = null;
                try
                {
                    logPath = Path.Combine(Config.BUNDLE_ROOT, "OBSReplayKit-error.log");
                    File.WriteAllText(logPath, tb, new UTF8Encoding(false));
                }
                catch (Exception)
                {
                }
                try
                {
                    Console.WriteLine("OBS ReplayKit setup failed.");
                    if (logPath != null) Console.WriteLine($"Details were written to: {logPath}");
                    Console.WriteLine();
                    Console.WriteLine(tb);
                    Console.Write("Press Enter to exit...");
                    Console.ReadLine();
                }
                catch (Exception)
                {
                }
                return 1;
            }
        }
    }
}

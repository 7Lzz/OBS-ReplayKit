using System;
using System.IO;
using System.Linq;
using System.Text;

namespace ReplayKitSetup
{
    // OBS ReplayKit setup entry point. ported from main.py.
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // headless dock-button / auto-updater modes detach from any inherited console so a dying parent cant kill them partway thru an install (see FastExit.DetachFromConsole); the normal run is a windows app with no console at all.
            if (IsHeadlessMode(args)) FastExit.DetachFromConsole();

            int rc = Run(args);
            AssetBundle.Cleanup();
            FastExit.FastExitNow(rc);
            return rc; // unreachable -- FastExitNow terminates the process directly, matching pythons fast_exit(main()) at module scope.
        }

        // the flags Update.TryRunUpdateFromArgv handles -- all spawned detached from a dock button with no user at a console.
        private static bool IsHeadlessMode(string[] args) =>
            args.Contains("--update") || args.Contains("--cleanup") ||
            args.Contains("--install-discord-screenshare") || args.Contains("--uninstall-discord-screenshare");

        // pythons KeyboardInterrupt/SystemExit catches have no equivalent here: InstallConsoleCloseHandler already intercepts ctrl+c/window-close at the native handler level above, and FastExitNow terminates without raising a catchable exception, so neither branch has anything to adapt.
        private static int Run(string[] args)
        {
            try
            {
                int? updateRc = Update.TryRunUpdateFromArgv(args);
                if (updateRc.HasValue) return updateRc.Value;
                return InstallerApp.Run();
            }
            catch (Exception ex)
            {
                string tb = ex.ToString();
                try { SetupLog.Line("FATAL: " + tb); } catch (Exception) { }
                string logPath = null;
                try
                {
                    string candidate = Path.Combine(AppContext.BaseDirectory, "OBSReplayKit-error.log");
                    File.WriteAllText(candidate, tb, new UTF8Encoding(false));
                    logPath = candidate;
                }
                catch (Exception)
                {
                }
                try { if (logPath == null) logPath = SetupLog.Path; } catch (Exception) { }
                // headless modes have no ui and their record is the log file; the windowed run surfaces the failure in a dialog.
                if (!IsHeadlessMode(args)) InstallerApp.ShowFatalError(tb, logPath);
                return 1;
            }
        }
    }
}

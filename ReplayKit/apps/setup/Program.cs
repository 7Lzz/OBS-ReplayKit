using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// every dll this exe imports is a windows one, and the setup runs as admin from the folder it was downloaded to, so a dll of the same name dropped next to it must never be the one loaded
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace ReplayKitSetup
{
    // OBS ReplayKit setup entry point
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // the .net 8 build ran with invariant globalization, so formatting, parsing and casing never followed the culture of the machine, and every thread here does the same
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;

            // headless dock-button / auto-updater modes detach from any inherited console so a dying parent cant kill them partway thru an install (see FastExit.DetachFromConsole); the normal run is a windows app with no console at all.
            if (IsHeadlessMode(args)) FastExit.DetachFromConsole();

            int rc = Run(args);
            AssetBundle.Cleanup();
            FastExit.FastExitNow(rc);
            return rc; // unreachable -- FastExitNow terminates the process directly
        }

        // the modes that run without a setup window or console
        private static bool IsHeadlessMode(string[] args) =>
            args.Contains("--update") || args.Contains("--cleanup") || args.Contains(ConfigBackups.ClearFlag) ||
            args.Contains("--install-discord-screenshare") || args.Contains("--uninstall-discord-screenshare");

        private static int Run(string[] args)
        {
            try
            {
                if (!NetFramework.IsInstalled())
                {
                    SetupLog.Line(".NET Framework 4.8 is not installed; setup stopped before doing anything");
                    if (!IsHeadlessMode(args)) NetFramework.OfferInstall(args);
                    return 1;
                }
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

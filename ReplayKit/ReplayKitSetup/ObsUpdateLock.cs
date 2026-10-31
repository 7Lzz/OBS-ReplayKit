using System;
using System.IO;
using System.Text;

namespace ReplayKitSetup
{
    // turns off the updater of obs itself, so obs only changes version when a replaykit release moves obs-versions.json and its add-ons with it. obs 32 and 33 both read this file at startup (obs-main.cpp: disable_updater beside bin\) and then skip the startup check, grey out help > check for updates and repair, and hide the update settings
    public static class ObsUpdateLock
    {
        public static string MarkerPath => Path.Combine(Config.PROGRAMFILES_OBS_DIR, "disable_updater.txt");

        private const string MarkerText = "OBS ReplayKit turned off the OBS updater so OBS stays on a version ReplayKit supports. ReplayKit updates OBS itself when it supports a newer one. Uninstalling ReplayKit removes this file.\r\n";

        public static bool Lock(Action<string> log = null)
        {
            if (!Obs.CoreInstalledAt(Config.PROGRAMFILES_OBS_DIR))
            {
                log?.Invoke("OBS is not installed at " + Config.PROGRAMFILES_OBS_DIR + "; no OBS updater to turn off");
                return false;
            }
            try
            {
                if (File.Exists(MarkerPath) && File.ReadAllText(MarkerPath) == MarkerText)
                {
                    log?.Invoke("OBS updater already off: " + MarkerPath);
                    return true;
                }
                File.WriteAllText(MarkerPath, MarkerText, new UTF8Encoding(false));
                log?.Invoke("turned off the OBS updater: " + MarkerPath);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log?.Invoke("warn: could not turn off the OBS updater: " + ex.Message);
                return false;
            }
        }
    }
}

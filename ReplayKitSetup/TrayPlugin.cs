using System;
using System.IO;

namespace ReplayKitSetup
{
    // installs the bundled ReplayKit plugin (clips / share preview / restart obs tray items) -- unlike the other obs plugins here, this one is first-party: compiled from source at release time and bundled straight into assets/, nothing to download or extract. ported from obs_replaykit/tray_plugin.py.
    public static class TrayPlugin
    {
        // copy the bundled replaykit.dll into obss no-admin plugin search path; assumes obs is already closed (the apply flow closes it first), since the dll would otherwise be locked, same as updating any other loaded plugin.
        public static bool InstallReplaykitTrayPlugin(Action<string> log = null)
        {
            if (!File.Exists(Config.REPLAYKIT_TRAY_DLL_BUNDLED))
            {
                log?.Invoke($"(no {Path.GetFileName(Config.REPLAYKIT_TRAY_DLL_BUNDLED)} bundled, skipping tray plugin)");
                return false;
            }

            string target = Config.REPLAYKIT_TRAY_DLL_TARGET;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(Config.REPLAYKIT_TRAY_DLL_BUNDLED, target, true);
            }
            catch (Exception exc) when (exc is IOException || exc is UnauthorizedAccessException)
            {
                log?.Invoke("warn: could not install replaykit-tray plugin: " + exc.Message);
                return false;
            }

            log?.Invoke("installed -> " + target);
            log?.Invoke("    OBS will load it on next launch (View Clips appears in the tray menu)");
            return true;
        }
    }
}

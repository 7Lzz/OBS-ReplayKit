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

            RemoveLegacyTrayPlugins(log);
            log?.Invoke("installed -> " + target);
            log?.Invoke("    OBS will load it on next launch (View Clips appears in the tray menu)");
            return true;
        }

        // the plugin used to be replaykit-tray.dll, first in its own plugins\replaykit-tray\ folder and later beside the current dll. obs loads whatever it finds, so a leftover copy is loaded next to the new one and every tray item it registers appears twice. renaming rather than only deleting matters because a rename still succeeds on a dll obs currently has loaded, which is what stops it coming back on the next launch even when the delete cannot happen.
        public static void RemoveLegacyTrayPlugins(Action<string> log = null)
        {
            NeutralizeLegacyDll(Config.REPLAYKIT_TRAY_LEGACY_DLL_IN_CURRENT_DIR, log);
            NeutralizeLegacyDll(Path.Combine(Config.REPLAYKIT_TRAY_LEGACY_PLUGIN_DIR, "bin", "64bit", "replaykit-tray.dll"), log);

            if (!Directory.Exists(Config.REPLAYKIT_TRAY_LEGACY_PLUGIN_DIR)) return;
            try
            {
                Directory.Delete(Config.REPLAYKIT_TRAY_LEGACY_PLUGIN_DIR, true);
                log?.Invoke("removed legacy plugin folder -> " + Config.REPLAYKIT_TRAY_LEGACY_PLUGIN_DIR);
            }
            catch (Exception exc) when (exc is IOException || exc is UnauthorizedAccessException)
            {
                log?.Invoke("warn: legacy plugin folder left in place (its dll is disabled): " + exc.Message);
            }
        }

        private static void NeutralizeLegacyDll(string path, Action<string> log)
        {
            if (!File.Exists(path)) return;
            try
            {
                File.Delete(path);
                log?.Invoke("removed legacy plugin -> " + path);
                return;
            }
            catch (Exception exc) when (exc is IOException || exc is UnauthorizedAccessException)
            {
                log?.Invoke("legacy plugin is locked, disabling instead: " + exc.Message);
            }

            string disabled = path + ".disabled";
            try
            {
                if (File.Exists(disabled)) File.Delete(disabled);
                File.Move(path, disabled);
                log?.Invoke("disabled legacy plugin -> " + disabled);
            }
            catch (Exception exc) when (exc is IOException || exc is UnauthorizedAccessException)
            {
                log?.Invoke("warn: could not disable legacy plugin " + path + ": " + exc.Message);
            }
        }
    }
}

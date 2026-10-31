using System;
using System.IO;

namespace ReplayKitSetup
{
    // installs the bundled ReplayKit plugin (window chrome, tray menu, notifications, hotkeys) -- unlike the other obs plugins here, this one is first-party: compiled from source at release time and bundled straight into assets/, nothing to download or extract.
    public static class NativePlugin
    {
        // true when this build carries a plugin to install
        public static bool IsBundled() => File.Exists(Config.REPLAYKIT_PLUGIN_DLL_BUNDLED);

        // obs 33 reads plugins\replaykit\replaykit.dll first and marks the bin\64bit copy legacy, while older obs only reads bin\64bit, so the layout follows the line of the installed obs
        private static string TargetPath(bool modern) =>
            modern ? Path.Combine(Config.REPLAYKIT_PLUGIN_DIR, "replaykit.dll") : Path.Combine(Config.REPLAYKIT_PLUGIN_DIR, "bin", "64bit", "replaykit.dll");

        // the installed plugin is byte for byte the bundled one
        public static bool IsCurrent()
        {
            try
            {
                var line = ObsVersions.Current;
                if (line == null) return false;
                string target = TargetPath(line.ModernPlugins);
                if (!IsBundled() || !File.Exists(target)) return false;
                return HashOf(Config.REPLAYKIT_PLUGIN_DLL_BUNDLED) == HashOf(target);
            }
            catch (Exception exc) when (exc is IOException || exc is UnauthorizedAccessException || exc is InvalidDataException)
            {
                return false;
            }
        }

        private static string HashOf(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        }

        // copy the bundled replaykit.dll into obss no-admin plugin search path; assumes obs is already closed (the apply flow closes it first), since the dll would otherwise be locked, same as updating any other loaded plugin.
        public static bool InstallReplaykitPlugin(Action<string> log = null)
        {
            if (!File.Exists(Config.REPLAYKIT_PLUGIN_DLL_BUNDLED))
            {
                log?.Invoke($"(no {Path.GetFileName(Config.REPLAYKIT_PLUGIN_DLL_BUNDLED)} bundled, skipping the plugin)");
                return false;
            }

            var line = ObsVersions.Current;
            if (line == null)
            {
                log?.Invoke("warn: the OBS version could not be read, so where OBS loads the ReplayKit plugin from is unknown");
                return false;
            }
            bool modern = line.ModernPlugins;
            string target = TargetPath(modern);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                // a virus scanner or the indexer can hold a freshly closed dll for a moment, so the copy is tried again for a while before it counts as failed
                Installer.WriteWithRetry(() => File.Copy(Config.REPLAYKIT_PLUGIN_DLL_BUNDLED, target, true));
            }
            catch (Exception exc) when (exc is IOException || exc is UnauthorizedAccessException)
            {
                log?.Invoke("warn: could not install the replaykit plugin: " + exc.Message);
                return false;
            }
            if (!IsCurrent())
            {
                log?.Invoke("warn: the installed replaykit plugin is not the bundled one after the copy");
                return false;
            }

            RemoveLegacyTrayPlugins(log);
            // a copy in the other layout is from before obs changed line; obs 33 skips it, but an older obs would load it instead of this one
            NeutralizeLegacyDll(TargetPath(!modern), log);
            log?.Invoke("installed -> " + target);
            log?.Invoke("    OBS will load it on next launch (the ReplayKit window, notifications and tray menu come from it)");
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

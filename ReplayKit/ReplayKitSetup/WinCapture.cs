using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace ReplayKitSetup
{
    // install the win-capture-audio obs plugin -- powers the "Desktop Audio (excl. Discord)" source via windows 10+ per-process loopback. without it the scenes audio_capture source cant instantiate and obs has no desktop audio. gpl-2.0, from bozbez/win-capture-audio. the bundled zip is the replaykit build of Ayrton09s maintained fork, made by utils/obs-plugins/win-capture-audio from the vendored source and its patches; the 2022 release that older setups put in is replaced by it
    public static class WinCapture
    {
        private static string DllPath() => Path.Combine(Config.PROGRAMFILES_OBS_DIR, Config.WIN_CAPTURE_AUDIO_DLL_REL.Replace('/', Path.DirectorySeparatorChar));

        // true iff the win-capture-audio dll is installed for obs.
        private static bool IsInstalled() => File.Exists(DllPath());

        // true when this build carries the plugin and there is an obs to install it into, so a missing or old dll is a problem and not just a machine that has no use for it
        public static bool IsExpected() => File.Exists(Config.WIN_CAPTURE_AUDIO_ZIP) && Obs.CoreInstalledAt(Config.PROGRAMFILES_OBS_DIR);

        // the installed dll is byte for byte the one in the bundled zip
        public static bool IsCurrent()
        {
            try
            {
                if (!IsInstalled()) return false;
                string bundled = BundledDllHash();
                return bundled != null && string.Equals(InstalledDllHash(), bundled, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception exc) when (exc is IOException || exc is InvalidDataException || exc is UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static string HashOf(Stream stream) => Convert.ToHexString(SHA256.HashData(stream));

        // the sha256 of the dll inside the bundled zip, null when the zip has none
        private static string BundledDllHash()
        {
            using (var archive = ZipFile.OpenRead(Config.WIN_CAPTURE_AUDIO_ZIP))
            {
                var entry = archive.GetEntry(Config.WIN_CAPTURE_AUDIO_DLL_REL);
                if (entry == null) return null;
                using (var stream = entry.Open()) return HashOf(stream);
            }
        }

        private static string InstalledDllHash()
        {
            using (var stream = new FileStream(DllPath(), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) return HashOf(stream);
        }

        // extract the bundled plugin into the obs install. idempotent: the bundled dll is put in when none is installed or when the installed one is not the bundled one, and left alone when it is. assumes obs is closed like the other plugin installs
        public static bool InstallWinCaptureAudio(Action<string> log = null)
        {
            if (!File.Exists(Config.WIN_CAPTURE_AUDIO_ZIP))
            {
                log?.Invoke($"(no {Path.GetFileName(Config.WIN_CAPTURE_AUDIO_ZIP)} bundled, skipping)");
                return false;
            }

            if (!Obs.CoreInstalledAt(Config.PROGRAMFILES_OBS_DIR))
            {
                log?.Invoke($"OBS not installed at {Config.PROGRAMFILES_OBS_DIR} (no bin\\64bit\\obs64.exe) - install OBS first, then re-run");
                return false;
            }

            string bundled;
            try { bundled = BundledDllHash(); }
            catch (Exception exc) when (exc is IOException || exc is InvalidDataException || exc is UnauthorizedAccessException)
            {
                log?.Invoke("failed to read the bundled plugin: " + exc.Message);
                return false;
            }
            if (bundled == null)
            {
                log?.Invoke("the bundled plugin zip has no " + Config.WIN_CAPTURE_AUDIO_DLL_REL);
                return false;
            }

            if (IsInstalled())
            {
                string installed = null;
                try { installed = InstalledDllHash(); }
                catch (Exception exc) when (exc is IOException || exc is UnauthorizedAccessException) { log?.Invoke("warn: could not read the installed plugin: " + exc.Message); }
                if (string.Equals(installed, bundled, StringComparison.OrdinalIgnoreCase))
                {
                    log?.Invoke("win-capture-audio plugin already installed");
                    return true;
                }
                log?.Invoke("replacing the installed win-capture-audio plugin with the bundled build");
            }

            log?.Invoke("extracting " + Path.GetFileName(Config.WIN_CAPTURE_AUDIO_ZIP) + " -> " + Config.PROGRAMFILES_OBS_DIR);

            try
            {
                // a virus scanner or the indexer can hold a freshly closed dll for a moment, so the extraction is tried again for a while before it counts as failed
                Installer.WriteWithRetry(() => ZipFile.ExtractToDirectory(Config.WIN_CAPTURE_AUDIO_ZIP, Config.PROGRAMFILES_OBS_DIR, overwriteFiles: true));
            }
            catch (UnauthorizedAccessException exc)
            {
                log?.Invoke($"permission denied writing to {Config.PROGRAMFILES_OBS_DIR}: {exc.Message}");
                log?.Invoke("(this script must run elevated -- the installer .exe self-elevates)");
                return false;
            }
            catch (Exception exc)
            {
                log?.Invoke("failed to extract plugin: " + exc.Message);
                return false;
            }

            if (!IsInstalled())
            {
                log?.Invoke("warn: plugin DLL not found after extraction at expected path " + DllPath());
                return false;
            }
            if (!IsCurrent())
            {
                log?.Invoke("warn: the installed win-capture-audio plugin is not the bundled one after extraction");
                return false;
            }

            log?.Invoke("installed -> " + DllPath());
            log?.Invoke("    obs loads it on its next launch");
            return true;
        }
    }
}

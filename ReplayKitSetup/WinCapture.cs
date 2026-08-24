using System;
using System.IO;
using System.IO.Compression;

namespace ReplayKitSetup
{
    // install the win-capture-audio obs plugin -- powers the "Desktop Audio (excl. Discord)" source via windows 10+ per-process loopback. without it the scenes audio_capture source cant instantiate and obs has no desktop audio. gpl-2.0 from bozbez/win-capture-audio. ported from obs_replaykit/wincapture.py.
    public static class WinCapture
    {
        private static string DllPath() => Path.Combine(Config.PROGRAMFILES_OBS_DIR, Config.WIN_CAPTURE_AUDIO_DLL_REL.Replace('/', Path.DirectorySeparatorChar));

        // true iff the win-capture-audio dll is installed for obs.
        private static bool IsInstalled() => File.Exists(DllPath());

        // extract the bundled plugin into the obs install. idempotent.
        public static bool InstallWinCaptureAudio(Action<string> log = null)
        {
            if (IsInstalled())
            {
                log?.Invoke("win-capture-audio plugin already installed");
                return true;
            }

            if (!File.Exists(Config.WIN_CAPTURE_AUDIO_ZIP))
            {
                log?.Invoke($"(no {Path.GetFileName(Config.WIN_CAPTURE_AUDIO_ZIP)} bundled, skipping)");
                return false;
            }

            if (!Directory.Exists(Config.PROGRAMFILES_OBS_DIR))
            {
                log?.Invoke($"OBS not installed at {Config.PROGRAMFILES_OBS_DIR} - install OBS first, then re-run");
                return false;
            }

            log?.Invoke("extracting " + Path.GetFileName(Config.WIN_CAPTURE_AUDIO_ZIP) + " -> " + Config.PROGRAMFILES_OBS_DIR);

            try
            {
                ZipFile.ExtractToDirectory(Config.WIN_CAPTURE_AUDIO_ZIP, Config.PROGRAMFILES_OBS_DIR);
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

            log?.Invoke("installed -> " + DllPath());
            log?.Invoke("    OBS will pick it up on next launch (Sources -> Add -> Application Audio Output Capture)");
            return true;
        }
    }
}

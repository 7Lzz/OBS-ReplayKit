using System;
using System.IO;
using System.Security.Cryptography;

namespace ReplayKitSetup
{
    // install the bundled obs shaderfilter plugin used by replaykit motion blur
    public static class ShaderFilter
    {
        private const string PluginDllRel = "obs-plugins/64bit/obs-shaderfilter.dll";
        private const string PluginPdbRel = "obs-plugins/64bit/obs-shaderfilter.pdb";
        private const string PluginDataRel = "data/obs-plugins/obs-shaderfilter";

        private static readonly string PluginDll = Path.Combine(Config.PROGRAMFILES_OBS_DIR, "obs-plugins", "64bit", "obs-shaderfilter.dll");
        private static readonly string PluginData = Path.Combine(Config.PROGRAMFILES_OBS_DIR, "data", "obs-plugins", "obs-shaderfilter");
        private static readonly string MotionBlurShader = Path.Combine(PluginData, "examples", "motion_blur.shader");

        private static readonly string[] CompositeBlurTargets =
        {
            Path.Combine(Config.PROGRAMFILES_OBS_DIR, "obs-plugins", "64bit", "obs-composite-blur.dll"),
            Path.Combine(Config.PROGRAMFILES_OBS_DIR, "obs-plugins", "64bit", "obs-composite-blur.pdb"),
            Path.Combine(Config.PROGRAMFILES_OBS_DIR, "data", "obs-plugins", "obs-composite-blur"),
            Path.Combine(Config.PROGRAMDATA, "obs-studio", "plugins", "obs-composite-blur"),
        };

        private static bool AllowedArchiveMember(string relText)
        {
            return relText == PluginDllRel || relText == PluginPdbRel || relText.StartsWith(PluginDataRel + "/");
        }

        public static bool IsShaderfilterInstalled() => File.Exists(Config.SHADERFILTER_ZIP) && VerifyArchive(Config.SHADERFILTER_ZIP) &&
            BundledFiles.IsCurrent(Config.SHADERFILTER_ZIP, Config.PROGRAMFILES_OBS_DIR, include: AllowedArchiveMember);

        private static bool VerifyArchive(string path, Action<string> log = null)
        {
            string actual;
            try
            {
                using (var sha = SHA256.Create())
                using (var fh = File.OpenRead(path))
                {
                    actual = BitConverter.ToString(sha.ComputeHash(fh)).Replace("-", "").ToLowerInvariant();
                }
            }
            catch (Exception exc) when (exc is IOException || exc is UnauthorizedAccessException)
            {
                log?.Invoke("OBS Shaderfilter archive could not be read: " + exc.Message);
                return false;
            }
            if (actual != Config.SHADERFILTER_ZIP_SHA256)
            {
                log?.Invoke("OBS Shaderfilter archive hash mismatch: " + actual);
                return false;
            }
            return true;
        }

        // extract obs shaderfilter from the bundled archive into the obs install root.
        public static bool InstallShaderfilterPlugin(Action<string> log = null)
        {
            if (IsShaderfilterInstalled())
            {
                log?.Invoke("OBS Shaderfilter already installed at " + PluginDll);
                return true;
            }
            if (!Obs.CoreInstalledAt(Config.PROGRAMFILES_OBS_DIR))
            {
                log?.Invoke("OBS is not installed at " + Config.PROGRAMFILES_OBS_DIR + " (no bin\\64bit\\obs64.exe); skipping OBS Shaderfilter");
                return false;
            }
            if (!File.Exists(Config.SHADERFILTER_ZIP))
            {
                log?.Invoke(Path.GetFileName(Config.SHADERFILTER_ZIP) + " is not bundled in assets; skipping OBS Shaderfilter");
                return false;
            }
            if (!VerifyArchive(Config.SHADERFILTER_ZIP, log)) return false;

            log?.Invoke("extracting " + Path.GetFileName(Config.SHADERFILTER_ZIP) + " -> " + Config.PROGRAMFILES_OBS_DIR);

            try
            {
                if (!BundledFiles.Install(Config.SHADERFILTER_ZIP, Config.PROGRAMFILES_OBS_DIR, log, include: AllowedArchiveMember)) return false;
            }
            catch (Exception exc)
            {
                log?.Invoke("OBS Shaderfilter extraction failed: " + exc.Message);
                return false;
            }

            if (!File.Exists(PluginDll))
            {
                log?.Invoke("OBS Shaderfilter extraction finished but " + PluginDll + " is missing");
                return false;
            }
            if (!File.Exists(MotionBlurShader))
            {
                log?.Invoke("OBS Shaderfilter extraction finished but " + MotionBlurShader + " is missing");
                return false;
            }

            log?.Invoke("installed OBS Shaderfilter -> " + PluginDll);
            log?.Invoke("                        + " + MotionBlurShader);
            return true;
        }

        // remove the retired composite blur plugin previously used by replaykit.
        public static bool RemoveCompositeBlurPlugin(Action<string> log = null)
        {
            bool ok = true;
            foreach (var target in CompositeBlurTargets)
            {
                bool isDir = Directory.Exists(target);
                bool isFile = File.Exists(target);
                if (!isDir && !isFile) continue;
                try
                {
                    if (isDir) Directory.Delete(target, true);
                    else File.Delete(target);
                    log?.Invoke("removed retired Composite Blur path: " + target);
                }
                catch (Exception exc) when (exc is IOException || exc is UnauthorizedAccessException)
                {
                    ok = false;
                    log?.Invoke("warn: could not remove retired Composite Blur path " + target + ": " + exc.Message);
                }
            }
            return ok;
        }

        // install the current motion blur plugin and remove the retired one.
        public static bool InstallReplaykitMotionBlurPlugin(Action<string> log = null)
        {
            bool removed = RemoveCompositeBlurPlugin(log);
            bool installed = InstallShaderfilterPlugin(log);
            return removed && installed;
        }
    }
}

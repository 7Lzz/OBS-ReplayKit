using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace ReplayKitSetup
{
    // install the Bango Cat folder (modes, pictures, live2d models) that the bongo cat source of replaykit.dll draws, and take away the opengl bongobs-cat plugin older installs put next to it
    public static class BongoCat
    {
        private const string ZipRoot = "Bango Cat/";
        private static readonly string PluginData = Path.Combine(Config.PROGRAMFILES_OBS_DIR, "bin", "64bit", "Bango Cat");
        private static readonly string[] LegacyPlugin =
        {
            Path.Combine(Config.PROGRAMFILES_OBS_DIR, "obs-plugins", "64bit", "bongobs-cat.dll"),
            Path.Combine(Config.PROGRAMFILES_OBS_DIR, "data", "obs-plugins", "bongobs-cat"),
        };

        private static bool IsSafeRelative(string[] parts)
        {
            return parts.Length > 0 && parts.All(p => p != "" && p != "." && p != "..");
        }

        private static string TargetPath(string relPosix)
        {
            string targetRoot = Path.GetFullPath(Config.PROGRAMFILES_OBS_DIR).TrimEnd('\\', '/');
            var parts = relPosix.Split('/');
            string target = Path.GetFullPath(Path.Combine(new[] { Config.PROGRAMFILES_OBS_DIR }.Concat(parts).ToArray()));
            if (!string.Equals(target, targetRoot, StringComparison.OrdinalIgnoreCase) &&
                !target.StartsWith(targetRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("unsafe archive path: " + relPosix);
            }
            return target;
        }

        // every bundled file present at its bundled size, not just the dll and the data folder: a partial or older copy used to count as installed, so running the installer again never repaired it
        public static bool IsBongoCatInstalled()
        {
            if (!Directory.Exists(PluginData)) return false;
            if (!File.Exists(Config.BONGO_CAT_ZIP)) return true;
            try
            {
                using (var zf = ZipFile.OpenRead(Config.BONGO_CAT_ZIP))
                {
                    foreach (var entry in zf.Entries)
                    {
                        string name = entry.FullName.Replace("\\", "/");
                        if (!name.StartsWith(ZipRoot) || name.EndsWith("/")) continue;
                        string relText = name.Substring(ZipRoot.Length).Trim('/');
                        if (relText.Length == 0) continue;
                        var installed = new FileInfo(TargetPath(relText));
                        if (!installed.Exists || installed.Length != entry.Length) return false;
                    }
                }
            }
            catch (Exception exc) when (exc is IOException || exc is InvalidDataException || exc is InvalidOperationException || exc is UnauthorizedAccessException)
            {
                return false;
            }
            return true;
        }

        // the old plugin is only files; obs is closed while setup runs, and a file that cannot go is reported and left, since nothing loads its source any more
        private static void RemoveLegacyPlugin(Action<string> log)
        {
            foreach (var path in LegacyPlugin)
            {
                try
                {
                    if (File.Exists(path)) { File.Delete(path); log?.Invoke("removed the old Bongo Cat plugin file " + path); }
                    else if (Directory.Exists(path)) { Directory.Delete(path, true); log?.Invoke("removed the old Bongo Cat plugin folder " + path); }
                }
                catch (Exception exc) when (exc is IOException || exc is UnauthorizedAccessException)
                {
                    log?.Invoke("could not remove the old Bongo Cat plugin at " + path + ": " + exc.Message);
                }
            }
        }

        // extract the Bango Cat folder of the archive into the obs install root.
        public static bool InstallBongoCat(Action<string> log = null)
        {
            RemoveLegacyPlugin(log);
            if (IsBongoCatInstalled())
            {
                log?.Invoke("Bongo Cat files already installed at " + PluginData);
                return true;
            }
            if (!Obs.CoreInstalledAt(Config.PROGRAMFILES_OBS_DIR))
            {
                log?.Invoke("OBS is not installed at " + Config.PROGRAMFILES_OBS_DIR + " (no bin\\64bit\\obs64.exe); skipping Bongo Cat");
                return false;
            }
            if (!File.Exists(Config.BONGO_CAT_ZIP))
            {
                log?.Invoke(Path.GetFileName(Config.BONGO_CAT_ZIP) + " is not bundled in assets; skipping Bongo Cat");
                return false;
            }

            log?.Invoke("extracting " + Path.GetFileName(Config.BONGO_CAT_ZIP) + " -> " + Config.PROGRAMFILES_OBS_DIR);

            try
            {
                using (var zf = ZipFile.OpenRead(Config.BONGO_CAT_ZIP))
                {
                    foreach (var entry in zf.Entries)
                    {
                        string name = entry.FullName.Replace("\\", "/");
                        if (!name.StartsWith(ZipRoot)) continue;
                        string relText = name.Substring(ZipRoot.Length).Trim('/');
                        if (relText.Length == 0) continue;
                        var parts = relText.Split('/');
                        if (!IsSafeRelative(parts)) throw new InvalidOperationException("unsafe archive path: " + entry.FullName);
                        string dest = TargetPath(relText);
                        bool isDir = name.EndsWith("/") && entry.Length == 0;
                        if (isDir)
                        {
                            Directory.CreateDirectory(dest);
                            continue;
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        using (var src = entry.Open())
                        using (var dst = File.Open(dest, FileMode.Create, FileAccess.Write))
                        {
                            src.CopyTo(dst);
                        }
                    }
                }
            }
            catch (Exception exc)
            {
                log?.Invoke("Bongo Cat extraction failed: " + exc.Message);
                return false;
            }

            if (!Directory.Exists(PluginData))
            {
                log?.Invoke("Bongo Cat extraction finished but " + PluginData + " is missing");
                return false;
            }

            log?.Invoke("installed -> " + PluginData);
            return true;
        }
    }
}

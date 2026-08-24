using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using Microsoft.Win32;

namespace ReplayKitSetup
{
    // constants + resolved paths derived from env vars and the local obs install. ported from obs_replaykit/config.py.
    public static class Config
    {
        public static readonly string USERNAME;
        public static readonly string USERPROFILE;
        public static readonly string APPDATA;
        public static readonly string LOCALAPPDATA;
        public static readonly string PROGRAMDATA;

        public static readonly string OBS_CONFIG;

        // consolidated runtime root for everything obs replaykit installs (lua scripts, dock html, input-overlay presets). keeps the obs-studio tree easy to reason about and lets cleanup remove replaykit-owned config without touching obs scenes/profiles.
        public static readonly string REPLAYKIT_CONFIG;
        public static readonly string REPLAYKIT_SETUP_CACHE;
        public static readonly string REPLAYKIT_SETUP_EXE;
        public static readonly string REPLAYKIT_USER_STATE_CACHE;

        // dock html the local helper serves on 127.0.0.1:8767.
        public static readonly string DOCK_TARGET;

        public static readonly string[] OBS_PROCESSES = { "obs64.exe", "obs32.exe", "obs.exe" };

        private static readonly string _DEFAULT_OBS_DIR;

        public static readonly string[] OBS_EXE_CANDIDATES;

        // bundled assets root. always the exe's own directory -- unlike pyinstaller, a compiled .net assembly has no separate frozen-vs-source-mode distinction to detect.
        public static readonly string BUNDLE_ROOT;
        public static readonly string ASSETS_DIR;
        public static readonly string OBS_ASSETS_DIR;

        public static readonly string OBS_INSTALL_DIR;
        public static readonly string PROGRAMFILES_OBS_DIR;

        // input-overlay plugin + its vc++ redist prerequisite. plugin installer is bundled in installers.zip; vc++ is downloaded from microsoft only when missing and is signature-checked before running.
        public static readonly string INPUT_OVERLAY_INSTALLERS_ZIP;
        public const string INPUT_OVERLAY_INSTALLER_NAME = "input-overlay-installer.exe";
        public const string VCPP_REDIST_DOWNLOAD_URL = "https://aka.ms/vc14/vc_redist.x64.exe";
        public const long VCPP_REDIST_DOWNLOAD_MAX_BYTES = 64 * 1024 * 1024;

        // bundled virtual audio driver -- creates the obs stream audio render endpoint and matching loopback capture endpoint used by discord.
        public const string VBCABLE_DRIVER_PACK_NAME = "VBCABLE_Driver_Pack45.zip";
        public const string VBCABLE_SETUP_EXE_NAME = "VBCABLE_Setup_x64.exe";

        // input-overlay preset pack -- extracted under replaykit_config so all replaykit-managed assets live under one umbrella.
        public static readonly string INPUT_OVERLAY_ZIP;
        public static readonly string INPUT_OVERLAY_TARGET;

        // win-capture-audio plugin (https://github.com/bozbez/win-capture-audio) powers the "desktop audio (excl. discord)" source via windows 10+ per-process loopback.
        public static readonly string WIN_CAPTURE_AUDIO_ZIP;
        public const string WIN_CAPTURE_AUDIO_DLL_REL = "obs-plugins/64bit/win-capture-audio.dll";

        // bongobs/bango cat plugin -- the archive is distributed as a manual obs-root extract, so installer code strips the top-level folder and writes only safe relative paths into programfiles_obs_dir.
        public static readonly string BONGO_CAT_ZIP;

        // obs shaderfilter plugin -- replaykit uses its bundled motion_blur.shader when the optional motion blur setting is enabled.
        public static readonly string SHADERFILTER_ZIP;
        public const string SHADERFILTER_ZIP_SHA256 = "0e75fc5f2523befd9c66c0adb14f9c838cc0cd705b32487e121abb03ad2f2486";

        // replaykits own tray plugin -- compiled from source at release time and bundled straight into assets/, installed under %programdata%\obs-studio\plugins\ (obss no-admin plugin search path).
        public static readonly string REPLAYKIT_TRAY_DLL_BUNDLED;
        public static readonly string REPLAYKIT_TRAY_PLUGIN_DIR;
        public static readonly string REPLAYKIT_TRAY_DLL_TARGET;

        // file extensions the installer treats as text (PathRewrite + Prefs.ApplyPreferences).
        public static readonly HashSet<string> TEXT_EXTS = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".ini", ".json", ".bak", ".lua", ".ps1", ".txt",
        };

        static Config()
        {
            USERNAME = Environment.GetEnvironmentVariable("USERNAME") ?? Environment.GetEnvironmentVariable("USER") ?? "User";
            USERPROFILE = Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            APPDATA = Environment.GetEnvironmentVariable("APPDATA") ?? Path.Combine(USERPROFILE, "AppData", "Roaming");
            LOCALAPPDATA = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Path.Combine(USERPROFILE, "AppData", "Local");
            PROGRAMDATA = Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData";

            OBS_CONFIG = Path.Combine(APPDATA, "obs-studio");
            REPLAYKIT_CONFIG = Path.Combine(OBS_CONFIG, "obs-replayKit");
            REPLAYKIT_SETUP_CACHE = Path.Combine(LOCALAPPDATA, "OBS ReplayKit");
            REPLAYKIT_SETUP_EXE = Path.Combine(REPLAYKIT_SETUP_CACHE, "OBSReplayKitSetup.exe");
            REPLAYKIT_USER_STATE_CACHE = Path.Combine(REPLAYKIT_SETUP_CACHE, "state");
            DOCK_TARGET = Path.Combine(REPLAYKIT_CONFIG, "obs-custom-dock");

            _DEFAULT_OBS_DIR = Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files", "obs-studio");

            OBS_EXE_CANDIDATES = UniquePaths(
                EnvObsCandidates()
                    .Concat(RunningObsCandidates())
                    .Concat(RegistryObsCandidates())
                    .Concat(PathObsCandidates())
                    .Concat(DefaultObsCandidates()));

            BUNDLE_ROOT = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
            ASSETS_DIR = Path.Combine(BUNDLE_ROOT, "assets");
            OBS_ASSETS_DIR = Path.Combine(ASSETS_DIR, "obs-studio");

            OBS_INSTALL_DIR = FindObsInstallDir() ?? _DEFAULT_OBS_DIR;
            PROGRAMFILES_OBS_DIR = OBS_INSTALL_DIR;

            INPUT_OVERLAY_INSTALLERS_ZIP = Path.Combine(ASSETS_DIR, "installers.zip");
            INPUT_OVERLAY_ZIP = Path.Combine(ASSETS_DIR, "input-overlay-presets.zip");
            INPUT_OVERLAY_TARGET = Path.Combine(REPLAYKIT_CONFIG, "input-overlay-presets");
            WIN_CAPTURE_AUDIO_ZIP = Path.Combine(ASSETS_DIR, "win-capture-audio.zip");
            BONGO_CAT_ZIP = Path.Combine(ASSETS_DIR, "Bango Cat.zip");
            SHADERFILTER_ZIP = Path.Combine(ASSETS_DIR, "obs-shaderfilter.zip");

            REPLAYKIT_TRAY_DLL_BUNDLED = Path.Combine(ASSETS_DIR, "obs-plugins", "replaykit-tray", "bin", "64bit", "replaykit-tray.dll");
            REPLAYKIT_TRAY_PLUGIN_DIR = Path.Combine(PROGRAMDATA, "obs-studio", "plugins", "replaykit-tray");
            REPLAYKIT_TRAY_DLL_TARGET = Path.Combine(REPLAYKIT_TRAY_PLUGIN_DIR, "bin", "64bit", "replaykit-tray.dll");
        }

        private static bool IsValidObsExe(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string name = Path.GetFileName(path).ToLowerInvariant();
            return OBS_PROCESSES.Contains(name) && File.Exists(path);
        }

        private static string ObsExeFromRoot(string root)
        {
            return Path.Combine(root, "bin", "64bit", "obs64.exe");
        }

        private static string ObsRootFromExe(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                string bitDir = Path.GetDirectoryName(full);
                string binDir = bitDir == null ? null : Path.GetDirectoryName(bitDir);
                if (bitDir != null && binDir != null &&
                    string.Equals(Path.GetFileName(bitDir), "64bit", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Path.GetFileName(binDir), "bin", StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetDirectoryName(binDir);
                }
            }
            catch (ArgumentException) { }
            catch (PathTooLongException) { }
            catch (NotSupportedException) { }
            return null;
        }

        private static IEnumerable<string> EnvObsCandidates()
        {
            var result = new List<string>();
            string exe = (Environment.GetEnvironmentVariable("OBS_REPLAYKIT_OBS_EXE") ?? "").Trim().Trim('"');
            if (!string.IsNullOrEmpty(exe)) result.Add(exe);
            string root = (Environment.GetEnvironmentVariable("OBS_REPLAYKIT_OBS_DIR") ?? "").Trim().Trim('"');
            if (!string.IsNullOrEmpty(root)) result.Add(ObsExeFromRoot(root));
            return result;
        }

        // native wmi query instead of shelling out to powershell for the same cim lookup the python version used -- same result, one less process spawned.
        private static IEnumerable<string> RunningObsCandidates()
        {
            var result = new List<string>();
            try
            {
                string clause = string.Join(" OR ", OBS_PROCESSES.Select(n => "Name = '" + n.Replace("'", "''") + "'"));
                using (var searcher = new ManagementObjectSearcher("SELECT ExecutablePath FROM Win32_Process WHERE " + clause))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        var execPath = mo["ExecutablePath"] as string;
                        if (!string.IsNullOrEmpty(execPath)) result.Add(execPath);
                    }
                }
            }
            catch (ManagementException)
            {
            }
            return result.Distinct();
        }

        private static IEnumerable<string> RegistryObsCandidates()
        {
            var result = new List<string>();

            Action<string> addRoot = value =>
            {
                if (!string.IsNullOrEmpty(value)) result.Add(ObsExeFromRoot(value.Trim().Trim('"')));
            };
            Action<string> addExe = value =>
            {
                if (string.IsNullOrEmpty(value)) return;
                string raw = value.Trim().Trim('"');
                int comma = raw.IndexOf(',');
                if (comma >= 0) raw = raw.Substring(0, comma).Trim().Trim('"');
                result.Add(raw);
            };

            var hives = new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine };
            var views = new[] { RegistryView.Default, RegistryView.Registry64, RegistryView.Registry32 };

            foreach (var hive in hives)
            {
                foreach (var view in views)
                {
                    RegistryKey baseKey;
                    try { baseKey = RegistryKey.OpenBaseKey(hive, view); }
                    catch { continue; }

                    using (baseKey)
                    {
                        try
                        {
                            using (var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\obs64.exe"))
                            {
                                if (key != null) addExe(key.GetValue("") as string);
                            }
                        }
                        catch (System.Security.SecurityException) { }
                        catch (UnauthorizedAccessException) { }
                        catch (IOException) { }

                        foreach (var rootKeyPath in new[]
                        {
                            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
                        })
                        {
                            try
                            {
                                using (var root = baseKey.OpenSubKey(rootKeyPath))
                                {
                                    if (root == null) continue;
                                    foreach (var subName in root.GetSubKeyNames())
                                    {
                                        try
                                        {
                                            using (var sub = root.OpenSubKey(subName))
                                            {
                                                if (sub == null) continue;
                                                var display = sub.GetValue("DisplayName") as string;
                                                if (display == null || !display.ToLowerInvariant().StartsWith("obs studio")) continue;
                                                addRoot(sub.GetValue("InstallLocation") as string);
                                                addExe(sub.GetValue("DisplayIcon") as string);
                                            }
                                        }
                                        catch (System.Security.SecurityException) { continue; }
                                        catch (UnauthorizedAccessException) { continue; }
                                        catch (IOException) { continue; }
                                    }
                                }
                            }
                            catch (System.Security.SecurityException) { }
                            catch (UnauthorizedAccessException) { }
                            catch (IOException) { }
                        }
                    }
                }
            }
            return result;
        }

        private static IEnumerable<string> PathObsCandidates()
        {
            var result = new List<string>();
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var name in OBS_PROCESSES)
            {
                foreach (var dir in pathEnv.Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    string candidate;
                    try { candidate = Path.Combine(dir.Trim(), name); }
                    catch { continue; }
                    if (File.Exists(candidate))
                    {
                        result.Add(candidate);
                        break;
                    }
                }
            }
            return result;
        }

        private static IEnumerable<string> DefaultObsCandidates()
        {
            var roots = new[]
            {
                Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files", "obs-studio"),
                Path.Combine(Environment.GetEnvironmentVariable("ProgramW6432") ?? @"C:\Program Files", "obs-studio"),
                Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? @"C:\Program Files (x86)", "obs-studio"),
            };
            return roots.Select(ObsExeFromRoot);
        }

        private static string[] UniquePaths(IEnumerable<string> paths)
        {
            var result = new List<string>();
            var seen = new HashSet<string>();
            foreach (var path in paths)
            {
                if (path == null) continue;
                string key;
                try { key = Path.GetFullPath(path).ToLowerInvariant(); }
                catch { key = path.ToLowerInvariant(); }
                if (!seen.Add(key)) continue;
                result.Add(path);
            }
            return result.ToArray();
        }

        public static string FindObsExeCandidate()
        {
            foreach (var candidate in OBS_EXE_CANDIDATES)
            {
                if (IsValidObsExe(candidate)) return candidate;
            }
            return null;
        }

        public static string FindObsInstallDir()
        {
            string exe = FindObsExeCandidate();
            return exe == null ? null : ObsRootFromExe(exe);
        }

        // Encoding.UTF8 (and File.ReadAllText's convenience overload) replaces invalid bytes and silently strips a leading BOM instead of throwing -- neither matches pythons read_text(encoding="utf-8"), which decodes a bom as a literal u+feff and raises UnicodeDecodeError on invalid bytes. this mirrors that exactly: strict utf-8 first, latin-1 fallback (every byte 0-255 is valid latin-1, so it never fails).
        private static readonly System.Text.Encoding StrictUtf8NoBom = new System.Text.UTF8Encoding(false, true);

        public static string ReadTextFileFlexible(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            try
            {
                return StrictUtf8NoBom.GetString(bytes);
            }
            catch (System.Text.DecoderFallbackException)
            {
                return System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(bytes);
            }
        }
    }
}

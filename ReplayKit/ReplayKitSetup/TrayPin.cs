using System;
using System.Linq;
using Microsoft.Win32;

namespace ReplayKitSetup
{
    // pin the obs tray icon so windows never buries it behind the '^' overflow arrow. ported from obs_replaykit/tray_pin.py.
    public static class TrayPin
    {
        private const string NotifyIconSettings = @"Control Panel\NotifyIconSettings";

        // last n path segments, lowercased -- windows stores a standard installs executablepath knownfolderid-relative instead of a plain drive letter, so comparing full paths would never match, but the tail is stable either way.
        private static string PathTail(string pathStr, int parts = 4)
        {
            var segments = (pathStr ?? "").Replace("/", "\\").Split('\\').Where(s => s.Length > 0);
            return string.Join("\\", segments.Skip(Math.Max(0, segments.Count() - parts))).ToLowerInvariant();
        }

        // sets isPromoted=1 on obss tray icon entry so windows always shows it instead of hiding it behind the overflow arrow; no-op if obs has never registered a tray icon yet or the registry layout looks unexpected, since this is a reverse-engineered per-user scheme, not a documented api.
        public static bool PinObsTrayIcon(Action<string> log = null)
        {
            string obsExe = Config.FindObsExeCandidate();
            if (obsExe == null) return false;
            string target = PathTail(obsExe);

            RegistryKey root;
            try { root = Registry.CurrentUser.OpenSubKey(NotifyIconSettings); }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is System.IO.IOException) { root = null; }
            if (root == null)
            {
                log?.Invoke("OBS tray icon not pinned: no tray icons registered on this account yet");
                return false;
            }

            using (root)
            {
                foreach (var subkeyName in root.GetSubKeyNames())
                {
                    RegistryKey sub;
                    try { sub = root.OpenSubKey(subkeyName, writable: true); }
                    catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException) { continue; }
                    if (sub == null) continue;

                    using (sub)
                    {
                        try
                        {
                            string exePath = sub.GetValue("ExecutablePath") as string;
                            if (exePath == null || PathTail(exePath) != target) continue;
                            object current = sub.GetValue("IsPromoted") ?? 0;
                            if (Convert.ToInt32(current) == 1)
                            {
                                log?.Invoke("OBS tray icon already pinned");
                                return true;
                            }
                            sub.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                            log?.Invoke("OBS tray icon pinned to the taskbar");
                            return true;
                        }
                        catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException)
                        {
                            continue;
                        }
                    }
                }
            }

            log?.Invoke("OBS tray icon not pinned: no matching entry yet (run OBS at least once first)");
            return false;
        }

        // sets isPromoted=0, restoring windows default overflow behaviour; same match/no-op rules as PinObsTrayIcon.
        public static bool UnpinObsTrayIcon(Action<string> log = null)
        {
            string obsExe = Config.FindObsExeCandidate();
            if (obsExe == null) return false;
            string target = PathTail(obsExe);

            RegistryKey root;
            try { root = Registry.CurrentUser.OpenSubKey(NotifyIconSettings); }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is System.IO.IOException) { root = null; }
            if (root == null) return false;

            using (root)
            {
                foreach (var subkeyName in root.GetSubKeyNames())
                {
                    RegistryKey sub;
                    try { sub = root.OpenSubKey(subkeyName, writable: true); }
                    catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException) { continue; }
                    if (sub == null) continue;

                    using (sub)
                    {
                        try
                        {
                            string exePath = sub.GetValue("ExecutablePath") as string;
                            if (exePath == null || PathTail(exePath) != target) continue;
                            sub.SetValue("IsPromoted", 0, RegistryValueKind.DWord);
                            log?.Invoke("OBS tray icon unpinned");
                            return true;
                        }
                        catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException)
                        {
                            continue;
                        }
                    }
                }
            }
            return false;
        }
    }
}

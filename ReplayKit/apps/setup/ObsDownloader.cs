using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;

namespace ReplayKitSetup
{
    // fetch the official OBS Studio installer of the newest version obs-versions.json supports, check its hash and signature and run it silently. called when OBS is missing or older than that version -- the setup exe is already elevated (app.manifest), so the nsis installer needs no extra prompt. nothing is bundled; there is no offline path.
    public static class ObsDownloader
    {
        // obss windows installer is ~150 mb; the cap only exists so a hijacked url cannot fill the disk.
        private const long MaxInstallerBytes = 500L * 1024 * 1024;
        private const int InstallTimeoutMs = 10 * 60 * 1000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);
        private const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x00000004;

        // files OBSs installer must overwrite that get loaded (and locked) by other processes -- the virtual-camera DirectShow filter is loaded by chrome / Medal / Discord / the Camera app the moment they enumerate cameras.
        private static readonly string[] LockProneRelPaths =
        {
            @"data\obs-plugins\win-dshow\obs-virtualcam-module64.dll",
            @"data\obs-plugins\win-dshow\obs-virtualcam-module32.dll",
        };

        // the update flow has only a log, and the obs download is the one step that reports more than lines
        private sealed class LogProgress : IInstallProgress
        {
            private readonly Action<string> log;
            public LogProgress(Action<string> log) { this.log = log; }
            public int TotalSteps { get; set; }
            public List<string> Issues { get; } = new List<string>();
            public void Render(int completed, SetupLocale.Text title, SetupLocale.Text detail, string state) { }
            public void LogLine(string message) => log?.Invoke(message);
            public void AddIssue(SetupLocale.Text message) => log?.Invoke("warn: " + message.English);
            public void SubProgress(double fraction, string detail) { }
        }

        public static bool EnsureObsVersion(Action<string> log) => EnsureObsVersion(new LogProgress(log));

        // replaykit supports one obs version, the tested version of the newest line: obs is installed when it is missing, updated when it is older, and put back on that version when it is a newer release of the same major (its settings carry over within a major). a newer major is left alone and reported, since going back across majors can break the settings of obs; steam keeps its own copy, so that one is never touched. callers check ObsVersions.IsInstalledSupported afterwards
        public static bool EnsureObsVersion(IInstallProgress progress, bool forceClose = false)
        {
            var target = ObsVersions.Install.Tested;
            string exe = Obs.FindObsExe();
            var installed = exe == null ? null : ObsVersions.InstalledVersion();
            if (installed == target)
            {
                progress.LogLine("OBS " + installed + " is installed");
                return true;
            }
            string unreachable = UnreachableObs();
            if (unreachable != null)
            {
                progress.LogLine("warn: " + unreachable);
                return false;
            }
            if (exe != null)
            {
                progress.LogLine((installed != null && installed > target ? "moving OBS " : "updating OBS ") + (installed?.ToString() ?? "(unknown version)") + " to " + target + ", the version this ReplayKit supports");
                Obs.CloseObs(progress.LogLine, forceCloseRemaining: forceClose);
            }
            if (!EnsureObsInstalled(progress)) return false;
            installed = ObsVersions.InstalledVersion();
            if (installed == target) return true;
            progress.LogLine("warn: OBS " + target + " was installed but OBS reports version " + (installed?.ToString() ?? "(none)"));
            return false;
        }

        // why the installed obs can never be put on the supported version by this installer, in the language of the setup, or null: steam decides the version of its own copy, and going back across majors can break the settings of obs
        public static string UnreachableObs()
        {
            var target = ObsVersions.Install.Tested;
            string exe = Obs.FindObsExe();
            if (exe == null) return null;
            var installed = ObsVersions.InstalledVersion();
            if (installed == target) return null;
            if (exe.IndexOf(@"\steamapps\", StringComparison.OrdinalIgnoreCase) >= 0)
                return SetupLocale.Message("setup.theSteamCopyOfObsIsVersion", "installed", installed?.ToString() ?? "?", "target", target.ToString());
            if (installed != null && installed.Major > target.Major)
                return SetupLocale.Message("setup.obsIsNewerThanTheVersionReplaykitSupports", "installed", installed.ToString(), "target", target.ToString());
            return null;
        }

        public static bool EnsureObsInstalled(IInstallProgress progress)
        {
            Action<string> log = progress.LogLine;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            string installer = null;
            string workDir = null;
            try
            {
                var download = ObsVersions.Install.Installer;
                string assetName = Path.GetFileName(new Uri(download.Url).AbsolutePath);
                log("OBS installer: " + assetName);

                workDir = NetCompat.CreateTempSubdirectory("ReplayKit-obs-setup-").FullName;
                installer = Path.Combine(workDir, assetName);
                if (!DownloadInstaller(download.Url, installer, progress))
                    return false;

                string actual;
                using (var stream = File.OpenRead(installer)) actual = NetCompat.ToHex(NetCompat.Sha256(stream)).ToLowerInvariant();
                if (actual != download.Sha256)
                {
                    log("The downloaded OBS installer is not the one ReplayKit expects (sha256 " + actual + "). Try again later, or install OBS yourself from obsproject.com.");
                    return false;
                }

                if (!Authenticode.IsObsSigned(installer, log))
                {
                    log("The downloaded OBS installer is not validly signed by OBS Project. Install OBS yourself from obsproject.com, then re-run this installer.");
                    return false;
                }

                progress.SubProgress(1.0, SetupLocale.Message("setup.installingObsStudio"));
                log("running the OBS installer silently...");
                // OBSs /S installer aborts (exit 6, ~1s) when it cannot overwrite a loaded file -- almost always the virtual-camera filter that a browser / capture app holds locked. move any locked file aside first.
                PrepareObsInstallDir(Config.PROGRAMFILES_OBS_DIR, log);
                if (!RunSilentInstaller(installer, log))
                    return false;

                string found = Obs.FindObsExe();
                if (found == null && PrepareObsInstallDir(Config.PROGRAMFILES_OBS_DIR, log) > 0)
                {
                    log("retrying the OBS installer after moving locked files aside...");
                    if (RunSilentInstaller(installer, log)) found = Obs.FindObsExe();
                }
                if (found == null)
                {
                    log("OBS Studio's installer did not complete -- if it reported a file in use, close OBS / your browser / "
                        + "any screen-capture app (or reboot) and run this installer again.");
                    return false;
                }
                log("OBS installed: " + found);
                return true;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is InvalidDataException || ex is IOException || ex is System.OperationCanceledException)
            {
                log("Could not download OBS: " + ex.Message + ". Install OBS yourself from obsproject.com, then re-run this installer.");
                return false;
            }
            finally
            {
                try { if (Directory.Exists(workDir)) Directory.Delete(workDir, true); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }

        private static bool DownloadInstaller(string url, string dst, IInstallProgress progress)
        {
            Action<string> log = progress.LogLine;
            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) })
            {
                client.DefaultRequestHeaders.Add("User-Agent", Download.UserAgent);
                DateTime lastTick = DateTime.UtcNow;
                Download.Save(client, url, dst, MaxInstallerBytes, TimeSpan.FromMinutes(15), 0, (done, total) =>
                {
                    DateTime now = DateTime.UtcNow;
                    if ((now - lastTick).TotalMilliseconds < 250) return;
                    lastTick = now;
                    double frac = total > 0 ? Math.Min(0.99, (double)done / total) : 0.0;
                    string mb = total > 0
                        ? $"{done / (1024 * 1024)} / {total / (1024 * 1024)} MB"
                        : $"{done / (1024 * 1024)} MB";
                    progress.SubProgress(frac, SetupLocale.Message("setup.downloadingObsStudioSize", "size", mb));
                });
                long done = new FileInfo(dst).Length;
                progress.SubProgress(1.0, SetupLocale.Message("setup.downloadedObsStudio"));
                log($"OBS installer downloaded ({done} bytes)");
                return true;
            }
        }

        // rename every currently-locked file in an existing OBS dir aside (a loaded image can be renamed within its folder) so the installer can lay down a fresh copy, and queue the stale one for delete-on-reboot. returns the count.
        private static int PrepareObsInstallDir(string obsDir, Action<string> log)
        {
            if (string.IsNullOrEmpty(obsDir) || !Directory.Exists(obsDir)) return 0;

            var candidates = new List<string>();
            foreach (var rel in LockProneRelPaths)
            {
                string p = Path.Combine(obsDir, rel);
                if (File.Exists(p)) candidates.Add(p);
            }
            try
            {
                foreach (var p in Directory.EnumerateFiles(obsDir, "*", SearchOption.AllDirectories))
                    if (!candidates.Contains(p)) candidates.Add(p);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }

            int moved = 0;
            foreach (var path in candidates)
            {
                if (!IsFileLocked(path)) continue;
                string aside = path + ".rk-old-" + DateTime.Now.ToString("HHmmssfff");
                try
                {
                    File.Move(path, aside);
                    try { MoveFileEx(aside, null, MOVEFILE_DELAY_UNTIL_REBOOT); } catch (Exception) { }
                    moved++;
                    log?.Invoke("moved a locked file aside so the OBS installer can replace it: " + Path.GetFileName(path));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    log?.Invoke("warn: the OBS installer may fail -- could not move locked file " + path + ": " + ex.Message);
                }
            }
            if (moved > 0) log?.Invoke(moved + " locked file(s) moved aside; they clear on the next reboot");
            return moved;
        }

        private static bool IsFileLocked(string path)
        {
            try
            {
                using var s = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return false;
            }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        private static bool RunSilentInstaller(string installer, Action<string> log)
        {
            try
            {
                var psi = new ProcessStartInfo(installer, "/S")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(installer),
                };
                using var proc = Process.Start(psi);
                if (!proc.WaitForExit(InstallTimeoutMs))
                {
                    try { proc.Kill(); } catch (InvalidOperationException) { }
                    log("OBS installer did not finish within 10 minutes");
                    return false;
                }
                // nsis returns 0 on success. a non-zero code is logged but not treated as fatal on its own -- the caller re-checks for obs64.exe, which is the real signal. exit 6 in ~1s = a file OBS had to overwrite was in use.
                if (proc.ExitCode != 0) log("warn: OBS installer exited with code " + proc.ExitCode + " (a nonzero code usually means a file was in use)");
                return true;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            {
                log("Could not start the OBS installer: " + ex.Message);
                return false;
            }
        }
    }
}

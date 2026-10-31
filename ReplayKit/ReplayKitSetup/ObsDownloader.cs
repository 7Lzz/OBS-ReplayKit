using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;

namespace ReplayKitSetup
{
    // fetch the official OBS Studio installer of the newest version obs-versions.json supports, check its hash and signature and run it silently. called when OBS is missing or older than that version -- the setup exe is already elevated (app.manifest), so the nsis installer needs no extra prompt. nothing is bundled; there is no offline path.
    public static class ObsDownloader
    {
        private const string UserAgent = "OBSReplayKit/1.0 (+https://github.com/7Lzz/OBS-ReplayKit)";

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
            public void Render(int completed, string title, string detail, string state) { }
            public void LogLine(string message) => log?.Invoke(message);
            public void AddIssue(string message) => log?.Invoke("warn: " + message);
            public void SubProgress(double fraction, string detail) { }
        }

        public static bool EnsureObsVersion(Action<string> log) => EnsureObsVersion(new LogProgress(log));

        // replaykit supports one obs version, the tested version of the newest line: obs is installed when it is missing, updated when it is older, and put back on that version when it is a newer release of the same major (its settings carry over within a major). a newer major is left alone and reported, since going back across majors can break the settings of obs; steam keeps its own copy, so that one is never touched. callers check ObsVersions.IsInstalledSupported afterwards
        public static bool EnsureObsVersion(IInstallProgress progress)
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
                Obs.CloseObs(progress.LogLine);
            }
            if (!EnsureObsInstalled(progress)) return false;
            installed = ObsVersions.InstalledVersion();
            if (installed == target) return true;
            progress.LogLine("warn: OBS " + target + " was installed but OBS reports version " + (installed?.ToString() ?? "(none)"));
            return false;
        }

        // obs updated in place keeps the first-party plugins and browser of its old version in obs-plugins\64bit and data\obs-plugins (about 300 MB), which obs 33 and later skip since they live in core\ now; only names core\ has too are removed, so third-party plugins in the legacy folders stay. obs is closed when this runs
        public static void RemoveReplacedCoreModules(Action<string> log)
        {
            string root = Config.PROGRAMFILES_OBS_DIR, core = Path.Combine(root, "core");
            if (!Directory.Exists(core)) return;
            var coreNames = new HashSet<string>(Directory.EnumerateFiles(core, "*", SearchOption.AllDirectories).Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
            int removed = 0, failed = 0;
            long bytes = 0;
            string bin = Path.Combine(root, "obs-plugins", "64bit");
            if (Directory.Exists(bin))
            {
                foreach (string file in Directory.EnumerateFiles(bin, "*", SearchOption.AllDirectories).ToList())
                {
                    if (!coreNames.Contains(Path.GetFileName(file))) continue;
                    try { long size = new FileInfo(file).Length; File.Delete(file); removed++; bytes += size; }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { failed++; }
                }
                foreach (string dir in Directory.EnumerateDirectories(bin, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).ToList())
                {
                    try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                }
            }
            string data = Path.Combine(root, "data", "obs-plugins");
            if (Directory.Exists(data))
            {
                foreach (string dir in Directory.EnumerateDirectories(data).ToList())
                {
                    if (!Directory.Exists(Path.Combine(core, Path.GetFileName(dir)))) continue;
                    try { bytes += Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); Directory.Delete(dir, true); removed++; }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { failed++; }
                }
            }
            if (removed > 0) log?.Invoke($"removed {removed} file(s) and folder(s) of the old OBS version that OBS no longer loads ({bytes / (1024 * 1024)} MB)");
            if (failed > 0) log?.Invoke($"{failed} old OBS file(s) were in use and stay until the next update");
        }

        // why the installed obs can never be put on the supported version by this installer, or null: steam decides the version of its own copy, and going back across majors can break the settings of obs
        public static string UnreachableObs()
        {
            var target = ObsVersions.Install.Tested;
            string exe = Obs.FindObsExe();
            if (exe == null) return null;
            var installed = ObsVersions.InstalledVersion();
            if (installed == target) return null;
            if (exe.IndexOf(@"\steamapps\", StringComparison.OrdinalIgnoreCase) >= 0)
                return "The Steam copy of OBS is version " + (installed?.ToString() ?? "(unknown)") + " and ReplayKit needs OBS " + target + ". Steam decides which version it runs, so install OBS " + target + " from obsproject.com instead.";
            if (installed != null && installed.Major > target.Major)
                return "OBS " + installed + " is newer than OBS " + target + ", the version this ReplayKit supports. Install OBS " + target + " from obsproject.com, then run the ReplayKit installer again.";
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

                workDir = Directory.CreateTempSubdirectory("ReplayKit-obs-setup-").FullName;
                installer = Path.Combine(workDir, assetName);
                if (!DownloadInstaller(download.Url, installer, 0, progress))
                    return false;

                string actual;
                using (var stream = File.OpenRead(installer)) actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
                if (actual != download.Sha256)
                {
                    log("The downloaded OBS installer is not the one ReplayKit expects (sha256 " + actual + "). Try again later, or install OBS yourself from obsproject.com.");
                    return false;
                }

                if (!IsObsSigned(installer, log))
                {
                    log("The downloaded OBS installer is not validly signed by OBS Project. Install OBS yourself from obsproject.com, then re-run this installer.");
                    return false;
                }

                progress.SubProgress(1.0, "Installing OBS Studio...");
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


        private static bool DownloadInstaller(string url, string dst, long expectedLen, IInstallProgress progress)
        {
            Action<string> log = progress.LogLine;
            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) })
            {
                client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
                DateTime lastTick = DateTime.UtcNow;
                Download.Save(client, url, dst, MaxInstallerBytes, TimeSpan.FromMinutes(15), expectedLen, (done, total) =>
                {
                    DateTime now = DateTime.UtcNow;
                    if ((now - lastTick).TotalMilliseconds < 250) return;
                    lastTick = now;
                    double frac = total > 0 ? Math.Min(0.99, (double)done / total) : 0.0;
                    string mb = total > 0
                        ? $"{done / (1024 * 1024)} / {total / (1024 * 1024)} MB"
                        : $"{done / (1024 * 1024)} MB";
                    progress.SubProgress(frac, "Downloading OBS Studio  " + mb);
                });
                long done = new FileInfo(dst).Length;
                progress.SubProgress(1.0, "Downloaded OBS Studio");
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

        // shells out to Get-AuthenticodeSignature -- same reasoning as InputOverlay.IsMicrosoftSigned: the os cmdlet is a safer bet than a hand-rolled WinVerifyTrust p/invoke for a security-critical check before running the exe.
        private static bool IsObsSigned(string path, Action<string> log)
        {
            const string script = @"
$sig = Get-AuthenticodeSignature -LiteralPath $env:OBSREPLAYKIT_OBS_INSTALLER
$cert = $sig.SignerCertificate
[pscustomobject]@{
  Status = [string]$sig.Status
  Subject = if ($cert) { [string]$cert.Subject } else { """" }
} | ConvertTo-Json -Compress
";
            string stdout, stderr;
            try
            {
                string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
                var psi = new ProcessStartInfo(powershell, Win32Args.Build("-NoProfile", "-NonInteractive", "-Command", script))
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                psi.EnvironmentVariables["PSModulePath"] = Path.Combine(Path.GetDirectoryName(powershell), "Modules");
                psi.EnvironmentVariables["OBSREPLAYKIT_OBS_INSTALLER"] = path;
                using var proc = new Process { StartInfo = psi };
                proc.Start();
                var outTask = proc.StandardOutput.ReadToEndAsync();
                var errTask = proc.StandardError.ReadToEndAsync();
                if (!proc.WaitForExit(30000))
                {
                    try { proc.Kill(); } catch (InvalidOperationException) { }
                    log("OBS installer signature check timed out");
                    return false;
                }
                stdout = outTask.GetAwaiter().GetResult();
                stderr = errTask.GetAwaiter().GetResult();
                if (proc.ExitCode != 0)
                {
                    log("OBS installer signature check failed: " + stderr.Trim());
                    return false;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            {
                log("OBS installer signature check could not run: " + ex.Message);
                return false;
            }

            JObject info;
            try { info = JObject.Parse(stdout); }
            catch (Newtonsoft.Json.JsonException) { log("OBS installer signature check returned unreadable data"); return false; }
            string status = info.Value<string>("Status") ?? "";
            string subject = info.Value<string>("Subject") ?? "";
            // "Valid" already means the authenticode signature is intact and chains to a trusted root; the subject check is a sanity guard so a differently-signed exe swapped in over a hijacked link still gets rejected.
            bool ok = status == "Valid" &&
                      (subject.IndexOf("OBS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       subject.IndexOf("Hugh Bailey", StringComparison.OrdinalIgnoreCase) >= 0);
            if (!ok) log($"OBS installer signature rejected: status={status} subject={subject}");
            return ok;
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

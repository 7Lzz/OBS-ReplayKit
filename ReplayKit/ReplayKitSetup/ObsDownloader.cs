using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace ReplayKitSetup
{
    // fetch the official OBS Studio installer from the obsproject github releases and run it silently. only called when
    // no OBS install is detected -- the setup exe is already elevated (app.manifest), so the nsis installer needs no
    // extra prompt. nothing is bundled; there is no offline path.
    public static class ObsDownloader
    {
        private const string LatestReleaseApi = "https://api.github.com/repos/obsproject/obs-studio/releases/latest";
        private const string UserAgent = "OBSReplayKit/1.0 (+https://github.com/7Lzz/OBS-ReplayKit)";

        // obs's windows installer is ~150 mb; the cap only exists so a hijacked url cannot fill the disk.
        private const long MaxInstallerBytes = 500L * 1024 * 1024;
        private const int InstallTimeoutMs = 10 * 60 * 1000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);
        private const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x00000004;

        // files OBS's installer must overwrite that get loaded (and locked) by other processes -- the virtual-camera
        // DirectShow filter is loaded by chrome / Medal / Discord / the Camera app the moment they enumerate cameras.
        private static readonly string[] LockProneRelPaths =
        {
            @"data\obs-plugins\win-dshow\obs-virtualcam-module64.dll",
            @"data\obs-plugins\win-dshow\obs-virtualcam-module32.dll",
        };

        public static bool EnsureObsInstalled(IInstallProgress progress)
        {
            Action<string> log = progress.LogLine;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            string installer = null;
            string workDir = Path.Combine(Path.GetTempPath(), "ReplayKit", "obs-setup");
            try
            {
                if (!TryResolveInstallerAsset(log, out string url, out string assetName, out long expectedLen))
                    return false;

                Directory.CreateDirectory(workDir);
                installer = Path.Combine(workDir, assetName);
                if (!DownloadInstaller(url, installer, expectedLen, progress))
                    return false;

                if (!IsObsSigned(installer, log))
                {
                    log("The downloaded OBS installer is not validly signed by OBS Project. Install OBS yourself from obsproject.com, then re-run this installer.");
                    return false;
                }

                progress.SubProgress(1.0, "Installing OBS Studio...");
                log("running the OBS installer silently...");
                // OBS's /S installer aborts (exit 6, ~1s) when it cannot overwrite a loaded file -- almost always the
                // virtual-camera filter that a browser / capture app holds locked. move any locked file aside first.
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
            catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is System.Threading.Tasks.TaskCanceledException)
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

        // pick the windows full-installer .exe asset from the latest release. rejects arm64 and the portable zip.
        private static bool TryResolveInstallerAsset(Action<string> log, out string url, out string name, out long size)
        {
            url = null; name = null; size = 0;
            string body;
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
            {
                client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
                client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
                var resp = client.GetAsync(LatestReleaseApi).GetAwaiter().GetResult();
                body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode)
                {
                    log($"GitHub said HTTP {(int)resp.StatusCode} when asked for the latest OBS release. Try again later, or install OBS yourself from obsproject.com.");
                    return false;
                }
            }

            JObject release;
            try { release = JObject.Parse(body); }
            catch (Newtonsoft.Json.JsonException) { log("GitHub returned an unreadable release list for OBS."); return false; }

            if (!(release["assets"] is JArray assets)) { log("The latest OBS release listed no downloads."); return false; }

            var installerRe = new Regex(@"(windows.*installer|full-installer).*\.exe$", RegexOptions.IgnoreCase);
            JObject best = null;
            foreach (var token in assets)
            {
                if (!(token is JObject asset)) continue;
                string an = asset.Value<string>("name") ?? "";
                if (!installerRe.IsMatch(an)) continue;
                if (an.IndexOf("arm", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                // prefer an explicitly x64/64-bit asset when the release ships more than one match.
                if (best == null || an.IndexOf("64", StringComparison.OrdinalIgnoreCase) >= 0)
                    best = asset;
            }
            if (best == null) { log("The latest OBS release had no Windows installer .exe."); return false; }

            url = best.Value<string>("browser_download_url");
            name = best.Value<string>("name");
            size = best.Value<long?>("size") ?? 0;
            if (string.IsNullOrEmpty(url)) { log("The OBS installer download link was missing from the release."); return false; }
            if (size > MaxInstallerBytes) { log($"The OBS installer is larger than the {MaxInstallerBytes / (1024 * 1024)} MB safety cap; not downloading it."); return false; }
            log("OBS installer: " + name + (size > 0 ? $" ({size / (1024 * 1024)} MB)" : ""));
            return true;
        }

        private static bool DownloadInstaller(string url, string dst, long expectedLen, IInstallProgress progress)
        {
            Action<string> log = progress.LogLine;
            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) })
            {
                client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
                var resp = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode)
                {
                    log($"OBS installer download returned HTTP {(int)resp.StatusCode}.");
                    return false;
                }
                long total = resp.Content.Headers.ContentLength ?? expectedLen;
                if (total > MaxInstallerBytes) { log("OBS installer exceeded the size cap; aborting."); return false; }

                long done = 0;
                DateTime lastTick = DateTime.UtcNow;
                using (var src = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                using (var outFile = File.Open(dst, FileMode.Create, FileAccess.Write))
                {
                    var buffer = new byte[1024 * 1024];
                    int read;
                    while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        done += read;
                        if (done > MaxInstallerBytes) { log("OBS installer exceeded the size cap mid-download; aborting."); return false; }
                        outFile.Write(buffer, 0, read);
                        DateTime now = DateTime.UtcNow;
                        if ((now - lastTick).TotalMilliseconds >= 250)
                        {
                            lastTick = now;
                            double frac = total > 0 ? Math.Min(0.99, (double)done / total) : 0.0;
                            string mb = total > 0
                                ? $"{done / (1024 * 1024)} / {total / (1024 * 1024)} MB"
                                : $"{done / (1024 * 1024)} MB";
                            progress.SubProgress(frac, "Downloading OBS Studio  " + mb);
                        }
                    }
                }

                // a dropped connection makes src.Read return 0 early -- without this the caller would run NSIS on a
                // partial exe (exit 6, installs nothing). expectedLen is the size GitHub listed for the asset.
                long expect = expectedLen > 0 ? expectedLen : total;
                if (expect > 0 && done < expect)
                {
                    log($"OBS installer download was incomplete: got {done} of {expect} bytes.");
                    try { File.Delete(dst); } catch (Exception) { }
                    return false;
                }
                progress.SubProgress(1.0, "Downloaded OBS Studio");
                log($"OBS installer downloaded ({done} bytes)");
                return true;
            }
        }

        // rename every currently-locked file in an existing OBS dir aside (a loaded image can be renamed within its
        // folder) so the installer can lay down a fresh copy, and queue the stale one for delete-on-reboot. returns the count.
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

        // shells out to Get-AuthenticodeSignature -- same reasoning as InputOverlay.IsMicrosoftSigned: the os cmdlet is a
        // safer bet than a hand-rolled WinVerifyTrust p/invoke for a security-critical check before running the exe.
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
                var psi = new ProcessStartInfo("powershell.exe", Win32Args.Build("-NoProfile", "-NonInteractive", "-Command", script))
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
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
            // "Valid" already means the authenticode signature is intact and chains to a trusted root; the subject check
            // is a sanity guard so a differently-signed exe swapped in over a hijacked link still gets rejected.
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
                // nsis returns 0 on success. a non-zero code is logged but not treated as fatal on its own -- the caller
                // re-checks for obs64.exe, which is the real signal. exit 6 in ~1s = a file OBS had to overwrite was in use.
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

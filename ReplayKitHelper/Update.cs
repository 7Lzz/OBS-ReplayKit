using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // self-update: checks github releases, downloads + sha256-verifies the installer, then launches it in --update mode (ReplayKitSetup/Update.cs is the receiving end of that launch -- a seperate, already-complete project, not duplicated logic). ported from obs_replaykit helper modules/63_update.ps1. uses HttpClient instead of Invoke-RestMethod/Invoke-WebRequest for the same reliability reason curl.exe is used for the streamable api elsewhere in this port (PowerShell 5.1s http cmdlets can hang under proxy auto-detect) -- HttpClient doesnt share that specific failure mode, so unlike the streamable api calls this doesnt need a subprocess.
    internal static class Update
    {
        private const string Owner = "7Lzz";
        private const string Repo = "OBS-ReplayKit";
        private const string InstallerAsset = "OBSReplayKit.exe";
        private const string HashAsset = "OBSReplayKit.exe.sha256";

        // a release exe built without its embedded assets\ payload is around 1 mb and can install nothing -- it closes obs first and only then discovers it has no files, which strands the user with no obs and no helper. the runtime tree alone is over 10 mb, so a complete build never lands anywhere near this floor. mirrors the same guard build.bat applies at the producing end.
        private const long MinInstallerBytes = 6L * 1024 * 1024;

        private static readonly HttpClient Http = CreateHttpClient();
        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("OBSReplayKit-Updater");
            return client;
        }

        // temp debug log, always written regardless of the logging-enabled flag, so a "popup didnt appear" report can be diagnosed without turning logging on first.
        private static readonly string UpdateDebugLog = Path.Combine(Constants.LOG_DIR, "replaykit_update_debug.log");
        private static void WriteUpdateDebug(string msg)
        {
            try
            {
                string line = string.Format("[{0}] PID={1} helper {2}", DateTime.Now.ToString("o"), Process.GetCurrentProcess().Id, msg);
                lock (Server.State.LogLock)
                {
                    try { File.AppendAllText(UpdateDebugLog, line + Environment.NewLine); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                }
            }
            catch { }
        }

        public static string GetReplayKitRootDir()
        {
            string helperDir = (AppConfig.GetScriptDir() ?? "").TrimEnd('\\', '/');
            string scriptsDir = Directory.GetParent(helperDir).FullName;
            return Directory.GetParent(scriptsDir).FullName;
        }

        public static string GetVersionPath() => Path.Combine(GetReplayKitRootDir(), "version.json");

        public static string GetInstalledVersion()
        {
            string path = GetVersionPath();
            if (!File.Exists(path)) return "0.0.0";
            try
            {
                var data = JObject.Parse(File.ReadAllText(path));
                string version = data["version"]?.Value<string>();
                if (!string.IsNullOrEmpty(version)) return version;
            }
            catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new InvalidOperationException("Installed ReplayKit version file is invalid.");
            }
            throw new InvalidOperationException("Installed ReplayKit version file is missing version.");
        }

        public static string NormalizeVersion(string version)
        {
            string v = (version ?? "").Trim();
            if (v.StartsWith("v", StringComparison.OrdinalIgnoreCase)) v = v.Substring(1);
            var m = Regex.Match(v, @"^\d+(?:\.\d+){0,3}");
            if (!m.Success) throw new InvalidOperationException("Invalid version: " + version);
            return m.Value;
        }

        public static int CompareVersion(string left, string right)
        {
            var a = NormalizeVersion(left).Split('.');
            var b = NormalizeVersion(right).Split('.');
            for (int i = 0; i < 4; i++)
            {
                int av = i < a.Length ? int.Parse(a[i]) : 0;
                int bv = i < b.Length ? int.Parse(b[i]) : 0;
                if (av < bv) return -1;
                if (av > bv) return 1;
            }
            return 0;
        }

        private static JObject InvokeGitHubApi(string url)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var response = Http.GetAsync(url).GetAwaiter().GetResult();
            string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("GitHub API request failed (" + (int)response.StatusCode + "): " + body);
            return JObject.Parse(body);
        }

        private static JToken GetReleaseAsset(JObject release, string name)
        {
            if (release["assets"] is JArray assets)
            {
                foreach (var asset in assets)
                {
                    if (asset["name"]?.Value<string>() == name) return asset;
                }
            }
            return null;
        }

        public sealed class ReleaseInfo
        {
            public string TagName;
            public string LatestVersion;
            public string HtmlUrl;
            public string InstallerUrl;
            public string HashUrl = "";
            public string Body = "";
            public string Name = "";
        }

        public static ReleaseInfo GetLatestRelease()
        {
            string url = "https://api.github.com/repos/" + Owner + "/" + Repo + "/releases/latest";
            var release = InvokeGitHubApi(url);
            string tagName = release["tag_name"]?.Value<string>();
            if (string.IsNullOrEmpty(tagName)) throw new InvalidOperationException("Latest release did not include a tag name.");
            var installer = GetReleaseAsset(release, InstallerAsset);
            string installerUrl = installer?["browser_download_url"]?.Value<string>();
            if (installer == null || string.IsNullOrEmpty(installerUrl))
                throw new InvalidOperationException("Latest release is missing " + InstallerAsset + ".");
            var hash = GetReleaseAsset(release, HashAsset);
            return new ReleaseInfo
            {
                TagName = tagName,
                LatestVersion = NormalizeVersion(tagName),
                HtmlUrl = release["html_url"]?.Value<string>() ?? "",
                InstallerUrl = installerUrl,
                // body and name are part of the same /releases/latest payload, no extra api call -- body is github-flavored markdown, the popup renders + escapes it client-side.
                HashUrl = hash?["browser_download_url"]?.Value<string>() ?? "",
                Body = release["body"]?.Value<string>() ?? "",
                Name = release["name"]?.Value<string>() ?? "",
            };
        }

        public static JObject GetUpdateStatus()
        {
            try
            {
                string installed = NormalizeVersion(GetInstalledVersion());
                var latest = GetLatestRelease();
                int cmp = CompareVersion(installed, latest.LatestVersion);
                return new JObject
                {
                    ["ok"] = true,
                    ["installedVersion"] = installed,
                    ["latestVersion"] = latest.LatestVersion,
                    ["tagName"] = latest.TagName,
                    ["releaseUrl"] = latest.HtmlUrl,
                    ["releaseName"] = latest.Name,
                    ["releaseNotes"] = latest.Body,
                    ["updateAvailable"] = cmp < 0,
                    ["hashRequired"] = true,
                };
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("(404)"))
                {
                    return new JObject
                    {
                        ["ok"] = true,
                        ["installedVersion"] = NormalizeVersion(GetInstalledVersion()),
                        ["latestVersion"] = "",
                        ["tagName"] = "",
                        ["releaseUrl"] = "",
                        ["releaseName"] = "",
                        ["releaseNotes"] = "",
                        ["updateAvailable"] = false,
                        ["hashRequired"] = true,
                        ["message"] = "No GitHub Release has been published yet.",
                    };
                }
                return new JObject { ["ok"] = false, ["message"] = ex.Message };
            }
        }

        public static JObject GetAutoUpdateStatus()
        {
            try
            {
                var settings = ReplaykitSettings.ReadSettings();
                bool enabled = settings["autoUpdateEnabled"]?.Value<bool>() ?? true;
                if (!enabled)
                {
                    return new JObject
                    {
                        ["ok"] = true, ["autoUpdateEnabled"] = false, ["updateAvailable"] = false,
                        ["prompt"] = false, ["message"] = "Automatic update prompts are disabled.",
                    };
                }

                var status = GetUpdateStatus();
                status["autoUpdateEnabled"] = true;
                status["prompt"] = false;
                if ((status["ok"]?.Value<bool>() ?? false) && (status["updateAvailable"]?.Value<bool>() ?? false) && !string.IsNullOrWhiteSpace(status["latestVersion"]?.Value<string>()))
                {
                    string dismissed = (settings["lastUpdatePromptVersion"]?.Value<string>() ?? "").Trim();
                    if (!string.IsNullOrWhiteSpace(dismissed)) dismissed = NormalizeVersion(dismissed);
                    status["prompt"] = dismissed != status["latestVersion"]?.Value<string>();
                }
                return status;
            }
            catch (Exception ex)
            {
                return new JObject { ["ok"] = false, ["prompt"] = false, ["message"] = ex.Message };
            }
        }

        public static JObject GetStartupUpdateStatus()
        {
            WriteUpdateDebug("GetStartupUpdateStatus invoked (alreadyChecked=" + Server.State.ReplaykitStartupUpdateChecked + " admin=" + BrowserCookies.TestIsAdmin() + ")");
            // check-and-claim has to be one atomic op, or two concurrent callers at startup could both see "not checked yet" and both fire the github api call below.
            bool alreadyClaimed;
            lock (Server.State.UpdateCheckLock)
            {
                alreadyClaimed = Server.State.ReplaykitStartupUpdateChecked;
                Server.State.ReplaykitStartupUpdateChecked = true;
            }
            if (alreadyClaimed)
            {
                WriteUpdateDebug("returning alreadyChecked");
                return new JObject
                {
                    ["ok"] = true, ["prompt"] = false, ["startupCheck"] = true,
                    ["alreadyChecked"] = true, ["message"] = "Startup update check already ran.",
                };
            }

            // honors the autoUpdateEnabled toggle, but intentionally does NOT consult lastUpdatePromptVersion here -- the popup should show on every launch, since a previous "later" click shouldnt suppress it forever; the admin gate is also dropped since install self-elevates via uac if needed.
            JObject settings;
            try { settings = ReplaykitSettings.ReadSettings(); }
            catch (Exception) { settings = new JObject { ["autoUpdateEnabled"] = true }; }
            bool enabled = settings["autoUpdateEnabled"]?.Value<bool>() ?? true;
            if (!enabled)
            {
                var disabled = new JObject
                {
                    ["ok"] = true, ["prompt"] = false, ["startupCheck"] = true,
                    ["admin"] = BrowserCookies.TestIsAdmin(), ["autoUpdateEnabled"] = false,
                    ["message"] = "Automatic update prompts are disabled in settings.",
                };
                WriteUpdateDebug("autoUpdateEnabled=false; returning prompt=false");
                return disabled;
            }

            var status = GetUpdateStatus();
            status["startupCheck"] = true;
            status["admin"] = BrowserCookies.TestIsAdmin();
            status["autoUpdateEnabled"] = true;
            status["prompt"] = (status["ok"]?.Value<bool>() ?? false) && (status["updateAvailable"]?.Value<bool>() ?? false) && !string.IsNullOrWhiteSpace(status["latestVersion"]?.Value<string>());
            try { WriteUpdateDebug("startup-check result: " + status.ToString(Formatting.None)); } catch (Exception) { }
            return status;
        }

        public static JObject SetUpdatePromptDismissed(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) throw new InvalidOperationException("Missing update version.");
            string normalized = NormalizeVersion(version);
            var settings = ReplaykitSettings.ReadSettings();
            settings["lastUpdatePromptVersion"] = normalized;
            ReplaykitSettings.WriteSettings(settings);
            return new JObject { ["ok"] = true, ["version"] = normalized };
        }

        // written by the detached installer on its way out (ReplayKitSetup/Update.cs WriteResult). the popup polls this so a failed install shows its real reason instead of sitting on its last progress stage until the watchdog gives up.
        private static string InstallResultPath() => Path.Combine(Constants.LOG_DIR, "update_result.json");

        public static JObject GetInstallResult()
        {
            var result = new JObject { ["ok"] = true, ["present"] = false, ["installedVersion"] = "" };
            try { result["installedVersion"] = NormalizeVersion(GetInstalledVersion()); }
            catch (Exception) { }
            try
            {
                string path = InstallResultPath();
                if (!File.Exists(path)) return result;
                var data = JObject.Parse(File.ReadAllText(path));
                result["present"] = true;
                result["installOk"] = data["ok"]?.Value<bool>() ?? false;
                result["stage"] = data["stage"]?.Value<string>() ?? "";
                result["message"] = data["message"]?.Value<string>() ?? "";
                result["version"] = data["version"]?.Value<string>() ?? "";
                result["finishedAt"] = data["finishedAt"]?.Value<string>() ?? "";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                WriteUpdateDebug("install-result unreadable: " + ex.Message);
            }
            return result;
        }

        // cleared before each apply so the popup can never read a previous updates verdict as this ones.
        private static void ClearInstallResult()
        {
            try { File.Delete(InstallResultPath()); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        public static string GetUpdateTempDir() => Constants.UPDATE_DIR;

        private static void AssertSafeUpdateTemp(string path)
        {
            string root = Path.GetFullPath(Constants.REPLAYKIT_TEMP_ROOT).TrimEnd('\\');
            string full = Path.GetFullPath(path).TrimEnd('\\');
            if (Path.GetFileName(full) != "update") throw new InvalidOperationException("Update temp folder name is invalid.");
            if (!full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Update temp folder resolved outside %TEMP%\\ReplayKit.");
        }

        // wipe the update temp dir before a fresh download. a stray locked leftover (an aborted prior run, an av scan,
        // or a chromium profile some other process parked in here) must not abort the whole update -- if the full
        // recursive delete cant finish, just clear the two files this flow actually rewrites and carry on. only a lock
        // on those specific files is fatal, and then with a clear message instead of a cryptic child-file one.
        private static void ClearUpdateTemp(string tempDir)
        {
            if (!Directory.Exists(tempDir)) return;
            try
            {
                Directory.Delete(tempDir, true);
                return;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                WriteUpdateDebug("update temp not fully cleared (" + ex.Message + "); clearing download slots only");
            }
            foreach (var name in new[] { InstallerAsset, HashAsset })
            {
                string p = Path.Combine(tempDir, name);
                try { if (File.Exists(p)) File.Delete(p); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    throw new IOException("A previous update download in " + tempDir + " is still locked. Close any open ReplayKit update window, then try again.", ex);
                }
            }
        }

        private static void SaveUrlFile(string url, string path)
        {
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Update download URL must use HTTPS.");
            using (var response = Http.GetAsync(url).GetAwaiter().GetResult())
            {
                response.EnsureSuccessStatusCode();
                using (var stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                using (var fileStream = File.Create(path))
                {
                    stream.CopyTo(fileStream);
                }
            }
        }

        private static string GetSha256FromText(string text)
        {
            var m = Regex.Match(text, @"(?i)\b[a-f0-9]{64}\b");
            if (!m.Success) throw new InvalidOperationException("SHA-256 file did not contain a valid hash.");
            return m.Value.ToUpperInvariant();
        }

        private static string GetUpdateObsPath()
        {
            var procs = new List<Process>();
            foreach (var name in new[] { "obs64", "obs32", "obs" }) procs.AddRange(Process.GetProcessesByName(name));
            try
            {
                foreach (var p in procs)
                {
                    try { if (p.MainModule != null) return p.MainModule.FileName; }
                    catch (System.ComponentModel.Win32Exception) { }
                }
                foreach (var p in procs)
                {
                    try
                    {
                        using (var searcher = new ManagementObjectSearcher("SELECT ExecutablePath FROM Win32_Process WHERE ProcessId=" + p.Id))
                        {
                            foreach (ManagementObject mo in searcher.Get())
                            {
                                string path = mo["ExecutablePath"] as string;
                                if (!string.IsNullOrEmpty(path)) return path;
                            }
                        }
                    }
                    catch (ManagementException) { }
                }
                return "";
            }
            finally
            {
                foreach (var p in procs) p.Dispose();
            }
        }

        private static string PsQuote(string value) => "'" + (value ?? "").Replace("'", "''") + "'";

        // the installer can still be torn down mid-flight by something outside its control (this helper dies moments after obs does, and an av or a policy kill lands the same way), and a TerminateProcess skips its finally block -- so obs would stay dead with the popup waiting on a restart that never comes. this watchdog outlives all of it: detached + breakaway like the installer spawn itself, it waits for the installer pid to go away and only acts if no verdict was written, which is exactly the case where nobody else is going to bring obs back.
        private static void StartUpdateWatchdog(int installerPid, string obsPath, string targetVersion)
        {
            string script = string.Join("\n", new[]
            {
                "$ErrorActionPreference='SilentlyContinue'",
                "$installerPid=" + installerPid,
                "$result=" + PsQuote(InstallResultPath()),
                "$obs=" + PsQuote(obsPath ?? ""),
                "$target=" + PsQuote(targetVersion ?? ""),
                "$versionFile=" + PsQuote(GetVersionPath()),
                "$deadline=(Get-Date).AddMinutes(15)",
                "while ((Get-Date) -lt $deadline -and (Get-Process -Id $installerPid -ErrorAction SilentlyContinue)) { Start-Sleep -Seconds 3 }",
                "if (Test-Path -LiteralPath $result) { exit }",
                "$installed=''",
                "try { $installed=(Get-Content -LiteralPath $versionFile -Raw | ConvertFrom-Json).version } catch {}",
                "if (-not (Get-Process -Name obs64 -ErrorAction SilentlyContinue)) { if ($obs -and (Test-Path -LiteralPath $obs)) { Start-Process -FilePath $obs -WorkingDirectory (Split-Path -Parent $obs) } }",
                "if ($installed -eq $target) { $o=@{ok=$true;stage='done';message=\"ReplayKit $target installed; OBS was restarted by the update watchdog.\";version=$target} }",
                "else { $o=@{ok=$false;stage='aborted';message='The installer stopped before it finished. OBS has been restarted -- the update was not applied.';version=$target} }",
                "$o.finishedAt=(Get-Date).ToUniversalTime().ToString('o')",
                "[IO.File]::WriteAllText($result, ($o | ConvertTo-Json -Compress))",
            });
            string encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
            string cmdLine = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + encoded;
            int pid = Native.SpawnDetached(cmdLine, Constants.LOG_DIR);
            WriteUpdateDebug(pid > 0 ? "update watchdog started (pid=" + pid + ", watching installer " + installerPid + ")" : "update watchdog failed to start; a mid-install abort will not self-recover");
        }

        private static JObject StartUpdater(string installerPath, string tempDir, string targetVersion)
        {
            string obsPath = GetUpdateObsPath();
            // wait on this helpers own pid, not obs -- obs is already confirmed dead synchronously before the installer even reaches this wait, but this helper process (which holds the lock on its own exe under scripts/helper/) only exits afterward, asynchronously, once its parent-watchdog notices obs is gone. waiting on obs pid here races the copy step against that and loses.
            int waitPid = Process.GetCurrentProcess().Id;
            var argList = new List<string> { "--update", "--cleanup-dir", tempDir, "--start-delay-ms", "1200" };
            if (!string.IsNullOrWhiteSpace(obsPath)) { argList.Add("--relaunch-obs"); argList.Add(obsPath); }
            if (waitPid > 0) { argList.Add("--wait-pid"); argList.Add(waitPid.ToString()); }

            if (BrowserCookies.TestIsAdmin())
            {
                // helper is already elevated -- spawn detached with create_breakaway_from_job so the installer is NOT inside the helpers kill-on-close job, otherwise installer taskkills obs64, obs dies, the helpers parent-watchdog exits, the job closes, and the installer gets killed mid-copy (version.json never updates, obs never relaunches) -- this was a real bug; SpawnDetached is the same primitive used elsewhere in this port for exactly this reason.
                string cmdLine = ProcessArgs.Quote(installerPath) + " " + ProcessArgs.Join(argList.ToArray());
                WriteUpdateDebug("StartUpdater (admin, detached): " + cmdLine);
                int installerPid = Native.SpawnDetached(cmdLine, tempDir);
                if (installerPid <= 0) throw new InvalidOperationException("SpawnDetached returned 0 for installer");
                StartUpdateWatchdog(installerPid, obsPath, targetVersion);
                return new JObject { ["ok"] = true, ["processId"] = installerPid };
            }

            // helper is unelevated -- ShellExecute+verb=runas triggers uac, and the new admin process is its own session (different token) so it is NOT inherited into the helpers job, meaning the kill-on-close chain that affects admin spawns doesnt apply here.
            var psi = new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = ProcessArgs.Join(argList.ToArray()),
                WorkingDirectory = tempDir,
                UseShellExecute = true,
                Verb = "runas",
                // CreateNoWindow is ignored under UseShellExecute, so WindowStyle is the only knob that actually suppresses the consoles window here.
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            WriteUpdateDebug("StartUpdater (non-admin, runas): " + installerPath);
            var proc = Process.Start(psi);
            StartUpdateWatchdog(proc.Id, obsPath, targetVersion);
            return new JObject { ["ok"] = true, ["processId"] = proc.Id };
        }

        public static JObject ApplyUpdate()
        {
            // claim before doing anything else -- a second concurrent call would otherwise race the delete + recreate + download into the same fixed temp dir below.
            bool claimed;
            lock (Server.State.UpdateCheckLock)
            {
                if (Server.State.UpdateApplyInProgress) return new JObject { ["ok"] = false, ["message"] = "An update is already being applied." };
                Server.State.UpdateApplyInProgress = true;
                claimed = true;
            }
            try
            {
                string installed = NormalizeVersion(GetInstalledVersion());
                var latest = GetLatestRelease();
                if (CompareVersion(installed, latest.LatestVersion) >= 0)
                {
                    return new JObject
                    {
                        ["ok"] = true, ["updateAvailable"] = false,
                        ["installedVersion"] = installed, ["latestVersion"] = latest.LatestVersion,
                        ["message"] = "ReplayKit is already up to date.",
                    };
                }
                if (string.IsNullOrWhiteSpace(latest.HashUrl)) throw new InvalidOperationException("Latest release is missing " + HashAsset + ".");

                string tempDir = GetUpdateTempDir();
                AssertSafeUpdateTemp(tempDir);
                ClearUpdateTemp(tempDir);
                Directory.CreateDirectory(tempDir);

                string installerPath = Path.Combine(tempDir, InstallerAsset);
                string hashPath = Path.Combine(tempDir, HashAsset);
                SaveUrlFile(latest.InstallerUrl, installerPath);
                SaveUrlFile(latest.HashUrl, hashPath);

                string expected = GetSha256FromText(File.ReadAllText(hashPath));
                string actual;
                using (var sha = SHA256.Create())
                using (var stream = File.OpenRead(installerPath))
                {
                    actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToUpperInvariant();
                }
                if (actual != expected)
                {
                    try { Directory.Delete(tempDir, true); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                    throw new InvalidOperationException("Downloaded installer hash did not match the release hash.");
                }

                long installerBytes = new FileInfo(installerPath).Length;
                if (installerBytes < MinInstallerBytes)
                {
                    try { Directory.Delete(tempDir, true); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                    throw new InvalidOperationException("The " + latest.LatestVersion + " installer is incomplete (" + (installerBytes / (1024 * 1024)) + " MB) -- it was published without its bundled ReplayKit files. OBS was left running; try again once a fixed release is out.");
                }

                ClearInstallResult();
                var started = StartUpdater(installerPath, tempDir, latest.LatestVersion);
                return new JObject
                {
                    ["ok"] = true, ["updateAvailable"] = true, ["installing"] = true,
                    ["installedVersion"] = installed, ["latestVersion"] = latest.LatestVersion,
                    ["processId"] = started["processId"],
                    ["message"] = "ReplayKit " + latest.LatestVersion + " is installing. OBS will restart.",
                };
            }
            catch (Exception ex)
            {
                return new JObject { ["ok"] = false, ["message"] = ex.Message };
            }
            finally
            {
                if (claimed)
                {
                    lock (Server.State.UpdateCheckLock) { Server.State.UpdateApplyInProgress = false; }
                }
            }
        }
    }
}

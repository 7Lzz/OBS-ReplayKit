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
        private const string ReleasePage = "https://github.com/7Lzz/OBS-ReplayKit/releases/latest";

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
                        ["releaseUrl"] = ReleasePage,
                        ["releaseName"] = "",
                        ["releaseNotes"] = "",
                        ["updateAvailable"] = false,
                        ["hashRequired"] = true,
                        ["message"] = "No GitHub Release has been published yet.",
                    };
                }
                return new JObject { ["ok"] = false, ["message"] = ex.Message, ["releaseUrl"] = ReleasePage };
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

            // a failed update restarts OBS, which runs this check again -- without these two guards the user gets a
            // fresh update window stacked on the one still showing them why the last attempt failed.
            if (status["prompt"]?.Value<bool>() ?? false)
            {
                string suppress = StartupPromptSuppressedBecause(status["latestVersion"]?.Value<string>());
                if (suppress != null)
                {
                    status["prompt"] = false;
                    status["message"] = suppress;
                    WriteUpdateDebug("startup prompt suppressed: " + suppress);
                }
            }
            try { WriteUpdateDebug("startup-check result: " + status.ToString(Formatting.None)); } catch (Exception) { }
            return status;
        }

        // how long a failed attempt keeps the startup prompt quiet. long enough to cover the restart the failure
        // itself causes, short enough that the next real session still offers the update.
        private static readonly TimeSpan FailureQuietPeriod = TimeSpan.FromMinutes(10);

        // null when the prompt should show. the update window is its own browser process, so it survives the OBS
        // restart a failed update triggers -- opening a second one on top of it is the loop the user sees.
        private static string StartupPromptSuppressedBecause(string latestVersion)
        {
            try
            {
                if (Native.WindowWithTitleExists("ReplayKit Update"))
                    return "An update window is already open.";
            }
            catch (Exception ex) { WriteUpdateDebug("update window probe failed: " + ex.Message); }

            try
            {
                string path = InstallResultPath();
                if (!File.Exists(path)) return null;
                var data = JObject.Parse(File.ReadAllText(path));
                if (data["ok"]?.Value<bool>() ?? true) return null;
                // never suppress a verdict the user has not been shown yet -- the window reopens to report it.
                if (!(data["seen"]?.Value<bool>() ?? false)) return null;
                // only for the version that failed -- a newer release should still be offered straight away.
                string failedVersion = data["version"]?.Value<string>() ?? "";
                if (!string.IsNullOrEmpty(latestVersion) && !string.Equals(failedVersion, latestVersion, StringComparison.OrdinalIgnoreCase)) return null;
                var finishedAt = data["finishedAt"]?.Value<DateTime?>();
                if (finishedAt == null) return null;
                if (DateTime.UtcNow - finishedAt.Value.ToUniversalTime() > FailureQuietPeriod) return null;
                return "The last update attempt failed; not prompting again yet.";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                return null;
            }
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

        // the detached installer records its own pid here (ReplayKitSetup DetachedSpawn.WriteOwnPid) -- the process
        // the helper starts is only a launcher and exits immediately, so watching that pid would read as "died".
        private static string InstallPidPath() => Path.Combine(Constants.LOG_DIR, "update_pid");

        public static JObject GetInstallResult()
        {
            var result = new JObject { ["ok"] = true, ["present"] = false, ["installedVersion"] = "", ["releaseUrl"] = ReleasePage };
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
                result["releaseUrl"] = data["releaseUrl"]?.Value<string>() ?? ReleasePage;
                // "seen" means the update window has actually shown this verdict to the user. closing that window
                // with the X runs no script at all, so a result can otherwise be written and never read by anyone.
                result["seen"] = data["seen"]?.Value<bool>() ?? false;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                WriteUpdateDebug("install-result unreadable: " + ex.Message);
            }
            return result;
        }

        // stamped by the update window once it has displayed the verdict. until then the verdict counts as unseen and
        // the next startup check reopens the window to show it, rather than the outcome being silently swallowed.
        public static JObject MarkInstallResultSeen()
        {
            try
            {
                string path = InstallResultPath();
                if (!File.Exists(path)) return new JObject { ["ok"] = true, ["present"] = false };
                var data = JObject.Parse(File.ReadAllText(path));
                data["seen"] = true;
                File.WriteAllText(path, data.ToString(Formatting.Indented) + "\n", new System.Text.UTF8Encoding(false));
                return new JObject { ["ok"] = true, ["present"] = true };
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                return new JObject { ["ok"] = false, ["message"] = ex.Message };
            }
        }

        // cleared before each apply so the popup can never read a previous updates verdict as this ones.
        private static void ClearInstallResult()
        {
            foreach (string path in new[] { InstallResultPath(), InstallPidPath() })
            {
                try { File.Delete(path); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }

        // chromium browsers the update window can be hosted in, in the order replaykit_update_bootstrap.ps1 tries
        // them. the dock cannot open this window itself -- window.open from a page inside OBS's CEF escapes to the
        // users real browser and lands as an ordinary tab, so the helper spawns the --app window instead.
        private static string FindPromptBrowser(out string browserName)
        {
            string programFiles = Environment.GetEnvironmentVariable("ProgramFiles") ?? "";
            string programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? "";
            string localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
            var candidates = new[]
            {
                new[] { "msedge", Path.Combine(programFilesX86, @"Microsoft\Edge\Application\msedge.exe") },
                new[] { "msedge", Path.Combine(programFiles, @"Microsoft\Edge\Application\msedge.exe") },
                new[] { "chrome", Path.Combine(programFiles, @"Google\Chrome\Application\chrome.exe") },
                new[] { "chrome", Path.Combine(programFilesX86, @"Google\Chrome\Application\chrome.exe") },
                new[] { "chrome", Path.Combine(localAppData, @"Google\Chrome\Application\chrome.exe") },
                new[] { "brave", Path.Combine(programFiles, @"BraveSoftware\Brave-Browser\Application\brave.exe") },
                new[] { "brave", Path.Combine(programFilesX86, @"BraveSoftware\Brave-Browser\Application\brave.exe") },
                new[] { "brave", Path.Combine(localAppData, @"BraveSoftware\Brave-Browser\Application\brave.exe") },
                new[] { "vivaldi", Path.Combine(localAppData, @"Vivaldi\Application\vivaldi.exe") },
            };
            foreach (var candidate in candidates)
            {
                if (string.IsNullOrEmpty(candidate[1])) continue;
                if (File.Exists(candidate[1])) { browserName = candidate[0]; return candidate[1]; }
            }
            browserName = "";
            return "";
        }

        // opens the same borderless update window the startup check shows, so an update started from Settings runs
        // the identical flow. returns ok=false when no chromium browser is installed, so the caller can fall back to
        // the github link instead of pretending a window appeared.
        public static JObject OpenUpdatePromptWindow(string version)
        {
            // one window is enough -- bring the existing one forward rather than stacking another browser process on it.
            try
            {
                if (Native.WindowWithTitleExists("ReplayKit Update"))
                {
                    Native.FocusWindow("ReplayKit Update");
                    WriteUpdateDebug("update prompt already open; focused it instead of opening another");
                    return new JObject { ["ok"] = true, ["alreadyOpen"] = true };
                }
            }
            catch (Exception ex) { WriteUpdateDebug("update window probe failed: " + ex.Message); }

            string browserName;
            string browser = FindPromptBrowser(out browserName);
            if (string.IsNullOrEmpty(browser))
                return new JObject { ["ok"] = false, ["message"] = "No supported browser was found to show the update window." };

            int port = Server.State.Config?["port"]?.Value<int?>() ?? Constants.DEFAULT_PORT;
            string url = "http://127.0.0.1:" + port + "/update-prompt";
            if (!string.IsNullOrWhiteSpace(version)) url += "?version=" + Uri.EscapeDataString(version);

            // its own profile so the users real browser data is never touched, and deliberately NOT under the update
            // temp dir -- /update/apply wipes that, and a browser holding a lockfile in there would break the delete.
            string profileDir = Path.Combine(Constants.REPLAYKIT_TEMP_ROOT, "update-prompt-profile-" + browserName);
            try { Directory.CreateDirectory(profileDir); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }

            int width = 700, height = 580, x = 200, y = 200;
            try
            {
                var bounds = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                x = bounds.X + (bounds.Width - width) / 2;
                y = bounds.Y + (bounds.Height - height) / 2;
            }
            catch (Exception ex) { WriteUpdateDebug("prompt window centering failed: " + ex.Message); }

            var args = new[]
            {
                "--app=" + url,
                "--window-size=" + width + "," + height,
                "--window-position=" + x + "," + y,
                "--no-first-run",
                "--no-default-browser-check",
                "--user-data-dir=" + profileDir,
            };
            try
            {
                var psi = new ProcessStartInfo(browser, ProcessArgs.Join(args)) { UseShellExecute = false, CreateNoWindow = true };
                var proc = Process.Start(psi);
                WriteUpdateDebug("update prompt opened from settings: " + browserName + " pid=" + (proc != null ? proc.Id : 0) + " url=" + url);
                return new JObject { ["ok"] = true, ["browser"] = browserName };
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            {
                WriteUpdateDebug("update prompt spawn failed: " + ex.Message);
                return new JObject { ["ok"] = false, ["message"] = "Could not open the update window: " + ex.Message };
            }
        }

        public static string GetUpdateTempDir() => Constants.UPDATE_DIR;

        private static void AssertSafeUpdateTemp(string path)
        {
            string root = Path.GetFullPath(Constants.REPLAYKIT_TEMP_ROOT).TrimEnd('\\');
            string full = Path.GetFullPath(path).TrimEnd('\\');
            string name = Path.GetFileName(full);
            if (!Regex.IsMatch(name ?? "", @"^update-[a-f0-9]{32}$", RegexOptions.IgnoreCase))
                throw new InvalidOperationException("Update temp folder name is invalid.");
            if (!full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Update temp folder resolved outside %TEMP%\\ReplayKit.");
        }

        private static string CreateUpdateTemp()
        {
            Directory.CreateDirectory(Constants.REPLAYKIT_TEMP_ROOT);
            string path = Path.Combine(Constants.REPLAYKIT_TEMP_ROOT, "update-" + Guid.NewGuid().ToString("N"));
            AssertSafeUpdateTemp(path);
            Directory.CreateDirectory(path);
            return path;
        }

        private static void SaveUrlFile(string url, string path)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("Update download URL must use HTTPS.");
            string temp = path + ".part";
            Exception failure = null;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                    using (var response = Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
                    {
                        response.EnsureSuccessStatusCode();
                        using (var stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                        using (var fileStream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, FileOptions.WriteThrough))
                        {
                            stream.CopyTo(fileStream);
                            fileStream.Flush(true);
                        }
                    }
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(temp, path);
                    return;
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is UnauthorizedAccessException || ex is System.Threading.Tasks.TaskCanceledException)
                {
                    failure = ex;
                    WriteUpdateDebug("download attempt " + attempt + " failed for " + Path.GetFileName(path) + ": " + ex.Message);
                    if (attempt < 3) System.Threading.Thread.Sleep(attempt * 750);
                }
            }
            try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            throw new IOException("Downloading " + Path.GetFileName(path) + " failed after 3 attempts.", failure);
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

        // The watchdog is a detached copy of this C# helper. Keeping it native avoids
        // hidden PowerShell, execution-policy bypasses, and encoded commands.
        private static void StartUpdateWatchdog(int installerPid, string obsPath, string targetVersion, string releaseUrl)
        {
            string source = Process.GetCurrentProcess().MainModule.FileName;
            string watchdogDir = Path.Combine(Constants.REPLAYKIT_TEMP_ROOT, "watchdog-" + Guid.NewGuid().ToString("N"));
            string watchdogExe = Path.Combine(watchdogDir, "OBSReplayKit-watchdog.exe");
            try
            {
                Directory.CreateDirectory(watchdogDir);
                File.Copy(source, watchdogExe, true);
            }
            catch (Exception ex)
            {
                WriteUpdateDebug("update watchdog copy failed: " + ex.Message);
                return;
            }
            var args = new[]
            {
                "--update-watchdog", "-LauncherPid", installerPid.ToString(),
                "-PidFile", InstallPidPath(), "-Result", InstallResultPath(),
                "-ObsPath", obsPath ?? "", "-TargetVersion", targetVersion ?? "",
                "-ReleaseUrl", releaseUrl ?? ReleasePage, "-VersionPath", GetVersionPath(),
            };
            string cmdLine = ProcessArgs.Quote(watchdogExe) + " " + ProcessArgs.Join(args);
            int pid = Native.SpawnDetached(cmdLine, Constants.LOG_DIR);
            WriteUpdateDebug(pid > 0 ? "update watchdog started (pid=" + pid + ", watching installer " + installerPid + ")" : "update watchdog failed to start; a mid-install abort will not self-recover");
        }

        private static JObject StartUpdater(string installerPath, string tempDir, string targetVersion, string releaseUrl)
        {
            string obsPath = GetUpdateObsPath();
            // wait on this helpers own pid, not obs -- obs is already confirmed dead synchronously before the installer even reaches this wait, but this helper process (which holds the lock on its own exe under scripts/helper/) only exits afterward, asynchronously, once its parent-watchdog notices obs is gone. waiting on obs pid here races the copy step against that and loses.
            int waitPid = Process.GetCurrentProcess().Id;
            var argList = new List<string> { "--update", "--cleanup-dir", tempDir, "--start-delay-ms", "1200" };
            argList.Add("--release-url");
            argList.Add(releaseUrl ?? ReleasePage);
            if (!string.IsNullOrWhiteSpace(obsPath)) { argList.Add("--relaunch-obs"); argList.Add(obsPath); }
            if (waitPid > 0) { argList.Add("--wait-pid"); argList.Add(waitPid.ToString()); }

            if (BrowserCookies.TestIsAdmin())
            {
                // helper is already elevated -- spawn detached with create_breakaway_from_job so the installer is NOT inside the helpers kill-on-close job, otherwise installer taskkills obs64, obs dies, the helpers parent-watchdog exits, the job closes, and the installer gets killed mid-copy (version.json never updates, obs never relaunches) -- this was a real bug; SpawnDetached is the same primitive used elsewhere in this port for exactly this reason.
                string cmdLine = ProcessArgs.Quote(installerPath) + " " + ProcessArgs.Join(argList.ToArray());
                WriteUpdateDebug("StartUpdater (admin, detached): " + cmdLine);
                int installerPid = Native.SpawnDetached(cmdLine, tempDir);
                if (installerPid <= 0) throw new InvalidOperationException("SpawnDetached returned 0 for installer");
                StartUpdateWatchdog(installerPid, obsPath, targetVersion, releaseUrl);
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
            StartUpdateWatchdog(proc.Id, obsPath, targetVersion, releaseUrl);
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
            string releaseUrl = ReleasePage;
            try
            {
                string installed = NormalizeVersion(GetInstalledVersion());
                var latest = GetLatestRelease();
                if (!string.IsNullOrWhiteSpace(latest.HtmlUrl)) releaseUrl = latest.HtmlUrl;
                if (CompareVersion(installed, latest.LatestVersion) >= 0)
                {
                    return new JObject
                    {
                        ["ok"] = true, ["updateAvailable"] = false,
                        ["installedVersion"] = installed, ["latestVersion"] = latest.LatestVersion,
                        ["releaseUrl"] = releaseUrl,
                        ["message"] = "ReplayKit is already up to date.",
                    };
                }
                if (string.IsNullOrWhiteSpace(latest.HashUrl)) throw new InvalidOperationException("Latest release is missing " + HashAsset + ".");

                string tempDir = CreateUpdateTemp();

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
                var started = StartUpdater(installerPath, tempDir, latest.LatestVersion, releaseUrl);
                return new JObject
                {
                    ["ok"] = true, ["updateAvailable"] = true, ["installing"] = true,
                    ["installedVersion"] = installed, ["latestVersion"] = latest.LatestVersion,
                    ["releaseUrl"] = releaseUrl,
                    ["processId"] = started["processId"],
                    ["message"] = "ReplayKit " + latest.LatestVersion + " is installing. OBS will restart.",
                };
            }
            catch (Exception ex)
            {
                return new JObject { ["ok"] = false, ["message"] = ex.Message, ["releaseUrl"] = releaseUrl };
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

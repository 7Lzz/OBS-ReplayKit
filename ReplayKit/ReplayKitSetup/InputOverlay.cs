using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;

namespace ReplayKitSetup
{
    // install the input-overlay plugin from its raw bundled files and extract its preset pack. downloads the vc++ redist on demand if missing
    public static class InputOverlay
    {
        // canonical install paths -- same locations the official inno setup installer used to write, so nothing downstream (obs, cli.cs) needed to change.
        private static readonly string PluginDll = Path.Combine(Config.PROGRAMFILES_OBS_DIR, "obs-plugins", "64bit", "input-overlay.dll");
        private static readonly string PluginSdl2 = Path.Combine(Config.PROGRAMFILES_OBS_DIR, "obs-plugins", "64bit", "SDL2.dll");

        private const string MouseLayoutRel = "mouse/mouse-no-movement.json";
        private const int MouseLayoutWidth = 285;
        private const int MouseLayoutHeight = 421;

        // same safe zip-walk shape as BongoCat.cs -- bounds-checks every entry against Config.PROGRAMFILES_OBS_DIR before writing, so a malformed archive path can never escape the install root.
        private const string PluginZipRoot = "input-overlay/";

        // vc++ 2015-2022 (x64) redistributable -- prerequisite for input-overlay.dll. downloaded from microsoft only when missing.
        private static readonly HashSet<string> VcppAllowedDownloadHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "aka.ms",
            "download.visualstudio.microsoft.com",
        };

        // check the hklm ...\visualstudio\14.0\vc\runtimes\x64\installed flag microsoft flips to 1 on a successful redist install.
        private static bool IsVcpp20152022X64Installed()
        {
            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64"))
                {
                    if (key == null) return false;
                    var installed = key.GetValue("Installed");
                    return installed != null && Convert.ToInt32(installed) == 1;
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is IOException)
            {
                return false;
            }
        }

        private static string DownloadVcppRedist(Action<string> log = null)
        {
            string tmpdir = Path.Combine(Path.GetTempPath(), "obsreplaykit_vcredist_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpdir);
            string target = Path.Combine(tmpdir, "vc_redist.x64.exe");
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var handler = new HttpClientHandler { AllowAutoRedirect = true };
                using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) })
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "OBSReplayKit");
                    Download.Save(client, Config.VCPP_REDIST_DOWNLOAD_URL, target, Config.VCPP_REDIST_DOWNLOAD_MAX_BYTES,
                        TimeSpan.FromSeconds(60), allowedHosts: VcppAllowedDownloadHosts);
                    byte[] hash;
                    using (var sha = SHA256.Create())
                    using (var file = File.OpenRead(target)) hash = sha.ComputeHash(file);
                    string hex = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                    long mb = Math.Max(1, new FileInfo(target).Length / (1024 * 1024));
                    log?.Invoke($"downloaded VC++ redist from Microsoft ({mb} MB, sha256 {hex.Substring(0, 12)}...)");
                }
                return target;
            }
            catch (Exception exc)
            {
                try { Directory.Delete(tmpdir, true); } catch (IOException) { }
                log?.Invoke("VC++ redist download failed: " + exc.Message);
                return null;
            }
        }

        // shells out to windows own Get-AuthenticodeSignature rather than reimplementing authenticode verification natively -- this is a security-critical check (confirms the downloaded exe is genuinely microsoft-signed before running it), and the well-tested os cmdlet is a safer bet here than a hand-rolled WinVerifyTrust p/invoke.
        internal static bool IsMicrosoftSigned(string path, Action<string> log = null)
        {
            const string script = @"
$sig = Get-AuthenticodeSignature -LiteralPath $env:OBSREPLAYKIT_REDIST_PATH
$cert = $sig.SignerCertificate
[pscustomobject]@{
  Status = [string]$sig.Status
  Subject = if ($cert) { [string]$cert.Subject } else { """" }
  Issuer = if ($cert) { [string]$cert.Issuer } else { """" }
} | ConvertTo-Json -Compress
";
            using var proc = new Process();
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
                psi.EnvironmentVariables["OBSREPLAYKIT_REDIST_PATH"] = path;
                proc.StartInfo = psi;
                proc.Start();
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();
                if (!proc.WaitForExit(30000))
                {
                    try { proc.Kill(); } catch (InvalidOperationException) { }
                    log?.Invoke("Microsoft installer signature check failed to run: timed out");
                    return false;
                }
                stdout = stdoutTask.GetAwaiter().GetResult();
                stderr = stderrTask.GetAwaiter().GetResult();
            }
            catch (Exception exc) when (exc is System.ComponentModel.Win32Exception || exc is InvalidOperationException)
            {
                log?.Invoke("Microsoft installer signature check failed to run: " + exc.Message);
                return false;
            }
            if (proc.ExitCode != 0)
            {
                log?.Invoke("Microsoft installer signature check failed: " + Truncate(stderr.Trim(), 160));
                return false;
            }

            JObject info;
            try { info = JObject.Parse(stdout); }
            catch (Newtonsoft.Json.JsonException)
            {
                log?.Invoke("Microsoft installer signature check returned invalid data");
                return false;
            }
            string status = info.Value<string>("Status") ?? "";
            string subject = info.Value<string>("Subject") ?? "";
            string issuer = info.Value<string>("Issuer") ?? "";
            bool ok = status == "Valid" && subject.Contains("Microsoft Corporation") && issuer.Contains("Microsoft");
            if (!ok) log?.Invoke($"Microsoft installer signature rejected: status={status} subject={Truncate(subject, 80)}");
            return ok;
        }

        private static string Truncate(string s, int max) => s.Length > max ? s.Substring(0, max) : s;

        // download + run the vc++ redist if missing. without it the plugin dlls msvcp140/vcruntime140 imports fail and obs reports "plugin load error".
        public static bool InstallVcppRedist(Action<string> log = null)
        {
            if (IsVcpp20152022X64Installed())
            {
                log?.Invoke("VC++ 2015-2022 (x64) Redistributable already installed");
                return true;
            }

            log?.Invoke("VC++ redist missing; downloading latest x64 package from Microsoft...");
            string redist = DownloadVcppRedist(log);
            if (redist == null) return false;
            if (!IsMicrosoftSigned(redist, log))
            {
                try { Directory.Delete(Path.GetDirectoryName(redist), true); } catch (IOException) { }
                return false;
            }

            log?.Invoke("installing VC++ 2015-2022 Redistributable (silent)...");

            using var proc = new Process();
            string stderr;
            try
            {
                var psi = new ProcessStartInfo(redist, Win32Args.Build("/install", "/quiet", "/norestart"))
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                proc.StartInfo = psi;
                proc.Start();
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();
                if (!proc.WaitForExit(300000))
                {
                    try { proc.Kill(); } catch (InvalidOperationException) { }
                    log?.Invoke("VC++ redist install timed out after 300s");
                    return false;
                }
                stdoutTask.GetAwaiter().GetResult();
                stderr = stderrTask.GetAwaiter().GetResult();
            }
            catch (Exception exc) when (exc is System.ComponentModel.Win32Exception || exc is InvalidOperationException)
            {
                log?.Invoke("VC++ redist launch failed: " + exc.Message);
                return false;
            }
            finally
            {
                try { Directory.Delete(Path.GetDirectoryName(redist), true); } catch (IOException) { }
            }

            // ms installer success codes: 0 installed, 1638 newer already installed, 3010 success-with-restart-requested (we passed /norestart so we treat it as ok).
            if (proc.ExitCode == 0 || proc.ExitCode == 1638 || proc.ExitCode == 3010)
            {
                log?.Invoke($"VC++ redist install completed (exit {proc.ExitCode})");
                return true;
            }

            log?.Invoke($"VC++ redist install FAILED (exit {proc.ExitCode})");
            if (!string.IsNullOrWhiteSpace(stderr)) log?.Invoke("stderr: " + stderr.Trim().Split('\n')[0]);
            return false;
        }

        // description of the installed plugin, or null.
        private static string AlreadyInstalled()
        {
            return BundledFiles.IsCurrent(Config.INPUT_OVERLAY_PLUGIN_ZIP, Config.PROGRAMFILES_OBS_DIR, PluginZipRoot) ? "installed at " + PluginDll : null;
        }

        // install input-overlay and verify obs can load its dlls.
        public static bool InstallInputOverlayPlugin(Action<string> log = null)
        {
            if (!InstallVcppRedist(log))
            {
                log?.Invoke("input-overlay prerequisite missing; skipping input-overlay install");
                return false;
            }

            string existing = AlreadyInstalled();
            if (existing != null)
            {
                log?.Invoke("already " + existing);
                return true;
            }

            if (!File.Exists(Config.INPUT_OVERLAY_PLUGIN_ZIP))
            {
                log?.Invoke($"(no {Path.GetFileName(Config.INPUT_OVERLAY_PLUGIN_ZIP)} bundled, skipping)");
                return false;
            }

            if (!Obs.CoreInstalledAt(Config.PROGRAMFILES_OBS_DIR))
            {
                log?.Invoke($"OBS not installed at {Config.PROGRAMFILES_OBS_DIR} (no bin\\64bit\\obs64.exe) - install OBS first, then re-run");
                return false;
            }

            log?.Invoke($"extracting {Path.GetFileName(Config.INPUT_OVERLAY_PLUGIN_ZIP)} -> {Config.PROGRAMFILES_OBS_DIR}");

            try
            {
                if (!BundledFiles.Install(Config.INPUT_OVERLAY_PLUGIN_ZIP, Config.PROGRAMFILES_OBS_DIR, log, PluginZipRoot)) return false;
            }
            catch (Exception exc)
            {
                log?.Invoke("input-overlay extraction failed: " + exc.Message);
                return false;
            }

            if (!File.Exists(PluginDll))
            {
                log?.Invoke("installer ran but " + PluginDll + " is missing");
                return false;
            }
            if (!File.Exists(PluginSdl2))
            {
                // Both bundled DLLs are required; a partial install must be reported as failed.
                log?.Invoke("warn: SDL2.dll missing at " + PluginSdl2 + " - plugin cannot be verified");
                return false;
            }

            log?.Invoke("installed -> " + PluginDll);
            log?.Invoke($"           + {Path.GetFileName(PluginSdl2)} ({(File.Exists(PluginSdl2) ? "ok" : "MISSING!")})");
            log?.Invoke("    OBS will load it on next launch (Tools menu)");
            return true;
        }

        private static byte[] PresetBytes(string relative, byte[] bytes)
        {
            if (relative != MouseLayoutRel) return bytes;
            var data = JObject.Parse(System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
            data["default_width"] = MouseLayoutWidth;
            data["default_height"] = MouseLayoutHeight;
            return new System.Text.UTF8Encoding(false).GetBytes(data.ToString(Newtonsoft.Json.Formatting.Indented));
        }

        public static bool InstallInputOverlayPresets(Action<string> log = null)
        {
            if (!File.Exists(Config.INPUT_OVERLAY_ZIP))
            {
                log?.Invoke("warn: the bundled input-overlay presets are missing.");
                return false;
            }
            try
            {
                if (BundledFiles.IsCurrent(Config.INPUT_OVERLAY_ZIP, Config.INPUT_OVERLAY_TARGET, "input-overlay-presets/", normalize: PresetBytes)) return true;
                return BundledFiles.Install(Config.INPUT_OVERLAY_ZIP, Config.INPUT_OVERLAY_TARGET, log, "input-overlay-presets/", normalize: PresetBytes);
            }
            catch (Exception ex)
            {
                log?.Invoke("warn: input-overlay preset repair failed: " + ex.Message);
                return false;
            }
        }
    }
}

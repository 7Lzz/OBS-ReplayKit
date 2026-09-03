using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;

namespace ReplayKitSetup
{
    // install the input-overlay plugin from its raw bundled files and extract its preset pack. downloads the vc++ redist on demand if missing. ported from obs_replaykit/input_overlay.py.
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
                    var response = client.GetAsync(Config.VCPP_REDIST_DOWNLOAD_URL, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                    Uri finalUri = response.RequestMessage.RequestUri;
                    string host = (finalUri.Host ?? "").ToLowerInvariant();
                    if (!string.Equals(finalUri.Scheme, "https", StringComparison.OrdinalIgnoreCase) || !VcppAllowedDownloadHosts.Contains(host))
                    {
                        throw new InvalidOperationException("unexpected download host: " + finalUri);
                    }

                    long? length = response.Content.Headers.ContentLength;
                    if (length.HasValue && length.Value > Config.VCPP_REDIST_DOWNLOAD_MAX_BYTES)
                    {
                        throw new InvalidOperationException("download is larger than expected");
                    }

                    long total = 0;
                    byte[] hash;
                    using (var sha = SHA256.Create())
                    {
                        using (var stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                        using (var fh = File.Open(target, FileMode.Create, FileAccess.Write))
                        {
                            var buffer = new byte[1024 * 1024];
                            int read;
                            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                total += read;
                                if (total > Config.VCPP_REDIST_DOWNLOAD_MAX_BYTES) throw new InvalidOperationException("download exceeded size limit");
                                sha.TransformBlock(buffer, 0, read, null, 0);
                                fh.Write(buffer, 0, read);
                            }
                            sha.TransformFinalBlock(new byte[0], 0, 0);
                            hash = sha.Hash;
                        }
                    }
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
        private static bool IsMicrosoftSigned(string path, Action<string> log = null)
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
            Process proc;
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
                psi.EnvironmentVariables["OBSREPLAYKIT_REDIST_PATH"] = path;
                proc = Process.Start(psi);
                stdout = proc.StandardOutput.ReadToEnd();
                stderr = proc.StandardError.ReadToEnd();
                if (!proc.WaitForExit(30000))
                {
                    try { proc.Kill(); } catch (InvalidOperationException) { }
                    log?.Invoke("VC++ redist signature check failed to run: timed out");
                    return false;
                }
            }
            catch (Exception exc) when (exc is System.ComponentModel.Win32Exception || exc is InvalidOperationException)
            {
                log?.Invoke("VC++ redist signature check failed to run: " + exc.Message);
                return false;
            }
            if (proc.ExitCode != 0)
            {
                log?.Invoke("VC++ redist signature check failed: " + Truncate(stderr.Trim(), 160));
                return false;
            }

            JObject info;
            try { info = JObject.Parse(stdout); }
            catch (Newtonsoft.Json.JsonException)
            {
                log?.Invoke("VC++ redist signature check returned invalid data");
                return false;
            }
            string status = info.Value<string>("Status") ?? "";
            string subject = info.Value<string>("Subject") ?? "";
            string issuer = info.Value<string>("Issuer") ?? "";
            bool ok = status == "Valid" && subject.Contains("Microsoft Corporation") && issuer.Contains("Microsoft");
            if (!ok) log?.Invoke($"VC++ redist signature rejected: status={status} subject={Truncate(subject, 80)}");
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

            Process proc;
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
                proc = Process.Start(psi);
                proc.StandardOutput.ReadToEndAsync();
                stderr = proc.StandardError.ReadToEnd();
                if (!proc.WaitForExit(300000))
                {
                    try { proc.Kill(); } catch (InvalidOperationException) { }
                    log?.Invoke("VC++ redist install timed out after 300s");
                    return false;
                }
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
            return File.Exists(PluginDll) && File.Exists(PluginSdl2) ? "installed at " + PluginDll : null;
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

            log?.Invoke($"extracting {Path.GetFileName(Config.INPUT_OVERLAY_PLUGIN_ZIP)} -> {Config.PROGRAMFILES_OBS_DIR}");

            try
            {
                using (var zf = ZipFile.OpenRead(Config.INPUT_OVERLAY_PLUGIN_ZIP))
                {
                    foreach (var entry in zf.Entries)
                    {
                        string name = entry.FullName.Replace("\\", "/");
                        if (!name.StartsWith(PluginZipRoot)) continue;
                        string relText = name.Substring(PluginZipRoot.Length).Trim('/');
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
                // sdl2 might be elsewhere on path, so warn but dont fail the install.
                log?.Invoke("warn: SDL2.dll missing at " + PluginSdl2 + " - plugin may not load");
            }

            log?.Invoke("installed -> " + PluginDll);
            log?.Invoke($"           + {Path.GetFileName(PluginSdl2)} ({(File.Exists(PluginSdl2) ? "ok" : "MISSING!")})");
            log?.Invoke("    OBS will load it on next launch (Tools menu)");
            return true;
        }

        // extract input-overlay-presets.zip directly under INPUT_OVERLAY_TARGET (no double-nested folder). zip has a top-level input-overlay-presets/ that lines up with the target name, so extracting into target.parent is what makes the files land at the right path.
        public static bool InstallInputOverlayPresets(Action<string> log = null)
        {
            if (!File.Exists(Config.INPUT_OVERLAY_ZIP))
            {
                log?.Invoke($"(no {Path.GetFileName(Config.INPUT_OVERLAY_ZIP)} bundled, skipping)");
                return false;
            }

            string target = Config.INPUT_OVERLAY_TARGET;
            Directory.CreateDirectory(target);

            string wasdSample = Path.Combine(target, "wasd", "wasd.png");
            if (File.Exists(wasdSample))
            {
                if (!RepairMouseOverlayLayout(target, log)) return false;
                log?.Invoke("already extracted -> " + target);
                return true;
            }

            long sizeMb = new FileInfo(Config.INPUT_OVERLAY_ZIP).Length / (1024 * 1024);
            log?.Invoke($"extracting {Path.GetFileName(Config.INPUT_OVERLAY_ZIP)} ({sizeMb} MB) -> {target}");

            // extract into the parent so the zips top-level input-overlay-presets/ folder lands as target. extracting into target itself would double-nest.
            try
            {
                ZipFile.ExtractToDirectory(Config.INPUT_OVERLAY_ZIP, Path.GetDirectoryName(target.TrimEnd('\\', '/')));
            }
            catch (Exception exc)
            {
                log?.Invoke("warn: failed to extract presets: " + exc.Message);
                return false;
            }

            log?.Invoke("extracted -> " + target);
            return RepairMouseOverlayLayout(target, log);
        }

        // ensure the mouse preset has explicit source dimensions so obs does not render it as a canvas-sized source.
        private static bool RepairMouseOverlayLayout(string target, Action<string> log = null)
        {
            string layout = Path.Combine(target, MouseLayoutRel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(layout))
            {
                log?.Invoke("warn: missing input-overlay mouse layout: " + layout);
                return false;
            }

            JObject data;
            try
            {
                data = JObject.Parse(ReadUtf8SigText(layout));
            }
            catch (Exception exc) when (exc is IOException || exc is Newtonsoft.Json.JsonException)
            {
                log?.Invoke("warn: invalid input-overlay mouse layout: " + exc.Message);
                return false;
            }

            if (data.Value<int?>("default_width") == MouseLayoutWidth && data.Value<int?>("default_height") == MouseLayoutHeight)
            {
                return true;
            }

            data["default_width"] = MouseLayoutWidth;
            data["default_height"] = MouseLayoutHeight;
            try
            {
                File.WriteAllText(layout, data.ToString(Newtonsoft.Json.Formatting.Indented), new System.Text.UTF8Encoding(false));
            }
            catch (Exception exc) when (exc is IOException || exc is UnauthorizedAccessException)
            {
                log?.Invoke("warn: failed to repair input-overlay mouse layout: " + exc.Message);
                return false;
            }

            log?.Invoke("repaired input-overlay mouse layout dimensions -> " + layout);
            return true;
        }

        private static string ReadUtf8SigText(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            var enc = new System.Text.UTF8Encoding(false);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return enc.GetString(bytes, 3, bytes.Length - 3);
            }
            return enc.GetString(bytes);
        }
    }
}

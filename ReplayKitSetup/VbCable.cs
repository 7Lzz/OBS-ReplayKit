using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ReplayKitSetup
{
    // install and rename the virtual audio device used for obs stream audio. ported from obs_replaykit/vbcable.py. the com interop the python version ran through a powershell Add-Type script now lives natively in AudioHelper.cs -- straight in-process calls instead of shelling out.
    public static class VbCable
    {
        // post-install rename table -- the render endpoint is what obs monitors to, the loopback endpoint is what discord captures when the user selects it.
        private static readonly (string Orig, string New)[] EndpointRenames =
        {
            ("CABLE Input", "OBS Stream Audio"),
            ("CABLE In 16ch", "OBS Stream Audio (Surround)"),
            ("CABLE Output", "OBS Stream Audio Loopback"),
            ("CABLE Out 16ch", "OBS Stream Audio Loopback (Surround)"),
        };

        private static readonly string[] DiscordProcessNames =
        {
            "Discord.exe", "DiscordCanary.exe", "DiscordPTB.exe", "DiscordDevelopment.exe", "DiscordSystemHelper.exe",
        };
        private static readonly HashSet<string> DiscordRestartProcessNames = new HashSet<string>
        {
            "Discord.exe", "DiscordCanary.exe", "DiscordPTB.exe", "DiscordDevelopment.exe",
        };

        // guid-form device id of the current defualt playback (render) endpoint, or null. was a [AudioHelper]::GetDefaultRender() call shelled out to a fresh powershell process; AudioHelper is now ReplayKitSetup/AudioHelper.cs, a straight in-process call.
        private static string GetDefaultPlayback(Action<string> log = null)
        {
            try { return AudioHelper.GetDefaultRender(); }
            catch (COMException ex) { log?.Invoke("warn: could not query default playback: " + ex.Message); return null; }
        }

        // guid-form device id of the current defualt capture (mic) endpoint, or null.
        private static string GetDefaultCapture(Action<string> log = null)
        {
            try { return AudioHelper.GetDefaultCapture(); }
            catch (COMException ex) { log?.Invoke("warn: could not query default recording: " + ex.Message); return null; }
        }

        private static bool SetDefaultPlayback(string deviceId, Action<string> log = null)
        {
            try { return AudioHelper.SetDefaultRender(deviceId) == 0; }
            catch (COMException ex) { log?.Invoke("warn: could not set default playback: " + ex.Message); return false; }
        }

        private static bool SetDefaultCapture(string deviceId, Action<string> log = null)
        {
            try { return AudioHelper.SetDefaultCapture(deviceId) == 0; }
            catch (COMException ex) { log?.Invoke("warn: could not set default recording: " + ex.Message); return false; }
        }

        // matches a leading index prefix some endpoint friendly names carry (e.g. "2 - Speakers" -> "Speakers") before matching against the rename table.
        private static readonly Regex LeadingIndexPrefix = new Regex(@"^\s*\d+\s*-\s*");

        // the FriendlyName property under an endpoint's Properties key, registry-value-name-encoded as "{fmtid},pid" -- same PKEY the old powershell Get-ItemProperty call read.
        private const string FriendlyNameValue = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";
        private const int HiddenStateFlag = 0x10000000;

        private static void TryAudioCall(Action action, string what, Action<string> log)
        {
            try { action(); }
            catch (COMException ex) { log?.Invoke($"warn: {what} failed: {ex.Message}"); }
        }

        // rename the installed audio endpoints to obs stream audio names -- native registry walk over the same MMDevices keys the old powershell script read, calling straight into AudioHelper instead of Add-Type-compiling it per invocation.
        private static void RenameEndpoints(Action<string> log = null)
        {
            var renameLookup = EndpointRenames.ToDictionary(r => r.Orig, r => r.New);
            int successes = 0, hidden = 0;

            foreach (int dataFlow in new[] { 0, 1 })
            {
                string root = dataFlow == 0
                    ? @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render"
                    : @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture";
                using (var rootKey = Registry.LocalMachine.OpenSubKey(root))
                {
                    if (rootKey == null) continue;
                    foreach (string guid in rootKey.GetSubKeyNames())
                    {
                        using (var endpointKey = rootKey.OpenSubKey(guid))
                        using (var propsKey = rootKey.OpenSubKey(guid + @"\Properties"))
                        {
                            if (propsKey == null) continue;
                            object curValue = propsKey.GetValue(FriendlyNameValue);
                            if (curValue == null) continue;
                            string cur = curValue.ToString();
                            if (string.IsNullOrEmpty(cur)) continue;

                            object stateValue = endpointKey?.GetValue("DeviceState");
                            int state = stateValue != null ? Convert.ToInt32(stateValue) : 0;

                            string canonical = LeadingIndexPrefix.Replace(cur, "").Trim();
                            string lower = canonical.ToLowerInvariant();
                            bool hideEndpoint = lower.StartsWith("cable in 16ch") ||
                                lower.StartsWith("cable out 16ch") ||
                                lower == "obs stream audio (surround)" ||
                                lower == "obs stream audio loopback (surround)";

                            string newName = null;
                            if (renameLookup.TryGetValue(canonical, out string mapped)) newName = mapped;
                            else if (lower.StartsWith("cable input")) newName = "OBS Stream Audio";
                            else if (lower.StartsWith("cable in 16ch")) newName = "OBS Stream Audio (Surround)";
                            else if (lower.StartsWith("cable output")) newName = "OBS Stream Audio Loopback";
                            else if (lower.StartsWith("cable out 16ch")) newName = "OBS Stream Audio Loopback (Surround)";

                            if (newName == null && !hideEndpoint) continue;

                            string deviceId = $"{{0.0.{dataFlow}.00000000}}.{guid}";
                            if (newName != null)
                            {
                                int rc = -1;
                                TryAudioCall(() => rc = AudioHelper.RenameEndpoint(deviceId, newName), "rename " + cur, log);
                                if (rc == 0) { successes++; log?.Invoke($"renamed: {cur} -> {newName}"); }
                                else log?.Invoke($"rename FAILED for {cur}: rc=0x{rc:x}");
                            }
                            if (hideEndpoint && (state & HiddenStateFlag) == 0)
                            {
                                string label = newName ?? cur;
                                int vrc = -1;
                                TryAudioCall(() => vrc = AudioHelper.SetEndpointVisible(deviceId, false), "hide " + label, log);
                                if (vrc == 0) { hidden++; log?.Invoke($"hidden: {label}"); }
                                else log?.Invoke($"hide FAILED for {label}: rc=0x{vrc:x}");
                            }
                        }
                    }
                }
            }

            if (successes + hidden > 0)
            {
                BounceAudioServices(log);
                log?.Invoke($"renames={successes} hidden={hidden}");
            }
            else
            {
                log?.Invoke("renames=0 hidden=0 audio-stack=unchanged");
            }
        }

        // native equivalent of the old Stop-Service AudioEndpointBuilder -Force / Start-Service pair -- -Force stops dependent services too, which ServiceController does not do automatically, so DependentServices is walked by hand.
        private static void BounceAudioServices(Action<string> log = null)
        {
            try
            {
                using (var aeb = new ServiceController("AudioEndpointBuilder"))
                {
                    foreach (var dep in aeb.DependentServices)
                    {
                        dep.Refresh();
                        if (dep.Status == ServiceControllerStatus.Running)
                        {
                            try { dep.Stop(); dep.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(5)); }
                            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ServiceProcess.TimeoutException) { }
                        }
                    }
                    aeb.Refresh();
                    if (aeb.Status != ServiceControllerStatus.Stopped)
                    {
                        try { aeb.Stop(); aeb.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(5)); }
                        catch (Exception ex) when (ex is InvalidOperationException || ex is System.ServiceProcess.TimeoutException) { }
                    }
                    aeb.Refresh();
                    if (aeb.Status != ServiceControllerStatus.Running)
                    {
                        try { aeb.Start(); aeb.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(5)); }
                        catch (Exception ex) when (ex is InvalidOperationException || ex is System.ServiceProcess.TimeoutException) { }
                    }
                }
                using (var audiosrv = new ServiceController("Audiosrv"))
                {
                    audiosrv.Refresh();
                    if (audiosrv.Status != ServiceControllerStatus.Running)
                    {
                        try { audiosrv.Start(); audiosrv.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(5)); }
                        catch (Exception ex) when (ex is InvalidOperationException || ex is System.ServiceProcess.TimeoutException) { }
                    }
                }
                System.Threading.Thread.Sleep(500);
                string aebStatus = "unknown", asrStatus = "unknown";
                try { using (var s = new ServiceController("AudioEndpointBuilder")) { s.Refresh(); aebStatus = s.Status.ToString(); } } catch (InvalidOperationException) { }
                try { using (var s = new ServiceController("Audiosrv")) { s.Refresh(); asrStatus = s.Status.ToString(); } } catch (InvalidOperationException) { }
                log?.Invoke($"audio-stack=AEB:{aebStatus} Audiosrv:{asrStatus}");
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
            {
                log?.Invoke("warn: could not bounce audio services: " + ex.Message);
            }
        }

        // true iff the bundled virtual audio driver is registered.
        public static bool IsVbcableInstalled()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_SoundDevice WHERE Name LIKE '%VB-Audio Virtual Cable%'"))
                using (var results = searcher.Get())
                {
                    return results.Count > 0;
                }
            }
            catch (ManagementException)
            {
                return false;
            }
        }

        // native replacement for the old Get-CimInstance Win32_Process discord enumeration.
        private static List<(int Pid, string Name, string ExecutablePath)> QueryDiscordProcesses()
        {
            var results = new List<(int, string, string)>();
            string where = string.Join(" OR ", DiscordProcessNames.Select(n => $"Name='{n.Replace("'", "''")}'"));
            try
            {
                using (var searcher = new ManagementObjectSearcher($"SELECT ProcessId, Name, ExecutablePath FROM Win32_Process WHERE {where}"))
                using (var rows = searcher.Get())
                {
                    foreach (ManagementObject row in rows)
                    {
                        int pid = Convert.ToInt32(row["ProcessId"]);
                        string name = row["Name"] as string ?? "";
                        string exePath = row["ExecutablePath"] as string ?? "";
                        results.Add((pid, name, exePath));
                    }
                }
            }
            catch (ManagementException) { }
            return results;
        }

        // unpack the bundled audio-driver installer into a temp directory.
        private static string ExtractDriverPack(Action<string> log = null)
        {
            if (!File.Exists(Config.INPUT_OVERLAY_INSTALLERS_ZIP))
            {
                log?.Invoke($"(no {Path.GetFileName(Config.INPUT_OVERLAY_INSTALLERS_ZIP)} bundled)");
                return null;
            }
            string tmpdir = Path.Combine(Path.GetTempPath(), "obsreplaykit_vbcable_" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var outer = ZipFile.OpenRead(Config.INPUT_OVERLAY_INSTALLERS_ZIP))
                {
                    var packEntry = outer.Entries.FirstOrDefault(e => e.FullName == Config.VBCABLE_DRIVER_PACK_NAME);
                    if (packEntry == null)
                    {
                        log?.Invoke("(OBS Stream Audio installer not in installers.zip - skipping)");
                        return null;
                    }
                    Directory.CreateDirectory(tmpdir);
                    string innerZipPath = Path.Combine(Path.GetTempPath(), "obsreplaykit_vbcable_inner_" + Guid.NewGuid().ToString("N") + ".zip");
                    try
                    {
                        packEntry.ExtractToFile(innerZipPath);
                        ZipFile.ExtractToDirectory(innerZipPath, tmpdir);
                    }
                    finally
                    {
                        if (File.Exists(innerZipPath)) { try { File.Delete(innerZipPath); } catch (IOException) { } }
                    }
                }
            }
            catch (Exception exc)
            {
                log?.Invoke("failed to extract OBS Stream Audio installer: " + exc.Message);
                return null;
            }

            string setup = Path.Combine(tmpdir, Config.VBCABLE_SETUP_EXE_NAME);
            if (!File.Exists(setup))
            {
                log?.Invoke($"warn: {Config.VBCABLE_SETUP_EXE_NAME} not found after extraction");
                try { Directory.Delete(tmpdir, true); } catch (IOException) { }
                return null;
            }
            return tmpdir;
        }

        // install the obs stream audio device and restore the users audio defaults.
        public static bool InstallVbcable(Action<string> log = null)
        {
            if (IsVbcableInstalled())
            {
                log?.Invoke("OBS Stream Audio device already installed");
                return true;
            }

            string packDir = ExtractDriverPack(log);
            if (packDir == null) return false;

            string savedRender = GetDefaultPlayback(log);
            string savedCapture = GetDefaultCapture(log);
            var voicemeeterSnapshot = VoiceMeeter.SnapshotVoicemeeterInputs(log);
            if (savedRender != null) log?.Invoke("saved current default playback:  " + savedRender);
            else log?.Invoke("warn: couldn't query current default playback - cannot restore after install");
            if (savedCapture != null) log?.Invoke("saved current default recording: " + savedCapture);
            else log?.Invoke("warn: couldn't query current default recording - cannot restore after install");

            log?.Invoke("running OBS Stream Audio installer...");
            log?.Invoke("    Windows Security may prompt for an audio driver - click Install");

            string setup = Path.Combine(packDir, Config.VBCABLE_SETUP_EXE_NAME);
            Process proc;
            string stderr;
            try
            {
                var psi = new ProcessStartInfo(setup, Win32Args.Build("-i", "-h"))
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
                    log?.Invoke("OBS Stream Audio installer timed out after 300s");
                    try { Directory.Delete(packDir, true); } catch (IOException) { }
                    return false;
                }
            }
            catch (Exception exc) when (exc is System.ComponentModel.Win32Exception || exc is InvalidOperationException)
            {
                log?.Invoke("OBS Stream Audio installer launch failed: " + exc.Message);
                try { Directory.Delete(packDir, true); } catch (IOException) { }
                return false;
            }
            finally
            {
                try { Directory.Delete(packDir, true); } catch (IOException) { }
            }

            if (proc.ExitCode != 0)
            {
                log?.Invoke("OBS Stream Audio installer exited with code " + proc.ExitCode);
                if (!string.IsNullOrWhiteSpace(stderr)) log?.Invoke("stderr: " + stderr.Trim().Split('\n')[0]);
                return false;
            }

            if (!IsVbcableInstalled())
            {
                log?.Invoke("OBS Stream Audio installer ran but the driver was not detected");
                return false;
            }

            // rename before restoring defaults: both bounce audiosrv, so pay the audio-interrupt cost once.
            RenameEndpoints(log);

            if (savedRender != null)
            {
                if (SetDefaultPlayback(savedRender, log)) log?.Invoke("restored default playback  -> " + savedRender);
                else log?.Invoke("warn: failed to restore default playback - fix manually in Sound settings");
            }
            if (savedCapture != null)
            {
                if (SetDefaultCapture(savedCapture, log)) log?.Invoke("restored default recording -> " + savedCapture);
                else log?.Invoke("warn: failed to restore default recording - fix manually in Sound settings");
            }

            VoiceMeeter.RestoreVoicemeeterInputs(voicemeeterSnapshot, log);

            log?.Invoke("OBS Stream Audio device installed");
            return true;
        }

        // install obs stream audio if it is missing.
        public static bool EnsureVbcable(Action<string> log = null)
        {
            if (IsVbcableInstalled())
            {
                log?.Invoke("OBS Stream Audio device already installed");
                string savedRender = GetDefaultPlayback(log);
                string savedCapture = GetDefaultCapture(log);
                var voicemeeterSnapshot = VoiceMeeter.SnapshotVoicemeeterInputs(log);
                RenameEndpoints(log);
                if (savedRender != null)
                {
                    if (SetDefaultPlayback(savedRender, log)) log?.Invoke("restored default playback  -> " + savedRender);
                    else log?.Invoke("warn: failed to restore default playback - fix manually in Sound settings");
                }
                if (savedCapture != null)
                {
                    if (SetDefaultCapture(savedCapture, log)) log?.Invoke("restored default recording -> " + savedCapture);
                    else log?.Invoke("warn: failed to restore default recording - fix manually in Sound settings");
                }
                VoiceMeeter.RestoreVoicemeeterInputs(voicemeeterSnapshot, log);
                return true;
            }
            return InstallVbcable(log);
        }

        // stop discord variants that commonly hold the replaykit cable open.
        private static (bool Ok, List<string> RestartPaths) StopDiscordForDriverUninstall(Action<string> log = null)
        {
            var procs = QueryDiscordProcesses();
            var restartPaths = procs
                .Where(p => DiscordRestartProcessNames.Contains(p.Name) && !string.IsNullOrWhiteSpace(p.ExecutablePath))
                .Select(p => p.ExecutablePath)
                .Distinct()
                .ToList();

            int stopped = 0;
            foreach (var p in procs)
            {
                try
                {
                    using (var proc = Process.GetProcessById(p.Pid))
                    {
                        proc.Kill();
                        proc.WaitForExit(2000);
                        stopped++;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
                {
                    // already exited, or couldnt be killed -- the remaining-count check below is what actually gates success.
                }
            }
            if (stopped > 0) System.Threading.Thread.Sleep(1200);

            var remaining = QueryDiscordProcesses();
            if (stopped > 0) log?.Invoke($"stopped Discord audio client(s) before driver removal ({stopped})");
            if (remaining.Count > 0)
            {
                log?.Invoke($"OBS Stream Audio cleanup blocked by Discord: {remaining.Count} Discord process(es) still running");
                return (false, restartPaths);
            }
            return (true, restartPaths);
        }

        private static void RestartDiscordApps(List<string> paths, Action<string> log = null)
        {
            foreach (var exe in paths.Distinct())
            {
                if (!File.Exists(exe)) continue;
                try
                {
                    var psi = new ProcessStartInfo(exe)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = Path.GetDirectoryName(exe),
                    };
                    Process.Start(psi);
                    log?.Invoke("restarted Discord -> " + exe);
                }
                catch (Exception exc) when (exc is System.ComponentModel.Win32Exception || exc is InvalidOperationException)
                {
                    log?.Invoke("warn: could not restart Discord at " + exe + ": " + exc.Message);
                }
            }
        }

        // uninstall the obs stream audio virtual audio driver.
        public static bool UninstallVbcable(Action<string> log = null)
        {
            if (!IsVbcableInstalled()) return true;

            var (okToUninstall, restartApps) = StopDiscordForDriverUninstall(log);
            if (!okToUninstall)
            {
                RestartDiscordApps(restartApps, log);
                return false;
            }

            string packDir = ExtractDriverPack(log);
            if (packDir == null)
            {
                RestartDiscordApps(restartApps, log);
                return false;
            }

            string setup = Path.Combine(packDir, Config.VBCABLE_SETUP_EXE_NAME);
            Process proc;
            try
            {
                var psi = new ProcessStartInfo(setup, Win32Args.Build("-u", "-h"))
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                proc = Process.Start(psi);
                proc.StandardOutput.ReadToEndAsync();
                proc.StandardError.ReadToEndAsync();
                if (!proc.WaitForExit(300000))
                {
                    try { proc.Kill(); } catch (InvalidOperationException) { }
                    log?.Invoke("OBS Stream Audio uninstaller timed out after 300s");
                    try { Directory.Delete(packDir, true); } catch (IOException) { }
                    RestartDiscordApps(restartApps, log);
                    return false;
                }
            }
            catch (Exception exc) when (exc is System.ComponentModel.Win32Exception || exc is InvalidOperationException)
            {
                log?.Invoke("OBS Stream Audio uninstaller launch failed: " + exc.Message);
                try { Directory.Delete(packDir, true); } catch (IOException) { }
                RestartDiscordApps(restartApps, log);
                return false;
            }
            finally
            {
                try { Directory.Delete(packDir, true); } catch (IOException) { }
                RestartDiscordApps(restartApps, log);
            }

            if (proc.ExitCode != 0)
            {
                log?.Invoke("OBS Stream Audio uninstaller exited with code " + proc.ExitCode);
                return false;
            }

            return !IsVbcableInstalled();
        }
    }
}

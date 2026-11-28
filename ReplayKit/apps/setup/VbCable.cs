using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using ReplayKitShared;

namespace ReplayKitSetup
{
    // install and rename the virtual audio device used for obs stream audio; the audio endpoint com interop lives in AudioHelper.cs
    public static class VbCable
    {
        // post-install rename table -- the render endpoint is what obs monitors to, the loopback endpoint is what discord captures when the user selects it.
        private static readonly (string Orig, string New)[] EndpointRenames =
        {
            (AudioEndpoints.CableRenderName, AudioEndpoints.ShareCableName),
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

        // guid-form device id of the current defualt playback (render) endpoint, or null.
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

        private const string FriendlyNameValue = AudioEndpoints.FriendlyNameValue;
        private const int HiddenStateFlag = 0x10000000;

        private static void TryAudioCall(Action action, string what, Action<string> log)
        {
            try { action(); }
            catch (COMException ex) { log?.Invoke($"warn: {what} failed: {ex.Message}"); }
        }

        // rename the installed audio endpoints to obs stream audio names and hide the surround ones
        private static void RenameEndpoints(Action<string> log = null)
        {
            var renameLookup = EndpointRenames.ToDictionary(r => r.Orig, r => r.New);
            int successes = 0, hidden = 0;

            foreach (int dataFlow in new[] { 0, 1 })
            {
                string root = dataFlow == 0 ? AudioEndpoints.RenderKey : AudioEndpoints.CaptureKey;
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

        // a cable that was already on the machine belongs to the user (streamers and voicemeeter setups often have one): replaykit borrows it for share preview, gives its endpoints their vb-audio names back on removal and never runs the uninstaller on it, so only a cable replaykit installed carries this mark
        private const string OwnerKey = @"SOFTWARE\OBS ReplayKit";
        private const string OwnerValue = "InstalledVbCable";

        private static bool InstalledByReplayKit()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(OwnerKey);
                return key?.GetValue(OwnerValue) is int marked && marked == 1;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException) { return false; }
        }

        private static void MarkInstalledByReplayKit(bool installed, Action<string> log)
        {
            try
            {
                if (installed)
                {
                    using var key = Registry.LocalMachine.CreateSubKey(OwnerKey);
                    key.SetValue(OwnerValue, 1, RegistryValueKind.DWord);
                }
                else
                {
                    using var key = Registry.LocalMachine.OpenSubKey(OwnerKey, true);
                    key?.DeleteValue(OwnerValue, false);
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException)
            {
                log?.Invoke("warn: could not record who installed OBS Stream Audio: " + ex.Message);
            }
        }

        // undoes RenameEndpoints on a cable of the user: the vb-audio names come back and the surround endpoints show again
        private static void RestoreEndpointNames(Action<string> log = null)
        {
            var restoreLookup = EndpointRenames.ToDictionary(r => r.New, r => r.Orig, StringComparer.OrdinalIgnoreCase);
            int changes = 0;
            foreach (int dataFlow in new[] { 0, 1 })
            {
                string root = dataFlow == 0 ? AudioEndpoints.RenderKey : AudioEndpoints.CaptureKey;
                using (var rootKey = Registry.LocalMachine.OpenSubKey(root))
                {
                    if (rootKey == null) continue;
                    foreach (string guid in rootKey.GetSubKeyNames())
                    {
                        using (var endpointKey = rootKey.OpenSubKey(guid))
                        using (var propsKey = rootKey.OpenSubKey(guid + @"\Properties"))
                        {
                            string cur = propsKey?.GetValue(FriendlyNameValue)?.ToString();
                            if (string.IsNullOrEmpty(cur)) continue;
                            string canonical = LeadingIndexPrefix.Replace(cur, "").Trim();
                            if (!restoreLookup.TryGetValue(canonical, out string original)) continue;
                            string deviceId = $"{{0.0.{dataFlow}.00000000}}.{guid}";
                            int rc = -1;
                            TryAudioCall(() => rc = AudioHelper.RenameEndpoint(deviceId, original), "rename " + cur, log);
                            if (rc == 0) { changes++; log?.Invoke($"renamed back: {cur} -> {original}"); }
                            else log?.Invoke($"rename back FAILED for {cur}: rc=0x{rc:x}");
                            object stateValue = endpointKey?.GetValue("DeviceState");
                            int state = stateValue != null ? Convert.ToInt32(stateValue) : 0;
                            if ((state & HiddenStateFlag) != 0)
                            {
                                int vrc = -1;
                                TryAudioCall(() => vrc = AudioHelper.SetEndpointVisible(deviceId, true), "show " + original, log);
                                if (vrc == 0) { changes++; log?.Invoke($"shown again: {original}"); }
                            }
                        }
                    }
                }
            }
            if (changes > 0) BounceAudioServices(log);
        }

        // restarts AudioEndpointBuilder with the services that depend on it, so windows reads the new endpoint names; ServiceController does not stop dependents itself, so DependentServices is walked by hand.
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

        public static bool IsVbcableInstalled() => ReplayKitShared.VbCableDriver.IsInstalled();

        // true when the cable is installed AND its render endpoint already carries the "OBS Stream Audio" name -- i.e. EnsureVbcable has nothing to do, so the setup step can skip it (no installer, no audiosrv bounce).
        public static bool IsShareAudioReady()
        {
            if (!IsVbcableInstalled()) return false;
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(AudioEndpoints.RenderKey);
                if (root == null) return false;
                foreach (string guid in root.GetSubKeyNames())
                {
                    using var props = root.OpenSubKey(guid + @"\Properties");
                    string name = props?.GetValue(FriendlyNameValue)?.ToString();
                    if (!string.IsNullOrEmpty(name) && name.IndexOf("OBS Stream Audio", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException) { }
            return false;
        }

        // running discord processes as (pid, "Name.exe", exe path); Name keeps the .exe so DiscordRestartProcessNames matches.
        private static List<(int Pid, string Name, string ExecutablePath)> QueryDiscordProcesses()
        {
            var results = new List<(int, string, string)>();
            foreach (string exeName in DiscordProcessNames)
            {
                string bare = Path.GetFileNameWithoutExtension(exeName);
                Process[] procs;
                try { procs = Process.GetProcessesByName(bare); }
                catch (InvalidOperationException) { continue; }
                foreach (var p in procs)
                {
                    string path = "";
                    try { path = p.MainModule?.FileName ?? ""; }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException || ex is NotSupportedException) { }
                    try { results.Add((p.Id, exeName, path)); }
                    catch (InvalidOperationException) { }
                    finally { p.Dispose(); }
                }
            }
            return results;
        }

        // unpack the bundled audio-driver installer into a temp directory.
        private static string ExtractDriverPack(Action<string> log = null)
        {
            if (!File.Exists(Config.INSTALLERS_ZIP))
            {
                log?.Invoke($"(no {Path.GetFileName(Config.INSTALLERS_ZIP)} bundled)");
                return null;
            }
            string tmpdir = Path.Combine(Path.GetTempPath(), "obsreplaykit_vbcable_" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var outer = ZipFile.OpenRead(Config.INSTALLERS_ZIP))
                {
                    var packEntry = outer.Entries.FirstOrDefault(e => e.FullName == Config.VBCABLE_DRIVER_PACK_NAME);
                    if (packEntry == null)
                    {
                        log?.Invoke("(OBS Stream Audio installer not in " + Path.GetFileName(Config.INSTALLERS_ZIP) + " - skipping)");
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

            // a mark left from a cable that has since been removed by hand must not make a later cable of the user look like ours
            MarkInstalledByReplayKit(false, log);
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
            using var proc = new Process();
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
                proc.StartInfo = psi;
                proc.Start();
                proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();
                if (!proc.WaitForExit(300000))
                {
                    try { proc.Kill(); } catch (InvalidOperationException) { }
                    log?.Invoke("OBS Stream Audio installer timed out after 300s");
                    return false;
                }
                stderr = stderrTask.GetAwaiter().GetResult();
            }
            catch (Exception exc) when (exc is System.ComponentModel.Win32Exception || exc is InvalidOperationException)
            {
                log?.Invoke("OBS Stream Audio installer launch failed: " + exc.Message);
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
            MarkInstalledByReplayKit(true, log);

            // rename before restoring defaults: both bounce audiosrv, so pay the audio-interrupt cost once.
            RenameEndpoints(log);

            RestoreAudioDefaults(savedRender, savedCapture, voicemeeterSnapshot, log);

            log?.Invoke("OBS Stream Audio device installed");
            return true;
        }

        // the rename and the driver install both bounce the audio stack, which can move the windows defaults and the voicemeeter inputs, so what was saved before is put back
        private static void RestoreAudioDefaults(string savedRender, string savedCapture, VoicemeeterSnapshot voicemeeterSnapshot, Action<string> log)
        {
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
                RestoreAudioDefaults(savedRender, savedCapture, voicemeeterSnapshot, log);
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

        // explorer starts each one from a shortcut at the level of the signed in user, so discord does not come back with the administrator token of this setup; one shortcut per app, since explorer reads it after this returns
        private static void RestartDiscordApps(List<string> paths, Action<string> log = null)
        {
            int index = 0;
            foreach (var exe in paths.Distinct())
            {
                if (!File.Exists(exe)) continue;
                string link = Path.Combine(ReplayKitShared.TempLayout.Scratch, "discord-restart-" + index++ + ".lnk");
                if (ReplayKitShared.ShellLaunch.Start(exe, "", link, log)) log?.Invoke("restarted Discord -> " + exe);
                else log?.Invoke("warn: could not restart Discord at " + exe);
            }
        }

        // uninstall the obs stream audio virtual audio driver.
        public static bool UninstallVbcable(Action<string> log = null)
        {
            if (!IsVbcableInstalled())
            {
                MarkInstalledByReplayKit(false, log);
                return true;
            }
            if (!InstalledByReplayKit())
            {
                log?.Invoke("VB-Audio Virtual Cable was not installed by ReplayKit -- leaving the driver in place and giving its endpoints their names back");
                RestoreEndpointNames(log);
                return true;
            }

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
            using var proc = new Process();
            try
            {
                var psi = new ProcessStartInfo(setup, Win32Args.Build("-u", "-h"))
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                proc.StartInfo = psi;
                proc.Start();
                proc.StandardOutput.ReadToEndAsync();
                proc.StandardError.ReadToEndAsync();
                if (!proc.WaitForExit(300000))
                {
                    try { proc.Kill(); } catch (InvalidOperationException) { }
                    log?.Invoke("OBS Stream Audio uninstaller timed out after 300s");
                    return false;
                }
            }
            catch (Exception exc) when (exc is System.ComponentModel.Win32Exception || exc is InvalidOperationException)
            {
                log?.Invoke("OBS Stream Audio uninstaller launch failed: " + exc.Message);
                return false;
            }
            // the one place the pack goes and discord comes back, however the uninstaller ended
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

            if (IsVbcableInstalled()) return false;
            MarkInstalledByReplayKit(false, log);
            return true;
        }
    }
}

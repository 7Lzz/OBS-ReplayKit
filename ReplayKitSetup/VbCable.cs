using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace ReplayKitSetup
{
    // install and rename the virtual audio device used for obs stream audio. ported from obs_replaykit/vbcable.py. keeps the same mechanism the python version used -- a powershell script that Add-Type-compiles an inline c# com-interop helper -- rather than inlining the com interfaces natively in this project; that indirection existed to avoid ~150 lines of ctypes glue in python, but re-deriving the registry-walk-plus-com rename logic natively here is a bigger behavioral-risk surface than just launching the exact same, already-correct script from a different parent process.
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

        // powershell helper that compiles a c# class at runtime via add-type and calls IMMDeviceEnumerator + IPolicyConfigVistaClient. one powershell process per call (~300ms), same cost the python original paid.
        private const string PsAudioHelper = @"
$ErrorActionPreference = 'Stop'
$source = @'
using System;
using System.Runtime.InteropServices;

[ComImport, Guid(""BCDE0395-E52F-467C-8E3D-C4579291692E"")]
public class MMDeviceEnumerator { }

[Guid(""A95664D2-9614-4F35-A746-DE8DB63617E6""),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDeviceEnumerator {
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr ppDevices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
}

[Guid(""D666063F-1587-4E43-81F1-B948E807363F""),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDevice {
    [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, out IntPtr ppInterface);
    [PreserveSig] int OpenPropertyStore(int stgmAccess, out IntPtr ppProperties);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
    [PreserveSig] int GetState(out int pdwState);
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct PROPERTYKEY {
    public Guid fmtid;
    public uint pid;
}

[StructLayout(LayoutKind.Sequential)]
public struct PROPVARIANT {
    public ushort vt;
    public ushort r1;
    public ushort r2;
    public ushort r3;
    public IntPtr pszVal;
    public IntPtr padding;
}

[ComImport, Guid(""294935CE-F637-4E7C-A41B-AB255460B862"")]
public class _CPolicyConfigVistaClient { }

[ComImport, Guid(""870AF99C-171D-4F9E-AF0D-E63DF40C2BC9"")]
public class _CPolicyConfigClient { }

[Guid(""568B9108-44BF-40B4-9006-86AFE5B5A620""),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPolicyConfigVistaClient {
    [PreserveSig] int GetMixFormat(string a, IntPtr b);
    [PreserveSig] int GetDeviceFormat(string a, bool b, IntPtr c);
    [PreserveSig] int SetDeviceFormat(string a, IntPtr b, IntPtr c);
    [PreserveSig] int GetProcessingPeriod(string a, bool b, IntPtr c, IntPtr d);
    [PreserveSig] int SetProcessingPeriod(string a, IntPtr b);
    [PreserveSig] int GetShareMode(string a, IntPtr b);
    [PreserveSig] int SetShareMode(string a, IntPtr b);
    [PreserveSig] int GetPropertyValue(string a, bool b, ref PROPERTYKEY key, IntPtr pv);
    [PreserveSig] int SetPropertyValue(string deviceId, bool bFxStore, ref PROPERTYKEY key, ref PROPVARIANT pv);
    [PreserveSig] int SetDefaultEndpoint(string deviceId, uint role);
    [PreserveSig] int SetEndpointVisibility(string a, bool b);
}

[Guid(""F8679F50-850A-41CF-9C72-430F290290C8""),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPolicyConfigClient {
    [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string a, IntPtr b);
    [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string a, int b, IntPtr c);
    [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string a);
    [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string a, IntPtr b, IntPtr c);
    [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string a, int b, IntPtr c, IntPtr d);
    [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string a, IntPtr b);
    [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string a, IntPtr b);
    [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string a, IntPtr b);
    [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string a, ref PROPERTYKEY key, IntPtr pv);
    [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string a, ref PROPERTYKEY key, ref PROPVARIANT pv);
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
    [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
}

public static class AudioHelper {
    private static string _GetDefault(int dataFlow) {
        var enumerator = (IMMDeviceEnumerator)(new MMDeviceEnumerator());
        IMMDevice dev;
        int rc = enumerator.GetDefaultAudioEndpoint(dataFlow, 0, out dev);
        if (rc != 0 || dev == null) return null;
        string id;
        if (dev.GetId(out id) != 0) return null;
        return id;
    }
    private static int _SetDefault(string id) {
        var client = (IPolicyConfigVistaClient)(new _CPolicyConfigVistaClient());
        int rc0 = client.SetDefaultEndpoint(id, 0);
        int rc1 = client.SetDefaultEndpoint(id, 1);
        int rc2 = client.SetDefaultEndpoint(id, 2);
        return rc0 | rc1 | rc2;
    }
    public static string GetDefaultRender()      { return _GetDefault(0); }
    public static string GetDefaultCapture()     { return _GetDefault(1); }
    public static int    SetDefaultRender(string id)  { return _SetDefault(id); }
    public static int    SetDefaultCapture(string id) { return _SetDefault(id); }
    public static int    SetEndpointVisible(string id, bool visible) {
        var client = (IPolicyConfigClient)(new _CPolicyConfigClient());
        return client.SetEndpointVisibility(id, visible ? 1 : 0);
    }
    public static int RenameEndpoint(string deviceId, string newName) {
        var client = (IPolicyConfigVistaClient)(new _CPolicyConfigVistaClient());
        var key = new PROPERTYKEY {
            fmtid = new Guid(""a45c254e-df1c-4efd-8020-67d146a850e0""),
            pid = 2
        };
        IntPtr strPtr = Marshal.StringToCoTaskMemUni(newName);
        var pv = new PROPVARIANT { vt = 31, pszVal = strPtr };
        try {
            return client.SetPropertyValue(deviceId, false, ref key, ref pv);
        } finally {
            Marshal.FreeCoTaskMem(strPtr);
        }
    }
}
'@
Add-Type -TypeDefinition $source -Language CSharp | Out-Null
";

        // invoke powershell with the audio-helper c# class defined, run a one-liner that returns a string, and return the trimmed stdout.
        private static string RunPs(string commandAfterHelper, Action<string> log = null)
        {
            string script = PsAudioHelper + "\n" + commandAfterHelper;
            Process proc;
            string stdout, stderr;
            try
            {
                var psi = new ProcessStartInfo("powershell", Win32Args.Build("-NoProfile", "-NonInteractive", "-Command", script))
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                proc = Process.Start(psi);
                stdout = proc.StandardOutput.ReadToEnd();
                stderr = proc.StandardError.ReadToEnd();
                if (!proc.WaitForExit(30000))
                {
                    try { proc.Kill(); } catch (InvalidOperationException) { }
                    log?.Invoke("powershell helper failed: timed out");
                    return null;
                }
            }
            catch (Exception exc) when (exc is System.ComponentModel.Win32Exception || exc is InvalidOperationException)
            {
                log?.Invoke("powershell helper failed: " + exc.Message);
                return null;
            }
            if (proc.ExitCode != 0)
            {
                string err = (stderr ?? "").Trim();
                log?.Invoke("powershell helper exit " + proc.ExitCode + ": " + (err.Length > 0 ? err.Split('\n')[0] : ""));
                return null;
            }
            return stdout.Trim();
        }

        // guid-form device id of the current defualt playback (render) endpoint, or null.
        private static string GetDefaultPlayback(Action<string> log = null)
        {
            string outp = RunPs("[AudioHelper]::GetDefaultRender()", log);
            return string.IsNullOrEmpty(outp) ? null : outp;
        }

        // guid-form device id of the current defualt capture (mic) endpoint, or null.
        private static string GetDefaultCapture(Action<string> log = null)
        {
            string outp = RunPs("[AudioHelper]::GetDefaultCapture()", log);
            return string.IsNullOrEmpty(outp) ? null : outp;
        }

        private static bool SetDefaultPlayback(string deviceId, Action<string> log = null)
        {
            string safe = deviceId.Replace("'", "''");
            string outp = RunPs($"[AudioHelper]::SetDefaultRender('{safe}')", log);
            return outp != null && int.TryParse(outp, out int rc) && rc == 0;
        }

        private static bool SetDefaultCapture(string deviceId, Action<string> log = null)
        {
            string safe = deviceId.Replace("'", "''");
            string outp = RunPs($"[AudioHelper]::SetDefaultCapture('{safe}')", log);
            return outp != null && int.TryParse(outp, out int rc) && rc == 0;
        }

        // rename the installed audio endpoints to obs stream audio names.
        private static void RenameEndpoints(Action<string> log = null)
        {
            string psPairs = string.Join(";\n", EndpointRenames.Select(r => $"    '{r.Orig}' = '{r.New}'"));
            string script = PsAudioHelper + $@"
$renames = @{{
{psPairs}
}}

$render  = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render'
$capture = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture'
$friendlyKey = '{{a45c254e-df1c-4efd-8020-67d146a850e0}},2'
$hiddenStateFlag = 0x10000000

$successes = 0
$hidden = 0
foreach ($root in @($render, $capture)) {{
    foreach ($guid in (Get-ChildItem $root -ErrorAction SilentlyContinue).PSChildName) {{
        $endpoint = Join-Path $root $guid
        $props = Join-Path $root ""$guid\Properties""
        if (-not (Test-Path $props)) {{ continue }}
        $cur = (Get-ItemProperty -LiteralPath $props -Name $friendlyKey -ErrorAction SilentlyContinue).$friendlyKey
        if (-not $cur) {{ continue }}
        $state = (Get-ItemProperty -LiteralPath $endpoint -Name 'DeviceState' -ErrorAction SilentlyContinue).DeviceState
        if ($null -eq $state) {{ $state = 0 }}
        $newName = $null
        $canonical = ([string]$cur -replace '^\s*\d+\s*-\s*', '').Trim()
        $lower = $canonical.ToLowerInvariant()
        $hideEndpoint = $lower.StartsWith('cable in 16ch') -or
            $lower.StartsWith('cable out 16ch') -or
            $lower -eq 'obs stream audio (surround)' -or
            $lower -eq 'obs stream audio loopback (surround)'
        if ($renames.ContainsKey($canonical)) {{
            $newName = $renames[$canonical]
        }} else {{
            if ($lower.StartsWith('cable input')) {{
                $newName = 'OBS Stream Audio'
            }} elseif ($lower.StartsWith('cable in 16ch')) {{
                $newName = 'OBS Stream Audio (Surround)'
            }} elseif ($lower.StartsWith('cable output')) {{
                $newName = 'OBS Stream Audio Loopback'
            }} elseif ($lower.StartsWith('cable out 16ch')) {{
                $newName = 'OBS Stream Audio Loopback (Surround)'
            }}
        }}
        if (-not $newName -and -not $hideEndpoint) {{ continue }}

        $dataFlow = if ($root -eq $render) {{ '0' }} else {{ '1' }}
        $deviceId = ""{{0.0.$dataFlow.00000000}}.$guid""
        if ($newName) {{
            $rc = [AudioHelper]::RenameEndpoint($deviceId, $newName)
            if ($rc -eq 0) {{
                $successes++
                Write-Host ""renamed: $cur -> $newName""
            }} else {{
                Write-Host ""rename FAILED for ${{cur}}: rc=0x$('{{0:x}}' -f $rc)""
            }}
        }}
        if ($hideEndpoint -and (($state -band $hiddenStateFlag) -eq 0)) {{
            $label = $cur
            if ($newName) {{ $label = $newName }}
            $vrc = [AudioHelper]::SetEndpointVisible($deviceId, $false)
            if ($vrc -eq 0) {{
                $hidden++
                Write-Host ""hidden: $label""
            }} else {{
                Write-Host ""hide FAILED for ${{label}}: rc=0x$('{{0:x}}' -f $vrc)""
            }}
        }}
    }}
}}

if (($successes + $hidden) -gt 0) {{
    Stop-Service AudioEndpointBuilder -Force -ErrorAction SilentlyContinue
    Start-Service AudioEndpointBuilder -ErrorAction SilentlyContinue
    Start-Service Audiosrv -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500

    $aeb = (Get-Service AudioEndpointBuilder -ErrorAction SilentlyContinue).Status
    $asr = (Get-Service Audiosrv               -ErrorAction SilentlyContinue).Status
    Write-Host ""renames=$successes hidden=$hidden audio-stack=AEB:$aeb Audiosrv:$asr explorer=unchanged""
}} else {{
    Write-Host ""renames=0 hidden=0 audio-stack=unchanged explorer=unchanged""
}}
";
            Process proc;
            string stdout, stderr;
            try
            {
                var psi = new ProcessStartInfo("powershell", Win32Args.Build("-NoProfile", "-NonInteractive", "-Command", script))
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                proc = Process.Start(psi);
                stdout = proc.StandardOutput.ReadToEnd();
                stderr = proc.StandardError.ReadToEnd();
                if (!proc.WaitForExit(30000))
                {
                    try { proc.Kill(); } catch (InvalidOperationException) { }
                    return;
                }
            }
            catch (Exception exc) when (exc is System.ComponentModel.Win32Exception || exc is InvalidOperationException)
            {
                log?.Invoke("warn: endpoint rename failed: " + exc.Message);
                return;
            }
            if (log != null)
            {
                foreach (var line in (stdout ?? "").Trim().Split('\n')) if (line.Trim().Length > 0) log(line.TrimEnd('\r'));
                if (proc.ExitCode != 0)
                {
                    string err = (stderr ?? "").Trim();
                    log("warn: rename script exited " + proc.ExitCode + ": " + (err.Length > 0 ? err.Split('\n')[0] : ""));
                }
            }
        }

        // true iff the bundled virtual audio driver is registered.
        public static bool IsVbcableInstalled()
        {
            try
            {
                var psi = new ProcessStartInfo("powershell", Win32Args.Build("-NoProfile", "-NonInteractive", "-Command",
                    "if (Get-CimInstance Win32_SoundDevice | Where-Object { $_.Name -match 'VB-Audio Virtual Cable' }) { 'yes' }"))
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using (var proc = Process.Start(psi))
                {
                    string stdout = proc.StandardOutput.ReadToEnd();
                    if (!proc.WaitForExit(10000))
                    {
                        try { proc.Kill(); } catch (InvalidOperationException) { }
                        return false;
                    }
                    return (stdout ?? "").Contains("yes");
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
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
            string names = string.Join(",", DiscordProcessNames.Select(n => "'" + n.Replace("'", "''") + "'"));
            string restartNames = string.Join(",", DiscordRestartProcessNames.Select(n => "'" + n.Replace("'", "''") + "'"));
            string script = $@"
$ErrorActionPreference = 'Stop'
$names = @({names})
$restartNames = @({restartNames})
$procs = @(Get-CimInstance Win32_Process | Where-Object {{ $names -contains $_.Name }})
$restart = @($procs | Where-Object {{ $restartNames -contains $_.Name -and -not [string]::IsNullOrWhiteSpace($_.ExecutablePath) }} | Select-Object -ExpandProperty ExecutablePath -Unique)
foreach ($p in $procs) {{
    try {{ Stop-Process -Id ([int]$p.ProcessId) -Force -ErrorAction Stop }} catch {{ }}
}}
if ($procs.Count -gt 0) {{ Start-Sleep -Milliseconds 1200 }}
$left = @(Get-CimInstance Win32_Process | Where-Object {{ $names -contains $_.Name }})
[pscustomobject]@{{
    stopped = [int]$procs.Count
    remaining = [int]$left.Count
    restart = $restart
}} | ConvertTo-Json -Compress
if ($left.Count -gt 0) {{ exit 2 }}
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
                proc = Process.Start(psi);
                stdout = proc.StandardOutput.ReadToEnd();
                stderr = proc.StandardError.ReadToEnd();
                if (!proc.WaitForExit(20000))
                {
                    try { proc.Kill(); } catch (InvalidOperationException) { }
                    log?.Invoke("OBS Stream Audio cleanup could not check Discord: timed out");
                    return (false, new List<string>());
                }
            }
            catch (Exception exc) when (exc is System.ComponentModel.Win32Exception || exc is InvalidOperationException)
            {
                log?.Invoke("OBS Stream Audio cleanup could not check Discord: " + exc.Message);
                return (false, new List<string>());
            }

            var restartPaths = new List<string>();
            int stopped = 0, remaining = 0;
            try
            {
                var info = JObject.Parse(string.IsNullOrWhiteSpace(stdout) ? "{}" : stdout.Trim());
                var restartValue = info["restart"];
                if (restartValue is JArray arr) restartPaths = arr.Select(t => t.ToString()).Where(s => s.Trim().Length > 0).ToList();
                else if (restartValue != null && restartValue.Type == JTokenType.String) restartPaths = new List<string> { restartValue.ToString() };
                stopped = info.Value<int?>("stopped") ?? 0;
                remaining = info.Value<int?>("remaining") ?? 0;
            }
            catch (Newtonsoft.Json.JsonException)
            {
                stopped = 0;
                remaining = 0;
            }

            if (stopped > 0) log?.Invoke($"stopped Discord audio client(s) before driver removal ({stopped})");
            if (proc.ExitCode != 0)
            {
                string detail = (string.IsNullOrEmpty(stderr) ? stdout : stderr) ?? "";
                detail = detail.Trim();
                string msg = detail.Length > 0 ? detail.Split('\n')[0] : $"{remaining} Discord process(es) still running";
                log?.Invoke("OBS Stream Audio cleanup blocked by Discord: " + msg);
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

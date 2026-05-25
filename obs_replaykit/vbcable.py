"""Install and rename the virtual audio device used for OBS stream audio."""

from __future__ import annotations

import json
import shutil
import subprocess
import tempfile
import zipfile
from pathlib import Path
from typing import Callable, Optional

from .config import (
    INPUT_OVERLAY_INSTALLERS_ZIP,
    VBCABLE_DRIVER_PACK_NAME,
    VBCABLE_SETUP_EXE_NAME,
)
from .voicemeeter import restore_voicemeeter_inputs, snapshot_voicemeeter_inputs

LogFn = Optional[Callable[[str], None]]


# Post-install rename table. The render endpoint is what OBS monitors to; the
# loopback endpoint is what Discord captures when the user selects it.
_ENDPOINT_RENAMES = {
    "CABLE Input":    "OBS Stream Audio",
    "CABLE In 16ch":  "OBS Stream Audio (Surround)",
    "CABLE Output":   "OBS Stream Audio Loopback",
    "CABLE Out 16ch": "OBS Stream Audio Loopback (Surround)",
}

_DISCORD_PROCESS_NAMES = (
    "Discord.exe",
    "DiscordCanary.exe",
    "DiscordPTB.exe",
    "DiscordDevelopment.exe",
    "DiscordSystemHelper.exe",
)
_DISCORD_RESTART_PROCESS_NAMES = frozenset({
    "Discord.exe",
    "DiscordCanary.exe",
    "DiscordPTB.exe",
    "DiscordDevelopment.exe",
})


# powershell helper that compiles a c# class at runtime via add-type and calls IMMDeviceEnumerator + IPolicyConfigVistaClient. doing this from powershell instead of python+ctypes saves ~150 lines of com vtable glue at the cost of one powershell process per call (~300ms).

_PS_AUDIO_HELPER = r"""
$ErrorActionPreference = 'Stop'
$source = @'
using System;
using System.Runtime.InteropServices;

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
public class MMDeviceEnumerator { }

[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDeviceEnumerator {
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr ppDevices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
}

[Guid("D666063F-1587-4E43-81F1-B948E807363F"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDevice {
    [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, out IntPtr ppInterface);
    [PreserveSig] int OpenPropertyStore(int stgmAccess, out IntPtr ppProperties);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
    [PreserveSig] int GetState(out int pdwState);
}

// PROPERTYKEY: identifies a property (GUID + pid pair). For PKEY_Device_FriendlyName
// the GUID is {a45c254e-df1c-4efd-8020-67d146a850e0} and pid=2.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct PROPERTYKEY {
    public Guid fmtid;
    public uint pid;
}

// PROPVARIANT: 16 bytes header + 8 bytes payload union (24 bytes on x64
// with the trailing padding the layout below adds explicitly). We only
// ever store a VT_LPWSTR (=31) pointing to a CoTaskMem-allocated UTF-16
// string, so the other union slots aren't declared.
[StructLayout(LayoutKind.Sequential)]
public struct PROPVARIANT {
    public ushort vt;
    public ushort r1;
    public ushort r2;
    public ushort r3;
    public IntPtr pszVal;
    public IntPtr padding;
}

[ComImport, Guid("294935CE-F637-4E7C-A41B-AB255460B862")]
public class _CPolicyConfigVistaClient { }

[ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
public class _CPolicyConfigClient { }

[Guid("568B9108-44BF-40B4-9006-86AFE5B5A620"),
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

[Guid("F8679F50-850A-41CF-9C72-430F290290C8"),
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
    // EDataFlow: 0 = eRender (playback / speakers / sinks)
    //            1 = eCapture (recording / mic / sources)
    // ERole:     0 = eConsole, 1 = eMultimedia, 2 = eCommunications
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
        // Apply to all three roles so apps following any of them follow us.
        int rc0 = client.SetDefaultEndpoint(id, 0);  // Console
        int rc1 = client.SetDefaultEndpoint(id, 1);  // Multimedia
        int rc2 = client.SetDefaultEndpoint(id, 2);  // Communications
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
    // Rename an endpoint's PKEY_Device_FriendlyName. The audio service
    // honours this even when direct registry writes are blocked by the
    // TrustedInstaller ACL on \MMDevices\Audio\...\<guid>\Properties.
    public static int RenameEndpoint(string deviceId, string newName) {
        var client = (IPolicyConfigVistaClient)(new _CPolicyConfigVistaClient());
        var key = new PROPERTYKEY {
            fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
            pid = 2
        };
        IntPtr strPtr = Marshal.StringToCoTaskMemUni(newName);
        var pv = new PROPVARIANT { vt = 31, pszVal = strPtr };  // 31 = VT_LPWSTR
        try {
            return client.SetPropertyValue(deviceId, false, ref key, ref pv);
        } finally {
            Marshal.FreeCoTaskMem(strPtr);
        }
    }
}
'@
Add-Type -TypeDefinition $source -Language CSharp | Out-Null
"""


def _run_ps(command_after_helper: str, log: LogFn = None) -> Optional[str]:
    """invoke powershell with the audio-helper c# class defined, run a one-liner that returns a string, and return the trimmed stdout."""
    script = _PS_AUDIO_HELPER + "\n" + command_after_helper
    try:
        result = subprocess.run(
            ["powershell", "-NoProfile", "-NonInteractive", "-Command", script],
            capture_output=True,
            text=True,
            timeout=30,
        )
    except Exception as exc:
        if log:
            log(f"powershell helper failed: {exc}")
        return None
    if result.returncode != 0:
        if log:
            err = (result.stderr or "").strip().splitlines()
            log(f"powershell helper exit {result.returncode}: {err[0] if err else ''}")
        return None
    return result.stdout.strip()


def _get_default_playback(log: LogFn = None) -> Optional[str]:
    """guid-form device id of the current defualt playback (render) endpoint, or None."""
    out = _run_ps("[AudioHelper]::GetDefaultRender()", log=log)
    return out or None


def _get_default_capture(log: LogFn = None) -> Optional[str]:
    """guid-form device id of the current defualt capture (mic) endpoint, or None."""
    out = _run_ps("[AudioHelper]::GetDefaultCapture()", log=log)
    return out or None


def _set_default_playback(device_id: str, log: LogFn = None) -> bool:
    """restore device_id as defualt playback for console / multimedia / communications."""
    safe = device_id.replace("'", "''")
    out = _run_ps(f"[AudioHelper]::SetDefaultRender('{safe}')", log=log)
    if out is None:
        return False
    try:
        return int(out) == 0
    except ValueError:
        return False


def _set_default_capture(device_id: str, log: LogFn = None) -> bool:
    """restore device_id as defualt capture (mic) for console / multimedia / communications."""
    safe = device_id.replace("'", "''")
    out = _run_ps(f"[AudioHelper]::SetDefaultCapture('{safe}')", log=log)
    if out is None:
        return False
    try:
        return int(out) == 0
    except ValueError:
        return False


def _rename_endpoints(log: LogFn = None) -> None:
    """Rename the installed audio endpoints to OBS Stream Audio names."""
    # build the ps rename table from _endpoint_renames; lookup names are simple ascii so no escaping needed.
    ps_pairs = ";\n".join(
        f"    '{orig}' = '{new}'"
        for orig, new in _ENDPOINT_RENAMES.items()
    )
    script = _PS_AUDIO_HELPER + f"""
$renames = @{{
{ps_pairs}
}}

$render  = 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\MMDevices\\Audio\\Render'
$capture = 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\MMDevices\\Audio\\Capture'
$friendlyKey = '{{a45c254e-df1c-4efd-8020-67d146a850e0}},2'
$hiddenStateFlag = 0x10000000

$successes = 0
$hidden = 0
foreach ($root in @($render, $capture)) {{
    foreach ($guid in (Get-ChildItem $root -ErrorAction SilentlyContinue).PSChildName) {{
        $endpoint = Join-Path $root $guid
        $props = Join-Path $root "$guid\\Properties"
        if (-not (Test-Path $props)) {{ continue }}
        $cur = (Get-ItemProperty -LiteralPath $props -Name $friendlyKey -ErrorAction SilentlyContinue).$friendlyKey
        if (-not $cur) {{ continue }}
        $state = (Get-ItemProperty -LiteralPath $endpoint -Name 'DeviceState' -ErrorAction SilentlyContinue).DeviceState
        if ($null -eq $state) {{ $state = 0 }}
        $newName = $null
        $lower = $cur.ToLowerInvariant()
        $hideEndpoint = $lower.StartsWith('cable in 16ch') -or
            $lower.StartsWith('cable out 16ch') -or
            $lower -eq 'obs stream audio (surround)' -or
            $lower -eq 'obs stream audio loopback (surround)'
        if ($renames.ContainsKey($cur)) {{
            $newName = $renames[$cur]
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

        # mmdevice id format: "{{0.0.x.00000000}}.{{guid}}" -- x=0 render, x=1 capture.
        $dataFlow = if ($root -eq $render) {{ '0' }} else {{ '1' }}
        $deviceId = "{{0.0.$dataFlow.00000000}}.$guid"
        if ($newName) {{
            $rc = [AudioHelper]::RenameEndpoint($deviceId, $newName)
            if ($rc -eq 0) {{
                $successes++
                Write-Host "renamed: $cur -> $newName"
            }} else {{
                Write-Host "rename FAILED for ${{cur}}: rc=0x$('{{0:x}}' -f $rc)"
            }}
        }}
        if ($hideEndpoint -and (($state -band 1) -ne 0) -and (($state -band $hiddenStateFlag) -eq 0)) {{
            $label = $cur
            if ($newName) {{ $label = $newName }}
            $vrc = [AudioHelper]::SetEndpointVisible($deviceId, $false)
            if ($vrc -eq 0) {{
                $hidden++
                Write-Host "hidden: $label"
            }} else {{
                Write-Host "hide FAILED for ${{label}}: rc=0x$('{{0:x}}' -f $vrc)"
            }}
        }}
    }}
}}

if (($successes + $hidden) -gt 0) {{
    # bounce the audio stack so the new names appear in the sound output picker + quick settings without a reboot. win11 quick settings is fed by audioendpointbuilder + a shell cache in explorer.exe, both of which hold their own copies. start-service on the parent doesnt cascade-start dependents, so audiosrv needs an explicit start after audioendpointbuilder -- skip it and the user loses audio until reboot.
    Stop-Service AudioEndpointBuilder -Force -ErrorAction SilentlyContinue
    Start-Service AudioEndpointBuilder -ErrorAction SilentlyContinue
    Start-Service Audiosrv -ErrorAction SilentlyContinue  # cascade-stopped above
    Start-Sleep -Milliseconds 500
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
    # windows auto-respawns explorer; no start-process needed.

    # sanity-report so the install log shows whether anythings still stopped.
    $aeb = (Get-Service AudioEndpointBuilder -ErrorAction SilentlyContinue).Status
    $asr = (Get-Service Audiosrv               -ErrorAction SilentlyContinue).Status
    Write-Host "renames=$successes hidden=$hidden audio-stack=AEB:$aeb Audiosrv:$asr explorer=restarted"
}} else {{
    Write-Host "renames=0 hidden=0 audio-stack=unchanged explorer=unchanged"
}}
"""

    try:
        result = subprocess.run(
            ["powershell", "-NoProfile", "-NonInteractive", "-Command", script],
            capture_output=True,
            text=True,
            timeout=30,
        )
    except Exception as exc:
        if log:
            log(f"warn: endpoint rename failed: {exc}")
        return
    if log:
        for line in (result.stdout or "").strip().splitlines():
            log(line)
        if result.returncode != 0:
            err = (result.stderr or "").strip().splitlines()
            log(f"warn: rename script exited {result.returncode}: {err[0] if err else ''}")


def is_vbcable_installed() -> bool:
    """True iff the bundled virtual audio driver is registered."""
    try:
        result = subprocess.run(
            ["powershell", "-NoProfile", "-NonInteractive", "-Command",
             "if (Get-CimInstance Win32_SoundDevice | "
             "Where-Object { $_.Name -match 'VB-Audio Virtual Cable' }) { 'yes' }"],
            capture_output=True,
            text=True,
            timeout=10,
        )
    except Exception:
        return False
    return "yes" in (result.stdout or "")


def _extract_driver_pack(log: LogFn = None) -> Optional[Path]:
    """Unpack the bundled audio-driver installer into a temp directory."""
    if not INPUT_OVERLAY_INSTALLERS_ZIP.is_file():
        if log:
            log(f"(no {INPUT_OVERLAY_INSTALLERS_ZIP.name} bundled)")
        return None
    try:
        with zipfile.ZipFile(INPUT_OVERLAY_INSTALLERS_ZIP) as outer:
            if VBCABLE_DRIVER_PACK_NAME not in outer.namelist():
                if log:
                    log("(OBS Stream Audio installer not in installers.zip - skipping)")
                return None
            tmpdir = Path(tempfile.mkdtemp(prefix="obsreplaykit_vbcable_"))
            with outer.open(VBCABLE_DRIVER_PACK_NAME) as inner_fp:
                with zipfile.ZipFile(inner_fp) as inner:
                    inner.extractall(tmpdir)
    except Exception as exc:
        if log:
            log(f"failed to extract OBS Stream Audio installer: {exc}")
        return None

    setup = tmpdir / VBCABLE_SETUP_EXE_NAME
    if not setup.is_file():
        if log:
            log(f"warn: {VBCABLE_SETUP_EXE_NAME} not found after extraction")
        shutil.rmtree(tmpdir, ignore_errors=True)
        return None
    return tmpdir


def install_vbcable(log: LogFn = None) -> bool:
    """Install the OBS Stream Audio device and restore the user's audio defaults."""
    if is_vbcable_installed():
        if log:
            log("OBS Stream Audio device already installed")
        return True

    pack_dir = _extract_driver_pack(log=log)
    if pack_dir is None:
        return False

    saved_render  = _get_default_playback(log=log)
    saved_capture = _get_default_capture(log=log)
    voicemeeter_snapshot = snapshot_voicemeeter_inputs(log=log)
    if log:
        if saved_render:
            log(f"saved current default playback:  {saved_render}")
        else:
            log("warn: couldn't query current default playback - cannot restore after install")
        if saved_capture:
            log(f"saved current default recording: {saved_capture}")
        else:
            log("warn: couldn't query current default recording - cannot restore after install")

    if log:
        log("running OBS Stream Audio installer...")
        log("    Windows Security may prompt for an audio driver - click Install")

    setup = pack_dir / VBCABLE_SETUP_EXE_NAME
    try:
        result = subprocess.run(
            [str(setup), "-i", "-h"],
            capture_output=True,
            text=True,
            timeout=300,
        )
    except subprocess.TimeoutExpired:
        if log:
            log("OBS Stream Audio installer timed out after 300s")
        shutil.rmtree(pack_dir, ignore_errors=True)
        return False
    except Exception as exc:
        if log:
            log(f"OBS Stream Audio installer launch failed: {exc}")
        shutil.rmtree(pack_dir, ignore_errors=True)
        return False
    finally:
        shutil.rmtree(pack_dir, ignore_errors=True)

    if result.returncode != 0:
        if log:
            log(f"OBS Stream Audio installer exited with code {result.returncode}")
            err = (result.stderr or "").strip().splitlines()
            if err:
                log(f"stderr: {err[0]}")
        return False

    if not is_vbcable_installed():
        if log:
            log("OBS Stream Audio installer ran but the driver was not detected")
        return False

    # rename before restoring defaults: both bounce audiosrv, so pay the audio-interrupt cost once.
    _rename_endpoints(log=log)

    if saved_render:
        if _set_default_playback(saved_render, log=log):
            if log:
                log(f"restored default playback  -> {saved_render}")
        else:
            if log:
                log("warn: failed to restore default playback - fix manually in Sound settings")

    if saved_capture:
        if _set_default_capture(saved_capture, log=log):
            if log:
                log(f"restored default recording -> {saved_capture}")
        else:
            if log:
                log("warn: failed to restore default recording - fix manually in Sound settings")

    restore_voicemeeter_inputs(voicemeeter_snapshot, log=log)

    if log:
        log("OBS Stream Audio device installed")
    return True


def ensure_vbcable(log: LogFn = None) -> bool:
    """Install OBS Stream Audio if it is missing."""
    if is_vbcable_installed():
        if log:
            log("OBS Stream Audio device already installed")
        _rename_endpoints(log=log)
        return True
    return install_vbcable(log=log)


def _stop_discord_for_driver_uninstall(log: LogFn = None) -> tuple[bool, list[Path]]:
    """Stop Discord variants that commonly hold the ReplayKit cable open."""
    names = ",".join("'" + name.replace("'", "''") + "'" for name in _DISCORD_PROCESS_NAMES)
    restart_names = ",".join("'" + name.replace("'", "''") + "'" for name in _DISCORD_RESTART_PROCESS_NAMES)
    script = rf"""
$ErrorActionPreference = 'Stop'
$names = @({names})
$restartNames = @({restart_names})
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
"""
    try:
        result = subprocess.run(
            ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script],
            capture_output=True,
            text=True,
            timeout=20,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
    except Exception as exc:
        if log:
            log(f"OBS Stream Audio cleanup could not check Discord: {exc}")
        return False, []

    restart_paths: list[Path] = []
    try:
        info = json.loads((result.stdout or "{}").strip() or "{}")
        restart_value = info.get("restart", [])
        if isinstance(restart_value, str):
            restart_value = [restart_value]
        restart_paths = [Path(str(p)) for p in restart_value if str(p).strip()]
        stopped = int(info.get("stopped", 0) or 0)
        remaining = int(info.get("remaining", 0) or 0)
    except (TypeError, ValueError, json.JSONDecodeError):
        stopped = 0
        remaining = 0

    if stopped and log:
        log(f"stopped Discord audio client(s) before driver removal ({stopped})")
    if result.returncode != 0:
        if log:
            detail = (result.stderr or result.stdout or "").strip().splitlines()
            msg = detail[0] if detail else f"{remaining} Discord process(es) still running"
            log(f"OBS Stream Audio cleanup blocked by Discord: {msg}")
        return False, restart_paths
    return True, restart_paths


def _restart_discord_apps(paths: list[Path], log: LogFn = None) -> None:
    flags = getattr(subprocess, "CREATE_NO_WINDOW", 0) | getattr(subprocess, "DETACHED_PROCESS", 0)
    for exe in dict.fromkeys(paths):
        if not exe.is_file():
            continue
        try:
            subprocess.Popen(
                [str(exe)],
                cwd=str(exe.parent),
                stdin=subprocess.DEVNULL,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                close_fds=True,
                creationflags=flags,
            )
            if log:
                log(f"restarted Discord -> {exe}")
        except Exception as exc:
            if log:
                log(f"warn: could not restart Discord at {exe}: {exc}")


def uninstall_vbcable(log: LogFn = None) -> bool:
    """Uninstall the OBS Stream Audio virtual audio driver."""
    if not is_vbcable_installed():
        return True

    ok_to_uninstall, restart_apps = _stop_discord_for_driver_uninstall(log=log)
    if not ok_to_uninstall:
        _restart_discord_apps(restart_apps, log=log)
        return False

    pack_dir = _extract_driver_pack(log=log)
    if pack_dir is None:
        _restart_discord_apps(restart_apps, log=log)
        return False

    setup = pack_dir / VBCABLE_SETUP_EXE_NAME
    try:
        result = subprocess.run(
            [str(setup), "-u", "-h"],
            capture_output=True,
            text=True,
            timeout=300,
        )
    except subprocess.TimeoutExpired:
        if log:
            log("OBS Stream Audio uninstaller timed out after 300s")
        shutil.rmtree(pack_dir, ignore_errors=True)
        return False
    except Exception as exc:
        if log:
            log(f"OBS Stream Audio uninstaller launch failed: {exc}")
        shutil.rmtree(pack_dir, ignore_errors=True)
        return False
    finally:
        shutil.rmtree(pack_dir, ignore_errors=True)
        _restart_discord_apps(restart_apps, log=log)

    if result.returncode != 0:
        if log:
            log(f"OBS Stream Audio uninstaller exited with code {result.returncode}")
        return False

    return not is_vbcable_installed()

"""In-menu cleanup for OBS ReplayKit installs."""

from __future__ import annotations

import os
import shutil
import subprocess
from pathlib import Path
from typing import Callable, Optional

from .config import OBS_CONFIG, PROGRAMDATA, PROGRAMFILES_OBS_DIR
from .obs import close_obs
from .scheduled_task import delete_elevation_task
from .sleep_override import remove_sleep_override
from .startup import configure_obs_startup
from .vbcable import uninstall_vbcable


LogFn = Optional[Callable[[str], None]]


def _run_hidden(args: list[str], timeout: int = 30) -> subprocess.CompletedProcess:
    return subprocess.run(
        args,
        capture_output=True,
        text=True,
        timeout=timeout,
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
    )


def _current_parent_pid() -> int | None:
    try:
        result = _run_hidden([
            "powershell.exe",
            "-NoProfile",
            "-NonInteractive",
            "-Command",
            f"(Get-CimInstance Win32_Process -Filter \"ProcessId={os.getpid()}\").ParentProcessId",
        ])
    except (OSError, subprocess.TimeoutExpired):
        return None
    try:
        return int((result.stdout or "").strip())
    except ValueError:
        return None


def stop_obs_and_helpers(log: LogFn = None) -> bool:
    """Close OBS and ReplayKit helper processes while keeping this setup process alive."""
    close_obs(log=log)
    keep = {os.getpid()}
    parent = _current_parent_pid()
    if parent:
        keep.add(parent)
    keep_csv = ",".join(str(pid) for pid in keep)
    script = rf"""
$keep = @({keep_csv})
Get-CimInstance Win32_Process |
  Where-Object {{ $_.Name -in @('OBSReplayKit.exe','OBSReplayKit-Encoder.exe') -and $keep -notcontains [int]$_.ProcessId }} |
  ForEach-Object {{
    try {{ Stop-Process -Id $_.ProcessId -Force -ErrorAction Stop }} catch {{ }}
  }}
"""
    try:
        _run_hidden(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script])
    except (OSError, subprocess.TimeoutExpired) as exc:
        if log:
            log(f"warn: helper stop failed: {exc}")
        return False
    return True


def remove_replaykit_plugins(log: LogFn = None) -> bool:
    targets = (
        PROGRAMFILES_OBS_DIR / "obs-plugins" / "64bit" / "win-capture-audio.dll",
        PROGRAMFILES_OBS_DIR / "obs-plugins" / "64bit" / "win-capture-audio.pdb",
        PROGRAMFILES_OBS_DIR / "data" / "obs-plugins" / "win-capture-audio",
        PROGRAMFILES_OBS_DIR / "obs-plugins" / "64bit" / "input-overlay.dll",
        PROGRAMFILES_OBS_DIR / "obs-plugins" / "64bit" / "SDL2.dll",
        PROGRAMFILES_OBS_DIR / "data" / "obs-plugins" / "input-overlay",
        PROGRAMFILES_OBS_DIR / "obs-plugins" / "64bit" / "bongobs-cat.dll",
        PROGRAMFILES_OBS_DIR / "bin" / "64bit" / "Bango Cat",
        PROGRAMFILES_OBS_DIR / "data" / "obs-plugins" / "bongobs-cat",
        PROGRAMFILES_OBS_DIR / "obs-plugins" / "64bit" / "obs-composite-blur.dll",
        PROGRAMFILES_OBS_DIR / "obs-plugins" / "64bit" / "obs-composite-blur.pdb",
        PROGRAMFILES_OBS_DIR / "data" / "obs-plugins" / "obs-composite-blur",
        PROGRAMDATA / "obs-studio" / "plugins" / "obs-composite-blur",
        PROGRAMFILES_OBS_DIR / "obs-plugins" / "64bit" / "obs-shaderfilter.dll",
        PROGRAMFILES_OBS_DIR / "obs-plugins" / "64bit" / "obs-shaderfilter.pdb",
        PROGRAMFILES_OBS_DIR / "data" / "obs-plugins" / "obs-shaderfilter",
    )
    ok = True
    for target in targets:
        if not target.exists():
            continue
        try:
            if target.is_dir():
                shutil.rmtree(target)
            else:
                target.unlink()
        except OSError as exc:
            ok = False
            if log:
                log(f"warn: could not remove {target}: {exc}")
    return ok


def remove_virtual_display_driver(log: LogFn = None) -> bool:
    script = r"""
$ErrorActionPreference = 'Continue'
$vddDevice = Get-PnpDevice -ErrorAction SilentlyContinue |
    Where-Object { $_.FriendlyName -eq 'Virtual Display Driver' } |
    Select-Object -First 1
if ($vddDevice) {
    Disable-PnpDevice -InstanceId $vddDevice.InstanceId -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
    pnputil.exe /remove-device $vddDevice.InstanceId | Out-Null
}
$drivers = pnputil.exe /enum-drivers | Out-String
$matches = [regex]::Matches($drivers, "Published Name:\s+(oem\d+\.inf)\s+Original Name:\s+MttVDD\.inf", "IgnoreCase")
foreach ($m in $matches) {
    pnputil.exe /delete-driver $m.Groups[1].Value /uninstall /force | Out-Null
}
if (Test-Path "C:\IddSampleDriver") {
    Remove-Item -Recurse -Force "C:\IddSampleDriver" -ErrorAction SilentlyContinue
}
"""
    try:
        result = _run_hidden(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script], timeout=60)
    except (OSError, subprocess.TimeoutExpired) as exc:
        if log:
            log(f"warn: virtual display cleanup failed: {exc}")
        return False
    if result.returncode != 0 and log:
        err = (result.stderr or result.stdout or "").strip().splitlines()
        log(f"warn: virtual display cleanup returned {result.returncode}: {err[0] if err else ''}")
    return result.returncode == 0


def wipe_obs_config(log: LogFn = None) -> bool:
    if not OBS_CONFIG.exists():
        return True
    try:
        shutil.rmtree(OBS_CONFIG)
        return True
    except OSError as exc:
        if log:
            log(f"warn: could not wipe {OBS_CONFIG}: {exc}")
        return False


def run_cleanup(progress) -> list[str]:
    steps: list[tuple[str, str, Callable[[], object]]] = []

    def add(title: str, detail: str, action: Callable[[], object]) -> None:
        steps.append((title, detail, action))

    add("Close OBS", "Stops OBS and ReplayKit helpers, but keeps this setup window alive.", lambda: stop_obs_and_helpers(log=progress.log))
    add("Remove launch permission", "Deletes the ReplayKit scheduled task.", lambda: delete_elevation_task(log=progress.log))
    add("Remove Windows startup", "Stops ReplayKit from launching OBS when Windows signs in.", lambda: configure_obs_startup(False, log=progress.log))
    add("Remove monitor-sleep override", "Restores default Windows display-sleep behavior for OBS.", lambda: remove_sleep_override(log=progress.log))
    add("Remove OBS plugins", "Deletes ReplayKit OBS plugins from the OBS install folder.", lambda: remove_replaykit_plugins(log=progress.log))
    add("Remove OBS Stream Audio", "Uninstalls the ReplayKit virtual audio device.", lambda: uninstall_vbcable(log=progress.log))
    add("Remove virtual display driver", "Deletes the optional virtual display driver if it exists.", lambda: remove_virtual_display_driver(log=progress.log))
    add("Wipe OBS ReplayKit config", "Deletes the OBS config folder. ReplayKit preferences are kept.", lambda: wipe_obs_config(log=progress.log))

    progress.total_steps = len(steps)
    from .cli import _run_apply_step
    for index, (title, detail, action) in enumerate(steps, 1):
        _run_apply_step(progress, index, title, detail, action)
    progress.render(progress.total_steps, "Cleanup complete", "OBS ReplayKit changes were removed.", state="done")
    return progress.issues

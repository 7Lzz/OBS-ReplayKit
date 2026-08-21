"""in-menu cleanup for OBS ReplayKit installs."""

from __future__ import annotations

import os
import shutil
import subprocess
from pathlib import Path
from typing import Callable, Optional

from .config import OBS_CONFIG, PROGRAMDATA, PROGRAMFILES_OBS_DIR, REPLAYKIT_CONFIG, REPLAYKIT_SETUP_CACHE, REPLAYKIT_TRAY_PLUGIN_DIR, REPLAYKIT_USER_STATE_CACHE
from .obs import close_obs
from .scheduled_task import delete_elevation_task
from .sleep_override import remove_sleep_override
from .startup import configure_obs_startup
from .vbcable import uninstall_vbcable


LogFn = Optional[Callable[[str], None]]

_REPLAYKIT_RUNTIME_STATE_RELS = (
    Path("obs-replayKit/scripts/helper/clips_db.json"),
    Path("obs-replayKit/scripts/helper/clips_index.json"),
)


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
    """close OBS and ReplayKit helper processes while keeping this setup process alive."""
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
        REPLAYKIT_TRAY_PLUGIN_DIR,
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
    if not REPLAYKIT_CONFIG.exists():
        return True
    try:
        shutil.rmtree(REPLAYKIT_CONFIG)
        return True
    except OSError as exc:
        if log:
            log(f"warn: could not wipe {REPLAYKIT_CONFIG}: {exc}")
        return False


def save_user_settings(log: LogFn = None) -> bool:
    ok = True
    try:
        from .prefs import PREFS_FILE, load_prefs

        prefs = load_prefs()
        prefs.save()
        if log:
            log(f"ReplayKit user settings kept -> {PREFS_FILE}")
    except Exception as exc:
        ok = False
        if log:
            log(f"warn: could not keep ReplayKit user settings: {exc}")

    try:
        REPLAYKIT_USER_STATE_CACHE.mkdir(parents=True, exist_ok=True)
        kept = 0
        for rel in _REPLAYKIT_RUNTIME_STATE_RELS:
            source = OBS_CONFIG / rel
            if not source.is_file():
                continue
            target = REPLAYKIT_USER_STATE_CACHE / rel.name
            shutil.copy2(source, target)
            kept += 1
        if kept and log:
            log(f"ReplayKit clip state kept -> {REPLAYKIT_USER_STATE_CACHE}")
    except OSError as exc:
        ok = False
        if log:
            log(f"warn: could not keep ReplayKit clip state: {exc}")
    return ok


def remove_user_settings(log: LogFn = None) -> bool:
    try:
        from .prefs import PREFS_FILE
    except Exception:
        PREFS_FILE = None

    targets: list[Path] = []
    if PREFS_FILE is not None:
        targets.append(PREFS_FILE)
    targets.append(REPLAYKIT_SETUP_CACHE / "prefs.json")
    targets.append(REPLAYKIT_SETUP_CACHE / "clips_state.json")

    ok = True
    seen: set[str] = set()
    for target in targets:
        key = str(target.resolve(strict=False)).lower()
        if key in seen:
            continue
        seen.add(key)
        if not target.exists():
            continue
        try:
            target.unlink()
        except OSError as exc:
            ok = False
            if log:
                log(f"warn: could not remove {target}: {exc}")
    if REPLAYKIT_USER_STATE_CACHE.exists():
        try:
            shutil.rmtree(REPLAYKIT_USER_STATE_CACHE)
        except OSError as exc:
            ok = False
            if log:
                log(f"warn: could not remove {REPLAYKIT_USER_STATE_CACHE}: {exc}")
    return ok


def run_cleanup(progress, keep_user_settings: bool = True) -> list[str]:
    steps: list[tuple[str, str, Callable[[], object]]] = []

    def add(title: str, detail: str, action: Callable[[], object]) -> None:
        steps.append((title, detail, action))

    add("Close OBS", "Stops OBS and ReplayKit helpers, but keeps this setup window alive.", lambda: stop_obs_and_helpers(log=progress.log))
    add("Remove launch permission", "Deletes the ReplayKit scheduled task.", lambda: delete_elevation_task(log=progress.log))
    add("Remove Windows startup", "Stops ReplayKit from launching OBS when Windows signs in.", lambda: configure_obs_startup(False, log=progress.log))
    add("Remove Windows sleep override", "Restores default Windows sleep behavior for OBS.", lambda: remove_sleep_override(log=progress.log))
    add("Remove OBS plugins", "Deletes ReplayKit OBS plugins from the OBS install folder.", lambda: remove_replaykit_plugins(log=progress.log))
    add("Remove OBS Stream Audio", "Uninstalls the ReplayKit virtual audio device.", lambda: uninstall_vbcable(log=progress.log))
    add("Remove virtual display driver", "Deletes the optional virtual display driver if it exists.", lambda: remove_virtual_display_driver(log=progress.log))
    if keep_user_settings:
        add("Keep ReplayKit settings", "Saves current ReplayKit settings for the next install.", lambda: save_user_settings(log=progress.log))
    else:
        add("Remove ReplayKit settings", "Deletes saved ReplayKit preferences.", lambda: remove_user_settings(log=progress.log))
    add("Wipe OBS ReplayKit config", "Deletes ReplayKit's OBS config folder while preserving OBS scenes and profiles.", lambda: wipe_obs_config(log=progress.log))

    progress.total_steps = len(steps)
    from .cli import _run_apply_step
    for index, (title, detail, action) in enumerate(steps, 1):
        _run_apply_step(progress, index, title, detail, action)
    progress.render(progress.total_steps, "Cleanup complete", "OBS ReplayKit changes were removed.", state="done")
    return progress.issues

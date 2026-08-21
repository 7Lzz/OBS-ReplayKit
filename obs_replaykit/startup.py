"""windows startup integration for launching OBS with ReplayKit."""

from __future__ import annotations

import os
import base64
import json
import subprocess
from pathlib import Path
from typing import Callable, Optional

from .obs import OBS_START_ARGS, find_obs_exe

LogFn = Optional[Callable[[str], None]]

_RUN_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"
_VALUE_NAME = "OBS ReplayKit"
_SHORTCUT_NAME = "OBS ReplayKit.lnk"


def _startup_folder() -> Path | None:
    appdata = os.environ.get("APPDATA")
    if not appdata:
        return None
    return Path(appdata) / "Microsoft" / "Windows" / "Start Menu" / "Programs" / "Startup"


def _startup_shortcut_path() -> Path | None:
    folder = _startup_folder()
    if folder is None:
        return None
    return folder / _SHORTCUT_NAME


def _remove_legacy_run_value(log: LogFn = None) -> bool:
    import winreg

    try:
        with winreg.CreateKeyEx(winreg.HKEY_CURRENT_USER, _RUN_KEY, 0, winreg.KEY_SET_VALUE) as key:
            try:
                winreg.DeleteValue(key, _VALUE_NAME)
                if log:
                    log("Windows startup: removed legacy registry entry")
            except FileNotFoundError:
                pass
        return True
    except OSError as exc:
        if log:
            log(f"warn: could not remove legacy Windows startup registry entry: {exc}")
        return False


def _create_startup_shortcut(log: LogFn = None) -> bool:
    obs_exe = find_obs_exe()
    shortcut_path = _startup_shortcut_path()
    if obs_exe is None:
        if log:
            log("warn: OBS install not found - Windows startup was not changed")
        return False
    if shortcut_path is None:
        if log:
            log("warn: Windows Startup folder was not found")
        return False

    try:
        shortcut_path.parent.mkdir(parents=True, exist_ok=True)
    except OSError as exc:
        if log:
            log(f"warn: could not create Windows Startup folder: {exc}")
        return False

    script = """
$ErrorActionPreference = 'Stop'
$payload = $env:OBS_REPLAYKIT_STARTUP_SHORTCUT | ConvertFrom-Json
$shortcutPath = [string]$payload.shortcutPath
$targetPath = [string]$payload.targetPath
$arguments = [string]$payload.arguments
$workingDirectory = [string]$payload.workingDirectory
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $targetPath
$shortcut.Arguments = $arguments
$shortcut.WorkingDirectory = $workingDirectory
$shortcut.IconLocation = "$targetPath,0"
$shortcut.Description = 'Start OBS ReplayKit when Windows signs in.'
$shortcut.Save()
"""
    payload = {
        "shortcutPath": str(shortcut_path),
        "targetPath": str(obs_exe),
        "arguments": OBS_START_ARGS,
        "workingDirectory": str(obs_exe.parent),
    }
    env = os.environ.copy()
    env["OBS_REPLAYKIT_STARTUP_SHORTCUT"] = json.dumps(payload)
    encoded_script = base64.b64encode(script.encode("utf-16le")).decode("ascii")
    try:
        result = subprocess.run(
            [
                "powershell.exe",
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-EncodedCommand",
                encoded_script,
            ],
            capture_output=True,
            text=True,
            timeout=20,
            env=env,
            creationflags=0x08000000,
        )
    except (subprocess.TimeoutExpired, OSError) as exc:
        if log:
            log(f"warn: could not create Windows startup shortcut: {exc}")
        return False

    if result.returncode != 0:
        message = (result.stderr or result.stdout or "").strip().splitlines()
        if log:
            log(f"warn: could not create Windows startup shortcut: {message[0] if message else result.returncode}")
        return False

    if log:
        log(f"Windows startup: created shortcut at {shortcut_path}")
    return True


def _delete_startup_shortcut(log: LogFn = None) -> bool:
    shortcut_path = _startup_shortcut_path()
    if shortcut_path is None:
        return True
    try:
        shortcut_path.unlink(missing_ok=True)
        if log:
            log("Windows startup: removed Startup folder shortcut")
        return True
    except OSError as exc:
        if log:
            log(f"warn: could not remove Windows startup shortcut: {exc}")
        return False


def configure_obs_startup(enabled: bool, log: LogFn = None) -> bool:
    """add or remove OBS ReplayKit from the current users Windows startup apps."""
    if os.name != "nt":
        if log:
            log("warn: Windows startup setting is only available on Windows")
        return False

    if enabled:
        registry_removed = _remove_legacy_run_value(log)
        created = _create_startup_shortcut(log)
        if created:
            registry_removed = _remove_legacy_run_value(log) and registry_removed
            if log:
                log("Windows startup: OBS will start when you sign in")
        return created and registry_removed

    shortcut_removed = _delete_startup_shortcut(log)
    registry_removed = _remove_legacy_run_value(log)
    if log:
        log("Windows startup: off")
    return shortcut_removed and registry_removed

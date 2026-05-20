"""Windows startup integration for launching OBS with ReplayKit."""

from __future__ import annotations

import os
from typing import Callable, Optional

from .obs import OBS_START_ARGS, find_obs_exe

LogFn = Optional[Callable[[str], None]]

_RUN_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"
_VALUE_NAME = "OBS ReplayKit"


def _startup_command() -> str | None:
    obs_exe = find_obs_exe()
    if obs_exe is None:
        return None
    return f'"{obs_exe}" {OBS_START_ARGS}'


def configure_obs_startup(enabled: bool, log: LogFn = None) -> bool:
    """Add or remove OBS ReplayKit from the current user's Windows startup apps."""
    if os.name != "nt":
        if log:
            log("warn: Windows startup setting is only available on Windows")
        return False

    import winreg

    try:
        with winreg.CreateKeyEx(winreg.HKEY_CURRENT_USER, _RUN_KEY, 0, winreg.KEY_SET_VALUE) as key:
            if enabled:
                command = _startup_command()
                if command is None:
                    if log:
                        log("warn: OBS install not found - Windows startup was not changed")
                    return False
                winreg.SetValueEx(key, _VALUE_NAME, 0, winreg.REG_SZ, command)
                if log:
                    log("Windows startup: OBS will start when you sign in")
            else:
                try:
                    winreg.DeleteValue(key, _VALUE_NAME)
                except FileNotFoundError:
                    pass
                if log:
                    log("Windows startup: off")
        return True
    except OSError as exc:
        if log:
            log(f"warn: could not update Windows startup: {exc}")
        return False

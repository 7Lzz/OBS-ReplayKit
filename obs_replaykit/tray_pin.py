"""pin the OBS tray icon so Windows never buries it behind the '^' overflow arrow."""

from __future__ import annotations

import winreg
from typing import Callable, Optional

from .config import find_obs_exe_candidate

LogFn = Optional[Callable[[str], None]]

_NOTIFY_ICON_SETTINGS = r"Control Panel\NotifyIconSettings"


def _path_tail(path_str: str, parts: int = 4) -> str:
    """last N path segments, lowercased -- windows stores a standard installs ExecutablePath knownfolderid-relative instead of a plain drive letter, so comparing full paths would never match, but the tail is stable either way."""
    segments = [s for s in str(path_str).replace("/", "\\").split("\\") if s]
    return "\\".join(segments[-parts:]).lower()


def pin_obs_tray_icon(log: LogFn = None) -> bool:
    """sets IsPromoted=1 on obss tray icon entry so windows always shows it instead of hiding it behind the overflow arrow; no-op if obs has never registered a tray icon yet or the registry layout looks unexpected, since this is a reverse-engineered per-user scheme, not a documented api."""
    obs_exe = find_obs_exe_candidate()
    if obs_exe is None:
        return False
    target = _path_tail(str(obs_exe))

    try:
        root = winreg.OpenKey(winreg.HKEY_CURRENT_USER, _NOTIFY_ICON_SETTINGS)
    except OSError:
        if log:
            log("OBS tray icon not pinned: no tray icons registered on this account yet")
        return False

    try:
        index = 0
        while True:
            try:
                subkey_name = winreg.EnumKey(root, index)
            except OSError:
                break
            index += 1
            try:
                sub = winreg.OpenKey(root, subkey_name, 0, winreg.KEY_READ | winreg.KEY_SET_VALUE)
            except OSError:
                continue
            try:
                exe_path, _ = winreg.QueryValueEx(sub, "ExecutablePath")
                if _path_tail(str(exe_path)) != target:
                    continue
                try:
                    current, _ = winreg.QueryValueEx(sub, "IsPromoted")
                except OSError:
                    current = 0
                if int(current) == 1:
                    if log:
                        log("OBS tray icon already pinned")
                    return True
                winreg.SetValueEx(sub, "IsPromoted", 0, winreg.REG_DWORD, 1)
                if log:
                    log("OBS tray icon pinned to the taskbar")
                return True
            except OSError:
                continue
            finally:
                sub.Close()
    finally:
        root.Close()

    if log:
        log("OBS tray icon not pinned: no matching entry yet (run OBS at least once first)")
    return False


def unpin_obs_tray_icon(log: LogFn = None) -> bool:
    """sets IsPromoted=0, restoring windows default overflow behaviour; same match/no-op rules as pin_obs_tray_icon."""
    obs_exe = find_obs_exe_candidate()
    if obs_exe is None:
        return False
    target = _path_tail(str(obs_exe))

    try:
        root = winreg.OpenKey(winreg.HKEY_CURRENT_USER, _NOTIFY_ICON_SETTINGS)
    except OSError:
        return False

    try:
        index = 0
        while True:
            try:
                subkey_name = winreg.EnumKey(root, index)
            except OSError:
                break
            index += 1
            try:
                sub = winreg.OpenKey(root, subkey_name, 0, winreg.KEY_READ | winreg.KEY_SET_VALUE)
            except OSError:
                continue
            try:
                exe_path, _ = winreg.QueryValueEx(sub, "ExecutablePath")
                if _path_tail(str(exe_path)) != target:
                    continue
                winreg.SetValueEx(sub, "IsPromoted", 0, winreg.REG_DWORD, 0)
                if log:
                    log("OBS tray icon unpinned")
                return True
            except OSError:
                continue
            finally:
                sub.Close()
    finally:
        root.Close()
    return False

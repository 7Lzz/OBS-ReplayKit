"""drop OBS power requests so Windows idle monitor/system sleep can still run."""

from __future__ import annotations

import re
import subprocess
from typing import Callable, Optional

LogFn = Optional[Callable[[str], None]]

_OBS_EXE_NAME = "obs64.exe"
_REQUEST_FLAGS = ("DISPLAY", "SYSTEM", "AWAYMODE")

# suppress the powercfg console flash when spawned from a non-console parent (pyinstaller apply exe).
_CREATE_NO_WINDOW = 0x08000000


def _run_powercfg(args: list[str], log: LogFn = None) -> Optional[subprocess.CompletedProcess]:
    """subprocess.run wrapper with our standard timeout + create_no_window flags. None on oserror/timeout."""
    try:
        return subprocess.run(
            ["powercfg.exe", *args],
            capture_output=True,
            text=True,
            timeout=10,
            creationflags=_CREATE_NO_WINDOW,
        )
    except (subprocess.TimeoutExpired, OSError) as exc:
        if log:
            log(f"warn: powercfg {' '.join(args)} failed: {exc}")
        return None


def _installed_request_flags() -> set[str]:
    result = _run_powercfg(["/requestsoverride"])
    if result is None or result.returncode != 0:
        return set()
    flags: set[str] = set()
    for line in result.stdout.splitlines():
        stripped = line.strip()
        if not stripped:
            continue
        if stripped.lower().startswith(_OBS_EXE_NAME.lower()):
            tail = stripped[len(_OBS_EXE_NAME):]
            for flag in _REQUEST_FLAGS:
                if re.search(rf"\b{re.escape(flag)}\b", tail, re.IGNORECASE):
                    flags.add(flag)
    return flags


def is_sleep_override_installed() -> bool:
    """true iff all ReplayKit-managed OBS power request overrides are already in place."""
    installed = _installed_request_flags()
    return all(flag in installed for flag in _REQUEST_FLAGS)


def install_sleep_override(log: LogFn = None) -> bool:
    """ignore OBS display/system/awaymode requests so Windows sleep timers still work."""
    if is_sleep_override_installed():
        if log:
            log(f"sleep override already in place ({_OBS_EXE_NAME} -> {'/'.join(_REQUEST_FLAGS)})")
        return True

    result = _run_powercfg(
        ["/requestsoverride", "PROCESS", _OBS_EXE_NAME, *_REQUEST_FLAGS],
        log=log,
    )
    if result is None:
        return False
    if result.returncode != 0:
        err = (result.stderr or result.stdout or "").strip().splitlines()
        err_line = err[0] if err else f"exit {result.returncode}"
        if log:
            log(f"warn: powercfg /requestsoverride failed: {err_line}")
        return False

    if log:
        log(f"sleep override installed ({_OBS_EXE_NAME} -> {'/'.join(_REQUEST_FLAGS)} requests ignored)")
        log("Windows monitor and PC sleep timers can now run while OBS/ReplayKit is active.")
    return True


def remove_sleep_override(log: LogFn = None) -> bool:
    """drop the override entry. powercfg /requestsoverride PROCESS <exe> with no request-type flags after the exe name removes the entry entirely. safe to call when no override exists (powercfg exits 0)."""
    result = _run_powercfg(
        ["/requestsoverride", "PROCESS", _OBS_EXE_NAME],
        log=log,
    )
    if result is None:
        return False
    if result.returncode != 0:
        err = (result.stderr or result.stdout or "").strip().splitlines()
        err_line = err[0] if err else f"exit {result.returncode}"
        if log:
            log(f"warn: powercfg remove override failed: {err_line}")
        return False
    if log:
        log(f"sleep override removed for {_OBS_EXE_NAME}")
    return True

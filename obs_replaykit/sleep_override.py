"""drop the DISPLAY execution-state lock obs holds while recording/streaming, so monitors can sleep with obs open. SYSTEM/AWAYMODE locks stay intact so a live session isnt killed when windows idles the box. uses powercfg /requestsoverride, persisted in HKLM\\SYSTEM\\CCS\\Control\\Power\\PowerRequestOverride."""

from __future__ import annotations

import re
import subprocess
from typing import Callable, Optional

LogFn = Optional[Callable[[str], None]]

_OBS_EXE_NAME = "obs64.exe"
_DISPLAY_FLAG = "DISPLAY"

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


def is_sleep_override_installed() -> bool:
    """True iff a DISPLAY override is already in place for obs64.exe. any subprocess failure -> 'not installed' so the caller errs on the side of re-applying."""
    result = _run_powercfg(["/requestsoverride"])
    if result is None or result.returncode != 0:
        return False
    # powercfg /requestsoverride output: process obs64.exe display ... -- tolerant of whitespace and case.
    for line in result.stdout.splitlines():
        stripped = line.strip()
        if not stripped:
            continue
        if stripped.lower().startswith(_OBS_EXE_NAME.lower()):
            # match \bDISPLAY\b in the tail so we dont false-negative if someone manually combined display with system.
            tail = stripped[len(_OBS_EXE_NAME):]
            return bool(re.search(r"\bDISPLAY\b", tail, re.IGNORECASE))
    return False


def install_sleep_override(log: LogFn = None) -> bool:
    """add the DISPLAY override for obs64.exe. on powercfg failure the rest of apply continues; the user just keeps the old 'monitors stay awake while obs is open' behavior until they re-run apply elevated."""
    if is_sleep_override_installed():
        if log:
            log(f"sleep override already in place ({_OBS_EXE_NAME} -> {_DISPLAY_FLAG})")
        return True

    result = _run_powercfg(
        ["/requestsoverride", "PROCESS", _OBS_EXE_NAME, _DISPLAY_FLAG],
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
        log(f"sleep override installed ({_OBS_EXE_NAME} -> {_DISPLAY_FLAG} requests ignored)")
        log("monitors can now power off while OBS is open; replay buffer / streams")
        log("keep running (SYSTEM/AWAYMODE locks left intact).")
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

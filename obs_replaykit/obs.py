"""obs process management: locate obs64.exe, close running instances, clear crash flags, relaunch."""

import os
import shutil
import subprocess
import tempfile
import time
from pathlib import Path
from typing import Callable, Optional

from .config import OBS_CONFIG, OBS_PROCESSES, find_obs_exe_candidate

LogFn = Optional[Callable[[str], None]]

OBS_START_ARGS = (
    "--background-color=ff272a33 "
    "--default-background-color=ff272a33 "
    "--disable-direct-composition-video-overlays"
)


def find_obs_exe() -> Optional[Path]:
    """best detected OBS executable path, or None."""
    return find_obs_exe_candidate()


def close_obs(log: LogFn = None) -> int:
    """taskkill any running obs process. returns the kill count."""
    killed = 0
    for proc in OBS_PROCESSES:
        # no /T: this runs from the update flow, where this very process can be a descendant of obs64.exe (helper -> updater) -- /T walks the whole tree by parent pid regardless of job/breakaway state, so it would take the updater down with obs mid-copy. obs-browser-page.exe is reaped separately below instead.
        result = subprocess.run(
            ["taskkill", "/F", "/IM", proc], capture_output=True, text=True
        )
        if result.returncode == 0:
            if log:
                log(f"killed {proc}")
            killed += 1
    # cef renderer children outlive obs64.exe otherwise and keep plugin_config/obs-browser locked, which stalls the backup step right after this.
    subprocess.run(["taskkill", "/F", "/IM", "obs-browser-page.exe"], capture_output=True, text=True)
    if killed:
        time.sleep(1.5)  # give the process tree time to actualy exit
    return killed


def cleanup_crash_flags(log: LogFn = None) -> None:
    """remove safe_mode + .sentinel/* so obs doesnt show its crash-recovery prompt on the next launch."""
    safe_mode = OBS_CONFIG / "safe_mode"
    if safe_mode.exists():
        try:
            safe_mode.unlink()
            if log:
                log("removed safe_mode flag")
        except OSError:
            pass

    sentinel = OBS_CONFIG / ".sentinel"
    if sentinel.is_dir():
        cleared = 0
        for item in sentinel.iterdir():
            try:
                if item.is_file():
                    item.unlink()
                else:
                    shutil.rmtree(item, ignore_errors=True)
                cleared += 1
            except OSError:
                pass
        if cleared and log:
            log(f"cleared .sentinel ({cleared} item(s))")


def _vbs_literal(value: str) -> str:
    return '"' + value.replace('"', '""') + '"'


def _launch_obs_via_wscript(obs_exe: Path, log: LogFn = None) -> bool:
    """start OBS from a temporary GUI-script host so native plugins cannot write to our console."""
    command = f'"{obs_exe}" {OBS_START_ARGS}'
    script = f"""Option Explicit
On Error Resume Next

Dim shell, fso, scriptPath, rc
Set shell = CreateObject("WScript.Shell")
shell.CurrentDirectory = {_vbs_literal(str(obs_exe.parent))}
rc = 0
shell.Run {_vbs_literal(command)}, 1, False
If Err.Number <> 0 Then rc = 1

scriptPath = WScript.ScriptFullName
Set fso = CreateObject("Scripting.FileSystemObject")
fso.DeleteFile scriptPath, True

WScript.Quit rc
"""
    try:
        with tempfile.NamedTemporaryFile(
            mode="w",
            encoding="utf-16",
            suffix=".vbs",
            prefix="obsreplaykit_launch_",
            delete=False,
        ) as handle:
            handle.write(script)
            script_path = Path(handle.name)
    except OSError as exc:
        if log:
            log(f"failed to prepare OBS launcher: {exc}")
        return False

    flags = getattr(subprocess, "CREATE_NO_WINDOW", 0) | getattr(subprocess, "DETACHED_PROCESS", 0)
    try:
        subprocess.Popen(
            ["wscript.exe", "//B", str(script_path)],
            cwd=tempfile.gettempdir(),
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            close_fds=True,
            creationflags=flags,
        )
        return True
    except Exception as exc:
        try:
            script_path.unlink(missing_ok=True)
        except OSError:
            pass
        if log:
            log(f"failed to launch OBS through detached helper: {exc}")
        return False


def launch_obs(log: LogFn = None) -> bool:
    """launch OBS without attaching it to the setup console."""
    obs_exe = find_obs_exe()
    if obs_exe is None:
        if log:
            log("OBS install was not found - install OBS, set OBS_REPLAYKIT_OBS_EXE, or re-run from an active OBS install.")
        return False

    if log:
        log(f"starting {obs_exe}")

    if os.name == "nt":
        return _launch_obs_via_wscript(obs_exe, log=log)

    try:
        subprocess.Popen(
            [str(obs_exe), *OBS_START_ARGS.split()],
            cwd=str(obs_exe.parent),
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            close_fds=True,
        )
        return True
    except Exception as exc:
        if log:
            log(f"failed to launch OBS: {exc}")
        return False

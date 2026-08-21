"""register the OBSReplayKit-Elevate task scheduler entry so the in-obs lua can relaunch obs elevated without a uac popup on every launch."""

from __future__ import annotations

import os
import subprocess
import tempfile
from pathlib import Path
from typing import Callable, Optional

from .config import OBS_CONFIG

LogFn = Optional[Callable[[str], None]]


# public so the lua script + any later uninstaller share the exact name. renaming this orphans existing installs.
TASK_NAME = "OBSReplayKit-Elevate"

# where the relauncher vbs lives after install_obs_config runs. the task action points here; the vbs reads its runtime args from %temp%\obsreplaykit_elevate.txt when invoked without arguments.
_RELAUNCHER_VBS = OBS_CONFIG / "obs-replayKit" / "scripts" / "obs_elevation" / "hidden_relauncher.vbs"


# task xml notes: runlevel=highestavailable elevates only for users already in administrators (no fake elevation for standard users); empty <triggers/> + allowstartondemand makes it on-demand only; hidden=true keeps it out of the defualt task scheduler view; //b suppresses wscripts error popups; the xml MUST be utf-16 with bom or schtasks /xml rejects it with a misleading "incorrectly formatted or out of range" error.
_TASK_XML_TEMPLATE = """\
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>OBS ReplayKit -- elevated OBS relaunch helper (UAC-free per-launch)</Description>
    <Author>OBS ReplayKit</Author>
  </RegistrationInfo>
  <Principals>
    <Principal id="Author">
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>true</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT5M</ExecutionTimeLimit>
    <Priority>4</Priority>
  </Settings>
  <Triggers/>
  <Actions Context="Author">
    <Exec>
      <Command>wscript.exe</Command>
      <Arguments>//B "{vbs_path}"</Arguments>
    </Exec>
  </Actions>
</Task>
"""


def install_elevation_task(log: LogFn = None) -> bool:
    """create or overwrite the OBSReplayKit-Elevate scheduled task. on any failure the lua falls back to shellexecuteex+uac, so a False here just means the user keeps seeing a per-launch popup."""
    if not _RELAUNCHER_VBS.is_file():
        if log:
            log(
                f"warn: {_RELAUNCHER_VBS.name} not present at {_RELAUNCHER_VBS}; "
                "elevation task install skipped (Lua will fall back to per-launch UAC)"
            )
        return False

    xml = _TASK_XML_TEMPLATE.format(vbs_path=str(_RELAUNCHER_VBS))

    fd, xml_path_str = tempfile.mkstemp(prefix="obsreplaykit_task_", suffix=".xml")
    os.close(fd)
    xml_path = Path(xml_path_str)
    try:
        # utf-16 (not utf-16-le) so the bom schtasks looks for is included.
        xml_path.write_text(xml, encoding="utf-16")

        # /f overwrites an existing task so re-apply is idempotent. create_no_window suppresses the schtasks console flash.
        try:
            result = subprocess.run(
                [
                    "schtasks.exe",
                    "/Create",
                    "/TN", TASK_NAME,
                    "/XML", str(xml_path),
                    "/F",
                ],
                capture_output=True,
                text=True,
                timeout=20,
                creationflags=0x08000000,  # create_no_window
            )
        except (subprocess.TimeoutExpired, OSError) as exc:
            if log:
                log(f"warn: schtasks /Create failed: {exc}")
            return False

        if result.returncode != 0:
            err = (result.stderr or result.stdout or "").strip().splitlines()
            err_line = err[0] if err else f"exit {result.returncode}"
            if log:
                log(f"warn: schtasks /Create returned {result.returncode}: {err_line}")
            return False

        if log:
            log(f"installed scheduled task '{TASK_NAME}' (no per-launch UAC popup)")
        return True
    finally:
        try:
            xml_path.unlink()
        except OSError:
            pass


def is_elevation_task_installed() -> bool:
    """cheap pre-check so an idempotent re-apply can skip the install. any schtasks failure is treated as 'not installed'."""
    try:
        result = subprocess.run(
            ["schtasks.exe", "/Query", "/TN", TASK_NAME],
            capture_output=True,
            text=True,
            timeout=10,
            creationflags=0x08000000,  # create_no_window
        )
    except (subprocess.TimeoutExpired, OSError):
        return False
    return result.returncode == 0


def delete_elevation_task(log: LogFn = None) -> bool:
    """delete the ReplayKit scheduled task. safe when the task is not present."""
    try:
        result = subprocess.run(
            ["schtasks.exe", "/Delete", "/TN", TASK_NAME, "/F"],
            capture_output=True,
            text=True,
            timeout=20,
            creationflags=0x08000000,
        )
    except (subprocess.TimeoutExpired, OSError) as exc:
        if log:
            log(f"warn: schtasks /Delete failed: {exc}")
        return False
    if result.returncode == 0:
        if log:
            log(f"removed scheduled task '{TASK_NAME}'")
        return True
    text = (result.stderr or result.stdout or "").lower()
    if "cannot find" in text or "does not exist" in text:
        return True
    if log:
        err = (result.stderr or result.stdout or "").strip().splitlines()
        log(f"warn: schtasks /Delete returned {result.returncode}: {err[0] if err else ''}")
    return False

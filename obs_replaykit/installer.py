"""install bundled obs config into %appdata%/obs-studio/. walks assets/obs-studio/ as a 1:1 mirror, pipes text files thru rewrite_user_paths + apply_preferences before writing."""

import shutil
import subprocess
from datetime import datetime
from pathlib import Path
from typing import Callable, Optional

from .audio import find_render_endpoint
from .config import BUNDLE_ROOT, OBS_ASSETS_DIR, OBS_CONFIG, TEXT_EXTS, USERNAME, USERPROFILE
from .dock import verify_dock_install
from .ffmpeg_install import install_ffmpeg as _install_ffmpeg
from .pathrewrite import rewrite_user_paths
from .prefs import Preferences
from .scheduled_task import install_elevation_task as _install_elevation_task
from .sleep_override import install_sleep_override as _install_sleep_override
from .transform import apply_preferences, set_ini_value
from .websocket import install_websocket_config

LogFn = Optional[Callable[[str], None]]

# Friendly name assigned to the ReplayKit audio render endpoint. Looked up by
# name because the Windows endpoint GUID changes after driver reinstall.
OBS_AUDIO_FRIENDLY_NAME = "OBS Stream Audio"

# top-level entries backed up before overwriting. obs-replayKit/ covers the whole replaykit-managed tree (lua scripts, dock html, presets); global.ini is merged not overwritten but cheap to back up anyway.
_BACKUP_TARGETS = ("basic", "obs-replayKit", "plugin_manager", "plugin_config", "user.ini", "global.ini")


def _install_file(src: Path, rel: Path, dst: Path, prefs: Preferences) -> None:
    """copy src to dst; text files get rewrite_user_paths + apply_preferences first."""
    dst.parent.mkdir(parents=True, exist_ok=True)
    if src.suffix.lower() in TEXT_EXTS:
        try:
            content = src.read_text(encoding="utf-8")
        except UnicodeDecodeError:
            content = src.read_text(encoding="latin-1")
        content = rewrite_user_paths(content, USERNAME)
        content = apply_preferences(rel, content, prefs)
        dst.write_text(content, encoding="utf-8")
    else:
        shutil.copy2(src, dst)


def install_obs_config(prefs: Preferences, log: LogFn = None) -> int:
    """mirror assets/obs-studio/ into %appdata%/obs-studio/. returns the file count."""
    if not OBS_ASSETS_DIR.is_dir():
        if log:
            log(f"warn: {OBS_ASSETS_DIR} not found - nothing to install")
        return 0

    count = 0
    for src in OBS_ASSETS_DIR.rglob("*"):
        if src.is_dir():
            continue
        rel = src.relative_to(OBS_ASSETS_DIR)
        dst = OBS_CONFIG / rel
        _install_file(src, rel, dst, prefs)
        if log:
            log(f"-> {rel.as_posix()}")
        count += 1
    return count


def backup_existing_config(log: LogFn = None) -> Optional[Path]:
    """copy the users current obs config to obs-studio.bak.<timestamp>. returns the backup path, or None if there was nothing to back up."""
    if not OBS_CONFIG.exists():
        return None

    present = [OBS_CONFIG / name for name in _BACKUP_TARGETS]
    present = [p for p in present if p.exists()]
    if not present:
        return None

    stamp      = datetime.now().strftime("%Y%m%d-%H%M%S")
    backup_dir = OBS_CONFIG.parent / f"obs-studio.bak.{stamp}"
    backup_dir.mkdir(parents=True, exist_ok=True)

    for src in present:
        dst = backup_dir / src.name
        try:
            if src.is_dir():
                shutil.copytree(src, dst, dirs_exist_ok=True)
            else:
                shutil.copy2(src, dst)
        except Exception as exc:
            if log:
                log(f"warn: backup of {src.name} failed: {exc}")

    if log:
        log(f"backed up existing config -> {backup_dir}")
    return backup_dir


def set_monitoring_device_to_obs_audio(log: LogFn = None) -> bool:
    """Point OBS audio monitoring at OBS Stream Audio before the first launch."""
    device_id = find_render_endpoint(OBS_AUDIO_FRIENDLY_NAME)
    if device_id is None:
        if log:
            log(f"(couldn't find render endpoint '{OBS_AUDIO_FRIENDLY_NAME}' - leaving monitoring at Default)")
        return False

    basic_ini = OBS_CONFIG / "basic" / "profiles" / "Untitled" / "basic.ini"
    if not basic_ini.is_file():
        if log:
            log(f"warn: {basic_ini} not present - skipping monitoring-device patch")
        return False

    try:
        text = basic_ini.read_text(encoding="utf-8")
    except UnicodeDecodeError:
        text = basic_ini.read_text(encoding="latin-1")

    text = set_ini_value(text, "Audio", "MonitoringDeviceId",   device_id)
    text = set_ini_value(text, "Audio", "MonitoringDeviceName", OBS_AUDIO_FRIENDLY_NAME)
    basic_ini.write_text(text, encoding="utf-8")

    if log:
        log(f"set monitoring device -> {OBS_AUDIO_FRIENDLY_NAME}")
        log(f"    id: {device_id}")
    return True


def ensure_recording_dirs(prefs: Preferences, log: LogFn = None) -> None:
    """make sure the recording output folder exists. ~/videos is also created as the simple-output fallback obs reverts to if a profile is reset."""
    targets = {Path(prefs.recording_path), USERPROFILE / "Videos"}
    for target in targets:
        try:
            target.mkdir(parents=True, exist_ok=True)
            if log:
                log(f"ok: {target}")
        except OSError as exc:
            if log:
                log(f"warn: could not create {target}: {exc}")


def install_obs_custom_dock(log: LogFn = None) -> int:
    """sanity-check the dock html survived the main walker. the walker already mirror-copies it; this just flags missing files loudly."""
    return verify_dock_install(log=log)


def install_obs_elevation_task(log: LogFn = None) -> bool:
    """register the obsreplaykit-elevate scheduled task so the lua elevation script can relaunch obs elevated without a per-launch uac popup. must run AFTER install_obs_config copies hidden_relauncher.vbs into place."""
    return _install_elevation_task(log=log)


def install_obs_ffmpeg(log: LogFn = None) -> bool:
    """download + drop ffmpeg.exe and ffprobe.exe next to the helper. obs ships only obs-ffmpeg-mux.exe; compress/trim need the full pair. idempotent."""
    return _install_ffmpeg(log=log)


def configure_obs_websocket(log: LogFn = None) -> bool:
    """enable obs-websocket on port 4455 (no auth, loopback) so the save replay dock button can drive obs."""
    return install_websocket_config(log=log)


def install_obs_sleep_override(log: LogFn = None) -> bool:
    """let monitors sleep while obs is open. obs holds a DISPLAY execution-state lock while replay buffer / streams / vcam are running; we suppress just the display lock for obs64.exe and keep SYSTEM/AWAYMODE intact. idempotent; Clean reset removes the entry."""
    return _install_sleep_override(log=log)


_LAUNCHER_REL = Path("obs-studio/obs-replayKit/scripts/streamable/OBSReplayKit.exe")


def ensure_launcher_built(log: LogFn = None) -> bool:
    """make sure the bundled OBSReplayKit.exe launcher is on disk; compile it from utils/launcher/build_launcher.ps1 if not. returning False is non-fatal -- the lua falls back to plain powershell.exe, just without the obs-replaykit branding in task manager."""
    launcher_path = OBS_ASSETS_DIR / "obs-replayKit" / "scripts" / "streamable" / "OBSReplayKit.exe"
    if launcher_path.is_file():
        return True

    build_script = BUNDLE_ROOT / "utils" / "launcher" / "build_launcher.ps1"
    if not build_script.is_file():
        if log:
            log(f"warn: launcher missing and build_launcher.ps1 not present at {build_script} - dock will run as plain powershell.exe")
        return False

    if log:
        log(f"launcher EXE missing; compiling from {build_script.name} ...")
    try:
        result = subprocess.run(
            [
                "powershell.exe",
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                str(build_script),
                "-OutPath",
                str(launcher_path),
            ],
            capture_output=True,
            text=True,
            timeout=60,
        )
    except (subprocess.TimeoutExpired, OSError) as exc:
        if log:
            log(f"warn: launcher compile failed: {exc}")
        return False

    if result.returncode != 0 or not launcher_path.is_file():
        if log:
            log(f"warn: launcher compile exited {result.returncode}; stderr: {result.stderr.strip()[:240]}")
        return False

    if log:
        size_kb = max(1, launcher_path.stat().st_size // 1024)
        log(f"launcher built: {launcher_path.name} ({size_kb} KB)")
    return True

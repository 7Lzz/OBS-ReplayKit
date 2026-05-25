"""install bundled obs config into %appdata%/obs-studio/. walks assets/obs-studio/ as a 1:1 mirror, pipes text files thru rewrite_user_paths + apply_preferences before writing."""

import copy
import shutil
import subprocess
import json
from datetime import datetime
from pathlib import Path
from typing import Any, Callable, Optional

from . import __version__
from .audio import find_replaykit_monitoring_endpoint
from .config import BUNDLE_ROOT, OBS_ASSETS_DIR, OBS_CONFIG, REPLAYKIT_CONFIG, TEXT_EXTS, USERNAME, USERPROFILE
from .dock import verify_dock_install
from .ffmpeg_install import install_ffmpeg as _install_ffmpeg
from .pathrewrite import rewrite_user_paths
from .prefs import Preferences
from .scheduled_task import install_elevation_task as _install_elevation_task
from .sleep_override import install_sleep_override as _install_sleep_override
from .shaderfilter import install_replaykit_motion_blur_plugin
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


def write_replaykit_version(log: LogFn = None) -> Path:
    """Write the installed ReplayKit runtime version used by the updater."""
    path = REPLAYKIT_CONFIG / "version.json"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps({"version": __version__}, indent=2) + "\n", encoding="utf-8")
    if log:
        log(f"ReplayKit version -> {__version__}")
    return path


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
    write_replaykit_version(log=log)
    cleanup_replaykit_legacy_files(log=log)
    return count


_RUNTIME_PRESERVE_RELS = {
    Path("obs-replayKit/scripts/replaykit_settings.json"),
}

_RUNTIME_DELETE_RELS = {
    Path("obs-replayKit/scripts/replay_buffer/replay_buffer_saved.mp3"),
}


# scene-source patches applied during auto-update so source-property fixes (eg. flipping
# capture_audio on Game Capture, adjusting monitoring_type) land on existing users without a fresh
# setup run. each key is a source name as it appears in basic/scenes/Untitled.json; each value is a
# flat map of dotted-path -> value. only the listed fields are touched; everything else on that
# source (the users uuid, their volume slider, hotkeys, custom filters, etc.) is left alone. add
# entries here when shipping a release that needs to change a source property on existing installs.
_SCENE_SOURCE_PATCHES: dict[str, dict[str, Any]] = {
    # empty for now. example for the next release:
    #   "Game Capture": { "settings.capture_audio": False },
    #   "Desktop Audio (excl. Discord)": { "monitoring_type": 0 },
}

# scene path inside %APPDATA%\obs-studio\ that gets patched. mirrors what install_obs_config writes
# on fresh setups.
_USER_SCENE_REL = Path("basic/scenes/Untitled.json")


def cleanup_replaykit_legacy_files(log: LogFn = None) -> int:
    """Remove ReplayKit-managed files that were renamed or retired."""
    removed = 0
    for rel in _RUNTIME_DELETE_RELS:
        target = OBS_CONFIG / rel
        try:
            if target.exists() and target.is_file():
                target.unlink()
                removed += 1
                if log:
                    log(f"removed legacy: {rel.as_posix()}")
        except OSError as exc:
            if log:
                log(f"warn: could not remove legacy {rel.as_posix()}: {exc}")
    return removed


def _set_nested(target: dict[str, Any], dotted: str, value: Any) -> None:
    """Write value into target at a dotted path, creating intermediate dicts as needed."""
    parts = dotted.split(".")
    cursor = target
    for part in parts[:-1]:
        existing = cursor.get(part)
        if not isinstance(existing, dict):
            existing = {}
            cursor[part] = existing
        cursor = existing
    cursor[parts[-1]] = value


def apply_scene_patches(log: LogFn = None) -> int:
    """Patch known ReplayKit-managed sources in the user's scene file in place.

    Walks _SCENE_SOURCE_PATCHES, finds matching sources by name, and overwrites only the listed
    fields. Returns the number of sources patched. Safe to call when the file is missing or has no
    matches -- returns 0 silently. Skips writing if nothing actually changed so we don't bump the
    mtime when there's no work to do.
    """
    if not _SCENE_SOURCE_PATCHES:
        return 0
    scene_path = OBS_CONFIG / _USER_SCENE_REL
    if not scene_path.is_file():
        return 0
    try:
        data = json.loads(scene_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        if log:
            log(f"warn: cannot parse scene file for patches: {exc}")
        return 0

    patched = 0
    sources = data.get("sources")
    if not isinstance(sources, list):
        return 0

    for source in sources:
        if not isinstance(source, dict):
            continue
        name = source.get("name", "")
        spec = _SCENE_SOURCE_PATCHES.get(name)
        if not spec:
            continue
        before = copy.deepcopy(source)
        for dotted, value in spec.items():
            _set_nested(source, dotted, value)
        if source != before:
            patched += 1
            if log:
                log(f"scene patch -> {name}: {list(spec.keys())}")

    if patched > 0:
        try:
            scene_path.write_text(json.dumps(data, indent=4) + "\n", encoding="utf-8")
        except OSError as exc:
            if log:
                log(f"warn: scene patch save failed: {exc}")
            return 0
    return patched


def _install_runtime_file(src: Path, rel: Path, dst: Path) -> None:
    """Copy one ReplayKit runtime file for update mode without applying user prefs."""
    dst.parent.mkdir(parents=True, exist_ok=True)
    if src.suffix.lower() in TEXT_EXTS:
        try:
            content = src.read_text(encoding="utf-8")
        except UnicodeDecodeError:
            content = src.read_text(encoding="latin-1")
        dst.write_text(rewrite_user_paths(content, USERNAME), encoding="utf-8")
    else:
        shutil.copy2(src, dst)


def install_replaykit_runtime_update(log: LogFn = None) -> int:
    """Refresh only ReplayKit-managed runtime files, preserving user OBS config/state.

    Coverage:
      - All files under obs-replayKit/ get copied/overwritten.
      - replaykit_settings.json is preserved (user values stick, new defaults auto-populate via
        Get-DefaultReplayKitSettings on next helper load).
      - Files in _RUNTIME_DELETE_RELS are removed (rename/retire path).
      - Source-property fixes in basic/scenes/Untitled.json land via _SCENE_SOURCE_PATCHES so
        existing users get scene tweaks (capture_audio flips, monitoring_type adjustments, etc.)
        without re-running the full installer.
      - Idempotent driver installs (ffmpeg, elevation task) are re-run so newly added or
        replaced helper-side dependencies show up on existing installs.
    """
    runtime_src = OBS_ASSETS_DIR / "obs-replayKit"
    if not runtime_src.is_dir():
        raise FileNotFoundError(f"ReplayKit runtime assets not found: {runtime_src}")

    count = 0
    for src in runtime_src.rglob("*"):
        if src.is_dir():
            continue
        rel = Path("obs-replayKit") / src.relative_to(runtime_src)
        dst = OBS_CONFIG / rel
        if rel in _RUNTIME_PRESERVE_RELS and dst.exists():
            if log:
                log(f"preserve: {rel.as_posix()}")
            continue
        _install_runtime_file(src, rel, dst)
        if log:
            log(f"-> {rel.as_posix()}")
        count += 1

    write_replaykit_version(log=log)
    cleanup_replaykit_legacy_files(log=log)

    # scene patches and driver re-runs are best-effort. an exception here must not abort the
    # update -- the runtime files are already on disk and obs will relaunch with them.
    try:
        patched = apply_scene_patches(log=log)
        if patched and log:
            log(f"scene patches applied: {patched}")
    except Exception as exc:
        if log:
            log(f"warn: scene patch pass failed: {exc}")

    try:
        run_update_driver_refresh(log=log)
    except Exception as exc:
        if log:
            log(f"warn: driver refresh pass failed: {exc}")

    return count


def run_update_driver_refresh(log: LogFn = None) -> None:
    """Re-run idempotent driver/installer functions during an auto-update.

    Each underlying installer short-circuits when its target is already present (ffmpeg checks
    file hashes; elevation task checks schtasks). New releases can plug in additional driver/
    plugin installs here and existing users will pick them up on their next auto-update.
    """
    try:
        install_obs_ffmpeg(log=log)
    except Exception as exc:
        if log:
            log(f"warn: ffmpeg refresh skipped: {exc}")

    try:
        install_replaykit_motion_blur_plugin(log=log)
    except Exception as exc:
        if log:
            log(f"warn: OBS Shaderfilter refresh skipped: {exc}")

    try:
        install_obs_elevation_task(log=log)
    except Exception as exc:
        if log:
            log(f"warn: elevation task refresh skipped: {exc}")


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
    device = find_replaykit_monitoring_endpoint()
    if device is None:
        if log:
            log(f"(couldn't find an active '{OBS_AUDIO_FRIENDLY_NAME}' render endpoint - leaving monitoring at Default)")
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

    text = set_ini_value(text, "Audio", "MonitoringDeviceId",   device.device_id)
    text = set_ini_value(text, "Audio", "MonitoringDeviceName", device.name)
    basic_ini.write_text(text, encoding="utf-8")

    if log:
        log(f"set monitoring device -> {device.name}")
        log(f"    id: {device.device_id}")
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

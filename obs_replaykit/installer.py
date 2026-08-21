"""install bundled obs config into %appdata%/obs-studio/. walks assets/obs-studio/ as a 1:1 mirror, pipes text files thru rewrite_user_paths + apply_preferences before writing."""

import copy
import shutil
import subprocess
import json
import sys
import time
from datetime import datetime
from pathlib import Path
from typing import Any, Callable, Optional

from . import __version__
from .config import (
    BUNDLE_ROOT,
    OBS_ASSETS_DIR,
    OBS_CONFIG,
    REPLAYKIT_CONFIG,
    REPLAYKIT_SETUP_EXE,
    REPLAYKIT_USER_STATE_CACHE,
    TEXT_EXTS,
    USERNAME,
    USERPROFILE,
)
from .dock import verify_dock_install
from .ffmpeg_install import install_ffmpeg as _install_ffmpeg
from .pathrewrite import rewrite_user_paths
from .prefs import Preferences
from .scheduled_task import install_elevation_task as _install_elevation_task
from .sleep_override import install_sleep_override as _install_sleep_override
from .sleep_override import remove_sleep_override as _remove_sleep_override
from .shaderfilter import install_replaykit_motion_blur_plugin
from .transform import apply_preferences
from .websocket import install_websocket_config

LogFn = Optional[Callable[[str], None]]

# top-level entries backed up before overwriting. obs-replayKit/ covers the whole replaykit-managed tree (lua scripts, dock html, presets); global.ini is merged not overwritten but cheap to back up anyway.
_BACKUP_TARGETS = ("basic", "obs-replayKit", "plugin_manager", "plugin_config", "user.ini", "global.ini")

# scene path inside %APPDATA%\obs-studio\ transformed on fresh installs and patched on runtime updates -- when it already exists, the full installer uses that live file as the transform input so user-added filters and source state survive reinstall/repair installs.
_USER_SCENE_REL = Path("basic/scenes/Untitled.json")


def _read_text_file(path: Path) -> str:
    try:
        return path.read_text(encoding="utf-8")
    except UnicodeDecodeError:
        return path.read_text(encoding="latin-1")


def _install_text_source(src: Path, rel: Path, dst: Path) -> str:
    if rel == _USER_SCENE_REL and dst.is_file():
        try:
            content = _read_text_file(dst)
            json.loads(content)
            return content
        except (OSError, UnicodeDecodeError, json.JSONDecodeError):
            pass
    return _read_text_file(src)


def _write_with_retry(write_fn: Callable[[], None]) -> None:
    """retry a write for a few seconds on PermissionError -- during an update, the outgoing helper process can still hold its own exe file open for a moment after obs exits, since it notices obs is gone and self-exits asynchronously rather than at that same instant. an update-initiating caller running old code may also be racing this on its own (see 63_update.ps1s wait-pid), so this retry is the backstop that holds regardless of what triggered the update."""
    deadline = time.monotonic() + 5.0
    while True:
        try:
            write_fn()
            return
        except PermissionError:
            if time.monotonic() >= deadline:
                raise
            time.sleep(0.25)


def _install_file(src: Path, rel: Path, dst: Path, prefs: Preferences) -> None:
    """copy src to dst; text files get rewrite_user_paths + apply_preferences first."""
    dst.parent.mkdir(parents=True, exist_ok=True)
    if src.suffix.lower() in TEXT_EXTS:
        content = _install_text_source(src, rel, dst)
        content = rewrite_user_paths(content, USERNAME)
        content = apply_preferences(rel, content, prefs)
        _write_with_retry(lambda: dst.write_text(content, encoding="utf-8"))
    else:
        _write_with_retry(lambda: shutil.copy2(src, dst))


def write_replaykit_version(log: LogFn = None) -> Path:
    """write the installed ReplayKit runtime version used by the updater."""
    path = REPLAYKIT_CONFIG / "version.json"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps({"version": __version__}, indent=2) + "\n", encoding="utf-8")
    if log:
        log(f"ReplayKit version -> {__version__}")
    return path


def cache_setup_executable(log: LogFn = None) -> bool:
    source = Path(sys.executable).resolve() if getattr(sys, "frozen", False) else BUNDLE_ROOT / "OBSReplayKit.exe"
    if not source.is_file():
        if log:
            log("warn: setup executable was not cached for in-app cleanup")
        return False
    target = REPLAYKIT_SETUP_EXE
    try:
        target.parent.mkdir(parents=True, exist_ok=True)
        if source.resolve(strict=False) != target.resolve(strict=False):
            shutil.copy2(source, target)
        if log:
            log(f"cached setup executable -> {target}")
        return True
    except OSError as exc:
        if log:
            log(f"warn: setup executable cache failed: {exc}")
        return False


def install_obs_config(prefs: Preferences, log: LogFn = None) -> int:
    """mirror assets/obs-studio/ into %appdata%/obs-studio/. returns the file count."""
    if not OBS_ASSETS_DIR.is_dir():
        if log:
            log(f"warn: {OBS_ASSETS_DIR} not found - nothing to install")
        return 0

    _migrate_legacy_streamable_state(log=log)

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
    cache_setup_executable(log=log)
    cleanup_replaykit_legacy_files(log=log)
    restore_replaykit_user_state(log=log)
    return count


_RUNTIME_PRESERVE_RELS = {
    Path("obs-replayKit/scripts/replaykit_settings.json"),
    Path("obs-replayKit/scripts/helper/clips_db.json"),
    Path("obs-replayKit/scripts/helper/clips_index.json"),
}

_RUNTIME_DELETE_RELS = {
    Path("obs-replayKit/scripts/replay_buffer/replay_buffer_saved.mp3"),
    Path("obs-replayKit/scripts/audio/auto_pick_monitor_device.lua"),
}

_RUNTIME_STATE_RELS = (
    Path("obs-replayKit/scripts/helper/clips_db.json"),
    Path("obs-replayKit/scripts/helper/clips_index.json"),
)

# installs made before the streamable/ -> helper/ rename: source name (in the old directory) -> new relative path.
_LEGACY_STREAMABLE_DIR_REL = Path("obs-replayKit/scripts/streamable")
_LEGACY_STREAMABLE_STATE_RELS = {
    "clips_db.json": Path("obs-replayKit/scripts/helper/clips_db.json"),
    "clips_index.json": Path("obs-replayKit/scripts/helper/clips_index.json"),
    "helper_config.json": Path("obs-replayKit/scripts/helper/helper_config.json"),
}


def _migrate_legacy_streamable_state(log: LogFn = None) -> None:
    """one-time migration for installs made before the streamable/ directory was renamed to helper/: carries the live clip db/index/helper-config forward to their new path, then removes the old directory tree. no-ops once the old directory is gone, so its safe to call on every install."""
    old_dir = OBS_CONFIG / _LEGACY_STREAMABLE_DIR_REL
    if not old_dir.is_dir():
        return
    for name, new_rel in _LEGACY_STREAMABLE_STATE_RELS.items():
        old_file = old_dir / name
        new_file = OBS_CONFIG / new_rel
        if old_file.is_file() and not new_file.exists():
            try:
                new_file.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(old_file, new_file)
                if log:
                    log(f"migrated legacy state: {name} -> {new_rel.as_posix()}")
            except OSError as exc:
                if log:
                    log(f"warn: could not migrate legacy {name}: {exc}")
    try:
        shutil.rmtree(old_dir)
        if log:
            log(f"removed legacy directory: {_LEGACY_STREAMABLE_DIR_REL.as_posix()}")
    except OSError as exc:
        if log:
            log(f"warn: could not remove legacy directory {_LEGACY_STREAMABLE_DIR_REL.as_posix()}: {exc}")


# scene-source patches applied during auto-update so property fixes (eg. flipping capture_audio on Game Capture) land on existing users without a fresh setup run -- each key is a source name in Untitled.json, each value a flat dotted-path -> value map, and only the listed fields are touched, everything else on that source is left alone. add entries here when a release needs to change a source property on existing installs.
_SCENE_SOURCE_PATCHES: dict[str, dict[str, Any]] = {
    "Game Capture": {
        "settings.capture_audio": False,
        "settings.hook_rate": 2,
        "settings.limit_framerate": True,
    },
    "Audio Input Capture": {
        "monitoring_type": 0,
    },
    "Desktop Audio (excl. Discord)": {
        "monitoring_type": 0,
    },
    "Discord Audio (record only)": {
        "monitoring_type": 0,
    },
}

def cleanup_replaykit_legacy_files(log: LogFn = None) -> int:
    """remove ReplayKit-managed files that were renamed or retired."""
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


def restore_replaykit_user_state(log: LogFn = None) -> int:
    """restore user-owned clip state after an uninstall/reinstall that kept settings."""
    restored = 0
    for rel in _RUNTIME_STATE_RELS:
        source = REPLAYKIT_USER_STATE_CACHE / rel.name
        target = OBS_CONFIG / rel
        if not source.is_file() or target.exists():
            continue
        try:
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, target)
            restored += 1
            if log:
                log(f"restored state: {rel.as_posix()}")
        except OSError as exc:
            if log:
                log(f"warn: could not restore state {rel.as_posix()}: {exc}")
    return restored


def _set_nested(target: dict[str, Any], dotted: str, value: Any) -> None:
    """write value into target at a dotted path, creating intermediate dicts as needed."""
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
    """patch known ReplayKit-managed sources in the users scene file in place, walking _SCENE_SOURCE_PATCHES and overwriting only the listed fields; safe when the file is missing or has no matches (returns 0), and skips writing if nothing changed so the mtime doesnt bump for no reason."""
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
    """copy one ReplayKit runtime file for update mode without applying user prefs."""
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
    """refresh only ReplayKit-managed runtime files while preserving user obs config/state: copies everything under obs-replayKit/, preserves replaykit_settings.json, removes retired files, applies _SCENE_SOURCE_PATCHES to the scene file, and re-runs idempotent tool installs (ffmpeg, elevation task) so existing installs pick up new dependencies."""
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
    cache_setup_executable(log=log)
    cleanup_replaykit_legacy_files(log=log)
    restore_replaykit_user_state(log=log)

    # scene patches and tool refreshes are best-effort -- an exception here must not abort the update, since the runtime files are already on disk and obs will relaunch with them.
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
            log(f"warn: tool refresh pass failed: {exc}")

    return count


def run_update_driver_refresh(log: LogFn = None) -> None:
    """re-run idempotent installer functions during an auto-update; each one short-circuits when its target is already present (ffmpeg checks file hashes, elevation task checks schtasks), so new releases can plug in additional plugin installs here and existing users pick them up next auto-update."""
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

    try:
        from .prefs import load_prefs

        prefs = load_prefs()
        install_obs_sleep_override(bool(getattr(prefs, "allow_sleep_while_active", True)), log=log)
    except Exception as exc:
        if log:
            log(f"warn: sleep override refresh skipped: {exc}")

    try:
        from .prefs import load_prefs
        from .tray_pin import pin_obs_tray_icon, unpin_obs_tray_icon

        prefs = load_prefs()
        if bool(getattr(prefs, "pin_obs_tray_icon", True)):
            pin_obs_tray_icon(log=log)
        else:
            unpin_obs_tray_icon(log=log)
    except Exception as exc:
        if log:
            log(f"warn: tray icon pin refresh skipped: {exc}")


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
    """enable obs-websocket on port 6455 (no auth, loopback) so the save replay dock button can drive obs."""
    return install_websocket_config(log=log)


def install_obs_sleep_override(allow_sleep: bool = True, log: LogFn = None) -> bool:
    """let Windows monitor/system sleep timers run while OBS is active, or restore OBS defaults."""
    if allow_sleep:
        return _install_sleep_override(log=log)
    return _remove_sleep_override(log=log)


_LAUNCHER_REL = Path("obs-studio/obs-replayKit/scripts/helper/OBSReplayKit.exe")


def ensure_launcher_built(log: LogFn = None) -> bool:
    """make sure the bundled OBSReplayKit.exe launcher is on disk; compile it from utils/launcher/build_launcher.ps1 if not. returning False is non-fatal -- the lua falls back to plain powershell.exe, just without the obs-replaykit branding in task manager."""
    launcher_path = OBS_ASSETS_DIR / "obs-replayKit" / "scripts" / "helper" / "OBSReplayKit.exe"
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

"""Install the bundled OBS Shaderfilter plugin used by ReplayKit motion blur."""

from __future__ import annotations

import hashlib
import shutil
import zipfile
from pathlib import Path, PurePosixPath
from typing import Callable, Optional

from .config import PROGRAMDATA, PROGRAMFILES_OBS_DIR, SHADERFILTER_ZIP, SHADERFILTER_ZIP_SHA256


LogFn = Optional[Callable[[str], None]]

_PLUGIN_DLL_REL = PurePosixPath("obs-plugins/64bit/obs-shaderfilter.dll")
_PLUGIN_PDB_REL = PurePosixPath("obs-plugins/64bit/obs-shaderfilter.pdb")
_PLUGIN_DATA_REL = PurePosixPath("data/obs-plugins/obs-shaderfilter")

_PLUGIN_DLL = PROGRAMFILES_OBS_DIR / Path(*_PLUGIN_DLL_REL.parts)
_PLUGIN_DATA = PROGRAMFILES_OBS_DIR / Path(*_PLUGIN_DATA_REL.parts)
_MOTION_BLUR_SHADER = _PLUGIN_DATA / "examples" / "motion_blur.shader"

_COMPOSITE_BLUR_TARGETS = (
    PROGRAMFILES_OBS_DIR / "obs-plugins" / "64bit" / "obs-composite-blur.dll",
    PROGRAMFILES_OBS_DIR / "obs-plugins" / "64bit" / "obs-composite-blur.pdb",
    PROGRAMFILES_OBS_DIR / "data" / "obs-plugins" / "obs-composite-blur",
    PROGRAMDATA / "obs-studio" / "plugins" / "obs-composite-blur",
)


def _is_safe_relative(path: PurePosixPath) -> bool:
    return bool(path.parts) and all(part not in ("", ".", "..") for part in path.parts)


def _target_path(rel: PurePosixPath) -> Path:
    target_root = PROGRAMFILES_OBS_DIR.resolve(strict=False)
    target = (PROGRAMFILES_OBS_DIR / Path(*rel.parts)).resolve(strict=False)
    if target != target_root and target_root not in target.parents:
        raise ValueError(f"unsafe archive path: {rel}")
    return target


def _allowed_archive_member(rel: PurePosixPath) -> bool:
    rel_text = rel.as_posix()
    return (
        rel_text == _PLUGIN_DLL_REL.as_posix()
        or rel_text == _PLUGIN_PDB_REL.as_posix()
        or rel_text.startswith(_PLUGIN_DATA_REL.as_posix() + "/")
    )


def is_shaderfilter_installed() -> bool:
    return _PLUGIN_DLL.is_file() and _MOTION_BLUR_SHADER.is_file()


def _verify_archive(path: Path, log: LogFn = None) -> bool:
    digest = hashlib.sha256()
    try:
        with path.open("rb") as fh:
            while True:
                chunk = fh.read(1024 * 1024)
                if not chunk:
                    break
                digest.update(chunk)
    except OSError as exc:
        if log:
            log(f"OBS Shaderfilter archive could not be read: {exc}")
        return False
    actual = digest.hexdigest().lower()
    if actual != SHADERFILTER_ZIP_SHA256:
        if log:
            log(f"OBS Shaderfilter archive hash mismatch: {actual}")
        return False
    return True


def install_shaderfilter_plugin(log: LogFn = None) -> bool:
    """Extract OBS Shaderfilter from the bundled archive into the OBS install root."""
    if is_shaderfilter_installed():
        if log:
            log(f"OBS Shaderfilter already installed at {_PLUGIN_DLL}")
        return True
    if not PROGRAMFILES_OBS_DIR.is_dir():
        if log:
            log(f"OBS install folder was not found: {PROGRAMFILES_OBS_DIR}")
        return False
    if not SHADERFILTER_ZIP.is_file():
        if log:
            log(f"{SHADERFILTER_ZIP.name} is not bundled in assets; skipping OBS Shaderfilter")
        return False
    if not _verify_archive(SHADERFILTER_ZIP, log=log):
        return False

    if log:
        log(f"extracting {SHADERFILTER_ZIP.name} -> {PROGRAMFILES_OBS_DIR}")

    try:
        with zipfile.ZipFile(SHADERFILTER_ZIP) as zf:
            for entry in zf.infolist():
                name = entry.filename.replace("\\", "/").strip("/")
                if not name:
                    continue
                rel = PurePosixPath(name)
                if not _is_safe_relative(rel):
                    raise ValueError(f"unsafe archive path: {entry.filename}")
                if not _allowed_archive_member(rel):
                    continue
                dest = _target_path(rel)
                if entry.is_dir():
                    dest.mkdir(parents=True, exist_ok=True)
                    continue
                dest.parent.mkdir(parents=True, exist_ok=True)
                with zf.open(entry) as src, dest.open("wb") as dst:
                    shutil.copyfileobj(src, dst)
    except Exception as exc:
        if log:
            log(f"OBS Shaderfilter extraction failed: {exc}")
        return False

    if not _PLUGIN_DLL.is_file():
        if log:
            log(f"OBS Shaderfilter extraction finished but {_PLUGIN_DLL} is missing")
        return False
    if not _MOTION_BLUR_SHADER.is_file():
        if log:
            log(f"OBS Shaderfilter extraction finished but {_MOTION_BLUR_SHADER} is missing")
        return False

    if log:
        log(f"installed OBS Shaderfilter -> {_PLUGIN_DLL}")
        log(f"                        + {_MOTION_BLUR_SHADER}")
    return True


def remove_composite_blur_plugin(log: LogFn = None) -> bool:
    """Remove the retired Composite Blur plugin previously used by ReplayKit."""
    ok = True
    for target in _COMPOSITE_BLUR_TARGETS:
        if not target.exists():
            continue
        try:
            if target.is_dir():
                shutil.rmtree(target)
            else:
                target.unlink()
            if log:
                log(f"removed retired Composite Blur path: {target}")
        except OSError as exc:
            ok = False
            if log:
                log(f"warn: could not remove retired Composite Blur path {target}: {exc}")
    return ok


def install_replaykit_motion_blur_plugin(log: LogFn = None) -> bool:
    """Install the current motion blur plugin and remove the retired one."""
    removed = remove_composite_blur_plugin(log=log)
    installed = install_shaderfilter_plugin(log=log)
    return removed and installed

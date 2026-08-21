"""install the Bongobs/Bango Cat OBS plugin from the bundled zip."""

from __future__ import annotations

import shutil
import zipfile
from pathlib import Path, PurePosixPath
from typing import Callable, Optional

from .config import BONGO_CAT_ZIP, PROGRAMFILES_OBS_DIR


LogFn = Optional[Callable[[str], None]]

_ZIP_ROOT = "Bango Cat/"
_PLUGIN_DLL = PROGRAMFILES_OBS_DIR / "obs-plugins" / "64bit" / "bongobs-cat.dll"
_PLUGIN_DATA = PROGRAMFILES_OBS_DIR / "bin" / "64bit" / "Bango Cat"


def _is_safe_relative(path: PurePosixPath) -> bool:
    return bool(path.parts) and all(part not in ("", ".", "..") for part in path.parts)


def _target_path(rel: PurePosixPath) -> Path:
    target_root = PROGRAMFILES_OBS_DIR.resolve(strict=False)
    target = (PROGRAMFILES_OBS_DIR / Path(*rel.parts)).resolve(strict=False)
    if target != target_root and target_root not in target.parents:
        raise ValueError(f"unsafe archive path: {rel}")
    return target


def is_bongo_cat_installed() -> bool:
    return _PLUGIN_DLL.is_file() and _PLUGIN_DATA.is_dir()


def install_bongo_cat_plugin(log: LogFn = None) -> bool:
    """extract the Bango Cat archive into the OBS install root."""
    if is_bongo_cat_installed():
        if log:
            log(f"Bongo Cat plugin already installed at {_PLUGIN_DLL}")
        return True
    if not PROGRAMFILES_OBS_DIR.is_dir():
        if log:
            log(f"OBS install folder was not found: {PROGRAMFILES_OBS_DIR}")
        return False
    if not BONGO_CAT_ZIP.is_file():
        if log:
            log(f"{BONGO_CAT_ZIP.name} is not bundled in assets; skipping Bongo Cat")
        return False

    if log:
        log(f"extracting {BONGO_CAT_ZIP.name} -> {PROGRAMFILES_OBS_DIR}")

    try:
        with zipfile.ZipFile(BONGO_CAT_ZIP) as zf:
            for entry in zf.infolist():
                name = entry.filename.replace("\\", "/")
                if not name.startswith(_ZIP_ROOT):
                    continue
                rel_text = name[len(_ZIP_ROOT):].strip("/")
                if not rel_text:
                    continue
                rel = PurePosixPath(rel_text)
                if not _is_safe_relative(rel):
                    raise ValueError(f"unsafe archive path: {entry.filename}")
                dest = _target_path(rel)
                if entry.is_dir():
                    dest.mkdir(parents=True, exist_ok=True)
                    continue
                dest.parent.mkdir(parents=True, exist_ok=True)
                with zf.open(entry) as src, dest.open("wb") as dst:
                    shutil.copyfileobj(src, dst)
    except Exception as exc:
        if log:
            log(f"Bongo Cat extraction failed: {exc}")
        return False

    if not _PLUGIN_DLL.is_file():
        if log:
            log(f"Bongo Cat extraction finished but {_PLUGIN_DLL} is missing")
        return False
    if not _PLUGIN_DATA.is_dir():
        if log:
            log(f"Bongo Cat extraction finished but {_PLUGIN_DATA} is missing")
        return False

    if log:
        log(f"installed -> {_PLUGIN_DLL}")
        log(f"           + {_PLUGIN_DATA}")
    return True

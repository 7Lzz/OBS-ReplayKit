"""install the win-capture-audio obs plugin -- powers the 'Desktop Audio (excl. Discord)' source via windows 10+ per-process loopback. without it the scenes audio_capture source cant instantiate and obs has no desktop audio. gpl-2.0 from bozbez/win-capture-audio."""

from __future__ import annotations

import shutil
import zipfile
from pathlib import Path
from typing import Callable, Optional

from .config import (
    PROGRAMFILES_OBS_DIR,
    WIN_CAPTURE_AUDIO_DLL_REL,
    WIN_CAPTURE_AUDIO_ZIP,
)

LogFn = Optional[Callable[[str], None]]


def _is_installed() -> bool:
    """true iff the win-capture-audio DLL is installed for OBS."""
    return (PROGRAMFILES_OBS_DIR / WIN_CAPTURE_AUDIO_DLL_REL).is_file()


def install_win_capture_audio(log: LogFn = None) -> bool:
    """extract the bundled plugin into the OBS install. idempotent."""
    if _is_installed():
        if log:
            log("win-capture-audio plugin already installed")
        return True

    if not WIN_CAPTURE_AUDIO_ZIP.is_file():
        if log:
            log(f"(no {WIN_CAPTURE_AUDIO_ZIP.name} bundled, skipping)")
        return False

    if not PROGRAMFILES_OBS_DIR.is_dir():
        if log:
            log(f"OBS not installed at {PROGRAMFILES_OBS_DIR} - install OBS first, then re-run")
        return False

    if log:
        log(f"extracting {WIN_CAPTURE_AUDIO_ZIP.name} -> {PROGRAMFILES_OBS_DIR}")

    try:
        with zipfile.ZipFile(WIN_CAPTURE_AUDIO_ZIP) as zf:
            zf.extractall(PROGRAMFILES_OBS_DIR)
    except PermissionError as exc:
        if log:
            log(f"permission denied writing to {PROGRAMFILES_OBS_DIR}: {exc}")
            log("(this script must run elevated -- the installer .exe self-elevates)")
        return False
    except Exception as exc:
        if log:
            log(f"failed to extract plugin: {exc}")
        return False

    if not _is_installed():
        if log:
            log(f"warn: plugin DLL not found after extraction at expected path "
                f"{PROGRAMFILES_OBS_DIR / WIN_CAPTURE_AUDIO_DLL_REL}")
        return False

    if log:
        log(f"installed -> {PROGRAMFILES_OBS_DIR / WIN_CAPTURE_AUDIO_DLL_REL}")
        log("    OBS will pick it up on next launch (Sources -> Add -> Application Audio Output Capture)")
    return True

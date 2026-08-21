"""installs the bundled replaykit-tray plugin (View Clips / Share Preview / Restart OBS tray items) -- unlike the other obs plugins here, this one is first-party: compiled from source at release time and bundled straight into assets/, nothing to download or extract."""

import shutil
from typing import Callable, Optional

from .config import REPLAYKIT_TRAY_DLL_BUNDLED, REPLAYKIT_TRAY_DLL_TARGET

LogFn = Optional[Callable[[str], None]]


def install_replaykit_tray_plugin(log: LogFn = None) -> bool:
    """copy the bundled replaykit-tray.dll into obss no-admin plugin search path; assumes obs is already closed (the apply flow closes it first), since the dll would otherwise be locked, same as updating any other loaded plugin."""
    if not REPLAYKIT_TRAY_DLL_BUNDLED.is_file():
        if log:
            log(f"(no {REPLAYKIT_TRAY_DLL_BUNDLED.name} bundled, skipping tray plugin)")
        return False

    target = REPLAYKIT_TRAY_DLL_TARGET
    try:
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(REPLAYKIT_TRAY_DLL_BUNDLED, target)
    except OSError as exc:
        if log:
            log(f"warn: could not install replaykit-tray plugin: {exc}")
        return False

    if log:
        log(f"installed -> {target}")
        log("    OBS will load it on next launch (View Clips appears in the tray menu)")
    return True

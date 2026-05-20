"""post-install sanity check that the ReplayKit browser HTML landed."""

from __future__ import annotations

from typing import Callable, Optional

from .config import DOCK_TARGET

LogFn = Optional[Callable[[str], None]]

__all__ = ("DOCK_TARGET", "verify_dock_install")


# canonical files we expect under DOCK_TARGET. anything else the walker copies is fine; this is just the sanity-check set.
_REQUIRED_FILES = ("controls.html", "controls_app.html", "clips.html")


def verify_dock_install(log: LogFn = None) -> int:
    """count how many of the canonical dock files landed. logs a warning for any missing one so an install failure shows up in apply output instead of as a confusing 'couldnt load that page' inside obs."""
    if not DOCK_TARGET.is_dir():
        if log:
            log(f"warn: {DOCK_TARGET} does not exist after install")
        return 0

    present = 0
    missing = []
    for name in _REQUIRED_FILES:
        if (DOCK_TARGET / name).is_file():
            present += 1
        else:
            missing.append(name)

    if missing and log:
        log(f"warn: dock install missing: {', '.join(missing)}")
    elif log:
        log(f"-> {DOCK_TARGET} ({present} file(s))")
    return present

"""enable obs-websocket on loopback so the docks save-replay button can drive obs. plugin ships with obs since 28; just flip server_enabled=true on port 4455 with auth off. existing auth settings are preserved."""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any, Callable, Dict, Optional

from .config import OBS_CONFIG

LogFn = Optional[Callable[[str], None]]


WEBSOCKET_CONFIG_PATH = OBS_CONFIG / "plugin_config" / "obs-websocket" / "config.json"

# shape obs writes on first run; used as the seed when no config exists yet.
_DEFAULT_CONFIG: Dict[str, Any] = {
    "alerts_enabled":  False,
    "auth_required":   False,
    "first_load":      False,
    "server_enabled":  True,
    "server_password": "",
    "server_port":     4455,
}


def _load_existing() -> Dict[str, Any]:
    """current config or {} (also {} on parse failure so a corrupted file doesnt block install)."""
    if not WEBSOCKET_CONFIG_PATH.is_file():
        return {}
    try:
        return json.loads(WEBSOCKET_CONFIG_PATH.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError, UnicodeDecodeError):
        return {}


def install_websocket_config(log: LogFn = None) -> bool:
    """ensure obs-websocket is enabled on port 4455 (or whatever the user already set). preserves auth_required if the user has already turned it on."""
    existing = _load_existing()
    merged   = dict(_DEFAULT_CONFIG)
    merged.update(existing)

    # force only what the helper depends on; everything else (port, alerts, etc.) keeps the users value.
    merged["server_enabled"] = True
    if "server_port" not in existing:
        merged["server_port"] = 4455
    # if auth is already on, leave it on. only defualt to off for fresh configs.
    if "auth_required" not in existing:
        merged["auth_required"] = False

    try:
        WEBSOCKET_CONFIG_PATH.parent.mkdir(parents=True, exist_ok=True)
        WEBSOCKET_CONFIG_PATH.write_text(
            json.dumps(merged, indent=2),
            encoding="utf-8",
        )
    except OSError as exc:
        if log:
            log(f"warn: could not write obs-websocket config: {exc}")
        return False

    if log:
        auth_note = " (auth required - helper will need the password)" if merged["auth_required"] else ""
        log(f"server_enabled=true port={merged['server_port']}{auth_note}")
    return True

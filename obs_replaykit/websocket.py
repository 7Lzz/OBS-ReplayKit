"""enable obs-websocket on loopback so the docks save-replay button can drive obs. plugin ships with obs since 28; just flip server_enabled=true, auth off. windows/wsl hyper-v port exclusion ranges are recomputed every reboot from whatever the machines dynamic port range happens to be, so no single hardcoded port stays safe forever on every pc -- 4455 then 6455 both eventually got swallowed (WSAEACCES on listen) on different machines. this now verifies the configured port actually binds and auto-repicks from a spread of fallbacks when it does not, instead of hardcoding a third number and hoping."""

from __future__ import annotations

import json
import socket
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
    "server_port":     6455,
}

# tried in order after the currently-configured port fails; spread 10000 apart so one exclusion block (seen up to ~100 ports wide in practice) cant catch more than one candidate.
_PORT_FALLBACKS = (6455, 16455, 26455, 36455, 46455, 12455, 22455)


def _port_is_bindable(port: int) -> bool:
    """true if a loopback listener can actually take this port right now."""
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
            s.bind(("127.0.0.1", port))
        return True
    except OSError:
        return False


def _pick_working_port(preferred: int) -> Optional[int]:
    """preferred if it binds, else the first fallback that does. None if every candidate is blocked."""
    candidates = [preferred] + [p for p in _PORT_FALLBACKS if p != preferred]
    for port in candidates:
        if _port_is_bindable(port):
            return port
    return None


def _load_existing() -> Dict[str, Any]:
    """current config or {} (also {} on parse failure so a corrupted file doesnt block install)."""
    if not WEBSOCKET_CONFIG_PATH.is_file():
        return {}
    try:
        return json.loads(WEBSOCKET_CONFIG_PATH.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError, UnicodeDecodeError):
        return {}


def install_websocket_config(log: LogFn = None) -> bool:
    """ensure obs-websocket is enabled on a port that actually binds on this pc. preserves auth_required if the user has already turned it on. runs on every apply (not just fresh installs), so a port thats become blocked since the last run gets caught and repicked automatically -- called with obs already closed (see run_apply_flow), so this is testing the same bind obs-websocket itself will attempt."""
    existing = _load_existing()
    merged   = dict(_DEFAULT_CONFIG)
    merged.update(existing)

    # force only what the helper depends on; everything else (alerts, etc.) keeps the users value.
    merged["server_enabled"] = True
    # if auth is already on, leave it on. only defualt to off for fresh configs.
    if "auth_required" not in existing:
        merged["auth_required"] = False

    wanted_port = int(merged.get("server_port") or 6455)
    if not _port_is_bindable(wanted_port):
        found = _pick_working_port(wanted_port)
        if found is not None and found != wanted_port:
            if log:
                log(f"port {wanted_port} is blocked on this pc (windows port reservation) - switching to {found}")
            merged["server_port"] = found
        elif found is None and log:
            log(f"warn: every candidate port is blocked on this pc; leaving server_port={wanted_port} (obs-websocket will fail to start)")

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

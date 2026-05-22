"""parse human-typed key combos ('shift+\\\\', 'ctrl+f10') into the qt-flavored json obs persists for hotkeys (OBS_KEY_<x> + shift/control/alt/command booleans)."""

from __future__ import annotations

import json
from typing import Dict, List, Optional, Tuple


# OBS_KEY_ identifiers from libobs/obs-hotkey.h. only the subset a human is likely to type for a clip keybind: letters, digits, function keys, common symbol keys on a us layout, navigation, numpad.
_NAMED_KEYS: Dict[str, str] = {
    # symbols (us-ansi layout names)
    "\\": "OBS_KEY_BACKSLASH",
    "/":  "OBS_KEY_SLASH",
    ",":  "OBS_KEY_COMMA",
    ".":  "OBS_KEY_PERIOD",
    ";":  "OBS_KEY_SEMICOLON",
    "'":  "OBS_KEY_APOSTROPHE",
    "`":  "OBS_KEY_QUOTELEFT",
    "-":  "OBS_KEY_MINUS",
    "=":  "OBS_KEY_EQUAL",
    "[":  "OBS_KEY_BRACKETLEFT",
    "]":  "OBS_KEY_BRACKETRIGHT",

    # word-named keys
    "space":     "OBS_KEY_SPACE",
    "tab":       "OBS_KEY_TAB",
    "enter":     "OBS_KEY_RETURN",
    "return":    "OBS_KEY_RETURN",
    "escape":    "OBS_KEY_ESCAPE",
    "esc":       "OBS_KEY_ESCAPE",
    "backspace": "OBS_KEY_BACKSPACE",
    "insert":    "OBS_KEY_INSERT",
    "delete":    "OBS_KEY_DELETE",
    "del":       "OBS_KEY_DELETE",
    "home":      "OBS_KEY_HOME",
    "end":       "OBS_KEY_END",
    "pageup":    "OBS_KEY_PAGEUP",
    "pagedown":  "OBS_KEY_PAGEDOWN",
    "up":        "OBS_KEY_UP",
    "down":      "OBS_KEY_DOWN",
    "left":      "OBS_KEY_LEFT",
    "right":     "OBS_KEY_RIGHT",
    "capslock":  "OBS_KEY_CAPSLOCK",
    "printscreen": "OBS_KEY_PRINT",
    "scrolllock":  "OBS_KEY_SCROLLLOCK",
    "pause":     "OBS_KEY_PAUSE",
}

# pretty-print labels in the obs hotkey dialog style.
_NAMED_KEY_LABELS: Dict[str, str] = {
    "OBS_KEY_BACKSLASH":    "\\",
    "OBS_KEY_SLASH":        "/",
    "OBS_KEY_COMMA":        ",",
    "OBS_KEY_PERIOD":       ".",
    "OBS_KEY_SEMICOLON":    ";",
    "OBS_KEY_APOSTROPHE":   "'",
    "OBS_KEY_QUOTELEFT":    "`",
    "OBS_KEY_MINUS":        "-",
    "OBS_KEY_EQUAL":        "=",
    "OBS_KEY_BRACKETLEFT":  "[",
    "OBS_KEY_BRACKETRIGHT": "]",
    "OBS_KEY_SPACE":        "Space",
    "OBS_KEY_TAB":          "Tab",
    "OBS_KEY_RETURN":       "Enter",
    "OBS_KEY_ESCAPE":       "Esc",
    "OBS_KEY_BACKSPACE":    "Backspace",
    "OBS_KEY_INSERT":       "Insert",
    "OBS_KEY_DELETE":       "Delete",
    "OBS_KEY_HOME":         "Home",
    "OBS_KEY_END":          "End",
    "OBS_KEY_PAGEUP":       "PageUp",
    "OBS_KEY_PAGEDOWN":     "PageDown",
    "OBS_KEY_UP":           "Up",
    "OBS_KEY_DOWN":         "Down",
    "OBS_KEY_LEFT":         "Left",
    "OBS_KEY_RIGHT":        "Right",
    "OBS_KEY_CAPSLOCK":     "CapsLock",
    "OBS_KEY_PRINT":        "PrintScreen",
    "OBS_KEY_SCROLLLOCK":   "ScrollLock",
    "OBS_KEY_PAUSE":        "Pause",
}

# modifier synonyms -> the json field obs expects.
_MOD_ALIASES: Dict[str, str] = {
    "shift": "shift",
    "ctrl":  "control",
    "control": "control",
    "alt":   "alt",
    "opt":   "alt",
    "option":"alt",
    "win":   "command",
    "cmd":   "command",
    "meta":  "command",
    "command": "command",
}

# pretty modifier order for display labels.
_MOD_ORDER = ("control", "alt", "shift", "command")
_MOD_LABEL = {"control": "Ctrl", "alt": "Alt", "shift": "Shift", "command": "Win"}


def _obs_key_for_token(token: str) -> Optional[str]:
    """map a single token (the non-modifier part of a combo) to an OBS_KEY_."""
    if not token:
        return None
    if token in _NAMED_KEYS:
        return _NAMED_KEYS[token]
    low = token.lower()
    if low in _NAMED_KEYS:
        return _NAMED_KEYS[low]
    if len(token) == 1 and token.isalpha():
        return f"OBS_KEY_{token.upper()}"
    if len(token) == 1 and token.isdigit():
        return f"OBS_KEY_{token}"
    if low.startswith("f") and low[1:].isdigit():
        n = int(low[1:])
        if 1 <= n <= 24:
            return f"OBS_KEY_F{n}"
    if low.startswith("numpad") and low[6:].isdigit():
        return f"OBS_KEY_NUM{low[6:]}"
    return None


def parse_combo(text: str) -> Optional[Dict[str, object]]:
    """parse 'shift+\\\\' / 'ctrl+f10' / 'f9' into an obs keybind dict. None if no key was identified or input is modifier-only (obs wont fire on a bare shift)."""
    if not text:
        return None

    # strip surrounding quotes and normalize separators. tolerate the spaced-out look the obs hotkey dialog uses ('shift + \\').
    cleaned = text.strip().strip("'\"")
    cleaned = cleaned.replace("-", "+").replace(" ", "")
    if not cleaned:
        return None

    parts: List[str] = []
    buf = ""
    # split on + but keep a literal + if the user typed e.g. 'shift++'.
    for ch in cleaned:
        if ch == "+" and buf:
            parts.append(buf)
            buf = ""
        else:
            buf += ch
    if buf:
        parts.append(buf)
    if not parts:
        return None

    combo: Dict[str, object] = {}
    key_token: Optional[str] = None
    for raw in parts:
        low = raw.lower()
        if low in _MOD_ALIASES and raw != key_token:
            combo[_MOD_ALIASES[low]] = True
            continue
        # first non-modifier token wins as the key.
        if key_token is None:
            key_token = raw

    if key_token is None:
        return None

    obs_key = _obs_key_for_token(key_token)
    if obs_key is None:
        return None
    combo["key"] = obs_key
    return combo


def combo_to_label(combo: Dict[str, object]) -> str:
    """format an obs keybind dict like 'Shift + \\\\' for the cli."""
    if not combo or "key" not in combo:
        return "(none)"
    parts: List[str] = [_MOD_LABEL[m] for m in _MOD_ORDER if combo.get(m)]
    obs_key = str(combo["key"])
    if obs_key in _NAMED_KEY_LABELS:
        parts.append(_NAMED_KEY_LABELS[obs_key])
    elif obs_key.startswith("OBS_KEY_F") and obs_key[len("OBS_KEY_F"):].isdigit():
        parts.append(obs_key[len("OBS_KEY_"):])
    elif obs_key.startswith("OBS_KEY_NUM"):
        parts.append("Numpad " + obs_key[len("OBS_KEY_NUM"):])
    elif obs_key.startswith("OBS_KEY_"):
        parts.append(obs_key[len("OBS_KEY_"):].title())
    else:
        parts.append(obs_key)
    return " + ".join(parts)


def to_basic_ini_value(combo: Dict[str, object]) -> str:
    """serialise the combo into the exact json obs writes for ReplayBuffer. compact form: no whitespace, modifiers before key, only true-valued modifiers emitted."""
    if not combo or "key" not in combo:
        return json.dumps({"ReplayBuffer.Save": []}, separators=(",", ":"))
    ordered: Dict[str, object] = {}
    for mod in _MOD_ORDER:
        if combo.get(mod):
            ordered[mod] = True
    ordered["key"] = combo["key"]
    inner = json.dumps(ordered, separators=(",", ":"))
    return json.dumps({"ReplayBuffer.Save": [json.loads(inner)]}, separators=(",", ":"))


def to_obs_hotkey_value(combo: Dict[str, object]) -> str:
    """serialise a frontend OBS hotkey value such as OBSBasic.StartRecording."""
    if not combo or "key" not in combo:
        return json.dumps({"bindings": []}, separators=(",", ":"))
    ordered: Dict[str, object] = {}
    for mod in _MOD_ORDER:
        if combo.get(mod):
            ordered[mod] = True
    ordered["key"] = combo["key"]
    inner = json.dumps(ordered, separators=(",", ":"))
    return json.dumps({"bindings": [json.loads(inner)]}, separators=(",", ":"))


def from_basic_ini_value(text: str) -> Optional[Dict[str, object]]:
    """parse the json obs persists under [Hotkeys] ReplayBuffer=. returns the first ReplayBuffer.Save keybind dict, or None if malformed."""
    try:
        data = json.loads(text)
    except (json.JSONDecodeError, TypeError):
        return None
    binds = data.get("ReplayBuffer.Save") if isinstance(data, dict) else None
    if not isinstance(binds, list) or not binds:
        return None
    first = binds[0]
    if not isinstance(first, dict) or "key" not in first:
        return None
    return first


def default_combo() -> Dict[str, object]:
    """the shift+\\\\ keybind the bundled basic.ini ships with."""
    return {"shift": True, "key": "OBS_KEY_BACKSLASH"}

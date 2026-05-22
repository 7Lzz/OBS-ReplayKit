"""Persisted user preferences for OBS ReplayKit."""

import json
import sys
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any, Dict

from .audio import DEFAULT_DEVICE_ID, DEFAULT_DEVICE_NAME
from .config import REPLAYKIT_CONFIG, USERPROFILE
from .keybind import default_combo


def _prefs_dir() -> Path:
    """Use the exe folder in packaged builds and the project folder in source builds."""
    if getattr(sys, "frozen", False):
        return Path(sys.executable).resolve().parent
    return Path(__file__).resolve().parent.parent


PREFS_DIR  = _prefs_dir()
PREFS_FILE = PREFS_DIR / "prefs.json"
RUNTIME_SETTINGS_FILE = REPLAYKIT_CONFIG / "scripts" / "replaykit_settings.json"


# defaults match the bundled config so an unconfigured run produces the same install the previous version did.
DEFAULT_RECORDING_PRESET    = "balanced"
DEFAULT_INPUT_OVERLAY       = True
DEFAULT_OVERLAY_STYLE       = "input_overlay"
ALLOWED_OVERLAY_STYLES      = ("input_overlay", "bongo_cat", "off")
DEFAULT_REPLAY_BUFFER_SECS  = 90
REPLAY_BUFFER_MIN           = 5
REPLAY_BUFFER_MAX           = 600
DEFAULT_OBS_STARTUP         = True
DEFAULT_CLIP_NOTIFICATION   = True
DEFAULT_RECORDING_NOTIFICATION = True
DEFAULT_TRIM_PRECISE        = False

# "auto" picks the best HEVC/H.264 combo the user's GPU can run; the user can
# force a specific codec for playback support (H.264) or quality/size (AV1 on
# RTX 40+/Arc/RX 7000+).
DEFAULT_CODEC_PREFERENCE    = "auto"
ALLOWED_CODEC_PREFERENCES   = ("auto", "h264", "h265", "av1")

# compression mode trades gpu cost against file size at the same visual quality. balanced is the validated default (~10-11% gpu on a 3060 ti at 1080p60 hevc). lower_gpu uses a faster nvenc preset + no lookahead + no b-frames (bigger files); smaller_files uses a slower preset + multipass + lookahead + more b-frames (more gpu, tighter files).
DEFAULT_COMPRESSION_MODE    = "balanced"
ALLOWED_COMPRESSION_MODES   = ("lower_gpu", "balanced", "smaller_files")

PRESET_COMPRESSION_DEFAULTS = {
    "performance": "lower_gpu",
    "balanced": "balanced",
    "quality": "smaller_files",
}


def default_compression_for_preset(preset_name: str) -> str:
    return PRESET_COMPRESSION_DEFAULTS.get(preset_name, DEFAULT_COMPRESSION_MODE)


def _default_recording_path() -> str:
    """~/pictures/videos forward-slashed -- matches the bundled obs profiles advout.recfilepath/ffflepath defaults so the path is consistent across simple/advanced output modes."""
    return (USERPROFILE / "Pictures" / "Videos").as_posix()


@dataclass
class Preferences:
    """in-memory user config. persists itself via save()."""
    recording_preset:        str  = DEFAULT_RECORDING_PRESET
    input_overlay_enabled:   bool = DEFAULT_INPUT_OVERLAY
    overlay_style:           str  = DEFAULT_OVERLAY_STYLE
    microphone_device_id:    str  = DEFAULT_DEVICE_ID
    microphone_name:         str  = DEFAULT_DEVICE_NAME
    replay_buffer_seconds:   int  = DEFAULT_REPLAY_BUFFER_SECS
    recording_path:          str  = field(default_factory=_default_recording_path)
    clip_keybind:            Dict[str, Any] = field(default_factory=default_combo)
    recording_keybind:       Dict[str, Any] = field(default_factory=dict)
    codec_preference:        str  = DEFAULT_CODEC_PREFERENCE
    compression_mode:        str  = DEFAULT_COMPRESSION_MODE
    obs_startup_enabled:     bool = DEFAULT_OBS_STARTUP
    clip_notification_enabled: bool = DEFAULT_CLIP_NOTIFICATION
    recording_notification_enabled: bool = DEFAULT_RECORDING_NOTIFICATION
    trim_precise_default:    bool = DEFAULT_TRIM_PRECISE

    def save(self) -> None:
        PREFS_DIR.mkdir(parents=True, exist_ok=True)
        PREFS_FILE.write_text(
            json.dumps(asdict(self), indent=2),
            encoding="utf-8",
        )


def _coerce_keybind(value: Any) -> Dict[str, Any]:
    """fall back to the default combo on anything malformed so a corrupted prefs file never blocks startup."""
    if value == {}:
        return {}
    if isinstance(value, dict) and isinstance(value.get("key"), str):
        return value
    return default_combo()


def _coerce_optional_keybind(value: Any) -> Dict[str, Any]:
    """recording hotkeys default to none; malformed values stay disabled."""
    if isinstance(value, dict) and (value == {} or isinstance(value.get("key"), str)):
        return value
    return {}


def _coerce_bool(value: Any, default: bool) -> bool:
    return value if isinstance(value, bool) else default


def _runtime_settings_overlay() -> Dict[str, Any]:
    """Import settings changed from the in-OBS Custom Controls window."""
    if not RUNTIME_SETTINGS_FILE.is_file():
        return {}
    try:
        runtime = json.loads(RUNTIME_SETTINGS_FILE.read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError):
        return {}
    if not isinstance(runtime, dict):
        return {}

    mapped: Dict[str, Any] = {}
    key_map = {
        "recordingPreset": "recording_preset",
        "compressionMode": "compression_mode",
        "codecPreference": "codec_preference",
        "replaySeconds": "replay_buffer_seconds",
        "overlayStyle": "overlay_style",
        "obsStartupEnabled": "obs_startup_enabled",
        "clipNotificationEnabled": "clip_notification_enabled",
        "recordingNotificationEnabled": "recording_notification_enabled",
        "trimPreciseDefault": "trim_precise_default",
    }
    for src, dst in key_map.items():
        if src in runtime:
            mapped[dst] = runtime[src]

    if "clipDir" in runtime:
        clip_dir = str(runtime.get("clipDir") or "").strip()
        mapped["recording_path"] = clip_dir or _default_recording_path()
    if isinstance(runtime.get("clipKeybind"), dict):
        mapped["clip_keybind"] = runtime["clipKeybind"]
    if isinstance(runtime.get("recordingKeybind"), dict):
        mapped["recording_keybind"] = runtime["recordingKeybind"]

    return mapped


def load_prefs() -> Preferences:
    """return saved preferences, or defaults if the file is missing/corrupt."""
    data: Dict[str, Any] = {}
    if PREFS_FILE.is_file():
        try:
            data = json.loads(PREFS_FILE.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            data = {}
        if not isinstance(data, dict):
            data = {}
    data.update(_runtime_settings_overlay())

    codec_pref = data.get("codec_preference", DEFAULT_CODEC_PREFERENCE)
    if codec_pref not in ALLOWED_CODEC_PREFERENCES:
        codec_pref = DEFAULT_CODEC_PREFERENCE

    compression_mode = data.get("compression_mode", DEFAULT_COMPRESSION_MODE)
    if compression_mode not in ALLOWED_COMPRESSION_MODES:
        compression_mode = DEFAULT_COMPRESSION_MODE

    overlay_style = data.get("overlay_style", DEFAULT_OVERLAY_STYLE)
    if overlay_style not in ALLOWED_OVERLAY_STYLES:
        overlay_style = DEFAULT_OVERLAY_STYLE
    input_overlay_enabled = overlay_style != "off"

    return Preferences(
        recording_preset         = data.get("recording_preset",         DEFAULT_RECORDING_PRESET),
        input_overlay_enabled    = input_overlay_enabled,
        overlay_style            = overlay_style,
        microphone_device_id     = data.get("microphone_device_id",     DEFAULT_DEVICE_ID),
        microphone_name          = data.get("microphone_name",          DEFAULT_DEVICE_NAME),
        replay_buffer_seconds    = int(data.get("replay_buffer_seconds",    DEFAULT_REPLAY_BUFFER_SECS)),
        recording_path           = data.get("recording_path",           _default_recording_path()),
        clip_keybind             = _coerce_keybind(data.get("clip_keybind")),
        recording_keybind        = _coerce_optional_keybind(data.get("recording_keybind")),
        codec_preference         = codec_pref,
        compression_mode         = compression_mode,
        obs_startup_enabled      = _coerce_bool(data.get("obs_startup_enabled"), DEFAULT_OBS_STARTUP),
        clip_notification_enabled = _coerce_bool(data.get("clip_notification_enabled"), DEFAULT_CLIP_NOTIFICATION),
        recording_notification_enabled = _coerce_bool(data.get("recording_notification_enabled"), DEFAULT_RECORDING_NOTIFICATION),
        trim_precise_default     = _coerce_bool(data.get("trim_precise_default"), DEFAULT_TRIM_PRECISE),
    )

"""persisted user preferences for OBS ReplayKit."""

import json
import sys
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any, Dict

from .audio import DEFAULT_DEVICE_ID, DEFAULT_DEVICE_NAME
from .config import REPLAYKIT_CONFIG, REPLAYKIT_SETUP_CACHE, USERPROFILE
from .keybind import default_combo


def _prefs_dir() -> Path:
    """use the exe folder in packaged builds and the project folder in source builds."""
    if getattr(sys, "frozen", False):
        return Path(sys.executable).resolve().parent
    return Path(__file__).resolve().parent.parent


PREFS_DIR  = _prefs_dir()
PREFS_FILE = PREFS_DIR / "prefs.json"
SETUP_CACHE_PREFS_FILE = REPLAYKIT_SETUP_CACHE / "prefs.json"
RUNTIME_SETTINGS_FILE = REPLAYKIT_CONFIG / "scripts" / "replaykit_settings.json"


# defaults match the bundled config so an unconfigured run produces the same install the previous version did.
DEFAULT_RECORDING_PRESET    = "balanced"
DEFAULT_INPUT_OVERLAY       = True
DEFAULT_OVERLAY_STYLE       = "input_overlay"
ALLOWED_OVERLAY_STYLES      = ("input_overlay", "bongo_cat", "off")
DEFAULT_OVERLAY_OPACITY     = 100
DEFAULT_OVERLAY_SCALE       = 100
DEFAULT_OVERLAY_HUE_SHIFT   = 0.0
DEFAULT_OVERLAY_COLOR_MULTIPLY = "#ffffff"
DEFAULT_OVERLAY_COLOR_ADD   = "#000000"
OVERLAY_OPACITY_MIN         = 0
OVERLAY_OPACITY_MAX         = 100
OVERLAY_SCALE_MIN           = 50
OVERLAY_SCALE_MAX           = 200
OVERLAY_HUE_SHIFT_MIN       = -180.0
OVERLAY_HUE_SHIFT_MAX       = 180.0
DEFAULT_REPLAY_BUFFER_SECS  = 90
REPLAY_BUFFER_MIN           = 5
REPLAY_BUFFER_MAX           = 1200
DEFAULT_OBS_STARTUP         = True
DEFAULT_DISABLE_OBS_CLOSE_WARNING = True
DEFAULT_ALLOW_SLEEP_WHILE_ACTIVE = True
DEFAULT_PIN_OBS_TRAY_ICON  = True
DEFAULT_DEBUG_LOGGING_ENABLED = False
DEFAULT_CLIP_NOTIFICATION   = True
DEFAULT_RECORDING_NOTIFICATION = True
DEFAULT_TRIM_PRECISE        = False
DEFAULT_CLIP_SOUND_VOLUME   = 100
DEFAULT_RECORDING_SOUND_VOLUME = 100
DEFAULT_SHARE_MODE          = "projector"
ALLOWED_SHARE_MODES         = ("projector", "virtual_camera_legacy", "vcam", "screenshare")
DEFAULT_DISCORD_SCREENSHARE_ENABLED = True
DEFAULT_DISCORD_OUTPUT_MODE = "projector"
ALLOWED_DISCORD_OUTPUT_MODES = ("projector", "virtual_camera_legacy")
DEFAULT_DISCORD_PROJECTOR_ENABLED = True
DEFAULT_DISCORD_PROJECTOR_WIDTH = 0
DEFAULT_DISCORD_PROJECTOR_HEIGHT = 0
DEFAULT_DISCORD_PROJECTOR_VISIBLE_PIXELS = 1
DEFAULT_DISCORD_PROJECTOR_MONITOR_INDEX = 0
DEFAULT_DISCORD_PROJECTOR_EDGE = "bottom"
ALLOWED_DISCORD_PROJECTOR_EDGES = ("right", "left", "top", "bottom")
DEFAULT_DISCORD_PROJECTOR_TITLE_HINT = "OBS ReplayKit Discord Share"
DEFAULT_DISCORD_PROJECTOR_HIDE_TASKBAR = True
DEFAULT_SCREENSHARE_CAPTURE_MODE = "hybrid_auto"
ALLOWED_SCREENSHARE_CAPTURE_MODES = ("hybrid_auto", "desktop", "game_auto", "game_window")
DEFAULT_SCREENSHARE_GAME_WINDOW = ""
DEFAULT_SCREENSHARE_GAME_OVERRIDES = ()
DEFAULT_SCREENSHARE_AUTO_GAME_KEEP_FOCUSED = False
DEFAULT_MOTION_BLUR         = False
DEFAULT_MOTION_BLUR_STRENGTH = 0.075
MOTION_BLUR_STRENGTH_MIN    = 0.0
MOTION_BLUR_STRENGTH_MAX    = 1.0

# "auto" picks the best hevc/h.264 combo the users gpu can run, or the user can force h.264 for maximum playback support -- av1 is deliberately not offered since most iphones and plenty of android devices have no av1 decoder at all, and unlike a container-tag issue theres no fix for missing silicon.
DEFAULT_CODEC_PREFERENCE    = "auto"
ALLOWED_CODEC_PREFERENCES   = ("auto", "h264", "h265")

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
    overlay_opacity:         int  = DEFAULT_OVERLAY_OPACITY
    overlay_scale:           int  = DEFAULT_OVERLAY_SCALE
    overlay_hue_shift:       float = DEFAULT_OVERLAY_HUE_SHIFT
    overlay_color_multiply:  str  = DEFAULT_OVERLAY_COLOR_MULTIPLY
    overlay_color_add:       str  = DEFAULT_OVERLAY_COLOR_ADD
    microphone_device_id:    str  = DEFAULT_DEVICE_ID
    microphone_name:         str  = DEFAULT_DEVICE_NAME
    replay_buffer_seconds:   int  = DEFAULT_REPLAY_BUFFER_SECS
    recording_path:          str  = field(default_factory=_default_recording_path)
    clip_keybind:            Dict[str, Any] = field(default_factory=default_combo)
    recording_keybind:       Dict[str, Any] = field(default_factory=dict)
    codec_preference:        str  = DEFAULT_CODEC_PREFERENCE
    compression_mode:        str  = DEFAULT_COMPRESSION_MODE
    obs_startup_enabled:     bool = DEFAULT_OBS_STARTUP
    disable_obs_close_warning: bool = DEFAULT_DISABLE_OBS_CLOSE_WARNING
    allow_sleep_while_active: bool = DEFAULT_ALLOW_SLEEP_WHILE_ACTIVE
    pin_obs_tray_icon:       bool = DEFAULT_PIN_OBS_TRAY_ICON
    clip_notification_enabled: bool = DEFAULT_CLIP_NOTIFICATION
    recording_notification_enabled: bool = DEFAULT_RECORDING_NOTIFICATION
    trim_precise_default:    bool = DEFAULT_TRIM_PRECISE
    debug_logging_enabled:   bool = DEFAULT_DEBUG_LOGGING_ENABLED
    clip_sound_volume:       int  = DEFAULT_CLIP_SOUND_VOLUME
    recording_sound_volume:  int  = DEFAULT_RECORDING_SOUND_VOLUME
    share_mode:              str  = DEFAULT_SHARE_MODE
    discord_screenshare_enabled: bool = DEFAULT_DISCORD_SCREENSHARE_ENABLED
    discord_output_mode:     str  = DEFAULT_DISCORD_OUTPUT_MODE
    discord_projector_enabled: bool = DEFAULT_DISCORD_PROJECTOR_ENABLED
    discord_projector_width: int = DEFAULT_DISCORD_PROJECTOR_WIDTH
    discord_projector_height: int = DEFAULT_DISCORD_PROJECTOR_HEIGHT
    discord_projector_visible_pixels: int = DEFAULT_DISCORD_PROJECTOR_VISIBLE_PIXELS
    discord_projector_monitor_index: int = DEFAULT_DISCORD_PROJECTOR_MONITOR_INDEX
    discord_projector_edge:  str  = DEFAULT_DISCORD_PROJECTOR_EDGE
    discord_projector_title_hint: str = DEFAULT_DISCORD_PROJECTOR_TITLE_HINT
    discord_projector_hide_taskbar: bool = DEFAULT_DISCORD_PROJECTOR_HIDE_TASKBAR
    screenshare_capture_mode: str = DEFAULT_SCREENSHARE_CAPTURE_MODE
    screenshare_game_window: str = DEFAULT_SCREENSHARE_GAME_WINDOW
    screenshare_game_overrides: list[dict[str, str]] = field(default_factory=list)
    screenshare_auto_game_keep_focused: bool = DEFAULT_SCREENSHARE_AUTO_GAME_KEEP_FOCUSED
    motion_blur_enabled:     bool = DEFAULT_MOTION_BLUR
    motion_blur_strength:    float = DEFAULT_MOTION_BLUR_STRENGTH

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


def _coerce_int_range(value: Any, default: int, minimum: int, maximum: int) -> int:
    try:
        number = int(value)
    except (TypeError, ValueError):
        return default
    return max(minimum, min(maximum, number))


def _coerce_float_range(value: Any, default: float, minimum: float, maximum: float) -> float:
    try:
        number = float(value)
    except (TypeError, ValueError):
        return default
    return max(minimum, min(maximum, number))


def _coerce_hex_color(value: Any, default: str) -> str:
    text = str(value or "").strip().lower()
    if len(text) == 7 and text[0] == "#" and all(ch in "0123456789abcdef" for ch in text[1:]):
        return text
    return default


def _clean_override_text(value: Any, max_length: int) -> str:
    text = str(value or "").strip()
    if not text or len(text) > max_length or any(ord(ch) < 32 for ch in text):
        return ""
    return text


def _coerce_game_overrides(value: Any) -> list[dict[str, str]]:
    if not isinstance(value, list):
        return []
    out: list[dict[str, str]] = []
    seen: set[str] = set()
    for item in value[:32]:
        if not isinstance(item, dict):
            continue
        token = _clean_override_text(item.get("token") or item.get("value"), 512)
        exe_name = _clean_override_text(item.get("exeName"), 96)
        if not token or not exe_name or not exe_name.lower().endswith(".exe"):
            continue
        if token in seen:
            continue
        seen.add(token)
        entry = {
            "token": token,
            "value": token,
            "label": _clean_override_text(item.get("label"), 256) or f"[{exe_name}]",
            "exeName": exe_name,
        }
        title = _clean_override_text(item.get("title"), 160)
        class_name = _clean_override_text(item.get("className"), 120)
        if title:
            entry["title"] = title
        if class_name:
            entry["className"] = class_name
        out.append(entry)
    return out


def _runtime_settings_overlay() -> Dict[str, Any]:
    """import settings changed from the in-OBS Custom Controls window."""
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
        "overlayOpacity": "overlay_opacity",
        "overlayScale": "overlay_scale",
        "overlayHueShift": "overlay_hue_shift",
        "overlayColorMultiply": "overlay_color_multiply",
        "overlayColorAdd": "overlay_color_add",
        "obsStartupEnabled": "obs_startup_enabled",
        "disableObsCloseWarning": "disable_obs_close_warning",
        "allowSleepWhileActive": "allow_sleep_while_active",
        "pinObsTrayIcon": "pin_obs_tray_icon",
        "clipNotificationEnabled": "clip_notification_enabled",
        "recordingNotificationEnabled": "recording_notification_enabled",
        "trimPreciseDefault": "trim_precise_default",
        "debugLoggingEnabled": "debug_logging_enabled",
        "clipSoundVolume": "clip_sound_volume",
        "recordingSoundVolume": "recording_sound_volume",
        "shareMode": "share_mode",
        "discord_screenshare_enabled": "discord_screenshare_enabled",
        "discordScreenshareEnabled": "discord_screenshare_enabled",
        "discord_output_mode": "discord_output_mode",
        "discord_projector_enabled": "discord_projector_enabled",
        "discord_projector_width": "discord_projector_width",
        "discord_projector_height": "discord_projector_height",
        "discord_projector_visible_pixels": "discord_projector_visible_pixels",
        "discord_projector_monitor_index": "discord_projector_monitor_index",
        "discord_projector_edge": "discord_projector_edge",
        "discord_projector_title_hint": "discord_projector_title_hint",
        "discord_projector_hide_taskbar": "discord_projector_hide_taskbar",
        "screenshareCaptureMode": "screenshare_capture_mode",
        "screenshare_capture_mode": "screenshare_capture_mode",
        "screenshareGameWindow": "screenshare_game_window",
        "screenshare_game_window": "screenshare_game_window",
        "screenshareGameOverrides": "screenshare_game_overrides",
        "screenshare_game_overrides": "screenshare_game_overrides",
        "screenshareAutoGameKeepFocused": "screenshare_auto_game_keep_focused",
        "screenshare_auto_game_keep_focused": "screenshare_auto_game_keep_focused",
        "motionBlurEnabled": "motion_blur_enabled",
        "motionBlurStrength": "motion_blur_strength",
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
    prefs_file = PREFS_FILE
    if getattr(sys, "frozen", False) and not prefs_file.is_file() and SETUP_CACHE_PREFS_FILE.is_file():
        prefs_file = SETUP_CACHE_PREFS_FILE
    if prefs_file.is_file():
        try:
            data = json.loads(prefs_file.read_text(encoding="utf-8"))
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

    share_mode = data.get("share_mode", DEFAULT_SHARE_MODE)
    if share_mode == "share_bridge":
        share_mode = DEFAULT_SHARE_MODE
    if share_mode not in ALLOWED_SHARE_MODES:
        share_mode = DEFAULT_SHARE_MODE
    if share_mode == "vcam":
        share_mode = DEFAULT_SHARE_MODE
    elif share_mode == "screenshare":
        share_mode = DEFAULT_SHARE_MODE

    discord_output_mode = data.get("discord_output_mode", DEFAULT_DISCORD_OUTPUT_MODE)
    if discord_output_mode == "share_bridge":
        discord_output_mode = DEFAULT_DISCORD_OUTPUT_MODE
    if discord_output_mode not in ALLOWED_DISCORD_OUTPUT_MODES:
        discord_output_mode = DEFAULT_DISCORD_OUTPUT_MODE

    discord_projector_edge = data.get("discord_projector_edge", DEFAULT_DISCORD_PROJECTOR_EDGE)
    if discord_projector_edge not in ALLOWED_DISCORD_PROJECTOR_EDGES:
        discord_projector_edge = DEFAULT_DISCORD_PROJECTOR_EDGE

    discord_projector_title_hint = str(
        data.get("discord_projector_title_hint", DEFAULT_DISCORD_PROJECTOR_TITLE_HINT)
    ).strip()
    if (
        not discord_projector_title_hint
        or len(discord_projector_title_hint) > 128
        or any(ord(ch) < 32 for ch in discord_projector_title_hint)
    ):
        discord_projector_title_hint = DEFAULT_DISCORD_PROJECTOR_TITLE_HINT

    screenshare_capture_mode = data.get("screenshare_capture_mode", DEFAULT_SCREENSHARE_CAPTURE_MODE)
    if screenshare_capture_mode not in ALLOWED_SCREENSHARE_CAPTURE_MODES:
        screenshare_capture_mode = DEFAULT_SCREENSHARE_CAPTURE_MODE

    screenshare_game_window = str(data.get("screenshare_game_window", DEFAULT_SCREENSHARE_GAME_WINDOW)).strip()
    if len(screenshare_game_window) > 512 or any(ord(ch) < 32 for ch in screenshare_game_window):
        screenshare_game_window = DEFAULT_SCREENSHARE_GAME_WINDOW
    screenshare_game_overrides = _coerce_game_overrides(
        data.get("screenshare_game_overrides", DEFAULT_SCREENSHARE_GAME_OVERRIDES)
    )

    return Preferences(
        recording_preset         = data.get("recording_preset",         DEFAULT_RECORDING_PRESET),
        input_overlay_enabled    = input_overlay_enabled,
        overlay_style            = overlay_style,
        overlay_opacity          = _coerce_int_range(data.get("overlay_opacity"), DEFAULT_OVERLAY_OPACITY, OVERLAY_OPACITY_MIN, OVERLAY_OPACITY_MAX),
        overlay_scale            = _coerce_int_range(data.get("overlay_scale"), DEFAULT_OVERLAY_SCALE, OVERLAY_SCALE_MIN, OVERLAY_SCALE_MAX),
        overlay_hue_shift        = _coerce_float_range(data.get("overlay_hue_shift"), DEFAULT_OVERLAY_HUE_SHIFT, OVERLAY_HUE_SHIFT_MIN, OVERLAY_HUE_SHIFT_MAX),
        overlay_color_multiply   = _coerce_hex_color(data.get("overlay_color_multiply"), DEFAULT_OVERLAY_COLOR_MULTIPLY),
        overlay_color_add        = _coerce_hex_color(data.get("overlay_color_add"), DEFAULT_OVERLAY_COLOR_ADD),
        microphone_device_id     = data.get("microphone_device_id",     DEFAULT_DEVICE_ID),
        microphone_name          = data.get("microphone_name",          DEFAULT_DEVICE_NAME),
        replay_buffer_seconds    = int(data.get("replay_buffer_seconds",    DEFAULT_REPLAY_BUFFER_SECS)),
        recording_path           = data.get("recording_path",           _default_recording_path()),
        clip_keybind             = _coerce_keybind(data.get("clip_keybind")),
        recording_keybind        = _coerce_optional_keybind(data.get("recording_keybind")),
        codec_preference         = codec_pref,
        compression_mode         = compression_mode,
        obs_startup_enabled      = _coerce_bool(data.get("obs_startup_enabled"), DEFAULT_OBS_STARTUP),
        disable_obs_close_warning = _coerce_bool(data.get("disable_obs_close_warning"), DEFAULT_DISABLE_OBS_CLOSE_WARNING),
        allow_sleep_while_active = _coerce_bool(data.get("allow_sleep_while_active"), DEFAULT_ALLOW_SLEEP_WHILE_ACTIVE),
        pin_obs_tray_icon        = _coerce_bool(data.get("pin_obs_tray_icon"), DEFAULT_PIN_OBS_TRAY_ICON),
        clip_notification_enabled = _coerce_bool(data.get("clip_notification_enabled"), DEFAULT_CLIP_NOTIFICATION),
        recording_notification_enabled = _coerce_bool(data.get("recording_notification_enabled"), DEFAULT_RECORDING_NOTIFICATION),
        trim_precise_default     = _coerce_bool(data.get("trim_precise_default"), DEFAULT_TRIM_PRECISE),
        debug_logging_enabled    = _coerce_bool(data.get("debug_logging_enabled"), DEFAULT_DEBUG_LOGGING_ENABLED),
        clip_sound_volume        = _coerce_int_range(data.get("clip_sound_volume"), DEFAULT_CLIP_SOUND_VOLUME, 0, 100),
        recording_sound_volume   = _coerce_int_range(data.get("recording_sound_volume"), DEFAULT_RECORDING_SOUND_VOLUME, 0, 100),
        share_mode               = share_mode,
        discord_screenshare_enabled = _coerce_bool(data.get("discord_screenshare_enabled"), DEFAULT_DISCORD_SCREENSHARE_ENABLED),
        discord_output_mode      = discord_output_mode,
        discord_projector_enabled = _coerce_bool(data.get("discord_projector_enabled"), DEFAULT_DISCORD_PROJECTOR_ENABLED),
        discord_projector_width  = _coerce_int_range(data.get("discord_projector_width"), DEFAULT_DISCORD_PROJECTOR_WIDTH, 0, 7680),
        discord_projector_height = _coerce_int_range(data.get("discord_projector_height"), DEFAULT_DISCORD_PROJECTOR_HEIGHT, 0, 4320),
        discord_projector_visible_pixels = _coerce_int_range(data.get("discord_projector_visible_pixels"), DEFAULT_DISCORD_PROJECTOR_VISIBLE_PIXELS, 0, 200),
        discord_projector_monitor_index = _coerce_int_range(data.get("discord_projector_monitor_index"), DEFAULT_DISCORD_PROJECTOR_MONITOR_INDEX, 0, 64),
        discord_projector_edge   = discord_projector_edge,
        discord_projector_title_hint = discord_projector_title_hint,
        discord_projector_hide_taskbar = _coerce_bool(data.get("discord_projector_hide_taskbar"), DEFAULT_DISCORD_PROJECTOR_HIDE_TASKBAR),
        screenshare_capture_mode = screenshare_capture_mode,
        screenshare_game_window  = screenshare_game_window,
        screenshare_game_overrides = screenshare_game_overrides,
        screenshare_auto_game_keep_focused = _coerce_bool(
            data.get("screenshare_auto_game_keep_focused"),
            DEFAULT_SCREENSHARE_AUTO_GAME_KEEP_FOCUSED,
        ),
        motion_blur_enabled      = _coerce_bool(data.get("motion_blur_enabled"), DEFAULT_MOTION_BLUR),
        motion_blur_strength     = _coerce_float_range(
            data.get("motion_blur_strength"),
            DEFAULT_MOTION_BLUR_STRENGTH,
            MOTION_BLUR_STRENGTH_MIN,
            MOTION_BLUR_STRENGTH_MAX,
        ),
    )

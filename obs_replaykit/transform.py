"""splice user prefs into bundled obs config files (basic.ini, recordencoder.json, scenes json, user.ini, global.ini)."""

import json
import math
import re
from pathlib import Path
from typing import Optional

from .config import INPUT_OVERLAY_TARGET, OBS_CONFIG, PROGRAMFILES_OBS_DIR
from .display import primary_display
from .dock import DOCK_TARGET
from .encoder import EncoderChoice, pick_encoder
from .gpu import primary_gpu
from .keybind import to_basic_ini_value, to_obs_hotkey_value
from .prefs import Preferences
from .recording import RecordingPreset, get_preset


# targeted regex over configparser becuase obss basic.ini has formatting we want byte-identical (no spaces around =, blank lines, base64 blobs in geometry=).

def set_ini_value(text: str, section: str, key: str, value: str) -> str:
    """public alias for _set_ini_value."""
    return _set_ini_value(text, section, key, value)


def _set_ini_value(text: str, section: str, key: str, value: str) -> str:
    """set [section] key=value, preserving formatting. value is treated literally -- backslashes and re.sub template syntax dont expand (callable replacement)."""
    key_re = re.escape(key)

    # both section and key present -> replace value in place.
    update_re = re.compile(
        rf"(\[{re.escape(section)}\][^\[]*?\n){key_re}=[^\r\n]*",
        flags=re.DOTALL,
    )
    if update_re.search(text):
        return update_re.sub(
            lambda m: f"{m.group(1)}{key}={value}",
            text,
            count=1,
        )

    # section present, key missing -> insert before the next section header.
    section_re = re.compile(
        rf"(\[{re.escape(section)}\][^\[]*?)(?=\n\[|\Z)",
        flags=re.DOTALL,
    )
    m = section_re.search(text)
    if m:
        body = m.group(1).rstrip()
        return text[:m.start()] + body + f"\n{key}={value}\n" + text[m.end():]

    # section missing entirely -> append a fresh one at end of file.
    sep = "" if text.endswith("\n") else "\n"
    return f"{text}{sep}\n[{section}]\n{key}={value}\n"


# memoised so apply_basic_ini and apply_record_encoder_json agree on which encoder got picked.
_encoder_cache: tuple = ()  # (codec_preference, recording_preset, compression_mode, encoderchoice)


def _resolve_encoder(prefs: Preferences) -> EncoderChoice:
    """pick the recording encoder once per install, memoised across basic.ini + recordencoder.json writes."""
    global _encoder_cache
    if _encoder_cache and _encoder_cache[0] == prefs.codec_preference \
                     and _encoder_cache[1] == prefs.recording_preset \
                     and _encoder_cache[2] == prefs.compression_mode:
        return _encoder_cache[3]
    preset = get_preset(prefs.recording_preset)
    choice = pick_encoder(primary_gpu(), prefs.codec_preference, preset.cqp_target, prefs.compression_mode)
    _encoder_cache = (prefs.codec_preference, prefs.recording_preset, prefs.compression_mode, choice)
    return choice


def reset_encoder_cache() -> None:
    """force a re-pick on the next encoder resolve (e.g. user swapped gpu between runs)."""
    global _encoder_cache
    _encoder_cache = ()


def _even_dimension(value: float) -> int:
    number = max(2, min(4096, int(round(value))))
    if number % 2:
        number -= 1
    return max(2, number)


def _scaled_even_size(source_w: int, source_h: int, max_w: int, max_h: int) -> tuple[int, int]:
    if source_w < 2 or source_h < 2 or max_w < 2 or max_h < 2:
        return 1920, 1080
    scale = min(1.0, max_w / source_w, max_h / source_h)
    return _even_dimension(source_w * scale), _even_dimension(source_h * scale)


def _preset_video_ini(prefs: Preferences) -> dict[str, str]:
    preset = get_preset(prefs.recording_preset)
    video = dict(preset.basic_ini.get("Video", {}))
    primary = primary_display()

    if primary is not None and primary.width >= 320 and primary.height >= 240:
        target_w = int(video.get("OutputCX", 1920))
        target_h = int(video.get("OutputCY", 1080))
        base_w, base_h = _scaled_even_size(primary.width, primary.height, 4096, 4096)
        output_w, output_h = _scaled_even_size(base_w, base_h, target_w, target_h)
        video["BaseCX"] = str(base_w)
        video["BaseCY"] = str(base_h)
        video["OutputCX"] = str(output_w)
        video["OutputCY"] = str(output_h)

    video["ScaleType"] = "lanczos"
    return {key: str(value) for key, value in video.items()}


_MIN_RB_SIZE_MB = 32

# realistic peak cqp bitrate per preset tier (mbps) -- a high-motion scene at that resolution/cqp_target, not a padded worst-of-worst-case number. cqp output size is content-driven so this is an estimate, not exact.
_RB_PEAK_MBPS = {
    "performance": 8,
    "balanced":    20,
    "quality":     32,
}
_RB_SAFETY_FACTOR = 1.5  # headroom over the peak estimate, not a second multiplier on top of an already-padded one


def _scaled_rb_size_mb(preset: RecordingPreset, prefs: Preferences) -> int:
    """size the replay-buffer memory cap from an actual bitrate estimate for this preset x the users chosen buffer length, so it tracks ram usage realistically instead of ballooning at long buffer lengths (the old version scaled a flat per-tier constant that was itself padded for safety, which compounded into gb-scale caps most peoples machines dont have)."""
    peak_mbps = _RB_PEAK_MBPS.get(preset.name, _RB_PEAK_MBPS["balanced"])
    mb_per_second = peak_mbps * _RB_SAFETY_FACTOR / 8
    return max(_MIN_RB_SIZE_MB, math.ceil(mb_per_second * prefs.replay_buffer_seconds))


def apply_basic_ini(text: str, prefs: Preferences) -> str:
    preset = get_preset(prefs.recording_preset)
    for section, items in preset.basic_ini.items():
        if section == "Video":
            items = _preset_video_ini(prefs)
        for key, value in items.items():
            text = _set_ini_value(text, section, key, value)

    # advout.recencoder must match the encoder whose settings we write into recordencoder.json.
    encoder = _resolve_encoder(prefs)
    text = _set_ini_value(text, "AdvOut", "RecEncoder", encoder.obs_encoder_id)

    text = _set_ini_value(text, "AdvOut", "RecRB",     "true")
    text = _set_ini_value(text, "AdvOut", "RecRBTime", str(prefs.replay_buffer_seconds))
    text = _set_ini_value(text, "AdvOut", "RecRBSize", str(_scaled_rb_size_mb(preset, prefs)))

    # forward slashes -- obs accepts them in ini values and skips escaping.
    rec_path = prefs.recording_path.replace("\\", "/")
    text = _set_ini_value(text, "SimpleOutput", "FilePath",    rec_path)
    text = _set_ini_value(text, "AdvOut",       "RecFilePath", rec_path)
    text = _set_ini_value(text, "AdvOut",       "FFFilePath",  rec_path)

    text = _set_ini_value(
        text, "Hotkeys", "ReplayBuffer", to_basic_ini_value(prefs.clip_keybind)
    )
    if prefs.recording_keybind:
        recording_hotkey = to_obs_hotkey_value(prefs.recording_keybind)
        text = _set_ini_value(text, "Hotkeys", "OBSBasic.StartRecording", recording_hotkey)
        text = _set_ini_value(text, "Hotkeys", "OBSBasic.StopRecording", recording_hotkey)

    monitor = _find_obs_stream_audio_render_endpoint()
    if monitor is not None:
        text = _set_ini_value(text, "Audio", "MonitoringDeviceId", monitor[0])
        text = _set_ini_value(text, "Audio", "MonitoringDeviceName", monitor[1])

    return text


def _find_obs_stream_audio_render_endpoint() -> Optional[tuple[str, str]]:
    try:
        import winreg
    except ImportError:
        return None

    root_path = r"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render"
    friendly_key = "{a45c254e-df1c-4efd-8020-67d146a850e0},2"
    candidates: list[tuple[int, str, str]] = []
    try:
        root = winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, root_path)
    except OSError:
        return None

    with root:
        index = 0
        while True:
            try:
                guid = winreg.EnumKey(root, index)
            except OSError:
                break
            index += 1
            try:
                with winreg.OpenKey(root, guid) as endpoint:
                    state, _ = winreg.QueryValueEx(endpoint, "DeviceState")
                if (int(state) & 0x1) == 0:
                    continue
                with winreg.OpenKey(root, rf"{guid}\Properties") as props:
                    name, _ = winreg.QueryValueEx(props, friendly_key)
            except OSError:
                continue

            display_name = str(name).strip()
            canonical = re.sub(r"^\s*\d+\s*-\s*", "", display_name).strip().lower()
            if re.search(r"surround|16ch|loopback|do not select", canonical):
                continue
            rank = 999
            if canonical == "obs stream audio":
                rank = 0
            elif canonical.startswith("obs stream audio"):
                rank = 1
            elif canonical.startswith("cable input"):
                rank = 2
            if rank == 999:
                continue
            candidates.append((rank, display_name, f"{{0.0.0.00000000}}.{guid}"))

    if not candidates:
        return None
    rank, display_name, device_id = sorted(candidates, key=lambda item: (item[0], item[1].lower()))[0]
    return device_id, display_name


def apply_record_encoder_json(_text: str, prefs: Preferences) -> str:
    """replace recordencoder.json with the gpu-aware encoder config picked in encoder.py."""
    encoder = _resolve_encoder(prefs)
    return json.dumps(encoder.settings)


# stable uuid for our custom controls dock entry. repeat applies update the same row instead of stacking duplicates.
_CUSTOM_CONTROLS_DOCK_UUID = "a59ce0ef-5d6f-4a4f-91d9-c7c3c1d4e2b0"


def _dock_url() -> str:
    """bare windows path (no file:// prefix, backslashes) -- same shape obs writes when the user adds a custom browser dock via the dialog."""
    return str(DOCK_TARGET / "controls_app.html").replace("/", "\\")


def _managed_dock_entry() -> dict:
    return {
        "title": "Custom Controls",
        "url":   _dock_url(),
        "uuid":  _CUSTOM_CONTROLS_DOCK_UUID,
    }


def _extra_browser_docks_value() -> str:
    """render basicwindow.extrabrowserdocks as json with backslashes double-escaped (obss ini parser unescapes once on read)."""
    raw = json.dumps([_managed_dock_entry()])
    return raw.replace("\\", "\\\\")


def _is_obs_replaykit_dock(item: object) -> bool:
    """match the managed OBS ReplayKit dock entry."""
    if not isinstance(item, dict):
        return False
    title = str(item.get("title", "")).strip().lower()
    url = str(item.get("url", "")).replace("\\", "/").lower()
    uuid = str(item.get("uuid", "")).replace("-", "").lower()
    managed_uuid = _CUSTOM_CONTROLS_DOCK_UUID.replace("-", "").lower()
    return (
        uuid == managed_uuid
        or title == "custom controls"
        or "obs-replaykit/obs-custom-dock/controls.html" in url
        or "obs-replaykit/obs-custom-dock/controls_app.html" in url
    )


def _merge_extra_browser_docks_value(existing: str) -> str:
    """inject our custom controls entry into the existing extrabrowserdocks array, replacing any stale obs-replaykit row. other user-added docks are preserved."""
    try:
        unescaped = existing.replace("\\\\", "\\")
        docks = json.loads(unescaped)
        if not isinstance(docks, list):
            docks = []
    except (json.JSONDecodeError, ValueError):
        docks = []

    target = _managed_dock_entry()
    rebuilt = []
    inserted_at = -1
    for item in docks:
        if _is_obs_replaykit_dock(item):
            if inserted_at == -1:
                inserted_at = len(rebuilt)
                rebuilt.append(target)
        else:
            rebuilt.append(item)
    if inserted_at == -1:
        rebuilt.append(target)

    return json.dumps(rebuilt).replace("\\", "\\\\")


def _read_live_user_ini_value(key: str) -> Optional[str]:
    """raw value of [BasicWindow] <key>= from the live user.ini, or None. used to preserve dockstate and feed the extrabrowserdocks merge."""
    live_path = OBS_CONFIG / "user.ini"
    if not live_path.is_file():
        return None
    try:
        try:
            live_text = live_path.read_text(encoding="utf-8")
        except UnicodeDecodeError:
            live_text = live_path.read_text(encoding="latin-1")
    except OSError:
        return None
    m = re.search(
        rf"\[BasicWindow\][^\[]*?\n{re.escape(key)}=([^\r\n]*)",
        live_text,
        flags=re.DOTALL,
    )
    return m.group(1) if m else None


def apply_user_ini(text: str, prefs: Preferences) -> str:
    """write one canonical Custom Controls dock entry and reset stale dock layout state."""

    section_re = re.compile(
        r"(\[BasicWindow\][^\[]*?\n)ExtraBrowserDocks=([^\r\n]*)",
        flags=re.DOTALL,
    )
    m = section_re.search(text)
    if m:
        # fold the live ini into the bundle seed so user-added docks are preserved.
        live_value = _read_live_user_ini_value("ExtraBrowserDocks") or ""
        seed = m.group(2)
        if live_value and live_value != seed:
            seed = _combine_extra_browser_docks(seed, live_value)
        merged = _merge_extra_browser_docks_value(seed)
        text = section_re.sub(
            lambda _m: f"{m.group(1)}ExtraBrowserDocks={merged}", text, count=1
        )
        return _set_ini_value(text, "General", "ConfirmOnExit", _confirm_on_exit_value(prefs))

    text = _set_ini_value(text, "BasicWindow", "ExtraBrowserDocks", _extra_browser_docks_value())
    return _set_ini_value(text, "General", "ConfirmOnExit", _confirm_on_exit_value(prefs))


def _combine_extra_browser_docks(bundle_value: str, live_value: str) -> str:
    """concat bundle + live extrabrowserdocks arrays, dedupe by json serialisation. obs-custom-dock duplicates are collapsed in the downstream merge."""
    def _decode(value: str) -> list:
        try:
            unescaped = value.replace("\\\\", "\\")
            parsed = json.loads(unescaped)
            return parsed if isinstance(parsed, list) else []
        except (json.JSONDecodeError, ValueError):
            return []

    combined: list = []
    seen: set = set()
    for item in _decode(bundle_value) + _decode(live_value):
        key = json.dumps(item, sort_keys=True)
        if key not in seen:
            seen.add(key)
            combined.append(item)

    return json.dumps(combined).replace("\\", "\\\\")


# global.ini is merged not overwritten -- preserve installguid, [crashhandler], [locations], and any input-overlay plugin settings; only force-set the perf/ux toggles below.

_ENFORCED_GLOBAL_INI = (
    ("General", "BrowserHWAccel",      "true"),
    ("Audio",   "DisableAudioDucking", "true"),
)


def _confirm_on_exit_value(prefs: Preferences) -> str:
    return "false" if bool(getattr(prefs, "disable_obs_close_warning", True)) else "true"


def apply_global_ini(text: str, prefs: Preferences) -> str:
    """merge the bundled global.ini into the users existing file -- preserves installguid, [crashhandler], [locations], input-overlay plugin settings; force-sets browserhwaccel + disableaudioducking."""
    existing_path = OBS_CONFIG / "global.ini"
    if existing_path.is_file():
        try:
            base = existing_path.read_text(encoding="utf-8")
        except UnicodeDecodeError:
            base = existing_path.read_text(encoding="latin-1")
    else:
        base = text  # fresh install -- start from the bundled defaults.

    for section, key, value in _ENFORCED_GLOBAL_INI:
        base = _set_ini_value(base, section, key, value)
    base = _set_ini_value(base, "General", "ConfirmOnExit", _confirm_on_exit_value(prefs))
    return base


# scenes json editing

_INPUT_OVERLAY_SOURCE_ID = "input-overlay"
_INPUT_OVERLAY_GROUP_NAME = "Group"
_INPUT_OVERLAY_WASD_NAME = "WASD Overlay"
_INPUT_OVERLAY_MOUSE_NAME = "Mouse Overlay"
_INPUT_OVERLAY_GROUP_W = 628.0
_INPUT_OVERLAY_GROUP_H = 292.0
_INPUT_OVERLAY_MOUSE_OFFSET_X = 431.0
_INPUT_OVERLAY_WASD_SOURCE_W = 568.0
_INPUT_OVERLAY_WASD_SOURCE_H = 394.0
_INPUT_OVERLAY_MOUSE_SOURCE_W = 285.0
_INPUT_OVERLAY_MOUSE_SOURCE_H = 421.0
_INPUT_OVERLAY_WASD_SCALE_X = 0.7395833134651184
_INPUT_OVERLAY_WASD_SCALE_Y = 0.7388888597488403
_INPUT_OVERLAY_MOUSE_SCALE = 0.6909722089767456
_INPUT_OVERLAY_POS_REF_H = 1080.0
_INPUT_OVERLAY_POS_X_AT_REF = 15.0
_INPUT_OVERLAY_BOTTOM_MARGIN_AT_REF = 16.0
_OVERLAY_OPACITY_FILTER_NAME = "ReplayKit Overlay Opacity"
_OVERLAY_OPACITY_FILTER_ID = "color_filter"
_BONGO_CAT_SOURCE_ID     = "bongobs-cat"
_BONGO_CAT_SOURCE_NAME   = "Bongo Cat Overlay"
_BONGO_CAT_SOURCE_UUID   = "c93f3934-0dfd-4f4f-96e4-0abf45423f0f"
_BONGO_CAT_SOURCE_W      = 1280.0
_BONGO_CAT_SOURCE_H      = 768.0
_BONGO_CAT_CANVAS_W      = 1920.0
_BONGO_CAT_CANVAS_H      = 1080.0
_BONGO_CAT_POS_REL_X     = -1.7777777777777777
_BONGO_CAT_POS_REL_Y     = 0.47962962962962963
_BONGO_CAT_SCALE_X       = 0.4892578125
_BONGO_CAT_SCALE_Y       = 0.48828125
_BONGO_CAT_ITEM_ID       = 13
_OVERLAY_OPACITY_FILTER_UUIDS = {
    _INPUT_OVERLAY_WASD_NAME: "a65fb4f0-a894-463e-9b9b-f0a9d5fb4fa1",
    _INPUT_OVERLAY_MOUSE_NAME: "c097fe72-641f-4da5-94f6-71f7c6353f9f",
    _BONGO_CAT_SOURCE_NAME: "4ecb70c4-e8f0-4207-a2cc-0307ff771722",
}
_MIC_SOURCE_ID           = "wasapi_input_capture"
_MONITOR_SOURCE_ID       = "monitor_capture"
_GROUP_SOURCE_ID         = "group"
_MOTION_BLUR_FILTER_NAME = "ReplayKit Motion Blur"
_MOTION_BLUR_FILTER_ID   = "shader_filter"
_RETIRED_MOTION_BLUR_FILTER_ID = "obs_composite_blur"
_GAME_CAPTURE_SOURCE_ID  = "game_capture"
_GAME_CAPTURE_SOURCE_NAME = "Game Capture"
_GAME_CAPTURE_HOOK_RATE_FAST = 2
_WINDOW_CAPTURE_SOURCE_ID = "window_capture"
_WINDOW_CAPTURE_SOURCE_NAME = "Window Capture"
_WINDOW_CAPTURE_SOURCE_UUID = "edb2d9d4-7b53-4f3a-a760-61cd03ce9b6c"
_DESKTOP_AUDIO_SOURCE_NAME = "Desktop Audio (excl. Discord)"
_DESKTOP_AUDIO_EXCLUDE_PROCESSES = [
    "Discord.exe",
    "DiscordSystemHelper.exe",
    "DiscordCanary.exe",
    "DiscordPTB.exe",
    "DiscordDevelopment.exe",
    "obs64.exe",
    "obs32.exe",
    "obs.exe",
    # obss cef subprocess -- excluded so clip playback audio in the dock isnt captured as desktop audio and monitored back into the discord share, doubling the copy discord already grabs from the same process tree.
    "obs-browser-page.exe",
]
_MOTION_BLUR_SOURCE_UUIDS = {
    "Display Capture": "e371efc8-8c99-44cb-95e7-94381d9c9e41",
    "Game Capture": "26bc5a11-5315-4390-b028-f77667c7fda3",
    "Window Capture": "9b73d6cb-b65e-44a1-895f-4e2f326a8d77",
}

# match an input-overlay preset path and capture the tail so it can be re-rooted.
_INPUT_OVERLAY_PATH_RE = re.compile(
    r"^.*?-presets[\\/](?P<tail>[^\r\n]+)$",
)

_OBS_BOUNDS_SCALE_INNER = 2  # libobs obs_bounds_type from scene-item-properties.cpp -- fit inside bounds, preserve aspect.

# single obs-visible script entry; feature scripts are loaded inside replaykit.lua to keep tools->scripts uncluttered.
_REPLAYKIT_SCRIPT_RELPATH = "obs-replayKit/scripts/replaykit.lua"


def _default_clip_dir_norm() -> str:
    """forward-slashed, lowercased ~/pictures/videos -- matches the lua and ps fallback."""
    from .config import USERPROFILE
    return (USERPROFILE / "Pictures" / "Videos").as_posix().lower()


def _apply_replaykit_runtime_settings(settings: dict, prefs: Preferences) -> None:
    """inject ReplayKit runtime settings into the managed OBS script entry."""
    rec_dir_norm = prefs.recording_path.replace("\\", "/").rstrip("/").lower()
    if rec_dir_norm == _default_clip_dir_norm():
        settings["clip_dir"] = ""
    else:
        # forward slashes so the value round-trips thru the lua json writer without re-escaping.
        settings["clip_dir"] = prefs.recording_path.replace("\\", "/")
    settings["clip_notification_enabled"] = bool(getattr(prefs, "clip_notification_enabled", True))
    settings["recording_notification_enabled"] = bool(getattr(prefs, "recording_notification_enabled", True))
    settings["clip_notification_seconds"] = int(prefs.replay_buffer_seconds)
    settings["clip_sound_volume"] = int(getattr(prefs, "clip_sound_volume", 100))
    settings["recording_sound_volume"] = int(getattr(prefs, "recording_sound_volume", 100))


def apply_replaykit_settings_json(_text: str, prefs: Preferences) -> str:
    """write the runtime settings consumed by the OBS dock helper."""
    rec_dir_norm = prefs.recording_path.replace("\\", "/").rstrip("/").lower()
    clip_dir = "" if rec_dir_norm == _default_clip_dir_norm() else prefs.recording_path.replace("\\", "/")
    return json.dumps({
        "recordingPreset": prefs.recording_preset,
        "compressionMode": prefs.compression_mode,
        "codecPreference": prefs.codec_preference,
        "replaySeconds": int(prefs.replay_buffer_seconds),
        "clipDir": clip_dir,
        "clipKeybind": prefs.clip_keybind,
        "recordingKeybind": prefs.recording_keybind,
        "overlayStyle": prefs.overlay_style,
        "overlayOpacity": int(getattr(prefs, "overlay_opacity", 100)),
        "overlayScale": int(getattr(prefs, "overlay_scale", 100)),
        "overlayHueShift": float(getattr(prefs, "overlay_hue_shift", 0.0)),
        "overlayColorMultiply": getattr(prefs, "overlay_color_multiply", "#ffffff"),
        "overlayColorAdd": getattr(prefs, "overlay_color_add", "#000000"),
        "obsStartupEnabled": bool(prefs.obs_startup_enabled),
        "disableObsCloseWarning": bool(getattr(prefs, "disable_obs_close_warning", True)),
        "allowSleepWhileActive": bool(getattr(prefs, "allow_sleep_while_active", True)),
        "clipNotificationEnabled": bool(prefs.clip_notification_enabled),
        "recordingNotificationEnabled": bool(prefs.recording_notification_enabled),
        "clipNotificationSeconds": int(prefs.replay_buffer_seconds),
        "trimPreciseDefault": bool(prefs.trim_precise_default),
        "debugLoggingEnabled": bool(getattr(prefs, "debug_logging_enabled", False)),
        "clipSoundVolume": int(getattr(prefs, "clip_sound_volume", 100)),
        "recordingSoundVolume": int(getattr(prefs, "recording_sound_volume", 100)),
        "shareMode": getattr(prefs, "share_mode", "projector"),
        "discord_screenshare_enabled": bool(getattr(prefs, "discord_screenshare_enabled", True)),
        "discord_output_mode": getattr(prefs, "discord_output_mode", "projector"),
        "discord_projector_enabled": bool(getattr(prefs, "discord_projector_enabled", True)),
        "discord_projector_width": int(getattr(prefs, "discord_projector_width", 0)),
        "discord_projector_height": int(getattr(prefs, "discord_projector_height", 0)),
        "discord_projector_visible_pixels": int(getattr(prefs, "discord_projector_visible_pixels", 1)),
        "discord_projector_monitor_index": int(getattr(prefs, "discord_projector_monitor_index", 0)),
        "discord_projector_edge": getattr(prefs, "discord_projector_edge", "bottom"),
        "discord_projector_title_hint": getattr(prefs, "discord_projector_title_hint", "OBS ReplayKit Discord Share"),
        "discord_projector_hide_taskbar": bool(getattr(prefs, "discord_projector_hide_taskbar", True)),
        "screenshareCaptureMode": getattr(prefs, "screenshare_capture_mode", "hybrid_auto"),
        "screenshareGameWindow": getattr(prefs, "screenshare_game_window", ""),
        "screenshareGameOverrides": list(getattr(prefs, "screenshare_game_overrides", [])),
        "screenshareAutoGameKeepFocused": bool(getattr(prefs, "screenshare_auto_game_keep_focused", False)),
        "motionBlurEnabled": bool(getattr(prefs, "motion_blur_enabled", False)),
        "motionBlurStrength": float(getattr(prefs, "motion_blur_strength", 0.075)),
    }, indent=2)


def _replaykit_script_path() -> str:
    return (OBS_CONFIG / _REPLAYKIT_SCRIPT_RELPATH).as_posix()


def _entry_basename(entry: dict) -> str:
    return str(entry.get("path", "")).replace("\\", "/").rsplit("/", 1)[-1].lower()


def _is_replaykit_entry(entry: dict) -> bool:
    return _entry_basename(entry) == Path(_REPLAYKIT_SCRIPT_RELPATH).name.lower()


def _collect_replaykit_settings(scripts: list, prefs: Preferences) -> dict:
    """preserve settings from the managed replaykit.lua script entry."""
    settings: dict = {}
    for entry in scripts:
        if not _is_replaykit_entry(entry):
            continue
        entry_settings = entry.get("settings")
        if not isinstance(entry_settings, dict):
            continue
        settings.update(entry_settings)
    _apply_replaykit_runtime_settings(settings, prefs)
    return settings


def _fit_scene_item_to_canvas(
    item: dict, canvas_w: float, canvas_h: float,
    source_w: int = 0, source_h: int = 0,
) -> None:
    """fit a scene item to canvas (obs ctrl+f equivalent). source_w/h zero -> bounds-based fallback for sources without known dimensions (game capture, no display detected)."""
    canvas_w = float(canvas_w)
    canvas_h = float(canvas_h)

    if source_w > 0 and source_h > 0:
        # explicit-scale path: compute scale + centered pos. modern obs prefers pos_rel/bounds_rel (canvas-relative) over absolute pos/bounds, so we set both.
        scale       = min(canvas_w / source_w, canvas_h / source_h)
        scaled_w    = source_w * scale
        scaled_h    = source_h * scale
        pos_x       = (canvas_w - scaled_w) / 2.0
        pos_y       = (canvas_h - scaled_h) / 2.0

        item["align"]        = 5  # obs_align_left | obs_align_top -- pos is top-left
        item["pos"]          = {"x": pos_x, "y": pos_y}
        item["pos_rel"]      = {
            "x": (pos_x - canvas_w / 2.0) / (canvas_h / 2.0),
            "y": (pos_y - canvas_h / 2.0) / (canvas_h / 2.0),
        }
        item["scale"]        = {"x": scale, "y": scale}
        item["scale_rel"]    = {"x": 1.0, "y": 1.0}
        item["scale_ref"]    = {"x": float(source_w), "y": float(source_h)}
        item["bounds"]       = {"x": 0.0, "y": 0.0}
        item["bounds_rel"]   = {"x": 0.0, "y": 0.0}
        item["bounds_type"]  = 0
        item["bounds_align"] = 0
    else:
        # bounds-based fit. also clears bounds_rel so the bounds actualy take effect.
        item["align"]        = 5
        item["pos"]          = {"x": 0.0, "y": 0.0}
        item["pos_rel"]      = {"x": -canvas_w / canvas_h, "y": -1.0}
        item["bounds"]       = {"x": canvas_w, "y": canvas_h}
        item["bounds_rel"]   = {"x": 2.0 * canvas_w / canvas_h, "y": 2.0}
        item["bounds_type"]  = _OBS_BOUNDS_SCALE_INNER
        item["bounds_align"] = 0


def _canvas_size(prefs: Preferences) -> tuple:
    """(basecx, basecy) that the selected preset writes into basic.ini."""
    video = _preset_video_ini(prefs)
    return int(video.get("BaseCX", 1920)), int(video.get("BaseCY", 1080))


def _rewrite_overlay_path(value: str) -> str:
    """re-root one io.overlay_image / io.layout_file under the installs preset folder. non-preset paths are left alone."""
    if not value:
        return value
    match = _INPUT_OVERLAY_PATH_RE.match(value.replace("\\", "/"))
    if match is None:
        return value
    tail = match.group("tail").lstrip("/\\")
    parts = [part for part in tail.replace("\\", "/").split("/") if part]
    if parts and parts[0].lower().startswith("input-overlay") and parts[0].lower().endswith("-presets"):
        tail = "/".join(parts[1:])
    return (INPUT_OVERLAY_TARGET / tail).as_posix()


def _resolve_overlay_paths(src: dict) -> None:
    """re-root the input-overlay sources image/layout settings in place."""
    settings = src.setdefault("settings", {})
    for key in ("io.overlay_image", "io.layout_file"):
        cur = settings.get(key)
        if isinstance(cur, str):
            settings[key] = _rewrite_overlay_path(cur)


def _collect_overlay_uuids(sources: list) -> set:
    """uuids of every input-overlay source in the scene file."""
    return {
        s.get("uuid") for s in sources
        if s.get("id") == _INPUT_OVERLAY_SOURCE_ID and s.get("uuid")
    }


def _collect_overlay_group_names(sources: list, groups: list) -> set:
    """names of groups containing input-overlay sources -- the new scene structure wraps the overlay sources in a group and toggles visibility on the group."""
    overlay_uuids = _collect_overlay_uuids(sources)
    if not overlay_uuids:
        return set()

    overlay_groups: set = set()
    for grp in groups:
        items = grp.get("settings", {}).get("items", [])
        for item in items:
            if item.get("source_uuid") in overlay_uuids:
                if grp.get("name"):
                    overlay_groups.add(grp["name"])
                break
    return overlay_groups


def _selected_overlay_style(prefs: Preferences) -> str:
    style = getattr(prefs, "overlay_style", "input_overlay")
    return style if style in ("input_overlay", "bongo_cat", "off") else "input_overlay"


def _is_input_overlay_group(group: dict, overlay_uuids: set) -> bool:
    for item in group.get("settings", {}).get("items", []):
        if item.get("source_uuid") in overlay_uuids:
            return True
    return False


def _remove_sources_by_uuid_or_id(sources: list, uuids: set, source_id: str) -> None:
    sources[:] = [
        src for src in sources
        if src.get("id") != source_id and src.get("uuid") not in uuids
    ]


def _remove_bongo_sources(sources: list) -> None:
    _remove_sources_by_uuid_or_id(sources, {_BONGO_CAT_SOURCE_UUID}, _BONGO_CAT_SOURCE_ID)


def _motion_blur_shader_path() -> str:
    return (PROGRAMFILES_OBS_DIR / "data" / "obs-plugins" / "obs-shaderfilter" / "examples" / "motion_blur.shader").as_posix()


def _motion_blur_filter_settings(strength: float) -> dict:
    return {
        "from_file": True,
        "shader_file_name": _motion_blur_shader_path(),
        "override_entire_effect": False,
        "strength": max(0.0, min(1.0, float(strength))),
    }


def _new_motion_blur_filter(source_name: str, enabled: bool, strength: float) -> dict:
    return {
        "prev_ver": 536936450,
        "name": _MOTION_BLUR_FILTER_NAME,
        "uuid": _MOTION_BLUR_SOURCE_UUIDS[source_name],
        "id": _MOTION_BLUR_FILTER_ID,
        "versioned_id": _MOTION_BLUR_FILTER_ID,
        "settings": _motion_blur_filter_settings(strength),
        "mixers": 0,
        "sync": 0,
        "flags": 0,
        "volume": 1.0,
        "balance": 0.5,
        "enabled": bool(enabled),
        "muted": False,
        "push-to-mute": False,
        "push-to-mute-delay": 0,
        "push-to-talk": False,
        "push-to-talk-delay": 0,
        "hotkeys": {},
        "deinterlace_mode": 0,
        "deinterlace_field_order": 0,
        "monitoring_type": 0,
        "private_settings": {},
    }


def _is_shaderfilter_motion_blur(filter_obj: dict) -> bool:
    settings = filter_obj.get("settings", {})
    shader_file = str(settings.get("shader_file_name", "")).replace("\\", "/").lower()
    return filter_obj.get("id") == _MOTION_BLUR_FILTER_ID and shader_file.endswith("/motion_blur.shader")


def _apply_motion_blur_filter(src: dict, enabled: bool, strength: float) -> None:
    source_name = str(src.get("name", ""))
    if source_name not in _MOTION_BLUR_SOURCE_UUIDS:
        return
    filters = src.get("filters")
    if not isinstance(filters, list):
        filters = []
        src["filters"] = filters

    found = False
    for filter_obj in filters:
        if (
            filter_obj.get("name") == _MOTION_BLUR_FILTER_NAME
            or filter_obj.get("uuid") == _MOTION_BLUR_SOURCE_UUIDS[source_name]
            or filter_obj.get("id") == _RETIRED_MOTION_BLUR_FILTER_ID
            or _is_shaderfilter_motion_blur(filter_obj)
        ):
            filter_obj.update(_new_motion_blur_filter(source_name, enabled, strength))
            found = True
            break
    if not found:
        filters.append(_new_motion_blur_filter(source_name, enabled, strength))


def _remove_motion_blur_filter(src: dict) -> None:
    filters = src.get("filters")
    if not isinstance(filters, list):
        return
    managed_uuid = _MOTION_BLUR_SOURCE_UUIDS.get(str(src.get("name", "")))
    filters[:] = [
        item for item in filters
        if (
            item.get("name") != _MOTION_BLUR_FILTER_NAME
            and item.get("uuid") != managed_uuid
            and item.get("id") != _RETIRED_MOTION_BLUR_FILTER_ID
            and not _is_shaderfilter_motion_blur(item)
        )
    ]
    if not filters:
        src.pop("filters", None)


def _new_bongo_source() -> dict:
    return {
        "prev_ver": 536936450,
        "name": _BONGO_CAT_SOURCE_NAME,
        "uuid": _BONGO_CAT_SOURCE_UUID,
        "id": _BONGO_CAT_SOURCE_ID,
        "versioned_id": _BONGO_CAT_SOURCE_ID,
        "settings": {
            "Mode": "standard",
            "width": int(_BONGO_CAT_SOURCE_W),
            "height": int(_BONGO_CAT_SOURCE_H),
            "x": 0.0,
            "y": 0.02,
            "scale": 1.83,
            "delay": 1.0,
            "delaytime": 1.0,
            "random_motion": True,
            "breath": True,
            "eyeblink": True,
            "track": True,
            "live2d": True,
            "relative_mouse": True,
            "mouse_horizontal_flip": True,
            "mouse_vertical_flip": True,
            "mask": False,
        },
        "mixers": 0,
        "sync": 0,
        "flags": 0,
        "volume": 1.0,
        "balance": 0.5,
        "enabled": True,
        "muted": False,
        "push-to-mute": False,
        "push-to-mute-delay": 0,
        "push-to-talk": False,
        "push-to-talk-delay": 0,
        "hotkeys": {},
        "deinterlace_mode": 0,
        "deinterlace_field_order": 0,
        "monitoring_type": 0,
        "private_settings": {},
    }


def _ensure_bongo_source(sources: list) -> None:
    for src in sources:
        if src.get("uuid") == _BONGO_CAT_SOURCE_UUID or src.get("id") == _BONGO_CAT_SOURCE_ID:
            src["name"] = _BONGO_CAT_SOURCE_NAME
            src["id"] = _BONGO_CAT_SOURCE_ID
            src["versioned_id"] = _BONGO_CAT_SOURCE_ID
            src["enabled"] = True
            settings = src.setdefault("settings", {})
            for key, value in _new_bongo_source()["settings"].items():
                settings[key] = value
            return
    sources.append(_new_bongo_source())


def _window_capture_input_settings(prefs: Preferences) -> dict:
    return {
        "window": getattr(prefs, "screenshare_game_window", ""),
        "method": 2,
        "priority": 0,
        "cursor": True,
        "client_area": True,
        "compatibility": False,
        "force_sdr": False,
        "capture_audio": False,
    }


def _new_window_capture_source(prefs: Preferences) -> dict:
    return {
        "prev_ver": 536936450,
        "name": _WINDOW_CAPTURE_SOURCE_NAME,
        "uuid": _WINDOW_CAPTURE_SOURCE_UUID,
        "id": _WINDOW_CAPTURE_SOURCE_ID,
        "versioned_id": _WINDOW_CAPTURE_SOURCE_ID,
        "settings": _window_capture_input_settings(prefs),
        "mixers": 0,
        "sync": 0,
        "flags": 0,
        "volume": 1.0,
        "balance": 0.5,
        "enabled": True,
        "muted": False,
        "push-to-mute": False,
        "push-to-mute-delay": 0,
        "push-to-talk": False,
        "push-to-talk-delay": 0,
        "hotkeys": {},
        "deinterlace_mode": 0,
        "deinterlace_field_order": 0,
        "monitoring_type": 0,
        "private_settings": {},
    }


def _ensure_window_capture_source(sources: list, prefs: Preferences) -> None:
    for src in sources:
        if src.get("uuid") == _WINDOW_CAPTURE_SOURCE_UUID or src.get("name") == _WINDOW_CAPTURE_SOURCE_NAME:
            src["name"] = _WINDOW_CAPTURE_SOURCE_NAME
            src["uuid"] = _WINDOW_CAPTURE_SOURCE_UUID
            src["id"] = _WINDOW_CAPTURE_SOURCE_ID
            src["versioned_id"] = _WINDOW_CAPTURE_SOURCE_ID
            src["enabled"] = True
            settings = src.setdefault("settings", {})
            settings.update(_window_capture_input_settings(prefs))
            return
    sources.append(_new_window_capture_source(prefs))


def _scene_item_ids(items: list) -> set:
    ids: set = set()
    for item in items:
        try:
            ids.add(int(item.get("id")))
        except (TypeError, ValueError):
            continue
    return ids


def _next_scene_item_id(items: list) -> int:
    used = _scene_item_ids(items)
    candidate = _BONGO_CAT_ITEM_ID
    while candidate in used:
        candidate += 1
    return candidate


def _move_scene_item_after(items: list, target_name: str, after_name: str) -> None:
    target = None
    for item in items:
        if item.get("name") == target_name:
            target = item
            break
    if target is None:
        return
    items.remove(target)
    insert_at = 0
    for idx, item in enumerate(items):
        if item.get("name") == after_name:
            insert_at = idx + 1
            break
    items.insert(insert_at, target)


def _new_capture_scene_item(
    source_name: str,
    source_uuid: str,
    item_id: int,
    canvas_w: float,
    canvas_h: float,
    visible: bool,
) -> dict:
    item = {
        "name": source_name,
        "source_uuid": source_uuid,
        "visible": bool(visible),
        "locked": False,
        "rot": 0.0,
        "align": 5,
        "bounds_type": 0,
        "bounds_align": 0,
        "bounds_crop": False,
        "crop_left": 0,
        "crop_top": 0,
        "crop_right": 0,
        "crop_bottom": 0,
        "id": item_id,
        "group_item_backup": False,
        "scale_filter": "disable",
        "blend_method": "default",
        "blend_type": "normal",
        "show_transition": {"duration": 300},
        "hide_transition": {"duration": 300},
        "private_settings": {},
    }
    _fit_scene_item_to_canvas(item, canvas_w, canvas_h)
    return item


def _scene_rel_pos(x: float, y: float, canvas_w: float, canvas_h: float) -> dict:
    return {
        "x": (x - canvas_w / 2.0) / (canvas_h / 2.0),
        "y": (y - canvas_h / 2.0) / (canvas_h / 2.0),
    }


def _overlay_scale_factor(prefs: Preferences) -> float:
    try:
        scale = int(getattr(prefs, "overlay_scale", 100))
    except (TypeError, ValueError):
        scale = 100
    return max(50, min(200, scale)) / 100.0


def _overlay_opacity(prefs: Preferences) -> int:
    try:
        opacity = int(getattr(prefs, "overlay_opacity", 100))
    except (TypeError, ValueError):
        opacity = 100
    return max(0, min(100, opacity))


def _overlay_hue_shift(prefs: Preferences) -> float:
    try:
        hue_shift = float(getattr(prefs, "overlay_hue_shift", 0.0))
    except (TypeError, ValueError):
        hue_shift = 0.0
    return max(-180.0, min(180.0, hue_shift))


def _overlay_hex_color(prefs: Preferences, attr: str, default: str) -> str:
    value = str(getattr(prefs, attr, default) or "").strip().lower()
    if len(value) == 7 and value[0] == "#" and all(ch in "0123456789abcdef" for ch in value[1:]):
        return value
    return default


def _overlay_color_value(hex_color: str) -> int:
    red = int(hex_color[1:3], 16)
    green = int(hex_color[3:5], 16)
    blue = int(hex_color[5:7], 16)
    return red | (green << 8) | (blue << 16)


def _has_overlay_color_adjustments(prefs: Preferences) -> bool:
    return (
        abs(_overlay_hue_shift(prefs)) >= 0.001
        or _overlay_hex_color(prefs, "overlay_color_multiply", "#ffffff") != "#ffffff"
        or _overlay_hex_color(prefs, "overlay_color_add", "#000000") != "#000000"
    )


def _overlay_content_rect(
    canvas_w: float, canvas_h: float, capture_w: int = 0, capture_h: int = 0
) -> tuple[float, float, float, float, float]:
    if canvas_w <= 0.0 or canvas_h <= 0.0:
        canvas_w, canvas_h = 1920.0, 1080.0
    source_w = float(capture_w) if capture_w > 0 else canvas_w
    source_h = float(capture_h) if capture_h > 0 else canvas_h
    scale = min(canvas_w / source_w, canvas_h / source_h)
    if scale <= 0.0:
        scale = 1.0
    width = source_w * scale
    height = source_h * scale
    return (canvas_w - width) / 2.0, (canvas_h - height) / 2.0, width, height, scale


def _input_overlay_pos(
    content: tuple[float, float, float, float, float],
    source_w: float,
    source_h: float,
    scale_x: float,
    scale_y: float,
) -> tuple[float, float]:
    content_x, content_y, _content_w, content_h, _content_scale = content
    ref_scale = content_h / _INPUT_OVERLAY_POS_REF_H
    x = content_x + (_INPUT_OVERLAY_POS_X_AT_REF * ref_scale)
    y = content_y + content_h - (source_h * scale_y) - (_INPUT_OVERLAY_BOTTOM_MARGIN_AT_REF * ref_scale)
    return max(0.0, x), max(0.0, y)


def _bottom_left_corner_overlay_pos(
    content: tuple[float, float, float, float, float],
    source_w: float,
    source_h: float,
    scale_x: float,
    scale_y: float,
) -> tuple[float, float]:
    content_x, content_y, _content_w, content_h, _content_scale = content
    x = content_x
    y = content_y + content_h - (source_h * scale_y)
    return max(0.0, x), max(0.0, y)


def _overlay_opacity_filter_settings(prefs: Preferences, legacy_percent: bool = False) -> dict:
    opacity = _overlay_opacity(prefs)
    settings = {
        "hue_shift": _overlay_hue_shift(prefs),
    }
    if legacy_percent:
        settings["opacity"] = opacity
        settings["color"] = _overlay_color_value(
            _overlay_hex_color(prefs, "overlay_color_multiply", "#ffffff")
        )
        return settings
    settings.update({
        "opacity": max(0.0, min(1.0, opacity / 100.0)),
        "color_multiply": _overlay_color_value(
            _overlay_hex_color(prefs, "overlay_color_multiply", "#ffffff")
        ),
        "color_add": _overlay_color_value(
            _overlay_hex_color(prefs, "overlay_color_add", "#000000")
        ),
    })
    return settings


def _new_overlay_opacity_filter(source_name: str, prefs: Preferences) -> dict:
    return {
        "prev_ver": 536936450,
        "name": _OVERLAY_OPACITY_FILTER_NAME,
        "uuid": _OVERLAY_OPACITY_FILTER_UUIDS[source_name],
        "id": _OVERLAY_OPACITY_FILTER_ID,
        "versioned_id": "color_filter_v2",
        "settings": _overlay_opacity_filter_settings(prefs),
        "mixers": 0,
        "sync": 0,
        "flags": 0,
        "volume": 1.0,
        "balance": 0.5,
        "enabled": True,
        "muted": False,
        "push-to-mute": False,
        "push-to-mute-delay": 0,
        "push-to-talk": False,
        "push-to-talk-delay": 0,
        "hotkeys": {},
        "deinterlace_mode": 0,
        "deinterlace_field_order": 0,
        "monitoring_type": 0,
        "private_settings": {},
    }


def _apply_overlay_opacity_filter(src: dict, prefs: Preferences) -> None:
    source_name = str(src.get("name", ""))
    if source_name not in _OVERLAY_OPACITY_FILTER_UUIDS:
        return
    opacity = _overlay_opacity(prefs)
    has_color_adjustments = _has_overlay_color_adjustments(prefs)
    managed_uuid = _OVERLAY_OPACITY_FILTER_UUIDS[source_name]
    filters = src.get("filters")
    if not isinstance(filters, list):
        filters = []

    def is_managed_opacity_filter(item: dict) -> bool:
        return (
            item.get("name") == _OVERLAY_OPACITY_FILTER_NAME
            or item.get("uuid") == managed_uuid
        )

    managed_filter = None
    kept_filters = []
    for item in filters:
        if is_managed_opacity_filter(item):
            if managed_filter is None:
                managed_filter = item
            continue
        kept_filters.append(item)

    if managed_filter is not None:
        filter_settings = managed_filter.setdefault("settings", {})
        versioned_id = str(managed_filter.get("versioned_id", ""))
        if managed_filter.get("id") == _OVERLAY_OPACITY_FILTER_ID and versioned_id != "color_filter_v2":
            filter_settings.update(_overlay_opacity_filter_settings(prefs, True))
        else:
            filter_settings.update(_overlay_opacity_filter_settings(prefs))
        managed_filter["enabled"] = True
        kept_filters.append(managed_filter)
    elif opacity < 100 or has_color_adjustments:
        kept_filters.append(_new_overlay_opacity_filter(source_name, prefs))

    if kept_filters:
        src["filters"] = kept_filters
    else:
        src.pop("filters", None)


def _remove_overlay_opacity_filter(src: dict) -> None:
    source_name = str(src.get("name", ""))
    managed_uuid = _OVERLAY_OPACITY_FILTER_UUIDS.get(source_name)
    filters = src.get("filters")
    if not isinstance(filters, list):
        return
    filters[:] = [
        item for item in filters
        if (
            item.get("name") != _OVERLAY_OPACITY_FILTER_NAME
            and (not managed_uuid or item.get("uuid") != managed_uuid)
        )
    ]
    if not filters:
        src.pop("filters", None)


def _input_overlay_group_geometry(
    canvas_w: float,
    canvas_h: float,
    prefs: Preferences,
    content: tuple[float, float, float, float, float] | None = None,
) -> tuple[float, float, float]:
    if content is None:
        content = _overlay_content_rect(canvas_w, canvas_h)
    content_h = content[3]
    scale = (content_h / 1440.0) * _overlay_scale_factor(prefs)
    x, y = _input_overlay_pos(
        content, _INPUT_OVERLAY_GROUP_W, _INPUT_OVERLAY_GROUP_H, scale, scale
    )
    return x, y, scale


def _apply_input_overlay_scene_item_geometry(
    item: dict,
    name: str,
    canvas_w: float,
    canvas_h: float,
    prefs: Preferences,
    content: tuple[float, float, float, float, float] | None = None,
) -> None:
    group_x, group_y, group_scale = _input_overlay_group_geometry(canvas_w, canvas_h, prefs, content)
    if name == _INPUT_OVERLAY_MOUSE_NAME:
        source_w = _INPUT_OVERLAY_MOUSE_SOURCE_W
        source_h = _INPUT_OVERLAY_MOUSE_SOURCE_H
        scale_x = _INPUT_OVERLAY_MOUSE_SCALE * group_scale
        scale_y = _INPUT_OVERLAY_MOUSE_SCALE * group_scale
        x = group_x + (_INPUT_OVERLAY_MOUSE_OFFSET_X * group_scale)
    else:
        source_w = _INPUT_OVERLAY_WASD_SOURCE_W
        source_h = _INPUT_OVERLAY_WASD_SOURCE_H
        scale_x = _INPUT_OVERLAY_WASD_SCALE_X * group_scale
        scale_y = _INPUT_OVERLAY_WASD_SCALE_Y * group_scale
        x = group_x
    y = group_y
    item["align"] = 5
    item["pos"] = {"x": x, "y": y}
    item["pos_rel"] = _scene_rel_pos(x, y, canvas_w, canvas_h)
    item["scale"] = {"x": scale_x, "y": scale_y}
    item["scale_rel"] = {"x": scale_x * 1440.0 / canvas_h, "y": scale_y * 1440.0 / canvas_h}
    item["scale_ref"] = {"x": source_w, "y": source_h}
    item["bounds"] = {"x": 0.0, "y": 0.0}
    item["bounds_rel"] = {"x": 0.0, "y": 0.0}
    item["bounds_type"] = 0
    item["bounds_align"] = 0


def _apply_input_overlay_group_geometry(
    item: dict,
    canvas_w: float,
    canvas_h: float,
    prefs: Preferences,
    content: tuple[float, float, float, float, float] | None = None,
) -> None:
    x, y, scale = _input_overlay_group_geometry(canvas_w, canvas_h, prefs, content)
    item["align"] = 5
    item["pos"] = {"x": x, "y": y}
    item["pos_rel"] = _scene_rel_pos(x, y, canvas_w, canvas_h)
    item["scale"] = {"x": scale, "y": scale}
    scale_rel = _overlay_scale_factor(prefs)
    item["scale_rel"] = {"x": scale_rel, "y": scale_rel}
    item["scale_ref"] = {"x": canvas_w, "y": canvas_h}
    item["bounds"] = {"x": 0.0, "y": 0.0}
    item["bounds_rel"] = {"x": 0.0, "y": 0.0}
    item["bounds_type"] = 0
    item["bounds_align"] = 0


def _apply_bongo_geometry(
    item: dict,
    canvas_w: float,
    canvas_h: float,
    prefs: Preferences,
    content: tuple[float, float, float, float, float] | None = None,
) -> None:
    if content is None:
        content = _overlay_content_rect(canvas_w, canvas_h)
    content_h = content[3]
    scale_ratio = (content_h / _BONGO_CAT_CANVAS_H) * _overlay_scale_factor(prefs)
    scale_x = _BONGO_CAT_SCALE_X * scale_ratio
    scale_y = _BONGO_CAT_SCALE_Y * scale_ratio
    scale_rel_x = scale_x * _BONGO_CAT_SOURCE_H / max(1.0, canvas_h)
    scale_rel_y = scale_y * _BONGO_CAT_SOURCE_H / max(1.0, canvas_h)
    x, y = _bottom_left_corner_overlay_pos(content, _BONGO_CAT_SOURCE_W, _BONGO_CAT_SOURCE_H, scale_x, scale_y)
    x = 0.0 if abs(x) < 0.001 else x
    y = 0.0 if abs(y) < 0.001 else y

    item.update({
        "name": _BONGO_CAT_SOURCE_NAME,
        "source_uuid": _BONGO_CAT_SOURCE_UUID,
        "visible": True,
        "locked": False,
        "rot": 0.0,
        "scale_ref": {"x": _BONGO_CAT_SOURCE_W, "y": _BONGO_CAT_SOURCE_H},
        "align": 5,
        "bounds_type": 0,
        "bounds_align": 0,
        "bounds_crop": False,
        "crop_left": 0,
        "crop_top": 0,
        "crop_right": 0,
        "crop_bottom": 0,
        "group_item_backup": False,
        "pos": {"x": x, "y": y},
        "pos_rel": _scene_rel_pos(x, y, canvas_w, canvas_h),
        "scale": {"x": scale_x, "y": scale_y},
        "scale_rel": {"x": scale_rel_x, "y": scale_rel_y},
        "bounds": {"x": 0.0, "y": 0.0},
        "bounds_rel": {"x": 0.0, "y": 0.0},
        "scale_filter": "disable",
        "blend_method": "default",
        "blend_type": "normal",
        "show_transition": {"duration": 300},
        "hide_transition": {"duration": 300},
        "private_settings": {},
    })


def _new_bongo_scene_item(
    item_id: int,
    canvas_w: float,
    canvas_h: float,
    prefs: Preferences,
    content: tuple[float, float, float, float, float] | None = None,
) -> dict:
    item = {"id": item_id}
    _apply_bongo_geometry(item, canvas_w, canvas_h, prefs, content)
    return item


def _ensure_bongo_scene_item(
    items: list,
    settings: dict,
    canvas_w: float,
    canvas_h: float,
    prefs: Preferences,
    content: tuple[float, float, float, float, float] | None = None,
) -> None:
    for item in items:
        if item.get("source_uuid") == _BONGO_CAT_SOURCE_UUID or item.get("name") == _BONGO_CAT_SOURCE_NAME:
            _apply_bongo_geometry(item, canvas_w, canvas_h, prefs, content)
            return

    items.append(_new_bongo_scene_item(_next_scene_item_id(items), canvas_w, canvas_h, prefs, content))
    try:
        settings["id_counter"] = max(int(settings.get("id_counter", 0)), max(_scene_item_ids(items)))
    except (TypeError, ValueError):
        settings["id_counter"] = max(_scene_item_ids(items))


def _game_capture_input_settings(prefs: Preferences) -> dict:
    return {
        "capture_audio": False,
        "hook_rate": _GAME_CAPTURE_HOOK_RATE_FAST,
        "limit_framerate": True,
        "capture_cursor": False,
        "capture_overlays": False,
        "anti_cheat_hook": True,
        "capture_mode": "any_fullscreen",
        "window": "",
    }


def _apply_game_capture_defaults(source: dict, prefs: Preferences) -> None:
    if source.get("id") != _GAME_CAPTURE_SOURCE_ID and source.get("name") != _GAME_CAPTURE_SOURCE_NAME:
        return
    settings = source.setdefault("settings", {})
    settings.update(_game_capture_input_settings(prefs))


def _apply_desktop_audio_exclusions(source: dict) -> None:
    if source.get("id") != "audio_capture" or source.get("name") != _DESKTOP_AUDIO_SOURCE_NAME:
        return
    settings = source.setdefault("settings", {})
    settings["mode"] = "session"
    settings["executable_list"] = [{"value": name} for name in _DESKTOP_AUDIO_EXCLUDE_PROCESSES]
    settings["exclude"] = True


def apply_scenes_json(text: str, prefs: Preferences) -> str:
    """apply user prefs to the scenes file: mic device, display id, selected overlay, display + game capture fit-to-canvas, scripts-tool replaykit entry."""
    data    = json.loads(text)
    sources = data.get("sources", [])
    groups  = data.get("groups", [])

    primary = primary_display()
    canvas_w, canvas_h = _canvas_size(prefs)
    overlay_style       = _selected_overlay_style(prefs)
    use_input_overlay   = overlay_style == "input_overlay"
    use_bongo_cat       = overlay_style == "bongo_cat"
    use_motion_blur     = bool(getattr(prefs, "motion_blur_enabled", False))
    motion_blur_strength = float(getattr(prefs, "motion_blur_strength", 0.075))
    overlay_uuids       = _collect_overlay_uuids(sources)
    overlay_group_names = _collect_overlay_group_names(sources, groups)

    if not use_input_overlay:
        _remove_sources_by_uuid_or_id(sources, overlay_uuids, _INPUT_OVERLAY_SOURCE_ID)
        groups[:] = [
            group for group in groups
            if group.get("name") not in overlay_group_names and not _is_input_overlay_group(group, overlay_uuids)
        ]

    if use_bongo_cat:
        _ensure_bongo_source(sources)
    else:
        _remove_bongo_sources(sources)
    _ensure_window_capture_source(sources, prefs)

    primary_w = primary.width  if primary is not None else 0
    primary_h = primary.height if primary is not None else 0
    overlay_content = _overlay_content_rect(canvas_w, canvas_h, primary_w, primary_h)

    for src in sources:
        sid = src.get("id")

        if sid == _MIC_SOURCE_ID:
            src.setdefault("settings", {})["device_id"] = prefs.microphone_device_id

        elif sid == _MONITOR_SOURCE_ID and primary is not None:
            settings = src.setdefault("settings", {})
            settings["monitor_id"] = primary.device_id
            settings["capture_cursor"] = False

        _apply_game_capture_defaults(src, prefs)
        if src.get("name") == _WINDOW_CAPTURE_SOURCE_NAME:
            src["id"] = _WINDOW_CAPTURE_SOURCE_ID
            src["versioned_id"] = _WINDOW_CAPTURE_SOURCE_ID
            src.setdefault("settings", {}).update(_window_capture_input_settings(prefs))
        _apply_desktop_audio_exclusions(src)

        if src.get("name") in _MOTION_BLUR_SOURCE_UUIDS:
            if use_motion_blur:
                _apply_motion_blur_filter(src, True, motion_blur_strength)
            else:
                _remove_motion_blur_filter(src)

        if sid == _INPUT_OVERLAY_SOURCE_ID:
            src["enabled"] = use_input_overlay
            _resolve_overlay_paths(src)
            if use_input_overlay:
                _apply_overlay_opacity_filter(src, prefs)
            else:
                _remove_overlay_opacity_filter(src)

        elif sid == _BONGO_CAT_SOURCE_ID or src.get("name") == _BONGO_CAT_SOURCE_NAME:
            if use_bongo_cat:
                _apply_overlay_opacity_filter(src, prefs)

        elif sid == "scene":
            settings = src.get("settings", {})
            items = settings.get("items", [])
            kept_items = []
            mode = getattr(prefs, "screenshare_capture_mode", "hybrid_auto")
            found_window_capture = False
            for item in items:
                name = str(item.get("name", ""))
                # match the overlay scene item by source_uuid so renaming the source doesnt break the toggle.
                if item.get("source_uuid") in overlay_uuids:
                    if use_input_overlay:
                        item["visible"] = True
                        _apply_input_overlay_scene_item_geometry(item, name, canvas_w, canvas_h, prefs, overlay_content)
                        kept_items.append(item)
                    continue
                # group-style structure: hide/show by group name (groups dont expose source_uuid here).
                if name in overlay_group_names:
                    if use_input_overlay:
                        item["visible"] = True
                        _apply_input_overlay_group_geometry(item, canvas_w, canvas_h, prefs, overlay_content)
                        kept_items.append(item)
                    continue
                if item.get("source_uuid") == _BONGO_CAT_SOURCE_UUID or name == _BONGO_CAT_SOURCE_NAME:
                    if use_bongo_cat:
                        _apply_bongo_geometry(item, canvas_w, canvas_h, prefs, overlay_content)
                        kept_items.append(item)
                    continue
                if name == "Display Capture":
                    item["visible"] = mode in ("hybrid_auto", "desktop")
                    _fit_scene_item_to_canvas(
                        item, canvas_w, canvas_h, primary_w, primary_h
                    )
                elif name == _WINDOW_CAPTURE_SOURCE_NAME:
                    found_window_capture = True
                    item["source_uuid"] = _WINDOW_CAPTURE_SOURCE_UUID
                    item["visible"] = mode == "game_window"
                    _fit_scene_item_to_canvas(item, canvas_w, canvas_h)
                elif name == "Game Capture":
                    item["visible"] = mode == "game_auto"
                    # game capture has no install-time source dims -- bounds-based fit lets obs scale at runtime.
                    _fit_scene_item_to_canvas(item, canvas_w, canvas_h)
                kept_items.append(item)

            if not found_window_capture:
                window_item = _new_capture_scene_item(
                    _WINDOW_CAPTURE_SOURCE_NAME,
                    _WINDOW_CAPTURE_SOURCE_UUID,
                    _next_scene_item_id(kept_items),
                    canvas_w,
                    canvas_h,
                    mode == "game_window",
                )
                insert_at = 0
                for idx, item in enumerate(kept_items):
                    if item.get("name") == "Display Capture":
                        insert_at = idx + 1
                        break
                kept_items.insert(insert_at, window_item)
                try:
                    settings["id_counter"] = max(int(settings.get("id_counter", 0)), max(_scene_item_ids(kept_items)))
                except (TypeError, ValueError):
                    settings["id_counter"] = max(_scene_item_ids(kept_items))
            _move_scene_item_after(kept_items, _WINDOW_CAPTURE_SOURCE_NAME, "Display Capture")

            if use_bongo_cat:
                _ensure_bongo_scene_item(kept_items, settings, canvas_w, canvas_h, prefs, overlay_content)
            settings["items"] = kept_items

    modules = data.setdefault("modules", {})
    scripts = modules.setdefault("scripts-tool", [])
    replaykit_settings = _collect_replaykit_settings(scripts, prefs)

    scripts = [e for e in scripts if not _is_replaykit_entry(e)]
    scripts.append({
        "path": _replaykit_script_path(),
        "settings": replaykit_settings,
    })

    modules["scripts-tool"] = scripts

    return json.dumps(data, indent=4)


# (rel_path within assets/obs-studio) -> transformer
_DISPATCH = {
    "basic/profiles/Untitled/basic.ini":          apply_basic_ini,
    "basic/profiles/Untitled/recordEncoder.json": apply_record_encoder_json,
    "basic/scenes/Untitled.json":                 apply_scenes_json,
    "obs-replayKit/scripts/replaykit_settings.json": apply_replaykit_settings_json,
    "user.ini":                                   apply_user_ini,
    "global.ini":                                 apply_global_ini,
}


def apply_preferences(rel_path: Path, content: str, prefs: Preferences) -> str:
    """rewrite content according to prefs if a transformer is registered, else return as-is."""
    transformer = _DISPATCH.get(rel_path.as_posix())
    if transformer is None:
        return content
    return transformer(content, prefs)

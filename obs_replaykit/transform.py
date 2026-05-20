"""splice user prefs into bundled obs config files (basic.ini, recordencoder.json, scenes json, user.ini, global.ini)."""

import json
import re
from pathlib import Path
from typing import Optional

from .config import INPUT_OVERLAY_TARGET, OBS_CONFIG
from .display import primary_display
from .dock import DOCK_TARGET
from .encoder import EncoderChoice, pick_encoder
from .gpu import primary_gpu
from .keybind import to_basic_ini_value
from .prefs import Preferences
from .recording import get_preset


# targeted regex over configparser becuase obss basic.ini has formatting we want byte-identical (no spaces around =, blank lines, base64 blobs in geometry=).

def set_ini_value(text: str, section: str, key: str, value: str) -> str:
    """Public alias for _set_ini_value."""
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


def apply_basic_ini(text: str, prefs: Preferences) -> str:
    preset = get_preset(prefs.recording_preset)
    for section, items in preset.basic_ini.items():
        for key, value in items.items():
            text = _set_ini_value(text, section, key, value)

    # advout.recencoder must match the encoder whose settings we write into recordencoder.json.
    encoder = _resolve_encoder(prefs)
    text = _set_ini_value(text, "AdvOut", "RecEncoder", encoder.obs_encoder_id)

    text = _set_ini_value(text, "AdvOut", "RecRB",     "true")
    text = _set_ini_value(text, "AdvOut", "RecRBTime", str(prefs.replay_buffer_seconds))
    text = _set_ini_value(text, "AdvOut", "RecRBSize", str(preset.rec_rb_size_mb))

    # forward slashes -- obs accepts them in ini values and skips escaping.
    rec_path = prefs.recording_path.replace("\\", "/")
    text = _set_ini_value(text, "SimpleOutput", "FilePath",    rec_path)
    text = _set_ini_value(text, "AdvOut",       "RecFilePath", rec_path)
    text = _set_ini_value(text, "AdvOut",       "FFFilePath",  rec_path)

    text = _set_ini_value(
        text, "Hotkeys", "ReplayBuffer", to_basic_ini_value(prefs.clip_keybind)
    )

    return text


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
    """Match the managed OBS ReplayKit dock entry."""
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


def apply_user_ini(text: str, _prefs: Preferences) -> str:
    """Write one canonical Custom Controls dock entry and reset stale dock layout state."""

    section_re = re.compile(
        r"(\[BasicWindow\][^\[]*?\n)ExtraBrowserDocks=([^\r\n]*)",
        flags=re.DOTALL,
    )
    m = section_re.search(text)
    if m:
        # Fold the live ini into the bundle seed so user-added docks are preserved.
        live_value = _read_live_user_ini_value("ExtraBrowserDocks") or ""
        seed = m.group(2)
        if live_value and live_value != seed:
            seed = _combine_extra_browser_docks(seed, live_value)
        merged = _merge_extra_browser_docks_value(seed)
        return section_re.sub(
            lambda _m: f"{m.group(1)}ExtraBrowserDocks={merged}", text, count=1
        )

    return _set_ini_value(text, "BasicWindow", "ExtraBrowserDocks", _extra_browser_docks_value())


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


def apply_global_ini(text: str, _prefs: Preferences) -> str:
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
    return base


# scenes json editing

_INPUT_OVERLAY_SOURCE_ID = "input-overlay"
_BONGO_CAT_SOURCE_ID     = "bongobs-cat"
_BONGO_CAT_SOURCE_NAME   = "Bongo Cat Overlay"
_BONGO_CAT_SOURCE_UUID   = "c93f3934-0dfd-4f4f-96e4-0abf45423f0f"
_BONGO_CAT_SOURCE_W      = 1280.0
_BONGO_CAT_SOURCE_H      = 768.0
_BONGO_CAT_CANVAS_W      = 1920.0
_BONGO_CAT_CANVAS_H      = 1080.0
_BONGO_CAT_POS_REL_X     = -1.7777777910232544
_BONGO_CAT_POS_REL_Y     = 0.6296296119689941
_BONGO_CAT_SCALE_X       = 0.26093751192092896
_BONGO_CAT_SCALE_Y       = 0.2604166567325592
_BONGO_CAT_SCALE_REL_X   = 0.185555562376976
_BONGO_CAT_SCALE_REL_Y   = 0.18518517911434174
_BONGO_CAT_ITEM_ID       = 13
_MIC_SOURCE_ID           = "wasapi_input_capture"
_MONITOR_SOURCE_ID       = "monitor_capture"
_GROUP_SOURCE_ID         = "group"

# Match an input-overlay preset path and capture the tail so it can be re-rooted.
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


def _apply_streamable_settings(settings: dict, prefs: Preferences) -> None:
    """Inject ReplayKit runtime settings into the managed OBS script entry."""
    rec_dir_norm = prefs.recording_path.replace("\\", "/").rstrip("/").lower()
    if rec_dir_norm == _default_clip_dir_norm():
        settings["clip_dir"] = ""
    else:
        # forward slashes so the value round-trips thru the lua json writer without re-escaping.
        settings["clip_dir"] = prefs.recording_path.replace("\\", "/")
    settings["clip_notification_enabled"] = bool(getattr(prefs, "clip_notification_enabled", True))
    settings["clip_notification_seconds"] = int(prefs.replay_buffer_seconds)


def _replaykit_script_path() -> str:
    return (OBS_CONFIG / _REPLAYKIT_SCRIPT_RELPATH).as_posix()


def _entry_basename(entry: dict) -> str:
    return str(entry.get("path", "")).replace("\\", "/").rsplit("/", 1)[-1].lower()


def _is_replaykit_entry(entry: dict) -> bool:
    return _entry_basename(entry) == Path(_REPLAYKIT_SCRIPT_RELPATH).name.lower()


def _collect_replaykit_settings(scripts: list, prefs: Preferences) -> dict:
    """Preserve settings from the managed replaykit.lua script entry."""
    settings: dict = {}
    for entry in scripts:
        if not _is_replaykit_entry(entry):
            continue
        entry_settings = entry.get("settings")
        if not isinstance(entry_settings, dict):
            continue
        settings.update(entry_settings)
    _apply_streamable_settings(settings, prefs)
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
    preset = get_preset(prefs.recording_preset)
    video  = preset.basic_ini.get("Video", {})
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


def _new_bongo_source() -> dict:
    return {
        "prev_ver": 536936450,
        "name": _BONGO_CAT_SOURCE_NAME,
        "uuid": _BONGO_CAT_SOURCE_UUID,
        "id": _BONGO_CAT_SOURCE_ID,
        "versioned_id": _BONGO_CAT_SOURCE_ID,
        "settings": {
            "mode": "standard",
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
            "relative_mouse": False,
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
                settings.setdefault(key, value)
            return
    sources.append(_new_bongo_source())


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


def _scene_rel_pos(x: float, y: float, canvas_w: float, canvas_h: float) -> dict:
    return {
        "x": (x - canvas_w / 2.0) / (canvas_h / 2.0),
        "y": (y - canvas_h / 2.0) / (canvas_h / 2.0),
    }


def _apply_bongo_geometry(item: dict, canvas_w: float, canvas_h: float) -> None:
    scale_ratio = canvas_h / _BONGO_CAT_CANVAS_H
    scale_x = _BONGO_CAT_SCALE_X * scale_ratio
    scale_y = _BONGO_CAT_SCALE_Y * scale_ratio
    scale_rel_x = _BONGO_CAT_SCALE_REL_X * scale_ratio
    scale_rel_y = _BONGO_CAT_SCALE_REL_Y * scale_ratio
    x = (canvas_w / 2.0) + (_BONGO_CAT_POS_REL_X * canvas_h / 2.0)
    y = (canvas_h / 2.0) + (_BONGO_CAT_POS_REL_Y * canvas_h / 2.0)
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
        "pos_rel": {"x": _BONGO_CAT_POS_REL_X, "y": _BONGO_CAT_POS_REL_Y},
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


def _new_bongo_scene_item(item_id: int, canvas_w: float, canvas_h: float) -> dict:
    item = {"id": item_id}
    _apply_bongo_geometry(item, canvas_w, canvas_h)
    return item


def _ensure_bongo_scene_item(items: list, settings: dict, canvas_w: float, canvas_h: float) -> None:
    for item in items:
        if item.get("source_uuid") == _BONGO_CAT_SOURCE_UUID or item.get("name") == _BONGO_CAT_SOURCE_NAME:
            _apply_bongo_geometry(item, canvas_w, canvas_h)
            return

    items.append(_new_bongo_scene_item(_next_scene_item_id(items), canvas_w, canvas_h))
    try:
        settings["id_counter"] = max(int(settings.get("id_counter", 0)), max(_scene_item_ids(items)))
    except (TypeError, ValueError):
        settings["id_counter"] = max(_scene_item_ids(items))


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

    primary_w = primary.width  if primary is not None else 0
    primary_h = primary.height if primary is not None else 0

    for src in sources:
        sid = src.get("id")

        if sid == _MIC_SOURCE_ID:
            src.setdefault("settings", {})["device_id"] = prefs.microphone_device_id

        elif sid == _MONITOR_SOURCE_ID and primary is not None:
            src.setdefault("settings", {})["monitor_id"] = primary.device_id

        elif sid == _INPUT_OVERLAY_SOURCE_ID:
            src["enabled"] = use_input_overlay
            _resolve_overlay_paths(src)

        elif sid == "scene":
            settings = src.get("settings", {})
            items = settings.get("items", [])
            kept_items = []
            for item in items:
                name = str(item.get("name", ""))
                # match the overlay scene item by source_uuid so renaming the source doesnt break the toggle.
                if item.get("source_uuid") in overlay_uuids:
                    if use_input_overlay:
                        item["visible"] = True
                        kept_items.append(item)
                    continue
                # group-style structure: hide/show by group name (groups dont expose source_uuid here).
                if name in overlay_group_names:
                    if use_input_overlay:
                        item["visible"] = True
                        kept_items.append(item)
                    continue
                if item.get("source_uuid") == _BONGO_CAT_SOURCE_UUID or name == _BONGO_CAT_SOURCE_NAME:
                    if use_bongo_cat:
                        _apply_bongo_geometry(item, canvas_w, canvas_h)
                        kept_items.append(item)
                    continue
                if name == "Display Capture":
                    _fit_scene_item_to_canvas(
                        item, canvas_w, canvas_h, primary_w, primary_h
                    )
                elif name == "Game Capture":
                    # game capture has no install-time source dims -- bounds-based fit lets obs scale at runtime.
                    _fit_scene_item_to_canvas(item, canvas_w, canvas_h)
                kept_items.append(item)

            if use_bongo_cat:
                _ensure_bongo_scene_item(kept_items, settings, canvas_w, canvas_h)
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
    "user.ini":                                   apply_user_ini,
    "global.ini":                                 apply_global_ini,
}


def apply_preferences(rel_path: Path, content: str, prefs: Preferences) -> str:
    """rewrite content according to prefs if a transformer is registered, else return as-is."""
    transformer = _DISPATCH.get(rel_path.as_posix())
    if transformer is None:
        return content
    return transformer(content, prefs)

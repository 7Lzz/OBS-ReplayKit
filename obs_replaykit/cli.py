"""interactive CLI setup menu for OBS ReplayKit."""

from __future__ import annotations

import os
import shutil
import textwrap
import time
from pathlib import Path
from typing import Callable

from . import __version__
from .audio import DEFAULT_DEVICE_ID, DEFAULT_DEVICE_NAME, list_microphones
from .bongo_cat import install_bongo_cat_plugin
from .config import OBS_CONFIG, USERNAME
from .display import primary_display
from .encoder import available_codecs, pick_encoder
from .fast_exit import fast_exit
from .gpu import primary_gpu
from .input_overlay import install_input_overlay_plugin, install_input_overlay_presets
from .installer import (
    backup_existing_config,
    configure_obs_websocket,
    ensure_launcher_built,
    ensure_recording_dirs,
    install_obs_config,
    install_obs_custom_dock,
    install_obs_elevation_task,
    install_obs_ffmpeg,
    install_obs_sleep_override,
)
from .keybind import combo_to_label, parse_combo
from .obs import cleanup_crash_flags, close_obs, find_obs_exe, launch_obs
from .prefs import (
    ALLOWED_CODEC_PREFERENCES,
    ALLOWED_COMPRESSION_MODES,
    Preferences,
    REPLAY_BUFFER_MAX,
    REPLAY_BUFFER_MIN,
    default_compression_for_preset,
    load_prefs,
)
from .recording import PRESETS, get_preset
from .shaderfilter import install_replaykit_motion_blur_plugin
from .startup import configure_obs_startup
from .tray_pin import pin_obs_tray_icon, unpin_obs_tray_icon
from .tray_plugin import install_replaykit_tray_plugin
from .vbcable import ensure_vbcable
from .wincapture import install_win_capture_audio
from .cleanup import run_cleanup

_COLOR_ENABLED = False


class C:
    reset = "\033[0m"
    title = "\033[1;97m"
    heading = "\033[1;36m"
    label = "\033[90m"
    value = "\033[97m"
    dim = "\033[90m"
    choice = "\033[96m"
    good = "\033[92m"
    warn = "\033[93m"
    bad = "\033[91m"
    accent = "\033[94m"


def color(text: str, code: str) -> str:
    return f"{code}{text}{C.reset}" if _COLOR_ENABLED else text


def content_width() -> int:
    columns = shutil.get_terminal_size(fallback=(124, 40)).columns
    return min(max(columns - 2, 96), 124)


STORAGE_INFO = {
    "lower_gpu": {
        "title": "Lowest GPU use / larger clips",
        "summary": "Lowest recording impact. Files are larger.",
        "detail": "Baseline GPU use. Best if a game is sensitive to recording overhead.",
    },
    "balanced": {
        "title": "Balanced GPU and clip size",
        "summary": "Recommended. Smaller clips with only a small encoder cost.",
        "detail": "Usually about +2-5% GPU over Lowest GPU use, with noticeably smaller files.",
    },
    "smaller_files": {
        "title": "Smallest clips / more GPU use",
        "summary": "More encoder work to shrink clips for storage and uploads.",
        "detail": "Usually about +5-12% GPU over Lowest GPU use. Use this when disk size matters most.",
    },
}


def configure_console() -> None:
    global _COLOR_ENABLED
    if os.name != "nt":
        _COLOR_ENABLED = True
        return
    try:
        os.system("title OBS ReplayKit Setup")
        # 54 lines fits the full settings menu without scrolling the title banner off the top
        os.system("mode con: cols=124 lines=54 >nul")
    except Exception:
        pass
    try:
        import ctypes
        kernel32 = ctypes.windll.kernel32
        handle = kernel32.GetStdHandle(-11)
        mode = ctypes.c_uint32()
        if kernel32.GetConsoleMode(handle, ctypes.byref(mode)):
            kernel32.SetConsoleMode(handle, mode.value | 0x0004)
            _COLOR_ENABLED = True
            _try_set_console_font(kernel32, handle)
    except Exception:
        _COLOR_ENABLED = False


def _try_set_console_font(kernel32, handle) -> None:
    """use a modern readable console font when classic conhost allows it."""
    try:
        import ctypes

        class Coord(ctypes.Structure):
            _fields_ = [("X", ctypes.c_short), ("Y", ctypes.c_short)]

        class ConsoleFontInfoEx(ctypes.Structure):
            _fields_ = [
                ("cbSize", ctypes.c_ulong),
                ("nFont", ctypes.c_ulong),
                ("dwFontSize", Coord),
                ("FontFamily", ctypes.c_uint),
                ("FontWeight", ctypes.c_uint),
                ("FaceName", ctypes.c_wchar * 32),
            ]

        info = ConsoleFontInfoEx()
        info.cbSize = ctypes.sizeof(ConsoleFontInfoEx)
        if not kernel32.GetCurrentConsoleFontEx(handle, False, ctypes.byref(info)):
            return
        info.dwFontSize.Y = max(info.dwFontSize.Y + 2, 18)
        info.FontWeight = 400
        info.FaceName = "Cascadia Mono"
        kernel32.SetCurrentConsoleFontEx(handle, False, ctypes.byref(info))
    except Exception:
        return


def clear() -> None:
    os.system("cls" if os.name == "nt" else "clear")


def pause(message: str = "Press Enter to continue...") -> None:
    input(f"\n{message}")


def prompt(label: str = "Choose") -> str:
    return input(f"\n{color(label, C.heading)}: ").strip()


def section(title: str) -> None:
    print()
    print(color(title, C.heading))
    print(color("-" * content_width(), C.dim))


def row(name: str, value: str, note: str = "") -> None:
    width = content_width()
    prefix = f"  {name:<26} "
    line = color(prefix, C.label) + color(value, C.value)
    if not note:
        print(line)
        return
    # most notes fit right after the value on the same line; only wraps below for the few too long to fit -- used to always wrap below regardless of length, pushing the menu past the consoles line count and scrolling the title banner off the top.
    if len(prefix) + len(value) + 2 + len(note) <= width:
        print(line + "  " + color(note, C.dim))
        return
    print(line)
    indent = " " * len(prefix)
    for wrapped in textwrap.wrap(note, width=max(40, width - len(prefix))):
        print(color(indent + wrapped, C.dim))


def menu_line(key: str, name: str, note: str, key_color: str = C.choice) -> None:
    print(
        f"  {color(f'[{key}]', key_color)} "
        f"{color(name.ljust(25), C.value)} "
        f"{color(note, C.dim)}"
    )


def option_line(key: str, title: str, note: str = "", selected: bool = False) -> None:
    marker = color("*", C.good) if selected else " "
    print(f"  {color(f'[{key}]', C.choice)} {marker} {color(title, C.value)}")
    if note:
        width = content_width()
        indent = " " * 8
        for wrapped in textwrap.wrap(note, width=max(40, width - len(indent))):
            print(color(indent + wrapped, C.dim))


def cancel_line() -> None:
    print()
    print(f"  {color('[Q]', C.choice)} {color('Cancel', C.dim)}")


def storage_info(mode: str) -> dict[str, str]:
    return STORAGE_INFO.get(mode, STORAGE_INFO["balanced"])


def current_encoder(prefs: Preferences):
    preset = get_preset(prefs.recording_preset)
    return pick_encoder(primary_gpu(), prefs.codec_preference, preset.cqp_target, prefs.compression_mode)


def display_text() -> str:
    display = primary_display()
    if display is None:
        return "No display detected"
    return f"{display.adapter}  {display.width}x{display.height}  ({display.edid_model})"


def gpu_text() -> str:
    gpu = primary_gpu()
    if gpu is None:
        return "No GPU detected - software encoder will be used"
    return f"{gpu.label} ({gpu.gen_label})"


def overlay_label(prefs: Preferences) -> str:
    if prefs.overlay_style == "input_overlay":
        return "WASD/mouse"
    if prefs.overlay_style == "bongo_cat":
        return "Bongo Cat"
    return "Off"


def startup_label(prefs: Preferences) -> str:
    return "On" if prefs.obs_startup_enabled else "Off"


def clip_notification_label(prefs: Preferences) -> str:
    return "On" if prefs.clip_notification_enabled else "Off"


def recording_notification_label(prefs: Preferences) -> str:
    return "On" if prefs.recording_notification_enabled else "Off"


def trim_precise_label(prefs: Preferences) -> str:
    return "Precise" if prefs.trim_precise_default else "Fast"


def discord_screenshare_label(prefs: Preferences) -> str:
    return "On" if prefs.discord_screenshare_enabled else "Off"


def sleep_override_label(prefs: Preferences) -> str:
    return "Allowed" if prefs.allow_sleep_while_active else "Blocked by OBS"


def render_menu(prefs: Preferences) -> None:
    clear()
    preset = get_preset(prefs.recording_preset)
    storage = storage_info(prefs.compression_mode)
    encoder = current_encoder(prefs)
    obs_exe = find_obs_exe()

    width = content_width()
    print()
    print(color(" OBS REPLAYKIT SETUP ".center(width, "="), C.title))
    print(color(f" version {__version__} ".center(width), C.dim))
    print(color("=" * width, C.dim))
    row("Windows user", USERNAME)
    row("OBS config", str(OBS_CONFIG))
    row("OBS install", str(obs_exe) if obs_exe else "OBS was not found")
    row("Clip keybind", combo_to_label(prefs.clip_keybind))
    row("Recording keybind", combo_to_label(prefs.recording_keybind))

    section("Setup overview")
    row("Recording quality", preset.label, preset.description)
    row("GPU load vs file size", storage["title"], storage["summary"])
    row("GPU detected", gpu_text())
    row("Recording encoder", encoder.label, encoder.description)
    row("Display capture", display_text())
    row("Clip length", f"{prefs.replay_buffer_seconds} seconds")
    row("Save folder", prefs.recording_path)
    row("Microphone", prefs.microphone_name)
    row("Overlay", overlay_label(prefs))
    row("Windows startup", startup_label(prefs), "Start OBS automatically when you sign in.")
    row("Clip saved popup", clip_notification_label(prefs), "Show a small desktop popup after saving a clip.")
    row("Recording popups", recording_notification_label(prefs), "Show a small desktop popup when recording starts or stops.")
    row("Trim default", trim_precise_label(prefs), "Default clip trimmer mode.")
    row("Discord screenshare", discord_screenshare_label(prefs), "Installs OBS Stream Audio for Discord Share Preview.")
    row("Windows sleep", sleep_override_label(prefs), "Let Windows sleep timers run while OBS or Share Preview is active.")

    section("Change settings")
    menu_line("1", "Recording quality", "resolution, FPS, and visual quality")
    menu_line("2", "GPU load vs file size", "choose lower GPU use or smaller clips")
    menu_line("3", "Recording codec", "auto, H.264, or HEVC")
    menu_line("4", "Clip length", "how many seconds the hotkey saves")
    menu_line("5", "Save folder", "where recordings and clips go")
    menu_line("6", "Microphone", "system default or a specific device")
    menu_line("7", "Clip keybind", "hotkey that saves a clip")
    menu_line("8", "Recording keybind", "hotkey that starts and stops recording")
    menu_line("9", "Overlay", "off, WASD/mouse, or Bongo Cat")
    menu_line("10", "Windows startup", "turn automatic OBS launch on or off")
    menu_line("11", "Clip saved popup", "show or hide the clip saved popup")
    menu_line("12", "Recording popups", "show or hide recording started/stopped popups")
    menu_line("13", "Trim default", "fast keyframe trim or precise re-encode trim")
    menu_line("14", "Discord screenshare", "turn Share Preview audio support on or off")
    print()
    menu_line("A", "Apply and launch OBS", "write settings, install selected tools, start OBS", C.good)
    menu_line("R", "Clean reset", "remove ReplayKit OBS config and audio device changes", C.bad)
    menu_line("Q", "Quit", "close setup")


def choose_recording_quality(prefs: Preferences) -> None:
    clear()
    section("Recording quality")
    for idx, preset in enumerate(PRESETS, 1):
        option_line(str(idx), preset.label, preset.description, preset.name == prefs.recording_preset)
    cancel_line()
    choice = prompt()
    if choice.lower() == "q":
        return
    try:
        selected = PRESETS[int(choice) - 1]
    except (ValueError, IndexError):
        pause("Invalid choice. Press Enter...")
        return
    prefs.recording_preset = selected.name
    prefs.compression_mode = default_compression_for_preset(selected.name)
    prefs.save()


def choose_storage_mode(prefs: Preferences) -> None:
    clear()
    section("GPU load vs file size")
    print(color("This keeps visual quality the same. Pick lower GPU use or smaller clip files.", C.dim))
    print()
    modes = list(ALLOWED_COMPRESSION_MODES)
    for idx, mode in enumerate(modes, 1):
        info = storage_info(mode)
        option_line(
            str(idx),
            info["title"],
            f"{info['summary']} {info['detail']}",
            mode == prefs.compression_mode,
        )
    cancel_line()
    choice = prompt()
    if choice.lower() == "q":
        return
    try:
        prefs.compression_mode = modes[int(choice) - 1]
    except (ValueError, IndexError):
        pause("Invalid choice. Press Enter...")
        return
    prefs.save()


def choose_codec(prefs: Preferences) -> None:
    clear()
    section("Recording codec")
    gpu = primary_gpu()
    codecs = available_codecs(gpu)
    keys = [key for key in ALLOWED_CODEC_PREFERENCES if key in codecs]
    for idx, key in enumerate(keys, 1):
        option_line(str(idx), key.upper(), codecs[key], key == prefs.codec_preference)
    cancel_line()
    choice = prompt()
    if choice.lower() == "q":
        return
    try:
        prefs.codec_preference = keys[int(choice) - 1]
    except (ValueError, IndexError):
        pause("Invalid choice. Press Enter...")
        return
    prefs.save()


def choose_replay_length(prefs: Preferences) -> None:
    clear()
    section("Clip length")
    row("Current", f"{prefs.replay_buffer_seconds} seconds")
    row("Allowed", f"{REPLAY_BUFFER_MIN}-{REPLAY_BUFFER_MAX} seconds")
    value = prompt("Seconds, or Q to cancel")
    if value.lower() == "q":
        return
    try:
        seconds = int(value)
    except ValueError:
        pause("Clip length must be a number. Press Enter...")
        return
    if not (REPLAY_BUFFER_MIN <= seconds <= REPLAY_BUFFER_MAX):
        pause(f"Clip length must be between {REPLAY_BUFFER_MIN} and {REPLAY_BUFFER_MAX}. Press Enter...")
        return
    prefs.replay_buffer_seconds = seconds
    prefs.save()


def choose_save_folder(prefs: Preferences) -> None:
    clear()
    section("Save folder")
    row("Current", prefs.recording_path)
    print(color("Paste a folder path. It will be created during Apply if it does not exist.", C.dim))
    value = prompt("Folder path, or Q to cancel")
    if value.lower() == "q":
        return
    path = value.strip().strip('"')
    if not path or "\x00" in path:
        pause("Invalid folder path. Press Enter...")
        return
    prefs.recording_path = Path(path).expanduser().as_posix()
    prefs.save()


def choose_microphone(prefs: Preferences) -> None:
    clear()
    section("Microphone")
    devices = [(DEFAULT_DEVICE_NAME, DEFAULT_DEVICE_ID)]
    devices.extend((dev.name, dev.device_id) for dev in list_microphones())
    for idx, (name, device_id) in enumerate(devices, 1):
        option_line(str(idx), name, "", device_id == prefs.microphone_device_id)
    cancel_line()
    choice = prompt()
    if choice.lower() == "q":
        return
    try:
        name, device_id = devices[int(choice) - 1]
    except (ValueError, IndexError):
        pause("Invalid choice. Press Enter...")
        return
    prefs.microphone_name = name
    prefs.microphone_device_id = device_id
    prefs.save()


def choose_keybind(prefs: Preferences) -> None:
    clear()
    section("Clip keybind")
    row("Current", combo_to_label(prefs.clip_keybind))
    print(color("Examples: shift+\\, ctrl+f10, alt+s, f9", C.dim))
    value = prompt("New keybind, or Q to cancel")
    if value.lower() == "q":
        return
    combo = parse_combo(value)
    if combo is None or combo.get("key") is None:
        pause("Invalid keybind. Press Enter...")
        return
    prefs.clip_keybind = combo
    prefs.save()


def choose_recording_keybind(prefs: Preferences) -> None:
    clear()
    section("Recording keybind")
    row("Current", combo_to_label(prefs.recording_keybind))
    print(color("This same key starts and stops OBS recording.", C.dim))
    print(color("Examples: ctrl+f9, alt+r, f10. Type CLEAR to disable it.", C.dim))
    value = prompt("New keybind, CLEAR, or Q to cancel")
    low = value.lower()
    if low == "q":
        return
    if low == "clear":
        prefs.recording_keybind = {}
        prefs.save()
        return
    combo = parse_combo(value)
    if combo is None or combo.get("key") is None:
        pause("Invalid keybind. Press Enter...")
        return
    prefs.recording_keybind = combo
    prefs.save()


def input_overlay_menu(prefs: Preferences) -> None:
    clear()
    section("Overlay")
    print(color("Choose the overlay ReplayKit installs and shows in OBS.", C.dim))
    print()
    row("Current", overlay_label(prefs))
    print()
    option_line("1", "WASD/mouse input overlay", "Shows keyboard and mouse presses.", prefs.overlay_style == "input_overlay")
    option_line("2", "Bongo Cat", "Animated keyboard/mouse overlay.", prefs.overlay_style == "bongo_cat")
    option_line("3", "Off", "No input overlay in the OBS scene.", prefs.overlay_style == "off")
    cancel_line()
    choice = prompt()
    if choice == "1":
        prefs.input_overlay_enabled = True
        prefs.overlay_style = "input_overlay"
        prefs.save()
    elif choice == "2":
        prefs.input_overlay_enabled = True
        prefs.overlay_style = "bongo_cat"
        prefs.save()
    elif choice == "3":
        prefs.input_overlay_enabled = False
        prefs.overlay_style = "off"
        prefs.save()
    elif choice.lower() == "q":
        return
    else:
        pause("Invalid choice. Press Enter...")


def choose_windows_startup(prefs: Preferences) -> None:
    clear()
    section("Windows startup")
    print(color("Choose whether Windows starts OBS automatically when you sign in.", C.dim))
    print()
    option_line("1", "On", "OBS opens on Windows sign-in. This is the default.", prefs.obs_startup_enabled)
    option_line("2", "Off", "OBS only opens when you launch it yourself.", not prefs.obs_startup_enabled)
    cancel_line()
    choice = prompt()
    if choice == "1":
        prefs.obs_startup_enabled = True
        prefs.save()
    elif choice == "2":
        prefs.obs_startup_enabled = False
        prefs.save()
    elif choice.lower() == "q":
        return
    else:
        pause("Invalid choice. Press Enter...")


def choose_clip_notification(prefs: Preferences) -> None:
    clear()
    section("Clip saved popup")
    print(color("Choose whether ReplayKit shows a small desktop popup after OBS saves a clip.", C.dim))
    print()
    option_line("1", "On", "Show a ReplayKit popup like: Saved the last 20s.", prefs.clip_notification_enabled)
    option_line("2", "Off", "Only use the save sound and OBS status.", not prefs.clip_notification_enabled)
    cancel_line()
    choice = prompt()
    if choice == "1":
        prefs.clip_notification_enabled = True
        prefs.save()
    elif choice == "2":
        prefs.clip_notification_enabled = False
        prefs.save()
    elif choice.lower() == "q":
        return
    else:
        pause("Invalid choice. Press Enter...")


def choose_recording_notification(prefs: Preferences) -> None:
    clear()
    section("Recording popups")
    print(color("Choose whether ReplayKit shows a small desktop popup when OBS recording starts or stops.", C.dim))
    print()
    option_line("1", "On", "Show Recording started and Recording stopped popups.", prefs.recording_notification_enabled)
    option_line("2", "Off", "Only use OBS status for recording state.", not prefs.recording_notification_enabled)
    cancel_line()
    choice = prompt()
    if choice == "1":
        prefs.recording_notification_enabled = True
        prefs.save()
    elif choice == "2":
        prefs.recording_notification_enabled = False
        prefs.save()
    elif choice.lower() == "q":
        return
    else:
        pause("Invalid choice. Press Enter...")


def choose_trim_default(prefs: Preferences) -> None:
    clear()
    section("Trim default")
    print(color("Choose the default mode for clip trimming.", C.dim))
    print()
    option_line("1", "Fast", "Trim at nearby keyframes without re-encoding.", not prefs.trim_precise_default)
    option_line("2", "Precise", "Re-encode around the trim points for exact timing.", prefs.trim_precise_default)
    cancel_line()
    choice = prompt()
    if choice == "1":
        prefs.trim_precise_default = False
        prefs.save()
    elif choice == "2":
        prefs.trim_precise_default = True
        prefs.save()
    elif choice.lower() == "q":
        return
    else:
        pause("Invalid choice. Press Enter...")


def choose_discord_screenshare(prefs: Preferences) -> None:
    clear()
    section("Discord screenshare")
    print(color("Choose whether ReplayKit installs and uses OBS Stream Audio for Discord Share Preview.", C.dim))
    print()
    option_line("1", "On", "Install the virtual audio cable and enable the Discord Share Preview path.", prefs.discord_screenshare_enabled)
    option_line("2", "Off", "Skip the virtual audio cable during setup and keep Share Preview disabled.", not prefs.discord_screenshare_enabled)
    cancel_line()
    choice = prompt()
    if choice == "1":
        prefs.discord_screenshare_enabled = True
        prefs.save()
    elif choice == "2":
        prefs.discord_screenshare_enabled = False
        prefs.discord_projector_enabled = False
        prefs.save()
    elif choice.lower() == "q":
        return
    else:
        pause("Invalid choice. Press Enter...")


def clean_reset() -> None:
    clear()
    section("Clean reset")
    print(color("This closes OBS, wipes OBS user config, removes ReplayKit OBS plugins,", C.dim))
    print(color("and removes ReplayKit audio device changes.", C.dim))
    print()
    confirm = prompt("Press R again to clean reset, or Q to cancel")
    key = confirm.lower()
    if key == "q":
        return
    if key != "r":
        pause("Reset cancelled. Press Enter...")
        return
    progress = InstallProgress(0, " OBS REPLAYKIT SETUP - CLEANING ")
    try:
        issues = run_cleanup(progress)
    except Exception:
        pause("Cleanup stopped. Press Enter to return to setup...")
        return
    print()
    print(color("Cleanup complete.", C.good))
    print(color("Your ReplayKit preferences were kept.", C.dim))
    if issues:
        print()
        print(color("Some cleanup steps reported warnings. Re-run Clean reset if needed.", C.warn))
    pause()


class InstallProgress:
    def __init__(self, total_steps: int, title: str = " OBS REPLAYKIT SETUP - APPLYING ") -> None:
        self.total_steps = total_steps
        self.title = title
        self.current_step = 0
        self.current_title = ""
        self.current_detail = ""
        self.issues: list[str] = []

    def render(self, completed: int, title: str, detail: str, state: str = "working") -> None:
        self.current_step = completed
        self.current_title = title
        self.current_detail = detail
        clear()
        width = content_width()
        print()
        print(color(self.title.center(width, "="), C.title))
        print(color("=" * width, C.dim))
        print()

        bar_width = min(58, max(32, width - 32))
        filled = int(bar_width * completed / max(1, self.total_steps))
        empty = bar_width - filled
        percent = int(100 * completed / max(1, self.total_steps))
        bar = color("#" * filled, C.good) + color("-" * empty, C.dim)
        print(f"  {bar} {color(f'{percent:3d}%', C.value)}")
        print()

        step_number = completed if state == "done" else completed + 1
        step_text = f"Step {min(max(step_number, 1), self.total_steps)} of {self.total_steps}"
        print(color(f"  {step_text}", C.heading))
        status = "Failed" if state == "failed" else ("Done" if state == "done" else "Working")
        status_color = C.bad if state == "failed" else (C.good if state == "done" else C.warn)
        print(f"  {color(status + ':', status_color)} {color(title, C.value)}")
        if detail:
            for wrapped in textwrap.wrap(detail, width=max(40, width - 4)):
                print(color(f"  {wrapped}", C.dim))

        if self.issues:
            print()
            print(color("  Needs attention", C.warn))
            for issue in self.issues[-3:]:
                print(color(f"  - {issue}", C.dim))

    def log(self, message: str) -> None:
        text = message.strip()
        if not text:
            return
        lowered = text.lower()
        if lowered.startswith("downloading ") or lowered.startswith("downloaded "):
            self.current_detail = text
            self.render(max(0, self.current_step), self.current_title, self.current_detail)
        issue_words = ("warn", "failed", "missing", "not found", "skipped", "timed out", "permission denied")
        if any(word in lowered for word in issue_words):
            self.add_issue(text)

    def add_issue(self, message: str) -> None:
        cleaned = " ".join(message.split())
        if len(cleaned) > 118:
            cleaned = cleaned[:115] + "..."
        if cleaned not in self.issues:
            self.issues.append(cleaned)


def _run_apply_step(
    progress: InstallProgress,
    index: int,
    title: str,
    detail: str,
    action: Callable[[], object],
) -> None:
    progress.render(index - 1, title, detail)
    try:
        result = action()
    except Exception as exc:
        progress.add_issue(f"{title} failed: {exc}")
        progress.render(index - 1, title, detail, state="failed")
        raise
    if result is False:
        progress.add_issue(f"{title} did not complete. Re-run Apply after fixing the issue.")
    progress.render(index, title, detail, state="done")
    time.sleep(0.12)


def run_apply_flow(prefs: Preferences) -> list[str]:
    steps: list[tuple[str, str, Callable[[], object]]] = []

    def add(title: str, detail: str, action: Callable[[], object]) -> None:
        steps.append((title, detail, action))

    progress = InstallProgress(0)

    add("Close OBS", "Stops OBS so settings can be written cleanly.", lambda: close_obs(log=progress.log))
    add("Back up current OBS settings", "Keeps a restore copy before ReplayKit writes the new setup.", lambda: backup_existing_config(log=progress.log) or True)
    add("Prepare OBS settings folder", "Creates the OBS config folder and clears crash-recovery prompts.", lambda: (OBS_CONFIG.mkdir(parents=True, exist_ok=True), cleanup_crash_flags(log=progress.log), True)[-1])
    add("Build ReplayKit helper", "Makes sure the local Clips and controls helper is ready.", lambda: ensure_launcher_built(log=progress.log))
    if prefs.discord_screenshare_enabled:
        add("Install OBS Stream Audio", "Installs and renames VB-Audio Cable for Discord share audio.", lambda: ensure_vbcable(log=progress.log))
    else:
        add("Skip OBS Stream Audio", "Discord screenshare support is off, so no virtual audio cable driver is installed.", lambda: True)
    add("Write ReplayKit OBS profile", "Applies your quality, clip, encoder, microphone, and overlay choices.", lambda: install_obs_config(prefs, log=progress.log) > 0)
    add("Enable OBS WebSocket", "Lets the Clips and controls windows talk to OBS locally.", lambda: configure_obs_websocket(log=progress.log))
    add("Install Custom Controls and Clips", "Adds the ReplayKit dock and Clips browser files.", lambda: install_obs_custom_dock(log=progress.log) > 0)
    add("Install tray plugin", "Adds View Clips, Share Preview, and Restart OBS to the system tray menu.", lambda: install_replaykit_tray_plugin(log=progress.log))
    add("Register launcher permission", "Avoids a UAC prompt every time OBS starts ReplayKit.", lambda: install_obs_elevation_task(log=progress.log))
    if prefs.allow_sleep_while_active:
        add("Allow monitor and PC sleep", "Lets Windows sleep timers run even if OBS, Replay Buffer, or Share Preview is active.", lambda: install_obs_sleep_override(True, log=progress.log))
    else:
        add("Restore OBS sleep blocking", "Removes ReplayKit's sleep override so OBS can keep active sessions awake.", lambda: install_obs_sleep_override(False, log=progress.log))
    if prefs.pin_obs_tray_icon:
        add("Pin OBS tray icon", "Keeps the OBS icon visible next to the clock instead of hidden behind the overflow arrow.", lambda: pin_obs_tray_icon(log=progress.log) or True)
    else:
        add("Skip tray icon pinning", "Leaves the OBS tray icon at Windows' default overflow behavior.", lambda: unpin_obs_tray_icon(log=progress.log) or True)
    add("Install video tools", "Downloads or verifies the trim/compress tools used by Clips.", lambda: install_obs_ffmpeg(log=progress.log))

    add(
        "Install WASD/mouse overlay",
        "Adds the keyboard and mouse overlay plugin and presets so Settings can switch to it later.",
        lambda: install_input_overlay_plugin(log=progress.log) and install_input_overlay_presets(log=progress.log),
    )
    add(
        "Install Bongo Cat overlay",
        "Adds the Bongo Cat keyboard and mouse overlay plugin so Settings can switch to it later.",
        lambda: install_bongo_cat_plugin(log=progress.log),
    )
    add(
        "Install motion blur filter",
        "Adds the bundled OBS Shaderfilter plugin and removes the retired Composite Blur plugin.",
        lambda: install_replaykit_motion_blur_plugin(log=progress.log),
    )

    add("Install desktop audio capture", "Adds clean desktop/game audio capture for OBS.", lambda: install_win_capture_audio(log=progress.log))
    add("Prepare clip folders", "Creates the recording and clip folders if needed.", lambda: (ensure_recording_dirs(prefs, log=progress.log), True)[-1])
    add("Set Windows startup", "Adds or removes OBS from Windows startup based on your setup choice.", lambda: configure_obs_startup(prefs.obs_startup_enabled, log=progress.log))
    add("Launch OBS", "Starts OBS with the updated ReplayKit setup.", lambda: launch_obs(log=progress.log))

    progress.total_steps = len(steps)
    for index, (title, detail, action) in enumerate(steps, 1):
        _run_apply_step(progress, index, title, detail, action)

    progress.render(progress.total_steps, "Setup complete", "OBS ReplayKit is ready.", state="done")
    return progress.issues


def apply_settings(prefs: Preferences) -> None:
    clear()
    prefs.save()
    try:
        issues = run_apply_flow(prefs)
    except Exception:
        pause("Apply stopped. Press Enter to return to setup...")
        return
    print()
    print(color("Setup complete.", C.good))
    if prefs.discord_screenshare_enabled:
        print(color("Discord share window: OBS ReplayKit Discord Share", C.value))
        print(color("Select that Windowed Projector in Discord Go Live. ReplayKit keeps it parked automatically.", C.dim))
    else:
        print(color("Discord Share Preview is disabled. OBS Stream Audio was not installed.", C.dim))
    if issues:
        print()
        print(color("Some steps reported warnings. Re-run Apply if OBS does not behave as expected.", C.warn))
    pause()


def run_cli() -> int:
    configure_console()
    prefs = load_prefs()
    while True:
        render_menu(prefs)
        choice = prompt().lower()
        if choice == "1":
            choose_recording_quality(prefs)
        elif choice == "2":
            choose_storage_mode(prefs)
        elif choice == "3":
            choose_codec(prefs)
        elif choice == "4":
            choose_replay_length(prefs)
        elif choice == "5":
            choose_save_folder(prefs)
        elif choice == "6":
            choose_microphone(prefs)
        elif choice == "7":
            choose_keybind(prefs)
        elif choice == "8":
            choose_recording_keybind(prefs)
        elif choice == "9":
            input_overlay_menu(prefs)
        elif choice == "10":
            choose_windows_startup(prefs)
        elif choice == "11":
            choose_clip_notification(prefs)
        elif choice == "12":
            choose_recording_notification(prefs)
        elif choice == "13":
            choose_trim_default(prefs)
        elif choice == "14":
            choose_discord_screenshare(prefs)
        elif choice == "a":
            apply_settings(prefs)
        elif choice == "r":
            clean_reset()
        elif choice == "q":
            fast_exit(0)
        else:
            pause("Invalid choice. Press Enter...")

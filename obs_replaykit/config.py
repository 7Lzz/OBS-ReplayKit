"""constants + resolved paths derived from env vars (USERNAME, USERPROFILE, APPDATA, ProgramFiles) so the tool works for any user without code changes."""

import os
import sys
from pathlib import Path


# user / environment

USERNAME    = os.environ.get("USERNAME") or os.environ.get("USER") or "User"
USERPROFILE = Path(os.environ.get("USERPROFILE") or Path.home())
APPDATA     = Path(os.environ.get("APPDATA") or USERPROFILE / "AppData" / "Roaming")
PROGRAMDATA = Path(os.environ.get("ProgramData", r"C:\ProgramData"))


# obs install / config

OBS_CONFIG = APPDATA / "obs-studio"

# consolidated runtime root for everything obs replaykit installs (lua scripts, dock html, input-overlay presets). keeps the obs-studio tree easy to reason about and lets cleanup do a single obs-studio wipe. casing matches the project name even though windows fs is case-insensitive -- the on-disk folder records whatever name we create it with.
REPLAYKIT_CONFIG = OBS_CONFIG / "obs-replayKit"

# dock html the local helper serves on 127.0.0.1:8767. transform.py builds the file:// url written into user.ini from this path.
DOCK_TARGET = REPLAYKIT_CONFIG / "obs-custom-dock"

OBS_PROCESSES = ("obs64.exe", "obs32.exe", "obs.exe")

OBS_EXE_CANDIDATES = (
    Path(os.environ.get("ProgramFiles",      r"C:\Program Files"))     / "obs-studio" / "bin" / "64bit" / "obs64.exe",
    Path(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")) / "obs-studio" / "bin" / "64bit" / "obs64.exe",
    Path(os.environ.get("ProgramW6432",      r"C:\Program Files"))     / "obs-studio" / "bin" / "64bit" / "obs64.exe",
)


# bundled assets root. source mode -> project dir. pyinstaller --onefile -> _meipass temp dir where the bundle is extracted at runtime.

if getattr(sys, "frozen", False):
    BUNDLE_ROOT = Path(getattr(sys, "_MEIPASS", Path(sys.executable).parent))
else:
    BUNDLE_ROOT = Path(__file__).resolve().parent.parent

ASSETS_DIR     = BUNDLE_ROOT / "assets"
OBS_ASSETS_DIR = ASSETS_DIR / "obs-studio"  # mirrors %appdata%/obs-studio


# OBS plugin install location. %APPDATA%\obs-studio\plugins\ is not scanned.

PROGRAMFILES_OBS_DIR = Path(os.environ.get("ProgramFiles", r"C:\Program Files")) / "obs-studio"

# input-overlay plugin + its vc++ redist prerequisite. plugin installer is bundled in installers.zip; vc++ is downloaded from microsoft only when missing and is signature-checked before running.

INPUT_OVERLAY_INSTALLERS_ZIP = ASSETS_DIR / "installers.zip"
INPUT_OVERLAY_INSTALLER_NAME = "input-overlay-installer.exe"  # filename inside the zip
VCPP_REDIST_DOWNLOAD_URL = "https://aka.ms/vc14/vc_redist.x64.exe"
VCPP_REDIST_DOWNLOAD_MAX_BYTES = 64 * 1024 * 1024


# Bundled virtual audio driver. Creates the OBS Stream Audio render endpoint and
# matching loopback capture endpoint used by Discord.
VBCABLE_DRIVER_PACK_NAME = "VBCABLE_Driver_Pack45.zip"
VBCABLE_SETUP_EXE_NAME   = "VBCABLE_Setup_x64.exe"


# input-overlay preset pack -- extracted under REPLAYKIT_CONFIG so all replaykit-managed assets live under one umbrella. scenes transform rewrites io.overlay_image / io.layout_file paths to land under here, so paths auto-resolve for any user.

INPUT_OVERLAY_ZIP    = ASSETS_DIR / "input-overlay-presets.zip"
INPUT_OVERLAY_TARGET = REPLAYKIT_CONFIG / "input-overlay-presets"


# win-capture-audio plugin (https://github.com/bozbez/win-capture-audio) powers the "Desktop Audio (excl. Discord)" source via windows 10+ per-process loopback. without it the scenes audio_capture source cant instantiate.

WIN_CAPTURE_AUDIO_ZIP = ASSETS_DIR / "win-capture-audio.zip"
WIN_CAPTURE_AUDIO_DLL_REL = "obs-plugins/64bit/win-capture-audio.dll"

# Bongobs/Bango Cat plugin. The archive is distributed as a manual OBS-root
# extract; installer code strips the top-level folder and writes only safe
# relative paths into PROGRAMFILES_OBS_DIR.
BONGO_CAT_ZIP = ASSETS_DIR / "Bango Cat.zip"


# OBS Shaderfilter plugin. ReplayKit uses its bundled motion_blur.shader when
# the optional motion blur setting is enabled.
SHADERFILTER_ZIP = ASSETS_DIR / "obs-shaderfilter.zip"
SHADERFILTER_ZIP_SHA256 = "0e75fc5f2523befd9c66c0adb14f9c838cc0cd705b32487e121abb03ad2f2486"


# file extensions the installer treats as text (rewrite_user_paths + apply_preferences).

TEXT_EXTS = frozenset({".ini", ".json", ".bak", ".lua", ".ps1", ".txt"})

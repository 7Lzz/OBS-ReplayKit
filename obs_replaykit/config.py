"""constants + resolved paths derived from env vars and the local OBS install."""

import os
import shutil
import subprocess
import sys
from pathlib import Path


# user / environment

USERNAME    = os.environ.get("USERNAME") or os.environ.get("USER") or "User"
USERPROFILE = Path(os.environ.get("USERPROFILE") or Path.home())
APPDATA     = Path(os.environ.get("APPDATA") or USERPROFILE / "AppData" / "Roaming")
LOCALAPPDATA = Path(os.environ.get("LOCALAPPDATA") or USERPROFILE / "AppData" / "Local")
PROGRAMDATA = Path(os.environ.get("ProgramData", r"C:\ProgramData"))


# obs install / config

OBS_CONFIG = APPDATA / "obs-studio"

# consolidated runtime root for everything obs replaykit installs (lua scripts, dock html, input-overlay presets). keeps the obs-studio tree easy to reason about and lets cleanup remove replaykit-owned config without touching OBS scenes/profiles. casing matches the project name even though windows fs is case-insensitive -- the on-disk folder records whatever name we create it with.
REPLAYKIT_CONFIG = OBS_CONFIG / "obs-replayKit"
REPLAYKIT_SETUP_CACHE = LOCALAPPDATA / "OBS ReplayKit"
REPLAYKIT_SETUP_EXE = REPLAYKIT_SETUP_CACHE / "OBSReplayKitSetup.exe"
REPLAYKIT_USER_STATE_CACHE = REPLAYKIT_SETUP_CACHE / "state"

# dock html the local helper serves on 127.0.0.1:8767. transform.py builds the file:// url written into user.ini from this path.
DOCK_TARGET = REPLAYKIT_CONFIG / "obs-custom-dock"

OBS_PROCESSES = ("obs64.exe", "obs32.exe", "obs.exe")

_DEFAULT_OBS_DIR = Path(os.environ.get("ProgramFiles", r"C:\Program Files")) / "obs-studio"


def _valid_obs_exe(path: Path) -> bool:
    return path.name.lower() in OBS_PROCESSES and path.is_file()


def _obs_exe_from_root(root: Path) -> Path:
    return root / "bin" / "64bit" / "obs64.exe"


def _obs_root_from_exe(path: Path) -> Path | None:
    try:
        path = path.resolve(strict=False)
        if path.parent.name.lower() == "64bit" and path.parent.parent.name.lower() == "bin":
            return path.parent.parent.parent
    except OSError:
        pass
    return None


def _env_obs_candidates() -> list[Path]:
    out: list[Path] = []
    exe = os.environ.get("OBS_REPLAYKIT_OBS_EXE", "").strip().strip('"')
    if exe:
        out.append(Path(exe))
    root = os.environ.get("OBS_REPLAYKIT_OBS_DIR", "").strip().strip('"')
    if root:
        out.append(_obs_exe_from_root(Path(root)))
    return out


def _running_obs_candidates() -> list[Path]:
    if os.name != "nt":
        return []
    names = "','".join(name.replace("'", "''") for name in OBS_PROCESSES)
    script = (
        f"Get-CimInstance Win32_Process | "
        f"Where-Object {{ $_.Name -in @('{names}') -and $_.ExecutablePath }} | "
        f"Select-Object -ExpandProperty ExecutablePath -Unique"
    )
    try:
        result = subprocess.run(
            ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script],
            capture_output=True,
            text=True,
            timeout=3,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
    except (OSError, subprocess.TimeoutExpired):
        return []
    if result.returncode != 0:
        return []
    return [Path(line.strip()) for line in result.stdout.splitlines() if line.strip()]


def _registry_obs_candidates() -> list[Path]:
    if os.name != "nt":
        return []
    try:
        import winreg
    except ImportError:
        return []

    out: list[Path] = []
    views = [0]
    if hasattr(winreg, "KEY_WOW64_64KEY"):
        views.append(winreg.KEY_WOW64_64KEY)
    if hasattr(winreg, "KEY_WOW64_32KEY"):
        views.append(winreg.KEY_WOW64_32KEY)

    def add_root(value: str | None) -> None:
        if value:
            out.append(_obs_exe_from_root(Path(value.strip().strip('"'))))

    def add_exe(value: str | None) -> None:
        if value:
            raw = value.strip().strip('"')
            if "," in raw:
                raw = raw.split(",", 1)[0].strip().strip('"')
            out.append(Path(raw))

    for hive in (winreg.HKEY_CURRENT_USER, winreg.HKEY_LOCAL_MACHINE):
        for view in views:
            access = winreg.KEY_READ | view
            try:
                with winreg.OpenKeyEx(hive, r"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\obs64.exe", 0, access) as key:
                    value, _ = winreg.QueryValueEx(key, "")
                    add_exe(str(value))
            except OSError:
                pass
            for root_key in (
                r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                r"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            ):
                try:
                    with winreg.OpenKeyEx(hive, root_key, 0, access) as root:
                        for i in range(winreg.QueryInfoKey(root)[0]):
                            try:
                                sub_name = winreg.EnumKey(root, i)
                                with winreg.OpenKeyEx(root, sub_name, 0, access) as sub:
                                    display, _ = winreg.QueryValueEx(sub, "DisplayName")
                                    if not str(display).lower().startswith("obs studio"):
                                        continue
                                    try:
                                        install_location, _ = winreg.QueryValueEx(sub, "InstallLocation")
                                        add_root(str(install_location))
                                    except OSError:
                                        pass
                                    try:
                                        display_icon, _ = winreg.QueryValueEx(sub, "DisplayIcon")
                                        add_exe(str(display_icon))
                                    except OSError:
                                        pass
                            except OSError:
                                continue
                except OSError:
                    pass
    return out


def _path_obs_candidates() -> list[Path]:
    return [Path(found) for name in OBS_PROCESSES if (found := shutil.which(name))]


def _default_obs_candidates() -> list[Path]:
    roots = (
        Path(os.environ.get("ProgramFiles", r"C:\Program Files")) / "obs-studio",
        Path(os.environ.get("ProgramW6432", r"C:\Program Files")) / "obs-studio",
        Path(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")) / "obs-studio",
    )
    return [_obs_exe_from_root(root) for root in roots]


def _unique_paths(paths: list[Path]) -> tuple[Path, ...]:
    out: list[Path] = []
    seen: set[str] = set()
    for path in paths:
        key = str(path.resolve(strict=False)).lower()
        if key in seen:
            continue
        seen.add(key)
        out.append(path)
    return tuple(out)


OBS_EXE_CANDIDATES = _unique_paths(
    _env_obs_candidates()
    + _running_obs_candidates()
    + _registry_obs_candidates()
    + _path_obs_candidates()
    + _default_obs_candidates()
)


def find_obs_exe_candidate() -> Path | None:
    for candidate in OBS_EXE_CANDIDATES:
        if _valid_obs_exe(candidate):
            return candidate
    return None


def find_obs_install_dir() -> Path | None:
    exe = find_obs_exe_candidate()
    if exe is None:
        return None
    return _obs_root_from_exe(exe)


# bundled assets root. source mode -> project dir. pyinstaller --onefile -> _meipass temp dir where the bundle is extracted at runtime.

if getattr(sys, "frozen", False):
    BUNDLE_ROOT = Path(getattr(sys, "_MEIPASS", Path(sys.executable).parent))
else:
    BUNDLE_ROOT = Path(__file__).resolve().parent.parent

ASSETS_DIR     = BUNDLE_ROOT / "assets"
OBS_ASSETS_DIR = ASSETS_DIR / "obs-studio"  # mirrors %appdata%/obs-studio


# OBS plugin install location. %APPDATA%\obs-studio\plugins\ is not scanned.

OBS_INSTALL_DIR = find_obs_install_dir() or _DEFAULT_OBS_DIR
PROGRAMFILES_OBS_DIR = OBS_INSTALL_DIR

# input-overlay plugin + its vc++ redist prerequisite. plugin installer is bundled in installers.zip; vc++ is downloaded from microsoft only when missing and is signature-checked before running.

INPUT_OVERLAY_INSTALLERS_ZIP = ASSETS_DIR / "installers.zip"
INPUT_OVERLAY_INSTALLER_NAME = "input-overlay-installer.exe"  # filename inside the zip
VCPP_REDIST_DOWNLOAD_URL = "https://aka.ms/vc14/vc_redist.x64.exe"
VCPP_REDIST_DOWNLOAD_MAX_BYTES = 64 * 1024 * 1024


# bundled virtual audio driver -- creates the OBS Stream Audio render endpoint and matching loopback capture endpoint used by Discord
VBCABLE_DRIVER_PACK_NAME = "VBCABLE_Driver_Pack45.zip"
VBCABLE_SETUP_EXE_NAME   = "VBCABLE_Setup_x64.exe"


# input-overlay preset pack -- extracted under REPLAYKIT_CONFIG so all replaykit-managed assets live under one umbrella. scenes transform rewrites io.overlay_image / io.layout_file paths to land under here, so paths auto-resolve for any user.

INPUT_OVERLAY_ZIP    = ASSETS_DIR / "input-overlay-presets.zip"
INPUT_OVERLAY_TARGET = REPLAYKIT_CONFIG / "input-overlay-presets"


# win-capture-audio plugin (https://github.com/bozbez/win-capture-audio) powers the "Desktop Audio (excl. Discord)" source via windows 10+ per-process loopback. without it the scenes audio_capture source cant instantiate.

WIN_CAPTURE_AUDIO_ZIP = ASSETS_DIR / "win-capture-audio.zip"
WIN_CAPTURE_AUDIO_DLL_REL = "obs-plugins/64bit/win-capture-audio.dll"

# Bongobs/Bango Cat plugin -- the archive is distributed as a manual obs-root extract, so installer code strips the top-level folder and writes only safe relative paths into PROGRAMFILES_OBS_DIR.
BONGO_CAT_ZIP = ASSETS_DIR / "Bango Cat.zip"


# OBS Shaderfilter plugin -- ReplayKit uses its bundled motion_blur.shader when the optional motion blur setting is enabled.
SHADERFILTER_ZIP = ASSETS_DIR / "obs-shaderfilter.zip"
SHADERFILTER_ZIP_SHA256 = "0e75fc5f2523befd9c66c0adb14f9c838cc0cd705b32487e121abb03ad2f2486"


# ReplayKits own tray plugin -- unlike the third-party plugins above, this one is ours: compiled from source at release time (build.bat step 3.6) and bundled straight into assets/, not downloaded, and installed under %ProgramData%\obs-studio\plugins\ (obss no-admin plugin search path) rather than the PROGRAMFILES_OBS_DIR/obs-plugins layout the plugins above use.
REPLAYKIT_TRAY_DLL_BUNDLED = ASSETS_DIR / "obs-plugins" / "replaykit-tray" / "bin" / "64bit" / "replaykit-tray.dll"
REPLAYKIT_TRAY_PLUGIN_DIR  = PROGRAMDATA / "obs-studio" / "plugins" / "replaykit-tray"
REPLAYKIT_TRAY_DLL_TARGET  = REPLAYKIT_TRAY_PLUGIN_DIR / "bin" / "64bit" / "replaykit-tray.dll"


# file extensions the installer treats as text (rewrite_user_paths + apply_preferences).

TEXT_EXTS = frozenset({".ini", ".json", ".bak", ".lua", ".ps1", ".txt"})

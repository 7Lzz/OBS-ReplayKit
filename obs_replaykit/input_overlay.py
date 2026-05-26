"""install the input-overlay plugin (via the official inno setup installer) and extract its bundled presets. downloads the vc++ redist on demand if missing."""

import hashlib
import json
import os
import shutil
import subprocess
import tempfile
import urllib.parse
import urllib.request
import winreg
import zipfile
from pathlib import Path
from typing import Callable, Optional

from .config import (
    INPUT_OVERLAY_INSTALLER_NAME,
    INPUT_OVERLAY_INSTALLERS_ZIP,
    INPUT_OVERLAY_TARGET,
    INPUT_OVERLAY_ZIP,
    PROGRAMFILES_OBS_DIR,
    VCPP_REDIST_DOWNLOAD_MAX_BYTES,
    VCPP_REDIST_DOWNLOAD_URL,
)

LogFn = Optional[Callable[[str], None]]


# Canonical install paths the official Inno Setup installer writes.
_PLUGIN_DLL  = PROGRAMFILES_OBS_DIR / "obs-plugins" / "64bit" / "input-overlay.dll"
_PLUGIN_SDL2 = PROGRAMFILES_OBS_DIR / "obs-plugins" / "64bit" / "SDL2.dll"

_MOUSE_LAYOUT_REL = Path("mouse") / "mouse-no-movement.json"
_MOUSE_LAYOUT_WIDTH = 285
_MOUSE_LAYOUT_HEIGHT = 421


def _extract_installer(name: str, log: LogFn = None) -> Optional[Path]:
    """extract one installer from installers.zip to a temp dir. ships as zip_stored so pyinstaller/upx dont alter the pe bytes (which would break inno setups crc check)."""
    if not INPUT_OVERLAY_INSTALLERS_ZIP.is_file():
        if log:
            log(f"(no {INPUT_OVERLAY_INSTALLERS_ZIP.name} bundled)")
        return None
    try:
        with zipfile.ZipFile(INPUT_OVERLAY_INSTALLERS_ZIP) as zf:
            if name not in zf.namelist():
                if log:
                    log(f"({name} not in installers.zip)")
                return None
            tmpdir = Path(tempfile.mkdtemp(prefix="obsreplaykit_"))
            zf.extract(name, tmpdir)
            return tmpdir / name
    except Exception as exc:
        if log:
            log(f"failed to extract {name}: {exc}")
        return None


# vc++ 2015-2022 (x64) redistributable -- prerequisite for input-overlay.dll. downloaded from microsoft only when missing.

_VCPP_ALLOWED_DOWNLOAD_HOSTS = frozenset({
    "aka.ms",
    "download.visualstudio.microsoft.com",
})


def _is_vcpp_2015_2022_x64_installed() -> bool:
    """check the HKLM ...\\VisualStudio\\14.0\\VC\\Runtimes\\x64\\Installed flag microsoft flips to 1 on a successful redist install."""
    try:
        access = winreg.KEY_READ | getattr(winreg, "KEY_WOW64_64KEY", 0)
        with winreg.OpenKeyEx(
            winreg.HKEY_LOCAL_MACHINE,
            r"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64",
            0,
            access,
        ) as key:
            installed, _ = winreg.QueryValueEx(key, "Installed")
            return installed == 1
    except OSError:
        return False


def _download_vcpp_redist(log: LogFn = None) -> Optional[Path]:
    tmpdir = Path(tempfile.mkdtemp(prefix="obsreplaykit_vcredist_"))
    target = tmpdir / "vc_redist.x64.exe"
    try:
        req = urllib.request.Request(
            VCPP_REDIST_DOWNLOAD_URL,
            headers={"User-Agent": "OBSReplayKit"},
        )
        with urllib.request.urlopen(req, timeout=60) as response:
            final_url = response.geturl()
            parsed = urllib.parse.urlparse(final_url)
            host = (parsed.hostname or "").lower()
            if parsed.scheme.lower() != "https" or host not in _VCPP_ALLOWED_DOWNLOAD_HOSTS:
                raise RuntimeError(f"unexpected download host: {final_url}")

            length = response.headers.get("Content-Length")
            if length and int(length) > VCPP_REDIST_DOWNLOAD_MAX_BYTES:
                raise RuntimeError("download is larger than expected")

            total = 0
            digest = hashlib.sha256()
            with target.open("wb") as fh:
                while True:
                    chunk = response.read(1024 * 1024)
                    if not chunk:
                        break
                    total += len(chunk)
                    if total > VCPP_REDIST_DOWNLOAD_MAX_BYTES:
                        raise RuntimeError("download exceeded size limit")
                    digest.update(chunk)
                    fh.write(chunk)
        if log:
            mb = max(1, target.stat().st_size // (1024 * 1024))
            log(f"downloaded VC++ redist from Microsoft ({mb} MB, sha256 {digest.hexdigest()[:12]}...)")
        return target
    except Exception as exc:
        shutil.rmtree(tmpdir, ignore_errors=True)
        if log:
            log(f"VC++ redist download failed: {exc}")
        return None


def _is_microsoft_signed(path: Path, log: LogFn = None) -> bool:
    script = r"""
$sig = Get-AuthenticodeSignature -LiteralPath $env:OBSREPLAYKIT_REDIST_PATH
$cert = $sig.SignerCertificate
[pscustomobject]@{
  Status = [string]$sig.Status
  Subject = if ($cert) { [string]$cert.Subject } else { "" }
  Issuer = if ($cert) { [string]$cert.Issuer } else { "" }
} | ConvertTo-Json -Compress
"""
    env = os.environ.copy()
    env["OBSREPLAYKIT_REDIST_PATH"] = str(path)
    try:
        result = subprocess.run(
            ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script],
            capture_output=True,
            text=True,
            timeout=30,
            env=env,
        )
    except Exception as exc:
        if log:
            log(f"VC++ redist signature check failed to run: {exc}")
        return False
    if result.returncode != 0:
        if log:
            log(f"VC++ redist signature check failed: {result.stderr.strip()[:160]}")
        return False
    try:
        info = json.loads(result.stdout)
    except json.JSONDecodeError:
        if log:
            log("VC++ redist signature check returned invalid data")
        return False
    subject = info.get("Subject", "")
    issuer = info.get("Issuer", "")
    ok = (
        info.get("Status") == "Valid"
        and "Microsoft Corporation" in subject
        and "Microsoft" in issuer
    )
    if not ok and log:
        log(f"VC++ redist signature rejected: status={info.get('Status')} subject={subject[:80]}")
    return ok


def install_vcpp_redist(log: LogFn = None) -> bool:
    """download + run the vc++ redist if missing. without it the plugin dlls msvcp140/vcruntime140 imports fail and obs reports 'plugin load error'."""
    if _is_vcpp_2015_2022_x64_installed():
        if log:
            log("VC++ 2015-2022 (x64) Redistributable already installed")
        return True

    if log:
        log("VC++ redist missing; downloading latest x64 package from Microsoft...")
    redist = _download_vcpp_redist(log=log)
    if redist is None:
        return False
    if not _is_microsoft_signed(redist, log=log):
        shutil.rmtree(redist.parent, ignore_errors=True)
        return False

    if log:
        log("installing VC++ 2015-2022 Redistributable (silent)...")

    try:
        result = subprocess.run(
            [str(redist), "/install", "/quiet", "/norestart"],
            capture_output=True,
            text=True,
            timeout=300,
        )
    except subprocess.TimeoutExpired:
        if log:
            log("VC++ redist install timed out after 300s")
        return False
    except Exception as exc:
        if log:
            log(f"VC++ redist launch failed: {exc}")
        return False
    finally:
        shutil.rmtree(redist.parent, ignore_errors=True)

    # ms installer success codes: 0 installed, 1638 newer already installed, 3010 success-with-restart-requested (we passed /norestart so we treat it as ok).
    if result.returncode in (0, 1638, 3010):
        if log:
            log(f"VC++ redist install completed (exit {result.returncode})")
        return True

    if log:
        log(f"VC++ redist install FAILED (exit {result.returncode})")
        if result.stderr.strip():
            log(f"stderr: {result.stderr.strip().splitlines()[0]}")
    return False


def _already_installed() -> Optional[str]:
    """Description of the installed plugin, or None."""
    if _PLUGIN_DLL.is_file() and _PLUGIN_SDL2.is_file():
        return f"installed at {_PLUGIN_DLL}"
    return None


def install_input_overlay_plugin(log: LogFn = None) -> bool:
    """Install input-overlay and verify OBS can load its DLLs."""
    if not install_vcpp_redist(log=log):
        if log:
            log("input-overlay prerequisite missing; skipping input-overlay install")
        return False

    existing = _already_installed()
    if existing:
        if log:
            log(f"already {existing}")
        return True

    installer = _extract_installer(INPUT_OVERLAY_INSTALLER_NAME, log=log)
    if installer is None:
        if log:
            log("(input-overlay installer not available, skipping)")
        return False

    if log:
        log(f"running {INPUT_OVERLAY_INSTALLER_NAME} (silent)...")

    try:
        result = subprocess.run(
            [
                str(installer),
                "/VERYSILENT",
                "/SUPPRESSMSGBOXES",
                "/NORESTART",
                f"/DIR={PROGRAMFILES_OBS_DIR}",
            ],
            capture_output=True,
            text=True,
            timeout=120,
        )
    except subprocess.TimeoutExpired:
        if log:
            log("installer timed out after 120s - skipping")
        return False
    except Exception as exc:
        if log:
            log(f"installer launch failed: {exc}")
        return False
    finally:
        shutil.rmtree(installer.parent, ignore_errors=True)

    if result.returncode != 0:
        if log:
            log(f"installer exited with code {result.returncode}")
            if result.stderr.strip():
                log(f"stderr: {result.stderr.strip().splitlines()[0]}")
        return False

    if not _PLUGIN_DLL.is_file():
        if log:
            log(f"installer ran but {_PLUGIN_DLL} is missing")
        return False
    if not _PLUGIN_SDL2.is_file():
        # sdl2 might be elsewhere on path, so warn but dont fail the install.
        if log:
            log(f"warn: SDL2.dll missing at {_PLUGIN_SDL2} - plugin may not load")

    if log:
        log(f"installed -> {_PLUGIN_DLL}")
        log(f"           + {_PLUGIN_SDL2.name} ({'ok' if _PLUGIN_SDL2.is_file() else 'MISSING!'})")
        log("    OBS will load it on next launch (Tools menu)")
    return True


def install_input_overlay_presets(log: LogFn = None) -> bool:
    """extract input-overlay-presets.zip directly under INPUT_OVERLAY_TARGET (no double-nested folder). zip has a top-level input-overlay-presets/ that lines up with the target name, so extracting into target.parent is what makes the files land at the right path."""
    if not INPUT_OVERLAY_ZIP.is_file():
        if log:
            log(f"(no {INPUT_OVERLAY_ZIP.name} bundled, skipping)")
        return False

    target = INPUT_OVERLAY_TARGET
    target.mkdir(parents=True, exist_ok=True)

    # known sample at the correct un-nested location.
    wasd_sample = target / "wasd" / "wasd.png"
    if wasd_sample.is_file():
        if not _repair_mouse_overlay_layout(target, log=log):
            return False
        if log:
            log(f"already extracted -> {target}")
        return True

    size_mb = INPUT_OVERLAY_ZIP.stat().st_size // (1024 * 1024)
    if log:
        log(f"extracting {INPUT_OVERLAY_ZIP.name} ({size_mb} MB) -> {target}")

    # extract into the parent so the zips top-level input-overlay-presets/ folder lands as target. extracting into target itself would double-nest.
    try:
        with zipfile.ZipFile(INPUT_OVERLAY_ZIP) as zf:
            zf.extractall(target.parent)
    except Exception as exc:
        if log:
            log(f"warn: failed to extract presets: {exc}")
        return False

    if log:
        log(f"extracted -> {target}")
    return _repair_mouse_overlay_layout(target, log=log)


def _repair_mouse_overlay_layout(target: Path, log: LogFn = None) -> bool:
    """Ensure the mouse preset has explicit source dimensions so OBS does not render it as a canvas-sized source."""
    layout = target / _MOUSE_LAYOUT_REL
    if not layout.is_file():
        if log:
            log(f"warn: missing input-overlay mouse layout: {layout}")
        return False

    try:
        data = json.loads(layout.read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError) as exc:
        if log:
            log(f"warn: invalid input-overlay mouse layout: {exc}")
        return False

    if not isinstance(data, dict):
        if log:
            log("warn: invalid input-overlay mouse layout structure")
        return False

    if data.get("default_width") == _MOUSE_LAYOUT_WIDTH and data.get("default_height") == _MOUSE_LAYOUT_HEIGHT:
        return True

    data["default_width"] = _MOUSE_LAYOUT_WIDTH
    data["default_height"] = _MOUSE_LAYOUT_HEIGHT
    try:
        layout.write_text(json.dumps(data, indent=4), encoding="utf-8")
    except OSError as exc:
        if log:
            log(f"warn: failed to repair input-overlay mouse layout: {exc}")
        return False

    if log:
        log(f"repaired input-overlay mouse layout dimensions -> {layout}")
    return True

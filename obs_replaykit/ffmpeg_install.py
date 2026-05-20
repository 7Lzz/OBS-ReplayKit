"""Install ffmpeg.exe + ffprobe.exe into the helper dir."""

from __future__ import annotations

import hashlib
import ssl
import tempfile
import urllib.error
import urllib.request
import zipfile
from pathlib import Path
from typing import Callable, Optional

from .config import OBS_CONFIG

LogFn = Optional[Callable[[str], None]]


# next to local_helper_server.ps1 under the consolidated obs-replayKit/ tree. find-toolinclipdirs in 52_compression.ps1 probes $script:helperroot first.
_HELPER_DIR = OBS_CONFIG / "obs-replayKit" / "scripts" / "streamable"
_FFMPEG_DST = _HELPER_DIR / "ffmpeg.exe"
_FFPROBE_DST = _HELPER_DIR / "ffprobe.exe"

_DOWNLOAD_BASE = "https://raw.githubusercontent.com/7Lzz/OBS-ReplayKit/main/utils/downloads"
_ARCHIVE_URLS = (
    f"{_DOWNLOAD_BASE}/ffmpeg-tools.zip",
)

_TOOLS = (
    ("ffmpeg.exe", _FFMPEG_DST, "228d7a8556258de907fdb55f36850078ebc7680b84ec30d84ea02e99bec1d1eb"),
    ("ffprobe.exe", _FFPROBE_DST, "0fde260f5abd35c9cafd96f594cc76365a780c1b73a90e35b6a3409ea1db1bf0"),
)

# size caps so a hijacked url/archive cant fill the users disk.
_MAX_ARCHIVE_BYTES = 130 * 1024 * 1024
_MAX_TOOL_BYTES = 110 * 1024 * 1024
_PROGRESS_INTERVAL_BYTES = 5 * 1024 * 1024


def _already_installed() -> bool:
    return all(_valid_tool(dst, expected_sha256) for _, dst, expected_sha256 in _TOOLS)


def _file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _valid_tool(path: Path, expected_sha256: str) -> bool:
    return path.is_file() and _file_sha256(path).lower() == expected_sha256.lower()


def _copy_repo_tools(log: LogFn = None) -> bool:
    """Use checked-in developer copies when running from a source checkout."""
    source_dir = Path(__file__).resolve().parents[1] / "utils" / "downloads"
    if not source_dir.is_dir():
        return False
    ok = True
    copied = False
    _HELPER_DIR.mkdir(parents=True, exist_ok=True)
    for name, dst, expected_sha256 in _TOOLS:
        src = source_dir / name
        if not src.is_file():
            ok = False
            continue
        actual = _file_sha256(src)
        if actual.lower() != expected_sha256.lower():
            if log:
                log(f"warn: local {name} hash mismatch: {actual}")
            ok = False
            continue
        if _valid_tool(dst, expected_sha256):
            if log:
                log(f"{name} already present at {dst}")
            continue
        tmp = dst.with_name(f"{dst.name}.copying")
        try:
            tmp.write_bytes(src.read_bytes())
            tmp.replace(dst)
            copied = True
            if log:
                log(f"installed -> {dst}")
        finally:
            if tmp.exists():
                try:
                    tmp.unlink()
                except OSError:
                    pass
    return ok and copied


def _download_archive(url: str, log: LogFn = None) -> Path | None:
    """Stream a ReplayKit-hosted FFmpeg archive with live progress."""
    ctx = ssl.create_default_context()
    req = urllib.request.Request(
        url,
        headers={"User-Agent": "OBSReplayKit/1.0 (+https://github.com/7Lzz/OBS-ReplayKit)"},
    )
    tmp_path: Path | None = None
    try:
        with urllib.request.urlopen(req, timeout=90, context=ctx) as resp:
            status = getattr(resp, "status", 200)
            if status is not None and status != 200:
                if log:
                    log(f"warn: FFmpeg archive download returned HTTP {status}")
                return None
            length_header = resp.headers.get("Content-Length", "")
            expected_len = int(length_header) if length_header.isdigit() else 0
            if expected_len > _MAX_ARCHIVE_BYTES:
                if log:
                    log(f"warn: FFmpeg archive is larger than {_MAX_ARCHIVE_BYTES // (1024*1024)} MB cap")
                return None

            _HELPER_DIR.mkdir(parents=True, exist_ok=True)
            fd, raw_tmp = tempfile.mkstemp(prefix="ffmpeg-tools.", suffix=".zip.download", dir=str(_HELPER_DIR))
            tmp_path = Path(raw_tmp)
            total = 0
            next_log = _PROGRESS_INTERVAL_BYTES
            with open(fd, "wb") as out:
                while True:
                    data = resp.read(1024 * 1024)
                    if not data:
                        break
                    total += len(data)
                    if total > _MAX_ARCHIVE_BYTES:
                        if log:
                            log(f"warn: FFmpeg archive exceeded {_MAX_ARCHIVE_BYTES // (1024*1024)} MB cap, aborting")
                        return None
                    out.write(data)
                    if log and total >= next_log:
                        if expected_len:
                            pct = int((total / expected_len) * 100)
                            log(f"ffmpeg tools: {total // (1024*1024)} MB / {expected_len // (1024*1024)} MB ({pct}%)")
                        else:
                            log(f"ffmpeg tools: {total // (1024*1024)} MB")
                        next_log += _PROGRESS_INTERVAL_BYTES

            return tmp_path
    except urllib.error.URLError as exc:
        if log:
            log(f"warn: FFmpeg archive download failed: {exc.reason}")
        return None
    except (TimeoutError, ssl.SSLError, OSError) as exc:
        if log:
            log(f"warn: FFmpeg archive download error: {exc}")
        return None


def _extract_archive(archive: Path, log: LogFn = None) -> bool:
    tmp_paths: list[Path] = []
    try:
        with zipfile.ZipFile(archive) as zf:
            infos = {Path(info.filename).name.lower(): info for info in zf.infolist()}
            for name, dst, expected_sha256 in _TOOLS:
                info = infos.get(name.lower())
                if info is None:
                    if log:
                        log(f"warn: {name} missing from FFmpeg archive")
                    return False
                if info.file_size <= 0 or info.file_size > _MAX_TOOL_BYTES:
                    if log:
                        log(f"warn: {name} has an unexpected archive size")
                    return False

                fd, raw_tmp = tempfile.mkstemp(prefix=f"{name}.", suffix=".extract", dir=str(_HELPER_DIR))
                tmp = Path(raw_tmp)
                tmp_paths.append(tmp)
                digest = hashlib.sha256()
                total = 0
                with zf.open(info, "r") as src, open(fd, "wb") as out:
                    while True:
                        data = src.read(1024 * 1024)
                        if not data:
                            break
                        total += len(data)
                        if total > _MAX_TOOL_BYTES:
                            if log:
                                log(f"warn: {name} exceeded {_MAX_TOOL_BYTES // (1024*1024)} MB cap while extracting")
                            return False
                        digest.update(data)
                        out.write(data)
                actual = digest.hexdigest()
                if actual.lower() != expected_sha256.lower():
                    if log:
                        log(f"warn: {name} hash mismatch after extract: {actual}")
                    return False

                tmp.replace(dst)
                tmp_paths.remove(tmp)
                if log:
                    log(f"installed -> {dst}")
        return _already_installed()
    except (OSError, zipfile.BadZipFile) as exc:
        if log:
            log(f"warn: FFmpeg archive extract failed: {exc}")
        return False
    finally:
        for tmp in tmp_paths:
            if tmp.exists():
                try:
                    tmp.unlink()
                except OSError:
                    pass


def _download_and_extract_tools(log: LogFn = None) -> bool:
    for url in _ARCHIVE_URLS:
        if log:
            log(f"  {url}")
        archive = _download_archive(url, log=log)
        if archive is None:
            continue
        try:
            if _extract_archive(archive, log=log):
                return True
        finally:
            if archive.exists():
                try:
                    archive.unlink()
                except OSError:
                    pass
    if log:
        log("warn: ReplayKit ffmpeg-tools.zip was unavailable or invalid")
    return False


def _download_tool(name: str, dst: Path, expected_sha256: str, log: LogFn = None) -> bool:
    if _valid_tool(dst, expected_sha256):
        if log:
            log(f"{name} already present at {dst}")
        return True

    url = f"{_DOWNLOAD_BASE}/{name}"
    if log:
        log(f"  {url}")
    ctx = ssl.create_default_context()
    req = urllib.request.Request(
        url,
        headers={"User-Agent": "OBSReplayKit/1.0 (+https://github.com/7Lzz/OBS-ReplayKit)"},
    )
    tmp_path: Path | None = None
    try:
        with urllib.request.urlopen(req, timeout=90, context=ctx) as resp:
            status = getattr(resp, "status", 200)
            if status is not None and status != 200:
                if log:
                    log(f"warn: {name} download returned HTTP {status}")
                return False
            length_header = resp.headers.get("Content-Length", "")
            expected_len = int(length_header) if length_header.isdigit() else 0
            if expected_len > _MAX_TOOL_BYTES:
                if log:
                    log(f"warn: {name} is larger than {_MAX_TOOL_BYTES // (1024*1024)} MB cap")
                return False

            fd, raw_tmp = tempfile.mkstemp(prefix=f"{name}.", suffix=".download", dir=str(_HELPER_DIR))
            tmp_path = Path(raw_tmp)
            digest = hashlib.sha256()
            total = 0
            next_log = _PROGRESS_INTERVAL_BYTES
            with open(fd, "wb") as out:
                while True:
                    data = resp.read(1024 * 1024)
                    if not data:
                        break
                    total += len(data)
                    if total > _MAX_TOOL_BYTES:
                        if log:
                            log(f"warn: {name} exceeded {_MAX_TOOL_BYTES // (1024*1024)} MB cap, aborting")
                        return False
                    digest.update(data)
                    out.write(data)
                    if log and total >= next_log:
                        if expected_len:
                            pct = int((total / expected_len) * 100)
                            log(f"{name}: {total // (1024*1024)} MB / {expected_len // (1024*1024)} MB ({pct}%)")
                        else:
                            log(f"{name}: {total // (1024*1024)} MB")
                        next_log += _PROGRESS_INTERVAL_BYTES

            actual = digest.hexdigest()
            if actual.lower() != expected_sha256.lower():
                if log:
                    log(f"warn: {name} hash mismatch: {actual}")
                return False
            tmp_path.replace(dst)
            if log:
                log(f"installed -> {dst}")
            return True
    except urllib.error.URLError as exc:
        if log:
            log(f"warn: {name} download failed: {exc.reason}")
        return False
    except (TimeoutError, ssl.SSLError, OSError) as exc:
        if log:
            log(f"warn: {name} download error: {exc}")
        return False
    finally:
        if tmp_path and tmp_path.exists():
            try:
                tmp_path.unlink()
            except OSError:
                pass


def _download_individual_tools(log: LogFn = None) -> bool:
    if log:
        log("ReplayKit ffmpeg-tools.zip unavailable; trying individual tool downloads...")
    ok = True
    for name, dst, expected_sha256 in _TOOLS:
        ok = _download_tool(name, dst, expected_sha256, log=log) and ok
    return ok and _already_installed()


def _log_existing_tools(log: LogFn = None) -> None:
    if not log:
        return
    for name, dst, expected_sha256 in _TOOLS:
        if _valid_tool(dst, expected_sha256):
            log(f"{name} already present at {dst}")
        elif dst.is_file():
            log(f"warn: {name} hash mismatch; replacing it")


def _install_tools(log: LogFn = None) -> bool:
    if _already_installed():
        _log_existing_tools(log)
        return True

    copied = _copy_repo_tools(log=log)
    if _already_installed():
        return True
    if copied:
        return False

    if _download_and_extract_tools(log=log):
        return True
    return _download_individual_tools(log=log)


def install_ffmpeg(log: LogFn = None) -> bool:
    """Install ffmpeg.exe + ffprobe.exe next to the helper."""
    if _already_installed():
        _log_existing_tools(log)
        return True

    _HELPER_DIR.mkdir(parents=True, exist_ok=True)
    if log:
        log("installing ffmpeg tools...")

    ok = _install_tools(log=log)
    if not ok and log:
        log("ffmpeg install SKIPPED -- compress / trim will be unavailable")
        log("Check the network connection and re-run Apply.")
    return ok

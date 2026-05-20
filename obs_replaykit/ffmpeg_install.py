"""Install ffmpeg.exe + ffprobe.exe into the helper dir."""

from __future__ import annotations

import hashlib
import ssl
import tempfile
import time
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

_ARCHIVE_URLS = (
    "https://github.com/7Lzz/OBS-ReplayKit/raw/refs/heads/main/utils/downloads/ffmpeg-tools.zip",
)

_TOOLS = (
    ("ffmpeg.exe", _FFMPEG_DST, "228d7a8556258de907fdb55f36850078ebc7680b84ec30d84ea02e99bec1d1eb"),
    ("ffprobe.exe", _FFPROBE_DST, "0fde260f5abd35c9cafd96f594cc76365a780c1b73a90e35b6a3409ea1db1bf0"),
)

# size caps so a hijacked url/archive cant fill the users disk.
_MAX_ARCHIVE_BYTES = 130 * 1024 * 1024
_MAX_TOOL_BYTES = 110 * 1024 * 1024
_PROGRESS_INTERVAL_BYTES = 5 * 1024 * 1024
_PROGRESS_INTERVAL_SECONDS = 0.5


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


def _format_size(num_bytes: int) -> str:
    value = float(max(0, num_bytes))
    for unit in ("B", "KB", "MB", "GB"):
        if value < 1024.0 or unit == "GB":
            if unit == "B":
                return f"{int(value)} {unit}"
            return f"{value:.1f} {unit}"
        value /= 1024.0
    return f"{value:.1f} GB"


def _format_duration(seconds: float) -> str:
    if seconds < 0 or seconds == float("inf"):
        return "--:--"
    seconds = int(seconds)
    minutes, secs = divmod(seconds, 60)
    hours, minutes = divmod(minutes, 60)
    if hours:
        return f"{hours:d}:{minutes:02d}:{secs:02d}"
    return f"{minutes:02d}:{secs:02d}"


def _download_progress_line(total: int, expected_len: int, started_at: float) -> str:
    elapsed = max(0.001, time.monotonic() - started_at)
    rate = total / elapsed
    if expected_len > 0:
        pct = max(0.0, min(100.0, (total / expected_len) * 100.0))
        filled = int(round((pct / 100.0) * 24))
        bar = "#" * filled + "-" * (24 - filled)
        remaining = max(0, expected_len - total)
        eta = remaining / rate if rate > 0 else float("inf")
        return (
            f"Downloading ffmpeg tools [{bar}] {pct:5.1f}% "
            f"{_format_size(total)} / {_format_size(expected_len)} "
            f"@ {_format_size(int(rate))}/s ETA {_format_duration(eta)}"
        )
    return f"Downloading ffmpeg tools {_format_size(total)} @ {_format_size(int(rate))}/s"


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
                    log(f"warn: FFmpeg archive download returned HTTP {status}: {url}")
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
            last_log_at = time.monotonic()
            started_at = last_log_at
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
                    now = time.monotonic()
                    if log and (total >= next_log or (now - last_log_at) >= _PROGRESS_INTERVAL_SECONDS):
                        log(_download_progress_line(total, expected_len, started_at))
                        next_log += _PROGRESS_INTERVAL_BYTES
                        last_log_at = now

            if log:
                log(_download_progress_line(total, expected_len, started_at))
            return tmp_path
    except urllib.error.URLError as exc:
        if log:
            log(f"warn: FFmpeg archive download failed: {exc.reason}: {url}")
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
        log("Upload ffmpeg-tools.zip to utils/downloads on the public main branch, then re-run Apply.")
    return False


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

    return _download_and_extract_tools(log=log)


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
        log("Make the GitHub ZIP URL public, then re-run Apply.")
    return ok

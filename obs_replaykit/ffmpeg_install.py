"""download ffmpeg.exe + ffprobe.exe (gyan.devs essentials build) into the helper dir so the compress/trim pipelines have a real ffmpeg. obs only ships obs-ffmpeg-mux which is a stripped-down muxer."""

from __future__ import annotations

import io
import shutil
import ssl
import tempfile
import urllib.error
import urllib.request
import zipfile
from pathlib import Path
from typing import Callable, Optional, Tuple

from .config import OBS_CONFIG

LogFn = Optional[Callable[[str], None]]


# next to local_helper_server.ps1 under the consolidated obs-replayKit/ tree. find-toolinclipdirs in 52_compression.ps1 probes $script:helperroot first.
_HELPER_DIR = OBS_CONFIG / "obs-replayKit" / "scripts" / "streamable"
_FFMPEG_DST  = _HELPER_DIR / "ffmpeg.exe"
_FFPROBE_DST = _HELPER_DIR / "ffprobe.exe"

# stable alias that always redirects to the latest gyan.dev release -- avoids replaykit going stale every ffmpeg release. if the alias ever dies, swap to a pinned github asset and the next apply surfaces the change.
_FFMPEG_URL = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"

# size cap so a hijacked url cant fill the users disk. real archive is ~50 mb.
_MAX_DOWNLOAD_BYTES = 200 * 1024 * 1024


def _already_installed() -> bool:
    return _FFMPEG_DST.is_file() and _FFPROBE_DST.is_file()


def _download(url: str, log: LogFn = None) -> Optional[bytes]:
    """one-shot fetch with 60s timeout. urllib stdlib so we dont pull in requests/certifi for one download."""
    # explicit context becuase pythons bundled ca bundle can lag behind windows trust store; gyan.devs cert validates against the system store.
    ctx = ssl.create_default_context()
    req = urllib.request.Request(
        url,
        # friendly ua so gyan.devs analytics show obs replaykit instead of python-urllib (which some cdns rate-limit or 403).
        headers={"User-Agent": "OBSReplayKit/1.0 (+https://github.com)"},
    )
    try:
        with urllib.request.urlopen(req, timeout=60, context=ctx) as resp:
            # stream into bytesio so we can hit the size cap mid-read instead of allocating the whole file at once.
            buf = io.BytesIO()
            chunk = 1024 * 64
            total = 0
            while True:
                data = resp.read(chunk)
                if not data:
                    break
                total += len(data)
                if total > _MAX_DOWNLOAD_BYTES:
                    if log:
                        log(f"warn: download exceeded {_MAX_DOWNLOAD_BYTES // (1024*1024)} MB cap, aborting")
                    return None
                buf.write(data)
            if log:
                log(f"downloaded {total // (1024*1024)} MB")
            return buf.getvalue()
    except urllib.error.URLError as exc:
        if log:
            log(f"warn: download failed: {exc.reason}")
        return None
    except (TimeoutError, ssl.SSLError, OSError) as exc:
        if log:
            log(f"warn: download error: {exc}")
        return None


def _extract_binaries(zip_bytes: bytes, log: LogFn = None) -> Optional[Tuple[Path, Path]]:
    """pull ffmpeg.exe + ffprobe.exe out of the gyan archive into a temp dir. archive layout is ffmpeg-<version>-essentials_build/bin/<exe>; matched by bin/ tail so the version segment isnt hardcoded."""
    try:
        with zipfile.ZipFile(io.BytesIO(zip_bytes)) as zf:
            names = zf.namelist()
            ffmpeg_name = next(
                (n for n in names
                 if n.lower().endswith("/bin/ffmpeg.exe") or
                    n.lower().endswith("\\bin\\ffmpeg.exe")),
                None,
            )
            ffprobe_name = next(
                (n for n in names
                 if n.lower().endswith("/bin/ffprobe.exe") or
                    n.lower().endswith("\\bin\\ffprobe.exe")),
                None,
            )
            if not (ffmpeg_name and ffprobe_name):
                if log:
                    log("warn: ffmpeg.exe / ffprobe.exe not found in archive")
                return None

            tmpdir = Path(tempfile.mkdtemp(prefix="obsreplaykit_ffmpeg_"))
            ff_path = tmpdir / "ffmpeg.exe"
            fp_path = tmpdir / "ffprobe.exe"
            with zf.open(ffmpeg_name) as src, open(ff_path, "wb") as dst:
                shutil.copyfileobj(src, dst)
            with zf.open(ffprobe_name) as src, open(fp_path, "wb") as dst:
                shutil.copyfileobj(src, dst)
            return ff_path, fp_path
    except (zipfile.BadZipFile, OSError) as exc:
        if log:
            log(f"warn: archive extraction failed: {exc}")
        return None


def install_ffmpeg(log: LogFn = None) -> bool:
    """install ffmpeg.exe + ffprobe.exe next to the helper. on failure the rest of apply continues degraded -- compress/trim disabled until a future apply succeeds (or the user drops their own ffmpeg.exe into the clip folder, which find-toolinclipdirs also picks up)."""
    if _already_installed():
        if log:
            log(f"ffmpeg already present at {_FFMPEG_DST}")
        return True

    _HELPER_DIR.mkdir(parents=True, exist_ok=True)
    if log:
        log(f"downloading ffmpeg essentials build...")
        log(f"  {_FFMPEG_URL}")

    zip_bytes = _download(_FFMPEG_URL, log=log)
    if zip_bytes is None:
        if log:
            log("ffmpeg install SKIPPED -- compress / trim will be unavailable")
            log("Re-run Apply once you're back online to retry.")
        return False

    extracted = _extract_binaries(zip_bytes, log=log)
    if extracted is None:
        return False

    ff_tmp, fp_tmp = extracted
    try:
        # atomic move so a half-written exe from a crash never leaves the helper trying to spawn a corrupted binary.
        shutil.move(str(ff_tmp), str(_FFMPEG_DST))
        shutil.move(str(fp_tmp), str(_FFPROBE_DST))
        if log:
            log(f"installed -> {_FFMPEG_DST}")
            log(f"           + {_FFPROBE_DST.name}")
        return True
    except OSError as exc:
        if log:
            log(f"warn: could not move binaries into place: {exc}")
        return False
    finally:
        # best-effort cleanup of the temp dir.
        try:
            ff_tmp.parent.rmdir()
        except OSError:
            pass

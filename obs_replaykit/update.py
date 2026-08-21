"""updater mode for OBS ReplayKit release installers."""

from __future__ import annotations

import ctypes
import os
import subprocess
import sys
import tempfile
import time
from pathlib import Path
from typing import Callable, Optional

from . import __version__
from .installer import install_replaykit_runtime_update
from .obs import OBS_START_ARGS, cleanup_crash_flags, close_obs, find_obs_exe

LogFn = Optional[Callable[[str], None]]


def _log_file() -> Path:
    return Path(tempfile.gettempdir()) / "OBSReplayKitUpdate.log"


def _log(message: str) -> None:
    try:
        with _log_file().open("a", encoding="utf-8") as handle:
            handle.write(f"[{time.strftime('%Y-%m-%d %H:%M:%S')}] {message}\n")
    except OSError:
        pass


def _wait_for_pid(pid: int, timeout_s: int = 60) -> bool:
    if pid <= 0 or os.name != "nt":
        return True
    kernel32 = ctypes.windll.kernel32
    handle = kernel32.OpenProcess(0x00100000, False, int(pid))  # SYNCHRONIZE
    if not handle:
        return True
    try:
        result = kernel32.WaitForSingleObject(handle, int(timeout_s * 1000))
        return result == 0  # WAIT_OBJECT_0
    finally:
        kernel32.CloseHandle(handle)


def _safe_cleanup_dir(path: str | None) -> Optional[Path]:
    if not path:
        return None
    temp_root = Path(tempfile.gettempdir()).resolve()
    target = Path(path).resolve()
    if target.name != "ReplayKitUpdate":
        return None
    try:
        target.relative_to(temp_root)
    except ValueError:
        return None
    return target


def _ps_single_quote(value: str) -> str:
    return "'" + value.replace("'", "''") + "'"


def _schedule_cleanup(path: str | None) -> None:
    target = _safe_cleanup_dir(path)
    if target is None:
        return
    command = (
        "Start-Sleep -Seconds 3; "
        f"Remove-Item -LiteralPath {_ps_single_quote(str(target))} -Recurse -Force -ErrorAction SilentlyContinue"
    )
    flags = getattr(subprocess, "CREATE_NO_WINDOW", 0) | getattr(subprocess, "DETACHED_PROCESS", 0)
    try:
        subprocess.Popen(
            [
                "powershell.exe",
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-WindowStyle",
                "Hidden",
                "-Command",
                command,
            ],
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            close_fds=True,
            creationflags=flags,
        )
    except OSError as exc:
        _log(f"cleanup launch failed: {exc}")


def _launch_obs_path(obs_path: str | None, log: LogFn = None) -> bool:
    obs = Path(obs_path) if obs_path else find_obs_exe()
    if obs is None or not obs.is_file():
        if log:
            log("OBS executable was not found for relaunch.")
        return False
    flags = getattr(subprocess, "CREATE_NO_WINDOW", 0) | getattr(subprocess, "DETACHED_PROCESS", 0)
    try:
        subprocess.Popen(
            [str(obs), *OBS_START_ARGS.split()],
            cwd=str(obs.parent),
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            close_fds=True,
            creationflags=flags,
        )
        if log:
            log(f"relaunched OBS: {obs}")
        return True
    except OSError as exc:
        if log:
            log(f"OBS relaunch failed: {exc}")
        return False


def run_update_mode(
    cleanup_dir: str | None = None,
    relaunch_obs: str | None = None,
    wait_pid: int = 0,
    start_delay_ms: int = 0,
    relaunch: bool = True,
) -> int:
    """run a non-interactive repair/update install from a downloaded release exe."""
    def log(message: str) -> None:
        _log(message)
        try:
            print(message)
        except Exception:
            pass

    try:
        _log(f"update starting version={__version__}")
        if start_delay_ms > 0:
            time.sleep(min(start_delay_ms, 5000) / 1000.0)

        close_obs(log=log)
        _wait_for_pid(int(wait_pid or 0), timeout_s=60)
        cleanup_crash_flags(log=log)
        count = install_replaykit_runtime_update(log=log)
        log(f"runtime update copied {count} file(s)")
        if relaunch:
            _launch_obs_path(relaunch_obs, log=log)
        return 0
    except Exception as exc:
        _log(f"update failed: {exc}")
        return 1
    finally:
        _schedule_cleanup(cleanup_dir)


class CleanupProgress:
    def __init__(self) -> None:
        self.total_steps = 0
        self.issues: list[str] = []

    def render(self, completed: int, title: str, detail: str, state: str = "working") -> None:
        status = "done" if state == "done" else ("failed" if state == "failed" else "working")
        message = f"[{completed}/{max(1, self.total_steps)}] {status}: {title}"
        if detail:
            message += f" - {detail}"
        _log(message)
        try:
            print(message)
        except Exception:
            pass

    def log(self, message: str) -> None:
        text = str(message or "").strip()
        if not text:
            return
        _log(text)
        lowered = text.lower()
        if any(word in lowered for word in ("warn", "failed", "missing", "not found", "skipped", "timed out", "permission denied")):
            self.add_issue(text)
        try:
            print(text)
        except Exception:
            pass

    def add_issue(self, message: str) -> None:
        cleaned = " ".join(str(message or "").split())
        if len(cleaned) > 118:
            cleaned = cleaned[:115] + "..."
        if cleaned and cleaned not in self.issues:
            self.issues.append(cleaned)


def run_cleanup_mode(start_delay_ms: int = 0, keep_user_settings: bool = True) -> int:
    from .cleanup import run_cleanup

    try:
        _log("cleanup starting")
        if start_delay_ms > 0:
            time.sleep(min(start_delay_ms, 5000) / 1000.0)
        progress = CleanupProgress()
        issues = run_cleanup(progress, keep_user_settings=keep_user_settings)
        if issues:
            _log(f"cleanup complete with {len(issues)} warning(s)")
        else:
            _log("cleanup complete")
        return 0
    except Exception as exc:
        _log(f"cleanup failed: {exc}")
        return 1


def update_arg_parser():
    import argparse

    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--update", action="store_true")
    parser.add_argument("--cleanup-dir", default="")
    parser.add_argument("--relaunch-obs", default="")
    parser.add_argument("--no-relaunch-obs", action="store_true")
    parser.add_argument("--wait-pid", type=int, default=0)
    parser.add_argument("--start-delay-ms", type=int, default=0)
    return parser


def cleanup_arg_parser():
    import argparse

    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--cleanup", action="store_true")
    parser.add_argument("--start-delay-ms", type=int, default=0)
    parser.add_argument("--remove-user-settings", action="store_true")
    return parser


def try_run_update_from_argv(argv: list[str] | None = None) -> Optional[int]:
    argv = list(sys.argv[1:] if argv is None else argv)
    if "--cleanup" in argv:
        args, _ = cleanup_arg_parser().parse_known_args(argv)
        return run_cleanup_mode(start_delay_ms=args.start_delay_ms, keep_user_settings=not args.remove_user_settings)
    if "--update" not in argv:
        return None
    args, _ = update_arg_parser().parse_known_args(argv)
    return run_update_mode(
        cleanup_dir=args.cleanup_dir,
        relaunch_obs=args.relaunch_obs,
        wait_pid=args.wait_pid,
        start_delay_ms=args.start_delay_ms,
        relaunch=not args.no_relaunch_obs,
    )

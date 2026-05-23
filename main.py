"""OBS ReplayKit setup entry point."""

import sys

from obs_replaykit.fast_exit import fast_exit, install_console_close_handler


install_console_close_handler()

from obs_replaykit.cli import run_cli
from obs_replaykit.update import try_run_update_from_argv


def main() -> int:
    try:
        update_rc = try_run_update_from_argv()
        if update_rc is not None:
            return update_rc
        return run_cli()
    except KeyboardInterrupt:
        return 1
    except SystemExit:
        raise
    except Exception:
        import traceback
        tb = traceback.format_exc()
        log_path = None
        try:
            from pathlib import Path
            root = Path(sys.executable).resolve().parent if getattr(sys, "frozen", False) else Path(__file__).resolve().parent
            log_path = root / "OBSReplayKit-error.log"
            log_path.write_text(tb, encoding="utf-8")
        except Exception:
            pass
        try:
            print("OBS ReplayKit setup failed.")
            if log_path:
                print(f"Details were written to: {log_path}")
            print()
            print(tb)
            input("Press Enter to exit...")
        except Exception:
            pass
        return 1


if __name__ == "__main__":
    fast_exit(main())

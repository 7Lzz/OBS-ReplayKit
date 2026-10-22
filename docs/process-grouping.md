# Shared process host

The same-executable probe grouped three real processes on Windows 11 build
26300.9711. Matching AppUserModelIDs and Windows job membership did not group
different executables. This changes Task Manager presentation, not process count
or memory use.

The implementation uses a thin native executable with OBS frontend, CEF subprocess
and managed ReplayKit helper roles. Components are loaded into the selected role's
process; starting the three original executables behind a launcher is insufficient.
Frontend/browser patches are built from the installed OBS release, currently
32.2.2. No third-party DLL is injected into another process.

Build checkout: `%LOCALAPPDATA%/ReplayKit/host-build/obs-src-32.2.2`.
Live installation is authorized by the user. Back up every replaced file and verify
no recording or clip operation is running before the live restart. Keep stock OBS
available for rollback. Detached updater/watchdog processes must remain usable
while the installed host is replaced.

Status: installed into the local live OBS 32.2.2 installation for evaluation.
After a normal restart the frontend, automatically started ReplayKit helper and
four CEF children all used `C:/Program Files/obs-studio/bin/64bit/obs64.exe`.
The helper reported `obsConnected=true`, `recordActive=false` and
`replayBufferActive=true`. The real Task Manager group still requires a visual
check; matching executable paths alone are not that visual confirmation.

Verification completed:

- Native host/frontend/browser build, managed helper merge and installer build.
- Host component check, OBS version and managed helper path/role diagnostics.
- Browser rendering callback from a portable browser-only scene.
- Recording and replay-buffer saving through authenticated obs-websocket in that
  isolated profile, with output files confirmed on disk.
- Normal portable shutdown removed all its browser children.
- Staged install/restore under Windows PowerShell restored original hashes and
  removed added files, including the repair cache.
- Live graceful exit/relaunch and automatic helper startup; live controls and
  cached installer verification. No live recording was started for testing.

The helper resolves assets relative to its managed assembly. Lua reads the native
executable path through Win32 rather than Lua's cached C runtime environment.
Restart enumeration excludes the current helper, which now also has the `obs64`
process name. Detached update windows, watchdogs and upload polling workers retain
their managed executable so they can survive OBS shutdown. These temporary workers
may appear separately in Task Manager.

The live backup location is saved in ignored `build/process-host/live-backup.txt`.
It contains original and staged files, hashes, an install manifest and a local
`manage-live.ps1` wrapper. Original profiles, scenes and clip metadata are not
replaced. The repair installer cache is updated along with the helper and Lua.
Exit OBS using the tray's Exit command before restoring; closing to the tray is
insufficient. From the repository root, restore with the normal Windows UAC prompt:

```powershell
$backup = (Get-Content -LiteralPath './build/process-host/live-backup.txt' -Raw).Trim()
$script = Join-Path $backup 'manage-live.ps1'
Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -ArgumentList (
    '-NoProfile -ExecutionPolicy Bypass -File "' + $script + '" -Mode Restore'
)
```

Check `Restore-result.json` and `Restore.log` in that backup before starting OBS.
Restore refuses unexpected file changes instead of overwriting later work.
The checked-in `install.ps1` also supports Prepare, Apply and Restore with explicit
installation roots, rejects traversal/reparse points and checks hashes before
writing. Each replacement is atomic, and a failed Apply attempts Restore.

This remains an experimental source-built OBS frontend. The stock OBS updater is
disabled only for the hosted process: replacing just the host executable would
break browser dispatch. Restore stock OBS before updating OBS itself or installing
a public ReplayKit release that predates the managed hosting bridge. The local
installer includes the compatible helper; the four native host components are not
bundled into the normal release installer. Official Twitch, Restream and YouTube
account integrations are not compiled in because vendor OAuth credentials are not
available in this source build. Grouping does not reduce process count or RAM.

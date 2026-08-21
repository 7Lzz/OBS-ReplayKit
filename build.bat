@echo off
setlocal
cd /d "%~dp0"

rem build one standalone obsreplaykit.exe in this folder. uses the active python install and does not create a repo-local venv or tools folder.

set "PY="
where py     >nul 2>nul && set "PY=py"
if "%PY%"=="" ( where python >nul 2>nul && set "PY=python" )
if "%PY%"=="" goto :no_python

echo Python: %PY%
echo.

rem pyinstaller must already be installed for the selected python. this keeps the repo build from creating a venv or installing packages behind the scenes.
echo [1/4] Checking PyInstaller ...
%PY% -m PyInstaller --version >nul 2>nul
if errorlevel 1 goto :no_pyinstaller

rem obs_replaykit\__init__.py is the single source of truth for release version.
echo Syncing bundled ReplayKit version ...
%PY% -c "import json,pathlib,obs_replaykit; p=pathlib.Path('assets/obs-studio/obs-replayKit/version.json'); p.write_text(json.dumps({'version': obs_replaykit.__version__}, indent=2) + '\n', encoding='utf-8'); print('    ReplayKit version ' + obs_replaykit.__version__)"
if errorlevel 1 goto :build_fail

rem use upx only when it is already on path. otherwise build without compression instead of downloading tools into the repo.
echo [2/4] Locating UPX ...
set "UPX_ARG=--noupx"
set "UPX_STATUS=disabled"
where upx >nul 2>nul && goto :upx_on_path
echo     UPX not found on PATH; building without UPX compression.
goto :upx_ready

:upx_on_path
for /f "tokens=*" %%P in ('where upx') do (
    if not defined UPX_BIN set "UPX_BIN=%%P"
)
for /f "tokens=2" %%v in ('upx --version 2^>nul ^| findstr /B /C:"upx "') do (
    if not defined UPX_VER set "UPX_VER=%%v"
)
echo     Found UPX %UPX_VER% on PATH: %UPX_BIN%
set "UPX_ARG="
set "UPX_STATUS=enabled (UPX %UPX_VER% from PATH)"

:upx_ready

rem remove old pyinstaller output before rebuilding so the folder only keeps the latest final exe.
echo [3/4] Cleaning previous build artifacts ...
if exist "build"             rmdir /S /Q "build"
if exist "dist"              rmdir /S /Q "dist"
if exist "OBSReplayKit.spec" del   /Q   "OBSReplayKit.spec"
if exist "OBSReplayKit.exe"  del   /Q   "OBSReplayKit.exe"

rem build the small branded helper used by the obs replaykit dock.
echo [3.5/4] Compiling helper launcher into assets\ ...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0utils\launcher\build_launcher.ps1"
if errorlevel 1 goto :launcher_fail

rem builds the native tray plugin (view clips / share preview / restart obs) and bundles it into assets\ itself, same as build_launcher.ps1 already does for the launcher exe above; build.ps1 compiles against whatever obs version is installed and caches its qt6/obs-headers deps under %temp%\replaykit-tray-build (deliberately outside build\, which gets wiped every run), and skips the whole rebuild when the bundled dll is already newer than its source + the installed obs -- requires gh (authenticated) and vs2022/2019 build tools with the c++ workload, see utils\obs-plugins\replaykit-tray\build.ps1 for details.
echo [3.6/4] Compiling tray plugin into assets\ ...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0utils\obs-plugins\replaykit-tray\build.ps1"
if errorlevel 1 goto :tray_plugin_fail
if not exist "%~dp0assets\obs-plugins\replaykit-tray\bin\64bit\replaykit-tray.dll" goto :tray_plugin_fail

rem build the final exe directly into the repo root so no dist folder is left behind.
rem upx-exclude covers every bundled pe binary pyinstaller would otherwise try to compress -- replaykit-tray.dll needed its own entry becuase upx had silently compressed it (54272 -> 23040 bytes) with no build error, and obs just failed to load the result with no visible error either, since a upx-packed dll cant self-decompress the way a upx-packed standalone exe can. confirmed 2026-08-08.
echo [4/4] Building OBSReplayKit.exe ...
echo.
%PY% -m PyInstaller ^
    --noconfirm ^
    --onefile ^
    --console ^
    --log-level WARN ^
    --name "OBSReplayKit" ^
    --distpath "%CD%" ^
    --workpath "%CD%\build" ^
    --specpath "%CD%" ^
    --icon "%~dp0utils\icon\obs-replaykit.ico" ^
    --add-data "assets;assets" ^
    --collect-submodules obs_replaykit ^
    --uac-admin ^
    --upx-exclude "OBSReplayKit.exe" ^
    --upx-exclude "replaykit-tray.dll" ^
    %UPX_ARG% ^
    main.py
if errorlevel 1 goto :build_fail

rem trim intermediates, leaving only the final exe.
if exist "build"             rmdir /S /Q "build"
if exist "dist"              rmdir /S /Q "dist"
if exist "OBSReplayKit.spec" del   /Q   "OBSReplayKit.spec"

set "EXE_PATH=%CD%\OBSReplayKit.exe"
if not exist "%EXE_PATH%" goto :build_fail
echo Generating release hash ...
powershell -NoProfile -ExecutionPolicy Bypass -Command "(Get-FileHash -LiteralPath '%EXE_PATH%' -Algorithm SHA256).Hash + '  OBSReplayKit.exe' | Set-Content -LiteralPath '%EXE_PATH%.sha256' -Encoding ASCII"
if errorlevel 1 goto :build_fail
for %%I in ("%EXE_PATH%") do set /a "EXE_MB=%%~zI / 1048576"
for %%I in ("%EXE_PATH%") do set "EXE_BYTES=%%~zI"

echo.
echo ============================================================
echo  Build complete.
echo ============================================================
echo  Output:  %EXE_PATH%
echo  Hash:    %EXE_PATH%.sha256
echo  Size:    ~%EXE_MB% MB  (%EXE_BYTES% bytes)
echo  UPX:     %UPX_STATUS%
echo.
echo  Note: VC++ redist is not bundled; it downloads from Microsoft only
echo        when missing. Input-overlay presets are trimmed to WASD/mouse.
echo ============================================================
echo.
pause
endlocal
exit /b 0


:no_python
echo.
echo Python was not found on PATH.
echo Install Python from https://www.python.org/downloads/ then re-run.
echo.
pause
endlocal
exit /b 1

:no_pyinstaller
echo.
echo PyInstaller is not installed for %PY%.
echo Install it with: %PY% -m pip install --user pyinstaller
echo.
pause
endlocal
exit /b 1

:build_fail
echo.
echo Build FAILED.
pause
endlocal
exit /b 1

:launcher_fail
echo.
echo Failed to compile utils\launcher\OBSReplayKit.cs (csc.exe error).
echo Verify .NET Framework 4.x is installed (default on Windows 10/11).
pause
endlocal
exit /b 1

:tray_plugin_fail
echo.
echo Failed to build the tray plugin (utils\obs-plugins\replaykit-tray\build.ps1).
echo Requires: an installed OBS, the GitHub CLI (gh, already authenticated), and
echo VS2022 or VS2019 Build Tools with the "Desktop development with C++" workload.
pause
endlocal
exit /b 1

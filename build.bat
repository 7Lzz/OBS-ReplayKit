@echo off
setlocal
cd /d "%~dp0"

rem build OBSReplayKit.exe (ReplayKitSetup, the c# port) straight into this folder via dotnet publish.
rem the old pyinstaller-based build (single-file, self-extracting bootloader) is retired -- that
rem self-extraction pattern was the dominant driver of av false positives across every pyinstaller
rem variant tried, and no build flag fixed it. a compiled net48 exe has no bootstrap/extract step,
rem so assets\ ships as a plain sibling folder instead of embedded data.

where dotnet >nul 2>nul
if errorlevel 1 goto :no_dotnet

echo [1/5] Syncing bundled ReplayKit version ...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0utils\sync_version.ps1"
if errorlevel 1 goto :build_fail

rem build the small branded helper used by the obs replaykit dock.
echo [2/5] Compiling helper launcher into assets\ ...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0utils\launcher\build_launcher.ps1"
if errorlevel 1 goto :launcher_fail

rem builds the native tray plugin (view clips / share preview / restart obs) and bundles it into assets\ itself, same as build_launcher.ps1 already does for the launcher exe above; build.ps1 compiles against whatever obs version is installed and caches its qt6/obs-headers deps under %temp%\replaykit-tray-build (deliberately outside build\, which gets wiped every run), and skips the whole rebuild when the bundled dll is already newer than its source + the installed obs -- requires gh (authenticated) and vs2022/2019 build tools with the c++ workload, see utils\obs-plugins\replaykit-tray\build.ps1 for details.
echo [3/5] Compiling tray plugin into assets\ ...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0utils\obs-plugins\replaykit-tray\build.ps1"
if errorlevel 1 goto :tray_plugin_fail
if not exist "%~dp0assets\obs-plugins\replaykit-tray\bin\64bit\replaykit-tray.dll" goto :tray_plugin_fail

rem remove old output before rebuilding so the folder only keeps the latest final exe.
echo [4/5] Cleaning previous build artifacts ...
if exist "ReplayKitSetup\bin"    rmdir /S /Q "ReplayKitSetup\bin"
if exist "ReplayKitSetup\obj"    rmdir /S /Q "ReplayKitSetup\obj"
if exist "publish_tmp"           rmdir /S /Q "publish_tmp"
if exist "OBSReplayKit.exe"      del   /Q   "OBSReplayKit.exe"
if exist "OBSReplayKit.exe.sha256" del /Q   "OBSReplayKit.exe.sha256"
rem stale companions from before the exe shipped as a single merged file -- harmless to leave the check in even once every checkout has moved past them.
if exist "OBSReplayKit.exe.config" del /Q   "OBSReplayKit.exe.config"
if exist "Newtonsoft.Json.dll"   del   /Q   "Newtonsoft.Json.dll"

rem publish, then merge Newtonsoft.Json.dll straight into the exe (see ILRepackPublish in
rem ReplayKitSetup.csproj) so the shipped artifact is one file with nothing else alongside it.
rem the small auto-generated .exe.config (declares the target framework) isnt required at
rem runtime on any machine with only .net framework 4.8 present, which is every current
rem windows install, so its dropped too -- confirmed by running without it. no .pdb copied
rem out either -- debug symbols arent needed by end users and stay in ReplayKitSetup\bin like
rem any other build byproduct.
echo [5/5] Building OBSReplayKit.exe ...
echo.
dotnet publish "ReplayKitSetup\ReplayKitSetup.csproj" -c Release -o "publish_tmp" --nologo
if errorlevel 1 goto :build_fail

copy /Y "publish_tmp\OBSReplayKit.exe" "OBSReplayKit.exe" >nul
if errorlevel 1 goto :build_fail
rmdir /S /Q "publish_tmp"

set "EXE_PATH=%CD%\OBSReplayKit.exe"
if not exist "%EXE_PATH%" goto :build_fail

rem the shipped exe is safely copied out to the repo root above, so ReplayKitSetup\bin and \obj
rem (the compiled intermediates dotnet publish leaves behind, including the pre-merge exe copy
rem and the debug symbols) are done being useful -- clear them so the folder doesnt accumulate
rem build byproducts across runs. next build recreates both from scratch either way, since the
rem cleanup step above already force-cleans them before every build regardless.
if exist "ReplayKitSetup\bin" rmdir /S /Q "ReplayKitSetup\bin"
if exist "ReplayKitSetup\obj" rmdir /S /Q "ReplayKitSetup\obj"

echo Generating release hash ...
powershell -NoProfile -ExecutionPolicy Bypass -Command "(Get-FileHash -LiteralPath '%EXE_PATH%' -Algorithm SHA256).Hash + '  OBSReplayKit.exe' | Set-Content -LiteralPath '%EXE_PATH%.sha256' -Encoding ASCII"
if errorlevel 1 goto :build_fail
for %%I in ("%EXE_PATH%") do set /a "EXE_KB=%%~zI / 1024"
for %%I in ("%EXE_PATH%") do set "EXE_BYTES=%%~zI"

echo.
echo ============================================================
echo  Build complete.
echo ============================================================
echo  Output:  %EXE_PATH%
echo  Hash:    %EXE_PATH%.sha256
echo  Size:    ~%EXE_KB% KB  (%EXE_BYTES% bytes)
echo.
echo  Note: single file, nothing else needs to ship alongside it. VC++ redist
echo        is not bundled; it downloads from Microsoft only when missing.
echo        Input-overlay presets are trimmed to WASD/mouse.
echo ============================================================
echo.
pause
endlocal
exit /b 0


:no_dotnet
echo.
echo .NET SDK was not found on PATH.
echo Install it from https://dotnet.microsoft.com/download then re-run.
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

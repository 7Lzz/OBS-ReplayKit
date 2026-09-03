@echo off
setlocal
cd /d "%~dp0"

rem build OBSReplayKit.exe (ReplayKitSetup, the c# port) into build\ via dotnet publish.
rem the old pyinstaller-based build (single-file, self-extracting bootloader) is retired -- that
rem self-extraction pattern was the dominant driver of av false positives across every pyinstaller
rem variant tried, and no build flag fixed it. a compiled net48 exe has no bootstrap/extract step,
rem so assets\ ships as a plain sibling folder instead of embedded data.

set "BUILD_DIR=%~dp0build"

where dotnet >nul 2>nul
if errorlevel 1 goto :no_dotnet

echo [1/5] Syncing bundled ReplayKit version ...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0utils\sync_version.ps1"
if errorlevel 1 goto :build_fail

rem build the ReplayKit helper server (Clips browser, dock http api, upload/compress/trim workers) straight into assets\ -- same net48+ILRepack pattern as the setup exe itself (step 5 below), just a different project and a different sibling-file drop location; this replaced a much smaller thin launcher that used to just host PowerShell in-process to run the (now-retired) PS implementation of the same server. build_helper.ps1 skips the rebuild entirely when the bundled exe is already newer than every source file, same convention as the tray plugin build below.
echo [2/5] Compiling ReplayKit helper into assets\ ...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0utils\build_helper.ps1"
if errorlevel 1 goto :launcher_fail
if not exist "assets\obs-studio\obs-replayKit\scripts\helper\OBSReplayKit.exe" goto :launcher_fail

rem builds the ReplayKit plugin (clips / share preview / restart obs) and bundles it into assets\ itself, same as the helper build above does for the helper exe; build.ps1 compiles against whatever obs version is installed and caches its qt6/obs-headers deps under %temp%\replaykit-tray-build (deliberately outside build\, which gets wiped every run), and skips the whole rebuild when the bundled dll is already newer than its source + the installed obs -- requires gh (authenticated) and vs2022/2019 build tools with the c++ workload, see utils\obs-plugins\replaykit-tray\build.ps1 for details.
echo [3/5] Compiling tray plugin into assets\ ...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0utils\obs-plugins\replaykit-tray\build.ps1"
if errorlevel 1 goto :tray_plugin_fail
if not exist "%~dp0assets\obs-plugins\replaykit\bin\64bit\replaykit.dll" goto :tray_plugin_fail

rem remove old output before rebuilding so build\ only ever holds the latest release files.
echo [4/5] Cleaning previous build artifacts ...
if exist "ReplayKitSetup\bin"    rmdir /S /Q "ReplayKitSetup\bin"
if exist "ReplayKitSetup\obj"    rmdir /S /Q "ReplayKitSetup\obj"
if exist "publish_tmp"           rmdir /S /Q "publish_tmp"
if exist "%BUILD_DIR%"           rmdir /S /Q "%BUILD_DIR%"
mkdir "%BUILD_DIR%"
rem stale companions from the days the exe shipped to the repo root, plus the earlier
rem per-file .sha256 pair now replaced by one combined checksums.sha256 -- harmless to
rem leave the check in even once every checkout has moved past them.
if exist "OBSReplayKit.exe"        del /Q "OBSReplayKit.exe"
if exist "OBSReplayKit.exe.sha256" del /Q "OBSReplayKit.exe.sha256"
if exist "OBSReplayKit.exe.config" del /Q "OBSReplayKit.exe.config"
if exist "Newtonsoft.Json.dll"        del /Q "Newtonsoft.Json.dll"
if exist "Newtonsoft.Json.dll.sha256" del /Q "Newtonsoft.Json.dll.sha256"

rem publish; Newtonsoft.Json.dll ships as a loose sibling file next to the exe rather than
rem ilrepack-merged into it (see ReplayKitSetup.csproj) -- a merged single file reads as
rem packed to several av heuristics, and this is a completely standard two-assembly .net
rem deploy. the auto-updater (ReplayKitHelper/Update.cs) downloads both plus checksums.sha256
rem as separate github release assets, so all three have to be uploaded when cutting a
rem release, not just the exe. the small auto-generated .exe.config (declares the target framework) isnt required at
rem runtime on any machine with only .net framework 4.8 present, which is every current
rem windows install, so its dropped too -- confirmed by running without it. no .pdb copied
rem out either -- debug symbols arent needed by end users and stay in ReplayKitSetup\bin like
rem any other build byproduct.
echo [5/5] Building OBSReplayKit.exe ...
echo.
dotnet publish "ReplayKitSetup\ReplayKitSetup.csproj" -c Release -o "publish_tmp" --nologo
if errorlevel 1 goto :build_fail

copy /Y "publish_tmp\OBSReplayKit.exe" "%BUILD_DIR%\OBSReplayKit.exe" >nul
if errorlevel 1 goto :build_fail
copy /Y "publish_tmp\Newtonsoft.Json.dll" "%BUILD_DIR%\Newtonsoft.Json.dll" >nul
if errorlevel 1 goto :build_fail
rmdir /S /Q "publish_tmp"

set "EXE_PATH=%BUILD_DIR%\OBSReplayKit.exe"
if not exist "%EXE_PATH%" goto :build_fail

rem the shipped files are safely copied out to build\ above, so ReplayKitSetup\bin and \obj
rem (the compiled intermediates dotnet publish leaves behind, including the debug symbols)
rem are done being useful -- clear them so the folder doesnt accumulate build byproducts
rem across runs. next build recreates both from scratch either way, since the cleanup step
rem above already force-cleans them before every build regardless.
if exist "ReplayKitSetup\bin" rmdir /S /Q "ReplayKitSetup\bin"
if exist "ReplayKitSetup\obj" rmdir /S /Q "ReplayKitSetup\obj"

rem one combined checksums.sha256 (one "<hash>  <filename>" line per asset) instead of a
rem .sha256 per file -- ReplayKitHelper/Update.cs parses both lines back out of it, same
rem real integrity check per file, one fewer file to upload per release.
echo Generating checksums.sha256 ...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$e=(Get-FileHash -LiteralPath '%EXE_PATH%' -Algorithm SHA256).Hash; $d=(Get-FileHash -LiteralPath '%BUILD_DIR%\Newtonsoft.Json.dll' -Algorithm SHA256).Hash; ($e+'  OBSReplayKit.exe'+[Environment]::NewLine+$d+'  Newtonsoft.Json.dll') | Set-Content -LiteralPath '%BUILD_DIR%\checksums.sha256' -Encoding ASCII"
if errorlevel 1 goto :build_fail
for %%I in ("%EXE_PATH%") do set /a "EXE_KB=%%~zI / 1024"
for %%I in ("%EXE_PATH%") do set "EXE_BYTES=%%~zI"

rem assets\ is embedded in the exe as a zip resource (PackAssetBundle in ReplayKitSetup.csproj, unpacked by AssetBundle.cs). without it the exe is around 1 mb and the auto-updater, which downloads it plus Newtonsoft.Json.dll, closes obs and then has nothing to install -- so a build that lost the payload must not reach a release.
if %EXE_BYTES% LSS 6000000 goto :bundle_fail

echo.
echo ============================================================
echo  Build complete.
echo ============================================================
echo  Output:  %BUILD_DIR%\
echo    OBSReplayKit.exe            (~%EXE_KB% KB, %EXE_BYTES% bytes)
echo    Newtonsoft.Json.dll
echo    checksums.sha256
echo.
echo  Note: Newtonsoft.Json.dll ships as a loose sibling file next to the exe, not
echo        merged into it -- upload all three files when cutting a github release.
echo        assets\ is embedded inside the exe itself and unpacked at runtime, so
echo        that part of the auto-update download still works off just these two
echo        files. VC++ redist is not bundled; it downloads from Microsoft only
echo        when missing. Input-overlay presets are trimmed to WASD/mouse.
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

:bundle_fail
echo.
echo Build FAILED: OBSReplayKit.exe is only %EXE_BYTES% bytes, so assets\ was not
echo embedded. Shipping it would break both fresh installs and the auto-update.
echo Check the PackAssetBundle target in ReplayKitSetup\ReplayKitSetup.csproj.
del /Q "%EXE_PATH%" >nul 2>nul
del /Q "%BUILD_DIR%\Newtonsoft.Json.dll" >nul 2>nul
del /Q "%BUILD_DIR%\checksums.sha256" >nul 2>nul
pause
endlocal
exit /b 1

:launcher_fail
echo.
echo Failed to compile or install the ReplayKit helper (ReplayKitHelper.csproj).
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

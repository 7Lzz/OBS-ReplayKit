@echo off
setlocal
rem Windows PowerShell must use its own modules when launched from PowerShell 7.
set "PSModulePath="
powershell -NoProfile -File "%~dp0utils\build.ps1"
set "BUILD_RESULT=%ERRORLEVEL%"
if not "%~1"=="--no-pause" pause
exit /b %BUILD_RESULT%

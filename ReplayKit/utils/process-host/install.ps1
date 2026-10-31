param(
    [ValidateSet('Prepare', 'Apply', 'Restore')][string]$Mode = 'Prepare',
    [string]$Backup = '',
    [string]$ObsRoot = '',
    [string]$ReplayKitRoot = '',
    [string]$SetupCacheRoot = '',
    [string]$Components = ''
)
throw 'Process-role installation is part of ReplayKit setup now. Run build.bat, then build/OBSReplayKit.exe --update. Setup preserves the stock OBS executable and backs up the original browser; uninstall restores it. The former full-frontend prototype installer is retired.'
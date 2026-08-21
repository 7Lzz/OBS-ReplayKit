function Get-ReplayKitSetupCacheDir {
    if ($env:LOCALAPPDATA) { return (Join-Path $env:LOCALAPPDATA 'OBS ReplayKit') }
    return (Join-Path (Get-UserProfile) 'AppData\Local\OBS ReplayKit')
}

function Get-ReplayKitSetupExecutable {
    return (Join-Path (Get-ReplayKitSetupCacheDir) 'OBSReplayKitSetup.exe')
}

function Start-ReplayKitCleanupFromSettings([string]$body) {
    if ([string]::IsNullOrWhiteSpace($body)) {
        throw 'Missing uninstall confirmation.'
    }
    $incoming = ConvertTo-PlainHash (ConvertFrom-Json $body)
    if (-not $incoming.ContainsKey('confirm') -or [string]$incoming.confirm -ne 'confirm') {
        throw 'Invalid uninstall confirmation.'
    }
    $keepUserSettings = $true
    if ($incoming.ContainsKey('keepUserSettings')) {
        if ($incoming.keepUserSettings -isnot [bool]) {
            throw 'Keep user settings must be a JSON boolean.'
        }
        $keepUserSettings = [bool]$incoming.keepUserSettings
    }

    $cacheDir = [System.IO.Path]::GetFullPath((Get-ReplayKitSetupCacheDir))
    $setupExe = [System.IO.Path]::GetFullPath((Get-ReplayKitSetupExecutable))
    $cachePrefix = $cacheDir.TrimEnd('\') + '\'
    if (-not $setupExe.StartsWith($cachePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Invalid setup executable path.'
    }
    if (-not (Test-Path -LiteralPath $setupExe -PathType Leaf)) {
        throw 'ReplayKit setup executable is missing. Re-run the installer once, then uninstall from Advanced.'
    }

    $argList = @('--cleanup', '--start-delay-ms', '900')
    if (-not $keepUserSettings) {
        $argList += '--remove-user-settings'
    }
    if ($script:IsAdmin -eq $true) {
        $cmdLine = (Quote-ReplayKitProcessArg $setupExe) + ' ' +
                   (($argList | ForEach-Object { Quote-ReplayKitProcessArg ([string]$_) }) -join ' ')
        $cleanupPid = [ReplayKitNative]::SpawnDetached($cmdLine, $cacheDir)
        if ($cleanupPid -le 0) {
            throw 'Could not start ReplayKit cleanup.'
        }
        Write-Log "ReplayKit cleanup started detached as PID $cleanupPid."
        return @{ ok = $true; processId = [int]$cleanupPid; message = 'Uninstall started. OBS will close.' }
    }

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $setupExe
    $psi.Arguments = (($argList | ForEach-Object { Quote-ReplayKitProcessArg ([string]$_) }) -join ' ')
    $psi.WorkingDirectory = $cacheDir
    $psi.UseShellExecute = $true
    $psi.Verb = 'runas'
    $p = [System.Diagnostics.Process]::Start($psi)
    if ($null -eq $p) {
        throw 'Could not start ReplayKit cleanup.'
    }
    Write-Log "ReplayKit cleanup started elevated as PID $($p.Id)."
    return @{ ok = $true; processId = [int]$p.Id; message = 'Uninstall started. OBS will close.' }
}

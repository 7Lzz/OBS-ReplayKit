param(
    [Parameter(Mandatory = $true)][string]$ObsPath,
    [Parameter(Mandatory = $true)][int]$ObsPid
)

# standalone obs relauncher. spawned detached by the helpers /settings restart path before the helper kills obs and exits. lives outside the helpers kill-on-close job (createprocess flag create_breakaway_from_job) so it survives helper death. waits for the targeted obs pid to exit, clears the unclean-exit sentinel that obs writes on launch, then starts a fresh obs with the same flags the helper uses elsewhere.

$ErrorActionPreference = 'SilentlyContinue'

function Wait-ObsExit([int]$obsPid, [int]$timeoutMs) {
    $proc = Get-Process -Id $obsPid -ErrorAction SilentlyContinue
    if (-not $proc) { return $true }
    try {
        if ($proc.WaitForExit($timeoutMs)) { return $true }
    } catch {}
    return $false
}

# helper already called stop-process -force, but the exit takes a moment. wait up to 15s; if obs still hasnt died we force it ourselves so the new instance doesnt collide on the singleton.
if (-not (Wait-ObsExit -obsPid $ObsPid -timeoutMs 15000)) {
    try { Stop-Process -Id $ObsPid -Force -ErrorAction Stop } catch {}
    [void](Wait-ObsExit -obsPid $ObsPid -timeoutMs 5000)
}

# obs writes a run_<uuid> file under %appdata%\obs-studio\.sentinel on launch and deletes it on graceful exit. stop-process -force never gets to delete it, and on the next launch obs sees the leftover file and shows its safe-mode prompt. wipe the file before relaunching.
$sentinelDir = Join-Path $env:APPDATA 'obs-studio\.sentinel'
if (Test-Path -LiteralPath $sentinelDir) {
    Get-ChildItem -LiteralPath $sentinelDir -Filter 'run_*' -Force -ErrorAction SilentlyContinue |
        ForEach-Object {
            try { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction Stop } catch {}
        }
}

# overlay-style change: the helper wrote the new scene collection to <name>.json.replaykit-pending because a restart-triggered graceful close makes obs save its old in-memory scene over the real file. obs is confirmed gone now, so move the staged copy into place before the fresh obs reads it.
$scenesDir = Join-Path $env:APPDATA 'obs-studio\basic\scenes'
if (Test-Path -LiteralPath $scenesDir) {
    Get-ChildItem -LiteralPath $scenesDir -Filter '*.json.replaykit-pending' -Force -ErrorAction SilentlyContinue |
        ForEach-Object {
            $target = $_.FullName.Substring(0, $_.FullName.Length - '.replaykit-pending'.Length)
            try { Move-Item -LiteralPath $_.FullName -Destination $target -Force -ErrorAction Stop } catch {}
        }
}

# theme change: obs rewrites user.ini from memory on its graceful exit, wiping the [Appearance] Theme= the helper set. the helper staged the wanted id in .replaykit-theme-pending; splice it into the fresh user.ini now.
$themeMarker = Join-Path $env:APPDATA 'obs-studio\.replaykit-theme-pending'
if (Test-Path -LiteralPath $themeMarker) {
    $want = ''
    try { $want = ([System.IO.File]::ReadAllText($themeMarker)).Trim() } catch {}
    $iniPath = Join-Path $env:APPDATA 'obs-studio\user.ini'
    try {
        $lines = [System.Collections.Generic.List[string]]::new()
        if (Test-Path -LiteralPath $iniPath) { [System.IO.File]::ReadAllLines($iniPath) | ForEach-Object { $lines.Add($_) } }
        $appIdx = -1; $keyIdx = -1
        for ($i = 0; $i -lt $lines.Count; $i++) {
            $s = $lines[$i].Trim()
            if ($s -match '^\[.*\]$') { if ($appIdx -ge 0) { break }; if ($s -ieq '[Appearance]') { $appIdx = $i }; continue }
            if ($appIdx -ge 0 -and $keyIdx -lt 0 -and $s -imatch '^Theme=') { $keyIdx = $i }
        }
        if ([string]::IsNullOrEmpty($want)) {
            if ($keyIdx -ge 0) { $lines.RemoveAt($keyIdx) }
        } elseif ($keyIdx -ge 0) {
            $lines[$keyIdx] = "Theme=$want"
        } elseif ($appIdx -ge 0) {
            $lines.Insert($appIdx + 1, "Theme=$want")
        } else {
            $lines.Add('[Appearance]'); $lines.Add("Theme=$want")
        }
        [System.IO.File]::WriteAllLines($iniPath, $lines)
    } catch {}
    Remove-Item -LiteralPath $themeMarker -Force -ErrorAction SilentlyContinue
}

if (-not (Test-Path -LiteralPath $ObsPath)) { exit 1 }

$workDir = [System.IO.Path]::GetDirectoryName($ObsPath)
# $args is an automatic variable in powershell (holds the scripts unbound argv). use a different name so we dont shadow it.
$obsArgs = '--background-color=ff272a33 --default-background-color=ff272a33 --disable-direct-composition-video-overlays'

# useshellexecute=true so the relaunch goes through the shell -- inherits our admin token (we were spawned by the admin helper) without prompting for uac, and isnt parented to this powershell process (whose console we want gone as soon as obs is back up).
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName        = $ObsPath
$psi.Arguments       = $obsArgs
$psi.WorkingDirectory = $workDir
$psi.UseShellExecute  = $true
try {
    [void][System.Diagnostics.Process]::Start($psi)
} catch {
    exit 2
}
exit 0

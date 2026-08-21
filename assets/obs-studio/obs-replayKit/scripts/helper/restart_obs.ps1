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

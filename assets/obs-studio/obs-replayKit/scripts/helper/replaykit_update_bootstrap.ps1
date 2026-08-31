# standalone update-prompt launcher, spawned by helper_bootstrap.lua right after the helper so the check works even when obs is minimized to the tray and the cef dock is suspended (what was killing the dock-side window.open at startup) -- waits for the helper to bind its port, calls /update/startup-check, and if an update is pending opens update_prompt.html in a borderless msedge --app window (winforms messagebox as fallback); every step is appended to %temp%\replaykit_update_debug.log so the user can share it.

param(
    [int]$Port = 8767,
    [int]$WaitSeconds = 3
)

$ErrorActionPreference = 'SilentlyContinue'

$script:DebugLogPath = Join-Path $env:TEMP 'ReplayKit\logs\replaykit_update_debug.log'
function Write-DebugLog([string]$msg) {
    try {
        $dir = Split-Path -Parent $script:DebugLogPath
        if (-not (Test-Path -LiteralPath $dir)) { [void](New-Item -ItemType Directory -Path $dir -Force -ErrorAction SilentlyContinue) }
    } catch {}
    $line = '[{0}] PID={1} {2}' -f (Get-Date -Format 'o'), $PID, $msg
    try { Add-Content -LiteralPath $script:DebugLogPath -Value $line -ErrorAction SilentlyContinue } catch {}
}

Write-DebugLog "bootstrap starting (port=$Port wait=${WaitSeconds}s)"

Start-Sleep -Seconds $WaitSeconds

# the helper accepts only same-origin requests on the trusted routes, so pretend to be the dock.
$origin = "http://127.0.0.1:$Port"
$headers = @{ Origin = $origin }

# step 1: ask the helper if a prompt is needed -- /update/startup-check sets the once-per-launch flag on the helper so the dock wont also try to open its own popup afterwards; retry a few times in case the helper takes longer than $WaitSeconds to bind the port on slow boxes.
$resp = $null
for ($attempt = 1; $attempt -le 6; $attempt++) {
    try {
        $resp = Invoke-RestMethod -Uri "$origin/update/startup-check" -TimeoutSec 30 -Headers $headers
        break
    } catch {
        Write-DebugLog "startup-check attempt $attempt failed: $($_.Exception.Message)"
        Start-Sleep -Seconds 3
    }
}
if (-not $resp) {
    Write-DebugLog "startup-check never succeeded after retries; giving up"
    return
}

try { Write-DebugLog ("startup-check response: " + (ConvertTo-Json -InputObject $resp -Compress -Depth 5)) } catch {}

if (-not $resp.ok) {
    Write-DebugLog "startup-check returned ok=false; bailing"
    return
}
if (-not $resp.prompt) {
    Write-DebugLog "no prompt needed (admin=$($resp.admin) updateAvailable=$($resp.updateAvailable) alreadyChecked=$($resp.alreadyChecked))"
    return
}

$latest = [string]$resp.latestVersion
$installed = [string]$resp.installedVersion
Write-DebugLog "update available: installed=$installed latest=$latest"

# step 2: prefer a chromium browser in --app mode -- it renders the existing update_prompt.html, already styled to match obs, and the page handles /update/apply + /update/later itself; tries msedge, then chrome, then brave, then vivaldi, first one found wins.
$browserCandidates = @(
    @{ name = 'msedge'; path = (Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe') },
    @{ name = 'msedge'; path = (Join-Path $env:ProgramFiles 'Microsoft\Edge\Application\msedge.exe') },
    @{ name = 'chrome'; path = (Join-Path $env:ProgramFiles 'Google\Chrome\Application\chrome.exe') },
    @{ name = 'chrome'; path = (Join-Path ${env:ProgramFiles(x86)} 'Google\Chrome\Application\chrome.exe') },
    @{ name = 'chrome'; path = (Join-Path $env:LOCALAPPDATA 'Google\Chrome\Application\chrome.exe') },
    @{ name = 'brave';  path = (Join-Path $env:ProgramFiles 'BraveSoftware\Brave-Browser\Application\brave.exe') },
    @{ name = 'brave';  path = (Join-Path ${env:ProgramFiles(x86)} 'BraveSoftware\Brave-Browser\Application\brave.exe') },
    @{ name = 'brave';  path = (Join-Path $env:LOCALAPPDATA 'BraveSoftware\Brave-Browser\Application\brave.exe') },
    @{ name = 'vivaldi';path = (Join-Path $env:LOCALAPPDATA 'Vivaldi\Application\vivaldi.exe') }
)
$browserName = $null
$browserPath = $null
foreach ($c in $browserCandidates) {
    if ($c.path -and (Test-Path -LiteralPath $c.path)) {
        $browserName = $c.name
        $browserPath = $c.path
        break
    }
}

if ($browserPath) {
    Write-DebugLog "browser found: $browserName at $browserPath"
    $versionParam = if ($latest) { '?version=' + [System.Uri]::EscapeDataString($latest) } else { '' }
    $url = "$origin/update-prompt$versionParam"

    # separate profile so we never read or touch the users real browser data. kept OUTSIDE ReplayKit\update -- that dir is what /update/apply wipes before downloading, and the browser holding this profile open (CrashpadMetrics-active.pma, lockfile) would make that recursive delete fail.
    $profileDir = Join-Path $env:TEMP ("ReplayKit\update-prompt-profile-$browserName")
    try { [void](New-Item -ItemType Directory -Path $profileDir -Force -ErrorAction SilentlyContinue) } catch {}

    # center on the primary monitor -- winforms is the cheapest way to get screen bounds from ps; if the load fails (Server Core etc.) we fall back to a sensible fixed offset rather than abort.
    $winWidth = 700
    $winHeight = 580
    $posX = 200
    $posY = 200
    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
        $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $posX = [int]($screen.X + (($screen.Width - $winWidth) / 2))
        $posY = [int]($screen.Y + (($screen.Height - $winHeight) / 2))
    } catch {
        Write-DebugLog "could not load winforms for screen bounds: $($_.Exception.Message); using fallback position"
    }

    $browserArgs = @(
        '--app=' + $url,
        ('--window-size={0},{1}' -f $winWidth, $winHeight),
        ('--window-position={0},{1}' -f $posX, $posY),
        '--no-first-run',
        '--no-default-browser-check',
        '--user-data-dir=' + $profileDir
    )
    try {
        $proc = Start-Process -FilePath $browserPath -ArgumentList $browserArgs -PassThru
        Write-DebugLog "$browserName --app spawned (PID=$($proc.Id)) url=$url pos=$posX,$posY size=$winWidth,$winHeight"
        return
    } catch {
        Write-DebugLog "$browserName --app failed: $($_.Exception.Message); falling back to winforms"
    }
} else {
    Write-DebugLog "no chromium browser found in standard locations; using winforms fallback"
}

# step 3: winforms messagebox fallback -- ugly compared to the html popup but always available on windows, so the user never silently misses the update.
try {
    Add-Type -AssemblyName System.Windows.Forms
    $message = "ReplayKit $latest is available.`n`nInstalled: $installed`nLatest: $latest`n`nOBS will restart after the download is verified.`n`nDownload and install now?"
    $result = [System.Windows.Forms.MessageBox]::Show(
        $message,
        'ReplayKit Update',
        [System.Windows.Forms.MessageBoxButtons]::YesNo,
        [System.Windows.Forms.MessageBoxIcon]::Information
    )
    if ($result -eq [System.Windows.Forms.DialogResult]::Yes) {
        Write-DebugLog "user clicked yes -> /update/apply"
        try {
            $applyResp = Invoke-RestMethod -Uri "$origin/update/apply" -Method Post -TimeoutSec 120 -Headers $headers
            Write-DebugLog ("apply response: " + (ConvertTo-Json -InputObject $applyResp -Compress -Depth 4))
        } catch {
            Write-DebugLog "/update/apply threw: $($_.Exception.Message)"
        }
    } else {
        Write-DebugLog "user clicked no -> /update/later"
        try {
            $bodyJson = ConvertTo-Json @{ version = $latest } -Compress
            $laterResp = Invoke-RestMethod -Uri "$origin/update/later" -Method Post -Body $bodyJson -ContentType 'application/json' -TimeoutSec 10 -Headers $headers
            Write-DebugLog ("later response: " + (ConvertTo-Json -InputObject $laterResp -Compress -Depth 4))
        } catch {
            Write-DebugLog "/update/later threw: $($_.Exception.Message)"
        }
    }
} catch {
    Write-DebugLog "winforms fallback failed: $($_.Exception.Message)"
}

param(
    [Parameter(Mandatory = $true)][string]$Shortcode,
    [Parameter(Mandatory = $true)][string]$ClipName,
    [Parameter(Mandatory = $true)][string]$DbPath,
    [Parameter(Mandatory = $true)][string]$Api,
    [string]$LogPath = '',
    [string]$CookieJar = ''
)

# polls streamables /api/v1/videos/<shortcode> until status reaches 2 (ready) or 30 minutes pass, writing transcode_status/transcode_percent/ready into clips_db.json as they change. spawned detached (SpawnDetached, create_breakaway_from_job) by 50_upload_state.ps1 specifically so this survives an obs/helper restart mid-poll -- a plain child process died with the helper the instant OBS closed, which is what left clips permanently stuck showing "processing" even after streamable had long since finished, since nothing ever resumed a killed poll.

function L([string]$m) {
    if (-not $LogPath) { return }
    try {
        $line = "[{0}] area=transcode shortcode={1} {2}" -f (Get-Date -Format 'o'), $Shortcode, $m
        $dir = Split-Path -Parent $LogPath
        if ($dir -and -not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }
        Add-Content -LiteralPath $LogPath -Value $line -ErrorAction SilentlyContinue
    } catch {}
}

L "start clip='$ClipName'"

$deadline = (Get-Date).AddMinutes(30)
$utf8 = New-Object System.Text.UTF8Encoding $false
$lastWritten = ''
# self-contained worker, not dot-sourced into the module system, so $script:SCRATCH_DIR (00_state.ps1) isnt available here -- same %temp%\ReplayKit\scratch path every other part of the app uses, just built inline.
$scratchDir = Join-Path $env:TEMP 'ReplayKit\scratch'
if (-not (Test-Path -LiteralPath $scratchDir)) {
    try { New-Item -ItemType Directory -Path $scratchDir -Force | Out-Null } catch {}
}

# tmp lands in scratchDir, not next to $DbPath, so a crash between the write and the move never leaves a stray .tmp sitting beside clips_db.json.
function Save-ClipsDbAtomic($db) {
    $tmp = Join-Path $scratchDir ([System.IO.Path]::GetFileName($DbPath) + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    [System.IO.File]::WriteAllText($tmp, (ConvertTo-Json $db -Depth 4), $utf8)
    Move-Item -LiteralPath $tmp -Destination $DbPath -Force
}

while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    $respPath = Join-Path $scratchDir ('strmbl_st_' + [Guid]::NewGuid().ToString('N') + '.txt')
    $status = $null; $percent = 0
    try {
        $url = "$Api/api/v1/videos/$Shortcode"
        $cargs = @('-s','-S','--max-time','8',
                   '-H','Origin: https://streamable.com',
                   '-H','Referer: https://streamable.com/',
                   '-H','Accept: application/json',
                   '-A','Mozilla/5.0 (Windows NT 10.0; Win64; x64)',
                   '-o',$respPath,'-w','%{http_code}')
        if ($CookieJar -and (Test-Path -LiteralPath $CookieJar)) {
            $cargs += @('-b', $CookieJar)
        }
        $cargs += $url
        $rawCode = & curl.exe @cargs 2>&1
        $code = 0
        [void][int]::TryParse(([string]$rawCode).Trim(), [ref]$code)
        if ($code -ge 200 -and $code -lt 300 -and (Test-Path -LiteralPath $respPath)) {
            $body = [System.IO.File]::ReadAllText($respPath)
            try {
                $obj = $body | ConvertFrom-Json
                if ($obj.status -ne $null) { $status = [int]$obj.status }
                if ($obj.percentage_complete -ne $null) {
                    $percent = [int]$obj.percentage_complete
                }
            } catch {}
        } else {
            L "HTTP $code"
        }
    } catch {
        L "exception: $($_.Exception.Message)"
    } finally {
        if (Test-Path -LiteralPath $respPath) {
            try { Remove-Item -LiteralPath $respPath -Force -ErrorAction SilentlyContinue } catch {}
        }
    }

    if ($status -eq $null) { continue }
    # skip the db read/write when nothing observable changed.
    $stateKey = "$status|$percent"
    if ($stateKey -eq $lastWritten) {
        if ($status -ge 2) { break }
        continue
    }
    $lastWritten = $stateKey

    try {
        if (-not (Test-Path -LiteralPath $DbPath)) { continue }
        $raw = [System.IO.File]::ReadAllText($DbPath)
        $parsed = $raw | ConvertFrom-Json
        $db = @{}
        $parsed.PSObject.Properties | ForEach-Object { $db[$_.Name] = $_.Value }
        # user may have deleted the clip while we were polling -- dont silently resurrect its entry.
        if (-not $db.ContainsKey($ClipName)) { L "entry gone"; break }
        $entry = $db[$ClipName]
        $newEntry = @{}
        $entry.PSObject.Properties | ForEach-Object { $newEntry[$_.Name] = $_.Value }
        $newEntry.transcode_status  = $status
        $newEntry.transcode_percent = $percent
        # status: 0/1 = queued/processing, 2 = ready, 3 = failed.
        $newEntry.ready  = ($status -eq 2)
        $newEntry.failed = ($status -eq 3)
        $db[$ClipName] = $newEntry
        # this poller is a genuinely seperate detached powershell.exe process so it cant take the helpers in-process ClipsMetaLock -- atomic temp+rename at least keeps a concurrent reader from ever seeing a torn file, even without full mutual exclusion against the helpers other clips_db.json writers.
        Save-ClipsDbAtomic $db
        L "wrote status=$status percent=$percent ready=$($newEntry.ready)"
    } catch {
        L "db update failed: $($_.Exception.Message)"
    }

    if ($status -ge 2) { break }
}

if ((Get-Date) -ge $deadline) {
    try {
        if (Test-Path -LiteralPath $DbPath) {
            $raw = [System.IO.File]::ReadAllText($DbPath)
            $parsed = $raw | ConvertFrom-Json
            $db = @{}
            $parsed.PSObject.Properties | ForEach-Object { $db[$_.Name] = $_.Value }
            if ($db.ContainsKey($ClipName)) {
                $entry = $db[$ClipName]
                $newEntry = @{}
                $entry.PSObject.Properties | ForEach-Object { $newEntry[$_.Name] = $_.Value }
                if ([int]$newEntry.transcode_status -lt 2) {
                    $newEntry.transcode_status = 4
                    $newEntry.ready = $false
                    $newEntry.failed = $false
                    $newEntry.transcode_error = 'Streamable status check timed out after 30 minutes.'
                    $db[$ClipName] = $newEntry
                    Save-ClipsDbAtomic $db
                    L 'marked status check timed out'
                }
            }
        }
    } catch {
        L "timeout update failed: $($_.Exception.Message)"
    }
}
L "exit"

function New-RequestId {
    return [Guid]::NewGuid().ToString('N')
}

function Get-UploadStatusPath([string]$requestId) {
    return Join-Path $script:SCRATCH_DIR ("streamable_upload_status_$requestId.json")
}

function New-UploadJobRecord([string]$requestId) {
    return @{
        state     = 'idle'
        active    = $false
        clipName  = ''
        startedAt = 0
        updatedAt = 0
        url       = ''
        error     = ''
        message   = ''
        phase     = ''
        percent   = 0
        requestId = $requestId
        processId = 0
        statusPath = ''
        tempPath  = ''
        kind      = 'upload'
        cancelRequested = $false
    }
}

function Copy-UploadJobForJson($job) {
    if (-not $job) { return @{} }
    return [ordered]@{
        state     = [string]$job.state
        active    = [bool]$job.active
        clipName  = [string]$job.clipName
        startedAt = [int64]$job.startedAt
        updatedAt = [int64]$job.updatedAt
        url       = [string]$job.url
        error     = [string]$job.error
        message   = [string]$job.message
        phase     = [string]$job.phase
        percent   = [int]$job.percent
        requestId = [string]$job.requestId
        kind      = [string]$job.kind
    }
}

function Select-CurrentUploadJobLocked {
    $chosen = $null
    foreach ($job in $script:State.Jobs.Values) {
        if (-not $job.active) { continue }
        if (-not $chosen -or ([int64]$job.updatedAt -gt [int64]$chosen.updatedAt)) {
            $chosen = $job
        }
    }
    if ($chosen) { return $chosen }
    return $script:State.Upload
}

function Remove-OldUploadJobsLocked([int64]$nowMs) {
    $cutoff = $nowMs - (10 * 60 * 1000)
    $remove = @()
    foreach ($key in @($script:State.Jobs.Keys)) {
        $job = $script:State.Jobs[$key]
        if ($job.active) { continue }
        $updated = [int64]$job.updatedAt
        if ($updated -gt 0 -and $updated -lt $cutoff) {
            $remove += $key
        }
    }
    foreach ($key in $remove) { $script:State.Jobs.Remove($key) }
}

# cached cpu percent sample. cim/wmis percentprocessortime is an instantaneous reading -- we cache it for a few seconds so that a burst of "can we start one more" checks coming from the bulk compress queue doesnt fire wmi queries back to back. returns -1 if sampling fails so callers know to fall back to the steady-state limit only (never the burst headroom). lives on $script:State (CpuSamplePercent/CpuSampleAt) -- only ever called from Get-UploadJobStartDecision below, which already holds UploadLock for its whole body, so no seperate lock here.
function Get-RecentCpuPercent {
    $now = [DateTime]::UtcNow
    if ($script:State.CpuSamplePercent -ge 0 -and ($now - $script:State.CpuSampleAt).TotalSeconds -lt 4) {
        return $script:State.CpuSamplePercent
    }
    try {
        $row = Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor `
            -Filter "Name='_Total'" -ErrorAction Stop
        if ($row -and $row.PercentProcessorTime -ne $null) {
            $script:State.CpuSamplePercent = [int]$row.PercentProcessorTime
            $script:State.CpuSampleAt      = $now
            return $script:State.CpuSamplePercent
        }
    } catch {
        # win32_perfformatteddata_* can flake on freshly-booted systems before the perflib counters are warm. we just return -1 and the caller stays at the steady-state cap.
    }
    return -1
}

function Get-UploadJobStartDecision([string]$clipName) {
    [System.Threading.Monitor]::Enter($script:State.UploadLock)
    try {
        $activeCount = 0
        foreach ($job in $script:State.Jobs.Values) {
            if (-not $job.active) { continue }
            if ([string]::Equals([string]$job.clipName, $clipName, [StringComparison]::OrdinalIgnoreCase)) {
                return @{ ok = $false; busy = $true; message = 'That clip already has an operation running' }
            }
            $activeCount++
        }
        # below the steady-state cap -- always allowed.
        if ($activeCount -lt $script:MAX_CONCURRENT_VIDEO_JOBS) {
            return @{ ok = $true }
        }
        # between steady-state and burst -- only allowed if the host isnt already pegged. lets "compress all" auto-scale on underutilised machines without thrashing busy ones.
        if ($activeCount -lt $script:MAX_BURST_CONCURRENT_VIDEO_JOBS) {
            $cpu = Get-RecentCpuPercent
            if ($cpu -ge 0 -and $cpu -lt $script:CPU_BURST_THRESHOLD_PCT) {
                return @{ ok = $true; burst = $true; cpu = $cpu }
            }
        }
        return @{
            ok = $false
            busy = $true
            message = "Already running $activeCount video operations"
        }
    } finally {
        [System.Threading.Monitor]::Exit($script:State.UploadLock)
    }
}

function Test-ClipHasActiveUploadJob([string]$clipName) {
    if ([string]::IsNullOrWhiteSpace($clipName)) { return $false }
    [System.Threading.Monitor]::Enter($script:State.UploadLock)
    try {
        foreach ($job in $script:State.Jobs.Values) {
            if ($job.active -and [string]::Equals([string]$job.clipName, $clipName, [StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
        }
        return $false
    } finally {
        [System.Threading.Monitor]::Exit($script:State.UploadLock)
    }
}

function Set-UploadState([hashtable]$next) {
    [System.Threading.Monitor]::Enter($script:State.UploadLock)
    try {
        $nowMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        $requestId = ''
        if ($next.ContainsKey('requestId')) { $requestId = [string]$next.requestId }
        if ([string]::IsNullOrWhiteSpace($requestId)) {
            $requestId = [string]$script:State.Upload.requestId
        }

        $job = $null
        if (-not [string]::IsNullOrWhiteSpace($requestId)) {
            if ($script:State.Jobs.ContainsKey($requestId)) {
                $job = $script:State.Jobs[$requestId]
            } else {
                $job = New-UploadJobRecord $requestId
            }
            foreach ($k in $next.Keys) { $job[$k] = $next[$k] }
            $job.requestId = $requestId
            if (-not $job.startedAt) { $job.startedAt = $nowMs }
            $job.updatedAt = $nowMs
            $script:State.Jobs[$requestId] = $job
        }

        $u = if ($job) { $job } else { $script:State.Upload }
        if (-not $job) {
            foreach ($k in $next.Keys) { $u[$k] = $next[$k] }
            $u.updatedAt = $nowMs
        }
        Remove-OldUploadJobsLocked $nowMs
        $script:State.Upload = Select-CurrentUploadJobLocked
        if (-not $script:State.Upload -or -not $script:State.Upload.active) {
            $script:State.Upload = $u
        }
    } finally {
        [System.Threading.Monitor]::Exit($script:State.UploadLock)
    }
}

function Update-UploadStateFromStatusFile {
    $jobs = @()
    [System.Threading.Monitor]::Enter($script:State.UploadLock)
    try {
        foreach ($job in $script:State.Jobs.Values) {
            if ($job.active -and -not [string]::IsNullOrWhiteSpace([string]$job.statusPath)) {
                $jobs += @{
                    requestId  = [string]$job.requestId
                    statusPath = [string]$job.statusPath
                }
            }
        }
    } finally {
        [System.Threading.Monitor]::Exit($script:State.UploadLock)
    }

    foreach ($u in $jobs) {
        $path = [string]$u.statusPath
        if ([string]::IsNullOrWhiteSpace($path) -or -not (Test-Path -LiteralPath $path)) { continue }
        try {
            $raw = [System.IO.File]::ReadAllText($path)
            if ([string]::IsNullOrWhiteSpace($raw)) { continue }
            $s = ConvertFrom-Json $raw
            if ($u.requestId -and $s.requestId -and ([string]$s.requestId -ne [string]$u.requestId)) { continue }
            $next = @{ requestId = [string]$u.requestId }
            if ($s.state)   { $next.state = [string]$s.state }
            if ($s.phase)   { $next.phase = [string]$s.phase }
            if ($s.state -and ([string]$s.state -eq 'done' -or [string]$s.state -eq 'error')) {
                $next.active = $false
                $next.processId = 0
            }
            if ($s.percent -ne $null) {
                $pct = [int][Math]::Max(0, [Math]::Min(100, [int]$s.percent))
                $next.percent = $pct
            }
            if ($s.message) { $next.message = [string]$s.message }
            if ($s.url -and ([string]$s.url -match '^https://streamable\.com/[A-Za-z0-9_-]+$')) {
                $next.url = [string]$s.url
            }
            if ($s.state -and ([string]$s.state -eq 'error') -and $s.message) {
                $next.error = [string]$s.message
            }
            if ($next.Count -gt 1) { Set-UploadState $next }
        } catch {}
    }
}

function Get-UploadStatusSnapshot {
    Update-UploadStateFromStatusFile
    [System.Threading.Monitor]::Enter($script:State.UploadLock)
    try {
        $jobs = @()
        $activeCount = 0
        foreach ($job in $script:State.Jobs.Values) {
            if ($job.active) { $activeCount++ }
            $jobs += (Copy-UploadJobForJson $job)
        }
        $current = Copy-UploadJobForJson $script:State.Upload
        $current['jobs'] = @($jobs | Sort-Object -Property updatedAt -Descending)
        $current['activeJobs'] = $activeCount
        $current['maxConcurrentJobs'] = $script:MAX_CONCURRENT_VIDEO_JOBS
        return $current
    } finally {
        [System.Threading.Monitor]::Exit($script:State.UploadLock)
    }
}

# nb: dont name the parameter $pid -- powershells $pid is a read-only automatic variable, and a [int]$pid parameter throws variablenotwritable during binding before any code in the body runs. the function then appears to "silently do nothing" becuase the helpers try/catch swallows the binding error.
function Stop-ProcessTree([int]$processId) {
    if ($processId -le 0) { return }
    try {
        Get-CimInstance Win32_Process -Filter "ParentProcessId=$processId" -ErrorAction SilentlyContinue | ForEach-Object {
            Stop-ProcessTree ([int]$_.ProcessId)
        }
    } catch {}
    try { Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue } catch {}
}

function Cancel-ActiveUpload([string]$clipName = '', [string]$requestId = '') {
    $u = $null
    [System.Threading.Monitor]::Enter($script:State.UploadLock)
    try {
        if (-not [string]::IsNullOrWhiteSpace($requestId) -and $script:State.Jobs.ContainsKey($requestId)) {
            $u = $script:State.Jobs[$requestId]
        } elseif (-not [string]::IsNullOrWhiteSpace($clipName)) {
            foreach ($job in $script:State.Jobs.Values) {
                if ($job.active -and [string]::Equals([string]$job.clipName, $clipName, [StringComparison]::OrdinalIgnoreCase)) {
                    $u = $job
                    break
                }
            }
        } elseif ($script:State.Upload.active) {
            $u = $script:State.Upload
        }
    } finally {
        [System.Threading.Monitor]::Exit($script:State.UploadLock)
    }

    if (-not $u -or -not $u.active) {
        return @{ ok = $false; message = 'No upload is running' }
    }

    $requestId = [string]$u.requestId
    $pidToStop = [int]$u.processId
    $statusPath = [string]$u.statusPath
    $tempPath = [string]$u.tempPath
    # compress workers run in a seperate runspace and cant reach the main helpers $script:state, so they record their ffmpeg pid in a sidecar file ($statuspath.pid) instead of going thru set-uploadstate. if we got handed a job with processid=0 (the defualt the route handler writes before the worker actualy spawns its child), check the sidecar before giving up. without this, clicking stop on a compress all would mark the job cancelled without ever killing the encoder.
    if ($pidToStop -le 0 -and $statusPath) {
        $pidPath = "$statusPath.pid"
        if (Test-Path -LiteralPath $pidPath) {
            try {
                $raw = [System.IO.File]::ReadAllText($pidPath).Trim()
                $parsed = 0
                if ([int]::TryParse($raw, [ref]$parsed) -and $parsed -gt 0) {
                    $pidToStop = $parsed
                }
            } catch {}
        }
    }
    # mark cancelled before the kill so the watcher (which is blocked on proc.waitforexit) sees the flag as soon as it wakes up and skips its generic "upload failed (exit=n)" overwrite.
    Set-UploadState @{
        requestId = $requestId
        state = 'error'; active = $false; error = 'Cancelled'; phase = 'cancelled'
        percent = 0; processId = 0; statusPath = ''; tempPath = ''
        cancelRequested = $true
    }
    if ($pidToStop -gt 0) { Stop-ProcessTree $pidToStop }
    if ($tempPath -and (Test-Path -LiteralPath $tempPath)) {
        try { Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue } catch {}
    }
    if ($statusPath -and (Test-Path -LiteralPath $statusPath)) {
        try { Remove-Item -LiteralPath $statusPath -Force -ErrorAction SilentlyContinue } catch {}
    }
    return @{ ok = $true; message = 'Cancelled' }
}

function Clear-StaleCompressedTempFiles {
    $root = [System.IO.Path]::GetFullPath($script:COMPRESS_TMP_DIR)
    if ($root.StartsWith([System.IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $root)) {
        Get-ChildItem -LiteralPath $root -File -ErrorAction SilentlyContinue | ForEach-Object {
            try { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue } catch {}
        }
    }
    # sweeps the current scratch dir plus bare %temp% root -- the latter only matters for leftovers from before everything moved under %temp%\ReplayKit, and stays harmless once those are gone since a fresh install never drops matching names there again.
    foreach ($sweepRoot in @($script:SCRATCH_DIR, $env:TEMP)) {
        if (-not (Test-Path -LiteralPath $sweepRoot)) { continue }
        Get-ChildItem -LiteralPath $sweepRoot -File -ErrorAction SilentlyContinue | Where-Object {
            $_.Name -like 'streamable_upload_status_*.json' -or
            $_.Name -like 'replaykit_compress_*'           -or
            $_.Name -like 'replaykit_trim_*'
        } | ForEach-Object {
            try { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue } catch {}
        }
    }
    # every atomic-write .tmp sidecar (clips_db.json, clips_index.json, replaykit_settings.json, obss own ini/json, the hevc remux, ...) now lands in scratch, not next to its real file -- catch anything a crash left behind mid-write with one broad sweep, scoped to just this ReplayKit-owned dir so it never touches an unrelated apps temp files.
    if (Test-Path -LiteralPath $script:SCRATCH_DIR) {
        Get-ChildItem -LiteralPath $script:SCRATCH_DIR -File -Filter '*.tmp' -ErrorAction SilentlyContinue | ForEach-Object {
            try { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue } catch {}
        }
    }
    # sweep the clip folder for crashed-worker leftovers (sidecars from cross-volume finalize + older naming patterns). test-path before get-childitem -- get-clipdir can resolve to a path that doesnt exist yet on a fresh install.
    try {
        $clipDir = Get-ClipDir
        if ($clipDir -and (Test-Path -LiteralPath $clipDir)) {
            Get-ChildItem -LiteralPath $clipDir -File -ErrorAction SilentlyContinue | Where-Object {
                $_.Name -like '_replaykit_*'      -or
                $_.Name -like '_compress_tmp_*'    -or
                $_.Name -like '_trim_tmp_*'
            } | ForEach-Object {
                try { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue } catch {}
            }
        }
    } catch {}
}

function Start-UploadResultWatcher($proc, [string]$clipName, [string]$statusPath, [string]$requestId) {
    # watch the upload from a runspace so we dont block the request handler. when the child exits, read its status json and resolve to done/error in the shared state.
    $watcher = [powershell]::Create()
    $watcher.RunspacePool = $script:WatcherPool
    # use `$null = ...` (not `[void] ... | out-null`) to discard the fluent chain. combining `[void]` with `| out-null` produces "argument type cant be system.void." becuase the [void] cast makes the pipeline input void, which out-nulls parameter binder refuses.
    $null = $watcher.AddScript({
        param($proc, $clipName, $statusPath, $requestId, $state)
        try { $proc.WaitForExit() } catch {}
        # cancel-activeupload has already written the final "cancelled" state and removed the status file. dont overwrite it with "upload failed (exit=n)" built from the killed processs exit code -- just clear the flag and clean up the status file if cancel didnt get to it (race-safe).
        $cancelled = $false
        [System.Threading.Monitor]::Enter($state.UploadLock)
        try {
            $job = if ($requestId -and $state.Jobs.ContainsKey($requestId)) { $state.Jobs[$requestId] } else { $state.Upload }
            if ($job.cancelRequested) {
                $job.cancelRequested = $false
                if ($requestId -and $state.Jobs.ContainsKey($requestId)) {
                    $state.Jobs[$requestId] = $job
                } else {
                    $state.Upload = $job
                }
                $cancelled = $true
            }
        } finally { [System.Threading.Monitor]::Exit($state.UploadLock) }
        if ($cancelled) {
            if ($statusPath) {
                try { Remove-Item -LiteralPath $statusPath -Force -ErrorAction SilentlyContinue } catch {}
            }
            return
        }

        function Set-SharedJobState([hashtable]$next) {
            $nowMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
            [System.Threading.Monitor]::Enter($state.UploadLock)
            try {
                $job = if ($requestId -and $state.Jobs.ContainsKey($requestId)) {
                    $state.Jobs[$requestId]
                } else {
                    @{ requestId = $requestId; clipName = $clipName }
                }
                foreach ($k in $next.Keys) { $job[$k] = $next[$k] }
                $job.requestId = $requestId
                $job.clipName = $clipName
                $job.updatedAt = $nowMs
                $state.Jobs[$requestId] = $job

                $chosen = $null
                foreach ($j in $state.Jobs.Values) {
                    if (-not $j.active) { continue }
                    if (-not $chosen -or ([int64]$j.updatedAt -gt [int64]$chosen.updatedAt)) {
                        $chosen = $j
                    }
                }
                $state.Upload = if ($chosen) { $chosen } else { $job }
            } finally {
                [System.Threading.Monitor]::Exit($state.UploadLock)
            }
        }

        $status = $null
        $result = ''
        try {
            if ($statusPath -and (Test-Path -LiteralPath $statusPath)) {
                $raw = [System.IO.File]::ReadAllText($statusPath)
                if (-not [string]::IsNullOrWhiteSpace($raw)) {
                    $status = ConvertFrom-Json $raw
                    if ($status.url) { $result = [string]$status.url }
                }
            }
        } catch {}
        $isUrl = $result -match '^https://streamable\.com/[A-Za-z0-9_-]+$'
        if ($isUrl) {
            # update db. inline becuase runspaces dont share function scope.
            $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
            $cfg = $state.Config
            $dbPath = Join-Path $cfg.scriptDir 'clips_db.json'
            $shortcode = ''
            [System.Threading.Monitor]::Enter($state.ClipsMetaLock)
            try {
                $db = @{}
                if (Test-Path -LiteralPath $dbPath) {
                    try {
                        $raw = [System.IO.File]::ReadAllText($dbPath)
                        $parsed = ConvertFrom-Json $raw
                        $parsed.PSObject.Properties | ForEach-Object { $db[$_.Name] = $_.Value }
                    } catch {}
                }
                # tag with the retention that applied at upload time. the /clips listing filters per-entry, so signing in later doesnt extend a stale anonymous links lifetime.
                $retentionDays = if ($state.Auth -and $state.Auth.retentionDays) {
                    [int]$state.Auth.retentionDays
                } else { 2 }
                # the transcode step we posted only triggers streamables encode; the video isnt watchable until streamable finishes processing it (visible as "were processing this video" on the site). mark ready=false here and let the background poller flip it once streamables api reports status=2.
                if ($result -match '^https://streamable\.com/([A-Za-z0-9_-]+)$') {
                    $shortcode = $matches[1]
                }
                $db[$clipName] = @{
                    url               = $result
                    uploaded_at       = $now
                    retention_days    = $retentionDays
                    shortcode         = $shortcode
                    ready             = $false
                    transcode_status  = 1 # 1 = processing per streamables api
                    transcode_percent = 0
                }
                $obj = @{}; foreach ($k in $db.Keys) { $obj[$k] = $db[$k] }
                try {
                    $utf8 = New-Object System.Text.UTF8Encoding $false
                    # $script:SCRATCH_DIR isnt reachable from this runspace (see the "inline becuase runspaces dont share function scope" note above) so its built inline -- must match 00_state.ps1s definition (%temp%\ReplayKit\scratch), and lands the tmp there instead of next to clips_db.json so a crash between the write and the move never leaves a stray .tmp sitting beside it.
                    $scratchDir = Join-Path $env:TEMP 'ReplayKit\scratch'
                    if (-not (Test-Path -LiteralPath $scratchDir)) {
                        [void](New-Item -ItemType Directory -Path $scratchDir -Force)
                    }
                    $tmpDb = Join-Path $scratchDir ([System.IO.Path]::GetFileName($dbPath) + '.' + [System.Guid]::NewGuid().ToString('N') + '.tmp')
                    [System.IO.File]::WriteAllText($tmpDb, (ConvertTo-Json $obj -Depth 4), $utf8)
                    Move-Item -LiteralPath $tmpDb -Destination $dbPath -Force
                } catch {}
                $state.ClipsCacheAt = [DateTime]::MinValue
                $state.ClipsCacheSig = ''
                $state.ClipsCacheVersion = ''
                $state.ClipsCacheJson = ''
            } finally { [System.Threading.Monitor]::Exit($state.ClipsMetaLock) }
            Set-SharedJobState @{
                state = 'done'; active = $false; url = $result; error = ''
                phase = 'done'; percent = 100; processId = 0
                statusPath = ''; tempPath = ''
            }
            if ($statusPath) { try { Remove-Item -LiteralPath $statusPath -Force -ErrorAction SilentlyContinue } catch {} }
            # windows toast notification: tell the user the upload finished and the link is on the clipboard. spawned as a seperate hidden powershell.exe so this watcher runspace doesnt have to load system.windows.forms or pump a windows message loop. title/body are passed via env vars so the url doesnt have to be string-escaped into an inline command.
            try {
                $toastScript = @'
Add-Type -AssemblyName System.Windows.Forms
$n = New-Object System.Windows.Forms.NotifyIcon
$n.Icon = [System.Drawing.SystemIcons]::Information
$n.Visible = $true
$n.ShowBalloonTip(5000, $env:S_TOAST_TITLE, $env:S_TOAST_BODY, [System.Windows.Forms.ToolTipIcon]::Info)
$deadline = [DateTime]::UtcNow.AddSeconds(6)
while ([DateTime]::UtcNow -lt $deadline) {
    [System.Windows.Forms.Application]::DoEvents()
    Start-Sleep -Milliseconds 100
}
$n.Visible = $false
$n.Dispose()
'@
                $toastBytes   = [System.Text.Encoding]::Unicode.GetBytes($toastScript)
                $toastEncoded = [System.Convert]::ToBase64String($toastBytes)
                $psi3 = New-Object System.Diagnostics.ProcessStartInfo
                $psi3.FileName        = 'powershell.exe'
                $psi3.Arguments       = "-NoProfile -WindowStyle Hidden -EncodedCommand $toastEncoded"
                $psi3.UseShellExecute = $false
                $psi3.CreateNoWindow  = $true
                $psi3.WindowStyle     = 'Hidden'
                $psi3.EnvironmentVariables['S_TOAST_TITLE'] = 'OBS clip uploaded'
                $psi3.EnvironmentVariables['S_TOAST_BODY']  = "Link copied to clipboard`n$result"
                [void][System.Diagnostics.Process]::Start($psi3)
            } catch {}

            # background transcode poller. streamables /transcode call only queues encoding; the video isnt watchable until the `status` field on /api/v1/videos/<shortcode> reaches 2. poll for up to 30 minutes and write an explicit terminal state if Streamable never responds, so a clip can never be labelled "processing" forever. spawned via SpawnDetached (create_breakaway_from_job) into transcode_poll_worker.ps1 rather than a plain Process.Start, becuase a plain child inherits the helpers kill-on-close job and dies the instant obs/the helper exits -- confirmed 2026-08-19 this was leaving clips permanently stuck showing "processing" even long after streamable had actually finished, since nothing ever resumed a poll that got killed mid-flight (restarting obs doesnt fix it either: the stale db entry just sits there since nothing re-polls it). Quote-ReplayKitProcessArg isnt reachable from this runspace (see the "inline becuase runspaces dont share function scope" note above), so args are hand-quoted with plain double-quote wrapping -- safe here since none of these values can contain a quote or a trailing backslash: clip names cant (windows forbids " and \ in filenames), and the path values are built from known-safe pieces with no trailing separator.
            if ($shortcode) {
                try {
                    $workerPath = Join-Path $cfg.scriptDir 'transcode_poll_worker.ps1'
                    if (Test-Path -LiteralPath $workerPath -PathType Leaf) {
                        $psExe = "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe"
                        if (-not (Test-Path -LiteralPath $psExe)) { $psExe = 'powershell.exe' }
                        $loggingOn = [bool]$state.Capabilities.logging
                        # $script:LOG_DIR isnt reachable from this runspace (see the "inline becuase runspaces dont share function scope" note above) so its built inline -- must match 00_state.ps1s definition (%temp%\ReplayKit\logs) or this log lands somewhere the "open log folder" button never looks.
                        $logPath = if ($loggingOn) {
                            Join-Path $env:TEMP 'ReplayKit\logs\transcode.log'
                        } else { '' }
                        $cookieJar = ''
                        if ($state.Auth -and $state.Auth.signedIn -and $env:LOCALAPPDATA) {
                            $candidate = Join-Path $env:LOCALAPPDATA 'OBS Streamable Helper\auth_cookies.txt'
                            if (Test-Path -LiteralPath $candidate) { $cookieJar = $candidate }
                        }
                        $cmdLine = '"' + $psExe + '"' +
                            ' -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $workerPath + '"' +
                            ' -Shortcode "' + $shortcode + '"' +
                            ' -ClipName "' + $clipName + '"' +
                            ' -DbPath "' + $dbPath + '"' +
                            ' -Api "https://api-f.streamable.com"' +
                            ' -LogPath "' + $logPath + '"' +
                            ' -CookieJar "' + $cookieJar + '"'
                        [void][ReplayKitNative]::SpawnDetached($cmdLine, $cfg.scriptDir)
                    }
                } catch {}
            }
        } else {
            $exitCode = -1
            try { $exitCode = [int]$proc.ExitCode } catch {}
            $msg = if ($status -and $status.message) {
                [string]$status.message
            } elseif ($exitCode -ne 0) {
                "Upload failed (exit=$exitCode)"
            } else {
                'Upload finished without a Streamable result'
            }
            Set-SharedJobState @{
                state = 'error'; active = $false; error = $msg
                phase = 'error'; processId = 0
                statusPath = ''; tempPath = ''
            }
            if ($statusPath) { try { Remove-Item -LiteralPath $statusPath -Force -ErrorAction SilentlyContinue } catch {} }
        }
    }).AddArgument($proc).AddArgument($clipName).AddArgument($statusPath).AddArgument($requestId).AddArgument($script:State)

    # if the watcher setup fails (addscript / addargument / begininvoke all sit in this try), the upload may still run but its completion is never observed -- /status would stay "uploading" forever. surface the failure immediately instead.
    try {
        $null = $watcher.BeginInvoke()
    } catch {
        $msg = "Could not start upload watcher: $($_.Exception.Message)"
        Write-Log $msg
        Set-UploadState @{ requestId = $requestId; state = 'error'; active = $false; error = $msg }
        return @{ ok = $false; message = $msg }
    }
    return @{ ok = $true }
}

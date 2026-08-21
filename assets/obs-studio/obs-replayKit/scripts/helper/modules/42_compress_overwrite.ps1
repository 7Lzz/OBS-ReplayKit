# compress a clip locally with libx264 + atomically replace the original. no upload. runs async via a hidden powershell.exe worker so the helpers accept loop stays responsive, and reuses the upload state machine so the clip card shows the same "compressing x% - cancel" ui as the existing compress-then-upload flow.

function Start-CompressOverwriteFile($selected, [string]$mode = 'smaller') {
    Load-Config
    $requestId = New-RequestId
    $mode = if ($mode) { $mode.ToLowerInvariant() } else { 'smaller' }
    if ($mode -ne 'fast' -and $mode -ne 'smaller') { $mode = 'smaller' }

    if (-not $selected -or -not (Test-Path -LiteralPath $selected.full)) {
        return @{ ok = $false; message = 'Clip not found' }
    }

    $decision = Get-UploadJobStartDecision $selected.name
    if (-not $decision.ok) { return $decision }

    $caps = Get-HelperCapabilities
    $ffmpeg = [string]$caps.ffmpeg
    if ([string]::IsNullOrWhiteSpace($ffmpeg) -or -not (Test-Path -LiteralPath $ffmpeg)) {
        return @{ ok = $false; message = 'ffmpeg.exe not found in clip folder' }
    }
    $ffprobe = [string]$caps.ffprobe

    $metadata = Get-VideoMetadata $ffprobe $ffmpeg $selected.full
    $duration = [double]$metadata.duration
    if ($duration -lt 1) {
        return @{ ok = $false; message = 'Could not read video duration' }
    }

    $ext = [System.IO.Path]::GetExtension($selected.name)
    # encode into %temp% so the user never sees the in-flight file in the clip folder. the worker handles cross-volume safely with a sidecar copy + atomic rename on the source volume.
    $tempPath = Join-Path $script:SCRATCH_DIR ("replaykit_compress_" + $requestId + $ext)
    $statusPath = Get-UploadStatusPath $requestId
    $compressLogFile = if ($script:State.LogEnabled) { $script:COMPRESS_LOG_PATH } else { '' }
    # snapshot the input size now so the worker can record the original filesize in the compress marker (used for "compressed from x mb" ui surfaces). the atomic replace later in the worker invalidates this filehandle, so its important we capture before then.
    $preBytes = 0
    try { $preBytes = ([System.IO.FileInfo]::new($selected.full)).Length } catch {}
    $dbPath = Get-DbPath

    Set-UploadState @{
        state     = 'compressing'
        active    = $true
        clipName  = $selected.name
        startedAt = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        url       = ''
        error     = ''
        phase     = 'compressing'
        percent   = 1
        requestId = $requestId
        processId = 0
        statusPath = $statusPath
        tempPath  = $tempPath
        kind      = 'compress-overwrite'
        cancelRequested = $false
    }

    # two encode profiles. "fast" uses libx264 fast crf 23 -- encodes at multi-x realtime on modern cpus and produces files ~50-70% of source, h264 plays everywhere. "smaller" uses libx265 medium crf 25 -- encodes at sub-realtime but cuts file size ~half vs libx264 at indistinguishable quality (hvc1 tag for Chromium/CEF playback support). progress is captured by piping ffmpegs -progress to stdout (rather than to a file) and reading it line-by-line from the worker. -progress <file> works but windows file caching can delay reads relative to ffmpegs writes; the pipe path is bounded by ffmpegs fflush() and is the standard fix.
    $workerScript = @'
param(
    [string]$Ffmpeg,
    [string]$InputPath,
    [string]$TempPath,
    [string]$SourcePath,
    [double]$DurationSec,
    [string]$Mode = 'smaller',
    [string]$StatusPath,
    [string]$RequestId,
    [string]$CompressLogFile,
    [string]$DbPath,
    [int64]$PreBytes = 0,
    # json-serialised capabilities dict from get-helpercapabilities, carrying the per-encoder availability flags + the picked fastencoder / smallerencoder names + the cpu core count.
    [string]$CapsJson = '{}',
    # picked encoder ids -- redundant with capsjson but cheaper for the worker to read. the dispatcher resolves them once from caps so all parallel workers agree on the same encoder choice.
    [string]$FastEncoder = 'libx264',
    [string]$SmallerEncoder = 'libx264',
    # $script:State.ClipsMetaLock, handed over as a plain object reference so this self-contained worker script can serialise its clips_db.json write against the helpers other writers without needing the rest of $script:State.
    [object]$ClipsMetaLock = $null,
    # $script:State.LogLock -- compress all can run several of these workers at once, all logging to the same file; without this, concurrent Add-Content calls can hit a sharing violation (silently dropped today via -ErrorAction SilentlyContinue, but still a lost log line).
    [object]$LogLock = $null
)
$ErrorActionPreference = 'Stop'
$ci = [Globalization.CultureInfo]::InvariantCulture
# re-bind to the lower-case names the rest of the worker uses, so the bulk of the worker stays identical to its old env-var-driven form. worker used to run in a spawned powershell.exe child process with env vars carrying the parameters; it now runs as a runspace inside the helper (no more "windows powershell" clutter in task manager during compress all) and gets its parameters via param() binding.
$compressLogFile = $CompressLogFile
$statusPath = $StatusPath
$requestId = $RequestId
$ffmpeg = $Ffmpeg
$inputPath = $InputPath
$tempPath = $TempPath
$sourcePath = $SourcePath
$durationSec = $DurationSec
$mode = if ($Mode) { $Mode } else { 'smaller' }
$dbPath = $DbPath
$preBytes = $PreBytes
# caps parsing -- never throws even on malformed json; we just fall back to libx264 in that case (always-present last-resort encoder).
$caps = $null
try { if ($CapsJson) { $caps = $CapsJson | ConvertFrom-Json } } catch { $caps = $null }
$cpuCount = if ($caps -and $caps.cpuCount) {
    [int]$caps.cpuCount
} else {
    [Environment]::ProcessorCount
}
# half-cores per encode for sw codecs. two parallel libx265 jobs split the cpu cleanly with this -- one job grabbing every core context-switches itself silly when a second job lands.
$swPools = [Math]::Max(2, [int][Math]::Floor($cpuCount / 2))
$pickedEncoder = if ($mode -eq 'fast') {
    if ($FastEncoder) { $FastEncoder } else { 'libx264' }
} else {
    if ($SmallerEncoder) { $SmallerEncoder } else { 'libx264' }
}

# capture the source clips filesystem timestamps before the atomic replace. restoring them after the encode keeps the clip in its original sort position in the dock instead of jumping to the top of the list every time its compressed.
$origLastWrite = $null
$origCreation  = $null
try {
    $srcInfo = [System.IO.FileInfo]::new($sourcePath)
    if ($srcInfo.Exists) {
        $origLastWrite = $srcInfo.LastWriteTimeUtc
        $origCreation  = $srcInfo.CreationTimeUtc
    }
} catch {}

function L([string]$msg) {
    $line = '[{0}] area=compress_overwrite request={1} {2}' -f (Get-Date -Format 'HH:mm:ss'), $requestId, $msg
    if ($compressLogFile) {
        $dir = Split-Path -Parent $compressLogFile
        if ($dir -and -not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }
        if ($LogLock) { [System.Threading.Monitor]::Enter($LogLock) }
        try {
            Add-Content -LiteralPath $compressLogFile -Value $line -ErrorAction SilentlyContinue
        } finally {
            if ($LogLock) { [System.Threading.Monitor]::Exit($LogLock) }
        }
    }
}

function Write-Status([string]$state, [string]$phase, [int]$percent, [string]$message = '') {
    if (-not $statusPath) { return }
    $percent = [Math]::Max(0, [Math]::Min(100, $percent))
    $obj = [ordered]@{
        requestId = $requestId
        state     = $state
        phase     = $phase
        percent   = $percent
        message   = $message
        updatedAt = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    }
    $json = ConvertTo-Json $obj -Compress
    $tmp = "$statusPath.tmp"
    [System.IO.File]::WriteAllText($tmp, $json, [System.Text.Encoding]::UTF8)
    Move-Item -LiteralPath $tmp -Destination $statusPath -Force
}

function Quote-Arg([string]$arg) {
    if ([string]::IsNullOrEmpty($arg)) { return '""' }
    if ($arg -notmatch '[\s"]') { return $arg }
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('"')
    $slashes = 0
    for ($i = 0; $i -lt $arg.Length; $i++) {
        $c = $arg[$i]
        if ($c -eq '\') { $slashes++ }
        elseif ($c -eq '"') {
            [void]$sb.Append('\' * ($slashes * 2 + 1))
            [void]$sb.Append('"')
            $slashes = 0
        } else {
            [void]$sb.Append('\' * $slashes)
            $slashes = 0
            [void]$sb.Append($c)
        }
    }
    [void]$sb.Append('\' * ($slashes * 2))
    [void]$sb.Append('"')
    return $sb.ToString()
}

L "start mode=$mode input=$inputPath temp=$tempPath duration=$durationSec"
Write-Status 'compressing' 'compressing' 1

# hidden marker embedded in the mp4s udta:comment atom so the helper can later identify files weve already compressed without a sidecar db -- format obs-replaykit_compress_v2_<fast|slow>:<unix_ms>:<pre_compress_bytes>, where "fast"/"slow" map to the fast and smaller modes and the actual encoder is picked from caps below.
$tagMode = if ($mode -eq 'fast') { 'fast' } else { 'slow' }
$tagTs   = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
$preSize = 0
try { $preSize = ([System.IO.FileInfo]::new($inputPath)).Length } catch {}
$comment = "obs-replaykit_compress_v2_${tagMode}:${tagTs}:${preSize}"

# build encoder-specific argv. goals: fast mode -> shortest wall time, ok quality. gpu encoders win here becuase they barely touch the cpu (so games keep running fine) and ffmpeg sustains 5-10x realtime on them. smaller mode -> smallest file at watch-anywhere quality. cpu sw encoders win here -- libsvtav1 and libx265 are ~2-3x more bit-efficient than nvenc/amf/qsv at matched perceptual quality. qp / crf targets are picked one or two notches below the obs source (nvenc hevc defaults to cqp 22-24 for the balanced/quality presets), so the output is guaranteed to be a step worse perceptually and a noticeable step smaller in size. the size guard further down verifies that and reverts the replace if the output is somehow not smaller (very short clips, already-tiny sources, etc). common flags shared by every branch:
$commonHead = @('-y','-hide_banner','-loglevel','error','-i', $inputPath)
$commonTail = @(
    '-c:a','aac',
    '-b:a','96k',
    '-metadata',"comment=$comment",
    '-movflags','+faststart',
    '-progress','pipe:1','-nostats',
    $tempPath
)
switch ($pickedEncoder) {
    'hevc_nvenc' {
        # nvidia hevc. constqp + spatial-aq + lookahead is the safe combo: spatial-aq redistributes the fixed qp across the frame (helps quality in flat regions) without raising the bitrate ceiling, becuase there is no bitrate ceiling to raise in constqp mode. qp 28 -> ~50% of source at quality close to the hevc qp24 input.
        $argv = $commonHead + @(
            '-c:v','hevc_nvenc',
            '-preset','p4',
            '-tune','hq',
            '-rc','constqp',
            '-qp','28',
            '-bf','2',
            '-spatial-aq','1',
            '-rc-lookahead','16',
            '-multipass','disabled',
            '-profile:v','main',
            '-tag:v','hvc1',
            '-pix_fmt','yuv420p'
        ) + $commonTail
    }
    'h264_nvenc' {
        # h264 needs ~30% more bits than hevc for the same quality, so the qp target sits a couple notches above hevc.
        $argv = $commonHead + @(
            '-c:v','h264_nvenc',
            '-preset','p4',
            '-tune','hq',
            '-rc','constqp',
            '-qp','28',
            '-bf','2',
            '-spatial-aq','1',
            '-rc-lookahead','16',
            '-multipass','disabled',
            '-profile:v','high',
            '-pix_fmt','yuv420p'
        ) + $commonTail
    }
    'hevc_amf' {
        # amd amf hevc. cqp mode + matching qp_i / qp_p / qp_b is the rate-control combination that honours the qp target without padding with filler bits. qp 28 mirrors the hevc_nvenc target for comparable compression on amd silicon.
        $argv = $commonHead + @(
            '-c:v','hevc_amf',
            '-usage','transcoding',
            '-quality','quality',
            '-rc','cqp',
            '-qp_i','28','-qp_p','28','-qp_b','28',
            '-profile:v','main',
            '-tag:v','hvc1'
        ) + $commonTail
    }
    'h264_amf' {
        $argv = $commonHead + @(
            '-c:v','h264_amf',
            '-usage','transcoding',
            '-quality','quality',
            '-rc','cqp',
            '-qp_i','28','-qp_p','28','-qp_b','28',
            '-profile:v','high'
        ) + $commonTail
    }
    'hevc_qsv' {
        # intel quick sync hevc. global_quality maps to icq on qsv -- the closest analogue to nvencs constqp. icq 26 puts us at roughly the same quality target as hevc_nvenc qp. look_ahead 0 keeps file size from creeping up on already-compressed sources.
        $argv = $commonHead + @(
            '-c:v','hevc_qsv',
            '-preset','medium',
            '-global_quality','26',
            '-look_ahead','0',
            '-profile:v','main',
            '-tag:v','hvc1'
        ) + $commonTail
    }
    'h264_qsv' {
        $argv = $commonHead + @(
            '-c:v','h264_qsv',
            '-preset','medium',
            '-global_quality','25',
            '-look_ahead','0',
            '-profile:v','high'
        ) + $commonTail
    }
    'libx265' {
        # libx265 medium crf 25 -- the textbook "compress with good quality" config, tuned a notch tighter than the original crf 28 defualt. pools=$swpools + frame-threads=2 lets two encodes share one cpu cleanly; without the cap, each libx265 process grabs every core and contends.
        $argv = $commonHead + @(
            '-c:v','libx265',
            '-preset','medium',
            '-crf','25',
            '-tag:v','hvc1',
            '-x265-params',"pools=${swPools}:frame-threads=2:log-level=error"
        ) + $commonTail
    }
    default {
        # libx264 -- universal fallback when nothing else linked. preset varies by mode so fast feels fast even on machines with no hw encoder at all. crf values target ~50% of source for fast and ~35% for smaller on typical game footage.
        $preset = if ($mode -eq 'fast') { 'veryfast' } else { 'medium' }
        $crf    = if ($mode -eq 'fast') { '23' } else { '21' }
        $argv = $commonHead + @(
            '-c:v','libx264',
            '-preset',$preset,
            '-crf',$crf,
            '-threads',[string]$swPools
        ) + $commonTail
    }
}
L "encoder=$pickedEncoder mode=$mode cpu=$cpuCount swPools=$swPools"

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $ffmpeg
$psi.Arguments = (($argv | ForEach-Object { Quote-Arg $_ }) -join ' ')
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError  = $true
$proc = New-Object System.Diagnostics.Process
$proc.StartInfo = $psi
[void]$proc.Start()

# drop to belownormal so a running encode never starves the users foreground apps (game, browser, obs itself). on windows this is enough to keep the desktop responsive even with several parallel encodes -- ffmpeg still uses every idle cpu cycle; it just yields instantly when something interactive needs the core. best-effort: if priorityclass set fails (rare race / weird security context), the encode just runs at normal -- functionally fine, slightly louder on system load.
try { $proc.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::BelowNormal } catch {}

# record the ffmpeg pid to a sidecar next to the status json so the main helpers cancel-activeupload can find and kill us when the user clicks stop on compress all. we cant call set-uploadstate from in here -- the worker runs in a seperate runspace and doesnt share the helpers $script:state -- so the sidecar file is the coordination channel. the cancel routes first job is to delete this sidecar, so a stale file from a crashed worker cant survive to bite the next encode (we also stop-process the pid before the delete, which silently fails if the pid is reused or gone).
if ($statusPath) {
    try {
        [System.IO.File]::WriteAllText("$statusPath.pid",
            [string]$proc.Id, [System.Text.Encoding]::ASCII)
    } catch {
        L "warn: could not write pid sidecar: $($_.Exception.Message)"
    }
}

# stderr is collected once at end for diagnostics only.
$stderrTask = $proc.StandardError.ReadToEndAsync()

# pull progress from stdout line-by-line. -progress pipe:1 makes ffmpeg flush each progress block to stdout immediately, so each readline returns within ~1s of the actual encode position. the alternative (-progress <file>) goes thru the os page cache and can lag arbitrarily on windows. we strictly match out_time_us= (microseconds). out_time_ms= was changed from milliseconds to microseconds in ffmpeg 5.x and is inconsistent across builds, so we never trust it.
$durationUs = [Math]::Max(1.0, $durationSec * 1000000.0)
$lastReportedPct = 1
$linesSeen = 0
try {
    while ($true) {
        $line = $proc.StandardOutput.ReadLine()
        if ($null -eq $line) { break }
        $linesSeen++
        if ($line.StartsWith('out_time_us=')) {
            $valStr = $line.Substring(12)
            $us = 0.0
            if ([double]::TryParse($valStr, [System.Globalization.NumberStyles]::Float, $ci, [ref]$us)) {
                $pct = [int][Math]::Floor(2 + (94.0 * [Math]::Min(1.0, $us / $durationUs)))
                if ($pct -ne $lastReportedPct) {
                    $lastReportedPct = $pct
                    Write-Status 'compressing' 'compressing' $pct
                }
            }
        }
    }
} catch {
    L "stdout read error: $($_.Exception.Message)"
}

$proc.WaitForExit()
$stderr = $stderrTask.Result
if ($stderr) { L "stderr: $stderr" }
L "progress lines=$linesSeen lastPct=$lastReportedPct"

if ($proc.ExitCode -ne 0) {
    if (Test-Path -LiteralPath $tempPath) {
        Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
    }
    Write-Status 'error' 'error' 0 ("ffmpeg failed (exit=" + $proc.ExitCode + ")")
    exit 1
}
if (-not (Test-Path -LiteralPath $tempPath)) {
    Write-Status 'error' 'error' 0 'ffmpeg produced no output file'
    exit 1
}

# size guard. compression is supposed to shrink files; if it didnt, dont replace the original. this catches: very short clips where mp4 header + first idr + aac priming overhead exceeds whatever the encode saved sources already encoded more aggressively than our target (e.g. obs "performance" preset at cqp 28 vs our qp 28 fast mode -- still smaller but barely; the size guard surfaces edge cases) any future encoder misconfiguration we havent anticipated the clip stays untouched, the marker is not written, and the next /clips poll shows the file in its original state. the user sees a clean "already at minimum size" message.
$outBytes = 0
try { $outBytes = ([System.IO.FileInfo]::new($tempPath)).Length } catch {}
if ($preBytes -gt 0 -and $outBytes -gt 0 -and $outBytes -ge $preBytes) {
    Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
    $preMb = [Math]::Round($preBytes / 1MB, 1)
    $outMb = [Math]::Round($outBytes / 1MB, 1)
    L "abort: would inflate (${outMb} MB >= ${preMb} MB source); keeping original"
    Write-Status 'error' 'error' 0 ("Already at minimum size (${outMb} MB would be larger than ${preMb} MB)")
    exit 1
}

Write-Status 'compressing' 'replacing' 98
try {
    # same-volume move-item is an atomic rename. across volumes it falls back to copy-then-delete which can leave the destination half-written on crash. detect the volume mismatch and route thru a sidecar on the sources volume so the final replace is always atomic. the sidecar is named with the _replaykit_ prefix that the clip listing filters out.
    $sourceVol = [System.IO.Path]::GetPathRoot($sourcePath)
    $tempVol   = [System.IO.Path]::GetPathRoot($tempPath)
    if ($sourceVol.ToLowerInvariant() -eq $tempVol.ToLowerInvariant()) {
        Move-Item -LiteralPath $tempPath -Destination $sourcePath -Force
    } else {
        $extOut = [System.IO.Path]::GetExtension($sourcePath)
        $sideTemp = Join-Path ([System.IO.Path]::GetDirectoryName($sourcePath)) ("_replaykit_finalize_" + [Guid]::NewGuid().ToString('N') + $extOut)
        Copy-Item -LiteralPath $tempPath -Destination $sideTemp -Force
        Move-Item -LiteralPath $sideTemp -Destination $sourcePath -Force
        Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
    }
} catch {
    if (Test-Path -LiteralPath $tempPath) {
        Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
    }
    Write-Status 'error' 'error' 0 ("Could not replace original: " + $_.Exception.Message)
    exit 1
}

# restore the original file timestamps onto the freshly-replaced file. use system.io.file so we set utc times directly without tz math, and do creationtime first so the lastwritetime "wins" any potential windows file-system-tunneling refresh.
if ($origLastWrite) {
    try {
        if ($origCreation) {
            [System.IO.File]::SetCreationTimeUtc($sourcePath, $origCreation)
        }
        [System.IO.File]::SetLastWriteTimeUtc($sourcePath, $origLastWrite)
        L ("timestamps restored: ctime=" + ($origCreation.ToString('o')) + " mtime=" + ($origLastWrite.ToString('o')))
    } catch {
        L "timestamp restore failed: $($_.Exception.Message)"
    }
}

# persist the compress marker into clips_db.json so the docks get-clipslistuncached cache check (cmp_mtime == file mtime) sees the new mode immediately. without this, the preserved mtime makes the cache key still match, but the cached cmp_mode is whatever it was before -- empty for first-time compress -- so the dock would keep showing the clip as compressable until something else invalidates the entry. ClipsMetaLock (the same lock Read-ClipsDb/Save-ClipsDb/Mark-Uploaded take in the main runspace) serialises this against every other clips_db.json writer, not just concurrent compress workers.
if ($dbPath) {
    $tagMode = if ($mode -eq 'fast') { 'fast' } else { 'slow' }
    $clipName = [System.IO.Path]::GetFileName($sourcePath)
    $newMtimeTicks = if ($origLastWrite) {
        [int64]$origLastWrite.Ticks
    } else {
        try { [int64]([System.IO.FileInfo]::new($sourcePath).LastWriteTimeUtc.Ticks) } catch { [int64]0 }
    }
    $nowMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()

    if ($ClipsMetaLock) { [System.Threading.Monitor]::Enter($ClipsMetaLock) }
    try {
        $dbDir = [System.IO.Path]::GetDirectoryName($dbPath)
        if ($dbDir -and -not (Test-Path -LiteralPath $dbDir)) {
            New-Item -ItemType Directory -Path $dbDir -Force | Out-Null
        }
        $db = @{}
        if (Test-Path -LiteralPath $dbPath) {
            try {
                $raw = [System.IO.File]::ReadAllText($dbPath)
                if (-not [string]::IsNullOrWhiteSpace($raw)) {
                    $parsed = ConvertFrom-Json $raw
                    foreach ($prop in $parsed.PSObject.Properties) {
                        $vEntry = @{}
                        foreach ($p2 in $prop.Value.PSObject.Properties) {
                            $vEntry[$p2.Name] = $p2.Value
                        }
                        $db[$prop.Name] = $vEntry
                    }
                }
            } catch {
                L "db read failed (continuing with empty db): $($_.Exception.Message)"
            }
        }
        $entry = if ($db.ContainsKey($clipName)) { $db[$clipName] } else { @{} }
        $entry.cmp_mode  = $tagMode
        $entry.cmp_mtime = $newMtimeTicks
        $entry.cmp_ts    = [int64]$nowMs
        if ($preBytes -gt 0) { $entry.cmp_pre = [int64]$preBytes }
        # stamp the entry as written by the v2 pipeline. without this, the /clips cache in 40_clips.ps1 ignores the cached cmp_mode (forces a marker re-probe), which would be a perf hit on every list of an unchanged folder.
        $entry.cmp_ver   = 2
        $db[$clipName] = $entry
        $json = ConvertTo-Json $db -Depth 4
        $utf8 = New-Object System.Text.UTF8Encoding($false)
        # this runs in the detached worker process, not dot-sourced into the module system, so $script:SCRATCH_DIR isnt available here -- same %temp%\ReplayKit\scratch path every other part of the app uses, just built inline. lands the tmp there instead of next to clips_db.json so a crash between the write and the move never leaves a stray .tmp sitting beside it.
        $scratchDirForDb = Join-Path $env:TEMP 'ReplayKit\scratch'
        if (-not (Test-Path -LiteralPath $scratchDirForDb)) {
            [void](New-Item -ItemType Directory -Path $scratchDirForDb -Force)
        }
        $tmpDb = Join-Path $scratchDirForDb ([System.IO.Path]::GetFileName($dbPath) + '.' + [System.Guid]::NewGuid().ToString('N') + '.tmp')
        [System.IO.File]::WriteAllText($tmpDb, $json, $utf8)
        Move-Item -LiteralPath $tmpDb -Destination $dbPath -Force
        L "marker written: name=$clipName mode=$tagMode mtime=$newMtimeTicks"
    } catch {
        L "marker write failed: $($_.Exception.Message)"
    } finally {
        if ($ClipsMetaLock) { [System.Threading.Monitor]::Exit($ClipsMetaLock) }
    }
}

L 'done'
Write-Status 'done' 'done' 100
exit 0
'@

    # run the worker as an in-process runspace instead of a spawned powershell.exe child. two visible wins for users: task manager no longer shows a "windows powershell" entry per active compress (was the main source of clutter during compress all -- up to n copies at once). ~80 mb less ram and ~300 ms less startup latency per job, becuase we skip the powershell.exe cold-start. the workers body is unchanged below -- only the spawn mechanism is diffrent. param values that used to be carried over as env vars are now bound to the scripts param() block via addparameter.
    Write-Log "Start-CompressOverwriteFile clip=$($selected.name) mode=$mode duration=$duration" 'compress' $requestId
    $ps = [powershell]::Create()
    $ps.RunspacePool = $script:WatcherPool
    $null = $ps.AddScript($workerScript)
    $null = $ps.AddParameter('Ffmpeg',          $ffmpeg)
    $null = $ps.AddParameter('InputPath',       $selected.full)
    $null = $ps.AddParameter('TempPath',        $tempPath)
    $null = $ps.AddParameter('SourcePath',      $selected.full)
    $null = $ps.AddParameter('DurationSec',     [double]$duration)
    $null = $ps.AddParameter('Mode',            $mode)
    $null = $ps.AddParameter('StatusPath',      $statusPath)
    $null = $ps.AddParameter('RequestId',       $requestId)
    $null = $ps.AddParameter('CompressLogFile', $compressLogFile)
    $null = $ps.AddParameter('DbPath',          [string]$dbPath)
    $null = $ps.AddParameter('PreBytes',        [int64]$preBytes)
    # hand the full caps dict + the resolved encoder picks to the worker. the worker uses fastencoder/smallerencoder directly for argv construction; capsjson is kept for cpucount and any future caps the worker needs (e.g. amd/intel hints).
    $null = $ps.AddParameter('CapsJson',        (ConvertTo-Json $caps -Compress -Depth 4))
    $null = $ps.AddParameter('FastEncoder',     [string]$caps.fastEncoder)
    $null = $ps.AddParameter('SmallerEncoder',  [string]$caps.smallerEncoder)
    $null = $ps.AddParameter('ClipsMetaLock',   $script:State.ClipsMetaLock)
    $null = $ps.AddParameter('LogLock',         $script:State.LogLock)
    try {
        $asyncResult = $ps.BeginInvoke()
        Set-UploadState @{ requestId = $requestId; processId = 0 }
    } catch {
        try { $ps.Dispose() } catch {}
        $msg = "Could not launch compression worker: $($_.Exception.Message)"
        Set-UploadState @{
            requestId = $requestId
            state    = 'error'
            active   = $false
            clipName = $selected.name
            error    = $msg
        }
        return @{ ok = $false; message = $msg }
    }

    $watch = Start-CompressOverwriteResultWatcher $ps $asyncResult $selected.name $statusPath $tempPath $requestId
    if (-not $watch.ok) {
        try { $ps.Stop() } catch {}
        try { $ps.Dispose() } catch {}
        return $watch
    }

    return @{ ok = $true; state = 'compressing'; clip = $selected.name; requestId = $requestId }
}

# runs in a watcher runspace. when the worker runspace completes (was previously a powershell.exe child process; now an in-process [powershell] instance), read the final status, clear/error the upload state, and invalidate the clips cache so the ui sees the new files size/mtime.
function Start-CompressOverwriteResultWatcher($workerPs, $workerAsync, [string]$clipName, [string]$statusPath, [string]$tempPath, [string]$requestId) {
    $watcher = [powershell]::Create()
    $watcher.RunspacePool = $script:WatcherPool
    $null = $watcher.AddScript({
        param($workerPs, $workerAsync, $clipName, $statusPath, $tempPath, $requestId, $state)
        # block until the worker runspace finishes. endinvoke is the runspace analogue of process.waitforexit. it also re-throws any unhandled exception from the worker script, which we catch and surface as an error status.
        $workerThrew = $null
        try {
            [void]$workerPs.EndInvoke($workerAsync)
        } catch {
            $workerThrew = $_.Exception
        }
        $workerHadErrors = [bool]$workerPs.HadErrors
        # best effort: free the worker runspaces resources right away so the pool slot can be reused by the next job.
        try { $workerPs.Dispose() } catch {}

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
            if ($statusPath -and (Test-Path -LiteralPath $statusPath)) {
                try { Remove-Item -LiteralPath $statusPath -Force -ErrorAction SilentlyContinue } catch {}
                try { Remove-Item -LiteralPath "$statusPath.pid" -Force -ErrorAction SilentlyContinue } catch {}
            }
            if ($tempPath -and (Test-Path -LiteralPath $tempPath)) {
                try { Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue } catch {}
            }
            return
        }

        $status = $null
        try {
            if ($statusPath -and (Test-Path -LiteralPath $statusPath)) {
                $raw = [System.IO.File]::ReadAllText($statusPath)
                if (-not [string]::IsNullOrWhiteSpace($raw)) {
                    $status = ConvertFrom-Json $raw
                }
            }
        } catch {}

        # success means: the worker didnt throw, had no error stream records, and wrote a final status of "done". any one of those failing flips us into error.
        $success = (-not $workerThrew) -and (-not $workerHadErrors) -and `
                   ($status -and [string]$status.state -eq 'done')
        $exitDescription = if ($workerThrew) {
            "worker exception: $($workerThrew.Message)"
        } elseif ($workerHadErrors) {
            'worker reported errors'
        } else {
            'worker exited'
        }

        [System.Threading.Monitor]::Enter($state.UploadLock)
        try {
            $u = if ($requestId -and $state.Jobs.ContainsKey($requestId)) {
                $state.Jobs[$requestId]
            } else {
                @{ requestId = $requestId; clipName = $clipName }
            }
            if ($success) {
                # no url to set -- the clip card just goes back to its normal state once /clips picks up the new mtime/size.
                $u.state = 'idle'; $u.active = $false; $u.error = ''
                $u.phase = ''; $u.percent = 0; $u.processId = 0
                $u.statusPath = ''; $u.tempPath = ''; $u.url = ''
            } else {
                $msg = if ($status -and $status.message) {
                    [string]$status.message
                } elseif ($workerThrew) {
                    "Compress failed: $($workerThrew.Message)"
                } else {
                    "Compress finished without success ($exitDescription)"
                }
                $u.state = 'error'; $u.active = $false; $u.error = $msg
                $u.phase = 'error'; $u.percent = 0; $u.processId = 0
                $u.statusPath = ''; $u.tempPath = ''
            }
            $u.updatedAt = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
            $u.requestId = $requestId
            $u.clipName = $clipName
            $state.Jobs[$requestId] = $u
            $chosen = $null
            foreach ($j in $state.Jobs.Values) {
                if (-not $j.active) { continue }
                if (-not $chosen -or ([int64]$j.updatedAt -gt [int64]$chosen.updatedAt)) {
                    $chosen = $j
                }
            }
            $state.Upload = if ($chosen) { $chosen } else { $u }
        } finally { [System.Threading.Monitor]::Exit($state.UploadLock) }

        if ($tempPath -and (Test-Path -LiteralPath $tempPath)) {
            try { Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue } catch {}
        }
        if ($statusPath -and (Test-Path -LiteralPath $statusPath)) {
            try { Remove-Item -LiteralPath $statusPath -Force -ErrorAction SilentlyContinue } catch {}
            try { Remove-Item -LiteralPath "$statusPath.pid" -Force -ErrorAction SilentlyContinue } catch {}
        }

        [System.Threading.Monitor]::Enter($state.ClipsMetaLock)
        try {
            $state.ClipsCacheAt = [DateTime]::MinValue
            $state.ClipsCacheSig = ''
            $state.ClipsCacheVersion = ''
            $state.ClipsCacheJson = ''
        } finally { [System.Threading.Monitor]::Exit($state.ClipsMetaLock) }
    }).AddArgument($workerPs).AddArgument($workerAsync).AddArgument($clipName).AddArgument($statusPath).AddArgument($tempPath).AddArgument($requestId).AddArgument($script:State)

    try {
        $null = $watcher.BeginInvoke()
    } catch {
        return @{ ok = $false; message = "Could not start compress watcher: $($_.Exception.Message)" }
    }
    return @{ ok = $true }
}

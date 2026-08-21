# local file edits on clips: stream-copy trim and verbatim duplicate. both operations leave the source untouched by defualt; the trim function also supports an explicit "overwrite" mode that atomically replaces the source via a sibling temp file + move-item -force, so a crashed ffmpeg can never leave the original half-written.

$script:TrimKeyframeWorkerTimeoutMs = 300000
$script:PooledConstantNames += @('TrimKeyframeWorkerTimeoutMs')

# probe the sources video bitrate in bits/sec. used by the precise trim branch to cap libx264s output so an already-compressed source cant inflate during re-encode (crf 14 alone faithfully reproduces blocking/ banding from a low-bitrate input, costing more bits than the source carried -- the maxrate cap stops that). strategy: ask ffprobe for the v:0 streams bit_rate tag (cheapest, most mp4s from obs carry it). fall back to (filesize_bytes * 8) / duration_sec, knocking 10% off to ballpark the video portion (audio is typically a few hundred kbps of the total). returns 0 if both fail; callers should skip the cap in that case rather than guess.
function Get-VideoBitratePerSec([string]$path) {
    $caps    = Get-HelperCapabilities
    $ffmpeg  = [string]$caps.ffmpeg
    $ffprobe = [string]$caps.ffprobe

    if (-not [string]::IsNullOrWhiteSpace($ffprobe) -and (Test-Path -LiteralPath $ffprobe)) {
        try {
            $r = Invoke-NativeCapture $ffprobe @(
                '-v', 'error',
                '-select_streams', 'v:0',
                '-show_entries', 'stream=bit_rate',
                '-of', 'default=nokey=1:noprint_wrappers=1',
                $path
            )
            if ($r.ExitCode -eq 0 -and $r.Output.Count -gt 0) {
                $raw = ($r.Output -join '').Trim()
                $value = [int64]0
                if ([int64]::TryParse($raw, [ref]$value) -and $value -gt 0) {
                    return $value
                }
            }
        } catch {
            Write-Log "Get-VideoBitratePerSec ffprobe failed: $($_.Exception.Message)"
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($ffmpeg) -and (Test-Path -LiteralPath $ffmpeg)) {
        try {
            $duration = Get-VideoDurationSec $ffmpeg $path
            if ($duration -gt 0) {
                $size = ([System.IO.FileInfo]::new($path)).Length
                if ($size -gt 0) {
                    return [int64]([Math]::Floor((($size * 8.0) / $duration) * 0.9))
                }
            }
        } catch {
            Write-Log "Get-VideoBitratePerSec fallback failed: $($_.Exception.Message)"
        }
    }

    return [int64]0
}

function Convert-TrimKeyframeWorkerResult($data, [string]$fallbackName) {
    if ($null -eq $data) {
        return @{ ok = $false; message = 'Keyframe worker returned no data'; keyframes = @() }
    }
    $keyframes = @()
    foreach ($raw in @($data.keyframes)) {
        $value = [double]0
        if ([double]::TryParse([string]$raw, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$value) -and $value -ge 0) {
            $keyframes += [double]$value
        }
    }
    $ok = [bool]$data.ok
    return @{
        ok = $ok
        name = if ([string]::IsNullOrWhiteSpace([string]$data.name)) { $fallbackName } else { [string]$data.name }
        keyframes = $keyframes
        count = if ($ok) { [int]$keyframes.Count } else { 0 }
        cached = [bool]$data.cached
        probeMs = if ($data.probeMs -ne $null) { [int]$data.probeMs } else { 0 }
        message = if ($ok) { '' } else { [string]$data.message }
    }
}

function Copy-TrimKeyframeResult([hashtable]$result) {
    $copy = @{}
    foreach ($key in $result.Keys) { $copy[$key] = $result[$key] }
    return $copy
}

function Save-TrimKeyframeCache([string]$cacheKey, [string]$cacheSig, [hashtable]$result) {
    $lockTaken = $false
    try {
        [System.Threading.Monitor]::Enter($script:State.TrimKeyframeCacheLock, [ref]$lockTaken)
        $script:State.TrimKeyframeCache[$cacheKey] = @{
            sig = $cacheSig
            at = [DateTime]::UtcNow
            result = $result
        }
        if ($script:State.TrimKeyframeCache.Count -gt 64) {
            $removeCount = $script:State.TrimKeyframeCache.Count - 64
            $old = $script:State.TrimKeyframeCache.GetEnumerator() |
                Sort-Object { $_.Value.at } |
                Select-Object -First $removeCount
            foreach ($item in @($old)) {
                [void]$script:State.TrimKeyframeCache.Remove($item.Key)
            }
        }
    } finally {
        if ($lockTaken) { [System.Threading.Monitor]::Exit($script:State.TrimKeyframeCacheLock) }
    }
}

function Get-TrimKeyframeCache([string]$cacheKey, [string]$cacheSig) {
    $lockTaken = $false
    try {
        [System.Threading.Monitor]::Enter($script:State.TrimKeyframeCacheLock, [ref]$lockTaken)
        $entry = $script:State.TrimKeyframeCache[$cacheKey]
        if ($entry -and [string]$entry['sig'] -eq $cacheSig -and $entry['result']) {
            $cached = Copy-TrimKeyframeResult ([hashtable]$entry['result'])
            $cached['cached'] = $true
            $cached['pending'] = $false
            return $cached
        }
    } finally {
        if ($lockTaken) { [System.Threading.Monitor]::Exit($script:State.TrimKeyframeCacheLock) }
    }
    return $null
}

function Get-TrimKeyframeWorkerResult([hashtable]$job, [string]$cacheKey, [string]$cacheSig, [string]$clipName) {
    $statusPath = [string]$job.statusPath
    $jobKey = [string]$job.key
    if ($statusPath -and (Test-Path -LiteralPath $statusPath)) {
        try {
            $raw = [System.IO.File]::ReadAllText($statusPath)
            $result = Convert-TrimKeyframeWorkerResult (ConvertFrom-Json $raw) $clipName
            if ($result.ok) {
                Save-TrimKeyframeCache $cacheKey $cacheSig $result
            }
            $result['pending'] = $false
            return $result
        } catch {
            return @{ ok = $false; pending = $false; message = "Could not read keyframe worker result: $($_.Exception.Message)"; keyframes = @() }
        } finally {
            try { Remove-Item -LiteralPath $statusPath -Force -ErrorAction SilentlyContinue } catch {}
            $lockTaken = $false
            try {
                [System.Threading.Monitor]::Enter($script:State.TrimKeyframeJobsLock, [ref]$lockTaken)
                [void]$script:State.TrimKeyframeJobs.Remove($jobKey)
            } finally {
                if ($lockTaken) { [System.Threading.Monitor]::Exit($script:State.TrimKeyframeJobsLock) }
            }
        }
    }

    $startedAt = if ($job.startedAt -is [DateTime]) { [DateTime]$job.startedAt } else { [DateTime]::UtcNow }
    $ageMs = ([DateTime]::UtcNow - $startedAt).TotalMilliseconds
    if ($ageMs -gt $script:TrimKeyframeWorkerTimeoutMs) {
        try {
            $pid = [int]$job.processId
            if ($pid -gt 0) { Stop-Process -Id $pid -Force -ErrorAction SilentlyContinue }
        } catch {}
        $lockTaken = $false
        try {
            [System.Threading.Monitor]::Enter($script:State.TrimKeyframeJobsLock, [ref]$lockTaken)
            [void]$script:State.TrimKeyframeJobs.Remove($jobKey)
        } finally {
            if ($lockTaken) { [System.Threading.Monitor]::Exit($script:State.TrimKeyframeJobsLock) }
        }
        return @{ ok = $false; pending = $false; message = 'Keyframe scan timed out; using normal fast trim'; keyframes = @() }
    }

    try {
        $pid = [int]$job.processId
        if ($pid -gt 0 -and -not (Get-Process -Id $pid -ErrorAction SilentlyContinue)) {
            $lockTaken = $false
            try {
                [System.Threading.Monitor]::Enter($script:State.TrimKeyframeJobsLock, [ref]$lockTaken)
                [void]$script:State.TrimKeyframeJobs.Remove($jobKey)
            } finally {
                if ($lockTaken) { [System.Threading.Monitor]::Exit($script:State.TrimKeyframeJobsLock) }
            }
            return @{ ok = $false; pending = $false; message = 'Keyframe worker exited without a result'; keyframes = @() }
        }
    } catch {}

    return @{ ok = $false; pending = $true; message = 'Keyframe snap points are still loading'; keyframes = @(); retryMs = 500 }
}

function Start-TrimKeyframeWorker([hashtable]$source, [string]$ffprobe, [string]$cacheKey, [string]$cacheSig) {
    $worker = Join-Path (Get-ScriptDir) 'trim_keyframes_worker.ps1'
    if (-not (Test-Path -LiteralPath $worker)) {
        return @{ ok = $false; pending = $false; message = 'Keyframe worker script not found'; keyframes = @() }
    }

    $jobKey = "$cacheKey|$cacheSig"
    $statusPath = Join-Path $script:SCRATCH_DIR ("obs_replaykit_keyframes_" + [Guid]::NewGuid().ToString('N') + ".json")
    $powershell = Join-Path $PSHOME 'powershell.exe'
    $args = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', $worker,
        '-Ffprobe', $ffprobe,
        '-SourcePath', [string]$source.full,
        '-ClipName', [string]$source.name,
        '-OutputPath', $statusPath
    )

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $powershell
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $false
    $psi.RedirectStandardError = $false
    $psi.CreateNoWindow = $true
    $psi.Arguments = (($args | ForEach-Object { Quote-Arg ([string]$_) }) -join ' ')

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    try {
        if (-not $proc.Start()) {
            return @{ ok = $false; pending = $false; message = 'Could not start keyframe worker'; keyframes = @() }
        }
        $job = @{
            key = $jobKey
            sig = $cacheSig
            statusPath = $statusPath
            processId = [int]$proc.Id
            startedAt = [DateTime]::UtcNow
        }
        $lockTaken = $false
        try {
            [System.Threading.Monitor]::Enter($script:State.TrimKeyframeJobsLock, [ref]$lockTaken)
            $script:State.TrimKeyframeJobs[$jobKey] = $job
        } finally {
            if ($lockTaken) { [System.Threading.Monitor]::Exit($script:State.TrimKeyframeJobsLock) }
        }
        return @{ ok = $false; pending = $true; message = 'Keyframe snap points are loading'; keyframes = @(); retryMs = 500 }
    } catch {
        return @{ ok = $false; pending = $false; message = "Could not start keyframe worker: $($_.Exception.Message)"; keyframes = @() }
    } finally {
        try { $proc.Dispose() } catch {}
    }
}

function Get-ClipKeyframeTimes([string]$sourceName, [double]$durationSec = 0.0) {
    $source = Get-SafeClipPath $sourceName
    if (-not $source -or -not (Test-Path -LiteralPath $source.full)) {
        return @{ ok = $false; message = 'Source clip not found'; keyframes = @() }
    }
    $fi = [System.IO.FileInfo]::new($source.full)
    if (-not $fi.Exists) {
        return @{ ok = $false; message = 'Source clip not found'; keyframes = @() }
    }

    $cacheKey = $fi.FullName.ToLowerInvariant()
    $cacheSig = '{0}:{1}' -f $fi.Length, $fi.LastWriteTimeUtc.Ticks
    $cached = Get-TrimKeyframeCache $cacheKey $cacheSig
    if ($cached) { return $cached }

    $jobKey = "$cacheKey|$cacheSig"
    $lockTaken = $false
    $job = $null
    try {
        [System.Threading.Monitor]::Enter($script:State.TrimKeyframeJobsLock, [ref]$lockTaken)
        $job = $script:State.TrimKeyframeJobs[$jobKey]
    } finally {
        if ($lockTaken) { [System.Threading.Monitor]::Exit($script:State.TrimKeyframeJobsLock) }
    }
    if ($job) {
        return Get-TrimKeyframeWorkerResult ([hashtable]$job) $cacheKey $cacheSig $source.name
    }

    $caps = Get-HelperCapabilities
    $ffprobe = [string]$caps.ffprobe
    if ([string]::IsNullOrWhiteSpace($ffprobe) -or -not (Test-Path -LiteralPath $ffprobe)) {
        return @{ ok = $false; message = 'ffprobe.exe not found in clip folder'; keyframes = @() }
    }

    return Start-TrimKeyframeWorker $source $ffprobe $cacheKey $cacheSig
}

# pick a non-colliding "<base> (<suffix>).<ext>" or "<base> (<suffix> n).<ext>" filename inside the clip folder. returns $null if 99 candidates are taken.
function Get-SuffixedOutputName([string]$sourceName, [string]$suffix) {
    $base = [System.IO.Path]::GetFileNameWithoutExtension($sourceName)
    $ext  = [System.IO.Path]::GetExtension($sourceName)
    $clipDir = Get-ClipDir
    $candidate = "$base ($suffix)$ext"
    if (-not (Test-Path -LiteralPath (Join-Path $clipDir $candidate))) { return $candidate }
    for ($i = 2; $i -le 99; $i++) {
        $candidate = "$base ($suffix $i)$ext"
        if (-not (Test-Path -LiteralPath (Join-Path $clipDir $candidate))) { return $candidate }
    }
    return $null
}

# trim a clip with ffmpeg. two precision modes: $precise=$true (defualt): decode-then-seek (-ss after -i) + libx264 crf 14 re-encode. cut lands frame-accurate at the picked time; encode takes roughly clip-length seconds at the veryfast preset. $precise=$false: stream-copy with input-side seek. lossless and near-instant, but the cut snaps to the previous keyframe (every 1-2s for obs replay-buffer recordings). $mode is copy (defualt; new file) or overwrite (replaces the source in place via a temp sibling + move-item -force). $removeAudio drops all audio streams from the edited output.
function Invoke-ClipTrim([string]$sourceName, [double]$startSec, [double]$endSec, [string]$mode = 'copy', [bool]$precise = $true, [bool]$removeAudio = $false) {
    if ([double]::IsNaN($startSec) -or [double]::IsNaN($endSec)) {
        return @{ ok = $false; message = 'Bad start/end' }
    }
    if ($startSec -lt 0)        { return @{ ok = $false; message = 'Start must be >= 0' } }
    if ($endSec  -le $startSec) { return @{ ok = $false; message = 'End must be greater than start' } }
    $duration = $endSec - $startSec
    if ($duration -lt 0.5) {
        return @{ ok = $false; message = 'Trim must be at least 0.5s long' }
    }

    $mode = if ($mode) { $mode.ToLowerInvariant() } else { 'copy' }
    if ($mode -ne 'copy' -and $mode -ne 'overwrite') {
        return @{ ok = $false; message = "Unknown trim mode '$mode'" }
    }
    $overwrite = ($mode -eq 'overwrite')

    $source = Get-SafeClipPath $sourceName
    if (-not $source -or -not (Test-Path -LiteralPath $source.full)) {
        return @{ ok = $false; message = 'Source clip not found' }
    }

    $caps = Get-HelperCapabilities
    $ffmpeg = [string]$caps.ffmpeg
    if ([string]::IsNullOrWhiteSpace($ffmpeg) -or -not (Test-Path -LiteralPath $ffmpeg)) {
        return @{ ok = $false; message = 'ffmpeg.exe not found in clip folder' }
    }

    $ext = [System.IO.Path]::GetExtension($source.name)
    # for overwrite mode, snapshot the sources filesystem timestamps now (before the atomic replace destroys them) so we can restore them after the encode. same rationale as the compress-overwrite path: keeps the clip in its original sort position rather than jumping to the top of the dock list every time its trimmed.
    $origLastWriteUtc = $null
    $origCreationUtc  = $null
    if ($overwrite) {
        try {
            $srcInfo = [System.IO.FileInfo]::new($source.full)
            if ($srcInfo.Exists) {
                $origLastWriteUtc = $srcInfo.LastWriteTimeUtc
                $origCreationUtc  = $srcInfo.CreationTimeUtc
            }
        } catch {}
    }
    if ($overwrite) {
        # encode into %temp% so the in-flight file never shows in the clip folder. cross-volume finalize uses a _replaykit_ sidecar on the sources volume (filtered out of the clip listing).
        $outName  = $source.name
        $tempPath = Join-Path $script:SCRATCH_DIR ("replaykit_trim_" + [Guid]::NewGuid().ToString('N') + $ext)
        $outPath  = $tempPath
        $finalPath = $source.full
    } else {
        $suffix = if ($precise) { 'trimmed' } else { 'trimmed (fast)' }
        $outName  = Get-SuffixedOutputName $source.name $suffix
        if (-not $outName) { return @{ ok = $false; message = 'Too many existing trim outputs' } }
        $finalPath = Join-Path (Get-ClipDir) $outName
        $tempPath = Join-Path (Get-ClipDir) ("_replaykit_trim_copy_" + [Guid]::NewGuid().ToString('N') + $ext)
        $outPath  = $tempPath
    }

    # invariant-culture decimal formatting -- ffmpeg only accepts . as the decimal separator, never ,, regardless of system locale.
    $ci = [System.Globalization.CultureInfo]::InvariantCulture
    $startStr = $startSec.ToString('F3', $ci)
    $durStr   = $duration.ToString('F3', $ci)

    if ($precise) {
        # ss after -i is output-side / decode-then-seek: ffmpeg decodes frames from the start of the file but discards everything before $startsec, then emits exactly the picked window. this is the only way to land the cut on the exact frame the user chose; the tradeoff is a full re-encode. crf 14 is the perceptual quality target -- visually indistinguishable from the source. on a fresh obs clip (30-60 mbps) crf 14 lands around 70-85% of source size; on a heavily compressed source it would happily exceed the source bitrate trying to preserve compression artifacts, so we also probe the sources video bitrate and pass it as -maxrate / -bufsize. that puts x264 in "capped crf" mode: crf still drives the quality target, but if reaching it would mean spending more bits than the source had, rate-control caps the output. end result: trims of original clips look the same as before; trims of compressed clips no longer inflate. audio is stream-copied (-c:a copy) so the source aac bytes are preserved exactly, no second-generation transcoding loss. preset is "fast" rather than "veryfast" -- veryfast at low bitrates throws away bit-allocation efficiency the cap depends on, and the wall-clock difference for a typical trim is small enough to be invisible.
        $argv = @(
            '-hide_banner', '-loglevel', 'error',
            '-i', $source.full,
            '-ss', $startStr,
            '-t', $durStr,
            '-c:v', 'libx264',
            '-preset', 'fast',
            '-crf', '14'
        )

        $srcBps = Get-VideoBitratePerSec $source.full
        if ($srcBps -gt 0) {
            # floor at 200 kbps so a near-empty clip cant drive the cap so low that the encoder produces unwatchable output.
            $kbps = [int][Math]::Max(200, [Math]::Floor($srcBps / 1000.0))
            $argv += @(
                '-maxrate', ($kbps.ToString() + 'k'),
                '-bufsize', (($kbps * 2).ToString() + 'k')
            )
            Write-Log "Trim cap: source=${srcBps} bps, maxrate=${kbps}k"
        } else {
            Write-Log "Trim cap: source bitrate unknown, leaving CRF unconstrained"
        }

        if ($removeAudio) {
            $argv += @('-an')
        } else {
            $argv += @('-c:a', 'copy')
        }

        $argv += @(
            '-avoid_negative_ts', 'make_zero',
            '-movflags', '+faststart',
            '-y',
            $outPath
        )
    } else {
        # ss before -i is input-side / fast-seek: ffmpeg jumps to the nearest keyframe at-or-before $startsec without decoding the preceding frames. combined with -c copy this is a remux only, so the operation is lossless and finishes in well under a second -- at the cost of snap-to-keyframe imprecision.
        $argv = @(
            '-hide_banner', '-loglevel', 'error',
            '-ss', $startStr,
            '-i', $source.full,
            '-t', $durStr,
            '-c:v', 'copy'
        )
        if ($removeAudio) {
            $argv += @('-an')
        } else {
            $argv += @('-c:a', 'copy')
        }
        $argv += @(
            '-avoid_negative_ts', 'make_zero',
            '-y',
            $outPath
        )
    }

    Write-Log "Trim ($mode, precise=$precise, removeAudio=$removeAudio): $ffmpeg $($argv -join ' ')"
    try {
        $result = Invoke-NativeCapture $ffmpeg $argv
        if ($result.ExitCode -ne 0) {
            $combined = ($result.Output -join "`n")
            $msg = if ($combined.Length -gt 400) { $combined.Substring(0, 400) + '...' } else { $combined }
            if (Test-Path -LiteralPath $outPath) {
                try { Remove-Item -LiteralPath $outPath -Force -ErrorAction SilentlyContinue } catch {}
            }
            return @{ ok = $false; message = "ffmpeg trim failed (exit=$($result.ExitCode)): $msg" }
        }
    } catch {
        if (Test-Path -LiteralPath $outPath) {
            try { Remove-Item -LiteralPath $outPath -Force -ErrorAction SilentlyContinue } catch {}
        }
        return @{ ok = $false; message = "Trim failed: $($_.Exception.Message)" }
    }

    if (-not (Test-Path -LiteralPath $outPath)) {
        return @{ ok = $false; message = 'ffmpeg reported success but the output file is missing' }
    }

    if (-not $overwrite) {
        try {
            if (Test-Path -LiteralPath $finalPath) {
                Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
                return @{ ok = $false; message = 'A clip with that output name already exists' }
            }
            [System.IO.File]::Move($tempPath, $finalPath)
        } catch {
            if (Test-Path -LiteralPath $tempPath) {
                try { Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue } catch {}
            }
            return @{ ok = $false; message = "Could not publish trimmed copy: $($_.Exception.Message)" }
        }
    } else {
        # same-volume move-item is an atomic rename; across volumes its copy-then-delete which can corrupt the destination on crash. detect and route thru a sidecar on the sources volume when needed, so the final replace is always atomic.
        try {
            $sourceVol = [System.IO.Path]::GetPathRoot($source.full)
            $tempVol   = [System.IO.Path]::GetPathRoot($tempPath)
            if ($sourceVol.ToLowerInvariant() -eq $tempVol.ToLowerInvariant()) {
                Move-Item -LiteralPath $tempPath -Destination $source.full -Force
            } else {
                $sideTemp = Join-Path ([System.IO.Path]::GetDirectoryName($source.full)) ("_replaykit_finalize_" + [Guid]::NewGuid().ToString('N') + $ext)
                Copy-Item -LiteralPath $tempPath -Destination $sideTemp -Force
                Move-Item -LiteralPath $sideTemp -Destination $source.full -Force
                Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
            }
        } catch {
            if (Test-Path -LiteralPath $tempPath) {
                try { Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue } catch {}
            }
            return @{ ok = $false; message = "Could not replace original: $($_.Exception.Message)" }
        }
        # restore the original mtime/ctime that we captured before the encode. without this the clip would jump to the top of the date-sorted list every time the user trims it in place.
        if ($origLastWriteUtc) {
            try {
                if ($origCreationUtc) {
                    [System.IO.File]::SetCreationTimeUtc($source.full, $origCreationUtc)
                }
                [System.IO.File]::SetLastWriteTimeUtc($source.full, $origLastWriteUtc)
                Write-Log ("Trim overwrite: timestamps restored on " + $source.name)
            } catch {
                Write-Log "Trim overwrite: timestamp restore failed: $($_.Exception.Message)"
            }
        }
    }

    Clear-ClipsCache
    return @{
        ok          = $true
        name        = $outName
        sourceName  = $source.name
        mode        = $mode
        precise     = $precise
        removeAudio = $removeAudio
        startSec    = $startSec
        endSec      = $endSec
        durationSec = $duration
    }
}

# put the clips file path onto the windows clipboard as cf_hdrop so the user can paste the actual file into discord / explorer / etc. -- not the files bytes, the file reference (same thing ctrl+c on a file in explorer produces). requires sta threading -- the connection pool runspaces this now runs on are explicitly set to sta in 90_runtime.ps1 for exactly this reason, not left to default.
function Set-FileClipboard([string]$sourceName) {
    $source = Get-SafeClipPath $sourceName
    if (-not $source -or -not (Test-Path -LiteralPath $source.full)) {
        return @{ ok = $false; message = 'Source clip not found' }
    }
    try {
        Add-Type -AssemblyName System.Windows.Forms
        $col = New-Object System.Collections.Specialized.StringCollection
        [void]$col.Add($source.full)
        [System.Windows.Forms.Clipboard]::SetFileDropList($col)
        return @{ ok = $true; name = $source.name }
    } catch {
        return @{ ok = $false; message = "Clipboard copy failed: $($_.Exception.Message)" }
    }
}

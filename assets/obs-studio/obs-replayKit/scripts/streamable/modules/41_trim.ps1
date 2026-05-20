# local file edits on clips: stream-copy trim and verbatim duplicate. both operations leave the source untouched by defualt; the trim function also supports an explicit "overwrite" mode that atomically replaces the source via a sibling temp file + move-item -force, so a crashed ffmpeg can never leave the original half-written.

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

# trim a clip with ffmpeg. two precision modes: $precise=$true (defualt): decode-then-seek (-ss after -i) + libx264 crf 18 re-encode. cut lands frame-accurate at the picked time; encode takes roughly clip-length seconds at the veryfast preset. $precise=$false: stream-copy with input-side seek. lossless and near-instant, but the cut snaps to the previous keyframe (every 1-2s for obs replay-buffer recordings). $mode is copy (defualt; new file) or overwrite (replaces the source in place via a temp sibling + move-item -force).
function Invoke-ClipTrim([string]$sourceName, [double]$startSec, [double]$endSec, [string]$mode = 'copy', [bool]$precise = $true) {
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
        # encode into %temp% so the in-flight file never shows in the clip folder. cross-volume finalize uses a _streamable_ sidecar on the sources volume (filtered out of the clip listing).
        $outName  = $source.name
        $tempPath = Join-Path $env:TEMP ("streamable_trim_" + [Guid]::NewGuid().ToString('N') + $ext)
        $outPath  = $tempPath
    } else {
        $suffix = if ($precise) { 'trimmed' } else { 'trimmed (fast)' }
        $outName  = Get-SuffixedOutputName $source.name $suffix
        if (-not $outName) { return @{ ok = $false; message = 'Too many existing trim outputs' } }
        $outPath  = Join-Path (Get-ClipDir) $outName
        $tempPath = $null
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

        $argv += @(
            '-c:a', 'copy',
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
            '-c', 'copy',
            '-avoid_negative_ts', 'make_zero',
            '-y',
            $outPath
        )
    }

    Write-Log "Trim ($mode, precise=$precise): $ffmpeg $($argv -join ' ')"
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

    if ($overwrite) {
        # same-volume move-item is an atomic rename; across volumes its copy-then-delete which can corrupt the destination on crash. detect and route thru a sidecar on the sources volume when needed, so the final replace is always atomic.
        try {
            $sourceVol = [System.IO.Path]::GetPathRoot($source.full)
            $tempVol   = [System.IO.Path]::GetPathRoot($tempPath)
            if ($sourceVol.ToLowerInvariant() -eq $tempVol.ToLowerInvariant()) {
                Move-Item -LiteralPath $tempPath -Destination $source.full -Force
            } else {
                $sideTemp = Join-Path ([System.IO.Path]::GetDirectoryName($source.full)) ("_streamable_finalize_" + [Guid]::NewGuid().ToString('N') + $ext)
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
        startSec    = $startSec
        endSec      = $endSec
        durationSec = $duration
    }
}

# put the clips file path onto the windows clipboard as cf_hdrop so the user can paste the actual file into discord / explorer / etc. -- not the files bytes, the file reference (same thing ctrl+c on a file in explorer produces). requires sta threading, which is the defualt for powershell.exe on windows.
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

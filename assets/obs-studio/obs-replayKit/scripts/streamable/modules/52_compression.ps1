function Find-ToolInClipDirs([string]$exeName) {
    $candidates = New-Object System.Collections.Generic.List[string]
    $seen = New-Object System.Collections.Generic.HashSet[string] ([StringComparer]::OrdinalIgnoreCase)

    # Search order, highest priority first: the helper directory where Apply
    # installs ffmpeg/ffprobe, then the configured/default clip folders, then
    # PATH for users who manage their own ffmpeg install.
    if ($script:HelperRoot) {
        $candidate = Join-Path $script:HelperRoot $exeName
        if ($seen.Add($candidate)) { [void]$candidates.Add($candidate) }
    }
    foreach ($dir in @((Get-ClipDir), (Get-DefaultClipDir))) {
        if ([string]::IsNullOrWhiteSpace($dir)) { continue }
        $candidate = Join-Path $dir $exeName
        if ($seen.Add($candidate)) { [void]$candidates.Add($candidate) }
    }
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }
    try {
        $cmd = Get-Command $exeName -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($cmd -and $cmd.Source -and (Test-Path -LiteralPath $cmd.Source)) {
            return [System.IO.Path]::GetFullPath($cmd.Source)
        }
    } catch {}
    return ''
}

function Ensure-FfmpegAlias([string]$ffmpegPath) {
    # create / reuse a same-volume hard link from ffmpeg.exe to a branded filename ("obsreplaykit-encoder.exe") so task manager shows our naming when the process is running. hard link shares the underlying ntfs file content -- no extra disk, no stale copy to manage; an in-place ffmpeg.exe update is automatically picked up. falls back to returning the original path if the alias cant be created (cross-volume, fat/exfat, permissions, etc.). the icon and pe filedescription stay ffmpegs (we cant modify the gpld binarys resources) -- only the visible process name changes.
    if (-not $ffmpegPath -or -not (Test-Path -LiteralPath $ffmpegPath)) {
        return $ffmpegPath
    }
    try {
        $aliasDir  = [System.IO.Path]::GetDirectoryName($ffmpegPath)
        $aliasPath = Join-Path $aliasDir 'OBSReplayKit-Encoder.exe'
        if (Test-Path -LiteralPath $aliasPath) {
            return [System.IO.Path]::GetFullPath($aliasPath)
        }
        $srcVol = [System.IO.Path]::GetPathRoot($ffmpegPath)
        $dstVol = [System.IO.Path]::GetPathRoot($aliasPath)
        if ($srcVol.ToLowerInvariant() -eq $dstVol.ToLowerInvariant()) {
            New-Item -ItemType HardLink -Path $aliasPath -Value $ffmpegPath -ErrorAction Stop | Out-Null
            Write-Log "ffmpeg alias created: $aliasPath -> $ffmpegPath  (hard link)"
            return [System.IO.Path]::GetFullPath($aliasPath)
        } else {
            # cross-volume: hard links arent supported on windows. we dont want to copy ~80 mb of ffmpeg around just for a process-name cosmetic, so fall back to the original path.
            Write-Log "ffmpeg alias skipped: cross-volume (would require a full copy)"
            return $ffmpegPath
        }
    } catch {
        Write-Log "ffmpeg alias failed: $($_.Exception.Message) (falling back to ffmpeg.exe)"
        return $ffmpegPath
    }
}

function Get-HelperCapabilities([switch]$Refresh) {
    $now = [DateTime]::UtcNow
    if (-not $Refresh -and $script:State.Capabilities -and
        (($now - $script:State.CapabilitiesAt).TotalMinutes -lt 10)) {
        return $script:State.Capabilities
    }
    Load-Config
    $ffmpegRaw = Find-ToolInClipDirs 'ffmpeg.exe'
    $ffmpeg    = Ensure-FfmpegAlias $ffmpegRaw

    # detect everything the local ffmpeg can actualy drive. hw encoders are probed with a real tiny encode (compiled-in does not mean works-at-runtime -- driver / permission / silicon mismatch all show up only at first encode). sw encoders are checked against the ffmpeg encoder list, which is reliable.
    $encoders = @{
        av1_nvenc  = (Test-HwEncoderAvailable $ffmpeg 'av1_nvenc')
        hevc_nvenc = (Test-HwEncoderAvailable $ffmpeg 'hevc_nvenc')
        h264_nvenc = (Test-HwEncoderAvailable $ffmpeg 'h264_nvenc')
        hevc_amf   = (Test-HwEncoderAvailable $ffmpeg 'hevc_amf')
        h264_amf   = (Test-HwEncoderAvailable $ffmpeg 'h264_amf')
        hevc_qsv   = (Test-HwEncoderAvailable $ffmpeg 'hevc_qsv')
        h264_qsv   = (Test-HwEncoderAvailable $ffmpeg 'h264_qsv')
        libsvtav1  = (Test-SwEncoderLinked   $ffmpeg 'libsvtav1')
        libx265    = (Test-SwEncoderLinked   $ffmpeg 'libx265')
        libx264    = (Test-SwEncoderLinked   $ffmpeg 'libx264')
    }
    # pick the best fast and smaller encoders for this machine. fast is gpu-first (av1 > hevc > h264, nvidia > amd > intel) so games keep the cpu free; falls thru to libx264 veryfast if no hw encoder works at all. smaller is cpu-first by design -- sw encoders are ~2-3x more bit-efficient than hw at the same perceived quality, and the user trades wall time for file size when they pick this mode. libsvtav1 (av1) wins when present; otherwise libx265, with libx264 as a last resort if the build was stripped down.
    $fast = 'libx264'
    foreach ($cand in @('av1_nvenc','hevc_nvenc','hevc_amf','hevc_qsv','h264_nvenc','h264_amf','h264_qsv','libx264')) {
        if ($encoders[$cand]) { $fast = $cand; break }
    }
    $smaller = 'libx264'
    foreach ($cand in @('libsvtav1','libx265','libx264')) {
        if ($encoders[$cand]) { $smaller = $cand; break }
    }

    $caps = @{
        ffmpeg        = $ffmpeg
        ffprobe       = Find-ToolInClipDirs 'ffprobe.exe'
        logging       = [bool]$script:LOG_ENABLED
        compressTmp   = [string]$script:COMPRESS_TMP_DIR
        authDir       = [string]$script:AUTH_DIR
        encoders      = $encoders
        fastEncoder   = $fast
        smallerEncoder = $smaller
        cpuCount      = [Environment]::ProcessorCount
    }
    Write-Log ("capabilities: fast=$fast smaller=$smaller " +
               "av1n=$([int]$encoders.av1_nvenc) " +
               "hevcn=$([int]$encoders.hevc_nvenc) " +
               "h264n=$([int]$encoders.h264_nvenc) " +
               "hevca=$([int]$encoders.hevc_amf) " +
               "h264a=$([int]$encoders.h264_amf) " +
               "hevcq=$([int]$encoders.hevc_qsv) " +
               "h264q=$([int]$encoders.h264_qsv) " +
               "svtav1=$([int]$encoders.libsvtav1) " +
               "x265=$([int]$encoders.libx265)")
    $script:State.Capabilities = $caps
    $script:State.CapabilitiesAt = $now
    return $caps
}

# probe whether the bundled ffmpeg can actualy drive a given hardware encoder. stricter than parsing `-encoders` output: nvenc/amf/qsv can be compiled in but still fail at runtime (no driver, no gpu, mismatched silicon, wrong permissions). we encode a tiny synthetic clip to a null sink and look at the exit code. bounded by a 5 s wait so a frozen driver cant stall helper startup. returns $false on any failure -- callers fall thru to the next encoder in the priority list.
function Test-HwEncoderAvailable([string]$ffmpegPath, [string]$encoder) {
    if (-not $ffmpegPath -or -not (Test-Path -LiteralPath $ffmpegPath)) {
        return $false
    }
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $ffmpegPath
        # 256x256 clears the nvenc/amf/qsv minimum frame size across every codec (smallest minimum is av1 nvencs 128x128). yuv420p forces 8-bit to avoid 10-bit-only failures on encoders that auto-pick higher bit depths from a synthetic source.
        $psi.Arguments = ('-hide_banner -loglevel error -f lavfi ' +
                          '-i color=black:s=256x256:r=1:d=0.1 ' +
                          "-pix_fmt yuv420p -c:v $encoder -frames:v 1 -f null -")
        $psi.UseShellExecute        = $false
        $psi.CreateNoWindow         = $true
        $psi.RedirectStandardError  = $true
        $psi.RedirectStandardOutput = $true
        $proc = [System.Diagnostics.Process]::Start($psi)
        if (-not $proc.WaitForExit(5000)) {
            try { $proc.Kill() } catch {}
            return $false
        }
        return ($proc.ExitCode -eq 0)
    } catch {
        return $false
    }
}

# cheap sw-encoder availability check -- if ffmpeg lists it as a known encoder, itll run. we dont bother with a real test encode becuase libx264 / libx265 / libsvtav1 have no runtime dependencies that could make them fail per-machine the way hw encoders do.
function Test-SwEncoderLinked([string]$ffmpegPath, [string]$encoder) {
    if (-not $ffmpegPath -or -not (Test-Path -LiteralPath $ffmpegPath)) {
        return $false
    }
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $ffmpegPath
        $psi.Arguments = "-hide_banner -loglevel quiet -encoders"
        $psi.UseShellExecute        = $false
        $psi.CreateNoWindow         = $true
        $psi.RedirectStandardError  = $true
        $psi.RedirectStandardOutput = $true
        $proc = [System.Diagnostics.Process]::Start($psi)
        $out = $proc.StandardOutput.ReadToEnd()
        if (-not $proc.WaitForExit(5000)) {
            try { $proc.Kill() } catch {}
            return $false
        }
        if ($proc.ExitCode -ne 0) { return $false }
        return [bool]([regex]::IsMatch($out, "(?m)^\s*[VAS][^\s]*\s+$([regex]::Escape($encoder))\s"))
    } catch {
        return $false
    }
}

function Find-CompressionFfmpeg {
    $caps = Get-HelperCapabilities
    return [string]$caps.ffmpeg
}

function Find-CompressionFfprobe {
    $caps = Get-HelperCapabilities
    return [string]$caps.ffprobe
}

function Invoke-NativeCapture([string]$exe, [string[]]$arguments) {
    $oldEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & $exe @arguments 2>&1
        return @{
            ExitCode = $LASTEXITCODE
            Output   = @($out | ForEach-Object { [string]$_ })
        }
    } finally {
        $ErrorActionPreference = $oldEap
    }
}

function Get-VideoDurationSec([string]$ffmpeg, [string]$path) {
    try {
        $probe = Invoke-NativeCapture $ffmpeg @('-hide_banner', '-i', $path)
        $text = ($probe.Output | Out-String)
        $m = [regex]::Match($text, 'Duration:\s*(\d+):(\d{2}):(\d{2}(?:\.\d+)?)')
        if (-not $m.Success) {
            Write-Log "Get-VideoDurationSec: no Duration line from ffmpeg exit=$($probe.ExitCode) output=$(($text -replace '\s+',' ').Substring(0, [Math]::Min(220, $text.Length)))"
            return 0
        }
        $hours = [int]$m.Groups[1].Value
        $mins  = [int]$m.Groups[2].Value
        $secs  = [double]::Parse($m.Groups[3].Value, [Globalization.CultureInfo]::InvariantCulture)
        return ($hours * 3600.0) + ($mins * 60.0) + $secs
    } catch {
        Write-Log "Get-VideoDurationSec failed: $($_.Exception.Message)"
        return 0
    }
}

function Get-VideoMetadata([string]$ffprobe, [string]$ffmpeg, [string]$path) {
    if (-not [string]::IsNullOrWhiteSpace($ffprobe) -and (Test-Path -LiteralPath $ffprobe)) {
        try {
            $probe = Invoke-NativeCapture $ffprobe @(
                '-v', 'error',
                '-show_entries', 'format=duration',
                '-of', 'json',
                $path
            )
            if ($probe.ExitCode -eq 0 -and $probe.Output.Count -gt 0) {
                $json = ($probe.Output -join "`n")
                $obj = ConvertFrom-Json $json
                $duration = [double]::Parse([string]$obj.format.duration, [Globalization.CultureInfo]::InvariantCulture)
                if ($duration -gt 0) {
                    return @{ ok = $true; duration = $duration; source = 'ffprobe' }
                }
            }
        } catch {
            Write-Log "ffprobe metadata failed: $($_.Exception.Message)"
        }
    }
    $durationFallback = Get-VideoDurationSec $ffmpeg $path
    return @{ ok = ($durationFallback -gt 0); duration = $durationFallback; source = 'ffmpeg' }
}

# read the hidden compress-history marker that 42_compress_overwrite.ps1 writes into the mp4s comment metadata atom. returns a hashtable: @{ mode = fast|slow|; ts = <ms>; pre = <bytes> } empty mode = no marker found / not one of ours. cheap: ffprobe with -show_entries format_tags=comment touches only the header atoms, no decode. about 30-60ms per file on local ssd. the marker prefix is "obs-replaykit_compress_v2_" -- the v2 bump invalidates every clip that was processed by the old (broken) pipeline so it becomes eligible for re-compression under the dynamic/av1-aware pipeline. v1 markers are intentionally treated as "no marker" so those clips show as compressable again.
function Get-CompressMarker([string]$path) {
    $result = @{ mode = ''; ts = [int64]0; pre = [int64]0 }
    if ([string]::IsNullOrWhiteSpace($path) -or -not (Test-Path -LiteralPath $path)) {
        return $result
    }
    $caps = Get-HelperCapabilities
    $ffprobe = [string]$caps.ffprobe
    if ([string]::IsNullOrWhiteSpace($ffprobe) -or -not (Test-Path -LiteralPath $ffprobe)) {
        return $result
    }
    try {
        $probe = Invoke-NativeCapture $ffprobe @(
            '-v', 'error',
            '-show_entries', 'format_tags=comment',
            '-of', 'default=nokey=1:noprint_wrappers=1',
            $path
        )
        if ($probe.ExitCode -ne 0 -or $probe.Output.Count -eq 0) { return $result }
        $raw = ($probe.Output -join '').Trim()
        if (-not $raw.StartsWith('obs-replaykit_compress_v2_')) { return $result }
        $tail = $raw.Substring('obs-replaykit_compress_v2_'.Length)
        $parts = $tail -split ':', 3
        if ($parts.Count -lt 1) { return $result }
        $mode = $parts[0].Trim().ToLowerInvariant()
        if ($mode -ne 'fast' -and $mode -ne 'slow') { return $result }
        $result.mode = $mode
        if ($parts.Count -ge 2) {
            $tsVal = [int64]0
            if ([int64]::TryParse($parts[1].Trim(), [ref]$tsVal) -and $tsVal -gt 0) {
                $result.ts = $tsVal
            }
        }
        if ($parts.Count -ge 3) {
            $preVal = [int64]0
            if ([int64]::TryParse($parts[2].Trim(), [ref]$preVal) -and $preVal -gt 0) {
                $result.pre = $preVal
            }
        }
    } catch {
        Write-Log "Get-CompressMarker failed for $($path): $($_.Exception.Message)"
    }
    return $result
}

function Start-CompressedStreamableUpload($selected) {
    Load-Config
    $requestId = New-RequestId

    if (-not $selected -or -not (Test-Path -LiteralPath $selected.full)) {
        Set-UploadState @{ requestId = $requestId; state = 'error'; active = $false; error = 'Clip not found' }
        return @{ ok = $false; message = 'Clip not found' }
    }

    $decision = Get-UploadJobStartDecision $selected.name
    if (-not $decision.ok) { return $decision }

    $effCap = Get-EffectiveUploadCap
    if ($effCap -le 0) {
        return Start-StreamableUpload $selected
    }

    $caps = Get-HelperCapabilities
    $ffmpeg = [string]$caps.ffmpeg
    $ffprobe = [string]$caps.ffprobe
    if ([string]::IsNullOrWhiteSpace($ffmpeg)) {
        $msg = 'ffmpeg.exe not found in your configured clip folder.'
        Set-UploadState @{ requestId = $requestId; state = 'error'; active = $false; clipName = $selected.name; error = $msg }
        return @{ ok = $false; message = $msg }
    }

    $ps = Get-PsHelperPath
    if (-not (Test-Path -LiteralPath $ps)) {
        $msg = "Missing PowerShell helper: $ps"
        Set-UploadState @{ requestId = $requestId; state = 'error'; active = $false; clipName = $selected.name; error = $msg }
        return @{ ok = $false; message = $msg }
    }

    $metadata = Get-VideoMetadata $ffprobe $ffmpeg $selected.full
    $duration = [double]$metadata.duration
    if ($duration -lt 1) {
        $msg = 'Could not read video duration for compression.'
        Set-UploadState @{ requestId = $requestId; state = 'error'; active = $false; clipName = $selected.name; error = $msg }
        return @{ ok = $false; message = $msg }
    }

    $targetBytes = [long][Math]::Floor([double]$effCap * 0.88)
    if ($targetBytes -lt (5MB)) {
        $msg = 'Upload size limit is too small for automatic compression.'
        Set-UploadState @{ requestId = $requestId; state = 'error'; active = $false; clipName = $selected.name; error = $msg }
        return @{ ok = $false; message = $msg }
    }

    $totalKbps = [int][Math]::Floor((([double]$targetBytes * 8.0) / $duration) / 1000.0)
    $audioKbps = if ($totalKbps -lt 400) { 48 } elseif ($totalKbps -lt 900) { 64 } else { 96 }
    $videoKbps = [int]($totalKbps - $audioKbps)
    if ($videoKbps -lt 120) {
        $capMb = [int]($effCap / 1MB)
        $sizeMb = [int][Math]::Ceiling(([System.IO.FileInfo]::new($selected.full)).Length / 1MB)
        $msg = "Clip is too long to compress under $capMb MB without going below a safe video bitrate"
        Set-UploadState @{ requestId = $requestId; state = 'error'; active = $false; clipName = $selected.name; error = $msg }
        return @{ ok = $false; message = $msg; tooBig = $true; sizeMb = $sizeMb; capMb = $capMb }
    }

    $auth = Resolve-UploadAuthJar
    if (-not $auth.ok) {
        $msg = [string]$auth.message
        Set-UploadState @{ requestId = $requestId; state = 'error'; active = $false; clipName = $selected.name; error = $msg }
        return @{ ok = $false; message = $msg }
    }

    $tmpDir = $script:COMPRESS_TMP_DIR
    if (-not (Test-Path -LiteralPath $tmpDir)) {
        [void](New-Item -ItemType Directory -Path $tmpDir -Force)
    }
    $id = $requestId
    $safeBase = [System.IO.Path]::GetFileNameWithoutExtension($selected.name) -replace '[^A-Za-z0-9_.-]+', '_'
    if ([string]::IsNullOrWhiteSpace($safeBase)) { $safeBase = 'clip' }
    $tempOut = Join-Path $tmpDir ($safeBase + '_' + $id + '.mp4')
    $passLog = Join-Path $tmpDir ('ffmpeg-pass-' + $id)
    $statusPath = Get-UploadStatusPath $requestId
    $compressLogFile = if ($script:LOG_ENABLED) { $script:COMPRESS_LOG_PATH } else { '' }
    $uploadLogFile = if ($script:LOG_ENABLED) { $script:UPLOAD_LOG_PATH } else { '' }
    $authJar = if ($auth.required) { [string]$auth.path } else { '' }

    $workerScript = @'
$ErrorActionPreference = 'Stop'
$compressLogFile = $env:S_COMPRESS_LOGFILE
$uploadLogFile = $env:S_UPLOAD_LOGFILE
$statusPath = $env:S_STATUS
$requestId = $env:S_REQUEST_ID
$durationSec = [double]::Parse($env:S_DURATION_SEC, [Globalization.CultureInfo]::InvariantCulture)
function L([string]$msg) {
    $line = '[{0}] area=compress request={1} {2}' -f (Get-Date -Format 'HH:mm:ss'), $requestId, $msg
    if ($compressLogFile) {
        $dir = Split-Path -Parent $compressLogFile
        if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        Add-Content -LiteralPath $compressLogFile -Value $line -ErrorAction SilentlyContinue
    }
}
function Add-ExternalOutput($lines) {
    if (-not $compressLogFile -or -not $lines) { return }
    foreach ($line in $lines) {
        Add-Content -LiteralPath $compressLogFile -Value ([string]$line) -ErrorAction SilentlyContinue
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
        if ($c -eq '\') {
            $slashes++
        } elseif ($c -eq '"') {
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
function Run-Native([string]$exe, [string[]]$argv) {
    $oldEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & $exe @argv 2>&1
        return @{
            ExitCode = $LASTEXITCODE
            Output   = @($out | ForEach-Object { [string]$_ })
        }
    } finally {
        $ErrorActionPreference = $oldEap
    }
}
function Run-FfmpegProgress([string[]]$argv, [string]$phase, [double]$base, [double]$span) {
    $progressPath = Join-Path ([System.IO.Path]::GetDirectoryName($env:S_TEMP)) ("progress_" + $requestId + "_" + ($phase -replace '[^A-Za-z0-9]+','_') + ".txt")
    Remove-Item -LiteralPath $progressPath -ErrorAction SilentlyContinue
    $argv = @($argv + @('-progress', $progressPath, '-nostats'))
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $env:S_FFMPEG
    $psi.Arguments = (($argv | ForEach-Object { Quote-Arg $_ }) -join ' ')
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    [void]$proc.Start()
    $stdoutTask = $proc.StandardOutput.ReadToEndAsync()
    $stderrTask = $proc.StandardError.ReadToEndAsync()
    $durationUs = [Math]::Max(1.0, $durationSec * 1000000.0)
    while (-not $proc.HasExited) {
        try {
            if (Test-Path -LiteralPath $progressPath) {
                $raw = [System.IO.File]::ReadAllText($progressPath)
                $matches = [regex]::Matches($raw, 'out_time_(?:us|ms)=(\d+)')
                if ($matches.Count -gt 0) {
                    $us = [double]$matches[$matches.Count - 1].Groups[1].Value
                    $pct = [int][Math]::Floor($base + ($span * [Math]::Min(1.0, $us / $durationUs)))
                    Write-Status 'compressing' $phase $pct
                }
            }
        } catch {}
        Start-Sleep -Milliseconds 500
    }
    $proc.WaitForExit()
    $stdout = $stdoutTask.Result
    $stderr = $stderrTask.Result
    if ($stdout) { Add-ExternalOutput ($stdout -split "`r?`n") }
    if ($stderr) { Add-ExternalOutput ($stderr -split "`r?`n") }
    Remove-Item -LiteralPath $progressPath -ErrorAction SilentlyContinue
    Write-Status 'compressing' $phase ([int][Math]::Floor($base + $span))
    if ($proc.ExitCode -ne 0) { throw "$phase failed (exit=$($proc.ExitCode))" }
}
try {
    $ffmpeg = $env:S_FFMPEG
    $input  = $env:S_INPUT
    $temp   = $env:S_TEMP
    $pass   = $env:S_PASSLOG
    $cap    = [int64]$env:S_CAP_BYTES
    $vk     = [int]$env:S_VIDEO_KBPS
    $ak     = [int]$env:S_AUDIO_KBPS
    $ps     = $env:S_PSHELPER
    $auth   = $env:S_AUTHJAR
    $requireAuth = ($env:S_REQUIRE_AUTH -eq '1')
    Write-Status 'compressing' 'analyzing' 1
    L "Compressing temp copy: video=${vk}k audio=${ak}k"

    $pass1 = @('-y','-hide_banner','-i',$input,'-map','0:v:0','-c:v','libx264','-preset','veryfast','-b:v',("${vk}k"),'-pass','1','-passlogfile',$pass,'-an','-f','null','NUL')
    Run-FfmpegProgress $pass1 'compress pass 1' 2 47

    $pass2 = @('-y','-hide_banner','-i',$input,'-map','0:v:0','-map','0:a:0?','-c:v','libx264','-preset','veryfast','-b:v',("${vk}k"),'-pass','2','-passlogfile',$pass,'-c:a','aac','-b:a',("${ak}k"),'-movflags','+faststart',$temp)
    Run-FfmpegProgress $pass2 'compress pass 2' 50 44
    if (-not (Test-Path -LiteralPath $temp)) { throw 'ffmpeg did not create a compressed file' }

    $fi = [System.IO.FileInfo]::new($temp)
    if ($fi.Length -le 0) { throw 'Compressed file is empty' }
    if ($cap -gt 0 -and $fi.Length -gt $cap) {
        $mb = [int][Math]::Ceiling($fi.Length / 1MB)
        $capMb = [int]($cap / 1MB)
        throw "Compressed file is still too big: $mb MB / $capMb MB limit"
    }

    L "Uploading compressed temp copy ($([Math]::Round($fi.Length / 1MB, 2)) MB)"
    Write-Status 'uploading' 'uploading' 95
    $uploadArgs = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$ps,'-Clip',$temp,'-LogFile',$uploadLogFile,'-StatusFile',$statusPath,'-RequestId',$requestId,'-ProgressBase','95','-ProgressSpan','4')
    # loggingenabled is a [switch] on the worker -- append the flag only when on. passing a "true"/"false" string would break param binding before the workers try block ever runs.
    if ($env:S_LOGGING_ENABLED -eq '1') { $uploadArgs += '-LoggingEnabled' }
    if ($auth -and (Test-Path -LiteralPath $auth)) { $uploadArgs += @('-AuthJar',$auth) }
    if ($requireAuth) { $uploadArgs += @('-RequireAuth') }
    $uploadResult = Run-Native 'powershell.exe' $uploadArgs
    Add-ExternalOutput $uploadResult.Output
    exit $uploadResult.ExitCode
} catch {
    $msg = $_.Exception.Message
    L "ERROR: $msg"
    Write-Status 'error' 'error' 0 $msg
    exit 1
} finally {
    if ($env:S_TEMP -and (Test-Path -LiteralPath $env:S_TEMP)) {
        Remove-Item -LiteralPath $env:S_TEMP -Force -ErrorAction SilentlyContinue
    }
    if ($env:S_PASSLOG) {
        Get-ChildItem -Path ($env:S_PASSLOG + '*') -ErrorAction SilentlyContinue |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }
}
'@

    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($workerScript))
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName        = 'powershell.exe'
    $psi.Arguments       = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand $encoded"
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow  = $true
    $psi.WindowStyle     = 'Hidden'
    $psi.EnvironmentVariables['S_FFMPEG']     = $ffmpeg
    $psi.EnvironmentVariables['S_FFPROBE']    = $ffprobe
    $psi.EnvironmentVariables['S_INPUT']      = $selected.full
    $psi.EnvironmentVariables['S_TEMP']       = $tempOut
    $psi.EnvironmentVariables['S_PASSLOG']    = $passLog
    $psi.EnvironmentVariables['S_CAP_BYTES']  = [string]$effCap
    $psi.EnvironmentVariables['S_VIDEO_KBPS'] = [string]$videoKbps
    $psi.EnvironmentVariables['S_AUDIO_KBPS'] = [string]$audioKbps
    $psi.EnvironmentVariables['S_DURATION_SEC'] = ([string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0:R}', $duration))
    $psi.EnvironmentVariables['S_PSHELPER']   = $ps
    $psi.EnvironmentVariables['S_COMPRESS_LOGFILE'] = $compressLogFile
    $psi.EnvironmentVariables['S_UPLOAD_LOGFILE'] = $uploadLogFile
    $psi.EnvironmentVariables['S_LOGGING_ENABLED'] = if ($script:LOG_ENABLED) { '1' } else { '0' }
    $psi.EnvironmentVariables['S_STATUS']     = $statusPath
    $psi.EnvironmentVariables['S_REQUEST_ID'] = $requestId
    $psi.EnvironmentVariables['S_AUTHJAR']    = $authJar
    $psi.EnvironmentVariables['S_REQUIRE_AUTH'] = if ($auth.required) { '1' } else { '0' }

    Set-UploadState @{
        state     = 'compressing'
        active    = $true
        clipName  = $selected.name
        startedAt = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        url       = ''
        error     = ''
        phase     = 'analyzing'
        percent   = 1
        requestId = $requestId
        processId = 0
        statusPath = $statusPath
        tempPath  = $tempOut
        kind      = 'compress-upload'
    }

    Write-Log "Start-CompressedStreamableUpload spawning ffmpeg=$ffmpeg ffprobe=$ffprobe metadata=$($metadata.source) clip=$($selected.name) targetKbps=$totalKbps videoKbps=$videoKbps audioKbps=$audioKbps" 'compress' $requestId
    try {
        $proc = [System.Diagnostics.Process]::Start($psi)
        Set-UploadState @{ requestId = $requestId; processId = [int]$proc.Id }
    } catch {
        $msg = "Could not launch compression worker: $($_.Exception.Message)"
        Set-UploadState @{ requestId = $requestId; state = 'error'; active = $false; clipName = $selected.name; error = $msg }
        return @{ ok = $false; message = $msg }
    }

    $watch = Start-UploadResultWatcher $proc $selected.name $statusPath $requestId
    if (-not $watch.ok) {
        try { if ($proc -and -not $proc.HasExited) { $proc.Kill() } } catch {}
        return $watch
    }

    return @{ ok = $true; state = 'compressing'; clip = $selected.name; requestId = $requestId; message = 'Compressing temp copy and uploading' }
}


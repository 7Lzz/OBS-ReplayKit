param(
    [Parameter(Mandatory = $true)][string]$Ffprobe,
    [Parameter(Mandatory = $true)][string]$SourcePath,
    [Parameter(Mandatory = $true)][string]$ClipName,
    [Parameter(Mandatory = $true)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Write-Result($result) {
    $dir = Split-Path -Parent $OutputPath
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    $json = ConvertTo-Json $result -Depth 8 -Compress
    $tmp = $OutputPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    [System.IO.File]::WriteAllText($tmp, $json, (New-Object System.Text.UTF8Encoding $false))
    if (Test-Path -LiteralPath $OutputPath) {
        Remove-Item -LiteralPath $OutputPath -Force -ErrorAction SilentlyContinue
    }
    [System.IO.File]::Move($tmp, $OutputPath)
}

function Add-KeyframeTime($times, [string]$raw) {
    if ([string]::IsNullOrWhiteSpace($raw) -or $raw -eq 'N/A') { return }
    $clean = $raw.Trim().Trim('"')
    $value = [double]0
    if ([double]::TryParse($clean, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$value) -and $value -ge 0) {
        [void]$times.Add($value)
    }
}

function Complete-KeyframeTimes($times) {
    $unique = @()
    $last = [double]::NaN
    foreach ($time in ($times | Sort-Object)) {
        if ([double]::IsNaN($last) -or [Math]::Abs(([double]$time) - $last) -gt 0.001) {
            $unique += [double]$time
            $last = [double]$time
        }
    }
    if ($unique.Count -eq 0 -or [Math]::Abs([double]$unique[0]) -gt 0.001) {
        $unique = @([double]0.0) + $unique
    }
    return $unique
}

function Convert-PacketOutputToKeyframes($output) {
    $times = New-Object System.Collections.Generic.List[double]
    foreach ($line in @($output)) {
        $text = [string]$line
        if ([string]::IsNullOrWhiteSpace($text)) { continue }
        $parts = $text.Split(',')
        if ($parts.Count -lt 2) { continue }
        $flags = [string]$parts[$parts.Count - 1]
        if ($flags -notmatch 'K') { continue }
        Add-KeyframeTime $times ([string]$parts[0])
    }
    return Complete-KeyframeTimes $times
}

function Convert-FrameJsonToKeyframes([string]$json) {
    $data = ConvertFrom-Json $json
    $times = New-Object System.Collections.Generic.List[double]
    foreach ($frame in @($data.frames)) {
        $raw = ''
        if ($frame.best_effort_timestamp_time -ne $null) { $raw = [string]$frame.best_effort_timestamp_time }
        elseif ($frame.pkt_pts_time -ne $null) { $raw = [string]$frame.pkt_pts_time }
        Add-KeyframeTime $times $raw
    }
    return Complete-KeyframeTimes $times
}

function Short-Output($output) {
    $combined = (@($output) | ForEach-Object { [string]$_ }) -join "`n"
    if ($combined.Length -gt 300) { return $combined.Substring(0, 300) + '...' }
    return $combined
}

try {
    if (-not (Test-Path -LiteralPath $Ffprobe -PathType Leaf)) {
        Write-Result @{ ok = $false; message = 'ffprobe.exe not found'; keyframes = @() }
        exit 0
    }
    if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) {
        Write-Result @{ ok = $false; message = 'Source clip not found'; keyframes = @() }
        exit 0
    }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $packetOutput = & $Ffprobe @(
        '-v', 'error',
        '-select_streams', 'v:0',
        '-show_packets',
        '-show_entries', 'packet=pts_time,flags',
        '-of', 'csv=p=0',
        $SourcePath
    ) 2>&1
    $packetExitCode = $LASTEXITCODE
    $method = 'packets'
    $unique = @()

    if ($packetExitCode -eq 0) {
        $unique = Convert-PacketOutputToKeyframes $packetOutput
    }

    if ($packetExitCode -ne 0 -or $unique.Count -le 1) {
        $frameOutput = & $Ffprobe @(
        '-v', 'error',
        '-select_streams', 'v:0',
        '-skip_frame', 'nokey',
        '-show_frames',
        '-show_entries', 'frame=best_effort_timestamp_time,pkt_pts_time',
        '-of', 'json=compact=1',
        $SourcePath
        ) 2>&1
        $frameExitCode = $LASTEXITCODE
        $method = 'frames'
        if ($frameExitCode -ne 0) {
            $packetMsg = if ($packetExitCode -ne 0) { Short-Output $packetOutput } else { 'packet probe returned no keyframe packet data' }
            $frameMsg = Short-Output $frameOutput
            $sw.Stop()
            Write-Result @{ ok = $false; message = "ffprobe keyframe probe failed (packet exit=$packetExitCode, frame exit=$frameExitCode): $packetMsg $frameMsg"; keyframes = @(); probeMs = [int]$sw.ElapsedMilliseconds }
            exit 0
        }

        $json = (@($frameOutput) | ForEach-Object { [string]$_ }) -join "`n"
        if ([string]::IsNullOrWhiteSpace($json)) {
            $sw.Stop()
            Write-Result @{ ok = $false; message = 'ffprobe returned no keyframe data'; keyframes = @(); probeMs = [int]$sw.ElapsedMilliseconds }
            exit 0
        }
        $unique = Convert-FrameJsonToKeyframes $json
    }
    $sw.Stop()

    Write-Result @{
        ok = $true
        name = $ClipName
        keyframes = $unique
        count = [int]$unique.Count
        cached = $false
        method = $method
        probeMs = [int]$sw.ElapsedMilliseconds
    }
} catch {
    try {
        Write-Result @{ ok = $false; message = "Could not read keyframes: $($_.Exception.Message)"; keyframes = @() }
    } catch {}
}

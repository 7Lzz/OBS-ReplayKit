param(
    [string]$ClipDir,
    [string]$IndexPath,
    [string]$Ffprobe,
    [string]$AllowedExts = '.mp4;.mkv;.mov',
    [int]$MaxFiles = 20000
)

$ErrorActionPreference = 'Stop'
$script:Utf8NoBom = New-Object System.Text.UTF8Encoding $false

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
            [void]$sb.Append($c)
            $slashes = 0
        }
    }
    [void]$sb.Append('\' * ($slashes * 2))
    [void]$sb.Append('"')
    return $sb.ToString()
}

function Get-Value($entry, [string]$name) {
    if (-not $entry) { return $null }
    if ($entry -is [System.Collections.IDictionary]) {
        if ($entry.Contains($name)) { return $entry[$name] }
        return $null
    }
    $prop = $entry.PSObject.Properties[$name]
    if ($prop) { return $prop.Value }
    return $null
}

function Convert-RateToDouble([string]$rate) {
    if ([string]::IsNullOrWhiteSpace($rate) -or $rate -eq '0/0') { return 0.0 }
    if ($rate.Contains('/')) {
        $parts = $rate.Split('/')
        if ($parts.Count -ne 2) { return 0.0 }
        [double]$num = 0
        [double]$den = 0
        if (-not [double]::TryParse($parts[0], [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$num)) { return 0.0 }
        if (-not [double]::TryParse($parts[1], [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$den)) { return 0.0 }
        if ($den -le 0) { return 0.0 }
        return $num / $den
    }
    [double]$plain = 0
    if ([double]::TryParse($rate, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$plain)) {
        return $plain
    }
    return 0.0
}

function Read-ExistingIndex([string]$path) {
    $entries = @{}
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $entries }
    try {
        $raw = [System.IO.File]::ReadAllText($path)
        if ([string]::IsNullOrWhiteSpace($raw)) { return $entries }
        $parsed = ConvertFrom-Json $raw
        if (-not $parsed -or -not $parsed.clips) { return $entries }
        $parsed.clips.PSObject.Properties | ForEach-Object {
            if ($_.Name) { $entries[$_.Name] = $_.Value }
        }
    } catch {}
    return $entries
}

function Test-EntryCurrent($entry, [System.IO.FileInfo]$file) {
    if (-not $entry) { return $false }
    $size = Get-Value $entry 'size'
    $mtimeTicks = Get-Value $entry 'mtimeTicks'
    if ($size -eq $null -or $mtimeTicks -eq $null) { return $false }
    return ([int64]$size -eq [int64]$file.Length -and [int64]$mtimeTicks -eq [int64]$file.LastWriteTimeUtc.Ticks)
}

function Test-EntryReusable($entry, [System.IO.FileInfo]$file) {
    if (-not (Test-EntryCurrent $entry $file)) { return $false }
    $duration = Get-Value $entry 'duration'
    $codec = Get-Value $entry 'codec'
    if ($duration -ne $null -and [double]$duration -gt 0 -and -not [string]::IsNullOrWhiteSpace([string]$codec)) { return $true }
    $failedAt = Get-Value $entry 'failedAt'
    if ($failedAt -eq $null) { return $false }
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    return (($now - [int64]$failedAt) -lt 21600)
}

function Read-FfprobeMetadata([string]$path) {
    if ([string]::IsNullOrWhiteSpace($Ffprobe) -or -not (Test-Path -LiteralPath $Ffprobe -PathType Leaf)) {
        return $null
    }
    $args = @(
        '-v', 'error',
        '-select_streams', 'v:0',
        '-show_entries', 'stream=width,height,r_frame_rate,avg_frame_rate,codec_name,codec_tag_string:format=duration',
        '-of', 'json=compact=1',
        $path
    )
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Ffprobe
    $psi.Arguments = (($args | ForEach-Object { Quote-Arg ([string]$_) }) -join ' ')
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $proc = [System.Diagnostics.Process]::Start($psi)
    if (-not $proc.WaitForExit(15000)) {
        try { $proc.Kill() } catch {}
        return $null
    }
    $stdout = $proc.StandardOutput.ReadToEnd()
    if ($proc.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($stdout)) { return $null }
    try { return ConvertFrom-Json $stdout } catch { return $null }
}

function New-IndexEntry([System.IO.FileInfo]$file, $metadata) {
    $duration = 0.0
    $width = 0
    $height = 0
    $fps = 0.0
    $codec = ''
    $tag = ''
    if ($metadata) {
        try {
            if ($metadata.format -and $metadata.format.duration -ne $null) {
                $duration = [double]::Parse([string]$metadata.format.duration, [Globalization.CultureInfo]::InvariantCulture)
            }
        } catch { $duration = 0.0 }
        try {
            $stream = @($metadata.streams | Select-Object -First 1)[0]
            if ($stream) {
                if ($stream.width -ne $null) { $width = [int]$stream.width }
                if ($stream.height -ne $null) { $height = [int]$stream.height }
                $rate = if ($stream.avg_frame_rate) { [string]$stream.avg_frame_rate } else { [string]$stream.r_frame_rate }
                $fps = Convert-RateToDouble $rate
                if ($fps -le 0) { $fps = Convert-RateToDouble ([string]$stream.r_frame_rate) }
                if ($stream.codec_name) { $codec = [string]$stream.codec_name }
                if ($stream.codec_tag_string) { $tag = [string]$stream.codec_tag_string }
            }
        } catch {}
    }
    $entry = [ordered]@{
        name       = $file.Name
        size       = [int64]$file.Length
        mtime      = [int64][Math]::Floor(([DateTimeOffset]$file.LastWriteTimeUtc).ToUnixTimeSeconds())
        mtimeTicks = [int64]$file.LastWriteTimeUtc.Ticks
        duration   = [Math]::Round([Math]::Max(0.0, $duration), 3)
        width      = [int]$width
        height     = [int]$height
        fps        = [Math]::Round([Math]::Max(0.0, $fps), 3)
        codec      = $codec
        tag        = $tag
        indexedAt  = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    }
    if ($duration -le 0) {
        $entry.failedAt = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    }
    return $entry
}

function Write-IndexFile([string]$path, [hashtable]$entries) {
    $parent = [System.IO.Path]::GetDirectoryName($path)
    if ([string]::IsNullOrWhiteSpace($parent)) { throw 'Invalid index path.' }
    if (-not (Test-Path -LiteralPath $parent)) {
        [void](New-Item -ItemType Directory -Path $parent -Force)
    }
    $payload = [ordered]@{
        version   = 1
        updatedAt = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
        clips     = $entries
    }
    $json = ConvertTo-Json -InputObject $payload -Depth 8 -Compress
    # tmp lands in %temp%\ReplayKit\scratch, not next to $path, so a crash mid-write never leaves a stray .tmp sitting beside clips_index.json. self-contained worker, not dot-sourced into the module system, so $script:SCRATCH_DIR (00_state.ps1) isnt available here -- same path every other part of the app uses, just built inline.
    $scratchDir = Join-Path $env:TEMP 'ReplayKit\scratch'
    if (-not (Test-Path -LiteralPath $scratchDir)) {
        [void](New-Item -ItemType Directory -Path $scratchDir -Force)
    }
    $tmp = Join-Path $scratchDir ([System.IO.Path]::GetFileName($path) + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    [System.IO.File]::WriteAllText($tmp, $json, $script:Utf8NoBom)
    # a single Move-Item -Force is an atomic replace, unlike the delete-then-move this used to do -- that left a real crash window where a process death between the two steps lost the index entirely, not just a stray tmp. ([System.IO.File]::Replace()s null-backup-path overload throws "path is not of a legal form" on this powershell, which is what pushed the original code to delete-then-move instead -- Move-Item -Force doesnt hit that bug since its a different underlying call.)
    try {
        Move-Item -LiteralPath $tmp -Destination $path -Force -ErrorAction Stop
    } catch {
        try { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue } catch {}
        throw
    }
}

try {
    if ([string]::IsNullOrWhiteSpace($ClipDir) -or [string]::IsNullOrWhiteSpace($IndexPath)) { exit 0 }
    $root = [System.IO.Path]::GetFullPath($ClipDir)
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { exit 0 }
    $indexFull = [System.IO.Path]::GetFullPath($IndexPath)
    $allowed = New-Object System.Collections.Generic.HashSet[string] ([StringComparer]::OrdinalIgnoreCase)
    foreach ($ext in $AllowedExts.Split(';')) {
        if (-not [string]::IsNullOrWhiteSpace($ext)) { [void]$allowed.Add($ext.Trim()) }
    }
    $existing = Read-ExistingIndex $indexFull
    $entries = @{}
    $count = 0
    $files = [System.IO.DirectoryInfo]::new($root).EnumerateFiles() |
        Where-Object {
            $allowed.Contains($_.Extension) -and
            $_.Name -notlike '_replaykit_*' -and
            $_.Name -notlike '_compress_tmp_*' -and
            $_.Name -notlike '_trim_tmp_*'
        } |
        Sort-Object -Property LastWriteTimeUtc -Descending

    foreach ($file in $files) {
        if ($MaxFiles -gt 0 -and $count -ge $MaxFiles) { break }
        $count++
        $old = if ($existing.ContainsKey($file.Name)) { $existing[$file.Name] } else { $null }
        if (Test-EntryReusable $old $file) {
            $entries[$file.Name] = $old
            continue
        }
        $metadata = Read-FfprobeMetadata $file.FullName
        $entries[$file.Name] = New-IndexEntry $file $metadata
    }
    Write-IndexFile $indexFull $entries
} catch {
    try {
        $errPath = $IndexPath + '.error.txt'
        [System.IO.File]::WriteAllText($errPath, $_.Exception.Message, $script:Utf8NoBom)
    } catch {}
}

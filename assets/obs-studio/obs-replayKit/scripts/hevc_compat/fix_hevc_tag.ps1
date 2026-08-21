# fixes the classic apple hevc-in-mp4 gotcha: obs sometimes muxes hevc tagged hev1 instead of hvc1, which ios/avfoundation (and so discords iphone app) refuses to decode -- a pure, lossless container retag (ffmpeg -c copy -tag:v hvc1), never a re-encode. standalone on purpose, no dependency on the replaykit helper module chain, so this still runs if the helper is down -- auto_fix_hevc_tag.lua spawns it once per clip/recording right after obs finishes writing it, and once at startup in -SweepDirs mode to catch anything already on disk.

param(
    [string]$Path = '',
    [string]$SweepDirs = ''
)

$ErrorActionPreference = 'SilentlyContinue'
$scriptDir = $PSScriptRoot
$allowedExts = @('.mp4', '.mkv', '.mov')
$scratchDir = Join-Path $env:TEMP 'ReplayKit\scratch'
$sweepStatePath = Join-Path $scratchDir 'hevc_sweep.json'
$logPath = Join-Path $env:TEMP 'ReplayKit\logs\hevc_tag_fix.log'
# standalone script (see file header), so it cant rely on the main helpers startup directory creation -- this can be the very first ReplayKit process to touch %temp% on a given run.
foreach ($__dir in @((Split-Path -Parent $sweepStatePath), (Split-Path -Parent $logPath))) {
    if (-not (Test-Path -LiteralPath $__dir)) { try { [void](New-Item -ItemType Directory -Path $__dir -Force) } catch {} }
}

function Write-FixLog([string]$msg) {
    try {
        $dir = Split-Path -Parent $logPath
        if (-not (Test-Path -LiteralPath $dir)) { [void](New-Item -ItemType Directory -Path $dir -Force) }
        Add-Content -LiteralPath $logPath -Value ("[{0}] {1}" -f (Get-Date -Format 'o'), $msg) -ErrorAction SilentlyContinue
    } catch {}
}

function Get-ClipDirFromHelperConfig {
    # mirrors the helpers own Get-ClipDir (same json, same defualt); reading it directly here avoids dot-sourcing the whole module chain for one string.
    $default = Join-Path $env:USERPROFILE 'Pictures\Videos'
    $cfgPath = Join-Path $scriptDir '..\helper\helper_config.json'
    $cfgPath = [System.IO.Path]::GetFullPath($cfgPath)
    if (-not (Test-Path -LiteralPath $cfgPath)) { return $default }
    try {
        $cfg = Get-Content -LiteralPath $cfgPath -Raw | ConvertFrom-Json
        if ($cfg.clipDir -and (Test-Path -LiteralPath ([string]$cfg.clipDir))) {
            return [string]$cfg.clipDir
        }
    } catch {}
    return $default
}

function Get-ObsRecordingDir {
    # advanced-output RecFilePath from the active profile, if it differs from the clip dir -- usually the same folder for this product, but not guaranteed on every install.
    try {
        $profileIni = Join-Path $env:APPDATA 'obs-studio\basic\profiles\Untitled\basic.ini'
        if (-not (Test-Path -LiteralPath $profileIni)) { return $null }
        $line = Get-Content -LiteralPath $profileIni | Where-Object { $_ -match '^RecFilePath=' } | Select-Object -Last 1
        if (-not $line) { return $null }
        $val = ($line -split '=', 2)[1]
        if ($val -and (Test-Path -LiteralPath $val)) { return $val }
    } catch {}
    return $null
}

function Resolve-Tool([string]$exeName) {
    $primary = [System.IO.Path]::GetFullPath((Join-Path $scriptDir "..\helper\$exeName"))
    if (Test-Path -LiteralPath $primary) { return $primary }
    foreach ($dir in @((Get-ClipDirFromHelperConfig), (Join-Path $env:USERPROFILE 'Pictures\Videos'))) {
        if ([string]::IsNullOrWhiteSpace($dir)) { continue }
        $candidate = Join-Path $dir $exeName
        if (Test-Path -LiteralPath $candidate) { return [System.IO.Path]::GetFullPath($candidate) }
    }
    $cmd = Get-Command $exeName -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmd -and $cmd.Source -and (Test-Path -LiteralPath $cmd.Source)) { return $cmd.Source }
    return $null
}

$script:Ffmpeg  = Resolve-Tool 'ffmpeg.exe'
$script:Ffprobe = Resolve-Tool 'ffprobe.exe'

function Get-VideoTag([string]$file) {
    if (-not $script:Ffprobe) { return $null }
    $out = & $script:Ffprobe -v error -select_streams v:0 -show_entries stream=codec_name,codec_tag_string -of csv=p=0 -- $file 2>$null
    if (-not $out) { return $null }
    $parts = ([string]$out).Trim().Split(',')
    if ($parts.Length -lt 2) { return $null }
    return @{ codec = $parts[0]; tag = $parts[1] }
}

# checks whether obs still has the file open instead of guessing from a timestamp -- FileShare.None makes our own open attempt fail immediately while any other handle is still outstanding, and polling in short bursts resolves the common case fast while still giving a slow finalize as long as it needs, up to the cap.
function Wait-FileUnlocked([string]$file, [int]$maxWaitMs = 30000, [int]$pollMs = 250) {
    $waited = 0
    while ($waited -le $maxWaitMs) {
        try {
            $stream = [System.IO.File]::Open($file, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
            $stream.Close()
            return $true
        } catch {
            Start-Sleep -Milliseconds $pollMs
            $waited += $pollMs
        }
    }
    return $false
}

# the one thing that actually matters: rewrite hev1 -> hvc1 without touching a single encoded byte.
function Repair-OneFile([string]$file) {
    if (-not (Test-Path -LiteralPath $file)) { return }
    $ext = [System.IO.Path]::GetExtension($file).ToLowerInvariant()
    if ($allowedExts -notcontains $ext) { return }
    if (-not $script:Ffmpeg -or -not $script:Ffprobe) { return }

    if (-not (Wait-FileUnlocked $file)) {
        Write-FixLog "gave up waiting for exclusive access, still locked: $file"
        return
    }

    $info = Get-VideoTag $file
    if (-not $info -or $info.codec -ne 'hevc' -or $info.tag -eq 'hvc1') { return }

    # same reasoning as the compress/trim workers: this rewrites the container not the clip, so the original timestamps get restored after the swap instead of jumping to "now" and bumping an old clip to the top of the sorted-by-newest list.
    $origLastWriteUtc = $null
    $origCreationUtc  = $null
    try {
        $srcInfo = [System.IO.FileInfo]::new($file)
        $origLastWriteUtc = $srcInfo.LastWriteTimeUtc
        $origCreationUtc  = $srcInfo.CreationTimeUtc
    } catch {}

    # remuxed into scratch, not next to $file, so a crash mid-remux never leaves a stray tmp with a real video extension sitting in the clip folder where it could get indexed and shown as a bogus clip.
    if (-not (Test-Path -LiteralPath $scratchDir)) { try { [void](New-Item -ItemType Directory -Path $scratchDir -Force) } catch {} }
    $tmp = Join-Path $scratchDir ([System.IO.Path]::GetFileName($file) + '.hvc1fix.' + [Guid]::NewGuid().ToString('N') + $ext)
    try { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue } catch {}

    & $script:Ffmpeg -y -hide_banner -loglevel error -i $file -c copy -tag:v hvc1 -movflags +faststart -- $tmp 2>$null
    $ok = $LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $tmp)
    if ($ok) {
        # sanity check before committing: a truncated remux is worse than a hev1-tagged original.
        try {
            $srcLen = (Get-Item -LiteralPath $file).Length
            $tmpLen = (Get-Item -LiteralPath $tmp).Length
            if ($tmpLen -lt ($srcLen * 0.9)) { $ok = $false }
        } catch { $ok = $false }
    }
    if ($ok) {
        # a single Move-Item -Force (same-volume) or copy-to-sidecar-then-move (cross-volume, since $tmp now lives in scratch, not next to $file) is an atomic replace -- unlike the delete-then-move this used to do, theres no window where a crash leaves the original deleted with nothing in its place. ([System.IO.File]::Replace()s null/empty backup-path overload throws "path is not of a legal form" on this powershell, which is what pushed the original code to delete-then-move instead -- Move-Item -Force doesnt hit that bug since its a different underlying call.)
        try {
            $fileVol = [System.IO.Path]::GetPathRoot($file)
            $tmpVol  = [System.IO.Path]::GetPathRoot($tmp)
            if ($fileVol.ToLowerInvariant() -eq $tmpVol.ToLowerInvariant()) {
                Move-Item -LiteralPath $tmp -Destination $file -Force -ErrorAction Stop
            } else {
                $sideTemp = Join-Path ([System.IO.Path]::GetDirectoryName($file)) ("_replaykit_finalize_" + [Guid]::NewGuid().ToString('N') + $ext)
                Copy-Item -LiteralPath $tmp -Destination $sideTemp -Force -ErrorAction Stop
                Move-Item -LiteralPath $sideTemp -Destination $file -Force -ErrorAction Stop
                Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
            }
            if ($origLastWriteUtc) {
                try {
                    [System.IO.File]::SetLastWriteTimeUtc($file, $origLastWriteUtc)
                    if ($origCreationUtc) { [System.IO.File]::SetCreationTimeUtc($file, $origCreationUtc) }
                } catch {
                    Write-FixLog "timestamp restore failed for $file : $($_.Exception.Message)"
                }
            }
            Write-FixLog "fixed hev1 -> hvc1: $file"
        } catch {
            Write-FixLog "replace failed for $file : $($_.Exception.Message)"
            try { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue } catch {}
        }
    } else {
        Write-FixLog "remux failed, leaving original untouched: $file"
        try { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue } catch {}
    }
}

function Repair-WithRetry([string]$file) {
    for ($i = 0; $i -lt 3; $i++) {
        if (-not (Test-Path -LiteralPath $file)) { Start-Sleep -Milliseconds 500; continue }
        Repair-OneFile $file
        return
    }
}

function Invoke-Sweep([string[]]$dirs) {
    $watermark = [DateTime]::MinValue
    if (Test-Path -LiteralPath $sweepStatePath) {
        try {
            $state = Get-Content -LiteralPath $sweepStatePath -Raw | ConvertFrom-Json
            if ($state.lastSweepUtc) { $watermark = [DateTime]::Parse($state.lastSweepUtc).ToUniversalTime() }
        } catch {}
    }

    $seenDirs = New-Object System.Collections.Generic.HashSet[string] ([StringComparer]::OrdinalIgnoreCase)
    $sweepStart = [DateTime]::UtcNow
    foreach ($dir in $dirs) {
        if ([string]::IsNullOrWhiteSpace($dir)) { continue }
        $full = [System.IO.Path]::GetFullPath($dir)
        if (-not $seenDirs.Add($full)) { continue }
        if (-not (Test-Path -LiteralPath $full)) { continue }
        foreach ($ext in $allowedExts) {
            Get-ChildItem -LiteralPath $full -Filter "*$ext" -File -ErrorAction SilentlyContinue | ForEach-Object {
                if ($_.LastWriteTimeUtc -gt $watermark) {
                    Repair-OneFile $_.FullName
                }
            }
        }
    }

    try {
        @{ lastSweepUtc = $sweepStart.ToString('o') } | ConvertTo-Json | Set-Content -LiteralPath $sweepStatePath -Encoding utf8
    } catch {}
}

if ($Path) {
    Repair-WithRetry $Path
} elseif ($SweepDirs) {
    $dirs = $SweepDirs -split ';' | Where-Object { $_ }
    Invoke-Sweep $dirs
} else {
    $clipDir = Get-ClipDirFromHelperConfig
    $recDir  = Get-ObsRecordingDir
    Invoke-Sweep @($clipDir, $recDir)
}

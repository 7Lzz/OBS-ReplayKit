# tells the build whether a newer ffmpeg release is out, the way pip tells about a newer pip: the version in downloads/ffmpeg-tools.zip against the latest gyan.dev essentials release. build.ps1 runs it last, and it only reports and never fails the build, since the bundled ffmpeg is pinned by hash and is changed by hand
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$latestUrl = 'https://www.gyan.dev/ffmpeg/builds/release-version'

function Write-UpdateWarning([string]$text) {
    Write-Host 'WARNING: ' -ForegroundColor Yellow -NoNewline
    Write-Host $text -ForegroundColor Yellow
}

# the gyan.dev zips hold one folder named ffmpeg-<version>-essentials_build, which is the only place the archive says its version
function Get-ZipVersion([string]$path) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($path)
    try {
        foreach ($entry in $zip.Entries) {
            $m = [regex]::Match($entry.FullName, '^ffmpeg-([0-9][0-9.]*)-')
            if ($m.Success) { return $m.Groups[1].Value.TrimEnd('.') }
        }
        return $null
    } finally { $zip.Dispose() }
}

function Compare-Version([string]$a, [string]$b) {
    $va = $null; $vb = $null
    if ([version]::TryParse(($a + '.0').Split('.')[0..2] -join '.', [ref]$va) -and [version]::TryParse(($b + '.0').Split('.')[0..2] -join '.', [ref]$vb)) { return $va.CompareTo($vb) }
    return [string]::CompareOrdinal($a, $b)
}

try {
    $repoZip = Join-Path $repoRoot 'downloads\ffmpeg-tools.zip'
    $current = if (Test-Path -LiteralPath $repoZip) { Get-ZipVersion $repoZip } else { $null }
    if (-not $current) { Write-UpdateWarning "Could not read the ffmpeg version of downloads\ffmpeg-tools.zip."; return }

    $latest = $null
    try {
        $answer = Invoke-WebRequest -UseBasicParsing -Uri $latestUrl -TimeoutSec 6
        $text = if ($answer.Content -is [byte[]]) { [Text.Encoding]::ASCII.GetString($answer.Content) } else { [string]$answer.Content }
        $text = $text.Trim()
        if ($text -match '^[0-9]+(\.[0-9]+){0,3}$') { $latest = $text }
    } catch { }

    if (-not $latest) {
        Write-UpdateWarning "Could not reach $latestUrl to check for a newer ffmpeg (repo has $current)."
    } elseif ((Compare-Version $latest $current) -gt 0) {
        Write-UpdateWarning "A new release of ffmpeg is available: $current > $latest"
        Write-UpdateWarning "To update, replace ReplayKit\downloads\ffmpeg-tools.zip with the gyan.dev ffmpeg-$latest-essentials_build zip and update FfmpegSha256 and FfprobeSha256 in shared\VideoTools.cs."
    } else {
        Write-Host "ffmpeg $current is the latest release." -ForegroundColor Green
    }
} catch {
    Write-UpdateWarning "The ffmpeg update check failed ($($_.Exception.Message))."
}

param([string]$Version = '', [string]$WorkDir = (Join-Path $env:LOCALAPPDATA 'ReplayKit/host-build/verification-roles'))
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../obs_versions.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$lines = @((Read-ObsVersions).lines)
if ($Version) {
    $lines = @($lines | Where-Object tested -eq $Version)
    if ($lines.Count -ne 1) { throw 'The requested host version is not supported.' }
}
foreach ($line in $lines) {
    $null = Get-ObsBinaries $line
    $version = $line.tested
    $archive = Join-Path $ObsVersionsCache ([IO.Path]::GetFileName($line.portable.url))
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $line.portable.sha256) {
        throw 'The cached official OBS archive changed.'
    }
    $root = Join-Path $WorkDir $version
    $marker = Join-Path $root '.replaykit-portable'
    if (-not (Test-Path -LiteralPath $marker) -or (Get-Content -LiteralPath $marker -Raw).Trim() -ne $line.portable.sha256) {
        Remove-WorkDirectory $WorkDir $root
        New-Item -ItemType Directory -Force -Path $root | Out-Null
        [IO.Compression.ZipFile]::ExtractToDirectory($archive, $root)
        Set-Content -LiteralPath $marker -Value $line.portable.sha256 -Encoding ascii
    }
    $components = Join-Path $PSScriptRoot "../../assets/process-host-roles/$version"
    $stock = @{}
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach ($relative in @('bin/64bit/obs64.exe', 'bin/64bit/obs-frontend-api.dll')) {
            $entry = $zip.GetEntry($relative)
            if (-not $entry) { throw "The official OBS archive has no $relative." }
            $stream = $entry.Open()
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $stock[$relative] = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '') }
            finally { $sha.Dispose(); $stream.Dispose() }
            if ((Get-FileHash -LiteralPath (Join-Path $root $relative) -Algorithm SHA256).Hash -ne $stock[$relative]) {
                throw "The verification copy is not the official stock OBS file: $relative"
            }
        }
    } finally { $zip.Dispose() }
    $manifest = Get-Content -LiteralPath (Join-Path $components 'manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.ObsVersion -ne $version) { throw 'The host manifest has the wrong OBS version.' }
    $browser = if ($line.pluginLayout -eq 'modern') { 'core/obs-browser' } else { 'obs-plugins/64bit' }
    $expected = @('bin/64bit/replaykit-host/obs64.exe', "$browser/obs-browser.dll", "$browser/obs-browser-role.dll")
    if (@($manifest.Files).Count -ne $expected.Count) { throw 'The host manifest has an unexpected file count.' }
    foreach ($relative in $expected) {
        $entries = @($manifest.Files | Where-Object Path -eq $relative)
        $source = Join-Path $components $relative
        if ($entries.Count -ne 1 -or (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $entries[0].Sha256) {
            throw "Invalid host component: $relative"
        }
        $target = Join-Path $root $relative
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Copy-Item -LiteralPath $source -Destination $target -Force
    }
    Write-Host "Verifying OBS $version process host against its official portable installation..."
    & (Join-Path $PSScriptRoot 'verify.ps1') -Prototype $root
    foreach ($relative in $stock.Keys) {
        if ((Get-FileHash -LiteralPath (Join-Path $root $relative) -Algorithm SHA256).Hash -ne $stock[$relative]) {
            throw 'The stock OBS executable or frontend API changed.'
        }
    }
    $roleVersion = (Get-Item -LiteralPath (Join-Path $root 'bin/64bit/replaykit-host/obs64.exe')).VersionInfo.FileVersion
    if ($roleVersion -notmatch ('^' + [regex]::Escape($version) + '(?:\.\d+)?$')) { throw 'The role host has the wrong version metadata.' }
    Write-Host 'PASS: exact-version role metadata and official stock OBS/frontend hashes'
}

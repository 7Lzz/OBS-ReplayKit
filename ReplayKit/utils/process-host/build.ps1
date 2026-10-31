param(
    [string]$Version = '',
    [string]$WorkDir = (Join-Path $env:LOCALAPPDATA 'ReplayKit/host-build'),
    [string]$OutDir = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../obs_versions.ps1')
if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot '../../assets/process-host-roles' }
$lines = @((Read-ObsVersions).lines)
if ($Version) {
    $lines = @($lines | Where-Object tested -eq $Version)
    if ($lines.Count -ne 1) { throw 'The requested host version is not in obs-versions.json.' }
}
$inputs = @(Get-ChildItem -LiteralPath $PSScriptRoot -File | Where-Object { $_.Extension -in '.cpp', '.cmake', '.manifest', '.ps1' } | Sort-Object Name)
$fingerprint = ($inputs | ForEach-Object { $_.Name + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }) -join '|'
foreach ($line in $lines) {
    $version = $line.tested
    $output = Join-Path $OutDir $version
    $manifestPath = Join-Path $output 'manifest.json'
    $browserDirectory = if ($line.pluginLayout -eq 'modern') { 'core/obs-browser' } else { 'obs-plugins/64bit' }
    $files = [ordered]@{
        'bin/64bit/replaykit-host/obs64.exe' = 'host/RelWithDebInfo/obs64.exe'
    }
    $files["$browserDirectory/obs-browser.dll"] = 'plugins/obs-browser/RelWithDebInfo/obs-browser.dll'
    $files["$browserDirectory/obs-browser-role.dll"] = 'plugins/obs-browser/RelWithDebInfo/obs-browser-role.dll'
    if (Test-Path -LiteralPath $manifestPath) {
        $cached = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        $valid = $cached.ObsVersion -eq $version -and $cached.Inputs -eq $fingerprint -and @($cached.Files).Count -eq $files.Count
        foreach ($relative in $files.Keys) {
            $entries = @($cached.Files | Where-Object Path -eq $relative)
            $target = Join-Path $output $relative
            if ($entries.Count -ne 1 -or -not (Test-Path -LiteralPath $target) -or
                (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $entries[0].Sha256) { $valid = $false; break }
        }
        if ($valid) { Write-Host "OBS $version process host is current."; continue }
    }
    $source = Join-Path $WorkDir ('obs-src-' + $version)
    New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
    if (-not (Test-Path -LiteralPath (Join-Path $source '.git'))) {
        git clone --depth 1 --branch $version --filter=blob:none https://github.com/obsproject/obs-studio.git $source
        if ($LASTEXITCODE -ne 0) { throw 'OBS source checkout failed.' }
    }
    $revision = git -C $source rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'OBS source revision is unavailable.' }
    $expected = git -C $source rev-parse ('refs/tags/' + $version + '^{}')
    if ($LASTEXITCODE -ne 0 -or $revision -ne $expected) { throw 'The source checkout does not match the selected OBS release.' }
    git -C $source submodule update --init --depth 1 plugins/obs-browser plugins/obs-websocket
    if ($LASTEXITCODE -ne 0) { throw 'OBS component checkout failed.' }
    & (Join-Path $PSScriptRoot 'prepare.ps1') -Source $source
    $build = Join-Path $WorkDir ('build-' + $version)
    $legacyBuild = Join-Path $WorkDir 'build'
    if ($version -eq '32.2.2' -and (Test-Path -LiteralPath (Join-Path $legacyBuild 'CMakeCache.txt'))) { $build = $legacyBuild }
    $modern = if ($line.pluginLayout -eq 'modern') { 'ON' } else { 'OFF' }
    $configure = @('-S', $source, '-B', $build, '-G', 'Visual Studio 18 2026', '-A', 'x64,version=10.0.26100.0',
        '-DENABLE_BROWSER=ON', '-DENABLE_WEBSOCKET=OFF', '-DENABLE_SCRIPTING=ON', '-DCMAKE_COMPILE_WARNING_AS_ERROR=OFF',
        ("-DREPLAYKIT_MODERN_PLUGINS=$modern"), ('-DREPLAYKIT_HOST_DIR=' + $PSScriptRoot.Replace('\','/')))
    & cmake @configure
    if ($LASTEXITCODE -ne 0) { throw "OBS $version host configuration failed." }
    cmake --build $build --config RelWithDebInfo --target replaykit-host --parallel 8
    if ($LASTEXITCODE -ne 0) { throw "OBS $version host build failed." }
    $null = Get-ObsBinaries $line
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archivePath = Join-Path $ObsVersionsCache ([IO.Path]::GetFileName($line.portable.url))
    if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $line.portable.sha256) {
        throw 'The cached official OBS archive changed.'
    }
    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    $originals = @{}
    try {
        foreach ($relative in @("$browserDirectory/obs-browser.dll")) {
            $entry = $archive.GetEntry($relative)
            if (-not $entry) { throw "The official OBS archive has no $relative." }
            $stream = $entry.Open()
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $originals[$relative] = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '') }
            finally { $sha.Dispose(); $stream.Dispose() }
        }
    } finally { $archive.Dispose() }
    $manifest = @()
    foreach ($relative in $files.Keys) {
        $target = Join-Path $output $relative
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Copy-Item -LiteralPath (Join-Path $build $files[$relative]) -Destination $target -Force
        $manifest += [pscustomobject]@{Path=$relative; Sha256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash; OriginalSha256=$originals[$relative]}
    }
    $browserRevision = git -C (Join-Path $source 'plugins/obs-browser') rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'OBS browser revision is unavailable.' }
    [IO.File]::WriteAllText($manifestPath, ([pscustomobject]@{
        ObsVersion=$version; ObsRevision=$revision; BrowserRevision=$browserRevision; Inputs=$fingerprint; Files=$manifest
    } | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
    Write-Host "Built the OBS $version shared host components: $output"
}

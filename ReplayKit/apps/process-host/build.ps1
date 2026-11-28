param(
    [string]$Version = '',
    [string]$WorkDir = '',
    [string]$OutDir = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../scripts/obs_versions.ps1')
& (Join-Path $PSScriptRoot '..\..\scripts\sync_shared.ps1')
if (-not $WorkDir) { $WorkDir = Join-Path $ReplayKitNative 'process-host' }
if (-not $OutDir) { $OutDir = Join-Path $ReplayKitPayload 'process-host' }
Assert-BuildPath $WorkDir
Assert-BuildPath $OutDir
$lines = @((Read-ObsVersions).lines)
if ($Version) {
    $lines = @($lines | Where-Object tested -eq $Version)
    if ($lines.Count -ne 1) { throw 'The requested host version is not in obs-versions.json.' }
}
# release like the official obs builds, since obs adds /LTCG /OPT:REF /OPT:ICF only to release while relwithdebinfo links incrementally and keeps every unused function, which made obs-browser.dll three times the official one
$config = 'Release'
$inputs = @(Get-ChildItem -LiteralPath $PSScriptRoot -File | Where-Object { $_.Extension -in '.cpp', '.hpp', '.cmake', '.manifest', '.ps1' } | Sort-Object Name)
$inputs += Get-Item -LiteralPath (Join-Path $ReplayKitGenerated 'replaykit_folder.hpp'), (Join-Path $ReplayKitGenerated 'obs_audio_group.hpp'), $ObsVersionsManifest
$fingerprint = ($inputs | ForEach-Object { $_.Name + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }) -join '|'
foreach ($line in $lines) {
    $version = $line.tested
    $output = Join-Path $OutDir $version
    $manifestPath = Join-Path $output 'manifest.json'
    $hostPath, $browserPath, $rolePath = Get-HostComponentPaths $line
    $files = [ordered]@{
        $hostPath = "host/$config/obs64.exe"
        $browserPath = "plugins/obs-browser/$config/obs-browser.dll"
        $rolePath = "plugins/obs-browser/$config/obs-browser-role.dll"
    }
    if (Test-Path -LiteralPath $manifestPath) {
        $cached = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        $valid = $cached.ObsVersion -eq $version -and $cached.Inputs -eq $fingerprint -and @($cached.Files).Count -eq $files.Count
        foreach ($relative in $files.Keys) {
            $entries = @($cached.Files | Where-Object Path -eq $relative)
            $target = Join-Path $output (Split-Path -Leaf $relative)
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
    # prepare.ps1 skips a browser source an older prepare.ps1 already patched, so the browser starts from its release files on every build
    git -C (Join-Path $source 'plugins/obs-browser') checkout -- .
    if ($LASTEXITCODE -ne 0) { throw 'OBS browser source reset failed.' }
    & (Join-Path $PSScriptRoot 'prepare.ps1') -Source $source
    $build = Join-Path $WorkDir ('build-' + $version)
    $modern = if ($line.pluginLayout -eq 'modern') { 'ON' } else { 'OFF' }
    $configure = @('-S', $source, '-B', $build, '-G', 'Visual Studio 18 2026', '-A', 'x64,version=10.0.26100.0',
        '-DENABLE_BROWSER=ON', '-DENABLE_WEBSOCKET=OFF', '-DENABLE_SCRIPTING=ON', '-DCMAKE_COMPILE_WARNING_AS_ERROR=OFF',
        ("-DREPLAYKIT_MODERN_PLUGINS=$modern"), ("-DREPLAYKIT_HOST_DIR=" + $PSScriptRoot.Replace('\','/')),
        ("-DREPLAYKIT_GENERATED_DIR=" + $ReplayKitGenerated.Replace('\','/')))
    & cmake @configure
    if ($LASTEXITCODE -ne 0) { throw "OBS $version host configuration failed." }
    cmake --build $build --config $config --target replaykit-host --parallel 8
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
        $entry = $archive.GetEntry($browserPath)
        if (-not $entry) { throw "The official OBS archive has no $browserPath." }
        $stream = $entry.Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $originals[$browserPath] = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '') }
        finally { $sha.Dispose(); $stream.Dispose() }
    } finally { $archive.Dispose() }
    $manifest = @()
    foreach ($relative in $files.Keys) {
        # stored flat under the version; the manifest keeps where each file installs, which is what the installer reads
        $target = Join-Path $output (Split-Path -Leaf $relative)
        New-Item -ItemType Directory -Force -Path $output | Out-Null
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

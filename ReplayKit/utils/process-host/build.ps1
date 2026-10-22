param(
    [string]$ObsRoot = 'C:/Program Files/obs-studio',
    [string]$WorkDir = (Join-Path $env:LOCALAPPDATA 'ReplayKit/host-build'),
    [string]$OutDir = ''
)
$ErrorActionPreference = 'Stop'
if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot '../../../build/process-host/components' }
$obsExe = Join-Path $ObsRoot 'bin/64bit/obs64.exe'
$version = (Get-Item -LiteralPath $obsExe).VersionInfo.FileVersion
if ($version -notmatch '^(\d+\.\d+\.\d+)(?:\.\d+)?$') { throw "Unsupported OBS version: $version" }
$version = $Matches[1]
$source = Join-Path $WorkDir ('obs-src-' + $version)
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
if (-not (Test-Path -LiteralPath (Join-Path $source '.git'))) {
    git clone --depth 1 --branch $version --filter=blob:none https://github.com/obsproject/obs-studio.git $source
    if ($LASTEXITCODE -ne 0) { throw 'OBS source checkout failed.' }
}
$revision = git -C $source rev-parse HEAD
$expected = git -C $source rev-parse ('refs/tags/' + $version + '^{}')
if ($LASTEXITCODE -ne 0 -or $revision -ne $expected) { throw 'The source checkout does not match the installed OBS release.' }
git -C $source submodule update --init --depth 1 plugins/obs-browser plugins/obs-websocket
if ($LASTEXITCODE -ne 0) { throw 'OBS component checkout failed.' }
& (Join-Path $PSScriptRoot 'prepare.ps1') -Source $source
$build = Join-Path $WorkDir 'build'
cmake -S $source -B $build -G 'Visual Studio 18 2026' -A 'x64,version=10.0.26100.0' `
    '-DENABLE_BROWSER=ON' '-DENABLE_WEBSOCKET=OFF' '-DENABLE_SCRIPTING=OFF' '-DCMAKE_COMPILE_WARNING_AS_ERROR=OFF' `
    ('-DREPLAYKIT_HOST_DIR=' + $PSScriptRoot.Replace('\','/'))
if ($LASTEXITCODE -ne 0) { throw 'OBS host configuration failed.' }
cmake --build $build --config RelWithDebInfo --target replaykit-host --parallel 8
if ($LASTEXITCODE -ne 0) { throw 'OBS host build failed.' }
$files = @{
    'bin/64bit/obs64.exe' = 'host/RelWithDebInfo/obs64.exe'
    'bin/64bit/obs-frontend-host.dll' = 'frontend/RelWithDebInfo/obs-frontend-host.dll'
    'obs-plugins/64bit/obs-browser.dll' = 'plugins/obs-browser/RelWithDebInfo/obs-browser.dll'
    'obs-plugins/64bit/obs-browser-role.dll' = 'plugins/obs-browser/RelWithDebInfo/obs-browser-role.dll'
}
$manifest = @()
foreach ($relative in $files.Keys) {
    $target = Join-Path $OutDir $relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Copy-Item -LiteralPath (Join-Path $build $files[$relative]) -Destination $target -Force
    $manifest += [pscustomobject]@{Path=$relative; Sha256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash}
}
$browserRevision = git -C (Join-Path $source 'plugins/obs-browser') rev-parse HEAD
[IO.File]::WriteAllText((Join-Path $OutDir 'manifest.json'), ([pscustomobject]@{
    ObsVersion=$version; ObsRevision=$revision; BrowserRevision=$browserRevision; Files=$manifest
} | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
Write-Output "Built the shared host components: $OutDir"

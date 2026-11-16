# builds ReplayKitShare.exe, the share window that outlives obs, with cmake/ninja from the vs build tools into dist/share-window/; it needs no obs sdk, and the build is skipped when the exe is newer than every input
param(
    [string]$WorkDir = (Join-Path $env:LOCALAPPDATA "ReplayKit\share-window-build"),
    [string]$OutDir = ''
)
$ErrorActionPreference = "Stop"
$ProgressPreference = 'SilentlyContinue'
. (Join-Path $PSScriptRoot '..\..\scripts\obs_versions.ps1')
# writes protocol/share_window_values.hpp, the names the window shares with the plugin and the helper
& (Join-Path $PSScriptRoot '..\..\scripts\sync_shared.ps1')

if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot '..\..\dist\share-window' }
$outExe = Join-Path $OutDir 'ReplayKitShare.exe'
$iconDir = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\resources\icons'))
$versionFile = Join-Path $PSScriptRoot '..\..\version.json'
$version = (Get-Content -LiteralPath $versionFile -Raw | ConvertFrom-Json).version
if ($version -notmatch '^\d+(\.\d+){0,3}$') { throw "Unexpected ReplayKit version: '$version'" }

# the version is stamped when the window is built but is no input, since an update ends a running window whose exe changed and a release that leaves the window alone should keep the stream
$inputs = @(Get-ChildItem -LiteralPath $PSScriptRoot -Recurse -File | ForEach-Object { $_.FullName }) + @(
    (Join-Path $iconDir 'replaykit.ico'),
    (Join-Path $PSScriptRoot '..\..\scripts\obs_versions.ps1')
)
if (Test-Path -LiteralPath $outExe -PathType Leaf) {
    $built = (Get-Item -LiteralPath $outExe).LastWriteTimeUtc
    $stale = $inputs | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) -or (Get-Item -LiteralPath $_).LastWriteTimeUtc -gt $built }
    if (-not $stale) { Write-Output "Current: $outExe"; return }
}

Enter-VsBuildEnvironment
$buildDir = Join-Path $WorkDir 'build'
Remove-WorkDirectory $WorkDir $buildDir
New-Item -ItemType Directory -Force -Path $buildDir | Out-Null
Push-Location $buildDir
try {
    cmake -G Ninja "-DCMAKE_BUILD_TYPE=Release" "-DREPLAYKIT_VERSION=$version" "-DREPLAYKIT_ICON_DIR=$iconDir" "$PSScriptRoot"
    if ($LASTEXITCODE -ne 0) { throw "share window cmake configure failed" }
    cmake --build . --config Release
    if ($LASTEXITCODE -ne 0) { throw "share window build failed" }
} finally {
    Pop-Location
}

$builtExe = Join-Path $buildDir 'ReplayKitShare.exe'
if (-not (Test-Path -LiteralPath $builtExe -PathType Leaf)) { throw "share window build left no exe at $builtExe" }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Copy-Item -LiteralPath $builtExe -Destination $outExe -Force
Write-Output "bundled -> $outExe"

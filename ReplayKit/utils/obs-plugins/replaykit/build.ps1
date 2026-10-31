# builds replaykit.dll (from the sources under src/) against the oldest obs line of assets/obs-versions.json (no bundled sdk) through Get-ObsSdk in utils/obs_versions.ps1 -- libobs/frontend/api headers at that obs-studio tag, the obs-deps qt6 package it was built with and import libs made from its own dlls -- and builds with cmake/ninja from vs2022/2019 build tools. the dll lands in the bundled assets, and -InstallAfterBuild also copies it into C:\ProgramData\obs-studio\plugins\replaykit (obs must be closed, and replacing an installed dll there needs admin).

param(
    # kept out of %TEMP% because the cached qt sdk there kept losing every file the last build had not read
    [string]$WorkDir = (Join-Path $env:LOCALAPPDATA "ReplayKit\plugin-build"),
    # where the built dll ends up; defualt lands it in the bundled assets, same as build_helper.ps1s $outPath does for the helper exe.
    [string]$OutPath = '',
    [switch]$InstallAfterBuild
)
$ErrorActionPreference = "Stop"
$ProgressPreference = 'SilentlyContinue'
. (Join-Path $PSScriptRoot '..\..\obs_versions.ps1')

if (-not $OutPath) {
    $OutPath = Join-Path $PSScriptRoot '..\..\..\assets\obs-plugins\replaykit\bin\64bit\replaykit.dll'
}

# the installer puts the dll at the root of the plugin folder for obs 33 and in bin\64bit for older obs, and obs 33 loads the root one first, so a fresh build goes wherever the installed one is
function Install-BuiltPlugin([string]$builtDll) {
    $root = "C:\ProgramData\obs-studio\plugins\replaykit"
    $dest = if (Test-Path -LiteralPath (Join-Path $root "replaykit.dll")) { Join-Path $root "replaykit.dll" } else { Join-Path $root "bin\64bit\replaykit.dll" }
    New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
    Copy-Item -Path $builtDll -Destination $dest -Force
    Write-Output "installed -> $dest (restart OBS to load it)"
}

# same shape as build_helper.ps1s Test-HelperOutputCurrent -- true only when $targetPath exists and is newer than every input.
function Test-PluginOutputCurrent([string]$targetPath, [string[]]$inputPaths) {
    if (-not (Test-Path -LiteralPath $targetPath -PathType Leaf)) { return $false }
    $target = Get-Item -LiteralPath $targetPath
    foreach ($inputPath in $inputPaths) {
        if ([string]::IsNullOrWhiteSpace($inputPath)) { continue }
        if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf)) { return $false }
        $inputItem = Get-Item -LiteralPath $inputPath
        if ($inputItem.LastWriteTimeUtc -gt $target.LastWriteTimeUtc) { return $false }
    }
    return $true
}

# skip the whole rebuild (qt6 download, header checkout, import-lib generation, cmake/ninja) when the bundled dll is already newer than every source input and the obs version manifest -- the manifest is in the list becuase its oldest line picks the headers/qt6/import-libs even if none of this plugins own files changed.
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot "src") -Recurse -File | ForEach-Object { $_.FullName })
$buildInputs = $sourceFiles + @(
    (Join-Path $PSScriptRoot "CMakeLists.txt"),
    (Join-Path $PSScriptRoot "third_party\obs-browser\browser-panel.hpp"),
    $ObsVersionsManifest,
    (Join-Path $PSScriptRoot '..\..\obs_versions.ps1'),
    $PSCommandPath
)
if (Test-PluginOutputCurrent $OutPath $buildInputs) {
    $built = Get-Item -LiteralPath $OutPath
    $kb = [int][Math]::Ceiling($built.Length / 1024)
    Write-Output ""
    Write-Output ("Current: " + $built.FullName + "  (" + $kb + " KB)")
    if ($InstallAfterBuild) { Install-BuiltPlugin $OutPath }
    return
}

$sdk = Get-ObsSdk (Get-ObsBuildLine) $WorkDir
Write-Output "building against OBS $($sdk.Version)"
Enter-VsBuildEnvironment

$buildDir = Join-Path $WorkDir "build"
Remove-WorkDirectory $WorkDir $buildDir
New-Item -ItemType Directory -Force -Path $buildDir | Out-Null
$pluginSrcDir = $PSScriptRoot

Push-Location $buildDir
try {
    cmake -G Ninja `
        "-DCMAKE_BUILD_TYPE=Release" `
        "-DCMAKE_PREFIX_PATH=$($sdk.Qt)" `
        "-DOBS_HEADERS_DIR=$($sdk.Headers)" `
        "-DOBS_IMPLIB_DIR=$($sdk.Implibs)" `
        "$pluginSrcDir"
    if ($LASTEXITCODE -ne 0) { throw "cmake configure failed" }
    cmake --build . --config Release
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
} finally {
    Pop-Location
}

$builtDll = Join-Path $buildDir "replaykit.dll"
Write-Output "BUILD OK -> $builtDll"

New-Item -ItemType Directory -Force -Path (Split-Path $OutPath) | Out-Null
Copy-Item -Path $builtDll -Destination $OutPath -Force
Write-Output "bundled -> $OutPath"

if ($InstallAfterBuild) { Install-BuiltPlugin $builtDll }

# builds replaykit-tray.dll against whatever obs version is actually installed (no bundled sdk) by matching the obs-deps qt6 abi via CMakePresets.json, sparse-checking-out libobs/frontend/api headers at that obs-studio tag, downloading the matching prebuilt qt6 package, generating obs.lib/obs-frontend-api.lib import libs from the installed dlls via dumpbin, and building with cmake/ninja from vs2022/2019 build tools -- produces build/replaykit-tray.dll, copy to C:\ProgramData\obs-studio\plugins\replaykit-tray\bin\64bit\replaykit-tray.dll (no admin needed, obs scans that path itself).

param(
    [string]$WorkDir = (Join-Path $env:TEMP "replaykit-tray-build"),
    # where the built dll ends up; defualt lands it in the bundled assets, same as build_helper.ps1s $outPath does for the helper exe.
    [string]$OutPath = '',
    [switch]$InstallAfterBuild
)
$ErrorActionPreference = "Stop"

if (-not $OutPath) {
    $OutPath = Join-Path $PSScriptRoot '..\..\..\assets\obs-plugins\replaykit-tray\bin\64bit\replaykit-tray.dll'
}

function Find-VsBuildTools {
    foreach ($year in @("2022", "2019")) {
        $root = "C:\Program Files (x86)\Microsoft Visual Studio\$year\BuildTools\VC\Tools\MSVC"
        if (Test-Path $root) {
            $ver = Get-ChildItem $root -Directory | Sort-Object Name -Descending | Select-Object -First 1
            if ($ver) { return Join-Path $ver.FullName "bin\Hostx64\x64" }
        }
    }
    throw "No VS2022/2019 BuildTools MSVC toolset found. Install 'Desktop development with C++' first."
}

# same shape as build_helper.ps1s Test-HelperOutputCurrent -- true only when $targetPath exists and is newer than every input.
function Test-TrayPluginOutputCurrent([string]$targetPath, [string[]]$inputPaths) {
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

$obsExe = "C:\Program Files\obs-studio\bin\64bit\obs64.exe"
if (-not (Test-Path $obsExe)) { throw "OBS not found at $obsExe" }
$obsVersion = (Get-Item $obsExe).VersionInfo.FileVersion.TrimEnd('.0') -replace '(\d+\.\d+\.\d+)\.\d+$', '$1'
Write-Output "detected OBS version: $obsVersion"

# skip the whole rebuild (qt6 download, header checkout, import-lib generation, cmake/ninja) when the bundled dll is already newer than every source input and the installed obs itself -- obs64.exe is in the list becuase a different obs version needs different headers/qt6/import-libs even if none of this plugins own files changed.
$buildInputs = @(
    (Join-Path $PSScriptRoot "replaykit-tray.cpp"),
    (Join-Path $PSScriptRoot "CMakeLists.txt"),
    (Join-Path $PSScriptRoot "browser-panel.hpp"),
    $obsExe,
    $PSCommandPath
)
if (Test-TrayPluginOutputCurrent $OutPath $buildInputs) {
    $built = Get-Item -LiteralPath $OutPath
    $kb = [int][Math]::Ceiling($built.Length / 1024)
    Write-Output ""
    Write-Output ("Current: " + $built.FullName + "  (" + $kb + " KB)")
    if ($InstallAfterBuild) {
        $dest = "C:\ProgramData\obs-studio\plugins\replaykit-tray\bin\64bit\replaykit-tray.dll"
        New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
        Copy-Item -Path $OutPath -Destination $dest -Force
        Write-Output "installed -> $dest (restart OBS to load it)"
    }
    return
}

$vcTools = Find-VsBuildTools
$dumpbin = Join-Path $vcTools "dumpbin.exe"
$libExe  = Join-Path $vcTools "lib.exe"
$ninja   = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe"
$vcvars  = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat"

New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

# step 1: find the obs-deps tag this exact obs version was built with
Write-Output "reading CMakePresets.json for obs-studio tag $obsVersion..."
# gh prints the base64 content wrapped at ~60 chars/line, and powershell splits that captured output into one array element per line, so it has to be rejoined into a single string before decoding -- decoding line-by-line silently corrupts anything past the first line instead of erroring.
$presetsLines = gh api "repos/obsproject/obs-studio/contents/CMakePresets.json?ref=$obsVersion" --jq '.content'
$presetsBase64 = $presetsLines -join ''
$presetsJson = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($presetsBase64))
$presets = $presetsJson | ConvertFrom-Json
$depsPreset = $presets.configurePresets | Where-Object { $_.name -eq 'dependencies' } | Select-Object -First 1
$depsVersion = $depsPreset.vendor.'obsproject.com/obs-studio'.dependencies.qt6.version
if (-not $depsVersion) { throw "could not find qt6 dependency version in CMakePresets.json for $obsVersion" }
Write-Output "obs-deps tag for this obs version: $depsVersion"

# step 2: sparse-checkout obs-studio headers at the matching tag
$obsSrcDir = Join-Path $WorkDir "obs-src"
if (-not (Test-Path (Join-Path $obsSrcDir "libobs\obs.h"))) {
    Write-Output "checking out obs-studio $obsVersion headers (libobs/, frontend/api/)..."
    if (Test-Path $obsSrcDir) { Remove-Item -Recurse -Force $obsSrcDir }
    git clone --filter=blob:none --no-checkout --depth 1 --branch $obsVersion https://github.com/obsproject/obs-studio.git $obsSrcDir
    Push-Location $obsSrcDir
    git sparse-checkout set --no-cone libobs frontend/api
    git checkout $obsVersion
    Pop-Location
}
$obsConfigPath = Join-Path $obsSrcDir "libobs\obsconfig.h"
if (-not (Test-Path $obsConfigPath)) {
    # cmake normally generates this from obsconfig.h.in; this plugin never reads the values it would carry (install paths, optional feature flags), so literal placeholders are fine.
    @"
#pragma once
#define OBS_RELEASE_CANDIDATE 0
#define OBS_BETA 0
"@ | Set-Content -LiteralPath $obsConfigPath -Encoding ascii
}

# step 3: download the matching prebuilt qt6 package
$qtDir = Join-Path $WorkDir "qt6"
if (-not (Test-Path (Join-Path $qtDir "lib\cmake\Qt6\Qt6Config.cmake"))) {
    Write-Output "downloading obs-deps qt6 ($depsVersion, ~250MB)..."
    $qtZip = Join-Path $WorkDir "qt6.zip"
    $qtUrl = "https://github.com/obsproject/obs-deps/releases/download/$depsVersion/windows-deps-qt6-$depsVersion-x64.zip"
    Invoke-WebRequest -Uri $qtUrl -OutFile $qtZip
    if (Test-Path $qtDir) { Remove-Item -Recurse -Force $qtDir }
    Expand-Archive -Path $qtZip -DestinationPath $qtDir -Force
    Remove-Item $qtZip
}

# step 4: generate import libs from the installed obs dlls
$implibDir = Join-Path $WorkDir "implibs"
New-Item -ItemType Directory -Force -Path $implibDir | Out-Null

function New-ImportLib([string]$dllPath, [string]$dllName, [string]$outName) {
    $exportsText = & $dumpbin /exports $dllPath
    $names = New-Object System.Collections.Generic.List[string]
    $inTable = $false
    foreach ($line in $exportsText) {
        if ($line -match '^\s*ordinal\s+hint\s+RVA\s+name') { $inTable = $true; continue }
        if (-not $inTable) { continue }
        if ($line -match '^\s*\d+\s+[0-9A-Fa-f]+\s+[0-9A-Fa-f]+\s+(\S+)') {
            $sym = ($matches[1] -split '\s*=\s*')[0]
            if ($sym) { $names.Add($sym) }
        }
    }
    if ($names.Count -eq 0) { throw "no exports parsed for $dllPath" }
    $defPath = Join-Path $implibDir "$outName.def"
    @("LIBRARY $dllName", "EXPORTS") + ($names | ForEach-Object { "    $_" }) |
        Set-Content -LiteralPath $defPath -Encoding ascii
    & $libExe "/def:$defPath" "/out:$(Join-Path $implibDir "$outName.lib")" "/machine:x64" | Out-Null
}
Write-Output "generating obs.lib / obs-frontend-api.lib from the installed dlls..."
New-ImportLib "C:\Program Files\obs-studio\bin\64bit\obs.dll" "obs.dll" "obs"
New-ImportLib "C:\Program Files\obs-studio\bin\64bit\obs-frontend-api.dll" "obs-frontend-api.dll" "obs-frontend-api"

# step 5: configure + build
# vcvars64.bat shells out to vswhere.exe itself before it ever sets up compiler/linker/sdk paths, and doesnt put the installers own tools dir on path for that lookup -- caused a harmless "'vswhere.exe' is not recognized..." on every build. has to go before vcvars runs, not after: vcvars writes that message to stderr, which is why it was never visible in $envDump below to catch and suppress there. this is vswhere.exes one stable, documented location since vs2017.
$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer" + ";" + $env:PATH
$envDump = cmd /c "`"$vcvars`" && set"
foreach ($line in $envDump) {
    if ($line -match '^([^=]+)=(.*)$') { [System.Environment]::SetEnvironmentVariable($matches[1], $matches[2]) }
}
$env:PATH = (Split-Path $ninja) + ";" + $env:PATH

$buildDir = Join-Path $WorkDir "build"
if (Test-Path $buildDir) { Remove-Item -Recurse -Force $buildDir }
New-Item -ItemType Directory -Force -Path $buildDir | Out-Null
$pluginSrcDir = $PSScriptRoot

Push-Location $buildDir
try {
    cmake -G Ninja `
        "-DCMAKE_BUILD_TYPE=Release" `
        "-DCMAKE_PREFIX_PATH=$qtDir" `
        "-DOBS_HEADERS_DIR=$obsSrcDir" `
        "-DOBS_IMPLIB_DIR=$implibDir" `
        "$pluginSrcDir"
    if ($LASTEXITCODE -ne 0) { throw "cmake configure failed" }
    cmake --build . --config Release
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
} finally {
    Pop-Location
}

$builtDll = Join-Path $buildDir "replaykit-tray.dll"
Write-Output "BUILD OK -> $builtDll"

New-Item -ItemType Directory -Force -Path (Split-Path $OutPath) | Out-Null
Copy-Item -Path $builtDll -Destination $OutPath -Force
Write-Output "bundled -> $OutPath"

if ($InstallAfterBuild) {
    $dest = "C:\ProgramData\obs-studio\plugins\replaykit-tray\bin\64bit\replaykit-tray.dll"
    New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
    Copy-Item -Path $builtDll -Destination $dest -Force
    Write-Output "installed -> $dest (restart OBS to load it)"
}

# builds win-capture-audio from the vendored copy of Ayrton09/win-capture-audio at one pinned commit (upstream/) plus the replaykit patches (patches/), against the libobs headers of obs 28.0.0 so the module loads on obs 28 to 32, and packs the dll with its locale files into assets\win-capture-audio.zip. nothing unpinned is trusted: the vendored files, the patches, the obs header checkout and the wil checkout are verified by hash or by commit before a compiler sees them, and the finished dll is checked for the modules it imports

param(
    [string]$WorkDir = (Join-Path $env:LOCALAPPDATA "ReplayKit\plugin-build"),
    [string]$OutZip = '',
    # the exact upstream source with no patch, for comparison: the zip goes to the work dir and never to the assets
    [switch]$Pristine,
    [switch]$Force
)
$ErrorActionPreference = "Stop"
$ProgressPreference = 'SilentlyContinue'

# the pins: the commit of the vendored upstream tree, the obs tag the headers come from (module api version 28.0.0 loads on obs 28 to 32) and the wil commit the upstream repo pins as its submodule
$upstreamCommit = 'd11b381d41333964735a57d60bda54747f2232d9'
$obsTag = '28.0.0'
$obsCommit = '09da3ac4397ee9b1431c9c97cd2c71484b240382'
$wilCommit = 'f02a61c68590c22de2efeeaf881032419f6fef54'
$repoUrl = 'https://github.com/7Lzz/OBS-ReplayKit'

function RemoveBuildDirectory([string]$path) {
    $root = [IO.Path]::GetFullPath($WorkDir).TrimEnd('\') + '\'
    $target = [IO.Path]::GetFullPath($path).TrimEnd('\')
    if (-not $target.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Build cleanup is outside the work directory: $target"
    }
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
}

if (-not $OutZip) {
    $OutZip = if ($Pristine) { Join-Path $WorkDir 'win-capture-audio.pristine.zip' } else { Join-Path $PSScriptRoot '..\..\..\assets\win-capture-audio.zip' }
}
$OutZip = [IO.Path]::GetFullPath($OutZip)
$upstreamDir = Join-Path $PSScriptRoot 'upstream'
$patchDir = Join-Path $PSScriptRoot 'patches'

# true only when the target exists and is newer than every input
function Test-OutputCurrent([string]$targetPath, [string[]]$inputPaths) {
    if (-not (Test-Path -LiteralPath $targetPath -PathType Leaf)) { return $false }
    $target = Get-Item -LiteralPath $targetPath
    foreach ($inputPath in $inputPaths) {
        if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf)) { return $false }
        if ((Get-Item -LiteralPath $inputPath).LastWriteTimeUtc -gt $target.LastWriteTimeUtc) { return $false }
    }
    return $true
}

$inputs = @(Get-ChildItem -LiteralPath $upstreamDir, $patchDir -Recurse -File | ForEach-Object { $_.FullName })
$inputs += @((Join-Path $PSScriptRoot 'MANIFEST.sha256'), (Join-Path $PSScriptRoot 'PATCHES.sha256'), $PSCommandPath)
if (-not $Force -and (Test-OutputCurrent $OutZip $inputs)) {
    Write-Output ("Current: " + $OutZip + "  (" + [int][Math]::Ceiling((Get-Item -LiteralPath $OutZip).Length / 1024) + " KB)")
    return
}

# every file of the folder must be in the manifest with the recorded sha256, and every file of the manifest must be there
function Test-HashManifest([string]$root, [string]$manifestPath, [string]$what) {
    $expected = @{}
    foreach ($line in Get-Content -LiteralPath $manifestPath) {
        if ($line -match '^([0-9a-f]{64})  (.+)$') { $expected[$matches[2]] = $matches[1] }
        elseif ($line.Trim()) { throw "unreadable line in ${manifestPath}: $line" }
    }
    $rootFull = (Resolve-Path -LiteralPath $root).Path.TrimEnd('\')
    $seen = @{}
    foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File) {
        $rel = $file.FullName.Substring($rootFull.Length + 1).Replace('\', '/')
        if (-not $expected.ContainsKey($rel)) { throw "$what holds a file that is not in its manifest: $rel" }
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -ne $expected[$rel]) { throw "$what was changed: $rel" }
        $seen[$rel] = $true
    }
    foreach ($rel in $expected.Keys) {
        if (-not $seen.ContainsKey($rel)) { throw "$what is missing a file of its manifest: $rel" }
    }
    return $expected.Count
}
$upstreamCount = Test-HashManifest $upstreamDir (Join-Path $PSScriptRoot 'MANIFEST.sha256') 'the vendored upstream source'
$patchCount = Test-HashManifest $patchDir (Join-Path $PSScriptRoot 'PATCHES.sha256') 'the patch set'
Write-Output "vendored upstream source verified: $upstreamCount files of commit $upstreamCommit; patches verified: $patchCount"

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
$msvcBin = Find-VsBuildTools
$dumpbin = Join-Path $msvcBin 'dumpbin.exe'
$libExe = Join-Path $msvcBin 'lib.exe'
$ninja = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe"
$vcvars = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
foreach ($tool in $dumpbin, $libExe, $ninja, $vcvars) {
    if (-not (Test-Path -LiteralPath $tool)) { throw "build tool not found: $tool" }
}
$obsDll = "C:\Program Files\obs-studio\bin\64bit\obs.dll"
if (-not (Test-Path -LiteralPath $obsDll)) { throw "OBS not found at $obsDll" }

# a checkout is only reused while it is on the pinned commit and git sees every file of it in place
function Test-PinnedCheckout([string]$dir, [string]$commit) {
    if (-not (Test-Path -LiteralPath (Join-Path $dir '.git'))) { return $false }
    $head = @(git -C $dir rev-parse HEAD)
    if ($LASTEXITCODE -ne 0 -or $head.Count -ne 1 -or $head[0] -ne $commit) { return $false }
    $status = @(git -C $dir status --porcelain)
    if ($LASTEXITCODE -ne 0) { return $false }
    return $status.Count -eq 0
}

New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

$obsSrcDir = Join-Path $WorkDir 'wca-obs-src'
if (-not (Test-PinnedCheckout $obsSrcDir $obsCommit)) {
    Write-Output "checking out the libobs headers of obs-studio $obsTag..."
    RemoveBuildDirectory $obsSrcDir
    git clone -q --filter=blob:none --no-checkout --depth 1 --branch $obsTag https://github.com/obsproject/obs-studio.git $obsSrcDir
    if ($LASTEXITCODE -ne 0) { throw 'OBS header checkout failed.' }
    git -C $obsSrcDir config core.autocrlf false
    git -C $obsSrcDir sparse-checkout set --no-cone libobs
    if ($LASTEXITCODE -ne 0) { throw 'OBS sparse checkout failed.' }
    git -C $obsSrcDir checkout -q $obsTag
    if ($LASTEXITCODE -ne 0) { throw 'OBS version checkout failed.' }
    if (-not (Test-PinnedCheckout $obsSrcDir $obsCommit)) { throw "obs-studio $obsTag is not the commit this build is pinned to ($obsCommit)." }
}

$wilDir = Join-Path $WorkDir 'wca-wil'
if (-not (Test-PinnedCheckout $wilDir $wilCommit)) {
    Write-Output "fetching microsoft/wil at $wilCommit..."
    RemoveBuildDirectory $wilDir
    New-Item -ItemType Directory -Force -Path $wilDir | Out-Null
    git -C $wilDir init -q
    git -C $wilDir remote add origin https://github.com/microsoft/wil.git
    git -C $wilDir config core.autocrlf false
    git -C $wilDir -c transfer.fsckObjects=true fetch -q --depth 1 origin $wilCommit
    if ($LASTEXITCODE -ne 0) { throw 'wil fetch failed.' }
    git -C $wilDir checkout -q --detach FETCH_HEAD
    if ($LASTEXITCODE -ne 0) { throw 'wil checkout failed.' }
    if (-not (Test-PinnedCheckout $wilDir $wilCommit)) { throw "wil is not the commit this build is pinned to ($wilCommit)." }
}

# the libobs sdk the sources expect: the obs 28 headers, the obsconfig.h obs generates itself and the obs.dll exports as an import library. the upstream cmake file finds it as a package and so never runs its own bootstrap script
$sdkDir = Join-Path $WorkDir 'wca-sdk'
$sdkStamp = "$obsCommit|$((Get-Item $obsDll).Length)|$((Get-Item $obsDll).LastWriteTimeUtc.Ticks)"
$stampFile = Join-Path $sdkDir 'sdk-stamp.txt'
if ($Force -or -not (Test-Path -LiteralPath $stampFile) -or ((Get-Content -LiteralPath $stampFile -Raw).Trim() -ne $sdkStamp)) {
    Write-Output "assembling the libobs sdk..."
    RemoveBuildDirectory $sdkDir
    $includeDir = Join-Path $sdkDir 'include'
    $libDir = Join-Path $sdkDir 'lib'
    $cmakeDir = Join-Path $sdkDir 'lib\cmake\libobs'
    New-Item -ItemType Directory -Force -Path $includeDir, $libDir, $cmakeDir | Out-Null
    $libobsSrc = Join-Path $obsSrcDir 'libobs'
    Copy-Item (Join-Path $libobsSrc '*.h') $includeDir
    foreach ($sub in 'util', 'media-io', 'graphics', 'callback') {
        $target = Join-Path $includeDir $sub
        New-Item -ItemType Directory -Force $target | Out-Null
        Copy-Item (Join-Path $libobsSrc "$sub\*.h") $target
    }
    # obsconfig.h comes out of the cmake run of obs itself and only its path macros matter to a plugin
    @'
#pragma once

#define OBS_DATA_PATH "../../data"
#define OBS_PLUGIN_PATH "../../obs-plugins/64bit"
#define OBS_PLUGIN_DESTINATION "obs-plugins/64bit"

#define OBS_RELEASE_CANDIDATE 0
#define OBS_BETA 0
'@ | Set-Content -LiteralPath (Join-Path $includeDir 'obsconfig.h') -Encoding ascii

    $exports = & $dumpbin /EXPORTS $obsDll
    if ($LASTEXITCODE -ne 0) { throw "Could not inspect exports: $obsDll" }
    $names = @($exports | ForEach-Object { if ($_ -match '^\s+\d+\s+[0-9A-Fa-f]+\s+[0-9A-Fa-f]{8}\s+(\S+)') { $matches[1] } } | Where-Object { $_ } | Sort-Object -Unique)
    if ($names.Count -lt 100) { throw "Only $($names.Count) exports found in obs.dll." }
    $defPath = Join-Path $libDir 'obs.def'
    Set-Content -LiteralPath $defPath -Value (@('LIBRARY obs.dll', 'EXPORTS') + $names) -Encoding ascii
    $libPath = Join-Path $libDir 'obs.lib'
    & $libExe /nologo "/def:$defPath" /machine:x64 "/out:$libPath" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'lib.exe failed to produce obs.lib' }

    $sdkSlash = $sdkDir.Replace('\', '/')
    @"
add_library(OBS::libobs SHARED IMPORTED)
set_target_properties(OBS::libobs PROPERTIES
    IMPORTED_IMPLIB "$sdkSlash/lib/obs.lib"
    IMPORTED_LOCATION "$sdkSlash/lib/obs.lib"
    INTERFACE_INCLUDE_DIRECTORIES "$sdkSlash/include"
    INTERFACE_COMPILE_DEFINITIONS "HAVE_OBSCONFIG_H")
"@ | Set-Content -LiteralPath (Join-Path $cmakeDir 'libobsConfig.cmake') -Encoding ascii
    Set-Content -LiteralPath $stampFile -Value $sdkStamp -Encoding ascii
}

# a fresh copy of the verified source, with the patches applied on top of it
$variant = if ($Pristine) { 'pristine' } else { 'replaykit' }
$srcDir = Join-Path $WorkDir "wca-build-$variant\src"
$buildDir = Join-Path $WorkDir "wca-build-$variant\out"
RemoveBuildDirectory (Join-Path $WorkDir "wca-build-$variant")
New-Item -ItemType Directory -Force -Path $srcDir | Out-Null
Copy-Item -Path (Join-Path $upstreamDir '*') -Destination $srcDir -Recurse
if (-not $Pristine) {
    foreach ($patch in (Get-ChildItem -LiteralPath $patchDir -Filter '*.patch' | Sort-Object Name)) {
        Push-Location $srcDir
        try {
            git apply --check --whitespace=nowarn $patch.FullName
            if ($LASTEXITCODE -ne 0) { throw "patch does not apply: $($patch.Name)" }
            git apply --whitespace=nowarn $patch.FullName
            if ($LASTEXITCODE -ne 0) { throw "patch failed: $($patch.Name)" }
        } finally { Pop-Location }
        Write-Output "applied $($patch.Name)"
    }
}

# vcvars64.bat shells out to vswhere.exe before it sets anything up and does not put the installer tools on the path for that, so it is added first
$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer" + ";" + $env:PATH
$envDump = cmd /c "`"$vcvars`" && set"
foreach ($line in $envDump) {
    if ($line -match '^([^=]+)=(.*)$') { [System.Environment]::SetEnvironmentVariable($matches[1], $matches[2]) }
}
$env:PATH = (Split-Path $ninja) + ";" + $env:PATH

$cmakeArgs = @(
    '-G', 'Ninja', '-S', $srcDir, '-B', $buildDir,
    '-DCMAKE_BUILD_TYPE=RelWithDebInfo',
    "-DCMAKE_PREFIX_PATH=$sdkDir",
    "-DWIL_INCLUDE_DIR=$(Join-Path $wilDir 'include')",
    # deterministic output, so the same inputs on the same toolchain give the same dll; symbols stay in the pdb
    '-DCMAKE_CXX_FLAGS_RELWITHDEBINFO=/Zi /O2 /Ob1 /DNDEBUG /Brepro',
    '-DCMAKE_MODULE_LINKER_FLAGS_RELWITHDEBINFO=/DEBUG /INCREMENTAL:NO /Brepro /OPT:REF /OPT:ICF'
)
cmake @cmakeArgs
if ($LASTEXITCODE -ne 0) { throw 'cmake configure failed' }
cmake --build $buildDir
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

$dll = Join-Path $buildDir 'win-capture-audio.dll'
$pdb = Join-Path $buildDir 'win-capture-audio.pdb'
if (-not (Test-Path -LiteralPath $dll)) { throw 'the build produced no dll' }

# the dll may only import what an audio capture needs: no networking, no shell, no crypto
$deps = & $dumpbin /DEPENDENTS $dll
if ($LASTEXITCODE -ne 0) { throw 'could not read the dependents of the dll' }
$modules = @($deps | ForEach-Object { if ($_ -match '^\s+(\S+\.dll)\s*$') { $matches[1].ToLowerInvariant() } } | Where-Object { $_ } | Sort-Object -Unique)
$allowed = @('obs.dll', 'kernel32.dll', 'user32.dll', 'ole32.dll', 'oleaut32.dll', 'advapi32.dll', 'mmdevapi.dll', 'ksuser.dll', 'avrt.dll', 'psapi.dll', 'vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll', 'ntdll.dll')
foreach ($m in $modules) {
    if ($m -notin $allowed -and $m -notlike 'api-ms-win-*') { throw "the dll imports a module it should not: $m (all: $($modules -join ', '))" }
}
Write-Output ("imports: " + ($modules -join ', '))

# the same check on the strings: no address of any kind inside the binary
$bytes = [IO.File]::ReadAllBytes($dll)
$ascii = [Text.Encoding]::GetEncoding(28591).GetString($bytes)
$wide = [Text.Encoding]::Unicode.GetString($bytes)
foreach ($text in $ascii, $wide) {
    if ($text -match '(?i)https?://|ftp://|wss?://') { throw "the dll holds an address: $($matches[0])" }
}

# the zip: the dll and the locale files in the layout inside the obs folder, the license and a source note beside them, with fixed dates and a fixed order so the same inputs give the same zip
$hash = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant()
$buildId = if ($Pristine) { "$upstreamCommit (no patch)" } else { "$upstreamCommit + replaykit patches rk2" }
$sourceNote = @"
win-capture-audio 2.3.4, built by OBS ReplayKit.

Upstream: https://github.com/Ayrton09/win-capture-audio, commit $upstreamCommit (a modernized fork of https://github.com/bozbez/win-capture-audio).
Built from: $buildId
The unmodified upstream files, the patches and the build script are in $repoUrl under ReplayKit/utils/obs-plugins/win-capture-audio/.
Licensed GPL-2.0, see LICENSE. Copyright of the original plugin: bozbez; maintained fork: Ayrton09.
"@
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutZip) | Out-Null
if (Test-Path -LiteralPath $OutZip) { Remove-Item -LiteralPath $OutZip -Force }
$stamp = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
$zip = [IO.Compression.ZipFile]::Open($OutZip, [IO.Compression.ZipArchiveMode]::Create)
try {
    function Add-ZipBytes([string]$name, [byte[]]$content) {
        $entry = $zip.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = $stamp
        $stream = $entry.Open()
        try { $stream.Write($content, 0, $content.Length) } finally { $stream.Dispose() }
    }
    $entries = New-Object System.Collections.Generic.List[object]
    $entries.Add(@('obs-plugins/64bit/win-capture-audio.dll', $bytes))
    foreach ($ini in (Get-ChildItem -LiteralPath (Join-Path $upstreamDir 'data\locale') -Filter '*.ini' | Sort-Object Name)) {
        $entries.Add(@("data/obs-plugins/win-capture-audio/locale/$($ini.Name)", [IO.File]::ReadAllBytes($ini.FullName)))
    }
    $entries.Add(@('data/obs-plugins/win-capture-audio/LICENSE', [IO.File]::ReadAllBytes((Join-Path $upstreamDir 'LICENSE'))))
    $entries.Add(@('data/obs-plugins/win-capture-audio/SOURCE.txt', [Text.Encoding]::UTF8.GetBytes($sourceNote.Replace("`r`n", "`n") + "`n")))
    foreach ($e in ($entries | Sort-Object { $_[0] })) { Add-ZipBytes $e[0] $e[1] }
} finally { $zip.Dispose() }
$zipHash = (Get-FileHash -LiteralPath $OutZip -Algorithm SHA256).Hash.ToLowerInvariant()

# what this zip was made of, kept in the repo next to the sources
if (-not $Pristine) {
    $record = [ordered]@{
        plugin = 'win-capture-audio 2.3.4'
        upstream = 'https://github.com/Ayrton09/win-capture-audio'
        upstreamCommit = $upstreamCommit
        manifestSha256 = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'MANIFEST.sha256') -Algorithm SHA256).Hash.ToLowerInvariant()
        patchesSha256 = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'PATCHES.sha256') -Algorithm SHA256).Hash.ToLowerInvariant()
        obsHeaders = "obs-studio $obsTag ($obsCommit)"
        wil = "microsoft/wil $wilCommit"
        compiler = "msvc " + (Get-Item -LiteralPath $msvcBin).Parent.Parent.Parent.Name
        dllSha256 = $hash
        dllBytes = $bytes.Length
        zipSha256 = $zipHash
        imports = $modules
    }
    $record | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'BUILD-RECORD.json') -Encoding ascii
}

Write-Output ""
Write-Output "BUILD OK ($variant): $dll"
Write-Output "  dll sha256 $hash ($($bytes.Length) bytes)"
Write-Output "  pdb        $pdb (keep it, it symbolizes any crash dump of this dll)"
Write-Output "  zip        $OutZip ($([int][Math]::Ceiling((Get-Item -LiteralPath $OutZip).Length / 1024)) KB, sha256 $zipHash)"

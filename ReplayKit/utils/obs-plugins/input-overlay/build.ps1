# builds input-overlay from its pinned upstream source release against the obs version of assets/obs-versions.json, the same way replaykit.dll is built, instead of shipping the upstream windows zip (which carries a debug file and a remote-client exe replaykit does not use). the zip lands in assets as input-overlay-plugin.zip with the dlls at its root and data beside them; the installer puts them where that obs loads plugins from

param(
    # shared with the replaykit plugin build, so the obs headers, qt6 and import libs are only fetched once
    [string]$WorkDir = (Join-Path $env:LOCALAPPDATA "ReplayKit\plugin-build"),
    [string]$OutPath = ''
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
. (Join-Path $PSScriptRoot '..\..\obs_versions.ps1')

$Version = '5.1.3'
$SourceUrl = "https://github.com/univrsal/input-overlay/releases/download/$Version/input-overlay-$Version-source.tar.xz"
# the checksum the upstream release notes list for the source tarball
$SourceSha256 = '944151409f22b9cf2c3ea9b85514000d6c5c6d237f3dd4ab2bbf8781951adb99'

if (-not $OutPath) { $OutPath = Join-Path $PSScriptRoot '..\..\..\assets\input-overlay-plugin.zip' }

$buildInputs = @($PSCommandPath, (Join-Path $PSScriptRoot 'CMakeLists.txt'), $ObsVersionsManifest, (Join-Path $PSScriptRoot '..\..\obs_versions.ps1'))
if (Test-Path -LiteralPath $OutPath -PathType Leaf) {
    $built = (Get-Item -LiteralPath $OutPath).LastWriteTimeUtc
    if (-not ($buildInputs | Where-Object { (Get-Item -LiteralPath $_).LastWriteTimeUtc -gt $built })) {
        Write-Output "Current: $([IO.Path]::GetFullPath($OutPath))"
        return
    }
}

New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
$tarball = Join-Path $WorkDir "input-overlay-$Version-source.tar.xz"
$hash = if (Test-Path -LiteralPath $tarball) { (Get-FileHash -LiteralPath $tarball -Algorithm SHA256).Hash.ToLowerInvariant() } else { '' }
if ($hash -ne $SourceSha256) {
    Write-Output "downloading input-overlay $Version source..."
    Invoke-WebRequest -Uri $SourceUrl -OutFile $tarball
    $hash = (Get-FileHash -LiteralPath $tarball -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $SourceSha256) {
        Remove-Item -LiteralPath $tarball -Force
        throw "The input-overlay $Version source does not match its published checksum (sha256 $hash)."
    }
}

# a fresh tree every build, so the build always starts from the published source and nothing else
$srcRoot = Join-Path $WorkDir 'input-overlay-src'
Remove-WorkDirectory $WorkDir $srcRoot
New-Item -ItemType Directory -Force -Path $srcRoot | Out-Null
# build-aux holds the formatter scripts of upstream as symlinks, which windows only lets an elevated or developer-mode process create, and the build never reads it; a virus scanner can hold a file it just saw for a moment, so the unpack gets one more try, and what tar said comes along when it still fails
$tarLog = Join-Path $WorkDir 'input-overlay-tar.log'
foreach ($attempt in 1..2) {
    $tar = Start-Process -FilePath "$env:SystemRoot\System32\tar.exe" -ArgumentList @('-xf', "`"$tarball`"", '-C', "`"$srcRoot`"", '--exclude', "input-overlay-$Version-source/build-aux") -NoNewWindow -Wait -PassThru -RedirectStandardError $tarLog
    if ($tar.ExitCode -eq 0) { break }
    if ($attempt -eq 2) { throw "The input-overlay source could not be unpacked: $((Get-Content -LiteralPath $tarLog -Raw) -replace '\s+$', '')" }
    Start-Sleep -Seconds 3
    Remove-WorkDirectory $WorkDir $srcRoot
    New-Item -ItemType Directory -Force -Path $srcRoot | Out-Null
}
$src = Join-Path $srcRoot "input-overlay-$Version-source"
if (-not (Test-Path -LiteralPath (Join-Path $src 'src\util\overlay.cpp'))) { throw 'The input-overlay source is not laid out as expected.' }


$sdk = Get-ObsSdk (Get-ObsBuildLine) $WorkDir
Write-Output "building input-overlay $Version against OBS $($sdk.Version)"
Enter-VsBuildEnvironment

$buildDir = Join-Path $WorkDir 'input-overlay-build'
Remove-WorkDirectory $WorkDir $buildDir
New-Item -ItemType Directory -Force -Path $buildDir | Out-Null
Push-Location $buildDir
try {
    cmake -G Ninja `
        '-DCMAKE_BUILD_TYPE=Release' `
        "-DCMAKE_PREFIX_PATH=$($sdk.Qt)" `
        "-DIO_SRC=$($src.Replace('\', '/'))" `
        "-DIO_VERSION=$Version" `
        "-DOBS_HEADERS_DIR=$($sdk.Headers)" `
        "-DOBS_IMPLIB_DIR=$($sdk.Implibs)" `
        "$PSScriptRoot"
    if ($LASTEXITCODE -ne 0) { throw 'cmake configure failed' }
    cmake --build . --config Release
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }
} finally {
    Pop-Location
}

# the dlls at the root of the zip and the data beside them, with a fixed date on every entry so a rebuild of the same source does not change the committed zip
$files = [ordered]@{
    'input-overlay/input-overlay.dll' = Join-Path $buildDir 'input-overlay.dll'
    'input-overlay/uiohook.dll' = Join-Path $buildDir 'uiohook.dll'
    'input-overlay/SDL3.dll' = Join-Path $src 'deps\sdl3\bin\x64\SDL3.dll'
    'input-overlay/data/LICENSE' = Join-Path $src 'LICENSE'
}
$dataDir = Join-Path $src 'data'
foreach ($file in Get-ChildItem -LiteralPath $dataDir -Recurse -File | Sort-Object FullName) {
    $files['input-overlay/data/' + $file.FullName.Substring($dataDir.Length + 1).Replace('\', '/')] = $file.FullName
}
foreach ($path in $files.Values) { if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "The input-overlay build is missing $path" } }

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$temporary = "$OutPath.tmp"
if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
$zip = [IO.Compression.ZipFile]::Open($temporary, 'Create')
try {
    foreach ($name in $files.Keys) {
        $entry = $zip.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
        $output = $entry.Open()
        try {
            $source = [IO.File]::OpenRead($files[$name])
            try { $source.CopyTo($output) } finally { $source.Dispose() }
        } finally { $output.Dispose() }
    }
} finally { $zip.Dispose() }
Move-Item -LiteralPath $temporary -Destination $OutPath -Force
Write-Output "bundled -> $([IO.Path]::GetFullPath($OutPath)) ($($files.Count) files)"

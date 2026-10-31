$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
# the release exe + checksums land in build\ at the actual git root, one level above ReplayKit\; publish_tmp stays under $repoRoot since ClearPublishDirectorys guard requires it there.
$buildDir = Join-Path (Split-Path -Parent $repoRoot) 'build'
$publishDir = Join-Path $repoRoot 'publish_tmp'

function ClearPublishDirectory {
    $resolved = [IO.Path]::GetFullPath($publishDir).TrimEnd('\')
    $expected = [IO.Path]::GetFullPath((Join-Path $repoRoot 'publish_tmp')).TrimEnd('\')
    if ($resolved -ne $expected -or -not $resolved.StartsWith($repoRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Publish directory is outside the workspace.'
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}

Write-Host '[1/4] Syncing version...'
& (Join-Path $PSScriptRoot 'sync_version.ps1')
Write-Host '[2/4] Building helper...'
& powershell -NoProfile -File (Join-Path $PSScriptRoot 'build_helper.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Helper build failed.' }
Write-Host '[3/4] Building OBS plugin...'
& powershell -NoProfile -File (Join-Path $PSScriptRoot 'obs-plugins\replaykit\build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'OBS plugin build failed.' }
Write-Host '[3b/4] Building win-capture-audio...'
& powershell -NoProfile -File (Join-Path $PSScriptRoot 'obs-plugins\win-capture-audio\build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'win-capture-audio build failed.' }
Write-Host '[3c/4] Building input-overlay...'
& powershell -NoProfile -File (Join-Path $PSScriptRoot 'obs-plugins\input-overlay\build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'input-overlay build failed.' }
Write-Host '[3d/4] Checking the plugins against every OBS version...'
& powershell -NoProfile -File (Join-Path $PSScriptRoot 'verify_obs_versions.ps1')
if ($LASTEXITCODE -ne 0) { throw 'A bundled plugin does not load in every OBS version of obs-versions.json.' }
Write-Host '[4/4] Publishing installer...'
Write-Host 'Building browser and helper process roles for every supported OBS version...'
& powershell -NoProfile -File (Join-Path $PSScriptRoot 'process-host\build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'OBS process role build failed.' }
& powershell -NoProfile -File (Join-Path $PSScriptRoot 'process-host\verify_versions.ps1')
if ($LASTEXITCODE -ne 0) { throw 'OBS process role verification failed.' }
ClearPublishDirectory
try {
    & dotnet publish (Join-Path $repoRoot 'ReplayKitSetup\ReplayKitSetup.csproj') -c Release -o $publishDir --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Installer publish failed.' }
    $exe = Join-Path $publishDir 'OBSReplayKit.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Installer executable is missing.' }
    if ((Get-Item -LiteralPath $exe).Length -lt 10MB) { throw 'Installer is smaller than the required self-contained payload.' }
    $unexpected = @(Get-ChildItem -LiteralPath $publishDir -Force | Where-Object { $_.Name -ne 'OBSReplayKit.exe' })
    if ($unexpected.Count -ne 0) { throw 'Publish produced unexpected companion files; refusing an incomplete single-file release.' }
    $bundle = Join-Path $repoRoot 'ReplayKitSetup\obj\assets.bundle.zip'
    if (-not (Test-Path -LiteralPath $bundle) -or (Get-Item -LiteralPath $bundle).Length -lt 4MB) { throw 'Embedded asset bundle is missing.' }
    $hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    New-Item -ItemType Directory -Force -Path $buildDir | Out-Null
    Copy-Item -LiteralPath $exe -Destination (Join-Path $buildDir 'OBSReplayKit.exe') -Force
    [IO.File]::WriteAllText((Join-Path $buildDir 'OBSReplayKit.exe.sha256'), "$hash  OBSReplayKit.exe`n", [Text.Encoding]::ASCII)
    $obsolete = Join-Path $buildDir 'Newtonsoft.Json.dll'
    if (Test-Path -LiteralPath $obsolete -PathType Leaf) { Remove-Item -LiteralPath $obsolete -Force }
    Write-Host "Built $buildDir\OBSReplayKit.exe ($((Get-Item -LiteralPath $exe).Length) bytes) and OBSReplayKit.exe.sha256."
} finally {
    ClearPublishDirectory
}

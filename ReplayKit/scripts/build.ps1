$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'build_layout.ps1')
$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $ReplayKitCache 'setup/publish'
$bundle = Join-Path $ReplayKitCache 'setup/obj/assets.bundle.zip'
$installer = Join-Path $ReplayKitBuildRoot 'OBSReplayKit.exe'
$checksum = "$installer.sha256"
$lockPath = Join-Path $ReplayKitBuildRoot '.build.lock'

foreach ($path in $lockPath, $installer, $checksum, $bundle, $publishDir, $ReplayKitPayload,
    (Join-Path $ReplayKitCache 'helper/bin'), (Join-Path $ReplayKitCache 'setup/bin'),
    (Join-Path $ReplayKitCache 'setup/obj/assets-bundle'), (Join-Path $ReplayKitCache 'setup/obj/assets-zips'),
    (Join-Path $ReplayKitDependencies 'nuget')) {
    Assert-BuildPath $path
}
New-Item -ItemType Directory -Force -Path $ReplayKitBuildRoot | Out-Null
try {
    $lock = [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None')
} catch [IO.IOException] {
    throw "Could not lock the ReplayKit build directory: $($_.Exception.Message)"
}
try {
    Write-Host '[1/4] Syncing version...'
    & (Join-Path $PSScriptRoot 'sync_version.ps1')
    & (Join-Path $PSScriptRoot 'sync_shared.ps1')
    & (Join-Path $PSScriptRoot 'newtonsoft_source.ps1')
    Write-Host '[2/4] Building helper...'
    & powershell -NoProfile -File (Join-Path $PSScriptRoot 'build_helper.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Helper build failed.' }
    Write-Host '[3/4] Building OBS plugin...'
    & powershell -NoProfile -File (Join-Path $PSScriptRoot '..\plugins\replaykit\build.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'OBS plugin build failed.' }
    Write-Host '[3b/4] Building win-capture-audio...'
    & powershell -NoProfile -File (Join-Path $PSScriptRoot '..\plugins\win-capture-audio\build.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'win-capture-audio build failed.' }
    Write-Host '[3c/4] Building input-overlay...'
    & powershell -NoProfile -File (Join-Path $PSScriptRoot '..\plugins\input-overlay\build.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'input-overlay build failed.' }
    Write-Host '[3e/4] Building the share window...'
    & powershell -NoProfile -File (Join-Path $PSScriptRoot '..\apps\share-window\build.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Share window build failed.' }
    Write-Host '[3d/4] Checking the plugins against every OBS version...'
    & powershell -NoProfile -File (Join-Path $PSScriptRoot 'verify_obs_versions.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'A bundled plugin does not load in every OBS version of obs-versions.json.' }
    Write-Host '[4/4] Publishing installer...'
    Write-Host 'Building browser and helper process roles for every supported OBS version...'
    & powershell -NoProfile -File (Join-Path $PSScriptRoot '..\apps\process-host\build.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'OBS process role build failed.' }
    & powershell -NoProfile -File (Join-Path $PSScriptRoot '..\apps\process-host\verify_versions.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'OBS process role verification failed.' }
    Remove-WorkDirectory $ReplayKitCache $publishDir
    try {
        & dotnet publish (Join-Path $repoRoot 'apps\setup\ReplayKitSetup.csproj') -c Release -o $publishDir --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Installer publish failed.' }
        $exe = Join-Path $publishDir 'OBSReplayKit.exe'
        if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Installer executable is missing.' }
        if ((Get-Item -LiteralPath $exe).Length -lt 10MB) { throw 'Installer is smaller than the required payload.' }
        $unexpected = @(Get-ChildItem -LiteralPath $publishDir -Force | Where-Object { $_.Name -ne 'OBSReplayKit.exe' })
        if ($unexpected.Count -ne 0) { throw 'Publish produced unexpected companion files; refusing an incomplete single-file release.' }
        if (-not (Test-Path -LiteralPath $bundle) -or (Get-Item -LiteralPath $bundle).Length -lt 4MB) { throw 'Embedded asset bundle is missing.' }
        $hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
        $checksumFile = Join-Path $publishDir 'OBSReplayKit.exe.sha256'
        [IO.File]::WriteAllText($checksumFile, "$hash  OBSReplayKit.exe`n", [Text.Encoding]::ASCII)
        $size = (Get-Item -LiteralPath $exe).Length
        foreach ($file in @(@{ From = $exe; To = $installer }, @{ From = $checksumFile; To = $checksum })) {
            if ([IO.File]::Exists($file.To)) { [IO.File]::Replace($file.From, $file.To, [NullString]::Value) }
            else { [IO.File]::Move($file.From, $file.To) }
        }
        Write-Host "Built $installer ($size bytes) and OBSReplayKit.exe.sha256."
    } finally {
        Remove-WorkDirectory $ReplayKitCache $publishDir
    }
    # last, so the notice is the final thing on screen and only shows once everything built
    & (Join-Path $PSScriptRoot 'check_ffmpeg.ps1')
} finally {
    $lock.Dispose()
}

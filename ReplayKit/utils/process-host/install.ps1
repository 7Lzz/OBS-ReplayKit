param(
    [ValidateSet('Prepare', 'Apply', 'Restore')][string]$Mode = 'Prepare',
    [Parameter(Mandatory)][string]$Backup,
    [string]$ObsRoot = 'C:/Program Files/obs-studio',
    [string]$ReplayKitRoot = (Join-Path $env:APPDATA 'obs-studio/obs-replayKit'),
    [string]$SetupCacheRoot = (Join-Path $env:LOCALAPPDATA 'OBS ReplayKit'),
    [string]$Components = ''
)
$ErrorActionPreference = 'Stop'
if (-not $Components) { $Components = Join-Path $PSScriptRoot '../../../build/process-host/unpackaged-components' }

function FullPath([string]$path) {
    $full = [IO.Path]::GetFullPath($path).TrimEnd('\')
    $current = $full
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Reparse points are not supported: $current"
            }
        }
        $current = Split-Path -Parent $current
    }
    return $full
}
function Within([string]$root, [string]$relative) {
    $segments = $relative.Replace('\', '/').Split('/')
    if ([IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or $segments -contains '..' -or $segments -contains '.') {
        throw 'Invalid relative path.'
    }
    $path = FullPath (Join-Path $root $relative)
    if (-not $path.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Path escapes its root.' }
    return $path
}
function Hash([string]$path) { return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
function CopyFile([string]$source, [string]$target, [string]$hash) {
    $directory = Split-Path -Parent $target
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $temporary = Join-Path $directory ([IO.Path]::GetFileName($target) + '.' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.File]::Copy($source, $temporary, $false)
        if ((Hash $temporary) -ne $hash) { throw "Copy verification failed: $target" }
        if (Test-Path -LiteralPath $target) { [IO.File]::Replace($temporary, $target, [NullString]::Value) }
        else { [IO.File]::Move($temporary, $target) }
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}

$ObsRoot = FullPath $ObsRoot
$ReplayKitRoot = FullPath $ReplayKitRoot
$SetupCacheRoot = FullPath $SetupCacheRoot
$Backup = FullPath $Backup
$Components = FullPath $Components
$obsExe = Within $ObsRoot 'bin/64bit/obs64.exe'
$helperExe = Within $ReplayKitRoot 'scripts/helper/OBSReplayKit.exe'
if (-not (Test-Path -LiteralPath $obsExe -PathType Leaf) -or -not (Test-Path -LiteralPath $helperExe -PathType Leaf)) {
    throw 'The existing OBS and ReplayKit installation must both be present.'
}
foreach ($root in @($ObsRoot, $ReplayKitRoot, $SetupCacheRoot)) {
    if ($Backup -eq $root -or $Backup.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The backup must be outside the installation.'
    }
}
$running = @(Get-CimInstance Win32_Process | Where-Object {
    $_.ExecutablePath -eq $obsExe -or $_.ExecutablePath -eq $helperExe
})
if ($running.Count) { throw "Close OBS and ReplayKit first. Still running: $($running.ProcessId -join ', ')" }
$nativePaths = @('bin/64bit/obs64.exe', 'bin/64bit/obs-frontend-host.dll',
    'obs-plugins/64bit/obs-browser.dll', 'obs-plugins/64bit/obs-browser-role.dll')
$manifestPath = Join-Path $Backup 'manifest.json'

if ($Mode -eq 'Prepare') {
    if (Test-Path -LiteralPath $Backup) { throw 'Choose a new backup directory.' }
    $native = Get-Content -LiteralPath (Within $Components 'manifest.json') -Raw | ConvertFrom-Json
    $version = (Get-Item -LiteralPath $obsExe).VersionInfo.FileVersion
    if ($version -notmatch ('^' + [regex]::Escape($native.ObsVersion) + '(?:\.\d+)?$')) {
        throw 'Host components do not match the installed OBS version.'
    }
    if (@($native.Files).Count -ne $nativePaths.Count) { throw 'Incomplete native component manifest.' }
    $files = @()
    foreach ($relative in $nativePaths) {
        $entry = @($native.Files | Where-Object Path -eq $relative)
        $source = Within $Components $relative
        if ($entry.Count -ne 1 -or (Hash $source) -ne $entry[0].Sha256) { throw "Invalid component: $relative" }
        $files += [pscustomobject]@{Root='obs'; Path=$relative; Source=$source}
    }
    $assets = FullPath (Join-Path $PSScriptRoot '../../assets/obs-studio/obs-replayKit')
    foreach ($relative in @('scripts/helper/OBSReplayKit.exe', 'scripts/helper/helper_bootstrap.lua',
        'icons/player/play.svg', 'icons/player/pause.svg', 'icons/editor/play.svg', 'icons/editor/pause.svg')) {
        $files += [pscustomobject]@{Root='runtime'; Path=$relative; Source=(Within $assets $relative)}
    }
    $dock = Within $assets 'obs-custom-dock'
    foreach ($file in Get-ChildItem -LiteralPath $dock -Recurse -File) {
        $relative = $file.FullName.Substring($assets.Length + 1).Replace('\', '/')
        $files += [pscustomobject]@{Root='runtime'; Path=$relative; Source=(Within $assets $relative)}
    }
    New-Item -ItemType Directory -Path $Backup | Out-Null
    $setup = FullPath (Join-Path $PSScriptRoot '../../../build/OBSReplayKit.exe')
    if ((Get-Item -LiteralPath $setup).Length -lt 10MB) { throw 'Rebuild the packaged installer before installing the host.' }
    $metadata = Join-Path $Backup 'setup-metadata.json'
    [IO.File]::WriteAllText($metadata, ([pscustomobject]@{
        sha256=(Hash $setup); size=(Get-Item -LiteralPath $setup).Length
        version=(Get-Item -LiteralPath $setup).VersionInfo.ProductVersion; cachedAt=[DateTime]::UtcNow.ToString('o')
    } | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    $files += [pscustomobject]@{Root='cache'; Path='OBSReplayKitSetup.exe'; Source=$setup}
    $files += [pscustomobject]@{Root='cache'; Path='OBSReplayKitSetup.exe.json'; Source=$metadata}
    $entries = @()
    foreach ($file in $files) {
        $root = if ($file.Root -eq 'obs') { $ObsRoot } elseif ($file.Root -eq 'cache') { $SetupCacheRoot } else { $ReplayKitRoot }
        $target = Within $root $file.Path
        $relative = $file.Root + '/' + $file.Path
        $staged = Within $Backup ('staged/' + $relative)
        $newHash = Hash $file.Source
        CopyFile $file.Source $staged $newHash
        $existed = Test-Path -LiteralPath $target -PathType Leaf
        $oldHash = $null
        if ($existed) {
            $oldHash = Hash $target
            CopyFile $target (Within $Backup ('original/' + $relative)) $oldHash
        }
        $entries += [pscustomobject]@{Root=$file.Root; Path=$file.Path; Existed=$existed; OriginalHash=$oldHash; InstalledHash=$newHash}
    }
    $manifest = [pscustomobject]@{ObsRoot=$ObsRoot; ReplayKitRoot=$ReplayKitRoot; SetupCacheRoot=$SetupCacheRoot; ObsVersion=$native.ObsVersion; Files=$entries}
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    Write-Output "Prepared $($entries.Count) files with verified originals: $Backup"
    return
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.ObsRoot -ne $ObsRoot -or $manifest.ReplayKitRoot -ne $ReplayKitRoot -or $manifest.SetupCacheRoot -ne $SetupCacheRoot) {
    throw 'Backup installation paths do not match.'
}
foreach ($relative in $nativePaths) {
    if (@($manifest.Files | Where-Object { $_.Root -eq 'obs' -and $_.Path -eq $relative }).Count -ne 1) {
        throw 'Backup is missing a required native component.'
    }
}
$seen = @{}
foreach ($file in $manifest.Files) {
    $relative = $file.Root + '/' + $file.Path
    if ($seen.ContainsKey($relative)) { throw 'Duplicate file in backup.' }
    $seen[$relative] = $true
    if ($file.Root -eq 'obs') {
        if ($file.Path -notin $nativePaths) { throw 'Unexpected OBS target in backup.' }
        $root = $ObsRoot
    } elseif ($file.Root -eq 'runtime') {
        if ($file.Path -notin @('scripts/helper/OBSReplayKit.exe', 'scripts/helper/helper_bootstrap.lua',
            'icons/player/play.svg', 'icons/player/pause.svg', 'icons/editor/play.svg', 'icons/editor/pause.svg') -and
            -not $file.Path.StartsWith('obs-custom-dock/', [StringComparison]::Ordinal)) { throw 'Unexpected runtime target in backup.' }
        $root = $ReplayKitRoot
    } elseif ($file.Root -eq 'cache') {
        if ($file.Path -notin @('OBSReplayKitSetup.exe', 'OBSReplayKitSetup.exe.json')) { throw 'Unexpected setup cache file.' }
        $root = $SetupCacheRoot
    } else { throw 'Unknown installation root.' }
    $target = Within $root $file.Path
    $current = if (Test-Path -LiteralPath $target -PathType Leaf) { Hash $target } else { $null }
    if ($Mode -eq 'Apply') {
        if ($current -ne $file.OriginalHash) { throw "File changed since backup: $target" }
        if ((Hash (Within $Backup ('staged/' + $relative))) -ne $file.InstalledHash) { throw 'Staged file changed.' }
    } else {
        if ($current -ne $file.InstalledHash -and $current -ne $file.OriginalHash) { throw "File changed after installation: $target" }
        if ($file.Existed -and (Hash (Within $Backup ('original/' + $relative))) -ne $file.OriginalHash) { throw 'Original backup changed.' }
    }
}
try {
    foreach ($file in $manifest.Files) {
        $root = if ($file.Root -eq 'obs') { $ObsRoot } elseif ($file.Root -eq 'cache') { $SetupCacheRoot } else { $ReplayKitRoot }
        $target = Within $root $file.Path
        $relative = $file.Root + '/' + $file.Path
        if ($Mode -eq 'Apply') {
            CopyFile (Within $Backup ('staged/' + $relative)) $target $file.InstalledHash
        } elseif ($file.Existed) {
            CopyFile (Within $Backup ('original/' + $relative)) $target $file.OriginalHash
        } elseif (Test-Path -LiteralPath $target -PathType Leaf) {
            Remove-Item -LiteralPath $target -Force
        }
    }
} catch {
    $failure = $_
    if ($Mode -eq 'Apply') {
        & $PSCommandPath -Mode Restore -Backup $Backup -ObsRoot $ObsRoot -ReplayKitRoot $ReplayKitRoot -SetupCacheRoot $SetupCacheRoot
    }
    throw $failure
}
Write-Output "$Mode completed for $($manifest.Files.Count) files. Backup: $Backup"

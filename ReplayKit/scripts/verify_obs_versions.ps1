# fails the build when a bundled obs plugin imports anything the tested obs of a line in config/obs-versions.json does not export: windows then refuses to load the dll and obs lists the plugin as failed, which is how obs 33 broke the plugins built for obs 32

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'obs_versions.ps1')


foreach ($name in 'SDL3.dll', 'uiohook.dll') {
    $dependency = Join-Path $ReplayKitPlugins "input-overlay\$name"
    if (-not (Test-Path -LiteralPath $dependency -PathType Leaf)) { throw "input-overlay is missing $dependency" }
}

# each bundled plugin dll, by the archive it ships in, or loose when it ships as itself
$plugins = @(
    @{ Name = 'replaykit.dll'; Path = Join-Path $ReplayKitPlugins 'replaykit.dll' },
    @{ Name = 'input-overlay.dll'; Path = Join-Path $ReplayKitPlugins 'input-overlay\input-overlay.dll' },
    @{ Name = 'obs-shaderfilter.dll'; Archive = Join-Path $PSScriptRoot '..\plugins\obs-shaderfilter\obs-shaderfilter.zip'; Entry = 'obs-plugins/64bit/obs-shaderfilter.dll' },
    @{ Name = 'win-capture-audio.dll'; Path = Join-Path $ReplayKitPlugins 'win-capture-audio\win-capture-audio.dll' }
)

Add-Type -AssemblyName System.IO.Compression.FileSystem
$work = Join-Path $ReplayKitCache 'verification'
$scratch = Join-Path $work ('plugins-' + [Guid]::NewGuid().ToString('N'))
Assert-BuildPath $scratch
New-Item -ItemType Directory -Force -Path $scratch | Out-Null
try {
    foreach ($plugin in $plugins) {
        if ($plugin.Path) {
            if (-not (Test-Path -LiteralPath $plugin.Path)) { throw "$($plugin.Name) is not built: $($plugin.Path)" }
            continue
        }
        $zip = [IO.Compression.ZipFile]::OpenRead($plugin.Archive)
        try {
            $entry = $zip.GetEntry($plugin.Entry)
            if (-not $entry) { throw "$($plugin.Archive) has no $($plugin.Entry)" }
            $plugin.Path = Join-Path $scratch $plugin.Name
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $plugin.Path, $true)
        } finally { $zip.Dispose() }
    }

    $failures = New-Object System.Collections.Generic.List[string]
    foreach ($line in (Read-ObsVersions).lines) {
        $bin = Get-ObsBinaries $line
        foreach ($plugin in $plugins) {
            $missing = Get-MissingImports $plugin.Path $bin
            if ($missing.Count -eq 0) { continue }
            $failures.Add("OBS $($line.tested): $($plugin.Name) imports $($missing.Count) thing(s) it does not have: " + ($missing -join ', '))
        }
        Write-Output "OBS $($line.tested): checked $($plugins.Count) plugins"
    }
    if ($failures.Count -gt 0) { throw ("A bundled plugin would fail to load:`n" + ($failures -join "`n")) }
    Write-Output 'every bundled plugin loads in every OBS line of obs-versions.json'
} finally {
    Remove-WorkDirectory $work $scratch
}

# fails the build when a bundled obs plugin imports anything the tested obs of a line in assets/obs-versions.json does not export: windows then refuses to load the dll and obs lists the plugin as failed, which is how obs 33 broke the plugins built for obs 32

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'obs_versions.ps1')
$assets = Join-Path $PSScriptRoot '..\assets'

# each bundled plugin dll, by the archive it ships in, or loose when it ships as itself
$plugins = @(
    @{ Name = 'replaykit.dll'; Path = Join-Path $assets 'obs-plugins\replaykit\bin\64bit\replaykit.dll' },
    @{ Name = 'input-overlay.dll'; Archive = Join-Path $assets 'input-overlay-plugin.zip'; Entry = 'input-overlay/input-overlay.dll' },
    @{ Name = 'obs-shaderfilter.dll'; Archive = Join-Path $assets 'obs-shaderfilter.zip'; Entry = 'obs-plugins/64bit/obs-shaderfilter.dll' },
    @{ Name = 'win-capture-audio.dll'; Archive = Join-Path $assets 'win-capture-audio.zip'; Entry = 'obs-plugins/64bit/win-capture-audio.dll' }
)

Add-Type -AssemblyName System.IO.Compression.FileSystem
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('ReplayKit-obs-verify-' + [Guid]::NewGuid().ToString('N'))
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
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

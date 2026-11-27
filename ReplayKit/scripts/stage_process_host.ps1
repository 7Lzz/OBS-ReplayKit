# puts the flat process host files of build/payload/process-host/<version>/ into the release bundle at the install paths their manifest names, which is the layout the installer of every release reads; the setup csproj runs it while it packs the bundle
param(
    [Parameter(Mandatory = $true)][string]$Stage
)
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'obs_versions.ps1')
$source = Join-Path $ReplayKitPayload 'process-host'
foreach ($line in (Read-ObsVersions).lines) {
    $version = $line.tested
    $folder = Join-Path $source $version
    Assert-BuildPath $folder
    $manifestPath = Join-Path $folder 'manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "Missing process host manifest: $manifestPath" }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.ObsVersion -ne $version) { throw "The process host manifest in $version is for OBS $($manifest.ObsVersion)." }
    $target = [IO.Path]::GetFullPath((Join-Path $Stage $version)).TrimEnd('\') + '\'
    $expected = @(Get-HostComponentPaths $line)
    if (@($manifest.Files).Count -ne $expected.Count) { throw "The process host manifest for OBS $version is incomplete." }
    $leaves = @{}
    foreach ($file in @($manifest.Files)) {
        $relative = [string]$file.Path
        if ($relative -cnotin $expected) { throw "Unexpected process host path: $relative" }
        # the manifest path is pasted under the bundle folder, so it must stay inside it
        if ($relative -notmatch '^[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*$' -or $relative -match '\.(?:/|$)') { throw "Unsafe process host path: $relative" }
        $to = [IO.Path]::GetFullPath((Join-Path $target $relative.Replace('/', '\')))
        if (-not $to.StartsWith($target, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe process host path: $relative" }
        $leaf = Split-Path -Leaf $relative
        if ($leaves.ContainsKey($leaf)) { throw "Two process host files share the name $leaf" }
        $leaves[$leaf] = $true
        $from = Join-Path $folder $leaf
        if (-not (Test-Path -LiteralPath $from -PathType Leaf)) { throw "Missing process host file: $from" }
        $stream = [IO.File]::OpenRead($from)
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $digest = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
        finally { $sha.Dispose(); $stream.Dispose() }
        if ($digest -ne $file.Sha256) { throw "The process host file $leaf does not match its manifest." }
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $to) | Out-Null
        Copy-Item -LiteralPath $from -Destination $to -Force
    }
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $target 'manifest.json') -Force
}

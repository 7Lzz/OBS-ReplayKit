# puts the flat process host files of dist/process-host/<version>/ into the release bundle at the install paths their manifest names, which is the layout the installer of every release reads; the setup csproj runs it while it packs the bundle
param(
    [Parameter(Mandatory = $true)][string]$Stage
)
$ErrorActionPreference = 'Stop'

$source = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\dist\process-host'))
$versions = @(Get-ChildItem -LiteralPath $source -Directory)
if ($versions.Count -eq 0) { throw "No process host builds in $source" }
foreach ($folder in $versions) {
    $manifestPath = Join-Path $folder.FullName 'manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath)) { throw "Missing process host manifest: $manifestPath" }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.ObsVersion -ne $folder.Name) { throw "The process host manifest in $($folder.Name) is for OBS $($manifest.ObsVersion)." }
    $target = Join-Path $Stage $folder.Name
    $leaves = @{}
    foreach ($file in @($manifest.Files)) {
        $relative = [string]$file.Path
        # the manifest path is pasted under the bundle folder, so it must stay inside it
        if ($relative -notmatch '^[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*$') { throw "Unsafe process host path: $relative" }
        $leaf = Split-Path -Leaf $relative
        if ($leaves.ContainsKey($leaf)) { throw "Two process host files share the name $leaf" }
        $leaves[$leaf] = $true
        $from = Join-Path $folder.FullName $leaf
        if (-not (Test-Path -LiteralPath $from -PathType Leaf)) { throw "Missing process host file: $from" }
        if ((Get-FileHash -LiteralPath $from -Algorithm SHA256).Hash -ne $file.Sha256) { throw "The process host file $leaf does not match its manifest." }
        $to = Join-Path $target $relative.Replace('/', '\')
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $to) | Out-Null
        Copy-Item -LiteralPath $from -Destination $to -Force
    }
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $target 'manifest.json') -Force
}

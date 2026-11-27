$BuildLayoutFile = Join-Path $PSScriptRoot '..\Directory.Build.props'
$layout = ([xml][IO.File]::ReadAllText($BuildLayoutFile)).Project.PropertyGroup
$ReplayKitBuildRoot = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $PSScriptRoot) $layout.BuildDirectory)).TrimEnd('\')
$ReplayKitPayload = Join-Path $ReplayKitBuildRoot $layout.BuildPayloadDirectory.TrimEnd('\')
$ReplayKitPlugins = Join-Path $ReplayKitPayload $layout.BuildPluginsDirectory.TrimEnd('\')
$ReplayKitCache = Join-Path $ReplayKitBuildRoot $layout.BuildCacheDirectory.TrimEnd('\')
$ReplayKitGenerated = Join-Path $ReplayKitCache $layout.BuildGeneratedDirectory.TrimEnd('\')
$ReplayKitDependencies = Join-Path $ReplayKitCache $layout.BuildDependencyDirectory.TrimEnd('\')
$ReplayKitNative = Join-Path $ReplayKitCache 'native'

function Assert-BuildPath([string]$path) {
    $directory = [IO.Path]::GetFullPath($path).TrimEnd('\')
    while ($directory -and (Split-Path -Parent $directory)) {
        if ((Test-Path -LiteralPath $directory) -and
            ((Get-Item -LiteralPath $directory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Build paths cannot use junctions or symlinks: $directory"
        }
        $directory = Split-Path -Parent $directory
    }
}

function Remove-WorkDirectory([string]$workDir, [string]$path) {
    $root = [IO.Path]::GetFullPath($workDir).TrimEnd('\') + '\'
    $target = [IO.Path]::GetFullPath($path).TrimEnd('\')
    if (-not $target.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Build cleanup is outside the work directory: $target"
    }
    Assert-BuildPath $target
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
}

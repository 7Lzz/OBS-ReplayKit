# writes dist/version.json and Version.props from version.json in the ReplayKit folder, the one place the release version is set; build.bat runs it before every build, VersionInfo.cs reads the version back from the setup assembly, and the installer writes the live version.json itself (Installer.WriteReplaykitVersion)
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$sourcePath = Join-Path $repoRoot "version.json"
$outPath = Join-Path $repoRoot "dist\version.json"
$propsPath = Join-Path $repoRoot "Version.props"

if (-not (Test-Path -LiteralPath $sourcePath)) {
    throw "Missing version source: $sourcePath"
}
$source = [System.IO.File]::ReadAllText($sourcePath) | ConvertFrom-Json
$version = [string]$source.version
# up to four numbers, the most a windows file version holds
if ($version -notmatch '^\d{1,5}(\.\d{1,5}){0,3}$') {
    throw "The version in $sourcePath must be numbers joined by dots, like 1.4.3 (it is '$version')."
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $outPath) | Out-Null
$json = "{`r`n  ""version"": ""$version""`r`n}`r`n"
[System.IO.File]::WriteAllText($outPath, $json, [System.Text.UTF8Encoding]::new($false))

# FileVersion/AssemblyVersion are the windows 4-part Major.Minor.Build.Revision -- pad whatever version.json has out to 4 parts rather than requiring it to already be in that shape.
$parts = @($version -split '\.' | Select-Object -First 4)
while ($parts.Count -lt 4) { $parts += "0" }
$fourPart = $parts -join "."

# both csproj files import this -- without it every build shipped 1.0.0.0 in its own Win32 version resource (dotnets default when no <Version> is set) regardless of what the source version said, which is one of the things that makes a binary look machine-generated rather than a maintained release to some av heuristics. gitignored and regenerated every build, same as dist/version.json above, so version.json stays the only place a human edits a version number.
$propsXml = @"
<Project>
  <PropertyGroup>
    <Version>$version</Version>
    <FileVersion>$fourPart</FileVersion>
    <AssemblyVersion>$fourPart</AssemblyVersion>
    <InformationalVersion>$version</InformationalVersion>
  </PropertyGroup>
</Project>
"@
[System.IO.File]::WriteAllText($propsPath, $propsXml, [System.Text.UTF8Encoding]::new($false))

Write-Host "    ReplayKit version $version"

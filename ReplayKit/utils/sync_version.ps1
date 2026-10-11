# writes version.json and Version.props from ReplayKitSetup/VersionInfo.cs, the one place the release version is set; build.bat runs it before every build and the installer writes the live version.json itself (Installer.WriteReplaykitVersion)
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$versionInfoPath = Join-Path $repoRoot "ReplayKitSetup\VersionInfo.cs"
$outPath = Join-Path $repoRoot "assets\obs-studio\obs-replayKit\version.json"
$propsPath = Join-Path $repoRoot "Version.props"

if (-not (Test-Path -LiteralPath $versionInfoPath)) {
    throw "Missing version source: $versionInfoPath"
}
$match = Select-String -LiteralPath $versionInfoPath -Pattern 'Version\s*=\s*"([^"]+)"' | Select-Object -First 1
if (-not $match) {
    throw "Could not find Version in $versionInfoPath"
}
$version = $match.Matches[0].Groups[1].Value

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $outPath) | Out-Null
$json = "{`r`n  ""version"": ""$version""`r`n}`r`n"
[System.IO.File]::WriteAllText($outPath, $json, [System.Text.UTF8Encoding]::new($false))

# FileVersion/AssemblyVersion are the windows 4-part Major.Minor.Build.Revision -- pad whatever VersionInfo.cs has out to 4 parts rather than requiring it to already be in that shape.
$parts = @($version -split '\.' | Select-Object -First 4)
while ($parts.Count -lt 4) { $parts += "0" }
$fourPart = $parts -join "."

# both csproj files import this -- without it every build shipped 1.0.0.0 in its own Win32 version resource (dotnets default when no <Version> is set) regardless of what VersionInfo.cs actually said, which is one of the things that makes a binary look machine-generated rather than a maintained release to some av heuristics. gitignored and regenerated every build, same as version.json above, so VersionInfo.cs stays the only place a human edits a version number.
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

# publish the helper with its managed dependencies merged into the executable.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectDir = Join-Path $repoRoot 'apps\helper'
$csproj = Join-Path $projectDir 'ReplayKitHelper.csproj'
$outDir = Join-Path $repoRoot 'dist\helper'
$publishTmp = Join-Path $projectDir 'bin\Release\publish'
$stampPath = Join-Path $projectDir 'obj\helper-build.json'
$requiredFiles = @('OBSReplayKit.exe', 'WebView2Loader.dll')

function Fingerprint([string[]]$paths) {
    return (@($paths | Sort-Object | ForEach-Object {
        if (-not (Test-Path -LiteralPath $_ -PathType Leaf)) { "missing:$_" }
        else { "$_`t$((Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash)" }
    }) -join "`n")
}

$sourceFiles = Get-ChildItem -LiteralPath $projectDir -Recurse -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.Extension -in '.cs', '.csproj', '.manifest', '.targets', '.bin' }
# the files the helper compiles from outside its folder come from the csproj itself, so a new shared file cannot be left out of the fingerprint
$linkedFiles = @(([xml](Get-Content -LiteralPath $csproj -Raw)).Project.ItemGroup.Compile | Where-Object { $_.Include -and $_.Include.StartsWith('..\') } | ForEach-Object { [IO.Path]::GetFullPath((Join-Path $projectDir $_.Include)) })
if ($linkedFiles.Count -eq 0) { throw 'The helper csproj lists no shared source files; the fingerprint would miss them.' }
foreach ($file in $linkedFiles) { if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Shared helper source is missing: $file" } }
$inputs = @($sourceFiles.FullName) + $linkedFiles + @(
    $PSCommandPath,
    (Join-Path $repoRoot 'Version.props'),
    (Join-Path $repoRoot 'lua\helper\obs-replaykit.ico')
)
$outputs = @($requiredFiles | ForEach-Object { Join-Path $outDir $_ })
$inputHash = Fingerprint $inputs
$outputHash = Fingerprint $outputs
if (Test-Path -LiteralPath $stampPath) {
    $stamp = Get-Content -LiteralPath $stampPath -Raw | ConvertFrom-Json
    if ($stamp.inputs -eq $inputHash -and $stamp.outputs -eq $outputHash) {
        Write-Output 'Current: helper and all dependencies -- skipping rebuild.'
        exit 0
    }
}

& dotnet publish $csproj -c Release -o $publishTmp --nologo
if ($LASTEXITCODE -ne 0) { throw 'Helper publish failed.' }
foreach ($name in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishTmp $name) -PathType Leaf)) {
        throw "Helper publish is missing $name."
    }
}
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
foreach ($name in $requiredFiles) {
    Copy-Item -LiteralPath (Join-Path $publishTmp $name) -Destination (Join-Path $outDir $name) -Force
}
# delete obsolete managed companions only after the merged executable is successfully published.
foreach ($name in @('Newtonsoft.Json.dll', 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll', 'OBSReplayKit.exe.config')) {
    $obsolete = Join-Path $outDir $name
    if (Test-Path -LiteralPath $obsolete -PathType Leaf) { Remove-Item -LiteralPath $obsolete -Force }
}
@{ inputs = $inputHash; outputs = (Fingerprint $outputs) } | ConvertTo-Json |
    Set-Content -LiteralPath $stampPath -Encoding UTF8
Write-Output 'Built: helper and required dependencies.'

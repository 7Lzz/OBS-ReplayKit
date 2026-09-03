# publishes ReplayKitHelper straight into assets\ (same net48+ilrepack pattern build.ps1 uses for the tray plugin) -- ships as the exe plus two loose sibling dlls (WebView2Loader.dll, which cant be merged; Newtonsoft.Json.dll, which isnt merged on purpose, see ReplayKitHelper.csproj) rather than one fully self-contained file. skips dotnet publish entirely when the bundled exe is already newer than every source file, since a rebuild that produces byte-identical output still bumps the tracked exes mtime and shows up as a spurious git diff.
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectDir = Join-Path $repoRoot "ReplayKitHelper"
$csproj = Join-Path $projectDir "ReplayKitHelper.csproj"
$outPath = Join-Path $repoRoot "assets\obs-studio\obs-replayKit\scripts\helper\OBSReplayKit.exe"
$loaderOutPath = Join-Path $repoRoot "assets\obs-studio\obs-replayKit\scripts\helper\WebView2Loader.dll"
$jsonOutPath = Join-Path $repoRoot "assets\obs-studio\obs-replayKit\scripts\helper\Newtonsoft.Json.dll"
$publishTmp = Join-Path $repoRoot "publish_helper_tmp"

# same shape as the native plugin build script's Test-TrayPluginOutputCurrent -- true only when $targetPath exists and is newer than every input.
function Test-HelperOutputCurrent([string]$targetPath, [string[]]$inputPaths) {
    if (-not (Test-Path -LiteralPath $targetPath -PathType Leaf)) { return $false }
    $target = Get-Item -LiteralPath $targetPath
    foreach ($inputPath in $inputPaths) {
        $inputItem = Get-Item -LiteralPath $inputPath
        if ($inputItem.LastWriteTimeUtc -gt $target.LastWriteTimeUtc) { return $false }
    }
    return $true
}

# excludes bin\/obj\ so a leftover compiled-output .cs (e.g. AssemblyInfo.cs under obj\) never counts as a source change; $PSCommandPath is included so an edit to this script itself also invalidates the cache.
$sourceFiles = Get-ChildItem -LiteralPath $projectDir -Recurse -Include *.cs, *.csproj, *.manifest, *.targets |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
$buildInputs = @($sourceFiles.FullName) + @($PSCommandPath)

if (Test-HelperOutputCurrent $outPath $buildInputs) {
    $built = Get-Item -LiteralPath $outPath
    $kb = [int][Math]::Ceiling($built.Length / 1024)
    Write-Output "Current: $($built.FullName)  ($kb KB) -- skipping rebuild."
    exit 0
}

if (Test-Path -LiteralPath (Join-Path $projectDir "bin")) { Remove-Item (Join-Path $projectDir "bin") -Recurse -Force }
if (Test-Path -LiteralPath (Join-Path $projectDir "obj")) { Remove-Item (Join-Path $projectDir "obj") -Recurse -Force }
if (Test-Path -LiteralPath $publishTmp) { Remove-Item $publishTmp -Recurse -Force }

& dotnet publish $csproj -c Release -o $publishTmp --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Copy-Item -LiteralPath (Join-Path $publishTmp "OBSReplayKit.exe") -Destination $outPath -Force
Copy-Item -LiteralPath (Join-Path $publishTmp "WebView2Loader.dll") -Destination $loaderOutPath -Force
Copy-Item -LiteralPath (Join-Path $publishTmp "Newtonsoft.Json.dll") -Destination $jsonOutPath -Force
Remove-Item $publishTmp -Recurse -Force
if (Test-Path -LiteralPath (Join-Path $projectDir "bin")) { Remove-Item (Join-Path $projectDir "bin") -Recurse -Force }
if (Test-Path -LiteralPath (Join-Path $projectDir "obj")) { Remove-Item (Join-Path $projectDir "obj") -Recurse -Force }

$built = Get-Item -LiteralPath $outPath
$kb = [int][Math]::Ceiling($built.Length / 1024)
Write-Output "Built: $($built.FullName)  ($kb KB)"

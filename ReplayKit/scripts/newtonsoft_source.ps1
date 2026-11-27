# checks out newtonsoft.json 13.0.3 at its pinned commit for the setup, which compiles it from source instead of shipping the dll beside the exe or merging it in
$ErrorActionPreference = 'Stop'
$commit = '0a2e291c0d9c0c7675d445703e51750363a549ef'
. (Join-Path $PSScriptRoot 'build_layout.ps1')
$source = Join-Path $ReplayKitDependencies 'newtonsoft'
Assert-BuildPath $source
New-Item -ItemType Directory -Force -Path $ReplayKitDependencies | Out-Null
if (-not (Test-Path -LiteralPath (Join-Path $source '.git'))) {
    git clone --quiet --depth 1 --branch 13.0.3 https://github.com/JamesNK/Newtonsoft.Json.git $source
    if ($LASTEXITCODE -ne 0) { throw 'The Newtonsoft.Json source checkout failed.' }
}
$head = git -C $source rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $head -ne $commit) { throw "The Newtonsoft.Json source is at $head, not the pinned $commit." }
$changes = git -C $source status --porcelain
if ($LASTEXITCODE -ne 0 -or $changes) { throw 'The Newtonsoft.Json source checkout has local changes.' }
Write-Host "    Newtonsoft.Json 13.0.3 source at $commit"

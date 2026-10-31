# dot-sourced by the plugin builds and verify_obs_versions.ps1: reads assets/obs-versions.json, fetches the obs binaries of a lines tested version from its official release zip (hash checked against the manifest, cached under %LOCALAPPDATA%\ReplayKit\obs-versions), puts together the sdk a plugin is built against for that version, and lists what a dll imports that an obs does not export

$ObsVersionsManifest = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\assets\obs-versions.json'))
$ObsVersionsCache = Join-Path $env:LOCALAPPDATA 'ReplayKit\obs-versions'

function Read-ObsVersions {
    $manifest = Get-Content -LiteralPath $ObsVersionsManifest -Raw | ConvertFrom-Json
    if (-not $manifest.lines -or @($manifest.lines).Count -eq 0) { throw 'obs-versions.json lists no lines.' }
    foreach ($line in $manifest.lines) {
        if ($line.tested -notmatch '^\d+\.\d+\.\d+$') { throw "obs-versions.json has a bad tested version: $($line.tested)" }
        if ($line.portable.url -notmatch '^https://github\.com/obsproject/obs-studio/releases/download/[0-9A-Za-z.\-]+/[0-9A-Za-z.\-]+\.zip$') { throw "obs-versions.json line $($line.tested) has no official portable zip." }
        if ($line.portable.sha256 -notmatch '^[0-9a-f]{64}$') { throw "obs-versions.json line $($line.tested) has a bad portable sha256." }
    }
    return $manifest
}

# the plugins are built against the oldest line, so they only import what every supported obs exports; verify_obs_versions.ps1 then checks them against each line
function Get-ObsBuildLine { return @((Read-ObsVersions).lines)[0] }

function Find-VsBuildTools {
    foreach ($year in @('2022', '2019')) {
        $root = "C:\Program Files (x86)\Microsoft Visual Studio\$year\BuildTools\VC\Tools\MSVC"
        if (-not (Test-Path $root)) { continue }
        $ver = Get-ChildItem $root -Directory | Sort-Object Name -Descending | Select-Object -First 1
        if ($ver) { return Join-Path $ver.FullName 'bin\Hostx64\x64' }
    }
    throw "No VS2022/2019 BuildTools MSVC toolset found. Install 'Desktop development with C++' first."
}

# vcvars64.bat shells out to vswhere.exe itself before it ever sets up compiler/linker/sdk paths, and doesnt put the installers own tools dir on path for that lookup -- caused a harmless "'vswhere.exe' is not recognized..." on every build. has to go before vcvars runs, not after: vcvars writes that message to stderr, which is why it was never visible in $envDump below to catch and suppress there. this is vswhere.exes one stable, documented location since vs2017.
function Enter-VsBuildEnvironment {
    $ninja = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe"
    $vcvars = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
    $env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer" + ";" + $env:PATH
    $envDump = cmd /c "`"$vcvars`" && set"
    foreach ($text in $envDump) {
        if ($text -match '^([^=]+)=(.*)$') { [System.Environment]::SetEnvironmentVariable($matches[1], $matches[2]) }
    }
    $env:PATH = (Split-Path $ninja) + ";" + $env:PATH
}

function Remove-WorkDirectory([string]$workDir, [string]$path) {
    $root = [IO.Path]::GetFullPath($workDir).TrimEnd('\') + '\'
    $target = [IO.Path]::GetFullPath($path).TrimEnd('\')
    if (-not $target.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Build cleanup is outside the work directory: $target"
    }
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
}

# the dlls of bin\64bit of a lines tested obs, which is all an import library or an import check needs
function Get-ObsBinaries($line) {
    $version = $line.tested
    $dir = Join-Path $ObsVersionsCache "$version\bin\64bit"
    $marker = Join-Path $ObsVersionsCache "$version\.replaykit-binaries"
    if ((Test-Path -LiteralPath $marker) -and ((Get-Content -LiteralPath $marker -Raw).Trim() -eq $line.portable.sha256) -and (Test-Path -LiteralPath (Join-Path $dir 'obs.dll'))) { return $dir }

    New-Item -ItemType Directory -Force -Path $ObsVersionsCache | Out-Null
    $zip = Join-Path $ObsVersionsCache ([IO.Path]::GetFileName($line.portable.url))
    $hash = if (Test-Path -LiteralPath $zip) { (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() } else { '' }
    if ($hash -ne $line.portable.sha256) {
        Write-Host "downloading OBS $version (about 200 MB, kept for later builds)..."
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $line.portable.url -OutFile $zip
        $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -ne $line.portable.sha256) {
            Remove-Item -LiteralPath $zip -Force
            throw "The OBS $version download does not match obs-versions.json (sha256 $hash)."
        }
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName -notmatch '^bin/64bit/[^/]+\.dll$') { continue }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $dir $entry.Name), $true)
        }
    } finally { $archive.Dispose() }
    if (-not (Test-Path -LiteralPath (Join-Path $dir 'obs.dll'))) { throw "The OBS $version zip has no bin\64bit\obs.dll." }
    Set-Content -LiteralPath $marker -Value $hash -Encoding ascii
    return $dir
}

# the qt sdk is only reused while it still holds every file it was extracted with. a cache that lost files passes a check on one cmake file and then dies in cmake configure, so an extraction leaves its file count in a marker and a short count counts as missing
$QtMarkerName = '.replaykit-extracted'
function Get-TreeFileCount([string]$dir) {
    $count = 0
    foreach ($file in [IO.Directory]::EnumerateFiles($dir, '*', [IO.SearchOption]::AllDirectories)) {
        if ([IO.Path]::GetFileName($file) -ne $QtMarkerName) { $count++ }
    }
    return $count
}
function Test-QtSdk([string]$dir) {
    $marker = Join-Path $dir $QtMarkerName
    if (-not (Test-Path -LiteralPath (Join-Path $dir 'lib\cmake\Qt6\Qt6Config.cmake'))) { return $false }
    if (-not (Test-Path -LiteralPath $marker)) { return $false }
    $expected = 0
    if (-not [int]::TryParse((Get-Content -LiteralPath $marker -Raw).Trim(), [ref]$expected)) { return $false }
    return (Get-TreeFileCount $dir) -ge $expected
}

# the header checkout is only reused while git still sees every file of it in place, since files cleaned out of a checkout show up as deleted
function Test-HeaderCheckout([string]$dir) {
    if (-not (Test-Path -LiteralPath (Join-Path $dir 'libobs\obs.h'))) { return $false }
    if (-not (Test-Path -LiteralPath (Join-Path $dir '.git'))) { return $false }
    $status = @(git -C $dir status --porcelain)
    if ($LASTEXITCODE -ne 0) { return $false }
    return -not ($status | Where-Object { $_ -match '^(.D|D.)' })
}

function New-ImportLib([string]$dllPath, [string]$dllName, [string]$outDir, [string]$outName) {
    $tools = Find-VsBuildTools
    $exportsText = & (Join-Path $tools 'dumpbin.exe') /exports $dllPath
    if ($LASTEXITCODE -ne 0) { throw "Could not inspect exports: $dllPath" }
    $names = New-Object System.Collections.Generic.List[string]
    $inTable = $false
    foreach ($text in $exportsText) {
        if ($text -match '^\s*ordinal\s+hint\s+RVA\s+name') { $inTable = $true; continue }
        if (-not $inTable) { continue }
        if ($text -match '^\s*\d+\s+[0-9A-Fa-f]+\s+[0-9A-Fa-f]+\s+(\S+)') {
            $sym = ($matches[1] -split '\s*=\s*')[0]
            if ($sym) { $names.Add($sym) }
        }
    }
    if ($names.Count -eq 0) { throw "no exports parsed for $dllPath" }
    $defPath = Join-Path $outDir "$outName.def"
    @("LIBRARY $dllName", 'EXPORTS') + ($names | ForEach-Object { "    $_" }) | Set-Content -LiteralPath $defPath -Encoding ascii
    & (Join-Path $tools 'lib.exe') "/def:$defPath" "/out:$(Join-Path $outDir "$outName.lib")" '/machine:x64' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not generate import library: $outName" }
}

# what a plugin is built against for a line, under $workDir: the libobs and frontend api headers at its obs-studio tag, the obs-deps qt6 package that obs version was built with, and obs.lib / obs-frontend-api.lib made from its own dlls
function Get-ObsSdk($line, [string]$workDir) {
    $version = $line.tested
    New-Item -ItemType Directory -Force -Path $workDir | Out-Null

    # gh prints the base64 content wrapped at ~60 chars/line, and powershell splits that captured output into one array element per line, so it has to be rejoined into a single string before decoding -- decoding line-by-line silently corrupts anything past the first line instead of erroring.
    Write-Host "reading CMakePresets.json for obs-studio tag $version..."
    $presetsLines = gh api "repos/obsproject/obs-studio/contents/CMakePresets.json?ref=$version" --jq '.content'
    if ($LASTEXITCODE -ne 0) { throw 'Could not read OBS dependency metadata.' }
    $presetsJson = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($presetsLines -join ''))
    $depsPreset = ($presetsJson | ConvertFrom-Json).configurePresets | Where-Object { $_.name -eq 'dependencies' } | Select-Object -First 1
    $depsVersion = $depsPreset.vendor.'obsproject.com/obs-studio'.dependencies.qt6.version
    if (-not $depsVersion) { throw "could not find qt6 dependency version in CMakePresets.json for $version" }
    Write-Host "obs-deps tag for OBS ${version}: $depsVersion"

    $headers = Join-Path $workDir "obs-src-$version"
    if (-not (Test-HeaderCheckout $headers)) {
        Write-Host "checking out obs-studio $version headers (libobs/, frontend/api/)..."
        Remove-WorkDirectory $workDir $headers
        git clone --filter=blob:none --no-checkout --depth 1 --branch $version https://github.com/obsproject/obs-studio.git $headers | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'OBS header checkout failed.' }
        git -C $headers sparse-checkout set --no-cone libobs frontend/api | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'OBS sparse checkout failed.' }
        git -C $headers checkout $version | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'OBS version checkout failed.' }
    }
    $obsConfigPath = Join-Path $headers 'libobs\obsconfig.h'
    if (-not (Test-Path $obsConfigPath)) {
        # cmake normally generates this from obsconfig.h.in; these plugins never read the values it would carry (install paths, optional feature flags), so literal placeholders are fine.
        "#pragma once`r`n#define OBS_RELEASE_CANDIDATE 0`r`n#define OBS_BETA 0`r`n" | Set-Content -LiteralPath $obsConfigPath -Encoding ascii -NoNewline
    }

    $qt = Join-Path $workDir "qt6-$depsVersion"
    if (-not (Test-QtSdk $qt)) {
        Write-Host "downloading obs-deps qt6 ($depsVersion, ~250MB)..."
        $qtZip = Join-Path $workDir 'qt6.zip'
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri "https://github.com/obsproject/obs-deps/releases/download/$depsVersion/windows-deps-qt6-$depsVersion-x64.zip" -OutFile $qtZip
        Remove-WorkDirectory $workDir $qt
        Expand-Archive -Path $qtZip -DestinationPath $qt -Force
        Remove-Item $qtZip
        Set-Content -LiteralPath (Join-Path $qt $QtMarkerName) -Value (Get-TreeFileCount $qt) -Encoding ascii
    }

    $bin = Get-ObsBinaries $line
    $implibs = Join-Path $workDir "implibs-$version"
    New-Item -ItemType Directory -Force -Path $implibs | Out-Null
    Write-Host "generating obs.lib / obs-frontend-api.lib from the OBS $version dlls..."
    New-ImportLib (Join-Path $bin 'obs.dll') 'obs.dll' $implibs 'obs'
    New-ImportLib (Join-Path $bin 'obs-frontend-api.dll') 'obs-frontend-api.dll' $implibs 'obs-frontend-api'

    return @{ Version = $version; Headers = $headers; Qt = $qt; Implibs = $implibs; Bin = $bin }
}

function Get-DllExports([string]$dumpbin, [string]$path) {
    $names = New-Object 'System.Collections.Generic.HashSet[string]'
    $inTable = $false
    foreach ($text in (& $dumpbin /exports $path)) {
        if ($text -match '^\s*ordinal\s+hint\s+RVA\s+name') { $inTable = $true; continue }
        if ($inTable -and $text -match '^\s*\d+\s+[0-9A-Fa-f]+\s+(?:[0-9A-Fa-f]+\s+)?(\S+)') { [void]$names.Add($matches[1]) }
    }
    if ($LASTEXITCODE -ne 0) { throw "Could not read the exports of $path" }
    return ,$names
}

# what a dll imports from the obs binaries in $obsBin that they do not export, as dll!name; an obs or qt dll obs does not ship counts as missing whole, since windows refuses to load a dll whose imports do not resolve and obs then lists the plugin as failed
function Get-MissingImports([string]$dll, [string]$obsBin) {
    $dumpbin = Join-Path (Find-VsBuildTools) 'dumpbin.exe'
    $imports = @{}
    $current = $null
    foreach ($text in (& $dumpbin /imports $dll)) {
        if ($text -match '^\s{4}(\S+\.dll)\s*$') { $current = $matches[1].ToLowerInvariant(); $imports[$current] = New-Object System.Collections.Generic.List[string]; continue }
        if ($current -and $text -match '^\s+[0-9A-Fa-f]+\s+(\S+)\s*$') { $imports[$current].Add($matches[1]) }
    }
    if ($LASTEXITCODE -ne 0) { throw "Could not read the imports of $dll" }
    $missing = New-Object System.Collections.Generic.List[string]
    foreach ($name in $imports.Keys) {
        $target = Join-Path $obsBin $name
        if (-not (Test-Path -LiteralPath $target)) {
            if ($name -match '^(obs|qt6|w32-pthreads)') { $missing.Add("$name (not shipped)") }
            continue
        }
        $exports = Get-DllExports $dumpbin $target
        foreach ($symbol in $imports[$name]) { if (-not $exports.Contains($symbol)) { $missing.Add("$name!$symbol") } }
    }
    return ,$missing
}

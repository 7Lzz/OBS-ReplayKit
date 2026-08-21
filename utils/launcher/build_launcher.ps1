# compile utils\launcher\obsreplaykit.cs into the launcher exe (defualt out: assets\obs-studio\obs-replayKit\scripts\helper\obsreplaykit.exe). uses csc.exe + system.management.automation.dll that ship with windows 10/11 -- no external dependencies. icon priority: caller -iconpath, then committed utils\icon\obs-replaykit.ico, then regenerated from obs64.exe via extract_obs_icon.ps1.

[CmdletBinding()]
param(
    # where to write the compiled launcher. the defualt lands it in the bundled assets so pyinstaller picks it up via --add-data.
    [string]$OutPath = '',

    # .ico for the win32 icon resource. empty -> try committed icon, then regenerated from obs64.exe, then no icon (generic .net console glyph).
    [string]$IconPath = ''
)

$ErrorActionPreference = 'Stop'

# $psscriptroot is sometimes empty during param() defualt evaluation depending on the host; resolve defaults here instead, after the body has started.
if (-not $OutPath) {
    $OutPath = Join-Path $PSScriptRoot '..\..\assets\obs-studio\obs-replayKit\scripts\helper\OBSReplayKit.exe'
}

function Find-CscExe {
    # .net framework 4.x is on every windows 8+ install. prefer 64-bit csc becuase the launcher targets x64 (matches obs64).
    $candidates = @(
        'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe',
        'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
    )
    foreach ($p in $candidates) {
        if (Test-Path -LiteralPath $p) { return $p }
    }
    return $null
}

function Find-SmaAssembly {
    # system.management.automation.dll location varies across windows versions / .net installs. try the well-known paths, then fall back to reflection lookup against the currently-loaded sma.
    $candidates = @(
        'C:\Windows\assembly\GAC_MSIL\System.Management.Automation\1.0.0.0__31bf3856ad364e35\System.Management.Automation.dll',
        'C:\Windows\Microsoft.Net\assembly\GAC_MSIL\System.Management.Automation\v4.0_3.0.0.0__31bf3856ad364e35\System.Management.Automation.dll'
    )
    foreach ($p in $candidates) {
        if (Test-Path -LiteralPath $p) { return $p }
    }
    try {
        # load sma into the current appdomain and read back its file path. this usually catches powershell-7-style paths too.
        Add-Type -AssemblyName System.Management.Automation -ErrorAction Stop
        $loc = [System.Management.Automation.PowerShell].Assembly.Location
        if ($loc -and (Test-Path -LiteralPath $loc)) { return $loc }
    } catch {
        # fall thru to error below
    }
    return $null
}

function Invoke-IconExtractor([string]$exePath, [string]$icoOut) {
    # defer to utils\extract_obs_icon.ps1, which reads rt_group_icon + rt_icon resources directly and writes a full multi-resolution .ico. icon.extractassociatedicon would only give us 32x32.
    $extractor = Join-Path $PSScriptRoot '..\extract_obs_icon.ps1'
    if (-not (Test-Path -LiteralPath $extractor)) { return $false }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $extractor `
        -ExePath $exePath -OutPath $icoOut | Out-Null
    return ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $icoOut))
}

function Test-LauncherOutputCurrent([string]$targetPath, [string[]]$inputPaths) {
    if (-not (Test-Path -LiteralPath $targetPath -PathType Leaf)) { return $false }
    $target = Get-Item -LiteralPath $targetPath
    foreach ($inputPath in $inputPaths) {
        if ([string]::IsNullOrWhiteSpace($inputPath)) { continue }
        if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf)) { return $false }
        $inputItem = Get-Item -LiteralPath $inputPath
        if ($inputItem.LastWriteTimeUtc -gt $target.LastWriteTimeUtc) { return $false }
    }
    return $true
}

$csc = Find-CscExe
if (-not $csc) {
    throw "csc.exe not found. .NET Framework 4.x is required to build the launcher (shipped with every Windows 10/11)."
}
Write-Host ("csc:    " + $csc)

$sma = Find-SmaAssembly
if (-not $sma) {
    throw "System.Management.Automation.dll not found. Install Windows PowerShell 5.1 (default on Windows 10/11)."
}
Write-Host ("SMA:    " + $sma)

# resolve / create the output directory.
$outDir = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($OutPath))
if (-not (Test-Path -LiteralPath $outDir)) {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}

# icon priority: caller -iconpath -> committed utils\icon\obs-replaykit.ico -> regenerate from obs64.exe. all three missing -> no icon (generic .net console glyph in task manager).
$bakedIco  = Join-Path $PSScriptRoot '..\icon\obs-replaykit.ico'
$winIcon   = ''
if ($IconPath -and (Test-Path -LiteralPath $IconPath)) {
    $winIcon = $IconPath
    Write-Host ("icon:   " + $winIcon + "  (caller-supplied)")
} elseif (Test-Path -LiteralPath $bakedIco) {
    $winIcon = $bakedIco
    Write-Host ("icon:   " + $winIcon + "  (baked)")
} else {
    $obsExe = 'C:\Program Files\obs-studio\bin\64bit\obs64.exe'
    if (Test-Path -LiteralPath $obsExe) {
        Write-Host ("icon:   " + $bakedIco + " missing; regenerating from " + $obsExe)
        if (Invoke-IconExtractor $obsExe $bakedIco) {
            $winIcon = $bakedIco
            Write-Host ("icon:   " + $winIcon + "  (regenerated)")
        } else {
            Write-Host "icon:   regeneration failed; launcher will build without an embedded icon"
        }
    } else {
        Write-Host "icon:   no baked icon and no obs64.exe to extract from; launcher will use the default .NET console icon"
    }
}

$srcPath = Join-Path $PSScriptRoot 'OBSReplayKit.cs'
if (-not (Test-Path -LiteralPath $srcPath)) {
    throw "Missing source: $srcPath"
}

$launcherInputs = @($srcPath, $csc, $sma)
if ($winIcon) { $launcherInputs += $winIcon }
if (Test-LauncherOutputCurrent $OutPath $launcherInputs) {
    $built = Get-Item -LiteralPath $OutPath
    $kb = [int][Math]::Ceiling($built.Length / 1024)
    Write-Host ''
    Write-Host ("Current: " + $built.FullName + "  (" + $kb + " KB)")
    return
}

$cscArgs = @(
    '/nologo',
    '/target:exe',
    '/platform:x64',
    '/optimize+',
    '/unsafe-',
    "/reference:`"$sma`"",
    "/out:`"$OutPath`""
)
if ($winIcon) {
    $cscArgs += "/win32icon:`"$winIcon`""
}
$cscArgs += "`"$srcPath`""

Write-Host ''
Write-Host ('Compiling: ' + (Split-Path -Leaf $OutPath))
$cscLine = ($cscArgs -join ' ')
& $csc @cscArgs
$exitCode = $LASTEXITCODE

if ($exitCode -ne 0) {
    throw "csc.exe failed with exit code $exitCode"
}

$built = Get-Item -LiteralPath $OutPath
$kb = [int][Math]::Ceiling($built.Length / 1024)
Write-Host ''
Write-Host ("Built: " + $built.FullName + "  (" + $kb + " KB)")

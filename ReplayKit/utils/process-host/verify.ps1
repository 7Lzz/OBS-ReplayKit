param([Parameter(Mandatory)][string]$Prototype)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($Prototype)
$exe = Join-Path $root 'bin/64bit/obs64.exe'
$profile = Join-Path $root 'verification-profile'
$roaming = Join-Path $profile 'Roaming'
$helper = Join-Path $roaming 'obs-studio/obs-replayKit/scripts/helper'
New-Item -ItemType Directory -Force -Path $helper,(Join-Path $profile 'Local'),(Join-Path $profile 'Temp') | Out-Null
$helperSource = Join-Path $PSScriptRoot '../../assets/obs-studio/obs-replayKit/scripts/helper/OBSReplayKit.exe'
Copy-Item -LiteralPath $helperSource -Destination (Join-Path $helper 'OBSReplayKit.exe') -Force
foreach ($arguments in @('--host-check', '--version', '--replaykit-helper --host-info')) {
    $start = [Diagnostics.ProcessStartInfo]::new($exe, $arguments)
    $start.WorkingDirectory = Split-Path -Parent $exe
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.EnvironmentVariables['APPDATA'] = $roaming
    $start.EnvironmentVariables['LOCALAPPDATA'] = Join-Path $profile 'Local'
    $start.EnvironmentVariables['USERPROFILE'] = $profile
    $start.EnvironmentVariables['TEMP'] = Join-Path $profile 'Temp'
    $start.EnvironmentVariables['TMP'] = Join-Path $profile 'Temp'
    $process = [Diagnostics.Process]::Start($start)
    $output = $process.StandardOutput.ReadToEndAsync()
    $errors = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(20000)) { throw "Verification did not finish: $arguments" }
    $text = $output.GetAwaiter().GetResult()
    $errorText = $errors.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) { throw "Verification failed ($arguments): $errorText" }
    if ($arguments.Contains('--host-info')) {
        $info = $text | ConvertFrom-Json
        if (-not $info.hosted -or [IO.Path]::GetFullPath($info.helperRoot) -ne [IO.Path]::GetFullPath($helper) -or
            -not $info.command.Contains('--replaykit-helper')) { throw 'The hosted helper did not preserve its assembly path or role.' }
    }
    Write-Output "PASS: $arguments"
    if ($text.Trim()) { Write-Output $text.Trim() }
    $process.Dispose()
}

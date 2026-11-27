param(
    [Parameter(Mandatory)][string]$Prototype,
    [string]$HelperAssembly = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../scripts/build_layout.ps1')
if (-not $HelperAssembly) { $HelperAssembly = Join-Path $ReplayKitPayload 'helper/OBSReplayKit.exe' }
$root = [IO.Path]::GetFullPath($Prototype)
if (-not (Test-Path -LiteralPath (Join-Path $root '.replaykit-portable') -PathType Leaf)) {
    throw 'Role verification requires a dedicated portable OBS fixture.'
}
$exe = Join-Path $root 'bin/64bit/replaykit-host/obs64.exe'
$profile = Join-Path $root 'verification-profile'
$roaming = Join-Path $profile 'Roaming'
# the helper goes where the role host looks for it, the place shared/ReplayKitFolder.cs names
$folderSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../../shared/ReplayKitFolder.cs') -Raw
$folderName = [regex]::Match($folderSource, 'const\s+string\s+Name\s*=\s*"([^"]+)"').Groups[1].Value
$helperExe = [regex]::Match($folderSource, 'const\s+string\s+HelperExe\s*=\s*@"([^"]+)"').Groups[1].Value
if (-not $folderName -or -not $helperExe) { throw 'Could not read the replaykit folder from shared/ReplayKitFolder.cs' }
$helperPath = Join-Path (Join-Path (Join-Path $roaming 'obs-studio') $folderName) $helperExe
$helper = Split-Path -Parent $helperPath
New-Item -ItemType Directory -Force -Path $helper,(Join-Path $profile 'Local'),(Join-Path $profile 'Temp') | Out-Null
Copy-Item -LiteralPath $HelperAssembly -Destination $helperPath -Force
function Start-RoleProcess([string]$image, [string]$arguments, [bool]$redirect) {
    $start = [Diagnostics.ProcessStartInfo]::new($image, $arguments)
    $start.WorkingDirectory = Split-Path -Parent $image
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $redirect
    $start.RedirectStandardError = $redirect
    $start.EnvironmentVariables['APPDATA'] = $roaming
    $start.EnvironmentVariables['LOCALAPPDATA'] = Join-Path $profile 'Local'
    $start.EnvironmentVariables['ProgramData'] = Join-Path $profile 'Local'
    $start.EnvironmentVariables['USERPROFILE'] = $profile
    $start.EnvironmentVariables['TEMP'] = Join-Path $profile 'Temp'
    $start.EnvironmentVariables['TMP'] = Join-Path $profile 'Temp'
    return [Diagnostics.Process]::Start($start)
}
foreach ($arguments in @('--host-check', '--version', '--replaykit-helper --host-info')) {
    $process = Start-RoleProcess $exe $arguments $true
    $output = $process.StandardOutput.ReadToEndAsync()
    $errors = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(20000)) { $process.Kill(); throw "Verification did not finish: $arguments" }
    $text = $output.GetAwaiter().GetResult()
    $errorText = $errors.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) { throw "Verification failed ($arguments): $errorText" }
    if ($arguments.Contains('--host-info')) {
        $info = $text | ConvertFrom-Json
        if (-not $info.hosted -or -not $info.grouped -or [IO.Path]::GetFullPath($info.helperRoot) -ne [IO.Path]::GetFullPath($helper) -or
            -not $info.command.Contains('--replaykit-helper')) { throw 'The hosted helper did not preserve its assembly path, role or process group.' }
    }
    Write-Output "PASS: $arguments"
    if ($text.Trim()) { Write-Output $text.Trim() }
    $process.Dispose()
}

if (-not ('ReplayKitHostVerification' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ReplayKitHostVerification
{
    [DllImport("kernel32.dll")]
    public static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool grouped);
}
'@
}
$config = Join-Path $root 'config/obs-studio'
$scenes = Join-Path $config 'basic/scenes'
New-Item -ItemType Directory -Force -Path $scenes | Out-Null
$page = Join-Path $profile 'renderer.html'
$readyMessage = 'ReplayKit renderer verification ' + [Guid]::NewGuid().ToString('N')
Set-Content -LiteralPath $page -Encoding Ascii -Value "<script>console.error('$readyMessage')</script>"
$browserId = [Guid]::NewGuid().ToString()
$scene = @{
    name = 'Role Verification'; version = 2; current_scene = 'Scene'; current_program_scene = 'Scene'
    scene_order = @(@{ name = 'Scene' })
    sources = @(
        @{ name = 'Verification Browser'; uuid = $browserId; id = 'browser_source'; versioned_id = 'browser_source'; enabled = $true
            settings = @{ is_local_file = $true; local_file = $page; width = 64; height = 64 } }
        @{ name = 'Scene'; uuid = [Guid]::NewGuid().ToString(); id = 'scene'; versioned_id = 'scene'; enabled = $true
            settings = @{ items = @(@{ name = 'Verification Browser'; source_uuid = $browserId; visible = $true; id = 1 }) } }
    )
}
[IO.File]::WriteAllText((Join-Path $scenes 'Role_Verification.json'), ($scene | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
# obs opens its auto-configuration wizard when FirstRun is missing and global.ini has no LastVersion, which a profile rewritten here and an obs killed before a normal exit never get
Set-Content -LiteralPath (Join-Path $config 'user.ini') -Encoding Ascii -Value @(
    '[General]', 'FirstRun=true',
    '[Basic]', 'ConfigOnNewProfile=false', 'SceneCollection=Role Verification', 'SceneCollectionFile=Role_Verification.json')
$stock = Join-Path $root 'bin/64bit/obs64.exe'
$obs = Start-RoleProcess $stock '--portable --multi --disable-updater --minimize-to-tray --disable-missing-files-check' $false
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    $rendererReady = $false
    do {
        $obs.Refresh()
        if ($obs.HasExited) { throw "Portable OBS exited before starting a browser renderer ($($obs.ExitCode))." }
        $logDir = Join-Path $config 'logs'
        if (Test-Path -LiteralPath $logDir) {
            $log = Get-ChildItem -LiteralPath $logDir -File | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
            $rendererReady = $log -and (Select-String -LiteralPath $log.FullName -Pattern $readyMessage -SimpleMatch -Quiet)
        }
        if ($rendererReady) { break }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not $rendererReady) { throw 'Portable OBS did not run the verification page in its browser renderer.' }
    $found = 0
    foreach ($role in [Diagnostics.Process]::GetProcessesByName('obs64')) {
        try {
            if ($role.MainModule.FileName -ne $exe) { continue }
            $grouped = $false
            if (-not [ReplayKitHostVerification]::IsProcessInJob($role.Handle, [IntPtr]::Zero, [ref]$grouped) -or -not $grouped) {
                throw 'An OBS browser process did not join a process group.'
            }
            $found++
        } catch [InvalidOperationException], [ComponentModel.Win32Exception] { }
        finally { $role.Dispose() }
    }
    if ($found -eq 0) { throw 'The OBS browser did not use the installed role host.' }
    Write-Host 'PASS: portable OBS started a renderer through the browser role host with job membership'
} finally {
    if (-not $obs.HasExited) { $obs.Kill(); [void]$obs.WaitForExit(5000) }
    $obs.Dispose()
}

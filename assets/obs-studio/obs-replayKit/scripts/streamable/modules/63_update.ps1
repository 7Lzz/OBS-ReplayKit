# ReplayKit self-update support. Uses GitHub Releases API and runs the downloaded
# installer in --update mode after verifying the release hash asset.
$script:REPLAYKIT_UPDATE_OWNER = '7Lzz'
$script:REPLAYKIT_UPDATE_REPO = 'OBS-ReplayKit'
$script:REPLAYKIT_UPDATE_INSTALLER_ASSET = 'OBSReplayKit.exe'
$script:REPLAYKIT_UPDATE_HASH_ASSET = 'OBSReplayKit.exe.sha256'

function Get-ReplayKitRootDir {
    $streamableDir = ([string](Get-ScriptDir)).TrimEnd('\', '/')
    $scriptsDir = [System.IO.Directory]::GetParent($streamableDir).FullName
    return [System.IO.Directory]::GetParent($scriptsDir).FullName
}

function Get-ReplayKitVersionPath {
    return Join-Path (Get-ReplayKitRootDir) 'version.json'
}

function Get-ReplayKitInstalledVersion {
    $path = Get-ReplayKitVersionPath
    if (-not (Test-Path -LiteralPath $path)) { return '0.0.0' }
    try {
        $data = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        if ($data.version) { return [string]$data.version }
    } catch {
        throw 'Installed ReplayKit version file is invalid.'
    }
    throw 'Installed ReplayKit version file is missing version.'
}

function Normalize-ReplayKitVersion([string]$version) {
    $v = ([string]$version).Trim()
    if ($v.StartsWith('v', [StringComparison]::OrdinalIgnoreCase)) { $v = $v.Substring(1) }
    $m = [regex]::Match($v, '^\d+(?:\.\d+){0,3}')
    if (-not $m.Success) { throw "Invalid version: $version" }
    return $m.Value
}

function Compare-ReplayKitVersion([string]$left, [string]$right) {
    $a = (Normalize-ReplayKitVersion $left).Split('.')
    $b = (Normalize-ReplayKitVersion $right).Split('.')
    for ($i = 0; $i -lt 4; $i++) {
        $av = if ($i -lt $a.Count) { [int]$a[$i] } else { 0 }
        $bv = if ($i -lt $b.Count) { [int]$b[$i] } else { 0 }
        if ($av -lt $bv) { return -1 }
        if ($av -gt $bv) { return 1 }
    }
    return 0
}

function Invoke-ReplayKitGitHubApi([string]$url) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    return Invoke-RestMethod -Uri $url -Headers @{ 'User-Agent' = 'OBSReplayKit-Updater' } -TimeoutSec 15
}

function Get-ReplayKitReleaseAsset($release, [string]$name) {
    foreach ($asset in @($release.assets)) {
        if ([string]$asset.name -eq $name) { return $asset }
    }
    return $null
}

function Get-ReplayKitLatestRelease {
    $url = "https://api.github.com/repos/$script:REPLAYKIT_UPDATE_OWNER/$script:REPLAYKIT_UPDATE_REPO/releases/latest"
    $release = Invoke-ReplayKitGitHubApi $url
    if (-not $release.tag_name) { throw 'Latest release did not include a tag name.' }
    $installer = Get-ReplayKitReleaseAsset $release $script:REPLAYKIT_UPDATE_INSTALLER_ASSET
    if ($null -eq $installer -or -not $installer.browser_download_url) {
        throw "Latest release is missing $script:REPLAYKIT_UPDATE_INSTALLER_ASSET."
    }
    $hash = Get-ReplayKitReleaseAsset $release $script:REPLAYKIT_UPDATE_HASH_ASSET
    return @{
        tagName = [string]$release.tag_name
        latestVersion = Normalize-ReplayKitVersion ([string]$release.tag_name)
        htmlUrl = [string]$release.html_url
        installerUrl = [string]$installer.browser_download_url
        hashUrl = if ($hash -and $hash.browser_download_url) { [string]$hash.browser_download_url } else { '' }
    }
}

function Get-ReplayKitUpdateStatus {
    try {
        $installed = Normalize-ReplayKitVersion (Get-ReplayKitInstalledVersion)
        $latest = Get-ReplayKitLatestRelease
        $cmp = Compare-ReplayKitVersion $installed ([string]$latest.latestVersion)
        return @{
            ok = $true
            installedVersion = $installed
            latestVersion = [string]$latest.latestVersion
            tagName = [string]$latest.tagName
            releaseUrl = [string]$latest.htmlUrl
            updateAvailable = ($cmp -lt 0)
            hashRequired = $true
        }
    } catch {
        $msg = $_.Exception.Message
        if ($msg -match '\(404\)') {
            return @{
                ok = $true
                installedVersion = Normalize-ReplayKitVersion (Get-ReplayKitInstalledVersion)
                latestVersion = ''
                tagName = ''
                releaseUrl = ''
                updateAvailable = $false
                hashRequired = $true
                message = 'No GitHub Release has been published yet.'
            }
        }
        return @{ ok = $false; message = $msg }
    }
}

function Get-ReplayKitAutoUpdateStatus {
    try {
        $settings = Read-ReplayKitSettings
        $enabled = [bool]$settings['autoUpdateEnabled']
        if (-not $enabled) {
            return @{
                ok = $true
                autoUpdateEnabled = $false
                updateAvailable = $false
                prompt = $false
                message = 'Automatic update prompts are disabled.'
            }
        }

        $status = Get-ReplayKitUpdateStatus
        $status['autoUpdateEnabled'] = $true
        $status['prompt'] = $false
        if ($status['ok'] -and [bool]$status['updateAvailable'] -and -not [string]::IsNullOrWhiteSpace([string]$status['latestVersion'])) {
            $dismissed = ([string]$settings['lastUpdatePromptVersion']).Trim()
            if (-not [string]::IsNullOrWhiteSpace($dismissed)) {
                $dismissed = Normalize-ReplayKitVersion $dismissed
            }
            $status['prompt'] = ($dismissed -ne [string]$status['latestVersion'])
        }
        return $status
    } catch {
        return @{ ok = $false; prompt = $false; message = $_.Exception.Message }
    }
}

function Set-ReplayKitUpdatePromptDismissed([string]$version) {
    if ([string]::IsNullOrWhiteSpace($version)) {
        throw 'Missing update version.'
    }
    $normalized = Normalize-ReplayKitVersion $version
    $settings = Read-ReplayKitSettings
    $settings['lastUpdatePromptVersion'] = $normalized
    Write-ReplayKitSettings $settings
    return @{ ok = $true; version = $normalized }
}

function Get-ReplayKitUpdateTempDir {
    return Join-Path $env:TEMP 'ReplayKitUpdate'
}

function Assert-ReplayKitSafeUpdateTemp([string]$path) {
    $root = [System.IO.Path]::GetFullPath($env:TEMP).TrimEnd([char]'\')
    $full = [System.IO.Path]::GetFullPath($path).TrimEnd([char]'\')
    if ([System.IO.Path]::GetFileName($full) -ne 'ReplayKitUpdate') {
        throw 'Update temp folder name is invalid.'
    }
    if (-not $full.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Update temp folder resolved outside %TEMP%.'
    }
}

function Save-ReplayKitUrlFile([string]$url, [string]$path) {
    if (-not $url.StartsWith('https://', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Update download URL must use HTTPS.'
    }
    Invoke-WebRequest -Uri $url -OutFile $path -UseBasicParsing -Headers @{ 'User-Agent' = 'OBSReplayKit-Updater' } -TimeoutSec 60
}

function Get-ReplayKitSha256FromText([string]$text) {
    $m = [regex]::Match($text, '(?i)\b[a-f0-9]{64}\b')
    if (-not $m.Success) { throw 'SHA-256 file did not contain a valid hash.' }
    return $m.Value.ToUpperInvariant()
}

function Quote-ReplayKitProcessArg([string]$arg) {
    if ([string]::IsNullOrEmpty($arg)) { return '""' }
    if ($arg -notmatch '[\s"]') { return $arg }
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('"')
    $slashes = 0
    for ($i = 0; $i -lt $arg.Length; $i++) {
        $c = $arg[$i]
        if ($c -eq '\') {
            $slashes++
        } elseif ($c -eq '"') {
            [void]$sb.Append('\' * ($slashes * 2 + 1))
            [void]$sb.Append('"')
            $slashes = 0
        } else {
            [void]$sb.Append('\' * $slashes)
            [void]$sb.Append($c)
            $slashes = 0
        }
    }
    [void]$sb.Append('\' * ($slashes * 2))
    [void]$sb.Append('"')
    return $sb.ToString()
}

function Get-ReplayKitUpdateObsPath {
    $procs = @(Get-Process -Name @('obs64', 'obs32', 'obs') -ErrorAction SilentlyContinue)
    foreach ($p in $procs) {
        try {
            if ($p.Path) { return [string]$p.Path }
        } catch {}
    }
    foreach ($p in $procs) {
        try {
            $cim = Get-CimInstance Win32_Process -Filter "ProcessId=$($p.Id)" -ErrorAction SilentlyContinue
            if ($cim -and $cim.ExecutablePath) { return [string]$cim.ExecutablePath }
        } catch {}
    }
    return ''
}

function Start-ReplayKitUpdater([string]$installerPath, [string]$tempDir) {
    $obsPath = Get-ReplayKitUpdateObsPath
    $waitPid = if ($script:ParentPid -gt 0) { [int]$script:ParentPid } else { 0 }
    $args = @('--update', '--cleanup-dir', $tempDir, '--start-delay-ms', '1200')
    if (-not [string]::IsNullOrWhiteSpace($obsPath)) {
        $args += @('--relaunch-obs', $obsPath)
    }
    if ($waitPid -gt 0) {
        $args += @('--wait-pid', [string]$waitPid)
    }

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $installerPath
    $psi.Arguments = (($args | ForEach-Object { Quote-ReplayKitProcessArg ([string]$_) }) -join ' ')
    $psi.WorkingDirectory = $tempDir
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    return @{ ok = $true; processId = [int]$p.Id }
}

function Invoke-ReplayKitApplyUpdate {
    try {
        $installed = Normalize-ReplayKitVersion (Get-ReplayKitInstalledVersion)
        $latest = Get-ReplayKitLatestRelease
        if ((Compare-ReplayKitVersion $installed ([string]$latest.latestVersion)) -ge 0) {
            return @{
                ok = $true
                updateAvailable = $false
                installedVersion = $installed
                latestVersion = [string]$latest.latestVersion
                message = 'ReplayKit is already up to date.'
            }
        }
        if ([string]::IsNullOrWhiteSpace([string]$latest.hashUrl)) {
            throw "Latest release is missing $script:REPLAYKIT_UPDATE_HASH_ASSET."
        }

        $tempDir = Get-ReplayKitUpdateTempDir
        Assert-ReplayKitSafeUpdateTemp $tempDir
        if (Test-Path -LiteralPath $tempDir) {
            Remove-Item -LiteralPath $tempDir -Recurse -Force
        }
        [void](New-Item -ItemType Directory -Path $tempDir -Force)

        $installerPath = Join-Path $tempDir $script:REPLAYKIT_UPDATE_INSTALLER_ASSET
        $hashPath = Join-Path $tempDir $script:REPLAYKIT_UPDATE_HASH_ASSET
        Save-ReplayKitUrlFile ([string]$latest.installerUrl) $installerPath
        Save-ReplayKitUrlFile ([string]$latest.hashUrl) $hashPath

        $expected = Get-ReplayKitSha256FromText ([System.IO.File]::ReadAllText($hashPath))
        $actual = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToUpperInvariant()
        if ($actual -ne $expected) {
            Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
            throw 'Downloaded installer hash did not match the release hash.'
        }

        $started = Start-ReplayKitUpdater $installerPath $tempDir
        return @{
            ok = $true
            updateAvailable = $true
            installing = $true
            installedVersion = $installed
            latestVersion = [string]$latest.latestVersion
            processId = [int]$started.processId
            message = "ReplayKit $($latest.latestVersion) is installing. OBS will restart."
        }
    } catch {
        return @{ ok = $false; message = $_.Exception.Message }
    }
}

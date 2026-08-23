function Get-ReplayKitScriptsDir {
    $dir = [string](Get-ScriptDir)
    $dir = $dir.TrimEnd([char]'\', [char]'/')
    return [System.IO.Directory]::GetParent($dir).FullName
}

function Get-ReplayKitSettingsPath {
    return Join-Path (Get-ReplayKitScriptsDir) 'replaykit_settings.json'
}

function Get-DefaultReplayKitSettings {
    return @{
        recordingPreset          = 'balanced'
        compressionMode          = 'balanced'
        codecPreference          = 'auto'
        replaySeconds            = 90
        clipDir                  = ''
        clipKeybind              = @{ shift = $true; key = 'OBS_KEY_BACKSLASH' }
        recordingKeybind         = @{}
        overlayStyle             = 'input_overlay'
        overlayOpacity           = 100
        overlayScale             = 100
        overlayHueShift          = 0
        overlayColorMultiply     = '#ffffff'
        overlayColorAdd          = '#000000'
        obsStartupEnabled        = $true
        runObsAsAdmin            = $false
        disableObsCloseWarning   = $true
        allowSleepWhileActive    = $true
        pinObsTrayIcon           = $true
        clipNotificationEnabled  = $true
        recordingNotificationEnabled = $true
        clipNotificationSeconds  = 90
        trimPreciseDefault       = $false
        debugLoggingEnabled      = $false
        autoDeleteLogsOnLaunch   = $true
        autoUpdateEnabled        = $true
        lastUpdatePromptVersion  = ''
        clipSoundVolume          = 100
        recordingSoundVolume     = 100
        motionBlurEnabled        = $false
        motionBlurStrength       = 0.075
        # legacy shareMode is kept only so older settings files dont break parsing; discord output uses the obs windowed projector directly.
        shareMode                = 'projector'
        discord_screenshare_enabled = $true
        discord_output_mode      = 'projector'
        discord_projector_enabled = $true
        discord_projector_width  = 0
        discord_projector_height = 0
        discord_projector_visible_pixels = 1
        discord_projector_monitor_index = 0
        discord_projector_edge   = 'bottom'
        discord_projector_title_hint = 'OBS ReplayKit Discord Share'
        discord_projector_hide_taskbar = $true
        screenshareCaptureMode   = 'hybrid_auto'
        screenshareGameWindow    = ''
        screenshareGameOverrides = @()
        screenshareAutoGameKeepFocused = $false
        screenshareSwitchDelaySeconds = 1.0
    }
}

function ConvertTo-PlainHash($value) {
    $out = @{}
    if ($null -eq $value) { return $out }
    foreach ($prop in $value.PSObject.Properties) {
        $out[$prop.Name] = $prop.Value
    }
    return $out
}

function Get-BoolSetting($data, [string]$key, [bool]$default) {
    if (-not $data.ContainsKey($key)) { return $default }
    $v = $data[$key]
    if ($v -is [bool]) { return [bool]$v }
    if ($v -is [string]) {
        $s = $v.Trim().ToLowerInvariant()
        if ($s -eq 'true' -or $s -eq '1' -or $s -eq 'yes' -or $s -eq 'on') { return $true }
        if ($s -eq 'false' -or $s -eq '0' -or $s -eq 'no' -or $s -eq 'off') { return $false }
    }
    throw "Invalid boolean setting: $key"
}

function Get-IntSetting($data, [string]$key, [int]$default, [int]$min, [int]$max) {
    if (-not $data.ContainsKey($key)) { return $default }
    $n = $default
    if (-not [int]::TryParse(([string]$data[$key]), [ref]$n)) {
        throw "Invalid number setting: $key"
    }
    if ($n -lt $min -or $n -gt $max) {
        throw "$key must be between $min and $max."
    }
    return $n
}

function Get-FloatSetting($data, [string]$key, [double]$default, [double]$min, [double]$max) {
    if (-not $data.ContainsKey($key)) { return $default }
    $n = $default
    if (-not [double]::TryParse(([string]$data[$key]), [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$n)) {
        throw "Invalid number setting: $key"
    }
    if ($n -lt $min -or $n -gt $max) {
        throw "$key must be between $min and $max."
    }
    return $n
}

function Get-HexColorSetting($data, [string]$key, [string]$default) {
    if (-not $data.ContainsKey($key)) { return $default }
    $v = ([string]$data[$key]).Trim().ToLowerInvariant()
    if ($v -match '^#[0-9a-f]{6}$') { return $v }
    throw "Invalid color setting: $key"
}

function Get-EnumSetting($data, [string]$key, [string]$default, [string[]]$allowed) {
    if (-not $data.ContainsKey($key)) { return $default }
    $v = ([string]$data[$key]).Trim()
    if ($allowed -contains $v) { return $v }
    throw "Invalid option for ${key}: $v"
}

function Get-TextSetting($data, [string]$key, [string]$default, [int]$maxLength) {
    if (-not $data.ContainsKey($key)) { return $default }
    $v = ([string]$data[$key]).Trim()
    if ([string]::IsNullOrWhiteSpace($v)) { return $default }
    if ($v.Length -gt $maxLength -or $v -match '[\x00-\x1F]') {
        throw "Invalid text setting: $key"
    }
    return $v
}

function Get-VersionMarkerSetting($data, [string]$key, [string]$default) {
    if (-not $data.ContainsKey($key)) { return $default }
    $v = ([string]$data[$key]).Trim()
    if ([string]::IsNullOrWhiteSpace($v)) { return '' }
    if ($v.Length -gt 32 -or $v -notmatch '^\d+(?:\.\d+){0,3}$') {
        throw "Invalid version setting: $key"
    }
    return $v
}

function Resolve-ClipDirSetting([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) { return '' }
    if ($value.Length -gt 4096) { throw 'Clip folder path is too long.' }
    $expanded = [Environment]::ExpandEnvironmentVariables($value.Trim())
    if (-not [System.IO.Path]::IsPathRooted($expanded)) {
        throw 'Clip folder must be an absolute path.'
    }
    $full = [System.IO.Path]::GetFullPath($expanded)
    return $full
}

function Normalize-HotkeyCombo($value, [hashtable]$default, [string]$settingName) {
    if ($null -eq $value) { return $default }
    $data = if ($value -is [hashtable]) { $value } else { ConvertTo-PlainHash $value }
    if (-not $data.ContainsKey('key') -or [string]::IsNullOrWhiteSpace([string]$data.key)) {
        return @{}
    }
    $key = ([string]$data.key).Trim()
    if ($key -notmatch '^OBS_KEY_[A-Z0-9_]{1,48}$') {
        throw "Invalid $settingName."
    }
    $out = @{ key = $key }
    foreach ($mod in @('control', 'alt', 'shift', 'command')) {
        if ($data.ContainsKey($mod) -and (Get-BoolSetting $data $mod $false)) {
            $out[$mod] = $true
        }
    }
    return $out
}

function Normalize-ClipKeybind($value) {
    return Normalize-HotkeyCombo $value @{ shift = $true; key = 'OBS_KEY_BACKSLASH' } 'clip keybind'
}

function Normalize-RecordingKeybind($value) {
    return Normalize-HotkeyCombo $value @{} 'recording keybind'
}

function Normalize-ScreenshareGameOverrides($value) {
    if ($null -eq $value) { return @() }
    if ($value -is [string] -and [string]::IsNullOrWhiteSpace($value)) { return @() }
    if ($value -is [string]) {
        $items = @($value)
    } elseif ($value -is [System.Collections.IEnumerable]) {
        $items = @($value)
    } else {
        throw 'screenshareGameOverrides must be a list.'
    }
    if ($items.Count -gt 32) { throw 'screenshareGameOverrides cannot contain more than 32 games.' }

    $seen = @{}
    $out = New-Object System.Collections.ArrayList
    foreach ($item in $items) {
        if ($null -eq $item) { continue }
        $token = ''
        if ($item -is [string]) {
            $token = [string]$item
        } else {
            $data = if ($item -is [hashtable]) { $item } else { ConvertTo-PlainHash $item }
            if ($data.ContainsKey('token')) {
                $token = [string]$data.token
            } elseif ($data.ContainsKey('value')) {
                $token = [string]$data.value
            }
        }
        $entry = ConvertFrom-ReplayKitObsWindowToken $token
        if ($null -eq $entry) { throw 'Invalid Auto Game List entry.' }
        if ($seen.ContainsKey($entry.token)) { continue }
        $seen[$entry.token] = $true
        [void]$out.Add($entry)
    }
    return @($out.ToArray())
}

function Normalize-ReplayKitSettings($raw) {
    $defaults = Get-DefaultReplayKitSettings
    $data = @{}
    foreach ($k in $defaults.Keys) { $data[$k] = $defaults[$k] }
    foreach ($k in $raw.Keys) { $data[$k] = $raw[$k] }

    $preset = Get-EnumSetting $data 'recordingPreset' $defaults.recordingPreset @('performance', 'balanced', 'quality')
    $compressionDefault = switch ($preset) {
        'performance' { 'lower_gpu' }
        'quality'     { 'smaller_files' }
        default       { 'balanced' }
    }
    $compression = Get-EnumSetting $data 'compressionMode' $compressionDefault @('lower_gpu', 'balanced', 'smaller_files')

    $clipDir = Resolve-ClipDirSetting ([string]$data.clipDir)
    $replaySeconds = Get-IntSetting $data 'replaySeconds' $defaults.replaySeconds 5 1200

    $shareMode = Get-EnumSetting $data 'shareMode' $defaults.shareMode @('projector', 'share_bridge', 'virtual_camera_legacy', 'vcam', 'screenshare')
    if ($shareMode -ne 'projector') {
        Write-Log "Discord shareMode '$shareMode' ignored; ReplayKit now uses OBS projector mode."
        $shareMode = 'projector'
    }
    $discordOutputMode = Get-EnumSetting $data 'discord_output_mode' $defaults.discord_output_mode @('projector', 'share_bridge', 'virtual_camera_legacy')
    if ($discordOutputMode -ne 'projector') {
        Write-Log "Discord output mode '$discordOutputMode' ignored; ReplayKit now uses OBS projector mode."
        $discordOutputMode = 'projector'
    }

    # av1 stays in the allowed list so existing settings.json values dont throw on load, but its no longer offered -- most iphones and plenty of android devices have no av1 decoder at all, and unlike the hevc hev1/hvc1 tag that can be fixed with a remux, theres no fix for missing silicon.
    $codecPreference = Get-EnumSetting $data 'codecPreference' $defaults.codecPreference @('auto', 'h264', 'h265', 'av1')
    if ($codecPreference -eq 'av1') {
        Write-Log "Recording codec 'av1' is no longer offered (playback compatibility); falling back to auto."
        $codecPreference = 'auto'
    }

    return @{
        recordingPreset          = $preset
        compressionMode          = $compression
        codecPreference          = $codecPreference
        replaySeconds            = $replaySeconds
        clipDir                  = $clipDir
        clipKeybind              = Normalize-ClipKeybind $data.clipKeybind
        recordingKeybind         = Normalize-RecordingKeybind $data.recordingKeybind
        overlayStyle             = Get-EnumSetting $data 'overlayStyle' $defaults.overlayStyle @('input_overlay', 'bongo_cat', 'off')
        overlayOpacity           = Get-IntSetting $data 'overlayOpacity' $defaults.overlayOpacity 0 100
        overlayScale             = Get-IntSetting $data 'overlayScale' $defaults.overlayScale 50 200
        overlayHueShift          = Get-FloatSetting $data 'overlayHueShift' $defaults.overlayHueShift -180.0 180.0
        overlayColorMultiply     = Get-HexColorSetting $data 'overlayColorMultiply' $defaults.overlayColorMultiply
        overlayColorAdd          = Get-HexColorSetting $data 'overlayColorAdd' $defaults.overlayColorAdd
        obsStartupEnabled        = Get-BoolSetting $data 'obsStartupEnabled' $defaults.obsStartupEnabled
        runObsAsAdmin            = Get-BoolSetting $data 'runObsAsAdmin' $defaults.runObsAsAdmin
        disableObsCloseWarning   = Get-BoolSetting $data 'disableObsCloseWarning' $defaults.disableObsCloseWarning
        allowSleepWhileActive    = Get-BoolSetting $data 'allowSleepWhileActive' $defaults.allowSleepWhileActive
        pinObsTrayIcon           = Get-BoolSetting $data 'pinObsTrayIcon' $defaults.pinObsTrayIcon
        clipNotificationEnabled  = Get-BoolSetting $data 'clipNotificationEnabled' $defaults.clipNotificationEnabled
        recordingNotificationEnabled = Get-BoolSetting $data 'recordingNotificationEnabled' $defaults.recordingNotificationEnabled
        clipNotificationSeconds  = Get-IntSetting $data 'clipNotificationSeconds' $replaySeconds 1 1200
        trimPreciseDefault       = Get-BoolSetting $data 'trimPreciseDefault' $defaults.trimPreciseDefault
        debugLoggingEnabled      = Get-BoolSetting $data 'debugLoggingEnabled' $defaults.debugLoggingEnabled
        autoDeleteLogsOnLaunch   = Get-BoolSetting $data 'autoDeleteLogsOnLaunch' $defaults.autoDeleteLogsOnLaunch
        autoUpdateEnabled        = Get-BoolSetting $data 'autoUpdateEnabled' $defaults.autoUpdateEnabled
        lastUpdatePromptVersion  = Get-VersionMarkerSetting $data 'lastUpdatePromptVersion' $defaults.lastUpdatePromptVersion
        clipSoundVolume          = Get-IntSetting $data 'clipSoundVolume' $defaults.clipSoundVolume 0 100
        recordingSoundVolume     = Get-IntSetting $data 'recordingSoundVolume' $defaults.recordingSoundVolume 0 100
        motionBlurEnabled        = Get-BoolSetting $data 'motionBlurEnabled' $defaults.motionBlurEnabled
        motionBlurStrength       = Get-FloatSetting $data 'motionBlurStrength' $defaults.motionBlurStrength 0.0 1.0
        shareMode                = $shareMode
        discord_screenshare_enabled = Get-BoolSetting $data 'discord_screenshare_enabled' $defaults.discord_screenshare_enabled
        discord_output_mode      = $discordOutputMode
        discord_projector_enabled = Get-BoolSetting $data 'discord_projector_enabled' $defaults.discord_projector_enabled
        discord_projector_width  = Get-IntSetting $data 'discord_projector_width' $defaults.discord_projector_width 0 7680
        discord_projector_height = Get-IntSetting $data 'discord_projector_height' $defaults.discord_projector_height 0 4320
        discord_projector_visible_pixels = Get-IntSetting $data 'discord_projector_visible_pixels' $defaults.discord_projector_visible_pixels 0 32
        discord_projector_monitor_index = Get-IntSetting $data 'discord_projector_monitor_index' $defaults.discord_projector_monitor_index 0 64
        discord_projector_edge   = Get-EnumSetting $data 'discord_projector_edge' $defaults.discord_projector_edge @('right', 'left', 'top', 'bottom')
        discord_projector_title_hint = Get-TextSetting $data 'discord_projector_title_hint' $defaults.discord_projector_title_hint 128
        discord_projector_hide_taskbar = $true
        screenshareCaptureMode   = Get-EnumSetting $data 'screenshareCaptureMode' $defaults.screenshareCaptureMode @('hybrid_auto', 'desktop', 'game_auto', 'game_window')
        screenshareGameWindow    = Get-TextSetting $data 'screenshareGameWindow' $defaults.screenshareGameWindow 512
        screenshareGameOverrides = @(Normalize-ScreenshareGameOverrides $data.screenshareGameOverrides)
        screenshareAutoGameKeepFocused = Get-BoolSetting $data 'screenshareAutoGameKeepFocused' $defaults.screenshareAutoGameKeepFocused
        screenshareSwitchDelaySeconds = Get-FloatSetting $data 'screenshareSwitchDelaySeconds' $defaults.screenshareSwitchDelaySeconds 0.05 5.0
    }
}

function Test-ReplayKitDiscordScreenshareEnabled([hashtable]$settings) {
    if ($null -eq $settings) { return $true }
    if (-not $settings.ContainsKey('discord_screenshare_enabled')) { return $true }
    return [bool]$settings.discord_screenshare_enabled
}

function Read-ReplayKitSettings {
    $path = Get-ReplayKitSettingsPath
    if (-not (Test-Path -LiteralPath $path)) {
        return Normalize-ReplayKitSettings @{}
    }
    try {
        $fi = [System.IO.FileInfo]::new($path)
        if ($fi.Length -gt 65536) { throw 'Settings file is too large.' }
        $json = [System.IO.File]::ReadAllText($path)
        if ([string]::IsNullOrWhiteSpace($json)) {
            return Normalize-ReplayKitSettings @{}
        }
        return Normalize-ReplayKitSettings (ConvertTo-PlainHash (ConvertFrom-Json $json))
    } catch {
        Write-Log "Read-ReplayKitSettings failed: $($_.Exception.Message)"
        throw "ReplayKit settings file is invalid: $($_.Exception.Message)"
    }
}

function Write-ReplayKitSettings([hashtable]$settings) {
    $path = Get-ReplayKitSettingsPath
    $parent = Split-Path -Parent $path
    if (-not (Test-Path -LiteralPath $parent)) {
        [void](New-Item -ItemType Directory -Path $parent -Force)
    }
    Write-Utf8 $path (ConvertTo-Json $settings -Depth 6)
}

function Get-ReplayKitObsConfigRoot {
    if ($env:APPDATA) { return (Join-Path $env:APPDATA 'obs-studio') }
    return (Join-Path (Get-UserProfile) 'AppData\Roaming\obs-studio')
}

function Set-ReplayKitIniValue([string]$text, [string]$section, [string]$key, [string]$value) {
    if ($null -eq $text) { $text = '' }
    $lines = if ([string]::IsNullOrEmpty($text)) { @() } else { [System.Text.RegularExpressions.Regex]::Split($text, '\r?\n') }
    $out = [System.Collections.Generic.List[string]]::new()
    $inSection = $false
    $sectionSeen = $false
    $keySet = $false
    $keyPattern = '^\s*' + [regex]::Escape($key) + '\s*='

    foreach ($line in $lines) {
        if ($line -match '^\s*\[(.+?)\]\s*$') {
            if ($inSection -and -not $keySet) {
                $out.Add("$key=$value")
                $keySet = $true
            }
            $inSection = ([string]$matches[1]) -ieq $section
            if ($inSection) { $sectionSeen = $true }
            $out.Add($line)
            continue
        }
        if ($inSection -and $line -match $keyPattern) {
            if (-not $keySet) {
                $out.Add("$key=$value")
                $keySet = $true
            }
            continue
        }
        $out.Add($line)
    }

    if ($inSection -and -not $keySet) {
        $out.Add("$key=$value")
    }
    if (-not $sectionSeen) {
        if ($out.Count -gt 0 -and $out[$out.Count - 1] -ne '') { $out.Add('') }
        $out.Add("[$section]")
        $out.Add("$key=$value")
    }

    return (($out -join "`r`n").TrimEnd() + "`r`n")
}

function Set-ReplayKitObsCloseWarningConfig([bool]$disabled) {
    try {
        $root = Get-ReplayKitObsConfigRoot
        if (-not (Test-Path -LiteralPath $root)) {
            [void](New-Item -ItemType Directory -Path $root -Force)
        }
        $resolvedRoot = [System.IO.Path]::GetFullPath($root)
        $expectedRoot = if ($env:APPDATA) {
            [System.IO.Path]::GetFullPath((Join-Path $env:APPDATA 'obs-studio'))
        } else {
            [System.IO.Path]::GetFullPath((Join-Path (Get-UserProfile) 'AppData\Roaming\obs-studio'))
        }
        if (-not $resolvedRoot.Equals($expectedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'OBS config root did not resolve to the expected AppData path.'
        }

        $confirmOnExit = if ($disabled) { 'false' } else { 'true' }
        $changed = @()
        $paths = @(
            (Join-Path $resolvedRoot 'user.ini'),
            (Join-Path $resolvedRoot 'global.ini')
        )
        foreach ($path in $paths) {
            $text = ''
            if (Test-Path -LiteralPath $path) {
                $text = [System.IO.File]::ReadAllText($path)
            }
            $next = Set-ReplayKitIniValue $text 'General' 'ConfirmOnExit' $confirmOnExit
            if ($next -ne $text) {
                Write-Utf8 $path $next
                $changed += (Split-Path -Leaf $path)
            }
        }
        return @{ ok = $true; confirmOnExit = $confirmOnExit; changed = $changed }
    } catch {
        return @{ ok = $false; message = $_.Exception.Message }
    }
}

function Invoke-ReplayKitPowerCfg([string[]]$arguments) {
    $allowed = @('/requestsoverride', 'PROCESS', 'obs64.exe', 'DISPLAY', 'SYSTEM', 'AWAYMODE')
    foreach ($arg in $arguments) {
        if ($allowed -notcontains $arg) {
            return @{ ok = $false; message = 'Invalid power configuration request.' }
        }
    }
    try {
        $psi = [System.Diagnostics.ProcessStartInfo]::new()
        $psi.FileName = 'powercfg.exe'
        $psi.Arguments = ($arguments -join ' ')
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.CreateNoWindow = $true
        $process = [System.Diagnostics.Process]::Start($psi)
        if ($null -eq $process) {
            return @{ ok = $false; message = 'powercfg could not start.' }
        }
        if (-not $process.WaitForExit(10000)) {
            try { $process.Kill() } catch {}
            return @{ ok = $false; message = 'powercfg timed out.' }
        }
        $output = (($process.StandardOutput.ReadToEnd() + "`n" + $process.StandardError.ReadToEnd()).Trim())
        if ($process.ExitCode -ne 0) {
            $line = ($output -split '\r?\n' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1)
            if ([string]::IsNullOrWhiteSpace($line)) { $line = "powercfg exited $($process.ExitCode)." }
            return @{ ok = $false; message = $line; exitCode = $process.ExitCode }
        }
        return @{ ok = $true; message = $output; exitCode = 0 }
    } catch {
        return @{ ok = $false; message = $_.Exception.Message }
    }
}

function Set-ReplayKitSleepOverrideSetting([bool]$allowSleep) {
    $args = @('/requestsoverride', 'PROCESS', 'obs64.exe')
    if ($allowSleep) {
        $args += @('DISPLAY', 'SYSTEM', 'AWAYMODE')
    }
    $result = Invoke-ReplayKitPowerCfg $args
    if (-not $result.ok) {
        return @{ ok = $false; message = $result.message }
    }
    $mode = if ($allowSleep) { 'allow_sleep' } else { 'obs_default' }
    return @{ ok = $true; mode = $mode }
}

function Apply-ReplayKitSleepOverrideSetting([bool]$allowSleep) {
    $result = Set-ReplayKitSleepOverrideSetting $allowSleep
    if ($result.ok) {
        return @{ applied = @('Windows sleep'); warnings = @() }
    }
    return @{
        applied = @()
        warnings = @("Windows sleep setting was saved, but Windows rejected the change: $($result.message)")
    }
}

function Get-StartupShortcutPath {
    $folder = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
    if ([string]::IsNullOrWhiteSpace($folder) -and $env:APPDATA) {
        $folder = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup'
    }
    if ([string]::IsNullOrWhiteSpace($folder)) {
        throw 'Windows Startup folder was not found.'
    }
    return Join-Path $folder 'OBS ReplayKit.lnk'
}

function Get-ObsStartupTarget {
    $obs = $script:OBS_EXE
    if (-not (Test-Path -LiteralPath $obs -PathType Leaf)) {
        $candidate = Resolve-ReplayKitObsExe
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $obs = $candidate }
    }
    if (-not (Test-Path -LiteralPath $obs -PathType Leaf)) {
        throw 'OBS executable was not found.'
    }
    return [string]$obs
}

function Remove-LegacyObsRunValue {
    $runPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $name = 'OBS ReplayKit'
    if (-not (Test-Path -LiteralPath $runPath)) { return }
    $property = Get-ItemProperty -LiteralPath $runPath -Name $name -ErrorAction SilentlyContinue
    if ($null -ne $property) {
        Remove-ItemProperty -LiteralPath $runPath -Name $name -ErrorAction Stop
    }
}

function New-ObsStartupShortcut {
    $shortcutPath = Get-StartupShortcutPath
    $obsPath = Get-ObsStartupTarget
    $shortcutDir = Split-Path -Parent $shortcutPath
    if (-not (Test-Path -LiteralPath $shortcutDir -PathType Container)) {
        New-Item -Path $shortcutDir -ItemType Directory -Force | Out-Null
    }
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $obsPath
    $shortcut.Arguments = '--background-color=ff272a33 --default-background-color=ff272a33 --disable-direct-composition-video-overlays'
    $shortcut.WorkingDirectory = [System.IO.Path]::GetDirectoryName($obsPath)
    $shortcut.IconLocation = "$obsPath,0"
    $shortcut.Description = 'Start OBS ReplayKit when Windows signs in.'
    $shortcut.Save()
}

function Remove-ObsStartupShortcut {
    $shortcutPath = Get-StartupShortcutPath
    if (Test-Path -LiteralPath $shortcutPath -PathType Leaf) {
        Remove-Item -LiteralPath $shortcutPath -Force -ErrorAction Stop
    }
}

function Set-ObsStartupSetting([bool]$enabled) {
    try {
        Remove-LegacyObsRunValue
        if ($enabled) {
            New-ObsStartupShortcut
        } else {
            Remove-ObsStartupShortcut
        }
        Remove-LegacyObsRunValue
        return @{ ok = $true }
    } catch {
        return @{ ok = $false; message = $_.Exception.Message }
    }
}

function Update-HelperConfigClipDir([string]$clipDir) {
    Load-Config
    $script:State.Config.clipDir = $clipDir
    $obj = [ordered]@{
        port           = if ($script:State.Config.port) { [int]$script:State.Config.port } else { $script:DEFAULT_PORT }
        scriptDir      = [string](Get-ScriptDir)
        clipDir        = $clipDir
        psHelper       = [string](Get-PsHelperPath)
        loggingEnabled = [bool]$script:State.LogEnabled
    }
    Write-Utf8 $script:State.ConfigPath (ConvertTo-Json $obj -Depth 4)
    $script:State.ConfigMTime = [DateTime]::MinValue
    Load-Config
}

function Set-ObsProfileParameterSafe([string]$category, [string]$name, [string]$value) {
    return Invoke-ObsWebSocketRequest 'SetProfileParameter' @{
        parameterCategory = $category
        parameterName     = $name
        parameterValue    = $value
    } 3000
}

function Get-ObsProfileParameterSafe([string]$category, [string]$name) {
    return Invoke-ObsWebSocketRequest 'GetProfileParameter' @{
        parameterCategory = $category
        parameterName     = $name
    } 3000
}

function Get-ReplayKitObsProfileParameterValue([string]$category, [string]$name) {
    $result = Get-ObsProfileParameterSafe $category $name
    if (-not $result.ok) {
        return @{ ok = $false; value = ''; message = $result.message }
    }
    $value = Get-ReplayKitJsonValue $result.data 'parameterValue' $null
    if ($null -eq $value) {
        return @{ ok = $false; value = ''; message = "OBS did not return ${category}.${name}." }
    }
    return @{ ok = $true; value = [string]$value; message = '' }
}

function Test-ReplayKitStringEqual([string]$actual, [string]$expected) {
    return [string]::Equals($actual, $expected, [System.StringComparison]::OrdinalIgnoreCase)
}

function Set-ReplayKitObsMonitoringDevice([string]$deviceId, [string]$deviceName, [int]$maxAttempts = 6) {
    $deviceId = ([string]$deviceId).Trim()
    $deviceName = ([string]$deviceName).Trim()
    if ([string]::IsNullOrWhiteSpace($deviceId) -or [string]::IsNullOrWhiteSpace($deviceName)) {
        return @{ ok = $false; applied = @(); message = 'OBS monitoring device id/name is missing.' }
    }
    if ($deviceId.Length -gt 512 -or $deviceName.Length -gt 256 -or $deviceId -match '[\x00-\x1F]' -or $deviceName -match '[\x00-\x1F]') {
        return @{ ok = $false; applied = @(); message = 'OBS monitoring device id/name is invalid.' }
    }

    $attempts = [Math]::Max(1, [Math]::Min(10, $maxAttempts))
    $lastMessage = ''
    for ($attempt = 1; $attempt -le $attempts; $attempt++) {
        $currentId = Get-ReplayKitObsProfileParameterValue 'Audio' 'MonitoringDeviceId'
        $currentName = Get-ReplayKitObsProfileParameterValue 'Audio' 'MonitoringDeviceName'
        if ($currentId.ok -and $currentName.ok -and
            (Test-ReplayKitStringEqual ([string]$currentId.value) $deviceId) -and
            (Test-ReplayKitStringEqual ([string]$currentName.value) $deviceName)) {
            return @{ ok = $true; applied = @('OBS monitoring device already set'); message = '' }
        }

        $setId = Set-ObsProfileParameterSafe 'Audio' 'MonitoringDeviceId' $deviceId
        if (-not $setId.ok) {
            $lastMessage = "MonitoringDeviceId rejected: $($setId.message)"
            Start-Sleep -Milliseconds ([Math]::Min(1000, 150 * $attempt))
            continue
        }
        $setName = Set-ObsProfileParameterSafe 'Audio' 'MonitoringDeviceName' $deviceName
        if (-not $setName.ok) {
            $lastMessage = "MonitoringDeviceName rejected: $($setName.message)"
            Start-Sleep -Milliseconds ([Math]::Min(1000, 150 * $attempt))
            continue
        }

        Start-Sleep -Milliseconds ([Math]::Min(1000, 150 * $attempt))
        $verifyId = Get-ReplayKitObsProfileParameterValue 'Audio' 'MonitoringDeviceId'
        $verifyName = Get-ReplayKitObsProfileParameterValue 'Audio' 'MonitoringDeviceName'
        if ($verifyId.ok -and $verifyName.ok -and
            (Test-ReplayKitStringEqual ([string]$verifyId.value) $deviceId) -and
            (Test-ReplayKitStringEqual ([string]$verifyName.value) $deviceName)) {
            return @{ ok = $true; applied = @("OBS monitoring device set to $deviceName"); message = '' }
        }

        $actualId = if ($verifyId.ok) { [string]$verifyId.value } else { $verifyId.message }
        $actualName = if ($verifyName.ok) { [string]$verifyName.value } else { $verifyName.message }
        $lastMessage = "OBS still reports monitoring device '$actualName' ($actualId)."
    }

    return @{ ok = $false; applied = @(); message = "Could not set OBS monitoring device to $deviceName after $attempts attempts. $lastMessage" }
}

function Convert-KeybindToObsBinding($combo) {
    if ($null -eq $combo -or -not $combo.ContainsKey('key') -or [string]::IsNullOrWhiteSpace([string]$combo.key)) {
        return $null
    }
    $bind = [ordered]@{}
    foreach ($mod in @('control', 'alt', 'shift', 'command')) {
        if ($combo.ContainsKey($mod) -and [bool]$combo[$mod]) {
            $bind[$mod] = $true
        }
    }
    $bind.key = [string]$combo.key
    return $bind
}

function Convert-ClipKeybindToBasicIni($combo) {
    $bind = Convert-KeybindToObsBinding $combo
    if ($null -eq $bind) {
        return ConvertTo-Json @{ 'ReplayBuffer.Save' = @() } -Depth 5 -Compress
    }
    return ConvertTo-Json @{ 'ReplayBuffer.Save' = @($bind) } -Depth 5 -Compress
}

function Convert-RecordingKeybindToBasicIni($combo) {
    $bind = Convert-KeybindToObsBinding $combo
    if ($null -eq $bind) {
        return ConvertTo-Json @{ bindings = @() } -Depth 5 -Compress
    }
    return ConvertTo-Json @{ bindings = @($bind) } -Depth 5 -Compress
}

function Get-EmptyObsHotkeyJson {
    return ConvertTo-Json @{ bindings = @() } -Depth 5 -Compress
}

function Get-ReplayKitEvenDimension([double]$value) {
    $n = [int][Math]::Round($value)
    $n = [Math]::Max(2, [Math]::Min(4096, $n))
    if (($n % 2) -ne 0) { $n -= 1 }
    return [Math]::Max(2, $n)
}

function Get-ReplayKitScaledEvenSize([int]$sourceWidth, [int]$sourceHeight, [int]$maxWidth, [int]$maxHeight) {
    if ($sourceWidth -lt 2 -or $sourceHeight -lt 2 -or $maxWidth -lt 2 -or $maxHeight -lt 2) {
        return @{ width = 1920; height = 1080 }
    }
    $scale = [Math]::Min(1.0, [Math]::Min($maxWidth / [double]$sourceWidth, $maxHeight / [double]$sourceHeight))
    return @{
        width = Get-ReplayKitEvenDimension ($sourceWidth * $scale)
        height = Get-ReplayKitEvenDimension ($sourceHeight * $scale)
    }
}

function Get-ReplayKitPrimaryMonitorCanvasSize {
    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
        $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $width = [int]$bounds.Width
        $height = [int]$bounds.Height
        if ($width -ge 320 -and $height -ge 240) {
            return @{ ok = $true; width = $width; height = $height }
        }
    } catch {
        Write-Log "Primary monitor size detection failed: $($_.Exception.Message)"
    }
    return @{ ok = $false; width = 1920; height = 1080 }
}

function Resolve-ReplayKitPresetVideoSpec([int]$targetWidth, [int]$targetHeight, [int]$fpsNumerator, [int]$fpsDenominator) {
    $monitor = Get-ReplayKitPrimaryMonitorCanvasSize
    $sourceWidth = if ($monitor.ok) { [int]$monitor.width } else { 1920 }
    $sourceHeight = if ($monitor.ok) { [int]$monitor.height } else { 1080 }
    $base = Get-ReplayKitScaledEvenSize $sourceWidth $sourceHeight 4096 4096
    $output = Get-ReplayKitScaledEvenSize ([int]$base.width) ([int]$base.height) $targetWidth $targetHeight

    return @{
        video = @{
            baseWidth = [int]$base.width; baseHeight = [int]$base.height
            outputWidth = [int]$output.width; outputHeight = [int]$output.height
            fpsNumerator = $fpsNumerator; fpsDenominator = $fpsDenominator
        }
        profile = @(
            @('Output', 'Mode', 'Advanced'),
            @('AdvOut', 'RecType', 'Standard'),
            @('AdvOut', 'RecTracks', '1'),
            @('Video', 'BaseCX', [string][int]$base.width),
            @('Video', 'BaseCY', [string][int]$base.height),
            @('Video', 'OutputCX', [string][int]$output.width),
            @('Video', 'OutputCY', [string][int]$output.height),
            @('Video', 'FPSCommon', [string]$fpsNumerator),
            @('Video', 'ScaleType', 'lanczos')
        )
        source = @{
            width = $sourceWidth
            height = $sourceHeight
        }
    }
}

function Convert-RecordingBasicIniToKeybind([string]$json) {
    if ([string]::IsNullOrWhiteSpace($json)) { return @{} }
    try {
        $data = ConvertFrom-Json $json
        if ($null -eq $data -or $null -eq $data.bindings) { return @{} }
        $bindings = @($data.bindings)
        if ($bindings.Count -lt 1 -or $null -eq $bindings[0]) { return @{} }
        return Normalize-RecordingKeybind $bindings[0]
    } catch {
        return @{}
    }
}

function Convert-ClipBasicIniToKeybind([string]$json) {
    if ([string]::IsNullOrWhiteSpace($json)) { return @{} }
    try {
        $data = ConvertFrom-Json $json
        if ($null -eq $data -or $null -eq $data.'ReplayBuffer.Save') { return @{} }
        $bindings = @($data.'ReplayBuffer.Save')
        if ($bindings.Count -lt 1 -or $null -eq $bindings[0]) { return @{} }
        return Normalize-ClipKeybind $bindings[0]
    } catch {
        return @{}
    }
}

function Set-ReplayBufferHotkeyJson([string]$json) {
    return Set-ObsProfileParameterSafe 'Hotkeys' 'ReplayBuffer' $json
}

function Set-RecordingHotkeyJson([string]$json) {
    return Set-RecordingHotkeyPairJson $json $json
}

function Set-RecordingHotkeyPairJson([string]$startJson, [string]$stopJson) {
    $errors = @()
    foreach ($entry in @(
        @('OBSBasic.StartRecording', $startJson),
        @('OBSBasic.StopRecording', $stopJson)
    )) {
        $name = [string]$entry[0]
        $json = [string]$entry[1]
        $r = Set-ObsProfileParameterSafe 'Hotkeys' $name $json
        if (-not $r.ok) {
            $errors += "${name}: $($r.message)"
        }
    }
    if ($errors.Count -gt 0) {
        return @{ ok = $false; message = ($errors -join '; ') }
    }
    return @{ ok = $true }
}

function Get-ReplayKitPresetSpec([string]$name) {
    switch ($name) {
        'performance' {
            $videoSpec = Resolve-ReplayKitPresetVideoSpec 1280 720 30 1
            return @{
                cqp = 26
                video = $videoSpec.video
                profile = $videoSpec.profile
                source = $videoSpec.source
            }
        }
        'quality' {
            $videoSpec = Resolve-ReplayKitPresetVideoSpec 1920 1080 60 1
            return @{
                cqp = 20
                video = $videoSpec.video
                profile = $videoSpec.profile
                source = $videoSpec.source
            }
        }
        default {
            $videoSpec = Resolve-ReplayKitPresetVideoSpec 1920 1080 60 1
            return @{
                cqp = 22
                video = $videoSpec.video
                profile = $videoSpec.profile
                source = $videoSpec.source
            }
        }
    }
}

function Get-ReplayKitScaledRbSizeMb([string]$presetName, [int]$replaySeconds) {
    # realistic peak cqp bitrate per preset tier (mbps), not a padded worst-case number -- mirrors _RB_PEAK_MBPS in obs_replaykit/transform.py (the fresh-install/repair path), keep both in sync if this changes. this is the live-apply path the custom settings dock actually hits, so it needs its own copy of the same formula rather than a static per-tier mb value that ignores replaySeconds.
    $peakMbps = switch ($presetName) {
        'performance' { 8 }
        'quality'     { 32 }
        default       { 20 }
    }
    $mbPerSecond = $peakMbps * 1.5 / 8
    return [Math]::Max(32, [Math]::Ceiling($mbPerSecond * $replaySeconds))
}

function Get-ReplayKitNvencEffort([string]$mode) {
    switch ($mode) {
        'lower_gpu'     { return @{ preset = 'p1'; multipass = 'disabled'; lookahead = $false; bf = 0 } }
        'smaller_files' { return @{ preset = 'p5'; multipass = 'qres';     lookahead = $true;  bf = 3 } }
        default         { return @{ preset = 'p2'; multipass = 'disabled'; lookahead = $false; bf = 2 } }
    }
}

function Get-ReplayKitAmfEffort([string]$mode) {
    switch ($mode) {
        'lower_gpu'     { return @{ preset = 'speed';    bf = 0 } }
        'smaller_files' { return @{ preset = 'quality';  bf = 3 } }
        default         { return @{ preset = 'balanced'; bf = 2 } }
    }
}

function Get-ReplayKitQsvEffort([string]$mode) {
    switch ($mode) {
        'lower_gpu'     { return 'veryfast' }
        'smaller_files' { return 'slower' }
        default         { return 'balanced' }
    }
}

function Get-ReplayKitX264Effort([string]$mode) {
    switch ($mode) {
        'lower_gpu'     { return 'superfast' }
        'smaller_files' { return 'medium' }
        default         { return 'veryfast' }
    }
}

function Convert-ReplayKitQsvQuality([int]$cqp) {
    return [Math]::Max(1, [Math]::Min(51, $cqp + 1))
}

function Convert-ReplayKitX264Quality([int]$cqp) {
    return [Math]::Max(0, [Math]::Min(51, $cqp - 2))
}

function New-ReplayKitEncoderSettings([string]$kind, [int]$cqp, [string]$mode, [string]$gen) {
    $nvencHevcBfOk = @('turing', 'ampere', 'ada', 'blackwell')
    $nvencLookaheadOk = @('pascal', 'turing', 'ampere', 'ada', 'blackwell')
    $amfBfOk = @('vega', 'rdna1', 'rdna2', 'rdna3', 'rdna4')

    switch ($kind) {
        'nvenc_h264' {
            $e = Get-ReplayKitNvencEffort $mode
            $s = [ordered]@{
                rate_control = 'CQP'
                cqp = $cqp
                keyint_sec = 2
                preset = $e.preset
                multipass = $e.multipass
                tune = 'hq'
                profile = 'high'
            }
            $s['bf'] = if (@('pascal', 'turing', 'ampere', 'ada', 'blackwell') -contains $gen) { [int]$e.bf } else { 0 }
            $s['lookahead'] = [bool]($e.lookahead -and ($nvencLookaheadOk -contains $gen))
            return $s
        }
        'nvenc_h265' {
            $e = Get-ReplayKitNvencEffort $mode
            $s = [ordered]@{
                rate_control = 'CQP'
                cqp = $cqp + 2
                keyint_sec = 2
                preset = $e.preset
                multipass = $e.multipass
                tune = 'hq'
                profile = 'main'
            }
            $s['bf'] = if ($nvencHevcBfOk -contains $gen) { [int]$e.bf } else { 0 }
            $s['lookahead'] = [bool]($e.lookahead -and ($nvencLookaheadOk -contains $gen))
            return $s
        }
        'nvenc_av1' {
            $e = Get-ReplayKitNvencEffort $mode
            return [ordered]@{
                rate_control = 'CQP'
                cqp = $cqp + 4
                keyint_sec = 2
                preset = $e.preset
                multipass = $e.multipass
                tune = 'hq'
                profile = 'main'
                lookahead = [bool]$e.lookahead
                bf = [int]$e.bf
            }
        }
        'amf_h264' {
            $e = Get-ReplayKitAmfEffort $mode
            return [ordered]@{
                rate_control = 'CQP'
                cqp = $cqp
                keyint_sec = 2
                preset = $e.preset
                profile = 'high'
                filler_data = $false
                bf = [int]$e.bf
            }
        }
        'amf_h265' {
            $e = Get-ReplayKitAmfEffort $mode
            $s = [ordered]@{
                rate_control = 'CQP'
                cqp = $cqp + 2
                keyint_sec = 2
                preset = $e.preset
                profile = 'main'
                filler_data = $false
            }
            $s['bf'] = if ($amfBfOk -contains $gen) { [int]$e.bf } else { 0 }
            return $s
        }
        'amf_av1' {
            $e = Get-ReplayKitAmfEffort $mode
            return [ordered]@{
                rate_control = 'CQP'
                cqp = $cqp + 4
                keyint_sec = 2
                preset = $e.preset
                profile = 'main'
                filler_data = $false
                bf = [int]$e.bf
            }
        }
        'qsv_h264' {
            return [ordered]@{
                rate_control = 'ICQ'
                icq_quality = Convert-ReplayKitQsvQuality $cqp
                keyint_sec = 2
                target_usage = Get-ReplayKitQsvEffort $mode
                profile = 'high'
                async_depth = 4
                low_power = $false
            }
        }
        'qsv_h265' {
            return [ordered]@{
                rate_control = 'ICQ'
                icq_quality = Convert-ReplayKitQsvQuality ($cqp + 2)
                keyint_sec = 2
                target_usage = Get-ReplayKitQsvEffort $mode
                profile = 'main'
                async_depth = 4
                low_power = $false
            }
        }
        'qsv_av1' {
            return [ordered]@{
                rate_control = 'ICQ'
                icq_quality = Convert-ReplayKitQsvQuality ($cqp + 4)
                keyint_sec = 2
                target_usage = Get-ReplayKitQsvEffort $mode
                profile = 'main'
                async_depth = 4
                low_power = $false
            }
        }
        default {
            return [ordered]@{
                rate_control = 'CRF'
                crf = Convert-ReplayKitX264Quality $cqp
                keyint_sec = 2
                preset = Get-ReplayKitX264Effort $mode
                profile = 'high'
                tune = ''
                x264opts = ''
            }
        }
    }
}

function Get-ReplayKitPrimaryGpu {
    try {
        $rows = @(Get-CimInstance Win32_VideoController -ErrorAction Stop)
    } catch {
        return $null
    }

    $out = @()
    foreach ($row in $rows) {
        $name = ([string]$row.Name).Trim()
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        if ($name -match '(?i)\bbasic\s+display\s+adapter\b|\bidd\b|indirect\s+display|\bremote\s+display\b|\bvirtual\s+display\b|\bdisplay\s*link\b') { continue }

        $vendor = 'unknown'
        $gen = $null
        if ($name -match '(?i)\bnvidia\b|\bgeforce\b|\bquadro\b|\brtx\b|\bgtx\b') {
            $vendor = 'nvidia'
            if ($name -match '(?i)\brtx\s*50\d{2}\b') { $gen = 'blackwell' }
            elseif ($name -match '(?i)\brtx\s*40\d{2}\b') { $gen = 'ada' }
            elseif ($name -match '(?i)\brtx\s*30\d{2}\b') { $gen = 'ampere' }
            elseif ($name -match '(?i)\brtx\s*20\d{2}\b|\bgtx\s*16\d{2}\b|\btitan\s*rtx\b') { $gen = 'turing' }
            elseif ($name -match '(?i)\bgtx\s*10\d{2}\b|\btitan\s*xp\b|\bquadro\s*p\d') { $gen = 'pascal' }
            elseif ($name -match '(?i)\bgtx\s*9\d{2}\b|\btitan\s*x\b') { $gen = 'maxwell2' }
            else { $gen = 'pre_maxwell2' }
        } elseif ($name -match '(?i)\bamd\b|\bradeon\b|\brx\s*\d') {
            $vendor = 'amd'
            if ($name -match '(?i)\brx\s*9\d{3}\b') { $gen = 'rdna4' }
            elseif ($name -match '(?i)\brx\s*7\d{3}\b') { $gen = 'rdna3' }
            elseif ($name -match '(?i)\brx\s*6\d{3}\b|\bradeon\s+graphics\s*\(rembrandt\)') { $gen = 'rdna2' }
            elseif ($name -match '(?i)\brx\s*5\d{3}\b') { $gen = 'rdna1' }
            elseif ($name -match '(?i)\brx\s+vega\b|\bvega\s*\d|\bradeon\s+vii\b') { $gen = 'vega' }
            elseif ($name -match '(?i)\brx\s*4\d{2}\b|\brx\s*5\d{2}(?!\d)') { $gen = 'polaris' }
            else { $gen = 'pre_polaris' }
        } elseif ($name -match '(?i)\bintel\b|\biris\b|\barc\b|\buhd\b|\bhd\s*graphics\b') {
            $vendor = 'intel'
            if ($name -match '(?i)\barc\s*a?\d{3}\b|\bxe-hpg\b') { $gen = 'arc' }
            elseif ($name -match '(?i)\b(12|13|14)\d{2}[a-z]?\b.*(uhd|iris)') { $gen = 'alder_lake' }
            elseif ($name -match '(?i)\b11\d{2}[a-z]?\b.*(iris|xe)|\bxe\s*graphics\b') { $gen = 'tiger_lake' }
            elseif ($name -match '(?i)\b10\d{2}[a-z]?\b.*iris') { $gen = 'ice_lake' }
            elseif ($name -match '(?i)\b(uhd|hd)\s*graphics\s*6\d{2}\b') { $gen = 'kaby_lake' }
            elseif ($name -match '(?i)\bhd\s*graphics\s*5\d{2}\b') { $gen = 'skylake' }
            else { $gen = 'pre_skylake' }
        }

        $vram = 0
        try { $vram = [int64]$row.AdapterRAM } catch { $vram = 0 }
        $isDiscrete = ($vram -ge 2147483648 -and $vendor -ne 'intel')
        $rank = switch ($vendor) {
            'nvidia' { 0 }
            'amd'    { 1 }
            'intel'  { 2 }
            default  { 3 }
        }
        $out += [pscustomobject]@{
            Name = $name
            Vendor = $vendor
            Generation = $gen
            DiscreteRank = if ($isDiscrete) { 0 } else { 1 }
            VendorRank = $rank
        }
    }
    return @($out | Sort-Object DiscreteRank, VendorRank | Select-Object -First 1)[0]
}

function Get-ReplayKitEncoderSpec([hashtable]$settings, [hashtable]$preset) {
    $gpu = Get-ReplayKitPrimaryGpu
    $candidates = @{}
    $gen = if ($null -ne $gpu -and $gpu.Generation) { [string]$gpu.Generation } else { '' }

    if ($null -ne $gpu -and $gpu.Vendor -eq 'nvidia') {
        $candidates['h264'] = @{ id = 'obs_nvenc_h264_tex'; codec = 'h264'; kind = 'nvenc_h264'; label = 'NVENC H.264' }
        if (@('maxwell2', 'pascal', 'turing', 'ampere', 'ada', 'blackwell') -contains $gen) {
            $candidates['h265'] = @{ id = 'obs_nvenc_hevc_tex'; codec = 'h265'; kind = 'nvenc_h265'; label = 'NVENC HEVC' }
        }
    } elseif ($null -ne $gpu -and $gpu.Vendor -eq 'amd') {
        $candidates['h264'] = @{ id = 'h264_amf'; codec = 'h264'; kind = 'amf_h264'; label = 'AMF H.264' }
        if (@('polaris', 'vega', 'rdna1', 'rdna2', 'rdna3', 'rdna4') -contains $gen) {
            $candidates['h265'] = @{ id = 'h265_amf'; codec = 'h265'; kind = 'amf_h265'; label = 'AMF HEVC' }
        }
    } elseif ($null -ne $gpu -and $gpu.Vendor -eq 'intel') {
        $candidates['h264'] = @{ id = 'obs_qsv11_h264'; codec = 'h264'; kind = 'qsv_h264'; label = 'Quick Sync H.264' }
        if (@('skylake', 'kaby_lake', 'ice_lake', 'tiger_lake', 'alder_lake', 'arc') -contains $gen) {
            $candidates['h265'] = @{ id = 'obs_qsv11_hevc'; codec = 'h265'; kind = 'qsv_h265'; label = 'Quick Sync HEVC' }
        }
    }
    $candidates['software'] = @{ id = 'obs_x264'; codec = 'h264'; kind = 'x264'; label = 'x264 software' }

    $pref = [string]$settings.codecPreference
    $order = switch ($pref) {
        'h265' { @('h265', 'h264', 'software') }
        'h264' { @('h264', 'software') }
        default { @('h265', 'h264', 'software') }
    }

    foreach ($codec in $order) {
        if (-not $candidates.ContainsKey($codec)) { continue }
        $choice = $candidates[$codec]
        $choice['settings'] = New-ReplayKitEncoderSettings ([string]$choice.kind) ([int]$preset.cqp) ([string]$settings.compressionMode) $gen
        if ($pref -ne 'auto' -and [string]$choice.codec -ne $pref) {
            $choice['warning'] = "Requested codec '$pref' is not supported by the detected GPU, so ReplayKit selected $($choice.label)."
        }
        return $choice
    }
    return $candidates['software']
}

function Get-ReplayKitCurrentProfileDir {
    $profilesRoot = Join-Path $env:APPDATA 'obs-studio\basic\profiles'
    $profileName = 'Untitled'
    $profileList = Invoke-ObsWebSocketRequest 'GetProfileList' $null 3000
    if ($profileList.ok -and $profileList.data.currentProfileName) {
        $profileName = [string]$profileList.data.currentProfileName
    }
    $rootFull = [System.IO.Path]::GetFullPath($profilesRoot).TrimEnd([char]'\')
    $profileDir = [System.IO.Path]::GetFullPath((Join-Path $profilesRoot $profileName))
    if (-not $profileDir.StartsWith($rootFull + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'OBS profile path resolved outside the OBS profiles directory.'
    }
    return $profileDir
}

function Write-ReplayKitRecordEncoderJson([hashtable]$encoder) {
    try {
        $profileDir = Get-ReplayKitCurrentProfileDir
        if (-not (Test-Path -LiteralPath $profileDir)) {
            [void](New-Item -ItemType Directory -Path $profileDir -Force)
        }
        $path = Join-Path $profileDir 'recordEncoder.json'
        Write-Utf8 $path (ConvertTo-Json $encoder.settings -Depth 8 -Compress)
        return @{ ok = $true; path = $path }
    } catch {
        return @{ ok = $false; message = $_.Exception.Message }
    }
}

function ConvertTo-RecursiveHash($value) {
    if ($null -eq $value) { return $null }
    if ($value -is [hashtable]) {
        $out = @{}
        foreach ($k in $value.Keys) { $out[$k] = ConvertTo-RecursiveHash $value[$k] }
        return $out
    }
    if ($value -is [System.Management.Automation.PSCustomObject]) {
        $out = @{}
        foreach ($p in $value.PSObject.Properties) { $out[$p.Name] = ConvertTo-RecursiveHash $p.Value }
        return $out
    }
    if ($value -is [System.Collections.IEnumerable] -and -not ($value -is [string])) {
        $arr = @()
        foreach ($item in $value) { $arr += ,(ConvertTo-RecursiveHash $item) }
        return $arr
    }
    return $value
}

function Stop-ObsOutputIfActive([string]$statusRequest, [string]$stopRequest, [string]$label) {
    $status = Invoke-ObsWebSocketRequest $statusRequest $null 3000
    if (-not $status.ok) {
        return @{ ok = $true; wasActive = $false; warning = "Could not read $label state: $($status.message)" }
    }
    if (-not [bool]$status.data.outputActive) {
        return @{ ok = $true; wasActive = $false }
    }
    $stop = Invoke-ObsWebSocketRequest $stopRequest $null 8000
    if (-not $stop.ok) {
        return @{ ok = $false; wasActive = $true; warning = "Could not stop $label before applying live settings: $($stop.message)" }
    }
    $wait = Wait-ObsOutputState $statusRequest $false $label 8000
    if (-not $wait.ok) {
        return @{ ok = $false; wasActive = $true; warning = $wait.warning }
    }
    return @{ ok = $true; wasActive = $true }
}

function Wait-ObsOutputState([string]$statusRequest, [bool]$desiredActive, [string]$label, [int]$timeoutMs) {
    $deadline = [DateTime]::UtcNow.AddMilliseconds([Math]::Max(1000, $timeoutMs))
    do {
        $status = Invoke-ObsWebSocketRequest $statusRequest $null 3000
        if ($status.ok -and [bool]$status.data.outputActive -eq $desiredActive) {
            return @{ ok = $true }
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    $want = if ($desiredActive) { 'start' } else { 'stop' }
    return @{ ok = $false; warning = "Timed out waiting for $label to $want." }
}

function Start-ObsOutputIfNeeded([hashtable]$state, [string]$startRequest, [string]$label) {
    if (-not $state.wasActive) { return @{ ok = $true } }
    $start = Invoke-ObsWebSocketRequest $startRequest $null 8000
    if (-not $start.ok) {
        return @{ ok = $false; warning = "Could not restart $label after applying live settings: $($start.message)" }
    }
    $statusRequest = switch ($startRequest) {
        'StartRecord'       { 'GetRecordStatus' }
        'StartReplayBuffer' { 'GetReplayBufferStatus' }
        'StartVirtualCam'   { 'GetVirtualCamStatus' }
        default             { '' }
    }
    if ($statusRequest) {
        $wait = Wait-ObsOutputState $statusRequest $true $label 8000
        if (-not $wait.ok) { return @{ ok = $false; warning = $wait.warning } }
    }
    return @{ ok = $true }
}

function Set-ReplayKitVideoSettingsLive([hashtable]$preset) {
    return Invoke-ObsWebSocketRequest 'SetVideoSettings' $preset.video 5000
}

function Set-ReplayKitReplayBufferOutputLive([hashtable]$settings, [hashtable]$preset) {
    $outputSettings = @{}
    $existing = Invoke-ObsWebSocketRequest 'GetOutputSettings' @{ outputName = 'Replay Buffer' } 3000
    if ($existing.ok -and $existing.data.outputSettings) {
        $outputSettings = ConvertTo-RecursiveHash $existing.data.outputSettings
    }

    $clipDir = [string]$settings.clipDir
    if ([string]::IsNullOrWhiteSpace($clipDir)) { $clipDir = Get-DefaultClipDir }
    $outputSettings['max_time_sec'] = [int]$settings.replaySeconds
    $outputSettings['max_size_mb'] = Get-ReplayKitScaledRbSizeMb ([string]$settings.recordingPreset) ([int]$settings.replaySeconds)
    $outputSettings['directory'] = $clipDir
    $outputSettings['path'] = $clipDir

    return Invoke-ObsWebSocketRequest 'SetOutputSettings' @{
        outputName = 'Replay Buffer'
        outputSettings = $outputSettings
    } 5000
}

function Find-ReplayKitOverlayAsset([string]$relativePath) {
    $root = Join-Path (Split-Path -Parent (Get-ReplayKitScriptsDir)) 'input-overlay-presets'
    if (-not (Test-Path -LiteralPath $root)) { return '' }
    $candidate = Join-Path $root $relativePath
    if (Test-Path -LiteralPath $candidate) { return $candidate }

    $parts = $relativePath -split '[\\/]'
    if ($parts.Count -lt 2) { return '' }
    $folder = $parts[$parts.Count - 2]
    $leaf = $parts[$parts.Count - 1]
    $match = Get-ChildItem -LiteralPath $root -Recurse -File -Filter $leaf -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -replace '/', '\' -like "*\$folder\$leaf" } |
        Select-Object -First 1
    if ($match) { return $match.FullName }
    return ''
}

function Get-ReplayKitSceneName {
    $scenes = Invoke-ObsWebSocketRequest 'GetSceneList' $null 3000
    if (-not $scenes.ok) { return @{ ok = $false; message = $scenes.message } }
    if ($scenes.data.currentProgramSceneName) {
        return @{ ok = $true; name = [string]$scenes.data.currentProgramSceneName }
    }
    $list = @($scenes.data.scenes)
    if ($list.Count -gt 0) { return @{ ok = $true; name = [string]$list[0].sceneName } }
    return @{ ok = $false; message = 'No OBS scene is available.' }
}

function Get-ReplayKitSceneItems([string]$sceneName) {
    $r = Invoke-ObsWebSocketRequest 'GetSceneItemList' @{ sceneName = $sceneName } 3000
    if (-not $r.ok) { return @{ ok = $false; message = $r.message; items = @() } }
    return @{ ok = $true; items = @($r.data.sceneItems) }
}

function Find-ReplayKitSceneItem($items, [string]$sourceName) {
    foreach ($item in @($items)) {
        if ([string]$item.sourceName -eq $sourceName) { return $item }
    }
    return $null
}

function Set-ReplayKitSceneItemEnabled([string]$sceneName, $item, [bool]$enabled) {
    if ($null -eq $item) { return @{ ok = $true; skipped = $true } }
    return Invoke-ObsWebSocketRequest 'SetSceneItemEnabled' @{
        sceneName = $sceneName
        sceneItemId = [int]$item.sceneItemId
        sceneItemEnabled = $enabled
    } 3000
}

function Get-ReplayKitSettableSceneItemTransform($transform) {
    $out = @{}
    if ($null -eq $transform) { return $out }
    foreach ($key in @(
        'positionX', 'positionY', 'scaleX', 'scaleY', 'rotation',
        'alignment', 'boundsType', 'boundsAlignment', 'cropToBounds',
        'cropLeft', 'cropRight', 'cropTop', 'cropBottom'
    )) {
        $value = Get-ReplayKitJsonValue $transform $key $null
        if ($null -ne $value) { $out[$key] = $value }
    }
    foreach ($key in @('boundsWidth', 'boundsHeight')) {
        $value = Get-ReplayKitJsonValue $transform $key $null
        if ($null -eq $value) { continue }
        try {
            if ([double]$value -gt 0.0) { $out[$key] = $value }
        } catch {}
    }
    return $out
}

function Set-ReplayKitSceneItemTransform([string]$sceneName, $item, $transform) {
    if ($null -eq $item) { return @{ ok = $true; skipped = $true } }
    return Invoke-ObsWebSocketRequest 'SetSceneItemTransform' @{
        sceneName = $sceneName
        sceneItemId = [int]$item.sceneItemId
        sceneItemTransform = (Get-ReplayKitSettableSceneItemTransform $transform)
    } 3000
}

function Get-ReplayKitSceneItemTransformLive([string]$sceneName, $item) {
    if ($null -eq $item) { return @{ ok = $true; skipped = $true; transform = $null } }
    $r = Invoke-ObsWebSocketRequest 'GetSceneItemTransform' @{
        sceneName = $sceneName
        sceneItemId = [int]$item.sceneItemId
    } 3000
    if (-not $r.ok) { return @{ ok = $false; message = $r.message; transform = $null } }
    $transform = Get-ReplayKitJsonValue $r.data 'sceneItemTransform' $null
    if ($null -eq $transform) { $transform = $r.data }
    return @{ ok = $true; skipped = $false; transform = $transform }
}

function Get-ReplayKitDoubleValue($obj, [string]$key, [double]$default) {
    $value = Get-ReplayKitJsonValue $obj $key $null
    if ($null -eq $value) { return $default }
    try { return [double]$value } catch { return $default }
}

function Get-ReplayKitScaledTransformFromCurrent($transform, [double]$scaleRatio, [hashtable]$preset = $null) {
    if ($null -eq $transform) { return @{} }
    if ($scaleRatio -le 0.0) { $scaleRatio = 1.0 }

    $positionX = Get-ReplayKitDoubleValue $transform 'positionX' 0.0
    $positionY = Get-ReplayKitDoubleValue $transform 'positionY' 0.0
    $scaleX = Get-ReplayKitDoubleValue $transform 'scaleX' 1.0
    $scaleY = Get-ReplayKitDoubleValue $transform 'scaleY' 1.0
    $width = Get-ReplayKitDoubleValue $transform 'width' 0.0
    $height = Get-ReplayKitDoubleValue $transform 'height' 0.0
    if ($width -le 0.0) {
        $sourceWidth = Get-ReplayKitDoubleValue $transform 'sourceWidth' 0.0
        $width = $sourceWidth * [Math]::Abs($scaleX)
    }
    if ($height -le 0.0) {
        $sourceHeight = Get-ReplayKitDoubleValue $transform 'sourceHeight' 0.0
        $height = $sourceHeight * [Math]::Abs($scaleY)
    }
    if ($width -lt 0.0) { $width = 0.0 }
    if ($height -lt 0.0) { $height = 0.0 }

    $nextWidth = $width * $scaleRatio
    $nextHeight = $height * $scaleRatio
    $nextPositionX = $positionX + (($width - $nextWidth) / 2.0)
    $nextPositionY = $positionY + (($height - $nextHeight) / 2.0)

    $canvasW = 0.0
    $canvasH = 0.0
    if ($null -ne $preset -and $null -ne $preset.video) {
        $canvasW = Get-ReplayKitDoubleValue $preset.video 'baseWidth' 0.0
        $canvasH = Get-ReplayKitDoubleValue $preset.video 'baseHeight' 0.0
    }
    if ($canvasW -gt 0.0 -and $width -gt 0.0) {
        $centerX = $positionX + ($width / 2.0)
        if ($centerX -le ($canvasW / 3.0)) {
            $nextPositionX = $positionX
        } elseif ($centerX -ge (($canvasW * 2.0) / 3.0)) {
            $nextPositionX = $positionX + ($width - $nextWidth)
        }
    }
    if ($canvasH -gt 0.0 -and $height -gt 0.0) {
        $centerY = $positionY + ($height / 2.0)
        if ($centerY -le ($canvasH / 3.0)) {
            $nextPositionY = $positionY
        } elseif ($centerY -ge (($canvasH * 2.0) / 3.0)) {
            $nextPositionY = $positionY + ($height - $nextHeight)
        }
    }

    return @{
        positionX = $nextPositionX
        positionY = $nextPositionY
        scaleX = $scaleX * $scaleRatio
        scaleY = $scaleY * $scaleRatio
    }
}

function Set-ReplayKitSceneItemScaledFromCurrent([string]$sceneName, $item, [double]$scaleRatio, [hashtable]$preset = $null) {
    if ($null -eq $item) { return @{ ok = $true; skipped = $true } }
    $current = Get-ReplayKitSceneItemTransformLive $sceneName $item
    if (-not $current.ok) { return $current }
    $transform = Get-ReplayKitScaledTransformFromCurrent $current.transform $scaleRatio $preset
    return Set-ReplayKitSceneItemTransform $sceneName $item $transform
}

function Ensure-ReplayKitInputSceneItem([string]$sceneName, [string]$name, [string]$kind, [hashtable]$inputSettings, [bool]$enabled) {
    $items = Get-ReplayKitSceneItems $sceneName
    if (-not $items.ok) { return $items }
    $item = Find-ReplayKitSceneItem $items.items $name
    if ($null -ne $item) {
        $settingsResult = Invoke-ObsWebSocketRequest 'SetInputSettings' @{
            inputName = $name
            inputSettings = $inputSettings
            overlay = $true
        } 3000
        if (-not $settingsResult.ok) { return $settingsResult }
        $enableResult = Set-ReplayKitSceneItemEnabled $sceneName $item $enabled
        if (-not $enableResult.ok) { return $enableResult }
        return @{ ok = $true; item = $item }
    }

    $created = Invoke-ObsWebSocketRequest 'CreateInput' @{
        sceneName = $sceneName
        inputName = $name
        inputKind = $kind
        inputSettings = $inputSettings
        sceneItemEnabled = $enabled
    } 5000
    if (-not $created.ok) {
        $settingsResult = Invoke-ObsWebSocketRequest 'SetInputSettings' @{
            inputName = $name
            inputSettings = $inputSettings
            overlay = $true
        } 3000
        if (-not $settingsResult.ok) { return $created }
        $sceneItem = Invoke-ObsWebSocketRequest 'CreateSceneItem' @{
            sceneName = $sceneName
            sourceName = $name
            sceneItemEnabled = $enabled
        } 5000
        if (-not $sceneItem.ok) { return $created }
        return @{ ok = $true; item = [pscustomobject]@{ sceneItemId = [int]$sceneItem.data.sceneItemId; sourceName = $name } }
    }
    return @{ ok = $true; item = [pscustomobject]@{ sceneItemId = [int]$created.data.sceneItemId; sourceName = $name } }
}

function Get-ReplayKitSceneItemIndexValue([string]$sceneName, $item) {
    if ($null -eq $item) { return @{ ok = $false; message = 'Scene item is missing.'; index = -1 } }
    $value = Get-ReplayKitJsonValue $item 'sceneItemIndex' $null
    if ($null -ne $value) {
        try { return @{ ok = $true; index = [int]$value } } catch {}
    }
    $result = Invoke-ObsWebSocketRequest 'GetSceneItemIndex' @{
        sceneName = $sceneName
        sceneItemId = [int]$item.sceneItemId
    } 3000
    if (-not $result.ok) { return @{ ok = $false; message = $result.message; index = -1 } }
    return @{ ok = $true; index = [int]$result.data.sceneItemIndex }
}

function Set-ReplayKitSceneItemIndex([string]$sceneName, $item, [int]$index) {
    if ($null -eq $item) { return @{ ok = $true; skipped = $true } }
    if ($index -lt 0) { $index = 0 }
    return Invoke-ObsWebSocketRequest 'SetSceneItemIndex' @{
        sceneName = $sceneName
        sceneItemId = [int]$item.sceneItemId
        sceneItemIndex = $index
    } 3000
}

function Set-ReplayKitWindowCaptureSceneOrder([string]$sceneName, $items) {
    $window = Find-ReplayKitSceneItem $items 'Window Capture'
    if ($null -eq $window) { return @{ ok = $true; skipped = $true } }
    $display = Find-ReplayKitSceneItem $items 'Display Capture'
    if ($null -eq $display) { return @{ ok = $true; skipped = $true } }

    $displayIndex = Get-ReplayKitSceneItemIndexValue $sceneName $display
    if (-not $displayIndex.ok) { return $displayIndex }
    $windowIndex = Get-ReplayKitSceneItemIndexValue $sceneName $window
    if (-not $windowIndex.ok) { return $windowIndex }

    $targetIndex = [Math]::Max(0, [int]$displayIndex.index + 1)
    if ([int]$windowIndex.index -eq $targetIndex) {
        return @{ ok = $true; skipped = $true }
    }
    return Set-ReplayKitSceneItemIndex $sceneName $window $targetIndex
}

function Get-ReplayKitOverlayOpacity([hashtable]$settings) {
    if ($null -eq $settings -or -not $settings.ContainsKey('overlayOpacity')) { return 100 }
    $value = [int]$settings.overlayOpacity
    if ($value -lt 0) { return 0 }
    if ($value -gt 100) { return 100 }
    return $value
}

function Get-ReplayKitOverlayHueShift([hashtable]$settings) {
    if ($null -eq $settings -or -not $settings.ContainsKey('overlayHueShift')) { return 0.0 }
    try { $value = [double]$settings.overlayHueShift } catch { $value = 0.0 }
    if ($value -lt -180.0) { return -180.0 }
    if ($value -gt 180.0) { return 180.0 }
    return $value
}

function Get-ReplayKitOverlayHexColor([hashtable]$settings, [string]$key, [string]$default) {
    if ($null -eq $settings -or -not $settings.ContainsKey($key)) { return $default }
    $value = ([string]$settings[$key]).Trim().ToLowerInvariant()
    if ($value -match '^#[0-9a-f]{6}$') { return $value }
    return $default
}

function Convert-ReplayKitHexColorToObsValue([string]$value, [string]$default) {
    $hex = $value
    if ($hex -notmatch '^#[0-9a-fA-F]{6}$') { $hex = $default }
    $red = [Convert]::ToInt32($hex.Substring(1, 2), 16)
    $green = [Convert]::ToInt32($hex.Substring(3, 2), 16)
    $blue = [Convert]::ToInt32($hex.Substring(5, 2), 16)
    return ($red -bor ($green -shl 8) -bor ($blue -shl 16))
}

function Test-ReplayKitOverlayColorAdjusted([hashtable]$settings) {
    return (
        [Math]::Abs((Get-ReplayKitOverlayHueShift $settings)) -ge 0.001 -or
        (Get-ReplayKitOverlayHexColor $settings 'overlayColorMultiply' '#ffffff') -ne '#ffffff' -or
        (Get-ReplayKitOverlayHexColor $settings 'overlayColorAdd' '#000000') -ne '#000000'
    )
}

function Get-ReplayKitOverlayScaleFactor([hashtable]$settings) {
    if ($null -eq $settings -or -not $settings.ContainsKey('overlayScale')) { return 1.0 }
    $value = [int]$settings.overlayScale
    if ($value -lt 50) { $value = 50 }
    if ($value -gt 200) { $value = 200 }
    return ([double]$value / 100.0)
}

function Get-ReplayKitOverlayContentRect([hashtable]$preset) {
    $canvasW = [double]$preset.video.baseWidth
    $canvasH = [double]$preset.video.baseHeight
    if ($canvasW -le 0.0 -or $canvasH -le 0.0) {
        $canvasW = 1920.0
        $canvasH = 1080.0
    }
    $sourceW = $canvasW
    $sourceH = $canvasH
    if ($null -ne $preset.source) {
        $sourceCandidateW = Get-ReplayKitDoubleValue $preset.source 'width' 0.0
        $sourceCandidateH = Get-ReplayKitDoubleValue $preset.source 'height' 0.0
        if ($sourceCandidateW -gt 0.0) { $sourceW = $sourceCandidateW }
        if ($sourceCandidateH -gt 0.0) { $sourceH = $sourceCandidateH }
    }
    if ($sourceW -le 0.0) { $sourceW = $canvasW }
    if ($sourceH -le 0.0) { $sourceH = $canvasH }
    $scale = [Math]::Min($canvasW / $sourceW, $canvasH / $sourceH)
    if ($scale -le 0.0) { $scale = 1.0 }
    $width = $sourceW * $scale
    $height = $sourceH * $scale
    return @{
        x = ($canvasW - $width) / 2.0
        y = ($canvasH - $height) / 2.0
        width = $width
        height = $height
        scale = $scale
        canvasWidth = $canvasW
        canvasHeight = $canvasH
        sourceWidth = $sourceW
        sourceHeight = $sourceH
    }
}

function Get-ReplayKitInputOverlayPosition([hashtable]$preset, [double]$sourceW, [double]$sourceH, [double]$scaleX, [double]$scaleY) {
    $content = Get-ReplayKitOverlayContentRect $preset
    $refScale = [double]$content.height / 1080.0
    $x = [double]$content.x + (15.0 * $refScale)
    $y = [double]$content.y + [double]$content.height - ($sourceH * $scaleY) - (16.0 * $refScale)
    if ($x -lt 0.0) { $x = 0.0 }
    if ($y -lt 0.0) { $y = 0.0 }
    return @{ x = $x; y = $y }
}

function Get-ReplayKitBottomLeftCornerOverlayPosition([hashtable]$preset, [double]$sourceW, [double]$sourceH, [double]$scaleX, [double]$scaleY) {
    $content = Get-ReplayKitOverlayContentRect $preset
    $x = [double]$content.x
    $y = [double]$content.y + [double]$content.height - ($sourceH * $scaleY)
    if ($x -lt 0.0) { $x = 0.0 }
    if ($y -lt 0.0) { $y = 0.0 }
    return @{ x = $x; y = $y }
}

function Get-ReplayKitInputOverlayGroupTransform([hashtable]$preset, [hashtable]$settings = $null) {
    $content = Get-ReplayKitOverlayContentRect $preset
    $scale = ([double]$content.height / 1440.0) * (Get-ReplayKitOverlayScaleFactor $settings)
    $pos = Get-ReplayKitInputOverlayPosition $preset 628.0 292.0 $scale $scale
    return @{
        positionX = [double]$pos.x
        positionY = [double]$pos.y
        scaleX = $scale
        scaleY = $scale
        rotation = 0.0
        alignment = 5
        boundsType = 'OBS_BOUNDS_NONE'
        boundsAlignment = 0
        cropToBounds = $false
    }
}

function Get-ReplayKitInputOverlayTransform([string]$name, [hashtable]$preset, [hashtable]$settings = $null) {
    $content = Get-ReplayKitOverlayContentRect $preset
    $scale = ([double]$content.height / 1440.0) * (Get-ReplayKitOverlayScaleFactor $settings)
    $group = Get-ReplayKitInputOverlayGroupTransform $preset $settings
    if ($name -eq 'Mouse Overlay') {
        return @{
            positionX = [double]$group.positionX + (431.0 * [double]$group.scaleX)
            positionY = [double]$group.positionY
            scaleX = 0.6909722089767456 * $scale
            scaleY = 0.6909722089767456 * $scale
            rotation = 0.0
            alignment = 5
            boundsType = 'OBS_BOUNDS_NONE'
            boundsAlignment = 0
            cropToBounds = $false
        }
    }
    return @{
        positionX = [double]$group.positionX
        positionY = [double]$group.positionY
        scaleX = 0.7395833134651184 * $scale
        scaleY = 0.7388888597488403 * $scale
        rotation = 0.0
        alignment = 5
        boundsType = 'OBS_BOUNDS_NONE'
        boundsAlignment = 0
        cropToBounds = $false
    }
}

function Get-ReplayKitBongoTransform([hashtable]$preset, [hashtable]$settings = $null) {
    $content = Get-ReplayKitOverlayContentRect $preset
    $ratio = ([double]$content.height / 1080.0) * (Get-ReplayKitOverlayScaleFactor $settings)
    $scaleX = 0.4892578125 * $ratio
    $scaleY = 0.48828125 * $ratio
    $pos = Get-ReplayKitBottomLeftCornerOverlayPosition $preset 1280.0 768.0 $scaleX $scaleY
    return @{
        positionX = [double]$pos.x
        positionY = [double]$pos.y
        scaleX = $scaleX
        scaleY = $scaleY
        rotation = 0.0
        alignment = 5
        boundsType = 'OBS_BOUNDS_NONE'
        boundsAlignment = 0
        cropToBounds = $false
    }
}

function Get-ReplayKitMainCaptureTransform([string]$name, [hashtable]$preset) {
    $canvasW = [double]$preset.video.baseWidth
    $canvasH = [double]$preset.video.baseHeight
    if ($canvasW -le 0.0 -or $canvasH -le 0.0) {
        $canvasW = 1920.0
        $canvasH = 1080.0
    }

    if ($name -eq 'Display Capture') {
        $sourceW = $canvasW
        $sourceH = $canvasH
        if ($null -ne $preset.source) {
            if ([double]$preset.source.width -gt 0.0) { $sourceW = [double]$preset.source.width }
            if ([double]$preset.source.height -gt 0.0) { $sourceH = [double]$preset.source.height }
        }
        $scale = [Math]::Min($canvasW / $sourceW, $canvasH / $sourceH)
        $scaledW = $sourceW * $scale
        $scaledH = $sourceH * $scale
        return @{
            positionX = ($canvasW - $scaledW) / 2.0
            positionY = ($canvasH - $scaledH) / 2.0
            scaleX = $scale
            scaleY = $scale
            rotation = 0.0
            alignment = 5
            boundsType = 'OBS_BOUNDS_NONE'
            boundsAlignment = 0
            cropToBounds = $false
            sourceWidth = $sourceW
            sourceHeight = $sourceH
        }
    }

    return @{
        positionX = 0.0
        positionY = 0.0
        scaleX = 1.0
        scaleY = 1.0
        rotation = 0.0
        alignment = 5
        boundsType = 'OBS_BOUNDS_SCALE_INNER'
        boundsWidth = $canvasW
        boundsHeight = $canvasH
        boundsAlignment = 0
        cropToBounds = $false
        sourceWidth = $canvasW
        sourceHeight = $canvasH
    }
}

function New-ReplayKitJsonObject {
    return ,[System.Collections.Generic.Dictionary[string, object]]::new()
}

function Set-ReplayKitJsonValue($obj, [string]$key, $value) {
    if ($obj -is [System.Collections.IDictionary]) {
        if ($obj.ContainsKey($key)) { $obj[$key] = $value }
        else { $obj.Add($key, $value) }
        return
    }
    $prop = $obj.PSObject.Properties[$key]
    if ($null -ne $prop) { $prop.Value = $value }
    else { Add-Member -InputObject $obj -NotePropertyName $key -NotePropertyValue $value }
}

function Get-ReplayKitJsonValue($obj, [string]$key, $default = $null) {
    if ($null -eq $obj) { return ,$default }
    if ($obj -is [System.Collections.IDictionary]) {
        if ($obj.ContainsKey($key)) { return ,$obj[$key] }
        return ,$default
    }
    $prop = $obj.PSObject.Properties[$key]
    if ($null -ne $prop) { return ,$prop.Value }
    return ,$default
}

function New-ReplayKitJsonPoint([double]$x, [double]$y) {
    $point = New-ReplayKitJsonObject
    Set-ReplayKitJsonValue $point 'x' $x
    Set-ReplayKitJsonValue $point 'y' $y
    return ,$point
}

function ConvertTo-ReplayKitJsonList($items) {
    $list = [System.Collections.ArrayList]::new()
    if ($null -eq $items) { return ,$list }
    if ($items -is [string] -or $items -is [System.Collections.IDictionary]) {
        [void]$list.Add($items)
        return ,$list
    }
    if ($items -is [System.Collections.IEnumerable]) {
        foreach ($item in $items) { [void]$list.Add($item) }
        return ,$list
    }
    [void]$list.Add($items)
    return ,$list
}

function Copy-ReplayKitJsonValue($value) {
    if ($null -eq $value) { return $null }
    if ($value -is [System.Collections.IDictionary]) {
        $copy = New-ReplayKitJsonObject
        foreach ($key in $value.Keys) {
            Set-ReplayKitJsonValue $copy ([string]$key) (Copy-ReplayKitJsonValue $value[$key])
        }
        return ,$copy
    }
    if ($value -is [System.Collections.IEnumerable] -and -not ($value -is [string])) {
        $copy = [System.Collections.ArrayList]::new()
        foreach ($item in $value) {
            [void]$copy.Add((Copy-ReplayKitJsonValue $item))
        }
        return ,$copy
    }
    return $value
}

function Get-ReplayKitSceneCollectionPath {
    $scenesRoot = Join-Path $env:APPDATA 'obs-studio\basic\scenes'
    $collectionName = 'Untitled'
    $list = Invoke-ObsWebSocketRequest 'GetSceneCollectionList' $null 3000
    if ($list.ok -and $list.data.currentSceneCollectionName) {
        $collectionName = [string]$list.data.currentSceneCollectionName
    }
    if ([string]::IsNullOrWhiteSpace($collectionName) -or $collectionName.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0) {
        throw 'OBS scene collection name is invalid.'
    }

    $rootFull = [System.IO.Path]::GetFullPath($scenesRoot).TrimEnd([char]'\')
    $path = [System.IO.Path]::GetFullPath((Join-Path $scenesRoot ($collectionName + '.json')))
    if (-not $path.StartsWith($rootFull + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'OBS scene collection path resolved outside the OBS scenes directory.'
    }
    return $path
}

function Set-ReplayKitBongoSourceJson($source, [string]$uuid) {
    Set-ReplayKitJsonValue $source 'prev_ver' 536936450
    Set-ReplayKitJsonValue $source 'name' 'Bongo Cat Overlay'
    Set-ReplayKitJsonValue $source 'uuid' $uuid
    Set-ReplayKitJsonValue $source 'id' 'bongobs-cat'
    Set-ReplayKitJsonValue $source 'versioned_id' 'bongobs-cat'
    Set-ReplayKitJsonValue $source 'mixers' 0
    Set-ReplayKitJsonValue $source 'sync' 0
    Set-ReplayKitJsonValue $source 'flags' 0
    Set-ReplayKitJsonValue $source 'volume' 1.0
    Set-ReplayKitJsonValue $source 'balance' 0.5
    Set-ReplayKitJsonValue $source 'enabled' $true
    Set-ReplayKitJsonValue $source 'muted' $false
    Set-ReplayKitJsonValue $source 'push-to-mute' $false
    Set-ReplayKitJsonValue $source 'push-to-mute-delay' 0
    Set-ReplayKitJsonValue $source 'push-to-talk' $false
    Set-ReplayKitJsonValue $source 'push-to-talk-delay' 0
    Set-ReplayKitJsonValue $source 'hotkeys' (New-ReplayKitJsonObject)
    Set-ReplayKitJsonValue $source 'deinterlace_mode' 0
    Set-ReplayKitJsonValue $source 'deinterlace_field_order' 0
    Set-ReplayKitJsonValue $source 'monitoring_type' 0
    Set-ReplayKitJsonValue $source 'private_settings' (New-ReplayKitJsonObject)

    $sourceSettings = Get-ReplayKitJsonValue $source 'settings'
    if ($null -eq $sourceSettings) {
        $sourceSettings = New-ReplayKitJsonObject
        Set-ReplayKitJsonValue $source 'settings' $sourceSettings
    }
    if ($sourceSettings.ContainsKey('mode')) { [void]$sourceSettings.Remove('mode') }
    Set-ReplayKitJsonValue $sourceSettings 'Mode' 'standard'
    Set-ReplayKitJsonValue $sourceSettings 'width' 1280
    Set-ReplayKitJsonValue $sourceSettings 'height' 768
    Set-ReplayKitJsonValue $sourceSettings 'x' 0.0
    Set-ReplayKitJsonValue $sourceSettings 'y' 0.02
    Set-ReplayKitJsonValue $sourceSettings 'scale' 1.83
    Set-ReplayKitJsonValue $sourceSettings 'delay' 1.0
    Set-ReplayKitJsonValue $sourceSettings 'delaytime' 1.0
    Set-ReplayKitJsonValue $sourceSettings 'random_motion' $true
    Set-ReplayKitJsonValue $sourceSettings 'breath' $true
    Set-ReplayKitJsonValue $sourceSettings 'eyeblink' $true
    Set-ReplayKitJsonValue $sourceSettings 'track' $true
    Set-ReplayKitJsonValue $sourceSettings 'live2d' $true
    Set-ReplayKitJsonValue $sourceSettings 'relative_mouse' $true
    Set-ReplayKitJsonValue $sourceSettings 'mouse_horizontal_flip' $true
    Set-ReplayKitJsonValue $sourceSettings 'mouse_vertical_flip' $true
    Set-ReplayKitJsonValue $sourceSettings 'mask' $false
}

function New-ReplayKitBongoSourceJson([string]$uuid) {
    $source = New-ReplayKitJsonObject
    Set-ReplayKitBongoSourceJson $source $uuid
    return ,$source
}

function Set-ReplayKitWindowCaptureSourceJson($source, [string]$uuid, [hashtable]$settings) {
    Set-ReplayKitJsonValue $source 'prev_ver' 536936450
    Set-ReplayKitJsonValue $source 'name' 'Window Capture'
    Set-ReplayKitJsonValue $source 'uuid' $uuid
    Set-ReplayKitJsonValue $source 'id' 'window_capture'
    Set-ReplayKitJsonValue $source 'versioned_id' 'window_capture'
    Set-ReplayKitJsonValue $source 'mixers' 0
    Set-ReplayKitJsonValue $source 'sync' 0
    Set-ReplayKitJsonValue $source 'flags' 0
    Set-ReplayKitJsonValue $source 'volume' 1.0
    Set-ReplayKitJsonValue $source 'balance' 0.5
    Set-ReplayKitJsonValue $source 'enabled' $true
    Set-ReplayKitJsonValue $source 'muted' $false
    Set-ReplayKitJsonValue $source 'push-to-mute' $false
    Set-ReplayKitJsonValue $source 'push-to-mute-delay' 0
    Set-ReplayKitJsonValue $source 'push-to-talk' $false
    Set-ReplayKitJsonValue $source 'push-to-talk-delay' 0
    Set-ReplayKitJsonValue $source 'hotkeys' (New-ReplayKitJsonObject)
    Set-ReplayKitJsonValue $source 'deinterlace_mode' 0
    Set-ReplayKitJsonValue $source 'deinterlace_field_order' 0
    Set-ReplayKitJsonValue $source 'monitoring_type' 0
    Set-ReplayKitJsonValue $source 'private_settings' (New-ReplayKitJsonObject)

    $sourceSettings = New-ReplayKitJsonObject
    foreach ($entry in (Get-ReplayKitWindowCaptureInputSettings $settings).GetEnumerator()) {
        Set-ReplayKitJsonValue $sourceSettings ([string]$entry.Key) $entry.Value
    }
    Set-ReplayKitJsonValue $source 'settings' $sourceSettings
}

function New-ReplayKitWindowCaptureSourceJson([string]$uuid, [hashtable]$settings) {
    $source = New-ReplayKitJsonObject
    Set-ReplayKitWindowCaptureSourceJson $source $uuid $settings
    return ,$source
}

function Set-ReplayKitInputOverlaySourceJson($source, [string]$name, [string]$uuid, [string]$image, [string]$layout) {
    if ([string]::IsNullOrWhiteSpace($image) -or [string]::IsNullOrWhiteSpace($layout)) {
        throw "Input overlay assets are missing for $name."
    }
    Set-ReplayKitJsonValue $source 'prev_ver' 536936450
    Set-ReplayKitJsonValue $source 'name' $name
    Set-ReplayKitJsonValue $source 'uuid' $uuid
    Set-ReplayKitJsonValue $source 'id' 'input-overlay'
    Set-ReplayKitJsonValue $source 'versioned_id' 'input-overlay'
    Set-ReplayKitJsonValue $source 'mixers' 0
    Set-ReplayKitJsonValue $source 'sync' 0
    Set-ReplayKitJsonValue $source 'flags' 0
    Set-ReplayKitJsonValue $source 'volume' 1.0
    Set-ReplayKitJsonValue $source 'balance' 0.5
    Set-ReplayKitJsonValue $source 'enabled' $true
    Set-ReplayKitJsonValue $source 'muted' $false
    Set-ReplayKitJsonValue $source 'push-to-mute' $false
    Set-ReplayKitJsonValue $source 'push-to-mute-delay' 0
    Set-ReplayKitJsonValue $source 'push-to-talk' $false
    Set-ReplayKitJsonValue $source 'push-to-talk-delay' 0
    Set-ReplayKitJsonValue $source 'hotkeys' (New-ReplayKitJsonObject)
    Set-ReplayKitJsonValue $source 'deinterlace_mode' 0
    Set-ReplayKitJsonValue $source 'deinterlace_field_order' 0
    Set-ReplayKitJsonValue $source 'monitoring_type' 0
    Set-ReplayKitJsonValue $source 'private_settings' (New-ReplayKitJsonObject)

    $sourceSettings = New-ReplayKitJsonObject
    Set-ReplayKitJsonValue $sourceSettings 'io.input_source' 'This computer'
    Set-ReplayKitJsonValue $sourceSettings 'io.overlay_image' ([string]$image).Replace('\', '/')
    Set-ReplayKitJsonValue $sourceSettings 'io.layout_file' ([string]$layout).Replace('\', '/')
    Set-ReplayKitJsonValue $source 'settings' $sourceSettings
}

function New-ReplayKitInputOverlaySourceJson([string]$name, [string]$uuid, [string]$image, [string]$layout) {
    $source = New-ReplayKitJsonObject
    Set-ReplayKitInputOverlaySourceJson $source $name $uuid $image $layout
    return ,$source
}

function Set-ReplayKitSceneItemBaseJson($item, [string]$name, [bool]$visible) {
    Set-ReplayKitJsonValue $item 'name' $name
    Set-ReplayKitJsonValue $item 'visible' $visible
    Set-ReplayKitJsonValue $item 'locked' $false
    Set-ReplayKitJsonValue $item 'rot' 0.0
    Set-ReplayKitJsonValue $item 'align' 5
    Set-ReplayKitJsonValue $item 'bounds_type' 0
    Set-ReplayKitJsonValue $item 'bounds_align' 0
    Set-ReplayKitJsonValue $item 'bounds_crop' $false
    Set-ReplayKitJsonValue $item 'crop_left' 0
    Set-ReplayKitJsonValue $item 'crop_top' 0
    Set-ReplayKitJsonValue $item 'crop_right' 0
    Set-ReplayKitJsonValue $item 'crop_bottom' 0
    Set-ReplayKitJsonValue $item 'bounds' (New-ReplayKitJsonPoint 0.0 0.0)
    Set-ReplayKitJsonValue $item 'bounds_rel' (New-ReplayKitJsonPoint 0.0 0.0)
    Set-ReplayKitJsonValue $item 'scale_filter' 'disable'
    Set-ReplayKitJsonValue $item 'blend_method' 'default'
    Set-ReplayKitJsonValue $item 'blend_type' 'normal'
    Set-ReplayKitJsonValue $item 'private_settings' (New-ReplayKitJsonObject)
}

function Set-ReplayKitMainCaptureSceneItemJson($item, [string]$name, [hashtable]$preset) {
    $canvasW = [double]$preset.video.baseWidth
    $canvasH = [double]$preset.video.baseHeight
    if ($canvasW -le 0.0 -or $canvasH -le 0.0) {
        $canvasW = 1920.0
        $canvasH = 1080.0
    }
    $visible = [bool](Get-ReplayKitJsonValue $item 'visible' $true)
    $transform = Get-ReplayKitMainCaptureTransform $name $preset
    Set-ReplayKitSceneItemBaseJson $item $name $visible
    Set-ReplayKitJsonValue $item 'scale_ref' (New-ReplayKitJsonPoint ([double]$transform.sourceWidth) ([double]$transform.sourceHeight))
    Set-ReplayKitJsonValue $item 'pos' (New-ReplayKitJsonPoint ([double]$transform.positionX) ([double]$transform.positionY))
    Set-ReplayKitJsonValue $item 'pos_rel' (New-ReplayKitJsonPoint ((([double]$transform.positionX - $canvasW / 2.0) / ($canvasH / 2.0))) ((([double]$transform.positionY - $canvasH / 2.0) / ($canvasH / 2.0))))
    Set-ReplayKitJsonValue $item 'scale' (New-ReplayKitJsonPoint ([double]$transform.scaleX) ([double]$transform.scaleY))
    Set-ReplayKitJsonValue $item 'scale_rel' (New-ReplayKitJsonPoint 1.0 1.0)
    Set-ReplayKitJsonValue $item 'group_item_backup' $false
    if ([string]$transform.boundsType -eq 'OBS_BOUNDS_SCALE_INNER') {
        Set-ReplayKitJsonValue $item 'bounds_type' 2
        Set-ReplayKitJsonValue $item 'bounds' (New-ReplayKitJsonPoint $canvasW $canvasH)
        Set-ReplayKitJsonValue $item 'bounds_rel' (New-ReplayKitJsonPoint ((2.0 * $canvasW) / $canvasH) 2.0)
    }
}

function Set-ReplayKitWindowCaptureSceneItemJson($item, [string]$uuid, [hashtable]$preset, [bool]$visible) {
    Set-ReplayKitMainCaptureSceneItemJson $item 'Window Capture' $preset
    Set-ReplayKitJsonValue $item 'source_uuid' $uuid
    Set-ReplayKitJsonValue $item 'visible' $visible
}

function Set-ReplayKitBongoSceneItemJson($item, [string]$uuid, [hashtable]$preset, [bool]$visible, [hashtable]$settings = $null) {
    $canvasW = [double]$preset.video.baseWidth
    $canvasH = [double]$preset.video.baseHeight
    $transform = Get-ReplayKitBongoTransform $preset $settings
    Set-ReplayKitSceneItemBaseJson $item 'Bongo Cat Overlay' $visible
    Set-ReplayKitJsonValue $item 'source_uuid' $uuid
    Set-ReplayKitJsonValue $item 'scale_ref' (New-ReplayKitJsonPoint 1280.0 768.0)
    Set-ReplayKitJsonValue $item 'pos' (New-ReplayKitJsonPoint ([double]$transform.positionX) ([double]$transform.positionY))
    Set-ReplayKitJsonValue $item 'pos_rel' (New-ReplayKitJsonPoint ((([double]$transform.positionX - $canvasW / 2.0) / ($canvasH / 2.0))) ((([double]$transform.positionY - $canvasH / 2.0) / ($canvasH / 2.0))))
    Set-ReplayKitJsonValue $item 'scale' (New-ReplayKitJsonPoint ([double]$transform.scaleX) ([double]$transform.scaleY))
    Set-ReplayKitJsonValue $item 'scale_rel' (New-ReplayKitJsonPoint (([double]$transform.scaleX * 768.0 / $canvasH)) (([double]$transform.scaleY * 768.0 / $canvasH)))
    Set-ReplayKitJsonValue $item 'group_item_backup' $false
}

function New-ReplayKitJsonTransition([int]$duration) {
    $transition = New-ReplayKitJsonObject
    Set-ReplayKitJsonValue $transition 'duration' $duration
    return ,$transition
}

function Set-ReplayKitInputOverlaySceneItemJson($item, [string]$name, [hashtable]$preset, [bool]$visible, [string]$sourceUuid = '', [bool]$groupBackup = $false, [hashtable]$settings = $null) {
    $canvasW = [double]$preset.video.baseWidth
    $canvasH = [double]$preset.video.baseHeight
    $transform = Get-ReplayKitInputOverlayTransform $name $preset $settings
    $sourceW = if ($name -eq 'Mouse Overlay') { 285.0 } else { 568.0 }
    $sourceH = if ($name -eq 'Mouse Overlay') { 421.0 } else { 394.0 }
    Set-ReplayKitSceneItemBaseJson $item $name $visible
    if (-not [string]::IsNullOrWhiteSpace($sourceUuid)) {
        Set-ReplayKitJsonValue $item 'source_uuid' $sourceUuid
    }
    Set-ReplayKitJsonValue $item 'scale_ref' (New-ReplayKitJsonPoint $sourceW $sourceH)
    Set-ReplayKitJsonValue $item 'pos' (New-ReplayKitJsonPoint ([double]$transform.positionX) ([double]$transform.positionY))
    Set-ReplayKitJsonValue $item 'pos_rel' (New-ReplayKitJsonPoint ((([double]$transform.positionX - $canvasW / 2.0) / ($canvasH / 2.0))) ((([double]$transform.positionY - $canvasH / 2.0) / ($canvasH / 2.0))))
    Set-ReplayKitJsonValue $item 'scale' (New-ReplayKitJsonPoint ([double]$transform.scaleX) ([double]$transform.scaleY))
    Set-ReplayKitJsonValue $item 'scale_rel' (New-ReplayKitJsonPoint (([double]$transform.scaleX * 1440.0 / $canvasH)) (([double]$transform.scaleY * 1440.0 / $canvasH)))
    Set-ReplayKitJsonValue $item 'group_item_backup' $groupBackup
}

function Set-ReplayKitInputOverlayGroupMemberJson($item, [string]$name, [string]$sourceUuid, [hashtable]$preset, [int]$id) {
    $canvasW = 2560.0
    $canvasH = 1440.0
    if ($name -eq 'Mouse Overlay') {
        $x = 431.0
        $y = 0.0
        $scaleX = 0.6909722089767456
        $scaleY = 0.6909722089767456
        $scaleRelX = 0.6909722089767456
        $scaleRelY = 0.6909722089767456
    } else {
        $x = 0.0
        $y = 0.0
        $scaleX = 0.7395833134651184
        $scaleY = 0.7388888597488403
        $scaleRelX = 0.7395833134651184
        $scaleRelY = 0.7388888597488403
    }
    Set-ReplayKitSceneItemBaseJson $item $name $true
    Set-ReplayKitJsonValue $item 'source_uuid' $sourceUuid
    Set-ReplayKitJsonValue $item 'scale_ref' (New-ReplayKitJsonPoint $canvasW $canvasH)
    Set-ReplayKitJsonValue $item 'id' $id
    Set-ReplayKitJsonValue $item 'group_item_backup' $false
    Set-ReplayKitJsonValue $item 'pos' (New-ReplayKitJsonPoint $x $y)
    Set-ReplayKitJsonValue $item 'pos_rel' (New-ReplayKitJsonPoint (($x - $canvasW / 2.0) / ($canvasH / 2.0)) (($y - $canvasH / 2.0) / ($canvasH / 2.0)))
    Set-ReplayKitJsonValue $item 'scale' (New-ReplayKitJsonPoint $scaleX $scaleY)
    Set-ReplayKitJsonValue $item 'scale_rel' (New-ReplayKitJsonPoint $scaleRelX $scaleRelY)
    Set-ReplayKitJsonValue $item 'show_transition' (New-ReplayKitJsonTransition 300)
    Set-ReplayKitJsonValue $item 'hide_transition' (New-ReplayKitJsonTransition 300)
}

function Set-ReplayKitInputOverlayGroupSourceJson($group, [string]$groupUuid, [hashtable]$inputSources, [hashtable]$preset) {
    Set-ReplayKitJsonValue $group 'prev_ver' 536936450
    Set-ReplayKitJsonValue $group 'name' 'Group'
    Set-ReplayKitJsonValue $group 'uuid' $groupUuid
    Set-ReplayKitJsonValue $group 'id' 'group'
    Set-ReplayKitJsonValue $group 'versioned_id' 'group'

    $settings = New-ReplayKitJsonObject
    Set-ReplayKitJsonValue $settings 'id_counter' 0
    Set-ReplayKitJsonValue $settings 'custom_size' $true
    Set-ReplayKitJsonValue $settings 'cx' 628
    Set-ReplayKitJsonValue $settings 'cy' 292
    $items = [System.Collections.ArrayList]::new()
    foreach ($name in @('WASD Overlay', 'Mouse Overlay')) {
        if (-not $inputSources.ContainsKey($name)) { continue }
        $member = New-ReplayKitJsonObject
        Set-ReplayKitInputOverlayGroupMemberJson $member $name ([string]$inputSources[$name]['uuid']) $preset ([int]$inputSources[$name]['id'])
        [void]$items.Add($member)
    }
    Set-ReplayKitJsonValue $settings 'items' $items
    Set-ReplayKitJsonValue $group 'settings' $settings

    Set-ReplayKitJsonValue $group 'mixers' 0
    Set-ReplayKitJsonValue $group 'sync' 0
    Set-ReplayKitJsonValue $group 'flags' 0
    Set-ReplayKitJsonValue $group 'volume' 1.0
    Set-ReplayKitJsonValue $group 'balance' 0.5
    Set-ReplayKitJsonValue $group 'enabled' $true
    Set-ReplayKitJsonValue $group 'muted' $false
    Set-ReplayKitJsonValue $group 'push-to-mute' $false
    Set-ReplayKitJsonValue $group 'push-to-mute-delay' 0
    Set-ReplayKitJsonValue $group 'push-to-talk' $false
    Set-ReplayKitJsonValue $group 'push-to-talk-delay' 0
    $hotkeys = New-ReplayKitJsonObject
    foreach ($name in @('WASD Overlay', 'Mouse Overlay')) {
        if (-not $inputSources.ContainsKey($name)) { continue }
        $id = [int]$inputSources[$name]['id']
        Set-ReplayKitJsonValue $hotkeys "libobs.show_scene_item.$id" ([System.Collections.ArrayList]::new())
        Set-ReplayKitJsonValue $hotkeys "libobs.hide_scene_item.$id" ([System.Collections.ArrayList]::new())
    }
    Set-ReplayKitJsonValue $group 'hotkeys' $hotkeys
    Set-ReplayKitJsonValue $group 'deinterlace_mode' 0
    Set-ReplayKitJsonValue $group 'deinterlace_field_order' 0
    Set-ReplayKitJsonValue $group 'monitoring_type' 0
    Set-ReplayKitJsonValue $group 'canvas_uuid' '6c69626f-6273-4c00-9d88-c5136d61696e'
    Set-ReplayKitJsonValue $group 'private_settings' (New-ReplayKitJsonObject)
}

function Set-ReplayKitInputOverlayGroupSceneItemJson($item, [string]$groupUuid, [hashtable]$preset, [bool]$visible, [hashtable]$settings = $null) {
    $canvasW = [double]$preset.video.baseWidth
    $canvasH = [double]$preset.video.baseHeight
    $transform = Get-ReplayKitInputOverlayGroupTransform $preset $settings
    $ratio = [double]$transform.scaleX
    $x = [double]$transform.positionX
    $y = [double]$transform.positionY
    Set-ReplayKitSceneItemBaseJson $item 'Group' $visible
    Set-ReplayKitJsonValue $item 'source_uuid' $groupUuid
    Set-ReplayKitJsonValue $item 'scale_ref' (New-ReplayKitJsonPoint $canvasW $canvasH)
    Set-ReplayKitJsonValue $item 'group_item_backup' $false
    Set-ReplayKitJsonValue $item 'pos' (New-ReplayKitJsonPoint $x $y)
    Set-ReplayKitJsonValue $item 'pos_rel' (New-ReplayKitJsonPoint (($x - $canvasW / 2.0) / ($canvasH / 2.0)) (($y - $canvasH / 2.0) / ($canvasH / 2.0)))
    Set-ReplayKitJsonValue $item 'scale' (New-ReplayKitJsonPoint $ratio $ratio)
    $scaleRel = Get-ReplayKitOverlayScaleFactor $settings
    Set-ReplayKitJsonValue $item 'scale_rel' (New-ReplayKitJsonPoint $scaleRel $scaleRel)
    Set-ReplayKitJsonValue $item 'show_transition' (New-ReplayKitJsonTransition 0)
    Set-ReplayKitJsonValue $item 'hide_transition' (New-ReplayKitJsonTransition 0)
    $private = New-ReplayKitJsonObject
    Set-ReplayKitJsonValue $private 'collapsed' $false
    Set-ReplayKitJsonValue $item 'private_settings' $private
}

function Get-ReplayKitNextJsonSceneItemId($items) {
    $max = 0
    foreach ($item in @($items)) {
        try {
            $id = [int](Get-ReplayKitJsonValue $item 'id' 0)
            if ($id -gt $max) { $max = $id }
        } catch {}
    }
    return $max + 1
}

function Remove-ReplayKitJsonListWhere($list, [scriptblock]$predicate) {
    if ($null -eq $list) { return }
    for ($i = [int]$list.Count - 1; $i -ge 0; $i--) {
        if (& $predicate $list[$i]) {
            $list.RemoveAt($i)
        }
    }
}

function Move-ReplayKitJsonSceneItemAfter($items, [string]$targetName, [string]$afterName) {
    if ($null -eq $items) { return }
    $target = $null
    for ($i = 0; $i -lt $items.Count; $i++) {
        if ([string](Get-ReplayKitJsonValue $items[$i] 'name' '') -eq $targetName) {
            $target = $items[$i]
            break
        }
    }
    if ($null -eq $target) { return }
    [void]$items.Remove($target)
    $insertAt = 0
    for ($i = 0; $i -lt $items.Count; $i++) {
        if ([string](Get-ReplayKitJsonValue $items[$i] 'name' '') -eq $afterName) {
            $insertAt = $i + 1
            break
        }
    }
    $items.Insert($insertAt, $target)
}

function Get-ReplayKitMotionBlurFilterUuid([string]$sourceName) {
    switch ($sourceName) {
        'Display Capture' { return 'e371efc8-8c99-44cb-95e7-94381d9c9e41' }
        'Game Capture'    { return '26bc5a11-5315-4390-b028-f77667c7fda3' }
        'Window Capture'  { return '9b73d6cb-b65e-44a1-895f-4e2f326a8d77' }
        default           { return '' }
    }
}

function Get-ReplayKitObsInstallRoot {
    $obs = [string]$script:OBS_EXE
    if ([string]::IsNullOrWhiteSpace($obs) -or -not (Test-Path -LiteralPath $obs)) {
        $candidate = Resolve-ReplayKitObsExe
        if (Test-Path -LiteralPath $candidate) { $obs = $candidate }
    }
    try {
        return [System.IO.Directory]::GetParent([System.IO.Directory]::GetParent([System.IO.Directory]::GetParent($obs).FullName).FullName).FullName
    } catch {
        if ($env:ProgramFiles) { return (Join-Path $env:ProgramFiles 'obs-studio') }
        return 'C:\Program Files\obs-studio'
    }
}

function Get-ReplayKitShaderfilterMotionBlurPath {
    $path = Join-Path (Get-ReplayKitObsInstallRoot) 'data\obs-plugins\obs-shaderfilter\examples\motion_blur.shader'
    return $path.Replace('\', '/')
}

function Get-ReplayKitMotionBlurStrength([hashtable]$settings) {
    $value = [double]$settings.motionBlurStrength
    if ($value -lt 0.0) { return 0.0 }
    if ($value -gt 1.0) { return 1.0 }
    return $value
}

function Get-ReplayKitMotionBlurFilterSettingsJson([double]$strength) {
    $settings = New-ReplayKitJsonObject
    Set-ReplayKitJsonValue $settings 'from_file' $true
    Set-ReplayKitJsonValue $settings 'shader_file_name' (Get-ReplayKitShaderfilterMotionBlurPath)
    Set-ReplayKitJsonValue $settings 'override_entire_effect' $false
    Set-ReplayKitJsonValue $settings 'strength' $strength
    return ,$settings
}

function Get-ReplayKitMotionBlurFilterSettingsLive([double]$strength) {
    return @{
        from_file              = $true
        shader_file_name       = (Get-ReplayKitShaderfilterMotionBlurPath)
        override_entire_effect = $false
        strength               = $strength
    }
}

function Set-ReplayKitMotionBlurFilterJson($filter, [string]$sourceName, [bool]$enabled, [double]$strength) {
    $uuid = Get-ReplayKitMotionBlurFilterUuid $sourceName
    if ([string]::IsNullOrWhiteSpace($uuid)) { throw "Unsupported motion blur source: $sourceName" }
    Set-ReplayKitJsonValue $filter 'prev_ver' 536936450
    Set-ReplayKitJsonValue $filter 'name' 'ReplayKit Motion Blur'
    Set-ReplayKitJsonValue $filter 'uuid' $uuid
    Set-ReplayKitJsonValue $filter 'id' 'shader_filter'
    Set-ReplayKitJsonValue $filter 'versioned_id' 'shader_filter'
    Set-ReplayKitJsonValue $filter 'settings' (Get-ReplayKitMotionBlurFilterSettingsJson $strength)
    Set-ReplayKitJsonValue $filter 'mixers' 0
    Set-ReplayKitJsonValue $filter 'sync' 0
    Set-ReplayKitJsonValue $filter 'flags' 0
    Set-ReplayKitJsonValue $filter 'volume' 1.0
    Set-ReplayKitJsonValue $filter 'balance' 0.5
    Set-ReplayKitJsonValue $filter 'enabled' $enabled
    Set-ReplayKitJsonValue $filter 'muted' $false
    Set-ReplayKitJsonValue $filter 'push-to-mute' $false
    Set-ReplayKitJsonValue $filter 'push-to-mute-delay' 0
    Set-ReplayKitJsonValue $filter 'push-to-talk' $false
    Set-ReplayKitJsonValue $filter 'push-to-talk-delay' 0
    Set-ReplayKitJsonValue $filter 'hotkeys' (New-ReplayKitJsonObject)
    Set-ReplayKitJsonValue $filter 'deinterlace_mode' 0
    Set-ReplayKitJsonValue $filter 'deinterlace_field_order' 0
    Set-ReplayKitJsonValue $filter 'monitoring_type' 0
    Set-ReplayKitJsonValue $filter 'private_settings' (New-ReplayKitJsonObject)
}

function New-ReplayKitMotionBlurFilterJson([string]$sourceName, [bool]$enabled, [double]$strength) {
    $filter = New-ReplayKitJsonObject
    Set-ReplayKitMotionBlurFilterJson $filter $sourceName $enabled $strength
    return ,$filter
}

function Test-ReplayKitShaderfilterMotionBlurJson($filter) {
    if ([string](Get-ReplayKitJsonValue $filter 'id' '') -ne 'shader_filter') { return $false }
    $filterSettings = Get-ReplayKitJsonValue $filter 'settings'
    if ($null -eq $filterSettings) { return $false }
    $shaderFile = [string](Get-ReplayKitJsonValue $filterSettings 'shader_file_name' '')
    return $shaderFile.Replace('\', '/').ToLowerInvariant().EndsWith('/motion_blur.shader')
}

function Test-ReplayKitManagedMotionBlurJson($filter, [string]$uuid) {
    $name = [string](Get-ReplayKitJsonValue $filter 'name' '')
    $filterUuid = [string](Get-ReplayKitJsonValue $filter 'uuid' '')
    $filterId = [string](Get-ReplayKitJsonValue $filter 'id' '')
    return (
        $name -eq 'ReplayKit Motion Blur' -or
        $filterUuid -eq $uuid -or
        $filterId -eq 'obs_composite_blur' -or
        (Test-ReplayKitShaderfilterMotionBlurJson $filter)
    )
}

function Set-ReplayKitMotionBlurSourceJson($source, [bool]$enabled, [double]$strength) {
    $sourceName = [string](Get-ReplayKitJsonValue $source 'name' '')
    $uuid = Get-ReplayKitMotionBlurFilterUuid $sourceName
    if ([string]::IsNullOrWhiteSpace($uuid)) { return }

    $filters = Get-ReplayKitJsonValue $source 'filters'
    if ($null -eq $filters) {
        $filters = [System.Collections.ArrayList]::new()
    } else {
        $filters = ConvertTo-ReplayKitJsonList $filters
    }
    Set-ReplayKitJsonValue $source 'filters' $filters

    foreach ($filter in @($filters)) {
        if (Test-ReplayKitManagedMotionBlurJson $filter $uuid) {
            Set-ReplayKitMotionBlurFilterJson $filter $sourceName $enabled $strength
            return
        }
    }
    [void]$filters.Add((New-ReplayKitMotionBlurFilterJson $sourceName $enabled $strength))
}

function Remove-ReplayKitMotionBlurSourceJson($source) {
    $sourceName = [string](Get-ReplayKitJsonValue $source 'name' '')
    $uuid = Get-ReplayKitMotionBlurFilterUuid $sourceName
    if ([string]::IsNullOrWhiteSpace($uuid)) { return }

    $filters = Get-ReplayKitJsonValue $source 'filters'
    if ($null -eq $filters) { return }
    $filters = ConvertTo-ReplayKitJsonList $filters
    Remove-ReplayKitJsonListWhere $filters {
        param($filter)
        Test-ReplayKitManagedMotionBlurJson $filter $uuid
    }
    Set-ReplayKitJsonValue $source 'filters' $filters
}

function Get-ReplayKitOverlayOpacityFilterUuid([string]$sourceName) {
    switch ($sourceName) {
        'WASD Overlay'      { return 'a65fb4f0-a894-463e-9b9b-f0a9d5fb4fa1' }
        'Mouse Overlay'     { return 'c097fe72-641f-4da5-94f6-71f7c6353f9f' }
        'Bongo Cat Overlay' { return '4ecb70c4-e8f0-4207-a2cc-0307ff771722' }
        default             { return '' }
    }
}

function Get-ReplayKitOverlayOpacityFilterSettingsLive([int]$opacityPercent, [bool]$legacyPercent = $false, [hashtable]$settings = $null) {
    $hueShift = Get-ReplayKitOverlayHueShift $settings
    $multiply = Convert-ReplayKitHexColorToObsValue (Get-ReplayKitOverlayHexColor $settings 'overlayColorMultiply' '#ffffff') '#ffffff'
    $add = Convert-ReplayKitHexColorToObsValue (Get-ReplayKitOverlayHexColor $settings 'overlayColorAdd' '#000000') '#000000'
    if ($legacyPercent) {
        $opacity = [Math]::Max(0, [Math]::Min(100, $opacityPercent))
        return @{
            opacity = [int]$opacity
            hue_shift = $hueShift
            color = $multiply
        }
    }
    $opacity = [Math]::Max(0.0, [Math]::Min(1.0, ([double]$opacityPercent / 100.0)))
    return @{
        opacity = $opacity
        hue_shift = $hueShift
        color_multiply = $multiply
        color_add = $add
    }
}

function Test-ReplayKitOverlayOpacityFilterUsesLegacyPercent([string]$kind, $settings = $null) {
    if ($kind -eq 'color_filter_v2') { return $false }
    if ($kind -eq 'color_filter') { return $true }
    if ($null -ne $settings) {
        $versionedId = [string](Get-ReplayKitJsonValue $settings 'versioned_id' '')
        if ($versionedId -eq 'color_filter_v2') { return $false }
        $raw = Get-ReplayKitJsonValue $settings 'opacity' $null
        if ($null -ne $raw) {
            try {
                if ([double]$raw -gt 1.0) { return $true }
            } catch {}
        }
    }
    return $false
}

function Set-ReplayKitOverlayOpacityFilterJson($filter, [string]$sourceName, [int]$opacityPercent, [hashtable]$settings = $null) {
    $uuid = Get-ReplayKitOverlayOpacityFilterUuid $sourceName
    if ([string]::IsNullOrWhiteSpace($uuid)) { throw "Unsupported overlay opacity source: $sourceName" }
    Set-ReplayKitJsonValue $filter 'prev_ver' 536936450
    Set-ReplayKitJsonValue $filter 'name' 'ReplayKit Overlay Opacity'
    Set-ReplayKitJsonValue $filter 'uuid' $uuid
    Set-ReplayKitJsonValue $filter 'id' 'color_filter'
    Set-ReplayKitJsonValue $filter 'versioned_id' 'color_filter_v2'
    Set-ReplayKitJsonValue $filter 'settings' (Get-ReplayKitOverlayOpacityFilterSettingsLive $opacityPercent $false $settings)
    Set-ReplayKitJsonValue $filter 'mixers' 0
    Set-ReplayKitJsonValue $filter 'sync' 0
    Set-ReplayKitJsonValue $filter 'flags' 0
    Set-ReplayKitJsonValue $filter 'volume' 1.0
    Set-ReplayKitJsonValue $filter 'balance' 0.5
    Set-ReplayKitJsonValue $filter 'enabled' $true
    Set-ReplayKitJsonValue $filter 'muted' $false
    Set-ReplayKitJsonValue $filter 'push-to-mute' $false
    Set-ReplayKitJsonValue $filter 'push-to-mute-delay' 0
    Set-ReplayKitJsonValue $filter 'push-to-talk' $false
    Set-ReplayKitJsonValue $filter 'push-to-talk-delay' 0
    Set-ReplayKitJsonValue $filter 'hotkeys' (New-ReplayKitJsonObject)
    Set-ReplayKitJsonValue $filter 'deinterlace_mode' 0
    Set-ReplayKitJsonValue $filter 'deinterlace_field_order' 0
    Set-ReplayKitJsonValue $filter 'monitoring_type' 0
    Set-ReplayKitJsonValue $filter 'private_settings' (New-ReplayKitJsonObject)
}

function New-ReplayKitOverlayOpacityFilterJson([string]$sourceName, [int]$opacityPercent, [hashtable]$settings = $null) {
    $filter = New-ReplayKitJsonObject
    Set-ReplayKitOverlayOpacityFilterJson $filter $sourceName $opacityPercent $settings
    return ,$filter
}

function Test-ReplayKitManagedOverlayOpacityJson($filter, [string]$uuid) {
    $name = [string](Get-ReplayKitJsonValue $filter 'name' '')
    $filterUuid = [string](Get-ReplayKitJsonValue $filter 'uuid' '')
    return (
        $name -eq 'ReplayKit Overlay Opacity' -or
        (-not [string]::IsNullOrWhiteSpace($uuid) -and $filterUuid -eq $uuid)
    )
}

function Set-ReplayKitOverlayOpacitySourceJson($source, [hashtable]$settings) {
    $sourceName = [string](Get-ReplayKitJsonValue $source 'name' '')
    $uuid = Get-ReplayKitOverlayOpacityFilterUuid $sourceName
    if ([string]::IsNullOrWhiteSpace($uuid)) { return }
    $opacity = Get-ReplayKitOverlayOpacity $settings
    $hasColorAdjustments = Test-ReplayKitOverlayColorAdjusted $settings

    $filters = Get-ReplayKitJsonValue $source 'filters'
    if ($null -eq $filters) {
        $filters = [System.Collections.ArrayList]::new()
    } else {
        $filters = ConvertTo-ReplayKitJsonList $filters
    }
    $managedFilter = $null
    $keptFilters = [System.Collections.ArrayList]::new()
    foreach ($filter in @($filters)) {
        $name = [string](Get-ReplayKitJsonValue $filter 'name' '')
        $filterUuid = [string](Get-ReplayKitJsonValue $filter 'uuid' '')
        if ($name -ne 'ReplayKit Overlay Opacity' -and
            ([string]::IsNullOrWhiteSpace($uuid) -or $filterUuid -ne $uuid)) {
            [void]$keptFilters.Add($filter)
            continue
        }
        if ($null -eq $managedFilter) {
            $managedFilter = $filter
            [void]$keptFilters.Add($filter)
        }
    }
    $filters = $keptFilters
    if ($null -ne $managedFilter) {
        $filterSettings = Get-ReplayKitJsonValue $managedFilter 'settings' $null
        if ($null -eq $filterSettings) { $filterSettings = New-ReplayKitJsonObject }
        $id = [string](Get-ReplayKitJsonValue $managedFilter 'id' '')
        $versionedId = [string](Get-ReplayKitJsonValue $managedFilter 'versioned_id' '')
        $legacyPercent = ($id -eq 'color_filter' -and $versionedId -ne 'color_filter_v2')
        $opacitySettings = Get-ReplayKitOverlayOpacityFilterSettingsLive $opacity $legacyPercent $settings
        foreach ($key in $opacitySettings.Keys) {
            Set-ReplayKitJsonValue $filterSettings $key $opacitySettings[$key]
        }
        Set-ReplayKitJsonValue $managedFilter 'settings' $filterSettings
        Set-ReplayKitJsonValue $managedFilter 'enabled' $true
    } elseif ($opacity -lt 100 -or $hasColorAdjustments) {
        [void]$filters.Add((New-ReplayKitOverlayOpacityFilterJson $sourceName $opacity $settings))
    }
    if ($filters.Count -gt 0) {
        Set-ReplayKitJsonValue $source 'filters' $filters
    } elseif (Get-ReplayKitJsonValue $source 'filters' $null) {
        if ($source -is [System.Collections.IDictionary]) { [void]$source.Remove('filters') }
        else { $source.PSObject.Properties.Remove('filters') }
    }
}

function Set-ReplayKitDisplayCaptureCursorSourceJson($source) {
    $sourceName = [string](Get-ReplayKitJsonValue $source 'name' '')
    $sourceId = [string](Get-ReplayKitJsonValue $source 'id' '')
    if ($sourceName -ne 'Display Capture' -and $sourceId -ne 'monitor_capture') { return }
    $sourceSettings = Get-ReplayKitJsonValue $source 'settings' $null
    if ($null -eq $sourceSettings) {
        $sourceSettings = New-ReplayKitJsonObject
        Set-ReplayKitJsonValue $source 'settings' $sourceSettings
    }
    Set-ReplayKitJsonValue $sourceSettings 'capture_cursor' $true
}

function Set-ReplayKitDisplayCaptureCursorLive {
    $set = Invoke-ObsWebSocketRequest 'SetInputSettings' @{
        inputName      = 'Display Capture'
        inputSettings  = @{ capture_cursor = $true }
        overlay        = $true
    } 3000
    if ($set.ok) { return @{ ok = $true; message = '' } }
    return @{ ok = $false; message = $set.message }
}

function Ensure-ReplayKitWindowApiType {
    if ('ReplayKit.WindowApi' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ReplayKit {
    public static class WindowApi {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        public const int GWL_STYLE = -16;
        public const int GWL_EXSTYLE = -20;
        public const int WS_CHILD = 0x40000000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int DWMWA_CLOAKED = 14;
    }
}
'@ -ErrorAction Stop
}

function Get-ReplayKitWindowTitle([IntPtr]$hwnd) {
    $length = [ReplayKit.WindowApi]::GetWindowTextLength($hwnd)
    if ($length -le 0) { return '' }
    if ($length -gt 512) { $length = 512 }
    $builder = [System.Text.StringBuilder]::new($length + 1)
    [void][ReplayKit.WindowApi]::GetWindowText($hwnd, $builder, $builder.Capacity)
    return ([string]$builder.ToString()).Trim()
}

function Get-ReplayKitWindowClass([IntPtr]$hwnd) {
    $builder = [System.Text.StringBuilder]::new(256)
    [void][ReplayKit.WindowApi]::GetClassName($hwnd, $builder, $builder.Capacity)
    return ([string]$builder.ToString()).Trim()
}

function Test-ReplayKitWindowCloaked([IntPtr]$hwnd) {
    $cloaked = 0
    try {
        $hr = [ReplayKit.WindowApi]::DwmGetWindowAttribute($hwnd, [ReplayKit.WindowApi]::DWMWA_CLOAKED, [ref]$cloaked, 4)
        return ($hr -eq 0 -and $cloaked -ne 0)
    } catch {
        return $false
    }
}

function ConvertTo-ReplayKitObsWindowTokenPart([string]$value) {
    return ([string]$value).Replace('#', '#22').Replace(':', '#3A')
}

function ConvertFrom-ReplayKitObsWindowTokenPart([string]$value) {
    return ([string]$value).Replace('#3A', ':').Replace('#22', '#')
}

function New-ReplayKitObsWindowToken([string]$title, [string]$className, [string]$exeName) {
    return '{0}:{1}:{2}' -f
        (ConvertTo-ReplayKitObsWindowTokenPart $title),
        (ConvertTo-ReplayKitObsWindowTokenPart $className),
        (ConvertTo-ReplayKitObsWindowTokenPart $exeName)
}

function Get-ReplayKitWindowLabelFromToken([string]$token) {
    if ([string]::IsNullOrWhiteSpace($token)) { return 'Saved game window' }
    $parts = $token -split ':', 3
    if ($parts.Count -lt 3) { return 'Saved game window' }
    $title = ConvertFrom-ReplayKitObsWindowTokenPart $parts[0]
    $exe = ConvertFrom-ReplayKitObsWindowTokenPart $parts[2]
    if ([string]::IsNullOrWhiteSpace($title)) { return "[$exe]" }
    return "[$exe]: $title"
}

function Get-ReplayKitBlockedGameWindowExes {
    return @(
        'applicationframehost.exe', 'discord.exe', 'discordcanary.exe', 'discorddevelopment.exe',
        'discordptb.exe', 'discordsystemhelper.exe', 'explorer.exe', 'lockapp.exe',
        'obs.exe', 'obs32.exe', 'obs64.exe', 'searchapp.exe', 'searchhost.exe',
        'shellexperiencehost.exe', 'startmenuexperiencehost.exe', 'systemsettings.exe',
        'textinputhost.exe', 'time.exe', 'video.ui.exe'
    )
}

function Test-ReplayKitBlockedGameWindowExe([string]$exeName) {
    $exe = ([string]$exeName).Trim().ToLowerInvariant()
    if ([string]::IsNullOrWhiteSpace($exe)) { return $true }
    return (Get-ReplayKitBlockedGameWindowExes) -contains $exe
}

function ConvertFrom-ReplayKitObsWindowToken([string]$token) {
    $raw = ([string]$token).Trim()
    if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
    if ($raw.Length -gt 512 -or $raw -match '[\x00-\x1F]') {
        throw 'Invalid game window token.'
    }
    $parts = $raw -split ':', 3
    if ($parts.Count -lt 3) { throw 'Invalid game window token.' }

    $title = (ConvertFrom-ReplayKitObsWindowTokenPart $parts[0]).Trim()
    $className = (ConvertFrom-ReplayKitObsWindowTokenPart $parts[1]).Trim()
    $exe = (ConvertFrom-ReplayKitObsWindowTokenPart $parts[2]).Trim()
    if ($title.Length -gt 160 -or $className.Length -gt 120 -or $exe.Length -gt 96) {
        throw 'Invalid game window token.'
    }
    if (($title + $className + $exe) -match '[\x00-\x1F]') {
        throw 'Invalid game window token.'
    }
    if ($exe -notmatch '^[^\\/:*?"<>|]+\.exe$') {
        throw 'Invalid game window executable.'
    }
    if (Test-ReplayKitBlockedGameWindowExe $exe) {
        throw 'That application cannot be added to the Auto Game List.'
    }

    $cleanToken = New-ReplayKitObsWindowToken $title $className $exe
    return [pscustomobject]@{
        token     = $cleanToken
        value     = $cleanToken
        label     = Get-ReplayKitWindowLabelFromToken $cleanToken
        title     = $title
        className = $className
        exeName   = $exe
    }
}

function Get-ReplayKitGameWindowCandidates([string]$savedWindow = '') {
    try {
        Ensure-ReplayKitWindowApiType
    } catch {
        Write-Log "Game window enumeration unavailable: $($_.Exception.Message)"
        return @()
    }

    $shellWindow = [ReplayKit.WindowApi]::GetShellWindow()
    $seen = @{}
    $processCache = @{}
    $candidates = New-Object System.Collections.Generic.List[object]

    $callback = [ReplayKit.WindowApi+EnumWindowsProc]{
        param([IntPtr]$hwnd, [IntPtr]$lparam)
        if ($hwnd -eq $shellWindow) { return $true }
        if (-not [ReplayKit.WindowApi]::IsWindowVisible($hwnd)) { return $true }
        if (Test-ReplayKitWindowCloaked $hwnd) { return $true }

        $style = [ReplayKit.WindowApi]::GetWindowLong($hwnd, [ReplayKit.WindowApi]::GWL_STYLE)
        $exStyle = [ReplayKit.WindowApi]::GetWindowLong($hwnd, [ReplayKit.WindowApi]::GWL_EXSTYLE)
        if (($style -band [ReplayKit.WindowApi]::WS_CHILD) -ne 0) { return $true }
        if (($exStyle -band [ReplayKit.WindowApi]::WS_EX_TOOLWINDOW) -ne 0) { return $true }

        $title = Get-ReplayKitWindowTitle $hwnd
        if ([string]::IsNullOrWhiteSpace($title)) { return $true }
        $className = Get-ReplayKitWindowClass $hwnd
        if ([string]::IsNullOrWhiteSpace($className)) { return $true }

        $processIdRef = [uint32]0
        [void][ReplayKit.WindowApi]::GetWindowThreadProcessId($hwnd, [ref]$processIdRef)
        $processId = [int]$processIdRef
        if ($processId -le 0) { return $true }
        if (-not $processCache.ContainsKey($processId)) {
            try {
                $processName = [string](Get-Process -Id $processId -ErrorAction Stop).ProcessName
                if (-not $processName.EndsWith('.exe', [System.StringComparison]::OrdinalIgnoreCase)) {
                    $processName = "$processName.exe"
                }
                $processCache[$processId] = $processName
            } catch {
                $processCache[$processId] = ''
            }
        }
        $exe = [string]$processCache[$processId]
        if ([string]::IsNullOrWhiteSpace($exe)) { return $true }
        if (Test-ReplayKitBlockedGameWindowExe $exe) { return $true }

        if ($title.Length -gt 160) { $title = $title.Substring(0, 160) }
        if ($className.Length -gt 120) { $className = $className.Substring(0, 120) }
        if ($exe.Length -gt 96) { $exe = $exe.Substring(0, 96) }
        $token = New-ReplayKitObsWindowToken $title $className $exe
        if ($seen.ContainsKey($token)) { return $true }
        $seen[$token] = $true

        [void]$candidates.Add([pscustomobject]@{
            value     = $token
            token     = $token
            label     = "[$exe]: $title"
            blurb     = $className
            title     = $title
            className = $className
            exeName   = $exe
        })
        return $candidates.Count -lt 80
    }

    [void][ReplayKit.WindowApi]::EnumWindows($callback, [IntPtr]::Zero)
    $items = @($candidates | Sort-Object -Property label)
    if (-not [string]::IsNullOrWhiteSpace($savedWindow)) {
        $hasSaved = $false
        foreach ($item in $items) {
            if ([string]$item.value -eq $savedWindow) {
                $hasSaved = $true
                break
            }
        }
        if (-not $hasSaved) {
            try {
                $saved = ConvertFrom-ReplayKitObsWindowToken $savedWindow
                $items = @([pscustomobject]@{
                    value     = $saved.token
                    token     = $saved.token
                    label     = $saved.label
                    blurb     = 'Saved selection'
                    title     = $saved.title
                    className = $saved.className
                    exeName   = $saved.exeName
                }) + $items
            } catch {
                $items = @([pscustomobject]@{
                    value = $savedWindow
                    label = Get-ReplayKitWindowLabelFromToken $savedWindow
                    blurb = 'Saved selection'
                }) + $items
            }
        }
    }
    return $items
}

function Get-ReplayKitGameCaptureInputSettings([hashtable]$settings) {
    $inputSettings = @{
        capture_audio = $false
        hook_rate = 2
        limit_framerate = $true
        capture_cursor = $true
        capture_overlays = $false
        anti_cheat_hook = $true
    }
    $inputSettings.capture_mode = 'any_fullscreen'
    $inputSettings.window = ''
    return $inputSettings
}

function Get-ReplayKitWindowCaptureInputSettings([hashtable]$settings) {
    $window = ''
    if ($null -ne $settings -and $settings.ContainsKey('screenshareGameWindow')) {
        $window = [string]$settings.screenshareGameWindow
    }
    return @{
        window = $window
        method = 2
        priority = 0
        cursor = $true
        client_area = $true
        compatibility = $false
        force_sdr = $false
        capture_audio = $false
    }
}

function Apply-ReplayKitScreenshareCaptureLive([hashtable]$settings, [hashtable]$preset = $null) {
    $warnings = @()
    $applied = @()
    $mode = [string]$settings.screenshareCaptureMode
    $useDesktop = $mode -eq 'desktop' -or $mode -eq 'hybrid_auto'
    $useGameCapture = $mode -eq 'game_auto'
    $useWindowCapture = $mode -eq 'game_window'

    $scene = Get-ReplayKitSceneName
    if (-not $scene.ok) {
        return @{ ok = $false; applied = $applied; warnings = @("Screenshare capture was saved, but OBS scene lookup failed: $($scene.message)") }
    }
    $sceneName = [string]$scene.name
    $itemsResult = Get-ReplayKitSceneItems $sceneName
    if (-not $itemsResult.ok) {
        return @{ ok = $false; applied = $applied; warnings = @("Screenshare capture was saved, but OBS scene items could not be read: $($itemsResult.message)") }
    }
    $items = $itemsResult.items

    # display capture and game capture used to be looked up only (assuming the bundled scene always has them) -- if either was ever missing (user deleted it by hand, a scene got recreated bare, etc.) this silently left screensharing broken with no way to recover short of a manual re-add. now created if missing, same treatment window capture already gets below. falls back to the plain lookup against the items already fetched above if the ensure call itself fails, matching the old behavior for that edge case.
    $gameInputSettings = Get-ReplayKitGameCaptureInputSettings $settings
    $displayEnsure = Ensure-ReplayKitInputSceneItem $sceneName 'Display Capture' 'monitor_capture' @{} $useDesktop
    if (-not $displayEnsure.ok) { $warnings += "Could not prepare Display Capture: $($displayEnsure.message)" }
    $display = if ($displayEnsure.ok) { $displayEnsure.item } else { Find-ReplayKitSceneItem $items 'Display Capture' }
    $gameEnsure = Ensure-ReplayKitInputSceneItem $sceneName 'Game Capture' 'game_capture' $gameInputSettings $useGameCapture
    if (-not $gameEnsure.ok) { $warnings += "Could not prepare Game Capture: $($gameEnsure.message)" }
    $game = if ($gameEnsure.ok) { $gameEnsure.item } else { Find-ReplayKitSceneItem $items 'Game Capture' }

    $windowSettings = Get-ReplayKitWindowCaptureInputSettings $settings
    $windowEnsure = Ensure-ReplayKitInputSceneItem $sceneName 'Window Capture' 'window_capture' $windowSettings $useWindowCapture
    if (-not $windowEnsure.ok) {
        $warnings += "Could not prepare Window Capture: $($windowEnsure.message)"
    }
    $itemsResult = Get-ReplayKitSceneItems $sceneName
    if ($itemsResult.ok) {
        $items = $itemsResult.items
    }
    $window = Find-ReplayKitSceneItem $items 'Window Capture'

    if (-not $useDesktop) {
        $hideDesktop = Set-ReplayKitSceneItemEnabled $sceneName $display $false
        if (-not $hideDesktop.ok) { $warnings += "Could not hide Display Capture: $($hideDesktop.message)" }
    }

    $cursorResult = Set-ReplayKitDisplayCaptureCursorLive
    if (-not $cursorResult.ok) { $warnings += "Could not enable Display Capture cursor: $($cursorResult.message)" }

    $setGame = Invoke-ObsWebSocketRequest 'SetInputSettings' @{
        inputName = 'Game Capture'
        inputSettings = $gameInputSettings
        overlay = $true
    } 3000
    if (-not $setGame.ok) {
        $warnings += "Could not update Game Capture settings: $($setGame.message)"
    }

    $setWindow = Invoke-ObsWebSocketRequest 'SetInputSettings' @{
        inputName = 'Window Capture'
        inputSettings = $windowSettings
        overlay = $true
    } 3000
    if (-not $setWindow.ok) {
        $warnings += "Could not update Window Capture settings: $($setWindow.message)"
    }

    if ($useWindowCapture -and [string]::IsNullOrWhiteSpace([string]$settings.screenshareGameWindow)) {
        $warnings += 'Specific game capture was selected without a game window.'
    }

    $enableGame = Set-ReplayKitSceneItemEnabled $sceneName $game $useGameCapture
    if (-not $enableGame.ok) { $warnings += "Could not toggle Game Capture: $($enableGame.message)" }
    $enableWindow = Set-ReplayKitSceneItemEnabled $sceneName $window $useWindowCapture
    if (-not $enableWindow.ok) { $warnings += "Could not toggle Window Capture: $($enableWindow.message)" }
    if ($useDesktop) {
        $enableDisplay = Set-ReplayKitSceneItemEnabled $sceneName $display $true
        if (-not $enableDisplay.ok) { $warnings += "Could not show Display Capture: $($enableDisplay.message)" }
    }

    $order = Set-ReplayKitWindowCaptureSceneOrder $sceneName $items
    if (-not $order.ok) { $warnings += "Could not position Window Capture under overlays: $($order.message)" }

    if ($null -ne $preset) {
        foreach ($captureName in @('Display Capture', 'Window Capture', 'Game Capture')) {
            $capture = Find-ReplayKitSceneItem $items $captureName
            $captureTransform = Get-ReplayKitMainCaptureTransform $captureName $preset
            $captureResult = Set-ReplayKitSceneItemTransform $sceneName $capture $captureTransform
            if (-not $captureResult.ok) {
                $warnings += "Could not fit ${captureName} to canvas: $($captureResult.message)"
            }
        }
    }

    if ($useDesktop) { $applied += 'desktop capture source' }
    elseif ($useWindowCapture) { $applied += 'window capture source' }
    else { $applied += 'game capture source' }
    return @{ ok = ($warnings.Count -eq 0); applied = $applied; warnings = $warnings }
}

function Get-ReplayKitOverlayOpacityLiveFilterName($filters) {
    $match = Get-ReplayKitOverlayOpacityLiveFilterInfo $filters
    if ($match.found) { return [string]$match.name }
    return ''
}

function Get-ReplayKitOverlayOpacityLiveFilterInfo($filters) {
    foreach ($filter in @($filters)) {
        $name = [string](Get-ReplayKitJsonValue $filter 'filterName' '')
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        $kind = [string](Get-ReplayKitJsonValue $filter 'filterKind' '')
        $settings = Get-ReplayKitJsonValue $filter 'filterSettings' @{}
        $enabled = Get-ReplayKitJsonValue $filter 'filterEnabled' $true
        if ($name -eq 'ReplayKit Overlay Opacity') {
            return @{ found = $true; name = $name; kind = $kind; settings = $settings; enabled = [bool]$enabled; managed = $true }
        }
    }
    return @{ found = $false; name = ''; kind = ''; settings = @{}; enabled = $false; managed = $false }
}

function Get-ReplayKitOverlayOpacityPercentFromFilterSettings($filterSettings) {
    $value = Get-ReplayKitJsonValue $filterSettings 'opacity' $null
    if ($null -eq $value) { return $null }
    try {
        $opacity = [double]$value
        if ($opacity -gt 1.0) {
            if ($opacity -gt 100.0) { $opacity = 100.0 }
            return [int][Math]::Round($opacity)
        }
        if ($opacity -lt 0.0) { $opacity = 0.0 }
        return [int][Math]::Round($opacity * 100.0)
    } catch {
        return $null
    }
}

function Get-ReplayKitSourceFilterListLive([string]$sourceName) {
    $list = Invoke-ObsWebSocketRequest 'GetSourceFilterList' @{ sourceName = $sourceName } 3000
    if (-not $list.ok) { return @{ ok = $false; message = $list.message; filters = @() } }
    $filters = Get-ReplayKitJsonValue $list.data 'filters' $null
    if ($null -eq $filters) {
        $filters = Get-ReplayKitJsonValue $list.data 'sourceFilters' @()
    }
    return @{ ok = $true; message = ''; filters = @($filters) }
}

function Test-ReplayKitSourceExistsForFilters([string]$sourceName) {
    $filters = Get-ReplayKitSourceFilterListLive $sourceName
    return [bool]$filters.ok
}

function Get-ReplayKitLiveOverlayOpacityPercent([hashtable]$settings) {
    foreach ($name in (Get-ReplayKitOrderedOverlayCandidateSourceNames ([string]$settings.overlayStyle))) {
        $filters = Get-ReplayKitSourceFilterListLive $name
        if (-not $filters.ok) { continue }
        $match = Get-ReplayKitOverlayOpacityLiveFilterInfo $filters.filters
        if (-not $match.found) { continue }
        $opacity = Get-ReplayKitOverlayOpacityPercentFromFilterSettings $match.settings
        if ($null -ne $opacity) { return @{ ok = $true; opacity = [int]$opacity; sourceName = $name; filterName = [string]$match.name } }
    }
    return @{ ok = $false; opacity = $null; sourceName = ''; filterName = '' }
}

function Get-ReplayKitLatestSceneCollectionPath {
    $root = Join-Path $env:APPDATA 'obs-studio\basic\scenes'
    if (-not (Test-Path -LiteralPath $root)) { return '' }
    $file = Get-ChildItem -LiteralPath $root -Filter '*.json' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($file) { return [string]$file.FullName }
    return ''
}

function Get-ReplayKitSceneFileOverlayOpacityPercent([hashtable]$settings) {
    $path = Get-ReplayKitLatestSceneCollectionPath
    if ([string]::IsNullOrWhiteSpace($path) -or -not (Test-Path -LiteralPath $path)) {
        return @{ ok = $false; opacity = $null; sourceName = ''; filterName = '' }
    }
    try {
        $json = [System.IO.File]::ReadAllText($path)
        if ([string]::IsNullOrWhiteSpace($json)) { return @{ ok = $false; opacity = $null; sourceName = ''; filterName = '' } }
        $data = ConvertFrom-Json $json
        $sources = @(Get-ReplayKitJsonValue $data 'sources' @())
        foreach ($name in (Get-ReplayKitOverlayCandidateSourceNames ([string]$settings.overlayStyle))) {
            foreach ($source in $sources) {
                if ([string](Get-ReplayKitJsonValue $source 'name' '') -ne $name) { continue }
                $filters = @(Get-ReplayKitJsonValue $source 'filters' @())
                foreach ($filter in $filters) {
                    if (-not (Test-ReplayKitManagedOverlayOpacityJson $filter '')) { continue }
                    $filterSettings = Get-ReplayKitJsonValue $filter 'settings' @{}
                    $opacity = Get-ReplayKitOverlayOpacityPercentFromFilterSettings $filterSettings
                    if ($null -ne $opacity) {
                        return @{ ok = $true; opacity = [int]$opacity; sourceName = $name; filterName = [string](Get-ReplayKitJsonValue $filter 'name' '') }
                    }
                }
            }
        }
    } catch {
        Write-Log "Get-ReplayKitSceneFileOverlayOpacityPercent failed: $($_.Exception.Message)"
    }
    return @{ ok = $false; opacity = $null; sourceName = ''; filterName = '' }
}

function Apply-ReplayKitOverlayOpacityLive([string]$sourceName, [int]$opacityPercent, [hashtable]$settings = $null) {
    $list = Get-ReplayKitSourceFilterListLive $sourceName
    if (-not $list.ok) { return @{ ok = $false; message = $list.message } }
    $existingFilter = Get-ReplayKitOverlayOpacityLiveFilterInfo $list.filters
    $existingFilterName = if ($existingFilter.found) { [string]$existingFilter.name } else { '' }
    $hasColorAdjustments = Test-ReplayKitOverlayColorAdjusted $settings
    $legacyPercent = $false
    if ($existingFilter.found) {
        $legacyPercent = Test-ReplayKitOverlayOpacityFilterUsesLegacyPercent ([string]$existingFilter.kind) $existingFilter.settings
    }

    if ($opacityPercent -ge 100 -and (-not $hasColorAdjustments)) {
        if ([string]::IsNullOrWhiteSpace($existingFilterName)) {
            return @{ ok = $true; message = '' }
        }
        $setFull = Invoke-ObsWebSocketRequest 'SetSourceFilterSettings' @{
            sourceName     = $sourceName
            filterName     = $existingFilterName
            filterSettings = (Get-ReplayKitOverlayOpacityFilterSettingsLive 100 $legacyPercent $settings)
            overlay        = $true
        } 3000
        if (-not $setFull.ok) { return @{ ok = $false; message = $setFull.message } }
        $enableFull = Invoke-ObsWebSocketRequest 'SetSourceFilterEnabled' @{
            sourceName    = $sourceName
            filterName    = $existingFilterName
            filterEnabled = $true
        } 3000
        if ($enableFull.ok) { return @{ ok = $true; message = '' } }
        return @{ ok = $false; message = $enableFull.message }
    }

    $filterSettings = Get-ReplayKitOverlayOpacityFilterSettingsLive $opacityPercent $legacyPercent $settings
    if (-not [string]::IsNullOrWhiteSpace($existingFilterName)) {
        $set = Invoke-ObsWebSocketRequest 'SetSourceFilterSettings' @{
            sourceName     = $sourceName
            filterName     = $existingFilterName
            filterSettings = $filterSettings
            overlay        = $true
        } 3000
        if (-not $set.ok) { return @{ ok = $false; message = $set.message } }
    } else {
        $create = Invoke-ObsWebSocketRequest 'CreateSourceFilter' @{
            sourceName     = $sourceName
            filterName     = 'ReplayKit Overlay Opacity'
            filterKind     = 'color_filter_v2'
            filterSettings = $filterSettings
        } 3000
        if (-not $create.ok) {
            $createFallback = Invoke-ObsWebSocketRequest 'CreateSourceFilter' @{
                sourceName     = $sourceName
                filterName     = 'ReplayKit Overlay Opacity'
                filterKind     = 'color_filter'
                filterSettings = (Get-ReplayKitOverlayOpacityFilterSettingsLive $opacityPercent $true $settings)
            } 3000
            if (-not $createFallback.ok) { return @{ ok = $false; message = $create.message } }
            $legacyPercent = $true
        }
        $existingFilterName = 'ReplayKit Overlay Opacity'
        $createdList = Get-ReplayKitSourceFilterListLive $sourceName
        if ($createdList.ok) {
            $createdFilter = Get-ReplayKitOverlayOpacityLiveFilterInfo $createdList.filters
            if ($createdFilter.found) {
                $legacyPercent = Test-ReplayKitOverlayOpacityFilterUsesLegacyPercent ([string]$createdFilter.kind) $createdFilter.settings
                if ($legacyPercent) {
                    $setCreated = Invoke-ObsWebSocketRequest 'SetSourceFilterSettings' @{
                        sourceName     = $sourceName
                        filterName     = $existingFilterName
                        filterSettings = (Get-ReplayKitOverlayOpacityFilterSettingsLive $opacityPercent $true $settings)
                        overlay        = $true
                    } 3000
                    if (-not $setCreated.ok) { return @{ ok = $false; message = $setCreated.message } }
                }
            }
        }
    }
    $enable = Invoke-ObsWebSocketRequest 'SetSourceFilterEnabled' @{
        sourceName    = $sourceName
        filterName    = $existingFilterName
        filterEnabled = $true
    } 3000
    if ($enable.ok) { return @{ ok = $true; message = '' } }
    return @{ ok = $false; message = $enable.message }
}

function Test-ReplayKitShaderfilterPluginInstalled {
    $root = Get-ReplayKitObsInstallRoot
    $dll = Join-Path $root 'obs-plugins\64bit\obs-shaderfilter.dll'
    $shader = Join-Path $root 'data\obs-plugins\obs-shaderfilter\examples\motion_blur.shader'
    return (Test-Path -LiteralPath $dll) -and (Test-Path -LiteralPath $shader)
}

function Get-ReplayKitMotionBlurLiveFilterName($filters) {
    foreach ($filter in @($filters)) {
        $name = [string](Get-ReplayKitJsonValue $filter 'filterName' '')
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        if ($name -eq 'ReplayKit Motion Blur') { return $name }
        $kind = [string](Get-ReplayKitJsonValue $filter 'filterKind' '')
        if ($kind -eq 'shader_filter') {
            $filterSettings = Get-ReplayKitJsonValue $filter 'filterSettings'
            $shaderFile = [string](Get-ReplayKitJsonValue $filterSettings 'shader_file_name' '')
            if ($shaderFile.Replace('\', '/').ToLowerInvariant().EndsWith('/motion_blur.shader')) {
                return $name
            }
        }
    }
    return ''
}

function Get-ReplayKitRetiredMotionBlurLiveFilterNames($filters) {
    $names = @()
    foreach ($filter in @($filters)) {
        $name = [string](Get-ReplayKitJsonValue $filter 'filterName' '')
        $kind = [string](Get-ReplayKitJsonValue $filter 'filterKind' '')
        if ($name -and $kind -eq 'obs_composite_blur') { $names += $name }
    }
    return $names
}

function Apply-ReplayKitMotionBlurLive([hashtable]$settings) {
    $warnings = @()
    $applied = @()
    $enabled = [bool]$settings.motionBlurEnabled
    $strength = Get-ReplayKitMotionBlurStrength $settings
    $filterName = 'ReplayKit Motion Blur'
    $sourceNames = @('Display Capture', 'Game Capture', 'Window Capture')

    if ($enabled -and -not (Test-ReplayKitShaderfilterPluginInstalled)) {
        $warnings += 'Motion blur was saved, but OBS Shaderfilter is not installed. Re-run ReplayKit setup, then restart OBS after the plugin install.'
        return @{ applied = $applied; warnings = $warnings }
    }

    foreach ($sourceName in $sourceNames) {
        $list = Invoke-ObsWebSocketRequest 'GetSourceFilterList' @{ sourceName = $sourceName } 3000
        if (-not $list.ok) {
            $warnings += "Motion blur was saved, but ${sourceName} filters could not be read: $($list.message)"
            continue
        }
        $filters = Get-ReplayKitJsonValue $list.data 'filters' $null
        if ($null -eq $filters) {
            $filters = Get-ReplayKitJsonValue $list.data 'sourceFilters' @()
        }
        foreach ($retiredName in @(Get-ReplayKitRetiredMotionBlurLiveFilterNames $filters)) {
            $removeRetired = Invoke-ObsWebSocketRequest 'RemoveSourceFilter' @{
                sourceName = $sourceName
                filterName = $retiredName
            } 3000
            if (-not $removeRetired.ok) {
                $warnings += "Could not remove retired ${sourceName} Composite Blur filter: $($removeRetired.message)"
            }
        }
        $existingFilterName = Get-ReplayKitMotionBlurLiveFilterName $filters

        if ($enabled) {
            if (-not [string]::IsNullOrWhiteSpace($existingFilterName)) {
                $set = Invoke-ObsWebSocketRequest 'SetSourceFilterSettings' @{
                    sourceName     = $sourceName
                    filterName     = $existingFilterName
                    filterSettings = (Get-ReplayKitMotionBlurFilterSettingsLive $strength)
                    overlay        = $false
                } 3000
                if (-not $set.ok) {
                    $warnings += "Could not update ${sourceName} motion blur settings: $($set.message)"
                    continue
                }
            } else {
                $create = Invoke-ObsWebSocketRequest 'CreateSourceFilter' @{
                    sourceName     = $sourceName
                    filterName     = $filterName
                    filterKind     = 'shader_filter'
                    filterSettings = (Get-ReplayKitMotionBlurFilterSettingsLive $strength)
                } 3000
                if (-not $create.ok) {
                    $warnings += "Could not create ${sourceName} motion blur filter: $($create.message)"
                    continue
                }
                $existingFilterName = $filterName
            }
            $enable = Invoke-ObsWebSocketRequest 'SetSourceFilterEnabled' @{
                sourceName    = $sourceName
                filterName    = $existingFilterName
                filterEnabled = $true
            } 3000
            if ($enable.ok) { $applied += "${sourceName} motion blur on" }
            else { $warnings += "Could not enable ${sourceName} motion blur: $($enable.message)" }
        } elseif (-not [string]::IsNullOrWhiteSpace($existingFilterName)) {
            $remove = Invoke-ObsWebSocketRequest 'RemoveSourceFilter' @{
                sourceName = $sourceName
                filterName = $existingFilterName
            } 3000
            if ($remove.ok) { $applied += "${sourceName} motion blur off" }
            else { $warnings += "Could not remove ${sourceName} motion blur filter: $($remove.message)" }
        }
    }

    return @{ applied = $applied; warnings = $warnings }
}

function Set-ReplayKitOverlaySceneFile([hashtable]$settings, [hashtable]$preset) {
    try {
        $path = Get-ReplayKitSceneCollectionPath
        if (-not (Test-Path -LiteralPath $path)) {
            return @{ ok = $false; message = 'OBS scene collection file was not found.' }
        }
        Add-Type -AssemblyName System.Web.Extensions -ErrorAction Stop
        $serializer = [System.Web.Script.Serialization.JavaScriptSerializer]::new()
        $serializer.MaxJsonLength = 64 * 1024 * 1024
        $data = $serializer.DeserializeObject([System.IO.File]::ReadAllText($path))
        $sources = Get-ReplayKitJsonValue $data 'sources'
        if ($null -eq $sources) { throw 'OBS scene collection has no sources list.' }
        $sources = ConvertTo-ReplayKitJsonList $sources
        Set-ReplayKitJsonValue $data 'sources' $sources
        $groups = Get-ReplayKitJsonValue $data 'groups'
        if ($null -eq $groups) {
            $groups = [System.Collections.ArrayList]::new()
        } else {
            $groups = ConvertTo-ReplayKitJsonList $groups
        }
        Set-ReplayKitJsonValue $data 'groups' $groups

        $overlayStyle = [string]$settings.overlayStyle
        $useInputOverlay = $overlayStyle -eq 'input_overlay'
        $useBongo = $overlayStyle -eq 'bongo_cat'
        $useMotionBlur = [bool]$settings.motionBlurEnabled
        $motionBlurStrength = Get-ReplayKitMotionBlurStrength $settings
        $inputSources = @{}
        $inputSourceNames = @('WASD Overlay', 'Mouse Overlay')
        $inputGroup = $null
        foreach ($group in @($groups)) {
            if ([string](Get-ReplayKitJsonValue $group 'name' '') -eq 'Group') {
                $inputGroup = $group
                break
            }
        }
        $inputGroupUuid = if ($null -ne $inputGroup) { [string](Get-ReplayKitJsonValue $inputGroup 'uuid' '') } else { '' }
        if ([string]::IsNullOrWhiteSpace($inputGroupUuid)) {
            $inputGroupUuid = [guid]::NewGuid().ToString()
        }

        $existingInputUuids = @{}
        foreach ($source in @($sources)) {
            $name = [string](Get-ReplayKitJsonValue $source 'name' '')
            if ($inputSourceNames -contains $name) {
                $uuid = [string](Get-ReplayKitJsonValue $source 'uuid' '')
                if (-not [string]::IsNullOrWhiteSpace($uuid)) { $existingInputUuids[$uuid] = $true }
            }
        }

        $bongoSource = $null
        foreach ($source in @($sources)) {
            if ([string](Get-ReplayKitJsonValue $source 'name' '') -eq 'Bongo Cat Overlay') {
                $bongoSource = $source
                break
            }
        }
        if ($null -eq $bongoSource) {
            foreach ($source in @($sources)) {
                if ([string](Get-ReplayKitJsonValue $source 'id' '') -eq 'bongobs-cat') {
                    $bongoSource = $source
                    break
                }
            }
        }
        $bongoUuid = if ($null -ne $bongoSource) { [string](Get-ReplayKitJsonValue $bongoSource 'uuid' '') } else { '' }
        $windowCaptureSource = $null
        foreach ($source in @($sources)) {
            $name = [string](Get-ReplayKitJsonValue $source 'name' '')
            $uuid = [string](Get-ReplayKitJsonValue $source 'uuid' '')
            if ($name -eq 'Window Capture' -or $uuid -eq 'edb2d9d4-7b53-4f3a-a760-61cd03ce9b6c') {
                $windowCaptureSource = $source
                break
            }
        }
        $windowCaptureUuid = if ($null -ne $windowCaptureSource) { [string](Get-ReplayKitJsonValue $windowCaptureSource 'uuid' '') } else { '' }
        if ([string]::IsNullOrWhiteSpace($windowCaptureUuid)) {
            $windowCaptureUuid = 'edb2d9d4-7b53-4f3a-a760-61cd03ce9b6c'
        }

        if ($useInputOverlay) {
            $inputSpecs = @(
                @{ name = 'WASD Overlay'; image = Find-ReplayKitOverlayAsset 'wasd\wasd.png'; layout = Find-ReplayKitOverlayAsset 'wasd\wasd-minimal.json' },
                @{ name = 'Mouse Overlay'; image = Find-ReplayKitOverlayAsset 'mouse\mouse.png'; layout = Find-ReplayKitOverlayAsset 'mouse\mouse-no-movement.json' }
            )
            foreach ($spec in $inputSpecs) {
                $inputSource = $null
                foreach ($source in @($sources)) {
                    if ([string](Get-ReplayKitJsonValue $source 'name' '') -eq [string]$spec.name) {
                        $inputSource = $source
                        break
                    }
                }
                $inputUuid = if ($null -ne $inputSource) { [string](Get-ReplayKitJsonValue $inputSource 'uuid' '') } else { '' }
                if ([string]::IsNullOrWhiteSpace($inputUuid)) {
                    $inputUuid = [guid]::NewGuid().ToString()
                }
                if ($null -eq $inputSource) {
                    $inputSource = New-ReplayKitInputOverlaySourceJson ([string]$spec.name) $inputUuid ([string]$spec.image) ([string]$spec.layout)
                    [void]$sources.Add($inputSource)
                } else {
                    Set-ReplayKitInputOverlaySourceJson $inputSource ([string]$spec.name) $inputUuid ([string]$spec.image) ([string]$spec.layout)
                }
                Set-ReplayKitOverlayOpacitySourceJson $inputSource $settings
                $inputSources[[string]$spec.name] = @{ uuid = $inputUuid }
            }
        } else {
            Remove-ReplayKitJsonListWhere $sources {
                param($source)
                $inputSourceNames -contains [string](Get-ReplayKitJsonValue $source 'name' '')
            }
            Remove-ReplayKitJsonListWhere $groups {
                param($group)
                [string](Get-ReplayKitJsonValue $group 'name' '') -eq 'Group' -or
                    [string](Get-ReplayKitJsonValue $group 'uuid' '') -eq $inputGroupUuid
            }
        }

        if ($useBongo -and $null -eq $bongoSource) {
            $bongoSource = New-ReplayKitBongoSourceJson ([guid]::NewGuid().ToString())
            [void]$sources.Add($bongoSource)
        }
        if ($useBongo) {
            $bongoUuid = [string](Get-ReplayKitJsonValue $bongoSource 'uuid' '')
            if ([string]::IsNullOrWhiteSpace($bongoUuid)) {
                $bongoUuid = [guid]::NewGuid().ToString()
            }
            Set-ReplayKitBongoSourceJson $bongoSource $bongoUuid
            Set-ReplayKitOverlayOpacitySourceJson $bongoSource $settings
        } else {
            Remove-ReplayKitJsonListWhere $sources {
                param($source)
                [string](Get-ReplayKitJsonValue $source 'name' '') -eq 'Bongo Cat Overlay' -or
                    [string](Get-ReplayKitJsonValue $source 'id' '') -eq 'bongobs-cat' -or
                    (-not [string]::IsNullOrWhiteSpace($bongoUuid) -and [string](Get-ReplayKitJsonValue $source 'uuid' '') -eq $bongoUuid)
            }
        }
        if ($null -eq $windowCaptureSource) {
            $windowCaptureSource = New-ReplayKitWindowCaptureSourceJson $windowCaptureUuid $settings
            [void]$sources.Add($windowCaptureSource)
        } else {
            Set-ReplayKitWindowCaptureSourceJson $windowCaptureSource $windowCaptureUuid $settings
        }

        foreach ($source in @($sources)) {
            $sourceName = [string](Get-ReplayKitJsonValue $source 'name' '')
            Set-ReplayKitDisplayCaptureCursorSourceJson $source
            if ([string]::IsNullOrWhiteSpace((Get-ReplayKitMotionBlurFilterUuid $sourceName))) { continue }
            if ($useMotionBlur) {
                Set-ReplayKitMotionBlurSourceJson $source $true $motionBlurStrength
            } else {
                Remove-ReplayKitMotionBlurSourceJson $source
            }
        }

        foreach ($source in @($sources)) {
            if ([string](Get-ReplayKitJsonValue $source 'id' '') -ne 'scene') { continue }
            $sceneSettings = Get-ReplayKitJsonValue $source 'settings'
            if ($null -eq $sceneSettings) { continue }
            $items = Get-ReplayKitJsonValue $sceneSettings 'items'
            if ($null -eq $items) { continue }
            $items = ConvertTo-ReplayKitJsonList $items
            Set-ReplayKitJsonValue $sceneSettings 'items' $items
            if (-not $useInputOverlay) {
                Remove-ReplayKitJsonListWhere $items {
                    param($item)
                    $name = [string](Get-ReplayKitJsonValue $item 'name' '')
                    $sourceUuid = [string](Get-ReplayKitJsonValue $item 'source_uuid' '')
                    ($inputSourceNames -contains $name) -or
                        $name -eq 'Group' -or
                        $sourceUuid -eq $inputGroupUuid -or
                        ($existingInputUuids.ContainsKey($sourceUuid))
                }
            }
            if (-not $useBongo) {
                Remove-ReplayKitJsonListWhere $items {
                    param($item)
                    $name = [string](Get-ReplayKitJsonValue $item 'name' '')
                    $sourceUuid = [string](Get-ReplayKitJsonValue $item 'source_uuid' '')
                    $name -eq 'Bongo Cat Overlay' -or
                        (-not [string]::IsNullOrWhiteSpace($bongoUuid) -and $sourceUuid -eq $bongoUuid)
                }
            }

            $foundBongoItem = $false
            $foundInputItems = @{ 'WASD Overlay' = $false; 'Mouse Overlay' = $false }
            $foundInputGroupItem = $false
            $foundWindowCaptureItem = $false
            $windowCaptureVisible = [string]$settings.screenshareCaptureMode -eq 'game_window'
            foreach ($item in @($items)) {
                $name = [string](Get-ReplayKitJsonValue $item 'name' '')
                $sourceUuid = [string](Get-ReplayKitJsonValue $item 'source_uuid' '')
                if ($name -eq 'Display Capture' -or $name -eq 'Game Capture') {
                    Set-ReplayKitMainCaptureSceneItemJson $item $name $preset
                } elseif ($name -eq 'Window Capture' -or $sourceUuid -eq $windowCaptureUuid) {
                    Set-ReplayKitWindowCaptureSceneItemJson $item $windowCaptureUuid $preset $windowCaptureVisible
                    $foundWindowCaptureItem = $true
                } elseif ($name -eq 'WASD Overlay' -or $name -eq 'Mouse Overlay') {
                    $inputUuid = ''
                    if ($inputSources.ContainsKey($name)) { $inputUuid = [string]$inputSources[$name]['uuid'] }
                    if ([string]::IsNullOrWhiteSpace($inputUuid)) { $inputUuid = $sourceUuid }
                    Set-ReplayKitInputOverlaySceneItemJson $item $name $preset $useInputOverlay $inputUuid $true $settings
                    $foundInputItems[$name] = $true
                    if ($inputSources.ContainsKey($name)) {
                        $inputSources[$name]['id'] = [int](Get-ReplayKitJsonValue $item 'id' 0)
                    }
                } elseif ($name -eq 'Group' -or $sourceUuid -eq $inputGroupUuid) {
                    Set-ReplayKitInputOverlayGroupSceneItemJson $item $inputGroupUuid $preset $useInputOverlay $settings
                    $foundInputGroupItem = $true
                } elseif ($name -eq 'Bongo Cat Overlay' -or $sourceUuid -eq $bongoUuid) {
                    Set-ReplayKitBongoSceneItemJson $item $bongoUuid $preset $useBongo $settings
                    $foundBongoItem = $true
                }
            }

            if (-not $foundWindowCaptureItem) {
                $newItem = New-ReplayKitJsonObject
                Set-ReplayKitJsonValue $newItem 'id' (Get-ReplayKitNextJsonSceneItemId $items)
                Set-ReplayKitWindowCaptureSceneItemJson $newItem $windowCaptureUuid $preset $windowCaptureVisible
                $insertAt = 0
                for ($i = 0; $i -lt $items.Count; $i++) {
                    if ([string](Get-ReplayKitJsonValue $items[$i] 'name' '') -eq 'Display Capture') {
                        $insertAt = $i + 1
                        break
                    }
                }
                $items.Insert($insertAt, $newItem)
                Set-ReplayKitJsonValue $sceneSettings 'id_counter' (Get-ReplayKitNextJsonSceneItemId $items)
            }
            Move-ReplayKitJsonSceneItemAfter $items 'Window Capture' 'Display Capture'
            if ($useBongo -and -not $foundBongoItem) {
                $newItem = New-ReplayKitJsonObject
                Set-ReplayKitJsonValue $newItem 'id' (Get-ReplayKitNextJsonSceneItemId $items)
                Set-ReplayKitBongoSceneItemJson $newItem $bongoUuid $preset $true $settings
                [void]$items.Add($newItem)
                Set-ReplayKitJsonValue $sceneSettings 'id_counter' (Get-ReplayKitNextJsonSceneItemId $items)
            }
            if ($useInputOverlay) {
                foreach ($name in @('WASD Overlay', 'Mouse Overlay')) {
                    if ([bool]$foundInputItems[$name]) { continue }
                    if (-not $inputSources.ContainsKey($name)) { throw "Input overlay source was not prepared for $name." }
                    $newItem = New-ReplayKitJsonObject
                    Set-ReplayKitJsonValue $newItem 'id' (Get-ReplayKitNextJsonSceneItemId $items)
                    Set-ReplayKitInputOverlaySceneItemJson $newItem $name $preset $true ([string]$inputSources[$name]['uuid']) $true $settings
                    $inputSources[$name]['id'] = [int](Get-ReplayKitJsonValue $newItem 'id' 0)
                    [void]$items.Add($newItem)
                    Set-ReplayKitJsonValue $sceneSettings 'id_counter' (Get-ReplayKitNextJsonSceneItemId $items)
                }
                if ($null -eq $inputGroup) {
                    $inputGroup = New-ReplayKitJsonObject
                    [void]$groups.Add($inputGroup)
                }
                Set-ReplayKitInputOverlayGroupSourceJson $inputGroup $inputGroupUuid $inputSources $preset
                if (-not $foundInputGroupItem) {
                    $groupItem = New-ReplayKitJsonObject
                    Set-ReplayKitJsonValue $groupItem 'id' (Get-ReplayKitNextJsonSceneItemId $items)
                    Set-ReplayKitInputOverlayGroupSceneItemJson $groupItem $inputGroupUuid $preset $true $settings
                    [void]$items.Add($groupItem)
                    Set-ReplayKitJsonValue $sceneSettings 'id_counter' (Get-ReplayKitNextJsonSceneItemId $items)
                }
            }
        }

        Write-Utf8 $path ($serializer.Serialize((Copy-ReplayKitJsonValue $data)))
        return @{ ok = $true; path = $path }
    } catch {
        return @{ ok = $false; message = $_.Exception.Message }
    }
}

function Apply-ReplayKitOverlayLive([hashtable]$settings, [hashtable]$preset, [bool]$recreateBongo = $true) {
    $warnings = @()
    $scene = Get-ReplayKitSceneName
    if (-not $scene.ok) { return @{ ok = $false; warnings = @("Overlay setting was saved, but OBS scene lookup failed: $($scene.message)") } }

    $sceneName = [string]$scene.name
    $itemsResult = Get-ReplayKitSceneItems $sceneName
    if (-not $itemsResult.ok) { return @{ ok = $false; warnings = @("Overlay setting was saved, but OBS scene items could not be read: $($itemsResult.message)") } }
    $items = $itemsResult.items
    $opacity = Get-ReplayKitOverlayOpacity $settings

    foreach ($name in @('WASD Overlay', 'Mouse Overlay', 'Group')) {
        $item = Find-ReplayKitSceneItem $items $name
        $enabled = ([string]$settings.overlayStyle -eq 'input_overlay')
        $r = Set-ReplayKitSceneItemEnabled $sceneName $item $enabled
        if (-not $r.ok) { $warnings += "Could not toggle ${name}: $($r.message)" }
    }

    $bongoEnabled = ([string]$settings.overlayStyle -eq 'bongo_cat')
    $bongo = Find-ReplayKitSceneItem $items 'Bongo Cat Overlay'
    # bongobs-cat (live2d) holds an "isLoad" + initialization state across hide/show that never gets reset by setinputsettings or sceneitemenabled toggling, so switching wasd/mouse -> bongo leaves the renderer producing no output until obs is restarted. only the plugins create callback (vtubercreate -> initvtuber + updata) puts the model back into a known-good state. so when the user picks bongo cat we destroy any existing input and recreate from scratch. that is heavier than a settings merge but its the only thing that survives the plugins lifecycle bugs (see https://github.com/a1928370421/Bongobs-Cat-Plugin VtuberFrameWork.cpp).
    if ($bongoEnabled) {
        if ($null -ne $bongo -and $recreateBongo) {
            $remove = Invoke-ObsWebSocketRequest 'RemoveInput' @{ inputName = 'Bongo Cat Overlay' } 3000
            if (-not $remove.ok) {
                $warnings += "Could not remove stale Bongo Cat overlay: $($remove.message)"
            } else {
                $bongo = $null
            }
        }
        if ($null -eq $bongo) {
            $bongoSettings = @{
                Mode = 'standard'
                width = 1280
                height = 768
                x = 0.0
                y = 0.02
                scale = 1.83
                delay = 1.0
                delaytime = 1.0
                random_motion = $true
                breath = $true
                eyeblink = $true
                track = $true
                live2d = $true
                relative_mouse = $true
                mouse_horizontal_flip = $true
                mouse_vertical_flip = $true
                mask = $false
            }
            $created = Invoke-ObsWebSocketRequest 'CreateInput' @{
                sceneName = $sceneName
                inputName = 'Bongo Cat Overlay'
                inputKind = 'bongobs-cat'
                inputSettings = $bongoSettings
                sceneItemEnabled = $true
            } 5000
            if ($created.ok) {
                $bongo = [pscustomobject]@{
                    sceneItemId = [int]$created.data.sceneItemId
                    sourceName = 'Bongo Cat Overlay'
                }
            } else {
                $warnings += "Could not create Bongo Cat overlay: $($created.message)"
            }
        } else {
            $rBongo = Set-ReplayKitSceneItemEnabled $sceneName $bongo $true
            if (-not $rBongo.ok) { $warnings += "Could not show Bongo Cat overlay: $($rBongo.message)" }
        }
    } else {
        if ($null -ne $bongo) {
            $rBongo = Set-ReplayKitSceneItemEnabled $sceneName $bongo $false
            if (-not $rBongo.ok) { $warnings += "Could not hide Bongo Cat overlay: $($rBongo.message)" }
        }
    }

    $cursorResult = Set-ReplayKitDisplayCaptureCursorLive
    if (-not $cursorResult.ok) {
        $warnings += "Could not enable Display Capture cursor: $($cursorResult.message)"
    }

    foreach ($captureName in @('Display Capture', 'Window Capture', 'Game Capture')) {
        $capture = Find-ReplayKitSceneItem $items $captureName
        $captureTransform = Get-ReplayKitMainCaptureTransform $captureName $preset
        $captureResult = Set-ReplayKitSceneItemTransform $sceneName $capture $captureTransform
        if (-not $captureResult.ok) {
            $warnings += "Could not fit ${captureName} to canvas: $($captureResult.message)"
        }
    }

    if ($bongoEnabled -and $null -ne $bongo) {
        $rTransform = Set-ReplayKitSceneItemTransform $sceneName $bongo (Get-ReplayKitBongoTransform $preset $settings)
        if (-not $rTransform.ok) { $warnings += "Could not position Bongo Cat overlay: $($rTransform.message)" }
        $rOpacity = Apply-ReplayKitOverlayOpacityLive 'Bongo Cat Overlay' $opacity $settings
        if (-not $rOpacity.ok) { $warnings += "Could not set Bongo Cat overlay opacity: $($rOpacity.message)" }
    }

    if ([string]$settings.overlayStyle -eq 'input_overlay') {
        $group = Find-ReplayKitSceneItem $items 'Group'
        if ($null -ne $group) {
            $rGroupTransform = Set-ReplayKitSceneItemTransform $sceneName $group (Get-ReplayKitInputOverlayGroupTransform $preset $settings)
            if (-not $rGroupTransform.ok) { $warnings += "Could not position input overlay group: $($rGroupTransform.message)" }
        }
        $wasdPng = Find-ReplayKitOverlayAsset 'wasd\wasd.png'
        $wasdJson = Find-ReplayKitOverlayAsset 'wasd\wasd-minimal.json'
        $mousePng = Find-ReplayKitOverlayAsset 'mouse\mouse.png'
        $mouseJson = Find-ReplayKitOverlayAsset 'mouse\mouse-no-movement.json'
        if ([string]::IsNullOrWhiteSpace($wasdPng) -or [string]::IsNullOrWhiteSpace($wasdJson) -or
            [string]::IsNullOrWhiteSpace($mousePng) -or [string]::IsNullOrWhiteSpace($mouseJson)) {
            $warnings += 'Input overlay assets are missing from the ReplayKit install, so OBS could not switch that overlay live.'
        } else {
            foreach ($entry in @(
                @{ name = 'WASD Overlay'; image = $wasdPng; layout = $wasdJson },
                @{ name = 'Mouse Overlay'; image = $mousePng; layout = $mouseJson }
            )) {
                $created = Ensure-ReplayKitInputSceneItem $sceneName ([string]$entry.name) 'input-overlay' @{
                    'io.input_source' = 'This computer'
                    'io.overlay_image' = [string]$entry.image
                    'io.layout_file' = [string]$entry.layout
                } $true
                if (-not $created.ok) {
                    $warnings += "Could not create $($entry.name): $($created.message)"
                    continue
                }
                $transform = Get-ReplayKitInputOverlayTransform ([string]$entry.name) $preset $settings
                $rTransform = Set-ReplayKitSceneItemTransform $sceneName $created.item $transform
                if (-not $rTransform.ok) { $warnings += "Could not position $($entry.name): $($rTransform.message)" }
                $rOpacity = Apply-ReplayKitOverlayOpacityLive ([string]$entry.name) $opacity $settings
                if (-not $rOpacity.ok) { $warnings += "Could not set $($entry.name) opacity: $($rOpacity.message)" }
            }
        }
    }

    return @{ ok = ($warnings.Count -eq 0); warnings = $warnings }
}

function Get-ReplayKitOverlaySceneItemNames([string]$overlayStyle) {
    if ($overlayStyle -eq 'bongo_cat') { return @('Bongo Cat Overlay') }
    if ($overlayStyle -eq 'input_overlay') { return @('Group', 'WASD Overlay', 'Mouse Overlay') }
    return @()
}

function Get-ReplayKitOverlaySourceNames([string]$overlayStyle) {
    if ($overlayStyle -eq 'bongo_cat') { return @('Bongo Cat Overlay') }
    if ($overlayStyle -eq 'input_overlay') { return @('Group', 'WASD Overlay', 'Mouse Overlay') }
    return @()
}

function Get-ReplayKitOverlayCandidateSourceNames([string]$overlayStyle) {
    $names = New-Object System.Collections.Generic.List[string]
    foreach ($name in @(Get-ReplayKitOverlaySourceNames $overlayStyle)) {
        if (-not [string]::IsNullOrWhiteSpace($name) -and -not $names.Contains($name)) {
            [void]$names.Add($name)
        }
    }
    foreach ($style in @('bongo_cat', 'input_overlay')) {
        foreach ($name in @(Get-ReplayKitOverlaySourceNames $style)) {
            if (-not [string]::IsNullOrWhiteSpace($name) -and -not $names.Contains($name)) {
                [void]$names.Add($name)
            }
        }
    }
    return @($names)
}

function Add-ReplayKitOverlayCandidateName([System.Collections.Generic.List[string]]$names, [string]$name) {
    if ([string]::IsNullOrWhiteSpace($name)) { return }
    if (-not $names.Contains($name)) { [void]$names.Add($name) }
}

function Get-ReplayKitOrderedOverlayCandidateSourceNames([string]$overlayStyle) {
    $candidates = @(Get-ReplayKitOverlayCandidateSourceNames $overlayStyle)
    $ordered = New-Object System.Collections.Generic.List[string]
    $items = @()

    $scene = Get-ReplayKitSceneName
    if ($scene.ok) {
        $itemsResult = Get-ReplayKitSceneItems ([string]$scene.name)
        if ($itemsResult.ok) { $items = @($itemsResult.items) }
    }

    if ($items.Count -gt 0) {
        foreach ($name in $candidates) {
            $item = Find-ReplayKitSceneItem $items $name
            if ($null -ne $item -and (Test-ReplayKitSceneItemVisible $item)) {
                Add-ReplayKitOverlayCandidateName $ordered $name
            }
        }
        foreach ($name in $candidates) {
            if ($null -ne (Find-ReplayKitSceneItem $items $name)) {
                Add-ReplayKitOverlayCandidateName $ordered $name
            }
        }
    }
    foreach ($name in $candidates) {
        Add-ReplayKitOverlayCandidateName $ordered $name
    }
    return @($ordered)
}

function Test-ReplayKitSceneItemVisible($item) {
    if ($null -eq $item) { return $false }
    $enabled = Get-ReplayKitJsonValue $item 'sceneItemEnabled' $true
    if ($null -eq $enabled) { return $true }
    return [bool]$enabled
}

function Get-ReplayKitExistingInputOverlayScaleTargets($items, [bool]$visibleOnly) {
    $group = Find-ReplayKitSceneItem $items 'Group'
    if ($null -ne $group -and ((-not $visibleOnly) -or (Test-ReplayKitSceneItemVisible $group))) {
        return @('Group')
    }
    $targets = @()
    foreach ($name in @('WASD Overlay', 'Mouse Overlay')) {
        $item = Find-ReplayKitSceneItem $items $name
        if ($null -eq $item) { continue }
        if ($visibleOnly -and -not (Test-ReplayKitSceneItemVisible $item)) { continue }
        $targets += $name
    }
    return $targets
}

function Get-ReplayKitOverlayScaleSceneItemNames($items, [string]$overlayStyle) {
    if ($overlayStyle -eq 'bongo_cat') {
        if ($null -ne (Find-ReplayKitSceneItem $items 'Bongo Cat Overlay')) { return @('Bongo Cat Overlay') }
    }
    if ($overlayStyle -eq 'input_overlay') {
        $inputTargets = @(Get-ReplayKitExistingInputOverlayScaleTargets $items $false)
        if ($inputTargets.Count -gt 0) { return $inputTargets }
    }

    $bongo = Find-ReplayKitSceneItem $items 'Bongo Cat Overlay'
    if ($null -ne $bongo -and (Test-ReplayKitSceneItemVisible $bongo)) { return @('Bongo Cat Overlay') }
    $visibleInputTargets = @(Get-ReplayKitExistingInputOverlayScaleTargets $items $true)
    if ($visibleInputTargets.Count -gt 0) { return $visibleInputTargets }
    if ($null -ne $bongo) { return @('Bongo Cat Overlay') }
    return @(Get-ReplayKitExistingInputOverlayScaleTargets $items $false)
}

function Get-ReplayKitOverlayOpacitySourceTargets([string]$overlayStyle) {
    $names = @(Get-ReplayKitOrderedOverlayCandidateSourceNames $overlayStyle)
    if ($names.Count -eq 0) { return @() }

    foreach ($name in $names) {
        $filters = Get-ReplayKitSourceFilterListLive $name
        if (-not $filters.ok) { continue }
        $match = Get-ReplayKitOverlayOpacityLiveFilterInfo $filters.filters
        if ($match.found) { return @($name) }
    }

    if ($overlayStyle -eq 'input_overlay') {
        if (Test-ReplayKitSourceExistsForFilters 'Group') { return @('Group') }
        $fallback = @()
        foreach ($name in @('WASD Overlay', 'Mouse Overlay')) {
            if (Test-ReplayKitSourceExistsForFilters $name) { $fallback += $name }
        }
        if ($fallback.Count -gt 0) { return $fallback }
    }
    if ($overlayStyle -eq 'bongo_cat' -and (Test-ReplayKitSourceExistsForFilters 'Bongo Cat Overlay')) {
        return @('Bongo Cat Overlay')
    }
    foreach ($name in $names) {
        if (Test-ReplayKitSourceExistsForFilters $name) { return @($name) }
    }
    return @()
}

function Apply-ReplayKitOverlayScaleLive([hashtable]$previous, [hashtable]$settings) {
    $warnings = @()
    $applied = @()
    $previousScale = [double](Get-ReplayKitOverlayScaleFactor $previous)
    $nextScale = [double](Get-ReplayKitOverlayScaleFactor $settings)
    if ($previousScale -le 0.0 -or [Math]::Abs($nextScale - $previousScale) -lt 0.0001) {
        return @{ ok = $true; applied = $applied; warnings = $warnings }
    }

    $scene = Get-ReplayKitSceneName
    if (-not $scene.ok) {
        return @{ ok = $false; applied = $applied; warnings = @("Overlay size was saved, but OBS scene lookup failed: $($scene.message)") }
    }
    $sceneName = [string]$scene.name
    $itemsResult = Get-ReplayKitSceneItems $sceneName
    if (-not $itemsResult.ok) {
        return @{ ok = $false; applied = $applied; warnings = @("Overlay size was saved, but OBS scene items could not be read: $($itemsResult.message)") }
    }

    $preset = Get-ReplayKitPresetSpec ([string]$settings.recordingPreset)
    $scaleRatio = $nextScale / $previousScale
    foreach ($name in (Get-ReplayKitOverlayScaleSceneItemNames $itemsResult.items ([string]$settings.overlayStyle))) {
        $item = Find-ReplayKitSceneItem $itemsResult.items $name
        if ($null -eq $item) { continue }
        $r = Set-ReplayKitSceneItemScaledFromCurrent $sceneName $item $scaleRatio $preset
        if ($r.ok) {
            if (-not $r.skipped) { $applied += "$name size" }
        } else {
            $warnings += "Could not resize ${name}: $($r.message)"
        }
    }
    return @{ ok = ($warnings.Count -eq 0); applied = $applied; warnings = $warnings }
}

function Apply-ReplayKitOverlayOpacityForStyleLive([hashtable]$settings) {
    $warnings = @()
    $applied = @()
    $opacity = Get-ReplayKitOverlayOpacity $settings
    foreach ($name in (Get-ReplayKitOverlayOpacitySourceTargets ([string]$settings.overlayStyle))) {
        $r = Apply-ReplayKitOverlayOpacityLive $name $opacity $settings
        if ($r.ok) { $applied += "$name opacity" }
        else { $warnings += "Could not set $name opacity: $($r.message)" }
    }
    return @{ ok = ($warnings.Count -eq 0); applied = $applied; warnings = $warnings }
}

function Apply-ReplayKitOverlayVisualSettingsLive([hashtable]$previous, [hashtable]$settings) {
    $warnings = @()
    $applied = @()
    if ([int]$previous.overlayScale -ne [int]$settings.overlayScale) {
        $scale = Apply-ReplayKitOverlayScaleLive $previous $settings
        $applied += $scale.applied
        $warnings += $scale.warnings
    }
    $colorChanged = ([int]$previous.overlayOpacity -ne [int]$settings.overlayOpacity) -or
        ([double]$previous.overlayHueShift -ne [double]$settings.overlayHueShift) -or
        ([string]$previous.overlayColorMultiply -ne [string]$settings.overlayColorMultiply) -or
        ([string]$previous.overlayColorAdd -ne [string]$settings.overlayColorAdd)
    if ($colorChanged) {
        $opacity = Apply-ReplayKitOverlayOpacityForStyleLive $settings
        $applied += $opacity.applied
        $warnings += $opacity.warnings
    }
    return @{ ok = ($warnings.Count -eq 0); applied = $applied; warnings = $warnings }
}

function Get-ReplayKitOverlayPreviewState([hashtable]$settings) {
    if ($null -ne $script:State.ReplayKitOverlayPreviewState) { return $script:State.ReplayKitOverlayPreviewState }

    # holds the lock across the whole obs-websocket round trip below, not just the check -- this only runs once per preview session, and a lost race here would mean two callers each capture their own "baseline", whichever stores last silently winning as the revert target.
    [System.Threading.Monitor]::Enter($script:State.OverlayPreviewLock)
    try {
    if ($null -ne $script:State.ReplayKitOverlayPreviewState) { return $script:State.ReplayKitOverlayPreviewState }

    $baselineSettings = @{}
    foreach ($key in $settings.Keys) { $baselineSettings[$key] = $settings[$key] }
    $liveOpacity = Get-ReplayKitLiveOverlayOpacityPercent $baselineSettings
    if ($liveOpacity.ok -and $null -ne $liveOpacity.opacity) {
        $baselineSettings.overlayOpacity = [int]$liveOpacity.opacity
    }

    $state = @{
        settings = $baselineSettings
        preset = (Get-ReplayKitPresetSpec ([string]$baselineSettings.recordingPreset))
        sceneName = ''
        transforms = @{}
        opacityFilters = @()
    }

    $scene = Get-ReplayKitSceneName
    if ($scene.ok) {
        $state.sceneName = [string]$scene.name
        $itemsResult = Get-ReplayKitSceneItems ([string]$scene.name)
        if ($itemsResult.ok) {
            foreach ($name in (Get-ReplayKitOverlayScaleSceneItemNames $itemsResult.items ([string]$settings.overlayStyle))) {
                $item = Find-ReplayKitSceneItem $itemsResult.items $name
                if ($null -eq $item) { continue }
                $current = Get-ReplayKitSceneItemTransformLive ([string]$scene.name) $item
                if ($current.ok -and $null -ne $current.transform) {
                    $state.transforms[$name] = @{
                        item = $item
                        transform = $current.transform
                    }
                }
            }
        }
    }

    $opacityFilters = @()
    foreach ($name in (Get-ReplayKitOverlayOpacitySourceTargets ([string]$baselineSettings.overlayStyle))) {
        $filters = Get-ReplayKitSourceFilterListLive $name
        if (-not $filters.ok) { continue }
        $match = Get-ReplayKitOverlayOpacityLiveFilterInfo $filters.filters
        if ($match.found) {
            $opacityFilters += @{
                sourceName = $name
                found = $true
                filterName = [string]$match.name
                settings = (Copy-ReplayKitJsonValue $match.settings)
                enabled = [bool]$match.enabled
            }
        } else {
            $opacityFilters += @{
                sourceName = $name
                found = $false
                filterName = ''
                settings = @{}
                enabled = $false
            }
        }
    }
    $state.opacityFilters = $opacityFilters

    $script:State.ReplayKitOverlayPreviewState = $state
    return $state
    } finally { [System.Threading.Monitor]::Exit($script:State.OverlayPreviewLock) }
}

function Clear-ReplayKitOverlayPreviewState {
    [System.Threading.Monitor]::Enter($script:State.OverlayPreviewLock)
    try { $script:State.ReplayKitOverlayPreviewState = $null } finally { [System.Threading.Monitor]::Exit($script:State.OverlayPreviewLock) }
}

function Get-ReplayKitOverlayPreviewRevision($incoming) {
    if ($null -eq $incoming -or -not $incoming.ContainsKey('overlayPreviewRevision')) { return $null }
    $raw = $incoming.overlayPreviewRevision
    [void]$incoming.Remove('overlayPreviewRevision')
    try {
        $revision = [Int64]$raw
    } catch {
        throw 'Invalid overlay preview revision.'
    }
    if ($revision -lt 1) { throw 'Invalid overlay preview revision.' }
    return $revision
}

function Apply-ReplayKitOverlayScalePreviewLive([hashtable]$preview, [hashtable]$settings) {
    $warnings = @()
    $applied = @()
    $baseScale = [double](Get-ReplayKitOverlayScaleFactor ([hashtable]$preview.settings))
    $nextScale = [double](Get-ReplayKitOverlayScaleFactor $settings)
    if ($baseScale -le 0.0) { $baseScale = 1.0 }
    if ([string]::IsNullOrWhiteSpace([string]$preview.sceneName)) {
        return @{ ok = $true; applied = $applied; warnings = $warnings }
    }
    $scaleRatio = $nextScale / $baseScale
    $preset = $preview.preset
    if ($null -eq $preset) { $preset = Get-ReplayKitPresetSpec ([string]$settings.recordingPreset) }
    foreach ($name in @($preview.transforms.Keys)) {
        $entry = $preview.transforms[$name]
        $transform = Get-ReplayKitScaledTransformFromCurrent $entry.transform $scaleRatio $preset
        $r = Set-ReplayKitSceneItemTransform ([string]$preview.sceneName) $entry.item $transform
        if ($r.ok) { $applied += "$name size" }
        else { $warnings += "Could not preview resize ${name}: $($r.message)" }
    }
    return @{ ok = ($warnings.Count -eq 0); applied = $applied; warnings = $warnings }
}

function Restore-ReplayKitOverlayPreviewLive([hashtable]$preview) {
    $warnings = @()
    $applied = @()
    if ($null -eq $preview) { return @{ ok = $true; applied = $applied; warnings = $warnings } }
    if (-not [string]::IsNullOrWhiteSpace([string]$preview.sceneName)) {
        foreach ($name in @($preview.transforms.Keys)) {
            $entry = $preview.transforms[$name]
            $r = Set-ReplayKitSceneItemTransform ([string]$preview.sceneName) $entry.item $entry.transform
            if ($r.ok) { $applied += "$name size restored" }
            else { $warnings += "Could not restore ${name}: $($r.message)" }
        }
    }
    $restoredExactOpacity = $false
    foreach ($entry in @($preview.opacityFilters)) {
        $sourceName = [string]$entry.sourceName
        if ([string]::IsNullOrWhiteSpace($sourceName)) { continue }
        if ([bool]$entry.found) {
            $filterName = [string]$entry.filterName
            if ([string]::IsNullOrWhiteSpace($filterName)) { continue }
            $set = Invoke-ObsWebSocketRequest 'SetSourceFilterSettings' @{
                sourceName = $sourceName
                filterName = $filterName
                filterSettings = $entry.settings
                overlay = $true
            } 3000
            if ($set.ok) {
                $enable = Invoke-ObsWebSocketRequest 'SetSourceFilterEnabled' @{
                    sourceName = $sourceName
                    filterName = $filterName
                    filterEnabled = [bool]$entry.enabled
                } 3000
                if ($enable.ok) {
                    $applied += "$sourceName opacity restored"
                    $restoredExactOpacity = $true
                } else {
                    $warnings += "Could not restore ${sourceName} opacity filter enabled state: $($enable.message)"
                }
            } else {
                $warnings += "Could not restore ${sourceName} opacity filter settings: $($set.message)"
            }
        } else {
            $filters = Get-ReplayKitSourceFilterListLive $sourceName
            if (-not $filters.ok) { continue }
            foreach ($filter in @($filters.filters)) {
                if ([string](Get-ReplayKitJsonValue $filter 'filterName' '') -ne 'ReplayKit Overlay Opacity') { continue }
                $remove = Invoke-ObsWebSocketRequest 'RemoveSourceFilter' @{
                    sourceName = $sourceName
                    filterName = 'ReplayKit Overlay Opacity'
                } 3000
                if ($remove.ok) {
                    $applied += "$sourceName opacity restored"
                    $restoredExactOpacity = $true
                } else {
                    $warnings += "Could not remove preview opacity filter from ${sourceName}: $($remove.message)"
                }
                break
            }
        }
    }
    if (-not $restoredExactOpacity) {
        $opacity = Apply-ReplayKitOverlayOpacityForStyleLive ([hashtable]$preview.settings)
        $applied += $opacity.applied
        $warnings += $opacity.warnings
    }
    Clear-ReplayKitOverlayPreviewState
    return @{ ok = ($warnings.Count -eq 0); applied = $applied; warnings = $warnings }
}

function Invoke-ReplayKitOverlayPreviewFromRequest([string]$body, [string]$mode = 'preview') {
    if ($mode -eq 'cancel') {
        # read the snapshot under a brief lock, then release before Restore-...Live does its own (possibly several) obs-websocket round trips -- a cancel and a fresh preview tick landing in the same tight window can still race on which baseline is "current", but holding the lock across the whole restore would block every other overlay-preview op for that entire duration, which is worse than this narrow, self-correcting edge case.
        [System.Threading.Monitor]::Enter($script:State.OverlayPreviewLock)
        try { $previewSnapshot = $script:State.ReplayKitOverlayPreviewState } finally { [System.Threading.Monitor]::Exit($script:State.OverlayPreviewLock) }
        $live = Restore-ReplayKitOverlayPreviewLive $previewSnapshot
        return @{
            ok = $true
            settings = (Read-ReplayKitSettings)
            applied = $live.applied
            warnings = $live.warnings
            restartRequired = $false
            restartReason = ''
        }
    }
    if ([string]::IsNullOrWhiteSpace($body)) {
        throw 'Missing overlay preview body.'
    }
    $incoming = ConvertTo-PlainHash (ConvertFrom-Json $body)
    $previewRevision = Get-ReplayKitOverlayPreviewRevision $incoming
    if ($incoming.Count -lt 1) {
        throw 'Missing overlay preview setting.'
    }
    foreach ($key in $incoming.Keys) {
        if ($key -ne 'overlayOpacity' -and
            $key -ne 'overlayScale' -and
            $key -ne 'overlayHueShift' -and
            $key -ne 'overlayColorMultiply' -and
            $key -ne 'overlayColorAdd') {
            throw "Unknown overlay preview setting: $key"
        }
    }
    if ($mode -eq 'preview' -and $null -ne $previewRevision) {
        # read-compare-write on the revision has to be one atomic op -- otherwise two preview ticks racing here could both pass the check and both think they own the latest revision.
        $stale = $false
        [System.Threading.Monitor]::Enter($script:State.OverlayPreviewLock)
        try {
            if ($null -ne $script:State.ReplayKitOverlayPreviewRevision -and [Int64]$script:State.ReplayKitOverlayPreviewRevision -ge [Int64]$previewRevision) {
                $stale = $true
            } else {
                $script:State.ReplayKitOverlayPreviewRevision = [Int64]$previewRevision
            }
        } finally { [System.Threading.Monitor]::Exit($script:State.OverlayPreviewLock) }
        if ($stale) {
            return @{
                ok = $true
                settings = (Normalize-ReplayKitSettings (Read-ReplayKitSettings))
                applied = @()
                warnings = @()
                skipped = $true
                restartRequired = $false
                restartReason = ''
            }
        }
    }

    $current = Read-ReplayKitSettings
    $previous = Normalize-ReplayKitSettings $current
    $preview = Get-ReplayKitOverlayPreviewState $previous
    foreach ($key in $incoming.Keys) {
        $current[$key] = $incoming[$key]
    }
    $settings = Normalize-ReplayKitSettings $current
    $forceColorSync = Test-ReplayKitOverlayColorRequest $incoming

    if ($mode -eq 'commit') {
        Write-ReplayKitSettings $settings
        $warnings = @()
        $applied = @()
        if ([int]$preview.settings.overlayScale -ne [int]$settings.overlayScale) {
            $scale = Apply-ReplayKitOverlayScalePreviewLive $preview $settings
            $applied += $scale.applied
            $warnings += $scale.warnings
        }
        $colorChanged = ([int]$preview.settings.overlayOpacity -ne [int]$settings.overlayOpacity) -or
            ([double]$preview.settings.overlayHueShift -ne [double]$settings.overlayHueShift) -or
            ([string]$preview.settings.overlayColorMultiply -ne [string]$settings.overlayColorMultiply) -or
            ([string]$preview.settings.overlayColorAdd -ne [string]$settings.overlayColorAdd)
        if ($colorChanged -or $forceColorSync) {
            $opacity = Apply-ReplayKitOverlayOpacityForStyleLive $settings
            $applied += $opacity.applied
            $warnings += $opacity.warnings
        }
        Clear-ReplayKitOverlayPreviewState
        return @{
            ok = ($warnings.Count -eq 0)
            settings = $settings
            applied = $applied
            warnings = $warnings
            restartRequired = $false
            restartReason = ''
        }
    }

    $warnings = @()
    $applied = @()
    if ([int]$preview.settings.overlayScale -ne [int]$settings.overlayScale) {
        $scale = Apply-ReplayKitOverlayScalePreviewLive $preview $settings
        $applied += $scale.applied
        $warnings += $scale.warnings
    }
    $colorChanged = ([int]$preview.settings.overlayOpacity -ne [int]$settings.overlayOpacity) -or
        ([double]$preview.settings.overlayHueShift -ne [double]$settings.overlayHueShift) -or
        ([string]$preview.settings.overlayColorMultiply -ne [string]$settings.overlayColorMultiply) -or
        ([string]$preview.settings.overlayColorAdd -ne [string]$settings.overlayColorAdd)
    if ($colorChanged -or $forceColorSync) {
        $opacity = Apply-ReplayKitOverlayOpacityForStyleLive $settings
        $applied += $opacity.applied
        $warnings += $opacity.warnings
    }

    return @{
        ok = $true
        settings = $settings
        applied = $applied
        warnings = $warnings
        restartRequired = $false
        restartReason = ''
    }
}

function New-ReplayKitInactiveOutputState {
    return @{ ok = $true; wasActive = $false }
}

function Apply-ReplayKitRuntimeOutputsLive([hashtable]$settings, [hashtable]$preset, [bool]$restartObs, [bool]$applyVideoSettings = $true, [bool]$applyReplayBufferOutput = $true) {
    $warnings = @()
    $applied = @()
    $legacyVcam = [string]$settings.discord_output_mode -eq 'virtual_camera_legacy'
    $stopAllOutputs = $restartObs -or $applyVideoSettings
    $record = New-ReplayKitInactiveOutputState
    $replay = New-ReplayKitInactiveOutputState
    $vcam = New-ReplayKitInactiveOutputState

    if ($stopAllOutputs) {
        $record = Stop-ObsOutputIfActive 'GetRecordStatus' 'StopRecord' 'recording'
        $replay = Stop-ObsOutputIfActive 'GetReplayBufferStatus' 'StopReplayBuffer' 'replay buffer'
        $vcam = Stop-ObsOutputIfActive 'GetVirtualCamStatus' 'StopVirtualCam' 'virtual camera'
    } elseif ($applyReplayBufferOutput) {
        $replay = Stop-ObsOutputIfActive 'GetReplayBufferStatus' 'StopReplayBuffer' 'replay buffer'
    }

    foreach ($state in @($record, $replay, $vcam)) {
        if ($state.warning) { $warnings += $state.warning }
    }

    if ($record.ok -and $replay.ok -and $vcam.ok) {
        if ($applyVideoSettings) {
            $video = Set-ReplayKitVideoSettingsLive $preset
            if ($video.ok) { $applied += 'OBS video format' }
            else { $warnings += "OBS video settings were saved, but live apply failed: $($video.message)" }
        }

        if ($applyReplayBufferOutput) {
            $rb = Set-ReplayKitReplayBufferOutputLive $settings $preset
            if ($rb.ok) { $applied += 'OBS replay buffer output' }
            else { $warnings += "OBS replay buffer settings were saved, but live apply failed: $($rb.message)" }
        }
    } else {
        $warnings += 'OBS outputs were not stopped safely, so video/output live changes were not applied.'
    }

    $outputs = @(
        @{ state = $replay; request = 'StartReplayBuffer'; label = 'replay buffer' },
        @{ state = $record; request = 'StartRecord'; label = 'recording' }
    )
    if ($legacyVcam) {
        $outputs = @(@{ state = $vcam; request = 'StartVirtualCam'; label = 'virtual camera (legacy)' }) + $outputs
    } elseif ($vcam.ok -and $vcam.wasActive) {
        $applied += 'virtual camera stopped for projector Discord output'
    }

    if ($restartObs) {
        foreach ($output in $outputs) {
            if ($output.state.ok -and $output.state.wasActive) {
                $applied += "$($output.label) stopped for OBS restart"
            }
        }
        return @{ applied = $applied; warnings = $warnings }
    }

    foreach ($restart in $outputs) {
        $r = Start-ObsOutputIfNeeded $restart.state $restart.request $restart.label
        if (-not $r.ok -and $r.warning) { $warnings += $r.warning }
        elseif ($restart.state.wasActive) { $applied += "$($restart.label) restarted" }
    }

    return @{ applied = $applied; warnings = $warnings }
}

function Set-ReplayKitProjectorMonitoringState([hashtable]$settings, [bool]$enabled) {
    $inputName = 'Desktop Audio (excl. Discord)'
    $monitorType = if ($enabled) {
        'OBS_MONITORING_TYPE_MONITOR_AND_OUTPUT'
    } else {
        'OBS_MONITORING_TYPE_NONE'
    }

    if ($monitorType -eq 'OBS_MONITORING_TYPE_NONE') {
        return Set-ReplayKitDesktopAudioMixerState $inputName $monitorType
    }

    if (-not (Get-Command -Name Get-ReplayKitObsStreamAudioRenderDevice -CommandType Function -ErrorAction SilentlyContinue)) {
        return @{ ok = $false; applied = @(); message = 'OBS Stream Audio render device resolver is unavailable.' }
    }
    $renderDevice = Get-ReplayKitObsStreamAudioRenderDevice
    if (-not $renderDevice.ok) {
        return @{ ok = $false; applied = @(); message = $renderDevice.message }
    }

    $monitorDevice = Set-ReplayKitObsMonitoringDevice ([string]$renderDevice.id) ([string]$renderDevice.name) 6
    if (-not $monitorDevice.ok) {
        return @{ ok = $false; applied = @(); message = $monitorDevice.message }
    }

    $applied = @($monitorDevice.applied)
    $mixer = Set-ReplayKitDesktopAudioMixerState $inputName $monitorType
    if (-not $mixer.ok) {
        return @{ ok = $false; applied = $applied; message = $mixer.message }
    }
    $applied += $mixer.applied
    return @{ ok = $true; applied = $applied; message = '' }
}

function Apply-ReplayKitDiscordOutputLive([hashtable]$settings) {
    $warnings = @()
    $applied = @()
    $ok = $true
    $message = ''
    $inputName = 'Desktop Audio (excl. Discord)'
    $mode = [string]$settings.discord_output_mode
    if ($mode -ne 'projector') { $mode = 'projector' }
    $isLegacyVcam = $false
    $screenshareEnabled = Test-ReplayKitDiscordScreenshareEnabled $settings
    $shareEnabled = ($screenshareEnabled -and [bool]$settings.discord_projector_enabled)
    if (-not $screenshareEnabled) {
        $applied += 'Discord screenshare support disabled'
    }
    $monitorType = 'OBS_MONITORING_TYPE_NONE'
    if ($shareEnabled) {
        $monitorType = 'OBS_MONITORING_TYPE_MONITOR_AND_OUTPUT'
    }

    $captureSettings = Set-ReplayKitDesktopAudioCaptureSettingsLive $inputName
    if ($captureSettings.ok) {
        $applied += 'desktop audio exclusions updated'
    } else {
        $warnings += "Discord output mode was saved, but Desktop Audio exclusions could not be updated: $($captureSettings.message)"
    }

    if ($mode -eq 'projector') {
        $mixer = Set-ReplayKitProjectorMonitoringState $settings $shareEnabled
        if ($mixer.ok) {
            $applied += $mixer.applied
            # obs persists audio monitoring in the scene collection -- keep that file in sync with the live websocket change so a disabled Share Preview cannot return after obs restarts.
            $sceneMonitoringType = if ($shareEnabled) { 2 } else { 0 }
            $sceneFile = Set-ReplayKitShareModeSceneFile $sceneMonitoringType $false
            if ($sceneFile.ok) {
                if ($sceneFile.changed) { $applied += 'desktop audio monitor state saved' }
            } else {
                $warnings += "Desktop Audio monitoring was applied live, but could not be saved for restart: $($sceneFile.message)"
            }
        } else {
            $ok = $false
            $message = "Desktop Audio monitoring state could not be verified: $($mixer.message)"
            $warnings += "Discord output mode was saved, but $message"
        }
    } else {
        $mixer = Set-ReplayKitDesktopAudioMixerState $inputName $monitorType
        if ($mixer.ok) {
            $applied += $mixer.applied
        } else {
            $ok = $false
            $message = "Desktop Audio mixer state could not be changed: $($mixer.message)"
            $warnings += "Discord output mode was saved, but $message"
        }
    }

    if (-not $ok -and $mode -ne 'projector') {
        return @{ ok = $false; message = $message; applied = $applied; warnings = $warnings; restartRequired = $false; restartReason = '' }
    }

    $status = Invoke-ObsWebSocketRequest 'GetVirtualCamStatus' $null 3000
    if (-not $status.ok) {
        $warnings += "Discord output mode was saved, but virtual camera state could not be read: $($status.message)"
    } else {
        $running = [bool]$status.data.outputActive
        if ($isLegacyVcam -and -not $running) {
            Write-Log 'Virtual Camera legacy mode enabled for Discord output.'
            $start = Invoke-ObsWebSocketRequest 'StartVirtualCam' $null 8000
            if ($start.ok) { $applied += 'virtual camera started (legacy)' }
            else { $warnings += "Discord output mode was saved, but legacy virtual camera could not be started: $($start.message)" }
        } elseif ((-not $isLegacyVcam) -and $running) {
            $stop = Invoke-ObsWebSocketRequest 'StopVirtualCam' $null 8000
            if ($stop.ok) { $applied += 'virtual camera stopped' }
            else { $warnings += "Discord output mode was saved, but virtual camera could not be stopped: $($stop.message)" }
        } else {
            $applied += if ($isLegacyVcam) { 'virtual camera already on (legacy)' } else { 'virtual camera already off' }
        }
    }

    if ($shareEnabled) {
        Stop-ReplayKitDiscordShareBridge
        Write-Log 'Discord projector mode enabled'
        Write-Log 'Skipping OBS Virtual Camera for Discord output'
        $projector = Invoke-ReplayKitDiscordProjectorRepark $settings
        if ($projector.ok) {
            $applied += $projector.applied
        } else {
            $ok = $false
            if ([string]::IsNullOrWhiteSpace($message)) { $message = "OBS projector was not ready: $($projector.message)" }
            $warnings += "OBS projector was not ready: $($projector.message)"
        }
        if ($projector.warnings) { $warnings += $projector.warnings }
    } elseif ($mode -eq 'projector') {
        Stop-ReplayKitDiscordShareBridge
        $projector = Invoke-ReplayKitDiscordProjectorDisable $settings
        if ($projector.ok) {
            $applied += $projector.applied
        } else {
            $ok = $false
            if ([string]::IsNullOrWhiteSpace($message)) { $message = "Share preview could not be disabled: $($projector.message)" }
            $warnings += "Share preview could not be disabled: $($projector.message)"
        }
        if ($projector.warnings) { $warnings += $projector.warnings }
    } else {
        $warnings += 'Virtual Camera is legacy/deprecated for Discord output.'
    }

    return @{ ok = $ok; message = $message; applied = $applied; warnings = $warnings; restartRequired = $false; restartReason = '' }
}

function Set-ReplayKitSharePreviewEnabled([bool]$enabled) {
    $previous = Read-ReplayKitSettings
    $settings = @{}
    foreach ($key in $previous.Keys) {
        $settings[$key] = $previous[$key]
    }
    $settings.discord_output_mode = 'projector'
    $settings.shareMode = 'projector'
    if ($enabled -and -not (Test-ReplayKitDiscordScreenshareEnabled $settings)) {
        $settings.discord_projector_enabled = $false
        $settings = Normalize-ReplayKitSettings $settings
        Write-ReplayKitSettings $settings
        $live = Apply-ReplayKitDiscordOutputLive $settings
        return @{
            ok = $true
            enabled = $false
            available = $false
            settings = $settings
            applied = $live.applied
            warnings = @($live.warnings) + @('Discord screenshare support is disabled in Advanced settings.')
            message = ''
            restartRequired = $false
            restartReason = ''
        }
    }
    $settings.discord_projector_enabled = $enabled
    $settings = Normalize-ReplayKitSettings $settings
    Write-ReplayKitSettings $settings
    $live = Apply-ReplayKitDiscordOutputLive $settings
    if (-not [bool]$live.ok) {
        return @{
            ok = $true
            enabled = ((Test-ReplayKitDiscordScreenshareEnabled $settings) -and [bool]$settings.discord_projector_enabled)
            available = (Test-ReplayKitDiscordScreenshareEnabled $settings)
            settings = $settings
            applied = $live.applied
            warnings = @($live.warnings) + @($live.message)
            message = ''
            restartRequired = $false
            restartReason = ''
        }
    }
    return @{
        ok = $true
        enabled = ((Test-ReplayKitDiscordScreenshareEnabled $settings) -and [bool]$settings.discord_projector_enabled)
        available = (Test-ReplayKitDiscordScreenshareEnabled $settings)
        settings = $settings
        applied = $live.applied
        warnings = $live.warnings
        restartRequired = $false
        restartReason = ''
    }
}

function Get-ReplayKitSharePreviewState([bool]$repairMonitoring = $false) {
    $settings = Read-ReplayKitSettings
    $available = Test-ReplayKitDiscordScreenshareEnabled $settings
    $enabled = ($available -and [string]$settings.discord_output_mode -eq 'projector' -and [bool]$settings.discord_projector_enabled)
    $inputName = 'Desktop Audio (excl. Discord)'
    $desiredMonitorType = if ($enabled) {
        'OBS_MONITORING_TYPE_MONITOR_AND_OUTPUT'
    } else {
        'OBS_MONITORING_TYPE_NONE'
    }
    $warnings = @()
    $repaired = $false

    $actual = Get-ReplayKitInputMonitorTypeValue $inputName
    $actualMonitorType = if ($actual.ok) { [string]$actual.value } else { '' }
    if (-not $actual.ok) { $warnings += "Could not read Desktop Audio monitor state: $($actual.message)" }

    $mute = Invoke-ObsWebSocketRequest 'GetInputMute' @{ inputName = $inputName } 3000
    $muteOk = [bool]$mute.ok
    $inputMuted = $null
    if ($muteOk) {
        $inputMuted = [bool](Get-ReplayKitJsonValue $mute.data 'inputMuted' $false)
    } else {
        $warnings += "Could not read Desktop Audio mute state: $($mute.message)"
    }

    $synced = ($actual.ok -and $actualMonitorType -eq $desiredMonitorType -and $muteOk -and -not [bool]$inputMuted)
    if ($repairMonitoring -and -not $synced) {
        $repair = Set-ReplayKitProjectorMonitoringState $settings $enabled
        if ($repair.ok) {
            $repaired = $true
            $actual = Get-ReplayKitInputMonitorTypeValue $inputName
            $actualMonitorType = if ($actual.ok) { [string]$actual.value } else { '' }
            $mute = Invoke-ObsWebSocketRequest 'GetInputMute' @{ inputName = $inputName } 3000
            $muteOk = [bool]$mute.ok
            $inputMuted = if ($muteOk) { [bool](Get-ReplayKitJsonValue $mute.data 'inputMuted' $false) } else { $null }
            $synced = ($actual.ok -and $actualMonitorType -eq $desiredMonitorType -and $muteOk -and -not [bool]$inputMuted)
        } else {
            $warnings += "Could not repair Desktop Audio monitoring state: $($repair.message)"
        }
    }

    return @{
        ok = $true
        enabled = $enabled
        available = $available
        settings = $settings
        monitoring = @{
            inputName = $inputName
            desiredMonitorType = $desiredMonitorType
            actualMonitorType = $actualMonitorType
            actualReadOk = [bool]$actual.ok
            inputMuted = $inputMuted
            muteReadOk = $muteOk
            synced = [bool]$synced
            repaired = [bool]$repaired
        }
        warnings = $warnings
    }
}

function Apply-ReplayKitShareModeLive([string]$shareMode) {
    $settings = Read-ReplayKitSettings
    $settings.discord_output_mode = 'projector'
    $settings.shareMode = 'projector'
    return Apply-ReplayKitDiscordOutputLive $settings
}

function Set-ReplayKitDesktopAudioMixerState([string]$inputName, [string]$monitorType) {
    $applied = @()
    $targetIsOff = $monitorType -eq 'OBS_MONITORING_TYPE_NONE'

    $lastMonitorMessage = ''
    $monitorReady = $false
    for ($attempt = 1; $attempt -le 6; $attempt++) {
        $actualBefore = Get-ReplayKitInputMonitorTypeValue $inputName
        if ($actualBefore.ok -and [string]$actualBefore.value -eq $monitorType) {
            $monitorReady = $true
            break
        }

        $monitor = Set-ReplayKitInputMonitorTypeRaw $inputName $monitorType
        if (-not $monitor.ok) {
            $lastMonitorMessage = $monitor.message
            Start-Sleep -Milliseconds ([Math]::Min(1000, 150 * $attempt))
            continue
        }

        Start-Sleep -Milliseconds ([Math]::Min(1000, 150 * $attempt))
        $actualAfter = Get-ReplayKitInputMonitorTypeValue $inputName
        if ($actualAfter.ok -and [string]$actualAfter.value -eq $monitorType) {
            $monitorReady = $true
            break
        }
        $lastMonitorMessage = if ($actualAfter.ok) {
            "OBS reported '$($actualAfter.value)' instead of '$monitorType'."
        } else {
            $actualAfter.message
        }
    }
    if (-not $monitorReady) {
        return @{ ok = $false; applied = $applied; message = "OBS did not report the requested monitor state after retries. $lastMonitorMessage" }
    }

    $unmute = Set-ReplayKitInputMuteState $inputName $false 6
    if (-not $unmute.ok) {
        return @{ ok = $false; applied = $applied; message = "monitor state changed, but Desktop Audio could not be unmuted: $($unmute.message)" }
    }

    $applied += 'desktop audio unmuted'
    $refresh = Refresh-ReplayKitDesktopAudioMixerIconState $inputName $monitorType
    if (-not $refresh.ok) {
        return @{ ok = $false; applied = $applied; message = "monitor state changed, but the OBS mixer icon could not be refreshed: $($refresh.message)" }
    }
    $applied += $refresh.applied
    $applied += if ($targetIsOff) { 'desktop audio monitor off' } else { 'desktop audio monitor on' }
    return @{ ok = $true; applied = $applied; message = '' }
}

function Set-ReplayKitInputMonitorTypeRaw([string]$inputName, [string]$monitorType) {
    $result = Invoke-ObsWebSocketRequest 'SetInputAudioMonitorType' @{
        inputName = $inputName
        monitorType = $monitorType
    } 3000
    if ($result.ok) { return @{ ok = $true; message = '' } }
    return @{ ok = $false; message = $result.message }
}

function Set-ReplayKitInputMuteRaw([string]$inputName, [bool]$muted) {
    $result = Invoke-ObsWebSocketRequest 'SetInputMute' @{
        inputName = $inputName
        inputMuted = $muted
    } 3000
    if ($result.ok) { return @{ ok = $true; message = '' } }
    return @{ ok = $false; message = $result.message }
}

function Set-ReplayKitInputMuteState([string]$inputName, [bool]$muted, [int]$maxAttempts = 4) {
    $attempts = [Math]::Max(1, [Math]::Min(10, $maxAttempts))
    $lastMessage = ''
    for ($attempt = 1; $attempt -le $attempts; $attempt++) {
        if (Test-ReplayKitInputMuteState $inputName $muted) { return @{ ok = $true; message = '' } }
        $result = Set-ReplayKitInputMuteRaw $inputName $muted
        if (-not $result.ok) {
            $lastMessage = $result.message
            Start-Sleep -Milliseconds ([Math]::Min(1000, 150 * $attempt))
            continue
        }
        Start-Sleep -Milliseconds ([Math]::Min(1000, 150 * $attempt))
        if (Test-ReplayKitInputMuteState $inputName $muted) { return @{ ok = $true; message = '' } }
        $lastMessage = 'OBS did not report the requested mute state after setting it.'
    }
    return @{ ok = $false; message = $lastMessage }
}

function Refresh-ReplayKitDesktopAudioMixerIconState([string]$inputName, [string]$monitorType) {
    $applied = @()

    $monitor = Set-ReplayKitInputMonitorTypeRaw $inputName $monitorType
    if (-not $monitor.ok) {
        return @{ ok = $false; applied = $applied; message = $monitor.message }
    }

    if ($monitorType -eq 'OBS_MONITORING_TYPE_NONE') {
        $mute = Set-ReplayKitInputMuteRaw $inputName $true
        if (-not $mute.ok) {
            return @{ ok = $false; applied = $applied; message = $mute.message }
        }
        Start-Sleep -Milliseconds 40
    }

    $unmute = Set-ReplayKitInputMuteRaw $inputName $false
    if (-not $unmute.ok) {
        return @{ ok = $false; applied = $applied; message = $unmute.message }
    }

    Start-Sleep -Milliseconds 40
    $monitor = Set-ReplayKitInputMonitorTypeRaw $inputName $monitorType
    if (-not $monitor.ok) {
        return @{ ok = $false; applied = $applied; message = $monitor.message }
    }

    for ($attempt = 1; $attempt -le 4; $attempt++) {
        Start-Sleep -Milliseconds ([Math]::Min(300, 50 * $attempt))
        $actual = Get-ReplayKitInputMonitorTypeValue $inputName
        if ($actual.ok -and [string]$actual.value -eq $monitorType -and (Test-ReplayKitInputMuteState $inputName $false)) {
            $applied += 'desktop audio mixer icon refreshed'
            return @{ ok = $true; applied = $applied; message = '' }
        }
    }

    return @{ ok = $false; applied = $applied; message = 'OBS did not report the refreshed monitor and mute state.' }
}

function Get-ReplayKitInputMonitorTypeValue([string]$inputName) {
    $verify = Invoke-ObsWebSocketRequest 'GetInputAudioMonitorType' @{ inputName = $inputName } 3000
    if (-not $verify.ok) { return @{ ok = $false; value = ''; message = $verify.message } }
    $monitorType = Get-ReplayKitJsonValue $verify.data 'monitorType' ''
    $inputAudioMonitorType = Get-ReplayKitJsonValue $verify.data 'inputAudioMonitorType' ''
    if ($monitorType) { return @{ ok = $true; value = [string]$monitorType; message = '' } }
    if ($inputAudioMonitorType) { return @{ ok = $true; value = [string]$inputAudioMonitorType; message = '' } }
    return @{ ok = $false; value = ''; message = 'OBS did not return an input monitor type.' }
}

function Test-ReplayKitInputMonitorType([string]$inputName, [string]$expected) {
    $actual = Get-ReplayKitInputMonitorTypeValue $inputName
    if (-not $actual.ok) { return $false }
    return [string]$actual.value -eq $expected
}

function Test-ReplayKitInputMuteState([string]$inputName, [bool]$expected) {
    $verify = Invoke-ObsWebSocketRequest 'GetInputMute' @{ inputName = $inputName } 3000
    if (-not $verify.ok) { return $false }
    $value = Get-ReplayKitJsonValue $verify.data 'inputMuted' $null
    if ($null -eq $value) { return $false }
    return [bool]$value -eq $expected
}

function Get-ReplayKitDesktopAudioExcludeList {
    $entries = [System.Collections.ArrayList]::new()
    foreach ($name in @(
        'Discord.exe',
        'DiscordSystemHelper.exe',
        'DiscordCanary.exe',
        'DiscordPTB.exe',
        'DiscordDevelopment.exe',
        'obs64.exe',
        'obs32.exe',
        'obs.exe',
        # obss cef subprocess -- without this, clip audio played in the dock gets treated as ordinary desktop audio, monitored back out to the discord share, and doubles up with the copy discord already grabs directly from the same process tree.
        'obs-browser-page.exe'
    )) {
        $entry = New-ReplayKitJsonObject
        Set-ReplayKitJsonValue $entry 'value' $name
        [void]$entries.Add($entry)
    }
    return ,$entries
}

function Set-ReplayKitDesktopAudioCaptureSettingsObject($settingsObject) {
    Set-ReplayKitJsonValue $settingsObject 'mode' 'session'
    Set-ReplayKitJsonValue $settingsObject 'executable_list' (Get-ReplayKitDesktopAudioExcludeList)
    Set-ReplayKitJsonValue $settingsObject 'exclude' $true
}

function Set-ReplayKitDesktopAudioCaptureSettingsLive([string]$inputName) {
    $inputSettings = New-ReplayKitJsonObject
    Set-ReplayKitDesktopAudioCaptureSettingsObject $inputSettings
    $result = Invoke-ObsWebSocketRequest 'SetInputSettings' @{
        inputName = $inputName
        inputSettings = $inputSettings
        overlay = $true
    } 3000
    if ($result.ok) { return @{ ok = $true; message = '' } }
    return @{ ok = $false; message = $result.message }
}

function Set-ReplayKitShareModeSceneFile([int]$monitoringType, [bool]$muted) {
    try {
        $path = Get-ReplayKitSceneCollectionPath
        if (-not (Test-Path -LiteralPath $path)) {
            return @{ ok = $true; changed = $false }
        }
        $data = ConvertFrom-Json ([System.IO.File]::ReadAllText($path))
        $sources = Get-ReplayKitJsonValue $data 'sources'
        if ($null -eq $sources) { return @{ ok = $true; changed = $false } }
        $changed = $false
        foreach ($source in @($sources)) {
            if ([string](Get-ReplayKitJsonValue $source 'name' '') -ne 'Desktop Audio (excl. Discord)') { continue }
            $settingsObject = Get-ReplayKitJsonValue $source 'settings' $null
            if ($null -eq $settingsObject) {
                $settingsObject = New-ReplayKitJsonObject
                Set-ReplayKitJsonValue $source 'settings' $settingsObject
            }
            $settingsBefore = ConvertTo-Json $settingsObject -Depth 30 -Compress
            Set-ReplayKitDesktopAudioCaptureSettingsObject $settingsObject
            $settingsAfter = ConvertTo-Json $settingsObject -Depth 30 -Compress
            if ($settingsBefore -ne $settingsAfter) { $changed = $true }
            $current = [int](Get-ReplayKitJsonValue $source 'monitoring_type' -1)
            if ($current -ne $monitoringType) {
                Set-ReplayKitJsonValue $source 'monitoring_type' $monitoringType
                $changed = $true
            }
            $currentMuted = [bool](Get-ReplayKitJsonValue $source 'muted' $true)
            if ($currentMuted -ne $muted) {
                Set-ReplayKitJsonValue $source 'muted' $muted
                $changed = $true
            }
        }
        if ($changed) {
            Write-Utf8 $path (ConvertTo-Json $data -Depth 80)
        }
        return @{ ok = $true; changed = $changed }
    } catch {
        return @{ ok = $false; changed = $false; message = $_.Exception.Message }
    }
}

function Get-RecordingHotkeyRestore([hashtable]$settings) {
    $start = Get-ObsProfileParameterSafe 'Hotkeys' 'OBSBasic.StartRecording'
    $stop = Get-ObsProfileParameterSafe 'Hotkeys' 'OBSBasic.StopRecording'
    if (-not $start.ok -or -not $stop.ok) {
        return @{ ok = $false; message = 'Could not snapshot OBS native recording hotkeys.' }
    }
    return @{
        ok    = $true
        start = [string]$start.data.parameterValue
        stop  = [string]$stop.data.parameterValue
    }
}

function Sync-RecordingKeybindFromObs([hashtable]$settings) {
    $start = Get-ObsProfileParameterSafe 'Hotkeys' 'OBSBasic.StartRecording'
    $stop = Get-ObsProfileParameterSafe 'Hotkeys' 'OBSBasic.StopRecording'
    if (-not $start.ok -and -not $stop.ok) { return $settings }

    $combo = @{}
    if ($start.ok) {
        $combo = Convert-RecordingBasicIniToKeybind ([string]$start.data.parameterValue)
    }
    if (-not $combo.ContainsKey('key') -and $stop.ok) {
        $combo = Convert-RecordingBasicIniToKeybind ([string]$stop.data.parameterValue)
    }

    $current = ConvertTo-Json $settings.recordingKeybind -Depth 5 -Compress
    $next = ConvertTo-Json $combo -Depth 5 -Compress
    if ($current -ne $next) {
        $settings.recordingKeybind = $combo
    }
    return $settings
}

function Sync-HotkeysFromObs([hashtable]$settings) {
    $before = ConvertTo-Json @{
        clip = $settings.clipKeybind
        recording = $settings.recordingKeybind
    } -Depth 6 -Compress

    $clip = Get-ObsProfileParameterSafe 'Hotkeys' 'ReplayBuffer'
    if ($clip.ok) {
        $combo = Convert-ClipBasicIniToKeybind ([string]$clip.data.parameterValue)
        if ($combo.ContainsKey('key')) {
            $current = ConvertTo-Json $settings.clipKeybind -Depth 5 -Compress
            $next = ConvertTo-Json $combo -Depth 5 -Compress
            if ($current -ne $next) {
                $settings.clipKeybind = $combo
            }
        }
    }

    $settings = Sync-RecordingKeybindFromObs $settings
    $after = ConvertTo-Json @{
        clip = $settings.clipKeybind
        recording = $settings.recordingKeybind
    } -Depth 6 -Compress
    if ($before -ne $after) {
        Write-ReplayKitSettings $settings
    }
    return $settings
}

function Sync-ReplayBufferSecondsFromObs([hashtable]$settings) {
    # obs' own settings dialog can edit RecRBTime directly, bypassing replaykit entirely -- without this, the custom settings dock keeps showing (and would re-apply) whatever replaykit last wrote, silently reverting the users obs-side edit on the next apply.
    $rb = Get-ReplayKitObsProfileParameterValue 'AdvOut' 'RecRBTime'
    if (-not $rb.ok) { return $settings }
    $seconds = 0
    if (-not [int]::TryParse($rb.value, [ref]$seconds)) { return $settings }
    if ($seconds -le 0 -or $seconds -eq [int]$settings.replaySeconds) { return $settings }
    $settings.replaySeconds = $seconds
    $settings.clipNotificationSeconds = $seconds
    Write-ReplayKitSettings $settings
    return $settings
}

function Set-ReplayKitHotkeyCapture([bool]$active) {
    if ($active) {
        # holds the lock across the whole snapshot capture, not just the check -- a lost race here means the second caller sees Active already true and skips capturing, but the FIRST callers capture is what everyone restores to later, so two callers must never both think they need to capture; rare and user-initiated, so the extra hold time is cheap to pay for that guarantee.
        [System.Threading.Monitor]::Enter($script:State.HotkeyCaptureLock)
        try {
            if (-not $script:State.ReplayKitHotkeyCaptureActive) {
                $settings = Read-ReplayKitSettings
                $recordingRestore = Get-RecordingHotkeyRestore $settings
                if (-not $recordingRestore.ok) { return $recordingRestore }
                $script:State.ReplayKitHotkeyCaptureRestore = @{
                    clip      = Convert-ClipKeybindToBasicIni $settings.clipKeybind
                    recordingStart = $recordingRestore.start
                    recordingStop  = $recordingRestore.stop
                }
                $script:State.ReplayKitHotkeyCaptureActive = $true
            }
        } finally { [System.Threading.Monitor]::Exit($script:State.HotkeyCaptureLock) }
        $emptyReplay = ConvertTo-Json @{ 'ReplayBuffer.Save' = @() } -Depth 5 -Compress
        $emptyRecording = Get-EmptyObsHotkeyJson
        $r1 = Set-ReplayBufferHotkeyJson $emptyReplay
        $r2 = Set-RecordingHotkeyJson $emptyRecording
        $errors = @()
        if (-not $r1.ok) { $errors += $r1.message }
        if (-not $r2.ok) { $errors += $r2.message }
        if ($errors.Count -gt 0) {
            return @{ ok = $false; message = ($errors -join '; ') }
        }
        return @{ ok = $true; active = $true }
    }

    if (-not $script:State.ReplayKitHotkeyCaptureActive) {
        return @{ ok = $true; active = $false }
    }

    $settings = Read-ReplayKitSettings
    return Restore-ReplayKitHotkeysFromSettings $settings
}

function Restore-ReplayKitHotkeysFromSettings([hashtable]$settings) {
    $clipJson = Convert-ClipKeybindToBasicIni $settings.clipKeybind
    $recordingJson = Convert-RecordingKeybindToBasicIni $settings.recordingKeybind
    $r1 = Set-ReplayBufferHotkeyJson ([string]$clipJson)
    $r2 = Set-RecordingHotkeyPairJson ([string]$recordingJson) ([string]$recordingJson)
    $errors = @()
    if (-not $r1.ok) { $errors += $r1.message }
    if (-not $r2.ok) { $errors += $r2.message }
    if ($errors.Count -gt 0) {
        return @{ ok = $false; message = ($errors -join '; ') }
    }
    [System.Threading.Monitor]::Enter($script:State.HotkeyCaptureLock)
    try {
        $script:State.ReplayKitHotkeyCaptureActive = $false
        $script:State.ReplayKitHotkeyCaptureRestore = $null
    } finally { [System.Threading.Monitor]::Exit($script:State.HotkeyCaptureLock) }
    return @{ ok = $true; active = $false }
}

function Ensure-ReplayKitHotkeyCaptureReleased([hashtable]$settings) {
    if (-not $script:State.ReplayKitHotkeyCaptureActive) {
        return @{ ok = $true; warnings = @(); applied = @() }
    }
    $restore = Restore-ReplayKitHotkeysFromSettings $settings
    if ($restore.ok) {
        return @{ ok = $true; warnings = @(); applied = @('OBS hotkeys restored') }
    }
    return @{ ok = $false; warnings = @("OBS hotkeys could not be restored: $($restore.message)"); applied = @() }
}

function Apply-ReplayKitLiveSettings([hashtable]$settings, [bool]$restartObs = $false, [bool]$applyOverlay = $true, [bool]$recreateBongo = $true, [bool]$applyMotionBlur = $true, [bool]$applyRuntimeOutputs = $true, [bool]$applyVideoSettings = $true, [bool]$applyReplayBufferOutput = $true) {
    $warnings = @()
    $applied = @()
    $preset = Get-ReplayKitPresetSpec ([string]$settings.recordingPreset)
    $encoder = Get-ReplayKitEncoderSpec $settings $preset
    if ($encoder.warning) { $warnings += [string]$encoder.warning }
    if ([bool]$settings.motionBlurEnabled -and -not (Test-ReplayKitShaderfilterPluginInstalled)) {
        $warnings += 'Motion blur was saved, but OBS Shaderfilter is not installed. Re-run ReplayKit setup, then restart OBS after the plugin install.'
    }

    try {
        if ($settings.clipDir) {
            [void](New-Item -ItemType Directory -Path $settings.clipDir -Force)
        }
        Update-HelperConfigClipDir ([string]$settings.clipDir)
        $applied += 'clip folder'
    } catch {
        $warnings += "Clip folder was saved, but the helper could not switch to it: $($_.Exception.Message)"
    }

    $startup = Set-ObsStartupSetting ([bool]$settings.obsStartupEnabled)
    if ($startup.ok) { $applied += 'Windows startup' }
    else { $warnings += "Windows startup was saved, but Windows rejected the change: $($startup.message)" }

    $closeWarning = Set-ReplayKitObsCloseWarningConfig ([bool]$settings.disableObsCloseWarning)
    if ($closeWarning.ok) { $applied += 'OBS close warning' }
    else { $warnings += "OBS close warning was saved, but OBS config could not be updated: $($closeWarning.message)" }

    $sleepOverride = Apply-ReplayKitSleepOverrideSetting ([bool]$settings.allowSleepWhileActive)
    $applied += $sleepOverride.applied
    $warnings += $sleepOverride.warnings

    $profileUpdates = @()
    foreach ($u in $preset.profile) { $profileUpdates += ,$u }
    $profileUpdates += @(
        @('AdvOut', 'RecRB', 'true'),
        @('AdvOut', 'RecRBTime', [string][int]$settings.replaySeconds),
        @('AdvOut', 'RecRBSize', [string](Get-ReplayKitScaledRbSizeMb ([string]$settings.recordingPreset) ([int]$settings.replaySeconds))),
        @('AdvOut', 'RecEncoder', [string]$encoder.id),
        @('Hotkeys', 'ReplayBuffer', (Convert-ClipKeybindToBasicIni $settings.clipKeybind))
    )
    $recordingHotkey = Convert-RecordingKeybindToBasicIni $settings.recordingKeybind
    $profileUpdates += @(
        @('Hotkeys', 'OBSBasic.StartRecording', $recordingHotkey),
        @('Hotkeys', 'OBSBasic.StopRecording', $recordingHotkey)
    )
    if ($settings.clipDir) {
        $profileUpdates += @(
            @('SimpleOutput', 'FilePath', [string]$settings.clipDir),
            @('AdvOut', 'RecFilePath', [string]$settings.clipDir),
            @('AdvOut', 'FFFilePath', [string]$settings.clipDir)
        )
    }

    $encoderWrite = Write-ReplayKitRecordEncoderJson $encoder
    if ($encoderWrite.ok) { $applied += 'recording encoder settings' }
    else { $warnings += "Recording codec was saved, but encoder settings could not be written: $($encoderWrite.message)" }

    foreach ($u in $profileUpdates) {
        $r = Set-ObsProfileParameterSafe $u[0] $u[1] $u[2]
        if (-not $r.ok) {
            $warnings += "OBS did not accept $($u[0]).$($u[1]): $($r.message)"
        }
    }

    $applied += 'OBS profile settings'
    if ($settings.clipDir) { $applied += 'OBS recording folder' }

    if ($applyRuntimeOutputs) {
        $outputs = Apply-ReplayKitRuntimeOutputsLive $settings $preset $restartObs $applyVideoSettings $applyReplayBufferOutput
        $applied += $outputs.applied
        $warnings += $outputs.warnings
    }

    $screenshareCapture = Apply-ReplayKitScreenshareCaptureLive $settings $preset
    $applied += $screenshareCapture.applied
    $warnings += $screenshareCapture.warnings

    $discordOutput = Apply-ReplayKitDiscordOutputLive $settings
    $applied += $discordOutput.applied
    $warnings += $discordOutput.warnings

    if ($applyOverlay -or $applyMotionBlur) {
        if ($restartObs) {
            $overlayFile = Set-ReplayKitOverlaySceneFile $settings $preset
            if ($overlayFile.ok) { $applied += 'OBS overlay scene file' }
            else { $warnings += "Overlay setting was saved, but the OBS scene file could not be prepared for restart: $($overlayFile.message)" }
        } else {
            if ($applyOverlay) {
                $overlay = Apply-ReplayKitOverlayLive $settings $preset $recreateBongo
                if ($overlay.ok) { $applied += 'OBS overlay' }
                else { $warnings += $overlay.warnings }
            }
            if ($applyMotionBlur) {
                $motionBlur = Apply-ReplayKitMotionBlurLive $settings
                $applied += $motionBlur.applied
                $warnings += $motionBlur.warnings
            }
        }
    }

    $restartReason = ''
    if ($restartObs) {
        $restartReason = 'Recording quality, GPU-use, clip-size, codec, or overlay changes require OBS to restart.'
    }

    return @{
        applied = $applied
        warnings = $warnings
        restartRequired = $restartObs
        restartReason = $restartReason
    }
}

function Test-ReplayKitSettingsOrigin([hashtable]$req) {
    if (-not $req.Headers.ContainsKey('origin')) { return $true }
    $origin = ([string]$req.Headers['origin']).Trim().ToLowerInvariant()
    $port = if ($script:State.Config.port) { [int]$script:State.Config.port } else { $script:DEFAULT_PORT }
    if ($origin -eq "http://127.0.0.1:$port" -or $origin -eq "http://localhost:$port") {
        return $true
    }
    if ($origin -eq 'null') {
        return (Test-ReplayKitInstalledDockReferer $req) -or (Test-ReplayKitObsBrowserUserAgent $req)
    }
    return $false
}

function Test-ReplayKitObsBrowserUserAgent([hashtable]$req) {
    if (-not $req.Headers.ContainsKey('user-agent')) { return $false }
    return ([string]$req.Headers['user-agent']) -match '(?i)\bOBS/'
}

function Test-ReplayKitInstalledDockReferer([hashtable]$req) {
    if (-not $req.Headers.ContainsKey('referer')) { return $false }
    try {
        $uri = [System.Uri]::new([string]$req.Headers['referer'])
        if (-not $uri.IsFile) { return $false }
        $path = [System.IO.Path]::GetFullPath($uri.LocalPath)
        $dockRoot = [System.IO.Path]::GetFullPath((Get-DockDir)).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
        $defaultRoot = [System.IO.Path]::GetFullPath((Get-DefaultDockDir)).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
        return (
            $path.StartsWith($dockRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
            $path.StartsWith($defaultRoot, [System.StringComparison]::OrdinalIgnoreCase)
        )
    } catch {
        return $false
    }
}

function Get-ReplayKitSettingsPayload {
    Load-Config
    $settings = Read-ReplayKitSettings
    $settings = Sync-HotkeysFromObs $settings
    $settings = Sync-ReplayBufferSecondsFromObs $settings
    if ([string]::IsNullOrWhiteSpace([string]$settings.clipDir) -and
        -not [string]::IsNullOrWhiteSpace([string]$script:State.Config.clipDir)) {
        $settings.clipDir = Resolve-ClipDirSetting ([string]$script:State.Config.clipDir)
    }
    $overlayOpacity = Get-ReplayKitLiveOverlayOpacityPercent $settings
    if (-not $overlayOpacity.ok) {
        $overlayOpacity = Get-ReplayKitSceneFileOverlayOpacityPercent $settings
    }
    if ($overlayOpacity.ok -and [int]$settings.overlayOpacity -ne [int]$overlayOpacity.opacity) {
        $settings.overlayOpacity = [int]$overlayOpacity.opacity
        Write-ReplayKitSettings $settings
    }
    return @{
        ok = $true
        settings = $settings
        options = @{
            recordingPresets = @(
                @{ value = 'performance'; label = 'Performance'; blurb = '720p30. Lowest load and smaller files.' },
                @{ value = 'balanced'; label = 'Balanced'; blurb = '1080p60. Recommended for most PCs.' },
                @{ value = 'quality'; label = 'Quality'; blurb = 'Higher-quality target for high-end PCs.' }
            )
            compressionModes = @(
                @{ value = 'lower_gpu'; label = 'Lowest GPU use'; blurb = 'Least encoder work. Larger clips.' },
                @{ value = 'balanced'; label = 'Balanced'; blurb = 'Good file size with modest encoder load.' },
                @{ value = 'smaller_files'; label = 'Smallest clips'; blurb = 'More encoder work for tighter files.' }
            )
            codecs = @(
                @{ value = 'auto'; label = 'Auto'; blurb = 'ReplayKit picks the best supported encoder.' },
                @{ value = 'h264'; label = 'H.264'; blurb = 'Largest files, broadest playback support.' },
                @{ value = 'h265'; label = 'HEVC'; blurb = 'Smaller files on modern GPUs.' }
            )
            overlays = @(
                @{ value = 'input_overlay'; label = 'WASD / mouse'; blurb = 'Simple keyboard and mouse overlay.' },
                @{ value = 'bongo_cat'; label = 'Bongo Cat'; blurb = 'Animated keyboard and mouse overlay.' },
                @{ value = 'off'; label = 'Off'; blurb = 'No input overlay in the OBS scene.' }
            )
            screenshareCaptureModes = @(
                @{ value = 'hybrid_auto'; label = 'Auto'; blurb = 'Desktop fallback with fullscreen Game Capture on top.' },
                @{ value = 'desktop'; label = 'Desktop'; blurb = 'Show the full ReplayKit desktop capture.' },
                @{ value = 'game_auto'; label = 'Game only'; blurb = 'Use OBS Game Capture for any fullscreen game.' },
                @{ value = 'game_window'; label = 'Specific game'; blurb = 'Use Window Capture for the selected game window.' }
            )
            screenshareGameWindows = @(Get-ReplayKitGameWindowCandidates ([string]$settings.screenshareGameWindow))
            discordOutputModes = @(
                @{ value = 'projector'; label = 'Projector'; blurb = 'OBS Windowed Projector parked by ReplayKit.' }
            )
            keybinds = @(
                @{ value = 'shift_backslash'; label = 'Shift + \'; blurb = 'Default ReplayKit save hotkey.'; combo = @{ shift = $true; key = 'OBS_KEY_BACKSLASH' } },
                @{ value = 'ctrl_shift_s'; label = 'Ctrl + Shift + S'; blurb = 'Easy to remember, uses two modifiers.'; combo = @{ control = $true; shift = $true; key = 'OBS_KEY_S' } },
                @{ value = 'f8'; label = 'F8'; blurb = 'Single function key.'; combo = @{ key = 'OBS_KEY_F8' } },
                @{ value = 'f9'; label = 'F9'; blurb = 'Single function key.'; combo = @{ key = 'OBS_KEY_F9' } },
                @{ value = 'f10'; label = 'F10'; blurb = 'Single function key.'; combo = @{ key = 'OBS_KEY_F10' } }
            )
        }
    }
}

function Test-ReplayKitRestartRequired([hashtable]$previous, [hashtable]$settings) {
    foreach ($key in @('recordingPreset', 'compressionMode', 'codecPreference')) {
        if ([string]$previous[$key] -ne [string]$settings[$key]) { return $true }
    }
    return ([string]$previous.overlayStyle -ne [string]$settings.overlayStyle)
}

function Test-ReplayKitRuntimeVideoSettingsChanged([hashtable]$previous, [hashtable]$settings) {
    return ([string]$previous.recordingPreset -ne [string]$settings.recordingPreset)
}

function Test-ReplayKitReplayBufferOutputChanged([hashtable]$previous, [hashtable]$settings) {
    foreach ($key in @('recordingPreset', 'replaySeconds', 'clipDir')) {
        if ([string]$previous[$key] -ne [string]$settings[$key]) { return $true }
    }
    return $false
}

function Test-ReplayKitShareModeOnlyRequest([hashtable]$incoming) {
    if ($incoming.Count -ne 1) { return $false }
    return $incoming.ContainsKey('shareMode')
}

function Test-ReplayKitDiscordOutputOnlyRequest([hashtable]$incoming) {
    if ($incoming.Count -lt 1) { return $false }
    $allowed = @(
        'shareMode',
        'discord_screenshare_enabled',
        'discord_output_mode',
        'discord_projector_enabled',
        'discord_projector_width',
        'discord_projector_height',
        'discord_projector_visible_pixels',
        'discord_projector_monitor_index',
        'discord_projector_edge',
        'discord_projector_title_hint',
        'discord_projector_hide_taskbar'
    )
    foreach ($key in $incoming.Keys) {
        if ($allowed -notcontains $key) { return $false }
    }
    return $true
}

function Test-ReplayKitScreenshareCaptureOnlyRequest([hashtable]$incoming) {
    if ($incoming.Count -lt 1) { return $false }
    $allowed = @('screenshareCaptureMode', 'screenshareGameWindow', 'screenshareGameOverrides', 'screenshareAutoGameKeepFocused', 'screenshareSwitchDelaySeconds')
    foreach ($key in $incoming.Keys) {
        if ($allowed -notcontains $key) { return $false }
    }
    return $true
}

function Test-ReplayKitIncomingSettingsChanged([hashtable]$incoming, [hashtable]$previous, [hashtable]$settings) {
    foreach ($key in $incoming.Keys) {
        $before = ConvertTo-Json $previous[$key] -Depth 20 -Compress
        $after = ConvertTo-Json $settings[$key] -Depth 20 -Compress
        if ($before -ne $after) { return $true }
    }
    return $false
}

function Test-ReplayKitOverlayColorRequest([hashtable]$incoming) {
    if ($null -eq $incoming) { return $false }
    return (
        $incoming.ContainsKey('overlayOpacity') -or
        $incoming.ContainsKey('overlayHueShift') -or
        $incoming.ContainsKey('overlayColorMultiply') -or
        $incoming.ContainsKey('overlayColorAdd')
    )
}

function Test-ReplayKitOnlyMotionBlurChanged([hashtable]$previous, [hashtable]$settings) {
    foreach ($key in (Get-DefaultReplayKitSettings).Keys) {
        if ($key -eq 'motionBlurEnabled' -or $key -eq 'motionBlurStrength') { continue }
        $before = ConvertTo-Json $previous[$key] -Depth 20 -Compress
        $after = ConvertTo-Json $settings[$key] -Depth 20 -Compress
        if ($before -ne $after) { return $false }
    }
    return (
        ([bool]$previous.motionBlurEnabled -ne [bool]$settings.motionBlurEnabled) -or
        ([double]$previous.motionBlurStrength -ne [double]$settings.motionBlurStrength)
    )
}

function Test-ReplayKitOnlyOverlayVisualChanged([hashtable]$previous, [hashtable]$settings) {
    foreach ($key in (Get-DefaultReplayKitSettings).Keys) {
        if ($key -eq 'overlayOpacity' -or
            $key -eq 'overlayScale' -or
            $key -eq 'overlayHueShift' -or
            $key -eq 'overlayColorMultiply' -or
            $key -eq 'overlayColorAdd') { continue }
        $before = ConvertTo-Json $previous[$key] -Depth 20 -Compress
        $after = ConvertTo-Json $settings[$key] -Depth 20 -Compress
        if ($before -ne $after) { return $false }
    }
    return (
        ([int]$previous.overlayOpacity -ne [int]$settings.overlayOpacity) -or
        ([int]$previous.overlayScale -ne [int]$settings.overlayScale) -or
        ([double]$previous.overlayHueShift -ne [double]$settings.overlayHueShift) -or
        ([string]$previous.overlayColorMultiply -ne [string]$settings.overlayColorMultiply) -or
        ([string]$previous.overlayColorAdd -ne [string]$settings.overlayColorAdd)
    )
}

function Test-ReplayKitOnlyScreenshareCaptureChanged([hashtable]$previous, [hashtable]$settings) {
    foreach ($key in (Get-DefaultReplayKitSettings).Keys) {
        if ($key -eq 'screenshareCaptureMode' -or $key -eq 'screenshareGameWindow' -or $key -eq 'screenshareGameOverrides' -or $key -eq 'screenshareAutoGameKeepFocused' -or $key -eq 'screenshareSwitchDelaySeconds') { continue }
        $before = ConvertTo-Json $previous[$key] -Depth 20 -Compress
        $after = ConvertTo-Json $settings[$key] -Depth 20 -Compress
        if ($before -ne $after) { return $false }
    }
    $beforeOverrides = ConvertTo-Json $previous.screenshareGameOverrides -Depth 20 -Compress
    $afterOverrides = ConvertTo-Json $settings.screenshareGameOverrides -Depth 20 -Compress
    return (
        ([string]$previous.screenshareCaptureMode -ne [string]$settings.screenshareCaptureMode) -or
        ([string]$previous.screenshareGameWindow -ne [string]$settings.screenshareGameWindow) -or
        ([bool]$previous.screenshareAutoGameKeepFocused -ne [bool]$settings.screenshareAutoGameKeepFocused) -or
        ([double]$previous.screenshareSwitchDelaySeconds -ne [double]$settings.screenshareSwitchDelaySeconds) -or
        ($beforeOverrides -ne $afterOverrides)
    )
}

function Test-ReplayKitOnlySleepOverrideChanged([hashtable]$previous, [hashtable]$settings) {
    foreach ($key in (Get-DefaultReplayKitSettings).Keys) {
        if ($key -eq 'allowSleepWhileActive') { continue }
        $before = ConvertTo-Json $previous[$key] -Depth 20 -Compress
        $after = ConvertTo-Json $settings[$key] -Depth 20 -Compress
        if ($before -ne $after) { return $false }
    }
    return ([bool]$previous.allowSleepWhileActive -ne [bool]$settings.allowSleepWhileActive)
}

function Save-ReplayKitOverlayPreviewFromRequest([string]$body) {
    return Invoke-ReplayKitOverlayPreviewFromRequest $body 'preview'
}

function Save-ReplayKitSettingsFromRequest([string]$body, [bool]$restartObs = $false) {
    if ([string]::IsNullOrWhiteSpace($body)) {
        throw 'Missing settings body.'
    }
    $incoming = ConvertTo-PlainHash (ConvertFrom-Json $body)
    $current = Read-ReplayKitSettings
    $previous = Normalize-ReplayKitSettings $current
    foreach ($k in $incoming.Keys) {
        if (-not $current.ContainsKey($k)) {
            throw "Unknown setting: $k"
        }
        $current[$k] = $incoming[$k]
    }
    $settings = Normalize-ReplayKitSettings $current
    # takes effect on this already-running helper the moment settings are saved, not just after the next reload -- Write-Log gates on this flag on every call, so flipping it here is what actualy lets someone enable logging, reproduce a bug, and have it show up without restarting obs.
    $script:State.LogEnabled = [bool]$settings.debugLoggingEnabled
    $hotkeyRelease = Ensure-ReplayKitHotkeyCaptureReleased $settings
    if (-not (Test-ReplayKitIncomingSettingsChanged $incoming $previous $settings)) {
        if (Test-ReplayKitOverlayColorRequest $incoming) {
            $live = Apply-ReplayKitOverlayOpacityForStyleLive $settings
            return @{
                ok = $true
                settings = $settings
                applied = @($hotkeyRelease.applied) + @($live.applied)
                warnings = @($hotkeyRelease.warnings) + @($live.warnings)
                restartRequired = $false
                restartReason = ''
            }
        }
        return @{
            ok = $true
            settings = $settings
            applied = $hotkeyRelease.applied
            warnings = $hotkeyRelease.warnings
            restartRequired = $false
            restartReason = ''
        }
    }
    if (Test-ReplayKitScreenshareCaptureOnlyRequest $incoming) {
        Write-ReplayKitSettings $settings
        $preset = Get-ReplayKitPresetSpec ([string]$settings.recordingPreset)
        $live = Apply-ReplayKitScreenshareCaptureLive $settings $preset
        return @{
            ok = $true
            settings = $settings
            applied = @($hotkeyRelease.applied) + @($live.applied)
            warnings = @($hotkeyRelease.warnings) + @($live.warnings)
            restartRequired = $false
            restartReason = ''
        }
    }
    if (Test-ReplayKitDiscordOutputOnlyRequest $incoming) {
        $live = Apply-ReplayKitDiscordOutputLive $settings
        if (-not [bool]$live.ok) {
            return @{
                ok = $false
                settings = $previous
                applied = $live.applied
                warnings = $live.warnings
                message = $live.message
                restartRequired = $false
                restartReason = ''
            }
        }
        Write-ReplayKitSettings $settings
        return @{
            ok = $true
            settings = $settings
            applied = @($hotkeyRelease.applied) + @($live.applied)
            warnings = @($hotkeyRelease.warnings) + @($live.warnings)
            restartRequired = $false
            restartReason = ''
        }
    }
    Write-ReplayKitSettings $settings
    if (Test-ReplayKitOnlySleepOverrideChanged $previous $settings) {
        $live = Apply-ReplayKitSleepOverrideSetting ([bool]$settings.allowSleepWhileActive)
        return @{
            ok = $true
            settings = $settings
            applied = @($hotkeyRelease.applied) + @($live.applied)
            warnings = @($hotkeyRelease.warnings) + @($live.warnings)
            restartRequired = $false
            restartReason = ''
        }
    }
    if (Test-ReplayKitOnlyMotionBlurChanged $previous $settings) {
        $live = Apply-ReplayKitMotionBlurLive $settings
        return @{
            ok = $true
            settings = $settings
            applied = @($hotkeyRelease.applied) + @($live.applied)
            warnings = @($hotkeyRelease.warnings) + @($live.warnings)
            restartRequired = $false
            restartReason = ''
        }
    }
    if (Test-ReplayKitOnlyOverlayVisualChanged $previous $settings) {
        $live = Apply-ReplayKitOverlayVisualSettingsLive $previous $settings
        return @{
            ok = $true
            settings = $settings
            applied = @($hotkeyRelease.applied) + @($live.applied)
            warnings = @($hotkeyRelease.warnings) + @($live.warnings)
            restartRequired = $false
            restartReason = ''
        }
    }
    if (Test-ReplayKitOnlyScreenshareCaptureChanged $previous $settings) {
        $preset = Get-ReplayKitPresetSpec ([string]$settings.recordingPreset)
        $live = Apply-ReplayKitScreenshareCaptureLive $settings $preset
        return @{
            ok = $true
            settings = $settings
            applied = @($hotkeyRelease.applied) + @($live.applied)
            warnings = @($hotkeyRelease.warnings) + @($live.warnings)
            restartRequired = $false
            restartReason = ''
        }
    }
    $overlayStyleChanged = [string]$previous.overlayStyle -ne [string]$settings.overlayStyle
    $overlayGeometryChanged = ([string]$previous.recordingPreset -ne [string]$settings.recordingPreset) -or
        ([int]$previous.overlayOpacity -ne [int]$settings.overlayOpacity) -or
        ([int]$previous.overlayScale -ne [int]$settings.overlayScale) -or
        ([double]$previous.overlayHueShift -ne [double]$settings.overlayHueShift) -or
        ([string]$previous.overlayColorMultiply -ne [string]$settings.overlayColorMultiply) -or
        ([string]$previous.overlayColorAdd -ne [string]$settings.overlayColorAdd)
    $motionBlurChanged = ([bool]$previous.motionBlurEnabled -ne [bool]$settings.motionBlurEnabled) -or
        ([double]$previous.motionBlurStrength -ne [double]$settings.motionBlurStrength)
    $applyOverlay = $overlayStyleChanged -or $overlayGeometryChanged
    $recreateBongo = $overlayStyleChanged -and [string]$settings.overlayStyle -eq 'bongo_cat'
    $restartObs = Test-ReplayKitRestartRequired $previous $settings
    $applyVideoSettings = Test-ReplayKitRuntimeVideoSettingsChanged $previous $settings
    $applyReplayBufferOutput = Test-ReplayKitReplayBufferOutputChanged $previous $settings
    $applyRuntimeOutputs = $restartObs -or $applyVideoSettings -or $applyReplayBufferOutput
    $live = Apply-ReplayKitLiveSettings $settings $restartObs $applyOverlay $recreateBongo $motionBlurChanged $applyRuntimeOutputs $applyVideoSettings $applyReplayBufferOutput
    return @{
        ok = $true
        settings = $settings
        applied = @($hotkeyRelease.applied) + @($live.applied)
        warnings = @($hotkeyRelease.warnings) + @($live.warnings)
        restartRequired = $live.restartRequired
        restartReason = $live.restartReason
    }
}

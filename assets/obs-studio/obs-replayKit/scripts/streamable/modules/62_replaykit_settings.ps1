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
        obsStartupEnabled        = $true
        clipNotificationEnabled  = $true
        recordingNotificationEnabled = $true
        clipNotificationSeconds  = 90
        trimPreciseDefault       = $false
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

function Get-EnumSetting($data, [string]$key, [string]$default, [string[]]$allowed) {
    if (-not $data.ContainsKey($key)) { return $default }
    $v = ([string]$data[$key]).Trim()
    if ($allowed -contains $v) { return $v }
    throw "Invalid option for ${key}: $v"
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
    $replaySeconds = Get-IntSetting $data 'replaySeconds' $defaults.replaySeconds 5 600

    return @{
        recordingPreset          = $preset
        compressionMode          = $compression
        codecPreference          = Get-EnumSetting $data 'codecPreference' $defaults.codecPreference @('auto', 'h264', 'h265', 'av1')
        replaySeconds            = $replaySeconds
        clipDir                  = $clipDir
        clipKeybind              = Normalize-ClipKeybind $data.clipKeybind
        recordingKeybind         = Normalize-RecordingKeybind $data.recordingKeybind
        overlayStyle             = Get-EnumSetting $data 'overlayStyle' $defaults.overlayStyle @('input_overlay', 'bongo_cat', 'off')
        obsStartupEnabled        = Get-BoolSetting $data 'obsStartupEnabled' $defaults.obsStartupEnabled
        clipNotificationEnabled  = Get-BoolSetting $data 'clipNotificationEnabled' $defaults.clipNotificationEnabled
        recordingNotificationEnabled = Get-BoolSetting $data 'recordingNotificationEnabled' $defaults.recordingNotificationEnabled
        clipNotificationSeconds  = Get-IntSetting $data 'clipNotificationSeconds' $replaySeconds 1 600
        trimPreciseDefault       = Get-BoolSetting $data 'trimPreciseDefault' $defaults.trimPreciseDefault
    }
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

function Get-ObsRunCommand {
    $obs = $script:OBS_EXE
    if (-not (Test-Path -LiteralPath $obs)) {
        $candidate = Join-Path $env:ProgramFiles 'obs-studio\bin\64bit\obs64.exe'
        if (Test-Path -LiteralPath $candidate) { $obs = $candidate }
    }
    return '"' + $obs + '" --background-color=ff272a33 --default-background-color=ff272a33 --disable-direct-composition-video-overlays'
}

function Set-ObsStartupSetting([bool]$enabled) {
    $runPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $name = 'OBS ReplayKit'
    try {
        if ($enabled) {
            if (-not (Test-Path -LiteralPath $runPath)) {
                [void](New-Item -Path $runPath -Force)
            }
            New-ItemProperty -LiteralPath $runPath -Name $name -Value (Get-ObsRunCommand) -PropertyType String -Force | Out-Null
        } else {
            Remove-ItemProperty -LiteralPath $runPath -Name $name -ErrorAction SilentlyContinue
        }
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
        loggingEnabled = [bool]$script:LOG_ENABLED
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
            return @{
                cqp = 26
                replayBufferMb = 256
                video = @{
                    baseWidth = 1920; baseHeight = 1080
                    outputWidth = 1280; outputHeight = 720
                    fpsNumerator = 30; fpsDenominator = 1
                }
                profile = @(
                    @('Output', 'Mode', 'Advanced'),
                    @('AdvOut', 'RecType', 'Standard'),
                    @('AdvOut', 'RecTracks', '1'),
                    @('Video', 'BaseCX', '1920'),
                    @('Video', 'BaseCY', '1080'),
                    @('Video', 'OutputCX', '1280'),
                    @('Video', 'OutputCY', '720'),
                    @('Video', 'FPSCommon', '30')
                )
            }
        }
        'quality' {
            return @{
                cqp = 20
                replayBufferMb = 1524
                video = @{
                    baseWidth = 2560; baseHeight = 1440
                    outputWidth = 1920; outputHeight = 1080
                    fpsNumerator = 60; fpsDenominator = 1
                }
                profile = @(
                    @('Output', 'Mode', 'Advanced'),
                    @('AdvOut', 'RecType', 'Standard'),
                    @('AdvOut', 'RecTracks', '1'),
                    @('Video', 'BaseCX', '2560'),
                    @('Video', 'BaseCY', '1440'),
                    @('Video', 'OutputCX', '1920'),
                    @('Video', 'OutputCY', '1080'),
                    @('Video', 'FPSCommon', '60')
                )
            }
        }
        default {
            return @{
                cqp = 22
                replayBufferMb = 1024
                video = @{
                    baseWidth = 1920; baseHeight = 1080
                    outputWidth = 1920; outputHeight = 1080
                    fpsNumerator = 60; fpsDenominator = 1
                }
                profile = @(
                    @('Output', 'Mode', 'Advanced'),
                    @('AdvOut', 'RecType', 'Standard'),
                    @('AdvOut', 'RecTracks', '1'),
                    @('Video', 'BaseCX', '1920'),
                    @('Video', 'BaseCY', '1080'),
                    @('Video', 'OutputCX', '1920'),
                    @('Video', 'OutputCY', '1080'),
                    @('Video', 'FPSCommon', '60')
                )
            }
        }
    }
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
        if (@('ada', 'blackwell') -contains $gen) {
            $candidates['av1'] = @{ id = 'obs_nvenc_av1_tex'; codec = 'av1'; kind = 'nvenc_av1'; label = 'NVENC AV1' }
        }
    } elseif ($null -ne $gpu -and $gpu.Vendor -eq 'amd') {
        $candidates['h264'] = @{ id = 'h264_amf'; codec = 'h264'; kind = 'amf_h264'; label = 'AMF H.264' }
        if (@('polaris', 'vega', 'rdna1', 'rdna2', 'rdna3', 'rdna4') -contains $gen) {
            $candidates['h265'] = @{ id = 'h265_amf'; codec = 'h265'; kind = 'amf_h265'; label = 'AMF HEVC' }
        }
        if (@('rdna3', 'rdna4') -contains $gen) {
            $candidates['av1'] = @{ id = 'av1_amf'; codec = 'av1'; kind = 'amf_av1'; label = 'AMF AV1' }
        }
    } elseif ($null -ne $gpu -and $gpu.Vendor -eq 'intel') {
        $candidates['h264'] = @{ id = 'obs_qsv11_h264'; codec = 'h264'; kind = 'qsv_h264'; label = 'Quick Sync H.264' }
        if (@('skylake', 'kaby_lake', 'ice_lake', 'tiger_lake', 'alder_lake', 'arc') -contains $gen) {
            $candidates['h265'] = @{ id = 'obs_qsv11_hevc'; codec = 'h265'; kind = 'qsv_h265'; label = 'Quick Sync HEVC' }
        }
        if ($gen -eq 'arc') {
            $candidates['av1'] = @{ id = 'obs_qsv11_av1'; codec = 'av1'; kind = 'qsv_av1'; label = 'Quick Sync AV1' }
        }
    }
    $candidates['software'] = @{ id = 'obs_x264'; codec = 'h264'; kind = 'x264'; label = 'x264 software' }

    $pref = [string]$settings.codecPreference
    $order = switch ($pref) {
        'av1'  { @('av1', 'h265', 'h264', 'software') }
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
    $outputSettings['max_size_mb'] = [int]$preset.replayBufferMb
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

function Set-ReplayKitSceneItemTransform([string]$sceneName, $item, [hashtable]$transform) {
    if ($null -eq $item) { return @{ ok = $true; skipped = $true } }
    return Invoke-ObsWebSocketRequest 'SetSceneItemTransform' @{
        sceneName = $sceneName
        sceneItemId = [int]$item.sceneItemId
        sceneItemTransform = $transform
    } 3000
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
    if (-not $created.ok) { return $created }
    return @{ ok = $true; item = [pscustomobject]@{ sceneItemId = [int]$created.data.sceneItemId; sourceName = $name } }
}

function Get-ReplayKitInputOverlayTransform([string]$name, [hashtable]$preset) {
    $baseH = [double]$preset.video.baseHeight
    $scale = $baseH / 1440.0
    if ($name -eq 'Mouse Overlay') {
        return @{
            positionX = 450.0 * $scale
            positionY = 1099.0 * $scale
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
        positionX = 19.0 * $scale
        positionY = 1099.0 * $scale
        scaleX = 0.7395833134651184 * $scale
        scaleY = 0.7388888597488403 * $scale
        rotation = 0.0
        alignment = 5
        boundsType = 'OBS_BOUNDS_NONE'
        boundsAlignment = 0
        cropToBounds = $false
    }
}

function Get-ReplayKitBongoTransform([hashtable]$preset) {
    $canvasW = [double]$preset.video.baseWidth
    $canvasH = [double]$preset.video.baseHeight
    $ratio = $canvasH / 1080.0
    $x = ($canvasW / 2.0) + (-1.7777777777777777 * $canvasH / 2.0)
    $y = ($canvasH / 2.0) + (0.47962962962962963 * $canvasH / 2.0)
    if ([Math]::Abs($x) -lt 0.001) { $x = 0.0 }
    if ([Math]::Abs($y) -lt 0.001) { $y = 0.0 }
    return @{
        positionX = $x
        positionY = $y
        scaleX = 0.3669433891773224 * $ratio
        scaleY = 0.3662109375 * $ratio
        rotation = 0.0
        alignment = 5
        boundsType = 'OBS_BOUNDS_NONE'
        boundsAlignment = 0
        cropToBounds = $false
    }
}

function New-ReplayKitJsonObject {
    return ,[System.Collections.Generic.Dictionary[string, object]]::new()
}

function Set-ReplayKitJsonValue($obj, [string]$key, $value) {
    if ($obj.ContainsKey($key)) { $obj[$key] = $value }
    else { $obj.Add($key, $value) }
}

function Get-ReplayKitJsonValue($obj, [string]$key, $default = $null) {
    if ($null -ne $obj -and $obj.ContainsKey($key)) { return ,$obj[$key] }
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
    foreach ($item in @($items)) { [void]$list.Add($item) }
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

function Set-ReplayKitBongoSceneItemJson($item, [string]$uuid, [hashtable]$preset, [bool]$visible) {
    $canvasW = [double]$preset.video.baseWidth
    $canvasH = [double]$preset.video.baseHeight
    $transform = Get-ReplayKitBongoTransform $preset
    Set-ReplayKitSceneItemBaseJson $item 'Bongo Cat Overlay' $visible
    Set-ReplayKitJsonValue $item 'source_uuid' $uuid
    Set-ReplayKitJsonValue $item 'scale_ref' (New-ReplayKitJsonPoint 1280.0 768.0)
    Set-ReplayKitJsonValue $item 'pos' (New-ReplayKitJsonPoint ([double]$transform.positionX) ([double]$transform.positionY))
    Set-ReplayKitJsonValue $item 'pos_rel' (New-ReplayKitJsonPoint ((([double]$transform.positionX - $canvasW / 2.0) / ($canvasH / 2.0))) ((([double]$transform.positionY - $canvasH / 2.0) / ($canvasH / 2.0))))
    Set-ReplayKitJsonValue $item 'scale' (New-ReplayKitJsonPoint ([double]$transform.scaleX) ([double]$transform.scaleY))
    Set-ReplayKitJsonValue $item 'scale_rel' (New-ReplayKitJsonPoint (([double]$transform.scaleX * 0.7111110858433616)) (([double]$transform.scaleY * 0.711111083984375)))
    Set-ReplayKitJsonValue $item 'group_item_backup' $false
}

function Set-ReplayKitInputOverlaySceneItemJson($item, [string]$name, [hashtable]$preset, [bool]$visible, [string]$sourceUuid = '') {
    $canvasW = [double]$preset.video.baseWidth
    $canvasH = [double]$preset.video.baseHeight
    $transform = Get-ReplayKitInputOverlayTransform $name $preset
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

        $overlayStyle = [string]$settings.overlayStyle
        $useInputOverlay = $overlayStyle -eq 'input_overlay'
        $useBongo = $overlayStyle -eq 'bongo_cat'
        $inputSources = @{}

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
                $inputSources[[string]$spec.name] = @{ uuid = $inputUuid }
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
        if ($null -eq $bongoSource) {
            $bongoSource = New-ReplayKitBongoSourceJson ([guid]::NewGuid().ToString())
            [void]$sources.Add($bongoSource)
        }
        $bongoUuid = [string](Get-ReplayKitJsonValue $bongoSource 'uuid' '')
        if ([string]::IsNullOrWhiteSpace($bongoUuid)) {
            $bongoUuid = [guid]::NewGuid().ToString()
        }
        Set-ReplayKitBongoSourceJson $bongoSource $bongoUuid

        foreach ($source in @($sources)) {
            if ([string](Get-ReplayKitJsonValue $source 'id' '') -ne 'scene') { continue }
            $sceneSettings = Get-ReplayKitJsonValue $source 'settings'
            if ($null -eq $sceneSettings) { continue }
            $items = Get-ReplayKitJsonValue $sceneSettings 'items'
            if ($null -eq $items) { continue }
            $items = ConvertTo-ReplayKitJsonList $items
            Set-ReplayKitJsonValue $sceneSettings 'items' $items

            $foundBongoItem = $false
            $foundInputItems = @{ 'WASD Overlay' = $false; 'Mouse Overlay' = $false }
            foreach ($item in @($items)) {
                $name = [string](Get-ReplayKitJsonValue $item 'name' '')
                $sourceUuid = [string](Get-ReplayKitJsonValue $item 'source_uuid' '')
                if ($name -eq 'WASD Overlay' -or $name -eq 'Mouse Overlay') {
                    $inputUuid = ''
                    if ($inputSources.ContainsKey($name)) { $inputUuid = [string]$inputSources[$name].uuid }
                    Set-ReplayKitInputOverlaySceneItemJson $item $name $preset $useInputOverlay $inputUuid
                    $foundInputItems[$name] = $true
                } elseif ($name -eq 'Group') {
                    Set-ReplayKitJsonValue $item 'visible' $useInputOverlay
                } elseif ($name -eq 'Bongo Cat Overlay' -or $sourceUuid -eq $bongoUuid) {
                    Set-ReplayKitBongoSceneItemJson $item $bongoUuid $preset $useBongo
                    $foundBongoItem = $true
                }
            }

            if ($useBongo -and -not $foundBongoItem) {
                $newItem = New-ReplayKitJsonObject
                Set-ReplayKitJsonValue $newItem 'id' (Get-ReplayKitNextJsonSceneItemId $items)
                Set-ReplayKitBongoSceneItemJson $newItem $bongoUuid $preset $true
                [void]$items.Add($newItem)
                Set-ReplayKitJsonValue $sceneSettings 'id_counter' (Get-ReplayKitNextJsonSceneItemId $items)
            }
            if ($useInputOverlay) {
                foreach ($name in @('WASD Overlay', 'Mouse Overlay')) {
                    if ([bool]$foundInputItems[$name]) { continue }
                    if (-not $inputSources.ContainsKey($name)) { throw "Input overlay source was not prepared for $name." }
                    $newItem = New-ReplayKitJsonObject
                    Set-ReplayKitJsonValue $newItem 'id' (Get-ReplayKitNextJsonSceneItemId $items)
                    Set-ReplayKitInputOverlaySceneItemJson $newItem $name $preset $true ([string]$inputSources[$name].uuid)
                    Set-ReplayKitJsonValue $newItem 'group_item_backup' $false
                    [void]$items.Add($newItem)
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
    if ($bongoEnabled -and $null -ne $bongo) {
        $rTransform = Set-ReplayKitSceneItemTransform $sceneName $bongo (Get-ReplayKitBongoTransform $preset)
        if (-not $rTransform.ok) { $warnings += "Could not position Bongo Cat overlay: $($rTransform.message)" }
    }

    if ([string]$settings.overlayStyle -eq 'input_overlay') {
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
                $transform = Get-ReplayKitInputOverlayTransform ([string]$entry.name) $preset
                $rTransform = Set-ReplayKitSceneItemTransform $sceneName $created.item $transform
                if (-not $rTransform.ok) { $warnings += "Could not position $($entry.name): $($rTransform.message)" }
            }
        }
    }

    return @{ ok = ($warnings.Count -eq 0); warnings = $warnings }
}

function Apply-ReplayKitRuntimeOutputsLive([hashtable]$settings, [hashtable]$preset, [bool]$restartObs) {
    $warnings = @()
    $applied = @()
    $record = Stop-ObsOutputIfActive 'GetRecordStatus' 'StopRecord' 'recording'
    $replay = Stop-ObsOutputIfActive 'GetReplayBufferStatus' 'StopReplayBuffer' 'replay buffer'
    $vcam = Stop-ObsOutputIfActive 'GetVirtualCamStatus' 'StopVirtualCam' 'virtual camera'

    foreach ($state in @($record, $replay, $vcam)) {
        if ($state.warning) { $warnings += $state.warning }
    }

    if ($record.ok -and $replay.ok -and $vcam.ok) {
        $video = Set-ReplayKitVideoSettingsLive $preset
        if ($video.ok) { $applied += 'OBS video format' }
        else { $warnings += "OBS video settings were saved, but live apply failed: $($video.message)" }

        $rb = Set-ReplayKitReplayBufferOutputLive $settings $preset
        if ($rb.ok) { $applied += 'OBS replay buffer output' }
        else { $warnings += "OBS replay buffer settings were saved, but live apply failed: $($rb.message)" }
    } else {
        $warnings += 'OBS outputs were not stopped safely, so video/output live changes were not applied.'
    }

    $outputs = @(
        @{ state = $vcam; request = 'StartVirtualCam'; label = 'virtual camera' },
        @{ state = $replay; request = 'StartReplayBuffer'; label = 'replay buffer' },
        @{ state = $record; request = 'StartRecord'; label = 'recording' }
    )

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

function Set-ReplayKitHotkeyCapture([bool]$active) {
    if (-not (Get-Variable -Name ReplayKitHotkeyCaptureActive -Scope Script -ErrorAction SilentlyContinue)) {
        $script:ReplayKitHotkeyCaptureActive = $false
        $script:ReplayKitHotkeyCaptureRestore = $null
    }

    if ($active) {
        if (-not $script:ReplayKitHotkeyCaptureActive) {
            $settings = Read-ReplayKitSettings
            $recordingRestore = Get-RecordingHotkeyRestore $settings
            if (-not $recordingRestore.ok) { return $recordingRestore }
            $script:ReplayKitHotkeyCaptureRestore = @{
                clip      = Convert-ClipKeybindToBasicIni $settings.clipKeybind
                recordingStart = $recordingRestore.start
                recordingStop  = $recordingRestore.stop
            }
            $script:ReplayKitHotkeyCaptureActive = $true
        }
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

    if (-not $script:ReplayKitHotkeyCaptureActive) {
        return @{ ok = $true; active = $false }
    }

    $restore = $script:ReplayKitHotkeyCaptureRestore
    if ($null -eq $restore) {
        $settings = Read-ReplayKitSettings
        $recordingRestore = Get-RecordingHotkeyRestore $settings
        if (-not $recordingRestore.ok) { return $recordingRestore }
        $restore = @{
            clip      = Convert-ClipKeybindToBasicIni $settings.clipKeybind
            recordingStart = $recordingRestore.start
            recordingStop  = $recordingRestore.stop
        }
    }
    $r1 = Set-ReplayBufferHotkeyJson ([string]$restore.clip)
    $r2 = Set-RecordingHotkeyPairJson ([string]$restore.recordingStart) ([string]$restore.recordingStop)
    $script:ReplayKitHotkeyCaptureActive = $false
    $script:ReplayKitHotkeyCaptureRestore = $null
    $errors = @()
    if (-not $r1.ok) { $errors += $r1.message }
    if (-not $r2.ok) { $errors += $r2.message }
    if ($errors.Count -gt 0) {
        return @{ ok = $false; message = ($errors -join '; ') }
    }
    return @{ ok = $true; active = $false }
}

function Apply-ReplayKitLiveSettings([hashtable]$settings, [bool]$restartObs = $false, [bool]$applyOverlay = $true, [bool]$recreateBongo = $true) {
    $warnings = @()
    $applied = @()
    $preset = Get-ReplayKitPresetSpec ([string]$settings.recordingPreset)
    $encoder = Get-ReplayKitEncoderSpec $settings $preset
    if ($encoder.warning) { $warnings += [string]$encoder.warning }

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

    $profileUpdates = @()
    foreach ($u in $preset.profile) { $profileUpdates += ,$u }
    $profileUpdates += @(
        @('AdvOut', 'RecRB', 'true'),
        @('AdvOut', 'RecRBTime', [string][int]$settings.replaySeconds),
        @('AdvOut', 'RecRBSize', [string][int]$preset.replayBufferMb),
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

    $outputs = Apply-ReplayKitRuntimeOutputsLive $settings $preset $restartObs
    $applied += $outputs.applied
    $warnings += $outputs.warnings

    if ($applyOverlay) {
        if ($restartObs) {
            $overlayFile = Set-ReplayKitOverlaySceneFile $settings $preset
            if ($overlayFile.ok) { $applied += 'OBS overlay scene file' }
            else { $warnings += "Overlay setting was saved, but the OBS scene file could not be prepared for restart: $($overlayFile.message)" }
        } else {
            $overlay = Apply-ReplayKitOverlayLive $settings $preset $recreateBongo
            if ($overlay.ok) { $applied += 'OBS overlay' }
            else { $warnings += $overlay.warnings }
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
    return (
        $origin -eq "http://127.0.0.1:$port" -or
        $origin -eq "http://localhost:$port"
    )
}

function Get-ReplayKitSettingsPayload {
    Load-Config
    $settings = Read-ReplayKitSettings
    $settings = Sync-HotkeysFromObs $settings
    if ([string]::IsNullOrWhiteSpace([string]$settings.clipDir) -and
        -not [string]::IsNullOrWhiteSpace([string]$script:State.Config.clipDir)) {
        $settings.clipDir = Resolve-ClipDirSetting ([string]$script:State.Config.clipDir)
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
                @{ value = 'h265'; label = 'HEVC'; blurb = 'Smaller files on modern GPUs.' },
                @{ value = 'av1'; label = 'AV1'; blurb = 'Best compression when the GPU supports it.' }
            )
            overlays = @(
                @{ value = 'input_overlay'; label = 'WASD / mouse'; blurb = 'Simple keyboard and mouse overlay.' },
                @{ value = 'bongo_cat'; label = 'Bongo Cat'; blurb = 'Animated keyboard and mouse overlay.' },
                @{ value = 'off'; label = 'Off'; blurb = 'No input overlay in the OBS scene.' }
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
    foreach ($key in @('recordingPreset', 'compressionMode', 'codecPreference', 'replaySeconds')) {
        if ([string]$previous[$key] -ne [string]$settings[$key]) { return $true }
    }
    return ([string]$previous.overlayStyle -ne [string]$settings.overlayStyle -and [string]$settings.overlayStyle -eq 'bongo_cat')
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
    Write-ReplayKitSettings $settings
    $overlayStyleChanged = [string]$previous.overlayStyle -ne [string]$settings.overlayStyle
    $overlayGeometryChanged = [string]$previous.recordingPreset -ne [string]$settings.recordingPreset
    $applyOverlay = $overlayStyleChanged -or $overlayGeometryChanged
    $recreateBongo = $overlayStyleChanged -and [string]$settings.overlayStyle -eq 'bongo_cat'
    $restartObs = Test-ReplayKitRestartRequired $previous $settings
    $live = Apply-ReplayKitLiveSettings $settings $restartObs $applyOverlay $recreateBongo
    $script:ReplayKitHotkeyCaptureActive = $false
    $script:ReplayKitHotkeyCaptureRestore = $null
    return @{
        ok = $true
        settings = $settings
        applied = $live.applied
        warnings = $live.warnings
        restartRequired = $live.restartRequired
        restartReason = $live.restartReason
    }
}

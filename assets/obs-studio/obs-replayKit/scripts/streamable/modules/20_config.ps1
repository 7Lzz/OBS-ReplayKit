# logging -- disabled by defualt for production. streamable_upload.lua writes loggingenabled into the shared helper config, so one constant controls the lua entrypoint, this helper, upload child, and compression child logging.
function Get-LogPath([string]$area) {
    switch ($area) {
        'upload'   { return $script:UPLOAD_LOG_PATH }
        'compress' { return $script:COMPRESS_LOG_PATH }
        default    { return $script:HELPER_LOG_PATH }
    }
}

function Write-Log([string]$msg, [string]$area = 'helper', [string]$requestId = '') {
    if (-not $script:LOG_ENABLED) { return }
    try {
        if (-not (Test-Path -LiteralPath $script:LOG_DIR)) {
            [void](New-Item -ItemType Directory -Path $script:LOG_DIR -Force)
        }
        $rid = if ($requestId) { " request=$requestId" } else { '' }
        $line = '[{0}] area={1}{2} {3}' -f (Get-Date -Format 'o'), $area, $rid, $msg
        Add-Content -LiteralPath (Get-LogPath $area) -Value $line -ErrorAction SilentlyContinue
    } catch {}
}

# utf-8 (no bom) text writer. ps 5.1s out-file -encoding utf8 emits a bom, which trips up things that read the files later. use this for every text file we write.
$script:Utf8NoBom = New-Object System.Text.UTF8Encoding $false
function Write-Utf8([string]$path, [string]$text) {
    [System.IO.File]::WriteAllText($path, $text, $script:Utf8NoBom)
}

# config loading. re-reads when mtime changes so script_update in obs is picked up without restarting the helper.
function Get-UserProfile {
    if ($env:USERPROFILE) { return $env:USERPROFILE }
    return 'C:\Users\Default'
}

function Get-DefaultClipDir { Join-Path (Get-UserProfile) 'Pictures\Videos' }
# dock html lives under obs replaykits consolidated runtime tree inside obs-studios config root, alongside scripts/ and input-overlay-presets/. prefer $env:appdata becuase windows roaming profiles can redirect appdata to a non-default location; fall back to the userprofile/appdata/roaming default for shells where the env var isnt set.
function Get-DefaultDockDir {
    if ($env:APPDATA) { return Join-Path $env:APPDATA 'obs-studio\obs-replayKit\obs-custom-dock' }
    return Join-Path (Get-UserProfile) 'AppData\Roaming\obs-studio\obs-replayKit\obs-custom-dock'
}

function Resolve-Dir([string]$value, [string]$fallback) {
    if ([string]::IsNullOrWhiteSpace($value)) { return $fallback }
    try { return [System.IO.Path]::GetFullPath($value) } catch { return $fallback }
}

function Get-ScriptDir { $script:State.Config.scriptDir }
function Get-ClipDir   { Resolve-Dir $script:State.Config.clipDir (Get-DefaultClipDir) }
function Get-DockDir   { Get-DefaultDockDir }
function Get-PsHelperPath {
    $val = $script:State.Config.psHelper
    if ([string]::IsNullOrWhiteSpace($val)) {
        return Join-Path (Get-ScriptDir) 'upload_worker.ps1'
    }
    return [System.IO.Path]::GetFullPath($val)
}
function Get-DbPath { Join-Path (Get-ScriptDir) 'clips_db.json' }

function Clear-ClipsCache {
    $script:State.ClipsCacheAt = [DateTime]::MinValue
    $script:State.ClipsCacheSig = ''
    $script:State.ClipsCacheVersion = ''
    $script:State.ClipsCacheJson = ''
}

function Stop-ClipFolderWatcher {
    foreach ($sub in @($script:State.ClipWatcherSubs)) {
        try { Unregister-Event -SubscriptionId $sub.Id -ErrorAction SilentlyContinue } catch {}
        try { Remove-Job -Id $sub.Id -Force -ErrorAction SilentlyContinue } catch {}
    }
    $script:State.ClipWatcherSubs = @()
    if ($script:State.ClipWatcher) {
        try { $script:State.ClipWatcher.EnableRaisingEvents = $false } catch {}
        try { $script:State.ClipWatcher.Dispose() } catch {}
    }
    $script:State.ClipWatcher = $null
    $script:State.ClipWatcherPath = ''
}

function Start-ClipFolderWatcher {
    $root = Resolve-Dir (Get-ClipDir) (Get-DefaultClipDir)
    try { $root = [System.IO.Path]::GetFullPath($root) } catch { return }
    if ($script:State.ClipWatcher -and
        $script:State.ClipWatcherPath -eq $root) {
        return
    }
    Stop-ClipFolderWatcher
    if (-not (Test-Path -LiteralPath $root)) { return }
    try {
        $watcher = New-Object System.IO.FileSystemWatcher
        $watcher.Path = $root
        $watcher.Filter = '*.*'
        $watcher.IncludeSubdirectories = $false
        $watcher.NotifyFilter = [System.IO.NotifyFilters]'FileName, LastWrite, Size, CreationTime'
        $action = {
            $state = $Event.MessageData
            $state.ClipsCacheAt = [DateTime]::MinValue
            $state.ClipsCacheSig = ''
            $state.ClipsCacheVersion = ''
            $state.ClipsCacheJson = ''
        }
        $subs = @()
        foreach ($eventName in @('Created', 'Changed', 'Deleted', 'Renamed')) {
            $subs += Register-ObjectEvent -InputObject $watcher -EventName $eventName -Action $action -MessageData $script:State
        }
        $watcher.EnableRaisingEvents = $true
        $script:State.ClipWatcher = $watcher
        $script:State.ClipWatcherPath = $root
        $script:State.ClipWatcherSubs = $subs
    } catch {
        Stop-ClipFolderWatcher
        Write-Log "Clip watcher disabled for '$root': $($_.Exception.Message)"
    }
}

function Load-Config {
    try {
        $fi = [System.IO.FileInfo]::new($script:State.ConfigPath)
        if (-not $fi.Exists) { return }
        if ($fi.LastWriteTimeUtc -eq $script:State.ConfigMTime) { return }
        $text = [System.IO.File]::ReadAllText($script:State.ConfigPath)
        if ($text.Length -gt 0 -and $text[0] -eq [char]0xFEFF) { $text = $text.Substring(1) }
        $parsed = ConvertFrom-Json $text
        $obj = @{}
        $parsed.PSObject.Properties | ForEach-Object { $obj[$_.Name] = $_.Value }
        if ($obj.ContainsKey('loggingEnabled')) {
            $script:LOG_ENABLED = [bool]$obj.loggingEnabled
        } else {
            $script:LOG_ENABLED = $script:DEFAULT_LOG_ENABLED
        }
        $script:State.Config        = $obj
        $script:State.ConfigMTime   = $fi.LastWriteTimeUtc
        Clear-ClipsCache
        $script:State.ClipsDbCache = $null
        $script:State.ClipsDbCacheSig = ''
        Start-ClipFolderWatcher
    } catch {
        Write-Log "Load-Config failed: $($_.Exception.Message)"
    }
}


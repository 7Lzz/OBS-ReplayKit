# logging -- disabled by defualt for production. helper_bootstrap.lua writes loggingenabled into the shared helper config, so one constant controls the lua entrypoint, this helper, upload child, and compression child logging.
function Get-LogPath([string]$area) {
    switch ($area) {
        'upload'   { return $script:UPLOAD_LOG_PATH }
        'compress' { return $script:COMPRESS_LOG_PATH }
        default    { return $script:HELPER_LOG_PATH }
    }
}

function Write-Log([string]$msg, [string]$area = 'helper', [string]$requestId = '') {
    if (-not $script:State.LogEnabled) { return }
    try {
        if (-not (Test-Path -LiteralPath $script:LOG_DIR)) {
            [void](New-Item -ItemType Directory -Path $script:LOG_DIR -Force)
        }
        $rid = if ($requestId) { " request=$requestId" } else { '' }
        $line = '[{0}] area={1}{2} {3}' -f (Get-Date -Format 'o'), $area, $rid, $msg
        # add-content opens/closes the file per call -- concurrent writers from pool threads can hit a sharing violation without this, and this is the single hottest call site in the helper.
        [System.Threading.Monitor]::Enter($script:State.LogLock)
        try {
            Add-Content -LiteralPath (Get-LogPath $area) -Value $line -ErrorAction SilentlyContinue
        } finally { [System.Threading.Monitor]::Exit($script:State.LogLock) }
    } catch {}
}

# runs once at helper startup, before anything in this session has written a line -- clears out whatever *.log files are left from prior sessions so debug logging left on overnight (or just forgotten about) doesnt slowly fill %TEMP%. gated on its own setting rather than LogEnabled, since old logs from a session where logging WAS on still need cleaning up after the user turns it back off.
function Clear-ReplayKitLogsAtStartup {
    try {
        $settings = Read-ReplayKitSettings
        if (-not [bool]$settings['autoDeleteLogsOnLaunch']) { return }
        if (-not (Test-Path -LiteralPath $script:LOG_DIR)) { return }
        Get-ChildItem -LiteralPath $script:LOG_DIR -File -Filter '*.log' -ErrorAction SilentlyContinue |
            ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue }
    } catch {}
}

# utf-8 (no bom) text writer. ps 5.1s out-file -encoding utf8 emits a bom, which trips up things that read the files later. use this for every text file we write. writes thru a per-call-unique temp file in the scratch dir (not next to $path) then a force-move so a concurrent reader never sees a half-written file and a crash between the write and the move never leaves a stray .tmp sitting next to a real file -- same pattern the upload/compress status files already use.
$script:Utf8NoBom = New-Object System.Text.UTF8Encoding $false
function Write-Utf8([string]$path, [string]$text) {
    if (-not (Test-Path -LiteralPath $script:SCRATCH_DIR)) {
        [void](New-Item -ItemType Directory -Path $script:SCRATCH_DIR -Force)
    }
    $tmp = Join-Path $script:SCRATCH_DIR ([System.IO.Path]::GetFileName($path) + '.' + [System.Guid]::NewGuid().ToString('N') + '.tmp')
    [System.IO.File]::WriteAllText($tmp, $text, $script:Utf8NoBom)
    $destVol = [System.IO.Path]::GetPathRoot($path)
    $tmpVol  = [System.IO.Path]::GetPathRoot($tmp)
    if ($destVol.ToLowerInvariant() -eq $tmpVol.ToLowerInvariant()) {
        Move-Item -LiteralPath $tmp -Destination $path -Force
    } else {
        # different volume -- move-item would silently fall back to copy+delete, which can leave $path half-written on crash. stage on the destinations own volume first so the final replace is always a same-volume atomic rename.
        $sideTemp = Join-Path ([System.IO.Path]::GetDirectoryName($path)) ('.' + [System.IO.Path]::GetFileName($path) + '.' + [System.Guid]::NewGuid().ToString('N') + '.tmp')
        Copy-Item -LiteralPath $tmp -Destination $sideTemp -Force
        Move-Item -LiteralPath $sideTemp -Destination $path -Force
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    }
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
function Get-ClipIndexPath { Join-Path (Get-ScriptDir) 'clips_index.json' }
function Get-ReplayKitUserStateDir {
    if ($env:LOCALAPPDATA) { return Join-Path $env:LOCALAPPDATA 'OBS ReplayKit' }
    return Join-Path (Get-UserProfile) 'AppData\Local\OBS ReplayKit'
}
function Get-ClipUiStatePath { Join-Path (Get-ReplayKitUserStateDir) 'clips_state.json' }

function Clear-ClipsCache {
    [System.Threading.Monitor]::Enter($script:State.ClipsMetaLock)
    try {
        $script:State.ClipsCacheAt = [DateTime]::MinValue
        $script:State.ClipsCacheSig = ''
        $script:State.ClipsCacheVersion = ''
        $script:State.ClipsCacheJson = ''
    } finally { [System.Threading.Monitor]::Exit($script:State.ClipsMetaLock) }
}

function Stop-ClipFolderWatcher {
    [System.Threading.Monitor]::Enter($script:State.ClipsMetaLock)
    try {
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
    } finally { [System.Threading.Monitor]::Exit($script:State.ClipsMetaLock) }
}

function Start-ClipFolderWatcher {
    $root = Resolve-Dir (Get-ClipDir) (Get-DefaultClipDir)
    try { $root = [System.IO.Path]::GetFullPath($root) } catch { return }
    # Stop-ClipFolderWatcher below re-enters the same lock -- Monitor is reentrant per-thread so this nests safely.
    [System.Threading.Monitor]::Enter($script:State.ClipsMetaLock)
    try {
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
                # runs on the powershell eventing threads own context, not the accept loop or a pool runspace -- a real 3rd execution context that touches this state, so it needs the lock same as everything else.
                [System.Threading.Monitor]::Enter($state.ClipsMetaLock)
                try {
                    $state.ClipsCacheAt = [DateTime]::MinValue
                    $state.ClipsCacheSig = ''
                    $state.ClipsCacheVersion = ''
                    $state.ClipsCacheJson = ''
                    $state.ClipIndexRepairQueued = $true
                    $state.ClipIndexRepairAt = [DateTime]::MinValue
                } finally { [System.Threading.Monitor]::Exit($state.ClipsMetaLock) }
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
    } finally { [System.Threading.Monitor]::Exit($script:State.ClipsMetaLock) }
}

function Load-Config {
    try {
        $fi = [System.IO.FileInfo]::new($script:State.ConfigPath)
        if (-not $fi.Exists) { return }
        # cheap common-case check outside the lock -- this runs at the top of every request, and on an unchanged config it should cost one file-info compare, nothing more.
        if ($fi.LastWriteTimeUtc -eq $script:State.ConfigMTime) { return }
        [System.Threading.Monitor]::Enter($script:State.ConfigLock)
        try {
            # re-check now that we hold the lock -- another thread may have already reloaded while we were waiting to get in here.
            if ($fi.LastWriteTimeUtc -eq $script:State.ConfigMTime) { return }
            $text = [System.IO.File]::ReadAllText($script:State.ConfigPath)
            if ($text.Length -gt 0 -and $text[0] -eq [char]0xFEFF) { $text = $text.Substring(1) }
            $parsed = ConvertFrom-Json $text
            $obj = @{}
            $parsed.PSObject.Properties | ForEach-Object { $obj[$_.Name] = $_.Value }
            if ($obj.ContainsKey('loggingEnabled')) {
                $script:State.LogEnabled = [bool]$obj.loggingEnabled
            } else {
                $script:State.LogEnabled = $script:DEFAULT_LOG_ENABLED
            }
            $script:State.Config        = $obj
            $script:State.ConfigMTime   = $fi.LastWriteTimeUtc
            # ConfigLock is always taken before ClipsMetaLock, never the reverse -- see the comment on $script:State in 00_state.ps1.
            [System.Threading.Monitor]::Enter($script:State.ClipsMetaLock)
            try {
                Clear-ClipsCache
                $script:State.ClipsDbCache = $null
                $script:State.ClipsDbCacheSig = ''
                $script:State.ClipIndexCache = $null
                $script:State.ClipIndexCacheSig = ''
                $script:State.ClipIndexRepairQueued = $true
                Start-ClipFolderWatcher
            } finally { [System.Threading.Monitor]::Exit($script:State.ClipsMetaLock) }
        } finally { [System.Threading.Monitor]::Exit($script:State.ConfigLock) }
    } catch {
        Write-Log "Load-Config failed: $($_.Exception.Message)"
    }
}

$script:PooledConstantNames += @('Utf8NoBom')


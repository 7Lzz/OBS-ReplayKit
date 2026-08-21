# runtime constants.
$script:HOST_ADDR          = '127.0.0.1'
$script:DEFAULT_PORT       = 8767
$script:CLIPS_CACHE_MAX_AGE_MS = 60000
$script:CLIPS_PAGE_LIMIT_MAX = 500
$script:MAX_CLIPS          = 0 # 0 = show every clip
# concurrency caps for ffmpeg-bound jobs (compress / trim / upload-with-compress). two limits so we can burst above the steady-state cap when the cpu is currently underutilised: max_concurrent_video_jobs -- the "always allowed" floor. tuned to the hosts logical processor count so a 4-core laptop doesnt queue an 8-core workstation behind the same 2-job wall. max_burst_concurrent_video_jobs -- the dynamic ceiling. get-uploadjob startdecision will exceed the base limit (but not the burst limit) when recent cpu usage is below cpu_burst_threshold_pct. used by "compress all" so a low-utilisation machine can chew thru the queue at higher parallelism without nailing the host into the ground during an active recording.
$__cpu = [Math]::Max(1, [Environment]::ProcessorCount)
# steady-state cap held at 2 regardless of host -- libx264/libx265 already thread well, so two concurrent encodes already pin a typical 8-16 core machine. going higher mostly just spawns more powershell workers without finishing faster, and clutters task manager during compress all.
$script:MAX_CONCURRENT_VIDEO_JOBS       = 2
# burst scales with core count but caps at 4. only used when the host is genuinely underutilised (cpu < threshold) -- e.g. trim, small clip uploads, idle. compress all saturates cpu quickly and the burst gate closes again within a few seconds.
$script:MAX_BURST_CONCURRENT_VIDEO_JOBS = [Math]::Max(2, [Math]::Min(4, [int][Math]::Floor($__cpu / 4)))
$script:CPU_BURST_THRESHOLD_PCT         = 40
Remove-Variable __cpu -ErrorAction SilentlyContinue
# chunk size per range response for /file/ -- kept well under a single-digit ms write on loopback so one chunk never hogs the single-threaded accept loop, while still big enough that keep-alive connections dont need a new chunk every few ms during fast (10x+) playback.
$script:PREVIEW_CHUNK      = 4 * 1024 * 1024
$script:MAX_PREVIEW_STREAM = 2
# every ReplayKit-owned temp path lives under this one folder instead of loose in %TEMP% root, so its all findable/cleanable in one place -- see the Advanced tab "Open log folder" button.
$script:REPLAYKIT_TEMP_ROOT = Join-Path $env:TEMP 'ReplayKit'
$script:THUMB_DIR          = Join-Path $script:REPLAYKIT_TEMP_ROOT 'thumbs'
function Test-ReplayKitObsExe([string]$path) {
    if ([string]::IsNullOrWhiteSpace($path)) { return $false }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $false }
    $name = [System.IO.Path]::GetFileName($path).ToLowerInvariant()
    return @('obs64.exe', 'obs32.exe', 'obs.exe') -contains $name
}

function Get-ReplayKitObsExeFromRoot([string]$root) {
    if ([string]::IsNullOrWhiteSpace($root)) { return '' }
    return (Join-Path $root 'bin\64bit\obs64.exe')
}

function Resolve-ReplayKitObsExe {
    $candidates = New-Object System.Collections.Generic.List[string]
    if ($env:OBS_REPLAYKIT_OBS_EXE) {
        [void]$candidates.Add($env:OBS_REPLAYKIT_OBS_EXE)
    }
    if ($env:OBS_REPLAYKIT_OBS_DIR) {
        [void]$candidates.Add((Get-ReplayKitObsExeFromRoot $env:OBS_REPLAYKIT_OBS_DIR))
    }

    try {
        $self = Get-CimInstance Win32_Process -Filter "ProcessId=$PID" -ErrorAction SilentlyContinue
        if ($self -and $self.ParentProcessId) {
            $parent = Get-CimInstance Win32_Process -Filter "ProcessId=$($self.ParentProcessId)" -ErrorAction SilentlyContinue
            if ($parent -and $parent.ExecutablePath) {
                [void]$candidates.Add([string]$parent.ExecutablePath)
            }
        }
    } catch {}

    foreach ($procName in @('obs64.exe', 'obs32.exe', 'obs.exe')) {
        try {
            Get-CimInstance Win32_Process -Filter "Name='$procName'" -ErrorAction SilentlyContinue |
                Where-Object { $_.ExecutablePath } |
                ForEach-Object { [void]$candidates.Add([string]$_.ExecutablePath) }
        } catch {}
    }

    foreach ($regPath in @(
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\obs64.exe',
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\obs64.exe'
    )) {
        try {
            $key = Get-Item -LiteralPath $regPath -ErrorAction Stop
            $value = [string]$key.GetValue('')
            if ($value) { [void]$candidates.Add(($value -replace ',.*$', '').Trim('"')) }
        } catch {}
    }

    $defaultRoots = New-Object System.Collections.Generic.List[string]
    foreach ($base in @($env:ProgramFiles, $env:ProgramW6432, ${env:ProgramFiles(x86)})) {
        if (-not [string]::IsNullOrWhiteSpace($base)) {
            [void]$defaultRoots.Add((Join-Path $base 'obs-studio'))
        }
    }
    foreach ($root in $defaultRoots) {
        [void]$candidates.Add((Get-ReplayKitObsExeFromRoot $root))
    }

    foreach ($candidate in $candidates) {
        if (Test-ReplayKitObsExe $candidate) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }
    return 'C:\Program Files\obs-studio\bin\64bit\obs64.exe'
}

$script:OBS_EXE            = Resolve-ReplayKitObsExe
$script:OBS_ICON_CACHE     = Join-Path $script:THUMB_DIR 'obs-icon.ico'

# auth-state tunables -- streamables free anonymous flow gives 250 mb / 1 days. signing in (free streamable account) bumps retention to 90 days but keeps the size cap. paid plans lift both. we adjust state.auth.sizecap and retentiondays whenever /me comes back from streamable with new plan info. set sizecap to 0 to mean "no cap".
$script:ANON_SIZE_CAP      = 250 * 1024 * 1024
$script:ANON_RETENTION_DAYS = 1
$script:SIGNED_IN_DEFAULT_RETENTION = 90 # days, free signed-in
$script:AUTH_DIR           = Join-Path $env:LOCALAPPDATA 'OBS Streamable Helper'
$script:AUTH_FILE          = Join-Path $script:AUTH_DIR 'auth.dat'
$script:STREAMABLE_API     = 'https://api-f.streamable.com'
$script:DEFAULT_LOG_ENABLED = $false
# LogEnabled lives on $script:State, not a standalone constant, becuase load-config can flip it on any request and every pooled connection runspace needs to see the same value, not its own copy.
$script:LOG_DIR            = Join-Path $script:REPLAYKIT_TEMP_ROOT 'logs'
$script:HELPER_LOG_PATH    = Join-Path $script:LOG_DIR 'helper.log'
$script:UPLOAD_LOG_PATH    = Join-Path $script:LOG_DIR 'streamable_upload.log'
$script:COMPRESS_LOG_PATH  = Join-Path $script:LOG_DIR 'compress.log'
$script:COMPRESS_TMP_DIR   = Join-Path $script:REPLAYKIT_TEMP_ROOT 'compressed'
# scratch home for the many small short-lived files (curl responses, vss junctions, trim/compress in-flight output, upload status json) that used to sit loose directly in %TEMP%.
$script:SCRATCH_DIR        = Join-Path $script:REPLAYKIT_TEMP_ROOT 'scratch'
$script:ALLOWED_EXTS       = @('.mp4', '.mkv', '.mov')
$script:CONTENT_TYPES      = @{
    '.mp4' = 'video/mp4'
    '.mov' = 'video/quicktime'
    '.mkv' = 'video/x-matroska'
}
# created eagerly at startup, not lazily by whichever consumer happens to write first -- a missing folder used to mean a write silently no-oped (every Write-Log call swallows its own errors) with no sign anything was wrong.
foreach ($__dir in @($script:THUMB_DIR, $script:LOG_DIR, $script:COMPRESS_TMP_DIR, $script:SCRATCH_DIR)) {
    if (-not (Test-Path -LiteralPath $__dir)) {
        try { [void](New-Item -ItemType Directory -Path $__dir -Force) } catch {}
    }
}
Remove-Variable __dir -ErrorAction SilentlyContinue

# state shared with the upload watcher runspace, the connection pool, and the main thread, all via this one synchronized hashtable -- fields whose name ends in Lock guard a specific cluster of fields around them, always [System.Threading.Monitor]::Enter/Exit (try/finally), so every lock in this file looks the same; nesting rule is ConfigLock always acquired before ClipsMetaLock when a caller needs both, never the reverse (Monitor is reentrant per-thread so a locked function calling another locked function on the same lock is safe).
$script:State = [hashtable]::Synchronized(@{
    Config           = @{}
    ConfigMTime      = [DateTime]::MinValue
    ConfigPath       = $ConfigPath
    ConfigLock       = New-Object Object
    LogEnabled       = $script:DEFAULT_LOG_ENABLED
    LogLock          = New-Object Object
    ClipsCacheAt     = [DateTime]::MinValue
    ClipsCacheBody   = $null
    ClipsCacheJson   = ''
    ClipsCacheSig    = ''
    ClipsCacheVersion = ''
    ClipsDbCache      = $null
    ClipsDbCacheSig   = ''
    ClipIndexCache     = $null
    ClipIndexCacheSig  = ''
    ClipIndexRepairRunning = $false
    ClipIndexRepairPid = 0
    ClipIndexRepairQueued = $false
    ClipIndexRepairAt  = [DateTime]::MinValue
    ClipWatcher       = $null
    ClipWatcherPath   = ''
    ClipWatcherSubs   = @()
    # covers ClipsCache*/ClipsDbCache*/ClipIndexCache*/ClipIndexRepair*/ClipWatcher* above and clips_db.json itself -- one lock since a db write and a cache invalidation are meant to be seen as one unit by concurrent readers.
    ClipsMetaLock     = New-Object Object
    TrimKeyframeCache = [hashtable]::Synchronized(@{})
    TrimKeyframeCacheLock = New-Object Object
    TrimKeyframeJobs = [hashtable]::Synchronized(@{})
    TrimKeyframeJobsLock = New-Object Object
    ActivePreviews   = 0
    PreviewLock      = New-Object Object
    Upload           = @{
        state     = 'idle'
        active    = $false
        clipName  = ''
        startedAt = 0
        updatedAt = 0
        url       = ''
        error     = ''
        phase     = ''
        percent   = 0
        requestId = ''
        processId = 0
        statusPath = ''
        tempPath  = ''
        # set true by cancel-activeupload before it kills the worker, and cleared by the upload-result watcher after it sees the killed process exit. without this the watcher would overwrite our "cancelled" status with the generic "upload failed (exit=n)" outcome built from the killed processs exit code.
        cancelRequested = $false
    }
    Jobs             = [hashtable]::Synchronized(@{})
    UploadLock       = New-Object Object
    # cpu sample cache for get-uploadjobstartdecision, only ever touched while that function holds UploadLock -- do not read/write these from anywhere else without adding a real lock first.
    CpuSamplePercent = -1
    CpuSampleAt      = [DateTime]::MinValue
    ThumbQueueLock   = New-Object Object
    Shutdown         = $false
    # read once by the main threads shutdown handling in 90_runtime.ps1, written by route handlers (/settings, /restart-obs-clean) that run on pool threads -- has to live in State or the main thread would never see the write.
    ClearStreamableOnExit = $false
    RestartAfterClean     = $null
    # auth: filled in by load-auth at startup if a saved session exists, or by /login. sizecap=0 means "unlimited" (paid plans); retentiondays applies to new uploads tagged into clips_db.
    Auth             = @{
        signedIn      = $false
        username      = ''
        plan          = ''
        sizeCap       = $script:ANON_SIZE_CAP
        retentionDays = $script:ANON_RETENTION_DAYS
    }
    AuthLock         = New-Object Object
    Capabilities     = @{}
    CapabilitiesAt   = [DateTime]::MinValue
    CapabilitiesLock = New-Object Object
    ReplayKitOverlayPreviewState    = $null
    ReplayKitOverlayPreviewRevision = $null
    OverlayPreviewLock              = New-Object Object
    ReplayKitHotkeyCaptureActive  = $false
    ReplayKitHotkeyCaptureRestore = $null
    HotkeyCaptureLock             = New-Object Object
    ReplayKitDiscordProjectorInspectMode          = $false
    ReplayKitDiscordProjectorTaskbarHiddenApplied = $false
    ProjectorLock                                 = New-Object Object
    # null = no open attempt currently pending. set to a hwnd snapshot the moment an open attempt gives up without finding its window, so a later attempt can tell a late arrival apart from a window that already existed (eg. the users own manually-opened OBS projector) -- see Open-ReplayKitDiscordProjectorIfMissing in 64_discord_projector.ps1.
    ReplayKitDiscordProjectorPendingBaseline      = $null
    # a live ClientWebSocket to obs-websocket, kept open across calls instead of reconnecting (tcp probe + handshake + hello/identify) on every single request -- see Invoke-ObsWebSocketRequest in 61_obs_websocket.ps1. ObsWebSocketPort is the port it was connected against, so a port change in obss own settings invalidates the cache instead of silently talking to the wrong instance. only ever touched with ObsWebSocketLock held.
    ObsWebSocket                                  = $null
    ObsWebSocketPort                              = 0
    ObsWebSocketLock                              = New-Object Object
    ReplayKitStartupUpdateChecked = $false
    # guards /update/apply against a second concurrent call racing the same fixed temp download dir -- shares UpdateCheckLock rather than getting its own, since these are both rare, occasional update-flow actions with no reason to run concurrently with each other either.
    UpdateApplyInProgress          = $false
    UpdateCheckLock                = New-Object Object
})
$script:CookieSourceMissCache = @{}

# returns the size limit for the current auth state. 0 means "unlimited".
function Get-EffectiveUploadCap {
    $cap = [long]$script:State.Auth.sizeCap
    if ($cap -le 0) { return 0 }
    return $cap
}

# returns the retention to tag new uploads with. clips_db filtering still uses each entrys own stored retention_days (so an old anonymous link doesnt suddenly "extend" after you sign in).
function Get-EffectiveRetentionDays {
    $d = [int]$script:State.Auth.retentionDays
    if ($d -le 0) { $d = $script:ANON_RETENTION_DAYS }
    return $d
}

function Get-MaskedIdentity([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) { return 'Signed in' }
    $v = $value.Trim()
    $at = $v.IndexOf('@')
    if ($at -gt 0) {
        $local = $v.Substring(0, $at)
        $domain = $v.Substring($at + 1)
        $first = $local.Substring(0, 1)
        $domParts = $domain.Split('.')
        $domainHint = if ($domParts.Count -gt 1) { $domParts[$domParts.Count - 1] } else { '' }
        $suffix = if ($domainHint) { ".$domainHint" } else { '' }
        return "$first***@***$suffix"
    }
    if ($v.Length -le 2) { return '**' }
    return $v.Substring(0, 1) + ('*' * [Math]::Min(6, $v.Length - 1))
}

# per-entry retention. entries without retention_days use anonymous retention.
function Get-EntryRetentionSec($entry) {
    if ($entry -and $entry.retention_days) {
        return ([int]$entry.retention_days) * 86400
    }
    return $script:ANON_RETENTION_DAYS * 86400
}

# names of read-only $script: constants a pooled connection runspace needs its own copy of, seeded into the connection pools initialsessionstate once at startup (90_runtime.ps1), not per-request -- other modules with their own scattered constants append to this same array at their own file bottom, and a name missing from here reads as $null in every pooled runspace with no error. LOG_ENABLED is deliberately not here, it lives on $script:State.LogEnabled instead.
$script:PooledConstantNames = @(
    'HOST_ADDR', 'DEFAULT_PORT', 'CLIPS_CACHE_MAX_AGE_MS', 'CLIPS_PAGE_LIMIT_MAX', 'MAX_CLIPS',
    'MAX_CONCURRENT_VIDEO_JOBS', 'MAX_BURST_CONCURRENT_VIDEO_JOBS', 'CPU_BURST_THRESHOLD_PCT',
    'PREVIEW_CHUNK', 'MAX_PREVIEW_STREAM', 'REPLAYKIT_TEMP_ROOT', 'THUMB_DIR', 'OBS_EXE', 'OBS_ICON_CACHE',
    'ANON_SIZE_CAP', 'ANON_RETENTION_DAYS', 'SIGNED_IN_DEFAULT_RETENTION', 'AUTH_DIR', 'AUTH_FILE',
    'STREAMABLE_API', 'DEFAULT_LOG_ENABLED', 'LOG_DIR', 'HELPER_LOG_PATH', 'UPLOAD_LOG_PATH',
    'COMPRESS_LOG_PATH', 'COMPRESS_TMP_DIR', 'SCRATCH_DIR', 'ALLOWED_EXTS', 'CONTENT_TYPES',
    'HelperRoot', 'ModuleRoot', 'ParentPid', 'IsAdmin'
)


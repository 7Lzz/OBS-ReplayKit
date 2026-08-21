# filename sanitisation and clip-folder path checks.
function Get-SafeFilename([string]$raw) {
    if (-not $raw) { return $null }
    $decoded = [System.Uri]::UnescapeDataString($raw)
    $name = [System.IO.Path]::GetFileName($decoded).Trim()
    if ([string]::IsNullOrEmpty($name)) { return $null }
    $ext = [System.IO.Path]::GetExtension($name).ToLowerInvariant()
    if ($script:ALLOWED_EXTS -notcontains $ext) { return $null }
    return $name
}

function Get-SafeClipPath([string]$raw) {
    $name = Get-SafeFilename $raw
    if (-not $name) { return $null }
    $root = [System.IO.Path]::GetFullPath((Get-ClipDir))
    $full = [System.IO.Path]::GetFullPath((Join-Path $root $name))
    $prefix = if ($root.EndsWith([IO.Path]::DirectorySeparatorChar)) { $root } else { $root + [IO.Path]::DirectorySeparatorChar }
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { return $null }
    return @{ name = $name; full = $full }
}

# clips db (filename -> {url, uploaded_at}) with retention expiry.
function Get-ClipsDbCacheSignature {
    $parts = New-Object System.Collections.Generic.List[string]
    [void]$parts.Add(('minute:{0}' -f [Math]::Floor([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() / 60)))
    foreach ($p in @((Get-DbPath))) {
        try {
            $fi = [System.IO.FileInfo]::new($p)
            if ($fi.Exists) {
                [void]$parts.Add(("{0}:{1}:{2}" -f $p, $fi.Length, $fi.LastWriteTimeUtc.Ticks))
            } else {
                [void]$parts.Add(("{0}:missing" -f $p))
            }
        } catch {
            [void]$parts.Add(("{0}:error" -f $p))
        }
    }
    return ($parts -join '|')
}

# Read-ClipsDb/Save-ClipsDb lock their whole body, not just the file i/o -- a cache-hit Read-ClipsDb returns the live $script:State.ClipsDbCache object, so two callers touching it at once would be concurrent unsynchronized access to a plain Hashtable, not just a stale-read risk.
function Read-ClipsDb {
    [System.Threading.Monitor]::Enter($script:State.ClipsMetaLock)
    try {
        $sig = Get-ClipsDbCacheSignature
        if ($script:State.ClipsDbCache -and $script:State.ClipsDbCacheSig -eq $sig) {
            return $script:State.ClipsDbCache
        }
        $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
        $db  = @{}
        $path = Get-DbPath
        if (Test-Path -LiteralPath $path) {
            try {
                $raw = [System.IO.File]::ReadAllText($path)
                $parsed = ConvertFrom-Json $raw
                $parsed.PSObject.Properties | ForEach-Object {
                    $v = $_.Value
                    # compress-history cache is independent of streamable upload state -- a clip might be marked as compressed without ever having been uploaded. read those fields first so they survive even when theres no url entry.
                    $cmpEntry = $null
                    if ($v.cmp_mode -ne $null) {
                        $cmpEntry = @{
                            cmp_mode  = [string]$v.cmp_mode
                            cmp_mtime = if ($v.cmp_mtime -ne $null) { [int64]$v.cmp_mtime } else { [int64]0 }
                            cmp_ts    = if ($v.cmp_ts    -ne $null) { [int64]$v.cmp_ts    } else { [int64]0 }
                            cmp_pre   = if ($v.cmp_pre   -ne $null) { [int64]$v.cmp_pre   } else { [int64]0 }
                            # cmp_ver = 2 marks entries written by the current compression pipeline.
                            cmp_ver   = if ($v.cmp_ver   -ne $null) { [int]$v.cmp_ver }   else { [int]0 }
                        }
                    }
                    if ($v.url -and $v.uploaded_at) {
                        $retSec = if ($v.retention_days) {
                            ([int]$v.retention_days) * 86400
                        } else { $script:ANON_RETENTION_DAYS * 86400 }
                        if (($now - [int64]$v.uploaded_at) -lt $retSec) {
                            $entry = @{ url = [string]$v.url; uploaded_at = [int64]$v.uploaded_at }
                            if ($v.retention_days) { $entry.retention_days = [int]$v.retention_days }
                            # preserve transcode-state fields the background poller writes. without this theyd be dropped on read and the dock would never show the "processing on streamable" badge.
                            if ($v.shortcode)               { $entry.shortcode         = [string]$v.shortcode }
                            if ($v.ready -ne $null)         { $entry.ready             = [bool]$v.ready }
                            if ($v.transcode_status -ne $null)  { $entry.transcode_status  = [int]$v.transcode_status }
                            if ($v.transcode_percent -ne $null) { $entry.transcode_percent = [int]$v.transcode_percent }
                            if ($v.failed -ne $null)        { $entry.failed            = [bool]$v.failed }
                            if ($cmpEntry) {
                                $entry.cmp_mode  = $cmpEntry.cmp_mode
                                $entry.cmp_mtime = $cmpEntry.cmp_mtime
                                $entry.cmp_ts    = $cmpEntry.cmp_ts
                                $entry.cmp_pre   = $cmpEntry.cmp_pre
                                $entry.cmp_ver   = $cmpEntry.cmp_ver
                            }
                            $db[$_.Name] = $entry
                        }
                    } elseif ($cmpEntry) {
                        # compress-only entry: clip has never been uploaded but we know its compress history. keep it.
                        $db[$_.Name] = $cmpEntry
                    }
                }
            } catch { Write-Log "Read-ClipsDb error: $($_.Exception.Message)" }
        }
        $script:State.ClipsDbCache = $db
        $script:State.ClipsDbCacheSig = $sig
        return $db
    } finally { [System.Threading.Monitor]::Exit($script:State.ClipsMetaLock) }
}

function Save-ClipsDb($db) {
    [System.Threading.Monitor]::Enter($script:State.ClipsMetaLock)
    try {
        $obj = @{}
        foreach ($k in $db.Keys) { $obj[$k] = $db[$k] }
        Write-Utf8 (Get-DbPath) ((ConvertTo-Json $obj -Depth 4))
        $script:State.ClipsDbCache = $db
        $script:State.ClipsDbCacheSig = Get-ClipsDbCacheSignature
    } finally { [System.Threading.Monitor]::Exit($script:State.ClipsMetaLock) }
}

function Mark-Uploaded([string]$name, [string]$url) {
    # holds the lock across the whole read-mutate-save so a concurrent Mark-Uploaded/Mark-Compressed cant interleave between our Read-ClipsDb and our Save-ClipsDb -- those re-enter the same lock internally, which is safe since Monitor is reentrant per-thread.
    [System.Threading.Monitor]::Enter($script:State.ClipsMetaLock)
    try {
        $db = Read-ClipsDb
        # tag entries with the retention that applied at upload time so filtering doesnt change retroactively when you sign in/out later. anonymous uploads stay 2-day even after you sign in.
        $entry = @{
            url            = $url
            uploaded_at    = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
            retention_days = Get-EffectiveRetentionDays
        }
        # preserve the compress-history cache fields if we already had them. otherwise an upload would wipe the fact that wed already ffprobed this files compress marker.
        if ($db.ContainsKey($name)) {
            $prev = $db[$name]
            if ($prev.cmp_mode -ne $null)  { $entry.cmp_mode  = [string]$prev.cmp_mode }
            if ($prev.cmp_mtime -ne $null) { $entry.cmp_mtime = [int64]$prev.cmp_mtime }
            if ($prev.cmp_ts -ne $null)    { $entry.cmp_ts    = [int64]$prev.cmp_ts }
            if ($prev.cmp_pre -ne $null)   { $entry.cmp_pre   = [int64]$prev.cmp_pre }
            if ($prev.cmp_ver -ne $null)   { $entry.cmp_ver   = [int]$prev.cmp_ver }
        }
        $db[$name] = $entry
        Save-ClipsDb $db
        Clear-ClipsCache
    } finally { [System.Threading.Monitor]::Exit($script:State.ClipsMetaLock) }
}

# called by the compress-overwrite watcher when the encode + atomic replace succeeds. stores the new mode + the freshly-written files mtime as the cache key, plus the timestamp + pre-compress size for ui purposes. survives later /clips polls without re-probing the file.
function Mark-Compressed([string]$name, [string]$mode, [int64]$mtimeTicks, [int64]$preBytes) {
    if ([string]::IsNullOrWhiteSpace($name)) { return }
    if ($mode -ne 'fast' -and $mode -ne 'slow') { return }
    [System.Threading.Monitor]::Enter($script:State.ClipsMetaLock)
    try {
        $db = Read-ClipsDb
        $entry = if ($db.ContainsKey($name)) { $db[$name] } else { @{} }
        $entry.cmp_mode  = $mode
        $entry.cmp_mtime = $mtimeTicks
        $entry.cmp_ts    = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        $entry.cmp_pre   = $preBytes
        # cmp_ver = 2 -- written by the v2 (dynamic encoder / size-guarded) compress pipeline. get-clipslistuncached refuses cache hits without this field, which forces v1-era entries to re-probe their mp4 atom.
        $entry.cmp_ver   = 2
        $db[$name] = $entry
        Save-ClipsDb $db
        Clear-ClipsCache
    } finally { [System.Threading.Monitor]::Exit($script:State.ClipsMetaLock) }
}

function Get-ClipIndexSignature {
    $path = Get-ClipIndexPath
    try {
        $fi = [System.IO.FileInfo]::new($path)
        if ($fi.Exists) {
            return ("{0}:{1}:{2}" -f $path, $fi.Length, $fi.LastWriteTimeUtc.Ticks)
        }
        return ("{0}:missing" -f $path)
    } catch {
        return ("{0}:error" -f $path)
    }
}

function Get-EntryValue($entry, [string]$key) {
    if (-not $entry) { return $null }
    if ($entry -is [System.Collections.IDictionary]) {
        if ($entry.Contains($key)) { return $entry[$key] }
        return $null
    }
    $prop = $entry.PSObject.Properties[$key]
    if ($prop) { return $prop.Value }
    return $null
}

function Read-ClipIndex {
    [System.Threading.Monitor]::Enter($script:State.ClipsMetaLock)
    try {
        $sig = Get-ClipIndexSignature
        if ($script:State.ClipIndexCache -and $script:State.ClipIndexCacheSig -eq $sig) {
            return $script:State.ClipIndexCache
        }
        $map = @{}
        $path = Get-ClipIndexPath
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            try {
                $raw = [System.IO.File]::ReadAllText($path)
                if (-not [string]::IsNullOrWhiteSpace($raw)) {
                    $parsed = ConvertFrom-Json $raw
                    if ($parsed -and $parsed.clips) {
                        $parsed.clips.PSObject.Properties | ForEach-Object {
                            if ($_.Name) { $map[$_.Name] = $_.Value }
                        }
                    }
                }
            } catch {
                Write-Log "Read-ClipIndex error: $($_.Exception.Message)"
            }
        }
        $script:State.ClipIndexCache = $map
        $script:State.ClipIndexCacheSig = $sig
        return $map
    } finally { [System.Threading.Monitor]::Exit($script:State.ClipsMetaLock) }
}

function Test-ClipIndexEntryCurrent($entry, [System.IO.FileInfo]$file) {
    if (-not $entry) { return $false }
    $size = Get-EntryValue $entry 'size'
    $mtimeTicks = Get-EntryValue $entry 'mtimeTicks'
    if ($size -eq $null -or $mtimeTicks -eq $null) { return $false }
    return ([int64]$size -eq [int64]$file.Length -and [int64]$mtimeTicks -eq [int64]$file.LastWriteTimeUtc.Ticks)
}

function Get-CurrentClipIndexEntry([System.IO.FileInfo]$file, [hashtable]$index) {
    if (-not $file -or -not $index -or -not $index.ContainsKey($file.Name)) { return $null }
    $entry = $index[$file.Name]
    if (-not (Test-ClipIndexEntryCurrent $entry $file)) { return $null }
    return $entry
}

function Add-ClipIndexFields([System.Collections.Specialized.OrderedDictionary]$item, $entry) {
    if (-not $item -or -not $entry) { return }
    $duration = Get-EntryValue $entry 'duration'
    if ($duration -ne $null -and [double]$duration -gt 0) {
        $item.duration = [double]$duration
    }
    foreach ($field in @('width', 'height')) {
        $value = Get-EntryValue $entry $field
        if ($value -ne $null -and [int]$value -gt 0) { $item[$field] = [int]$value }
    }
    $fps = Get-EntryValue $entry 'fps'
    if ($fps -ne $null -and [double]$fps -gt 0) { $item.fps = [double]$fps }
    $codec = Get-EntryValue $entry 'codec'
    if (-not [string]::IsNullOrWhiteSpace([string]$codec)) { $item.codec = [string]$codec }
    $tag = Get-EntryValue $entry 'tag'
    if (-not [string]::IsNullOrWhiteSpace([string]$tag)) { $item.tag = [string]$tag }
    $item.indexed = $true
}

function Test-ClipNeedsIndexRepair([System.IO.FileInfo]$file, [hashtable]$index) {
    if (-not $file) { return $false }
    $entry = Get-CurrentClipIndexEntry $file $index
    if (-not $entry) { return $true }
    $duration = Get-EntryValue $entry 'duration'
    $codec = Get-EntryValue $entry 'codec'
    if ($duration -ne $null -and [double]$duration -gt 0 -and -not [string]::IsNullOrWhiteSpace([string]$codec)) { return $false }
    $failedAt = Get-EntryValue $entry 'failedAt'
    if ($failedAt -ne $null) {
        $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
        if (($now - [int64]$failedAt) -lt 21600) { return $false }
    }
    return $true
}

function Resolve-ClipIndexFfprobe {
    try {
        if (Get-Command Find-ToolInClipDirs -ErrorAction SilentlyContinue) {
            return Find-ToolInClipDirs 'ffprobe.exe'
        }
    } catch {}
    try {
        $caps = Get-HelperCapabilities
        if ($caps -and $caps.ffprobe) { return [string]$caps.ffprobe }
    } catch {}
    return ''
}

function Update-ClipIndexRepairState {
    [System.Threading.Monitor]::Enter($script:State.ClipsMetaLock)
    try {
        if (-not $script:State.ClipIndexRepairRunning) { return }
        $pidValue = [int]$script:State.ClipIndexRepairPid
        # Pid <= 0 while Running is true means Start-ClipIndexRepairIfNeeded has claimed the slot but hasnt finished spawning yet (ffprobe resolution can take several seconds cold) -- leave the claim alone here rather than reading "no pid yet" as "not actually running", which would let a second caller spawn a duplicate repair; that same caller always sets a real Pid or resets Running itself if the spawn fails.
        if ($pidValue -le 0) { return }
        $stillRunning = $false
        try {
            $proc = Get-Process -Id $pidValue -ErrorAction SilentlyContinue
            $stillRunning = [bool]$proc
        } catch {}
        if ($stillRunning) { return }
        $script:State.ClipIndexRepairRunning = $false
        $script:State.ClipIndexRepairPid = 0
        $script:State.ClipIndexCache = $null
        $script:State.ClipIndexCacheSig = ''
        Clear-ClipsCache
    } finally { [System.Threading.Monitor]::Exit($script:State.ClipsMetaLock) }
}

# claims ClipIndexRepairRunning under the lock before doing any of the slow work (ffprobe resolution, which can hit Get-HelperCapabilitiess multi-second cold-cache probe, and Process.Start), then does that slow work lock-free so it cant hold up every other ClipsMetaLock user -- resets the claim back to false if we bail out before the process actually starts.
function Start-ClipIndexRepairIfNeeded([object[]]$files, [hashtable]$index) {
    Update-ClipIndexRepairState
    $now = [DateTime]::UtcNow
    $claimed = $false
    [System.Threading.Monitor]::Enter($script:State.ClipsMetaLock)
    try {
        if ($script:State.ClipIndexRepairRunning) { return }

        $needsRepair = [bool]$script:State.ClipIndexRepairQueued
        if (-not $needsRepair) {
            foreach ($file in @($files)) {
                if (Test-ClipNeedsIndexRepair $file $index) {
                    $needsRepair = $true
                    break
                }
            }
        }
        if (-not $needsRepair) { return }

        if (-not $script:State.ClipIndexRepairQueued -and
            (($now - $script:State.ClipIndexRepairAt).TotalSeconds -lt 30)) {
            return
        }

        $script:State.ClipIndexRepairRunning = $true
        $script:State.ClipIndexRepairAt = $now
        $claimed = $true
    } finally { [System.Threading.Monitor]::Exit($script:State.ClipsMetaLock) }

    if (-not $claimed) { return }

    $started = $false
    try {
        $scriptPath = Join-Path (Get-ScriptDir) 'clip_index_worker.ps1'
        if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) { return }
        $ffprobe = Resolve-ClipIndexFfprobe
        if ([string]::IsNullOrWhiteSpace($ffprobe) -or -not (Test-Path -LiteralPath $ffprobe -PathType Leaf)) { return }

        $psExe = "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe"
        if (-not (Test-Path -LiteralPath $psExe -PathType Leaf)) { $psExe = 'powershell.exe' }
        $maxFiles = if ($script:MAX_CLIPS -gt 0) { [Math]::Max($script:MAX_CLIPS, 1) } else { 20000 }
        $args = @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden',
            '-File', $scriptPath,
            '-ClipDir', (Get-ClipDir),
            '-IndexPath', (Get-ClipIndexPath),
            '-Ffprobe', $ffprobe,
            '-AllowedExts', ($script:ALLOWED_EXTS -join ';'),
            '-MaxFiles', [string]$maxFiles
        )
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $psExe
        $psi.Arguments = (($args | ForEach-Object { Quote-Arg ([string]$_) }) -join ' ')
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $psi.WorkingDirectory = Get-ScriptDir
        $proc = [System.Diagnostics.Process]::Start($psi)
        if ($proc) {
            [System.Threading.Monitor]::Enter($script:State.ClipsMetaLock)
            try {
                $script:State.ClipIndexRepairPid = [int]$proc.Id
                $script:State.ClipIndexRepairQueued = $false
            } finally { [System.Threading.Monitor]::Exit($script:State.ClipsMetaLock) }
            $started = $true
        }
    } catch {
        Write-Log "Start-ClipIndexRepairIfNeeded failed: $($_.Exception.Message)"
    } finally {
        if (-not $started) {
            [System.Threading.Monitor]::Enter($script:State.ClipsMetaLock)
            try { $script:State.ClipIndexRepairRunning = $false } finally { [System.Threading.Monitor]::Exit($script:State.ClipsMetaLock) }
        }
    }
}

function Normalize-ClipSort([string]$sort) {
    switch (($sort + '').ToLowerInvariant()) {
        'oldest'   { return 'oldest' }
        'shortest' { return 'shortest' }
        'longest'  { return 'longest' }
        'smallest' { return 'smallest' }
        'biggest'  { return 'biggest' }
        default    { return 'newest' }
    }
}

function Normalize-FavoriteClipName($value) {
    if ($null -eq $value) { return '' }
    $text = ([string]$value).Trim()
    if ([string]::IsNullOrWhiteSpace($text)) { return '' }
    $name = [System.IO.Path]::GetFileName($text).Trim()
    if ([string]::IsNullOrWhiteSpace($name) -or $name.Length -gt 260) { return '' }
    foreach ($ch in [System.IO.Path]::GetInvalidFileNameChars()) {
        if ($name.IndexOf($ch) -ge 0) { return '' }
    }
    $ext = [System.IO.Path]::GetExtension($name).ToLowerInvariant()
    if ($script:ALLOWED_EXTS -notcontains $ext) { return '' }
    return $name
}

function Normalize-FavoriteClipNames($names) {
    $out = New-Object System.Collections.Generic.List[string]
    $seen = @{}
    foreach ($value in @($names)) {
        $name = Normalize-FavoriteClipName $value
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        $key = $name.ToLowerInvariant()
        if ($seen.ContainsKey($key)) { continue }
        $seen[$key] = $true
        [void]$out.Add($name)
        if ($out.Count -ge 10000) { break }
    }
    return @($out)
}

function Read-ClipUiState {
    $state = [ordered]@{
        favorites = @()
        sort      = 'newest'
    }
    $path = Get-ClipUiStatePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $state }
    try {
        $raw = [System.IO.File]::ReadAllText($path)
        if ([string]::IsNullOrWhiteSpace($raw)) { return $state }
        $parsed = ConvertFrom-Json $raw
        $favorites = Get-EntryValue $parsed 'favorites'
        $sort = Get-EntryValue $parsed 'sort'
        $state.favorites = @(Normalize-FavoriteClipNames $favorites)
        $state.sort = Normalize-ClipSort ([string]$sort)
    } catch {
        Write-Log "Read-ClipUiState error: $($_.Exception.Message)"
    }
    return $state
}

function Save-ClipUiState($state) {
    $safe = [ordered]@{
        favorites = @(Normalize-FavoriteClipNames (Get-EntryValue $state 'favorites'))
        sort      = Normalize-ClipSort ([string](Get-EntryValue $state 'sort'))
    }
    $path = Get-ClipUiStatePath
    $dir = [System.IO.Path]::GetDirectoryName($path)
    if (-not (Test-Path -LiteralPath $dir -PathType Container)) {
        [void](New-Item -ItemType Directory -Path $dir -Force)
    }
    Write-Utf8 $path ((ConvertTo-Json $safe -Depth 4))
    return $safe
}

function Get-ClipUiStatePayload {
    $path = Get-ClipUiStatePath
    return @{
        ok     = $true
        exists = (Test-Path -LiteralPath $path -PathType Leaf)
        state  = Read-ClipUiState
    }
}

function Save-ClipUiStateFromJson([string]$body) {
    if ([string]::IsNullOrWhiteSpace($body)) { throw 'Clip state body is required.' }
    $incoming = ConvertFrom-Json $body
    if (-not $incoming) { throw 'Invalid clip state.' }
    return @{
        ok    = $true
        state = Save-ClipUiState $incoming
    }
}

function Get-ClipDurationSortValue($clip) {
    $duration = Get-EntryValue $clip 'duration'
    if ($duration -ne $null -and [double]$duration -gt 0) { return [double]$duration }
    return [double]::PositiveInfinity
}

function Get-ClipDurationKnownRank($clip) {
    $duration = Get-EntryValue $clip 'duration'
    if ($duration -ne $null -and [double]$duration -gt 0) { return 0 }
    return 1
}

function Get-ClipDurationDescendingValue($clip) {
    $duration = Get-EntryValue $clip 'duration'
    if ($duration -ne $null -and [double]$duration -gt 0) { return [double]$duration }
    return 0.0
}

function Sort-ClipsForPage([object[]]$items, [string]$sort) {
    $safeSort = Normalize-ClipSort $sort
    switch ($safeSort) {
        'oldest' {
            return @($items | Sort-Object -Property @{Expression={$_.mtime}}, @{Expression={$_.name}})
        }
        'smallest' {
            return @($items | Sort-Object -Property @{Expression={$_.size}}, @{Expression={$_.mtime}; Descending=$true}, @{Expression={$_.name}})
        }
        'biggest' {
            return @($items | Sort-Object -Property @{Expression={$_.size}; Descending=$true}, @{Expression={$_.mtime}; Descending=$true}, @{Expression={$_.name}})
        }
        'shortest' {
            return @($items | Sort-Object -Property @{Expression={Get-ClipDurationKnownRank $_}}, @{Expression={Get-ClipDurationSortValue $_}}, @{Expression={$_.mtime}; Descending=$true}, @{Expression={$_.name}})
        }
        'longest' {
            return @($items | Sort-Object -Property @{Expression={Get-ClipDurationKnownRank $_}}, @{Expression={Get-ClipDurationDescendingValue $_}; Descending=$true}, @{Expression={$_.mtime}; Descending=$true}, @{Expression={$_.name}})
        }
        default {
            return @($items | Sort-Object -Property @{Expression={$_.mtime}; Descending=$true}, @{Expression={$_.name}})
        }
    }
}

# clip listing -- cached by directory/db signature so unchanged popup polls dont re-enumerate the folder or re-serialize a large json list.
function Get-ClipsCacheSignature([string]$root) {
    $parts = New-Object System.Collections.Generic.List[string]
    try {
        $dir = [System.IO.DirectoryInfo]::new($root)
        if ($dir.Exists) {
            [void]$parts.Add("dir:$($dir.LastWriteTimeUtc.Ticks)")
        } else {
            [void]$parts.Add('dir:missing')
        }
    } catch {
        [void]$parts.Add('dir:error')
    }
    foreach ($p in @((Get-DbPath), (Get-ClipIndexPath))) {
        try {
            $fi = [System.IO.FileInfo]::new($p)
            if ($fi.Exists) {
                [void]$parts.Add(("{0}:{1}:{2}" -f $p, $fi.Length, $fi.LastWriteTimeUtc.Ticks))
            } else {
                [void]$parts.Add(("{0}:missing" -f $p))
            }
        } catch {
            [void]$parts.Add(("{0}:error" -f $p))
        }
    }
    return ($parts -join '|')
}

# holds ClipsMetaLock across the whole enumeration, not just the Read-ClipsDb/Read-ClipIndex calls -- both return live cached objects and this function does many scattered reads into them as it walks the folder, so a concurrent Mark-Uploaded/Save-ClipsDb writing to that same live Hashtable mid-enumeration would be real corruption, not just staleness; only runs on a clips-cache miss, so its not the hot per-poll path.
function Get-ClipsListUncached {
    [System.Threading.Monitor]::Enter($script:State.ClipsMetaLock)
    try {
    $root = Get-ClipDir
    if (-not (Test-Path -LiteralPath $root)) { return @() }
    $db = Read-ClipsDb
    $index = Read-ClipIndex
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $items = New-Object System.Collections.Generic.List[object]
    $eligibleFiles = New-Object System.Collections.Generic.List[object]
    try {
        $files = [System.IO.DirectoryInfo]::new($root).EnumerateFiles()
    } catch {
        return @()
    }
    foreach ($fi in $files) {
        $ext = $fi.Extension.ToLowerInvariant()
        if ($script:ALLOWED_EXTS -notcontains $ext) { continue }
        # skip in-flight worker temp files. trim/compress workers write under %temp%, but cross-volume finalizes briefly stage a _replaykit_finalize_ sidecar in the clip folder. older _compress_tmp_ / _trim_tmp_ names are filtered too in case a previous version of the worker left one behind.
        if ($fi.Name -like '_replaykit_*' -or
            $fi.Name -like '_compress_tmp_*' -or
            $fi.Name -like '_trim_tmp_*') { continue }
        [void]$eligibleFiles.Add($fi)
        $item = [ordered]@{
            name  = $fi.Name
            size  = $fi.Length
            mtime = [int][Math]::Floor(([DateTimeOffset]$fi.LastWriteTimeUtc).ToUnixTimeSeconds())
        }
        $indexEntry = Get-CurrentClipIndexEntry $fi $index
        if ($indexEntry) { Add-ClipIndexFields $item $indexEntry }

        # resolve compression state only from the helper-maintained cache during normal listing. probing every clip with ffprobe makes the clips popup wait seconds on large folders; compression routes still inspect the selected file server-side before modifying it.
        $fileMtimeTicks = $fi.LastWriteTimeUtc.Ticks
        $cmpMode  = ''
        $cmpTs    = [int64]0
        $cmpPre   = [int64]0
        $entry    = if ($db.ContainsKey($fi.Name)) { $db[$fi.Name] } else { $null }
        # cache hit requires both the mtime match and a cmp_ver=2 stamp. without the version check, clips compressed by the v1 pipeline would still report "already compressed" forever.
        $entryVer = 0
        if ($entry -and $entry.cmp_ver -ne $null) { $entryVer = [int]$entry.cmp_ver }
        if ($entry -and $entry.cmp_mtime -ne $null -and
            [int64]$entry.cmp_mtime -eq [int64]$fileMtimeTicks -and
            $entryVer -eq 2) {
            $cmpMode = [string]$entry.cmp_mode
            if ($entry.cmp_ts -ne $null)  { $cmpTs  = [int64]$entry.cmp_ts }
            if ($entry.cmp_pre -ne $null) { $cmpPre = [int64]$entry.cmp_pre }
        }
        if ($cmpMode -eq 'fast' -or $cmpMode -eq 'slow') {
            $item.cmp_mode = $cmpMode
            if ($cmpTs  -gt 0) { $item.cmp_ts  = $cmpTs }
            if ($cmpPre -gt 0) { $item.cmp_pre = $cmpPre }
        }

        if ($entry -and $entry.url -and $entry.uploaded_at) {
            if (($now - [int64]$entry.uploaded_at) -lt (Get-EntryRetentionSec $entry)) {
                $item.streamable_url = $entry.url
                $item.uploaded_at    = [int64]$entry.uploaded_at
                $item.retention_days = if ($entry.retention_days) {
                    [int]$entry.retention_days
                } else { $script:ANON_RETENTION_DAYS }
                # missing transcode-state fields read as ready. we never write $null for these fields, so $null reliably means absent.
                if ($entry.ready -ne $null) {
                    $item.ready = [bool]$entry.ready
                }
                if ($entry.transcode_status -ne $null) {
                    $item.transcode_status = [int]$entry.transcode_status
                }
                if ($entry.transcode_percent -ne $null) {
                    $item.transcode_percent = [int]$entry.transcode_percent
                }
            }
        }
        [void]$items.Add([pscustomobject]$item)
    }
    } finally { [System.Threading.Monitor]::Exit($script:State.ClipsMetaLock) }
    # Start-ClipIndexRepairIfNeeded is deliberately called after ClipsMetaLock is released, not from inside the try above -- Monitor is reentrant per-thread, so a nested Enter/Exit pair does not actually free the lock for other threads, and that functions "claim under lock, spawn the worker outside it" design only holds if callers dont re-wrap it in their own lock; doing so here once silently pinned a whole new powershell.exe + ffprobe.exe launch behind ClipsMetaLock, blocking every other pool thread that needed it for as long as that spawn took.
    Start-ClipIndexRepairIfNeeded -files @($eligibleFiles.ToArray()) -index $index
    $items = $items.ToArray() | Sort-Object -Property @{Expression={$_.mtime}; Descending=$true}, @{Expression={$_.name}}
    if ($script:MAX_CLIPS -gt 0 -and $items.Count -gt $script:MAX_CLIPS) {
        $items = $items[0..($script:MAX_CLIPS - 1)]
    }
    return @($items)
}

function Get-ClipsList {
    $now = [DateTime]::UtcNow
    $root = Get-ClipDir
    $sig = Get-ClipsCacheSignature $root
    if ($script:State.ClipsCacheBody -and
        $script:State.ClipsCacheSig -eq $sig -and
        (($now - $script:State.ClipsCacheAt).TotalMilliseconds -lt $script:CLIPS_CACHE_MAX_AGE_MS)) {
        return $script:State.ClipsCacheBody
    }
    $body = Get-ClipsListUncached
    # the several fields below are meant to be seen together as one cache "generation" -- lock the writes so a concurrent fast-path reader (deliberately lock-free above since its the hot per-poll path) never sees, say, a fresh Body paired with a stale Sig.
    [System.Threading.Monitor]::Enter($script:State.ClipsMetaLock)
    try {
        $script:State.ClipsCacheBody = $body
        $script:State.ClipsCacheJson = ConvertTo-Json -InputObject @($body) -Depth 8 -Compress
        if ([string]::IsNullOrWhiteSpace($script:State.ClipsCacheJson)) {
            $script:State.ClipsCacheJson = '[]'
        }
        $script:State.ClipsCacheSig  = $sig
        $script:State.ClipsCacheVersion = $sig
        $script:State.ClipsCacheAt   = $now
    } finally { [System.Threading.Monitor]::Exit($script:State.ClipsMetaLock) }
    return $body
}

function Get-ClipsListJson {
    [void](Get-ClipsList)
    if ([string]::IsNullOrWhiteSpace($script:State.ClipsCacheJson)) { return '[]' }
    return [string]$script:State.ClipsCacheJson
}

function Get-ClipsPageJson([int]$offset, [int]$limit, [string]$sort = 'newest') {
    $safeSort = Normalize-ClipSort $sort
    $items = @(Sort-ClipsForPage -items @(Get-ClipsList) -sort $safeSort)
    $total = [int]$items.Count
    $safeOffset = [Math]::Max(0, [Math]::Min($offset, $total))
    $safeLimit = if ($limit -gt 0) {
        [Math]::Min($limit, $script:CLIPS_PAGE_LIMIT_MAX)
    } else {
        [Math]::Min([Math]::Max($total, 1), $script:CLIPS_PAGE_LIMIT_MAX)
    }
    $page = @()
    if ($total -gt 0 -and $safeOffset -lt $total) {
        $end = [Math]::Min($total - 1, $safeOffset + $safeLimit - 1)
        $page = @($items[$safeOffset..$end])
    }
    $version = "{0}|sort:{1}" -f [string]$script:State.ClipsCacheVersion, $safeSort
    $payload = [ordered]@{
        version = $version
        total   = $total
        offset  = $safeOffset
        limit   = $safeLimit
        sort    = $safeSort
        indexing = @{
            running = [bool]$script:State.ClipIndexRepairRunning
            queued  = [bool]$script:State.ClipIndexRepairQueued
        }
        clips   = @($page)
    }
    return ConvertTo-Json -InputObject $payload -Depth 8 -Compress
}

function Get-ClipsByNameJson([object[]]$names, [string]$sort = 'newest') {
    $safeSort = Normalize-ClipSort $sort
    $wanted = New-Object System.Collections.Generic.HashSet[string] ([StringComparer]::OrdinalIgnoreCase)
    foreach ($raw in @($names)) {
        if ($wanted.Count -ge 500) { break }
        $decoded = ''
        try { $decoded = [System.Uri]::UnescapeDataString([string]$raw) } catch { continue }
        if ([string]::IsNullOrWhiteSpace($decoded) -or $decoded -match '[\\/]') { continue }
        $name = Get-SafeFilename $decoded
        if ($name) { [void]$wanted.Add($name) }
    }
    $items = New-Object System.Collections.Generic.List[object]
    if ($wanted.Count -gt 0) {
        foreach ($clip in @(Get-ClipsList)) {
            if ($clip -and $clip.name -and $wanted.Contains([string]$clip.name)) {
                [void]$items.Add($clip)
            }
        }
    }
    $sorted = @(Sort-ClipsForPage -items @($items.ToArray()) -sort $safeSort)
    $payload = [ordered]@{
        version = ("{0}|sort:{1}|names" -f [string]$script:State.ClipsCacheVersion, $safeSort)
        total   = [int]$sorted.Count
        sort    = $safeSort
        clips   = @($sorted)
    }
    return ConvertTo-Json -InputObject $payload -Depth 8 -Compress
}

function Find-LatestClip {
    $clips = Get-ClipsListUncached
    if (-not $clips -or $clips.Count -eq 0) { return $null }
    $selected = Get-SafeClipPath $clips[0].name
    if (-not $selected) { return $null }
    $fi = [System.IO.FileInfo]::new($selected.full)
    return @{ name = $selected.name; full = $selected.full; size = $fi.Length }
}


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

function Read-ClipsDb {
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
}

function Save-ClipsDb($db) {
    $obj = @{}
    foreach ($k in $db.Keys) { $obj[$k] = $db[$k] }
    Write-Utf8 (Get-DbPath) ((ConvertTo-Json $obj -Depth 4))
    $script:State.ClipsDbCache = $db
    $script:State.ClipsDbCacheSig = Get-ClipsDbCacheSignature
}

function Mark-Uploaded([string]$name, [string]$url) {
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
}

# called by the compress-overwrite watcher when the encode + atomic replace succeeds. stores the new mode + the freshly-written files mtime as the cache key, plus the timestamp + pre-compress size for ui purposes. survives later /clips polls without re-probing the file.
function Mark-Compressed([string]$name, [string]$mode, [int64]$mtimeTicks, [int64]$preBytes) {
    if ([string]::IsNullOrWhiteSpace($name)) { return }
    if ($mode -ne 'fast' -and $mode -ne 'slow') { return }
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

function Get-ClipsListUncached {
    $root = Get-ClipDir
    if (-not (Test-Path -LiteralPath $root)) { return @() }
    $db = Read-ClipsDb
    $dbDirty = $false
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $items = New-Object System.Collections.Generic.List[object]
    try {
        $files = [System.IO.DirectoryInfo]::new($root).EnumerateFiles()
    } catch {
        return @()
    }
    foreach ($fi in $files) {
        $ext = $fi.Extension.ToLowerInvariant()
        if ($script:ALLOWED_EXTS -notcontains $ext) { continue }
        # skip in-flight worker temp files. trim/compress workers write under %temp%, but cross-volume finalizes briefly stage a _streamable_finalize_ sidecar in the clip folder. older _compress_tmp_ / _trim_tmp_ names are filtered too in case a previous version of the worker left one behind.
        if ($fi.Name -like '_streamable_*' -or
            $fi.Name -like '_compress_tmp_*' -or
            $fi.Name -like '_trim_tmp_*') { continue }
        $item = [ordered]@{
            name  = $fi.Name
            size  = $fi.Length
            mtime = [int][Math]::Floor(([DateTimeOffset]$fi.LastWriteTimeUtc).ToUnixTimeSeconds())
        }

        # resolve the compress marker for this file. the cache key is the files ntfs mtime ticks -- if the file has been replaced or modified since we last probed, the cached cmp_mode is invalidated and we re-probe. the probe itself is a header-only ffprobe call (~30-60ms), so we only pay it on first listing or after a file changes.
        $fileMtimeTicks = $fi.LastWriteTimeUtc.Ticks
        $cmpMode  = ''
        $cmpTs    = [int64]0
        $cmpPre   = [int64]0
        $entry    = if ($db.ContainsKey($fi.Name)) { $db[$fi.Name] } else { $null }
        $cacheHit = $false
        # cache hit requires both the mtime match and a cmp_ver=2 stamp. without the version check, clips compressed by the v1 pipeline would still report "already compressed" forever; with it, those entries fall thru to a fresh get-compressmarker probe (which rejects v1 atoms) and the clip becomes eligible again.
        $entryVer = 0
        if ($entry -and $entry.cmp_ver -ne $null) { $entryVer = [int]$entry.cmp_ver }
        if ($entry -and $entry.cmp_mtime -ne $null -and
            [int64]$entry.cmp_mtime -eq [int64]$fileMtimeTicks -and
            $entryVer -eq 2) {
            $cacheHit = $true
            $cmpMode = [string]$entry.cmp_mode
            if ($entry.cmp_ts -ne $null)  { $cmpTs  = [int64]$entry.cmp_ts }
            if ($entry.cmp_pre -ne $null) { $cmpPre = [int64]$entry.cmp_pre }
        }
        if (-not $cacheHit) {
            $marker = Get-CompressMarker $fi.FullName
            $cmpMode = [string]$marker.mode
            $cmpTs   = [int64]$marker.ts
            $cmpPre  = [int64]$marker.pre
            # cache positive and negative probe results so we dont ffprobe every unmarked clip on every list. stamp them with cmp_ver=2 so the next list reuses the cached answer.
            if (-not $entry) { $entry = @{} }
            $entry.cmp_mode  = $cmpMode
            $entry.cmp_mtime = [int64]$fileMtimeTicks
            $entry.cmp_ts    = $cmpTs
            $entry.cmp_pre   = $cmpPre
            $entry.cmp_ver   = 2
            $db[$fi.Name] = $entry
            $dbDirty = $true
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
                # Missing transcode-state fields read as ready. We never write $null for these fields, so $null reliably means absent.
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
    if ($dbDirty) {
        try { Save-ClipsDb $db } catch { Write-Log "Save-ClipsDb (marker cache) failed: $($_.Exception.Message)" }
    }
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
    $script:State.ClipsCacheBody = $body
    $script:State.ClipsCacheJson = ConvertTo-Json -InputObject @($body) -Depth 8 -Compress
    if ([string]::IsNullOrWhiteSpace($script:State.ClipsCacheJson)) {
        $script:State.ClipsCacheJson = '[]'
    }
    $script:State.ClipsCacheSig  = $sig
    $script:State.ClipsCacheVersion = $sig
    $script:State.ClipsCacheAt   = $now
    return $body
}

function Get-ClipsListJson {
    [void](Get-ClipsList)
    if ([string]::IsNullOrWhiteSpace($script:State.ClipsCacheJson)) { return '[]' }
    return [string]$script:State.ClipsCacheJson
}

function Get-ClipsPageJson([int]$offset, [int]$limit) {
    $items = @(Get-ClipsList)
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
    $payload = [ordered]@{
        version = [string]$script:State.ClipsCacheVersion
        total   = $total
        offset  = $safeOffset
        limit   = $safeLimit
        clips   = @($page)
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


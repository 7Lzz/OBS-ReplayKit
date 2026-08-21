# thumbnails -- cached on disk by sha1(path + size + mtime). serialised via thumbqueuelock so we dont spawn 50 simultaneous shell-thumbnail extractors when the popup first loads.
function Get-ThumbnailName($selected, $fi) {
    $key = $selected.full + '|' + $fi.Length + '|' + ([int64]$fi.LastWriteTimeUtc.Ticks)
    $sha = [System.Security.Cryptography.SHA1]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($key)
        $hash  = $sha.ComputeHash($bytes)
        return (-join ($hash | ForEach-Object { $_.ToString('x2') })) + '.jpg'
    } finally { $sha.Dispose() }
}

function Get-CachedThumbnail($selected, $fi) {
    if (-not (Test-Path -LiteralPath $script:THUMB_DIR)) {
        [void](New-Item -ItemType Directory -Path $script:THUMB_DIR -Force)
    }
    $out = Join-Path $script:THUMB_DIR (Get-ThumbnailName $selected $fi)
    if (Test-Path -LiteralPath $out) { return $out }

    [System.Threading.Monitor]::Enter($script:State.ThumbQueueLock)
    try {
        # re-check after acquiring lock in case another worker just made it.
        if (Test-Path -LiteralPath $out) { return $out }
        $tmp = "$out.$PID.tmp.jpg"
        try {
            [ReplayKitNative]::SaveThumbnail($selected.full, $tmp)
            [System.IO.File]::Move($tmp, $out)
            return $out
        } catch {
            Write-Log "thumb failed for $($selected.name): $($_.Exception.Message)"
            if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -ErrorAction SilentlyContinue }
            return $null
        }
    } finally {
        [System.Threading.Monitor]::Exit($script:State.ThumbQueueLock)
    }
}

function Get-PlaceholderThumbnail {
    $svg = @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 480 270">
  <rect width="480" height="270" fill="#13141A"/>
  <circle cx="240" cy="135" r="44" fill="#272A33" stroke="#3C404D" stroke-width="2"/>
  <path d="M229 110v50l43-25z" fill="#969696"/>
</svg>
"@
    return [System.Text.Encoding]::UTF8.GetBytes($svg)
}

function Get-ObsIconSvg {
    $svg = @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64">
  <rect width="64" height="64" rx="12" fill="#111217"/>
  <circle cx="32" cy="32" r="27" fill="#1D1F26" stroke="#5B6273" stroke-width="2"/>
  <path fill="#F2F2F2" d="M31.3 8.8c7.3.2 13.5 4.6 16.3 10.9-6.7-3.1-14.8-.3-18.1 6.4-2.6-2.6-4.2-6.2-4.2-10.2 0-2.5.7-4.9 1.9-7 1.3-.1 2.7-.2 4.1-.1Z"/>
</svg>
"@
    return [System.Text.Encoding]::UTF8.GetBytes($svg)
}

function Get-ObsIconIco {
    if (Test-Path -LiteralPath $script:OBS_ICON_CACHE) { return $script:OBS_ICON_CACHE }
    if (-not (Test-Path -LiteralPath $script:OBS_EXE)) { return $null }
    if (-not (Test-Path -LiteralPath $script:THUMB_DIR)) {
        [void](New-Item -ItemType Directory -Path $script:THUMB_DIR -Force)
    }
    try {
        [ReplayKitNative]::SaveAssociatedIcon($script:OBS_EXE, $script:OBS_ICON_CACHE)
        return $script:OBS_ICON_CACHE
    } catch {
        Write-Log "OBS icon extraction failed: $($_.Exception.Message)"
        return $null
    }
}


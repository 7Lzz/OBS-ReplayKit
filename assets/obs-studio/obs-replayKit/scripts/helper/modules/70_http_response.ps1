# http response writers. all go to a networkstream that we hold open until we close the underlying tcpclient.
function Get-NoStoreHeaders([hashtable]$extra) {
    $h = @{
        'Access-Control-Allow-Origin'  = '*'
        'Access-Control-Allow-Methods' = 'GET,POST,OPTIONS'
        'Access-Control-Allow-Headers' = 'Content-Type,Range'
        'Cache-Control'                = 'no-store'
        'Connection'                   = 'close'
    }
    if ($extra) { foreach ($k in $extra.Keys) { $h[$k] = $extra[$k] } }
    return $h
}

function Format-HttpResponse([int]$status, [string]$statusText, [hashtable]$headers, [int]$bodyLength) {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append("HTTP/1.1 $status $statusText`r`n")
    if (-not $headers.ContainsKey('Content-Length')) {
        $headers['Content-Length'] = $bodyLength
    }
    foreach ($k in $headers.Keys) {
        # ${k}: avoids ps interpreting "$k:" as a drive-scoped variable.
        [void]$sb.Append("${k}: $($headers[$k])`r`n")
    }
    [void]$sb.Append("`r`n")
    return [System.Text.Encoding]::ASCII.GetBytes($sb.ToString())
}

# windows command-line quoting per msdns commandlinetoargvw rules: any backslashes immediately before a quote are doubled, and the whole thing is wrapped in quotes if it contains a space or quote. needed becuase windows powershell 5.1 ships on .net framework 4.x, where processstartinfo.arguments is a single string (not the .argumentlist collection added in .net 5+) -- we have to build the command line ourselves.
function Quote-Arg([string]$arg) {
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

function Send-Bytes($stream, [int]$status, [string]$statusText, [hashtable]$headers, [byte[]]$body) {
    $head = Format-HttpResponse $status $statusText $headers $body.Length
    $stream.Write($head, 0, $head.Length)
    if ($body.Length -gt 0) { $stream.Write($body, 0, $body.Length) }
    $stream.Flush()
}

function Send-File($stream, [int]$status, [string]$statusText, [hashtable]$headers, [string]$path) {
    $fi = [System.IO.FileInfo]::new($path)
    $head = Format-HttpResponse $status $statusText $headers ([int]$fi.Length)
    $stream.Write($head, 0, $head.Length)
    $fs = [System.IO.File]::OpenRead($path)
    try {
        $buf = New-Object byte[] 65536
        while (($n = $fs.Read($buf, 0, $buf.Length)) -gt 0) {
            $stream.Write($buf, 0, $n)
        }
        $stream.Flush()
    } finally {
        $fs.Dispose()
    }
}

function Send-Text($stream, [int]$status, [string]$statusText, [string]$text, [string]$ctype = 'text/plain; charset=utf-8') {
    $body = [System.Text.Encoding]::UTF8.GetBytes([string]$text)
    $h = Get-NoStoreHeaders @{ 'Content-Type' = $ctype }
    Send-Bytes $stream $status $statusText $h $body
}

function Send-Json($stream, [int]$status, $data) {
    $json = ConvertTo-Json $data -Depth 8 -Compress
    Send-Text $stream $status (Get-StatusText $status) $json 'application/json; charset=utf-8'
}

function Get-StatusText([int]$code) {
    switch ($code) {
        200 { 'OK' }
        202 { 'Accepted' }
        204 { 'No Content' }
        206 { 'Partial Content' }
        302 { 'Found' }
        400 { 'Bad Request' }
        403 { 'Forbidden' }
        404 { 'Not Found' }
        405 { 'Method Not Allowed' }
        409 { 'Conflict' }
        413 { 'Payload Too Large' }
        416 { 'Range Not Satisfiable' }
        500 { 'Internal Server Error' }
        503 { 'Service Unavailable' }
        default { 'OK' }
    }
}

function Serve-Html($stream, [string]$filename) {
    $candidates = @(
        (Join-Path (Get-DockDir) $filename),
        (Join-Path (Get-DefaultDockDir) $filename),
        (Join-Path (Get-ScriptDir) $filename)
    )
    foreach ($f in $candidates) {
        if (Test-Path -LiteralPath $f) {
            try {
                $bytes = [System.IO.File]::ReadAllBytes($f)
                $h = Get-NoStoreHeaders @{ 'Content-Type' = 'text/html; charset=utf-8' }
                Send-Bytes $stream 200 'OK' $h $bytes
                return
            } catch {}
        }
    }
    Send-Text $stream 404 'Not Found' "$filename not found"
}

# serves one range chunk of a clip and reports whether the connection is worth keeping open -- playback at high speed multipliers needs a new chunk far more often than realtime, and a fresh tcp handshake per chunk (the old connection: close behavior) couldnt keep up and showed up as buffering; returning $true lets handle-connection reuse the socket for the next range request.
function Serve-Preview($stream, [hashtable]$req, [string]$rawName) {
    # temporary diagnostic logging while chasing the intermittent trim-preview freeze -- this function otherwise never wrote a single log line on any path, so a stuck/failed preview left no trace at all; reqId lets start/end/error lines for the same call be matched up in the log.
    $reqId = [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    if ($script:State.ActivePreviews -ge $script:MAX_PREVIEW_STREAM) {
        # scrubbing/seeking fires a burst of overlapping range requests that each abort the last -- normal <video> behavior, not a bug, so two briefly overlapping here is routine, not the sustained overload this cap exists for. a slot typically frees in single-digit ms, so a short bounded wait clears almost every one of these instead of hard-failing a request a video element cant retry on its own -- confirmed via logging (2026-08-08) that this cap tripping mid-scrub, with nothing downstream to recover from a rejected chunk, is what was leaving the trim preview stuck; still rejects with 503 if a slot genuinely never frees.
        $busyWaitedMs = 0
        while ($script:State.ActivePreviews -ge $script:MAX_PREVIEW_STREAM -and $busyWaitedMs -lt 300) {
            Start-Sleep -Milliseconds 15
            $busyWaitedMs += 15
        }
        if ($script:State.ActivePreviews -ge $script:MAX_PREVIEW_STREAM) {
            Write-Log "Serve-Preview[$reqId] BUSY name=$rawName active=$($script:State.ActivePreviews) cap=$($script:MAX_PREVIEW_STREAM) waitedMs=$busyWaitedMs"
            Send-Text $stream 503 'Service Unavailable' 'Preview busy'
            return $false
        }
    }
    $selected = Get-SafeClipPath $rawName
    if (-not $selected) {
        Write-Log "Serve-Preview[$reqId] BAD-FILENAME raw=$rawName"
        Send-Text $stream 400 'Bad Request' 'Bad filename'; return $false
    }
    if (-not (Test-Path -LiteralPath $selected.full)) {
        Write-Log "Serve-Preview[$reqId] NOT-FOUND name=$($selected.name)"
        Send-Text $stream 404 'Not Found' 'Clip not found'; return $false
    }
    $fi = [System.IO.FileInfo]::new($selected.full)
    $fileSize = $fi.Length
    $start = 0
    $end   = $fileSize - 1

    $range = $req.Headers['range']
    if ($range -match '^bytes=(\d*)-(\d*)$') {
        $lo = $matches[1]
        $hi = $matches[2]
        if ($lo -eq '') {
            $suffix = [int64]$hi
            if ($suffix -le 0) { Send-Text $stream 416 'Range Not Satisfiable' ''; return $false }
            $start = [Math]::Max($fileSize - $suffix, 0)
        } else {
            $start = [int64]$lo
            if ($hi -ne '') { $end = [int64]$hi }
        }
    }
    if ($start -lt 0 -or $start -ge $fileSize -or $end -lt $start) {
        Send-Text $stream 416 'Range Not Satisfiable' ''; return $false
    }
    $end = [Math]::Min($end, $fileSize - 1)
    $end = [Math]::Min($end, $start + $script:PREVIEW_CHUNK - 1)
    $length = $end - $start + 1
    # honor an explicit client close request even on a successful chunk -- no point offering keep-alive to a socket the caller already said it was done with.
    $clientWantsClose = ([string]$req.Headers['connection']).Trim().ToLowerInvariant() -eq 'close'

    # [ref] on a hashtable property doesnt reach the real storage slot in powershell -- interlocked here silently never persisted, so the cap below never actually enforced; a real lock does.
    [System.Threading.Monitor]::Enter($script:State.PreviewLock)
    try { $script:State.ActivePreviews++ } finally { [System.Threading.Monitor]::Exit($script:State.PreviewLock) }
    Write-Log "Serve-Preview[$reqId] START name=$($selected.name) range=$($range) start=$start end=$end length=$length keepAliveReq=$(-not $clientWantsClose) active=$($script:State.ActivePreviews)"
    $sentFully = $false
    try {
        $ext   = [System.IO.Path]::GetExtension($selected.name).ToLowerInvariant()
        $ctype = if ($script:CONTENT_TYPES.ContainsKey($ext)) { $script:CONTENT_TYPES[$ext] } else { 'application/octet-stream' }
        $h = Get-NoStoreHeaders @{
            'Content-Type'  = $ctype
            'Accept-Ranges' = 'bytes'
            'Content-Range' = "bytes $start-$end/$fileSize"
            'Connection'    = if ($clientWantsClose) { 'close' } else { 'keep-alive' }
        }
        $head = Format-HttpResponse 206 'Partial Content' $h $length
        $stream.Write($head, 0, $head.Length)

        # explicit FileShare.Delete -- OpenRead()s default share mode (Read only) blocks any concurrent delete/rename/replace-on-top of this file for as long as this handle is open; harmless under the old single-threaded server, but a real gap now that /delete can land on a different pool thread while this ones mid-stream, which is what "being used by another process" on delete traces back to. Delete share here doesnt affect what were reading: windows keeps this handles view of the data valid even if the file gets unlinked out from under it mid-read.
        $fs = [System.IO.File]::Open($selected.full, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, ([System.IO.FileShare]::Read -bor [System.IO.FileShare]::Delete))
        try {
            [void]$fs.Seek($start, 'Begin')
            $buf = New-Object byte[] 65536
            $remaining = $length
            while ($remaining -gt 0) {
                $read = [Math]::Min($buf.Length, $remaining)
                $n = $fs.Read($buf, 0, $read)
                if ($n -le 0) { break }
                $stream.Write($buf, 0, $n)
                $remaining -= $n
            }
            $sentFully = $remaining -eq 0
            $stream.Flush()
        } finally { $fs.Dispose() }
    } catch {
        Write-Log "Serve-Preview[$reqId] EXCEPTION after $($sw.ElapsedMilliseconds)ms name=$($selected.name): $($_.Exception.GetType().Name): $($_.Exception.Message)"
        throw
    } finally {
        [System.Threading.Monitor]::Enter($script:State.PreviewLock)
        try { $script:State.ActivePreviews-- } finally { [System.Threading.Monitor]::Exit($script:State.PreviewLock) }
    }
    Write-Log "Serve-Preview[$reqId] DONE name=$($selected.name) sentFully=$sentFully elapsedMs=$($sw.ElapsedMilliseconds) keepAliveResp=$($sentFully -and -not $clientWantsClose)"
    # a short read already broke the promised content-length, so the stream is out of sync for whatever request would come next on this connection -- close instead of pretending its reusable.
    return $sentFully -and -not $clientWantsClose
}

function Serve-Thumbnail($stream, [string]$rawName) {
    $selected = Get-SafeClipPath $rawName
    if (-not $selected -or -not (Test-Path -LiteralPath $selected.full)) {
        $h = Get-NoStoreHeaders @{ 'Content-Type' = 'image/svg+xml' }
        Send-Bytes $stream 200 'OK' $h (Get-PlaceholderThumbnail)
        return
    }
    $fi = [System.IO.FileInfo]::new($selected.full)
    $thumb = Get-CachedThumbnail $selected $fi
    if ($thumb) {
        try {
            $h = @{
                'Access-Control-Allow-Origin' = '*'
                'Cache-Control'               = 'public, max-age=31536000, immutable'
                'Content-Type'                = 'image/jpeg'
                'Connection'                  = 'close'
            }
            Send-File $stream 200 'OK' $h $thumb
            return
        } catch {}
    }
    $h = Get-NoStoreHeaders @{ 'Content-Type' = 'image/svg+xml' }
    Send-Bytes $stream 200 'OK' $h (Get-PlaceholderThumbnail)
}

function Serve-ObsIcon($stream) {
    $iconPath = Get-ObsIconIco
    if ($iconPath -and (Test-Path -LiteralPath $iconPath)) {
        try {
            $bytes = [System.IO.File]::ReadAllBytes($iconPath)
            $h = Get-NoStoreHeaders @{ 'Content-Type' = 'image/x-icon' }
            Send-Bytes $stream 200 'OK' $h $bytes
            return
        } catch {}
    }
    $h = Get-NoStoreHeaders @{ 'Content-Type' = 'image/svg+xml' }
    Send-Bytes $stream 200 'OK' $h (Get-ObsIconSvg)
}


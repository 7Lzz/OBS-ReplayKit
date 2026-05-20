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
        204 { 'No Content' }
        206 { 'Partial Content' }
        302 { 'Found' }
        400 { 'Bad Request' }
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

function Serve-Preview($stream, [hashtable]$req, [string]$rawName) {
    if ($script:State.ActivePreviews -ge $script:MAX_PREVIEW_STREAM) {
        Send-Text $stream 503 'Service Unavailable' 'Preview busy'
        return
    }
    $selected = Get-SafeClipPath $rawName
    if (-not $selected) { Send-Text $stream 400 'Bad Request' 'Bad filename'; return }
    if (-not (Test-Path -LiteralPath $selected.full)) {
        Send-Text $stream 404 'Not Found' 'Clip not found'; return
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
            if ($suffix -le 0) { Send-Text $stream 416 'Range Not Satisfiable' ''; return }
            $start = [Math]::Max($fileSize - $suffix, 0)
        } else {
            $start = [int64]$lo
            if ($hi -ne '') { $end = [int64]$hi }
        }
    }
    if ($start -lt 0 -or $start -ge $fileSize -or $end -lt $start) {
        Send-Text $stream 416 'Range Not Satisfiable' ''; return
    }
    $end = [Math]::Min($end, $fileSize - 1)
    $end = [Math]::Min($end, $start + $script:PREVIEW_CHUNK - 1)
    $length = $end - $start + 1

    [System.Threading.Interlocked]::Increment([ref]$script:State.ActivePreviews) | Out-Null
    try {
        $ext   = [System.IO.Path]::GetExtension($selected.name).ToLowerInvariant()
        $ctype = if ($script:CONTENT_TYPES.ContainsKey($ext)) { $script:CONTENT_TYPES[$ext] } else { 'application/octet-stream' }
        $h = Get-NoStoreHeaders @{
            'Content-Type'  = $ctype
            'Accept-Ranges' = 'bytes'
            'Content-Range' = "bytes $start-$end/$fileSize"
        }
        $head = Format-HttpResponse 206 'Partial Content' $h $length
        $stream.Write($head, 0, $head.Length)

        $fs = [System.IO.File]::OpenRead($selected.full)
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
            $stream.Flush()
        } finally { $fs.Dispose() }
    } finally {
        [System.Threading.Interlocked]::Decrement([ref]$script:State.ActivePreviews) | Out-Null
    }
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


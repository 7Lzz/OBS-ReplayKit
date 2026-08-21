# parses one http request off an already-open reader; returns $null on a closed connection or a request line too broken to route, which callers treat as "stop reading this connection".
function Read-HttpRequest($stream, $reader) {
    $requestLine = $reader.ReadLine()
    if (-not $requestLine) { return $null }
    $parts = $requestLine.Split(' ')
    if ($parts.Length -lt 2) { return $null }
    $method  = $parts[0]
    $rawPath = $parts[1]

    $headers = @{}
    while ($true) {
        $line = $reader.ReadLine()
        if (-not $line) { break }
        $idx = $line.IndexOf(':')
        if ($idx -gt 0) {
            $k = $line.Substring(0, $idx).Trim().ToLowerInvariant()
            $v = $line.Substring($idx + 1).Trim()
            $headers[$k] = $v
        }
    }

    # read request body when content-length says theres one. login + similar json posts come thru here. ascii encoding on the reader means 1 char == 1 byte, which is what content-length counts -- correct for ascii / json-ascii payloads.
    $body = ''
    $bodyLen = 0
    if ($headers.ContainsKey('content-length')) {
        if (-not [int]::TryParse($headers['content-length'], [ref]$bodyLen) -or $bodyLen -lt 0) {
            Send-Text $stream 400 'Bad Request' 'Invalid Content-Length'
            return $null
        }
        if ($bodyLen -gt (1024 * 1024)) {
            Send-Text $stream 413 'Payload Too Large' 'Request body too large'
            return $null
        }
    }
    if ($bodyLen -gt 0) {
        $buf  = New-Object char[] $bodyLen
        $read = 0
        while ($read -lt $bodyLen) {
            $n = $reader.Read($buf, $read, $bodyLen - $read)
            if ($n -le 0) { break }
            $read += $n
        }
        $body = New-Object string ($buf, 0, $read)
    }

    $qIdx = $rawPath.IndexOf('?')
    $path = if ($qIdx -ge 0) { $rawPath.Substring(0, $qIdx) } else { $rawPath }
    $queryString = if ($qIdx -ge 0) { $rawPath.Substring($qIdx + 1) } else { '' }
    $query = @{}
    if ($queryString) {
        foreach ($pair in $queryString.Split('&')) {
            if (-not $pair) { continue }
            $eq = $pair.IndexOf('=')
            if ($eq -ge 0) {
                $k = [System.Uri]::UnescapeDataString($pair.Substring(0, $eq))
                $v = [System.Uri]::UnescapeDataString($pair.Substring($eq + 1))
                $query[$k] = $v
            } else {
                $query[[System.Uri]::UnescapeDataString($pair)] = ''
            }
        }
    }

    return @{
        Method  = $method.ToUpperInvariant()
        Path    = $path
        Query   = $query
        Headers = $headers
        Body    = $body
    }
}

function Handle-Connection([System.Net.Sockets.TcpClient]$client) {
    try {
        $client.NoDelay        = $true
        $client.ReceiveTimeout = 5000
        # a write that stalls (client not draining its receive window) blocks this connections pool thread for the full timeout before .net aborts it -- nothing on localhost legitimately needs anywhere near that long, and a stuck client shouldnt tie up a pool slot repeatedly.
        $client.SendTimeout    = 3000
        $stream = $client.GetStream()
        $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::ASCII, $false, 8192, $true)

        # every route answers once and closes (connection: close from get-nostoreheaders) except /file/, which can ask to keep going so a video elements next range request reuses this socket instead of paying a fresh tcp handshake per chunk -- the type check guards against any other route accidentally leaking a truthy, non-boolean pipeline result that would otherwise be misread as a keep-alive request.
        while ($true) {
            $req = Read-HttpRequest $stream $reader
            if (-not $req) { break }
            $result = Dispatch-Request $stream $req
            if (-not (($result -is [bool]) -and $result)) { break }
            # a kept-alive /file/ socket bets the same video elements next range request follows within tens of ms, so it gets a short idle timeout instead of the fresh-connection 5s one -- this used to also sacrifice the slot the instant any new connection showed up at the listener, on the theory a bounded pool needed to protect against starvation, but confirmed via logging (2026-08-08) that a new connection always gets its own pooled runspace immediately and essentially never actually waits on this ones slot, so sacrificing on every arrival was pure downside: it was breaking ordinary mid-playback traffic (another hover preview, the next /clips poll), forcing the video to retry aggressively and driving MAX_PREVIEW_STREAM into its 503 path. letting it live out its natural idle timeout instead is the simpler, correct fix.
            $waitedMs = 0
            $gotNextRequest = $false
            while ($waitedMs -lt 500) {
                if ($client.Client.Poll(20000, [System.Net.Sockets.SelectMode]::SelectRead)) {
                    $gotNextRequest = $true
                    break
                }
                $waitedMs += 20
            }
            # temporary diagnostic logging while chasing the intermittent trim-preview freeze.
            if (-not $gotNextRequest) {
                Write-Log "Handle-Connection: keep-alive idle-timeout path=$($req.Path) waitedMs=$waitedMs"
                break
            }
            $client.ReceiveTimeout = 500
        }
    } catch {
        Write-Log "connection error: $($_.Exception.Message)"
    } finally {
        try { $client.Close() } catch {}
    }
}


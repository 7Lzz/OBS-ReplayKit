function Handle-Connection([System.Net.Sockets.TcpClient]$client) {
    try {
        $client.NoDelay        = $true
        $client.ReceiveTimeout = 5000
        $client.SendTimeout    = 15000
        $stream = $client.GetStream()
        $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::ASCII, $false, 8192, $true)

        $requestLine = $reader.ReadLine()
        if (-not $requestLine) { return }
        $parts = $requestLine.Split(' ')
        if ($parts.Length -lt 2) { return }
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
                return
            }
            if ($bodyLen -gt (1024 * 1024)) {
                Send-Text $stream 413 'Payload Too Large' 'Request body too large'
                return
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

        $req = @{
            Method  = $method.ToUpperInvariant()
            Path    = $path
            Query   = $query
            Headers = $headers
            Body    = $body
        }
        Dispatch-Request $stream $req
    } catch {
        Write-Log "connection error: $($_.Exception.Message)"
    } finally {
        try { $client.Close() } catch {}
    }
}


function Get-ObsWebSocketSettings {
    $path = Join-Path $env:APPDATA 'obs-studio\plugin_config\obs-websocket\config.json'
    if (-not (Test-Path -LiteralPath $path)) {
        return @{ ok = $false; unavailable = $true; message = 'OBS websocket config not found.' }
    }
    try {
        $cfg = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    } catch {
        return @{ ok = $false; unavailable = $true; message = 'OBS websocket config is not valid JSON.' }
    }
    if (-not [bool]$cfg.server_enabled) {
        return @{ ok = $false; unavailable = $true; message = 'OBS websocket server is disabled.' }
    }
    if ([bool]$cfg.auth_required) {
        return @{ ok = $false; unavailable = $true; message = 'OBS websocket authentication is enabled.' }
    }
    $port = 6455
    if ($cfg.server_port) { [void][int]::TryParse(([string]$cfg.server_port), [ref]$port) }
    if ($port -le 0 -or $port -gt 65535) {
        return @{ ok = $false; unavailable = $true; message = 'OBS websocket port is invalid.' }
    }
    return @{ ok = $true; port = $port }
}

function Receive-ObsWebSocketMessage($socket, [Threading.CancellationToken]$token) {
    $buffer = New-Object byte[] 8192
    $segment = [ArraySegment[byte]]::new($buffer)
    $ms = New-Object System.IO.MemoryStream
    try {
        do {
            $result = $socket.ReceiveAsync($segment, $token).GetAwaiter().GetResult()
            if ($result.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) {
                throw 'OBS websocket closed the connection.'
            }
            if ($result.Count -gt 0) { $ms.Write($buffer, 0, $result.Count) }
        } while (-not $result.EndOfMessage)
        $json = [System.Text.Encoding]::UTF8.GetString($ms.ToArray())
        return ConvertFrom-Json $json
    } finally {
        $ms.Dispose()
    }
}

function Send-ObsWebSocketMessage($socket, $payload, [Threading.CancellationToken]$token) {
    $json = ConvertTo-Json $payload -Depth 8 -Compress
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $segment = [ArraySegment[byte]]::new($bytes)
    [void]$socket.SendAsync(
        $segment,
        [System.Net.WebSockets.WebSocketMessageType]::Text,
        $true,
        $token
    ).GetAwaiter().GetResult()
}

# drops the cached connection (if any) and closes it. only ever called with ObsWebSocketLock already held.
function Close-ObsWebSocketCached {
    $socket = $script:State.ObsWebSocket
    if ($null -eq $socket) { return }
    $script:State.ObsWebSocket = $null
    $script:State.ObsWebSocketPort = 0
    try {
        if ($socket.State -eq [System.Net.WebSockets.WebSocketState]::Open) {
            [void]$socket.CloseAsync(
                [System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure,
                'done',
                [Threading.CancellationToken]::None
            ).GetAwaiter().GetResult()
        }
    } catch {}
    $socket.Dispose()
}

# returns an open, identified socket -- reuses $script:State.ObsWebSocket when its still marked open and pointed at the current port, otherwise connects fresh. only ever called with ObsWebSocketLock already held.
function Get-ObsWebSocketConnected([hashtable]$settings, [Threading.CancellationToken]$token) {
    $cached = $script:State.ObsWebSocket
    if ($null -ne $cached -and $cached.State -eq [System.Net.WebSockets.WebSocketState]::Open -and $script:State.ObsWebSocketPort -eq [int]$settings.port) {
        return $cached
    }
    Close-ObsWebSocketCached

    # ClientWebSocket.ConnectAsync goes thru the same http stack as httpwebrequest on .net framework 4.x (proxy detection and all), which can cost multiple seconds against a port nothing is listening on instead of failing fast -- a raw tcp probe answers "is anything even listening" in well under a second, so a disabled/broken obs-websocket fails fast here instead of costing the caller close to the full timeout. only paid on a fresh connect, not on every reused request -- which is most of them once obs is up, since the socket now stays open between calls.
    $probe = [System.Net.Sockets.TcpClient]::new()
    try {
        $ar = $probe.BeginConnect('127.0.0.1', [int]$settings.port, $null, $null)
        if (-not $ar.AsyncWaitHandle.WaitOne(500)) {
            throw 'OBS websocket is not reachable.'
        }
        $probe.EndConnect($ar)
    } finally {
        $probe.Close()
    }

    $socket = [System.Net.WebSockets.ClientWebSocket]::new()
    try {
        $uri = [Uri]::new(('ws://127.0.0.1:{0}' -f [int]$settings.port))
        [void]$socket.ConnectAsync($uri, $token).GetAwaiter().GetResult()

        $hello = Receive-ObsWebSocketMessage $socket $token
        if ([int]$hello.op -ne 0) { throw 'OBS websocket did not send Hello.' }
        if ($hello.d.authentication) { throw 'OBS websocket requires authentication.' }

        $rpcVersion = 1
        if ($hello.d.rpcVersion) { $rpcVersion = [int]$hello.d.rpcVersion }
        $null = Send-ObsWebSocketMessage $socket @{ op = 1; d = @{ rpcVersion = $rpcVersion } } $token

        $identified = Receive-ObsWebSocketMessage $socket $token
        if ([int]$identified.op -ne 2) { throw 'OBS websocket did not identify this client.' }
    } catch {
        $socket.Dispose()
        throw
    }

    $script:State.ObsWebSocket = $socket
    $script:State.ObsWebSocketPort = [int]$settings.port
    return $socket
}

function Invoke-ObsWebSocketRequest([string]$requestType, $requestData = $null, [int]$timeoutMs = 3000) {
    $settings = Get-ObsWebSocketSettings

    # the whole connect-reuse-or-fresh + send + receive cycle runs under one lock -- the request/response matching below has no way to tell two concurrent callers requests apart on a shared socket, so at most one request can be in flight on it at a time regardless of which of the ~50 callers of this function fired it.
    [System.Threading.Monitor]::Enter($script:State.ObsWebSocketLock)
    try {
        if (-not $settings.ok) {
            Close-ObsWebSocketCached
            return $settings
        }

        $cts = [Threading.CancellationTokenSource]::new()
        $cts.CancelAfter([Math]::Max(1000, $timeoutMs))
        try {
            $socket = Get-ObsWebSocketConnected $settings $cts.Token

            $requestId = [Guid]::NewGuid().ToString('N')
            $data = @{
                requestType = $requestType
                requestId   = $requestId
            }
            if ($requestData -ne $null) { $data.requestData = $requestData }
            $null = Send-ObsWebSocketMessage $socket @{ op = 6; d = $data } $cts.Token

            while ($true) {
                $response = Receive-ObsWebSocketMessage $socket $cts.Token
                if ([int]$response.op -ne 7) { continue }
                if ([string]$response.d.requestId -ne $requestId) { continue }
                $status = $response.d.requestStatus
                if ($status -and [bool]$status.result) {
                    return @{ ok = $true; requestType = $requestType; data = $response.d.responseData }
                }
                $comment = if ($status.comment) { [string]$status.comment } else { "$requestType failed." }
                return @{
                    ok          = $false
                    requestType = $requestType
                    code        = if ($status.code) { [int]$status.code } else { 0 }
                    message     = $comment
                }
            }
        } catch {
            # anything thrown above (connect, hello/identify, send, receive, cancellation) leaves the socket in an unknown state -- drop it so the next call reconnects fresh instead of reusing something possibly half-broken. a request that obs itself answered with a failure result never reaches here, it returns from inside the try above.
            Close-ObsWebSocketCached
            return @{ ok = $false; unavailable = $true; message = $_.Exception.Message }
        } finally {
            $cts.Dispose()
        }
    } finally {
        [System.Threading.Monitor]::Exit($script:State.ObsWebSocketLock)
    }
}

function Invoke-ObsSaveReplayBuffer {
    return Invoke-ObsWebSocketRequest 'SaveReplayBuffer' $null 3000
}

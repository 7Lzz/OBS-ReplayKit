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
    $port = 4455
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

function Invoke-ObsWebSocketRequest([string]$requestType, $requestData = $null, [int]$timeoutMs = 3000) {
    $settings = Get-ObsWebSocketSettings
    if (-not $settings.ok) { return $settings }

    $socket = [System.Net.WebSockets.ClientWebSocket]::new()
    $cts = [Threading.CancellationTokenSource]::new()
    $cts.CancelAfter([Math]::Max(1000, $timeoutMs))
    try {
        $uri = [Uri]::new(('ws://127.0.0.1:{0}' -f [int]$settings.port))
        [void]$socket.ConnectAsync($uri, $cts.Token).GetAwaiter().GetResult()

        $hello = Receive-ObsWebSocketMessage $socket $cts.Token
        if ([int]$hello.op -ne 0) { throw 'OBS websocket did not send Hello.' }
        if ($hello.d.authentication) {
            return @{ ok = $false; unavailable = $true; message = 'OBS websocket requires authentication.' }
        }

        $rpcVersion = 1
        if ($hello.d.rpcVersion) { $rpcVersion = [int]$hello.d.rpcVersion }
        $null = Send-ObsWebSocketMessage $socket @{ op = 1; d = @{ rpcVersion = $rpcVersion } } $cts.Token

        $identified = Receive-ObsWebSocketMessage $socket $cts.Token
        if ([int]$identified.op -ne 2) { throw 'OBS websocket did not identify this client.' }

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
        return @{ ok = $false; unavailable = $true; message = $_.Exception.Message }
    } finally {
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
        $cts.Dispose()
    }
}

function Invoke-ObsSaveReplayBuffer {
    return Invoke-ObsWebSocketRequest 'SaveReplayBuffer' $null 3000
}

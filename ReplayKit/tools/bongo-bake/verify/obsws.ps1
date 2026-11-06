param([string]$Request, [string]$Data = '{}', [string]$Batch = '')
# one obs-websocket v5 request (or a json array batch file of {requestType, requestData}) with the local config password
$ErrorActionPreference = 'Stop'
$cfg = Get-Content "$env:APPDATA\obs-studio\plugin_config\obs-websocket\config.json" -Raw | ConvertFrom-Json
$ws = [System.Net.WebSockets.ClientWebSocket]::new()
$ct = [Threading.CancellationToken]::None
$ws.ConnectAsync([Uri]"ws://127.0.0.1:$($cfg.server_port)", $ct).Wait()
function Recv {
    $buf = [byte[]]::new(16MB); $ms = [IO.MemoryStream]::new()
    do { $r = $ws.ReceiveAsync([ArraySegment[byte]]::new($buf), $ct).Result; $ms.Write($buf, 0, $r.Count) } while (-not $r.EndOfMessage)
    [Text.Encoding]::UTF8.GetString($ms.ToArray()) | ConvertFrom-Json
}
function Send($obj) { $b = [Text.Encoding]::UTF8.GetBytes(($obj | ConvertTo-Json -Depth 20 -Compress)); $ws.SendAsync([ArraySegment[byte]]::new($b), 'Text', $true, $ct).Wait() }
$hello = Recv
$identify = @{ op = 1; d = @{ rpcVersion = 1 } }
if ($hello.d.authentication) {
    $sha = [Security.Cryptography.SHA256]::Create()
    $secret = [Convert]::ToBase64String($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($cfg.server_password + $hello.d.authentication.salt)))
    $identify.d.authentication = [Convert]::ToBase64String($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($secret + $hello.d.authentication.challenge)))
}
Send $identify
$null = Recv
$requests = if ($Batch) { Get-Content $Batch -Raw | ConvertFrom-Json } else { @(@{ requestType = $Request; requestData = ($Data | ConvertFrom-Json) }) }
$i = 0
foreach ($req in $requests) {
    Send @{ op = 6; d = @{ requestType = $req.requestType; requestId = "r$i"; requestData = $req.requestData } }
    do { $msg = Recv } while ($msg.op -ne 7)
    $msg.d | ConvertTo-Json -Depth 20 -Compress
    $i++
}
$ws.CloseAsync('NormalClosure', '', $ct).Wait()

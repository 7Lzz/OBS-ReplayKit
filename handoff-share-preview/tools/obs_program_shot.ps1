param([string]$Out, [string]$Scene = "All-In-One")
# one obs-websocket v5 session: identify (with auth when the server asks for it), take a screenshot of the scene, and also read which capture items are enabled
$cfg = Get-Content "$env:APPDATA\obs-studio\plugin_config\obs-websocket\config.json" -Raw | ConvertFrom-Json
$ws = New-Object System.Net.WebSockets.ClientWebSocket
$ws.ConnectAsync([Uri]("ws://127.0.0.1:" + $cfg.server_port), [Threading.CancellationToken]::None).Wait()
function Recv { $buf = New-Object byte[] 16777216; $seg = New-Object ArraySegment[byte] -ArgumentList (,$buf); $ms = New-Object IO.MemoryStream; do { $r = $ws.ReceiveAsync($seg, [Threading.CancellationToken]::None).Result; $ms.Write($buf, 0, $r.Count) } until ($r.EndOfMessage); [Text.Encoding]::UTF8.GetString($ms.ToArray()) | ConvertFrom-Json }
function Send($obj) { $b = [Text.Encoding]::UTF8.GetBytes(($obj | ConvertTo-Json -Depth 8 -Compress)); $ws.SendAsync((New-Object ArraySegment[byte] -ArgumentList (,$b)), 'Text', $true, [Threading.CancellationToken]::None).Wait() }
$hello = Recv
$identify = @{ op = 1; d = @{ rpcVersion = 1; eventSubscriptions = 0 } }
if ($hello.d.authentication) {
  $sha = [Security.Cryptography.SHA256]::Create()
  $secret = [Convert]::ToBase64String($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($cfg.server_password + $hello.d.authentication.salt)))
  $identify.d.authentication = [Convert]::ToBase64String($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($secret + $hello.d.authentication.challenge)))
}
Send $identify; [void](Recv)
Send @{ op = 6; d = @{ requestType = "GetSourceScreenshot"; requestId = "shot"; requestData = @{ sourceName = $Scene; imageFormat = "png"; imageWidth = 2560 } } }
$shot = Recv
$data = $shot.d.responseData.imageData -replace '^data:image/png;base64,', ''
[IO.File]::WriteAllBytes($Out, [Convert]::FromBase64String($data))
Send @{ op = 6; d = @{ requestType = "GetSceneItemList"; requestId = "items"; requestData = @{ sceneName = $Scene } } }
$items = Recv
$items.d.responseData.sceneItems | Where-Object { $_.sourceName -match 'Capture' } | ForEach-Object { "{0} enabled={1}" -f $_.sourceName, $_.sceneItemEnabled }
$ws.Dispose()

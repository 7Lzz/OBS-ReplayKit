# screenshots of temporary replaykit-bongo inputs in every mode and property combination, in a scene that is never shown, then removes them again; writes cases.json for compare.py
param([string]$OutDir = (Join-Path $env:TEMP 'ReplayKit\scratch\bongo-verify'))
$ErrorActionPreference = 'Stop'
$wsScript = Join-Path $PSScriptRoot 'obsws.ps1'
# a fresh connection right after the last one closed sometimes fails, so each request gets a few tries
function Ws { param([string]$Request, [string]$Data) for ($i = 0; $i -lt 5; $i++) { try { return & $wsScript -Request $Request -Data $Data } catch { Start-Sleep -Milliseconds 300 } }; throw "websocket $Request failed" }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$base = @{ mode = 'standard'; width = 1280; height = 768; x = 0.0; y = 0.02; scale = 1.83; delay = 1.0; delaytime = 1.0; random_motion = $true; breath = $true; eyeblink = $true; track = $true; live2d = $true; relative_mouse = $true; mouse_horizontal_flip = $true; mouse_vertical_flip = $true; mask = $false }
$cases = @(
    @{ name = 'standard'; o = @{} },
    @{ name = 'standard-no-live2d'; o = @{ live2d = $false } },
    @{ name = 'keyboard'; o = @{ mode = 'keyboard' } },
    @{ name = 'keyboard-no-live2d'; o = @{ mode = 'keyboard'; live2d = $false } },
    @{ name = 'feixue'; o = @{ mode = 'feixue' } },
    @{ name = 'feixue-no-live2d'; o = @{ mode = 'feixue'; live2d = $false } },
    @{ name = 'mania'; o = @{ mode = 'mania' } },
    @{ name = 'standard-mask'; o = @{ mask = $true } },
    @{ name = 'standard-flags-camera'; o = @{ track = $false; eyeblink = $false; breath = $false; mouse_horizontal_flip = $false; mouse_vertical_flip = $false; scale = 1.5; x = 0.1; y = -0.05 } },
    @{ name = 'standard-small'; o = @{ width = 1000; height = 600 } }
)
$scene = 'rk-bongo-test'
foreach ($c in $cases) { try { & $wsScript -Request RemoveInput -Data (@{ inputName = 'rk-bongo-test-' + $c.name } | ConvertTo-Json -Compress) | Out-Null } catch {} }
try { & $wsScript -Request RemoveScene -Data (@{ sceneName = $scene } | ConvertTo-Json -Compress) | Out-Null } catch {}
Ws -Request CreateScene -Data (@{ sceneName = $scene } | ConvertTo-Json -Compress) | Out-Null
$out = @()
try {
    foreach ($c in $cases) {
        $settings = $base.Clone(); foreach ($k in $c.o.Keys) { $settings[$k] = $c.o[$k] }
        $input = 'rk-bongo-test-' + $c.name
        Ws -Request CreateInput -Data (@{ sceneName = $scene; inputName = $input; inputKind = 'replaykit-bongo'; inputSettings = $settings; sceneItemEnabled = $true } | ConvertTo-Json -Compress -Depth 5) | Out-Null
        Start-Sleep -Milliseconds 400
        $shot = Join-Path $OutDir ('shot-' + $c.name + '.png')
        $w = [int][Math]::Floor($settings.width / 32) * 32
        $r = Ws -Request SaveSourceScreenshot -Data (@{ sourceName = $input; imageFormat = 'png'; imageFilePath = ($shot -replace '\\', '/'); imageWidth = $w; imageHeight = $settings.height } | ConvertTo-Json -Compress) | ConvertFrom-Json
        if (-not $r.requestStatus.result) { "screenshot failed for $($c.name): $($r.requestStatus.comment)" }
        $out += @{ name = $c.name; shot = $shot; settings = $settings }
    }
} finally {
    foreach ($c in $cases) { Ws -Request RemoveInput -Data (@{ inputName = 'rk-bongo-test-' + $c.name } | ConvertTo-Json -Compress) | Out-Null }
    Ws -Request RemoveScene -Data (@{ sceneName = $scene } | ConvertTo-Json -Compress) | Out-Null
}
$out | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutDir 'cases.json') -Encoding utf8
"$($out.Count) screenshots in $OutDir"

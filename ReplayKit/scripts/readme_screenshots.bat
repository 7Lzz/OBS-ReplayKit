<# :
@echo off
rem the batch part only hands the rest of this file to powershell, so the script opens with a double click
setlocal
set "RK_SELF=%~f0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "& ([ScriptBlock]::Create([IO.File]::ReadAllText($env:RK_SELF)))"
set "RC=%ERRORLEVEL%"
if /i not "%~1"=="--no-pause" pause
exit /b %RC%
#>

# takes the readme screenshots from the running obs replaykit at 1920x1080: the clip folder is pointed at demo clips made from store screenshots of popular games, each view is shown and the window is saved into docs/images, then the clip folder, the view, the window, the sections of the clips view and the account picture go back to how they were
$ErrorActionPreference = 'Stop'

$scriptsDir = Split-Path -Parent $env:RK_SELF
$kitRoot = Split-Path -Parent $scriptsDir
$imagesDir = Join-Path (Split-Path -Parent $kitRoot) 'docs\images'

function Read-Constant([string]$relativePath, [string]$pattern, [string]$what) {
    $path = Join-Path $kitRoot $relativePath
    $match = [regex]::Match([IO.File]::ReadAllText($path), $pattern)
    if (-not $match.Success) { throw "Could not read $what from $relativePath." }
    return $match.Groups[1].Value
}

$helper = 'http://' + (Read-Constant 'shared\HelperEndpoint.cs' 'const\s+string\s+Host\s*=\s*"([^"]+)"' 'the helper host') + ':' + (Read-Constant 'shared\HelperEndpoint.cs' 'const\s+int\s+Port\s*=\s*(\d+)\s*;' 'the helper port')
$tempRoot = Join-Path $env:TEMP (Read-Constant 'shared\TempLayout.cs' 'const\s+string\s+Folder\s*=\s*"([^"]+)"' 'the temp folder')
# the name obs gives every audio track, which tells the helper the tracks are the mix, desktop, discord and microphone
$obsAudioHandler = Read-Constant 'apps\helper\SavedClipAudio.cs' 'const\s+string\s+ObsAudioHandler\s*=\s*"([^"]+)"' 'the obs audio track name'
$demoDir = Join-Path $tempRoot 'readme-demo'
$shotsDir = Join-Path $demoDir 'readme-shots'
# the downloaded game pictures are kept between runs and checked against their hashes each time
$sourcesDir = Join-Path $tempRoot 'readme-sources'
$shotWidth = 1920
$shotHeight = 1080

Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class RkWindowPicture {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
}
"@
# the window is sized and measured in real pixels, so a scaled display still gives a 1920x1080 picture
[RkWindowPicture]::SetProcessDPIAware() | Out-Null

function Invoke-Sdk([string]$command, [hashtable]$commandArgs = @{}) {
    $body = @{ command = $command; args = $commandArgs } | ConvertTo-Json -Depth 6 -Compress
    $answer = Invoke-RestMethod -Uri "$helper/sdk/run" -Method Post -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec 30
    if (-not $answer.ok) { throw "The SDK command $command failed: $($answer.message)" }
    return $answer
}

function Wait-For([string]$what, [int]$seconds, [scriptblock]$done) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (& $done) { return }
        Start-Sleep -Milliseconds 400
    }
    throw "Timed out waiting for $what."
}

function Get-ObsWindow {
    $process = Get-Process obs64 -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero -and $_.MainWindowTitle -like 'OBS*' } | Select-Object -First 1
    if (-not $process) { throw 'The OBS window was not found.' }
    return $process.MainWindowHandle
}

function Get-WindowRect([IntPtr]$window) {
    $rect = New-Object RkWindowPicture+Rect
    if (-not [RkWindowPicture]::GetWindowRect($window, [ref]$rect)) { throw 'The size of the OBS window could not be read.' }
    return $rect
}

# 0x14 is SWP_NOZORDER with SWP_NOACTIVATE, so only the place and the size change
function Set-WindowRect([IntPtr]$window, [int]$left, [int]$top, [int]$width, [int]$height) {
    if (-not [RkWindowPicture]::SetWindowPos($window, [IntPtr]::Zero, $left, $top, $width, $height, 0x14)) { throw 'The OBS window could not be sized.' }
}

function Save-WindowPicture([string]$name) {
    $window = Get-ObsWindow
    $rect = Get-WindowRect $window
    if (($rect.Right - $rect.Left) -ne $shotWidth -or ($rect.Bottom - $rect.Top) -ne $shotHeight) { throw "The OBS window is not $($shotWidth)x$($shotHeight) for $name." }
    $bitmap = New-Object System.Drawing.Bitmap $shotWidth, $shotHeight
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $dc = $graphics.GetHdc()
        # 2 is PW_RENDERFULLCONTENT, which draws the window as it shows, the browser pages included
        $drawn = [RkWindowPicture]::PrintWindow($window, $dc, 2)
        $graphics.ReleaseHdc($dc)
        $graphics.Dispose()
        if (-not $drawn) { throw "The OBS window could not be drawn for $name." }
        $bitmap.Save((Join-Path $shotsDir "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $bitmap.Dispose()
    }
    Write-Host "  saved $name.png"
}

function Invoke-Ffmpeg([string[]]$arguments) {
    & $script:ffmpeg -y -hide_banner -loglevel error @arguments
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed with exit code $LASTEXITCODE." }
}

# a store screenshot from steam, downloaded once and refused when it is not the exact picture this script was written for
function Get-SourcePicture($source) {
    $path = Join-Path $sourcesDir ($source.Sha256 + '.jpg')
    if (-not (Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $source.Sha256) {
        Invoke-WebRequest -Uri $source.Url -OutFile $path -UseBasicParsing -TimeoutSec 60
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if ($hash -ne $source.Sha256) {
            Remove-Item -LiteralPath $path -Force
            throw "The picture for $($source.Game) changed on Steam ($($source.Url)); pick a new one and update its hash in this script."
        }
    }
    return $path
}

# a clip shaped like one obs saves: 1080p h264 video that pans slowly across a game picture, and four aac tracks, the mix first and then the desktop, discord and microphone tracks, all named the way obs names them so the editor lays out three rows
function New-DemoClip([string]$path, [string]$picture, [int]$seconds, [int]$tone) {
    $low = 150 + 23 * $tone
    $desktop = "aevalsrc='0.22*sin(2*PI*$low*t)*(0.55+0.45*sin(2*PI*0.4*t))+0.12*sin(2*PI*$([int]($low * 3 / 2))*t)*(0.5+0.5*sin(2*PI*0.9*t))|0.22*sin(2*PI*$low*t)*(0.55+0.45*sin(2*PI*0.4*t))+0.12*sin(2*PI*$($low * 2)*t)':s=48000"
    $discord = "anoisesrc=r=48000:a=0.35:c=pink:seed=$tone,lowpass=f=2500,volume='gt(sin(2*PI*0.45*t+$tone)+0.6*sin(2*PI*1.7*t),0.7)':eval=frame,pan=stereo|c0=c0|c1=c0"
    $mic = "anoisesrc=r=48000:a=0.3:c=brown:seed=$($tone + 50),highpass=f=200,volume='gt(sin(2*PI*0.3*t+$($tone * 2))+0.5*sin(2*PI*2.1*t),0.9)':eval=frame,pan=stereo|c0=c0|c1=c0"
    Invoke-Ffmpeg @(
        '-loop', '1', '-framerate', '30', '-t', "$seconds", '-i', $picture,
        '-f', 'lavfi', '-i', $desktop, '-f', 'lavfi', '-i', $discord, '-f', 'lavfi', '-i', $mic,
        '-filter_complex', "[0:v]scale=2112:1188,crop=1920:1080:x='(iw-ow)*t/$seconds':y='(ih-oh)/2',format=yuv420p[v];[1:a]asplit[d1][d2];[2:a]asplit[c1][c2];[3:a]asplit[m1][m2];[d1][c1][m1]amix=inputs=3:normalize=0[mix]",
        '-map', '[v]', '-map', '[mix]', '-map', '[d2]', '-map', '[c2]', '-map', '[m2]', '-t', "$seconds",
        '-c:v', 'libx264', '-preset', 'veryfast', '-crf', '23', '-c:a', 'aac', '-b:a', '128k',
        '-metadata:s:a', "handler_name=$obsAudioHandler", '-movflags', '+faststart', $path)
}

function Set-FileTime([string]$path, [datetime]$when) {
    $item = Get-Item -LiteralPath $path
    $item.CreationTime = $when
    $item.LastWriteTime = $when
}

$steam = 'https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/'
$now = Get-Date
# the demo clips: a store screenshot of a game each, how long the clip runs and how long ago it was saved
$demoClips = @(
    @{ Game = 'Rocket League'; Url = $steam + '252950/41bd974bde088dc623fae68db333f5b8bb42700e/ss_41bd974bde088dc623fae68db333f5b8bb42700e.1920x1080.jpg'; Sha256 = '0438AF02AFFAFBEEFEFBC95C04E8BFA8D891D28F8464FA5739D5483BBF841FF3'; Seconds = 34; Ago = 0.4; Title = '' }
    @{ Game = 'Counter-Strike 2'; Url = $steam + '730/ss_13bb35638c0267759276f511ee97064773b37a51.1920x1080.jpg'; Sha256 = 'F38E7018741194EE2F24B47CC64E2A44155BA9BAA0B2B61E377486C239B46468'; Seconds = 21; Ago = 3; Title = 'Clutch round' }
    @{ Game = 'Fall Guys'; Url = $steam + '1097150/ss_9e380dcadcaa9d0cd6c34b0a508f2624030b135b.1920x1080.jpg'; Sha256 = '6E74DDAEF731075F32831089328B44E973665D924D1728F85F85978C20E07C4E'; Seconds = 15; Ago = 7; Title = '' }
    @{ Game = 'Cyberpunk 2077'; Url = $steam + '1091500/900f9bda156599fcd235f08ffb64667f9baa1afe/ss_900f9bda156599fcd235f08ffb64667f9baa1afe.1920x1080.jpg'; Sha256 = '015E9B780D069251296A0B648906CA9A3D24A1EA10C5BC59E2BC8BDC3971130F'; Seconds = 12; Ago = 20; Title = '' }
    @{ Game = 'Sea of Thieves'; Url = $steam + '1172620/ss_ec623c77d75dfa098c622b547b1ab21ad4cae0a8.1920x1080.jpg'; Sha256 = '57282C5F5DB327E4A87A85C33C9303F0FA152B3884E758D759C938D64C7D78EB'; Seconds = 45; Ago = 26; Title = 'Late night session' }
    @{ Game = 'Subnautica'; Url = $steam + '264710/ss_5f2f2ea498cdc632cbffd6cf37c1a09670eb3272.1920x1080.jpg'; Sha256 = 'E63AAC2AB7B75FC0E68A44D414CB8D7F370AC7B20E4F0EE76EDE33E94C44893F'; Seconds = 18; Ago = 50; Title = '' }
    @{ Game = 'Hollow Knight'; Url = $steam + '367520/ss_a81e4231cc8d55f58b51a4a938898af46503cae5.1920x1080.jpg'; Sha256 = '2BC4CB29048A712281822A3A28CC6CD02E51E3FED89B216C27667159847DFE41'; Seconds = 9; Ago = 75; Title = '' }
    @{ Game = 'Apex Legends'; Url = $steam + '1172470/61bab081f3fdc1373da42fa962497ed2ea760890/ss_61bab081f3fdc1373da42fa962497ed2ea760890.1920x1080.jpg'; Sha256 = 'D635DC74E946D8EEFCC168CB4D6A3C22C2F71C3F525E5D8F28A85CA1ADFF10BE'; Seconds = 27; Ago = 100; Title = 'Ace' }
    @{ Game = 'Elden Ring'; Url = $steam + '1245620/ss_943bf6fe62352757d9070c1d33e50b92fe8539f1.1920x1080.jpg'; Sha256 = 'A36496EDE706BE315339F1AB0D4AACBB7269EC29870FBC9902AF0D36ED06EE42'; Seconds = 14; Ago = 130; Title = '' }
    @{ Game = 'Forza Horizon 5'; Url = $steam + '1551360/ss_00f0090174380eeaf8753bd3d1028b6772c3aebf.1920x1080.jpg'; Sha256 = '73E57D2E4B12DFA404387B0D87730825BC3B44897E8F1E44FADD0C1DF9C46D35'; Seconds = 31; Ago = 170; Title = '' }
)
$demoShots = @(
    @{ Game = 'Terraria'; Url = $steam + '105600/ss_8c03886f214d2108cafca13845533eaa3d87d83f.1920x1080.jpg'; Sha256 = '2D87F640DA5802848F7B4D2FB6EAFE5B8B40FBE165E26DCF38B4D70618E032CB'; Ago = 5 }
    @{ Game = "No Man's Sky"; Url = $steam + '275850/6c2ddcffe192b0e155a69aa2a1dd3f33b863033d/ss_6c2ddcffe192b0e155a69aa2a1dd3f33b863033d.1920x1080.jpg'; Sha256 = '9FA9C8BC9A6D0C3CDBD997DD3386229BF764D47EE061F3CA4A9ED18A6F3467BA'; Ago = 30 }
    @{ Game = 'Forza Horizon 5'; Url = $steam + '1551360/ss_cf56e25a0290556ba83229eb0ab370d10be0407c.1920x1080.jpg'; Sha256 = 'C84EF0B44C967E8F6C87A115902195F11DA459F6793FB34FD187652FAC1B6511'; Ago = 60 }
    @{ Game = 'Hollow Knight'; Url = $steam + '367520/ss_d5b6edd94e77ba6db31c44d8a3c09d807ab27751.1920x1080.jpg'; Sha256 = '41E03473A09CACB6C271E4A861BCD4B2E1924C64D4EF56DE40051E3ABA44E910'; Ago = 90 }
)
$favoriteIndexes = @(1, 4, 7)
$editorGame = 'Elden Ring'

$failures = New-Object System.Collections.Generic.List[string]
$made = New-Object System.Collections.Generic.List[string]
$restore = @{}

try {
    Write-Host 'Checking the running ReplayKit...'
    try { $info = Invoke-Sdk 'helper.info' }
    catch { throw "The ReplayKit helper did not answer at $helper. Start OBS with ReplayKit and turn on Development mode and SDK mode in ReplayKit Settings > Advanced. ($($_.Exception.Message))" }
    if (-not $info.pluginConnected) { throw 'The ReplayKit plugin is not connected to the helper yet. Wait for OBS to finish starting.' }
    $script:ffmpeg = $info.ffmpeg
    if (-not $script:ffmpeg -or -not (Test-Path -LiteralPath $script:ffmpeg)) { throw 'The helper has no ffmpeg to make the demo clips with.' }
    $ui = Invoke-Sdk 'plugin.ui.state'
    if (-not $ui.shell) { throw 'The Clean layout has to be on to take the screenshots.' }
    if ($ui.setupLocked) { throw 'Finish the ReplayKit setup first.' }
    # an open editor can hold unsaved work, which a change of the clip folder must never touch
    if ($ui.editorOpen.clips -or $ui.editorOpen.home) { throw 'Close the clip editor first.' }
    $jobs = Invoke-RestMethod -Uri "$helper/status" -TimeoutSec 10
    if ($jobs.activeJobs -gt 0 -or $jobs.clipSave.active) { throw 'Wait for the running upload, compression or clip save to finish first.' }

    Write-Host 'Getting the game pictures...'
    New-Item -ItemType Directory -Path $sourcesDir -Force | Out-Null
    $clipPictures = @($demoClips | ForEach-Object { Get-SourcePicture $_ })
    $shotPictures = @($demoShots | ForEach-Object { Get-SourcePicture $_ })

    # the demo folder is ours alone, so an old one is cleared out before it is filled again
    if (Test-Path -LiteralPath $demoDir) { Remove-Item -LiteralPath $demoDir -Recurse -Force }
    New-Item -ItemType Directory -Path (Join-Path $demoDir 'Screenshots'), $shotsDir | Out-Null

    Write-Host 'Making the demo clips...'
    $names = @()
    for ($i = 0; $i -lt $demoClips.Count; $i++) {
        $clip = $demoClips[$i]
        $when = $now.AddHours(-$clip.Ago)
        $name = if ($clip.Title) { "$($clip.Title).mp4" } else { "$($clip.Game) " + $when.ToString('yyyy-MM-dd HH-mm-ss') + '.mp4' }
        $path = Join-Path $demoDir $name
        New-DemoClip $path $clipPictures[$i] $clip.Seconds $i
        # an old write time keeps a clip from showing as just saved and still processing
        Set-FileTime $path $when
        $names += $name
        $made.Add($path)
        Write-Host "  $name"
    }
    for ($i = 0; $i -lt $demoShots.Count; $i++) {
        $when = $now.AddHours(-$demoShots[$i].Ago)
        $shot = Join-Path $demoDir ("Screenshots\$($demoShots[$i].Game) " + $when.ToString('yyyy-MM-dd HH-mm-ss') + '.png')
        Invoke-Ffmpeg @('-i', $shotPictures[$i], '-frames:v', '1', $shot)
        Set-FileTime $shot $when
        $made.Add($shot)
    }

    $settings = (Invoke-Sdk 'settings.get' @{ keys = @('clipDir') }).settings
    $restore.clipDir = [string]$settings.clipDir
    $restore.view = [string]$ui.view
    $restore.visible = [bool]$ui.visible
    $restore.minimized = [bool]$ui.minimized

    Write-Host 'Pointing the clip folder at the demo clips...'
    Invoke-Sdk 'settings.set' @{ values = @{ clipDir = $demoDir } } | Out-Null
    $restore.clipDirChanged = $true
    Wait-For 'the demo clips to be listed' 60 {
        # assigned first, since windows powershell hands a json array out of Invoke-RestMethod as one object
        $listed = Invoke-RestMethod -Uri "$helper/clips" -TimeoutSec 10
        $ready = @($listed | Where-Object { $names -contains $_.name -and $_.duration -gt 0 })
        $ready.Count -eq $names.Count
    }
    foreach ($name in $names) { Invoke-WebRequest -Uri ("$helper/thumb/" + [Uri]::EscapeDataString($name)) -UseBasicParsing -TimeoutSec 60 | Out-Null }
    $state = Invoke-RestMethod -Uri "$helper/clips/state" -TimeoutSec 10
    $favoriteBody = @{ folderKey = $state.folderKey; revision = $state.revision; favorites = @($favoriteIndexes | ForEach-Object { $names[$_] }) } | ConvertTo-Json -Compress
    Invoke-RestMethod -Uri "$helper/clips/state" -Method Post -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($favoriteBody)) -TimeoutSec 10 | Out-Null
    $restore.favoritesKey = $state.folderKey

    # the account picture is personal, so the title bar shows the plain circle while the pictures are taken
    Invoke-Sdk 'account.picture' @{ hidden = $true } | Out-Null
    $restore.pictureHidden = $true

    Write-Host 'Taking the screenshots...'
    if (-not $ui.visible -or $ui.minimized) { Invoke-Sdk 'plugin.ui.window' @{ mode = 'show' } | Out-Null }
    # a browser page behind other windows can stop painting, so the window is brought to the front
    Invoke-Sdk 'plugin.ui.bring-forward' | Out-Null
    $window = Get-ObsWindow
    $restore.maximized = [bool][RkWindowPicture]::IsZoomed($window)
    # 9 is SW_RESTORE, which a maximized window needs before it takes another size
    if ($restore.maximized) { [RkWindowPicture]::ShowWindow($window, 9) | Out-Null; Start-Sleep -Milliseconds 600 }
    $restore.rect = Get-WindowRect $window
    Set-WindowRect $window $restore.rect.Left $restore.rect.Top $shotWidth $shotHeight
    $restore.resized = $true
    Wait-For "the OBS window to be $($shotWidth)x$($shotHeight)" 10 {
        $size = Get-WindowRect (Get-ObsWindow)
        ($size.Right - $size.Left) -eq $shotWidth -and ($size.Bottom - $size.Top) -eq $shotHeight
    }
    Start-Sleep -Seconds 1

    Invoke-Sdk 'plugin.ui.view' @{ view = 'clips' } | Out-Null
    # a window that sat in the tray loads its pages again as it shows, and the sections can only be opened once the clips page is in
    Start-Sleep -Seconds 5
    # favorites and all clips show open in the picture even where they are collapsed here, and the saved choice is left alone
    Invoke-Sdk 'plugin.ui.clips-sections' @{ open = @('favorites', 'clips') } | Out-Null
    $restore.sections = $true
    Start-Sleep -Seconds 2
    Save-WindowPicture 'clip-library'

    $editorIndex = -1
    for ($i = 0; $i -lt $demoClips.Count; $i++) { if ($demoClips[$i].Game -eq $editorGame) { $editorIndex = $i } }
    if ($editorIndex -lt 0) { throw "No demo clip is of $editorGame." }
    Invoke-Sdk 'plugin.ui.editor' @{ name = $names[$editorIndex] } | Out-Null
    $restore.editor = $true
    Wait-For 'the editor to open' 30 { (Invoke-Sdk 'plugin.ui.state').editorOpen.clips }
    # the film strip and the waveforms of the three tracks are made after the editor opens
    Start-Sleep -Seconds 8
    # the picture shows the video picked with its panel on the right and the subtitles tab open on the left
    Invoke-Sdk 'plugin.ui.editor' @{ name = $names[$editorIndex]; select = 'video'; tab = 'subtitles' } | Out-Null
    Start-Sleep -Seconds 1
    Save-WindowPicture 'clip-editor'
    Invoke-Sdk 'plugin.ui.editor' | Out-Null
    Wait-For 'the editor to close' 20 { -not (Invoke-Sdk 'plugin.ui.state').editorOpen.clips }
    $restore.editor = $false

    Invoke-RestMethod -Uri "$helper/open-settings-window?page=output" -Method Post -TimeoutSec 10 | Out-Null
    Start-Sleep -Seconds 4
    Save-WindowPicture 'settings'

    foreach ($picture in Get-ChildItem -LiteralPath $shotsDir -Filter '*.png') {
        Copy-Item -LiteralPath $picture.FullName -Destination (Join-Path $imagesDir $picture.Name) -Force
    }
    Write-Host "The screenshots are in $imagesDir"
}
catch {
    $failures.Add($_.Exception.Message)
}
finally {
    # every step that changed something is put back on its own, so one that fails does not keep the others from running
    if ($restore.editor) {
        try { Invoke-Sdk 'plugin.ui.editor' | Out-Null; Start-Sleep -Seconds 2 } catch { $failures.Add("Closing the editor: $($_.Exception.Message)") }
    }
    if ($restore.sections) {
        try { Invoke-Sdk 'plugin.ui.clips-sections' @{ open = @() } | Out-Null } catch { $failures.Add("Showing the sections of the clips view as saved again: $($_.Exception.Message)") }
    }
    if ($restore.favoritesKey) {
        try {
            $state = Invoke-RestMethod -Uri "$helper/clips/state" -TimeoutSec 10
            if ($state.folderKey -eq $restore.favoritesKey) {
                $clearBody = @{ folderKey = $state.folderKey; revision = $state.revision; favorites = @() } | ConvertTo-Json -Compress
                Invoke-RestMethod -Uri "$helper/clips/state" -Method Post -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($clearBody)) -TimeoutSec 10 | Out-Null
            }
        } catch { $failures.Add("Clearing the demo favorites: $($_.Exception.Message)") }
    }
    if ($restore.clipDirChanged) {
        try { Invoke-Sdk 'settings.set' @{ values = @{ clipDir = $restore.clipDir } } | Out-Null; Write-Host "The clip folder is back on $($restore.clipDir)" }
        catch { $failures.Add("Setting the clip folder back to $($restore.clipDir): $($_.Exception.Message)") }
    }
    if ($restore.view) {
        try { Invoke-Sdk 'plugin.ui.view' @{ view = $restore.view } | Out-Null } catch { $failures.Add("Showing the $($restore.view) view again: $($_.Exception.Message)") }
    }
    if ($restore.pictureHidden) {
        try { Invoke-Sdk 'account.picture' @{ hidden = $false } | Out-Null } catch { $failures.Add("Showing the account picture again: $($_.Exception.Message)") }
    }
    if ($restore.resized -or $restore.maximized) {
        try {
            $window = Get-ObsWindow
            if ($restore.rect) { Set-WindowRect $window $restore.rect.Left $restore.rect.Top ($restore.rect.Right - $restore.rect.Left) ($restore.rect.Bottom - $restore.rect.Top) }
            # 3 is SW_MAXIMIZE
            if ($restore.maximized) { [RkWindowPicture]::ShowWindow($window, 3) | Out-Null }
        } catch { $failures.Add("Putting the size of the OBS window back: $($_.Exception.Message)") }
    }
    if ($restore.ContainsKey('visible')) {
        try {
            if (-not $restore.visible) { Invoke-Sdk 'plugin.ui.window' @{ mode = 'hide' } | Out-Null }
            # 6 is SW_MINIMIZE
            elseif ($restore.minimized) { [RkWindowPicture]::ShowWindow((Get-ObsWindow), 6) | Out-Null }
        } catch { $failures.Add("Putting the OBS window back: $($_.Exception.Message)") }
    }
    if ($restore.clipDirChanged) {
        # anything in the demo folder that this script did not make, such as a clip saved while it ran, stays where it is
        try {
            Start-Sleep -Seconds 2
            foreach ($path in $made) {
                $leaf = Split-Path -Leaf $path
                Get-ChildItem -LiteralPath $demoDir -Recurse -File -Filter $leaf -ErrorAction SilentlyContinue | Remove-Item -Force
            }
            $left = @(Get-ChildItem -LiteralPath $demoDir -Recurse -File | Where-Object { $_.Extension -match '^\.(mp4|mkv|mov|png|jpg|gif)$' -and -not $_.FullName.StartsWith($shotsDir) })
            if ($left.Count -eq 0) { Remove-Item -LiteralPath $demoDir -Recurse -Force }
            else { Write-Host "Left $demoDir in place: it holds $($left.Count) file(s) this script did not make." -ForegroundColor Yellow }
        } catch { $failures.Add("Removing the demo folder $($demoDir): $($_.Exception.Message)") }
    }
}

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) { Write-Host "ERROR: $failure" -ForegroundColor Red }
    exit 1
}
Write-Host 'Done.' -ForegroundColor Green
exit 0

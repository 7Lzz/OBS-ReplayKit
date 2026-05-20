# request dispatcher.
function Dispatch-Request($stream, [hashtable]$req) {
    Load-Config

    if ($req.Method -eq 'OPTIONS') {
        $h = Get-NoStoreHeaders @{}
        Send-Bytes $stream 204 'No Content' $h ([byte[]]@())
        return
    }

    $path  = $req.Path
    $query = $req.Query

    switch -Regex ($path) {
        '^/(?:controls\.html)?$' { Serve-Html $stream 'controls.html'; return }
        '^/controls_app\.html$' { Serve-Html $stream 'controls_app.html'; return }
        '^/clips-view$|^/clips\.html$' { Serve-Html $stream 'clips.html'; return }
        '^/signin-windows-status$' {
            # returns visible sign-in popups owned by obss process family so the dock can update its status label. with overlay=1, the google oauth popup is moved onto the streamable login window so the flow presents as one window while preserving streamables popup-based oauth.
            if ($req.Method -ne 'GET') { Send-Text $stream 405 'Method Not Allowed' 'GET required'; return }
            $ownerPid = if ($script:ParentPid) { [uint32]$script:ParentPid } else { [uint32]0 }
            $jsonArr = '[]'
            # hidegoogle=false: the google oauth popup must stay visible in case the user needs to pick an account, enter a password, or complete mfa. hiding it would soft-lock interactive sign-ins. status-update polling is the win; the popup gets wm_closed as soon as cookies land anyway, so its visible duration is ~500-1000ms.
            try {
                $overlayGoogle = $query.ContainsKey('overlay') -and [string]$query['overlay'] -eq '1'
                $jsonArr = [StreamableNative]::ListSignInWindows($ownerPid, $false, $overlayGoogle)
            } catch {
                Write-Log "/signin-windows-status: $($_.Exception.Message)"
            }
            $h = Get-NoStoreHeaders @{ 'Content-Type' = 'application/json' }
            Send-Bytes $stream 200 'OK' $h ([System.Text.Encoding]::UTF8.GetBytes($jsonArr))
            return
        }
        '^/close-signin-windows$' {
            # post to nuke the stuck sign-in popups via win32 wm_close. the dock js calls this after /import-session succeeds becuase cef refuses window.close() on cross-origin popups but windows-level wm_close works fine. we match only windows owned by the parent obs process, by title.
            if ($req.Method -ne 'POST') { Send-Text $stream 405 'Method Not Allowed' 'POST required'; return }
            $ownerPid = if ($script:ParentPid) { [uint32]$script:ParentPid } else { [uint32]0 }
            # specific titles only. we deliberately do not include a bare "streamable" since that would also match the clip-viewer popup (clips.html) and any other dock title that contains "streamable".
            $titles = @(
                'about:blank',
                'Sign In - Google Accounts',
                'Sign in - Google Accounts',
                'Signing in to Streamable',
                'Dashboard - Streamable',
                'Log in - Streamable',
                'Sign in to Streamable - Streamable',
                'Streamable | '
            )
            $n = 0
            try {
                $n = [StreamableNative]::CloseWindowsByTitle($titles, $ownerPid)
                Write-Log "/close-signin-windows: closed $n window(s) under OBS pid=$ownerPid"
            } catch {
                Write-Log "/close-signin-windows: $($_.Exception.Message)"
            }
            Send-Json $stream 200 @{ ok = $true; closed = $n }
            return
        }
        '^/sign-in-loader$' {
            # same-origin loading page that the docks popup navigates to instead of about:blank + document.write. removing the document.write step dodges a cef bug where the popup windows document isnt yet writable at the time window.open returns -- the popup just sits on about:blank forever. by having the helper serve this page directly, the popup gets a proper navigation from the start and paints reliably. the meta-refresh then takes it to streamable.com once the spinner has had a moment to show.
            $h = Get-NoStoreHeaders @{ 'Content-Type' = 'text/html; charset=utf-8' }
            $cb = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
            $body = @"
<!doctype html><html><head>
<meta charset="utf-8">
<meta http-equiv="refresh" content="0.4;url=https://streamable.com/login?_cb=$cb">
<title>Signing in to Streamable</title>
<style>
html,body{margin:0;height:100%;background:#1D1F26;
color:#FFFFFF;font:13px "Segoe UI",system-ui,sans-serif}
.wrap{display:flex;flex-direction:column;align-items:center;
justify-content:center;height:100%;gap:18px}
.spinner{width:34px;height:34px;border-radius:50%;
border:3px solid #3C404D;border-top-color:#476BD7;
animation:r 0.8s linear infinite}
@keyframes r{to{transform:rotate(360deg)}}
.label{color:#FFFFFF;font-size:14px;font-weight:500}
.sub{color:#969696;font-size:12px}
</style></head><body>
<div class="wrap">
<div class="spinner"></div>
<div class="label">Opening Streamable&hellip;</div>
<div class="sub">Sign in with Google, Facebook, or email</div>
</div></body></html>
"@
            Send-Bytes $stream 200 'OK' $h ([System.Text.Encoding]::UTF8.GetBytes($body))
            return
        }
        '^/obs-icon\.svg$|^/favicon\.ico$' {
            $h = Get-NoStoreHeaders @{ 'Content-Type' = 'image/svg+xml' }
            Send-Bytes $stream 200 'OK' $h (Get-ObsIconSvg)
            return
        }
        '^/obs-icon\.ico$' { Serve-ObsIcon $stream; return }
        '^/style-window$' {
            $title = if ($query.ContainsKey('title')) { $query['title'] } else { 'Clips' }
            $taskbar = $query.ContainsKey('taskbar') -and ([string]$query['taskbar'] -eq '1')
            $matched = -1
            try {
                $matched = [int][StreamableNative]::StyleWindow($title, $script:OBS_EXE, $taskbar)
            } catch {
                Write-Log "/style-window threw: $($_.Exception.Message)"
            }
            # logging only on misses keeps the log readable; success is the silent defualt.
            if ($matched -le 0) {
                Write-Log ("/style-window title=" + $title + " : 0 matching OBS-family windows found")
            }
            Send-Json $stream 200 @{ ok = $true; matched = $matched }
            return
        }
        '^/focus-window$' {
            # server-side foreground-raise for an existing dock-spawned popup (e.g. "view clips" clicked while the clips window is already open). the docks window.focus() is unreliable inside cef; this route uses win32 setforegroundwindow + the attachthreadinput dance which works regardless of who currently owns the foreground.
            $title = if ($query.ContainsKey('title')) { $query['title'] } else { 'Clips' }
            $focused = $false
            try { $focused = [StreamableNative]::FocusWindow($title) } catch {}
            Send-Json $stream 200 @{ ok = $true; focused = [bool]$focused }
            return
        }
        '^/window/maximize$' {
            # server-side maximize for a dock-spawned popup. called from clips.html when the user enters fullscreen on a clip so the host window fills the screen. js in cef cant resize its own host, hence the win32 round-trip. the native side snapshots the original rect so /window/restore can put it back unless the user manually unmaximized or dragged the host while fullscreened.
            $title = if ($query.ContainsKey('title')) { $query['title'] } else { 'Clips' }
            $maximized = $false
            try { $maximized = [StreamableNative]::MaximizeObsWindow($title) } catch {}
            Send-Json $stream 200 @{ ok = $true; maximized = [bool]$maximized }
            return
        }
        '^/window/restore$' {
            # counterpart to /window/maximize: restore the saved rect only if the Clips host is still maximized. if the user dragged or unmaximized it while fullscreened, their manual placement wins.
            $title = if ($query.ContainsKey('title')) { $query['title'] } else { 'Clips' }
            $restored = $false
            try { $restored = [StreamableNative]::RestoreObsWindow($title) } catch {}
            Send-Json $stream 200 @{ ok = $true; restored = [bool]$restored }
            return
        }
        '^/open_clips$' {
            $h = Get-NoStoreHeaders @{ 'Location' = '/clips-view' }
            Send-Bytes $stream 302 'Found' $h ([byte[]]@())
            return
        }
        '^/status$' {
            Send-Json $stream 200 (Get-UploadStatusSnapshot)
            return
        }
        '^/clips$' {
            try {
                if ($query.ContainsKey('refresh') -and [string]$query['refresh'] -eq '1') {
                    Clear-ClipsCache
                }
                if ($query.ContainsKey('offset') -or $query.ContainsKey('limit')) {
                    [int]$offset = 0
                    [int]$limit = $script:CLIPS_PAGE_LIMIT_MAX
                    if ($query.ContainsKey('offset')) { [void][int]::TryParse(([string]$query['offset']), [ref]$offset) }
                    if ($query.ContainsKey('limit')) { [void][int]::TryParse(([string]$query['limit']), [ref]$limit) }
                    Send-Text $stream 200 'OK' (Get-ClipsPageJson $offset $limit) 'application/json; charset=utf-8'
                } else {
                    Send-Text $stream 200 'OK' (Get-ClipsListJson) 'application/json; charset=utf-8'
                }
            }
            catch { Send-Json $stream 500 @{ error = $_.Exception.Message } }
            return
        }
        '^/capabilities$' {
            $caps = Get-HelperCapabilities
            Send-Json $stream 200 @{
                ok       = $true
                ffmpeg   = [bool]$caps.ffmpeg
                ffprobe  = [bool]$caps.ffprobe
                logging  = [bool]$caps.logging
            }
            return
        }
        '^/upload-latest$' {
            if ($req.Method -ne 'POST') { Send-Text $stream 405 'Method Not Allowed' 'POST required'; return }
            try {
                $result = Start-StreamableUpload $null
                Write-Log "/upload-latest -> $(ConvertTo-Json $result -Compress -Depth 4)"
                $code = if ($result.ok) { 200 } elseif ($result.busy) { 409 } else { 400 }
                Send-Json $stream $code $result
            } catch {
                $msg = $_.Exception.Message
                Write-Log "/upload-latest threw: $msg`n$($_.ScriptStackTrace)"
                Send-Json $stream 500 @{ ok = $false; message = "Helper error: $msg" }
            }
            return
        }
        '^/save-replay$' {
            if ($req.Method -ne 'POST') { Send-Text $stream 405 'Method Not Allowed' 'POST required'; return }
            try {
                $result = Invoke-ObsSaveReplayBuffer
                $code = if ($result.ok) { 200 } elseif ($result.unavailable) { 503 } else { 409 }
                Send-Json $stream $code $result
            } catch {
                Send-Json $stream 500 @{ ok = $false; message = $_.Exception.Message }
            }
            return
        }
        '^/upload$' {
            if ($req.Method -ne 'POST') { Send-Text $stream 405 'Method Not Allowed' 'POST required'; return }
            $file = if ($query.ContainsKey('file')) { $query['file'] } else { $null }
            $selected = Get-SafeClipPath $file
            if (-not $selected) { Send-Json $stream 400 @{ ok = $false; message = 'Bad filename' }; return }
            try {
                $result = Start-StreamableUpload $selected
                Write-Log "/upload($($selected.name)) -> $(ConvertTo-Json $result -Compress -Depth 4)"
                $code = if ($result.ok) { 200 } elseif ($result.busy) { 409 } else { 400 }
                Send-Json $stream $code $result
            } catch {
                $msg = $_.Exception.Message
                Write-Log "/upload threw: $msg`n$($_.ScriptStackTrace)"
                Send-Json $stream 500 @{ ok = $false; message = "Helper error: $msg" }
            }
            return
        }
        '^/cancel-upload$' {
            if ($req.Method -ne 'POST') { Send-Text $stream 405 'Method Not Allowed' 'POST required'; return }
            try {
                $file = if ($query.ContainsKey('file')) { [string]$query['file'] } else { '' }
                $requestId = if ($query.ContainsKey('requestId')) { [string]$query['requestId'] } else { '' }
                $result = Cancel-ActiveUpload $file $requestId
                $code = if ($result.ok) { 200 } else { 409 }
                Send-Json $stream $code $result
            } catch {
                Send-Json $stream 500 @{ ok = $false; message = $_.Exception.Message }
            }
            return
        }
        '^/file/' {
            Serve-Preview $stream $req ($path.Substring(6))
            return
        }
        '^/thumb/' {
            Serve-Thumbnail $stream ($path.Substring(7))
            return
        }
        '^/open-clip-folder$' {
            if ($req.Method -ne 'POST') { Send-Text $stream 405 'Method Not Allowed' 'POST required'; return }
            $root = [System.IO.Path]::GetFullPath((Get-ClipDir))
            if (-not (Test-Path -LiteralPath $root -PathType Container)) {
                Send-Json $stream 404 @{ ok = $false; message = 'Clip folder not found' }; return
            }
            try {
                # dedupe: if explorer already has the clip folder open, raise it instead of spawning another window. falls thru to the normal launch path if the com probe fails or no match is found.
                $existing = Get-ExplorerHwndForPath $root
                if ($existing -ne [IntPtr]::Zero) {
                    $focused = [StreamableNative]::FocusHwnd($existing)
                    Send-Json $stream 200 @{ ok = $true; focused = $focused }
                    return
                }
                $psi = New-Object System.Diagnostics.ProcessStartInfo
                $psi.FileName        = 'explorer.exe'
                $psi.Arguments       = Quote-Arg $root
                $psi.UseShellExecute = $false
                [void][System.Diagnostics.Process]::Start($psi)
                Send-Json $stream 200 @{ ok = $true; focused = $false }
            } catch {
                Send-Json $stream 500 @{ ok = $false; message = $_.Exception.Message }
            }
            return
        }
        '^/open-folder$' {
            if ($req.Method -ne 'POST') { Send-Text $stream 405 'Method Not Allowed' 'POST required'; return }
            $file = if ($query.ContainsKey('file')) { $query['file'] } else { $null }
            $selected = Get-SafeClipPath $file
            if (-not $selected -or -not (Test-Path -LiteralPath $selected.full)) {
                Send-Json $stream 404 @{ ok = $false; message = 'Clip not found' }; return
            }
            try {
                # if explorer already has the containing folder open, raise that window. we deliberately dont try to re-select the file via shell.application -- the com dance is fragile and the user clicking "open file location" from a clip card is usually just trying to surface the folder, not the specific row.
                $parent = [System.IO.Path]::GetDirectoryName($selected.full)
                if ($parent) {
                    $existing = Get-ExplorerHwndForPath $parent
                    if ($existing -ne [IntPtr]::Zero) {
                        $focused = [StreamableNative]::FocusHwnd($existing)
                        Send-Json $stream 200 @{ ok = $true; focused = $focused }
                        return
                    }
                }
                # explorer.exe /select,"<path>" opens the containing folder with the file already highlighted. arguments must be one string becuase explorer parses /select,<rest> as a unit.
                $psi = New-Object System.Diagnostics.ProcessStartInfo
                $psi.FileName        = 'explorer.exe'
                $psi.Arguments       = '/select,' + (Quote-Arg $selected.full)
                $psi.UseShellExecute = $false
                [void][System.Diagnostics.Process]::Start($psi)
                Send-Json $stream 200 @{ ok = $true; focused = $false }
            } catch {
                Send-Json $stream 500 @{ ok = $false; message = $_.Exception.Message }
            }
            return
        }
        '^/delete$' {
            if ($req.Method -ne 'POST') { Send-Text $stream 405 'Method Not Allowed' 'POST required'; return }
            $file = if ($query.ContainsKey('file')) { $query['file'] } else { $null }
            $selected = Get-SafeClipPath $file
            if (-not $selected -or -not (Test-Path -LiteralPath $selected.full)) {
                Send-Json $stream 404 @{ ok = $false; message = 'Clip not found' }; return
            }
            if (Test-ClipHasActiveUploadJob $selected.name) {
                Send-Json $stream 409 @{ ok = $false; message = 'Clip is currently being processed' }; return
            }
            try {
                Remove-Item -LiteralPath $selected.full -Force -ErrorAction Stop
                $db = Read-ClipsDb
                if ($db.ContainsKey($selected.name)) {
                    $db.Remove($selected.name)
                    Save-ClipsDb $db
                }
                Clear-ClipsCache
                Send-Json $stream 200 @{ ok = $true; name = $selected.name }
            } catch {
                Send-Json $stream 500 @{ ok = $false; message = $_.Exception.Message }
            }
            return
        }
        '^/compress$' {
            if ($req.Method -ne 'POST') { Send-Text $stream 405 'Method Not Allowed' 'POST required'; return }
            $file = if ($query.ContainsKey('file')) { $query['file'] } else { $null }
            $selected = Get-SafeClipPath $file
            if (-not $selected -or -not (Test-Path -LiteralPath $selected.full)) {
                Send-Json $stream 404 @{ ok = $false; message = 'Clip not found' }; return
            }
            try {
                $result = Start-CompressedStreamableUpload $selected
                Write-Log "/compress($($selected.name)) -> $(ConvertTo-Json $result -Compress -Depth 4)"
                $code = if ($result.ok) { 200 } elseif ($result.busy) { 409 } else { 400 }
                Send-Json $stream $code $result
            } catch {
                Write-Log "/compress threw: $($_.Exception.Message)`n$($_.ScriptStackTrace)"
                Send-Json $stream 500 @{ ok = $false; message = $_.Exception.Message }
            }
            return
        }
        '^/trim$' {
            if ($req.Method -ne 'POST') { Send-Text $stream 405 'Method Not Allowed' 'POST required'; return }
            $file  = if ($query.ContainsKey('file'))  { [string]$query['file']  } else { '' }
            $startStr = if ($query.ContainsKey('start')) { [string]$query['start'] } else { '' }
            $endStr   = if ($query.ContainsKey('end'))   { [string]$query['end']   } else { '' }
            $mode = if ($query.ContainsKey('mode'))  { [string]$query['mode']  } else { 'copy' }
            # precise=1 -> re-encode for frame-accurate cuts. precise=0 (default) -> stream-copy, keyframe-snapped (fast lossless).
            $preciseStr = if ($query.ContainsKey('precise')) { [string]$query['precise'] } else { '0' }
            $precise = ($preciseStr -ne '0' -and $preciseStr.ToLowerInvariant() -ne 'false')
            $startSec = [double]::NaN
            $endSec   = [double]::NaN
            $ci = [System.Globalization.CultureInfo]::InvariantCulture
            [void][double]::TryParse($startStr, [System.Globalization.NumberStyles]::Float, $ci, [ref]$startSec)
            [void][double]::TryParse($endStr,   [System.Globalization.NumberStyles]::Float, $ci, [ref]$endSec)
            try {
                $result = Invoke-ClipTrim $file $startSec $endSec $mode $precise
                $code = if ($result.ok) { 200 } else { 400 }
                Send-Json $stream $code $result
            } catch {
                Write-Log "/trim threw: $($_.Exception.Message)`n$($_.ScriptStackTrace)"
                Send-Json $stream 500 @{ ok = $false; message = $_.Exception.Message }
            }
            return
        }
        '^/clipboard$' {
            if ($req.Method -ne 'POST') { Send-Text $stream 405 'Method Not Allowed' 'POST required'; return }
            $file = if ($query.ContainsKey('file')) { [string]$query['file'] } else { '' }
            try {
                $result = Set-FileClipboard $file
                $code = if ($result.ok) { 200 } else { 400 }
                Send-Json $stream $code $result
            } catch {
                Write-Log "/clipboard threw: $($_.Exception.Message)`n$($_.ScriptStackTrace)"
                Send-Json $stream 500 @{ ok = $false; message = $_.Exception.Message }
            }
            return
        }
        '^/compress-overwrite$' {
            if ($req.Method -ne 'POST') { Send-Text $stream 405 'Method Not Allowed' 'POST required'; return }
            $file = if ($query.ContainsKey('file')) { [string]$query['file'] } else { '' }
            $mode = if ($query.ContainsKey('mode')) { [string]$query['mode'] } else { 'smaller' }
            # compress-history rules (enforced server-side as defense in depth; the dock ui also disables the relevant card): clip already marked slow (libx265 medium) -> reject entirely. slow is the best mode; re-running anything would just degrade quality without size gain. clip already marked fast (libx264 fast) + fast again requested -> reject. re-fast-compressing produces no meaningful gain. re-running with smaller is allowed (fast -> slow is a valid upgrade path).
            $checkSel = Get-SafeClipPath $file
            if ($checkSel -and (Test-Path -LiteralPath $checkSel.full)) {
                $existingMarker = Get-CompressMarker $checkSel.full
                $existingMode = [string]$existingMarker.mode
                if ($existingMode -eq 'slow') {
                    Send-Json $stream 409 @{
                        ok = $false
                        message = 'Already at maximum compression (slow mode).'
                        alreadyCompressed = $true
                        existingMode = 'slow'
                    }
                    return
                }
                if ($existingMode -eq 'fast' -and $mode -eq 'fast') {
                    Send-Json $stream 409 @{
                        ok = $false
                        message = 'Already fast-compressed. Choose Smaller for further reduction.'
                        alreadyCompressed = $true
                        existingMode = 'fast'
                    }
                    return
                }
            }
            $selected = Get-SafeClipPath $file
            if (-not $selected -or -not (Test-Path -LiteralPath $selected.full)) {
                Send-Json $stream 404 @{ ok = $false; message = 'Clip not found' }; return
            }
            try {
                $result = Start-CompressOverwriteFile $selected $mode
                Write-Log "/compress-overwrite($($selected.name), mode=$mode) -> $(ConvertTo-Json $result -Compress -Depth 4)"
                $code = if ($result.ok) { 200 } elseif ($result.busy) { 409 } else { 400 }
                Send-Json $stream $code $result
            } catch {
                Write-Log "/compress-overwrite threw: $($_.Exception.Message)`n$($_.ScriptStackTrace)"
                Send-Json $stream 500 @{ ok = $false; message = $_.Exception.Message }
            }
            return
        }
        '^/rename$' {
            if ($req.Method -ne 'POST') { Send-Text $stream 405 'Method Not Allowed' 'POST required'; return }
            $file = if ($query.ContainsKey('file')) { $query['file'] } else { $null }
            $to   = if ($query.ContainsKey('to'))   { $query['to']   } else { $null }
            $selected = Get-SafeClipPath $file
            if (-not $selected -or -not (Test-Path -LiteralPath $selected.full)) {
                Send-Json $stream 404 @{ ok = $false; message = 'Source clip not found' }; return
            }
            $newName = Get-SafeFilename $to
            if (-not $newName) {
                Send-Json $stream 400 @{ ok = $false; message = 'Bad new filename' }; return
            }
            if ($newName -eq $selected.name) {
                Send-Json $stream 200 @{ ok = $true; name = $newName }; return
            }
            $newPath = Join-Path (Get-ClipDir) $newName
            if (Test-Path -LiteralPath $newPath) {
                Send-Json $stream 409 @{ ok = $false; message = 'A file with that name already exists' }; return
            }
            try {
                [System.IO.File]::Move($selected.full, $newPath)
                # preserve the streamable url association across the rename so the clip card still shows "copy link" instead of silently going back to "create link".
                $db = Read-ClipsDb
                if ($db.ContainsKey($selected.name)) {
                    $db[$newName] = $db[$selected.name]
                    $db.Remove($selected.name)
                    Save-ClipsDb $db
                }
                Clear-ClipsCache
                Send-Json $stream 200 @{ ok = $true; name = $newName }
            } catch {
                Send-Json $stream 500 @{ ok = $false; message = $_.Exception.Message }
            }
            return
        }
        '^/me$' {
            $a = $script:State.Auth
            $masked = Get-MaskedIdentity ([string]$a.username)
            Send-Json $stream 200 @{
                signedIn      = [bool]$a.signedIn
                username      = ''
                displayName   = if ($a.signedIn) { 'Signed in' } else { '' }
                maskedUsername = if ($a.signedIn) { $masked } else { '' }
                plan          = [string]$a.plan
                sizeCap       = [long]$a.sizeCap # bytes; 0 = unlimited
                retentionDays = [int]$a.retentionDays
            }
            return
        }
        '^/logout$' {
            if ($req.Method -ne 'POST') { Send-Text $stream 405 'Method Not Allowed' 'POST required'; return }
            try { Invoke-StreamableBackgroundLogout } catch { Write-Log "Background logout failed: $($_.Exception.Message)" }
            Clear-Auth
            Clear-ClipsCache
            Send-Json $stream 200 @{ ok = $true }
            return
        }
        '^/import-session$' {
            # find a chromium-based browser on the box that has a signed-in streamable.com session (edge, chrome, brave, vivaldi, opera, or obss own cef), decrypt the cookies, validate against /me, and adopt them as our auth state. the find-and-validate logic is shared with restore-... so we dont pick up stale logged-out cookies and clobber working auth.
            if ($req.Method -ne 'POST') { Send-Text $stream 405 'Method Not Allowed' 'POST required'; return }
            try {
                $sourceParam = if ($query.ContainsKey('source')) { [string]$query['source'] } else { '' }
                $obsOnly = $sourceParam -eq 'obs'
                Write-Log "/import-session source='$sourceParam' obsOnly=$obsOnly"
                $result = if ($obsOnly) {
                    Find-WorkingStreamableSession -ObsOnly
                } else {
                    Find-WorkingStreamableSession
                }
                if (-not $result.ok) {
                    Send-Json $stream 404 @{ ok = $false; message = $result.error }
                    return
                }
                Save-AuthBlob $result.jar $result.user
                Apply-Auth   $result.user $result.jar
                Clear-ClipsCache
                $a = $script:State.Auth
                $masked = Get-MaskedIdentity ([string]$a.username)
                Write-Log "Imported session from $($result.source) for $($a.username) (plan=$($a.plan))"
                Send-Json $stream 200 @{
                    ok            = $true
                    source        = [string]$result.source
                    signedIn      = [bool]$a.signedIn
                    username      = ''
                    displayName   = if ($a.signedIn) { 'Signed in' } else { '' }
                    maskedUsername = if ($a.signedIn) { $masked } else { '' }
                    plan          = [string]$a.plan
                    sizeCap       = [long]$a.sizeCap
                    retentionDays = [int]$a.retentionDays
                }
            } catch {
                Write-Log "/import-session threw: $($_.Exception.Message)`n$($_.ScriptStackTrace)"
                Send-Json $stream 500 @{ ok = $false; message = $_.Exception.Message }
            }
            return
        }
        '^/shutdown$' {
            Send-Text $stream 200 'OK' 'bye'
            Stop-ClipFolderWatcher
            $script:State.Shutdown = $true
            return
        }
        '^/restart-obs-clean$' {
            # full-cycle "force re-login": note the obs executable path before we kill it wipe the helpers saved auth state immediately send the response so the dock can react forcibly close obs64 + every obs-browser-page child set shutdown = true so our accept loop exits and the finally block runs the cookie cleanup (which needs the file unlocked, which happens once obs is dead) the finally block then re-launches obs via the path we saved, inheriting our admin token
            if ($req.Method -ne 'POST') { Send-Text $stream 405 'Method Not Allowed' 'POST required'; return }
            $obs = Get-Process obs64 -ErrorAction SilentlyContinue | Select-Object -First 1
            $obsPath = $null
            if ($obs) { $obsPath = $obs.Path }
            if (-not $obsPath) {
                try {
                    $cim = Get-CimInstance Win32_Process -Filter "ProcessId=$($obs.Id)" -ErrorAction SilentlyContinue
                    if ($cim) { $obsPath = $cim.ExecutablePath }
                } catch {}
            }
            if (-not $obsPath) {
                Send-Json $stream 500 @{ ok = $false; message = 'Could not locate OBS executable path to relaunch from.' }
                return
            }
            try { Clear-AuthBlob } catch {}
            try { Clear-Auth } catch {}
            $script:ClearStreamableOnExit = $true
            $script:RestartAfterClean    = @{ obsPath = $obsPath }
            Write-Log "/restart-obs-clean: queued. Will kill OBS, wipe streamable cookies, relaunch from '$obsPath'."
            Send-Json $stream 200 @{ ok = $true; message = 'OBS will restart with a clean Streamable session.' }

            # give the response a moment to flush before we kill obs (otherwise the docks fetch sees a torn connection).
            Start-Sleep -Milliseconds 300
            $procs = @(Get-Process obs64,obs-browser-page -ErrorAction SilentlyContinue)
            foreach ($p in $procs) {
                try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch {
                    Write-Log "Stop-Process PID $($p.Id) failed: $($_.Exception.Message)"
                }
            }
            $script:State.Shutdown = $true
            return
        }
    }
    Send-Text $stream 404 'Not Found' 'Not found'
}

# per-connection handler. parses just enough http to dispatch (request line + headers, no body for our routes -- everything is get/post with query params or empty body).

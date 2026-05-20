function Resolve-UploadAuthJar {
    if (-not $script:State.Auth.signedIn) {
        return @{ ok = $true; required = $false; path = '' }
    }
    $authJar = Get-AuthCookieJarPath
    if (-not (Test-Path -LiteralPath $authJar)) {
        return @{
            ok = $false
            required = $true
            path = ''
            message = 'Signed-in Streamable session is unavailable. Sign out and sign in again.'
        }
    }
    return @{ ok = $true; required = $true; path = $authJar }
}

function Start-StreamableUpload($selected, [string]$UploadPath = '', [string]$DisplayName = '') {
    Load-Config
    $requestId = New-RequestId

    $clip = $selected
    if (-not $clip) { $clip = Find-LatestClip }
    if (-not $clip) {
        Set-UploadState @{ requestId = $requestId; state = 'error'; active = $false; error = 'No mp4/mkv/mov clip found' }
        return @{ ok = $false; message = 'No mp4/mkv/mov clip found' }
    }

    $clipName = if ([string]::IsNullOrWhiteSpace($DisplayName)) { $clip.name } else { $DisplayName }
    $decision = Get-UploadJobStartDecision $clipName
    if (-not $decision.ok) { return $decision }

    $uploadPath = if ([string]::IsNullOrWhiteSpace($UploadPath)) { $clip.full } else { [System.IO.Path]::GetFullPath($UploadPath) }
    if (-not (Test-Path -LiteralPath $uploadPath)) {
        $msg = "Upload file not found: $uploadPath"
        Set-UploadState @{ requestId = $requestId; state = 'error'; active = $false; clipName = $clipName; error = $msg }
        return @{ ok = $false; message = $msg }
    }

    $fi = [System.IO.FileInfo]::new($uploadPath)
    $effCap = Get-EffectiveUploadCap
    if ($effCap -gt 0 -and $fi.Length -gt $effCap) {
        # keep this short -- the dock button caps display at 60 chars and the popup buttons cap at 28. "too big: 2980 mb / 250 mb limit" is informative and fits both. full context goes via toast in the popup.
        $mb  = [int][Math]::Ceiling($fi.Length / 1MB)
        $cap = [int]($effCap / 1MB)
        $msg = "Too big: $mb MB / $cap MB limit"
        Set-UploadState @{ requestId = $requestId; state = 'error'; active = $false; clipName = $clipName; error = $msg }
        return @{ ok = $false; message = $msg; tooBig = $true; sizeMb = $mb; capMb = $cap }
    }

    $ps = Get-PsHelperPath
    if (-not (Test-Path -LiteralPath $ps)) {
        $msg = "Missing PowerShell helper: $ps"
        Set-UploadState @{ requestId = $requestId; state = 'error'; active = $false; clipName = $clipName; error = $msg }
        return @{ ok = $false; message = $msg }
    }

    $auth = Resolve-UploadAuthJar
    if (-not $auth.ok) {
        $msg = [string]$auth.message
        Set-UploadState @{ requestId = $requestId; state = 'error'; active = $false; clipName = $clipName; error = $msg }
        return @{ ok = $false; message = $msg }
    }

    $statusPath = Get-UploadStatusPath $requestId
    $logFile = if ($script:LOG_ENABLED) { $script:UPLOAD_LOG_PATH } else { '' }

    Set-UploadState @{
        state     = 'uploading'
        active    = $true
        clipName  = $clipName
        startedAt = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        url       = ''
        error     = ''
        phase     = 'preparing'
        percent   = 1
        requestId = $requestId
        processId = 0
        statusPath = $statusPath
        tempPath  = ''
        kind      = 'upload'
    }

    # nb: dont name this $args -- thats a powershell automatic variable that holds the functions positional argument list. shadowing it inside start-streamableupload is harmless here but its the kind of thing that hides bugs later.
    $childArgs = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass',
        '-File', $ps,
        '-Clip', $uploadPath,
        '-LogFile', $logFile,
        '-StatusFile', $statusPath,
        '-RequestId', $requestId
    )
    # loggingenabled is a [switch] on the worker side -- append the flag (with no value) only when logging is on. passing a string "true"/"false" here would fail param binding before the workers try block runs, leaving the dock with a generic exit=1 failure.
    if ($script:LOG_ENABLED) { $childArgs += '-LoggingEnabled' }
    # if the user is signed in, fail closed unless the saved cookie jar is availible. uploading anonymously from a signed-in ui would give the wrong retention/account behavior.
    if ($auth.required) {
        $childArgs += @('-AuthJar', [string]$auth.path, '-RequireAuth')
    }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName        = 'powershell.exe'
    $psi.Arguments       = (($childArgs | ForEach-Object { Quote-Arg $_ }) -join ' ')
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow  = $true
    $psi.WindowStyle     = 'Hidden'

    Write-Log "Start-StreamableUpload spawning: $($psi.FileName) $($psi.Arguments)" 'upload' $requestId
    try {
        $proc = [System.Diagnostics.Process]::Start($psi)
        Set-UploadState @{ requestId = $requestId; processId = [int]$proc.Id }
    } catch {
        $msg = "Could not launch PowerShell helper: $($_.Exception.Message)"
        Set-UploadState @{ requestId = $requestId; state = 'error'; active = $false; clipName = $clipName; error = $msg }
        return @{ ok = $false; message = $msg }
    }

    $watch = Start-UploadResultWatcher $proc $clipName $statusPath $requestId
    if (-not $watch.ok) { return $watch }

    return @{ ok = $true; state = 'uploading'; clip = $clipName; requestId = $requestId }
}


# auth storage (dpapi). saves the streamable session cookie + the user info json, encrypted with the current windows users dpapi master key -- so its tied to this account, never leaves disk readable, and survives reboots without us needing to store a password.

# returns the path to the curl cookie jar we hand to upload_worker.ps1 whenever the user is signed in. apply-auth rewrites it from the decrypted blob, so the cookies live on disk in plaintext only while a session is active.
function Get-AuthCookieJarPath {
    return Join-Path $script:AUTH_DIR 'auth_cookies.txt'
}

# persists a curl cookie jar (imported from a chromium browsers streamable.com session) encrypted with the current users dpapi key, so the session never lives on disk in plain text.
function Save-AuthBlob([string]$cookieFileContent, $user) {
    if (-not (Test-Path -LiteralPath $script:AUTH_DIR)) {
        [void](New-Item -ItemType Directory -Path $script:AUTH_DIR -Force)
    }
    $payload = @{
        cookies = $cookieFileContent
        user    = $user
        savedAt = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    } | ConvertTo-Json -Depth 6 -Compress
    Add-Type -AssemblyName System.Security
    $bytes     = [System.Text.Encoding]::UTF8.GetBytes($payload)
    $encrypted = [System.Security.Cryptography.ProtectedData]::Protect(
        $bytes, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
    [System.IO.File]::WriteAllBytes($script:AUTH_FILE, $encrypted)
}

function Load-AuthBlob {
    if (-not (Test-Path -LiteralPath $script:AUTH_FILE)) { return $null }
    try {
        Add-Type -AssemblyName System.Security
        $enc = [System.IO.File]::ReadAllBytes($script:AUTH_FILE)
        $dec = [System.Security.Cryptography.ProtectedData]::Unprotect(
            $enc, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
        $json = [System.Text.Encoding]::UTF8.GetString($dec)
        return ConvertFrom-Json $json
    } catch {
        Write-Log "Load-AuthBlob: $($_.Exception.Message)"
        return $null
    }
}

function Clear-AuthBlob {
    if (Test-Path -LiteralPath $script:AUTH_FILE) {
        try { Remove-Item -LiteralPath $script:AUTH_FILE -Force } catch {}
    }
    $jar = Get-AuthCookieJarPath
    if (Test-Path -LiteralPath $jar) {
        try { Remove-Item -LiteralPath $jar -Force } catch {}
    }
}

function Invoke-StreamableBackgroundLogout {
    $jar = Get-AuthCookieJarPath
    if (-not (Test-Path -LiteralPath $jar)) { return }
    $respPath = Join-Path $env:TEMP ('streamable_logout_' + [Guid]::NewGuid().ToString('N') + '.txt')
    try {
        $url = 'https://streamable.com/logout?_cb=' + [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        $rawCode = & curl.exe -s -S --max-time 10 -L -b $jar -c $jar -o $respPath -w '%{http_code}' $url 2>&1
        $curlExit = $LASTEXITCODE
        $codeText = ([string]$rawCode).Trim()
        Write-Log "Background Streamable logout http=$codeText curlExit=$curlExit"
    } finally {
        if (Test-Path -LiteralPath $respPath) { try { Remove-Item -LiteralPath $respPath -Force } catch {} }
    }
}

# translate a streamable user object into (sizecap, retentiondays). streamable hasnt published an exact byte-cap table for every plan, so we err on the generous side -- once a real user object is in the helper log the user can sharpen these numbers.
function Resolve-PlanLimits($user) {
    $plan = ''
    if ($user) {
        foreach ($k in 'plan','plan_name','plan_slug','user_type','subscription_status') {
            if ($user.$k) { $plan = ([string]$user.$k).ToLowerInvariant(); break }
        }
    }
    $size = $script:ANON_SIZE_CAP
    $ret  = $script:SIGNED_IN_DEFAULT_RETENTION
    switch -Regex ($plan) {
        '^(premium|pro|enterprise|business)' { $size = 0;            $ret = 365 }
        '^(plus)'                            { $size = 5 * 1024 * 1024 * 1024; $ret = 365 }
        '^(basic|paid)'                      { $size = 2 * 1024 * 1024 * 1024; $ret = 365 }
        default                              { $size = $script:ANON_SIZE_CAP; $ret = $script:SIGNED_IN_DEFAULT_RETENTION }
    }
    return @{ sizeCap = $size; retentionDays = $ret; planLabel = $plan }
}

# apply auth state into $script:state.auth (under authlock).
function Apply-Auth($user, [string]$cookieFile) {
    [System.Threading.Monitor]::Enter($script:State.AuthLock)
    try {
        $limits = Resolve-PlanLimits $user
        $username = ''
        if ($user) {
            # streamables actual /me response uses `user_name`. older snapshots / pystreamable used `username`. some flows populate `email` instead. check all of them.
            foreach ($k in 'user_name','username','email','name') {
                if ($user.$k) { $username = [string]$user.$k; break }
            }
        }
        $script:State.Auth = @{
            signedIn      = ($user -ne $null -and $user)
            username      = $username
            plan          = $limits.planLabel
            sizeCap       = $limits.sizeCap
            retentionDays = $limits.retentionDays
        }
    } finally {
        [System.Threading.Monitor]::Exit($script:State.AuthLock)
    }
    # write the plaintext cookie jar to disk for the upload script.
    if ($cookieFile) {
        if (-not (Test-Path -LiteralPath $script:AUTH_DIR)) {
            [void](New-Item -ItemType Directory -Path $script:AUTH_DIR -Force)
        }
        [System.IO.File]::WriteAllText((Get-AuthCookieJarPath), $cookieFile, [System.Text.Encoding]::ASCII)
    }
}

function Clear-Auth {
    [System.Threading.Monitor]::Enter($script:State.AuthLock)
    try {
        $script:State.Auth = @{
            signedIn      = $false
            username      = ''
            plan          = ''
            sizeCap       = $script:ANON_SIZE_CAP
            retentionDays = $script:ANON_RETENTION_DAYS
        }
    } finally {
        [System.Threading.Monitor]::Exit($script:State.AuthLock)
    }
    Clear-AuthBlob
}

# validate an existing session by hitting /api/v1/me. returns the user object on success, $null otherwise. called at startup from the dpapi-stored blob; if streamables session has expired we drop the stored auth.
function Invoke-StreamableMe([string]$cookieFileContent) {
    $jar = Join-Path $env:TEMP ('streamable_me_' + [Guid]::NewGuid().ToString('N') + '.txt')
    $respPath = Join-Path $env:TEMP ('streamable_me_resp_' + [Guid]::NewGuid().ToString('N') + '.txt')
    try {
        [System.IO.File]::WriteAllText($jar, $cookieFileContent, [System.Text.Encoding]::ASCII)
        $url = "$script:STREAMABLE_API/api/v1/me"
        $args = @(
            '-s','-S','--max-time','10',
            '-b', $jar,
            '-H', 'Origin: https://streamable.com',
            '-H', 'Referer: https://streamable.com/',
            '-H', 'Accept: application/json',
            '-A', 'Mozilla/5.0 (Windows NT 10.0; Win64; x64)',
            '-o', $respPath,
            '-w', '%{http_code}',
            $url
        )
        $code = & curl.exe @args
        $code = [int]$code
        if ($code -lt 200 -or $code -ge 300) { return $null }
        if (-not (Test-Path -LiteralPath $respPath)) { return $null }
        $body = [System.IO.File]::ReadAllText($respPath)
        # log the identity-relevant fields so we can see why username ends up blank when streamable returns a degraded response. we surface just the auth-shaped keys to keep the log clean.
        try {
            $u = ConvertFrom-Json $body
            $id     = if ($u.id)        { $u.id }        else { '?' }
            $name   = if ($u.user_name) { $u.user_name } else { '' }
            $email  = if ($u.email)     { $u.email }     else { '' }
            $plan   = if ($u.plan_name) { $u.plan_name } else { '' }
            $emailV = if ($u.PSObject.Properties['email_verified']) { [string]$u.email_verified } else { '?' }
            Write-Log ("Streamable /me ok: id=$id user_name='$name' email='$email' " +
                       "plan='$plan' email_verified=$emailV  bodylen=$($body.Length)")
            # if identity fields are missing, dump the entire response so we can see what fields are present. we truncate at 2kb to avoid log spam if a future response is enormous.
            if (-not $u.id -or -not $u.user_name) {
                $dump = if ($body.Length -gt 2000) { $body.Substring(0,2000) + '...' } else { $body }
                Write-Log ("Streamable /me DEGRADED body: " + ($dump -replace '\s+',' '))
            }
            return $u
        } catch {
            Write-Log "Streamable /me: JSON parse failed ($($_.Exception.Message)); body[0..200]=$($body.Substring(0,[Math]::Min(200,$body.Length)))"
            return $null
        }
    } catch { return $null }
    finally {
        if (Test-Path -LiteralPath $jar)      { try { Remove-Item -LiteralPath $jar      -Force } catch {} }
        if (Test-Path -LiteralPath $respPath) { try { Remove-Item -LiteralPath $respPath -Force } catch {} }
    }
}

# restore the signed-in session from the dpapi cookie blob. called once at startup. if streamable rejects the cookies (session expired, signed out elsewhere), drop the stored auth quietly.
function Restore-AuthAtStartup {
    $blob = Load-AuthBlob
    if (-not $blob -or -not $blob.cookies) { return }
    $live = Invoke-StreamableMe $blob.cookies
    if ($live) {
        Apply-Auth $live $blob.cookies
        Write-Log "Restored signed-in session for $($script:State.Auth.username) (plan=$($script:State.Auth.plan))"
        return
    }
    Write-Log "Stored auth blob exists but Streamable rejected the session -- clearing."
    Clear-AuthBlob
}


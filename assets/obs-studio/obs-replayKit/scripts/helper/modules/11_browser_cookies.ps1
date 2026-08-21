# cef cookie import (google sign-in path). strategy: open streamable.com/login as a cef window inside obs, let the user sign in, then dig the streamable.com session cookies out of obss own chromium cookie database. chromium 80+ encrypts cookie values with aes-gcm; the master key is dpapi-encrypted inside the `local state` json next to the cookies file.

# returns every chromium-based cookie store on this machine we know how to read. each entry is @{ name; cookies; localstate; root }. the users defualt browser (where theyre already signed in to google) is typically chrome or edge -- those come first. obss own cef is last (its only useful as a fallback when the user signs in inside the obs-hosted popup instead of their defualt browser).
function Get-BrowserCookieSources {
    # cached per-helper-process. install paths dont change while obs is running, and the enumeration costs ~50ms each call -- which adds up over many login polls. cache the resolved set after the first scan.
    if ($script:BrowserSourcesCache) { return $script:BrowserSourcesCache }

    $appData = $env:APPDATA
    $localApp = $env:LOCALAPPDATA
    if (-not $localApp) { return @() }

    # obs cef first: its the only source where our sign-in flow lands cookies (continue with google opens the streamable login as a child cef window), and its the only readable source on chrome 127+ machines (chromes own db is v20 abe-locked). scanning it first means the success path returns from this function without touching the others.
    $candidates = @()
    if ($appData) {
        $candidates += @{ name = 'OBS CEF'; root = (Join-Path $appData 'obs-studio\plugin_config\obs-browser') }
    }
    $candidates += @(
        @{ name = 'Edge';   root = (Join-Path $localApp 'Microsoft\Edge\User Data') },
        @{ name = 'Chrome'; root = (Join-Path $localApp 'Google\Chrome\User Data') },
        @{ name = 'Brave';  root = (Join-Path $localApp 'BraveSoftware\Brave-Browser\User Data') },
        @{ name = 'Vivaldi';root = (Join-Path $localApp 'Vivaldi\User Data') },
        @{ name = 'Opera';  root = (Join-Path $localApp 'Opera Software\Opera Stable') }
    )

    # cookies file moved between chromium revisions and lives one per profile. we enumerate every profile directory (defualt, profile 1, profile 2, ...) so users who dont use defualt still get matched.
    $cookieSubPaths = @('Network\Cookies', 'Cookies')

    $out = @()
    foreach ($c in $candidates) {
        if (-not (Test-Path -LiteralPath $c.root)) { continue }
        # local state holds the dpapi-wrapped aes-gcm master key used to decrypt v10/v11 cookie blobs. full chromium / edge / brave name it exactly local state. embedded cef (obs browser plugin) renames the same json shape to localprefs.json becuase that variant runs cef without a full chromium prefservice and uses cefs standalone preference store naming. the `os_crypt.encrypted_key` field and the "dpapi" + aes-256-key payload inside it are identical, so the same get-chromiummasterkey decryptor works on both.
        $local = Join-Path $c.root 'Local State'
        $hasLocal = Test-Path -LiteralPath $local
        if (-not $hasLocal) {
            $localCef = Join-Path $c.root 'LocalPrefs.json'
            if (Test-Path -LiteralPath $localCef) {
                $local = $localCef
                $hasLocal = $true
            }
        }

        # enumerate every profile directory. defualt, plus any "profile n" sibling. skip system / guest profiles -- those never hold real user sessions.
        $profiles = New-Object System.Collections.Generic.List[string]
        if (Test-Path -LiteralPath (Join-Path $c.root 'Default')) { [void]$profiles.Add('Default') }
        Get-ChildItem -LiteralPath $c.root -Directory -ErrorAction SilentlyContinue | ForEach-Object {
            if ($_.Name -match '^Profile \d+$') { [void]$profiles.Add($_.Name) }
        }
        # embedded cef apps (obs) and some chromium variants (notably opera stable) keep cookies right in the root with no profile dir.
        $profiles.Add('') | Out-Null

        foreach ($p in $profiles) {
            $profileRoot = if ($p) { Join-Path $c.root $p } else { $c.root }
            foreach ($sub in $cookieSubPaths) {
                $cookies = Join-Path $profileRoot $sub
                if (Test-Path -LiteralPath $cookies) {
                    $label = if ($p -and $p -ne 'Default') { "$($c.name) [$p]" } else { $c.name }
                    $out += @{
                        name       = $label
                        cookies    = $cookies
                        localState = if ($hasLocal) { $local } else { $null }
                        root       = $c.root
                    }
                    break
                }
            }
        }
    }
    Write-Log ("Get-BrowserCookieSources found " + $out.Count + " source(s): " +
        (($out | ForEach-Object { $_.name }) -join ', '))
    $script:BrowserSourcesCache = $out
    return $out
}

function Get-CookieSourceSignature($source) {
    $parts = New-Object System.Collections.Generic.List[string]
    $paths = New-Object System.Collections.Generic.List[string]
    if ($source.cookies) { [void]$paths.Add([string]$source.cookies) }
    foreach ($ext in '-wal','-shm','-journal') {
        if ($source.cookies) { [void]$paths.Add(([string]$source.cookies) + $ext) }
    }
    foreach ($p in $paths) {
        try {
            $fi = [System.IO.FileInfo]::new($p)
            if ($fi.Exists) {
                [void]$parts.Add(("{0}:{1}:{2}" -f $p, $fi.Length, $fi.LastWriteTimeUtc.Ticks))
            } else {
                [void]$parts.Add(("{0}:missing" -f $p))
            }
        } catch {
            [void]$parts.Add(("{0}:error" -f $p))
        }
    }
    return ($parts -join '|')
}

function Get-CachedCookieMiss($source, [string]$signature) {
    $key = [string]$source.name
    if (-not $key -or -not $signature) { return $null }
    $entry = $script:CookieSourceMissCache[$key]
    if ($entry -and [string]$entry.signature -eq $signature) { return $entry }
    return $null
}

function Set-CachedCookieMiss($source, [string]$signature, [string]$reason) {
    $key = [string]$source.name
    if (-not $key -or -not $signature) { return }
    $script:CookieSourceMissCache[$key] = @{
        signature = $signature
        reason    = $reason
        at        = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    }
}

function Clear-CachedCookieMiss($source) {
    $key = [string]$source.name
    if ($key -and $script:CookieSourceMissCache.ContainsKey($key)) {
        $script:CookieSourceMissCache.Remove($key)
    }
}

# decrypt the aes-gcm master key out of `local state`. format: base64(dpapi("dpapi" + 32-byte aes key)). strip the literal "dpapi" prefix (5 bytes) before handing to protecteddata.unprotect.
function Get-ChromiumMasterKey([string]$localStatePath) {
    if (-not (Test-Path -LiteralPath $localStatePath)) { return $null }
    try {
        $text = [System.IO.File]::ReadAllText($localStatePath)
        if ($text.Length -gt 0 -and $text[0] -eq [char]0xFEFF) { $text = $text.Substring(1) }
        $obj = ConvertFrom-Json $text
        $b64 = $obj.os_crypt.encrypted_key
        if (-not $b64) { return $null }
        $enc = [Convert]::FromBase64String([string]$b64)
        if ($enc.Length -lt 6) { return $null }
        # strip "dpapi" prefix (5 ascii bytes).
        $blob = New-Object byte[] ($enc.Length - 5)
        [Array]::Copy($enc, 5, $blob, 0, $blob.Length)
        Add-Type -AssemblyName System.Security
        return [System.Security.Cryptography.ProtectedData]::Unprotect(
            $blob, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
    } catch {
        Write-Log "Get-ChromiumMasterKey: $($_.Exception.Message)"
        return $null
    }
}

# decrypt a single cookies encrypted_value blob. returns the cookies plaintext value, or $null on failure / wrong format.
function Decrypt-CookieValue([byte[]]$masterKey, [byte[]]$encryptedValue) {
    if (-not $masterKey -or -not $encryptedValue -or $encryptedValue.Length -lt 32) { return $null }
    $prefix = [System.Text.Encoding]::ASCII.GetString($encryptedValue, 0, 3)
    if ($prefix -ne 'v10' -and $prefix -ne 'v11') { return $null }
    $nonceLen = 12
    $tagLen   = 16
    $nonce = New-Object byte[] $nonceLen
    [Array]::Copy($encryptedValue, 3, $nonce, 0, $nonceLen)
    $tagStart = $encryptedValue.Length - $tagLen
    $cipherLen = $tagStart - (3 + $nonceLen)
    if ($cipherLen -lt 0) { return $null }
    $cipher = New-Object byte[] $cipherLen
    [Array]::Copy($encryptedValue, 3 + $nonceLen, $cipher, 0, $cipherLen)
    $tag = New-Object byte[] $tagLen
    [Array]::Copy($encryptedValue, $tagStart, $tag, 0, $tagLen)
    try {
        $plain = [BCryptAesGcm]::Decrypt($masterKey, $nonce, $cipher, $tag)
        return [System.Text.Encoding]::UTF8.GetString($plain)
    } catch {
        Write-Log "Decrypt-CookieValue: $($_.Exception.Message)"
        return $null
    }
}

# copy a possibly-locked file using fileshare.readwrite|delete so we can read past chromiums exclusive lock on the active profiles cookies db. throws if the source cant even be opened that way -- chrome 105+ opens with file_share_none so this always throws for the active chrome profile; vss picks up the slack below.
function Copy-LockedFile([string]$src, [string]$dst) {
    $share = [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete
    $inS = [System.IO.File]::Open(
        $src, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, $share)
    try {
        $outS = [System.IO.File]::Create($dst)
        try { $inS.CopyTo($outS) } finally { $outS.Dispose() }
    } finally { $inS.Dispose() }
}

# are we admin vss shadow-copy creation needs it.
function Test-IsAdmin {
    try {
        $id  = [System.Security.Principal.WindowsIdentity]::GetCurrent()
        $p   = New-Object System.Security.Principal.WindowsPrincipal $id
        return $p.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch { return $false }
}

# volume shadow copy-based file copy. bypasses any exclusive file lock (chromes file_share_none, obs-cefs, anything else) becuase we read from a kernel-level point-in-time snapshot of the volume rather than the live file. requires admin; returns $false otherwise. flow: win32_shadowcopy::create("c:\", "clientaccessible") look up the shadows deviceobject (a \\\globalroot\device\... path) build the same relative file path under the shadow root and copy delete the shadow (they cost a few mb of system reserved space each if left around)
function Copy-WithVSS([string]$src, [string]$dst) {
    if (-not (Test-IsAdmin)) {
        Write-Log "VSS: not admin -- skipping shadow-copy fallback for $src"
        return $false
    }
    $volume = [System.IO.Path]::GetPathRoot($src)
    if (-not $volume) {
        Write-Log "VSS: no volume root resolvable from $src"
        return $false
    }
    $shadowID = $null
    $shadowObj = $null
    try {
        $shadowClass = [wmiclass]'Win32_ShadowCopy'
        $r = $shadowClass.Create($volume, 'ClientAccessible')
        if ($r.ReturnValue -ne 0) {
            Write-Log "VSS: Win32_ShadowCopy.Create returned $($r.ReturnValue) on $volume"
            return $false
        }
        $shadowID  = $r.ShadowID
        $shadowObj = Get-WmiObject -Class Win32_ShadowCopy -Filter "ID='$shadowID'"
        if (-not $shadowObj) {
            Write-Log "VSS: shadow $shadowID created but lookup returned null"
            return $false
        }
        $deviceObject = $shadowObj.DeviceObject
        # deviceobject is e.g. \\\globalroot\device\harddiskvolumeshadowcopy12 building "$deviceobject\$rest" gives a path the kernel will route into the snapshot, which createfile/copyfile accept.
        $relPath    = $src.Substring($volume.Length)
        $shadowSrc  = $deviceObject + '\' + $relPath
        try {
            [System.IO.File]::Copy($shadowSrc, $dst, $true)
            return $true
        } catch {
            # .net sometimes refuses globalroot paths in long-path normalization. fall back to a junction: point a temp dir at the snapshot root, then read via that.
            Write-Log "VSS: direct copy from $shadowSrc failed: $($_.Exception.Message); falling back to junction"
            $junction = Join-Path $script:SCRATCH_DIR ('vss_' + [Guid]::NewGuid().ToString('N'))
            try {
                $mklinkOut = cmd /c mklink /j "`"$junction`"" "`"$deviceObject`"" 2>&1
                if ($LASTEXITCODE -ne 0) {
                    Write-Log "VSS: mklink /j failed: $mklinkOut"
                    return $false
                }
                $junctionSrc = Join-Path $junction $relPath
                [System.IO.File]::Copy($junctionSrc, $dst, $true)
                return $true
            } finally {
                if (Test-Path -LiteralPath $junction) {
                    try { cmd /c rmdir "`"$junction`"" 2>&1 | Out-Null } catch {}
                }
            }
        }
    } catch {
        Write-Log "VSS: failed: $($_.Exception.Message)"
        return $false
    } finally {
        if ($shadowObj) {
            try { [void]$shadowObj.Delete() } catch {}
        }
    }
}

function Copy-WithVSSFiles($files) {
    if (-not (Test-IsAdmin)) {
        Write-Log "VSS: not admin -- skipping shadow-copy fallback for grouped cookie copy"
        return $false
    }
    if (-not $files -or $files.Count -eq 0) { return $false }

    $first = $files[0]
    $volume = [System.IO.Path]::GetPathRoot([string]$first['src'])
    if (-not $volume) {
        Write-Log "VSS: no volume root resolvable from $($first['src'])"
        return $false
    }

    $shadowObj = $null
    $junction = $null
    try {
        $shadowClass = [wmiclass]'Win32_ShadowCopy'
        $r = $shadowClass.Create($volume, 'ClientAccessible')
        if ($r.ReturnValue -ne 0) {
            Write-Log "VSS: Win32_ShadowCopy.Create returned $($r.ReturnValue) on $volume"
            return $false
        }
        $shadowObj = Get-WmiObject -Class Win32_ShadowCopy -Filter "ID='$($r.ShadowID)'"
        if (-not $shadowObj) {
            Write-Log "VSS: shadow $($r.ShadowID) created but lookup returned null"
            return $false
        }
        $deviceObject = $shadowObj.DeviceObject

        $copyFromBase = {
            param([string]$basePath)
            foreach ($f in $files) {
                $src = [string]$f['src']
                $dst = [string]$f['dst']
                $required = [bool]$f['required']
                $relPath = $src.Substring($volume.Length)
                $shadowSrc = $basePath + '\' + $relPath
                try {
                    [System.IO.File]::Copy($shadowSrc, $dst, $true)
                } catch {
                    if ($required) { throw }
                    Write-Log "VSS: optional sidecar copy skipped for $src : $($_.Exception.Message)"
                }
            }
        }

        try {
            & $copyFromBase $deviceObject
            return $true
        } catch {
            Write-Log "VSS: grouped direct copy failed: $($_.Exception.Message); falling back to junction"
            $junction = Join-Path $script:SCRATCH_DIR ('vss_' + [Guid]::NewGuid().ToString('N'))
            $mklinkOut = cmd /c mklink /j "`"$junction`"" "`"$deviceObject`"" 2>&1
            if ($LASTEXITCODE -ne 0) {
                Write-Log "VSS: mklink /j failed: $mklinkOut"
                return $false
            }
            & $copyFromBase $junction
            return $true
        }
    } catch {
        Write-Log "VSS: grouped copy failed: $($_.Exception.Message)"
        return $false
    } finally {
        if ($junction -and (Test-Path -LiteralPath $junction)) {
            try { cmd /c rmdir "`"$junction`"" 2>&1 | Out-Null } catch {}
        }
        if ($shadowObj) {
            try { [void]$shadowObj.Delete() } catch {}
        }
    }
}

# read streamable.com cookies from one chromium-based browser cookie store. returns the list (may be empty). expires_utc in chromium is microseconds since 1601-01-01 utc ("webkit time"). active chrome profile holds an exclusive os lock on its cookies file -- sqlite3_open_v2 returns sqlite_cantopen (rc=14) if we point at it directly. we sidestep that by copying the db (and its wal / shm sidecars, if any) into a temp directory using fileshare.readwrite and reading the copy. the copy is deleted after we close.
function Read-StreamableCookiesFromSource($source, [ref]$ErrorOut = $null) {
    # per-session blacklist: once weve confirmed a source uses an unreadable encryption envelope (chrome 127+s v20 app-bound encryption, which is intentionally undecryptable by anyone outside chromes elevated com service), skip it on subsequent /import-session polls. login polling fires every couple seconds while the user signs in, and a vss shadow-copy + sqlite scan of a 50k-row chrome db costs 2-3 seconds per source -- shaving the chrome sources out cuts each poll from ~6s to ~2s.
    if (-not $script:UnreadableSources) {
        $script:UnreadableSources = @{}
    }
    if ($script:UnreadableSources[$source.name]) {
        return @()
    }

    # master key is only required if encrypted_value blobs use aes-gcm. sources without local state (embedded cef builds with os_crypt disabled, e.g. obs) store cookies plaintext in the `value` column -- no master key needed there.
    $masterKey = $null
    if ($source.localState) {
        $masterKey = Get-ChromiumMasterKey $source.localState
        if (-not $masterKey) {
            Write-Log "$($source.name): Local State present but master key decrypt failed; will only see plaintext cookies."
        }
    }

    $tempDir = Join-Path $script:SCRATCH_DIR ("strmbl_helper_" + [Guid]::NewGuid().ToString('N'))
    $tempCookies = Join-Path $tempDir 'Cookies'
    $copiedViaVSS = $false
    try {
        [void](New-Item -ItemType Directory -Path $tempDir -Force)
        try {
            Copy-LockedFile $source.cookies $tempCookies
        } catch {
            # direct copy refused -- the holding process opened the file with no shared access (chrome 105+, obs cef). fall back to a volume shadow copy of the volume so we read from a kernel snapshot instead of the live file. needs admin; helper inherits admin from obs when obs is launched as administrator.
            Write-Log "$($source.name): direct copy refused ($($_.Exception.Message)); trying VSS"
            $vssFiles = @(@{ src = $source.cookies; dst = $tempCookies; required = $true })
            foreach ($ext in '-wal','-shm','-journal') {
                $sideSrc = $source.cookies + $ext
                if (Test-Path -LiteralPath $sideSrc) {
                    $vssFiles += @{ src = $sideSrc; dst = ($tempCookies + $ext); required = $false }
                }
            }
            if (-not (Copy-WithVSSFiles $vssFiles)) {
                throw "Both direct copy and VSS failed for $($source.cookies)"
            }
            $copiedViaVSS = $true
            Write-Log "$($source.name): VSS copy succeeded."
        }
        # carry the wal/shm sidecars too so we can see cookie writes that chromium hasnt yet checkpointed back to the main file. use whichever method worked for the main file.
        if (-not $copiedViaVSS) {
            foreach ($ext in '-wal','-shm','-journal') {
                $sideSrc = $source.cookies + $ext
                if (Test-Path -LiteralPath $sideSrc) {
                    try {
                        Copy-LockedFile $sideSrc ($tempCookies + $ext)
                    } catch {}
                }
            }
        }
    } catch {
        $msg = $_.Exception.Message
        Write-Log "$($source.name): could not copy cookies db: $msg"
        # surface "wed have needed vss, but were not admin" upward so find-workingstreamablesession can return an actionable error ("relaunch obs as admin") rather than the generic "no session found" that hides this root cause.
        if ($msg -match 'Both direct copy and VSS failed' -and -not (Test-IsAdmin)) {
            if ($ErrorOut) { $ErrorOut.Value = 'locked file, not admin' }
        } else {
            if ($ErrorOut) { $ErrorOut.Value = $msg }
        }
        if (Test-Path -LiteralPath $tempDir) {
            try { Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue } catch {}
        }
        return @()
    }
    # mark this source as successfully read so per-source diagnostics can tell apart "file copy failed" from "file copy fine but no streamable cookies present". an out-param instead of a shared global -- two concurrent /import-session calls each need their own error value, not one shared between them.
    if ($ErrorOut) { $ErrorOut.Value = '' }

    # open the temp copy. no lock contention, no need for immutable. %-encode spaces in path (just in case the temp path ever has any).
    $pathSlash = ($tempCookies -replace '\\','/') -replace ' ','%20'
    $uri = 'file:' + $pathSlash + '?mode=ro'
    $db = [IntPtr]::Zero
    $rc = [WinSqlite]::sqlite3_open_v2(
        [WinSqlite]::Utf8Z($uri), [ref]$db,
        ([WinSqlite]::SQLITE_OPEN_READONLY -bor [WinSqlite]::SQLITE_OPEN_URI),
        [IntPtr]::Zero)
    if ($rc -ne [WinSqlite]::SQLITE_OK) {
        Write-Log "$($source.name): sqlite3_open_v2 failed rc=$rc on copy at $tempCookies"
        try { Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue } catch {}
        return @()
    }

    $cookies = @()
    try {
        # diagnostic pass: how many cookies are in this db at all, and what domains look streamable-ish helps tell apart "db is fine, just no streamable cookies" from "we copied a stale or empty db".
        $diagStmt = [IntPtr]::Zero
        $rcD = [WinSqlite]::sqlite3_prepare_v2(
            $db,
            [WinSqlite]::Utf8Z("SELECT COUNT(*) FROM cookies"),
            -1, [ref]$diagStmt, [IntPtr]::Zero)
        if ($rcD -eq [WinSqlite]::SQLITE_OK) {
            if ([WinSqlite]::sqlite3_step($diagStmt) -eq [WinSqlite]::SQLITE_ROW) {
                $total = [WinSqlite]::sqlite3_column_int64($diagStmt, 0)
                Write-Log "$($source.name): cookie DB has $total row(s) total."
            }
            [void][WinSqlite]::sqlite3_finalize($diagStmt)
        }
        $diagStmt = [IntPtr]::Zero
        $rcD = [WinSqlite]::sqlite3_prepare_v2(
            $db,
            [WinSqlite]::Utf8Z("SELECT host_key, COUNT(*) FROM cookies WHERE host_key LIKE '%stream%' GROUP BY host_key"),
            -1, [ref]$diagStmt, [IntPtr]::Zero)
        if ($rcD -eq [WinSqlite]::SQLITE_OK) {
            $hits = @()
            while ([WinSqlite]::sqlite3_step($diagStmt) -eq [WinSqlite]::SQLITE_ROW) {
                $hits += ("{0}={1}" -f
                    [WinSqlite]::ColumnText($diagStmt, 0),
                    [WinSqlite]::sqlite3_column_int64($diagStmt, 1))
            }
            [void][WinSqlite]::sqlite3_finalize($diagStmt)
            if ($hits.Count -gt 0) {
                Write-Log "$($source.name): stream-ish hosts in cookie DB: $($hits -join ', ')"
            } else {
                Write-Log "$($source.name): no host_keys containing 'stream' in this DB."
            }
        }

        # pull both columns: encrypted_value (aes-gcm, master-keyed) for normal chromium, and value (plaintext) for embedded cef builds with os_crypt disabled.
        $sql = "SELECT host_key, name, encrypted_value, value, path, expires_utc, is_secure, is_httponly " +
               "FROM cookies " +
               "WHERE host_key LIKE '%streamable.com'"
        $stmt = [IntPtr]::Zero
        $rc = [WinSqlite]::sqlite3_prepare_v2(
            $db, [WinSqlite]::Utf8Z($sql), -1, [ref]$stmt, [IntPtr]::Zero)
        if ($rc -ne [WinSqlite]::SQLITE_OK) {
            Write-Log "$($source.name): sqlite3_prepare_v2 failed rc=$rc"
            return @()
        }
        # diagnostic tallies so we can tell exactly why a row was dropped. without this all skips look identical from outside ("no streamable.com cookies in this store") and we cant tell decrypt-failed apart from no-master-key apart from unknown-prefix.
        $rowsTotal      = 0
        $skipNoMaster   = 0
        $skipBadPrefix  = 0
        $skipDecrypt    = 0
        $skipEmpty      = 0
        $kept           = 0
        $prefixSamples  = @{} # prefix -> count
        $firstBlobBytes = $null

        try {
            while ([WinSqlite]::sqlite3_step($stmt) -eq [WinSqlite]::SQLITE_ROW) {
                # nb: dont name this $host -- thats a powershell automatic read-only variable (the current host program). assignments to it throw "cant overwrite variable host becuase it is read-only or constant".
                $hostKey    = [WinSqlite]::ColumnText($stmt, 0)
                $name       = [WinSqlite]::ColumnText($stmt, 1)
                $encVal     = [WinSqlite]::ColumnBlob($stmt, 2)
                $plainVal   = [WinSqlite]::ColumnText($stmt, 3)
                $path       = [WinSqlite]::ColumnText($stmt, 4)
                $expiresWk  = [WinSqlite]::sqlite3_column_int64($stmt, 5)
                $secure     = [WinSqlite]::sqlite3_column_int64($stmt, 6)
                $httpOnly   = [WinSqlite]::sqlite3_column_int64($stmt, 7)
                $rowsTotal++

                # sniff prefix even if we dont recognize it -- tells us if chrome moved to v20 app-bound encryption.
                $prefix = ''
                if ($encVal -and $encVal.Length -ge 3) {
                    $prefix = [System.Text.Encoding]::ASCII.GetString($encVal, 0, 3)
                    $prefixSamples[$prefix] = 1 + ([int]($prefixSamples[$prefix]))
                    if (-not $firstBlobBytes -and $encVal.Length -ge 8) {
                        $firstBlobBytes = ($encVal[0..7] | ForEach-Object { '{0:X2}' -f $_ }) -join ' '
                    }
                }

                $val = $null
                if ($prefix -eq 'v10' -or $prefix -eq 'v11') {
                    if (-not $masterKey) {
                        $skipNoMaster++
                    } else {
                        $val = Decrypt-CookieValue $masterKey $encVal
                        if ($null -eq $val) { $skipDecrypt++ }
                    }
                } elseif ($encVal -and $encVal.Length -gt 0) {
                    # unknown encryption envelope (e.g. v20 abe).
                    $skipBadPrefix++
                }

                if ($null -eq $val -and $plainVal) { $val = $plainVal }
                if ($null -eq $val) {
                    if ($prefix -eq '' -or $prefix -eq 'v10' -or $prefix -eq 'v11') { $skipEmpty++ }
                    continue
                }

                $kept++
                $cookies += @{
                    host     = $hostKey
                    name     = $name
                    value    = $val
                    path     = if ($path) { $path } else { '/' }
                    expires  = if ($expiresWk -gt 0) {
                        # webkit microseconds-since-1601 -> unix seconds.
                        [int64](($expiresWk / 1000000) - 11644473600)
                    } else { 0 }
                    secure   = ($secure -ne 0)
                    httpOnly = ($httpOnly -ne 0)
                }
            }
        } finally {
            [void][WinSqlite]::sqlite3_finalize($stmt)
        }

        # per-source summary. always logged, even when 0 cookies kept.
        $prefixStr = if ($prefixSamples.Count) {
            (($prefixSamples.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', ')
        } else { '(no encrypted blobs)' }
        $masterStr = if ($masterKey) { "yes ($($masterKey.Length)B)" } else { 'no' }
        Write-Log ("$($source.name): scanned=$rowsTotal kept=$kept " +
                   "skip-no-master=$skipNoMaster skip-prefix=$skipBadPrefix " +
                   "skip-decrypt=$skipDecrypt skip-empty=$skipEmpty " +
                   "master-key=$masterStr prefixes=[$prefixStr] " +
                   "firstBlob=[$($firstBlobBytes)]")

        # blacklist this source for the rest of the session if every streamable.com row used an envelope we cant decrypt. chrome 127+ writes v20 abe-wrapped blobs that no third-party process can unwrap, so polling the source again will only burn time. we require rowstotal > 0 (otherwise the source may just not have any cookies yet -- could be fresh) and every row to have failed via the unknown-prefix path (skip-prefix accounts for every scanned row).
        if ($rowsTotal -gt 0 -and $kept -eq 0 -and $skipBadPrefix -eq $rowsTotal) {
            $script:UnreadableSources[$source.name] = $true
            Write-Log "$($source.name): blacklisting for this session (unreadable encryption envelope)."
        }
    } finally {
        [void][WinSqlite]::sqlite3_close_v2($db)
        try { Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue } catch {}
    }
    return $cookies
}

# try every browser cookie store on the box, return the first one whose streamable.com cookies actualy validate via /me. we cant just take "first source that has cookies" -- a stale logged-out cookie set is still a cookie set, and we dont want to clobber a working session with one that 401s on /me.
function Find-WorkingStreamableSession([switch]$ObsOnly) {
    $sources = Get-BrowserCookieSources
    if (-not $sources -or $sources.Count -eq 0) {
        return @{ ok = $false; error = 'No supported browser cookie stores found on this machine.' }
    }
    if ($ObsOnly) {
        $sources = @($sources | Where-Object { $_.name -eq 'OBS CEF' })
        if (-not $sources -or $sources.Count -eq 0) {
            return @{ ok = $false; error = 'OBS browser cookie store was not found.' }
        }
    }
    # track whether every source failed for the same reason (locked file + no admin to vss around it). when thats the case we want to surface the actual root cause to the ui instead of the generic "no session found" line, which has been confusing for an hour.
    $allLockedNoAdmin = $true
    $sourcesAttempted = 0
    foreach ($s in $sources) {
        $sourcesAttempted++
        $sourceSignature = Get-CookieSourceSignature $s
        $cachedMiss = Get-CachedCookieMiss $s $sourceSignature
        if ($cachedMiss) {
            $reason = [string]$cachedMiss.reason
            Write-Log "$($s.name): skipping unchanged cookie source after previous miss ($reason)."
            if (-not ($reason -and $reason -match 'not admin')) { $allLockedNoAdmin = $false }
            continue
        }
        $readError = ''
        $cookies = Read-StreamableCookiesFromSource $s ([ref]$readError)
        if (-not $cookies -or $cookies.Count -eq 0) {
            Write-Log "$($s.name): no streamable.com cookies in this store."
            # if the most recent failure was the locked-file-not-admin pair, this source didnt disprove it. any other failure mode (cookie db was readable but had no rows, decrypt failed, etc.) clears the flag.
            $missReason = if ($readError) { [string]$readError } else { 'no streamable cookies' }
            Set-CachedCookieMiss $s $sourceSignature $missReason
            if (-not ($readError -and $readError -match 'not admin')) { $allLockedNoAdmin = $false }
            continue
        }
        $allLockedNoAdmin = $false
        Write-Log "$($s.name): found $($cookies.Count) streamable.com cookie(s)"
        $jar  = Format-CurlCookieJar $cookies
        $live = Invoke-StreamableMe $jar
        if ($live) {
            # reject "anonymous session" responses. streamable hands out a session cookie to anyone who visits /login, and /me returns 200 with plan defaults for those sessions too -- but with no id / user_name / email fields. that is structurally diffrent from a real signed-in user and we must not treat it as authenticated.
            $hasIdentity = ($live.id) -or ($live.user_name) -or ($live.email)
            if (-not $hasIdentity) {
                Write-Log "$($s.name): /me returned an anonymous session (no id/user_name/email). Ignoring."
                Set-CachedCookieMiss $s $sourceSignature 'anonymous streamable session'
            } else {
                Write-Log "Found valid Streamable session in $($s.name)."
                Clear-CachedCookieMiss $s
                return @{ ok = $true; source = $s.name; cookies = $cookies; jar = $jar; user = $live }
            }
        } else {
            Write-Log "$($s.name): /me rejected the cookies (session expired? wrong domain match?)"
        }
    }

    if ($allLockedNoAdmin -and $sourcesAttempted -gt 0) {
        return @{
            ok = $false
            error = ('OBS is not running as administrator, so the helper cannot read ' +
                     "Chrome's or OBS's locked cookies files. Close OBS and relaunch it " +
                     'with "Run as administrator", then try Continue with Google again.')
        }
    }
    return @{ ok = $false; error = 'No signed-in streamable.com session found. Sign in in your browser and try again.' }
}

# build a netscape-format cookie jar (the format curl reads via -b). format per line, tab-separated: domain flag path secure expires name value where flag is "true"/"false" for "include subdomains", determined by whether the host_key starts with a dot.
function Format-CurlCookieJar($cookies) {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine('# Netscape HTTP Cookie File')
    [void]$sb.AppendLine('# generated by local_helper_server.ps1')
    foreach ($c in $cookies) {
        $domain    = $c.host
        $subdomain = if ($domain.StartsWith('.')) { 'TRUE' } else { 'FALSE' }
        $secure    = if ($c.secure)   { 'TRUE' } else { 'FALSE' }
        $expires   = [int64]$c.expires
        $line = "$domain`t$subdomain`t$($c.path)`t$secure`t$expires`t$($c.name)`t$($c.value)"
        if ($c.httpOnly) { $line = "#HttpOnly_" + $line }
        [void]$sb.AppendLine($line)
    }
    return $sb.ToString()
}


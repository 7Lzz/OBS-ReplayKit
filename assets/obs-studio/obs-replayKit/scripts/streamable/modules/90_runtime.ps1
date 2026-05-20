# bootstrap: open the listener, set up the watcher runspace pool, accept loop. request handling is single-threaded -- the actual workload (one /status poll every 5 s + a /clips poll every 8-15 s + occasional /file range reads during preview hover) is well inside what one synchronous thread can handle, and going single-threaded sidesteps powershells per-runspace function-scope rules entirely. a long /file read briefly delays the next /status poll, but the dock polls slowly enough that the delay isnt observable. the upload watcher (which blocks on a child powershell.exe exit) does run on a seperate runspace via the watcherpool -- its script block is fully self-contained, no calls into our functions, so the runspace-scope rules dont bite us there.
Load-Config

# singleton: prevent a second helper from running - # tools -> scripts reload occasionally leaves the old helper alive (its blocked on a slow operation when /shutdown arrives, or com-apartment cleanup keeps the launcher process around after the hosted powershell already exited). the port-takeover dance below tries to recover, but it isnt bulletproof -- the user can end up with two "obs replaykit" entries in task manager. a named system mutex held for this processs lifetime makes the second helper bail out immediately, regardless of how the first one got stuck. held until process exit; the os releases it automatically (and any abandoned-mutex signal is fine for waiters).
$script:SingletonMutex = $null
try {
    $script:SingletonMutex = New-Object System.Threading.Mutex($false, 'Local\OBSReplayKit_Helper_Singleton')
    $owned = $false
    try { $owned = $script:SingletonMutex.WaitOne(0) }
    catch [System.Threading.AbandonedMutexException] { $owned = $true }
    if (-not $owned) {
        Write-Log "Singleton mutex held by another helper; asking it to /shutdown then retrying."
        try {
            $req = [System.Net.WebRequest]::Create("http://127.0.0.1:$($script:DEFAULT_PORT)/shutdown")
            $req.Method  = 'POST'
            $req.Timeout = 1500
            $req.GetResponse().Close()
        } catch {}
        try { $owned = $script:SingletonMutex.WaitOne(3000) }
        catch [System.Threading.AbandonedMutexException] { $owned = $true }
        if (-not $owned) {
            Write-Log "Singleton mutex still held after 3s; exiting to avoid a second OBS ReplayKit instance."
            exit 0
        }
        Write-Log "Acquired singleton mutex after the prior helper released it."
    }
} catch {
    Write-Log "Singleton mutex setup failed: $($_.Exception.Message). Continuing without singleton protection."
}

try { Clear-StaleCompressedTempFiles } catch {}
try { [void](Get-HelperCapabilities -Refresh) } catch {}
$port = $script:DEFAULT_PORT
if ($script:State.Config.port) { $port = [int]$script:State.Config.port }

# try to bind the listener. if another helper is already on the port (for example, a previous helper that did not see /shutdown), post /shutdown to it, wait briefly, then retry. this is what makes a script reload actualy pick up new helper code instead of silently leaving the old one in place.
function New-Listener([int]$p) {
    try {
        $l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $p)
        # exclusiveaddressuse=false sets so_reuseaddr, which lets us bind a port thats lingering in time_wait after a clean restart.
        $l.Server.SetSocketOption(
            [System.Net.Sockets.SocketOptionLevel]::Socket,
            [System.Net.Sockets.SocketOptionName]::ReuseAddress,
            $true)
        $l.Start()
        # clear handle_flag_inherit (0x1) on the underlying socket handle. without this, any child process the helper spawns (or any process that inherits our handle table via createprocess with binherithandles=true) will duplicate this handle. when the helper dies abruptly, the kernel cant gc the listening socket until every duplicate is closed -- and if those duplicates are inside long-lived obs cef children, the listener stays parked under the dead pid forever (no known win32 api can release it externally). stopping the inherit at bind time is the only documented prevention.
        if (-not ('Win32HandleInfo' -as [type])) {
            Add-Type -Namespace W32 -Name HandleInfo -MemberDefinition @'
[DllImport("kernel32.dll", SetLastError=true)]
public static extern bool SetHandleInformation(IntPtr h, uint mask, uint flags);
'@
        }
        try {
            [void][W32.HandleInfo]::SetHandleInformation($l.Server.Handle, 1, 0)
        } catch {
            Write-Log ("New-Listener: SetHandleInformation failed: " + $_.Exception.Message)
        }
        return $l
    } catch { return $null }
}

# find the pid currently bound to the loopback port, if any. we use this both to address the /shutdown post-mortem (so a wedged helper cant keep us locked out) and to log who we were fighting with.
function Get-PortListenerPid([int]$p) {
    try {
        $c = Get-NetTCPConnection -LocalPort $p -State Listen `
                                  -ErrorAction SilentlyContinue
        if ($c) { return [int]($c | Select-Object -First 1 -ExpandProperty OwningProcess) }
    } catch {}
    return 0
}

$listener = New-Listener $port
if (-not $listener) {
    $stalePid = Get-PortListenerPid $port
    Write-Log "Port $port busy (PID $stalePid) -- asking the existing helper to shut down."
    try {
        $req = [System.Net.WebRequest]::Create("http://127.0.0.1:$port/shutdown")
        $req.Method  = 'POST'
        $req.Timeout = 1500
        $res = $req.GetResponse()
        $res.Close()
    } catch {
        # old helper might not implement /shutdown, or might have died mid-handler. either way we still try to bind below.
    }
    for ($i = 0; $i -lt 8; $i++) {
        Start-Sleep -Milliseconds 250
        $listener = New-Listener $port
        if ($listener) { break }
    }
    if (-not $listener -and $stalePid -gt 0 -and $stalePid -ne $PID) {
        # shutdown didnt unstick it. force-kill the wedged listener. were running under the same obs process tree, so we have rights to kill our predecessor.
        Write-Log "Force-killing wedged helper PID $stalePid."
        try {
            Stop-Process -Id $stalePid -Force -ErrorAction Stop
        } catch {
            Write-Log "Stop-Process PID ${stalePid} failed: $($_.Exception.Message)"
        }
        for ($i = 0; $i -lt 16; $i++) {
            Start-Sleep -Milliseconds 250
            $listener = New-Listener $port
            if ($listener) { break }
        }
    }
    if (-not $listener) {
        Write-Log "Could not bind 127.0.0.1:$port after takeover attempt -- giving up."
        exit 0
    }
    Write-Log "Took over port $port from the previous helper (PID $stalePid)."
}

Write-Log "Streamable helper listening on http://$script:HOST_ADDR`:$port"

# surface admin / parent-process info at startup so its trivial to diagnose "vss not admin" later. with an elevation script that auto-re-launches obs, the lua can spawn the helper twice -- once under the pre-elevation non-admin obs (which then exits), and once under the post-elevation admin obs. the non-admin one can win port races and stick around as a useless orphan. the parent-process watchdog below kills the orphan when its obs goes away.
$script:IsAdmin = Test-IsAdmin
# pin the parent process by both pid and starttime so that pid reuse (rare, but possible on long-running windows sessions) cant fool the orphan watchdog. if get-process later returns a process at $script:parentpid whose starttime differs from what we record here, thats a diffrent process that happened to recycle the pid -- our original parent is gone.
try {
    $myProc = Get-CimInstance Win32_Process -Filter "ProcessId=$PID" -ErrorAction SilentlyContinue
    $script:ParentPid = if ($myProc) { [int]$myProc.ParentProcessId } else { 0 }
    # resolve the parents process name. we try two sources becuase get-process fails (returns $null) when the parent is at a higher integrity level than us -- e.g. an admin obs64 launching a non-admin helper via lua spawn_hidden. cims win32_process can often still see the name in that case.
    $script:ParentName = '(unknown)'
    $script:ParentStartTime = $null
    if ($script:ParentPid -gt 0) {
        $parentProc = Get-Process -Id $script:ParentPid -ErrorAction SilentlyContinue
        if ($parentProc) {
            $script:ParentName = $parentProc.ProcessName
            try { $script:ParentStartTime = $parentProc.StartTime } catch {}
        } else {
            $parentCim = Get-CimInstance Win32_Process -Filter "ProcessId=$script:ParentPid" -ErrorAction SilentlyContinue
            if ($parentCim -and $parentCim.Name) {
                # win32_process.name -> "obs64.exe"; strip .exe to match get-process.processname semantics so the ^obs regex used by the polling-fallback watchdog still matches.
                $script:ParentName = ($parentCim.Name -replace '\.exe$', '')
                if ($parentCim.CreationDate) {
                    try { $script:ParentStartTime = [DateTime]$parentCim.CreationDate } catch {}
                }
            }
        }
    }
} catch {
    $script:ParentPid = 0
    $script:ParentName = '(unknown)'
    $script:ParentStartTime = $null
}
Write-Log ("Helper PID=$PID admin=$script:IsAdmin parent=$script:ParentPid " +
           "($script:ParentName, started $script:ParentStartTime)")

# open a real os handle on the parent so the watchdog can waitforsingleobject instead of polling get-process. waitforsingleobject becomes signaled the instant the parent process is destroyed -- no zombie-state grace period the way get-process has -- so end task on obs64 kills the helper within one accept-loop iteration. falls back to the polling watchdog if openprocess fails for any reason (parent already gone, permission denied, etc.).
$script:ParentHandle = [IntPtr]::Zero
# open the parent handle (process_query_limited_information) so we can poll its exit state across integrity levels. if this fails, were at the wrong integrity level to manage our parent (or our parent is already dead) -- either way, the right move is to exit now so that the helper spawned by the correctly-elevated obs can take over without us blocking port 8767 + the singleton mutex. without this gate, a non-admin orphan would jam port 8767 forever and also fail to style obs64s windows due to uipi.
if ($script:ParentPid -gt 0) {
    try {
        $script:ParentHandle = [StreamableNative]::OpenParentForSync([uint32]$script:ParentPid)
        if ($script:ParentHandle -eq [IntPtr]::Zero) {
            Write-Log "OpenParentForSync($script:ParentPid) returned NULL -- this helper has the wrong integrity level for its parent (or parent is dead). Exiting so the correctly-elevated helper can bind 8767."
            if ($script:SingletonMutex) {
                try { $script:SingletonMutex.ReleaseMutex() } catch {}
                try { $script:SingletonMutex.Close() }       catch {}
            }
            exit 0
        }
        Write-Log "OpenParentForSync($script:ParentPid) ok; watchdog will use handle wait."
    } catch {
        Write-Log "OpenParentForSync threw: $($_.Exception.Message)"
    }
}

# bind this helper process to a windows job object with job_object_limit_kill_on_job_close. any worker child we spawn (compress / trim / upload powershell instances, and the ffmpeg they launch) inherits the job automatically on windows 8+. when this helper exits -- obs shutting down, the user reloading the lua script, "end task" in task manager, or even a crash -- the last reference to the job is released and the kernel terminates every process still in it. that stops orphaned ffmpeg workers from continuing to encode into clip files no ui is watching.
try {
    $script:JobHandle = [StreamableNative]::CreateKillOnCloseJob()
    if ($script:JobHandle -ne [IntPtr]::Zero) {
        Write-Log "Job object created (kill-on-close); workers will be auto-terminated on helper exit."
    } else {
        Write-Log "warn: CreateJobObject returned NULL -- worker cleanup will rely on parent-PID watchdog only."
    }
} catch {
    Write-Log "warn: Job object setup threw: $($_.Exception.Message)"
}

# restore the signed-in session (if any) from the dpapi blob. if streamables /me rejects the stored cookie, this drops the blob silently and we boot as anonymous.
try { Restore-AuthAtStartup } catch { Write-Log "Restore-AuthAtStartup: $($_.Exception.Message)" }

# shared runspace pool for in-process workers + their watchers. sized to 2x the burst job cap so that for each concurrent encode we can simultaneously hold: one slot for the compress / trim / upload worker runspace one slot for the watcher runspace that blocks on endinvoke. without the 2x sizing, the watcher would have to queue behind the worker its waiting on and deadlock. trimming this back to 1x is safe only if all jobs are reworked to not need a seperate watcher.
$script:WatcherPool = [runspacefactory]::CreateRunspacePool(1, ($script:MAX_BURST_CONCURRENT_VIDEO_JOBS * 2))
$script:WatcherPool.Open()

# single-threaded accept loop. pending() lets us cooperatively check the shutdown flag every ~50 ms instead of blocking in accepttcpclient forever (which we couldnt interrupt cleanly). also: parent-process watchdog. with the users auto-elevation flow we can end up spawned by a non-admin obs that then exits when its elevated replacement takes over. without this, the orphan helper happily holds port 8767 forever, blocking the admin obss helper from binding. once every 5s we check if our original parent (an obs64.exe / obs.exe) is still around; if not, we voluntarily exit so any newer helper can claim the port.
$script:LastParentCheck = [DateTime]::MinValue
function Check-ParentAlive {
    # kernel-level signal first. doesnt poll, doesnt throttle: if the parent handle is signaled, the parent has been terminated and we can act immediately. this catches end task in task manager within one accept-loop iteration (50 ms) instead of up to 5 s.
    if ($script:ParentHandle -ne [IntPtr]::Zero) {
        if ([StreamableNative]::ParentExited($script:ParentHandle, 0)) {
            Write-Log "Parent process handle signaled (terminated). Exiting."
            return $false
        }
    }
    $now = [DateTime]::UtcNow
    if (($now - $script:LastParentCheck).TotalSeconds -lt 5) { return $true }
    $script:LastParentCheck = $now
    if ($script:ParentPid -le 0) { return $true } # never knew parent; dont enforce
    # note: no ^obs name filter here. lua spawn_hidden always creates a direct child of obs64.exe, so if we had a parent pid at boot, that pid was obs64. the name might still resolve to (unknown) if obs64 ran at higher integrity than us (cross-integrity get-process fails) -- but the pid is still the right one to watch. filtering on name would skip the watchdog entirely in exactly the elevation-mismatch case we need it for.

    # identify our specific parent obs by pid + starttime.
    $alive = Get-Process -Id $script:ParentPid -ErrorAction SilentlyContinue
    if ($alive -and $script:ParentStartTime) {
        try {
            if ($alive.StartTime -ne $script:ParentStartTime) {
                # pid is being held by a diffrent process now.
                $alive = $null
            }
        } catch {
            # some processes (cross-integrity, protected) dont expose starttime to us; assume genuine match rather than false positive on pid reuse.
        }
    }
    if ($alive) { return $true }

    # specifically-our parent is gone. exit. the takeover dance in the next helper (whether admin or non-admin) will reclaim port 8767 via post /shutdown -> retry -> force-kill, so handing off cleanly during elevation transitions just means both this and the new helper need to be allowed to run their own startup logic. staying alive here based on "some other obs64 is up" would jam port 8767 and prevent the new helper from binding.
    Write-Log "Orphaned: original parent PID $($script:ParentPid) ($($script:ParentName), started $script:ParentStartTime) is gone. Exiting."
    return $false
}

try {
    while (-not $script:State.Shutdown) {
        if (-not (Check-ParentAlive)) { break }
        if (-not $listener.Pending()) {
            # poll parent first (cheap immediate-return getexitcodeprocess call), then idle 50 ms. the poll catches end task within one accept-loop iteration even across integrity levels -- process_query_limited_information (which is what parentexited holds) was specifically designed to work low-integrity -> high-integrity, where the older synchronize-based wait was rejected by the kernel.
            if ($script:ParentHandle -ne [IntPtr]::Zero -and
                [StreamableNative]::ParentExited($script:ParentHandle, 0)) {
                Write-Log "Parent terminated during idle (GetExitCodeProcess). Exiting."
                break
            }
            Start-Sleep -Milliseconds 50
            continue
        }
        $client = $listener.AcceptTcpClient()
        try {
            Handle-Connection $client
        } catch {
            Write-Log "handler error: $($_.Exception.Message)"
        }
    }
} catch {
    Write-Log "accept loop error: $($_.Exception.Message)"
} finally {
    try { Stop-ClipFolderWatcher } catch {}
    try { $listener.Stop() } catch {}
    # deliberately not calling $script:watcherpool.close() here. runspacepool.close() blocks until every in-flight runspace drains. our compress / trim watcher runspaces are blocked inside endinvoke() of a worker thats itself blocked inside $proc.waitforexit() on a long ffmpeg encode -- so close() waits forever, the helper never exits, and the job object never gets a chance to reap the encoder. end-tasking obs64 would then leave both the helper and the ffmpeg children alive in task manager. skipping close() is safe: environment.exit in the c# launcher tears the process down a few lines later, which releases the last handle on the job object, which kills every still-alive ffmpeg in the job. were not leaking anything that outlives the process.

    # if a /clear-streamable-cookies request queued a cookie cleanup, run it now while obs is exiting. we poll for the cookies file to become writable (obs-cef releases the file_share_none lock as the obs-browser plugin shuts down) for up to 20 seconds, then open the sqlite db and delete every streamable.com row.
    if ($script:ClearStreamableOnExit) {
        $obsCookies = Join-Path $env:APPDATA 'obs-studio\plugin_config\obs-browser\Network\Cookies'
        Write-Log "ClearStreamableOnExit: waiting for OBS to release $obsCookies"
        $unlocked = $false
        for ($i = 0; $i -lt 80; $i++) {
            try {
                $h = [System.IO.File]::Open($obsCookies, 'Open', 'ReadWrite', 'None')
                $h.Close()
                $unlocked = $true
                break
            } catch {
                Start-Sleep -Milliseconds 250
            }
        }
        if (-not $unlocked) {
            Write-Log "ClearStreamableOnExit: timed out waiting for cookies file unlock; skipping."
        } else {
            try {
                # open the live db read/write and delete every cookie that participates in the streamable + google oauth silent-reauth chain. why this list: streamable.com: our actual session cookies google.com (covers .google.com and accounts.google.com): __secure-1psid / __secure-3psid / sid / hsid / ssid / apisid / sapisid / lsid / nid. streamables /login page loads `gsi/client` which does google identity services silent oauth (prompt=none / fedcm / one tap). with googles session cookies still present, the next visit to /login silently re-mints a streamable session even though we wiped the streamable.com rows. googleapis.com: bearer / refresh-token-adjacent storage google sometimes uses. winsqlite3.dll p/invoke is already loaded at startup as [winsqlite].
                $uri = 'file:' + (($obsCookies -replace '\\','/') -replace ' ','%20')
                $db = [IntPtr]::Zero
                # sqlite_open_readwrite=0x02 | sqlite_open_uri=0x40 = 0x42. the class only exports readonly+uri as static consts; use the literal numeric value for the write path rather than a non-existent [winsqlite]::sqlite_open_readwrite (which silently returned $null -> flags=0 -> misuse).
                $rc = [WinSqlite]::sqlite3_open_v2(
                    [WinSqlite]::Utf8Z($uri), [ref]$db,
                    0x42,
                    [IntPtr]::Zero)
                if ($rc -ne [WinSqlite]::SQLITE_OK) {
                    Write-Log "ClearStreamableOnExit: sqlite3_open_v2 rc=$rc"
                } else {
                    try {
                        $sql = "DELETE FROM cookies WHERE " +
                               "host_key LIKE '%streamable.com' OR " +
                               "host_key LIKE '%google.com' OR " +
                               "host_key LIKE '%googleapis.com' OR " +
                               "host_key LIKE '%facebook.com' OR " +
                               "host_key LIKE '%facebook.net'"
                        $stmt = [IntPtr]::Zero
                        $rc = [WinSqlite]::sqlite3_prepare_v2(
                            $db, [WinSqlite]::Utf8Z($sql), -1, [ref]$stmt, [IntPtr]::Zero)
                        if ($rc -eq [WinSqlite]::SQLITE_OK) {
                            $sr = [WinSqlite]::sqlite3_step($stmt)
                            [void][WinSqlite]::sqlite3_finalize($stmt)
                            # changes() reports rows deleted by the last dml on this connection.
                            $stmt2 = [IntPtr]::Zero
                            $cnt = 0
                            $rc2 = [WinSqlite]::sqlite3_prepare_v2(
                                $db, [WinSqlite]::Utf8Z("SELECT changes()"),
                                -1, [ref]$stmt2, [IntPtr]::Zero)
                            if ($rc2 -eq [WinSqlite]::SQLITE_OK) {
                                if ([WinSqlite]::sqlite3_step($stmt2) -eq [WinSqlite]::SQLITE_ROW) {
                                    $cnt = [WinSqlite]::sqlite3_column_int64($stmt2, 0)
                                }
                                [void][WinSqlite]::sqlite3_finalize($stmt2)
                            }
                            Write-Log "ClearStreamableOnExit: DELETE step rc=$sr rows=$cnt (101=DONE expected)."
                        } else {
                            Write-Log "ClearStreamableOnExit: prepare rc=$rc"
                        }
                    } finally {
                        [void][WinSqlite]::sqlite3_close_v2($db)
                    }
                }

                # also wipe local storage + indexeddb for those origins so any cached oauth state cant replay either. we do this by deleting the per-origin subdirectories under the cef profile -- the next time the origin is visited, fresh empty storage is created automatically.
                $obsProfile = Join-Path $env:APPDATA 'obs-studio\plugin_config\obs-browser'
                $idxDir = Join-Path $obsProfile 'IndexedDB'
                if (Test-Path -LiteralPath $idxDir) {
                    Get-ChildItem -LiteralPath $idxDir -Force -Directory -ErrorAction SilentlyContinue |
                        Where-Object { $_.Name -match '^https?_(streamable\.com|accounts\.google\.com|google\.com|googleapis\.com|facebook\.com)_' } |
                        ForEach-Object {
                            try {
                                Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction Stop
                                Write-Log "ClearStreamableOnExit: removed IndexedDB $($_.Name)"
                            } catch {
                                Write-Log "ClearStreamableOnExit: IndexedDB $($_.Name): $($_.Exception.Message)"
                            }
                        }
                }
                # local storage uses a single leveldb shared by all origins -- we cant surgically delete per-origin keys without a leveldb library. the cookies wipe is the critical part anyway; localstorage doesnt carry oauth re-auth state on its own.
            } catch {
                Write-Log "ClearStreamableOnExit: $($_.Exception.Message)"
            }
        }
    }

    # restart broker: if /restart-obs-clean was the reason were exiting, relaunch obs now that the streamable cookies are gone. were admin, so the new obs inherits our admin token (no uac).
    if ($script:RestartAfterClean -and $script:RestartAfterClean.obsPath) {
        $obsPath = $script:RestartAfterClean.obsPath

        # clear the unclean-exit sentinel before relaunching, otherwise obs sees a leftover run_<uuid> file from the instance we just stop-processd and shows its "safe mode" prompt at startup. obs creates one of these on launch and deletes it on graceful exit; stop-process -force never gets to delete it.
        $sentinelDir = Join-Path $env:APPDATA 'obs-studio\.sentinel'
        if (Test-Path -LiteralPath $sentinelDir) {
            try {
                Get-ChildItem -LiteralPath $sentinelDir -Filter 'run_*' -Force -ErrorAction SilentlyContinue |
                    ForEach-Object {
                        try {
                            Remove-Item -LiteralPath $_.FullName -Force -ErrorAction Stop
                            Write-Log "RestartAfterClean: removed unclean-exit sentinel $($_.Name)"
                        } catch {
                            Write-Log "RestartAfterClean: could not remove $($_.Name): $($_.Exception.Message)"
                        }
                    }
            } catch {
                Write-Log "RestartAfterClean: sentinel cleanup error: $($_.Exception.Message)"
            }
        }

        Write-Log "RestartAfterClean: launching $obsPath"
        try {
            # use processstartinfo so we can set workingdirectory to obss install dir (it needs that for relative dll loads).
            $psi = New-Object System.Diagnostics.ProcessStartInfo
            $psi.FileName         = $obsPath
            $psi.Arguments        = '--background-color=ff272a33 --default-background-color=ff272a33'
            $psi.WorkingDirectory = [System.IO.Path]::GetDirectoryName($obsPath)
            $psi.UseShellExecute  = $true
            [void][System.Diagnostics.Process]::Start($psi)
            Write-Log "RestartAfterClean: launch issued."
        } catch {
            Write-Log "RestartAfterClean: launch failed: $($_.Exception.Message)"
        }
    }
}

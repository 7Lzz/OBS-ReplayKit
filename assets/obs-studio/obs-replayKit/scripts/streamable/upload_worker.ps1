# upload_worker.ps1 uploads a clip to streamable using the same anonymous flow streamable.com itself uses (no account needed): curl get /api/v1/uploads/shortcodesize=n (cookie jar) curl post <s3 url> multipart-form with the aws fields + file curl post /api/v1/transcode/<shortcode> (cookie jar) we use curl.exe (built into windows 10+) for the http becuase powershell 5.1s invoke-webrequest is unreliable when winhttp proxy auto-detect is slow or misconfigured -- it tends to hang silently before even hitting the wire. curl bypasses all of that. on success/failure writes structured progress and final result data to statusfile. the helper reads that json and updates the dock state. invoked by streamable_upload.lua via createprocessa + create_no_window so absolutely nothing flashes a console window.

param(
    [Parameter(Mandatory=$true)][string]$Clip,
    [string]$LogFile = "",
    [string]$StatusFile = "",
    [string]$RequestId = "",
    # if provided, this is a curl cookie jar containing the signed-in streamable session cookies (saved by the helper after /login). steps 1 and 3 will pass it via -b so the upload is attributed to the user -- longer retention + the plans size cap on the server.
    [string]$AuthJar = "",
    [switch]$RequireAuth,
    [int]$ProgressBase = 0,
    [int]$ProgressSpan = 100,
    # [switch] rather than [bool]: powershells -file invocation refuses to bind the string "true"/"false" to a [bool] parameter (it accepts only $true/$false/1/0). using a switch sidesteps that entirely -- callers add -loggingenabled to enable, omit it to disable.
    [switch]$LoggingEnabled
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RequestId)) {
    $RequestId = [Guid]::NewGuid().ToString("N")
}
$ProgressBase = [Math]::Max(0, [Math]::Min(99, $ProgressBase))
$ProgressSpan = [Math]::Max(1, [Math]::Min(100, $ProgressSpan))
$tempPrefix = Join-Path $env:TEMP ("streamable_" + $RequestId)
$jar = "$tempPrefix.cookies.txt"
$s3RespPath = "$tempPrefix.s3_resp.txt"
$transcodeBodyPath = "$tempPrefix.transcode_body.json"
$curlProgressPath = "$tempPrefix.curl_progress.txt"
$tempFiles = @($jar, $s3RespPath, $transcodeBodyPath, $curlProgressPath)
$exitCode = 0

function L($msg) {
    if (-not $LoggingEnabled) { return }
    $rid = if ($RequestId) { " request=$RequestId" } else { "" }
    $line = "[{0}] area=upload{1} {2}" -f (Get-Date -Format 'HH:mm:ss'), $rid, $msg
    Write-Host $line
    if ($LogFile -ne "") {
        $dir = Split-Path -Parent $LogFile
        if ($dir -and -not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }
        Add-Content -Path $LogFile -Value $line -ErrorAction SilentlyContinue
    }
}

function Write-Status([string]$state, [string]$phase, [int]$percent, [string]$message = "", [string]$Url = "") {
    if ($StatusFile -eq "") { return }
    if ($state -eq "error") {
        $percent = 0
    } elseif ($state -eq "done") {
        $percent = 100
    } else {
        $percent = [int][Math]::Round($ProgressBase + (($ProgressSpan * [double]$percent) / 100.0))
        $percent = [Math]::Max(0, [Math]::Min(99, $percent))
    }
    $obj = [ordered]@{
        requestId = $RequestId
        state     = $state
        phase     = $phase
        percent   = $percent
        message   = $message
        updatedAt = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    }
    if (-not [string]::IsNullOrWhiteSpace($Url)) {
        $obj.url = $Url
    }
    $json = ConvertTo-Json $obj -Compress
    $tmp = "$StatusFile.tmp"
    [System.IO.File]::WriteAllText($tmp, $json, [System.Text.Encoding]::UTF8)
    Move-Item -LiteralPath $tmp -Destination $StatusFile -Force
}

function Quote-Arg([string]$arg) {
    if ([string]::IsNullOrEmpty($arg)) { return '""' }
    if ($arg -notmatch '[\s"]') { return $arg }
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('"')
    $slashes = 0
    for ($i = 0; $i -lt $arg.Length; $i++) {
        $c = $arg[$i]
        if ($c -eq '\') {
            $slashes++
        } elseif ($c -eq '"') {
            [void]$sb.Append('\' * ($slashes * 2 + 1))
            [void]$sb.Append('"')
            $slashes = 0
        } else {
            [void]$sb.Append('\' * $slashes)
            [void]$sb.Append($c)
            $slashes = 0
        }
    }
    [void]$sb.Append('\' * ($slashes * 2))
    [void]$sb.Append('"')
    return $sb.ToString()
}

function Run-Curl {
    # $args is a reserved powershell automatic variable, so we use $curlargs to avoid silently breaking the splat below.
    param([string[]]$CurlArgs)
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = "curl.exe"
    $psi.Arguments = (($CurlArgs | ForEach-Object { Quote-Arg $_ }) -join " ")
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    [void]$proc.Start()
    $stdout = $proc.StandardOutput.ReadToEnd()
    $stderr = $proc.StandardError.ReadToEnd()
    $proc.WaitForExit()
    return @{
        ExitCode = $proc.ExitCode
        Body     = [string]$stdout
        Error    = [string]$stderr
    }
}

function Get-CurlTransportArgs {
    return @(
        "--http1.1",
        "--tlsv1.2",
        "--ssl-revoke-best-effort",
        "--connect-timeout", "15",
        "--retry", "2",
        "--retry-delay", "1",
        "--retry-all-errors"
    )
}

function Run-CurlUploadWithProgress {
    param(
        [string[]]$CurlArgs,
        [int]$StartPercent,
        [int]$EndPercent,
        [string]$ProgressFile
    )
    Remove-Item -LiteralPath $ProgressFile -ErrorAction SilentlyContinue
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = "curl.exe"
    $psi.Arguments = (($CurlArgs | ForEach-Object { Quote-Arg $_ }) -join " ")
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    [void]$proc.Start()
    $stdoutTask = $proc.StandardOutput.ReadToEndAsync()
    $stderrTask = $proc.StandardError.ReadToEndAsync()
    $lastPct = -1
    while (-not $proc.HasExited) {
        try {
            if (Test-Path -LiteralPath $ProgressFile) {
                $raw = [System.IO.File]::ReadAllText($ProgressFile)
                $matches = [regex]::Matches($raw, '(\d{1,3}(?:\.\d+)?)%')
                if ($matches.Count -gt 0) {
                    $curlPct = [double]::Parse(
                        $matches[$matches.Count - 1].Groups[1].Value,
                        [Globalization.CultureInfo]::InvariantCulture)
                    $curlPct = [Math]::Max(0.0, [Math]::Min(100.0, $curlPct))
                    $mapped = [int][Math]::Floor($StartPercent + (($EndPercent - $StartPercent) * $curlPct / 100.0))
                    if ($mapped -gt $lastPct) {
                        $lastPct = $mapped
                        Write-Status "uploading" "uploading" $mapped
                    }
                }
            }
        } catch {}
        Start-Sleep -Milliseconds 500
    }
    $proc.WaitForExit()
    $stdout = $stdoutTask.Result
    $stderr = $stderrTask.Result
    $progressText = ""
    try {
        if (Test-Path -LiteralPath $ProgressFile) {
            $progressText = [System.IO.File]::ReadAllText($ProgressFile)
        }
    } catch {}
    return @{
        ExitCode = $proc.ExitCode
        Body     = [string]$stdout
        Error    = ((@($stderr, $progressText) | Where-Object { $_ }) -join "`n")
    }
}

Write-Status "uploading" "preparing" 1

try {
    if (-not (Test-Path -LiteralPath $Clip)) {
        throw "Clip not found: $Clip"
    }
    $clipItem = Get-Item -LiteralPath $Clip
    $size = $clipItem.Length
    L "Uploading $($clipItem.Name) ($([math]::Round($size / 1MB, 2)) MB)"
    Write-Status "uploading" "preparing" 5

    # cookie jar shared between step 1 (which sets the session cookie) and step 3 (which needs it -- transcode returns 403 without it).
    Remove-Item -LiteralPath $jar -ErrorAction SilentlyContinue

    # if we were given an auth cookie jar (signed-in user), seed our session jar with its contents so step 1 goes out authenticated. we copy rather than just -bing the auth jar so curl can still -c into it (streamable issues additional session cookies during upload).
    $signedIn = $false
    if ($RequireAuth -and ([string]::IsNullOrWhiteSpace($AuthJar) -or -not (Test-Path -LiteralPath $AuthJar))) {
        throw "Signed-in Streamable session is unavailable. Sign out and sign in again."
    }
    if (-not [string]::IsNullOrWhiteSpace($AuthJar)) {
        if (-not (Test-Path -LiteralPath $AuthJar)) {
            throw "Signed-in Streamable session file is missing. Sign out and sign in again."
        }
        try {
            Copy-Item -LiteralPath $AuthJar -Destination $jar -Force
            $signedIn = $true
            L "Auth: using signed-in session cookies from $AuthJar"
        } catch {
            throw "Could not prepare signed-in Streamable session: $($_.Exception.Message)"
        }
    }

    # step 1: get shortcode + presigned s3 form
    L "Step 1: requesting upload shortcode..."
    Write-Status "uploading" "requesting upload" 10
    $url1 = "https://api-f.streamable.com/api/v1/uploads/shortcode?size=$size&version=unknown"
    $step1Args = @((Get-CurlTransportArgs) + @(
        "-s", "-S", # silent progress, but keep errors
        "-m", "30", # 30s total timeout
        "-c", $jar, # save cookies
        "-H", "Origin: https://streamable.com",
        "-H", "Referer: https://streamable.com/",
        "-H", "Pragma: no-cache",
        "-H", "Cache-Control: no-cache",
        "-A", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"
    ))
    if ($signedIn) { $step1Args += @("-b", $jar) }
    $step1Args += $url1
    $r1 = Run-Curl $step1Args
    if ($r1.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($r1.Body)) {
        $detail = ((@($r1.Error, $r1.Body) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join " ").Trim()
        if ([string]::IsNullOrWhiteSpace($detail)) { $detail = "no curl output" }
        throw "Step 1 curl failed (exit=$($r1.ExitCode)). Output: $detail"
    }
    $data = $r1.Body | ConvertFrom-Json
    $shortcode = $data.shortcode
    if (-not $shortcode) {
        throw "Step 1 response missing shortcode. Body: $($r1.Body.Substring(0, [Math]::Min(400, $r1.Body.Length)))"
    }
    L "shortcode: $shortcode"

    # step 2: post file (multipart) to s3
    L "Step 2: uploading to S3..."
    Write-Status "uploading" "uploading" 35
    # build curl -f args. field order matters: every aws form field first, then "file" last. convertfrom-json gives us the fields in insertion (json) order, which already matches what streamable.com itself sends.
    $forms = @()
    foreach ($f in $data.fields.PSObject.Properties) {
        $forms += "-F"
        $forms += "$($f.Name)=$($f.Value)"
    }
    $forms += "-F"
    $forms += "file=@$($clipItem.FullName)"

    $r2 = Run-CurlUploadWithProgress (@((Get-CurlTransportArgs) + @(
        "--progress-bar",
        "--stderr", $curlProgressPath,
        "-m", "900", # 15 minute upload cap
        "-o", $s3RespPath,
        "-w", "%{http_code}",
        "-X", "POST",
        $data.url
    ) + $forms)) 35 84 $curlProgressPath
    if ($r2.ExitCode -ne 0) {
        throw "S3 upload curl failed (exit=$($r2.ExitCode)). Output: $($r2.Error)"
    }
    $s3Status = [int]$r2.Body.Trim()
    if ($s3Status -lt 200 -or $s3Status -ge 300) {
        $errBody = Get-Content -LiteralPath $s3RespPath -Raw -ErrorAction SilentlyContinue
        if ([string]::IsNullOrWhiteSpace($errBody) -and $r2.Error) { $errBody = $r2.Error }
        throw "S3 upload failed: HTTP $s3Status. Body: $errBody"
    }
    L "S3: HTTP $s3Status"
    Write-Status "uploading" "finalizing" 85

    # step 3: trigger transcoding
    L "Step 3: triggering transcode..."
    Write-Status "uploading" "finalizing" 90
    ($data.transcoder_options | ConvertTo-Json -Compress) |
        Out-File -FilePath $transcodeBodyPath -Encoding ASCII -NoNewline

    $r3 = Run-Curl @((Get-CurlTransportArgs) + @(
        "-s", "-S",
        "-m", "30",
        "-b", $jar, # send cookies
        "-w", "`n%{http_code}",
        "-X", "POST",
        "-H", "Origin: https://streamable.com",
        "-H", "Referer: https://streamable.com/",
        "-H", "Content-Type: application/json",
        "-A", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)",
        "--data", "@$transcodeBodyPath",
        "https://api-f.streamable.com/api/v1/transcode/$shortcode"
    ))
    if ($r3.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($r3.Body)) {
        $detail = ((@($r3.Error, $r3.Body) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join " ").Trim()
        if ([string]::IsNullOrWhiteSpace($detail)) { $detail = "no curl output" }
        throw "Transcode trigger curl failed (exit=$($r3.ExitCode)). Output: $detail"
    }
    $tcLines = $r3.Body -split "`n"
    $tcStatus = 0
    if (-not [int]::TryParse($tcLines[-1].Trim(), [ref]$tcStatus)) {
        throw "Transcode trigger returned an unreadable status. Body: $($r3.Body.Substring(0, [Math]::Min(400, $r3.Body.Length)))"
    }
    if ($tcStatus -lt 200 -or $tcStatus -ge 300) {
        $tcBody = ($tcLines[0..($tcLines.Count-2)] -join "`n")
        throw "Transcode trigger failed: HTTP $tcStatus. Body: $tcBody"
    }
    L "transcode: HTTP $tcStatus"
    Write-Status "uploading" "copying link" 98

    $finalUrl = "https://streamable.com/$shortcode"
    L "DONE: $finalUrl"
    Write-Status "done" "done" 100 "" $finalUrl

    # push to clipboard so the user can paste anywhere immediately, without needing to look at the dock.
    Set-Clipboard -Value $finalUrl

} catch {
    $msg = $_.Exception.Message
    L "ERROR: $msg"
    Write-Status "error" "error" 0 $msg
    $exitCode = 1
} finally {
    foreach ($f in $tempFiles) {
        if ($f -and (Test-Path -LiteralPath $f)) {
            try { Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue } catch {}
        }
    }
}

exit $exitCode

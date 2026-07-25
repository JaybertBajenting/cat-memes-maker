<#
    CatMeme Maker launcher.

    Started by the desktop shortcut. Verifies the database is reachable, starts the
    published API (which also serves the React UI), waits for it to answer, then opens
    a browser. Closing this window stops the app.
#>

$ErrorActionPreference = 'Stop'

$Root    = Split-Path -Parent $PSScriptRoot
$Dist    = Join-Path $Root 'dist'
$Exe     = Join-Path $Dist 'CatMeme.Api.exe'
$Url     = 'http://localhost:5099'

$Host.UI.RawUI.WindowTitle = 'CatMeme Maker'

function Write-Step($msg) { Write-Host "  $msg" -ForegroundColor Gray }
function Write-Ok($msg)   { Write-Host "  $msg" -ForegroundColor Green }
function Write-Bad($msg)  { Write-Host "  $msg" -ForegroundColor Red }

Write-Host ''
Write-Host '  CatMeme Maker' -ForegroundColor Magenta
Write-Host '  ---------------------------------' -ForegroundColor DarkGray

# --- preflight -------------------------------------------------------------
if (-not (Test-Path $Exe)) {
    Write-Bad "Application not built. Expected: $Exe"
    Write-Step 'Run build.ps1 in the launch folder first.'
    Read-Host '  Press Enter to close'
    exit 1
}

# --- database --------------------------------------------------------------
# The app uses the full SQL Server instance (service MSSQLSERVER), not LocalDB.
#
# LocalDB was abandoned deliberately. Its auto-start races: several clients hitting a
# stopped instance at once each spawn an engine, one grabs the data file, and the one
# clients resolve to cannot open it - which surfaces as "Failed to open the explicitly
# specified database" and then a bogus CREATE DATABASE / error 5170. Worse, sqllocaldb
# reports success when the engine failed to register and failure when it started fine,
# so nothing it says can be trusted. A real service has none of these problems.
$DbServer = 'localhost'
$DbName   = 'CatMemePipeline'
$EnvFile  = Join-Path $Root '.env'
if (Test-Path $EnvFile) {
    $csLine = Select-String -Path $EnvFile -Pattern '^\s*DB_CONNECTION_STRING\s*=' | Select-Object -First 1
    if ($csLine) {
        $m = [regex]::Match($csLine.Line, 'Server=([^;]+)');   if ($m.Success) { $DbServer = $m.Groups[1].Value.Trim() }
        $m = [regex]::Match($csLine.Line, 'Database=([^;]+)'); if ($m.Success) { $DbName   = $m.Groups[1].Value.Trim() }
    }
}

Write-Step 'Checking database...'

$svc = Get-Service -Name 'MSSQLSERVER' -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne 'Running') {
    Write-Step 'SQL Server service is stopped; starting it...'
    try { Start-Service -Name 'MSSQLSERVER' -ErrorAction Stop; $svc.WaitForStatus('Running', '00:01:00') }
    catch { Write-Step 'Could not start it here (needs admin). Start "SQL Server (MSSQLSERVER)" in Services.' }
}

# A real connection is the only proof. Retried, because a service that has just started
# accepts sockets before its databases finish recovery.
$dbErr = $null
for ($i = 1; $i -le 10; $i++) {
    $conn = New-Object System.Data.SqlClient.SqlConnection `
        "Server=$DbServer;Database=$DbName;Trusted_Connection=True;TrustServerCertificate=True;Connect Timeout=30;Pooling=false"
    try { $conn.Open(); $dbErr = $null; break }
    catch { $dbErr = $_.Exception.Message.Split([Environment]::NewLine)[0] }
    finally { $conn.Dispose() }
    Start-Sleep -Seconds 2
}

if ($dbErr) {
    Write-Bad "Cannot reach database [$DbName] on $DbServer."
    Write-Step $dbErr
    Write-Step 'Check that the "SQL Server (MSSQLSERVER)" service is running, and that'
    Write-Step "DB_CONNECTION_STRING in .env points at the right server."
    Read-Host '  Press Enter to close'
    exit 1
}

Write-Ok 'Database ready.'

# --- already running? ------------------------------------------------------
try {
    $existing = Invoke-RestMethod -Uri "$Url/api/health" -TimeoutSec 2
    if ($existing.status -eq 'ok') {
        Write-Ok 'App is already running - opening browser.'
        Start-Process $Url
        exit 0
    }
}
catch {
    # not running yet, which is the normal path
}

# --- start the app ---------------------------------------------------------
Write-Step 'Starting pipeline service...'
$proc = Start-Process -FilePath $Exe -WorkingDirectory $Dist -PassThru -WindowStyle Hidden

$ready = $false
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 750
    if ($proc.HasExited) { break }
    try {
        $h = Invoke-RestMethod -Uri "$Url/api/health" -TimeoutSec 2
        if ($h.status -eq 'ok') { $ready = $true; break }
    }
    catch {
        # still booting
    }
}

if (-not $ready) {
    Write-Bad 'The service did not start.'
    $log = Join-Path $Root 'pipeline\logs'
    if (Test-Path $log) {
        $latest = Get-ChildItem $log -Filter *.log | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($latest) {
            Write-Step "Last lines of $($latest.Name):"
            Get-Content $latest.FullName -Tail 15 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
        }
    }
    if (-not $proc.HasExited) { $proc.Kill() }
    Read-Host '  Press Enter to close'
    exit 1
}

Write-Ok "Running at $Url"
Write-Host ''
Write-Host '  Opening browser...' -ForegroundColor Gray
Start-Process $Url

Write-Host ''
Write-Host '  ---------------------------------' -ForegroundColor DarkGray
Write-Host '  Close this window to stop the app.' -ForegroundColor Yellow
Write-Host ''

# Keep the console alive; killing it stops the service.
try {
    Wait-Process -Id $proc.Id
}
finally {
    if (-not $proc.HasExited) {
        Write-Host '  Stopping...' -ForegroundColor Gray
        $proc.Kill()
    }
}

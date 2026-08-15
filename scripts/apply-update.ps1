# apply-update.ps1 - dsh-app self-update executor
# (written to disk by AppUpdater from the embedded template, then launched)
#
# Responsibilities: wait for old instance to exit -> backup old exe ->
# replace with new exe -> launch -> 45s dual verification (process alive +
# port HTTP ready) -> on failure roll back automatically and write
# rolled-back.flag. Backup/cleanup duties belong to this script only
# (the app's startup cleanup only removes *.part/*.new/*.sha256 download
# leftovers, never backup/ or markers - prevents the "new instance cleanup
# deletes the backup and breaks rollback" race).
#
# NOTE: production launch is "-File script.ps1" with NO arguments; AppUpdater
# appends variable assignments at the end of the file to inject parameters
# (so NO Mandatory parameters here - PowerShell would hang waiting for input).
# For testing, run this template directly with -NewExe etc. Test-only switches
# (defaults keep production behavior):
#   -WaitProcessName <name> : process name to wait for (default dsh-app;
#                             tests pass a non-existent name to skip waiting)
#   -SkipStart              : do not start processes (test only)
#   -NoVerify               : skip 45s verification, treat as success (test only)
# Exit codes: 0 = success; 2 = wait timeout (not replaced); 3 = backup failed;
#             4 = replace failed (rollback attempted); 5 = verification failed
#             (rolled back).

param(
    [string]$NewExe = '',
    [string]$AppExe = '',
    [string]$BackupDir = '',
    [string]$LogFile = '',
    [int]$Port = 3080,
    [string]$WaitProcessName = 'dsh-app',
    [switch]$SkipStart,
    [switch]$NoVerify
)

# __INJECT_PARAMS_HERE__

$ErrorActionPreference = 'Stop'
$procName = $WaitProcessName

function Log([string]$m) {
    try { Add-Content -Path $LogFile -Value ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $m) -Encoding UTF8 } catch { }
}

function Test-AppAlive {
    $alive = Get-Process -Name $procName -ErrorAction SilentlyContinue
    if (-not $alive) { return $false }
    $webOk = $false
    try {
        $r = Invoke-WebRequest -Uri "http://127.0.0.1:$Port" -TimeoutSec 2 -UseBasicParsing
        if ($r.StatusCode -lt 500) { $webOk = $true }
    } catch { }
    return ($alive -and $webOk)
}

Log "updater started: new=$NewExe app=$AppExe port=$Port"

# 1) wait for all dsh-app processes to exit (60s timeout -> abort without
#    replacing; .new is kept for a later retry)
$deadline = (Get-Date).AddSeconds(60)
while ((Get-Date) -lt $deadline) {
    if (-not (Get-Process -Name $procName -ErrorAction SilentlyContinue)) { break }
    Start-Sleep -Seconds 1
}
if (Get-Process -Name $procName -ErrorAction SilentlyContinue) {
    Log "ERROR: $procName still running after 60s, aborting without replacing"
    exit 2
}
Log "old instance exited"

# 2) backup old exe
$oldVer = 'old'
try {
    $oldVer = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($AppExe).FileVersion
    if ([string]::IsNullOrWhiteSpace($oldVer)) { $oldVer = 'old' }
} catch { }
$backup = Join-Path $BackupDir ("dsh-app.$oldVer.exe")
try {
    New-Item -ItemType Directory -Path $BackupDir -Force | Out-Null
    Copy-Item -Path $AppExe -Destination $backup -Force
    Log "backed up old exe to $backup"
} catch {
    Log "ERROR backup: $_"
    exit 3
}

# 3) replace with new exe
try {
    Copy-Item -Path $NewExe -Destination $AppExe -Force
    Log "new exe copied over"
} catch {
    Log "ERROR replace: $_"
    try {
        Copy-Item -Path $backup -Destination $AppExe -Force
        Log "rolled back after replace failure"
    } catch { Log "ERROR rollback after replace failure: $_" }
    exit 4
}

# 4) launch new exe (SkipStart is a test switch; production does not pass it)
if (-not $SkipStart) {
    try {
        Start-Process -FilePath $AppExe | Out-Null
        Log "new instance launched"
    } catch {
        Log "ERROR start new instance: $_"
    }
}

# 5) 45s dual verification (process alive + port ready; cold start can take
#    up to ~30s, 45s leaves headroom)
$ok = $false
if (-not $NoVerify) {
    $deadline = (Get-Date).AddSeconds(45)
    while ((Get-Date) -lt $deadline) {
        if (Test-AppAlive) { $ok = $true; break }
        Start-Sleep -Seconds 2
    }
} else {
    $ok = $true
}

if (-not $ok) {
    Log "startup verification failed, rolling back"
    # kill the new instance that failed verification (the only possible
    # dsh-app process at this point is the new one)
    Get-Process -Name $procName -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    try {
        Copy-Item -Path $backup -Destination $AppExe -Force
        if (-not $SkipStart) {
            Start-Process -FilePath $AppExe | Out-Null
        }
        $flag = Join-Path (Split-Path $LogFile) 'rolled-back.flag'
        Set-Content -Path $flag -Value ("rolled back from " + $NewExe) -Encoding UTF8
        Log "rolled back and restarted old version"
    } catch {
        Log "ERROR rollback: $_"
        exit 5
    }
    exit 5
}

Log "update applied successfully"
# 6) success cleanup: new file, backup, script itself (updater.log is kept)
Remove-Item -Path $NewExe -Force -ErrorAction SilentlyContinue
Remove-Item -Path $backup -Force -ErrorAction SilentlyContinue
Remove-Item -Path $PSCommandPath -Force -ErrorAction SilentlyContinue
exit 0

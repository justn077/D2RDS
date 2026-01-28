# D2R mutex handle cleanup (pre-launch helper)
# Must run as Administrator
if (!([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole] "Administrator")) {
    Start-Process powershell.exe "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`"" -Verb RunAs -Wait
    exit
}

# Path to handle64.exe (must be placed next to D2RDS.exe)
$appRoot = Split-Path -Parent $PSScriptRoot
$handlePath = Join-Path $appRoot "handle64.exe"
if (!(Test-Path $handlePath)) {
    Write-Host "handle64.exe not found at: $handlePath" -ForegroundColor Red
    Write-Host "Download Sysinternals Handle and place handle64.exe next to D2RDS.exe." -ForegroundColor Yellow
    exit 1
}

Write-Host "Searching for all D2R handles..." -ForegroundColor Cyan

# Get handle info for ALL D2R processes
$handleOutput = & $handlePath -accepteula -a -p D2R.exe 2>&1

if ($handleOutput -match "No matching handles found") {
    Write-Host "D2R is not running!" -ForegroundColor Red
    exit 0
}

$handlesToKill = @()
$currentPid = $null

# Parse output and collect ALL handles
foreach($line in $handleOutput) {
    # Get PID
    if ($line -match 'D2R\.exe pid: (\d+)') {
        $currentPid = $matches[1]
    }
    # Get Handle ID for the mutex - must be Event type with the exact name
    if ($line -match '^\s*([0-9A-F]+):\s+Event\s+.*DiabloII Check For Other Instances') {
        $handleId = $matches[1]
        $handlesToKill += @{PID = $currentPid; HandleID = $handleId}
    }
}

if ($handlesToKill.Count -eq 0) {
    Write-Host "No D2R mutex handles found. Game may not be fully loaded yet." -ForegroundColor Red
    exit 0
}

Write-Host "Found $($handlesToKill.Count) D2R handle(s) to kill:" -ForegroundColor Green

# Kill all handles
foreach($handle in $handlesToKill) {
    Write-Host "  Killing PID: $($handle.PID), Handle: $($handle.HandleID)" -ForegroundColor Yellow
    & $handlePath -p $handle.PID -c $handle.HandleID -y | Out-Null
}

Write-Host "`nDone! All handles killed. You can now launch your next D2R instance." -ForegroundColor Green

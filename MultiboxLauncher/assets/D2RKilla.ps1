# D2R mutex handle cleanup (pre-launch helper)
# Must run as Administrator
if (!([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole] "Administrator")) {
    Start-Process powershell.exe "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`"" -Verb RunAs -Wait
    exit
}

# Path to handle64.exe (app root, config override, or PATH)
$appRoot = Split-Path -Parent $PSScriptRoot
$handlePath = Join-Path $appRoot "handle64.exe"
$configPath = Join-Path $appRoot "config.json"
if (Test-Path $configPath) {
    try {
        $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
        if ($cfg.preLaunch.handlePath) {
            $candidate = $cfg.preLaunch.handlePath
            $candidate = $candidate -replace "%APPDIR%", $appRoot
            $candidate = $candidate -replace "%DESKTOP%", [Environment]::GetFolderPath("Desktop")
            if (Test-Path $candidate) {
                $handlePath = $candidate
            }
        }
    } catch {
        # Ignore config parsing errors
    }
}

if (!(Test-Path $handlePath)) {
    $cmd = Get-Command handle64.exe -ErrorAction SilentlyContinue
    if ($cmd -and $cmd.Path) {
        $handlePath = $cmd.Path
    }
}

if (!(Test-Path $handlePath)) {
    Write-Host "handle64.exe not found." -ForegroundColor Red
    Write-Host "Place handle64.exe next to D2RDS.exe, set preLaunch.handlePath in config.json, or add its folder to PATH." -ForegroundColor Yellow
    exit 1
}

Write-Host "Searching for all D2R handles..." -ForegroundColor Cyan

$maxAttempts = 6
$delayMs = 700
$handlesToKill = @()
$currentPid = $null
$foundHandles = $false

for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
    # Get handle info for ALL D2R processes
    $handleOutput = & $handlePath -accepteula -a -p D2R.exe 2>&1

    if ($handleOutput -match "No matching handles found") {
        if ($attempt -lt $maxAttempts) {
            Start-Sleep -Milliseconds $delayMs
            continue
        }
        Write-Host "D2R is not running or handles not ready yet." -ForegroundColor Red
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
        if ($attempt -lt $maxAttempts) {
            Start-Sleep -Milliseconds $delayMs
            continue
        }
        Write-Host "No D2R mutex handles found. Game may not be fully loaded yet." -ForegroundColor Red
        exit 0
    }

    $foundHandles = $true
    break
}

if (!$foundHandles) {
    exit 0
}

Write-Host "Found $($handlesToKill.Count) D2R handle(s) to kill:" -ForegroundColor Green

# Kill all handles
foreach($handle in $handlesToKill) {
    Write-Host "  Killing PID: $($handle.PID), Handle: $($handle.HandleID)" -ForegroundColor Yellow
    & $handlePath -p $handle.PID -c $handle.HandleID -y | Out-Null
}

Write-Host "`nDone! All handles killed. You can now launch your next D2R instance." -ForegroundColor Green
Start-Sleep -Milliseconds 300
exit 0

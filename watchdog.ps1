# DTF Bot Watchdog — restarts DTF-Bot.exe whenever it dies
# Runs forever. Leave this window open (minimized is fine).
# Log: watchdog.log next to this script.

$ErrorActionPreference = 'SilentlyContinue'
$root = $PSScriptRoot
$exe  = Join-Path $root 'DTF-Bot.exe'
$log  = Join-Path $root 'watchdog.log'
$checkSeconds = 10

function Log($msg) {
    $line = "{0}  {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg
    Add-Content -Path $log -Value $line
    Write-Host $line
}

if (-not (Test-Path $exe)) {
    Log "FATAL: DTF-Bot.exe not found at $exe"
    exit 1
}

Log "watchdog started - checking every ${checkSeconds}s"

# if the bot is somehow already running, leave it alone and just monitor
$hadRunning = $false

while ($true) {
    $p = Get-Process -Name 'DTF-Bot' -ErrorAction SilentlyContinue

    if ($p) {
        if (-not $hadRunning) { Log "bot is running (PID $($p[0].Id))" }
        $hadRunning = $true
    }
    else {
        Log "bot NOT running -> restarting..."
        Start-Process -FilePath $exe -WorkingDirectory $root -WindowStyle Minimized
        Start-Sleep -Seconds 8
        $p2 = Get-Process -Name 'DTF-Bot' -ErrorAction SilentlyContinue
        if ($p2) { Log "restart OK (PID $($p2[0].Id))" }
        else     { Log "restart FAILED - will retry in ${checkSeconds}s" }
        $hadRunning = $false
    }

    Start-Sleep -Seconds $checkSeconds
}

$ErrorActionPreference = 'SilentlyContinue'
$w = @(Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like '*watchdog.ps1*' -and $_.ProcessId -ne $PID })
$b = @(Get-Process 'DTF-Bot' -ErrorAction SilentlyContinue)
Write-Output ("watchdogs: " + $w.Count + " | bots: " + $b.Count)

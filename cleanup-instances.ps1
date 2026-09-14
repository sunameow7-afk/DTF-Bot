$ErrorActionPreference = 'SilentlyContinue'
# kill every watchdog instance
Get-CimInstance Win32_Process |
    Where-Object { $_.CommandLine -like '*watchdog.ps1*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
# kill every bot instance
Stop-Process -Name 'DTF-Bot' -Force
Start-Sleep -Seconds 3

$w = @(Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like '*watchdog.ps1*' })
$b = @(Get-Process 'DTF-Bot' -ErrorAction SilentlyContinue)
Write-Output ("watchdogs: " + $w.Count + " | bots: " + $b.Count)

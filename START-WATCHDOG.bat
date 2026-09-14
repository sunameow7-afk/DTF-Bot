@echo off
title DTF Bot Watchdog
cd /d "%~dp0"
echo ============================================
echo   DTF Bot Watchdog
echo   Restarts DTF-Bot.exe automatically
echo   if it crashes or closes.
echo   Keep this window open (minimize is ok).
echo ============================================
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0watchdog.ps1"
pause

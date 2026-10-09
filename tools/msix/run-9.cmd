@echo off
rem #9 MSIX test in one go (one UAC prompt). Log: C:\dev\mongdock-tmp\run9\run9.log
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-9.ps1"
pause

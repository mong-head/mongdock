@echo off
rem #9 cleanup: remove MSIX test package, restore Run key, restart regular mongdock. No UAC.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-9-cleanup.ps1"
pause

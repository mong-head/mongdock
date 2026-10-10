@echo off
rem #9 cleanup: remove MSIX test package, restore Run key, restart regular mongdock. No UAC.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-9-cleanup.ps1"
rem No pause: the window closes when done. Results and failures go to the log in C:\dev\mongdock-tmp\run9\
exit /b %ERRORLEVEL%

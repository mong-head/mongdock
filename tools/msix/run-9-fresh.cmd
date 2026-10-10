@echo off
rem #9 extra: restart the MSIX build as a first-time install (moves %APPDATA%\mongdock aside). Undo with run-9-cleanup.cmd. No UAC.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-9-fresh.ps1"
rem No pause: the window closes when done. Results and failures go to the log in C:\dev\mongdock-tmp\run9\
exit /b %ERRORLEVEL%

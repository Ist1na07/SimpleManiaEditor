@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Collect-OsuDiagnostics.ps1" -DurationSeconds 1800 %*
if errorlevel 1 echo Diagnostic collection stopped or failed. Existing samples are retained.
pause

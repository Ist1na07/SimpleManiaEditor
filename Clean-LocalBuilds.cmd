@echo off
setlocal
pwsh -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Clean-LocalBuilds.ps1" %*
if errorlevel 1 (
  echo Cleanup stopped. Read the message above.
  pause
  exit /b 1
)
pause

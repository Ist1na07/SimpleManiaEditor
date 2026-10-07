@echo off
if not exist "%~dp0dist\SimpleManiaEditor\SimpleManiaEditor.exe" (
  echo The portable build is missing. See README.md for build instructions.
  pause
  exit /b 1
)
start "" "%~dp0dist\SimpleManiaEditor\SimpleManiaEditor.exe" %*

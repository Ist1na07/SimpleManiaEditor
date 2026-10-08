@echo off
if not exist "%~dp0dist\SimpleManiaEditor-0.1.7\SimpleManiaEditor.exe" (
  echo The portable build is missing. See README.md for build instructions.
  pause
  exit /b 1
)
start "" "%~dp0dist\SimpleManiaEditor-0.1.7\SimpleManiaEditor.exe" "%~dp0dist\SimpleManiaEditor-0.1.7\examples\7K-Sandbox\7K Sandbox.osu"

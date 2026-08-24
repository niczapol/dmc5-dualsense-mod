@echo off
chcp 65001 >nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Launch-DMC5.ps1"
set "DMC5DS_EXIT=%ERRORLEVEL%"
if not "%DMC5DS_EXIT%"=="0" (
  echo.
  pause
  exit /b %DMC5DS_EXIT%
)
timeout /t 2 /nobreak >nul

@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0QemuM-Diagnose.ps1" %*
echo.
pause

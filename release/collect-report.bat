@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0collect-report.ps1" %*
pause

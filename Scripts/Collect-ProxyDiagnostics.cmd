@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Collect-ProxyDiagnostics.ps1"
pause

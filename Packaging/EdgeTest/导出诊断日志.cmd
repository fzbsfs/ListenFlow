@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Export-Diagnostics.ps1" %*
set "result=%errorlevel%"
if not "%LISTENFLOW_NO_PAUSE%"=="1" pause
exit /b %result%

@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-ListenFlow.ps1" %*
set "result=%errorlevel%"
if not "%result%"=="0" echo Install failed with exit code: %result%
if not "%LISTENFLOW_NO_PAUSE%"=="1" pause
endlocal & exit /b %result%

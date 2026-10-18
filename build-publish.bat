@echo off
setlocal
cd /d "%~dp0"

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-release.ps1"

if errorlevel 1 (
  echo.
  echo [错误] 发布失败。请保留上方完整错误信息。
  pause
  exit /b 1
)

echo.
echo 发布完成。请检查 publish 文件夹中的 ListenFlow.exe。
pause

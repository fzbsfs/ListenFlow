@echo off
chcp 65001 >nul
title ListenFlow 人工验收版安装
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-ListenFlow.ps1"
set "result=%errorlevel%"
echo.
if not "%result%"=="0" echo 安装失败，错误代码：%result%
pause
exit /b %result%

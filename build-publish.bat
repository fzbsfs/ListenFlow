@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo [ERROR] 未找到 dotnet。请先安装 .NET 8 SDK：
  echo https://dotnet.microsoft.com/download/dotnet/8.0
  pause
  exit /b 1
)

echo 正在还原 NuGet 依赖并发布 Windows x64 单文件程序……
echo 首次构建需要网络下载 System.Speech 依赖；发布后的程序朗读时不需要网络。
dotnet publish ListenFlow.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -o "%~dp0publish"

if errorlevel 1 (
  echo.
  echo [ERROR] 发布失败。请把上方完整错误信息发给我。
  pause
  exit /b 1
)

echo.
echo 发布完成。请检查 publish 文件夹中的 ListenFlow.exe
pause

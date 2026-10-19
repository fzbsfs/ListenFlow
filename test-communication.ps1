[CmdletBinding()]
param(
    [switch]$RunProcessProbe
)

$ErrorActionPreference = "Stop"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"

$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$dotnetRoot = Split-Path $dotnet -Parent
$tests = Join-Path $PSScriptRoot "ListenFlow.Tests\ListenFlow.Tests.csproj"
$probeProject = Join-Path $PSScriptRoot "ListenFlow.CommunicationProbe\ListenFlow.CommunicationProbe.csproj"

Write-Host "[1/4] 还原通信测试依赖"
& $dotnet restore $tests -p:NuGetAudit=false -p:BuildInParallel=false -p:RestoreDisableParallel=true --disable-parallel /m:1
if ($LASTEXITCODE -ne 0) { throw "通信测试依赖还原失败。" }

Write-Host "[2/4] Release 构建"
& $dotnet build $tests -c Release --no-restore -p:BuildInParallel=false /m:1
if ($LASTEXITCODE -ne 0) { throw "通信项目构建失败。" }

Write-Host "[3/4] 运行协议、宿主与命名管道测试"
$filter = "FullyQualifiedName~ProtocolTests|FullyQualifiedName~DesktopCommandRouterTests|FullyQualifiedName~DesktopPipeServerTests|FullyQualifiedName~NativeHostRunnerTests|FullyQualifiedName~DesktopConnectionFactoryTests"
& $dotnet test $tests -c Release --no-build --no-restore --filter $filter
if ($LASTEXITCODE -ne 0) { throw "通信自动化测试失败。" }

Write-Host "[4/4] 构建真实进程通信探针"
& $dotnet build $probeProject -c Release -p:BuildInParallel=false /m:1
if ($LASTEXITCODE -ne 0) { throw "通信探针构建失败。" }

if ($RunProcessProbe) {
    $hostExe = Join-Path $PSScriptRoot "ListenFlow.NativeHost\bin\Release\net10.0-windows\ListenFlow.NativeHost.exe"
    $desktopExe = Join-Path $PSScriptRoot "bin\Release\net10.0-windows\ListenFlow.exe"
    $probeDll = Join-Path $PSScriptRoot "ListenFlow.CommunicationProbe\bin\Release\net10.0-windows\ListenFlow.CommunicationProbe.dll"
    $dataDirectory = Join-Path $PSScriptRoot ("test-artifacts\communication-probe-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
    & $dotnet $probeDll $hostExe $desktopExe $dataDirectory $dotnetRoot
    if ($LASTEXITCODE -ne 0) { throw "真实进程通信探针失败。" }
}

Write-Host "通信基础设施验证完成。"

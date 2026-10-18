[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "publish"),
    [switch]$RunVoiceIntegrationTests
)

$ErrorActionPreference = "Stop"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"

$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$project = Join-Path $PSScriptRoot "ListenFlow.csproj"
$tests = Join-Path $PSScriptRoot "ListenFlow.Tests\ListenFlow.Tests.csproj"

Write-Host "[1/4] 还原测试依赖"
& $dotnet restore $tests --nologo --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw "测试依赖还原失败。" }

Write-Host "[2/4] 运行自动化测试"
$filter = if ($RunVoiceIntegrationTests) { "" } else { "类别!=本机语音集成" }
if ($filter) {
    & $dotnet test $tests -c Release --no-restore --nologo --verbosity minimal --filter $filter
} else {
    & $dotnet test $tests -c Release --no-restore --nologo --verbosity minimal
}
if ($LASTEXITCODE -ne 0) { throw "自动化测试失败，已停止发布。" }

Write-Host "[3/4] 还原 Windows x64 发布依赖"
& $dotnet restore $project -r win-x64 --nologo --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw "发布依赖还原失败。" }

Write-Host "[4/4] 构建自包含单文件程序"
& $dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    --no-restore `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishReadyToRun=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $OutputDirectory `
    --nologo `
    --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw "发布失败。" }

$exe = Join-Path $OutputDirectory "ListenFlow.exe"
if (-not (Test-Path -LiteralPath $exe)) { throw "发布命令完成，但未找到 ListenFlow.exe。" }

$info = Get-Item -LiteralPath $exe
$hash = Get-FileHash -Algorithm SHA256 -LiteralPath $exe
Write-Host "发布完成：$($info.FullName)"
Write-Host "文件大小：$($info.Length) 字节"
Write-Host "SHA256：$($hash.Hash)"

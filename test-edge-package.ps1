[CmdletBinding()]
param(
    [string]$PackageDirectory = (Join-Path $PSScriptRoot "artifacts\ListenFlow-0.2.0-Edge人工验收-x64"),
    [string]$DotNetPath
)

$ErrorActionPreference = "Stop"
$packageFull = (Resolve-Path -LiteralPath $PackageDirectory).Path
$dotnet = if ($DotNetPath) { (Resolve-Path -LiteralPath $DotNetPath).Path } else { (Get-Command dotnet -ErrorAction Stop).Source }
$dotnetRoot = Split-Path -Parent $dotnet
$testRoot = Join-Path $PSScriptRoot "test-artifacts\package-install"
$installRoot = Join-Path $testRoot "Programs\ListenFlow-EdgeTest"
$dataRoot = Join-Path $testRoot "State\ListenFlow"
$manifestDirectory = Join-Path $dataRoot "NativeMessaging"
$registryPath = "HKCU:\Software\ListenFlow\Tests\NativeMessagingHosts\com.listenflow.native_host"
$startMenu = Join-Path $testRoot "StartMenu"
$installScript = Join-Path $packageFull "Install-ListenFlow.ps1"
$uninstallScript = Join-Path $packageFull "Uninstall-ListenFlow.ps1"
$installEntry = Join-Path $packageFull "安装 ListenFlow.cmd"
$uninstallEntry = Join-Path $packageFull "卸载 ListenFlow.cmd"
$exportEntry = Join-Path $packageFull "导出诊断日志.cmd"
$previousManifest = Join-Path $testRoot "previous-native-host.json"

function Assert-CmdFormat([string]$EntryPath) {
    $bytes = [IO.File]::ReadAllBytes($EntryPath)
    if ($bytes | Where-Object { $_ -gt 127 }) {
        throw "CMD 入口包含非 ASCII 字节：$EntryPath"
    }
    for ($index = 0; $index -lt $bytes.Length; $index++) {
        if ($bytes[$index] -eq 10 -and ($index -eq 0 -or $bytes[$index - 1] -ne 13)) {
            throw "CMD 入口包含没有 CR 的 LF 换行：$EntryPath"
        }
        if ($bytes[$index] -eq 13 -and ($index + 1 -ge $bytes.Length -or $bytes[$index + 1] -ne 10)) {
            throw "CMD 入口包含没有 LF 的 CR 换行：$EntryPath"
        }
    }
}

function Invoke-CmdEntry([string]$EntryPath, [string[]]$Arguments) {
    $previousNoPause = $env:LISTENFLOW_NO_PAUSE
    try {
        $env:LISTENFLOW_NO_PAUSE = "1"
        $cmdArguments = @("/d", "/c", "call", $EntryPath) + $Arguments
        & $env:ComSpec @cmdArguments | ForEach-Object { Write-Host $_ }
        $exitCode = $LASTEXITCODE
        return [int]$exitCode
    } finally {
        $env:LISTENFLOW_NO_PAUSE = $previousNoPause
    }
}

if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
[IO.File]::WriteAllText($previousManifest, '{"name":"previous.test.host"}', (New-Object Text.UTF8Encoding($false)))
New-Item -Path $registryPath -Force | Out-Null
Set-Item -LiteralPath $registryPath -Value $previousManifest

try {
    Assert-CmdFormat $installEntry
    Assert-CmdFormat $uninstallEntry
    Assert-CmdFormat $exportEntry

    $installArguments = @(
        "-InstallRoot", $installRoot,
        "-ManifestDirectory", $manifestDirectory,
        "-RegistryPath", $registryPath,
        "-StartMenuDirectory", $startMenu,
        "-SkipEdgeLaunch", "-SkipAppLaunch", "-SkipShortcut")

    Write-Host "[1/6] 使用 Windows CMD 入口执行隔离安装"
    $installExitCode = Invoke-CmdEntry $installEntry $installArguments
    if ($installExitCode -ne 0) { throw "CMD 入口隔离安装失败：$installExitCode" }

    $app = Join-Path $installRoot "app\ListenFlow.exe"
    $hostExecutable = Join-Path $installRoot "app\ListenFlow.NativeHost.exe"
    $extensionManifest = Join-Path $installRoot "extension\manifest.json"
    foreach ($required in @($app, $hostExecutable, $extensionManifest)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "安装后文件缺失：$required" }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $installRoot "app\edge-test-isolated-data.marker"))) {
        throw "验收版缺少独立数据目录标记。"
    }
    $installedUninstall = Get-Content -LiteralPath (Join-Path $installRoot "tools\Uninstall-ListenFlow.ps1") -Raw -Encoding UTF8
    if (-not $installedUninstall.Contains('ListenFlow-EdgeTest\Data')) {
        throw "卸载脚本没有把可选数据删除目标限制在验收版独立目录。"
    }

    Write-Host "[2/6] 验证扩展、宿主清单与注册路径"
    $nativeManifestPath = (Get-Item -LiteralPath $registryPath).GetValue("")
    $nativeManifest = Get-Content -LiteralPath $nativeManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([IO.Path]::GetFullPath([string]$nativeManifest.path) -ne [IO.Path]::GetFullPath($hostExecutable)) {
        throw "宿主清单没有指向已安装的自包含宿主。"
    }
    if ($nativeManifest.allowed_origins[0] -ne "chrome-extension://chdkljcgemgeljdnfdnblbelfhmkkfbp/") {
        throw "宿主清单扩展身份不匹配。"
    }

    Write-Host "[3/6] 重复执行 CMD 安装并验证幂等更新"
    $repeatExitCode = Invoke-CmdEntry $installEntry $installArguments
    if ($repeatExitCode -ne 0) { throw "重复执行 CMD 安装失败：$repeatExitCode" }
    $registeredAfterRepeat = (Get-Item -LiteralPath $registryPath).GetValue("")
    if (-not [string]::Equals($registeredAfterRepeat, $nativeManifestPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "重复安装后宿主注册发生偏移。"
    }

    Write-Host "[4/6] 使用已安装自包含组件运行真实通信探针"
    $probe = Join-Path $PSScriptRoot "ListenFlow.CommunicationProbe\bin\Release\net10.0-windows\ListenFlow.CommunicationProbe.dll"
    & $dotnet $probe $hostExecutable $app (Join-Path $dataRoot "Probe") $dotnetRoot
    if ($LASTEXITCODE -ne 0) { throw "已安装组件通信探针失败。" }

    Write-Host "[5/6] 使用 Windows PowerShell 5.1 验证诊断日志导出"
    $diagnosticData = Join-Path $testRoot "DiagnosticData"
    $diagnosticLogs = Join-Path $diagnosticData "logs"
    $diagnosticExports = Join-Path $testRoot "DiagnosticExports"
    New-Item -ItemType Directory -Path $diagnosticLogs, $diagnosticExports -Force | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $diagnosticLogs "listenflow-diagnostic-test.jsonl"),
        '{"component":"desktop","stage":"test","textLength":100}',
        (New-Object Text.UTF8Encoding($false)))
    $exportExitCode = Invoke-CmdEntry $exportEntry @(
        "-DataRoot", $diagnosticData,
        "-OutputDirectory", $diagnosticExports)
    if ($exportExitCode -ne 0 -or @(Get-ChildItem -LiteralPath $diagnosticExports -Filter "*.zip").Count -ne 1) {
        throw "Windows CMD 与 PowerShell 5.1 诊断导出验证失败。"
    }

    Write-Host "[6/6] 使用 Windows CMD 入口执行隔离卸载"
    $uninstallExitCode = Invoke-CmdEntry $uninstallEntry @(
        "-InstallRoot", $installRoot,
        "-ManifestDirectory", $manifestDirectory,
        "-RegistryPath", $registryPath,
        "-StartMenuDirectory", $startMenu,
        "-DataRoot", $dataRoot,
        "-RemoveUserData", "-SkipEdgeLaunch", "-SkipShortcut")
    if ($uninstallExitCode -ne 0) { throw "CMD 入口隔离卸载失败：$uninstallExitCode" }
    if (Test-Path -LiteralPath $installRoot) { throw "卸载后安装目录仍然存在。" }
    if (-not (Test-Path -LiteralPath $registryPath)) { throw "卸载后没有恢复原有测试注册。" }
    $restoredManifest = (Get-Item -LiteralPath $registryPath).GetValue("")
    if (-not [string]::Equals($restoredManifest, $previousManifest, [StringComparison]::OrdinalIgnoreCase)) {
        throw "卸载后恢复的宿主注册不正确。"
    }
    Write-Host "安装、真实进程通信和卸载验证全部通过。"
} finally {
    if (Test-Path -LiteralPath $registryPath) { Remove-Item -LiteralPath $registryPath -Recurse -Force }
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}

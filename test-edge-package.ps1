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
$windowsPowerShell = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
$installScript = Join-Path $packageFull "Install-ListenFlow.ps1"
$uninstallScript = Join-Path $packageFull "Uninstall-ListenFlow.ps1"
$previousManifest = Join-Path $testRoot "previous-native-host.json"

if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
[IO.File]::WriteAllText($previousManifest, '{"name":"previous.test.host"}', (New-Object Text.UTF8Encoding($false)))
New-Item -Path $registryPath -Force | Out-Null
Set-Item -LiteralPath $registryPath -Value $previousManifest

try {
    Write-Host "[1/4] 使用 Windows PowerShell 执行隔离安装"
    & $windowsPowerShell -NoLogo -NoProfile -ExecutionPolicy Bypass -File $installScript `
        -InstallRoot $installRoot `
        -ManifestDirectory $manifestDirectory `
        -RegistryPath $registryPath `
        -StartMenuDirectory $startMenu `
        -SkipEdgeLaunch -SkipAppLaunch -SkipShortcut
    if ($LASTEXITCODE -ne 0) { throw "隔离安装失败。" }

    $app = Join-Path $installRoot "app\ListenFlow.exe"
    $hostExecutable = Join-Path $installRoot "app\ListenFlow.NativeHost.exe"
    $extensionManifest = Join-Path $installRoot "extension\manifest.json"
    foreach ($required in @($app, $hostExecutable, $extensionManifest)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "安装后文件缺失：$required" }
    }

    Write-Host "[2/4] 验证扩展、宿主清单与注册路径"
    $nativeManifestPath = (Get-Item -LiteralPath $registryPath).GetValue("")
    $nativeManifest = Get-Content -LiteralPath $nativeManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([IO.Path]::GetFullPath([string]$nativeManifest.path) -ne [IO.Path]::GetFullPath($hostExecutable)) {
        throw "宿主清单没有指向已安装的自包含宿主。"
    }
    if ($nativeManifest.allowed_origins[0] -ne "chrome-extension://chdkljcgemgeljdnfdnblbelfhmkkfbp/") {
        throw "宿主清单扩展身份不匹配。"
    }

    Write-Host "[3/4] 使用已安装自包含组件运行真实通信探针"
    $probe = Join-Path $PSScriptRoot "ListenFlow.CommunicationProbe\bin\Release\net10.0-windows\ListenFlow.CommunicationProbe.dll"
    & $dotnet $probe $hostExecutable $app (Join-Path $dataRoot "Probe") $dotnetRoot
    if ($LASTEXITCODE -ne 0) { throw "已安装组件通信探针失败。" }

    Write-Host "[4/4] 使用 Windows PowerShell 执行隔离卸载"
    & $windowsPowerShell -NoLogo -NoProfile -ExecutionPolicy Bypass -File $uninstallScript `
        -InstallRoot $installRoot `
        -ManifestDirectory $manifestDirectory `
        -RegistryPath $registryPath `
        -StartMenuDirectory $startMenu `
        -DataRoot $dataRoot `
        -RemoveUserData -SkipEdgeLaunch -SkipShortcut
    if ($LASTEXITCODE -ne 0) { throw "隔离卸载失败。" }
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

[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA "Programs\ListenFlow-EdgeTest"),
    [string]$ManifestDirectory = (Join-Path $env:LOCALAPPDATA "ListenFlow-EdgeTest\NativeMessaging"),
    [string]$RegistryPath = "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.listenflow.native_host",
    [string]$StartMenuDirectory = (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"),
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA "ListenFlow-EdgeTest\Data"),
    [switch]$RemoveUserData,
    [switch]$SkipEdgeLaunch,
    [switch]$SkipShortcut
)

$ErrorActionPreference = "Stop"
$nativeHostName = "com.listenflow.native_host"
$installFull = [IO.Path]::GetFullPath($InstallRoot)
$manifestFull = [IO.Path]::GetFullPath((Join-Path $ManifestDirectory "$nativeHostName.json"))
$installedApp = Join-Path $installFull "app\ListenFlow.exe"
$installedHost = Join-Path $installFull "app\ListenFlow.NativeHost.exe"
$previousNativeHostManifest = $null
$installationInfoPath = Join-Path $installFull "installation.json"
if (Test-Path -LiteralPath $installationInfoPath -PathType Leaf) {
    try {
        $installationInfo = Get-Content -LiteralPath $installationInfoPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $previousNativeHostManifest = [string]$installationInfo.previousNativeHostManifest
    } catch {
    }
}

function Stop-InstalledProcesses([string[]]$ExecutablePaths) {
    $targets = @{}
    foreach ($path in $ExecutablePaths) { $targets[[IO.Path]::GetFullPath($path)] = $true }
    foreach ($process in Get-Process -ErrorAction SilentlyContinue) {
        try {
            $path = $process.MainModule.FileName
            if ($path -and $targets.ContainsKey([IO.Path]::GetFullPath($path))) {
                Stop-Process -Id $process.Id -Force -ErrorAction Stop
            }
        } catch {
        }
    }
}

Stop-InstalledProcesses @($installedApp, $installedHost)

if (Test-Path -LiteralPath $RegistryPath) {
    $registeredManifest = (Get-Item -LiteralPath $RegistryPath).GetValue("")
    if ([string]::Equals($registeredManifest, $manifestFull, [StringComparison]::OrdinalIgnoreCase)) {
        if ($previousNativeHostManifest -and (Test-Path -LiteralPath $previousNativeHostManifest -PathType Leaf)) {
            Set-Item -LiteralPath $RegistryPath -Value $previousNativeHostManifest
            Write-Host "已恢复先前的 Native Messaging Host 注册：$previousNativeHostManifest"
        } else {
            Remove-Item -LiteralPath $RegistryPath -Recurse -Force
        }
    } else {
        Write-Warning "注册表指向其他宿主清单，未删除：$registeredManifest"
    }
}

if (Test-Path -LiteralPath $manifestFull) {
    $canRemoveManifest = $false
    try {
        $manifest = Get-Content -LiteralPath $manifestFull -Raw -Encoding UTF8 | ConvertFrom-Json
        $canRemoveManifest = [string]::Equals(
            [IO.Path]::GetFullPath([string]$manifest.path),
            [IO.Path]::GetFullPath($installedHost),
            [StringComparison]::OrdinalIgnoreCase)
    } catch {
    }
    if ($canRemoveManifest) {
        Remove-Item -LiteralPath $manifestFull -Force
    } else {
        Write-Warning "宿主清单内容不属于当前安装，未删除：$manifestFull"
    }
}

if (-not $SkipShortcut) {
    foreach ($shortcut in @(
        "ListenFlow Edge 人工验收版.lnk",
        "卸载 ListenFlow Edge 人工验收版.lnk",
        "ListenFlow 0.2.1.lnk",
        "卸载 ListenFlow 0.2.1.lnk",
        "导出 ListenFlow 诊断日志.lnk")) {
        $path = Join-Path $StartMenuDirectory $shortcut
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    }
}

if (Test-Path -LiteralPath $installFull) {
    if ((Split-Path -Leaf $installFull) -notin @("ListenFlow-EdgeTest", "ListenFlow")) {
        throw "拒绝删除名称不是 ListenFlow-EdgeTest 的安装目录：$installFull"
    }
    Remove-Item -LiteralPath $installFull -Recurse -Force
}

if ($RemoveUserData) {
    $dataFull = [IO.Path]::GetFullPath($DataRoot)
    if ((Split-Path -Leaf $dataFull) -ne "ListenFlow") {
        throw "拒绝删除名称不是 ListenFlow 的数据目录：$dataFull"
    }
    if (Test-Path -LiteralPath $dataFull) {
        Remove-Item -LiteralPath $dataFull -Recurse -Force
    }
    Write-Host "本地阅读数据已删除：$dataFull"
} else {
    Write-Host "本地阅读数据已保留：$DataRoot"
}

Write-Host "ListenFlow 程序和 Native Messaging Host 注册已移除。"
if (-not $RemoveUserData) {
    Write-Host "验收数据与诊断日志仍保留在：$DataRoot"
}
Write-Host "请在 Edge 的 edge://extensions/ 页面移除“ListenFlow 网页听读”扩展。"

if (-not $SkipEdgeLaunch) {
    $edgeCandidates = @(@(
        (Join-Path ${env:ProgramFiles(x86)} "Microsoft\Edge\Application\msedge.exe"),
        (Join-Path $env:ProgramFiles "Microsoft\Edge\Application\msedge.exe")
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) })
    if ($edgeCandidates.Count -gt 0) {
        try {
            Start-Process -FilePath $edgeCandidates[0] -ArgumentList "edge://extensions/"
        } catch {
            Write-Warning "无法自动打开 Edge 扩展页，请手动打开 edge://extensions/。$($_.Exception.Message)"
        }
    }
}

[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA "Programs\ListenFlow-EdgeTest"),
    [string]$ManifestDirectory = (Join-Path $env:LOCALAPPDATA "ListenFlow-EdgeTest\NativeMessaging"),
    [string]$RegistryPath = "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.listenflow.native_host",
    [string]$StartMenuDirectory = (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"),
    [switch]$SkipEdgeLaunch,
    [switch]$SkipAppLaunch,
    [switch]$SkipShortcut
)

$ErrorActionPreference = "Stop"
$expectedExtensionId = "chdkljcgemgeljdnfdnblbelfhmkkfbp"
$nativeHostName = "com.listenflow.native_host"
$packageRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$payloadRoot = Join-Path $packageRoot "payload"
$checksumsPath = Join-Path $packageRoot "checksums.json"

function Get-ExtensionId([string]$PublicKey) {
    $keyBytes = [Convert]::FromBase64String($PublicKey)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = $sha.ComputeHash($keyBytes) } finally { $sha.Dispose() }
    $alphabet = "abcdefghijklmnop"
    $builder = New-Object Text.StringBuilder
    foreach ($value in $hash[0..15]) {
        [void]$builder.Append($alphabet[$value -shr 4])
        [void]$builder.Append($alphabet[$value -band 15])
    }
    return $builder.ToString()
}

function Get-Sha256([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash($stream)
        return ([BitConverter]::ToString($hash)).Replace("-", "")
    } finally {
        $sha.Dispose()
        $stream.Dispose()
    }
}

function Assert-PackageChecksums {
    if (-not (Test-Path -LiteralPath $checksumsPath)) {
        throw "安装包缺少 checksums.json。"
    }
    $rootPrefix = $packageRoot.TrimEnd('\') + '\'
    $entries = Get-Content -LiteralPath $checksumsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($entry in $entries) {
        $candidate = [IO.Path]::GetFullPath((Join-Path $packageRoot $entry.path))
        if (-not $candidate.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "校验文件越出安装包目录：$($entry.path)"
        }
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            throw "安装包文件缺失：$($entry.path)"
        }
        $actual = Get-Sha256 $candidate
        if (-not $actual.Equals([string]$entry.sha256, [StringComparison]::OrdinalIgnoreCase)) {
            throw "安装包文件校验失败：$($entry.path)"
        }
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

function Stop-RunningDesktopApp([string]$TargetExecutablePath) {
    $targetFull = [IO.Path]::GetFullPath($TargetExecutablePath)
    foreach ($runningApp in @(Get-Process -Name "ListenFlow" -ErrorAction SilentlyContinue)) {
        $runningPath = $null
        try { $runningPath = [IO.Path]::GetFullPath($runningApp.MainModule.FileName) } catch {
        }
        if (-not $runningPath -or
            -not [string]::Equals($runningPath, $targetFull, [StringComparison]::OrdinalIgnoreCase)) {
            throw "请先正常关闭其他位置正在运行的 ListenFlow，再安装人工验收版。进程号：$($runningApp.Id)"
        }

        for ($attempt = 0; $attempt -lt 20 -and -not $runningApp.HasExited; $attempt++) {
            $runningApp.Refresh()
            if ($runningApp.MainWindowHandle -ne [IntPtr]::Zero) {
                [void]$runningApp.CloseMainWindow()
                break
            }
            [Threading.Thread]::Sleep(250)
        }
        if (-not $runningApp.HasExited -and -not $runningApp.WaitForExit(5000)) {
            Stop-Process -Id $runningApp.Id -Force -ErrorAction Stop
            $runningApp.WaitForExit(5000)
        }
    }
}

if (-not (Test-Path -LiteralPath $payloadRoot -PathType Container)) {
    throw "安装包缺少 payload 目录。"
}
Assert-PackageChecksums

$sourceManifestPath = Join-Path $payloadRoot "extension\manifest.json"
$sourceAppPath = Join-Path $payloadRoot "app\ListenFlow.exe"
$sourceHostPath = Join-Path $payloadRoot "app\ListenFlow.NativeHost.exe"
foreach ($required in @($sourceManifestPath, $sourceAppPath, $sourceHostPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "安装包文件缺失：$required" }
}

$extensionManifest = Get-Content -LiteralPath $sourceManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$actualExtensionId = Get-ExtensionId ([string]$extensionManifest.key)
if ($actualExtensionId -ne $expectedExtensionId) {
    throw "扩展固定标识不匹配。期望 $expectedExtensionId，实际 $actualExtensionId。"
}
if ($extensionManifest.manifest_version -ne 3 -or
    -not ($extensionManifest.permissions -contains "nativeMessaging")) {
    throw "扩展清单缺少 Manifest V3 或 nativeMessaging 权限。"
}

$installFull = [IO.Path]::GetFullPath($InstallRoot)
$installParent = Split-Path -Parent $installFull
$manifestFull = [IO.Path]::GetFullPath((Join-Path $ManifestDirectory "$nativeHostName.json"))
$stageRoot = Join-Path $installParent (".ListenFlow.installing-" + [Guid]::NewGuid().ToString("N"))
$backupRoot = Join-Path $installParent (".ListenFlow.previous-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $installParent -Force | Out-Null

$previousNativeHostManifest = $null
if (Test-Path -LiteralPath $RegistryPath) {
    $previousNativeHostManifest = (Get-Item -LiteralPath $RegistryPath).GetValue("")
    if ([string]::Equals($previousNativeHostManifest, $manifestFull, [StringComparison]::OrdinalIgnoreCase)) {
        $oldInstallationInfoPath = Join-Path $installFull "installation.json"
        if (Test-Path -LiteralPath $oldInstallationInfoPath -PathType Leaf) {
            try {
                $oldInstallationInfo = Get-Content -LiteralPath $oldInstallationInfoPath -Raw -Encoding UTF8 | ConvertFrom-Json
                $previousNativeHostManifest = [string]$oldInstallationInfo.previousNativeHostManifest
            } catch {
                $previousNativeHostManifest = $null
            }
        } else {
            $previousNativeHostManifest = $null
        }
    }
}

$installedApp = Join-Path $installFull "app\ListenFlow.exe"
$installedHost = Join-Path $installFull "app\ListenFlow.NativeHost.exe"
Stop-RunningDesktopApp $installedApp
Stop-InstalledProcesses @($installedApp, $installedHost)

try {
    New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null
    Copy-Item -Path (Join-Path $payloadRoot "*") -Destination $stageRoot -Recurse -Force
    if (Test-Path -LiteralPath $installFull) {
        Move-Item -LiteralPath $installFull -Destination $backupRoot
    }
    Move-Item -LiteralPath $stageRoot -Destination $installFull
    if (Test-Path -LiteralPath $backupRoot) {
        Remove-Item -LiteralPath $backupRoot -Recurse -Force
    }
} catch {
    if ((-not (Test-Path -LiteralPath $installFull)) -and (Test-Path -LiteralPath $backupRoot)) {
        Move-Item -LiteralPath $backupRoot -Destination $installFull
    }
    if (Test-Path -LiteralPath $stageRoot) {
        Remove-Item -LiteralPath $stageRoot -Recurse -Force
    }
    throw
}

$installedExtension = Join-Path $installFull "extension"
$installedApp = Join-Path $installFull "app\ListenFlow.exe"
$installedHost = Join-Path $installFull "app\ListenFlow.NativeHost.exe"
New-Item -ItemType Directory -Path (Split-Path -Parent $manifestFull) -Force | Out-Null
$nativeManifest = [ordered]@{
    name = $nativeHostName
    description = "ListenFlow 本地离线听读通信宿主"
    path = $installedHost
    type = "stdio"
    allowed_origins = @("chrome-extension://$expectedExtensionId/")
}
$utf8NoBom = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText($manifestFull, ($nativeManifest | ConvertTo-Json -Depth 4), $utf8NoBom)

New-Item -Path $RegistryPath -Force | Out-Null
Set-Item -LiteralPath $RegistryPath -Value $manifestFull
$registeredManifest = (Get-Item -LiteralPath $RegistryPath).GetValue("")
if (-not [string]::Equals($registeredManifest, $manifestFull, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Native Messaging Host 注册验证失败。"
}

$installationInfo = [ordered]@{
    version = "0.2.0-edge-test"
    extensionId = $expectedExtensionId
    nativeHostName = $nativeHostName
    installRoot = $installFull
    extensionPath = $installedExtension
    nativeHostManifest = $manifestFull
    previousNativeHostManifest = $previousNativeHostManifest
    installedAt = [DateTimeOffset]::Now.ToString("o")
}
[IO.File]::WriteAllText(
    (Join-Path $installFull "installation.json"),
    ($installationInfo | ConvertTo-Json -Depth 4),
    $utf8NoBom)

if (-not $SkipShortcut) {
    New-Item -ItemType Directory -Path $StartMenuDirectory -Force | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $appShortcut = $shell.CreateShortcut((Join-Path $StartMenuDirectory "ListenFlow Edge 人工验收版.lnk"))
    $appShortcut.TargetPath = $installedApp
    $appShortcut.WorkingDirectory = Split-Path -Parent $installedApp
    $appShortcut.Description = "ListenFlow 本地听读助手"
    $appShortcut.Save()

    $uninstallShortcut = $shell.CreateShortcut((Join-Path $StartMenuDirectory "卸载 ListenFlow Edge 人工验收版.lnk"))
    $uninstallShortcut.TargetPath = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
    $uninstallScript = Join-Path $installFull "tools\Uninstall-ListenFlow.ps1"
    $uninstallShortcut.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$uninstallScript`""
    $uninstallShortcut.WorkingDirectory = $installFull
    $uninstallShortcut.Description = "卸载 ListenFlow，默认保留本地阅读数据"
    $uninstallShortcut.Save()
}

if (-not $SkipAppLaunch) {
    Start-Process -FilePath $installedApp -WorkingDirectory (Split-Path -Parent $installedApp)
}

Write-Host ""
Write-Host "ListenFlow 人工验收版安装完成。"
Write-Host "主程序：$installedApp"
Write-Host "扩展目录：$installedExtension"
Write-Host "扩展固定标识：$expectedExtensionId"
Write-Host "宿主清单：$manifestFull"
Write-Host ""
Write-Host "Edge 仍需手动加载一次解压缩扩展："
Write-Host "1. 打开 edge://extensions/ 并开启开发人员模式。"
Write-Host "2. 点击“加载解压缩的扩展”，选择：$installedExtension"
Write-Host "3. 确认扩展标识为：$expectedExtensionId"

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
    } else {
        Write-Warning "未找到 Microsoft Edge，请手动打开 edge://extensions/。"
    }
    try {
        Start-Process -FilePath "explorer.exe" -ArgumentList @($installedExtension)
    } catch {
        Write-Warning "无法自动打开扩展目录，请手动选择：$installedExtension"
    }
}

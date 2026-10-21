[CmdletBinding()]
param(
    [string]$PackagePath,
    [switch]$PrepareOnly
)

$ErrorActionPreference = 'Stop'
$version = '0.2.2'
$expectedHash = 'ED46DC3E843206D531FC4E24A19C880C24AA421CBA3C6D8059CD82E5CFF3CB84'
$downloadUri = "https://github.com/fzbsfs/ListenFlow/releases/download/v$version/ListenFlow-$version-Windows-x64.zip"
$cacheRoot = Join-Path $env:TEMP "ListenFlow-setup-$version"
New-Item -ItemType Directory -Path $cacheRoot -Force | Out-Null

if (-not $PackagePath) {
    $PackagePath = Join-Path $cacheRoot "ListenFlow-$version-Windows-x64.zip"
    if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
        Write-Host "正在从项目发布页下载 ListenFlow $version。下载阶段需要联网，朗读使用本地语音。"
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $temporaryPath = $PackagePath + '.partial'
        Invoke-WebRequest -Uri $downloadUri -OutFile $temporaryPath -UseBasicParsing
        if ((Get-FileHash -LiteralPath $temporaryPath -Algorithm SHA256).Hash -ne $expectedHash) {
            throw '下载文件校验失败，已停止安装。请删除临时下载文件后重试。'
        }
        Move-Item -LiteralPath $temporaryPath -Destination $PackagePath -Force
    }
}

$PackagePath = (Resolve-Path -LiteralPath $PackagePath).Path
if ((Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash -ne $expectedHash) {
    throw '安装包 SHA256 不匹配，已停止安装。'
}
$extractRoot = Join-Path $cacheRoot ([Guid]::NewGuid().ToString('N'))
Expand-Archive -LiteralPath $PackagePath -DestinationPath $extractRoot
$installer = Join-Path $extractRoot 'latest\Install-ListenFlow.ps1'
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
    throw '安装包缺少统一安装脚本。'
}
Write-Host "已验证并解压安装包：$extractRoot"
Write-Host '此脚本下载并安装程序、通信宿主和扩展文件，不下载微软语音模型。'
if ($PrepareOnly) { return }
& $installer
Write-Host '首次使用请在 Edge 扩展管理页面加载安装目录中的 extension；升级后重新加载扩展并刷新网页。'

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$HostExecutable
)

$ErrorActionPreference = "Stop"
$hostPath = (Resolve-Path -LiteralPath $HostExecutable).Path
if ([IO.Path]::GetExtension($hostPath) -ne ".exe") {
    throw "Native Messaging Host 必须是 EXE：$hostPath"
}

$directory = Join-Path $env:LOCALAPPDATA "ListenFlow\edge-native-host"
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$manifestPath = Join-Path $directory "com.listenflow.native_host.json"
$manifest = [ordered]@{
    name = "com.listenflow.native_host"
    description = "ListenFlow 本地离线听读通信宿主"
    path = $hostPath
    type = "stdio"
    allowed_origins = @("chrome-extension://chdkljcgemgeljdnfdnblbelfhmkkfbp/")
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM

$registryPath = "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.listenflow.native_host"
New-Item -Path $registryPath -Force | Out-Null
Set-Item -Path $registryPath -Value $manifestPath

Write-Host "已注册 Edge Native Messaging Host：$manifestPath"
Write-Host "扩展标识：chdkljcgemgeljdnfdnblbelfhmkkfbp"

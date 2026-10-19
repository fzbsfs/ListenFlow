$ErrorActionPreference = "Stop"
$registryPath = "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.listenflow.native_host"
if (Test-Path $registryPath) {
    Remove-Item -Path $registryPath -Recurse -Force
}
$manifestDirectory = Join-Path $env:LOCALAPPDATA "ListenFlow\edge-native-host"
$manifestPath = Join-Path $manifestDirectory "com.listenflow.native_host.json"
if (Test-Path -LiteralPath $manifestPath) {
    Remove-Item -LiteralPath $manifestPath -Force
}
if ((Test-Path -LiteralPath $manifestDirectory) -and
    -not (Get-ChildItem -LiteralPath $manifestDirectory -Force | Select-Object -First 1)) {
    Remove-Item -LiteralPath $manifestDirectory -Force
}
Write-Host "已移除 Edge Native Messaging Host 注册。"

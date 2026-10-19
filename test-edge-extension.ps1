[CmdletBinding()]
param(
    [string]$CdpEndpoint,
    [string]$PageUrl = "http://127.0.0.1:8765/",
    [string]$ScreenshotDirectory
)

$ErrorActionPreference = "Stop"
$extension = Join-Path $PSScriptRoot "EdgeExtension"
$stepCount = if ($CdpEndpoint) { 3 } else { 2 }

Write-Host "[1/$stepCount] 运行 UTF-16、句子范围和 DOM 映射核心测试"
node --test (Join-Path $extension "tests\snapshot-core.test.cjs")
if ($LASTEXITCODE -ne 0) { throw "Edge 核心测试失败。" }

Write-Host "[2/$stepCount] 验证 Manifest V3、固定扩展标识和本地依赖"
node (Join-Path $extension "tests\validate-extension.mjs")
if ($LASTEXITCODE -ne 0) { throw "Edge 扩展结构验证失败。" }

Write-Host "Edge 扩展静态验证完成。浏览器闭环仍需实际加载扩展测试。"

if ($CdpEndpoint) {
    Write-Host "[3/3] 运行真实 Edge、Native Messaging、ListenFlow 与 SAPI 闭环"
    $arguments = @(
        (Join-Path $extension "tests\browser-closed-loop.mjs"),
        $CdpEndpoint,
        $PageUrl
    )
    if ($ScreenshotDirectory) {
        $arguments += $ScreenshotDirectory
    }
    & node @arguments
    if ($LASTEXITCODE -ne 0) { throw "Edge 浏览器闭环测试失败。" }
}

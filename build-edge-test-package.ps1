[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "artifacts\latest"),
    [string]$DotNetPath,
    [switch]$RunVoiceIntegrationTests,
    [switch]$InstallAfterBuild
)

$ErrorActionPreference = "Stop"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot ".dotnet-home"

$dotnet = if ($DotNetPath) { (Resolve-Path -LiteralPath $DotNetPath).Path } else { (Get-Command dotnet -ErrorAction Stop).Source }
$productVersion = "0.2.2"
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "artifacts"))
$outputFull = [IO.Path]::GetFullPath($OutputDirectory)
$artifactsPrefix = $artifactsRoot.TrimEnd('\') + '\'
if (-not $outputFull.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "输出目录必须位于仓库 artifacts 目录内：$outputFull"
}

$stage = Join-Path $artifactsRoot (".edge-package-" + [Guid]::NewGuid().ToString("N"))
$mainPublish = Join-Path $stage "main"
$hostPublish = Join-Path $stage "host"
$packageStage = Join-Path $stage "package"
$payloadApp = Join-Path $packageStage "payload\app"
$payloadExtension = Join-Path $packageStage "payload\extension"
$payloadTools = Join-Path $packageStage "payload\tools"
$tests = Join-Path $PSScriptRoot "ListenFlow.Tests\ListenFlow.Tests.csproj"
$mainProject = Join-Path $PSScriptRoot "ListenFlow.csproj"
$hostProject = Join-Path $PSScriptRoot "ListenFlow.NativeHost\ListenFlow.NativeHost.csproj"

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
New-Item -ItemType Directory -Path $payloadApp, $payloadExtension, $payloadTools -Force | Out-Null

try {
    Write-Host "[1/7] 还原 Windows x64 构建依赖"
    & $dotnet restore $tests --ignore-failed-sources -p:NuGetAudit=false -p:RestoreDisableParallel=true --disable-parallel /m:1
    if ($LASTEXITCODE -ne 0) { throw "测试依赖还原失败。" }

    & $dotnet restore $mainProject -r win-x64 --ignore-failed-sources -p:NuGetAudit=false -p:RestoreDisableParallel=true --disable-parallel /m:1
    if ($LASTEXITCODE -ne 0) { throw "主程序运行时依赖还原失败。" }

    & $dotnet restore $hostProject -r win-x64 --ignore-failed-sources -p:NuGetAudit=false -p:RestoreDisableParallel=true --disable-parallel /m:1
    if ($LASTEXITCODE -ne 0) { throw "宿主依赖还原失败。" }

    Write-Host "[2/7] 运行自动化回归"
    $filter = if ($RunVoiceIntegrationTests) { "" } else { "类别!=本机语音集成" }
    if ($filter) {
        & $dotnet test $tests -c Release --no-restore --filter $filter
    } else {
        & $dotnet test $tests -c Release --no-restore
    }
    if ($LASTEXITCODE -ne 0) { throw "自动化回归失败。" }

    Write-Host "[3/7] 发布自包含 ListenFlow 主程序"
    & $dotnet publish $mainProject -c Release -r win-x64 --self-contained true --no-restore `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:PublishReadyToRun=false -p:DebugType=None -p:DebugSymbols=false `
        -o $mainPublish
    if ($LASTEXITCODE -ne 0) { throw "ListenFlow 主程序发布失败。" }

    Write-Host "[4/7] 发布自包含 Native Messaging Host"
    & $dotnet publish $hostProject -c Release -r win-x64 --self-contained true --no-restore `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:PublishReadyToRun=false -p:DebugType=None -p:DebugSymbols=false `
        -o $hostPublish
    if ($LASTEXITCODE -ne 0) { throw "Native Messaging Host 发布失败。" }

    Copy-Item -LiteralPath (Join-Path $mainPublish "ListenFlow.exe") -Destination $payloadApp
    Copy-Item -LiteralPath (Join-Path $hostPublish "ListenFlow.NativeHost.exe") -Destination $payloadApp

    Write-Host "[5/7] 复制离线 Edge 扩展与安装工具"
    $extensionManifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot "EdgeExtension\manifest.json") -Raw | ConvertFrom-Json
    if ([string]$extensionManifest.version -ne $productVersion) {
        throw "扩展版本与构建版本不一致：$($extensionManifest.version) != $productVersion"
    }
    $extensionFiles = @(
        "manifest.json", "background.js", "content-script.js", "content.css",
        "dom-snapshot.js", "highlighter.js", "snapshot-core.js"
    )
    foreach ($file in $extensionFiles) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "EdgeExtension\$file") -Destination $payloadExtension
    }
    New-Item -ItemType Directory -Path (Join-Path $payloadExtension "vendor") -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "EdgeExtension\vendor\Readability.js") -Destination (Join-Path $payloadExtension "vendor")
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "EdgeExtension\vendor\READABILITY-LICENSE.md") -Destination (Join-Path $payloadExtension "vendor")

    $packaging = Join-Path $PSScriptRoot "Packaging\EdgeTest"
    Copy-Item -LiteralPath (Join-Path $packaging "Install-ListenFlow.ps1") -Destination $packageStage
    Copy-Item -LiteralPath (Join-Path $packaging "Uninstall-ListenFlow.ps1") -Destination $packageStage
    Copy-Item -LiteralPath (Join-Path $packaging "Export-Diagnostics.ps1") -Destination $packageStage
    Copy-Item -LiteralPath (Join-Path $packaging "人工验收说明.txt") -Destination $packageStage
    Copy-Item -LiteralPath (Join-Path $packaging "Uninstall-ListenFlow.ps1") -Destination $payloadTools
    Copy-Item -LiteralPath (Join-Path $packaging "Export-Diagnostics.ps1") -Destination $payloadTools

    $cmdCopies = [ordered]@{
        "安装 ListenFlow.cmd" = Join-Path $packageStage "安装 ListenFlow.cmd"
        "卸载 ListenFlow.cmd" = Join-Path $packageStage "卸载 ListenFlow.cmd"
        "导出诊断日志.cmd" = Join-Path $packageStage "导出诊断日志.cmd"
        "已安装版本卸载.cmd" = Join-Path $packageStage "payload\卸载 ListenFlow.cmd"
    }
    $ascii = [Text.Encoding]::ASCII
    foreach ($cmdCopy in $cmdCopies.GetEnumerator()) {
        $sourceCmd = Join-Path $packaging $cmdCopy.Key
        $cmdText = [IO.File]::ReadAllText($sourceCmd)
        if ($cmdText.ToCharArray() | Where-Object { [int]$_ -gt 127 }) {
            throw "CMD 入口必须只包含 ASCII 字符：$sourceCmd"
        }
        $cmdLines = $cmdText -split "\r\n|\n|\r"
        $normalizedCmd = (($cmdLines | Where-Object { $_ -ne "" }) -join "`r`n") + "`r`n"
        [IO.File]::WriteAllText([string]$cmdCopy.Value, $normalizedCmd, $ascii)
    }

    Write-Host "[6/7] 生成构建信息与 SHA256 校验"
    $commit = (& git -C $PSScriptRoot rev-parse HEAD).Trim()
    $buildInfo = [ordered]@{
        product = "ListenFlow"
        version = $productVersion
        platform = "win-x64"
        selfContained = $true
        protocolVersion = 1
        extensionId = "chdkljcgemgeljdnfdnblbelfhmkkfbp"
        nativeHostName = "com.listenflow.native_host"
        commit = $commit
        builtAt = [DateTimeOffset]::Now.ToString("o")
    }
    $utf8NoBom = New-Object Text.UTF8Encoding($false)
    [IO.File]::WriteAllText(
        (Join-Path $packageStage "build-info.json"),
        ($buildInfo | ConvertTo-Json -Depth 4),
        $utf8NoBom)

    $checksumEntries = Get-ChildItem -LiteralPath (Join-Path $packageStage "payload") -Recurse -File | ForEach-Object {
        [ordered]@{
            path = $_.FullName.Substring($packageStage.Length + 1).Replace('\', '/')
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            length = $_.Length
        }
    }
    [IO.File]::WriteAllText(
        (Join-Path $packageStage "checksums.json"),
        ($checksumEntries | ConvertTo-Json -Depth 4),
        $utf8NoBom)

    Write-Host "[7/7] 生成可解压安装包"
    if (Test-Path -LiteralPath $outputFull) { Remove-Item -LiteralPath $outputFull -Recurse -Force }
    Move-Item -LiteralPath $packageStage -Destination $outputFull
    $zipPath = Join-Path $artifactsRoot "ListenFlow-$productVersion-Windows-x64.zip"
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Compress-Archive -LiteralPath $outputFull -DestinationPath $zipPath -CompressionLevel Optimal

    $zipInfo = Get-Item -LiteralPath $zipPath
    $zipHash = Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
    Get-ChildItem -LiteralPath $artifactsRoot -Force | Where-Object {
        $_.FullName -ne $outputFull -and $_.FullName -ne $zipPath -and
        ($_.Name -like "ListenFlow-*.zip" -or
         ($_.PSIsContainer -and ($_.Name -like "ListenFlow-*" -or $_.Name -like ".edge-package-*")))
    } | ForEach-Object {
        Remove-Item -LiteralPath $_.FullName -Recurse -Force
    }
    Write-Host "人工验收目录：$outputFull"
    Write-Host "人工验收压缩包：$zipPath"
    Write-Host "压缩包大小：$($zipInfo.Length) 字节"
    Write-Host "压缩包 SHA256：$($zipHash.Hash)"
    if ($InstallAfterBuild) {
        Write-Host "正在覆盖安装 ListenFlow $productVersion"
        & (Join-Path $outputFull "Install-ListenFlow.ps1") -SkipEdgeLaunch
        if ($LASTEXITCODE -ne 0) { throw "自动安装失败：$LASTEXITCODE" }
    }
} finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}

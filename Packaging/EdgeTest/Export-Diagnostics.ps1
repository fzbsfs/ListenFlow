[CmdletBinding()]
param(
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA "ListenFlow-EdgeTest\Data"),
    [string]$OutputDirectory = [Environment]::GetFolderPath("Desktop")
)

$ErrorActionPreference = "Stop"
$logRoot = Join-Path $DataRoot "logs"
if (-not (Test-Path -LiteralPath $logRoot -PathType Container)) {
    throw "No ListenFlow diagnostic log directory was found: $logRoot"
}
$logs = @(Get-ChildItem -LiteralPath $logRoot -File | Where-Object {
    $_.Name -like "*-diagnostic-*.jsonl" -or $_.Name -like "*.jsonl.1"
})
if ($logs.Count -eq 0) {
    throw "No ListenFlow diagnostic records were found: $logRoot"
}
$stage = Join-Path $env:TEMP ("ListenFlow-diagnostic-export-" + [Guid]::NewGuid().ToString("N"))
$output = Join-Path $OutputDirectory ("ListenFlow-diagnostics-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".zip")
try {
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    Copy-Item -LiteralPath $logs.FullName -Destination $stage
    Compress-Archive -LiteralPath (Join-Path $stage "*") -DestinationPath $output -CompressionLevel Optimal
    Write-Host "Diagnostic archive: $output"
} finally {
    if (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}

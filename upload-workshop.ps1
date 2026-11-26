param(
    [string]$UploaderExe = $env:STS2_MOD_UPLOADER,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$localConfig = Join-Path $PSScriptRoot "build.local.ps1"
if (Test-Path -LiteralPath $localConfig) {
    . $localConfig
}

if (-not $UploaderExe -or -not (Test-Path -LiteralPath $UploaderExe)) {
    throw "找不到官方上传工具 ModUploader.exe。请通过 -UploaderExe 或环境变量 STS2_MOD_UPLOADER 指定路径。"
}

& (Join-Path $PSScriptRoot "build.ps1") -Configuration $Configuration -Package

$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot "CardReminder.json") -Raw | ConvertFrom-Json
$pkgDir = Join-Path $PSScriptRoot "dist\CardReminder-$($manifest.version)"
$workspaceDir = Join-Path $PSScriptRoot "workshop"
$contentDir = Join-Path $workspaceDir "content"

New-Item -ItemType Directory -Path $contentDir -Force | Out-Null
Get-ChildItem -LiteralPath $contentDir -File -ErrorAction SilentlyContinue | Remove-Item -Force
foreach ($f in @("CardReminder.dll", "CardReminder.json", "CardReminder.pdb")) {
    $src = Join-Path $pkgDir $f
    if (Test-Path -LiteralPath $src) {
        Copy-Item -LiteralPath $src -Destination (Join-Path $contentDir $f) -Force
    }
}

Write-Host "Uploading workspace: $workspaceDir"
Push-Location (Split-Path $UploaderExe -Parent)
try {
    & $UploaderExe upload -w $workspaceDir
    if ($LASTEXITCODE -ne 0) {
        throw "ModUploader 上传失败，退出码 $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

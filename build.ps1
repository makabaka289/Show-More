param(
    [string]$Configuration = "Debug",
    [string]$Sts2GameDir = $env:STS2_GAME_DIR,
    [string]$RitsuLibRoot = $env:RITSULIB_ROOT,
    [string]$RitsuLibReferenceTarget = "0.113.0",
    [string]$Dotnet = "",
    [switch]$Install,
    [switch]$Package
)

$ErrorActionPreference = "Stop"

$localConfig = Join-Path $PSScriptRoot "build.local.ps1"
if (Test-Path -LiteralPath $localConfig) {
    . $localConfig
}

if (-not $Sts2GameDir) { throw "请通过 -Sts2GameDir 或环境变量 STS2_GAME_DIR 指定杀戮尖塔2目录。" }
if (-not $RitsuLibRoot -and (Test-Path (Join-Path $Sts2GameDir "mods\STS2-RitsuLib\RitsuLib.References.props"))) {
    $RitsuLibRoot = Join-Path $Sts2GameDir "mods\STS2-RitsuLib"
}
if (-not $RitsuLibRoot) { throw "请通过 -RitsuLibRoot 或环境变量 RITSULIB_ROOT 指定 RitsuLib 目录。" }

function Test-SdkDotnet([string]$exe) {
    if (-not $exe -or -not (Test-Path $exe)) { return $false }
    $sdks = & $exe --list-sdks 2>$null | Where-Object { $_ -match "^\d+\.\d+\.\d+" }
    return [bool]$sdks
}

$candidates = @()
if ($Dotnet) { $candidates += $Dotnet }
if ($env:DOTNET_EXE) { $candidates += $env:DOTNET_EXE }
$onPath = Get-Command dotnet -ErrorAction SilentlyContinue
if ($onPath) { $candidates += $onPath.Source }

$resolved = ""
foreach ($c in $candidates) {
    if (Test-SdkDotnet $c) { $resolved = $c; break }
}
if (-not $resolved) {
    throw "找不到带 SDK 的 dotnet。请安装 .NET 9 SDK 或使用 -Dotnet / 环境变量 DOTNET_EXE 指定路径。"
}
$Dotnet = $resolved

$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = "true"

$proj = Join-Path $PSScriptRoot "CardReminder.csproj"
Write-Host "dotnet       : $Dotnet"
Write-Host "project      : $proj"
Write-Host "game dir     : $Sts2GameDir"
Write-Host "RitsuLib     : $RitsuLibRoot (compat $RitsuLibReferenceTarget)"
Write-Host "configuration: $Configuration"

& $Dotnet build $proj `
    -c $Configuration `
    "-p:Sts2GameDir=$Sts2GameDir" `
    "-p:RitsuLibRoot=$RitsuLibRoot" `
    "-p:RitsuLibReferenceTarget=$RitsuLibReferenceTarget"

if ($LASTEXITCODE -ne 0) { throw "构建失败，退出码 $LASTEXITCODE" }

$outDir = Join-Path $PSScriptRoot ".godot\mono\temp\bin\$Configuration"
if (-not (Test-Path (Join-Path $outDir "CardReminder.dll"))) {
    $outDir = Join-Path $PSScriptRoot "bin\$Configuration\net9.0"
}

Write-Host ""
Write-Host "输出目录: $outDir"
Get-ChildItem $outDir -Filter "CardReminder.*" | Select-Object Name, Length | Format-Table -AutoSize

if ($Install) {
    $modDir = Join-Path $Sts2GameDir "mods\CardReminder"
    New-Item -ItemType Directory -Path $modDir -Force | Out-Null
    foreach ($f in @("CardReminder.dll", "CardReminder.pdb", "CardReminder.json")) {
        $src = Join-Path $outDir $f
        if (Test-Path $src) {
            Copy-Item -LiteralPath $src -Destination (Join-Path $modDir $f) -Force
        }
    }
    Write-Host "已部署到游戏 mods 目录: $modDir"
}

if ($Package) {
    $manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot "CardReminder.json") -Raw | ConvertFrom-Json
    $distRoot = Join-Path $PSScriptRoot "dist"
    $pkgDir = Join-Path $distRoot "CardReminder-$($manifest.version)"
    New-Item -ItemType Directory -Path $pkgDir -Force | Out-Null
    $files = if ($Configuration -eq "Release") { @("CardReminder.dll", "CardReminder.json") } else { @("CardReminder.dll", "CardReminder.pdb", "CardReminder.json") }
    $copied = @()
    foreach ($f in $files) {
        $src = Join-Path $outDir $f
        if (Test-Path $src) {
            $dst = Join-Path $pkgDir $f
            Copy-Item -LiteralPath $src -Destination $dst -Force
            $copied += $dst
        }
    }
    $zipPath = Join-Path $distRoot "CardReminder-$($manifest.version).zip"
    if (Test-Path $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Compress-Archive -LiteralPath $copied -DestinationPath $zipPath -Force
    Write-Host "导出目录 : $pkgDir"
    Write-Host "导出压缩包: $zipPath"
}

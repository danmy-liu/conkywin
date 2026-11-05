# 打包自包含发行包（目标机器无需安装 .NET 运行时）
#
# 用法：右键「使用 PowerShell 运行」，或在项目目录执行
#   powershell -ExecutionPolicy Bypass -File .\build-release.ps1
#
# 产物：dist/ConkyWin-<版本>-win-x64.zip
#   ConkyWin.exe    自包含单文件（内含 .NET 8 运行时，双击即用）
#   config.json     推荐配置（268px 宽 Harmattan 布局，可自行改）
#   script.lua      Harmattan 主题脚本（删掉即回内置简洁面板）
#   assets\icons\   天气图标
#   README.md / LICENSE

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# dotnet 路径：优先本机 SDK 目录，其次 PATH（PATH 里可能只有运行时、没有 SDK）
$candidates = @(
    (Join-Path $env:USERPROFILE 'dotnet8\dotnet.exe'),
    (Get-Command dotnet -ErrorAction SilentlyContinue).Source
) | Where-Object { $_ -and (Test-Path $_) }
$dotnet = $candidates | Where-Object { & $_ --list-sdks 2>$null } | Select-Object -First 1
if (-not $dotnet) { throw "找不到 .NET 8 SDK，请先安装：https://dotnet.microsoft.com/download/dotnet/8.0" }

$ver = (Select-String -Path ConkyWin.csproj -Pattern '<Version>(.*?)</Version>').Matches[0].Groups[1].Value
$outDir = "dist/ConkyWin-$ver-win-x64"

Write-Host "发布自包含单文件版本 $ver ..." -ForegroundColor Cyan
& $dotnet publish ConkyWin.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none `
    -o $outDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败" }

# 附带文件：示例配置、主题脚本、说明文档
Copy-Item config.example.json (Join-Path $outDir 'config.json') -Force
Copy-Item script.harmattan.lua (Join-Path $outDir 'script.lua') -Force
Copy-Item README.md, LICENSE $outDir -Force

# 只打包发行必需的文件（避免把本地调试产物带进去）
$zip = "dist/ConkyWin-$ver-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $outDir 'ConkyWin.exe'),
    (Join-Path $outDir 'assets'),
    (Join-Path $outDir 'config.json'),
    (Join-Path $outDir 'script.lua'),
    (Join-Path $outDir 'README.md'),
    (Join-Path $outDir 'LICENSE') `
    -DestinationPath $zip -Force

$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host ""
Write-Host "完成：$zip（$size MB）" -ForegroundColor Green
Write-Host "解压后双击 ConkyWin.exe 即可运行，无需安装 .NET" -ForegroundColor Green

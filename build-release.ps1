# 打包自包含发行包（目标机器无需安装 .NET 运行时）
#
# 同时产出两种交付形态：
#   dist/ConkyWin-<版本>-win-x64.zip    便携版：解压即用，配置在 exe 同目录
#   dist/ConkyWin-<版本>-setup.exe      安装版：可自选安装目录、桌面/开始菜单快捷方式、
#                                       开机自启、安装后启动、控制面板卸载
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File .\build-release.ps1
#   powershell -ExecutionPolicy Bypass -File .\build-release.ps1 -SkipInstaller   # 只出 zip
#   powershell -ExecutionPolicy Bypass -File .\build-release.ps1 -IsccPath "D:\tools\ISCC.exe"
#
# Inno Setup 编译器：脚本会自动在常见位置查找；找不到时下载官方 6.7.4，
# 以「仅当前用户 / 静默」方式装到 %LOCALAPPDATA%\ConkyWinBuildTools\IS6，不写系统目录、不弹 UAC。
# 也可用环境变量 CONKYWIN_ISCC 指定 ISCC.exe 路径（CI 里由 choco 预装）。

[CmdletBinding()]
param(
    [switch]$SkipInstaller,
    [string]$IsccPath = $env:CONKYWIN_ISCC
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# dotnet 路径：优先本机 SDK 目录，其次 PATH（PATH 里可能只有运行时、没有 SDK）
$candidates = @(
    (Join-Path $env:USERPROFILE 'dotnet8\dotnet.exe'),
    (Get-Command dotnet -ErrorAction SilentlyContinue).Source
) | Where-Object { $_ -and (Test-Path $_) }
$dotnet = $candidates | Where-Object { & $_ --list-sdks 2>$null } | Select-Object -First 1
if (-not $dotnet) { throw "找不到 .NET 8 SDK，请先安装：https://dotnet.microsoft.com/download/dotnet/8.0" }

function Find-Iscc {
    param([string]$Hint)
    $list = @()
    if ($Hint) { $list += $Hint }
    $list += @(
        (Join-Path $env:LOCALAPPDATA 'ConkyWinBuildTools\IS6\ISCC.exe'),
        (Join-Path $PSScriptRoot 'dist\_tools\IS6\ISCC.exe'),
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe',
        'C:\Program Files (x86)\Inno Setup 7\ISCC.exe',
        'C:\Program Files\Inno Setup 7\ISCC.exe'
    )
    foreach ($c in $list) { if ($c -and (Test-Path $c)) { return (Resolve-Path $c).Path } }

    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    foreach ($root in @($env:ProgramFiles, ${env:ProgramFiles(x86)}, "$env:LOCALAPPDATA\Programs")) {
        if (-not $root -or -not (Test-Path $root)) { continue }
        try {
            $hit = Get-ChildItem -Path $root -Filter 'ISCC.exe' -Recurse -Depth 3 -ErrorAction SilentlyContinue |
                   Where-Object { $_.FullName -like '*Inno Setup*' } | Select-Object -First 1
            if ($hit) { return $hit.FullName }
        } catch { }
    }
    return $null
}

function Install-Iscc {
    $tools = Join-Path $env:LOCALAPPDATA 'ConkyWinBuildTools'
    if (-not (Test-Path $tools)) { New-Item -ItemType Directory -Path $tools -Force | Out-Null }
    $setupExe = Join-Path $tools 'innosetup-6.7.4.exe'
    $url = 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_4/innosetup-6.7.4.exe'
    if (-not (Test-Path $setupExe) -or (Get-Item $setupExe).Length -lt 10MB) {
        Write-Host '  未找到 Inno Setup 编译器，正在下载官方 6.7.4 ...' -ForegroundColor Yellow
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $url -OutFile $setupExe -UseBasicParsing -TimeoutSec 600
    }
    $dest = Join-Path $tools 'IS6'
    Write-Host "  用户级静默安装到 $dest ..." -ForegroundColor Yellow
    $p = Start-Process -FilePath $setupExe -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER',
        "/DIR=$dest", '/NOICONS', '/TASKS='
    ) -Wait -PassThru
    if ($p.ExitCode -ne 0) { Write-Host "  Inno Setup 安装器返回 $($p.ExitCode)" -ForegroundColor Yellow }
    $iscc = Join-Path $dest 'ISCC.exe'
    if (Test-Path $iscc) { return $iscc }
    return $null
}

$ver = (Select-String -Path ConkyWin.csproj -Pattern '<Version>(.*?)</Version>').Matches[0].Groups[1].Value
$outDirName = "ConkyWin-$ver-win-x64"
$outDir = "dist/$outDirName"

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

# 天气图标等资源：增量 publish 会清空目标目录，却可能因为「已是最新」不重拷 None/Content，
# 结果 assets/icons 变成空目录。这里显式复制一次，保证 zip 与安装包装的都是完整图标。
$assetDst = Join-Path $outDir 'assets'
if (-not (Test-Path $assetDst)) { New-Item -ItemType Directory -Path $assetDst -Force | Out-Null }
Copy-Item -Path (Join-Path $PSScriptRoot 'assets\*') -Destination $assetDst -Recurse -Force
if (-not (Test-Path (Join-Path $assetDst 'icons\0.png'))) { throw "图标资源复制失败：$assetDst" }

# 便携标记：zip 版所有可写文件都在 exe 同目录（安装版目录里只有 installed.flag，没有这个文件）
Set-Content -Path (Join-Path $outDir 'portable.flag') -Encoding ASCII -Value @(
    'ConkyWin portable package.',
    'All user-editable files (config.json, script.lua, logs) live next to ConkyWin.exe.'
)

# 只打包发行必需的文件（避免把本地调试产物带进去）；-Force 直接覆盖旧包，不做删除
$zip = "dist/ConkyWin-$ver-win-x64.zip"
Compress-Archive -Path (Join-Path $outDir 'ConkyWin.exe'),
    (Join-Path $outDir 'assets'),
    (Join-Path $outDir 'config.json'),
    (Join-Path $outDir 'script.lua'),
    (Join-Path $outDir 'portable.flag'),
    (Join-Path $outDir 'README.md'),
    (Join-Path $outDir 'LICENSE') `
    -DestinationPath $zip -Force
$zipSize = [math]::Round((Get-Item $zip).Length / 1MB, 1)

# ---------- 安装版（Inno Setup）----------
$setupFile = "dist/ConkyWin-$ver-setup.exe"
$setupSize = $null
if (-not $SkipInstaller) {
    $iscc = Find-Iscc -Hint $IsccPath
    if (-not $iscc) {
        try { $iscc = Install-Iscc }
        catch { Write-Warning "自动准备 Inno Setup 编译器失败：$($_.Exception.Message)" }
    }
    if ($iscc) {
        Write-Host "编译安装程序：$iscc" -ForegroundColor Cyan
        & $iscc "/DPkgVer=$ver" "/DPkgDir=$outDirName" (Join-Path $PSScriptRoot 'installer\installer.iss')
        if ($LASTEXITCODE -ne 0) { throw "Inno Setup 编译失败（退出码 $LASTEXITCODE）" }
        if (-not (Test-Path $setupFile)) { throw "Inno Setup 未生成 $setupFile" }
        $setupSize = [math]::Round((Get-Item $setupFile).Length / 1MB, 1)
    }
    else {
        Write-Warning '未找到 Inno Setup 编译器，本次只生成便携 zip。'
        Write-Warning '可手动安装 Inno Setup 6.5+ 后重跑，或用 -IsccPath 指定 ISCC.exe 路径。'
    }
}

Write-Host ""
Write-Host "完成：$zip（$zipSize MB，便携版）" -ForegroundColor Green
if ($setupSize) { Write-Host "完成：$setupFile（$setupSize MB，安装版）" -ForegroundColor Green }
Write-Host "两个文件都可以作为 Release 附件上传；安装版支持自定义目录、桌面快捷方式与卸载。" -ForegroundColor Green

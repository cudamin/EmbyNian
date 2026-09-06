[CmdletBinding()]
param(
    # 不重新发布，直接拿 artifacts\publish\win-x64 里现成的那一份去编安装包 —— 与 publish.ps1
    # 的 -SkipPublish 同一个理由（程序正开着时它自己的 dll 删不掉）和同一个代价：这一趟没有
    # 任何东西证明那个目录是刚构建的。要「就是刚构建的这一份」，别加这个开关。
    [switch]$SkipPublish,

    [string]$ISCCPath,

    # 传给 publish.ps1 那一趟的开关（比如 -FrameworkDependent）。默认自包含，与发布件一致。
    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$iss = Join-Path $PSScriptRoot 'installer\EmbyNian.iss'
$languages = Join-Path $PSScriptRoot 'installer\Languages\ChineseSimplified.isl'
$publishRoot = Join-Path $repo 'artifacts\publish\win-x64'

# Inno Setup 6 的编译器：本机装在用户目录（winget 的 JRSoftware.InnoSetup），先给参数、
# 再问 PATH，最后落到那个已知位置。
if ([string]::IsNullOrWhiteSpace($ISCCPath)) {
    $ISCCPath = (Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source)
}
if ([string]::IsNullOrWhiteSpace($ISCCPath)) {
    $ISCCPath = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
}
if (-not (Test-Path -LiteralPath $ISCCPath -PathType Leaf)) {
    throw "找不到 ISCC.exe（Inno Setup 6 的命令行编译器）。用 -ISCCPath 指定，或 winget install JRSoftware.InnoSetup。"
}

if (-not (Test-Path -LiteralPath $iss -PathType Leaf)) { throw "找不到安装脚本：$iss" }
if (-not (Test-Path -LiteralPath $languages -PathType Leaf)) {
    throw "找不到中文语言文件：$languages —— 它是仓库的一部分，检出不完整时才会缺。"
}

# 版本号只认 Directory.Build.props 那一份出处，读法与 publish.ps1 相同。
$props = [xml](Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw)
$versionText = [string]($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($versionText)) { $versionText = '0.0.0' }
$version = ($versionText -split '\.')[0..2] -join '.'
$versionQuad = "$version.0"

if ($SkipPublish) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishRoot 'EmbyNian.exe') -PathType Leaf)) {
        throw "给了 -SkipPublish，但 $publishRoot 里没有 EmbyNian.exe。先跑一次不带这个开关的发布。"
    }
    Write-Output "跳过发布，拿 $publishRoot 里现成的那一份（它可能不是刚构建的，见 -SkipPublish 的说明）。"
} else {
    & (Join-Path $PSScriptRoot 'publish.ps1') -NoArchive $(if ($FrameworkDependent) { '-FrameworkDependent' })
    if ($LASTEXITCODE -ne 0) { throw 'publish.ps1 失败。' }
}

# 安装包落在 artifacts 根上，跟 zip / msix 并排。
Write-Output ("编译安装包 EmbyNian_windows-x64_{0}.exe ..." -f $version)
& $ISCCPath ("/DMyAppVersion=$version", "/DVersionQuad=$versionQuad", $iss)
if ($LASTEXITCODE -ne 0) { throw "ISCC 编译失败，退出码 $LASTEXITCODE。" }

$setupPath = Join-Path $repo "artifacts\EmbyNian_windows-x64_$version.exe"
if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) { throw "编译说成功了，但找不到 $setupPath。" }
$sizeMB = [math]::Round((Get-Item -LiteralPath $setupPath).Length / 1MB, 1)
Write-Output "安装包已生成：$setupPath（$sizeMB MB）"

[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',

    [switch]$FrameworkDependent,

    [switch]$Msix,

    # 打包那一步跳过。开发回路上每跑一次关卡都要把 375 MB 用 Optimal 压成一个 148 MB 的 zip，一两分钟就
    # 花在这里 —— 而自检要的是 artifacts\publish\win-x64 下面那个 exe，压缩包谁都不看。默认仍然打包：
    # 那个 zip 才是交出去的东西，少打包是迭代时的选择，不是发布时的。
    [switch]$NoArchive,

    # Off by default: publishing is something CI or a build script does, and writing to someone's desktop is
    # not. Pass it on the machine that actually runs the app.
    [switch]$Shortcut,

    # Both of these default to 「work it out below」 rather than to an expression, because Windows
    # PowerShell evaluates a param block's defaults before it populates $PSScriptRoot when the script
    # carries [CmdletBinding()] — an $OutputRoot computed here came out empty and the script died on its
    # first Split-Path.
    [string]$OutputRoot,

    [string]$DotnetPath
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$project = Join-Path $repo 'src\EmbyNian.Shell\EmbyNian.Shell.csproj'
$verify = Join-Path $PSScriptRoot 'verify-publish.ps1'
$shortcutScript = Join-Path $PSScriptRoot 'shortcut.ps1'

if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $repo 'artifacts' }

if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    throw "找不到 WinUI Shell 项目：$project"
}

# global.json pins an SDK version, and the dotnet on PATH is not necessarily the one that has it —
# on a machine with both a system-wide 8.x and a per-user 10.x, PATH finds the 8.x and every publish
# fails with 「找不到与 global.json 匹配的 SDK」. So each candidate is asked, in the repository root
# where global.json applies, whether it can resolve a version at all; the first that can is used.
$dotnetCandidates = @(
    $DotnetPath,
    $env:DOTNET_HOST_PATH,
    (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'),
    (Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source)
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique

$dotnet = $null
foreach ($candidate in $dotnetCandidates) {
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
    $reported = & $candidate --version 2>$null
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($reported)) {
        $dotnet = $candidate
        Write-Output "使用 SDK $($reported.Trim())：$dotnet"
        break
    }
}
if (-not $dotnet) {
    throw "找不到能满足 global.json 的 dotnet SDK（试过：$($dotnetCandidates -join '、')）。请安装 .NET 10 SDK，或用 -DotnetPath 指定 dotnet.exe。"
}

# 着色器文件（2026-09-03 起）和 libmpv 唯一那个非系统依赖 vulkan-1.dll（2026-09-04 起）都在仓库里，
# 由 csproj 当普通内容文件拷进输出目录 —— 从前两样都是发布时从 `C:\mpv_config-2026.08.12` 现拷的，
# 那等于「这个程序能不能正确发布，取决于另一个软件还装没装」。下面这两条只在检出不完整时会红。
foreach ($asset in @('assets\shaders', 'assets\mpv-runtime\vulkan-1.dll')) {
    $assetPath = Join-Path $repo $asset
    if (-not (Test-Path -LiteralPath $assetPath)) {
        throw "找不到 $assetPath。它是仓库的一部分，检出不完整时才会缺。"
    }
}

$libMpv = Join-Path $repo 'libmpv-2.dll'
if (-not (Test-Path -LiteralPath $libMpv -PathType Leaf)) {
    throw "找不到 $libMpv。请先把与当前 x64 构建匹配的 libmpv-2.dll 放到仓库根目录。"
}

$props = [xml](Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw)
$versionText = [string]($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($versionText)) { $versionText = '0.0.0' }
$version = ($versionText -split '\.')[0..2] -join '.'
$packageVersion = "$version.0"

$outputRootResolved = [System.IO.Path]::GetFullPath($OutputRoot)
$publishRoot = Join-Path $outputRootResolved "publish\$Runtime"
$zipPath = Join-Path $outputRootResolved "EmbyNian-$version-$Runtime.zip"
$msixPath = Join-Path $outputRootResolved "EmbyNian-$version-$Runtime.msix"
$stageRoot = Join-Path $outputRootResolved "msix-stage\$Runtime"

New-Item -ItemType Directory -Path $outputRootResolved -Force | Out-Null
foreach ($path in @($publishRoot, $stageRoot)) {
    if (Test-Path -LiteralPath $path) {
        $resolved = (Resolve-Path -LiteralPath $path).Path
        if (-not $resolved.StartsWith($outputRootResolved, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "拒绝清理输出目录之外的路径：$resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
# 上一次的压缩包一律删掉，-NoArchive 也删：一个跟刚发布的目录对不上的 zip 比没有 zip 更坏。
foreach ($archive in @($zipPath, $msixPath)) {
    if (Test-Path -LiteralPath $archive -PathType Leaf) { Remove-Item -LiteralPath $archive -Force }
}

$selfContained = -not $FrameworkDependent
$publishArgs = @(
    'publish', $project,
    '-c', $Configuration,
    '-r', $Runtime,
    '--self-contained', ($selfContained.ToString().ToLowerInvariant()),
    '-p:Platform=x64',
    ('-p:WindowsAppSDKSelfContained=' + $selfContained.ToString().ToLowerInvariant()),
    '-p:PublishSingleFile=false',
    '-p:PublishReadyToRun=false',
    '-p:DebugSymbols=false',
    '-p:DebugType=None',
    '-o', $publishRoot
)

Write-Output ("发布 {0} ({1}, {2})..." -f $project, $Runtime, ($(if ($selfContained) { '自包含' } else { '框架依赖' })))
& $dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败，退出码 $LASTEXITCODE。" }

& $verify -PublishRoot $publishRoot -RepositoryRoot $repo -SelfContained:$selfContained
if ($LASTEXITCODE -ne 0) { throw '发布验证失败。' }

if ($NoArchive) {
    Write-Output '按 -NoArchive 跳过打包。'
} else {
    Compress-Archive -Path (Join-Path $publishRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Output "ZIP 已生成：$zipPath"
}

if ($Shortcut) {
    & $shortcutScript -Exe (Join-Path $publishRoot 'EmbyNian.exe')
}

if ($Msix) {
    $makeAppxCommand = Get-Command makeappx.exe -ErrorAction SilentlyContinue
    if ($makeAppxCommand) {
        $makeAppxPath = $makeAppxCommand.Path
    } else {
        $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
        $makeAppxPath = Get-ChildItem -LiteralPath $kits -Recurse -Filter makeappx.exe -File -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\makeappx\.exe$' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1 -ExpandProperty FullName
    }
    if ([string]::IsNullOrWhiteSpace($makeAppxPath)) {
        throw '请求了 -Msix，但找不到 makeappx.exe。请安装 Windows 10/11 SDK 后重试。'
    }

    New-Item -ItemType Directory -Path (Join-Path $stageRoot 'Assets') -Force | Out-Null
    Copy-Item -Path (Join-Path $publishRoot '*') -Destination $stageRoot -Recurse -Force

    # MSIX logos must be PNG. Convert the repository ICO at packaging time so no generated binary
    # has to be checked in.
    Add-Type -AssemblyName System.Drawing
    $icon = [System.Drawing.Icon]::ExtractAssociatedIcon((Join-Path $repo 'app.ico'))
    $iconBitmap = $icon.ToBitmap()
    try {
        foreach ($asset in @(@('StoreLogo.png', 50), @('Square44x44Logo.png', 44), @('Square150x150Logo.png', 150))) {
            $bitmap = New-Object -TypeName System.Drawing.Bitmap -ArgumentList ([int]$asset[1]), ([int]$asset[1])
            try {
                $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
                try {
                    $graphics.Clear([System.Drawing.Color]::Transparent)
                    $graphics.DrawImage($iconBitmap, 0, 0, $asset[1], $asset[1])
                    $bitmap.Save((Join-Path $stageRoot "Assets\$($asset[0])"), [System.Drawing.Imaging.ImageFormat]::Png)
                } finally { $graphics.Dispose() }
            } finally { $bitmap.Dispose() }
        }
    } finally {
        $iconBitmap.Dispose()
        $icon.Dispose()
    }

    $manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10" IgnorableNamespaces="uap">
  <Identity Name="EmbyNian" Publisher="CN=EmbyNian" Version="$packageVersion" ProcessorArchitecture="x64" />
  <Properties>
    <DisplayName>EmbyNian</DisplayName>
    <PublisherDisplayName>EmbyNian</PublisherDisplayName>
    <Description>Emby 的 Windows 播放客户端</Description>
    <Logo>Assets\StoreLogo.png</Logo>
  </Properties>
  <Resources><Resource Language="zh-CN" /></Resources>
  <Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.17763.0" MaxVersionTested="10.0.26100.0" /></Dependencies>
  <Applications>
    <Application Id="EmbyNian" Executable="EmbyNian.exe" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements DisplayName="EmbyNian" Description="Emby 的 Windows 播放客户端" BackgroundColor="#171717" Square44x44Logo="Assets\Square44x44Logo.png" Square150x150Logo="Assets\Square150x150Logo.png" />
    </Application>
  </Applications>
</Package>
"@
    Set-Content -LiteralPath (Join-Path $stageRoot 'AppxManifest.xml') -Value $manifest -Encoding utf8

    & $makeAppxPath pack /d $stageRoot /p $msixPath /o
    if ($LASTEXITCODE -ne 0) { throw "makeappx 打包失败，退出码 $LASTEXITCODE。" }
    Write-Output "MSIX 已生成（未签名）：$msixPath"
}

[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',

    [switch]$FrameworkDependent,

    [switch]$Msix,

    # 给 -Msix 出来的包签名。签名用的是自签发的开发证书（winapp cert generate，出厂密码 password），
    # 它只够在本机测试 —— 而且**装这个包之前还要用管理员终端信任这张证书一次**：
    #     winapp cert install .\artifacts\devcert.pfx
    # 那一步这个脚本做不到（要提权），所以签完会把这行命令印出来。
    # 生产签名传 -CertPath 指一张真证书，并且用 -Timestamp 加时间戳，否则证书一过期签名就失效。
    [switch]$Sign,

    [string]$CertPath,

    [string]$CertPassword = 'password',

    [string]$Timestamp,

    # 不重新发布，直接拿 artifacts\publish\win-x64 里现成的那一份去打包。用在两种时候：程序正开着（它自己
    # 那些 dll 删不掉，重新发布会在第一步就失败），或者刚跑完闸门、只想把同一份构建装个包出去。
    # **代价说清**：这一趟没有任何东西证明那个目录是新的 —— 它可能是上一次构建留下的。要「就是刚构建的
    # 这一份」，就别加这个开关。
    [switch]$SkipPublish,

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
    # 相对路径先按调用者所在目录解析，再进入仓库，让 --version 真正检查这里的 global.json。
    $candidate = (Resolve-Path -LiteralPath $candidate).Path
    Push-Location -LiteralPath $repo
    try {
        $reported = & $candidate --version 2>$null
    } catch {
        # Windows PowerShell 5.1 即使重定向了 stderr，Stop 仍会把 SDK 不匹配变成异常；继续试下一项。
        continue
    } finally {
        Pop-Location
    }
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
foreach ($asset in @('assets\shaders', 'assets\mpv-runtime\vulkan-1.dll', 'assets\fonts')) {
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

# 只有真要重新发布的时候才清 publish；-SkipPublish 那一趟连碰都不碰它。理由是文件锁：程序正开着的时候
# 它自己那些 dll（clrjit.dll 第一个）删不掉，于是整个脚本在第一步就死 —— 而「程序开着」恰恰是想单独打个
# 包给人装的时候最常见的状态。msix-stage 照旧每次重建，它没人占着。
$toClean = @($stageRoot)
if (-not $SkipPublish) { $toClean += $publishRoot }
foreach ($path in $toClean) {
    if (Test-Path -LiteralPath $path) {
        $resolved = (Resolve-Path -LiteralPath $path).Path
        if (-not $resolved.StartsWith($outputRootResolved, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "拒绝清理输出目录之外的路径：$resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
# 上一次的压缩包一律删掉，-NoArchive 也删：一个跟刚发布的目录对不上的 zip 比没有 zip 更坏。
if (Test-Path -LiteralPath $zipPath -PathType Leaf) { Remove-Item -LiteralPath $zipPath -Force }

# .msix 是另一回事，2026-09-06 改的：从前它跟 zip 一起被无条件删掉，于是「打一次包 → 再跑一遍闸门」
# 这个再普通不过的次序会把刚交出去的安装包悄悄弄没 —— 真发生过一次，用户拿着路径去装，文件已经不在了。
# 现在只有这一趟真的要重打包（-Msix）时才删。留着的那一份可能对不上刚发布的目录，所以留就要说一声：
# 版本号一样而内容更旧的安装包，比没有安装包更坏，这一句就是防它。
if ($Msix) {
    if (Test-Path -LiteralPath $msixPath -PathType Leaf) { Remove-Item -LiteralPath $msixPath -Force }
} elseif (Test-Path -LiteralPath $msixPath -PathType Leaf) {
    Write-Warning "留着上一次的安装包没动：$msixPath —— 它是更早那次构建的，别拿它当刚发布的这一份。要重打包加 -Msix -Sign。"
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

if ($SkipPublish) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishRoot 'EmbyNian.exe') -PathType Leaf)) {
        throw "给了 -SkipPublish，但 $publishRoot 里没有 EmbyNian.exe。先跑一次不带这个开关的发布。"
    }
    Write-Output "跳过发布，拿 $publishRoot 里现成的那一份（它可能不是刚构建的，见 -SkipPublish 的说明）。"
} else {
    Write-Output ("发布 {0} ({1}, {2})..." -f $project, $Runtime, ($(if ($selfContained) { '自包含' } else { '框架依赖' })))
    & $dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败，退出码 $LASTEXITCODE。" }
}

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
    # 打包走 WinApp CLI，不走 makeappx。
    #
    # 从前这里找的是 Windows SDK 里的 makeappx.exe —— 而**这台机器上根本没装 Windows SDK**，所以
    # `-Msix` 一直只会抛「找不到 makeappx.exe」，这也就是「打包成 MSIX」这件活一直欠着的实际原因。
    # `winapp package` 自己把布局、PRI、打包、签名四件事做完（winui-packaging 那份技能的 Quick
    # Reference 就是这么写的），不需要 SDK，所以这条路在这台机器上是通的。
    $winapp = Get-Command winapp -ErrorAction SilentlyContinue
    if (-not $winapp) {
        throw '请求了 -Msix，但 PATH 上没有 winapp（WinApp CLI 0.6+）。装它见 winui-setup 那份技能；这台机器上没有 Windows SDK，所以没有第二条路。'
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

    # 清单从仓库里那一份来，不再在这个脚本里写第二遍。它是包身份唯一的出处，`winapp cert generate
    # --manifest` 也是从它读 Publisher 的；版本号则相反 —— 它在 Directory.Build.props 里，下面按那一份
    # 改写清单里的占位值，好让程序集和包不会各报一个版本。
    $manifestSource = Join-Path $repo 'src\EmbyNian.Shell\Package.appxmanifest'
    if (-not (Test-Path -LiteralPath $manifestSource -PathType Leaf)) {
        throw "找不到 $manifestSource。它是包身份唯一的出处，winui-packaging 那条「不要删掉 Package.appxmanifest」说的就是它。"
    }
    # -Encoding UTF8 不能省：这个脚本跑在 Windows PowerShell 5.1 上，Get-Content -Raw 对没有 BOM 的文件
    # 按 ANSI 代码页读，于是清单里的中文进来就是乱码，写进包里的显示名和说明也跟着乱。
    $manifestXml = [xml](Get-Content -LiteralPath $manifestSource -Raw -Encoding UTF8)
    $manifestXml.Package.Identity.Version = $packageVersion
    $manifestPath = Join-Path $stageRoot 'AppxManifest.xml'
    $manifestXml.Save($manifestPath)

    # 证书先备好，好让打包那一步顺手签名（技能里那条「宁可 package --cert，不要 package 完再 sign」）。
    $cert = $null
    if ($Sign) {
        $cert = $CertPath
        if ([string]::IsNullOrWhiteSpace($cert)) {
            # 自签证书放进 artifacts，跟别的生成物一起 —— 它带私钥，不该进仓库（artifacts 已被忽略）。
            # --if-exists skip 让重复发布不至于每次换一张新证书：换了的话上次信任过的那张就白信任了。
            $cert = Join-Path $outputRootResolved 'devcert.pfx'
            & $winapp.Path cert generate --manifest $manifestPath --output $cert --if-exists skip --quiet
            if ($LASTEXITCODE -ne 0) { throw "winapp cert generate 失败，退出码 $LASTEXITCODE。" }
        }
        if (-not (Test-Path -LiteralPath $cert -PathType Leaf)) { throw "找不到证书：$cert" }
    }

    # --skip-pri 是必须的，不是省一步。发布目录里那个 EmbyNian.pri 已经把框架那三份并进来了（见
    # verify-publish.ps1 里那一大段），让 winapp 再生成一遍会盖掉它、并出一个 103 KB 的版本 —— 装出来的
    # 程序一启动就死在 App.xaml。
    $packArgs = @('package', $stageRoot, '--manifest', $manifestPath, '--output', $msixPath, '--skip-pri')
    if ($cert) {
        $packArgs += @('--cert', $cert, '--cert-password', $CertPassword)
    }
    & $winapp.Path @packArgs
    if ($LASTEXITCODE -ne 0) { throw "winapp package 失败，退出码 $LASTEXITCODE。" }

    if (-not $cert) {
        Write-Output "MSIX 已生成（未签名）：$msixPath"
        Write-Output '未签名的包装不上。加 -Sign 让脚本自签，或者用 -CertPath 指一张真证书。'
    } else {
        if (-not [string]::IsNullOrWhiteSpace($Timestamp)) {
            & $winapp.Path sign $msixPath $cert --password $CertPassword --timestamp $Timestamp
            if ($LASTEXITCODE -ne 0) { throw "winapp sign（加时间戳）失败，退出码 $LASTEXITCODE。" }
        }
        Write-Output "MSIX 已生成并签名：$msixPath"
        Write-Output "证书：$cert"
        if ([string]::IsNullOrWhiteSpace($Timestamp)) {
            Write-Output '没有加时间戳，所以这张签名会随证书一起过期 —— 交出去的版本请传 -Timestamp。'
        }
        Write-Output ''
        Write-Output '还差一步，而且只有你能做（要管理员权限的终端），一台机器只用做一次：'
        Write-Output "    winapp cert install `"$cert`""
        Write-Output '信任之后双击那个 .msix 就能装。'
    }
}

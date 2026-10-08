<#
    解除下载来的 Momoka 可执行文件的「来自互联网」标记（MOTW / Zone.Identifier）。

    为什么是这一步：SmartScreen 只评估带这个标记的文件。自己构建出来的文件不带它，
    本来就不该被拦；下载来的安装包带它，第一次双击就会弹「Windows 已保护你的电脑 /
    发布者未知」。摘掉这个标记等于告诉 Windows「这份文件是我自己的」，之后再双击就不会
    再问一遍。

    为什么不是签名：微软官方明确写着自签名证书的 SmartScreen 行为**与不签名相同**
    （https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation），
    所以拿自签证书签这个 exe 消不掉提示，只会多一层假的安全感。真要让别人的机器也不提示，
    只有 CA 签发的证书或上架 Microsoft Store 两条路。

    补记（2026-10-01 续八·终版）：本案最终根因是**仓库目录上的低强制完整性标签**
    （Mandatory Label\Low，全树继承，urlmon 据此把本地文件判成 Internet 区）——
    不是 MOTW、不是签名。判别一条命令：`icacls <路径> | findstr Mandatory`。
    「SmartScreen 只评估带标记的文件」对下载件仍然成立，本脚本仍值得先用；
    但若文件无标记仍被拦，先查完整性标签，别再翻注册表。

    安全边界：只处理文件名以 Momoka 开头、扩展名是 .exe/.msix/.appx/.zip，并且（可执行文件
    的）版本信息里 ProductName 或 CompanyName 就是 Momoka 的文件。别的一律不碰、只报告不改。
    默认先看不做，加 -Apply 才真动手。

    用法：
        powershell -File tools\unblock-momoka.ps1            # 只看会动哪些文件
        powershell -File tools\unblock-momoka.ps1 -Apply     # 真摘标记
        powershell -File tools\unblock-momoka.ps1 -Apply -Path 'D:\下载'
#>
[CmdletBinding()]
param(
    # 真动手摘标记。不加只是列出来。
    [switch]$Apply,

    # 搜索位置。默认是本机放下载件和构建产物的那几处；-Path 可以指定别处（比如移动硬盘上的下载目录）。
    [string[]]$Path,

    # 顺带看一眼签名状态，方便判断「要不要买证书」。
    [switch]$ShowSignature
)

$ErrorActionPreference = 'Continue'

if (-not $Path -or $Path.Count -eq 0) {
    $Path = @(
        (Join-Path $env:USERPROFILE 'Downloads'),
        (Join-Path $env:USERPROFILE 'Desktop'),
        (Join-Path $env:LOCALAPPDATA 'Programs')
    )
}

$existing = @($Path | Where-Object { $_ -and (Test-Path -LiteralPath $_) })
if ($existing.Count -eq 0) {
    Write-Output '给的位置一个都不存在，没什么可查的。'
    return
}

Write-Output ("搜索位置：" + ($existing -join '、'))
Write-Output ''

# 只认 Momoka 自己的东西：文件名打头，扩展名在白名单里。
$namePattern = '^Momoka[^\\/]*\.(exe|msix|appx|zip)$'

function Test-MomokaProduct {
    param([string]$FullName, [string]$Extension)
    # 版本信息不适合读 .zip，压缩包按文件名认。
    if ($Extension -eq '.zip') { return $true }
    try {
        # Trim 不能省：Inno Setup 写进 PE 的字符串资源是定长补空格的，ProductName 实际是
        # 「Momoka」后面跟 40 多个空格，直接 -eq 永远不成立 —— 这个坑让第一版脚本一个文件都没匹配到。
        $info = (Get-Item -LiteralPath $FullName).VersionInfo
        $product = "$($info.ProductName)".Trim()
        $company = "$($info.CompanyName)".Trim()
        return ($product -eq 'Momoka') -or ($company -eq 'Momoka')
    } catch {
        return $false
    }
}

function Get-Motw {
    param([string]$FullName)
    try {
        $streams = Get-Item -LiteralPath $FullName -Stream * -ErrorAction Stop
        if ($streams | Where-Object { $_.Stream -eq 'Zone.Identifier' }) {
            return ((Get-Content -LiteralPath $FullName -Stream Zone.Identifier -ErrorAction SilentlyContinue) -join ' ')
        }
    } catch { }
    return $null
}

$candidates = [System.Collections.Generic.List[object]]::new()
foreach ($root in $existing) {
    Get-ChildItem -LiteralPath $root -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match $namePattern } |
        ForEach-Object {
            if (-not (Test-MomokaProduct -FullName $_.FullName -Extension $_.Extension)) { return }
            $motw = Get-Motw -FullName $_.FullName
            $sig = '（压缩包，不看签名）'
            if ($ShowSignature -and $_.Extension -ne '.zip') {
                try { $sig = (Get-AuthenticodeSignature -LiteralPath $_.FullName).Status.ToString() } catch { $sig = '读不出来' }
            }
            $candidates.Add([pscustomobject]@{
                FullName = $_.FullName
                Time     = $_.LastWriteTime
                SizeMB   = [math]::Round($_.Length / 1MB, 1)
                Motw     = [bool]$motw
                Sig      = $sig
            })
        }
}

if ($candidates.Count -eq 0) {
    Write-Output '没找到任何 Momoka 的可执行文件。'
    return
}

$locked = @($candidates | Where-Object { $_.Motw } | Sort-Object FullName)

Write-Output ("找到 Momoka 可执行文件 {0} 个，其中带「来自互联网」标记的 {1} 个：" -f $candidates.Count, $locked.Count)
foreach ($item in $locked) {
    Write-Output ("  [带标记] {0}  {1} MB  {2}" -f $item.Time, $item.SizeMB, $item.FullName)
    if ($ShowSignature) { Write-Output ("           签名：{0}" -f $item.Sig) }
}
if ($locked.Count -eq 0) {
    Write-Output '  （没有）—— 这台机器上已经没有会被 SmartScreen 评估的 Momoka 文件了。'
}
Write-Output ''

if (-not $Apply) {
    Write-Output '以上只是清点，没有改动任何文件。要真摘掉标记，重跑并加上 -Apply。'
    return
}

if ($locked.Count -eq 0) {
    Write-Output '没有需要摘标记的文件，什么都没动。'
    return
}

# 这一段刻意不复用 Unblock-File：在受限的沙箱终端里它会「执行成功但什么也没改」——
# 2026-10-01 实测，同一个文件上 Unblock-File -ErrorAction Stop 不抛异常，而
# [System.IO.File]::OpenWrite 明确报「访问被拒绝」，标记一个字节没少。
# 所以两条路都走一遍，再**读回真实状态**判断成败，而不是相信命令的返回值。
$done = 0
foreach ($item in $locked) {
    $removed = $false
    try {
        Unblock-File -LiteralPath $item.FullName -ErrorAction SilentlyContinue
    } catch { }
    if (-not (Get-Motw -FullName $item.FullName)) { $removed = $true }

    if (-not $removed) {
        # 第二条路：直接删掉那个备用数据流。Unblock-File 走的是同一个东西，
        # 但在它被拦下的环境里这一条有时能过。
        try {
            Remove-Item -LiteralPath "$($item.FullName):Zone.Identifier" -Force -ErrorAction Stop
        } catch { }
        if (-not (Get-Motw -FullName $item.FullName)) { $removed = $true }
    }

    if ($removed) {
        Write-Output ("  已解除锁定：{0}" -f $item.FullName)
        $done++
    } else {
        Write-Output ("  失败：标记还在 —— {0}" -f $item.FullName)
    }
}

Write-Output ''
Write-Output ("共处理 {0} 个文件，成功 {1} 个。" -f $locked.Count, $done)

if ($done -lt $locked.Count) {
    Write-Output ''
    Write-Output '有文件没改成。最常见的原因是当前终端跑在受限沙箱里：它对这个目录的写入会被'
    Write-Output '静默忽略（不报错，也不生效）。换一个普通 PowerShell 窗口重跑同一条命令即可：'
    Write-Output ''
    Write-Output ("    powershell -NoProfile -File `"{0}`" -Apply" -f $PSCommandPath)
    Write-Output ''
} else {
    Write-Output '再双击一次被拦的那个安装包试试：这次应当直接进安装向导，不再问「仍要运行」。'
}

<#
    给交付件做代码签名 —— 用一张装进本机受信任存储的自签证书。

    先说清这条路能做什么、不能做什么（2026-10-01 实测并对照微软文档）：

    * **能**：在这台机器上，让 Windows 认出「这是 EmbyNian 发的」，双击不再弹「发布者未知」。
      前提是证书被装进本机受信任存储（本脚本的 -Setup 做这件事，需要管理员终端一次）。
    * **不能**：对别人的机器无效。自签证书在别人那里不是受信任根，SmartScreen 的判定与不签名
      相同（微软文档 smartscreen-reputation 原文：Self-signed Certificate → 与不签名同行为）。
      要让下载者也免提示，只有 CA 签发的证书或上架 Microsoft Store 两条路。

    所以本脚本的适用场景写明白：**自己这台机器上自用**，或**企业内受管分发**（IT 把证书铺到
    所有机器）。对外发布件的签名仍然只能等真证书。

    用法（管理员终端，一次）：
        powershell -NoProfile -ExecutionPolicy Bypass -File tools\sign-release.ps1 -Setup

    之后（普通终端即可）：
        powershell -NoProfile -ExecutionPolicy Bypass -File tools\sign-release.ps1 -Path artifacts\publish\win-x64\EmbyNian.exe
        powershell -NoProfile -ExecutionPolicy Bypass -File tools\sign-release.ps1 -Path artifacts\EmbyNian_windows-x64_0.1.1.exe

    看现状：
        powershell -NoProfile -ExecutionPolicy Bypass -File tools\sign-release.ps1 -Status
#>
[CmdletBinding()]
param(
    # 建证书并装进本机受信任存储。需要管理员终端，只需做一次。
    [switch]$Setup,

    # 要看的东西：文件或目录（目录会挑出 .exe / .msix / .dll）。
    [string[]]$Path,

    # 证书指纹。不给就自动找本机 CN=EmbyNian 的那张。
    [string]$Thumbprint,

    # 时间戳服务器。生产签名要加，否则证书一过期签名就失效。
    [string]$Timestamp = 'http://timestamp.digicert.com',

    # 把这张证书登记成「受信任的发布者」（本机，管理员，只需一次）。
    # 这是 SmartScreen 认可的正规办法，不是关防护：它在
    # HKLM\SOFTWARE\Policies\Microsoft\Windows\Safer\CodeIdentifiers\262144\Paths 下按证书指纹
    # 建一条 Authenticode/TrustedPublisher 登记，属于企业环境里常用的受管配置。
    #
    # ！2026-10-01 实测结论：**这条登记对 SmartScreen 的「无法识别的应用」提示没有效果**。
    # 它管的是软件限制策略（SRP），跟资源管理器的「检查应用和文件」是两套机制；用户登记后
    # 双击仍被拦。留着是因为它在企业 SRP 受管环境里有意义，但别指望它消 SmartScreen 提示。
    [switch]$TrustPublisher,

    # 关掉/打开当前用户的「检查应用和文件」（SmartScreen 的应用与文件信誉检查）。
    # $null = 不动；'Off' = 置 0（不再拦未签名程序）；'On' = 置 1（恢复默认）。
    # 这是**关防护**：影响所有未签名程序，不只是本项目 —— 只能由用户点头后执行。
    # 写的是当前用户的 hive（提权后 HKCU 会指向管理员账户，所以按调用者的 SID 定位）。
    [ValidateSet('Off', 'On')]
    [string]$SetAppCheck,

    # 关掉/打开资源管理器信誉检查的落点：HKCU\...\Explorer\SmartScreenEnabled。
    # 注意：这不是「本地 exe 被拦」案的根因（那次的真凶是目录低完整性标签，判别用
    # icacls <路径> | findstr Mandatory）——本开关只管 SmartScreen 那一层的表现，
    # 留作应急，平时保持出厂（值不存在）。'Off' = 写 Off；'On' = 删值恢复出厂。
    # 只写 HKCU，不需要管理员。
    [ValidateSet('Off', 'On')]
    [string]$SetShellSmartScreen,

    # 只看状态，不签名。
    [switch]$Status
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$pfxPath = Join-Path $repo 'artifacts\code-signing-EmbyNian.pfx'

# signtool 没有随系统装（这台机器没有 Windows SDK），但 SDK.BuildTools 这个 NuGet 包里有，
# 本机 NuGet 缓存里就有三份。挑版本最高的一份用。
function Get-SignTool {
    $candidates = @(
        (Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools" -Directory -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName "bin\10.0.26100.0\x64\signtool.exe" })
    ) | Where-Object { Test-Path -LiteralPath $_ }
    if ($candidates.Count -gt 0) { return $candidates[0] }
    $onPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    throw '找不到 signtool.exe。装了 Windows SDK 或让 NuGet 有 microsoft.windows.sdk.buildtools 这个包再来。'
}

function Get-EmbyNianCert {
    param([string]$WantThumbprint)
    if ($WantThumbprint) {
        $found = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
            Where-Object { $_.Thumbprint -eq $WantThumbprint }
        if (-not $found) { throw "找不到指纹为 $WantThumbprint 的证书。" }
        return $found[0]
    }
    $found = Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -eq 'CN=EmbyNian' -and $_.HasPrivateKey } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $found) {
        throw '本机没有 CN=EmbyNian 的签名证书。先跑一次 -Setup（管理员终端）。'
    }
    return $found
}

function Test-Admin {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

# 「受信任发布者」登记的判据。抽成函数是为了让 -Status 和 -TrustPublisher 读同一处，
# 也为了让登记之后能**自动验证**，而不是只能靠用户说「不弹了」。
function Get-TrustPublisherState {
    param([string]$CertThumbprint)
    $base = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Safer\CodeIdentifiers'
    $state = [pscustomobject]@{
        PolicyKeyExists = $false
        DefaultLevel    = $null
        AuthenticodeEnabled = $null
        EntryExists     = $false
        Authenticode    = $null
        TrustedPublisher= $null
        MatchesCert     = $false
        EntryPath       = Join-Path (Join-Path $base '262144\Paths') $CertThumbprint
    }
    if (Test-Path -LiteralPath $base) {
        $state.PolicyKeyExists = $true
        $state.DefaultLevel = (Get-ItemProperty -LiteralPath $base -ErrorAction SilentlyContinue).DefaultLevel
    }
    # 262144 这份键上的 authenticodeenabled：**没有它，Paths 里的条目不会被读**。
    $softKey = Join-Path $base '262144'
    if (Test-Path -LiteralPath $softKey) {
        $state.AuthenticodeEnabled = (Get-ItemProperty -LiteralPath $softKey -ErrorAction SilentlyContinue).authenticodeenabled
    }
    if ($CertThumbprint -and (Test-Path -LiteralPath $state.EntryPath)) {
        $state.EntryExists = $true
        $props = Get-ItemProperty -LiteralPath $state.EntryPath -ErrorAction SilentlyContinue
        $state.Authenticode = $props.Authenticode
        $state.TrustedPublisher = $props.TrustedPublisher
        if ($props.Authenticode -eq $CertThumbprint -and $props.TrustedPublisher -eq 1) { $state.MatchesCert = $true }
    }
    return $state
}

function Show-CertStatus {
    Write-Output '==== 本机 CN=EmbyNian 证书 ===='
    $certs = @(Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue | Where-Object { $_.Subject -eq 'CN=EmbyNian' })
    if ($certs.Count -eq 0) {
        Write-Output '  用户证书库里没有。（-Setup 会在那里建一张）'
    } else {
        foreach ($c in $certs) {
            $eku = ($c.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' } | ForEach-Object { $_.Format($false) }) -join ' '
            $ekuOk = $eku -match '1\.3\.6\.1\.5\.5\.7\.3\.3' -or $eku -match '代码签名'
            Write-Output ("  指纹 {0}" -f $c.Thumbprint)
            Write-Output ("    有效期 {0:yyyy-MM-dd} → {1:yyyy-MM-dd}   私钥={2}   代码签名用途={3}" -f $c.NotBefore, $c.NotAfter, $c.HasPrivateKey, $ekuOk)
        }
    }

    Write-Output '==== 受信任存储里有没有它 ===='
    foreach ($store in @('Cert:\CurrentUser\Root', 'Cert:\LocalMachine\Root', 'Cert:\LocalMachine\TrustedPeople')) {
        $hit = Get-ChildItem $store -ErrorAction SilentlyContinue | Where-Object { $_.Subject -eq 'CN=EmbyNian' }
        if ($hit) { Write-Output ("  有：{0}" -f $store) } else { Write-Output ("  无：{0}" -f $store) }
    }

    Write-Output '==== 链能不能建起来（能不能建起来决定 SmartScreen 认不认）===='
    # 只用 5.1 上确实存在的 API：X509ChainPolicy.TrustMode 是 .NET Core 才加的，
    # 在 Windows PowerShell 里取它就是「找不到属性 TrustMode」—— 本脚本第一版就死在这里。
    $cert = Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue | Where-Object { $_.Subject -eq 'CN=EmbyNian' -and $_.HasPrivateKey } | Select-Object -First 1
    if ($cert) {
        $chain = New-Object System.Security.Cryptography.X509Certificates.X509Chain
        $chain.ChainPolicy.RevocationMode = 'NoCheck'
        $ok = $chain.Build($cert)
        Write-Output ("  建链结果: {0}" -f $ok)
        foreach ($el in $chain.ChainElements) {
            Write-Output ("    {0}   状态 {1}" -f $el.Certificate.Subject, $el.ChainElementStatus.Status)
        }
        if (-not $ok) {
            Write-Output '  链没建起来 → 双击时 SmartScreen 仍会去问微软。装证书那一步（-Setup）就是修这个。'
        } else {
            Write-Output '  链建起来了 → 在这台机器上，签名会被当作可信发布者。'
        }

        Write-Output '==== 「受信任发布者」登记（SmartScreen 拦不拦的最后一道）===='
        $tp = Get-TrustPublisherState -CertThumbprint $cert.Thumbprint
        if (-not $tp.PolicyKeyExists) {
            Write-Output '  策略键不存在 → 还没登记过。要登记，用管理员终端跑：'
            Write-Output '      powershell -NoProfile -ExecutionPolicy Bypass -File tools\sign-release.ps1 -TrustPublisher'
        } else {
            Write-Output ("  策略键存在，DefaultLevel = {0}" -f $tp.DefaultLevel)
            Write-Output ("  Authenticode 规则开关（262144\authenticodeenabled）= {0}" -f $(if ($null -eq $tp.AuthenticodeEnabled) { '（无此值 → 规则不会被读）' } else { $tp.AuthenticodeEnabled }))
            if ($tp.MatchesCert -and $tp.AuthenticodeEnabled -eq 1) {
                Write-Output ("  已登记本证书：Authenticode = {0}，TrustedPublisher = 1" -f $tp.Authenticode)
                Write-Output '  ！2026-10-01 实测：这条登记**消不掉** SmartScreen 的「无法识别的应用」提示（它管软件限制策略，不是那个开关）。别把它当解法。'
            } elseif ($tp.EntryExists) {
                Write-Output ("  有一条同指纹的登记，但值不对：Authenticode={0} TrustedPublisher={1}（期望 {2} 与 1）" -f $tp.Authenticode, $tp.TrustedPublisher, $cert.Thumbprint)
            } else {
                Write-Output ("  策略键在，但没有本证书（{0}）的登记条目 → 用 -TrustPublisher 补上（但别指望它消提示）。" -f $cert.Thumbprint)
            }
        }
    }

    Write-Output '==== SmartScreen「检查应用和文件」===='
    $appHost = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppHost'
    $userValue = (Get-ItemProperty -LiteralPath $appHost -ErrorAction SilentlyContinue).EnableWebContentEvaluation
    $machineValue = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppHost' -ErrorAction SilentlyContinue).EnableWebContentEvaluation
    Write-Output ("  AppHost 两处（对本框无效）：HKCU = {0}   HKLM = {1}" -f $(if ($null -eq $userValue) { '（无此值）' } else { $userValue }), $(if ($null -eq $machineValue) { '（无此值）' } else { $machineValue }))
    # ！2026-10-01 续七定论：这两处 **不是** 资源管理器那个框读的开关——续五/续六都是在这两处
    # 为 0 的状态下照样拦；写 Explorer\SmartScreenEnabled = Off 后同一个探针就不弹了。
    # -SetAppCheck 保留只为读写这两处的人复核，别再把它当本问题的开关。
    $shellValue = (Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer' -ErrorAction SilentlyContinue).SmartScreenEnabled
    Write-Output ("  Explorer HKCU SmartScreenEnabled = {0}（SmartScreen 层；本案根因是目录完整性标签，用 icacls 查）" -f $(if ($null -eq $shellValue) { '（无此值 → 出厂默认，开启）' } else { $shellValue }))
    if ($null -eq $shellValue) {
        Write-Output '  → 出厂默认。要临时关：-SetShellSmartScreen Off（关防护，须用户同意）。'
    } else {
        Write-Output '  → 非默认状态。恢复出厂（删值）：-SetShellSmartScreen On。'
    }
}

if ($Setup) {
    if (-not (Test-Admin)) {
        throw '建证书并装进本机受信任存储需要管理员权限。请用「以管理员身份运行」的 PowerShell 重跑这条命令。'
    }

    Write-Output '==== 建一张代码签名证书（CN=EmbyNian）===='
    $new = New-SelfSignedCertificate `
        -Subject 'CN=EmbyNian' `
        -Type CodeSigningCert `
        -KeyUsage DigitalSignature `
        -KeyAlgorithm RSA `
        -KeyLength 3072 `
        -HashAlgorithm SHA256 `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -NotAfter (Get-Date).AddYears(3) `
        -TextExtension @('2.5.29.19={text}CA=true&pathlength=0')
    Write-Output ("  指纹：{0}" -f $new.Thumbprint)
    Write-Output ("  有效期到：{0:yyyy-MM-dd}" -f $new.NotAfter)

    Write-Output '==== 导出带私钥的 pfx（artifacts 已被 git 忽略，私钥不进仓库）===='
    New-Item -ItemType Directory -Path (Split-Path -Parent $pfxPath) -Force | Out-Null
    $pwd = ConvertTo-SecureString -String 'embynian' -Force -AsPlainText
    Export-PfxCertificate -Cert $new -FilePath $pfxPath -Password $pwd | Out-Null
    Write-Output ("  已导出：{0}" -f $pfxPath)

    Write-Output '==== 装进本机受信任存储（这一步才是让 SmartScreen 不再问的关键）===='
    $pub = Join-Path $env:TEMP 'EmbyNian-code-signing.cer'
    Export-Certificate -Cert $new -FilePath $pub -Force | Out-Null
    Import-Certificate -FilePath $pub -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
    Import-Certificate -FilePath $pub -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
    Remove-Item -LiteralPath $pub -Force
    Write-Output '  已装进 LocalMachine\Root 与 LocalMachine\TrustedPeople。'

    Write-Output ''
    Write-Output '下一步（普通终端）：'
    Write-Output ("    powershell -NoProfile -ExecutionPolicy Bypass -File tools\sign-release.ps1 -Path artifacts\publish\win-x64\EmbyNian.exe")
    return
}

if ($SetShellSmartScreen) {
    $explorerKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer'
    $before = (Get-ItemProperty -LiteralPath $explorerKey -ErrorAction SilentlyContinue).SmartScreenEnabled
    if ($SetShellSmartScreen -eq 'Off') {
        New-ItemProperty -LiteralPath $explorerKey -Name 'SmartScreenEnabled' -PropertyType String -Value 'Off' -Force | Out-Null
    } else {
        # 恢复 = 删值：本机出厂状态就是没有这个值，没有 = 默认开启，比写 'On' 更干净。
        Remove-ItemProperty -LiteralPath $explorerKey -Name 'SmartScreenEnabled' -ErrorAction SilentlyContinue
    }
    $after = (Get-ItemProperty -LiteralPath $explorerKey -ErrorAction SilentlyContinue).SmartScreenEnabled
    $want = if ($SetShellSmartScreen -eq 'Off') { 'Off' } else { '（无此值）' }
    $beforeShow = if ($null -eq $before) { '（原本无此值）' } else { $before }
    $afterShow = if ($null -eq $after) { '（无此值）' } else { $after }
    Write-Output ("HKCU Explorer\SmartScreenEnabled：{0} → {1}（期望 {2}）" -f $beforeShow, $afterShow, $want)
    if ("$afterShow" -eq "$want") {
        if ($SetShellSmartScreen -eq 'Off') {
            Write-Output '已关闭资源管理器信誉检查：双击未识别程序不再弹「Windows 已保护你的电脑」。这是关防护（影响所有程序）；恢复默认用 -SetShellSmartScreen On。'
        } else {
            Write-Output '已恢复出厂默认（值不存在 = 信誉检查开启）。'
        }
    } else {
        Write-Output '结果与期望不符，没有生效。'
    }
    return
}

if ($SetAppCheck) {
    if (-not (Test-Admin)) {
        throw "改动「检查应用和文件」要写另一个用户的 hive，需要管理员权限。请用「以管理员身份运行」的 PowerShell 重跑。`n提示：提权后 HKCU 指向管理员账户，所以本脚本故意按调用者的 SID 定位 HKEY_USERS\<SID>。"
    }
    # 关键：提权进程里的 HKCU 是管理员自己的，不是调用者的。用调用者的 SID 显式定位。
    # 同一账户 UAC 提权时 SID 不变（管理员组成员的令牌是分裂的，SID 仍是本人的），
    # 所以这里取到的就是「当前登录用户」的 hive。用别的管理员账户提权时要改用 -UserSid 形式，
    # 那种情况下这里会写错 hive —— 脚本先把 SID 打出来，方便核对。
    $sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    Write-Output ("执行身份：{0}" -f [System.Security.Principal.WindowsIdentity]::GetCurrent().Name)
    $userKey = "HKEY_USERS\$sid\SOFTWARE\Microsoft\Windows\CurrentVersion\AppHost"
    $machineKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppHost'
    $value = if ($SetAppCheck -eq 'Off') { 0 } else { 1 }

    # 两处都要写：实测只写用户级不起作用，HKLM 那份才是生效的。
    $results = @()
    foreach ($kv in @(@{ Name = '用户级'; Path = "Registry::$userKey" }, @{ Name = '系统级'; Path = $machineKey })) {
        if (-not (Test-Path -LiteralPath $kv.Path)) { New-Item -Path $kv.Path -Force | Out-Null }
        $before = (Get-ItemProperty -LiteralPath $kv.Path -ErrorAction SilentlyContinue).EnableWebContentEvaluation
        New-ItemProperty -LiteralPath $kv.Path -Name 'EnableWebContentEvaluation' -PropertyType DWord -Value $value -Force | Out-Null
        $after = (Get-ItemProperty -LiteralPath $kv.Path).EnableWebContentEvaluation
        $results += [pscustomobject]@{ Name = $kv.Name; Path = $kv.Path; Before = $before; After = $after; Ok = ($after -eq $value) }
    }

    foreach ($r in $results) {
        Write-Output ("{0}：{1} → {2}（期望 {3}）{4}" -f $r.Name, $(if ($null -eq $r.Before) { '（原本无此值）' } else { $r.Before }), $r.After, $value, $(if ($r.Ok) { '' } else { '  ← 不符' }))
    }
    if (@($results | Where-Object { -not $_.Ok }).Count -eq 0) {
        if ($value -eq 0) {
            Write-Output '已关掉两处：双击未签名程序不再被拦（资源管理器已在运行的话，重启它或注销一次更保险）。要恢复就跑 -SetAppCheck On。'
        } else {
            Write-Output '已恢复两处默认：双击未签名程序会再被拦一次。'
        }
    } else {
        Write-Output '有位置没写成功，没生效。'
    }
    return
}

if ($TrustPublisher) {
    if (-not (Test-Admin)) {
        throw '登记「受信任的发布者」要写 HKLM，需要管理员权限。请用「以管理员身份运行」的 PowerShell 重跑。'
    }
    $cert = Get-EmbyNianCert -WantThumbprint $Thumbprint
    $key = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Safer\CodeIdentifiers\262144\Paths'
    New-Item -Path $key -Force | Out-Null
    # 条目名是证书指纹；Authenticode 说的是「按证书信任」，TrustedPublisher 说的是「它是可信发布者」。
    $entry = Join-Path $key $cert.Thumbprint
    New-Item -Path $entry -Force | Out-Null
    New-ItemProperty -Path $entry -Name 'Authenticode' -PropertyType String -Value $cert.Thumbprint -Force | Out-Null
    New-ItemProperty -Path $entry -Name 'TrustedPublisher' -PropertyType DWord -Value 1 -Force | Out-Null
    New-ItemProperty -Path $entry -Name 'SaferFlags' -PropertyType DWord -Value 0 -Force | Out-Null
    New-ItemProperty -Path $entry -Name 'ItemData' -PropertyType String -Value '' -Force | Out-Null
    New-ItemProperty -Path $entry -Name 'Description' -PropertyType String -Value 'EmbyNian（本机自签发布者）' -Force | Out-Null
    New-ItemProperty -Path $entry -Name 'LastModified' -PropertyType DWord -Value ([int][double]::Parse((Get-Date -UFormat %s))) -Force | Out-Null

    # ！这一步不能省：**光有 Paths 里的条目，规则不会被读**。
    # 2026-10-01 在本机读到 CodeIdentifiers 根上 authenticodeenabled = 0 —— Authenticode 发布者
    # 规则是关着的，那种状态下加条目等于没加（用户如果先跑了不带这一步的版本，会白跑一趟）。
    # 软管理员策略（262144）自己那份键上要写 authenticodeenabled = 1，规则才参与判定。
    $softKey = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Safer\CodeIdentifiers\262144'
    New-ItemProperty -Path $softKey -Name 'authenticodeenabled' -PropertyType DWord -Value 1 -Force | Out-Null
    # 条目时间戳之外，262144 也要有自己的 ItemData/LastModified，GUI（secpol）读得才顺。
    New-ItemProperty -Path $softKey -Name 'ItemData' -PropertyType String -Value '' -Force | Out-Null
    New-ItemProperty -Path $softKey -Name 'LastModified' -PropertyType DWord -Value ([int][double]::Parse((Get-Date -UFormat %s))) -Force | Out-Null

    # 根上的 DefaultLevel 只在**不存在**时补一个「不受限」，不覆盖已有策略：
    # 它决定 SRP 是拦还是放，动错了会把机器变成"只许白名单运行"，那是灾难。
    $rootKey = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Safer\CodeIdentifiers'
    $existingLevel = (Get-ItemProperty -LiteralPath $rootKey -ErrorAction SilentlyContinue).DefaultLevel
    if ($null -eq $existingLevel) {
        New-ItemProperty -Path $rootKey -Name 'DefaultLevel' -PropertyType DWord -Value 262144 -Force | Out-Null
        Write-Output '  根上原来没有 DefaultLevel，已补 262144（不受限，不启用白名单）。'
    } else {
        Write-Output ("  根上已有 DefaultLevel = {0}，保持不动。" -f $existingLevel)
    }

    Write-Output ("已登记受信任发布者：{0}" -f $cert.Thumbprint)
    Write-Output ("  注册表位置：{0}" -f $entry)
    Write-Output ("  Authenticode 规则开关：{0}\authenticodeenabled = 1" -f $softKey)

    # 写完立刻读回来验证，别只看命令有没有报错。
    $verify = Get-TrustPublisherState -CertThumbprint $cert.Thumbprint
    if ($verify.MatchesCert -and $verify.AuthenticodeEnabled -eq 1) {
        Write-Output '  读回验证：登记与 Authenticode 开关都已生效。'
    } else {
        Write-Output ("  读回验证不完整：Authenticode={0} TrustedPublisher={1} authenticodeenabled={2}" -f $verify.Authenticode, $verify.TrustedPublisher, $verify.AuthenticodeEnabled)
    }
    Write-Output '  现在双击那些用它签过的程序，SmartScreen 应当不再拦。若仍拦，那就只剩关掉「检查应用和文件」这一条路。'
    return
}

if ($Status -or -not $Path) {
    Show-CertStatus
    if (-not $Path) { return }
}

# 收集要签的文件
$targets = [System.Collections.Generic.List[string]]::new()
foreach ($p in $Path) {
    $full = if ([System.IO.Path]::IsPathRooted($p)) { $p } else { Join-Path $repo $p }
    if (-not (Test-Path -LiteralPath $full)) { throw "找不到：$full" }
    if ((Get-Item -LiteralPath $full) -is [System.IO.DirectoryInfo]) {
        Get-ChildItem -LiteralPath $full -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Extension -in '.exe', '.dll', '.msix', '.appx' } |
            ForEach-Object { $targets.Add($_.FullName) }
    } else {
        $targets.Add((Get-Item -LiteralPath $full).FullName)
    }
}
if ($targets.Count -eq 0) { throw '没找到可签的文件。' }

$cert = Get-EmbyNianCert -WantThumbprint $Thumbprint
$signtool = Get-SignTool

Write-Output ("用证书 {0}（到 {1:yyyy-MM-dd}）签 {2} 个文件" -f $cert.Thumbprint, $cert.NotAfter, $targets.Count)
Write-Output ("signtool: {0}" -f $signtool)
Write-Output ''

$ok = 0
$failed = 0
foreach ($t in $targets) {
    $args = @('sign', '/v', '/fd', 'SHA256', '/sha1', $cert.Thumbprint, '/s', 'My')
    if ($Timestamp) { $args += @('/tr', $Timestamp, '/td', 'SHA256') }
    $args += $t
    & $signtool @args 2>&1 | Where-Object { $_ -match 'Successfully signed|Error|错误|Done' } | ForEach-Object { "    $_" }
    if ($LASTEXITCODE -eq 0) { $ok++ } else { $failed++; Write-Output ("  签名失败：{0}（退出码 {1}）" -f $t, $LASTEXITCODE) }
}

Write-Output ''
Write-Output ("签名完成：成功 {0}，失败 {1}" -f $ok, $failed)
Write-Output ''
Write-Output '==== 复核（签名状态与链）===='
foreach ($t in $targets) {
    $sig = Get-AuthenticodeSignature -LiteralPath $t
    Write-Output ("  {0}`n    状态 {1}   签名者 {2}" -f $t, $sig.Status, $(if ($sig.SignerCertificate) { $sig.SignerCertificate.Subject } else { '（无）' }))
}

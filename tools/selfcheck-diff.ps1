# 闸门 4 的判据，机械版。
#
# 这套流程原来是人肉的：跑一次自检、打开日志、把「每次都会变的十行」按名单划掉、窗口最大化时还要手动把
# settings.json 里的 WindowMaximized 改了再改回来。一份要靠经验读的日志，退化以后没人看得出退化 —— 所以
# 这里只比「哪些检查跑了、各答了什么」：详情里的数字天生每次都变（见 docs\开发与验证.md 的「逐行比报告：
# 每次都会变的行」），不参与比对，剩下的就是是/否。
#
#   tools\selfcheck-diff.ps1                  跑一次自检再比（默认 artifacts\publish\win-x64\EmbyNian.exe）
#   tools\selfcheck-diff.ps1 -Log <文件>      只比已有日志，不启动任何东西（程序开着时也能用）
#   tools\selfcheck-diff.ps1 -UpdateBaseline  把这一次的结果认成基线（确认过再认）
#
# 红的三种情形：本次有 [失败]（有就是红，与基线无关）；基线里有的检查这次不见了；基线里「通过」的这次成了
# 「信息」或「跳过」（降级 —— 多半是没登录、服务器上没有可走的条目，正是「看着绿、其实没查」那种）。
# 新增检查只报不红。
[CmdletBinding()]
param(
    [string]$Exe,

    [string]$Log,

    [string]$Baseline,

    [switch]$UpdateBaseline
)

$ErrorActionPreference = 'Stop'

# 失败行的详情可以长到几百字（跨季那一行就是），报告里截一下，日志原样不动。
function Shorten([string]$Text, [int]$Limit = 220) {
    if (-not $Text) { return '' }
    if ($Text.Length -le $Limit) { return $Text }
    return $Text.Substring(0, $Limit) + '…'
}

$repo = Split-Path -Parent $PSScriptRoot
if (-not $Baseline) { $Baseline = Join-Path $repo 'docs\selfcheck-baseline.txt' }
if (-not $Exe) { $Exe = Join-Path $repo 'artifacts\publish\win-x64\EmbyNian.exe' }

$settings = Join-Path $env:LOCALAPPDATA 'EmbyNian\settings.json'
$settingsBackup = "$settings.selfcheck-backup"
$maximizedWasRestored = $false

try {
    if ($Log) {
        $logPath = (Resolve-Path -LiteralPath $Log).Path
    }
    else {
        if (-not (Test-Path -LiteralPath $Exe)) {
            throw "找不到自检用的 exe：$Exe —— 先跑 tools\publish.ps1 -NoArchive。"
        }

        if (Get-Process -Name 'EmbyNian' -ErrorAction SilentlyContinue) {
            Write-Output '提示：EmbyNian 正在跑。自检自己开一个实例、不抢单实例锁，可以照跑，只是它会在副屏上动窗口。'
        }

        # 最大化时有三行必红，那三条没有一行是回归（拿存储尺寸比最大化的客户区）。原来是他自己改
        # settings.json 再改回来，这里替他做一次，并且一定还原，不去动他的设置。
        if (Test-Path -LiteralPath $settings) {
            $json = Get-Content -LiteralPath $settings -Raw -Encoding UTF8
            if ($json -match '"WindowMaximized"\s*:\s*true') {
                Copy-Item -LiteralPath $settings -Destination $settingsBackup -Force
                [System.IO.File]::WriteAllText($settings, ($json -replace '"WindowMaximized"\s*:\s*true', '"WindowMaximized": false'))
                $maximizedWasRestored = $true
                Write-Output '窗口当时是最大化：临时按非最大化跑一次，跑完还原设置。'
            }
        }

        $logPath = Join-Path $env:LOCALAPPDATA 'EmbyNian\logs\selfcheck-shell.txt'
        if (Test-Path -LiteralPath $logPath) { Remove-Item -LiteralPath $logPath -Force }

        & $Exe --self-check --dump-ui | Out-Null
        Write-Output "自检退出码 $LASTEXITCODE（判据不看它，看日志）"
    }

    if (-not (Test-Path -LiteralPath $logPath)) { throw "没有自检日志：$logPath" }

    # 日志是 UTF-8 无 BOM，Windows PowerShell 默认按本地代码页读它 —— 不写 -Encoding UTF8 的话
    # 「通过」「失败」和那个长破折号全是乱码，正则一条都匹配不上，脚本会安静地报「全部一致」。
    $text = Get-Content -LiteralPath $logPath -Raw -Encoding UTF8

    $statusOf = @{}
    $detailOf = @{}
    $order = @()
    foreach ($line in ($text -split "`r?`n")) {
        if ($line -notmatch '^\[(通过|信息|失败|跳过)\](.+)$') { continue }

        $status = $Matches[1]
        $body = $Matches[2].Trim()
        $name = ($body -split ' — ')[0].Trim()
        if (-not $name) { continue }

        if (-not $statusOf.ContainsKey($name)) { $statusOf[$name] = $status; $order += $name }
        if (-not $detailOf.ContainsKey($name)) { $detailOf[$name] = $body }
    }

    if ($statusOf.Count -eq 0) { throw "日志里一行检查都没有：$logPath（没写完整？换一份日志或重跑）" }

    $resultLine = @($text -split "`r?`n" | Where-Object { $_ -match '^结果：' } | Select-Object -Last 1)
    $passed = @($order | Where-Object { $statusOf[$_] -eq '通过' }).Count
    $informed = @($order | Where-Object { $statusOf[$_] -eq '信息' }).Count
    $skipped = @($order | Where-Object { $statusOf[$_] -eq '跳过' }).Count
    $failed = @($order | Where-Object { $statusOf[$_] -eq '失败' })

    # 基线只存「状态 + 检查名」。同名多行的那些检查（设置卡片）在这里只有一份 —— 逐卡的行数由那一行自己的读数盯着。
    $baselineEntries = @()
    if (Test-Path -LiteralPath $Baseline) {
        $baselineEntries = @(Get-Content -LiteralPath $Baseline -Encoding UTF8 |
            Where-Object { $_ -match '\S' -and -not $_.TrimStart().StartsWith('#') })
    }

    $gone = @()
    $downgraded = @()
    $known = @{}
    foreach ($entry in $baselineEntries) {
        if ($entry -notmatch '^(通过|信息|跳过)  (.+)$') { continue }

        $name = $Matches[2].Trim()
        $known[$name] = $true
        $was = $Matches[1]

        if (-not $statusOf.ContainsKey($name)) {
            $gone += "$name（基线是 $was）"
        }
        elseif ($was -eq '通过' -and $statusOf[$name] -ne '通过') {
            $downgraded += "$name（通过 → $($statusOf[$name])）"
        }
    }

    # 正在失败的那些不算「新增」：它们已经在上面的 [失败] 里报过了。
    $added = @($order | Where-Object { -not $known.ContainsKey($_) -and $statusOf[$_] -ne '失败' } | Sort-Object)

    if ($UpdateBaseline) {
        $lines = @('# EmbyNian 自检基线 —— tools/selfcheck-diff.ps1 用，判据在那个脚本的头部注释与 CLAUDE.md 的闸门 4。')
        $lines += '# 只有「状态 + 检查名」：详情里的数字每次都会变（docs/开发与验证.md「逐行比报告：每次都会变的行」），不参与比对。'
        $lines += '# 同名多行的检查（设置卡片）在这里只有一份 —— 逐卡的行数由那一行自己的读数盯着。'
        $lines += '# 失败的行不写进来：有失败就是红的，与它无关。'
        $lines += @($order | Where-Object { $statusOf[$_] -ne '失败' } | ForEach-Object { "$($statusOf[$_])  $_" } | Sort-Object)
        # 手写 \n：WriteAllLines 用的是 Environment.NewLine（Windows 上是 CRLF），而仓库里的文本一律 LF。
        [System.IO.File]::WriteAllText($Baseline, ($lines -join "`n") + "`n", (New-Object System.Text.UTF8Encoding($false)))
        Write-Output "基线已更新：$Baseline（$($lines.Count - 4) 项）"
    }

    $red = ($failed.Count -gt 0) -or ($gone.Count -gt 0) -or ($downgraded.Count -gt 0)
    $verdict = if ($red) { '红' } else { '绿' }

    Write-Output ''
    Write-Output "闸门 4：$verdict ｜ 通过 $passed、信息 $informed、跳过 $skipped、失败 $($failed.Count)；与基线比：消失 $($gone.Count)、降级 $($downgraded.Count)、新增 $($added.Count)"
    if ($resultLine) { Write-Output "日志自己的结论：$resultLine" }

    foreach ($name in $failed) { Write-Output "  [失败] $(Shorten $detailOf[$name])" }
    foreach ($item in $gone) { Write-Output "  [消失] $item" }
    foreach ($item in $downgraded) { Write-Output "  [降级] $item" }
    if ($added.Count -gt 0) {
        $shown = @($added | Select-Object -First 8)
        $tail = if ($added.Count -gt $shown.Count) { " 等 $($added.Count) 项" } else { '' }
        $hint = if ($baselineEntries.Count -eq 0) { '还没有基线：确认这次结果之后用 -UpdateBaseline 认下来' } else { '新检查不算红，认下来就 -UpdateBaseline' }
        Write-Output "  [新增] $($shown -join '、')$tail —— $hint"
    }

    if ($red) { exit 1 }
}
finally {
    if ($maximizedWasRestored -and (Test-Path -LiteralPath $settingsBackup)) {
        Move-Item -LiteralPath $settingsBackup -Destination $settings -Force
        Write-Output '已把 WindowMaximized 还原。'
    }
}

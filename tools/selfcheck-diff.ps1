# 闸门 4：只比较本次确定的完整报告；任何失败、缺项、降级都判红。
# -Log <文件> 只读已有报告，不代表当前构建运行过。
# 默认运行当前树发布件。自检用全新 artifacts/selfcheck/run-<随机值>，只复制已保存设置，
# 不修改/恢复生产设置，不停用户进程。自检仍可登录并读取保存的真实媒体库，不是离线探针。
# -UpdateBaseline 只接纳通过复核的新增检查；有失败/消失/降级不能整份更新。
# 合法改名/合并/退役按 CLAUDE.md 复核后逐行修改基线，再运行本脚本。
[CmdletBinding()]
param(
    [string]$Exe,
    [string]$Log,
    [string]$Baseline,
    [switch]$UpdateBaseline
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Baseline) { $Baseline = Join-Path $repo 'docs\selfcheck-baseline.txt' }
if (-not $Exe) { $Exe = Join-Path $repo 'artifacts\publish\win-x64\EmbyNian.exe' }

function Shorten([string]$Text) {
    if ($Text.Length -le 220) { return $Text }
    return $Text.Substring(0, 220) + '…'
}

$processExitCode = 0
try {
    # A missing/empty/malformed baseline is not "zero regressions", even with UpdateBaseline.
    if (-not (Test-Path -LiteralPath $Baseline -PathType Leaf)) { throw '基线不存在，拒绝比较。' }
    $known = @{}
    foreach ($line in (Get-Content -LiteralPath $Baseline -Encoding UTF8)) {
        if (-not $line.Trim() -or $line.TrimStart().StartsWith('#')) { continue }
        if ($line -notmatch '^(通过|信息|跳过)  (\S.*)$') { throw '基线含无效行，须逐行复核。' }
        $name = $Matches[2].Trim()
        if ($known.ContainsKey($name)) { throw "基线检查重名：$name" }
        $known[$name] = $Matches[1]
    }
    if ($known.Count -eq 0) { throw '基线为空，拒绝比较。' }

    if ($Log) {
        $logPath = (Resolve-Path -LiteralPath $Log).Path
        Write-Output '仅比较已有日志；未运行当前构建。'
    }
    else {
        $exePath = (Resolve-Path -LiteralPath $Exe).Path
        if ([IO.Path]::GetExtension($exePath) -ne '.exe') { throw '自检入口必须是应用 exe。' }
        # Do NOT probe an old exe with an unknown switch: that could open the production profile.
        # The managed assembly has this AssemblyMetadata custom-attribute blob. Inspect it without
        # loading/running code (Windows PowerShell cannot reflection-load a .NET 10 assembly).
        $assemblyPath = [IO.Path]::ChangeExtension($exePath, '.dll')
        if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) { throw '缺少带隔离协议声明的应用程序集；未启动。' }
        $key = 'EmbyNian.SelfCheckIsolation'
        $blob = [string][char]1 + [char]0 + [char]$key.Length + $key + [char]1 + '1' + [char]0 + [char]0
        $assemblyText = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($assemblyPath))
        if (-not $assemblyText.Contains($blob)) { throw '该构建未声明自检隔离协议 1；拒绝启动旧 exe。' }

        $runParent = Join-Path $repo 'artifacts\selfcheck'
        [IO.Directory]::CreateDirectory($runParent) | Out-Null
        $runRoot = Join-Path $runParent ('run-' + [Guid]::NewGuid().ToString('N'))
        # Parent exists; target deliberately does not. The app owns exclusive creation and validation.
        Write-Output "自检程序：$exePath"
        Write-Output "独立运行目录：$runRoot（生产设置不改动）"
        $process = Start-Process -FilePath $exePath -WorkingDirectory (Split-Path $exePath) -ArgumentList @(
            '--self-check', '--dump-ui', '--self-check-data', ('"' + $runRoot + '"')
        ) -Wait -PassThru
        $processExitCode = $process.ExitCode
        Write-Output "自检进程退出码：$processExitCode"
        $receiptPath = Join-Path $runRoot 'logs\selfcheck-isolation.txt'
        if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) { throw '缺少本次隔离回执，不能将忽略新参数的 exe 当作正常运行。' }
        $receipt = @(Get-Content -LiteralPath $receiptPath -Encoding UTF8)
        if ($receipt.Count -ne 2 -or $receipt[0] -cne 'EmbyNian.SelfCheckIsolation=1' -or
            -not [string]::Equals($receipt[1], $runRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw '隔离回执不属于本次确定目录。'
        }
        $logPath = Join-Path $runRoot 'logs\selfcheck-shell.txt'
    }

    if (-not (Test-Path -LiteralPath $logPath -PathType Leaf)) { throw "没有本次自检日志：$logPath" }
    $text = Get-Content -LiteralPath $logPath -Raw -Encoding UTF8
    if (-not $text) { throw '报告为空。' }
    $lines = @($text -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 })
    $results = @($lines | Where-Object { $_ -match '^结果：' })
    if ($results.Count -ne 1 -or $lines[-1] -cne $results[0] -or
        $results[0] -notmatch '^结果：(全部通过|[1-9][0-9]* 项失败)$') {
        throw '报告不完整：需要唯一的完整结论，且结论必须是最后一行。'
    }

    # A duplicate name is represented by its worst status, never by whichever line appeared first.
    $rank = @{ '通过' = 0; '信息' = 1; '跳过' = 2; '失败' = 3 }
    $statusOf = @{}
    $detailOf = @{}
    $failureRows = 0
    foreach ($line in $lines) {
        if ($line -notmatch '^\[(通过|信息|失败|跳过)\](.*)$') { continue }
        $status = $Matches[1]
        $body = $Matches[2].Trim()
        $name = ($body -split ' — ', 2)[0].Trim()
        if (-not $name) { throw '报告含没有检查名称的状态行。' }
        if ($status -eq '失败') { $failureRows++ }
        if (-not $statusOf.ContainsKey($name) -or $rank[$status] -gt $rank[$statusOf[$name]]) {
            $statusOf[$name] = $status
            $detailOf[$name] = $body
        }
    }
    if ($statusOf.Count -eq 0) { throw '报告中没有任何检查。' }
    $summaryFailures = 0
    if ($results[0] -match '^结果：([1-9][0-9]*) 项失败$') { $summaryFailures = [int]$Matches[1] }
    if ($summaryFailures -ne $failureRows) { throw '报告结论与逐行失败数量不一致。' }

    $gone = @($known.Keys | Where-Object { -not $statusOf.ContainsKey($_) } | Sort-Object)
    $downgraded = @($known.Keys | Where-Object {
        $known[$_] -eq '通过' -and $statusOf.ContainsKey($_) -and $statusOf[$_] -ne '通过'
    } | Sort-Object)
    $failed = @($statusOf.Keys | Where-Object { $statusOf[$_] -eq '失败' } | Sort-Object)
    # 失败的检查按基线约定本来就不登记，再报一次「新增」只是重复；它已经在失败行里，也进不了 -UpdateBaseline。
    $added = @($statusOf.Keys | Where-Object { -not $known.ContainsKey($_) -and $statusOf[$_] -ne '失败' } | Sort-Object)
    $red = $processExitCode -ne 0 -or $failureRows -gt 0 -or $gone.Count -gt 0 -or $downgraded.Count -gt 0
    $verdict = if ($red) { '红' } else { '绿' }
    Write-Output "闸门 4：$verdict ｜ 检查 $($statusOf.Count)、失败行 $failureRows；消失 $($gone.Count)、降级 $($downgraded.Count)、新增 $($added.Count)"
    Write-Output "日志：$logPath"
    foreach ($name in $failed) { Write-Output "  [失败] $(Shorten $detailOf[$name])" }
    foreach ($name in $gone) { Write-Output "  [消失] $name" }
    foreach ($name in $downgraded) { Write-Output "  [降级] $name（通过 → $($statusOf[$name])）" }
    foreach ($name in $added) { Write-Output "  [新增] $name" }

    if ($UpdateBaseline) {
        if ($red) { throw '本次有失败、消失或降级，拒绝更新基线；合法退役须明确复核后逐行修改。' }
        # Append only reviewed additions. Preserve existing comments and entries, not a fresh snapshot.
        if ($added.Count -gt 0) {
            $baselineText = (Get-Content -LiteralPath $Baseline -Raw -Encoding UTF8).Replace("`r`n", "`n").TrimEnd()
            $newLines = @($added | ForEach-Object { "$($statusOf[$_])  $_" })
            [IO.File]::WriteAllText($Baseline, $baselineText + "`n" + ($newLines -join "`n") + "`n", (New-Object Text.UTF8Encoding($false)))
        }
        Write-Output "基线仅追加已复核的新检查：$Baseline"
    }
    if ($red) { exit 1 }
    exit 0
}
catch {
    Write-Output "闸门 4：红 ｜ $($_.Exception.Message)"
    exit 1
}

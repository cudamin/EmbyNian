# Microsoft.WindowsAppSDK.Analyzers 的副本维护。
#
# Directory.Build.props 引的是仓库里这一份手工拷贝（tools\analyzers），而且是 Condition=Exists —— 干净检出
# 没有技能目录也能构建，代价是**少了它构建照样通过、闸门 1 照样报「0 警告 0 错误」**，而其实一条 WUI 规则都
# 没跑。和已经修过的 PRI 事故同一类：闸门是绿的，但它没在查。
#
#   tools\refresh-analyzers.ps1          把技能里的两份拷过来，并打印新哈希
#   tools\refresh-analyzers.ps1 -Check   只比本机候选与仓库载荷，供独立升级评估，不是发版必过项
#
# 拷完把哈希写进 tests\EmbyNian.Tests\Agreements.txt 的 analyzer 行 —— 那里有一条测试会先红给你看
# （AgreementsTests「tools/analyzers 那份副本还在、没被换过」）。
[CmdletBinding()]
param(
    # 只比不动：技能里那份与本仓库这份不一致时退出码 1。
    [switch]$Check,

    # 技能里的 analyzer 目录；默认按插件文件写时间选候选，不保证语义版本最新。
    [string]$SkillRoot
)

$ErrorActionPreference = 'Stop'

$target = Join-Path $PSScriptRoot 'analyzers'
$names = @('Microsoft.WindowsAppSDK.Analyzers.dll', 'Microsoft.WindowsAppSDK.Analyzers.targets')

function Find-SkillAnalyzer {
    if ($SkillRoot) { return (Resolve-Path -LiteralPath $SkillRoot).Path }

    $cache = Join-Path $env:USERPROFILE '.claude\plugins\cache'
    if (-not (Test-Path -LiteralPath $cache)) { return $null }

    # 技能更新会换掉整个版本目录，所以按写时间取最新的那一份，而不是钉死某个版本号。
    $hits = @(Get-ChildItem -LiteralPath $cache -Recurse -Filter 'Microsoft.WindowsAppSDK.Analyzers.dll' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.DirectoryName -match 'winui-dev-workflow[\\/]analyzer$' } |
        Sort-Object -Property LastWriteTime -Descending)

    if ($hits.Count -eq 0) { return $null }
    return $hits[0].DirectoryName
}

$source = Find-SkillAnalyzer
if (-not $source) {
    if ($Check) {
        Write-Output '找不到技能里的分析器（这台机器上没有 win-dev-skills 的插件缓存，或用 -SkillRoot 指错了）—— 跳过比对，这一条不算通过。'
        exit 0
    }

    throw '找不到技能里的分析器副本。它在 winui-dev-workflow 技能里（<插件缓存>\winui\<版本>\agent-plugin\skills\winui-dev-workflow\analyzer），用 -SkillRoot 指定目录。'
}

Write-Output "技能里的那一份：$source"

$drift = @()
foreach ($name in $names) {
    $from = Join-Path $source $name
    if (-not (Test-Path -LiteralPath $from)) { throw "技能里缺 $name：$from" }

    $sourceHash = (Get-FileHash -LiteralPath $from -Algorithm SHA256).Hash
    $to = Join-Path $target $name
    $targetHash = if (Test-Path -LiteralPath $to) { (Get-FileHash -LiteralPath $to -Algorithm SHA256).Hash } else { '(没有这一份)' }

    if ($targetHash -ne $sourceHash) {
        $drift += [pscustomobject]@{ Name = $name; Source = $sourceHash; Target = $targetHash }
    }
}

if ($Check) {
    if ($drift.Count -eq 0) {
        Write-Output '两份一致：闸门 1 跑的是技能里那一版规则。'
        exit 0
    }

    Write-Output '两份不一致 —— 仓库这份不是技能里那一版了：'
    foreach ($item in $drift) {
        Write-Output "  $($item.Name)：技能 $($item.Source)，仓库 $($item.Target)"
    }
    Write-Output '这只是本机候选差异，不是发版失败。先核实来源和版本，单独安排升级；不要为匹配旧插件降级仓库载荷。'
    Write-Output '确定升级后用 -SkillRoot 指定来源刷新，复核诊断与哈希、逐行更新 Agreements.txt，再完整运行闸门 1～4。'
    exit 1
}

New-Item -ItemType Directory -Path $target -Force | Out-Null
foreach ($name in $names) {
    Copy-Item -LiteralPath (Join-Path $source $name) -Destination (Join-Path $target $name) -Force
}

Write-Output '已拷过来。tools\analyzers 现在的哈希（写进 Agreements.txt 的 analyzer 行）：'
foreach ($name in $names) {
    $hash = (Get-FileHash -LiteralPath (Join-Path $target $name) -Algorithm SHA256).Hash
    Write-Output "  analyzer  $name  $hash"
}

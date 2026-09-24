# Offline regression fixtures for selfcheck-diff.ps1. No Emby app, user settings, GUI or network.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
$repo = Split-Path -Parent $PSScriptRoot
$script = Join-Path $PSScriptRoot 'selfcheck-diff.ps1'
$root = Join-Path $repo ('artifacts\selfcheck-script-tests\' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$baseline = Join-Path $root 'baseline.txt'
$log = Join-Path $root 'report.txt'
$script:count = 0
$script:runRoots = @()
$utf8 = New-Object Text.UTF8Encoding($false)
function Put([string]$Path, [string]$Text) { [IO.File]::WriteAllText($Path, $Text, $utf8) }
function Assert-True([bool]$Value, [string]$Message) { if (-not $Value) { throw $Message } }
function Check([string]$Name, [bool]$Green, [string[]]$Arguments) {
    $output = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script @Arguments 2>&1)
    $code = $LASTEXITCODE
    $script:lastOutput = $output
    Assert-True (($code -eq 0) -eq $Green) "$Name 错误退出码 $code：$($output -join ' | ')"
    $script:count++
    Write-Output "通过 $Name"
}

try {
    Put $baseline "# preserve comment`n通过  A`n"
    Put $log "[通过] A — fixture`n结果：全部通过`n"
    Check '完整日志成功' $true @('-Log', $log, '-Baseline', $baseline)
    foreach ($case in @(
        @{ Name = '同名后失败不吞'; Text = "[通过] A`n[失败] A — second`n结果：1 项失败`n" },
        @{ Name = '同名前失败不吞'; Text = "[失败] A`n[通过] A`n结果：1 项失败`n" },
        @{ Name = '同名信息降级'; Text = "[通过] A`n[信息] A`n结果：全部通过`n" },
        @{ Name = '同名跳过降级'; Text = "[跳过] A`n[通过] A`n结果：全部通过`n" },
        @{ Name = '结论缺失'; Text = "[通过] A`n" },
        @{ Name = '未完成结论'; Text = "[通过] A`n结果：未完成`n" },
        @{ Name = '错误全通过结论'; Text = "[失败] A`n结果：全部通过`n" },
        @{ Name = '错误失败数量'; Text = "[失败] A`n结果：2 项失败`n" },
        @{ Name = '结论后仍写检查'; Text = "[通过] A`n结果：全部通过`n[通过] B`n" },
        @{ Name = '两次运行拼接'; Text = "[通过] A`n结果：全部通过`n[通过] A`n结果：全部通过`n" },
        @{ Name = '报告无检查'; Text = "结果：全部通过`n" },
        @{ Name = '报告空检查名'; Text = "[通过] `n结果：全部通过`n" },
        @{ Name = '报告为空'; Text = '' },
        @{ Name = '消失不默许'; Text = "[通过] B`n结果：全部通过`n" }
    )) {
        Put $log $case.Text
        Check $case.Name $false @('-Log', $log, '-Baseline', $baseline)
    }

    Put $log "[通过] A`n结果：全部通过`n"
    Check '缺失日志' $false @('-Log', (Join-Path $root 'missing-log.txt'), '-Baseline', $baseline)
    Check '缺失基线' $false @('-Log', $log, '-Baseline', (Join-Path $root 'missing.txt'))
    foreach ($bad in @('', '# comment only', '失败  A', 'invalid', "通过  A`n通过  A`n")) {
        Put $baseline $bad
        Check '无效或空基线拒绝' $false @('-Log', $log, '-Baseline', $baseline)
        Check 'UpdateBaseline也不洗无效基线' $false @('-Log', $log, '-Baseline', $baseline, '-UpdateBaseline')
        Assert-True (([IO.File]::ReadAllText($baseline)) -ceq $bad) '无效基线被改写了'
    }

    $original = "# preserve comment`n通过  A`n"
    Put $baseline $original
    Put $log "[通过] A`n[失败] A`n结果：1 项失败`n"
    Check 'UpdateBaseline拒绝失败' $false @('-Log', $log, '-Baseline', $baseline, '-UpdateBaseline')
    Assert-True (([IO.File]::ReadAllText($baseline)) -ceq $original) '失败时基线被写入'
    Put $log "[通过] B`n结果：全部通过`n"
    Check 'UpdateBaseline不吞退役' $false @('-Log', $log, '-Baseline', $baseline, '-UpdateBaseline')
    Assert-True (([IO.File]::ReadAllText($baseline)) -ceq $original) '消失时基线被写入'
    # Simulate an explicitly reviewed one-line retirement/rename, not accepting a fresh snapshot.
    Put $baseline "# reviewed A -> B`n通过  B`n"
    Check '明确逐行退役后允许比较' $true @('-Log', $log, '-Baseline', $baseline)
    Put $baseline $original
    Put $log "[通过] A`n[通过] B`n结果：全部通过`n"
    Check 'UpdateBaseline仅追加' $true @('-Log', $log, '-Baseline', $baseline, '-UpdateBaseline')
    Assert-True (([IO.File]::ReadAllText($baseline)) -ceq ($original + "通过  B`n")) '更新应保留原行并只追加'
    Put $baseline $original

    # A failing check is never in the baseline by convention, so it must not also be announced as 新增.
    Put $log "[通过] A`n[失败] C — detail`n结果：1 项失败`n"
    Check '失败的新检查不重复报成新增' $false @('-Log', $log, '-Baseline', $baseline)
    Assert-True (($script:lastOutput -join "`n") -match '新增 0') '失败行被当成新增'
    Assert-True (($script:lastOutput -join "`n") -match '\[失败\] C') '失败行未列出'
    Put $log "[通过] A`n[通过] C`n结果：全部通过`n"
    Check '通过的新检查仍要报新增' $true @('-Log', $log, '-Baseline', $baseline)
    Assert-True (($script:lastOutput -join "`n") -match '新增 1') '新通过项应报新增'

    # Build a tiny .NET Framework console executable. It has no app dependency; its only side effect
    # is writing fixtures under this tree. Copying its PE as .dll models the apphost's adjacent assembly.
    $exe = Join-Path $root 'Fixture.exe'
    Add-Type -OutputAssembly $exe -OutputType ConsoleApplication -TypeDefinition @'
using System;
using System.IO;
using System.Reflection;
[assembly: AssemblyMetadata("EmbyNian.SelfCheckIsolation", "1")]
public static class Fixture {
    public static int Main(string[] args) {
        string home = AppDomain.CurrentDomain.BaseDirectory;
        File.WriteAllText(Path.Combine(home, "invoked.txt"), "yes");
        if (args.Length != 4 || args[0] != "--self-check" || args[1] != "--dump-ui" || args[2] != "--self-check-data") return 19;
        string root = args[3];
        File.WriteAllText(Path.Combine(home, "last-run.txt"), root);
        string mode = File.ReadAllText(Path.Combine(home, "mode.txt"));
        if (mode == "ignore") return 0;
        string logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(logs);
        if (mode != "missing-receipt") File.WriteAllText(Path.Combine(logs, "selfcheck-isolation.txt"), "EmbyNian.SelfCheckIsolation=1\n" + (mode == "wrong-root" ? home : root) + "\n");
        if (mode != "missing-log") File.WriteAllText(Path.Combine(logs, "selfcheck-shell.txt"), "[通过] A\n结果：全部通过\n");
        return mode == "nonzero" ? 7 : 0;
    }
}
'@
    Copy-Item -LiteralPath $exe -Destination ([IO.Path]::ChangeExtension($exe, '.dll'))
    foreach ($mode in @('success', 'ignore', 'missing-receipt', 'wrong-root', 'missing-log', 'nonzero')) {
        Put (Join-Path $root 'mode.txt') $mode
        Check "进程运行路径 $mode" ($mode -eq 'success') @('-Exe', $exe, '-Baseline', $baseline)
        $script:runRoots += [IO.File]::ReadAllText((Join-Path $root 'last-run.txt'))
    }
    Assert-True (($script:runRoots | Select-Object -Unique).Count -eq $script:runRoots.Count) '每次运行须使用新目录'
    Remove-Item -LiteralPath (Join-Path $root 'invoked.txt')
    $dll = [IO.Path]::ChangeExtension($exe, '.dll')
    Remove-Item -LiteralPath $dll
    Check '缺少能力程序集时不启动' $false @('-Exe', $exe, '-Baseline', $baseline)
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $root 'invoked.txt'))) '缺少声明仍启动了exe'
    Put $dll 'old assembly without isolation protocol'
    Check '旧exe无隔离声明时不启动' $false @('-Exe', $exe, '-Baseline', $baseline)
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $root 'invoked.txt'))) '旧exe被启动'
    Write-Output "脚本离线回归：$script:count 项通过，0 失败，0 跳过；未使用应用/真实设置/媒体库。"
}
finally {
    foreach ($runRoot in $script:runRoots) {
        $expectedPrefix = (Join-Path $repo 'artifacts\selfcheck\run-')
        if ($runRoot.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $runRoot)) {
            Remove-Item -LiteralPath $runRoot -Recurse -Force
        }
    }
    Remove-Item -LiteralPath $root -Recurse -Force
}

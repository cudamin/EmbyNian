<#
    把当前工作树整份打包成一个时间点，放在仓库外面。

    为什么不用 git：这棵树整个是 WinForms → WinUI 3 那次迁移的未提交状态，`git stash` / `git checkout`
    这类动作本身就是要防的东西，而未跟踪的文件、settings、libmpv 那份大二进制也都不在 HEAD 里。所以备份
    是「原样复制一份」，不是「记一笔历史」—— 还原就是解压覆盖回去，不需要懂 git。

    包进去的是除了构建产物之外的一切：源码、.git、legacy 那份旧工程、截图、根目录那些文件（libmpv-2.dll
    也在，它是 116 MB，但少了它这份备份就还原不出一个能构建的树）。挡在外面的只有能重新生成的四类目录：
    bin、obj、artifacts、.vs。

    用法：
        powershell -NoProfile -ExecutionPolicy Bypass -File tools/backup.ps1
        powershell -NoProfile -ExecutionPolicy Bypass -File tools/backup.ps1 -Label 重绘界面前
#>
[CmdletBinding()]
param(
    # 放在 %USERPROFILE% 下而不是仓库里：备份不能被 artifacts 的清理、发布校验或者下一次打包扫到。
    [string]$OutputRoot = (Join-Path $env:USERPROFILE 'EmbyNian-backups'),

    # 追在文件名后面的一句话，方便半个月后认出「这是哪一次的备份」。
    [string]$Label = ''
)

$ErrorActionPreference = 'Stop'
$started = Get-Date

$root = Split-Path -Parent $PSScriptRoot
$skip = @('bin', 'obj', 'artifacts', '.vs')

if (-not (Test-Path $OutputRoot)) { New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null }

$stamp = $started.ToString('yyyyMMdd-HHmm')
$name = if ($Label) { "EmbyNian-$stamp-$Label.zip" } else { "EmbyNian-$stamp.zip" }
$target = Join-Path $OutputRoot $name

if (Test-Path $target) { Remove-Item $target -Force }

Write-Host "备份 $root"
Write-Host "挡在外面的目录：$($skip -join '、')"

# 逐个文件挑，而不是先复制到一个暂存目录再压：省下一次 150 MB 的整树复制。
$files = Get-ChildItem -LiteralPath $root -Recurse -File -Force | Where-Object {
    $relative = $_.FullName.Substring($root.Length + 1)
    $parts = $relative.Split([System.IO.Path]::DirectorySeparatorChar)
    -not ($parts | Where-Object { $skip -contains $_ })
}

$bytes = ($files | Measure-Object Length -Sum).Sum
Write-Host ("要打包 {0} 个文件，{1:N1} MB" -f $files.Count, ($bytes / 1MB))

# 备份自己带一张说明：时间、当时的 git 位置、挡掉了什么、怎么还原。半年后打开这个 zip 的人只看得到文件。
$head = & git -C $root rev-parse --short HEAD 2>$null
$dirty = (& git -C $root status --porcelain 2>$null | Measure-Object -Line).Lines
$notes = @"
EmbyNian 工作树备份
时间：$($started.ToString('yyyy-MM-dd HH:mm:ss zzz'))
来源：$root
git：HEAD $head，工作树里有 $dirty 个文件与 HEAD 不同（这棵树本来就是未提交状态，不是异常）
挡在外面：$($skip -join '、') —— 这四类都能由构建和 tools/publish.ps1 重新生成
文件数：$($files.Count)，原始大小 $("{0:N1}" -f ($bytes / 1MB)) MB

还原：把这个 zip 解压覆盖回 $root，然后
    "%USERPROFILE%\.dotnet\dotnet.exe" build EmbyNian.sln -c Release -m:1
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/publish.ps1
"@

Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null

$zip = [System.IO.Compression.ZipFile]::Open($target, 'Create')
try
{
    foreach ($file in $files)
    {
        $entry = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $entry, 'Optimal') | Out-Null
    }

    $stream = $zip.CreateEntry('备份说明.txt', 'Optimal').Open()
    try
    {
        $writer = New-Object System.IO.StreamWriter($stream, (New-Object System.Text.UTF8Encoding $true))
        $writer.Write($notes)
        $writer.Flush()
        $writer.Dispose()
    }
    finally { $stream.Dispose() }
}
finally { $zip.Dispose() }

# 立刻回读一遍：压完没打开过的压缩包等于没备份。
$check = [System.IO.Compression.ZipFile]::OpenRead($target)
$entries = $check.Entries.Count
$check.Dispose()

$size = (Get-Item $target).Length
$took = (Get-Date) - $started

if ($entries -ne ($files.Count + 1)) { throw "备份不完整：打包 $($files.Count) 个文件，回读只有 $entries 条" }

Write-Host ("备份完成：{0}" -f $target)
Write-Host ("回读 {0} 条（{1} 个文件 + 1 张说明），{2:N1} MB，用了 {3:N0} 秒" -f $entries, $files.Count, ($size / 1MB), $took.TotalSeconds)

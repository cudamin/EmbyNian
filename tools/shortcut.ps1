# Puts 「EmbyNian」 on the desktop, pointing at a published build.
#
# The shortcut has been part of the deliverable since the fourth phase, but nothing in the repository
# recreated it: it was made by hand once, against a path that has since moved twice (the WinForms shell went
# away, and the target framework went from net8.0 to net10.0). So it was gone, and there was nothing to run
# to get it back. This is that thing to run.
#
#   .\tools\shortcut.ps1                     指向 artifacts\publish\win-x64\EmbyNian.exe
#   .\tools\shortcut.ps1 -Exe <路径>         指向别的 exe（比如调试输出）
#   .\tools\shortcut.ps1 -Remove             删掉桌面上那个快捷方式
[CmdletBinding()]
param(
    [string]$Exe,

    [string]$Name = 'EmbyNian',

    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path

# GetFolderPath rather than $env:USERPROFILE\Desktop: a OneDrive-redirected desktop lives somewhere else
# entirely, and a shortcut written to the folder nobody looks at is the same as no shortcut.
$desktop = [Environment]::GetFolderPath('Desktop')
if ([string]::IsNullOrWhiteSpace($desktop)) { throw '找不到桌面目录。' }

$link = Join-Path $desktop "$Name.lnk"

if ($Remove) {
    if (Test-Path -LiteralPath $link -PathType Leaf) {
        Remove-Item -LiteralPath $link -Force
        Write-Output "已删除 $link"
    } else {
        Write-Output "桌面上没有 $Name.lnk，无需删除"
    }
    return
}

if ([string]::IsNullOrWhiteSpace($Exe)) {
    $Exe = Join-Path $repo 'artifacts\publish\win-x64\EmbyNian.exe'
}

if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) {
    throw "找不到 $Exe。先跑 .\tools\publish.ps1 生成发行版，或者用 -Exe 指向别的 EmbyNian.exe。"
}

$target = (Resolve-Path -LiteralPath $Exe).Path
$folder = Split-Path -Parent $target

$shell = New-Object -ComObject WScript.Shell
try {
    $shortcut = $shell.CreateShortcut($link)
    $shortcut.TargetPath = $target

    # The app finds libmpv-2.dll and shaders\ next to its own exe, so this is not load-bearing — but a
    # shortcut whose working directory is C:\Windows\System32 is what mpv writes its stray files into when
    # anything ever does resolve a relative path.
    $shortcut.WorkingDirectory = $folder
    $shortcut.IconLocation = "$target,0"
    $shortcut.Description = 'EmbyNian —— Emby 的 Windows 播放客户端'
    $shortcut.Save()
} finally {
    [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
}

$built = (Get-Item -LiteralPath $target).LastWriteTime
Write-Output ("桌面快捷方式已写好：{0}" -f $link)
Write-Output ("  → {0}（生成于 {1:yyyy-MM-dd HH:mm}）" -f $target, $built)

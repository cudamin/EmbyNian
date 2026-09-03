<#
    旁站观测鼠标指针：把 GetCursorInfo 按时间印成一条时间线，并在指定时刻拍照 —— 拍照时如果系统说「屏幕上有
    光标」，就把那只光标连热点一起画进图里。

    为什么要这么一支脚本：GDI 截图（CopyFromScreen / BitBlt）从来不含鼠标光标，所以一张「没有箭头」的普通截图
    什么都证明不了。而 tools\shot.ps1 更不能用 —— 它会先把目标窗口设成 HWND_TOPMOST 再放回，而「屏幕上那只光标
    归哪条消息队列管」正会被这一下搅乱。

    所以这支脚本有三条铁律：不 SetForegroundWindow、不 SetWindowPos、不 SetCursorPos。它只看，不碰。

    用法（配 EmbyNian.exe --hide-cursor）：
      powershell -NoProfile -ExecutionPolicy Bypass -File tools/cursor-watch.ps1 `
        -Seconds 8 -ShotAt "0.5,3.5" -Out artifacts/shots/cursor

    要的结果是：0.5 秒那张有箭头、角上写着「标志 0x01」，3.5 秒那张没有箭头、角上写着「标志 0x00」。
#>

[CmdletBinding()]
param(
    [double]$Seconds = 8,
    [int]$IntervalMs = 200,
    [string]$Out = "artifacts/shots/cursor",
    [string]$ShotAt = "0.5,3.5"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

# System.Drawing 要显式引用：Add-Type 编译这段 C# 时不会去看上面 Add-Type -AssemblyName 加载了什么。
Add-Type -ReferencedAssemblies 'System.Drawing' -TypeDefinition @'
using System;
using System.Drawing;
using System.Runtime.InteropServices;

public static class CursorWatch
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT pt; }

    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO info);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr window, System.Text.StringBuilder text, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] public static extern IntPtr LoadCursor(IntPtr instance, int name);
    [DllImport("user32.dll")] public static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
    [DllImport("user32.dll")] public static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon,
        int width, int height, uint step, IntPtr brush, uint flags);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr handle);

    public static string ClassOf(IntPtr window)
    {
        if (window == IntPtr.Zero) return "无";
        var text = new System.Text.StringBuilder(256);
        return GetClassName(window, text, text.Capacity) > 0 ? text.ToString() : "问不出";
    }

    /// <summary>把系统当前那只光标画到位图上，坐标按它自己的热点校正 —— 画出来的位置就是屏上看到的位置。</summary>
    public static void Paint(Bitmap frame, IntPtr cursor, int x, int y, int originX, int originY)
    {
        ICONINFO info;
        var hotX = 0;
        var hotY = 0;

        if (GetIconInfo(cursor, out info))
        {
            hotX = info.xHotspot;
            hotY = info.yHotspot;
            if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
            if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
        }

        using (var canvas = Graphics.FromImage(frame))
        {
            var dc = canvas.GetHdc();
            try
            {
                // 0x0003 = DI_NORMAL（画图像和掩码），step 0 表示不是动画光标。
                DrawIconEx(dc, x - originX - hotX, y - originY - hotY, cursor, 0, 0, 0, IntPtr.Zero, 3);
            }
            finally
            {
                canvas.ReleaseHdc(dc);
            }
        }
    }
}
'@

$arrow = [CursorWatch]::LoadCursor([IntPtr]::Zero, 32512)   # IDC_ARROW
$screen = [System.Windows.Forms.SystemInformation]::VirtualScreen

$shots = @()
foreach ($piece in ($ShotAt -split ',')) {
    $trimmed = $piece.Trim()
    if ($trimmed) { $shots += [double]$trimmed }
}

$folder = Split-Path -Parent $Out
if ($folder -and -not (Test-Path $folder)) { New-Item -ItemType Directory -Path $folder -Force | Out-Null }

Write-Host "观测 $Seconds 秒，每 $IntervalMs 毫秒一行；拍照时刻：$($shots -join '、') 秒"
Write-Host "虚拟桌面 $($screen.Width)x$($screen.Height) @ $($screen.X),$($screen.Y)"
Write-Host ""

$taken = New-Object 'System.Collections.Generic.HashSet[double]'
$clock = [System.Diagnostics.Stopwatch]::StartNew()
$first = $null
$last = $null

while ($clock.Elapsed.TotalSeconds -lt $Seconds) {
    $t = $clock.Elapsed.TotalSeconds

    $info = New-Object CursorWatch+CURSORINFO
    $info.cbSize = [System.Runtime.InteropServices.Marshal]::SizeOf($info)
    $ok = [CursorWatch]::GetCursorInfo([ref]$info)

    if ($ok) {
        $shape = if ($info.hCursor -eq [IntPtr]::Zero) { "没有" }
                 elseif ($info.hCursor -eq $arrow) { "系统箭头" }
                 else { "别的形状" }

        $under = [CursorWatch]::WindowFromPoint($info.pt)
        $ownerPid = 0
        [void][CursorWatch]::GetWindowThreadProcessId($under, [ref]$ownerPid)
        $front = [CursorWatch]::GetForegroundWindow()

        $line = ("t={0,5:0.00}s  标志 0x{1:X2}  形状 0x{2:X}（{3}）  位置 ({4},{5})  指针上={6}  前台={7}" -f `
            $t, $info.flags, [int64]$info.hCursor, $shape, $info.pt.X, $info.pt.Y,
            [CursorWatch]::ClassOf($under), [CursorWatch]::ClassOf($front))
        Write-Host $line

        if ($null -eq $first) { $first = $info.flags }
        $last = $info.flags
    }
    else {
        Write-Host ("t={0,5:0.00}s  问不出 GetCursorInfo" -f $t)
    }

    foreach ($moment in $shots) {
        if ($taken.Contains($moment) -or $t -lt $moment) { continue }
        [void]$taken.Add($moment)

        $frame = New-Object System.Drawing.Bitmap($screen.Width, $screen.Height)
        $canvas = [System.Drawing.Graphics]::FromImage($frame)
        $canvas.CopyFromScreen($screen.X, $screen.Y, 0, 0, $frame.Size)

        $note = "t={0:0.00}s  标志 0x{1:X2}  形状 0x{2:X}" -f $t, $info.flags, [int64]$info.hCursor
        if ($ok -and ($info.flags -band 1) -ne 0 -and $info.hCursor -ne [IntPtr]::Zero) {
            [CursorWatch]::Paint($frame, $info.hCursor, $info.pt.X, $info.pt.Y, $screen.X, $screen.Y)
            $note += "（光标已画进图里）"
        }
        else {
            $note += "（屏幕上没有光标）"
        }

        $font = New-Object System.Drawing.Font("Consolas", 20, [System.Drawing.FontStyle]::Bold)
        $canvas.FillRectangle([System.Drawing.Brushes]::Black, 0, 0, 900, 44)
        $canvas.DrawString($note, $font, [System.Drawing.Brushes]::Yellow, 8, 8)
        $font.Dispose()
        $canvas.Dispose()

        $path = "{0}-{1:0.0}s.png" -f $Out, $moment
        $frame.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        $frame.Dispose()
        Write-Host "        → 拍了一张：$path  $note"
    }

    Start-Sleep -Milliseconds $IntervalMs
}

Write-Host ""
Write-Host ("结论：开头 标志 0x{0:X2}，结尾 标志 0x{1:X2} —— {2}" -f `
    $first, $last, $(if (($first -band 1) -ne 0 -and ($last -band 1) -eq 0) { "指针从「显示」变成了「没有」，自动隐藏生效" } `
                    elseif (($last -band 1) -ne 0) { "结尾屏幕上还有光标" } `
                    else { "开头就没有光标，这一轮说明不了什么" }))

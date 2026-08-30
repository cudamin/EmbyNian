# Moves the real mouse over the running window, and optionally clicks or turns the wheel.
#
# The reveal rule is the one part of the player that cannot be checked any other way. PlayerPage's own
# ProbeReveal drives ChromeReveal directly, which proves the state-to-Visibility mapping but not that
# pointer events reach it at all — the WinForms shell got that wrong for a while precisely because its
# chrome lived in separate top-level windows that swallowed the moves. So: a real WM_MOUSEMOVE, sent to
# the real window, and then a screenshot of what came up. A card's hover buttons need the same thing for
# the same reason: 自检 can fake the pointer for one layout pass, but only this proves a real one arrives.
#
# Coordinates are client-relative and in the window's own pixels, so they mean the same thing whatever
# the window's position; -Fraction takes 0..1 instead, for "the bottom edge" and "the right edge".
param(
    [double]$X = 0.5,
    [double]$Y = 0.5,
    # Treat X and Y as fractions of the client area rather than pixels.
    [switch]$Fraction,
    [ValidateSet("none", "left", "double")]
    [string]$Click = "none",
    # Wheel notches: positive is up (volume up), negative down. 0 for no wheel.
    [int]$Wheel = 0,
    # Park the cursor at an absolute screen point instead, for 「移出窗口」: the reveal rule's departure
    # path can only be exercised from outside, and a client-relative point is by definition inside.
    [int]$ScreenX = -1,
    [int]$ScreenY = -1,
    [int]$SettleMs = 300
)

$ErrorActionPreference = "Stop"

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Poke {
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, int data, IntPtr extra);
    [DllImport("user32.dll")] public static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT {
        public int dx, dy; public uint data, flags, time; public IntPtr extra;
    }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public MOUSEINPUT mi; }

    public const uint Move = 0x0001, LeftDown = 0x0002, LeftUp = 0x0004, Wheel = 0x0800,
        Absolute = 0x8000, VirtualDesk = 0x4000;
    public const int NotchDelta = 120;
    public const int ScreenWidth = 0, ScreenHeight = 1;
    public const int DeskX = 76, DeskY = 77, DeskWidth = 78, DeskHeight = 79;
}
"@

# 注入一次绝对移动，而不是 SetCursorPos。SetCursorPos 把光标搬过去，XAML 岛却收不到，PointerEntered
# 一次都不发 —— 用它把光标放到卡片上再截图，截出来的永远是没有悬浮层的那张，看着像功能坏了，其实是
# 这支脚本没把事件送到。绝对移动走真实输入队列，和手推鼠标同一条路。
#
# 走 SendInput 而不是 mouse_event：绝对坐标那 0..65535 归的是哪一块屏，两者不一样。mouse_event 只按主屏
# 归一（VIRTUALDESK 那个旗标是 SendInput 的，老包装不认，传了会把落点整个算歪），所以副屏上的窗口它指不
# 准 —— 而自检是要求在第二屏上跑的，「指不准」在那边就是每一次点击都落在别处。SendInput 认这个旗标，于是
# 归一的是整个虚拟桌面（SM_XVIRTUALSCREEN 那四个），副屏、负坐标的左屏、竖屏都在里面。
$desk = @{
    X = [Poke]::GetSystemMetrics([Poke]::DeskX)
    Y = [Poke]::GetSystemMetrics([Poke]::DeskY)
    W = [Poke]::GetSystemMetrics([Poke]::DeskWidth)
    H = [Poke]::GetSystemMetrics([Poke]::DeskHeight)
}

function Send([int]$x, [int]$y) {
    $dx = [int][Math]::Round(($x - $desk.X) * 65535.0 / ($desk.W - 1))
    $dy = [int][Math]::Round(($y - $desk.Y) * 65535.0 / ($desk.H - 1))

    $mouse = New-Object Poke+MOUSEINPUT
    $mouse.dx = $dx
    $mouse.dy = $dy
    $mouse.flags = [Poke]::Move -bor [Poke]::Absolute -bor [Poke]::VirtualDesk

    $event = New-Object Poke+INPUT
    $event.type = 0
    $event.mi = $mouse

    [void][Poke]::SendInput(1, @($event), [System.Runtime.InteropServices.Marshal]::SizeOf($event))
}

$process = Get-Process -Name "EmbyNian" -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } |
    Select-Object -First 1
if (-not $process) { throw "没有正在运行的 EmbyNian 窗口" }

$handle = $process.MainWindowHandle
$client = New-Object Poke+RECT
if (-not [Poke]::GetClientRect($handle, [ref]$client)) { throw "取不到客户区尺寸" }

$point = New-Object Poke+POINT

if ($ScreenX -ge 0 -and $ScreenY -ge 0) {
    # Absolute: no activation either, because taking the foreground would itself be an event the window
    # reacts to, and the thing under test is the pointer leaving and nothing else.
    $point.X = $ScreenX
    $point.Y = $ScreenY
    $px = $ScreenX
    $py = $ScreenY
}
else {
    $px = if ($Fraction) { [int]($client.R * $X) } else { [int]$X }
    $py = if ($Fraction) { [int]($client.B * $Y) } else { [int]$Y }

    # Clamped one pixel inside: a point exactly on the far edge lands on the resize border, where the
    # window gets a hit-test rather than a move and the rail would never light up.
    $px = [Math]::Max(1, [Math]::Min($client.R - 2, $px))
    $py = [Math]::Max(1, [Math]::Min($client.B - 2, $py))

    $point.X = $px
    $point.Y = $py
    if (-not [Poke]::ClientToScreen($handle, [ref]$point)) { throw "客户区坐标转换失败" }

    [void][Poke]::SetForegroundWindow($handle)
}

# 落点仍然回读一次再校正。这支脚本的 DPI 感知随宿主而变（谁启动的 PowerShell 就跟谁），一旦窗口那边
# 给的是缩放过的坐标、虚拟桌面度量给的是物理的，第一发就会按比例歪。所以：按虚拟桌面估一发，再拿「要
# 多少、到了多少」按比例逼近。映射是线性的，一两步就落到目标像素上，读回来的数就是真落点。两边本来就
# 在同一个空间时（多数情况）第一发就是准的，这个循环一步都不用走。
$aim = @{ X = $point.X; Y = $point.Y }
$landed = New-Object Poke+POINT

# 先挪开一点。一次落在光标已经在的地方的移动不产生 WM_MOUSEMOVE，而悬浮层和播放器的显隐规则都是被
# 移动驱动的，不是被位置驱动的。
Send ($aim.X - 40) ($aim.Y - 40)
Start-Sleep -Milliseconds 40

for ($step = 0; $step -lt 6; $step++) {
    Send $aim.X $aim.Y
    Start-Sleep -Milliseconds 40
    [void][Poke]::GetCursorPos([ref]$landed)

    if ($landed.X -eq $point.X -and $landed.Y -eq $point.Y) { break }

    # 比例逼近，落点为 0 时退化成挪一步：除以 0 换不来更好的估计。
    $aim.X = if ($landed.X -ne 0) { [int][Math]::Round($aim.X * $point.X / $landed.X) } else { $aim.X + 10 }
    $aim.Y = if ($landed.Y -ne 0) { [int][Math]::Round($aim.Y * $point.Y / $landed.Y) } else { $aim.Y + 10 }
}

if ($Wheel -ne 0) {
    [Poke]::mouse_event([Poke]::Wheel, 0, 0, $Wheel * [Poke]::NotchDelta, [IntPtr]::Zero)
}

switch ($Click) {
    "left" { [Poke]::mouse_event([Poke]::LeftDown, 0, 0, 0, [IntPtr]::Zero); [Poke]::mouse_event([Poke]::LeftUp, 0, 0, 0, [IntPtr]::Zero) }
    "double" {
        [Poke]::mouse_event([Poke]::LeftDown, 0, 0, 0, [IntPtr]::Zero); [Poke]::mouse_event([Poke]::LeftUp, 0, 0, 0, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 60
        [Poke]::mouse_event([Poke]::LeftDown, 0, 0, 0, [IntPtr]::Zero); [Poke]::mouse_event([Poke]::LeftUp, 0, 0, 0, [IntPtr]::Zero)
    }
}

Start-Sleep -Milliseconds $SettleMs
Write-Output "客户区 $($client.R)x$($client.B)，指向 ($px,$py)，落点 ($($landed.X),$($landed.Y))，点击 $Click，滚轮 $Wheel"

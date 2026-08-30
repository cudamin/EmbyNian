# Launches the WinUI 3 shell, waits for it to settle, captures its window to a PNG and closes it.
# Screen capture rather than PrintWindow: the UI is a XAML island composited by DWM, and PrintWindow
# on the host HWND returns the erased background without the island's content.
param(
    [string]$Exe = "src\EmbyNian.Shell\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\EmbyNian.exe",
    [string]$Out = "shell.png",
    # Long enough for the saved token, the library list and the home page's three requests: the shot
    # is of real server data now, not of a mock grid that was up the moment the window was.
    [int]$SettleMs = 6000,
    # Passed straight to the exe. --show-library is the one that matters: a flyout cannot be captured
    # by waiting for it, so the app has to be told to leave one open. One space-separated string rather
    # than a string[] because this is called from bash, where -ExeArgs a,b arrives as a single token and
    # binds as one element.
    [string]$ExeArgs = "",
    # Photograph the instance that is already running instead of starting one, and leave it running
    # afterwards. The player needs this: it only exists while something is playing, a fresh launch would
    # be refused by the single-instance mutex, and closing the window is exactly what must not happen.
    [switch]$Attach,
    # Capture a named top-level window of the app instead of its main one. 设置 is a second window, so
    # waiting for the app gets the main window and the settings window is behind whatever we bring
    # forward; naming it here means the shot is of that window and of nothing around it.
    [string]$WindowTitle = ""
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Shot {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int c, uint flags);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int size);

    public delegate bool EnumProc(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, IntPtr state);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder text, int max);

    // One process's visible top-level window by title. EnumWindows rather than MainWindowHandle, which
    // only ever answers with the first one — and the settings window is the second.
    public static IntPtr Find(int process, string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr window, IntPtr state) {
            uint owner;
            GetWindowThreadProcessId(window, out owner);
            if (owner != (uint)process || !IsWindowVisible(window)) return true;
            System.Text.StringBuilder text = new System.Text.StringBuilder(256);
            GetWindowText(window, text, text.Capacity);
            if (text.ToString() != title) return true;
            found = window;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    public static readonly IntPtr Topmost = new IntPtr(-1);
    public static readonly IntPtr NoTopmost = new IntPtr(-2);
    public const uint KeepPlace = 0x0001 | 0x0002; // SWP_NOSIZE | SWP_NOMOVE
}
"@

if ($Attach) {
    $process = Get-Process -Name "EmbyNian" -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } |
        Select-Object -First 1
    if (-not $process) { throw "没有正在运行的 EmbyNian 窗口可供附着" }
}
else {
    $launch = @{ FilePath = (Resolve-Path $Exe); PassThru = $true }
    $flags = $ExeArgs.Split(@(' ', ','), [StringSplitOptions]::RemoveEmptyEntries)
    if ($flags.Count -gt 0) { $launch.ArgumentList = $flags }
    $process = Start-Process @launch
}

try {
    # Brought forward before the settle, not after: with --show-library the app leaves a flyout open,
    # and a flyout light-dismisses the moment something else takes activation. Activating first means
    # the only window that gets activated is this one, while there is still nothing to dismiss.
    $handle = [IntPtr]::Zero
    for ($waited = 0; $waited -lt 15000 -and $handle -eq [IntPtr]::Zero; $waited += 250) {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
        $handle = $process.MainWindowHandle
    }

    if ($handle -eq [IntPtr]::Zero) { throw "窗口尚未出现" }

    # A named window is looked for after the main one is up, because it is opened from the main window's
    # own startup path: before that there is nothing to find.
    if ($WindowTitle) {
        $named = [IntPtr]::Zero
        for ($waited = 0; $waited -lt 15000 -and $named -eq [IntPtr]::Zero; $waited += 250) {
            Start-Sleep -Milliseconds 250
            $named = [Shot]::Find($process.Id, $WindowTitle)
        }

        if ($named -eq [IntPtr]::Zero) { throw "没有找到标题为「$WindowTitle」的窗口" }
        $handle = $named
    }

    # SetForegroundWindow alone is not enough: Windows refuses it when the caller is not itself the
    # foreground process, which is exactly the case when this script runs from a terminal or an agent,
    # and the capture then reads whatever window really is on top. Topmost is not refused.
    [void][Shot]::SetWindowPos($handle, [Shot]::Topmost, 0, 0, 0, 0, [Shot]::KeepPlace)
    [void][Shot]::SetForegroundWindow($handle)

    Start-Sleep -Milliseconds $SettleMs

    # DWMWA_EXTENDED_FRAME_BOUNDS (9): the visible frame, without the invisible resize border that
    # GetWindowRect includes and that would put desktop pixels in the shot.
    $rect = New-Object Shot+RECT
    if ([Shot]::DwmGetWindowAttribute($handle, 9, [ref]$rect, 16) -ne 0) {
        [void][Shot]::GetWindowRect($handle, [ref]$rect)
    }

    $width = $rect.R - $rect.L
    $height = $rect.B - $rect.T
    $bitmap = New-Object System.Drawing.Bitmap $width, $height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($rect.L, $rect.T, 0, 0, $bitmap.Size)
    $graphics.Dispose()

    $target = if ([System.IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path (Get-Location) $Out }
    $folder = [System.IO.Path]::GetDirectoryName($target)
    if ($folder -and -not (Test-Path $folder)) { [void](New-Item -ItemType Directory -Path $folder) }
    $bitmap.Save($target, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()

    Write-Output "$target $($width)x$($height)"
}
finally {
    # The window that was made topmost, which with -WindowTitle is not the main one.
    $raised = if ($handle -and $handle -ne [IntPtr]::Zero) { $handle } else { $process.MainWindowHandle }
    if (-not $process.HasExited) { [void][Shot]::SetWindowPos($raised, [Shot]::NoTopmost, 0, 0, 0, 0, [Shot]::KeepPlace) }

    # Attached: the instance was somebody else's before this script ran and is still theirs after.
    if (-not $Attach) {
        if (-not $process.HasExited) { $process.CloseMainWindow() | Out-Null; Start-Sleep -Milliseconds 800 }
        if (-not $process.HasExited) { $process.Kill() }
    }
}

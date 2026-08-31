using System.Runtime.InteropServices;
using EmbyNian.Shell.Interop;
using EmbyNian.Shell.Views;
using EmbyNian.Shell.Windowing;

namespace EmbyNian.Shell;

/// <summary>
/// 会动真窗口的那几关：把客户区按画面比例重塑一遍、进出全屏、全屏时任务栏有没有让开。
/// <para>
/// 单独放一处是因为它们和上面那些不一样 —— 它们会真的改窗口的形状和层级，跑完必须原样放回去，否则后面每一关
/// 量到的几何都是错的。任务栏那一段还得去找托盘那个窗口、在它中点上问「这会儿谁在上面」。
/// 拆成几个文件的缘由见主文件 <see cref="ShellSelfCheck"/> 的类注释。
/// </para>
/// </summary>
internal static partial class ShellSelfCheck
{
    /// <summary>
    /// 画面比例, last of the player's probes and wrapped in a rect restore, because it is the only one that
    /// moves the window: <c>FitToPicture</c> reshapes the client area for real, and every geometry answer
    /// the probes above gave was measured against the size it had before. Modelled on
    /// <see cref="ReportFullscreen"/>, which drives the live window for the same reason and puts it back the
    /// same way.
    /// </summary>
    private static void ReportPictureAspect(HostWindow window, PlayerPage player, Action<string, bool, string> check)
    {
        var handle = window.Handle;
        var before = new NativeRect();
        var saved = handle != IntPtr.Zero && Native.GetWindowRect(handle, out before);

        (bool Ok, string Detail) aspect;
        try
        {
            aspect = player.ProbeAspect();
        }
        finally
        {
            if (saved)
            {
                Native.SetWindowPos(
                    handle, Native.HwndTop,
                    before.Left, before.Top, before.Width, before.Height,
                    Native.SwpNoZOrder | Native.SwpNoActivate);
            }
        }

        check("画面比例联动", aspect.Ok, aspect.Detail);
    }

    /// <summary>
    /// 全屏, driven on the real window: in and straight back out again, measuring the frame both times.
    /// <para>
    /// Three separate claims, so a failure says which one broke. The frame: covers the monitor exactly,
    /// both style bits gone, joins the topmost band, and coming back restores the rect, the style and the
    /// band it took away. The taskbar: whoever owns the pixels in the middle of the tray while the window
    /// is fullscreen had better be the window — that is 「全屏后 windows 任务栏还在」 stated as something
    /// measurable, and it is the only part of this the user can see. And the rule that decides whether the
    /// picture keeps that band when another application comes forward, which is 「屏幕1全屏播放时点击屏幕2的
    /// 应用」 — the one arrangement this cannot stage on the machine it is running on, so it is put as
    /// geometry, plus the question that comes before the geometry: whether the window in front is even
    /// somebody else's.
    /// </para>
    /// </summary>
    private static void ReportFullscreen(HostWindow window, Action<string, bool, string> check)
    {
        // First, and without needing a window at all: four arrangements put to the rule that decides whether
        // a fullscreen picture keeps the topmost band while another application is in front. The real
        // arrangement needs a second monitor with an app open on it, so the rule is asked about geometry
        // instead — a window on another screen keeps the band, and everything else gives it up, including
        // a window that is on another screen but reaches across into the picture and any monitor the OS
        // would not name.
        var here = new IntPtr(1);
        var there = new IntPtr(2);
        var picture = new NativeRect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        var beside = new NativeRect { Left = 2000, Top = 100, Right = 2600, Bottom = 700 };
        var across = new NativeRect { Left = 1800, Top = 100, Right = 2600, Bottom = 700 };
        var upon = new NativeRect { Left = 100, Top = 100, Right = 400, Bottom = 400 };

        var elsewhere = HostWindow.StandsClear(picture, here, beside, there);
        var straddling = HostWindow.StandsClear(picture, here, across, there);
        var sameScreen = HostWindow.StandsClear(picture, here, upon, here);
        var nameless = HostWindow.StandsClear(picture, here, beside, IntPtr.Zero);

        // And the question asked before any of that geometry: whose window is in front. Our own popups are
        // top-level windows sitting right over the picture, and a menu is not the user leaving.
        var mine = HostWindow.SameApp(window.Handle);
        var theirs = HostWindow.SameApp(Native.FindWindow("Shell_TrayWnd", null));

        check("全屏让位只看画面",
            elsewhere && !straddling && !sameScreen && !nameless && mine && !theirs,
            $"另一屏不重叠时保持置顶={elsewhere}，另一屏但压到画面={straddling}"
                + $"，同一屏={sameScreen}，问不出显示器={nameless}"
                + $"；自己的窗口算自家={mine}，任务栏算自家={theirs}");

        if (window.Handle == IntPtr.Zero || window.Fullscreen) return;

        var monitor = Native.MonitorFromWindow(window.Handle, Native.MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!Native.GetMonitorInfo(monitor, ref info))
        {
            check("全屏几何", false, "读不到显示器边界");
            return;
        }

        Native.GetWindowRect(window.Handle, out var before);
        var beforeStyle = (long)Native.GetWindowLongPtr(window.Handle, Native.GwlStyle);
        var beforeEx = (long)Native.GetWindowLongPtr(window.Handle, Native.GwlExStyle);

        NativeRect full;
        long fullStyle;
        long fullEx;
        bool dragRefused;
        (bool Ok, string Detail) taskbar;
        try
        {
            window.Fullscreen = true;

            Native.GetWindowRect(window.Handle, out full);
            fullStyle = (long)Native.GetWindowLongPtr(window.Handle, Native.GwlStyle);
            fullEx = (long)Native.GetWindowLongPtr(window.Handle, Native.GwlExStyle);

            // The title strip is still there in fullscreen, and dragging the window by it would pull the
            // frame off the monitor it is covering. Asked here because this is where a fullscreen window is.
            dragRefused = !window.BeginDrag(new NativePoint { X = full.Left + 100, Y = full.Top + 10 });
            window.EndDrag();

            taskbar = ReportTaskbarStandsAside(window.Handle, info.Monitor);
        }
        finally
        {
            window.Fullscreen = false;
        }

        Native.GetWindowRect(window.Handle, out var after);
        var afterStyle = (long)Native.GetWindowLongPtr(window.Handle, Native.GwlStyle);
        var afterEx = (long)Native.GetWindowLongPtr(window.Handle, Native.GwlExStyle);

        var covered = full.Left == info.Monitor.Left && full.Top == info.Monitor.Top
            && full.Width == info.Monitor.Width && full.Height == info.Monitor.Height;
        var stripped = (fullStyle & (Native.WsCaption | Native.WsThickFrame)) == 0;
        var raised = (fullEx & Native.WsExTopMost) != 0;
        var restored = after.Left == before.Left && after.Top == before.Top
            && after.Width == before.Width && after.Height == before.Height
            && afterStyle == beforeStyle && afterEx == beforeEx;

        check("全屏几何", covered && stripped && raised && restored,
            $"{full.Width}x{full.Height} 覆盖 {info.Monitor.Width}x{info.Monitor.Height}={covered}"
                + $"，去掉边框={stripped}，已置顶={raised}，退出后还原={restored}");

        check("全屏时的任务栏", taskbar.Ok, taskbar.Detail);

        // 拖动标题栏移动窗口, driven through the window's own three calls, because the OS move loop this
        // replaced could not be driven from anywhere at all: it was a modal loop inside DefWindowProc, and
        // 「点击标题后窗口会固定在鼠标上」 was the only way to find out it had been entered with no button held.
        // A synthetic grab point and one move — the window has to end up displaced by exactly that delta, and
        // has to stop following once the drag is over.
        Native.GetWindowRect(window.Handle, out var seat);
        var grab = new NativePoint { X = seat.Left + 100, Y = seat.Top + 10 };
        var began = window.BeginDrag(grab);

        window.DragTo(new NativePoint { X = grab.X + 37, Y = grab.Y + 23 });
        Native.GetWindowRect(window.Handle, out var moved);

        window.EndDrag();
        window.DragTo(new NativePoint { X = grab.X + 500, Y = grab.Y + 500 });
        Native.GetWindowRect(window.Handle, out var ignored);

        Native.SetWindowPos(
            window.Handle, Native.HwndTop,
            seat.Left, seat.Top, seat.Width, seat.Height,
            Native.SwpNoZOrder | Native.SwpNoActivate);

        var followed = moved.Left == seat.Left + 37 && moved.Top == seat.Top + 23
            && moved.Width == seat.Width && moved.Height == seat.Height;
        var stopped = ignored.Left == moved.Left && ignored.Top == moved.Top;

        check("拖动标题栏移动窗口", began && followed && stopped && !window.Dragging && dragRefused,
            $"按下={began}，移动 37,23 → {moved.Left - seat.Left},{moved.Top - seat.Top}（尺寸不变="
                + $"{moved.Width == seat.Width && moved.Height == seat.Height}）"
                + $"，松开后不再跟随={stopped}，全屏时不接受拖动={dragRefused}");
    }

    /// <summary>
    /// Whether the taskbar really is out of the way, phrased as the sentence the report prints.
    /// <para>
    /// Measured by asking who is on screen at the middle of the tray rather than by reading the tray's
    /// <c>WS_EX_TOPMOST</c> bit: the bit is explorer's private business and recent Windows keeps it set,
    /// while what the complaint was actually about — 「全屏后 windows 任务栏还在」 — is precisely whose
    /// pixels are at those coordinates. Polled, because explorer answers on its own thread.
    /// </para>
    /// </summary>
    private static (bool Ok, string Detail) ReportTaskbarStandsAside(IntPtr window, NativeRect monitor)
    {
        if (TrayOnScreen(monitor) is not { } tray)
            return (true, "这块屏幕上没有任务栏，无从遮挡");

        var (aside, waited) = PollTray(window, tray.Middle, 400);
        if (aside) return (true, $"任务栏中点上是本窗口（等了 {waited} 毫秒）");

        // 置顶 is tied to being the application in front, on purpose, so a window that lost activation
        // while this ran is behaving correctly by letting the taskbar back over it. Not a failure — but
        // said out loud, because it is also the one way this check can quietly stop meaning anything.
        return Native.GetForegroundWindow() == window
            ? (false, $"等满 {waited} 毫秒，任务栏仍压在画面上")
            : (true, $"等待期间窗口离开前台，任务栏本就该盖回来（等了 {waited} 毫秒）");
    }

    /// <summary>
    /// The taskbar standing on <paramref name="monitor"/> and the middle of it, or null when none of them is
    /// there.
    /// <para>
    /// Both classes, because Windows makes a separate bar for every screen that is not the main one and
    /// 「Shell_TrayWnd」 is only ever the main screen's. Asking about that one alone was enough while the shell
    /// always opened on the main screen; once a self-check run opens on another one (<c>--screen</c>) that
    /// question answers 「the taskbar is on the other monitor, it cannot be covering anything」 for a picture
    /// with a taskbar of its own sitting across the bottom of it.
    /// </para>
    /// </summary>
    private static (IntPtr Tray, NativePoint Middle)? TrayOnScreen(NativeRect monitor)
    {
        foreach (var tray in Trays())
        {
            if (!Native.GetWindowRect(tray, out var bar)) continue;

            var middle = new NativePoint { X = (bar.Left + bar.Right) / 2, Y = (bar.Top + bar.Bottom) / 2 };
            if (middle.X < monitor.Left || middle.X >= monitor.Right) continue;
            if (middle.Y < monitor.Top || middle.Y >= monitor.Bottom) continue;

            return (tray, middle);
        }

        return null;
    }

    /// <summary>Every taskbar there is: the main screen's, and then one per secondary display.</summary>
    private static IEnumerable<IntPtr> Trays()
    {
        var main = Native.FindWindow("Shell_TrayWnd", null);
        if (main != IntPtr.Zero) yield return main;

        var next = IntPtr.Zero;
        while ((next = Native.NextWindow(IntPtr.Zero, next, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            yield return next;
    }


    /// <summary>
    /// Waits for <paramref name="window"/> to be the window on screen at <paramref name="point"/>, keeping
    /// this thread's message loop turning meanwhile: a probe that blocks the UI thread is measuring a hung
    /// window, which is not the app whose behaviour is in question.
    /// </summary>
    private static (bool Ok, long Waited) PollTray(IntPtr window, NativePoint point, int milliseconds)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            while (Native.PeekMessage(out var message, IntPtr.Zero, 0, 0, Native.PmRemove))
            {
                Native.TranslateMessage(ref message);
                Native.DispatchMessage(ref message);
            }

            var hit = Native.WindowFromPoint(point);
            if (hit != IntPtr.Zero && Native.GetAncestor(hit, Native.GaRoot) == window)
                return (true, clock.ElapsedMilliseconds);

            if (clock.ElapsedMilliseconds >= milliseconds) return (false, clock.ElapsedMilliseconds);
            Thread.Sleep(25);
        }
    }
}

using System.Runtime.InteropServices;
using Momoka.Shell.Interop;
using Momoka.Shell.Views;
using Momoka.Shell.Windowing;

namespace Momoka.Shell;

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
    /// geometry, plus the questions that come before the geometry: whether the window in front is even
    /// somebody else's, and whether it has any pixels on screen at all (2026-09-13's telegram/qBittorrent
    /// 隐窗让位).
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

        // The question that comes before the geometry, since 2026-09-13's 「屏幕1全屏播放时点击屏幕2的
        // telegram和qbittorrent会唤出任务栏」: whether the window in front has any pixels to cover at all.
        // A minimized window's rectangle lives at (−32000,−32000) and a hidden helper's often sits right on
        // the primary screen — geometry calls both 「covering the picture」, and yielding to them hands the
        // screen to the taskbar. Staged on two real windows: a message-only dummy that owns no pixels by
        // construction, and the taskbar itself, which very much does.
        var ghost = Native.CreateWindowEx(0, "Static", null, 0, 0, 0, 0, 0,
            Native.HwndMessage, IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);
        var bodiless = ghost != IntPtr.Zero && HostWindow.PutsNoPixelsOnScreen(ghost);
        var embodied = !HostWindow.PutsNoPixelsOnScreen(Native.FindWindow("Shell_TrayWnd", null));
        if (ghost != IntPtr.Zero) Native.DestroyWindow(ghost);

        // And the question asked before even that: whose window is in front. Our own popups are
        // top-level windows sitting right over the picture, and a menu is not the user leaving.
        var mine = HostWindow.SameApp(window.Handle);
        var theirs = HostWindow.SameApp(Native.FindWindow("Shell_TrayWnd", null));

        check("全屏让位只看画面",
            elsewhere && !straddling && !sameScreen && !nameless && bodiless && embodied && mine && !theirs,
            $"另一屏不重叠时保持置顶={elsewhere}，另一屏但压到画面={straddling}"
                + $"，同一屏={sameScreen}，问不出显示器={nameless}"
                + $"，没有像素的前台（隐藏/最小化）不算挡画面={bodiless}，任务栏算挡画面={embodied}"
                + $"；自己的窗口算自家={mine}，任务栏算自家={theirs}");

        if (window.Handle == IntPtr.Zero || window.Fullscreen) return;

        var monitor = Native.MonitorFromWindow(window.Handle, Native.MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!Native.GetMonitorInfo(monitor, ref info))
        {
            check("全屏几何", false, "读不到显示器边界");
            return;
        }

        // 2026-09-14 第二次报上来：屏幕一全屏、点屏幕二的应用，屏幕一的任务栏又爬回画面上。这次日志点了名
        // （0x305EE，AyuGram 那扇 Qt 窗），量下去才看清 —— GetWindowRect 量的是外面那一圈，从 Win10 起还多
        // 包着约 7px 看不见的缩放边框，于是贴着两屏交界摆的窗口凭空「伸进」画面 7 像素，规则判它会挡着、
        // 让出置顶，任务栏就回来了。现在两边都按 DWM 报的可见边框量（`Native.GetVisibleFrame`）。
        //
        // 这一关把那 7 像素钉住，顺便钉住「不能改回外框量」：先拿这扇窗自己量出那圈边框有多厚，再照这个厚度
        // 把「屏幕二上齐着交界的窗口」搭出来 —— 按外框量必须判「会挡着」（旧行为，就是病根），按可见边框量
        // 必须判「不挡」（保持置顶）。没有第二台显示器也能把这件事量出来，靠的正是这条纯粹几何的规则。
        var outer = new NativeRect();
        var visible = new NativeRect();
        var framed = Native.GetWindowRect(window.Handle, out outer)
            && Native.GetVisibleFrame(window.Handle, out visible);

        // 外框比看得见的边框**更外**，所以那圈边框的厚度是「可见的左边减外框的左边」，是正数：本机实测这扇窗
        // 外框 1080×800、看得见的边框 1066×793，也就是左右各 7px、下边 7px。
        var border = framed ? visible.Left - outer.Left : 0;

        var flushVisible = new NativeRect
        {
            Left = info.Monitor.Right,
            Top = info.Monitor.Top,
            Right = info.Monitor.Right + 1080,
            Bottom = info.Monitor.Top + 640,
        };
        var flushOuter = new NativeRect
        {
            Left = flushVisible.Left - border,
            Top = flushVisible.Top,
            Right = flushVisible.Right,
            Bottom = flushVisible.Bottom,
        };

        var outerWouldYield = !HostWindow.StandsClear(info.Monitor, monitor, flushOuter, there);
        var visibleKeepsBand = HostWindow.StandsClear(info.Monitor, monitor, flushVisible, there);

        check("全屏让位量的是看得见的边框",
            framed && (border == 0 || (outerWouldYield && visibleKeepsBand)),
            $"本窗口外框 {outer.Width}×{outer.Height} / 看得见的边框 {visible.Width}×{visible.Height}"
                + $"（左边那圈看不见的边框 {border}px）={framed}；屏幕二上齐着两屏交界的窗口："
                + $"按外框量会让位={outerWouldYield}，按看得见的边框量保持置顶={visibleKeepsBand}");

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
    /// 设置窗口盖在画面上：全屏播放时主窗占着 topmost band（用户还可能亲手钉了 置顶），而 topmost 窗口压着
    /// 任何非 topmost 窗口 —— 设置窗口就是非 topmost 的。播放页右键菜单末尾那三行「打开设置」因此必须先让
    /// 画面站台下（<c>HostWindow.StandAsideForOwnWindow</c>），否则窗口开在画面背后：用户点完什么都看不见，
    /// 而日志里一切正常。这条读数量的是屏幕上谁在最前面。
    /// <para>
    /// 四件都要量，少一件就漏得掉一半：①设置窗口立着时，它正中间那一点属于它自己（不是画面）；②它开在该行
    /// 那张卡上（<c>SettingsPage.Select</c> 认不出的名字会安静地退回「播放器」，层级上看不出任何异样）；
    /// ③画面这会儿真的从 topmost 上下来了（不然①只是碰巧）；④收摊后画面把 topmost 拿了回来 —— 让位是借出去
    /// 的账，拿不回来就是「全屏播放丢了置顶」。
    /// </para>
    /// <para>
    /// 不需要在播：全屏 band 与手动置顶不依赖任何片子，这是这条读数能在没有服务器、不播任何东西的自检里跑的
    /// 原因（也是它放在 <see cref="ReportFullscreen"/> 之后的原因 —— 先把全屏那几关量完，再拿真窗口做这一场）。
    /// </para>
    /// </summary>
    private static void ReportSettingsOverPicture(
        HostWindow window, ShellPage shell, Action<string, bool, string> check)
    {
        var wasTop = window.TopMost;
        var wasFull = window.Fullscreen;
        var topmostOfPicture = false;
        var covered = false;
        var carded = false;
        var reclaimed = false;
        var opened = false;
        var clicked = false;

        // 走玩家真正走的那一条：右键画面菜单末尾那一行的真处理器（表里第一行＝字幕），而不是自检自己调
        // ShowSettings —— 「菜单行 → view model → 外壳 → 窗口」这一整根链子有一节断了，这条读数就要红。
        var link = Momoka.Mpv.PlayerSettingsLinks.All[0];

        try
        {
            window.Fullscreen = true;
            window.TopMost = true;

            clicked = shell.PlayerRoot.ClickPictureSettingsRow(link.Label);

            // 窗口是同一个 UI 线程上开的，但「开出来了」要等框架把帧画上：边等边泵消息，与 ReportTaskbarStandsAside
            // 那条轮询同一个理由 —— 堵着消息循环去量一扇刚建的窗，量到的是没画完的东西。
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (shell.SettingsWindowState is not { Open: true, Handle: not 0 } settings)
            {
                if (clock.ElapsedMilliseconds >= WaitMilliseconds) break;
                Pump();
                Thread.Sleep(25);
            }

            var state = shell.SettingsWindowState;
            opened = state.Open && state.Handle != 0;

            // 开在哪张卡上：与「窗口立起来了」是两件事 —— SettingsPage.Select 认不出的名字会安静地退回
            // 「播放器」，那种错从层级上看完全正常。
            var page = shell.SettingsRoot;
            carded = page is not null
                && string.Equals(page.SelectedCategory, link.Category, StringComparison.Ordinal);

            if (opened && Native.GetWindowRect(state.Handle, out var frame))
            {
                var middle = new NativePoint
                {
                    X = (frame.Left + frame.Right) / 2,
                    Y = (frame.Top + frame.Bottom) / 2
                };

                var hit = Native.WindowFromPoint(middle);
                covered = hit != IntPtr.Zero && Native.GetAncestor(hit, Native.GaRoot) == state.Handle;

                var pictureEx = (long)Native.GetWindowLongPtr(window.Handle, Native.GwlExStyle);
                topmostOfPicture = (pictureEx & Native.WsExTopMost) != 0;
            }

            shell.HideSettings();

            // 收摊那一半：让出去的置顶要回来（等一拍，SetWindowPos 之后风格位才读得到）。
            var back = System.Diagnostics.Stopwatch.StartNew();
            while (back.ElapsedMilliseconds < WaitMilliseconds)
            {
                var pictureEx = (long)Native.GetWindowLongPtr(window.Handle, Native.GwlExStyle);
                if ((pictureEx & Native.WsExTopMost) != 0)
                {
                    reclaimed = true;
                    break;
                }

                Pump();
                Thread.Sleep(25);
            }
        }
        finally
        {
            shell.HideSettings();
            window.TopMost = wasTop;
            window.Fullscreen = wasFull;
        }

        check("设置窗口盖在画面上",
            clicked && opened && covered && carded && !topmostOfPicture && reclaimed,
            !clicked
                ? $"右键画面菜单里点不到「{link.Label}」那一行 —— 三行设置入口没接上"
                : opened
                    ? $"点了菜单里的「{link.Label}」，设置窗口已立起并开在「{link.Category}」卡上={carded}，"
                        + $"它正中间的像素归它自己={covered}；立着时画面仍是 topmost={topmostOfPicture}（应为假）；"
                        + $"收摊后画面拿回置顶={reclaimed}"
                    : "设置窗口没立起来（已回退到主窗口内的设置页），层级无从量");
    }

    /// <summary>窗口等待那一小段里把消息泵开，别让刚建的窗在半路上等我们。</summary>
    private static void Pump()
    {
        while (Native.PeekMessage(out var message, IntPtr.Zero, 0, 0, Native.PmRemove))
        {
            Native.TranslateMessage(ref message);
            Native.DispatchMessage(ref message);
        }
    }

    /// <summary>等一扇窗立起来、等一次层级变更的上限。两条路都只等这一格，超了就是没发生。</summary>
    private const int WaitMilliseconds = 1500;

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
            Pump();

            var hit = Native.WindowFromPoint(point);
            if (hit != IntPtr.Zero && Native.GetAncestor(hit, Native.GaRoot) == window)
                return (true, clock.ElapsedMilliseconds);

            if (clock.ElapsedMilliseconds >= milliseconds) return (false, clock.ElapsedMilliseconds);
            Thread.Sleep(25);
        }
    }
}

using EmbyNian.Playback;
using EmbyNian.Shell.Interop;
using EmbyNian.Shell.Windowing;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 鼠标指针那两关：<c>ProbeCursor</c> 问「藏起来这件事有没有真的到系统那一层」，<c>ProbeCursorAlive</c> 问
/// 「静止两秒之后它到底藏了没有」。中间那几个小手 —— 问系统要当前形状、把指针挪到画面中心、把消息泵一遍 ——
/// 是这两关共用的。
/// <para>
/// 整份自检里最容易被外面的东西打断的就是这一段：<c>SetCursorPos</c> 会被别的前台窗口拒掉，而那两秒里真有人
/// 碰了鼠标读数就不一样了。所以这里每一步都把它实际量到的东西原样写进报告，不只给一个是或否 —— 报告上那句
/// 「XAML 事件 2 次」就是这么来的。拆成几个文件的缘由见 <c>PlayerPage.SelfCheck.cs</c> 的类注释。
/// </para>
/// </summary>
public sealed partial class PlayerPage
{
    /// <summary>
    /// 第十一报里那个注入位移的签名宽度 —— 四天日志里 AyuGram 每一次都恰好把指针横搬 60 像素
    /// （<c>dx=60, dy=0</c>）。探针照原样复刻一次，好让这一关测的是用户那个场景而不是一个随便的位移。
    /// </summary>
    private const int WARP_SIGNATURE = 60;

    /// <summary>
    /// That hiding the cursor actually reaches the OS — which over a WinUI 3 island is not the same thing as
    /// calling the OS. Everything above this is arithmetic and one field of ours; this is the step in between
    /// that nothing else in the app can see.
    /// <para>
    /// It exists because 「鼠标指针还是不会自动隐藏」 was reported against a self-check that passed: every cursor
    /// expectation in it read our own <c>_cursorHidden</c> back, so a call that never left the process looked
    /// exactly like one that worked. It then survived a second pass for the opposite reason —
    /// <c>ShowCursor</c> returned the count it promises, and the cursor stayed on screen anyway, because that
    /// counter governs Win32 surfaces and the pointer was over XAML content.
    /// </para>
    /// <para>
    /// So what is asserted here is the mechanism that does work. A <c>WM_SETCURSOR</c> put to the host window
    /// while the player wants no cursor is answered with 「no cursor」 and not passed on, and the same message
    /// while shown goes straight through; the island's procedure is asked the same pair of questions, because
    /// the island's popups are separate windows that ask separately; the interception is installed; and the show
    /// count still swings below zero and back for the surfaces it does govern. The host window is asked first
    /// because it is the window a moving pointer actually asks — the island's bridge is measured here to receive
    /// classic mouse messages by the dozen and <c>WM_SETCURSOR</c> never, its input arriving through the
    /// InputSite APIs instead, which is why 「no cursor」 is also re-said on the player's ten-hertz tick rather
    /// than resting on any one message.
    /// </para>
    /// <para>
    /// 单传感器（2026-09-15 重构）之后这一段也少了几条旧断言，随「六条杠杆砍到三条」一起退休：手工
    /// <c>InputPointerSource.Cursor</c> 往返（那一对如今由每拍 <c>Root.Cursor = BlankInputCursor</c> 承担，
    /// 见 <c>PlayerPage.Chrome.cs</c>，自检另有专条判它）、以及 <c>NudgeCursorState</c> 发布。类光标那一格
    /// 一度也被砍过，被自检当场抓了回来（见「藏了以后窗口树上的类光标也换成了透明的」那一行），理由写在
    /// <c>HostWindow.BlankClassCursors</c>：它是唯一够得着别的线程拥有的窗口的杠杆。退休的理由都是同一个：
    /// 每多一条杠杆就多一个自伤源，但每一条真的在扛事的都不能砍。
    /// </para>
    /// <para>
    /// The reading the assertions are actually made of is <c>GetCursor</c>: the shape this thread's queue has,
    /// which is 「none」 while hidden and a real handle otherwise, and which the OS updates when
    /// <c>SetCursor</c> is called rather than when the pointer next moves. The OS's <c>CURSORINFO</c> is printed
    /// at every step here and asserted at none of them, because it cannot settle the question: its 「showing」
    /// bit is about the desktop's cursor, so it reads 「显示」 whenever another application owns the foreground,
    /// and even with the desktop ours it does not follow this thread's <c>SetCursor</c> while the pointer holds
    /// still — measurable in the run above by the 等待 shape that never appears in it while appearing in
    /// <c>GetCursor</c> immediately. Nothing moves the pointer after the setup, on purpose: a move is a request
    /// for the cursor back. So <c>CURSORINFO</c> stays in the report as an outside witness rather than as
    /// evidence.
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) ProbeCursor()
    {
        if (!Attached || _window is null) return (false, "播放层未接线");

        // No island means the interception has nowhere to sit and half the probe has nothing to send to. The
        // pointer is over the island; the cursor question about it is the host window's.
        var island = _window.IslandHandle;
        if (island == IntPtr.Zero) return (false, "还没有 XAML 岛");

        var was = Visibility;
        Visibility = Visibility.Visible;
        UpdateLayout();

        var clock = Now;
        var report = new List<string>();
        var wrong = new List<string>();

        // Normalised rather than assumed: the reveal probe runs first and leaves the cursor shown, and
        // hiding what is already hidden would prove nothing about either transition.
        SetCursorHidden(false);

        Want("接管岛的光标消息", _window.CursorHookInstalled);
        report.Add($"接管岛的光标消息={_window.CursorHookInstalled}");

        // Both cleared up front rather than declared by the `out`s below: the chain short-circuits, and the
        // compiler is right that a `centre` the restore reads must be assigned on every path to it.
        NativePoint origin = default;
        NativePoint centre = default;

        var asked = _window.CursorAsksSeen;

        // 「Already there」 counts as not moved: a pointer that does not travel produces no WM_SETCURSOR, and
        // the question below would then be asked of a move that never happened.
        var moved = Native.GetCursorPos(out origin)
            && PictureCentre(out centre)
            && (origin.X != centre.X || origin.Y != centre.Y)
            && Native.SetCursorPos(centre.X, centre.Y);

        // Drained once, here, and the pointer then left alone for the rest of the probe: a pointer that moves
        // reaches OnPointerMoved, which is a request for the cursor back — it would undo the thing being
        // measured a millisecond after it was set up.
        Pump();

        var host = _window.Handle;
        var onUs = moved && Native.GetAncestor(Native.WindowFromPoint(centre), Native.GaRoot) == host;

        // Reported, not asserted, and the two conditions kept apart because they answer different questions:
        // WM_SETCURSOR goes to whatever window the pointer is over whether or not that window is active, so
        // 「中心上是本窗口」 is what makes the count below meaningful and 「在前台」 is only about the OS reading.
        // The count is the one measure of the island's real messages reaching our procedure rather than being
        // answered on one of the island's inner windows — which is why the hidden state is also re-asserted on
        // the tick and does not rest on this.
        report.Add($"画面中心={(moved ? $"挪到 {centre.X},{centre.Y}" : "没挪")}"
            + $"，在前台={Native.GetForegroundWindow() == host}"
            + $"，中心上是本窗口={onUs}"
            + $"，中心上的窗口={ClassOf(Native.WindowFromPoint(centre))}"
            + $"，接管的是={ClassOf(island)}"
            + $"，岛消息共 {_window.IslandMessagesSeen}"
            + $"，真移动问到 {asked}→{_window.CursorAsksSeen}"
            + $"，起点 {Says()}");

        // The control reading, and the one that decides whether anything below is worth reading: a shape the OS
        // can name, set from this thread, read back out of this thread's own cursor state. If 等待 does not come
        // back here then SetCursor is not reaching the queue at all and 「藏不住」 would be the honest answer.
        var wait = Native.LoadCursor(IntPtr.Zero, Native.WaitCursor);
        Native.SetCursor(wait);

        Want("设得上形状", wait != IntPtr.Zero && Native.GetCursor() == wait);
        report.Add($"设成等待：线程形状={Mine()}，系统 {Says()}");

        Native.SetCursor(Native.LoadCursor(IntPtr.Zero, Native.ArrowCursor));

        // The pointer input source, asked only whether it can be reached — the hand-rolled
        // <c>InputPointerSource.Cursor</c> round trip this used to stage (save, hide, restore, and a new shape
        // surviving the restore) is retired as of 2026-09-15 along with <c>HostWindow.KeepInputCursorHidden</c>:
        // it was a second writer to the same lever, competing with the page's own per-tick
        // <c>Root.Cursor = BlankInputCursor</c>. What remains here is the reachability question, because the
        // probe's other input-source readings below still need the object.
        var inputSource = InputPointerSource.GetForIsland(Root.XamlRoot.ContentIsland);
        Want("能取得岛的指针输入源", inputSource is not null);

        // ---- 藏起来 ----
        SetCursorHidden(true);

        Want("藏了以后窗口知道", _window.CursorHidden);
        Want("藏了以后显示计数为负", _cursorCount < 0);

        // The lever the other four were missing, and the one this round added. While the pointer is over XAML
        // content the shape on screen is the framework's, and none of the Win32 levers is on that path — a real
        // film's log has a two-minute hide with this queue blank the whole way and GetCursorInfo answering
        // 「system arrow」 throughout. So: the transparent cursor has to be wrappable as the framework's own
        // type, and the picture has to be holding it. Both are ours alone to get right, so both are judged.
        Want("透明光标包成框架的了", _window.BlankInputCursor is not null);
        Want("藏了以后框架的光标也换成透明的", ReferenceEquals(Root.Cursor, _window.BlankInputCursor));
        report.Add($"框架光标：包得出={_window.BlankInputCursor is not null}"
            + $"，藏着时画面上是{(Root.Cursor is null ? "默认" : "透明")}");
        Want("透明位图可识别", Native.CursorIsTransparent(_window.BlankCursor));
        Want("等待光标不能误判透明", !Native.CursorIsTransparent(wait));
        Want("系统箭头不能误判透明", !Native.CursorIsTransparent(Native.LoadCursor(IntPtr.Zero, Native.ArrowCursor)));

        // The assertion the whole fix stands on: the thread's cursor is 「none」, which is what a still pointer
        // over the picture is looking at. Everything else here is about keeping it that way.
        Want("藏了以后线程真的没有形状", NoShape());
        report.Add($"藏：窗口={_window.CursorHidden}，计数={_cursorCount}，线程形状={Mine()}，系统 {Says()}");

        // 第九报（2026-09-15，自检抓回来的回归）：类光标是唯一一条够得着「别的线程拥有的窗口」的杠杆。
        // 那次重构一度把它跟全树扫描一起砍了，这一格当场判红：指针压在画面的岛桥窗口
        // （Microsoft.UI.Content.DesktopChildSiteBridge，属框架线程）上时，桌面光标记录变回系统箭头，
        // 10 秒观察 933 次采样全部命中；而指针压在自己窗口上藏匿时读数是 [标志 0x00，形状 0x0]。
        // 本进程的 SetCursor/ShowCursor 只说给拥有队列的窗口，那个窗口的队列不是我们的——只剩类光标。
        Want("藏了以后窗口树上的类光标也换成了透明的", _window.ClassCursorsBlanked > 0);
        report.Add($"类光标：藏着换掉 {_window.ClassCursorsBlanked} 个类（这一趟共扫到 {_window.ClassCursorsSwept} 个）");

        // 藏鼠标之后，屏上是不是真的没有光标 —— 而这一句问的是「不挪鼠标能不能问到答案」，因为它替代的正是那条被
        // 退休的路：以前这里发一发注入的「一像素出去、一像素回来」（<c>Native.NudgeCursorState</c>），逼框架重新
        // 念一遍 <c>ProtectedCursor</c>。那一下确实有效，代价是它本身就是一个真实指针事件 —— 岛会为它抬一次
        // <c>PointerMoved</c>，DPI 缩放与绝对坐标取整把它的一像素读成两像素，于是「算作动手」的阈值被越过、静止时钟
        // 重新盖章，藏下去的光标一拍之后自己冒出来（2026-09-14 第二次报告，日志原文「框架事件走了 2,0 逻辑像素」）。
        // 成熟播放器没有一家靠注入刷新光标（mpv 答 WM_SETCURSOR、VLC 与 MPC-HC 直接 SetCursor、IINA 用系统 API），
        // 所以这一条退休，改由下面那两声 <c>WM_SETCURSOR</c> 承担。
        // <para>
        // 这里不再断言注入发得出去，改成断言「重申得出去」：不碰指针，把策略再说一遍，然后读线程的形状。这正是
        // 真放片子时每一拍做的事（<c>PlayerPage.Nudge</c> → <c>HostWindow.KeepCursorHidden</c>），而它不产生任何
        // 指针事件 —— 也就是这条修复的全部要点。
        // </para>
        asked = _window.CursorAsksSeen;
        var queueShape = Native.GetCursor();
        _window.KeepCursorHidden();
        var displayDeadline = Now + 1200;
        do
        {
            Pump();
            Nudge();
            ChaseForeignCursor();
            Thread.Sleep(10);
        } while (Now < displayDeadline && !ScreenHasNoCursor());
        var displayGone = ScreenHasNoCursor();
        if (Native.GetCursorPos(out var stationary)
            && stationary.X == centre.X && stationary.Y == centre.Y
            && Native.GetAncestor(Native.WindowFromPoint(stationary), Native.GaRoot) == host)
            Want("静止指针的桌面光标确实隐藏", displayGone);
        report.Add($"静止桌面重申：系统无可见光标={displayGone}，{Says()}");
        var samples = 0;
        var flashes = 0;
        var watchUntil = Now + 10000;
        while (Now < watchUntil)
        {
            Pump();
            Nudge();
            ChaseForeignCursor();
            // 第九报（2026-09-15 15:40）：加前台门，与下面 Screen 的三道门同构。这一轮自检（负计数
            // 锁上机后）此观察 922/922 全程「系统箭头」而红，但同一份报告里「在前台=False」——前台在别人手里
            // 时桌面光标归那个进程的队列，读到的箭头是别人的；负计数锁是本队列私有的，管不到也不该管到。
            // 本轮所有与本实现相关的判据（计数=-2、线程形状=无、放回一拍藏回、输入源三次恢复）全绿。
            // 只判「指针压在本窗口」而不判「前台是否本窗口」，等窗（SelfCheck.Cursor.cs 八报注释）自己写明
            // 的「showing bit 是桌面的」那个坑又踩了一遍。
            var front = Native.GetForegroundWindow() == host;
            if (Native.CursorSnapshot() is { } sample
                && front
                && Native.GetAncestor(Native.WindowFromPoint(sample.At), Native.GaRoot) == host)
            {
                samples++;
                if ((sample.Flags & 1) != 0 && sample.Shape == Native.LoadCursor(IntPtr.Zero, Native.ArrowCursor))
                    flashes++;
            }
            Thread.Sleep(10);
        }
        // 第九报（2026-09-15，用户原话「你直接抄这些开源项目吧」这一轮加的前提门）：这里的藏匿是页面
        // 领跑的——chrome 状态机（ChromeReveal）不知情，它的 CursorHidden 仍是 false。藏匿途中任何
        // ChromeReveal 翻转都会经 Render 的光标同步路（SetCursorHidden(_chrome.CursorHidden)）把藏匿
        // 掀回去。15:42:52.924 的现场：探针自己的 Pump 把排队的 VM 快照派发了，SetKeep(false) 重盖
        // 活跃时钟 → Settle 翻转 → Render → 藏了 1.25 秒的光标被放回去，此后 13 条判据测的全是
        // 「已显示」态，连带 14 条「不符」。负计数锁的判据（计数=-2、每拍重压幂等、还原拉回非负）
        // 在那份报告里全绿——这是探针与状态机的既有竞态（全天日志自检失败数一直在 1/2/3 之间摆），
        // 不是本轮改动引入的。与上面「前台不在本窗口，这一读数不判」同构：前提没了，判据就是在对
        // 空气断言——打印、跳过、不装绿。
        var staged = _window.CursorHidden;
        if (!staged)
            report.Add("藏匿中途被掀了（chrome 状态机经 Render 同步路显示——既有竞态，与负计数锁无关），以下藏匿态判据不判");

        if (samples > 0) Want("持续采样没有闪回系统箭头", flashes == 0);
        report.Add($"持续观察10秒：有效采样 {samples} 次，箭头 {flashes} 次"
            + (samples > 0 ? string.Empty : "（前台不在本窗口，这一读数不判）"));
        var queueWas = queueShape == IntPtr.Zero ? "无" : $"0x{queueShape:X}";
        report.Add($"重申一遍（不挪鼠标）：线程形状 {queueWas}→{Mine()}，我们这两个过程问到 {asked}→{_window.CursorAsksSeen}");
        if (staged) Want("重申之后线程还是没有形状", NoShape());

        // The message a still pointer never sends, sent by hand — to the host window first, because that is
        // where a moving pointer's own WM_SETCURSOR actually arrives. This is the assertion the probe is built
        // around: while the player wants no cursor, the answer is none and the message is not passed on.
        var answered = _window.CursorHidesAnswered;
        var reply = Native.SendMessage(host, Native.WmSetCursor, host, Moving());

        if (staged) Want("藏着时主窗口的消息由我们回答", _window.CursorHidesAnswered == answered + 1);
        if (staged) Want("藏着时回答的是「已设好」", (long)reply == 1);
        report.Add($"藏着问主窗口：回答={(long)reply}，接管 {answered}→{_window.CursorHidesAnswered}，{Says()}");

        // And the island's own procedure, for the messages that do reach it — a popup or a flyout of the
        // island's is a separate window and asks separately.
        answered = _window.CursorHidesAnswered;
        Native.SendMessage(island, Native.WmSetCursor, island, Moving());

        if (staged) Want("藏着时岛的消息也由我们回答", _window.CursorHidesAnswered == answered + 1);
        report.Add($"藏着问岛：接管 {answered}→{_window.CursorHidesAnswered}");

        // ---- 被别人放回形状之后 ----
        // The failure mode the ten-hertz re-assertion exists for, staged: something outside this file puts a
        // shape back while the player still wants none. In the real case it is XAML's own input site, which sets
        // the cursor from its pointer handling and never asks the window procedure — the reason the two counts
        // above are not the whole mechanism. Here it is one SetCursor of ours standing in for it, which is the
        // same thing as far as the thread's cursor state is concerned.
        Native.SetCursor(Native.LoadCursor(IntPtr.Zero, Native.ArrowCursor));
        Want("被放回的形状真的放回了", !NoShape());

        _window.KeepCursorHidden();
        if (staged) Want("下一拍就又藏回去", NoShape());
        report.Add($"被放回箭头后，一拍就藏回：线程形状={Mine()}");

        // 第九报（2026-09-15）退休的那一段在更早的版本里站在这儿：把 <c>inputSource.Cursor</c> 连续三次改成
        // 真箭头，再断言「不移动鼠标也能恢复无光标」。它测的是 <c>HostWindow.KeepInputCursorHidden</c> 那条
        // 手工往返，而那条往返随三件套上车一起退休了 —— 现在藏匿期唯一写输入源的人是页面自己（<c>Render</c>
        // 里每拍 <c>Root.Cursor = BlankInputCursor</c>），覆盖与恢复都是同一个赋值，没有「恢复」这一步可测。
        // 页面那一侧仍有专条（<c>PlayerPage.SelfCheck.Cursor.cs</c> 的「藏着时画面上的光标是透明的」与
        // <c>ProbeCursorAlive</c> 里同样的判据），留在这里的是它退休的理由，不是一条对空气的断言。

        // ---- 还原 ----
        SetCursorHidden(false);

        Want("还原以后窗口知道", !_window.CursorHidden);
        Want("还原以后显示计数归零", _cursorCount >= 0);
        Want("还原以后线程又有形状", !NoShape());
        Want("还原以后类光标一个不剩地放回去了", _window.ClassCursorsBlanked == 0);

        // Handed back to the framework, and this one matters more than it looks: left holding the transparent
        // cursor, the picture would have no pointer over it for the rest of the session.
        Want("还原以后画面把光标交还给框架", Root.Cursor is null);
        Want("还原以后输入源不再持有透明光标", inputSource is not null
            && !(inputSource.Cursor is null));
        report.Add($"还原：窗口={_window.CursorHidden}，计数={_cursorCount}，线程形状={Mine()}，系统 {Says()}"
            + $"，类光标剩 {_window.ClassCursorsBlanked} 个没还");

        // Both of them again, and neither may answer this time: a shown cursor is whatever the window under the
        // pointer says it is, and an interception that kept answering would pin the arrow away for good.
        answered = _window.CursorHidesAnswered;
        Native.SendMessage(host, Native.WmSetCursor, host, Moving());
        Native.SendMessage(island, Native.WmSetCursor, island, Moving());

        Want("显示时光标消息照原样传下去", _window.CursorHidesAnswered == answered);
        report.Add($"显示时问两处：接管不动={_window.CursorHidesAnswered == answered}，{Says()}");

        // Put back where it was found. The cursor itself needs nothing put back — the pair above balanced
        // itself, and a probe that left it hidden would leave it hidden over every window in the process.
        if (moved) Native.SetCursorPos(origin.X, origin.Y);

        // Left the way a player that is not running should be, by the same two calls the reveal probe ends
        // with: anything else paints a transport bar across the library grid behind this page.
        _chrome.Reset(++clock);
        _chrome.Tick(clock + SettleMilliseconds);
        Render();

        Visibility = was;
        UpdateLayout();

        return (wrong.Count == 0,
            string.Join("；", report) + (wrong.Count == 0 ? string.Empty : $"；不符：{string.Join('、', wrong)}"));

        void Want(string what, bool ok)
        {
            if (!ok) wrong.Add(what);
        }

        // MAKELONG(HTCLIENT, WM_MOUSEMOVE): 「the pointer is over your client area, having just moved」, the
        // one form of the message the interception exists to answer.
        static IntPtr Moving() => new((int)((Native.WmMouseMove << 16) | Native.HtClient));
    }

    /// <summary>
    /// Which window is which, in the OS's own words. The island is several windows deep and only one of them
    /// is under the pointer; a subclass on the wrong one answers nothing.
    /// </summary>
    private static string ClassOf(IntPtr window)
    {
        if (window == IntPtr.Zero) return "无";

        var buffer = new char[256];
        var length = Native.GetClassName(window, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : "问不出";
    }

    /// <summary>Only a successful system snapshot with no visible pixels proves the cursor is hidden.</summary>
    private bool ScreenHasNoCursor() =>
        Native.CursorSnapshot() is { } cursor && ScreenCursorGone(cursor.Flags, cursor.Shape);

    /// <summary>Whether the pointer is where it was just asked to go — an injection can be dropped silently.</summary>
    private static bool Landed(NativePoint at) =>
        Native.GetCursorPos(out var now) && Math.Abs(now.X - at.X) <= 1 && Math.Abs(now.Y - at.Y) <= 1;

    /// <summary>
    /// Whether this process can steer the pointer at all right now, proved rather than assumed: one pixel over
    /// and straight back, and the reading in between has to agree. When the input desktop is somebody else's —
    /// a lock screen, a consent prompt — every cursor call quietly does nothing and <c>GetCursorInfo</c> keeps
    /// answering with whatever was last on screen, which looks exactly like a cursor that refuses to hide. One
    /// pixel is below the two the poll treats as noise, so the stillness this runs inside is not disturbed.
    /// </summary>
    private static bool Steers(NativePoint at)
    {
        if (!Native.SetCursorPos(at.X + 1, at.Y)) return false;

        var steered = Native.GetCursorPos(out var now) && now.X == at.X + 1 && now.Y == at.Y;
        Native.SetCursorPos(at.X, at.Y);
        return steered;
    }

    /// <summary>
    /// What the OS says is on screen, printed whole rather than as one bit: 「显示」 with the show count at −1
    /// is either a call that did not take or a flag that does not mean what it looks like, and only the shape
    /// handle beside it tells those two apart.
    /// <para>
    /// The handle is labelled, because the report was full of bare <c>0x10003</c> and the next person to read it
    /// had to go and look that up: it is the shared handle <c>LoadCursor(NULL, IDC_ARROW)</c> returns, which is
    /// to say 「the framework is drawing its own arrow」.
    /// </para>
    /// </summary>
    private string Says()
    {
        if (Native.CursorSnapshot() is not { } cursor) return "问不出";

        var label = cursor.Shape == IntPtr.Zero ? "没有"
            : _window is { } window && cursor.Shape == window.BlankCursor ? "我们的透明光标"
            : cursor.Shape == Native.LoadCursor(IntPtr.Zero, Native.ArrowCursor) ? "系统箭头"
            : "别的形状";

        return $"[标志 0x{cursor.Flags:X2}，形状 0x{cursor.Shape:X}（{label}）]";
    }

    /// <summary>
    /// What this thread's queue says its cursor is — the reading both cursor probes are made of, because it
    /// is the one the OS updates the moment <c>SetCursor</c> is called rather than the next time the pointer
    /// moves.
    /// </summary>
    private string Mine() => NoShape() ? "无" : $"0x{Native.GetCursor():X}";

    /// <summary>
    /// Whether this thread's queue holds nothing visible. Not 「the handle is zero」: the player hides the
    /// cursor by setting a transparent shape rather than none, because a class cursor of none means 「this
    /// class does not set the cursor」 and leaves the last arrow on screen.
    /// </summary>
    private bool NoShape() => _window?.CursorShapeGone ?? Native.GetCursor() == IntPtr.Zero;

    /// <summary>
    /// Runs the message queue dry. Both cursor probes need it: one to deliver the single pointer move it sets
    /// up, the other because it spends three seconds of real time on the UI thread and everything the player
    /// does in those three seconds arrives as a message.
    /// </summary>
    private static void Pump()
    {
        while (Native.PeekMessage(out var message, IntPtr.Zero, 0, 0, Native.PmRemove))
        {
            Native.TranslateMessage(ref message);
            Native.DispatchMessage(ref message);
        }
    }

    /// <summary>
    /// The same hiding, with nothing about it made up: real wall-clock seconds, the real ten-hertz ticker, the
    /// real pointer sitting where a hand would leave it, and the answer read out of the OS at the end.
    /// <para>
    /// It exists because 「没有变化」 came back twice against a self-check that passed, and both times the reason
    /// was the same shape of mistake — every other cursor check here supplies its own timestamps. A rule driven
    /// by a clock the probe invents cannot be caught restamping that clock, and restamping it is exactly what
    /// happened: WinUI raises <c>PointerMoved</c> for a pointer that never moved, once for each change to the
    /// tree beneath it, and the chrome collapsing 650 ms into a stillness is one such change. Under a hand
    /// resting in the middle of the picture that event reveals nothing, so the chrome went down on time and
    /// looked right, and the cursor's two seconds started over invisibly. The only probe that can see this is
    /// one that lets real time pass and does not touch the clock.
    /// </para>
    /// <para>
    /// Both window shapes, because the report is 「全屏播放且鼠标在画面上时」 and full screen is where the page has
    /// no client edge for the pointer to leave by: windowed, a pointer that wanders off clears the whole state
    /// on its own, and that fallback is not available on the screen the user is actually watching.
    /// </para>
    /// <para>
    /// A hand on the mouse during the run would restart the two seconds legitimately, which is why the pointer
    /// is put back at the picture's centre and checked at the end: found somewhere else, the leg is printed and
    /// asserted about nothing. The alternative is a self-check that fails on a machine someone is using.
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) ProbeCursorAlive()
    {
        if (!Attached || _window is null) return (false, "播放层未接线");
        if (!Native.GetCursorPos(out var origin)) return (false, "问不出指针位置");

        var was = Visibility;
        var wasFull = _window.Fullscreen;
        var wasTicking = _ticker.IsEnabled;

        Visibility = Visibility.Visible;
        UpdateLayout();

        // The real driver, started if the player is not up: what is being measured is whether a hundred
        // milliseconds' worth of the page's own tick, thirty times over, comes to a hidden cursor.
        if (!wasTicking) _ticker.Start();

        var report = new List<string>();
        var wrong = new List<string>();

        // Set by Watch when it had to take the foreground to place the pointer, read by Screen. The display
        // cursor does not change hands the moment the foreground does, so a leg that began with a grab is one
        // whose system-level reading still belongs to the window we took it from — the same rule Screen applies
        // to a grab of its own, and it has to cover this one too or that guard is simply moved out of its way.
        var grabbed = false;

        // Whether the island actually received pointer input during this leg. The gate on the system-level
        // reading below, and the correction this round makes to the probe: every previous run reported
        // 「XAML 事件 0 次」, because SetCursorPos moves a coordinate without producing input — so four rounds of
        // 「fixed」 were measured in the one situation where the bug cannot occur.
        var heard = false;

        Watch("窗口化", fullscreen: false);
        Watch("全屏", fullscreen: true);

        if (!wasTicking) _ticker.Stop();

        _window.Fullscreen = wasFull;
        Native.SetCursorPos(origin.X, origin.Y);
        Pump();

        // Left the way a player that is not running should be, by the same two calls the other probes end
        // with, and with the cursor shown — hidden, it would stay hidden over every window in the process.
        SetCursorHidden(false);

        var clock = Now;
        _chrome.Reset(clock);
        _chrome.Tick(clock + SettleMilliseconds);
        Render();

        Visibility = was;
        UpdateLayout();

        return (wrong.Count == 0,
            string.Join("；", report) + (wrong.Count == 0 ? string.Empty : $"；不符：{string.Join('、', wrong)}"));

        // One window shape, from a shown cursor to a hidden one and back, in real time.
        void Watch(string where, bool fullscreen)
        {
            // Per leg, not per probe: by the second one the foreground taken for the first has long since
            // settled, and carrying the flag over would skip a reading that is perfectly good.
            grabbed = false;
            heard = false;

            _window!.Fullscreen = fullscreen;
            Pump();
            UpdateLayout();

            SetCursorHidden(false);
            _chrome.Reset(Now);
            Render();

            if (!PictureCentre(out var centre))
            {
                wrong.Add($"{where}问不出画面中心");
                return;
            }

            // Away from the centre first, so that landing on it is a real displacement. The OS recomputes which
            // queue owns the pointer, and asks it for a shape, when the pointer moves — so a placement that ends
            // where the pointer already was leaves it holding the arrow it was showing before the hide, and the
            // system-level reading in <see cref="Screen"/> then measures a stale cache rather than this rule.
            // A hand arriving at the picture is a displacement too, which is what this probe is standing in for.
            //
            // Through the real input queue rather than by SetCursorPos, and that is this round's correction to
            // the probe itself: SetCursorPos moves the coordinate and produces no input, so the island hears
            // nothing — 「XAML 事件 0 次」 in every previous report — and a pointer the island never heard about
            // is precisely the situation in which this bug cannot occur. Four rounds of 「fixed」 were measured
            // that way. SetCursorPos stays as the fallback, since an injection can be refused outright.
            if (!Native.MovePointerTo(centre.X + 40, centre.Y + 40))
                Native.SetCursorPos(centre.X + 40, centre.Y + 40);

            Pump();

            // The OS will not move the pointer for a process that is not in the foreground, so one grab at it
            // first — and if that does not take, a leg that says so and asserts nothing. On a machine someone
            // else is using, a chat window taking focus back is enough to deny the move, and that is nobody's
            // defect; it is the same tolerance as 「中途有人动了鼠标」 below, for the same reason.
            if (!Native.MovePointerTo(centre.X, centre.Y) || !Landed(centre))
            {
                Native.SetForegroundWindow(_window!.Handle);
                Pump();
                grabbed = true;

                if (!Native.MovePointerTo(centre.X, centre.Y) && !Native.SetCursorPos(centre.X, centre.Y))
                {
                    report.Add($"{where}：放不到画面中心，输入不在我们手上，这一轮只作参考");
                    return;
                }
            }

            Pump();

            var moves = _pointerMoves;
            var polls = _polledMoves;
            var ticks = _tickCount;
            var nudges = _cursorNudges;

            // Whether the island hears pointer input at all when something else provides it. This used to be a
            // probe of the hide's own injection (<see cref="Native.NudgeCursorState"/>) — 「did a XAML pointer
            // event arrive」 — and that injection is gone, so what is left to ask is the question the new
            // mechanism turns on: with the pointer parked and nothing moving it, does anything reach the island
            // on its own? It does not have to for the fix to hold (the class-cursor sweep and the WM_SETCURSOR
            // interception do not need an event), but the answer is what says whether a probe on this thread can
            // ever exercise the event path, and 「真实输入注不进」 has been the standing reason it cannot.
            var quiet = _stillMoves;

            Pump();
            heard = _pointerMoves > moves || _stillMoves > quiet;
            moves = _pointerMoves;

            // Nothing known about the pointer, exactly as at the start of a playback, so the first tick of the
            // loop below seeds it from the OS — through the same poll a film goes through. Deliberately not
            // seeded by hand: the events are the one path a probe on this thread cannot make WinUI take, and
            // hand-seeding it is what let two rounds of 「没有变化」 pass a check that looked thorough.
            _polledKnown = false;

            var began = Now;
            var span = ChromeReveal.CursorIdleMilliseconds + 700;
            var due = began;
            var pushed = 0;
            long hiddenAt = 0;

            while (Now - began < span)
            {
                Pump();

                if (Now >= due)
                {
                    due += 100;
                    pushed++;
                    OnTick(this, EventArgs.Empty);
                }

                if (hiddenAt == 0 && _cursorHidden) hiddenAt = Now;

                Thread.Sleep(5);
            }

            if (hiddenAt == 0 && _cursorHidden) hiddenAt = Now;

            // Whether anybody touched the mouse. A hand on it restarts the two seconds for real, so a leg that
            // was disturbed is printed and asserted about nothing. Two signals, and the second one is this
            // round's correction — three reports of this leg going red were filed against 「someone touched the
            // mouse, the report says so」 while quoting a number that says the opposite:
            //
            //  · The end position. Cheap and certain when it differs, but blind to a pointer that moved during
            //    the window and was put back — and blind is what it was, because it is the only signal there was.
            //  · The polled count, which is the one that actually answers the question. <c>_polledKnown</c> is
            //    cleared just above, so the loop's first poll seeds the reference and says nothing else —
            //    2026-09-17 起播种不计数也不唤醒（第一次读数不是位移，两个状态同一问 HandStep）。所以
            //    undisturbed 的腿报 0，而任何一个被计数的移动都是真位移：离冻参照过了
            //    <see cref="ChromeReveal.MovePixels"/> 的那种，桌面抖动绕着停点打转够不着它。无论哪种，
            //    空闲钟都重启了，这一轮没什么可判的。
            //
            // A regression cannot hide behind this. Hiding that stops working reports 0 polled moves and fails;
            // a spurious un-hide from a pointer that never moved arrives as a XAML event (「空事件」) and never
            // touches this counter, because it only advances when the OS's own coordinate changed.
            var polled = _polledMoves - polls;
            var moved = !Native.GetCursorPos(out var ended) || ended.X != centre.X || ended.Y != centre.Y;
            var disturbed = moved || polled > 0;

            report.Add($"{where}：{(hiddenAt == 0 ? $"{span}ms 过去也没藏" : $"静止 {hiddenAt - began}ms 就藏了")}"
                + $"，线程形状={Mine()}"
                + $"，真实输入{(heard ? "到位" : "注不进")}"
                + $"，轮询问出 {polled} 次移动{(polled == 0 ? "（开头那次播种不计数，也就是全程没人碰）" : $"（真的动了 {polled} 次）")}"
                + $"、XAML 事件 {_pointerMoves - moves} 次、空事件 {_stillMoves} 次"
                + $"，我们推了 {pushed} 拍、计时器自己 {Math.Max(0, _tickCount - ticks - pushed)} 拍"
                + $"，重申了 {_cursorNudges - nudges} 次（不碰指针）"
                + (disturbed
                    ? $"，中途有人动了鼠标（{(moved ? "指针没停在原处" : "轮询问出了真移动")}），这一轮只作参考"
                    : string.Empty));

            if (disturbed) return;

            // The seeding must have happened inside the loop, or 「藏了」 below would be true of a rule that
            // never knew where the pointer was — and a rule that believes the pointer is nowhere hides nothing.
            // The seed has been silent since 2026-09-17 (it is not a movement, so it counts nothing), which
            // makes the reference itself the observable.
            Want($"{where}轮询把指针的参照立起来了", _polledKnown);

            // The whole of the request, asked of real seconds: mpv.net 的 cursor-autohide，静止 1000ms
            // 就藏（2026-09-16 照搬，原先的「两秒」随用户拍板一起改）。
            Want($"{where}静止一秒后鼠标真藏了", _cursorHidden);
            Want($"{where}藏着的时候线程没有形状", NoShape());

            // And the lever that reaches the pixels the pointer is actually over: the picture's own shape. The
            // Win32 levers above govern every window except the one the pointer is on.
            if (_cursorHidden)
                Want($"{where}藏着时画面上的光标是透明的", ReferenceEquals(Root.Cursor, _window!.BlankInputCursor));

            // And the half of hiding that only this count can vouch for. The framework reads ProtectedCursor
            // while it handles pointer input, and this hide happens because nothing is moving — so the policy
            // has to be restated rather than said once. It used to be restated by injecting a one-pixel round
            // trip; since 2026-09-14 it is restated without touching the pointer at all (see PlayerPage.Nudge),
            // which is the whole point: the restatement must not be an input event. That is what
            // 「静止超过两秒后鼠标指针还是不会自动隐藏」 was about, from a player whose own readings all said
            // hidden. The desktop's own answer cannot stand in for this — see Screen.
            Want($"{where}藏的时候重申过一遍", _cursorNudges > nudges);

            if (_cursorHidden)
            {
                // 2026-09-15 单传感器重构：这一段原来测的是 <c>HostWindow.KeepInputCursorHidden</c> —— 把
                // <c>InputPointerSource.Cursor</c> 覆盖成真箭头，再断言隐藏期每拍把它压回 null。那条往返
                // 已经退休（它和页面自己的 <c>Root.Cursor</c> 是同一个杠杆上的两个写者，会互相打架），现在
                // 藏匿期写光标的人是页面自己：<c>Render</c> 里那句 <c>Root.Cursor = _window.BlankInputCursor</c>，
                // 由真实计时器每拍重说。所以这里改成测那一句 —— 覆盖 <c>Root.Cursor</c>（框架给控件选形状
                // 时走的就是这个属性），再让真实计时器推几拍，必须被重新压回透明。
                //
                // 这也正是用户报的那个场景：鼠标压在画面（XAML 内容）上时形状归框架决定，Win32 那几条杠杆
                // 一条都不在它的路上，唯一的把手就是这个属性。
                var beforeRepair = _tickCount;
                var beforeMoves = _polledMoves;
                using var arrow = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
                Root.Cursor = arrow;
                var until = Now + 1000;
                while (Now < until && ReferenceEquals(Root.Cursor, arrow))
                {
                    Pump();
                    Thread.Sleep(5);
                }

                var repaired = !ReferenceEquals(Root.Cursor, arrow);
                Want($"{where}真实计时器修复页面光标", _tickCount > beforeRepair && repaired);
                Want($"{where}页面光标刷新不唤醒静止指针", _cursorHidden && _polledMoves == beforeMoves);
                report.Add($"{where}页面光标覆盖回归：真实计时器推进 {_tickCount - beforeRepair} 拍，"
                    + $"压回透明={repaired}，仍隐藏={_cursorHidden}");
                if (ReferenceEquals(Root.Cursor, arrow)) Root.Cursor = _window.BlankInputCursor;
            }

            // And not before: the rule has one window for the chrome and a longer one for the cursor, and a
            // cursor that went before the chrome would mean the two had been collapsed into one. Skipped when
            // it never went, or one cause would be reported as two failures.
            //
            // 「一秒」在这里是**空闲时钟**那一秒（mpv.net 的 cursor-autohide，2026-09-16 照搬）：
            // chrome 650ms 先收，光标要等满 1000ms 才走，早于 850ms 就是把两扇窗并成了一扇。
            if (hiddenAt != 0)
                Want($"{where}不早于一秒", hiddenAt - began >= ChromeReveal.CursorIdleMilliseconds - 150);

            // And the thing the user reported: 「鼠标隐藏了一会又会自动跑出来」. The hide used to end with a real
            // injected one-pixel round trip (<see cref="Native.NudgeCursorState"/>) whose outbound half measured as
            // two logical pixels after DPI scaling, which is exactly the threshold — so the player woke itself out
            // of its own hide. That injection is gone as of 2026-09-14 (see <c>PlayerPage.Nudge</c>), which is what
            // makes this leg meaningful again in the direction it was originally about: 藏好之后桌面抖一下（一个
            // 像素）必须还藏着。A hand's first event is tens of pixels, so the two are an order of magnitude apart.
            //
            // Moved by coordinate rather than by injection, because that is the reading this machine can always
            // take: a real displacement through SetCursorPos moves the pointer whether or not the island hears
            // injected input, and the poll reads exactly this.
            //
            // One leg, at one pixel. The wake question is now <see cref="ChromeReveal.HandStep"/>（mpv.net
            // 的冻结参照点判据，严格大于）：一记 1 像素离藏点远得很，必须还藏着。要诚实写下另一半：
            // 参照点冻着，同方向的 1 像素挪上六次也会累计过线——那是 mpv.net 自己的语义，接受的代价，
            // 这条腿只挪一次再放回，量的就是「单次微动不醒」。The honest pair to hold on to is
            // 桌面抖一下（1 像素）不醒、真动一下（几十像素）要醒，and both are asserted here.
            if (_cursorHidden && Native.GetCursorPos(out var still))
            {
                if (!Native.MovePointerTo(still.X + 1, still.Y)) Native.SetCursorPos(still.X + 1, still.Y);
                Pump();

                for (var i = 0; i < 3; i++)
                {
                    Thread.Sleep(40);
                    OnTick(this, EventArgs.Empty);
                }

                var felt = Native.GetCursorPos(out var after) && (after.X != still.X || after.Y != still.Y);
                if (felt) Want($"{where}挪 1 个像素不叫醒它", _cursorHidden);

                report.Add($"{where}藏好后挪 1 个像素：{(felt ? string.Empty : "指针没挪动，这一句只作参考")}"
                    + $"过后{(_cursorHidden ? "还藏着" : "又显示了")}（阈值 {ChromeReveal.MovePixels} 像素以下的抖动不算动手）");

                // Put back before the 「一动就回来」 leg below, which measures from the centre.
                Native.SetCursorPos(still.X, still.Y);
                Pump();
                OnTick(this, EventArgs.Empty);
            }

            if (_cursorHidden) Screen(where);

            var back = _polledMoves;

            // 一记 60 像素，不再三步。判据换成了 mpv.net 的 HandStep（2026-09-16 照搬）：位置离
            // 上次记录点差超阈值就是手 —— 一记 60 像素的位移在旧判据里是「幽灵的签名，必须挡住」，
            // 在新判据里就是一次普通的鼠标移动，光标必须回来。一次搬运就够，也不再补见证：
            // 见证已经没有否决票可投。
            Native.SetCursorPos(centre.X + 60, centre.Y);
            Pump();
            OnTick(this, EventArgs.Empty);

            // Only asked if the pointer actually went — same reason as the reading above. When the input desktop
            // is not ours the move quietly does not happen, and 「一动鼠标就回来」 would be blaming the player for
            // a mouse that never moved.
            //
            // 而且「went」要看**轮询**有没有看见，不能只看坐标读回来变了没有。窗口化那一趟里两者会分开：
            // GetCursorPos 说指针挪到了 3160，轮询却是「轮询问出 0 次」——探针的程序化搬运落进了坐标，
            // 却没有进到输入队列那条被测的路里。拿坐标当真去判，这一条就是在问一记从未发生过的移动，
            // 而它必然红（光标本来就不该为一次没到达的移动醒来）。轮询才是这句话的传感器；它没看见，
            // 这一句就只作参考。
            var cursorWent = Native.GetCursorPos(out var now) && now.X != centre.X;
            var pollSawIt = _polledMoves > back;
            var went = cursorWent && pollSawIt;
            if (went) Want($"{where}一动鼠标就回来", !_cursorHidden && !NoShape());

            report.Add($"{where}挪一下就回来：轮询问出 {_polledMoves - back} 次，线程形状={Mine()}"
                + (went ? string.Empty
                    : cursorWent ? "，指针挪了但轮询没看见（搬运没进输入队列），这一句只作参考"
                    : "，指针没挪动，输入不在我们手上，这一句只作参考"));
        }

        // Everything between our call and the pixels, because the log once showed the two halves disagreeing:
        // the rule fires on time and this thread's queue holds no shape (「线程形状=无」), and the report is
        // still 「鼠标指针还是不会自动隐藏」. Three things can do that, and one reading each tells them apart.
        // <c>SetCursor</c> is per-queue, so the shape only reaches the screen if the window under the pointer
        // belongs to this thread — measured, not assumed, because WinUI created that window. The OS may also
        // simply not have recomputed yet: it asks for a shape when the pointer moves, and the pointer hiding
        // is precisely the pointer not moving, so the same reading is taken again after each of two ways of
        // making it ask without moving anything, in order of how little they disturb.
        //
        // Printed, and asserted about nothing. <c>GetCursorInfo</c> answers for the desktop, and on this
        // machine it answers 「arrow」 windowed and 「nothing」 full screen, run after run, with this thread's
        // queue holding no shape either way and the screen doing the right thing — the last real mouse movement
        // happened over another application's window, and until one happens over ours that is whose cursor the
        // desktop is still describing. Measured both ways, including a real displacement and a fresh hide at
        // the point it landed on, which did not change the windowed answer either. So the assertion that
        // stands for this is 「藏的时候催了一次框架」 in <c>Watch</c>: the ask is what this process is
        // responsible for and what was missing when the screen was wrong, and the desktop's own answer is a
        // diagnostic for whoever reads the report next.
        void Screen(string where)
        {
            var host = _window!.Handle;
            var ours = Native.GetCurrentThreadId();

            // Foreground first: a cursor over a window whose process is not in the foreground can be the
            // foreground process's business, and a reading taken then says nothing about playback.
            var wasFront = Native.GetForegroundWindow() == host;
            if (!wasFront) Native.SetForegroundWindow(host);
            Pump();

            var front = Native.GetForegroundWindow() == host;

            Native.GetCursorPos(out var at);
            var under = Native.WindowFromPoint(at);
            var owner = Native.GetWindowThreadProcessId(under, out _);
            var mine = owner == ours;

            var lines = new List<string>
            {
                $"前台={front}{(front == wasFront ? string.Empty : "（刚抢到的）")}",
                $"指针上的窗口={ClassOf(under)}，属于线程 {owner}{(mine ? "＝本线程" : $"≠本线程 {ours}")}",
                $"什么都没做时 系统 {Says()}"
            };

            // The reading that gets judged, taken here — before any of the three pokes below. They exist to tell
            // 「the OS has not recomputed yet」 apart from 「the screen really has an arrow on it」, and the last of
            // them was measured to *cause* the answer it was supposed to reveal: a synthetic WM_SETCURSOR sent to
            // WinUI's bridge is answered with the arrow, because the bridge only wants no cursor while it is
            // handling pointer input. Judging after that is judging the probe's own poke. Undisturbed is also the
            // stricter reading of the two — it is the state a hand that never moved would be looking at.
            var gone = ScreenHasNoCursor();

            // The restatement the whole hide now rests on, and it is deliberately not an input event: this used
            // to inject a one physical pixel round trip through the real input queue so that WinUI's pipeline
            // would run its 「who is the pointer over, what shape does he want」 round — the only moment it reads
            // ProtectedCursor. The injection worked and cost the fix it was built to serve: the island raised a
            // PointerMoved for it, DPI scaling read its one pixel as two, and the player woke itself out of its
            // own hide (「鼠标隐藏了一会然后又会自动冒出来」, second report, log line 「框架事件走了 2,0 逻辑像素」).
            // See PlayerPage.Nudge. What is measured here is that restating the policy needs nothing from the OS
            // and still leaves this thread's queue with no shape.
            _window.KeepCursorHidden();
            Pump();
            lines.Add($"重申一遍（不挪鼠标）后 系统 {Says()}，线程形状={Mine()}");

            // Straight to the source, and only when that window is ours: sent across threads this blocks
            // until the other one pumps, and a self-check that can hang is worse than one that skips a line.
            if (mine)
            {
                Native.SendMessage(under, Native.WmSetCursor, under,
                    new IntPtr((int)((Native.WmMouseMove << 16) | Native.HtClient)));
                Pump();
                lines.Add($"直接问它后 系统 {Says()}，线程形状={Mine()}");
            }
            else
            {
                lines.Add("没直接问它：那个窗口不在本线程上");
            }

            // What the desktop says about it. Judged now, but only behind three gates — see below.
            var settled = front && front == wasFront && !grabbed;
            var steers = Steers(at);

            lines.Add($"能挪指针={steers}");

            // 「Is there a pointer on the screen」 is the user's own question, and it is finally asked as an
            // assertion rather than printed as a curiosity. Three gates, because without them it is a coin toss
            // rather than a check: the desktop's cursor belongs to whichever queue last drew one, so the reading
            // is only about this application when the island really received pointer input during this leg
            // (heard), when the foreground was already ours rather than snatched a moment ago (settled), and
            // when this process can steer the pointer at all (steers). Any gate down and the reading is printed
            // with the reason it was not judged — which is honest, and is also what four earlier rounds got
            // wrong in the other direction: they read 「no cursor」 off a leg whose island never heard anything.
            var judged = heard && settled && steers;

            lines.Add($"没碰它的时候屏幕上没有系统箭头={gone}"
                + (judged ? string.Empty
                    : !heard ? "（真实输入注不进，这一读数不判）"
                    : !settled ? "（前台是刚抢到的，这一读数还归上一个拿着光标的窗口，不判）"
                    : "（此刻挪不动指针，输入不在我们手上，不判）"));

            if (judged) Want($"{where}没碰它的时候屏幕上没有系统箭头", gone);

            report.Add($"{where}显示层：{string.Join("，", lines)}");
        }

        void Want(string what, bool ok)
        {
            if (!ok) wrong.Add(what);
        }
    }

    /// <summary>
    /// 藏匿期唤醒那一关，<b>mpv.net 版</b>（2026-09-16 用户拍板「完全照搬 mpv.net」后整门重写）：
    /// 位置离上次记录点差超 <see cref="ChromeReveal.MovePixels"/> 就是手
    /// （<see cref="ChromeReveal.HandStep"/>），过线即醒。
    /// <para>
    /// 两条腿，方向都是「叫醒」：<b>一记 60 像素</b>——旧判据里幽灵的签名、必须挡住的那个几何，
    /// 新判据下就是一次普通的鼠标移动，光标必须回来；<b>三步各 2 像素</b>——参照点冻结语义的证明，
    /// 每一拍都不够阈值，累计六像素照样过线，「慢手也能叫回来」就是它。旧门里那些「搬一次不许醒」
    /// 「动画注入不许醒」「带见证的短幽灵不许醒」「回笼收走」的腿随旧判据一起退役——它们测的
    /// 不再是这个播放器的行为。
    /// </para>
    /// <para>
    /// 十七报的两条见证回归（<see cref="ProbeWitnessRegression"/>）留着：见证仍在取证记账，
    /// 账本的读法坏了，下一次幽灵报告的证据链就断了。
    /// </para>
    /// </summary>
    internal (bool Hidden, long Moves, int Refreshes, bool Watching, bool AttachedQueue) CursorProbeState =>
        (_cursorHidden, _polledMoves, _foreignPokes, _cursorVisibilityEvents?.Installed == true,
            _cursorVisibilityEvents?.IsAttached == true);

    /// <summary>Uses the production idle timer with no network playback session.</summary>
    internal void BeginCursorProbe()
    {
        HoldCursorForDemo();
        Cover.Visibility = Visibility.Collapsed;
        CoverRing.IsActive = false;
        _window!.VideoVisible = true;
    }

    internal unsafe bool ProbeCursorModifierPreservation()
    {
        if (_cursorVisibilityEvents is not { IsAttached: true } observer) return false;
        byte* original = stackalloc byte[256];
        byte* seeded = stackalloc byte[256];
        byte* after = stackalloc byte[256];
        if (!CursorVisibilityEvents.GetKeyboardState((IntPtr)original)) return false;
        new ReadOnlySpan<byte>(original, 256).CopyTo(new Span<byte>(seeded, 256));
        seeded[0x10] |= 0x80;
        seeded[0x11] |= 0x80;
        seeded[0x12] |= 0x80;
        try
        {
            if (!CursorVisibilityEvents.SetKeyboardState((IntPtr)seeded)) return false;
            observer.SetHidden(false);
            if (!CursorVisibilityEvents.GetKeyboardState((IntPtr)after)
                || (after[0x10] & after[0x11] & after[0x12] & 0x80) == 0) return false;
            observer.SetHidden(true);
            return observer.IsAttached && CursorVisibilityEvents.GetKeyboardState((IntPtr)after)
                && (after[0x10] & after[0x11] & after[0x12] & 0x80) != 0;
        }
        finally
        {
            CursorVisibilityEvents.SetKeyboardState((IntPtr)original);
            observer.SetHidden(_cursorHidden);
        }
    }

    internal (bool Ok, string Detail) ProbeCursorWarp()
    {
        if (!Attached || _window is null) return (false, "播放层未接线");
        if (!Native.GetCursorPos(out var origin)) return (false, "问不出指针位置");

        var was = Visibility;
        var wasFull = _window.Fullscreen;

        Visibility = Visibility.Visible;
        UpdateLayout();

        var report = new List<string>();
        var wrong = new List<string>();

        // 见证仍在取证记账（不再有否决票可投）。报一声在不在场；唤醒门槛如今只剩 HandStep 一行。
        report.Add($"见证：{(_window!.Witness.Ready ? "就绪（取证）" : "缺席（取证）")}"
            + $"，账 真{_window.Witness.RealMoves}/注{_window.Witness.InjectedMoves}/伪{_window.Witness.Forged}"
            + $"，唤醒门槛 位置离上次记录点差 {ChromeReveal.MovePixels:0} 像素（mpv.net 规则）");

        // 第十七报：字段顺序与哨兵溢出的两条回归，用合成记录钉住。Forge 测不出这两条 —— 它把
        // 时间戳变成真的，而沙箱注不进真实输入，聋与溢出都只有真机的「从未见过」状态才现形。
        // 合成记录直接铺 winuser.h 的字节布局：RAWINPUTHEADER{dwType@0, dwSize@4, hDevice@8}
        // + RAWMOUSE{…lLastX@36, lLastY@40}，与 Observe 的读法同一张图。
        var witnessRegression = ProbeWitnessRegression();
        report.Add(witnessRegression.Item2);
        if (!witnessRegression.Item1) wrong.Add("见证合成记录回归（十七报）");

        SetCursorHidden(false);
        _chrome.Reset(Now);
        Render();

        if (!PictureCentre(out var centre))
        {
            Visibility = was;
            return (false, "问不出画面中心");
        }

        // 把指针放到画面中心并让它稳定下来，再藏。藏匿必须先成立，不然下面判的是空气。
        if (!Native.SetCursorPos(centre.X, centre.Y)) Native.MovePointerTo(centre.X, centre.Y);
        Pump();
        OnTick(this, EventArgs.Empty);
        Pump();
        OnTick(this, EventArgs.Empty);

        SetCursorHidden(true);
        _polledKnown = true;
        Native.GetCursorPos(out _polled);

        // 藏匿先得成立，判据才有东西可判。而这一关最容易被环境噪声掐掉的是开头这一步：鼠标真在动、
        // 或光标正在被别的窗口按着，2600 毫秒的等待窗就不够它静下来。跳过是「前提没了不装绿」的正解，
        // 但这一关恰是第十一报的修复本身，静默跳过等于这一报根本没被验证过 —— 所以先再试一轮。
        var settled = false;
        for (var attempt = 0; attempt < 2 && !settled; attempt++)
        {
            if (attempt > 0)
            {
                report.Add("第一次没能先藏下去，等一拍再来一次");
                SetCursorHidden(false);
                _chrome.Reset(Now);
                Render();
                Thread.Sleep(800);
                if (!Native.SetCursorPos(centre.X, centre.Y)) Native.MovePointerTo(centre.X, centre.Y);
                Pump();
                OnTick(this, EventArgs.Empty);
                SetCursorHidden(true);
                _polledKnown = true;
                Native.GetCursorPos(out _polled);
            }

            var retryAt = Now;
            while (Now - retryAt < 2600) { Pump(); Thread.Sleep(20); }
            OnTick(this, EventArgs.Empty);
            Pump();
            settled = _cursorHidden;
        }

        if (!settled)
        {
            report.Add("两次都没能先藏下去（多半是有人真在动鼠标），这一关只作参考");
            SetCursorHidden(false);
            _chrome.Reset(Now);
            _chrome.Tick(Now + SettleMilliseconds);
            Render();
            Visibility = was;
            UpdateLayout();
            Native.SetCursorPos(origin.X, origin.Y);
            return (wrong.Count == 0, string.Join("；", report));
        }

        // —— 腿一：一记 60 像素（旧世界的幽灵签名）—— 光标必须回来 ——
        //
        // 这条腿的旧版要它「还藏着」，判据是二十一报的净位移坎；mpv.net 规则下同一个几何就是
        // 一次普通的鼠标移动（mpv 的 IsCursorPosDifferent 同款问法），光标必须回来。判红判绿
        // 反过来，几何一个字没改 —— 这正是「完全照搬」的代价与承诺所在。
        Native.GetCursorPos(out var at);

        // 60 是日志里那个签名本身；靠屏幕右沿太近就退一步，别撞到虚拟屏边界让 SetCursorPos 被裁。
        var jump = at.X + WARP_SIGNATURE;
        if (jump > VirtualRight()) jump = at.X - WARP_SIGNATURE;

        Native.SetCursorPos(jump, at.Y);
        Pump();
        OnTick(this, EventArgs.Empty);

        // 搬运可能被静默丢弃（输入桌面不在我们手上）——坐标没动就不判，只作参考，与
        // ProbeCursorAlive 里「一动就回来」那条的护栏同一理由。
        var went = Native.GetCursorPos(out var afterJump) && afterJump.X != at.X;
        var cameBack = !_cursorHidden;

        if (went) Want("藏匿期一记过阈值的位移叫回光标（mpv.net 规则）", cameBack);

        report.Add($"搬 60 像素一记（{at.X}→{jump}）后：光标{(cameBack ? "回来了" : "还藏着")}"
            + $"，读数 {afterJump.X},{afterJump.Y}"
            + (went ? string.Empty : "，指针没挪动（输入不在我们手上），这一腿只作参考")
            + (cameBack || !went ? string.Empty : "，线程形状=" + Mine()));

        // —— 腿二：慢移累计 —— 参照点冻结的证明 ——
        //
        // mpv.net 的参照点（_lastCursorPosition，这里是 _polled）在不够阈值的拍子上冻着：每拍
        // 2 像素的三步，头两拍离藏点 2、4 都不过线，第三拍累计 6 过线 —— 「慢手也能叫回来」
        // 说的就是这条路。旧判据下这段位移永远攒不起来（每一拍都不够格，账当场归零），这正是
        // 「每拍不足五像素的手走不到终点」那条代价；换了判据，它从反例变成了正例。
        SetCursorHidden(true);
        _polledKnown = true;
        Native.GetCursorPos(out _polled);

        if (_cursorHidden)
        {
            Native.GetCursorPos(out var slow);

            // 每步 +2、同方向；靠屏幕右沿太近就往左走，别撞虚拟屏边界。
            var dirSlow = slow.X + 12 <= VirtualRight() ? 1 : -1;
            var heldEarly = true;

            for (var i = 1; i <= 3; i++)
            {
                Native.SetCursorPos(slow.X + dirSlow * 2 * i, slow.Y);
                Pump();
                OnTick(this, EventArgs.Empty);

                if (i < 3 && !_cursorHidden) heldEarly = false;

                report.Add($"  慢移第 {i} 步（累计 {2 * i} 像素）后：光标{(i < 3 ? (_cursorHidden ? "还藏着" : "提前显示了") : (!_cursorHidden ? "回来了" : "还藏着"))}");
                Thread.Sleep(60);
            }

            Want("慢移头两拍不过线（2、4 像素都藏着）", heldEarly);
            Want("慢移累计过线叫回光标（冻结参照点语义）", !_cursorHidden);
            report.Add($"慢移累计（三步各 2、共 6 > {ChromeReveal.MovePixels:0}）后：光标{(!_cursorHidden ? "回来了" : "还藏着")}");
        }
        else
        {
            report.Add("腿一之后光标不在藏匿态，慢移腿不判（同一个前提没了不装绿）");
        }

        // —— 腿三：显示态慢手 —— 参照点冻结在显示态的证明 ——
        //
        // mpv 的 cursor-autohide 里「动了就不藏」（2026-09-17 参考 dyphire/mpv-config 统一后的语义）：
        // 只要手还在动，空闲钟就一直被过线重盖，光标不会死在半路。轮询传感器上这一语义就是显示态
        // 与藏匿态同用一张冻参照（ChromeReveal.HandStep）：每拍 2 像素的手每三拍过一次线（累计
        // 6 > 5），空闲钟永远走不满 CursorIdleMilliseconds。旧判据（显示态参照每拍推进）下这段慢移
        // 攒不起任何重盖，一秒后光标消失在移动中途——「每拍不足五像素的慢手走不到终点」说的就是它。
        if (!_cursorHidden)
        {
            Native.GetCursorPos(out var drift);

            // 每拍 +2、同方向，走 16 拍（32 像素，五次于过线）；靠屏幕右沿太近就往左走，别撞虚拟屏边界。
            var dirDrift = drift.X + 40 <= VirtualRight() ? 1 : -1;
            var lost = false;

            for (var i = 1; i <= 16; i++)
            {
                Native.SetCursorPos(drift.X + dirDrift * 2 * i, drift.Y);
                Pump();
                OnTick(this, EventArgs.Empty);

                if (_cursorHidden)
                {
                    lost = true;
                    report.Add($"  显示态慢移第 {i} 拍（累计 {2 * i} 像素）后：光标丢了");
                    break;
                }

                Thread.Sleep(60);
            }

            Want("显示态慢手一路走光标不丢（显示态同用冻参照）", !lost);
            report.Add($"显示态慢移 2 像素×16 拍（共 32 > {ChromeReveal.MovePixels:0}）后：光标{(lost ? "半路丢了（参照每拍推进的旧判据）" : "一直在")}");
        }
        else
        {
            report.Add("腿二之后光标不在显示态，显示态慢手腿不判（同一个前提没了不装绿）");
        }

        // —— 还原 ——
        SetCursorHidden(false);
        _chrome.Reset(Now);
        _chrome.Tick(Now + SettleMilliseconds);
        Render();
        Visibility = was;
        UpdateLayout();
        Native.SetCursorPos(origin.X, origin.Y);
        Pump();

        return (wrong.Count == 0,
            string.Join("；", report) + (wrong.Count == 0 ? string.Empty : $"；不符：{string.Join('、', wrong)}"));

        void Want(string what, bool ok)
        {
            if (!ok) wrong.Add(what);
        }

        static int VirtualRight()
        {
            var (x, _, width, _) = Native.VirtualScreen();
            return x + width - 1;
        }
    }

    /// <summary>
    /// 十七报两条见证回归的合成记录腿：不碰窗口、不碰指针，直接把三张账本摊开判。Forge 测不出
    /// 这两条 —— 它把时间戳变成真的，而沙箱注不进真实输入，聋与溢出都只有「从未见过」状态才现形。
    /// <para>
    /// ① 「从没见过」的一票必须答「没有」—— 哨兵 MinValue 相减溢出曾让它恒答「有」；② 一条
    /// hDevice ≠ 0 的合成真记录必须被记账并开窗 —— dwType 读错位（读了 dwSize=48）曾让每条记录
    /// 都在第一问被扔掉，见证全聋；③ 一条 hDevice == 0 的合成注入记录只能进注账，不许顶替真账。
    /// </para>
    /// </summary>
    private static (bool Ok, string Detail) ProbeWitnessRegression()
    {
        var wrong = new List<string>();

        // ① 出生的见证：什么都没见过，任何窗口宽度的「最近有真输入吗」都必须答没有。
        var witness = new RealInputWitness();
        if (witness.RecentRealInput(2000)) wrong.Add("从未见过真输入却答「有」（哨兵溢出）");

        // ② 合成真手记录：RAWINPUTHEADER{dwType@0=0（鼠标）、dwSize@4=48、hDevice@8 非零}
        // + RAWMOUSE{…lLastX@36=5、lLastY@40=7}，与 Observe 的读法同一张图。
        var real = new byte[64];
        BitConverter.GetBytes(0).CopyTo(real, 0);
        BitConverter.GetBytes(48).CopyTo(real, 4);
        BitConverter.GetBytes((long)0x1234).CopyTo(real, 8);
        BitConverter.GetBytes(5).CopyTo(real, 36);
        BitConverter.GetBytes(7).CopyTo(real, 40);
        witness.Observe(real);

        if (witness.RealMoves != 1) wrong.Add("真手记录没进真账（dwType 错位全扔）");
        if (!witness.RecentRealInput(2000)) wrong.Add("真手记录记了账却答「没有」");

        // ③ 同一布局的注入记录：hDevice 清零、位移 9,3 —— SendInput 一类只进注账。
        var injected = new byte[64];
        BitConverter.GetBytes(0).CopyTo(injected, 0);
        BitConverter.GetBytes(48).CopyTo(injected, 4);
        BitConverter.GetBytes(9).CopyTo(injected, 36);
        BitConverter.GetBytes(3).CopyTo(injected, 40);
        witness.Observe(injected);

        if (witness.InjectedMoves != 1) wrong.Add("无出处记录没进注账");
        if (witness.RealMoves != 1) wrong.Add("注入顶替了真账");

        var detail = wrong.Count == 0
            ? "见证合成回归全绿（从未见过→没有；真记进真账开窗；注入只进注账）"
            : $"见证合成回归不符：{string.Join('、', wrong)}";

        return (wrong.Count == 0, detail);
    }

    /// <summary>The middle of our client area in screen pixels, which is a point on the picture.</summary>
    private bool PictureCentre(out NativePoint point)
    {
        point = default;

        var handle = _window?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero || !Native.GetClientRect(handle, out var client)) return false;

        point = new NativePoint
        {
            X = (client.Right - client.Left) / 2,
            Y = (client.Bottom - client.Top) / 2
        };

        return Native.ClientToScreen(handle, ref point);
    }
}

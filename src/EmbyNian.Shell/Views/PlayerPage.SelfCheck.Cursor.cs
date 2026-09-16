using EmbyNian.Playback;
using EmbyNian.Shell.Interop;
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
            _window.KeepCursorHidden();
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
            _window.KeepCursorHidden();
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

    /// <summary>
    /// Whether the OS itself says nothing a user could see is on screen: the showing flag down, down to no
    /// shape at all, or down to a shape that draws nothing — a cursor made of nothing is not a cursor, and
    /// <c>GetCursorInfo</c> reports it as showing like any other handle.
    /// Every other reading in this file is the player's own bookkeeping, which was unanimous that the cursor
    /// was hidden through three rounds of 「鼠标指针还是不会自动隐藏」; this is the one that was disagreeing.
    /// A snapshot that cannot be taken is not evidence of a cursor, so it counts as gone.
    /// <para>
    /// The last clause is the one that took a while to earn. Hidden over the picture, WinUI does not push
    /// <b>our</b> blank <c>HCURSOR</c> to the compositor — it pushes a copy of it: a handle we never created,
    /// different on every run (0xF08BA, 0x244F0B19, 0x4D850313 have all been recorded), which draws nothing and
    /// which <c>CURSORINFO</c> nevertheless reports as showing. So the question this can honestly answer is
    /// 「is the thing on screen the system arrow」 rather than 「is there anything at all」, and it is asked only
    /// while this thread's own queue holds no shape, which is what makes 「some other real cursor」 impossible.
    /// The arrow is not a technicality here: an arrow standing over a paused film is exactly what was reported,
    /// and it is what this still goes red for.
    /// </para>
    /// </summary>
    private bool ScreenHasNoCursor() =>
        Native.CursorSnapshot() is not { } cursor
        || (cursor.Flags & 1) == 0
        || cursor.Shape == IntPtr.Zero
        || (_window is { } window && cursor.Shape == window.BlankCursor)
        || (NoShape() && cursor.Shape != Native.LoadCursor(IntPtr.Zero, Native.ArrowCursor));

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
            //    cleared just above, so the loop's first poll always counts one — it is the seeding, not a
            //    movement, and a pointer that truly never moves is filtered out before the counter by the
            //    <c>dx == 0 && dy == 0</c> return in PollPointer. So exactly 1 is what an undisturbed leg
            //    reports and anything past 1 is a real displacement: one that crossed
            //    <see cref="ChromeReveal.MovePixels"/>, in either cursor state — the same five pixels whether the
            //    cursor is showing or hidden, that number being 5 rather than 2 as of 2026-09-14 because a desk
            //    rattles up to two (see <c>ChromeReveal.MovePixels</c>). Either way the idle clock restarted, so
            //    there is nothing here to judge.
            //
            // A regression cannot hide behind this. Hiding that stops working reports 1 polled move and fails;
            // a spurious un-hide from a pointer that never moved arrives as a XAML event (「空事件」) and never
            // touches this counter, because it only advances when the OS's own coordinate changed.
            var polled = _polledMoves - polls;
            var moved = !Native.GetCursorPos(out var ended) || ended.X != centre.X || ended.Y != centre.Y;
            var disturbed = moved || polled > 1;

            report.Add($"{where}：{(hiddenAt == 0 ? $"{span}ms 过去也没藏" : $"静止 {hiddenAt - began}ms 就藏了")}"
                + $"，线程形状={Mine()}"
                + $"，真实输入{(heard ? "到位" : "注不进")}"
                + $"，轮询问出 {polled} 次移动{(polled <= 1 ? "（只有开头那次播种，也就是全程没人碰）" : $"（开头播种 1 次，真的动了 {polled - 1} 次）")}"
                + $"、XAML 事件 {_pointerMoves - moves} 次、空事件 {_stillMoves} 次"
                + $"，我们推了 {pushed} 拍、计时器自己 {Math.Max(0, _tickCount - ticks - pushed)} 拍"
                + $"，重申了 {_cursorNudges - nudges} 次（不碰指针）"
                + (disturbed
                    ? $"，中途有人动了鼠标（{(moved ? "指针没停在原处" : "轮询问出了真移动")}），这一轮只作参考"
                    : string.Empty));

            if (disturbed) return;

            // The seeding must have come from somewhere, or 「藏了」 below would be true of a rule that never
            // knew where the pointer was — and a rule that believes the pointer is nowhere hides nothing.
            Want($"{where}轮询问出了指针", _polledMoves > polls);

            // The whole of the request, asked of real seconds: 「鼠标静止不动两秒之后要自动隐藏」.
            Want($"{where}静止两秒后鼠标真藏了", _cursorHidden);
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
            // cursor that went at 650 ms would mean the two had been collapsed into one. Skipped when it never
            // went, or one cause would be reported as two failures.
            if (hiddenAt != 0)
                Want($"{where}不早于两秒", hiddenAt - began >= ChromeReveal.CursorIdleMilliseconds - 150);

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
            // One leg, at one pixel, and there used to be a second one at two. It is gone because it was written
            // against the retired architecture and contradicted the threshold it was measuring:
            // <see cref="ChromeReveal.Travelled"/> counts a step as movement at <b>≥</b>
            // <see cref="ChromeReveal.MovePixels"/>, so a leg asking the rule to disregard a two-pixel
            // displacement was asking it to disregard something a desk actually rattles — the number that made it
            // ambiguous was 2 because the threshold was 2, and both are gone as of 2026-09-14: the threshold is 5
            // and this leg moves one. The honest pair to hold on to is 桌面抖一下（1 像素）不醒、真动一下（几十像素）
            // 要醒，and both are asserted here.
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

            // 两次位移，不是一次。这一句问的是「手落在鼠标上会不会把光标叫回来」，而**一次** 60 像素的
            // SetCursorPos 恰恰是第十一报要挡掉的那个形状：注入（外屏 AyuGram 把光标整块搬走）与一次
            // 程序化搬运都表现为「搬完就冻住」，WarpOrHand 因此要求看下一拍 —— 位置又变了才算手。
            // 探针以前只搬一次、只推一拍，于是它测的其实是「注入会不会叫醒光标」，答案是不该叫醒，
            // 判据却在要它醒。手在鼠标上是一个**过程**：这里就走两步，第二拍位置再变，轮询就会认成手
            // 并唤醒——和真手连着走两拍是同一条路（那条判据在 ProbeCursorWarp 里）。
            Native.SetCursorPos(centre.X + 60, centre.Y);
            Pump();
            OnTick(this, EventArgs.Empty);
            Native.SetCursorPos(centre.X + 120, centre.Y);
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
    /// 第十一报（2026-09-15）新增的一关：<b>藏匿期别人把指针整块搬走一次，光标不该跟着出来。</b>
    /// <para>
    /// 病是屏幕二上的 AyuGram：每收到一条静音的群聊消息（没有弹窗、没有焦点变化）就把指针横向搬整整
    /// 60 像素，一秒不到就把藏了不到两秒的光标叫回来 —— 四天日志里同一个 <c>dx=60,dy=0</c> 数到 78 次，
    /// 藏点 <c>3100,932</c>、唤醒读数 <c>3160,932</c>。第九报那套负计数锁挡不住它，因为锁管的是箭头
    /// 画不画，而这条唤醒走的是位置：<c>GetCursorPos</c> 如实报了位移，单传感器如实判「动了」。
    /// </para>
    /// <para>
    /// 这一关就在这台机器上原样造一次那个场景：藏好之后，用 <see cref="Native.SetCursorPos"/> 把指针
    /// <b>搬一次</b> 60 像素就不动（这正是一次注入的形状：一个事件，不是一个过程），推几拍，光标必须
    /// 还藏着。它是自检里少有的「能真的把用户那个场景跑一遍」的关卡 —— 因为触发它的不是真实输入，
    /// 而这台机器注不进真实输入恰好不妨碍它。
    /// </para>
    /// <para>
    /// 反过来的那一半也判，不然「挡掉跳变」可以退化成一概不醒：同一次藏匿里再让指针<b>连着走两拍</b>
    /// （手在走的样子），光标必须回来。两个方向合起来才是这条规矩的完整判据。
    /// </para>
    /// </summary>
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

        // —— 一次跳变：搬 60 像素就不动，光标必须还藏着 ——
        var before = _warpsIgnored;
        Native.GetCursorPos(out var at);

        // 60 是日志里那个签名本身；靠屏幕右沿太近就退一步，别撞到虚拟屏边界让 SetCursorPos 被裁。
        var jump = at.X + WARP_SIGNATURE;
        if (jump > VirtualRight()) jump = at.X - WARP_SIGNATURE;

        Native.SetCursorPos(jump, at.Y);

        for (var i = 0; i < 5; i++)
        {
            Pump();
            OnTick(this, EventArgs.Empty);
            Thread.Sleep(60);
        }

        Pump();
        OnTick(this, EventArgs.Empty);

        var stillHidden = _cursorHidden;
        var counted = _warpsIgnored > before;

        Want("藏匿期被搬一次不叫醒光标", stillHidden);
        Want("那一次搬动被记进了账", counted);
        report.Add($"搬 60 像素一次（{at.X}→{jump}）后：光标{(_cursorHidden ? "还藏着" : "又显示了")}"
            + $"，挡掉跳变 {before}→{_warpsIgnored} 次");

        // —— 第二条消息：同一次藏匿里再来一记同款跳变，仍然不许叫醒 ——
        //
        // 第十五报的现场。用户的原话是「**每次**收到静音的群聊消息都会让鼠标显示」——不是第一次，是每
        // 一次。旧版的免检闩（判掉一跳之后的那一记当手放行）没有期限，而藏匿期里没有东西会去清它，
        // 于是第一条消息被挡住、第二条开始全部免检通过。这一腿就是第二条消息：等过了免检期，再搬一记
        // 同样的 60，它必须重新过「先挂起 → 下一拍冻在原地 → 判掉」的手续，并且记进账。
        //
        // 等待是这一腿的要害，不是拖延：免检只管紧跟着的那一记（WarpHandMilliseconds），而两条消息之间
        // 是秒级。睡得比那个窗长，模拟的才是「第二条消息」而不是「手还在走」。
        if (_cursorHidden)
        {
            var afterFirst = _warpsIgnored;
            var quietUntil = Now + ChromeReveal.WarpHandMilliseconds + 250;
            while (Now < quietUntil) { Pump(); Thread.Sleep(20); }

            var second = Native.GetCursorPos(out var at2) ? at2 : at;
            var jump2 = second.X + WARP_SIGNATURE;
            if (jump2 > VirtualRight()) jump2 = second.X - WARP_SIGNATURE;

            Native.SetCursorPos(jump2, second.Y);

            for (var i = 0; i < 5; i++)
            {
                Pump();
                OnTick(this, EventArgs.Empty);
                Thread.Sleep(60);
            }

            Pump();
            OnTick(this, EventArgs.Empty);

            Want("第二条消息也不叫醒光标", _cursorHidden);
            Want("第二条消息也记进了账", _warpsIgnored > afterFirst);
            report.Add($"隔了 {ChromeReveal.WarpHandMilliseconds + 250}ms 再搬一次 60"
                + $"（{second.X}→{jump2}）后：光标{(_cursorHidden ? "还藏着" : "又显示了")}"
                + $"，挡掉跳变 {afterFirst}→{_warpsIgnored} 次");
        }
        else
        {
            report.Add("第一记跳变就把光标叫醒了，第二条消息那一腿不判（同一个病，报一条就够）");
        }

        // —— 对面那一半：手在走（连着两拍都动），光标必须回来 ——
        if (_cursorHidden)
        {
            var from = _cursorHidden;
            Native.GetCursorPos(out var now1);

            for (var i = 0; i < 2; i++)
            {
                Native.SetCursorPos(now1.X + 40 * (i + 1), now1.Y + 10 * (i + 1));
                Pump();
                OnTick(this, EventArgs.Empty);
                Native.GetCursorPos(out var probe);
                report.Add($"  第 {i + 1} 拍搬到 {probe.X},{probe.Y} 后：光标{(_cursorHidden ? "还藏着" : "回来了")}"
                    + $"，规则{(_chrome.CursorHidden ? "藏" : "显")}"
                    + $"，挂起{(_chrome.WarpPendingAt is { } wp ? $"{wp.X},{wp.Y}" : "无")}");
                Thread.Sleep(120);
            }

            Want("真手连着走两拍要能叫醒光标", from && !_cursorHidden);
            report.Add($"连着走两拍（每次 +40）后：光标{(_cursorHidden ? "还藏着" : "回来了")}");
        }
        else
        {
            report.Add("跳变那一步就把光标叫醒了，后一半不判（同一个病，报一条就够）");
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

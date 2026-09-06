using EmbyNian.Playback;
using EmbyNian.Shell.Interop;
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

        // The one lever here that is not tied to this thread's queue, and the reason it exists: two of the
        // windows a pointer over a playing film sits on are made by somebody else's thread — the island's are
        // the framework's, libmpv's is libmpv's — and a class cursor is the only 「no cursor」 that crosses that
        // line. Asserted in both directions, because a class left blank outlives the window that blanked it.
        Want("藏了以后窗口树上的类光标也换成了透明的", _window.ClassCursorsBlanked > 0);
        report.Add($"类光标：藏着换掉 {_window.ClassCursorsBlanked} 个类（这一趟共扫到 {_window.ClassCursorsSwept} 个）");

        // 藏鼠标真正的扳机，发一遍并把它落到哪儿写出来。指针不动的时候上面每一句「没有光标」都是没人问的答案，
        // 而这一下就是那个理由 —— 一像素出去、一像素回来，走真实输入队列（<see cref="Native.NudgeCursorState"/>）。
        // <para>
        // 只印不断言，理由是这声 <c>WM_SETCURSOR</c> 未必落在我们手上：指针压在 XAML 内容上时它由框架自己那个
        // 内层窗口答掉，既不冒到主窗口的 <c>Route</c>、也不冒到岛的过程上来 —— 同一份报告里那句「真移动问到
        // 0→0」说的就是这件事（那一趟指针真的挪到了画面中心，两处一次都没被问到）。真放片子的时候指针压的是
        // mpv 自己那块子窗口，那是一个普通的 Win32 窗口，这声就落在它身上。而这一下真正要够到的不是
        // <c>WM_SETCURSOR</c>，是框架的输入管线：它只在处理指针输入的时候才去念 <c>ProtectedCursor</c>。发得出去
        // 这一半由「藏的时候催了一次框架」在 <see cref="ProbeCursorAlive"/> 里断言，听得见没有由那儿的
        // 「真实输入」一读答。
        // </para>
        asked = _window.CursorAsksSeen;
        var nudged = Native.NudgeCursorState();
        Pump();
        report.Add($"催一下框架：发得出={nudged}，我们这两个过程问到 {asked}→{_window.CursorAsksSeen}");

        // The message a still pointer never sends, sent by hand — to the host window first, because that is
        // where a moving pointer's own WM_SETCURSOR actually arrives. This is the assertion the probe is built
        // around: while the player wants no cursor, the answer is none and the message is not passed on.
        var answered = _window.CursorHidesAnswered;
        var reply = Native.SendMessage(host, Native.WmSetCursor, host, Moving());

        Want("藏着时主窗口的消息由我们回答", _window.CursorHidesAnswered == answered + 1);
        Want("藏着时回答的是「已设好」", (long)reply == 1);
        report.Add($"藏着问主窗口：回答={(long)reply}，接管 {answered}→{_window.CursorHidesAnswered}，{Says()}");

        // And the island's own procedure, for the messages that do reach it — a popup or a flyout of the
        // island's is a separate window and asks separately.
        answered = _window.CursorHidesAnswered;
        Native.SendMessage(island, Native.WmSetCursor, island, Moving());

        Want("藏着时岛的消息也由我们回答", _window.CursorHidesAnswered == answered + 1);
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
        Want("下一拍就又藏回去", NoShape());
        report.Add($"被放回箭头后，一拍就藏回：线程形状={Mine()}");

        // ---- 还原 ----
        SetCursorHidden(false);

        Want("还原以后窗口知道", !_window.CursorHidden);
        Want("还原以后显示计数归零", _cursorCount >= 0);
        Want("还原以后线程又有形状", !NoShape());
        Want("还原以后类光标一个不剩地放回去了", _window.ClassCursorsBlanked == 0);

        // Handed back to the framework, and this one matters more than it looks: left holding the transparent
        // cursor, the picture would have no pointer over it for the rest of the session.
        Want("还原以后画面把光标交还给框架", Root.Cursor is null);
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

            // Whether the island really hears the ask the hide is built on. Not a stand-in for it either: this is
            // the same <see cref="Native.NudgeCursorState"/> call, made at the point the pointer already sits on,
            // followed by the question 「did a XAML pointer event arrive」. That question is the premise the whole
            // leg rests on — hidden with the island silent proves nothing about a film, and that is what every
            // previous round measured — and since 2026-09-05 it is also the premise the fix rests on: the ask has
            // to reach WinUI's input pipeline or the transparent ProtectedCursor is a value nobody reads.
            // Counted as either kind of event, because the ask is a pixel out and a pixel back: with the cursor
            // still showing, a one-pixel hop is under PointerNoise and the return leg lands on the anchor, so
            // both arrive as 「空事件」 rather than as movement. 「The island heard something」 is the question,
            // not 「the island called it a movement」.
            var quiet = _stillMoves;

            Native.NudgeCursorState();
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
            //    reports and anything past 1 is a real displacement: past PointerNoise while the cursor shows,
            //    and any pixel at all once it is hidden, where a mouse rattling on a desk is by design a hand
            //    reaching for it. Either way the idle clock restarted, so there is nothing here to judge.
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
                + $"，催了框架 {_cursorNudges - nudges} 次"
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
            // while it handles pointer input, and this hide happens because nothing is moving — so without the
            // one-pixel round trip at the end of it the transparent cursor is a value nobody ever reads and the
            // last shape worked out stays on the screen. That is what 「静止超过两秒后鼠标指针还是不会自动隐藏」
            // was, from a player whose own readings all said hidden. The desktop's own answer cannot stand in
            // for this — see Screen.
            Want($"{where}藏的时候催了一次框架", _cursorNudges > nudges);

            // And not before: the rule has one window for the chrome and a longer one for the cursor, and a
            // cursor that went at 650 ms would mean the two had been collapsed into one. Skipped when it never
            // went, or one cause would be reported as two failures.
            if (hiddenAt != 0)
                Want($"{where}不早于两秒", hiddenAt - began >= ChromeReveal.CursorIdleMilliseconds - 150);

            if (_cursorHidden) Screen(where);

            var back = _polledMoves;
            Native.SetCursorPos(centre.X + 60, centre.Y);
            Pump();
            OnTick(this, EventArgs.Empty);

            // Only asked if the pointer actually went — same reason as the reading above. When the input desktop
            // is not ours the move quietly does not happen, and 「一动鼠标就回来」 would be blaming the player for
            // a mouse that never moved.
            var went = Native.GetCursorPos(out var now) && now.X != centre.X;
            if (went) Want($"{where}一动鼠标就回来", !_cursorHidden && !NoShape());

            report.Add($"{where}挪一下就回来：轮询问出 {_polledMoves - back} 次，线程形状={Mine()}"
                + (went ? string.Empty : "，指针没挪动，输入不在我们手上，这一句只作参考"));
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

            // The ask the whole hide rests on: one physical pixel out through the real input queue and straight
            // back, so that WinUI's input pipeline goes through the entire 「who is the pointer over, what shape
            // does he want」 round — which is the only moment it reads ProtectedCursor — while the pointer ends
            // exactly where it started. The poll and the event filter both know this player's own echo, so the
            // stillness being measured survives it.
            //
            // Two earlier spellings of this asked nothing at all: a same-point SetCursorPos, which never reaches
            // the island, and a zero-displacement SendInput, which produces no message whatever. See
            // Native.NudgeCursorState for the measurements.
            var sent = Native.NudgeCursorState();
            Pump();
            lines.Add($"催一下框架{(sent ? string.Empty : "（发不出去）")}后 系统 {Says()}，线程形状={Mine()}");

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

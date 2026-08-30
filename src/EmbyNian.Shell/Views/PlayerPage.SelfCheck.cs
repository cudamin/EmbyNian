using EmbyNian.Diagnostics;
using EmbyNian.Mpv;
using EmbyNian.Playback;
using EmbyNian.Shell.Interop;
using EmbyNian.Shell.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace EmbyNian.Shell.Views;

/// <summary>
/// The player's probes: the half of this page that a build cannot see and a unit test cannot reach.
/// <para>
/// Everything the player draws is built on demand — the six flyouts fill themselves as they open, the
/// 统计 grid and the chapter ticks are drawn in code, the reveal rule's output is three
/// <c>Visibility</c> flips — so none of it is touched by compiling the solution and all of it first runs
/// in front of a person, mid-film. Each probe drives the page's own code with synthetic input and puts
/// what it moved back; between them they answer the questions 「按了会不会崩」 and 「按了会不会没反应」
/// before a user has to.
/// </para>
/// <para>
/// The rules themselves are Core's and are unit-tested there. What these check is the wiring: that
/// <see cref="ChromeReveal"/>'s <c>Bar</c> flag really reaches the bar, that both brush keys resolve in
/// this page's own resource scope, that a click on the strip's empty half is not a click on the film.
/// </para>
/// </summary>
public sealed partial class PlayerPage
{
    /// <summary>
    /// How far a probe pushes its synthetic clock to be sure the reveal rule has finished settling: the
    /// longest window in it — the cursor's two seconds — plus the grace window a keyboard command lays on
    /// top, plus one.
    /// <para>
    /// One named constant because this used to be written out as 「idle + grace + 1」 in five places, which
    /// came to 1851 ms and stopped being long enough the moment the cursor was given a window of its own.
    /// Nothing but a probe failing on a machine would have said so, and it would have said it about the
    /// wrong thing.
    /// </para>
    /// </summary>
    private const long SettleMilliseconds =
        ChromeReveal.CursorIdleMilliseconds + ChromeReveal.GraceMilliseconds + 1;

    /// <summary>
    /// Drives the reveal rule the way a pointer would and checks what the page then showed, so the
    /// self-check can answer requirements 9/10/11 without a film and without a person watching.
    /// <para>
    /// Core's <see cref="ChromeReveal"/> is unit-tested on its own; what cannot be tested there is this
    /// page's mapping from a state onto three <c>Visibility</c> flags and one Win32 cursor counter, and a
    /// mapping that had <c>Bar</c> wired to the rail's flag would look perfectly correct until someone
    /// pressed play. Each step therefore says what it expects rather than only printing what it got.
    /// </para>
    /// <para>
    /// The clock only ever moves forward. The rule compares timestamps, so a probe that reset at one
    /// instant and then asked a question at an earlier one would be answered about a past it had already
    /// left behind — which is exactly how the wheel case first read as 「everything up」.
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) ProbeReveal()
    {
        var clock = Now;
        var height = Root.ActualHeight > 0 ? Root.ActualHeight : 900;
        var report = new List<string>();
        var wrong = new List<string>();

        // Long enough that the rule has finished settling; the two constants are its own.
        long Settle()
        {
            clock += SettleMilliseconds;
            _chrome.Tick(clock);
            return clock;
        }

        void Sample(string what, bool bar, bool title, bool rail, bool? cursorHidden = null)
        {
            Render();

            var isBar = Bar.Visibility == Visibility.Visible;
            var isTitle = TitleStrip.Visibility == Visibility.Visible;
            var isRail = Rail.Opacity > 0;

            var up = new List<string>(3);
            if (isBar) up.Add("进度条");
            if (isTitle) up.Add("标题栏");
            if (isRail) up.Add($"音量条 {Rail.Opacity:P0}");

            // The cursor only appears in the line that asked about it, and it has to appear there: the two
            // stillness samples below both read 「全部隐藏」, and without this the one difference between them
            // — the whole of 「鼠标静止不动两秒之后要自动隐藏」 — would be invisible in the report.
            report.Add($"{what}→{(up.Count == 0 ? "全部隐藏" : string.Join('+', up))}"
                       + (cursorHidden is null ? string.Empty : _cursorHidden ? "，鼠标已隐藏" : "，鼠标还在"));

            if (isBar != bar || isTitle != title || isRail != rail) wrong.Add(what);

            // A rail drawn at zero must not be clickable and a rail on screen must be, or the fade would
            // either swallow taps meant for the picture or refuse the hand reaching for the slider.
            if (Rail.IsHitTestVisible != isRail) wrong.Add($"{what}的音量条命中测试没跟上");

            // Read off the page rather than off the rule: this is the field Render pushed to ShowCursor,
            // and an unbalanced pair there leaves the cursor invisible over every window in the process.
            if (cursorHidden is { } want && _cursorHidden != want)
                wrong.Add($"{what}的鼠标{(want ? "没藏" : "没回来")}");
        }

        // 底部：requirement 9 as it now stands — 「显示进度条的时候不需要同步显示音量条」, which is the reverse of
        // 「显示进度条的时候音量条也有要显示」 this probe was written against.
        _chrome.Pointer(height - 10, height, ChromePart.None, railNear: -1, ++clock);
        Sample("指向底部", bar: true, title: false, rail: false, cursorHidden: false);

        // 右边缘：the strip that used not to trigger reliably. Sampled at the vertical middle, which is
        // where 「越接近右边的中心显示越明显」 puts it at full strength.
        _chrome.Pointer(height / 2, height, ChromePart.None, railNear: 1, ++clock);
        Sample("指向右边缘", bar: false, title: false, rail: true);

        // The control case, and the one asked before the wheel rather than after it: a flashed rail is
        // honoured for its whole grace period wherever the pointer then goes, so asking this second
        // would have reported the flash rather than the picture's own empty middle.
        _chrome.Pointer(height / 2, height, ChromePart.None, railNear: -1, ++clock);
        Sample("指向画面中间", bar: false, title: false, rail: false);

        // 滚轮：requirement 10's other half. Asked from a settled state on purpose — a wheel notch with
        // the bar already up proves nothing, and the case that matters is the pointer sitting still in
        // the middle of the picture with everything hidden.
        Settle();
        _chrome.FlashRail(++clock);
        Sample("滚轮调音量", bar: false, title: false, rail: true);

        // 静止：requirement 11's 「自动隐藏的速度再快些」, and the cursor that follows on a window of its own —
        // 「全屏播放且鼠标在画面上时，鼠标静止不动两秒之后要自动隐藏」. Sampled twice because the two windows are
        // the whole point: the chrome goes at 650 ms with the cursor still there to aim with, and only the
        // second, longer stillness takes the cursor. Settled first, so the wheel's rail grace is not still
        // running when the shorter of the two is asked about.
        Settle();
        var still = ++clock;
        _chrome.Pointer(height - 10, height, ChromePart.None, railNear: -1, still);

        clock = still + ChromeReveal.IdleMilliseconds + 1;
        _chrome.Tick(clock);
        Sample($"静止 {ChromeReveal.IdleMilliseconds}ms 后",
            bar: false, title: false, rail: false, cursorHidden: false);

        clock = still + ChromeReveal.CursorIdleMilliseconds + 1;
        _chrome.Tick(clock);
        Sample($"静止 {ChromeReveal.CursorIdleMilliseconds}ms 后",
            bar: false, title: false, rail: false, cursorHidden: true);

        // 停在控件上: 「全屏时最下方的进度条不会自动隐藏，鼠标也不会自动隐藏」. A pointer resting on a control
        // buys patience rather than immunity, and this is the case that used to have none — windowed, the
        // page notices the pointer leaving the client area and clears it; full screen there is nowhere to
        // leave to, so nothing but this timeout ever opened the latch.
        _chrome.Pointer(height - 10, height, ChromePart.Bar, railNear: -1, ++clock);
        Sample("停在进度条上", bar: true, title: false, rail: false, cursorHidden: false);

        clock += ChromeReveal.ParkedIdleMilliseconds + 1;
        _chrome.Tick(clock);
        Sample($"停在进度条上 {ChromeReveal.ParkedIdleMilliseconds}ms 后",
            bar: false, title: false, rail: false, cursorHidden: true);

        // 状态推送: the one thing that arrives at this rate while a film is actually playing, and the one
        // thing this probe never used to drive. mpv publishes four or more snapshots a second and the page
        // hands every one of them to the loading latch, which used to read 「not loading」 as activity — so
        // the idle clock was restamped four times a second and nothing ever expired:
        // 「别什么进度条标题音量条都持久显示在画面上」, then 「鼠标指针还是不会自动隐藏」. Driven at the real
        // cadence rather than asserted about, because the arithmetic was never the part that was wrong.
        var pushing = ++clock;
        _chrome.Pointer(height - 10, height, ChromePart.None, railNear: -1, pushing);

        for (var t = pushing; t <= pushing + ChromeReveal.CursorIdleMilliseconds; t += 250)
            _chrome.SetKeep(false, t);

        clock = pushing + ChromeReveal.CursorIdleMilliseconds;
        _chrome.Tick(clock);
        Sample("每 250ms 推一份状态", bar: false, title: false, rail: false, cursorHidden: true);

        // Left the way a player that is not running should be: hidden, and the cursor visible. Anything
        // else and the probe would paint a transport bar over the library grid behind it.
        _chrome.Reset(++clock);
        Settle();
        SetCursorHidden(false);
        Render();

        return (wrong.Count == 0,
            string.Join("；", report) + (wrong.Count == 0 ? string.Empty : $"；不符：{string.Join('、', wrong)}"));
    }

    /// <summary>
    /// 「加大音量条的尺寸，显示方式改为淡入淡出，鼠标指针越接近右边的中心显示越明显」, checked as three separate
    /// claims because they fail separately: the rail's measured size, the transition that draws it, and the
    /// strength curve that decides how strongly.
    /// <para>
    /// The gradient is Core's arithmetic and unit-tested there, but the two things it is arithmetic
    /// <em>about</em> are this page's: <see cref="RailNear"/> turns a real pointer position into the depth
    /// Core wants, and <see cref="FadeRail"/> turns the answer back into a pixel value. Either one wired
    /// wrongly — the zone measured off the wrong edge, the strength dropped on the floor by a
    /// <c>Visibility</c> flip left over from before — leaves a rule that passes every test and a rail that
    /// either never dims or never appears. So this drives real coordinates through the page and reads the
    /// opacity back off the element.
    /// </para>
    /// <para>
    /// The fade itself is asserted as a mechanism rather than as motion. <c>OpacityTransition</c> hands the
    /// interpolation to the compositor, which is the whole reason it was chosen: the property jumps to its
    /// new value at once and the pixels catch up, so every reader in this file — this probe,
    /// <see cref="ChromeShown"/>, <see cref="Covers"/> — stays synchronous and truthful. What can be checked
    /// on this thread is that the transition is still attached and still has a duration; a run in front of a
    /// person is what says it looks right.
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) ProbeRailFade()
    {
        var was = Visibility;
        Visibility = Visibility.Visible;
        UpdateLayout();

        if (Root.ActualWidth <= 0 || Root.ActualHeight <= 0)
        {
            Visibility = was;
            return (false, "画面还没有尺寸，量不出右侧带");
        }

        var width = Root.ActualWidth;
        var height = Root.ActualHeight;
        var clock = Now;
        var report = new List<string>();
        var wrong = new List<string>();

        void Want(string what, bool ok)
        {
            if (!ok) wrong.Add(what);
        }

        // 加大尺寸: measured rather than declared, because the numbers are in XAML and the layout is what
        // decides whether they survived — a rail crowded out by its own margin measures small with the
        // markup still reading 240.
        report.Add($"音量条 {Rail.ActualWidth:F0}×{Rail.ActualHeight:F0}，滑杆高 {VolumeSlider.ActualHeight:F0}");
        Want("音量条尺寸", VolumeSlider.ActualHeight >= 200 && Rail.ActualWidth >= 56);

        // 淡入淡出, and the standing visibility it needs: the rail is the one piece of chrome that is always
        // laid out and only ever changes strength, so a Visibility flip creeping back in here would take the
        // fade with it.
        var fade = Rail.OpacityTransition;
        report.Add($"淡入淡出={(fade is null ? "无" : $"{fade.Duration.TotalMilliseconds:F0}ms")}");
        Want("淡入淡出", fade is not null && fade.Duration > TimeSpan.Zero);
        Want("音量条常驻可见", Rail.Visibility == Visibility.Visible);

        // One pointer position, through the page's own geometry, read back off the element.
        double Strength(double x, double y)
        {
            _chrome.Pointer(y, height, ChromePart.None, RailNear(new Point(x, y)), ++clock);
            Render();
            return Rail.Opacity;
        }

        var middle = Strength(width / 2, height / 2);
        var entering = Strength(width - RailZoneWidth + 1, height / 2);
        var edgeCentre = Strength(width - 1, height / 2);
        var edgeTop = Strength(width - 1, 8);

        report.Add($"画面中间={middle:P0}，刚进右侧带={entering:P0}，右缘中央={edgeCentre:P0}，右缘靠上={edgeTop:P0}");

        Want("画面中间不显示音量条", middle == 0);
        Want("刚进右侧带就到下限", Math.Abs(entering - ChromeReveal.RailFloor) < 0.005);
        Want("右缘中央最明显", edgeCentre > 0.98);
        Want("越偏离中心越淡", edgeTop < edgeCentre - 0.1 && edgeTop >= ChromeReveal.RailFloor - 0.005);

        // 越接近…越明显 as a curve rather than as four points: nine samples across the strip at the vertical
        // middle, each at least as strong as the one to its left. Quantised to hundredths at the source, so
        // this is an ordering over exact values and not a tolerance.
        var rising = true;
        var previous = -1.0;
        for (var step = 0; step <= 8; step++)
        {
            var value = Strength(width - RailZoneWidth + step * (RailZoneWidth - 1) / 8.0, height / 2);
            if (value < previous) rising = false;
            previous = value;
        }

        report.Add($"由外向内递增={(rising ? "是" : "否")}");
        Want("由外向内递增", rising);

        // Full strength for everything that is not proximity. A wheel notch and a hand on the slider are
        // both readouts the user asked for outright, and dimming a number someone is reading would be the
        // gradient answering a question nobody asked.
        clock += SettleMilliseconds;
        _chrome.Tick(clock);
        _chrome.FlashRail(++clock);
        Render();
        var wheel = Rail.Opacity;

        clock += SettleMilliseconds;
        _chrome.Tick(clock);
        _chrome.Pointer(height / 2, height, ChromePart.Volume, railNear: -1, ++clock);
        Render();
        var onSlider = Rail.Opacity;

        report.Add($"滚轮={wheel:P0}，手在滑杆上={onSlider:P0}");
        Want("滚轮调音量看得清", wheel > 0.99);
        Want("手在滑杆上看得清", onSlider > 0.99);

        // A rail at zero must not be in the way of 「点击画面暂停」, and one on screen must take the click.
        _chrome.Reset(++clock);
        clock += SettleMilliseconds;
        _chrome.Tick(clock);
        Render();
        Want("收起后不挡点击", !Rail.IsHitTestVisible && Rail.Opacity == 0);

        // Put back the way ProbeThinLine does it: the page first, so the last Render leaves nothing of the
        // player's over the library grid behind it.
        Visibility = was;
        _chrome.Reset(++clock);
        _chrome.Tick(clock + SettleMilliseconds);
        SetCursorHidden(false);
        Render();

        return (wrong.Count == 0,
            string.Join("；", report) + (wrong.Count == 0 ? string.Empty : $"；不符：{string.Join('、', wrong)}"));
    }

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
    /// shape at all, or down to the transparent shape this window hides with — a cursor made of nothing is
    /// not a cursor, and <c>GetCursorInfo</c> reports it as showing like any other handle.
    /// Every other reading in this file is the player's own bookkeeping, which was unanimous that the cursor
    /// was hidden through three rounds of 「鼠标指针还是不会自动隐藏」; this is the one that was disagreeing.
    /// A snapshot that cannot be taken is not evidence of a cursor, so it counts as gone.
    /// </summary>
    private bool ScreenHasNoCursor() =>
        Native.CursorSnapshot() is not { } cursor
        || (cursor.Flags & 1) == 0
        || cursor.Shape == IntPtr.Zero
        || (_window is { } window && cursor.Shape == window.BlankCursor);

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
    /// </summary>
    private static string Says()
    {
        if (Native.CursorSnapshot() is not { } cursor) return "问不出";

        return $"[标志 0x{cursor.Flags:X2}，形状 0x{cursor.Shape:X}]";
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
            Native.SetCursorPos(centre.X + 40, centre.Y + 40);
            Pump();

            // The OS will not move the pointer for a process that is not in the foreground, so one grab at it
            // first — and if that does not take, a leg that says so and asserts nothing. On a machine someone
            // else is using, a chat window taking focus back is enough to deny the move, and that is nobody's
            // defect; it is the same tolerance as 「中途有人动了鼠标」 below, for the same reason.
            if (!Native.SetCursorPos(centre.X, centre.Y))
            {
                Native.SetForegroundWindow(_window!.Handle);
                Pump();
                grabbed = true;

                if (!Native.SetCursorPos(centre.X, centre.Y))
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
            // ends with the pointer somewhere other than where it was put is printed and asserted about nothing.
            var disturbed = !Native.GetCursorPos(out var ended) || ended.X != centre.X || ended.Y != centre.Y;

            report.Add($"{where}：{(hiddenAt == 0 ? $"{span}ms 过去也没藏" : $"静止 {hiddenAt - began}ms 就藏了")}"
                + $"，线程形状={Mine()}"
                + $"，轮询问出 {_polledMoves - polls} 次移动、XAML 事件 {_pointerMoves - moves} 次、空事件 {_stillMoves} 次"
                + $"，我们推了 {pushed} 拍、计时器自己 {Math.Max(0, _tickCount - ticks - pushed)} 拍"
                + $"，让系统重新问了 {_cursorNudges - nudges} 次"
                + (disturbed ? "，中途有人动了鼠标，这一轮只作参考" : string.Empty));

            if (disturbed) return;

            // The seeding must have come from somewhere, or 「藏了」 below would be true of a rule that never
            // knew where the pointer was — and a rule that believes the pointer is nowhere hides nothing.
            Want($"{where}轮询问出了指针", _polledMoves > polls);

            // The whole of the request, asked of real seconds: 「鼠标静止不动两秒之后要自动隐藏」.
            Want($"{where}静止两秒后鼠标真藏了", _cursorHidden);
            Want($"{where}藏着的时候线程没有形状", NoShape());

            // And the half of hiding that only this count can vouch for: the OS works out what the pointer is
            // over when the pointer moves, and this hide happens because nothing is moving, so without the
            // zero-displacement nudge at the end of it the last shape it worked out stays on the screen. That
            // is what 「静止超过两秒后鼠标指针还是不会自动隐藏」 was, from a player whose own readings all said
            // hidden. The desktop's own answer cannot stand in for this — see Screen.
            Want($"{where}藏的时候让系统重新问了一次", _cursorNudges > nudges);

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
        // is precisely the pointer not moving, so the same reading is taken again after each of three ways of
        // making it ask without moving anything, in order of how little they disturb.
        //
        // Printed, and asserted about nothing. <c>GetCursorInfo</c> answers for the desktop, and on this
        // machine it answers 「arrow」 windowed and 「nothing」 full screen, run after run, with this thread's
        // queue holding no shape either way and the screen doing the right thing — the last real mouse movement
        // happened over another application's window, and until one happens over ours that is whose cursor the
        // desktop is still describing. Measured both ways, including a real displacement and a fresh hide at
        // the point it landed on, which did not change the windowed answer either. So the assertion that
        // stands for this is 「藏的时候让系统重新问了一次」 in <c>Watch</c>: the ask is what this process is
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

            // Cheapest first: put the pointer where it already is. Costs nothing if it works, and both the
            // event filter and the poll ignore a zero displacement, so the stillness being measured survives.
            Native.SetCursorPos(at.X, at.Y);
            Pump();
            lines.Add($"同点重设后 系统 {Says()}，线程形状={Mine()}");

            // A mouse event with a displacement of zero: the pointer stays put, the OS still goes through the
            // whole 「who owns this point, what shape do they want」 round it does on a real move.
            var sent = Native.NudgeCursorState();
            Pump();
            lines.Add($"零位移注入{(sent ? string.Empty : "（发不出去）")}后 系统 {Says()}，线程形状={Mine()}");

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

            // What the desktop says about it, printed and not asserted; see the note on Screen for why.
            var settled = front && front == wasFront && !grabbed;

            lines.Add($"能挪指针={Steers(at)}");
            lines.Add($"系统说屏幕上没有光标={ScreenHasNoCursor()}"
                + (settled ? string.Empty : "（前台是刚抢到的，这一读数还归上一个拿着光标的窗口）"));

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

    /// <summary>
    /// 底边细进度线: on screen when the transport bar is down, and only in a window.
    /// <para>
    /// The one piece of chrome whose rule is the inverse of all the rest, which is why the reveal rule
    /// cannot state it — and 「全屏时最下方会有进度条」 was the half of it nobody had said out loud. Full screen
    /// those two bright pixels run the whole width of the monitor with no window edge to belong to: a
    /// progress bar standing over the film, rather than a readout along the edge of a window that has edges
    /// anyway. Driven through the real window, because 「窗口化」 is a question only the window can answer.
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) ProbeThinLine()
    {
        if (!Attached || _window is null) return (false, "播放层未接线");
        if (_window.Fullscreen) return (false, "窗口已在全屏，无从对比");

        var was = Visibility;
        Visibility = Visibility.Visible;
        UpdateLayout();

        var clock = Now;
        var report = new List<string>();
        var wrong = new List<string>();

        // The state the line belongs to: the pointer in the dead middle of the picture, and long enough
        // since it moved that nothing else is on screen. Re-driven after each window change, because the
        // fullscreen transition reseeds the rule from wherever the real cursor happens to be resting.
        void Settle()
        {
            var height = Root.ActualHeight > 0 ? Root.ActualHeight : 900;
            _chrome.Pointer(height / 2, height, ChromePart.None, railNear: -1, ++clock);
            clock += SettleMilliseconds;
            _chrome.Tick(clock);
            Render();
        }

        void Sample(string what, bool wanted)
        {
            var shown = ThinLine.Visibility == Visibility.Visible;
            report.Add($"{what}→{(shown ? "有细线" : "没有")}");
            if (shown != wanted) wrong.Add(what);
        }

        Settle();
        Sample("窗口化时浮层收起", wanted: true);

        try
        {
            _window.Fullscreen = true;
            Settle();
            Sample("全屏时浮层收起", wanted: false);

            // And with the bar back up, where the transport bar covers those two pixels itself.
            _chrome.WakeFully(++clock);
            Render();
            Sample("全屏时浮层展开", wanted: false);
        }
        finally
        {
            _window.Fullscreen = false;
        }

        Settle();
        Sample("退出全屏后", wanted: true);

        // Left the way a player that is not running should be, for the same reason ProbeReveal is — the page
        // put back first, so the last Render leaves the line down rather than across the library grid.
        Visibility = was;
        _chrome.Reset(++clock);
        _chrome.Tick(clock + SettleMilliseconds);
        Render();
        SetCursorHidden(false);
        UpdateLayout();

        return (wrong.Count == 0,
            string.Join("；", report) + (wrong.Count == 0 ? string.Empty : $"；不符：{string.Join('、', wrong)}"));
    }

    /// <summary>
    /// Opens the 统计 panel, fills it through the real formatting path, and puts it away again.
    /// <para>
    /// <see cref="PlaybackStats"/> is unit-tested on its own; what cannot be tested there is this page's
    /// half — that the toggle, the panel and <see cref="PlayerViewModel.StatsOpen"/> are wired to each
    /// other rather than each to itself, that the two brush keys <see cref="RenderStatRows"/> asks for
    /// resolve in this page's own resource scope, and that the grid it draws into really has the second
    /// column the values go in. Every one of those is a crash or a blank panel the first time a user
    /// presses 统计, and none of them shows up in a build.
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) ProbeStats()
    {
        if (!Attached) return (false, "播放层未接线");

        // A partial reading set on purpose: mpv answers nothing at all for a property that does not apply
        // to the file, so the omissions are the interesting half. No audio-params here, which is the
        // silent-file case, and the panel should come out with no 声道与采样率 row rather than a blank one.
        var readings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["width"] = "1920",
            ["height"] = "1080",
            ["video-codec"] = "h264 (High)",
            ["audio-codec-name"] = "aac",
            ["hwdec-current"] = "d3d11va-copy",
            ["current-vo"] = "gpu-next",
            ["video-bitrate"] = "4200000",
            ["avsync"] = "-0.002",
            ["container-fps"] = "23.976"
        };

        // Through the bound property rather than through a method of its own: the button, the panel and the
        // view model share one flag now, and driving that flag is what proves the three agree.
        ViewModel.StatsOpen = true;
        var opened = StatsPanel.Visibility == Visibility.Visible && (StatsButton.IsChecked ?? false);

        var rows = PlaybackStats.Format(readings);
        RenderStatRows(rows);
        var drawn = StatsRows.Children.Count;
        var lines = StatsRows.RowDefinitions.Count;

        // The empty case has its own row, and it is the one a user actually sees first.
        RenderStatRows([]);
        var waiting = StatsRows.Children.Count == 1;

        ViewModel.StatsOpen = false;
        var closed = StatsPanel.Visibility == Visibility.Collapsed
                     && !(StatsButton.IsChecked ?? false)
                     && StatsRows.Children.Count == 0;

        // Two children per row — the label and its value — is what proves both columns were reached.
        var ok = opened && closed && waiting && rows.Count > 0 && drawn == rows.Count * 2 && lines == rows.Count;

        return (ok, $"{PlaybackStats.Fields.Count} 个属性，{rows.Count} 行 → {drawn} 个文本块／{lines} 行高"
                    + $"；开={opened}，空态占位={waiting}，关={closed}");
    }

    /// <summary>
    /// Builds the right-click 画面菜单 the way the first right click would, and checks every row of the
    /// catalogue survived the trip. Counted against <see cref="PlayerMenuCatalog.Flatten"/> rather than
    /// against a number written here, so a row added to the catalogue is covered the day it is added.
    /// </summary>
    internal (bool Ok, string Detail) ProbePictureMenu()
    {
        OnPictureMenuOpening(this, new object());

        var (rows, groups, rules) = CountMenu(PictureMenu.Items);

        var catalogue = PlayerMenuCatalog.Flatten(PlayerMenuCatalog.Root).ToList();
        var wantRows = catalogue.Count(node => node.Kind is not (PlayerMenuKind.Group or PlayerMenuKind.Separator));
        var wantGroups = catalogue.Count(node => node.Kind == PlayerMenuKind.Group);
        var wantRules = catalogue.Count(node => node.Kind == PlayerMenuKind.Separator);

        // 裁切填充 by name, because it is the row the whole menu was missing for: panscan has no button
        // anywhere else in the chrome, so if this one row failed to build the feature would be unreachable
        // again with every count still adding up.
        var panscan = catalogue.Any(node => node.Label.Contains("裁切填充", StringComparison.Ordinal));

        var ok = rows == wantRows && groups == wantGroups && rules == wantRules && panscan;

        return (ok, $"{rows}/{wantRows} 行、{groups}/{wantGroups} 个子菜单、{rules}/{wantRules} 条分隔线"
                    + $"，裁切填充={(panscan ? "在" : "缺")}");
    }

    /// <summary>Walks a built flyout the same way <see cref="BuildMenuItems"/> built it.</summary>
    private static (int Rows, int Groups, int Rules) CountMenu(IList<MenuFlyoutItemBase> items)
    {
        var rows = 0;
        var groups = 0;
        var rules = 0;

        foreach (var item in items)
        {
            switch (item)
            {
                case MenuFlyoutSeparator:
                    rules++;
                    break;

                case MenuFlyoutSubItem group:
                    groups++;
                    var inner = CountMenu(group.Items);
                    rows += inner.Rows;
                    groups += inner.Groups;
                    rules += inner.Rules;
                    break;

                default:
                    rows++;
                    break;
            }
        }

        return (rows, groups, rules);
    }

    /// <summary>
    /// Opens all five control-bar pickers the way a click on each would, and reports what they built.
    /// <para>
    /// Every one of them is filled by its <c>Opening</c> handler rather than declared in XAML, so until
    /// something opens them they are five empty <c>MenuFlyout</c>s that cannot fail. A missing resource
    /// key or a null dereference in any of the builders would first be seen by a user mid-film, which is
    /// the worst possible moment and the reason this probe exists.
    /// </para>
    /// <para>
    /// Nothing is playing, so what is being proved is the empty case of each: 单集 has no episode list,
    /// the two track pickers have no tracks, 倍速 is built from a static list and should be full anyway,
    /// and 更多 reads settings rather than the file and should also be full. An empty picker that builds a
    /// 「没有可选的…」 row is a pass; one that builds nothing at all is not.
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) ProbeControlMenus()
    {
        if (!Attached) return (false, "播放层未接线");

        var built = new List<string>(5);
        var failures = 0;

        void Open(string name, MenuFlyout menu, Action opening, int least)
        {
            try
            {
                opening();
                var (rows, groups, rules) = CountMenu(menu.Items);
                var total = rows + groups;
                if (total < least) failures++;
                built.Add($"{name} {rows} 行"
                          + (groups > 0 ? $"+{groups} 子菜单" : string.Empty)
                          + (rules > 0 ? $"+{rules} 分隔" : string.Empty));
            }
            catch (Exception error)
            {
                failures++;
                built.Add($"{name} 出错（{error.GetType().Name}）");
                Log.Warn(Category, $"自检打开「{name}」失败", error);
            }
        }

        // 单集 and the two track pickers each owe one row even with nothing loaded — the 「没有可选的单集」
        // placeholder and 字幕's 关闭字幕. 倍速 owes one per SpeedChoices entry, 更多 owes its seven rows.
        Open("单集", EpisodeMenu, () => OnEpisodeMenuOpening(this, new object()), 1);
        Open("音轨", AudioMenu, () => OnAudioMenuOpening(this, new object()), 1);
        Open("字幕", SubtitleMenu, () => OnSubtitleMenuOpening(this, new object()), 1);
        Open("倍速", SpeedMenu, () => OnSpeedMenuOpening(this, new object()), PlayerViewModel.SpeedChoices.Length);
        Open("更多", MoreMenu, () => OnMoreMenuOpening(this, new object()), 6);

        return (failures == 0, string.Join("、", built));
    }

    /// <summary>
    /// 需求 7：the 字幕字体 box in the title strip — where it sits, whether the machine's families reached it,
    /// what a typed line resolves to, and the three things it does to the rest of the player.
    /// <para>
    /// Every one of them is invisible to a build and to a unit test, and every one of them is a box that
    /// looks perfectly fine until it is used. A control left out of the strip's own hit list is one whose
    /// press starts a window drag instead of placing a caret — the window then follows the hand. A hold
    /// that the reveal rule can drop is a strip that slides away from under what is being typed into it.
    /// A player that still owns single letters is one where spelling 「Consolas」 mutes the film and skips
    /// to the next episode. And <c>Resolve</c> is what stops Enter picking the family already in use: the
    /// current one is pinned into the list whatever is typed, so 「the first row」 is the wrong answer.
    /// </para>
    /// <para>
    /// Nothing here writes. The value is read and compared, never set: the row commits only when a family
    /// is picked, and no family is picked — same rule as <see cref="SettingFontRow.Measure"/>, and for the
    /// same reason, which is that a self-check has no business changing what the user watches films with.
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) ProbeSubtitleFont()
    {
        if (!Attached) return (false, "播放层未接线");

        var was = Visibility;
        var text = FontBox.Text;

        Visibility = Visibility.Visible;
        UpdateLayout();

        // Two passes, as in ProbeWindowCommands: the first sizes the page, the second sizes the strip that
        // Render has just revealed. Without the second the box measures zero and every question below is
        // answered about nothing.
        var clock = Now;
        _chrome.WakeFully(clock);
        Render();
        UpdateLayout();

        ViewModel.PrepareFonts();

        var trouble = new List<string>();
        var strip = BoundsOf(TitleStrip);
        var box = BoundsOf(FontBox);

        if (box.Width <= 0 || box.Height <= 0) trouble.Add("输入栏没有尺寸");
        else if (!Encloses(strip, box)) trouble.Add("输入栏越出了标题栏");

        foreach (var (name, other) in new (string Name, FrameworkElement Element)[]
                 {
                     ("统计", StatsButton),
                     ("置顶", PinButton),
                     ("窗口命令", WindowButtons)
                 })
        {
            if (Overlaps(box, BoundsOf(other))) trouble.Add($"输入栏压住了{name}");
        }

        // 右上角, structurally: right of the title, left of the three window commands. Both hold by the
        // grid's columns today, which is the point — a box moved into the wrong column still lays out.
        if (box.Left < BoundsOf(TitleText).Left) trouble.Add("输入栏跑到标题左边去了");
        if (box.Right > BoundsOf(WindowButtons).Left + GeometrySlack) trouble.Add("输入栏挤进了窗口命令那一角");

        // The strip's own hit list, asked at the middle of the box. A press there must not begin a drag.
        var centre = new Point(box.Left + (box.Width / 2), box.Top + (box.Height / 2));
        if (!OnStripControl(centre)) trouble.Add("按在输入栏上会开始拖窗口");

        var value = ViewModel.SubtitleFont.Value;
        if (!string.Equals(value, ViewModel.SubtitleFontSetting, StringComparison.Ordinal))
            trouble.Add($"输入栏显示的「{value}」不是设置里的「{ViewModel.SubtitleFontSetting}」");

        // The search, driven through the box's own path rather than the row's, and the row's own probe is
        // what covers the filter itself. A word out of the current family's name — 「YaHei」 for Microsoft
        // YaHei — because it is the one query that must match on every machine.
        SearchFonts(string.Empty);
        var all = ViewModel.SubtitleFont.Matches.Count;
        var total = ViewModel.SubtitleFont.Total;

        var word = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? value;
        SearchFonts(word);
        var found = ViewModel.SubtitleFont.Matches.Count;
        var byWord = ViewModel.SubtitleFont.Resolve(word);
        var byName = ViewModel.SubtitleFont.Resolve(value);

        const string nowhere = "没有任何字体会叫这个名字";
        SearchFonts(nowhere);
        var pinned = ViewModel.SubtitleFont.Matches.Count;
        var byNothing = ViewModel.SubtitleFont.Resolve(nowhere);

        SearchFonts(string.Empty);

        if (!ViewModel.FontsReady) trouble.Add("字体还没扫完");
        if (total <= 1) trouble.Add($"只有 {total} 个字体族");
        if (all != total) trouble.Add($"空搜索只列出 {all} / {total} 个");
        if (found == 0) trouble.Add($"搜「{word}」什么都没有");
        if (byWord is null) trouble.Add($"搜「{word}」按回车会落空");
        if (!string.Equals(byName?.Name, value, StringComparison.OrdinalIgnoreCase))
            trouble.Add($"整名字「{value}」按回车选到了「{byName?.Name ?? "空"}」");

        // The one that matters most: the current family is in that list of one, and Enter must still refuse
        // it — a query that matched nothing means nothing, not 「keep what you had by picking it again」.
        if (byNothing is not null) trouble.Add($"搜不存在的名字按回车会选到「{byNothing.Name}」");

        // What the box does to the chrome and to the keyboard, including the collision a single shared hold
        // flag used to have: a flyout opened and closed over a box that still has the keyboard.
        BeginFontSearch();
        var typing = _typing;
        var pinnedChrome = _chrome.HoldChrome;

        Hold(true, ChromeHold.Menu);
        Hold(false, ChromeHold.Menu);
        var survived = _chrome.HoldChrome;

        _chrome.Tick(clock + SettleMilliseconds);
        Render();
        var stillUp = TitleStrip.Visibility == Visibility.Visible;

        EndFontSearch();
        var released = !_typing && !_chrome.HoldChrome;

        if (!typing) trouble.Add("拿到键盘后播放键位没有让开");
        if (!pinnedChrome) trouble.Add("拿到键盘后标题栏没有被钉住");
        if (!survived) trouble.Add("菜单开关一次就把输入栏的钉子拔了");
        if (!stillUp) trouble.Add("输入中标题栏还是自己收起来了");
        if (!released) trouble.Add("离开输入栏后钉子没有拔掉");

        // Left the way a player that is not running should be, same as every other probe here.
        _chrome.Reset(++clock);
        _chrome.Tick(clock + SettleMilliseconds);
        Render();
        SetCursorHidden(false);
        FontBox.Text = text;
        Visibility = was;
        UpdateLayout();

        return (trouble.Count == 0,
            $"输入栏 {box.Width:0}×{box.Height:0} 逻辑像素，在标题栏右上角；当前「{value}」，共 {total} 个字体族，"
            + $"搜「{word}」得 {found} 个，搜不存在的名字剩 {pinned} 个（只有当前值）；"
            + (trouble.Count == 0
                ? "回车能认出整名字、认不出的不乱选，输入时钉住标题栏且不吃播放键位"
                : string.Join('、', trouble)));
    }

    /// <summary>
    /// The 跳过 offer and the seek bar's chapter ticks, both of which need a file to appear on their own.
    /// <para>
    /// The offer is driven through the view model's own <see cref="PlayerViewModel.ShowSkipPrompt"/> with a
    /// prompt a coordinator produced for a synthetic 片头, so what is checked is the real mapping onto the
    /// button's four bindings rather than a copy of it written here. The coordinator is this probe's own
    /// rather than the view model's: the live one is holding whatever the current file resolved to, and a
    /// self-check has no business replacing it.
    /// </para>
    /// <para>
    /// The ticks are drawn against an explicit width for the same reason the reveal probe drives an
    /// explicit clock: the track has no measured width while the player is collapsed, and 「drew nothing」
    /// would be the correct answer to the wrong question.
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) ProbeSkipAndChapters()
    {
        if (!Attached) return (false, "播放层未接线");

        // 30..120s of a 25-minute episode: long enough to be recognised, and it starts late enough that
        // the section is not simply the whole file.
        var opening = new SkipSection(SkipSectionKind.Opening, 30, 120, "片头", true);
        var skips = new SkipCoordinator { Mode = SkipSectionMode.Ask };

        skips.Begin([opening], 1500);
        var jump = skips.Advance(31, playing: true);

        ViewModel.ShowSkipPrompt(skips.Prompt);
        var offered = SkipButton.Visibility == Visibility.Visible
                      && SkipText.Text == "跳过片头"
                      && SkipCountdown.Value > 0.9;
        var caption = SkipText.Text;

        // 自动 mode is the other half of the same coordinator, and the one that must not put a button up.
        skips.Begin([opening], 1500);
        skips.Mode = SkipSectionMode.Auto;
        var jumped = skips.Advance(31, playing: true);
        ViewModel.ShowSkipPrompt(skips.Prompt);
        var silent = SkipButton.Visibility == Visibility.Collapsed;

        // Four chapters, of which the one at zero is the start of the file rather than a boundary: three
        // ticks is the right answer, and a renderer that drew four would put one hard against the left end.
        var marks = new List<SkipChapter>
        {
            new(0, "片头"),
            new(120, "第一节"),
            new(700, "第二节"),
            new(1400, "片尾")
        };

        RenderChapterTicks(marks, 600, 1500);
        var ticks = ChapterTicks.Children.Count;

        // Put everything back: nothing is playing, and a button or a tick left behind would be drawn over
        // the library grid the moment the player's own visibility says it may.
        RenderChapterTicks();
        ViewModel.ShowSkipPrompt(SkipPrompt.None);

        var ok = offered
                 && silent
                 && jump is null
                 && jumped is not null
                 && ticks == 3
                 && SkipButton.Visibility == Visibility.Collapsed
                 && ChapterTicks.Children.Count == 0;

        return (ok, $"询问：「{caption}」{(offered ? "已提供" : "未提供")}；自动：{(silent ? "直接跳过不提示" : "仍在提示")}"
                    + $"；章节刻度 {ticks}/3 条（4 个标记，起点不算）");
    }

    /// <summary>
    /// 点击画面暂停, asked where it can actually be got wrong: the geometry. The gate is put to the centre of
    /// the picture and to the centre of five things drawn over it with the chrome up, and then to the bar's
    /// own strip again with the chrome down, where the same point is film.
    /// <para>
    /// The page is laid out for the duration and put back. A collapsed player measures zero, so every
    /// control reports no size, <see cref="Covers"/> answers false everywhere, and a player that paused on
    /// top of its own play button would pass. Both flips happen inside this one call, so no frame is
    /// composed between them and the transport never appears over the library behind it.
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) ProbeTap()
    {
        var was = Visibility;
        Visibility = Visibility.Visible;
        UpdateLayout();

        var clock = Now;
        _chrome.WakeFully(clock);
        Render();

        // Twice, and both are load-bearing: the first pass gives the page its own size, and this one gives
        // it to the strips Render has just revealed. Without it every control still measures zero, Covers
        // answers false, and the whole chrome reads as picture — which is how this probe first came out
        // green on a page where all five controls were false positives.
        UpdateLayout();

        var width = Root.ActualWidth;
        var height = Root.ActualHeight;
        var centre = TapOnPicture(new Point(width / 2, height / 2));

        // The five that a hand aiming at a control would land on. Named, because a report saying 「one of
        // them pauses」 leaves the reader to find out which.
        var missed = new List<string>();
        foreach (var (name, element) in new (string Name, FrameworkElement Element)[]
                 {
                     ("播放键", PlayButton),
                     ("进度条", SeekTrack),
                     ("返回", BackButton),
                     ("音量条", Rail),
                     ("全屏", FullscreenButton)
                 })
        {
            if (element.Visibility == Visibility.Visible && TapOnPicture(Middle(element))) missed.Add(name);
        }

        // Chrome down: the strip is gone and its pixels are the film again, so the same point must pause.
        _chrome.Reset(++clock);
        _chrome.Tick(clock + SettleMilliseconds);
        Render();
        UpdateLayout();
        var uncovered = TapOnPicture(new Point(width / 2, height - 2));

        SetCursorHidden(false);
        Visibility = was;
        UpdateLayout();

        var ok = width > 0 && height > 0 && centre && missed.Count == 0 && uncovered
                 && !ChromeShown && Visibility == was;

        return (ok, $"{width:0}×{height:0} 逻辑像素：画面中央→{(centre ? "暂停" : "不暂停")}"
                    + $"；浮层五处控件{(missed.Count == 0 ? "都不暂停" : $"有 {string.Join('、', missed)} 会误触")}"
                    + $"；浮层收起后底边→{(uncovered ? "暂停" : "不暂停")}");
    }

    /// <summary>The centre of <paramref name="element"/> in <c>Root</c>'s own coordinates.</summary>
    private Point Middle(FrameworkElement element)
    {
        var origin = element.TransformToVisual(Root).TransformPoint(new Point(0, 0));
        return new Point(origin.X + element.ActualWidth / 2, origin.Y + element.ActualHeight / 2);
    }

    /// <summary>
    /// 暂停/播放 角标: that the one second of acknowledgement really runs — 「暂停后显示一秒暂停图标就行（开启播放
    /// 也弄个一秒的动画）」 — and shows the right glyph for what just happened.
    /// <para>
    /// The animation is begun for real, which is the whole point of the probe. A
    /// <c>Storyboard.TargetName</c> is resolved against a namescope at <c>Begin</c> and not before, so a
    /// badge renamed, or moved inside a template, throws 「Cannot resolve TargetName」 the first time somebody
    /// presses space — mid-film, on the frame they just paused, which is the least forgiving moment this app
    /// has. A build cannot see it and neither can a unit test: the storyboard is markup and the names it
    /// reaches for only exist once the page has been loaded.
    /// </para>
    /// <para>
    /// Both glyphs are asked for, in the order a pause and a resume produce them, because they are
    /// deliberately the opposite way round from the transport button's: a button says what pressing it will
    /// do, and this says what just happened. A crossed pair would tell every pause it had resumed, and would
    /// look entirely deliberate.
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) ProbePulse()
    {
        // Laid out for the duration and put back, as ProbeTap does: a collapsed page has no namescope
        // trouble to run into because nothing it names has been realised.
        var was = Visibility;
        Visibility = Visibility.Visible;
        UpdateLayout();

        var seen = new List<string>();
        var wrong = new List<string>();

        void Beat(string what, bool paused, int codepoint)
        {
            try
            {
                Pulse(paused);
            }
            catch (Exception error)
            {
                // The failure this probe exists for. Reported rather than thrown: the self-check's job is to
                // come back with a report, and a probe that takes the run down with it has none.
                wrong.Add($"{what}没能开始动画（{error.Message}）");
                return;
            }

            var shown = PulseBadge.Visibility == Visibility.Visible;
            var running = _pulse.GetCurrentState() is ClockState.Active or ClockState.Filling;
            var right = PulseGlyph.Glyph == Glyph(codepoint);

            // 「不要黑色的圆形边框，只要白色的三角形」: the plate is gone, so what is asserted now is that
            // nothing draws one — no fill, no ring — and that the glyph is the size the user asked for
            // rather than the 40 it was inside the circle, with the rim behind it carrying the same shape.
            var bare = PulseBadge.Background is null
                       && PulseBadge.BorderBrush is null
                       && PulseBadge.BorderThickness.Left == 0
                       && PulseBadge.BorderThickness.Top == 0
                       && PulseBadge.BorderThickness.Right == 0
                       && PulseBadge.BorderThickness.Bottom == 0;
            var big = PulseGlyph.FontSize >= 120 && PulseRim.FontSize > PulseGlyph.FontSize;
            var rimmed = PulseRim.Glyph == PulseGlyph.Glyph;

            seen.Add($"{what}→{(shown ? "出角标" : "没出角标")}"
                     + $"，{(running ? "动画在跑" : "动画没跑")}"
                     + $"，图标{(right ? "对" : "不对")}"
                     + $"，{(bare ? "没有底板" : "还有底板")}"
                     + $"，字号 {PulseGlyph.FontSize:0}/描边 {PulseRim.FontSize:0}{(rimmed ? "" : "（描边图标不一样）")}");

            if (!shown || !running || !right || !bare || !big || !rimmed) wrong.Add(what);
        }

        Beat("暂停", paused: true, PauseGlyphCode);
        Beat("恢复", paused: false, PlayGlyphCode);

        // Back to how a player nobody has paused looks: no clock in flight, and nothing drawn over whatever
        // page the shell is really on.
        _pulse.Stop();
        PulseBadge.Visibility = Visibility.Collapsed;
        Visibility = was;
        UpdateLayout();

        var ok = wrong.Count == 0 && PulseBadge.Visibility == Visibility.Collapsed && Visibility == was;

        return (ok, string.Join("；", seen)
                    + (wrong.Count == 0 ? "；结束后收起" : $"；不符：{string.Join('、', wrong)}"));
    }

    /// <summary>
    /// 最小化/最大化/关闭: that the three window commands are where a hand expects them and are not sitting on
    /// top of one another.
    /// <para>
    /// Playback takes the caption off the window, so these three are the only way back to a normal window
    /// short of the keyboard — and they are drawn by us, into a strip that also carries 返回, 统计 and 置顶.
    /// Nothing in a build or a unit test can see that one of them ended up outside the strip it is clipped
    /// by, or underneath the 置顶 toggle: both are a button that looks present and cannot be pressed.
    /// </para>
    /// <para>
    /// The two glyph checks are the other half. <see cref="UpdateMaximizeGlyph"/> and
    /// <see cref="ToggleFullscreen"/> each write one <c>FontIcon</c>, they are adjacent in the same strip,
    /// and a crossed pair would leave the window's own state being reported by the wrong button — which no
    /// amount of compiling would notice.
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) ProbeWindowCommands()
    {
        if (!Attached || _window is null) return (false, "播放层未接线");

        var was = Visibility;
        Visibility = Visibility.Visible;
        UpdateLayout();

        // Same two passes, for the same reason as ProbeTap: the first sizes the page, the second sizes the
        // strip that Render has just revealed. Without the second every button measures zero and every
        // geometry question below is answered about nothing.
        var clock = Now;
        _chrome.WakeFully(clock);
        Render();
        UpdateLayout();

        var strip = BoundsOf(TitleStrip);
        var trouble = new List<string>();

        foreach (var (name, button) in new (string Name, FrameworkElement Element)[]
                 {
                     ("最小化", MinimizeButton),
                     ("最大化", MaximizeButton),
                     ("关闭", CloseButton)
                 })
        {
            // 最大化 is the one that is meant to go away, and only in fullscreen — where the window's edges
            // are the monitor's and the button could do nothing at all.
            if (button.Visibility != Visibility.Visible)
            {
                if (button != MaximizeButton || !_window.Fullscreen) trouble.Add($"{name}不见了");
                continue;
            }

            var box = BoundsOf(button);
            if (box.Width <= 0 || box.Height <= 0) trouble.Add($"{name}没有尺寸");
            else if (!Encloses(strip, box)) trouble.Add($"{name}越出了标题栏");
            else if (Overlaps(box, BoundsOf(StatsButton)) || Overlaps(box, BoundsOf(PinButton)))
                trouble.Add($"{name}压住了别的按钮");
        }

        if (_window.Fullscreen && MaximizeButton.Visibility == Visibility.Visible) trouble.Add("全屏时最大化仍在");

        // The glyphs the two toggles write. Read after Render, which is what calls UpdateMaximizeGlyph.
        if (MaximizeGlyph.Glyph != Glyph(_window.IsMaximized ? RestoreGlyphCode : MaximizeGlyphCode))
            trouble.Add("最大化图标与窗口不符");

        if (FullscreenGlyph.Glyph != Glyph(_window.Fullscreen ? FullscreenExitCode : FullscreenEnterCode))
            trouble.Add("全屏图标与窗口不符");

        // Left the way a player that is not running should be, for the same reason ProbeReveal is.
        _chrome.Reset(++clock);
        _chrome.Tick(clock + SettleMilliseconds);
        Render();
        SetCursorHidden(false);
        Visibility = was;
        UpdateLayout();

        return (trouble.Count == 0,
            $"标题栏 {strip.Width:0}×{strip.Height:0} 逻辑像素，三个窗口命令"
            + (trouble.Count == 0 ? "都在栏内且互不重叠" : string.Join('、', trouble))
            + $"；最大化={(MaximizeButton.Visibility == Visibility.Visible ? "在" : "隐藏")}"
            + $"，窗口{(_window.IsMaximized ? "已最大化" : "未最大化")}"
            + $"，{(_window.Fullscreen ? "全屏" : "窗口化")}");
    }

    /// <summary>
    /// 章节预览: where the hover box lands, what it contains, and that it leaves with the bar it belongs to.
    /// <para>
    /// Three separate things have been wrong here and none of them is visible to a compiler. The box is
    /// placed by a <c>TranslateTransform</c> this page computes rather than by layout, so an off-by-a-box-width
    /// sends it off the side of the picture — and the pointer is at the far end of the seek bar exactly when
    /// that happens, which is the one place a user will notice and the one place a centred test would not.
    /// The second is 视频进度条预览不会自动消失: the preview outliving its bar, stranded over the film with
    /// nothing to explain it. The third is 预览没有画面 — a fixed-size <c>Image</c> holding no source, which
    /// is what every file looks like on a server that never extracted chapter stills.
    /// </para>
    /// <para>
    /// Three positions rather than one, and the two ends are the point: the middle of the bar cannot be
    /// clamped wrongly because it needs no clamping.
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) ProbePeek()
    {
        if (!Attached) return (false, "播放层未接线");

        var was = Visibility;
        Visibility = Visibility.Visible;
        UpdateLayout();

        var clock = Now;
        _chrome.WakeFully(clock);
        Render();
        UpdateLayout();

        // Shown before it is placed, and laid out in between: the clamp reads the box's own measured width,
        // and a collapsed Border measures nothing at all.
        //
        // Filled first, and with a still: the box is at its widest with a picture in it, which is the case
        // the clamp has to survive. An empty BitmapImage is enough — what is being proved is that a source
        // reaches the Image and reveals it, not that any particular JPEG decodes.
        var caption = ViewModel.ChapterCaption;
        ViewModel.ChapterCaption = "片头";
        ViewModel.ChapterClock = "00:02:15";
        ViewModel.ChapterStill = new BitmapImage();

        ChapterPeek.Visibility = Visibility.Visible;
        UpdateLayout();

        var framed = ChapterImage.Visibility == Visibility.Visible;
        var withStill = BoundsOf(ChapterPeek);

        var track = SeekTrack.ActualWidth;
        var picture = BoundsOf(Root);
        var strayed = new List<string>();

        foreach (var (name, x) in new (string Name, double X)[] { ("左端", 0), ("中间", track / 2), ("右端", track) })
        {
            PositionChapterPeek(x);
            UpdateLayout();

            var box = BoundsOf(ChapterPeek);
            if (box.Width <= 0 || box.Height <= 0) strayed.Add($"{name}没有尺寸");
            else if (!Encloses(picture, box)) strayed.Add($"{name}越出画面");
        }

        // 预览没有画面: a server that extracted no chapter images has none to send — every ChapterInfo then
        // arrives without an ImageTag and nothing is even fetched — and a fixed 212×119 Image with no Source
        // is an empty frame rather than nothing at all. The picture has to collapse and leave the two text
        // lines, which is a shrinking box: the same measurement that proves the frame is gone.
        ViewModel.ChapterStill = null;
        UpdateLayout();

        var textOnly = BoundsOf(ChapterPeek);
        var hollow = ChapterImage.Visibility == Visibility.Collapsed
                     && textOnly.Height > 0
                     && textOnly.Height < withStill.Height - 100;

        // With the bar up the preview is the pointer's own business and must be left alone.
        Render();
        var kept = ChapterPeek.Visibility == Visibility.Visible;

        // The bar going takes it: this is the half that a PointerExited over the track never raises.
        _chrome.Reset(++clock);
        _chrome.Tick(clock + SettleMilliseconds);
        Render();
        var gone = ChapterPeek.Visibility == Visibility.Collapsed;

        HideChapterPeek();
        SetCursorHidden(false);
        ViewModel.ChapterCaption = caption;
        Visibility = was;
        UpdateLayout();

        var ok = track > 0 && strayed.Count == 0 && framed && hollow && kept && gone;

        return (ok, $"进度条 {track:0} 逻辑像素宽，预览三处定位"
                    + (strayed.Count == 0 ? "都在画面内" : string.Join('、', strayed))
                    + $"；有缩略图时{(framed ? $"显示画面（{withStill.Height:0} 高）" : "没有显示画面")}"
                    + $"，没有缩略图时{(hollow ? $"只剩文字（{textOnly.Height:0} 高）" : "仍留着空画框")}"
                    + $"；浮层在时{(kept ? "保留" : "被收走")}"
                    + $"，浮层收起后{(gone ? "随之消失" : "仍然停留")}");
    }

    /// <summary>
    /// 画面比例: that the ratio the view model resolved actually reaches the window, and that zero releases it.
    /// <para>
    /// <c>AspectLock</c> works out the rectangle and is unit-tested doing it; what has no other test is the
    /// one hop in between — the view model's event, this page's handler, and the window's property. A
    /// handler that computed the right number and wrote it nowhere would leave 窗口化时视频有黑边 exactly as
    /// it was, and a build would be perfectly happy.
    /// </para>
    /// <para>
    /// Zero is the half worth insisting on. It means 「stop keeping」, and a window still locked to the
    /// shape of a film that finished half an hour ago cannot be dragged into any other shape at all.
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) ProbeAspect()
    {
        if (!Attached || _window is null) return (false, "播放层未接线");

        var restore = _window.PictureAspect;

        // Through the page's own handler rather than by writing the property: the wiring is the subject.
        OnPictureAspectChanged(16d / 9d);
        var took = Math.Abs(_window.PictureAspect - 16d / 9d) < 0.001;

        string fitted;
        var shaped = true;

        if (_window.Fullscreen || _window.IsMaximized)
        {
            // Both mean the edges belong to the monitor and FitToPicture stands aside, so asking about the
            // client shape here would be asking about the screen's.
            fitted = $"{(_window.Fullscreen ? "全屏" : "已最大化")}，不整形";
        }
        else
        {
            var size = _window.ClientSize;
            var wanted = (int)Math.Round(size.Width / (16d / 9d));
            var ratio = size.Height > 0 ? (double)size.Width / size.Height : 0;

            // One pixel, not a loose tolerance. Every clamp inside AspectLock.Fit preserves the ratio —
            // the minimum height widens rather than shortens, and a work-area cut recomputes the other
            // dimension — so a client that is not 16:9 to the pixel means the shape was got wrong, which
            // is exactly the black band this fit exists to remove.
            shaped = Math.Abs(size.Height - wanted) <= 1;
            fitted = $"客户区 {size.Width}×{size.Height} = {ratio:0.000}"
                + (shaped ? string.Empty : $"，应为 {size.Width}×{wanted}");
        }

        OnPictureAspectChanged(0);
        var cleared = _window.PictureAspect == 0;

        _window.PictureAspect = restore;

        return (took && shaped && cleared,
            $"16:9 {(took ? "已交给窗口" : "没有传到窗口")}；{fitted}；归零{(cleared ? "已解除" : "未解除")}");
    }

    // ---- 几何 ---------------------------------------------------------------------
    //
    // The three questions the two geometry probes ask of the live tree, in Root's own logical coordinates.
    // Half a pixel of slack throughout: layout rounding puts a 46-wide button at 45.9996 often enough that
    // an exact comparison would report a button as having escaped the strip that clips it.

    private const double GeometrySlack = 0.5;

    private Rect BoundsOf(FrameworkElement element)
    {
        var origin = element.TransformToVisual(Root).TransformPoint(new Point(0, 0));
        return new Rect(origin.X, origin.Y, element.ActualWidth, element.ActualHeight);
    }

    private static bool Overlaps(Rect a, Rect b) =>
        a.Width > 0 && a.Height > 0 && b.Width > 0 && b.Height > 0
        && a.Left < b.Right - GeometrySlack && b.Left < a.Right - GeometrySlack
        && a.Top < b.Bottom - GeometrySlack && b.Top < a.Bottom - GeometrySlack;

    private static bool Encloses(Rect outer, Rect inner) =>
        inner.Left >= outer.Left - GeometrySlack && inner.Right <= outer.Right + GeometrySlack
        && inner.Top >= outer.Top - GeometrySlack && inner.Bottom <= outer.Bottom + GeometrySlack;
}

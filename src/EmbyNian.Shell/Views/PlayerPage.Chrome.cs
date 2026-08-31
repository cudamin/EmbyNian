using EmbyNian.Diagnostics;
using EmbyNian.Playback;
using EmbyNian.Shell.Interop;
using EmbyNian.Shell.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace EmbyNian.Shell.Views;

/// <summary>
/// The player's chrome half: what the reveal rule's answer looks like on screen, the hit tests that
/// answer is computed from, and the seek bar's two decorations — the chapter ticks and the hover
/// preview.
/// <para>
/// The rule itself is <see cref="ChromeReveal"/>'s, in Core and under test; this file is the half that
/// cannot be tested there because it is <c>Visibility</c>, <c>TransformToVisual</c> and a Win32 cursor
/// counter. Nothing here decides when the chrome should be up — it asks, and draws the answer.
/// </para>
/// </summary>
public sealed partial class PlayerPage
{
    // ---- where the pointer is ---------------------------------------------------

    /// <summary>
    /// Where the pointer is on the desktop, asked of the OS once per tick and shared for the rest of it.
    /// <para>
    /// Four things in one tick want to know — 「has it moved since last time」, 「is it still inside the
    /// window」, a held button standing in for the events a motionless drag never sends, and the guard that
    /// takes a stranded chapter preview away — and each used to ask user32 for itself. The cost is the
    /// smaller half of why that is wrong: separate readings make one tick incoherent, because the pointer
    /// can move between two of them. The reseed can place it on a control and the departure check can then
    /// find it outside the window, and the chrome goes away under a hand that is on it. One reading makes
    /// the tick's answers agree with each other.
    /// </para>
    /// <para>
    /// Shared only for the length of the tick that took it. A pointer event or a probe asking in between
    /// reads the OS as it always did: a position from up to a tenth of a second ago is precisely the wrong
    /// answer to 「did the pointer really leave the track」.
    /// </para>
    /// </summary>
    private bool CursorScreen(out NativePoint screen)
    {
        if (_cursorShared)
        {
            screen = _cursorAt;
            return _cursorAtKnown;
        }

        return Native.GetCursorPos(out screen);
    }

    /// <summary>
    /// Whether the cursor is over our own client area, according to the OS. Falls back to 「inside」
    /// whenever it cannot tell, because the cost of a wrong 「outside」 is chrome vanishing under a hand
    /// that is still using it, and the cost of a wrong 「inside」 is one more 100 ms tick before it goes.
    /// </summary>
    private bool PointerInside()
    {
        var handle = _window?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return true;

        if (!CursorScreen(out var cursor)) return true;
        if (!Native.ScreenToClient(handle, ref cursor)) return true;
        if (!Native.GetClientRect(handle, out var client)) return true;

        return cursor.X >= client.Left && cursor.X < client.Right
            && cursor.Y >= client.Top && cursor.Y < client.Bottom;
    }

    /// <summary>
    /// Where the cursor is in the picture's own coordinates, or false when the OS will not say. The
    /// pointer arrives from Win32 in physical pixels and every layout question here is in logical ones,
    /// which is the whole of the conversion: the XAML island covers the client area, so the client origin
    /// and <c>Root</c>'s origin are the same point.
    /// </summary>
    private bool CursorPoint(out Point point)
    {
        point = default;

        var handle = _window?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return false;

        if (!CursorScreen(out var cursor)) return false;
        if (!Native.ScreenToClient(handle, ref cursor)) return false;

        var scale = XamlRoot?.RasterizationScale ?? 1;
        if (scale <= 0) scale = 1;

        point = new Point(cursor.X / scale, cursor.Y / scale);
        return true;
    }

    /// <summary>
    /// Asks the OS where the pointer is and tells the reveal rule, as though a pointer event had said so.
    /// <para>
    /// For the moments when no event will. A fullscreen or maximize transition moves every control out
    /// from under a pointer that never moved, so the recorded position — a fraction of a client height that
    /// no longer exists, and a hit test against a bar that has since moved to the bottom of the screen — is
    /// answering a question about a window that is gone. And a drag holds still as often as it moves, while
    /// the chrome's patience for a still pointer now has a limit; a slider that vanished mid-drag would
    /// take the pointer capture with it and drop the thumb.
    /// </para>
    /// </summary>
    /// <returns>
    /// True when the reveal rule was actually told something. False means the position could not be turned
    /// into a point on the picture — no size yet, no reading — and the rule still believes whatever it did
    /// before, which matters to <see cref="PollPointer"/> and to nobody else.
    /// </returns>
    private bool ReseedPointer()
    {
        // Nothing better to say than what is already recorded: a failed read is not a departure.
        if (!CursorPoint(out var point)) return false;

        if (Root.ActualWidth <= 0 || Root.ActualHeight <= 0) return false;

        if (point.X < 0 || point.Y < 0 || point.X >= Root.ActualWidth || point.Y >= Root.ActualHeight)
        {
            if (_chrome.PointerLeft(Now)) Render();
            return true;
        }

        // Recorded as well as reported: this is the same knowledge in the same coordinates the pointer events
        // are filtered against, and an anchor left behind by a fullscreen transition would make the next
        // event look like a move whether or not the hand had moved.
        var part = PartAt(point);
        _pointerAt = point;
        _pointerOn = part;

        if (_chrome.Pointer(point.Y, Root.ActualHeight, part, RailNear(point), Now)) Render();
        return true;
    }

    /// <summary>
    /// Asks the OS whether the mouse has moved since the last tick, and tells the reveal rule when it has.
    /// <para>
    /// This is where 「鼠标静止不动两秒」 is actually decided, because it is the only place that can be. The
    /// events cannot decide it on their own for two reasons, and they pull opposite ways: WinUI raises
    /// <c>PointerMoved</c> for a pointer that never moved — once per change to the tree under it, and the
    /// chrome collapsing 650 ms into a stillness is such a change, invisible in the middle of the picture
    /// where it reveals nothing and restarts the cursor's two seconds — and WinUI also raises nothing at all
    /// for input it cannot deliver, which a probe on this thread demonstrates every run. A position read out
    /// of the OS has neither problem: changed is movement, unchanged is stillness, and 「unchanged」 is
    /// answered by saying nothing, which is what lets the idle clock run out.
    /// </para>
    /// <para>
    /// The threshold is the same one the events are filtered by and applies for the same reason — a mouse
    /// resting on a desk rattles a pixel — and it is dropped once the cursor is hidden, where any change at
    /// all is a hand reaching for the mouse and wanting to see where it is.
    /// </para>
    /// </summary>
    private void PollPointer()
    {
        if (!CursorScreen(out var screen)) return;

        var dx = _polledKnown ? Math.Abs(screen.X - _polled.X) : int.MaxValue;
        var dy = _polledKnown ? Math.Abs(screen.Y - _polled.Y) : int.MaxValue;

        if (dx == 0 && dy == 0) return;

        // Not advancing the anchor is the point: jitter around one spot never accumulates into activity,
        // while a hand that really is moving the mouse crosses two pixels within a tick or two.
        if (_polledKnown && !_cursorHidden && dx < PointerNoise && dy < PointerNoise) return;

        _polled = screen;
        _polledKnown = true;
        _polledMoves++;
        _pointerMovedAt = Now;

        // Told either way. The reseed is the better answer when it works — it carries where the pointer is,
        // not just that it moved — and when it cannot translate the position, the movement is still news:
        // both clocks have to start from the same instant or 「静止两秒」 is measured from a stamp the rule
        // never got.
        if (!ReseedPointer() && _chrome.Moved(Now)) Render();
    }

    /// <summary>
    /// The picture changed size — 全屏, 最大化, a dragged window edge — and the pointer said nothing about
    /// it. 「全屏时最下方的进度条不会自动隐藏」 was this: entering fullscreen from the bar's own ⛶ leaves the
    /// pointer recorded as resting on a control, and in fullscreen there is no longer anywhere for it to
    /// leave to.
    /// </summary>
    private void OnRootResized(object sender, SizeChangedEventArgs e)
    {
        if (Attached) ReseedPointer();
    }

    /// <summary>
    /// Whether a tap at <paramref name="point"/> landed on the picture rather than on something drawn
    /// over it. Everything hidden answers 「picture」, which is the common case: with the chrome down the
    /// whole window is the film.
    /// <para>
    /// The 统计 panel and the 「正在切换…」 cover are asked about separately from
    /// <see cref="PartAt(Point)"/> because they are not parts of the reveal rule — the panel is pinned
    /// open by a button and the cover belongs to the handover — but a click on either is still not a
    /// click on the film.
    /// </para>
    /// </summary>
    private bool TapOnPicture(Point point) =>
        PartAt(point) == ChromePart.None && !Covers(StatsPanel, point) && !Covers(Cover, point);

    /// <summary>
    /// Which piece of chrome a point is on. Asked separately from <see cref="RailNear"/> on purpose:
    /// a hit test has to pick a single winner, and the skip button and the top strip both overlap the
    /// right edge, which is how 「音量条判定有问题，我鼠标移到窗口右边有时候不会显示」 happened.
    /// </summary>
    private ChromePart PartAt(Point point)
    {
        if (Covers(Bar, point)) return ChromePart.Bar;
        if (Covers(TitleStrip, point)) return ChromePart.Title;
        if (Covers(Rail, point)) return ChromePart.Volume;
        if (Covers(SkipButton, point)) return ChromePart.Skip;
        return ChromePart.None;
    }

    /// <summary>
    /// How deep into the rail's approach strip along the right edge a point is: -1 outside it, 0 at its
    /// inner boundary, 1 hard against the edge. The rule turns that into both 「show the rail」 and
    /// 「how strongly」 — 「鼠标指针越接近右边的中心显示越明显」 — and the strip's width is the whole of the
    /// horizontal half of it, so the value is a plain fraction of <see cref="RailZoneWidth"/>.
    /// </summary>
    private double RailNear(Point point)
    {
        if (Root.ActualWidth <= 0) return -1;

        var edge = Root.ActualWidth - RailZoneWidth;
        if (point.X < edge) return -1;

        return Math.Clamp((point.X - edge) / RailZoneWidth, 0, 1);
    }

    /// <summary>
    /// Whether <paramref name="element"/> is drawn under <paramref name="point"/>. Faded out counts as not
    /// there: the rail is the one overlay that arrives by degrees rather than by a <c>Visibility</c> flip
    /// — 「显示方式改为淡入淡出」 — so it stays in the tree at zero opacity, and a rail nobody can see must
    /// not swallow a click meant for the picture behind it. <c>IsHitTestVisible</c> is what the fade turns
    /// off, and it is the same question this method is being asked.
    /// </summary>
    private bool Covers(FrameworkElement element, Point point)
    {
        if (element.Visibility != Visibility.Visible || !element.IsHitTestVisible
            || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            return false;

        var origin = OriginIn(element);
        return point.X >= origin.X && point.X <= origin.X + element.ActualWidth
            && point.Y >= origin.Y && point.Y <= origin.Y + element.ActualHeight;
    }

    /// <summary>
    /// Where <paramref name="element"/>'s top-left corner sits in <c>Root</c>'s own coordinates, summed out
    /// of what Arrange already worked out rather than asked for as a transform.
    /// <para>
    /// <c>TransformToVisual</c> answers the same question and builds a <c>GeneralTransform</c> across the
    /// WinRT boundary to do it. That is nothing once — but <see cref="PartAt"/> asks it of up to four
    /// elements per pointer move as well as per tick, and a hand crossing the picture raises hundreds of
    /// moves a second. <c>ActualOffset</c> is a struct read of a value that is already computed, so this
    /// walk allocates nothing whatever.
    /// </para>
    /// <para>
    /// The two answers agree for as long as nothing between the element and <c>Root</c> is scaled or
    /// render-transformed. That holds for everything asked about here — this page's only two
    /// <c>TranslateTransform</c>s are on the 暂停 badge and on the preview box, and neither is ever the
    /// subject of a hit test — and the self-check compares the two answers element by element, so it is a
    /// measurement rather than a promise.
    /// </para>
    /// </summary>
    private Point OriginIn(FrameworkElement element)
    {
        var x = 0d;
        var y = 0d;

        DependencyObject? node = element;

        while (node is UIElement step && !ReferenceEquals(node, Root))
        {
            var offset = step.ActualOffset;
            x += offset.X;
            y += offset.Y;
            node = VisualTreeHelper.GetParent(node);
        }

        return new Point(x, y);
    }

    // ---- what that comes to on screen -------------------------------------------

    /// <summary>
    /// The reveal rule's whole visible effect: two <c>Visibility</c> flips, the rail's fade, and the
    /// cursor. Flips for the bar and the strip because requirement 9 was
    /// 「不要淡入淡出了，鼠标移动到对应位置直接显示」; the rail is the one exception the user later asked for,
    /// see <see cref="FadeRail"/>.
    /// </summary>
    private void Render()
    {
        var state = _chrome.State;
        Bar.Visibility = state.Bar ? Visibility.Visible : Visibility.Collapsed;
        TitleStrip.Visibility = state.Title ? Visibility.Visible : Visibility.Collapsed;
        FadeRail(state.Rail ? _chrome.RailStrength : 0);

        // The three window commands live in the strip, so they come and go with it. What is left to decide
        // per reveal is which of 最大化/还原 the middle one is offering, and whether it is offering anything.
        if (state.Title) UpdateMaximizeGlyph();

        // 视频进度条预览不会自动消失: the preview belongs to the bar it hovers, and the bar can go without
        // the pointer ever leaving the track — it goes when the pointer leaves the window entirely, which
        // raises no Exited over the track at all. A preview outliving its bar is a thumbnail stranded over
        // the picture with nothing to explain it.
        if (!state.Bar) HideChapterPeek();

        // 「浮层收起时底边留一条细进度线」 — windowed, where it is a readout along the edge of a window that
        // already has edges. Not full screen: 全屏时最下方会有进度条 is those same two pixels drawn bright
        // across the whole bottom of the monitor with nothing to frame them, and the point of full screen is
        // that nothing but the film is on screen. Only while the player is up either way, or it would draw a
        // line across the bottom of the library grid the moment the page went away.
        ThinLine.Visibility = !state.Bar && Visibility == Visibility.Visible && _window?.Fullscreen != true
            ? Visibility.Visible
            : Visibility.Collapsed;

        SetCursorHidden(_chrome.CursorHidden);
    }

    /// <summary>
    /// Takes the volume rail to <paramref name="strength"/>, fading rather than flipping —
    /// 「显示方式改为淡入淡出，鼠标指针越接近右边的中心显示越明显」.
    /// <para>
    /// An <c>OpacityTransition</c> declared on the rail does the animating, which is why this is an
    /// assignment and not a storyboard: the transition is an implicit one, so the property holds the value
    /// asked for the instant it is asked while the compositor takes the pixels there over its own duration.
    /// That matters beyond brevity — everything that reads the rail back, from the tap test to the
    /// self-check, then reads what the rule decided rather than whichever frame of an animation it caught.
    /// A storyboard would have inverted that: the value would lag the decision, and 「is the rail up」 would
    /// have become a question about timing.
    /// </para>
    /// <para>
    /// The rail therefore stays in the tree at zero opacity instead of collapsing, since a collapse would
    /// cut the fade off at its first frame. What has to be turned off with it is hit testing: an invisible
    /// rail that still answered <see cref="Covers"/> would swallow clicks on the picture behind it and stop
    /// 「点击画面暂停」 working along the right edge. Layout is unaffected either way — the rail is aligned
    /// to the right of a grid cell it shares with nothing.
    /// </para>
    /// </summary>
    private void FadeRail(double strength)
    {
        var wanted = Math.Clamp(strength, 0, 1);
        if (Math.Abs(Rail.Opacity - wanted) < 0.001) return;

        Rail.Opacity = wanted;
        Rail.IsHitTestVisible = wanted > 0;
    }

    /// <summary>
    /// Hides or shows the mouse cursor. There is no WinUI way to do this —
    /// <c>UIElement.ProtectedCursor</c> can name a shape but has no way to say 「none」 — so it is the window's
    /// job: <see cref="HostWindow.CursorHidden"/> says <c>SetCursor(NULL)</c> now and keeps saying it while
    /// this is on. Guarded because the counter below is a counter rather than a flag.
    /// <para>
    /// The counter alone used to be the whole of it, and it is why 「鼠标指针还是不会自动隐藏」 survived a fix
    /// to the rule that decides <em>when</em>: <c>ShowCursor</c> reaches user32 and has no effect whatever on
    /// a WinUI 3 island. It is kept because it is still the right thing to say to the OS about surfaces the
    /// island does not cover, and because the count it returns is one more thing a probe can read.
    /// </para>
    /// </summary>
    private void SetCursorHidden(bool hidden)
    {
        if (_cursorHidden == hidden) return;

        _cursorHidden = hidden;

        if (_window is not null) _window.CursorHidden = hidden;

        // And the same thing said to libmpv about its own window, which no call of ours can reach: see
        // PlayerViewModel.ShowMpvCursor.
        ViewModel.ShowMpvCursor(!hidden);

        // Kept because it is the OS's own answer about the one thing here that leaves the process: the new
        // display count, which has to be below zero for a hidden cursor and back at zero for a shown one.
        // Read by the self-check, which otherwise could only ask this file what it believes.
        _cursorCount = Native.ShowCursor(!hidden);

        if (hidden) _shapeBack = 0;

        // And now make the OS ask. Every 「no cursor」 above is an answer — the queue's shape, the class
        // cursors, mpv's own setting — and the OS only collects those answers when it has a reason to work out
        // what the pointer is over, which is when the pointer moves. Hiding happens *because* nothing is
        // moving, so without this the last shape it worked out stays on the screen until the hand comes back,
        // which is exactly the report: 「静止超过两秒后鼠标指针还是不会自动隐藏」, from a player whose own
        // readings all say hidden. A mouse event with a displacement of zero is the smallest possible reason:
        // the pointer does not move, and both places that judge movement — the event filter in
        // PlayerPage.Input and PollPointer above — discard a zero displacement, so the stillness this is part
        // of survives being nudged.
        // Counted, because this is the load-bearing half and the OS's own answer about it cannot be trusted to
        // arrive: GetCursorInfo reports the desktop's cursor, and on a machine where the last real mouse
        // movement happened over another app's window it goes on reporting that window's arrow however many
        // times we ask. The self-check therefore asserts that the ask was made — which is the thing this file
        // is responsible for — and only prints what the desktop says about it.
        if (hidden && Native.NudgeCursorState()) _cursorNudges++;

        // Written to the log because this is the one thing in the player a probe can only ask about under
        // conditions it made up, and 「没有变化」 three times over is what asking the wrong conditions costs.
        // Two transitions a film, so the cost is nothing.
        // <para>
        // The hide line carries who owns the pixels, because the last round proved the rule right and the
        // screen wrong: it fired on time, this thread's queue went to 「no shape」, and the arrow stayed. A
        // shape set here only reaches the screen while the pointer is over a window this thread owns, and
        // during playback the window under the pointer may be the island's or libmpv's rather than ours —
        // which is a fact about a real film, unavailable to any probe. The show line carries the count of
        // ticks that found a shape back while we still wanted none, which is the other way this can fail.
        // </para>
        Log.Debug(Category, hidden
            ? $"鼠标藏起来了：静止 {Now - _pointerMovedAt}ms，其间空事件 {_stillMoves} 次，线程形状"
              + $"{(_window?.CursorShapeGone == true ? "无" : "还在")}，计数 {_cursorCount}，{PointerOwner()}"
            : $"鼠标又显示了：轮询问出的移动共 {_polledMoves} 次、XAML 事件 {_pointerMoves} 次，计数 {_cursorCount}"
              + $"，藏着期间有 {_shapeBack} 拍发现形状又被放回来了");
    }

    /// <summary>
    /// Who owns the cursor at this instant, in the OS's words: the class of the window under the pointer,
    /// whether its message queue is this thread's, and what the OS says is on screen. Three facts, because
    /// hiding a cursor is per queue and the window under a playing film is not always one of ours.
    /// </summary>
    private static string PointerOwner()
    {
        if (!Native.GetCursorPos(out var at)) return "问不出指针位置";

        var under = Native.WindowFromPoint(at);
        var owner = Native.GetWindowThreadProcessId(under, out _);
        var mine = owner == Native.GetCurrentThreadId();
        var says = Native.CursorSnapshot() is { } cursor ? $"[标志 0x{cursor.Flags:X2}，形状 0x{cursor.Shape:X}]" : "问不出";

        return $"指针上的窗口={ClassOf(under)}（{(mine ? "本线程" : $"线程 {owner}，不是本线程")}），系统 {says}";
    }

    /// <summary>
    /// Why the chrome is being kept on screen regardless of the pointer. Four independent things ask for
    /// it and they overlap: a flyout can be opened and closed while the 字幕字体 box still has the keyboard,
    /// and a dialog can be raised from a flyout.
    /// </summary>
    [Flags]
    private enum ChromeHold
    {
        None = 0,

        /// <summary>One of the six menus is open — the pointer is in it, not resting on the picture.</summary>
        Menu = 1,

        /// <summary>The window is being dragged by the title strip, which reports nothing while the hand holds still.</summary>
        Drag = 2,

        /// <summary>播放信息 is up.</summary>
        Dialog = 4,

        /// <summary>需求 7's 字幕字体 box has the keyboard: a strip that hid itself while being typed into.</summary>
        Search = 8
    }

    /// <summary>
    /// A flyout is open, a drag is running, a dialog is up, or the 字幕字体 box has the keyboard: the chrome
    /// stays regardless of the pointer.
    /// <para>
    /// Reason-flagged rather than a plain boolean, because <see cref="ChromeReveal.SetHold"/> is one flag
    /// for the whole page and the reasons genuinely overlap: opening a menu while the search box has focus
    /// and then closing it would otherwise release a hold the box still needs, and the strip would slide
    /// away from under the caret. The rule in Core keeps a single flag on purpose — 「something is holding
    /// it」 is all a reveal rule can act on — so the bookkeeping belongs here, where the reasons are.
    /// </para>
    /// </summary>
    private void Hold(bool held, ChromeHold reason)
    {
        var before = _holds;
        _holds = held ? _holds | reason : _holds & ~reason;

        // Not a shortcut for its own sake: SetHold restamps the idle clock when a hold is released, and
        // releasing one that was never taken restarts the countdown from here — which leaves the chrome
        // standing over the picture for another window's worth of it.
        if (_holds == before) return;

        if (_chrome.SetHold(_holds != ChromeHold.None, Now)) Render();
    }

    /// <summary>
    /// Ten hertz, and the three things that expire rather than happen. Two of them are here because a
    /// pointer can leave without saying so; the third is the view model's, and is simply handed the tick.
    /// <para>
    /// The tick opens by asking the OS where the cursor is, once, and closes by giving that answer back. Up
    /// to four of the steps below want the position and every one of them used to ask for itself — see
    /// <see cref="CursorScreen"/> for why that made a single tick able to disagree with itself.
    /// </para>
    /// </summary>
    private void OnTick(object? sender, object e)
    {
        if (!Attached) return;

        _tickCount++;

        // Eagerly, not on first use: the sharing has to cover every step below equally, and a lazy read
        // would put the reading inside whichever of them happened to run first — which on a tick where the
        // pointer left the window is a different step from the tick before.
        _cursorAtKnown = Native.GetCursorPos(out _cursorAt);
        _cursorShared = true;

        // A drag in progress, ten times a second, whatever the pointer events are doing. They are the fast
        // path and this is the guarantee: the window is moving with the cursor, so the cursor is not moving
        // relative to the window, and there is no promise that a pointer event arrives for a move that
        // changes nothing about where the pointer sits inside the client area. This is also where a drag
        // whose release happened somewhere we never saw gets ended.
        if (_window?.Dragging == true) DragWindow();

        // 「移动了没有」, asked of the OS. Not while a drag is running: the window is moving with the cursor,
        // so the cursor is not moving relative to anything the rule cares about, and the line above has
        // already dealt with it.
        else PollPointer();

        // The departure the events can miss entirely. PointerExited is not guaranteed: alt-tab, another
        // window opening over ours, a cursor warped away by something else — in every one of those the
        // pointer's last known position is still parked on a control, and 「parked」 is precisely the
        // state the idle countdown does not apply to, so the chrome would stay up until the pointer came
        // back. Ten hertz makes the stuck state impossible rather than merely unlikely.
        if (!PointerInside() && _chrome.PointerLeft(Now)) Render();

        // A held mouse button with chrome on screen is a drag on one of the two sliders — or at least may
        // be — and a drag reports nothing at all while the hand holds still. The reveal rule's patience for
        // a still pointer is finite now, and a slider collapsing under a held thumb would lose the pointer
        // capture with it, so the button's own state stands in for the events that are not coming.
        if (Native.MouseButtonDown() && _chrome.State.Any) ReseedPointer();

        if (_chrome.Tick(Now)) Render();

        // Said again while it holds, because saying it once is only enough if nothing puts a shape back: see
        // HostWindow.KeepCursorHidden. Guarded on our own flag so a shown cursor costs nothing.
        // <para>
        // Counted, too. 「Something puts a shape back」 is a supposition every fix here has rested on and
        // nothing has ever measured: if these ticks keep finding a shape on the queue, the arrow the user
        // sees is being re-set ten times a second and hiding it once was never going to be enough. The count
        // goes out with the show line, so an ordinary film answers it.
        // </para>
        if (_cursorHidden)
        {
            if (_window?.CursorShapeGone == false) _shapeBack++;
            _window?.KeepCursorHidden();
        }

        // The same guarantee for the chapter preview, and for the same reason: it hides on the pointer
        // leaving the seek track, and PointerExited is not raised when the pointer leaves the window from
        // over it — alt-tab, a screenshot tool taking the foreground, a cursor warped elsewhere. Asking
        // the OS ten times a second where the cursor actually is makes a stranded preview impossible
        // rather than merely unlikely, which is what 视频进度条预览不会自动消失 turned out to be.
        //
        // Not while the thumb is being dragged: a drag captures the pointer, so it keeps arriving here
        // from wherever the hand has wandered to, and the preview is exactly what that hand is reading.
        if (!ViewModel.Scrubbing && ChapterPeek.Visibility == Visibility.Visible && !PointerOverSeekTrack())
            HideChapterPeek();

        // The coalesced seek, the 统计 refresh and the 跳过 countdown: all three are about what is
        // playing rather than about what is on screen, so all three are one call.
        ViewModel.Tick();

        // And the shared reading expires with the tick that took it. Not in a finally: this page's tick
        // cannot swallow an exception — App leaves Handled false on purpose, so a throw here takes the
        // process with it — and there is nothing to put back afterwards.
        _cursorShared = false;
    }

    // ---- 章节刻度与缩略图 ---------------------------------------------------------

    private void OnSeekTrackResized(object sender, SizeChangedEventArgs e) => RenderChapterTicks();

    /// <summary>
    /// Draws one tick per chapter boundary onto the canvas behind the slider, against the marks and the
    /// run time the view model currently holds. Nothing to play means nothing to mark, which is what
    /// clears the canvas on the way out of the player.
    /// </summary>
    private void RenderChapterTicks()
    {
        if (!Attached)
        {
            ChapterTicks.Children.Clear();
            return;
        }

        var duration = ViewModel.Status.HasDuration ? ViewModel.Status.Duration : 0;
        _ticksFor = duration;
        RenderChapterTicks(ViewModel.ChapterMarks, SeekTrack.ActualWidth, duration);
    }

    /// <summary>
    /// The same drawing against marks, a width and a run time given rather than read, which is the only
    /// way the self-check can look at it: nothing is playing and the player is collapsed, so the track has
    /// no measured width and the ordinary path would quite correctly draw nothing.
    /// <para>
    /// Borders rather than <c>Shapes.Rectangle</c>: a rectangle needs a <c>Fill</c> brush and would drag
    /// <c>Microsoft.UI.Xaml.Shapes</c> in for something a one-pixel-wide bordered box already does.
    /// </para>
    /// <para>
    /// The ticks already on the canvas are moved rather than thrown away and made again. Which matters for
    /// one caller in particular: the track's <c>SizeChanged</c> redraws them, and a dragged window edge
    /// raises that once a frame — so a file with chapters used to discard and rebuild its whole tick row
    /// sixty times a second for as long as the hand held the edge. Only the tail is really added or removed,
    /// and only when the number of boundaries changes, which is when the file does.
    /// </para>
    /// </summary>
    private void RenderChapterTicks(IReadOnlyList<SkipChapter> marks, double width, double duration)
    {
        var drawn = 0;

        // Two marks is the minimum that says anything: a single chapter at zero is every file.
        if (width > 0 && duration > 0 && marks.Count >= 2)
        {
            // Asked once rather than per tick: the brush is the same object for every mark, and it is a
            // dictionary walk to find.
            var brush = BrushFor("ChapterTickBrush");

            foreach (var mark in marks)
            {
                // The mark at zero is the start of the file, not a boundary anyone would want to see.
                if (mark.Start <= 0.5 || mark.Start >= duration) continue;

                Border tick;

                if (drawn < ChapterTicks.Children.Count)
                {
                    tick = (Border)ChapterTicks.Children[drawn];
                }
                else
                {
                    tick = new Border { Width = ChapterTickWidth, CornerRadius = new CornerRadius(1) };
                    ChapterTicks.Children.Add(tick);
                }

                // Assigned every time, not only at creation: the canvas's height is fixed but the brush is
                // the theme's, and a theme can change under a file that is already playing.
                tick.Height = ChapterTicks.Height;
                tick.Background = brush;

                Canvas.SetLeft(tick, Math.Clamp(mark.Start / duration * width - ChapterTickWidth / 2, 0, width - ChapterTickWidth));
                drawn++;
            }
        }

        // Whatever the last file left behind: fewer boundaries than this one has, or — leaving the player —
        // none at all, which is what has to clear the canvas rather than leave ticks over the library grid.
        while (ChapterTicks.Children.Count > drawn) ChapterTicks.Children.RemoveAt(ChapterTicks.Children.Count - 1);
    }

    /// <summary>
    /// The hover preview: where the box goes, and whether there is anything to put in it. What goes in it
    /// — the chapter's name and its still — is <see cref="PlayerViewModel.PeekChapterAt"/>'s, and arrives
    /// through the two bindings on the box itself.
    /// <para>
    /// The time bubble beside it is the slider's own tooltip, which the seek clock converter already
    /// fills — this adds the picture and the chapter's name, and nothing else, so hovering a file with no
    /// chapter stills still behaves exactly as it did before.
    /// </para>
    /// </summary>
    private void OnSeekTrackHover(object sender, PointerRoutedEventArgs e)
    {
        if (!Attached || !ViewModel.CanPeek) return;

        var width = SeekTrack.ActualWidth;
        if (width <= 0) return;

        var x = Math.Clamp(e.GetCurrentPoint(SeekTrack).Position.X, 0, width);

        if (!ViewModel.PeekChapterAt(x / width * ViewModel.Status.Duration))
        {
            HideChapterPeek();
            return;
        }

        // Positioned every move, contents only when the chapter changes — which is the view model's own
        // rule, above. The box follows the pointer along the bar for the price of a transform.
        PositionChapterPeek(x);
        ChapterPeek.Visibility = Visibility.Visible;
    }

    private void OnSeekTrackLeft(object sender, PointerRoutedEventArgs e)
    {
        // Same problem PointerExited has on Root: the slider is a child of the track, so stepping onto
        // it raises Exited here at a position still inside the track. The OS knows where the cursor is.
        if (PointerOverSeekTrack()) return;

        HideChapterPeek();
    }

    private bool PointerOverSeekTrack()
    {
        if (SeekTrack.ActualWidth <= 0 || !CursorPoint(out var point)) return false;

        var origin = OriginIn(SeekTrack);

        // A generous vertical band, because the track is a dozen pixels tall and the preview should not
        // flicker off at its edge.
        return point.X >= origin.X && point.X <= origin.X + SeekTrack.ActualWidth
            && point.Y >= origin.Y - 8 && point.Y <= origin.Y + SeekTrack.ActualHeight + 8;
    }

    /// <summary>
    /// Puts the preview above the pointer, kept inside the picture. Translated rather than laid out with
    /// margins, so following the pointer costs a transform rather than a layout pass per mouse move.
    /// </summary>
    private void PositionChapterPeek(double trackX)
    {
        var origin = OriginIn(SeekTrack);
        var boxWidth = ChapterPeek.ActualWidth > 0 ? ChapterPeek.ActualWidth : PlayerViewModel.ChapterPeekWidth + 10;
        var boxHeight = ChapterPeek.ActualHeight > 0 ? ChapterPeek.ActualHeight : 160;

        var left = origin.X + trackX - boxWidth / 2;
        if (Root.ActualWidth > 0) left = Math.Clamp(left, 8, Math.Max(8, Root.ActualWidth - boxWidth - 8));

        ChapterPeekOffset.X = left;
        ChapterPeekOffset.Y = Math.Max(8, origin.Y - boxHeight - 12);
    }

    private void HideChapterPeek()
    {
        ChapterPeek.Visibility = Visibility.Collapsed;
        if (Attached) ViewModel.ClearChapterPeek();
    }
}

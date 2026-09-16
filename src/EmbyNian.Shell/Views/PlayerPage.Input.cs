using EmbyNian.Playback;
using EmbyNian.Shell.Interop;
using EmbyNian.Shell.ViewModels;
using EmbyNian.Shell.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;

namespace EmbyNian.Shell.Views;

/// <summary>
/// The player's input half: the pointer, the two overlapping tap gestures, the keys, and the four
/// window commands the title strip carries.
/// <para>
/// Every handler here ends in one of two places — <see cref="ChromeReveal"/>, which decides what is on
/// screen, or one call on <see cref="PlayerViewModel"/>, which decides what mpv is told. Nothing in
/// this file knows what is playing: a gesture is a gesture until the view model gives it a meaning.
/// </para>
/// </summary>
public sealed partial class PlayerPage : IWin32KeySink
{
    /// <summary>How far one wheel notch moves 音量. mpv's own step, and the reason is in <see cref="OnPointerWheel"/>.</summary>
    private const int WheelStep = 2;

    /// <summary>How far one press of ↑/↓ moves 音量 — coarser than the wheel on purpose.</summary>
    private const int KeyStep = 5;

    // ---- the pointer ------------------------------------------------------------

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        // A drag in progress is the only thing the pointer can be saying: the strip is under the cursor
        // and staying there, which is the one case where the reveal rule has nothing to decide.
        if (_window?.Dragging == true)
        {
            DragWindow();
            return;
        }

        var point = e.GetCurrentPoint(Root).Position;
        var part = PartAt(point);

        // 单传感器（2026-09-15 重构）：这个事件不再参与「光标该不该藏」的判断——那条判断整个交给了
        // PollPointer 对 GetCursorPos 的读数。原因是 WinUI 会为**没动过的指针**抬这个事件（树在它底下变
        // 一次抬一次，chrome 650ms 收起就是一次），而事件的坐标来自框架重推的基准，真手与合成事件在坐标上
        // 无法可靠区分。它在这里只剩一件事：控件浮层的显隐（鼠标进了上/下边缘带就该亮出来），那是位置问题，
        // 不是「有没有动」的问题，事件带的位置正合适。
        if (!Moved(point, part)) return;

        // 第十一报续（2026-09-15，重构后的最后一个洞）：藏匿期这条路**不算移动**。
        //
        // 单传感器重构把「该不该藏」整个交给了 PollPointer 对 GetCursorPos 的读数，但这一条留了下来，
        // 它照样调 _chrome.Pointer(moved: true) —— 而那一下会重盖 _lastActivity（ChromeReveal:500），
        // 于是 Settle 的 hide 变回 false、CursorHidden 翻成显、Render 打出显示行。也就是说：这条路
        // 仍然能**独自**结束一次藏匿。这正是第八报定罪过的东西——WinUI 会为**没动过的指针**抬这个
        // 事件（树在它底下变一次就抬一次），而外屏 AyuGram 的活动恰恰会让我们的树/焦点变一下。
        //
        // 更糟的是它不挂名（用户第三次报「还是一样的毛病」那次的显示行就是「未标注的显示路径」＋
        // 移动=17＋挡下=0），也不经过藏匿期那道关——绕过了第十一报以来的整条防线。
        //
        // 修法是位置与移动照旧分开：藏匿期仍把位置交给规则（chrome 的命中判定要它），但不是以
        // 「移动」的名义——「位置是位置、动是动」。要真唤醒就走 PollPointer 那条路，那里有裁决、有
        // 名字。第十一报的判据「藏匿期被搬一次不醒」与「真手连着走三拍要能叫醒」都仍然成立：前者由
        // 净位移攒不够挡住，后者由手连着三拍走够 WakeTravelPixels 而唤醒并挂牌。
        if (_cursorHidden)
        {
            ReseedPointer(moved: false);
            return;
        }

        if (_chrome.Pointer(point.Y, Root.ActualHeight, part, RailNear(point), Now)) Render();
    }

    /// <summary>
    /// Whether a <c>PointerMoved</c> is a pointer that actually went somewhere, for the purpose of the
    /// <em>chrome's</em> reveal rule. Two answers, because the cost of each mistake is different:
    /// <list type="bullet">
    /// <item>The same pixel as last time is not a movement. WinUI raises the event when what is under a
    /// stationary pointer changes, and the chrome collapsing at 650 ms is exactly that.</item>
    /// <item>A hop under <see cref="ChromeReveal.MovePixels"/> is not a movement, and the anchor is deliberately
    /// not advanced, so a mouse rattling one pixel on a desk never accumulates into a movement while a hand that
    /// really is moving crosses the threshold within a frame or two.</item>
    /// <item>A control arriving under a pointer that has not moved counts as movement of its own — 「停在进度条
    /// 上」 and 「停在画面上」 are different states with the same position — but only while the cursor is showing,
    /// which is the only time the chrome is up to be revealed in the first place.</item>
    /// </list>
    /// <para>
    /// <b>What this method is not, as of the 2026-09-15 refactor.</b> It used to be the hide's second sensor and
    /// carried the whole defence against synthetic events: a claimed step was parked while the cursor was hidden
    /// and judged by the next poll, and a showing cursor's event was checked against the OS's live position. All
    /// of that is gone with the second sensor — see <c>PollPointer</c> for why one source is enough and why the
    /// XAML channel was the wrong one to trust. What is left is a position filter for a reveal rule that only
    /// asks 「is the pointer over the picture, and is it moving enough to be a hand」, and a false positive here
    /// costs a chrome panel that stays up for another second rather than a cursor that appears over the film.
    /// </para>
    /// <para>
    /// This path only ever sees a report this filter has accepted, so what arrives at <see cref="ChromeReveal.Pointer"/>
    /// is a movement by construction and it reaches the rule as one.
    /// </para>
    /// </summary>
    private bool Moved(Point point, ChromePart part)
    {
        var first = double.IsNaN(_pointerAt.X);
        var dx = first ? double.PositiveInfinity : Math.Abs(point.X - _pointerAt.X);
        var dy = first ? double.PositiveInfinity : Math.Abs(point.Y - _pointerAt.Y);

        // 控件到达算一次移动，控件离开不算：chrome 收起时控件从指针下走掉，part 变成 None——位置一个像素
        // 没动，旧代码却把「控件走了」读成「控件到了」。None→控件的真到达照旧放行。
        var arrived = part != _pointerOn && !_cursorHidden && part != ChromePart.None;
        var real = arrived || ChromeReveal.Travelled(dx, dy);

        if (!real)
        {
            _stillMoves++;
            return false;
        }

        _pointerAt = point;
        _pointerOn = part;
        _pointerMovedAt = Now;
        _pointerMoves++;
        _stillMoves = 0;

        return true;
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        // Only the pointer actually leaving the picture. Crossing onto one of our own controls raises
        // Exited on the way past too, and treating that as 「gone」 would blink the chrome off under the
        // hand that was reaching for it.
        //
        // Asked of the OS rather than of the event, which cannot answer it: this handler is on Root with
        // handledEventsToo, so an Exited raised on a child bubbles here reporting a position that is
        // still inside Root — identical, by geometry, whether the pointer stepped off that child onto the
        // picture or left the window from over it. Reading the event's position is what left the volume
        // rail pinned up after 「鼠标移到窗口右边显示音量条之后，再移出窗口」.
        if (PointerInside()) return;

        // A departure that ends a hide gets a name like every other wake: the show line prints whatever
        // _woke holds, and 「指针走了」 is a different fact from a press or a key.
        if (_cursorHidden) _woke = "指针离开了画面";
        if (_chrome.PointerLeft(Now)) Render();
    }

    /// <summary>
    /// 鼠标滚轮调整音量, anywhere over the picture — and the rail comes up to show where the level now sits,
    /// which is the other half of the same request.
    /// <para>
    /// Two per notch rather than five: 「滚轮调音量的时候不是很顺滑，音量条一顿一顿的」 (2026-09-04). On a rail
    /// this tall a step of five moves the thumb about eleven pixels at a time, and a wheel is turned in a
    /// continuous motion — so the readout arrives as a row of jumps. Two is also mpv's own wheel step, which
    /// is the number a viewer's hand is already calibrated to from every other player. The arrow keys keep
    /// five (see <see cref="KeyStep"/>): a keypress is a discrete act, and the complaint was about the wheel.
    /// </para>
    /// </summary>
    private void OnPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(Root).Properties.MouseWheelDelta;
        if (delta == 0) return;

        // 二十报：滚轮也销回笼的账；顺带补牌——FlashRail 会重盖空闲时钟把藏匿翻成显示，而这一路
        // 过去不挂牌（「未标注的显示路径」的最后一个漏网生产路径）。
        if (_cursorHidden) _woke = "滚轮调音量";
        _ghostArm = false;

        NudgeVolume(delta > 0 ? WheelStep : -WheelStep);
        e.Handled = true;
    }

    /// <summary>Any press is activity, including one that lands on a control and stops there.</summary>
    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (BeginWindowDrag(e.GetCurrentPoint(Root).Position, e.Pointer))
        {
            e.Handled = true;
            return;
        }

        // A press is a hand even when it moves nothing, and the show line should say so: label it before
        // Render writes the line, exactly as the poll labels its own wake before the reseed.
        if (_cursorHidden) _woke = "点击（画面上按下）";
        _ghostArm = false; // 二十报：点击是手的铁证，回笼监视就地销账
        if (_chrome.WakeFully(Now)) Render();
    }

    // ---- 拖动标题栏移动窗口 -------------------------------------------------------
    //
    // Playback's title strip is client area, so moving the window by it is this page's own job. Done by
    // hand — press, moves, release — rather than by handing the press to the OS move loop, which from
    // inside a XAML island starts too late to see the button still held and leaves the window stuck to the
    // cursor afterwards: 「点击标题后窗口会固定在鼠标上」. <see cref="HostWindow.BeginDrag"/> has the why.

    /// <summary>
    /// Starts a drag when the press landed on the strip's blank space — not on any of the controls it
    /// carries, each of which has a click of its own — and the window is in a shape the user may move.
    /// </summary>
    private bool BeginWindowDrag(Point point, Pointer pointer)
    {
        if (_window?.PlaybackTitleBar != true) return false;

        if (!Covers(TitleStrip, point) || OnStripControl(point)) return false;

        if (!Native.GetCursorPos(out var grab) || !_window.BeginDrag(grab)) return false;

        // The capture keeps the moves arriving after the strip has slid out from under the point of the
        // window the cursor started on; the hold stops the reveal rule taking that strip away mid-drag,
        // since a drag reports nothing at all for as long as the hand holds still.
        _dragPointer = Root.CapturePointer(pointer) ? pointer : null;
        Hold(true, ChromeHold.Drag);
        return true;
    }

    /// <summary>
    /// Whether a point in the title strip is on one of the controls it carries. Its own method because the
    /// self-check reads it: a control left off this list is a control whose press starts a window drag
    /// instead — and for 需求 7's search box that means the caret never lands and the window follows the
    /// hand instead.
    /// </summary>
    private bool OnStripControl(Point point) =>
        Covers(BackButton, point)
        || Covers(FontBox, point)
        || Covers(StatsButton, point)
        || Covers(PinButton, point)
        || Covers(WindowButtons, point);

    /// <summary>
    /// One step of the drag, from wherever the OS says the cursor is. Asked of the OS rather than read off
    /// the event because the window is moving with the pointer: what stays the same is its position
    /// <em>within</em> the window, and that is the only thing a pointer event can report.
    /// </summary>
    private void DragWindow()
    {
        if (_window is null) return;

        // The release the events can miss — over another monitor, over a window that opened on top, over
        // nothing of ours at all. The button's own state is the one thing that cannot get stuck.
        if (!Native.LeftButtonDown())
        {
            EndWindowDrag();
            return;
        }

        if (CursorScreen(out var cursor)) _window.DragTo(cursor);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_window?.Dragging != true) return;

        EndWindowDrag();
        e.Handled = true;
    }

    /// <summary>The capture going elsewhere — a flyout, another window taking the pointer — ends the drag.</summary>
    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndWindowDrag();

    private void EndWindowDrag()
    {
        if (_window is null) return;

        // Cleared before the capture is given back, because releasing it raises CaptureLost straight back
        // into this method: what stops that being a loop is that there is nothing left to undo by then.
        var dragged = _window.Dragging;
        _window.EndDrag();

        if (_dragPointer is { } pointer)
        {
            _dragPointer = null;
            Root.ReleasePointerCapture(pointer);
        }

        // Only the drag's own hold. Releasing one nobody took would restart the idle countdown from here
        // and leave the chrome standing over the picture for another window's worth of it.
        if (dragged) Hold(false, ChromeHold.Drag);
    }

    /// <summary>
    /// 双击画面全屏. The pause half is <see cref="SecondTapOnPicture"/>, kept separate so the self-check can
    /// drive it without taking the window fullscreen.
    /// </summary>
    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        SecondTapOnPicture();
        ToggleFullscreen();
    }

    /// <summary>
    /// 点击画面暂停. Only over the picture: the chrome is full of controls, and the strips they sit on are
    /// hit-testable in their own right, so a click on the bar's empty half would otherwise pause the film
    /// the user was reaching past it to see.
    /// <para>
    /// Both halves of 「where did this land」 are handed over — the point and the element the framework hit.
    /// The second one is what keeps 「跳过片头后会自动暂停」 fixed: see <see cref="TapOnPicture"/>.
    /// </para>
    /// <para>
    /// A tap that did not land on the picture still has to say so. Without that, a later double click on the
    /// transport bar would find the pause value this tap recorded and 「undo」 it — stopping a film that was
    /// playing perfectly well.
    /// </para>
    /// </summary>
    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        if (!Attached || !TapOnPicture(e.GetPosition(Root), e.OriginalSource))
        {
            DropTapHold();
            return;
        }

        TapPicture();
        e.Handled = true;
    }

    /// <summary>
    /// The tap itself: recorded and held rather than issued, because the first click of a double click raises
    /// a <c>Tapped</c> too. <see cref="PictureTap.HoldFor"/> caps the wait far below the OS's own double-click
    /// interval — half a second of nothing after clicking the picture would be a worse fault than the one this
    /// fixes.
    /// <para>
    /// Its own method rather than only a handler body: <c>TappedRoutedEventArgs</c> cannot be constructed, so
    /// this is the only shape the self-check can reach.
    /// </para>
    /// </summary>
    private void TapPicture()
    {
        _tap.First(ViewModel.Paused);
        _tapHold.Interval = TimeSpan.FromMilliseconds(PictureTap.HoldFor(Native.GetDoubleClickTime()));
        _tapHold.Start();
    }

    /// <summary>The hold expired with no second click: now the tap means what it always meant.</summary>
    private void OnTapHoldElapsed(object? sender, object e)
    {
        _tapHold.Stop();
        if (_tap.Elapsed() && Attached) ViewModel.TogglePause();
    }

    /// <summary>
    /// The second click of a double click, playback side only — fullscreen is the caller's, which is what lets
    /// the self-check walk this path without moving the window.
    /// <para>
    /// Caught inside the hold there is nothing to undo, which is the whole point. Missed it — a double click
    /// slower than the hold — and the pause is put back to the value read before it, exactly as it used to be;
    /// what is new is that the badge is silenced either way, so the gesture no longer flashes 暂停 and then
    /// 播放 across the middle of the picture.
    /// </para>
    /// </summary>
    private void SecondTapOnPicture()
    {
        _tapHold.Stop();

        if (_tap.Second() is { } before && Attached) ViewModel.SetPaused(before);

        _pulseMutedAt = Now;
        HidePulse();
    }

    /// <summary>This tap is off: it landed on a control, or the player is going away.</summary>
    private void DropTapHold()
    {
        _tapHold.Stop();
        _tap.Forget();
    }

    // ---- the keyboard -----------------------------------------------------------

    /// <summary>
    /// The player's own keys. On the control's own <c>KeyDown</c> rather than added with
    /// <c>handledEventsToo</c>, deliberately: a focused button has already handled Space by the time the
    /// event reaches here, and stealing it back would mean the button under the pointer could never be
    /// pressed with the keyboard.
    /// <para>
    /// 键位现在是可重绑的（「参考上图在设置中新增快捷键功能」，2026-09-08）：按下的键 ＋ 此刻的修饰键拼成一次
    /// <see cref="KeyStroke"/>，<see cref="ShortcutCatalog.Lookup"/> 查它绑到哪个动作，再由 <see cref="ShortcutHandlers"/>
    /// 那张 动作→处理器 表执行。这一头每个处理器仍是一句话 —— 一个窗口操作或一次视图模型调用；「键→动作」在
    /// Core 那张表、单测钉着，两张表靠动作 Id 对上。Esc 和 Y 两个固定键不参与重绑，留在下面 <see cref="Dispatch"/>
    /// 的开头原样处理。快退多少秒、倍速夹到哪、有没有下一集 —— 都不是键盘该回答的，这里一样不管。
    /// </para>
    /// </summary>
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!Attached || _window is null) return;

        // 需求 7's search box has the keyboard: everything below is a font name's letters as much as it is a
        // command. Typing 「Consolas」 would otherwise mute the film, skip to the next episode and reset the
        // speed on the way past. The box handles Escape itself — see OnFontBoxKeyDown — so there is still a
        // way out of the box and then out of fullscreen.
        if (_typing) return;

        if (!Dispatch(e.Key)) return;

        e.Handled = true;

        // A keyboard command has no pointer behind it, so the chrome is shown wherever the pointer
        // happens to be resting — otherwise pressing Space over the middle of the picture changes the
        // playback state with nothing on screen to say so.
        if (_cursorHidden) _woke = $"按键 {e.Key}";
        _ghostArm = false; // 二十报：按键销回笼的账
        if (_chrome.WakeFully(Now)) Render();
    }

    /// <summary>
    /// 这一下有没有当成播放器键位处理掉。两个固定键在前（原样，不查修饰键，和改造前一致）：Esc 全屏则退出全屏、
    /// 否则停止播放（Esc 是全局「退出」、也是设置里重绑方框的取消键）；Y 只在出现跳过提示时确认跳过 —— 没提示时
    /// 它落到下面那张表、查不到就返回 false，那一下照旧不被吃掉，和改造前「Y 不是播放器键」一致。其余键走可重绑
    /// 那张表。<see cref="ShortcutCatalog"/> 把 Esc、Y 列为保留键，所以那张表永远不会绑上这两颗。
    /// </summary>
    private bool Dispatch(VirtualKey key)
    {
        switch (key)
        {
            case VirtualKey.Escape when _window!.Fullscreen:
                ToggleFullscreen();
                return true;

            case VirtualKey.Escape:
                ViewModel.Stop();
                return true;

            case VirtualKey.Y when ViewModel.SkipOffered:
                ViewModel.TakeSkip();
                return true;
        }

        // 修饰键 KeyRoutedEventArgs 不带，从 Native 读（同改造前 Z/X 那套）。认不出的键当没有快捷键。
        if (KeyStrokeInterop.Token(key) is not { } token) return false;

        var stroke = new KeyStroke(token, Native.CtrlHeld, Native.AltHeld, Native.ShiftHeld);
        var action = ShortcutCatalog.Lookup(ViewModel.ShortcutBindings, stroke);
        if (action is null || !ShortcutHandlers.TryGetValue(action, out var run)) return false;

        run();
        return true;
    }

    private Dictionary<string, Action>? _shortcutHandlers;

    /// <summary>
    /// 动作 Id → 这一下做什么，可重绑那 19 个动作的另一半。Core 那张表（<see cref="ShortcutCatalog"/>）定
    /// 「键→动作」，这张表定「动作→干什么」，靠动作 Id 对上。这一头必须留在页面：音量增减和静音要顺带闪一下
    /// 音量条（走会闪条的页面包装 <see cref="NudgeVolume"/> / <see cref="ToggleMute"/>，不是直接调视图模型），
    /// 全屏和置顶是窗口的事、mpv 一无所知。<b>少一个动作没有处理器 = 那颗键按下去没反应</b>，自检
    /// <c>ProbeShortcuts</c> 拿这张表的键和 <see cref="ShortcutCatalog.Actions"/> 逐一对比，正是防这种静默死键。
    /// 懒建一次；只读键、不执行 —— 建这张表不会真去动 mpv，所以自检在没真播的时候读它也安全。
    /// </summary>
    private Dictionary<string, Action> ShortcutHandlers => _shortcutHandlers ??= new(StringComparer.Ordinal)
    {
        ["toggle-pause"] = () => ViewModel.TogglePause(),
        ["seek-backward"] = () => ViewModel.SeekBackward(),
        ["seek-forward"] = () => ViewModel.SeekForward(),
        ["volume-up"] = () => NudgeVolume(KeyStep),
        ["volume-down"] = () => NudgeVolume(-KeyStep),
        ["toggle-fullscreen"] = ToggleFullscreen,
        ["toggle-mute"] = ToggleMute,
        ["previous-episode"] = () => ViewModel.PreviousEpisode(),
        ["next-episode"] = () => ViewModel.NextEpisode(),
        ["toggle-pin"] = TogglePinByHand,
        ["chapter-previous"] = () => ViewModel.StepChapter(-1),
        ["chapter-next"] = () => ViewModel.StepChapter(1),
        ["speed-down"] = () => ViewModel.NudgeSpeed(-0.1),
        ["speed-up"] = () => ViewModel.NudgeSpeed(0.1),
        ["speed-reset"] = () => ViewModel.SetSpeed(1.0),
        ["subtitle-delay-decrease"] = () => ViewModel.NudgeDelay(subtitle: true, -0.1),
        ["subtitle-delay-increase"] = () => ViewModel.NudgeDelay(subtitle: true, 0.1),
        ["audio-delay-decrease"] = () => ViewModel.NudgeDelay(subtitle: false, -0.1),
        ["audio-delay-increase"] = () => ViewModel.NudgeDelay(subtitle: false, 0.1)
    };

    /// <summary>
    /// 空格总归「播放/暂停」——包括落点不在页面上的那几下。挂在 <see cref="Root"/> 上、带着
    /// <c>handledEventsToo</c>（构造函数里注册），因为键盘到不了 <see cref="OnKeyDown"/> 的情形有三种，
    /// 页面那路一个都救不了：
    /// <list type="bullet">
    /// <item>焦点被 chrome 的滑条一类拿走：值被标了已处理，页面（特意不带 handledEventsToo）收不到。</item>
    /// <item>焦点停在控件上、控件又没处理空格：能到，但这一路先把已处理的和没处理的一并收齐，行为才齐。</item>
    /// <item>焦点被 <see cref="OnChromeClick"/> 送回页面之前的那一瞬。</item>
    /// </list>
    /// <para>
    /// 两条让路。其一，重绑：空格若已不归 <c>toggle-pause</c>（设置里被让给了别的动作），这里直接返回、
    /// 不标已处理，那一下照旧走 <see cref="OnKeyDown"/> 的表 —— 这边只认「空格＝播放/暂停」这一种世界。
    /// 其二，焦点正停在 <see cref="ButtonBase"/> 上时不接：那是键盘 Tab 走到的按钮，空格按按钮的规矩按下
    /// 它才是对的；缺了这条豁免，停在播放键上的空格会被这里暂停一次、又被按钮 KeyUp 的 Click 播放一次，
    /// 按了个寂寞。点出来的焦点不成气候 —— <see cref="OnChromeClick"/> 当场把它还给页面。
    /// </para>
    /// <para>
    /// 处理完标已处理，<see cref="OnKeyDown"/> 那路（在树上更靠外、这次冒泡到不了）和它自己都不会再来
    /// 第二遍：焦点在页面上时空格从 <see cref="OnKeyDown"/> 走，根本不经过 Root；焦点在别处时只经过这里。
    /// 一颗键永远只走一条路。
    /// </para>
    /// </summary>
    private void OnSpaceShortcut(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Space || !Attached || _window is null || _typing) return;

        var stroke = new KeyStroke("Space", false, false, false);
        if (ShortcutCatalog.Lookup(ViewModel.ShortcutBindings, stroke) != "toggle-pause") return;
        if (FocusManager.GetFocusedElement() is ButtonBase) return;

        ViewModel.TogglePause();
        e.Handled = true;

        // A keyboard command has no pointer behind it, and this one may not even have had the page's usual
        // OnKeyDown half to wake the chrome on its way: show it wherever the pointer is resting, exactly as
        // OnKeyDown does for its own keys.
        if (_cursorHidden) _woke = "空格（播放/暂停）";
        _ghostArm = false;
        if (_chrome.WakeFully(Now)) Render();
    }

    /// <summary>
    /// 点过的 chrome 按钮把焦点还给页面。WinUI 的规矩是点一颗按钮、焦点就停在它身上，而从那一下起空格是
    /// 那颗按钮的、↑↓ 是它邻居的 —— 用户的手在控制条上点完，打算的却仍是「按空格暂停」。Click 冒泡到
    /// <see cref="Root"/> 时按钮自己的事已经做完（命令执行、菜单张开），此刻把焦点收回页面，谁也不少什么。
    /// <para>
    /// 带 Flyout 的按钮不收：菜单条要靠焦点接管上下键，而且它们本来就不吃空格。真正用 Tab 走到按钮上的
    /// 焦点不经过 Click，不受影响 —— 那一路空格按按钮的规矩来（<see cref="OnSpaceShortcut"/> 的豁免）。
    /// </para>
    /// </summary>
    private void OnChromeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ButtonBase || !Attached || Visibility != Visibility.Visible) return;

        // 带 Flyout 的按钮例外（这套投影里 Flyout 挂在 Button 上）：菜单要靠焦点接管上下键。
        if (sender is Button { Flyout: not null }) return;

        Focus(FocusState.Programmatic);
    }

    // ---- Win32 键盘兜底 ----------------------------------------------------------
    //
    // 「新增esc退出全屏 按空格开始播放」（2026-09-15）。OnKeyDown 和 OnSpaceShortcut 都只在 Win32
    // 键盘焦点落进 XAML 岛里时才响；全屏播放期间前台被 Edge、AyuGram 这类抢走过再回来，或焦点落在
    // 宿主窗口和视频子窗口上时，键被 DefWindowProc 吞掉 —— 用户日志里十三天「按键/空格」唤醒一次都
    // 没有，全是这条。窗口的线程级 WH_KEYBOARD 钩子在焦点不在岛里时把空格和 Esc 送到这里；焦点在
    // 岛里时它一概放行，两条路永远只有一条出键。

    /// <summary>接不接这一下。菜单/弹层开着让路，正在打字让路，其余只认空格和 Esc 两颗。</summary>
    bool IWin32KeySink.WantsKey(int virtualKey)
    {
        if (!Attached || _typing) return false;

        // 焦点在弹层上时 Win32 焦点本来就在岛里、走不到这里；这道闸留给「弹层开着而焦点又掉出岛」
        // 这种状态机打架的时刻 —— Esc 该归 XAML 去关弹层，兜底路不越权。
        if (_holds.HasFlag(ChromeHold.Menu)) return false;

        return virtualKey is (int)VirtualKey.Space or (int)VirtualKey.Escape;
    }

    /// <summary>
    /// 接。与岛内两条键路同一句话（<see cref="Dispatch"/>）：Esc 全屏则退全屏、非全屏则停止播放；
    /// 空格走可重绑表 —— 表里默认绑在 toggle-pause 上，重绑走了兜底路跟着走，岛内岛外永远做同一件事。
    /// </summary>
    void IWin32KeySink.Handle(int virtualKey)
    {
        var key = (VirtualKey)virtualKey;
        if (!Attached || !Dispatch(key)) return;

        // 姓名牌：兜底路自己的名字，和 XAML 那两路（「按键 X」「空格（播放/暂停）」）分得开 ——
        // 以后日志里见到「（Win32 兜底）」就是焦点掉出岛的那一阵。
        if (_cursorHidden) _woke = key == VirtualKey.Space ? "空格（播放/暂停，Win32 兜底）" : $"按键 {key}（Win32 兜底）";
        _ghostArm = false;
        if (_chrome.WakeFully(Now)) Render();
    }

    /// <summary>
    /// 自检：上面两条 Root 接线（空格拦截、chrome 焦点归还）都真的挂上了。注册被拆掉是「按空格没反应」
    /// 最静默的死法 —— 键盘到不了页面那一路，屏上什么都看不见，编译也看不出，只有这里能问。
    /// </summary>
    internal bool SpaceAndFocusWiringArmed => _spaceShortcutArmed && _chromeBlurArmed;

    private bool _spaceShortcutArmed;
    private bool _chromeBlurArmed;

    // ---- the window -------------------------------------------------------------

    private void OnToggleFullscreen(object sender, RoutedEventArgs e) => ToggleFullscreen();

    /// <summary>
    /// 全屏切换. The window's business and no one else's: the glyph is the button's own state, and mpv is
    /// told nothing at all — the video child fills whatever client area it is given.
    /// <para>
    /// Redrawn afterwards rather than left to the resize, because one piece of the chrome now reads the
    /// window's shape: the thin bottom line is windowed-only, and the reveal rule it otherwise rides on has
    /// nothing to report when the pointer never moved.
    /// </para>
    /// </summary>
    private void ToggleFullscreen()
    {
        if (_window is null) return;

        SetFullscreen(!_window.Fullscreen);
    }

    /// <summary>
    /// 进／退全屏, said as a state rather than a flip. Two callers want it this way: the button and the F key
    /// want the flip (<see cref="ToggleFullscreen"/>), and 开始播放后自动全屏 wants a plain 「进全屏」 that a
    /// second call cannot undo — 「开播那一下按一次 toggle」 would be right only while the window happened not
    /// to be fullscreen already, which is exactly the case a user in fullscreen for the last film is in.
    /// <para>
    /// Setting it to what it already is is nothing at all: <see cref="HostWindow.Fullscreen"/> returns early
    /// on an unchanged value, and the glyph is written the same either way.
    /// </para>
    /// </summary>
    internal void SetFullscreen(bool on)
    {
        if (_window is null) return;

        _window.Fullscreen = on;
        FullscreenGlyph.Glyph = Glyph(on ? FullscreenExitCode : FullscreenEnterCode);

        // 右上角那一颗的图标也跟着走（全屏时它是「窗口化」）。Render 只在标题条露着的时候才更新它，
        // 而按 F 或 Esc 进出全屏之后，人往往是**把鼠标移到右上角去看**的 —— 那时候才更新就已经晚了。
        UpdateMaximizeGlyph();

        // 挂名（第二十一报补上的最后一条漏牌的显示路）。进出全屏会让 Render 重排控件，藏着的光标可能
        // 因此回到屏上；不挂名的话下一份日志里那一行读到的是「未标注的显示路径」，而这条路其实一直
        // 有出处 —— 全屏只能由按钮、F 键、Esc 或开播自动全屏触发，全是人的动作。
        if (_cursorHidden) _woke = on ? "进全屏" : "退全屏";

        Render();
    }

    private void OnTogglePin(object sender, RoutedEventArgs e) => TogglePinByHand();

    /// <summary>
    /// 用户亲手拨置顶开关的那一下 —— 按钮和 T 键（toggle-pin）都从这儿走。除了立起/放下窗口的置顶，
    /// 还要把选择记进设置（「对播放页面"是否置顶"的设置进行持久化保存，程序重启后仍保留上次选择」，
    /// 2026-09-15）：恢复进场那一档是 <c>EnterPlayer</c> 的事，探针与退出播放的放下不记账，只有用户
    /// 亲手拨的这一下才算数。
    /// </summary>
    private void TogglePinByHand()
    {
        if (_window is null) return;

        SetPinned(!_window.TopMost);
        ViewModel.SavePinTopmost(_window.TopMost);
    }

    /// <summary>
    /// 置顶 on or off, in one place: the window, the two drawn pins, and the two sentences that tell somebody
    /// without a pointer which way the switch is thrown.
    /// <para>
    /// There is one piece of state now — <see cref="HostWindow.TopMost"/> — and this is the only thing that
    /// reads or writes it on this page. It used to live in two places at once, the window's flag and a
    /// <c>ToggleButton.IsChecked</c>, kept in step by three separate pairs of assignments: the T key, the
    /// button's own click, and the reset on the way out of the player. The fullscreen button next to it has
    /// been the right shape all along — one real piece of state on the window, one glyph swapped on screen.
    /// </para>
    /// <para>
    /// The window is checked for null separately because the page's constructor calls this before
    /// <see cref="Attach"/> has run: the two pins and both sentences have to start out agreeing with each
    /// other, and the alternative is a second copy of the starting state written into the markup.
    /// </para>
    /// </summary>
    private void SetPinned(bool pinned)
    {
        if (_window is not null) _window.TopMost = pinned;

        PinOnIcon.Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;
        PinOffIcon.Visibility = pinned ? Visibility.Collapsed : Visibility.Visible;

        AutomationProperties.SetName(PinButton, PinIndicator.Name(pinned));
        ToolTipService.SetToolTip(PinButton, PinIndicator.Tip(pinned));
    }

    // ---- 最小化 / 最大化 / 关闭 ---------------------------------------------------
    //
    // The player's, not the shell's, since the strip they sit in is the player's: playback takes the
    // caption off the window so the picture can reach the top edge, and these three are what is left of
    // it. Owning them here is also what lets them hide with the reveal rule instead of standing over the
    // picture for the whole film.

    private void OnMinimizeWindow(object sender, RoutedEventArgs e) => _window?.Minimize();

    private void OnToggleMaximizeWindow(object sender, RoutedEventArgs e)
    {
        // 全屏时这一颗是「窗口化」，不是「最大化」：退出全屏、窗口回到进全屏前的大小（那份几何
        // HostWindow.Fullscreen 自己记着）。其余时候才是真正的最大化／还原 —— ToggleMaximize 在全屏下
        // 会站着不动（窗口的边归显示器），所以这里必须分成两支，不能只把它转过去。
        if (_window?.Fullscreen == true) SetFullscreen(false);
        else _window?.ToggleMaximize();

        UpdateMaximizeGlyph();
    }

    /// <summary>
    /// 右上角那颗「关闭」（2026-09-15 的拍板）：播放中点它不再把窗口关掉 —— 主窗口播放时那一下等于退出
    /// 整个程序 —— 而是停止播放、外壳回主页。独立窗口模式同一句话也成立：停止播放会把那个窗口顺带收掉
    /// （PlayerHidden 上接着两头），主窗口则回到主页。没有外壳（页面还没挂上）时退回原样关窗口，那条路
    /// 只有程序自身收尾才会走到。
    /// </summary>
    private void OnCloseWindow(object sender, RoutedEventArgs e)
    {
        if (_shell is null)
        {
            _window?.Close();
            return;
        }

        _shell.ClosePlayerToHome();
    }

    /// <summary>
    /// 最大化 or 还原, whichever the button would do next —— 全屏时它说的也是这件事，只是那一档的「还原」
    /// 落成「退出全屏」（用户 2026-09-14 的拍板：「全屏时最大化的图标换成还原图标，点击后还原窗口大小」）。
    /// <para>
    /// 从前这一档是收起来不给看，理由是「全屏时窗口边就是显示器的边，最大化什么也做不了」—— 理由本身没错，
    /// 错在**那个位置缺了一颗按钮看着就像少了东西**：一个人从全屏里想把窗口变回窗口，鼠标移上去找的正是
    /// 这一颗的位置。所以它现在一直在，只是换了图标、也换了点下去做的事。
    /// </para>
    /// </summary>
    private void UpdateMaximizeGlyph()
    {
        MaximizeButton.Visibility = Visibility.Visible;

        MaximizeGlyph.Glyph = Glyph(_window?.Fullscreen == true || _window?.IsMaximized == true
            ? RestoreGlyphCode
            : MaximizeGlyphCode);
    }

    // ---- 音量 --------------------------------------------------------------------
    //
    // The slider itself is a two-way binding onto the view model, so the thumb and mpv keep up with each
    // other without passing through here. What is left is the half that is not about volume at all: the
    // rail has to be on screen to be read, and a wheel turn or an arrow key arrives with no pointer over
    // it to reveal it the usual way.

    /// <summary>
    /// Moves the volume and shows the rail. Both the wheel and the arrow keys land here, so the readout they
    /// bring up is the same for either; how far each one moves is the caller's (<see cref="WheelStep"/>
    /// against <see cref="KeyStep"/>).
    /// </summary>
    private void NudgeVolume(int delta)
    {
        if (!Attached) return;

        ViewModel.NudgeVolume(delta);
        if (_chrome.FlashRail(Now)) Render();
    }

    private void OnToggleMute(object sender, RoutedEventArgs e) => ToggleMute();

    private void ToggleMute()
    {
        if (!Attached) return;

        ViewModel.ToggleMute();
        if (_chrome.FlashRail(Now)) Render();
    }
}

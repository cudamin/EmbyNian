using EmbyNian.Playback;
using EmbyNian.Shell.Interop;
using EmbyNian.Shell.ViewModels;
using EmbyNian.Shell.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
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
public sealed partial class PlayerPage
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

        // An event is not a movement. See Moved: the rule's idle clock is what the cursor's two seconds are
        // counted from, and restamping it for a pointer that never went anywhere is what 「鼠标指针还是不会
        // 自动隐藏」 turned out to be.
        if (!Moved(point, part)) return;

        if (_chrome.Pointer(point.Y, Root.ActualHeight, part, RailNear(point), Now)) Render();
    }

    /// <summary>
    /// Whether a <c>PointerMoved</c> is a pointer moving. Three answers rather than one, because the cost of
    /// each mistake is different:
    /// <list type="bullet">
    /// <item>The same pixel as last time is never a movement. WinUI raises the event when what is under a
    /// stationary pointer changes, and the chrome collapsing at 650 ms is exactly that.</item>
    /// <item>A shorter hop than <see cref="PointerNoise"/> is not a movement <em>while the cursor is
    /// showing</em>, and the anchor is deliberately not advanced, so a mouse rattling one pixel on a desk
    /// never accumulates into activity while a hand that really is dragging the thing crosses the threshold
    /// within a frame or two.</item>
    /// <item>Once the cursor is hidden, any move at all brings it back. Asking for two pixels before
    /// answering a hand reaching for the mouse is the one failure here a user would notice.</item>
    /// <item>Except this player's own ask. Hiding the cursor ends with one physical pixel out and straight
    /// back through the real input queue, because that is the only thing the framework hears
    /// (<see cref="Native.NudgeCursorState"/>) — and without this clause the leg out satisfies the rule above
    /// and the player wakes itself the instant it goes to sleep. Recognised by distance <em>and</em> by the
    /// window since the ask went out; see <see cref="NudgeEcho"/>.</item>
    /// </list>
    /// <para>
    /// The part under the pointer counts as movement of its own: a control appearing beneath a still hand is
    /// something the rule has to hear about even though the coordinates are unchanged, because 「停在进度条
    /// 上」 and 「停在画面上」 are different states with the same position.
    /// </para>
    /// </summary>
    private bool Moved(Point point, ChromePart part)
    {
        var first = double.IsNaN(_pointerAt.X);
        var dx = first ? double.PositiveInfinity : Math.Abs(point.X - _pointerAt.X);
        var dy = first ? double.PositiveInfinity : Math.Abs(point.Y - _pointerAt.Y);

        // The echo of our own ask, which is a pixel out and a pixel back inside a couple of frames. The
        // anchor is deliberately left where it was, so the stillness this ask is part of goes on being
        // counted from the moment the hand actually stopped.
        var echo = _cursorHidden
                   && Now - _nudgedAt <= NudgeEcho
                   && dx < PointerNoise
                   && dy < PointerNoise;

        var real = part != _pointerOn
                   || (dx > 0 || dy > 0) && !echo && (_cursorHidden || dx >= PointerNoise || dy >= PointerNoise);

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
        ["toggle-pin"] = () => SetPinned(!_window!.TopMost),
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

        _window.Fullscreen = !_window.Fullscreen;
        FullscreenGlyph.Glyph = Glyph(_window.Fullscreen ? FullscreenExitCode : FullscreenEnterCode);
        Render();
    }

    private void OnTogglePin(object sender, RoutedEventArgs e)
    {
        if (_window is null) return;
        SetPinned(!_window.TopMost);
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
        _window?.ToggleMaximize();
        UpdateMaximizeGlyph();
    }

    private void OnCloseWindow(object sender, RoutedEventArgs e) => _window?.Close();

    /// <summary>
    /// 最大化 or 还原, whichever the button would do next, and nothing at all in fullscreen — where the
    /// window's edges are the monitor's and <see cref="HostWindow.ToggleMaximize"/> stands aside.
    /// </summary>
    private void UpdateMaximizeGlyph()
    {
        var fullscreen = _window?.Fullscreen == true;
        MaximizeButton.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        MaximizeGlyph.Glyph = Glyph(_window?.IsMaximized == true ? RestoreGlyphCode : MaximizeGlyphCode);
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

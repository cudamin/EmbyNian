using EmbyNian.Playback;
using EmbyNian.Shell.Interop;
using EmbyNian.Shell.ViewModels;
using EmbyNian.Shell.Windowing;
using Microsoft.UI.Xaml;
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

        var real = part != _pointerOn
                   || (dx > 0 || dy > 0) && (_cursorHidden || dx >= PointerNoise || dy >= PointerNoise);

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
    /// 鼠标滚轮调整音量, anywhere over the picture — and the rail comes up to show the number, which is
    /// the other half of the same request.
    /// </summary>
    private void OnPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(Root).Properties.MouseWheelDelta;
        if (delta == 0) return;

        NudgeVolume(delta > 0 ? 5 : -5);
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

        if (Native.GetCursorPos(out var cursor)) _window.DragTo(cursor);
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

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // Undo the single tap this gesture's first click already performed, so a double click is a
        // fullscreen toggle and nothing else. Restored to the value read then rather than by toggling
        // again: mpv's own 「pause」 has not come back through the status poll yet, so a second toggle
        // would read the same stale value and set the same thing twice.
        if (_tapPause is { } before && Attached) ViewModel.SetPaused(before);

        _tapPause = null;
        ToggleFullscreen();
    }

    /// <summary>
    /// 点击画面暂停. Only over the picture: the chrome is full of controls, and the strips they sit on are
    /// hit-testable in their own right, so a click on the bar's empty half would otherwise pause the film
    /// the user was reaching past it to see.
    /// </summary>
    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        _tapPause = null;
        if (!Attached || !TapOnPicture(e.GetPosition(Root))) return;

        _tapPause = ViewModel.Paused;
        ViewModel.TogglePause();
        e.Handled = true;
    }

    // ---- the keyboard -----------------------------------------------------------

    /// <summary>
    /// The player's own keys. On the control's own <c>KeyDown</c> rather than added with
    /// <c>handledEventsToo</c>, deliberately: a focused button has already handled Space by the time the
    /// event reaches here, and stealing it back would mean the button under the pointer could never be
    /// pressed with the keyboard.
    /// <para>
    /// Each case is one line, and every one of those lines is either a window operation or a single call
    /// on the view model. What 快退 means in seconds, what the speed clamps to, whether there is a next
    /// episode — none of that is a question about the keyboard, and none of it is decided here.
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

        var handled = true;

        switch (e.Key)
        {
            case VirtualKey.Space:
                ViewModel.TogglePause();
                break;

            case VirtualKey.Left:
                ViewModel.SeekBackward();
                break;

            case VirtualKey.Right:
                ViewModel.SeekForward();
                break;

            case VirtualKey.Up:
                NudgeVolume(5);
                break;

            case VirtualKey.Down:
                NudgeVolume(-5);
                break;

            case VirtualKey.F:
                ToggleFullscreen();
                break;

            case VirtualKey.M:
                ToggleMute();
                break;

            // 上一集 / 下一集. `P` is the 上一集 key README documents; 置顶 moved to `T`, which is what the
            // pin button's tooltip now says. The two used to be the same key, and the film-watching one
            // wins.
            case VirtualKey.P:
                ViewModel.PreviousEpisode();
                break;

            case VirtualKey.N:
                ViewModel.NextEpisode();
                break;

            case VirtualKey.T:
                PinButton.IsChecked = !(PinButton.IsChecked ?? false);
                _window.TopMost = PinButton.IsChecked ?? false;
                break;

            // 章节前后跳. mpv's own `add chapter`, which lands on the mark rather than a second either
            // side of it, and says nothing at all on a file with no chapters — hence the notice.
            case VirtualKey.PageUp:
                ViewModel.StepChapter(-1);
                break;

            case VirtualKey.PageDown:
                ViewModel.StepChapter(1);
                break;

            // 倍速微调 and its reset. VirtualKey has no names for the bracket keys, so the OEM codes are
            // spelled out: 219 is VK_OEM_4 「[」 and 221 is VK_OEM_6 「]」 on every layout that has them.
            case (VirtualKey)219:
                ViewModel.NudgeSpeed(-0.1);
                break;

            case (VirtualKey)221:
                ViewModel.NudgeSpeed(0.1);
                break;

            case VirtualKey.Back:
                ViewModel.SetSpeed(1.0);
                break;

            // 字幕/音频延迟. Shift is the other direction, and it is read from the OS because
            // KeyRoutedEventArgs does not carry it — Z and Shift+Z are the same VirtualKey. Unshifted is
            // negative, which is mpv's own direction for these keys and therefore the one a user of the
            // bundled configuration already has in their fingers.
            case VirtualKey.Z:
                ViewModel.NudgeDelay(subtitle: true, Native.ShiftHeld ? 0.1 : -0.1);
                break;

            case VirtualKey.X:
                ViewModel.NudgeDelay(subtitle: false, Native.ShiftHeld ? 0.1 : -0.1);
                break;

            // Only while there is something to take. Otherwise Y is not a player key at all.
            case VirtualKey.Y when ViewModel.SkipOffered:
                ViewModel.TakeSkip();
                break;

            // Fullscreen first: Escape from a fullscreen player means 「窗口化」, not 「停止」.
            case VirtualKey.Escape when _window.Fullscreen:
                ToggleFullscreen();
                break;

            case VirtualKey.Escape:
                ViewModel.Stop();
                break;

            default:
                handled = false;
                break;
        }

        if (!handled) return;

        e.Handled = true;

        // A keyboard command has no pointer behind it, so the chrome is shown wherever the pointer
        // happens to be resting — otherwise pressing Space over the middle of the picture changes the
        // playback state with nothing on screen to say so.
        if (_chrome.WakeFully(Now)) Render();
    }

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
        _window.TopMost = PinButton.IsChecked ?? false;
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
    // The slider itself is a two-way binding onto the view model, so the number and mpv keep up with each
    // other without passing through here. What is left is the half that is not about volume at all: the
    // rail has to be on screen to be read, and a wheel turn or an arrow key arrives with no pointer over
    // it to reveal it the usual way.

    /// <summary>
    /// Moves the volume and shows the rail. Both the wheel and the arrow keys land here, so the number
    /// they move by and the readout they bring up are the same for either.
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

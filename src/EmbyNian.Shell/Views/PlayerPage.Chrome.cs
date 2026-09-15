using EmbyNian.Diagnostics;
using EmbyNian.Infrastructure;
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
    private bool ReseedPointer() => ReseedPointer(moved: false);

    /// <summary>
    /// The same, for the two callers that mean different things by it. A resize genuinely moves every control
    /// out from under a pointer that never moved, so 「the pointer is not still」 is true and
    /// <paramref name="moved"/> is true; the ten-hertz poll has already asked the question properly, against
    /// the point the cursor was hidden at, and by the time it gets here it is only reporting a position.
    /// </summary>
    private bool ReseedPointer(bool moved)
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

        if (_chrome.Pointer(point.Y, Root.ActualHeight, part, RailNear(point), Now, moved)) Render();
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
    /// The threshold is <see cref="ChromeReveal.MovePixels"/>, the same one the events are filtered by, and it
    /// applies in both cursor states: a mouse resting on a desk rattles a pixel, up to two on this machine's
    /// own log. It used to have to clear a second, higher bar as well — this player's own hide-time ask was a
    /// real injected one-pixel round trip and had to be told from a hand — and no longer does, because that ask
    /// is gone. See <c>Nudge</c>.
    /// </para>
    /// </summary>
    private void PollPointer()
    {
        if (!CursorScreen(out var screen)) return;

        var dx = _polledKnown ? Math.Abs(screen.X - _polled.X) : int.MaxValue;
        var dy = _polledKnown ? Math.Abs(screen.Y - _polled.Y) : int.MaxValue;

        // 第八报（2026-09-15）：这是合成声明的裁决席之一。两个读数之间一个像素没动 = 指针物理没动，
        // 任何挂着的 XAML 移动声明（_xamlClaim）都是框架重推坐标基准送来的谎言——丢弃、计数。08:22:44
        // 的现场就是这一拍的形状：藏了 29.7 秒、声明声称走了 5 个逻辑像素、这里两个读数纹丝不动。
        if (dx == 0 && dy == 0)
        {
            DiscardSyntheticClaim();
            return;
        }

        // <b>A hidden cursor asks a different question, and asks it of the rule.</b> While the cursor is showing
        // the question is 「where is the pointer」 and this poll's own last reading is the right thing to measure
        // from — a step of five pixels is a hand. Hidden, the question is 「has anybody touched the mouse」, and
        // this reading is the wrong thing to measure from: it is advanced on every accepted step, so a desk
        // nudging the pointer one pixel at a time walks it along and the step never reaches five. The reference
        // that does not move is the point the cursor was hidden at, and the rule holds it — so a pointer within
        // a pixel of there is the desk however many reports arrive, and one anywhere else is a hand. See
        // ChromeReveal.HiddenTolerance.
        if (_cursorHidden)
        {
            if (!_chrome.WanderedFromHiding(screen.X, screen.Y))
            {
                // Read as still, so the countdown keeps running and this is not a movement. The reading is
                // deliberately not advanced: it stays at the hiding point for as long as the hide lasts, which
                // is what makes the comparison above about distance from the hide rather than distance from the
                // previous report.
                _hiddenNoise++;

                // 第八报（2026-09-15）：裁决席之二。指针离藏匿点不超过 HiddenTolerance = 没人碰过鼠标，
                // 挂着的 XAML 移动声明（可能正好在读数之间的小抖动里溜过了上面的零位移裁决）也是谎言，
                // 同样丢弃。真手在两拍之间走出的位移会落在这里之外，走不到这里。
                DiscardSyntheticClaim();
                return;
            }
        }
        else if (_polledKnown && !ChromeReveal.Travelled(dx, dy))
        {
            // Counted while the cursor is hidden, where the same step is also how a rattling desk shows up —
            // the count goes out beside the wake reason, so the next report says which it was.
            //
            // The threshold used to have a second job as well, and does not any more: this player's own one-pixel
            // ask used to land here and had to be told from a hand. That ask is gone — see Nudge.
            return;
        }

        // 第八报（2026-09-15）：走到这里 = OS 认可了移动（真手）。挂着的 XAML 声明无需再裁——它说的
        // 和轮询刚证实的同一件事，就地吸收；唤醒理由由下面的轮询句子写，事件自己的不再赘述。
        _xamlClaim = null;

        _polled = screen;
        _polledKnown = true;
        _polledMoves++;
        _pointerMovedAt = Now;

        var wasHidden = _cursorHidden;

        // Said BEFORE the reseed, not after. The show line is written inside SetCursorHidden, which the reseed
        // reaches through Render — so anything assigned after it is assigned too late and the log prints the
        // previous wake's reason or the default. That is exactly what the sixth report cost: a hide ended by
        // this poll's reading printed as 「没记到移动（按键、菜单或窗口变化）」, and three rounds of looking
        // everywhere but here because the log swore no pointer path had spoken. The reason exists before the
        // consequence; write it in that order.
        if (wasHidden)
        {
            // 第九报（2026-09-15）：唤醒行的取证三件套。用户报「屏幕一全屏播放时，屏幕二的 AyuGram 收到
            // 消息会唤起屏幕一静止隐藏的鼠标指针」，三天日志里同一签名（一拍整 60px、纵向恒 0）分不清是
            // 物理注入还是桌面重排搬了坐标——从此唤醒那拍连同读数绝对坐标、虚拟屏矩形、指针下的窗口
            // 一起出门：虚拟屏矩形没变＝读数变化是真的（有进程在注入位移，去抓注入者）；矩形变了＝
            // 指针根本没动，是桌面重排把坐标搬走，唤醒是误报。
            var vs = Native.VirtualScreen();
            _woke = $"轮询问出了 {dx},{dy} 物理像素，读数 {screen.X},{screen.Y}"
                  + $"，虚拟屏 ({vs.X},{vs.Y}) {vs.Width}×{vs.Height}，{PointerOwner()}";
        }

        // Told either way, and told as a movement: this point has already cleared whichever question applies
        // — a step of five pixels while the cursor is showing, or any distance past the hidden anchor while it
        // is not — so it is the last thing a hand did as far as this rule is concerned, and the countdown
        // restarts here. The reseed is the better answer when it works, because it carries where the pointer is
        // and not just that it moved; when it cannot translate the position the movement is still news: both
        // clocks have to start from the same instant or 「静止两秒」 is measured from a stamp the rule never got.
        if (!ReseedPointer(moved: true) && _chrome.Moved(Now)) Render();
    }

    /// <summary>
    /// Throws away a parked XAML movement claim, having convicted it: the poll has just established, from the
    /// OS's own coordinates, that the pointer is exactly where it was — so the event that claimed a step was
    /// synthetic, raised because the tree under a stationary pointer changed rather than because anything
    /// moved.
    /// <para>
    /// 第八报（2026-09-15）的收口动作。计数进 <see cref="_syntheticMoves"/> 并随显示行出门，因为用户问的
    /// 「AyuGram 来消息鼠标就冒出来，其他播放器都不会」只有数字能关卷：外来事件还会来（它们不归这个进程
    /// 管），要证明的是它们每一次都被这里认出来、按在原地。藏匿开始时清账，与 <see cref="_hiddenNoise"/>
    /// 同簿。
    /// </para>
    /// </summary>
    private void DiscardSyntheticClaim()
    {
        if (_xamlClaim is null) return;
        _xamlClaim = null;
        _syntheticMoves++;
    }

    /// <summary>
    /// The picture changed size — 全屏, 最大化, a dragged window edge — and the pointer said nothing about
    /// it. 「全屏时最下方的进度条不会自动隐藏」 was this: entering fullscreen from the bar's own ⛶ leaves the
    /// pointer recorded as resting on a control, and in fullscreen there is no longer anywhere for it to
    /// leave to.
    /// </summary>
    private void OnRootResized(object sender, SizeChangedEventArgs e)
    {
        // A movement, deliberately: every control has just been put somewhere else under a pointer that never
        // moved, so 「the pointer has not been still」 is the true answer and the idle clock has to start again.
        if (!Attached) return;

        // A resize that ends a hide is its own kind of wake — say so before the reseed renders.
        if (_cursorHidden) _woke = "窗口改变了大小";
        ReseedPointer(moved: true);
    }

    // ---- 两处竖直间距 -------------------------------------------------------------

    /// <summary>
    /// The transport bar was laid out, so what stands above it may have to move. Raised the first time the
    /// bar comes up, and again whenever its height changes — a dragged window edge, a font the settings
    /// window changed, a row that grew.
    /// </summary>
    private void OnBarResized(object sender, SizeChangedEventArgs e) => PlaceOverlays();

    /// <summary>
    /// Places the two overlays whose position is really a statement about a different overlay: 统计 sits
    /// under the title strip, and the 跳过 button sits over the transport bar.
    /// <para>
    /// Both were written down instead. The panel's 104 was the strip's declared 96 plus a gap, restated in a
    /// second file where nothing would notice the two drifting apart; the button's 148 was a guess at a
    /// height nothing declares at all — the bar has no <c>Height</c>, it is two auto rows and its padding,
    /// so it is whatever its buttons and its fonts come to. That number was never checked against the bar
    /// and had no way to follow it: one larger font in the transport row and the 跳过 offer would have been
    /// drawn across the seek slider, which is the one control it must never cover.
    /// </para>
    /// <para>
    /// Only the vertical halves are computed. The left and right insets are the overlays' own — how far from
    /// the edge of the picture they sit is a matter of taste rather than of clearance — so they stay in the
    /// markup where they can be seen, and are read back out of the margin here rather than restated.
    /// </para>
    /// <para>
    /// Each margin is written only when it has actually changed, which matters more than it looks: this is
    /// called every time the bar's size changes, and the bar changes size every time the chrome goes down or
    /// comes up. A margin assigned again with the number it already held would invalidate layout under a
    /// pointer that has not moved, and WinUI answers a change to the tree beneath the pointer with a
    /// <c>PointerMoved</c> — which the reveal rule can only read as a hand on the mouse, restarting the two
    /// seconds before the cursor hides, every time, forever.
    /// </para>
    /// </summary>
    private void PlaceOverlays()
    {
        // Declared, so it answers even while the strip is collapsed, which it is whenever the pointer has
        // been still — and 统计 is pinned open by a button and outlives the strip on purpose.
        Nudge(StatsPanel, new Thickness(StatsPanel.Margin.Left, TitleStrip.Height + OverlayGap, 0, 0));

        Nudge(SkipButton, new Thickness(0, 0, SkipButton.Margin.Right, BarHeight() + OverlayGap));

        static void Nudge(FrameworkElement element, Thickness margin)
        {
            if (element.Margin != margin) element.Margin = margin;
        }
    }

    /// <summary>
    /// How tall the transport bar is, from the last time it was arranged if it has been and from a measure
    /// on the spot if it has not.
    /// <para>
    /// The fallback is not hypothetical bookkeeping: this is called before the first frame of a playback, at
    /// a point where the bar has been made visible but nothing has been laid out yet, so
    /// <c>ActualHeight</c> is still zero. A collapsed or unarranged element measures nothing, hence the flip
    /// — the same trick the probes use, and safe for the same reason: it is put back inside this call, so no
    /// frame is composed with the bar up.
    /// </para>
    /// </summary>
    private double BarHeight()
    {
        if (Bar.ActualHeight > 0) return _barHeight = Bar.ActualHeight;
        if (_barHeight > 0) return _barHeight;

        var was = Bar.Visibility;
        Bar.Visibility = Visibility.Visible;
        Bar.Measure(new Size(Root.ActualWidth > 0 ? Root.ActualWidth : double.PositiveInfinity, double.PositiveInfinity));
        Bar.Visibility = was;

        return _barHeight = Bar.DesiredSize.Height;
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
    /// <para>
    /// <paramref name="origin"/> is the element the tap actually landed on, and it is the half geometry
    /// cannot answer: 「跳过片头后会自动暂停」 (2026-09-04). The 跳过 button takes itself off screen inside
    /// its own <c>Click</c>, which runs before the <c>Tapped</c> that follows the same release — so by the
    /// time this is asked, there is no button under the pointer any more, the hit test says 「picture」, and
    /// the click that skipped the opening pauses the film 150 ms later. Any control that hides or moves
    /// itself when pressed would do the same; what the tap hit does not change underneath us.
    /// </para>
    /// </summary>
    private bool TapOnPicture(Point point, object? origin = null) =>
        !FromChrome(origin) && PartAt(point) == ChromePart.None
        && !Covers(StatsPanel, point) && !Covers(Cover, point);

    /// <summary>
    /// Whether the tap landed inside one of the overlays rather than on the film. Walks up from the element
    /// the framework hit, and stops at <see cref="Root"/> — reaching the picture without meeting an overlay
    /// is the answer 「no」. Collapsing an element does not take it out of the visual tree, which is exactly
    /// why this survives the case <see cref="TapOnPicture"/> describes.
    /// <para>
    /// Subtractive on purpose: it can only ever refuse a tap that geometry would have accepted. Deciding
    /// 「the picture」 from the origin instead — anything that is not <c>Root</c> is chrome — would hand the
    /// 点击画面暂停 gesture to whatever element happens to be hit-testable over the film, and losing that
    /// gesture is a worse fault than the one being fixed.
    /// </para>
    /// </summary>
    private bool FromChrome(object? origin)
    {
        for (var node = origin as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, Bar) || ReferenceEquals(node, TitleStrip) || ReferenceEquals(node, Rail)
                || ReferenceEquals(node, SkipButton) || ReferenceEquals(node, StatsPanel)
                || ReferenceEquals(node, Cover))
                return true;

            if (ReferenceEquals(node, Root)) return false;
        }

        return false;
    }

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
    /// 工具用（<c>--show-osd [pinned|paused|playing]</c>）：把播放浮层摆到屏上留着，好让 <c>tools/shot.ps1</c>
    /// 拍一张。一个字节的视频都不播 —— <see cref="Render"/> 只读显隐规则，不问在放什么。
    /// <para>
    /// 不是探针，所以不住 <c>SelfCheck.*</c> 里。它存在的理由和 <c>--show-menu</c> 一模一样：这几层只有指针走到
    /// 对应的位置才浮上来，而这台机器上注不进鼠标事件，于是浮层上任何看得见的改动本来都拍不到照 —— 而浮层正是
    /// 用户最常盯着看的一片。
    /// </para>
    /// <para>
    /// 用 <see cref="ChromeHold"/> 钉住而不是靠 <c>WakeFully</c>：后者只撑一个宽限期，一秒多之后浮层自己就收了，
    /// 而拍照要等窗口稳下来。音量条走 <see cref="ChromeReveal.FlashRail"/> —— 那是滚轮调音量走的同一条路，一律
    /// 给足强度。徽标停在满亮上而不是让它跑那 0.2 秒的动画：0.2 秒里拍不到任何一帧。
    /// </para>
    /// </summary>
    internal void ShowChromeForShot(string? state)
    {
        Visibility = Visibility.Visible;
        UpdateLayout();

        if (string.Equals(state, "pinned", StringComparison.OrdinalIgnoreCase)) SetPinned(true);

        if (state is "paused" or "playing")
        {
            PulseShape.Data = state == "paused" ? _pauseArt : _playArt;
            PulseBadge.Visibility = Visibility.Visible;
            PulseBadge.Opacity = 1;
        }

        Hold(true, ChromeHold.Shot);
        _chrome.WakeFully(Now);
        _chrome.FlashRail(Now);
        Render();
        UpdateLayout();
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

        // And the one that actually does it while the pointer is over the picture. The four levers around this
        // line — SetCursor on this queue, ShowCursor's counter, the window classes, the nudge that makes the OS
        // work the shape out again — are all Win32, and Win32 is not who draws the pointer over XAML content:
        // the framework's own input pipeline is. A real film's log settles it. One hide lasted 2 minutes 5
        // seconds; across some twelve hundred ticks not one found a shape put back on this queue (_shapeBack
        // stayed at 0), the show count was −1, five window classes were blank, the nudge had been sent — and
        // GetCursorInfo answered 「system arrow, 0x10003」 the whole way through. Eighteen hides in that one
        // film, all identical.
        //
        // One clause of that has to be read with 2026-09-05 in mind: 「the nudge had been sent」 was a call that
        // produced no message whatever, so the four Win32 levers were never actually asked in that film. They
        // are not exonerated by it and they are not convicted by it — see Native.NudgeCursorState. And that
        // nudge itself was retired on 2026-09-14: it was real input, it woke the player out of its own hide,
        // and nothing injects it any more.
        //
        // ProtectedCursor is the framework's own lever and the only one it consults. Null hands the shape back
        // to it, which is an ordinary arrow. The four Win32 levers stay: they cover the windows the island is
        // not — the host window's non-client area, libmpv's child, XAML's own popup windows.
        //
        // NOT YET SHOWN TO REACH THE SCREEN, and that has to be written down rather than assumed. Measured
        // 2026-09-03 with the pointer parked on this window by SetCursorPos from another process: everything
        // this process can say about the cursor was set — this queue holding the blank shape, the show count at
        // −1, five window classes blanked, the nudge going out ten times a second, and this line — and
        // GetCursorInfo went on answering 「system arrow 0x10003」 for five seconds. The control experiment says
        // more: replacing the blank with a plainly visible Wait cursor did not put an hourglass on screen
        // either, so in this hosting model ProtectedCursor changed nothing observable at all.
        //
        // Kept, for three reasons. It is the framework's own documented lever and the only one left untried; it
        // costs one assignment per hide and is fully guarded (null when the wrapping fails); and every
        // measurement above used a pointer warped by SetCursorPos, which does not own the cursor the way a hand
        // does — the only reading taken with a real hand on a real film is the log line this writes, so the
        // next report will say which layer is still holding the arrow instead of guessing again.
        Root.Cursor = hidden ? _window?.BlankInputCursor : null;

        // And the same thing said to libmpv about its own window, which no call of ours can reach: see
        // PlayerViewModel.ShowMpvCursor.
        ViewModel.ShowMpvCursor(!hidden);

        // Kept because it is the OS's own answer about the one thing here that leaves the process: the new
        // display count, which has to be below zero for a hidden cursor and back at zero for a shown one.
        // Read by the self-check, which otherwise could only ask this file what it believes.
        // 第九报（2026-09-15）：show 侧从「拉一格」改成「拉到非负」（用户原话「你直接抄这些开源项目吧」，
        // HC-Player 的 while (ShowCursor(TRUE) < 0) {}）——藏匿期间计数可能被压得很深（每拍重锁 + 历史残留），
        // 单次 ShowCursor(TRUE) 只抬一格，残负会把下一次显示整个吞掉。hide 侧维持一次即可：HostWindow 每拍
        // 的重锁（SuppressCursorDisplay）会兜住任何抬回。
        if (hidden)
        {
            _cursorCount = Native.ShowCursor(false);
        }
        else
        {
            while ((_cursorCount = Native.ShowCursor(true)) < 0) { }
        }

        if (hidden) _shapeBack = 0;

        // And say the policy again, without asking the OS for anything. Every 「no cursor」 above is an answer —
        // this queue's shape, the class cursors, mpv's own setting, and above all the transparent
        // ProtectedCursor on the line above, which is the only one of them that reaches the pixels a pointer
        // over XAML content is on. WinUI reads that property while it is handling pointer input, so a value
        // assigned during stillness is a value nobody has read: the arrow the last real movement worked out
        // stays on the screen, which is 「静止超过两秒后鼠标指针还是不会自动隐藏」 from a player whose own
        // readings all say hidden.
        //
        // This used to be a one-pixel injection through the real input queue, and it is not any more — see
        // Nudge for what that cost and what replaced it. Restated for the first three ticks of a hide, because
        // the first assignment can land before the framework pushes its own value down and no reading here can
        // tell that case from a successful one.
        // 第九报（2026-09-15）：藏点的屏幕绝对坐标。用户报「屏幕一全屏播放时，屏幕二的 AyuGram 收到
        // 消息会唤起屏幕一静止隐藏的鼠标指针」，三天日志里那批唤醒都是同一签名：一拍之内横跳整整 60px、
        // 纵向恒 0，物理注入不会三天都恰好 60px。藏匿行从此带上藏点绝对坐标，与唤醒行的读数、虚拟屏
        // 度量三方对账——唤醒那一刻藏点没变而虚拟屏矩形变了＝桌面重排把坐标搬走了（唤醒是误报）；
        // 矩形没变＝真有进程在注入位移，顺着指针下的窗口与独立监视器去抓注入者。
        string hideAnchor = "？";

        if (hidden)
        {
            _nudgesThisHide = 0;
            _hiddenNoise = 0;
            _syntheticMoves = 0;
            _xamlClaim = null;
            _woke = "未标注的显示路径（见到此串即有路漏标）";

            // 第九报（2026-09-15）：外部画回的基线。HostWindow 的 GlobalShapeChanges/ExternalShapeRestores
            // 是跨藏匿期的累计值，本页只关心「这一次藏匿里发生了几次」——藏匿开始时钉下基线，独立日志
            // 与显示行都拿当前值减它。
            _globalBase = _window?.GlobalShapeChanges ?? 0;
            _externalBase = _window?.ExternalShapeRestores ?? 0;

            // Where the pointer was at the moment of hiding, which is the point every report during the hide is
            // judged against — MPC-HC's PointEqualsImprecise, and the reason a desk's one-pixel rattle can no
            // longer walk the reference along with it. Both anchors are pinned here: the rule's, which the poll
            // asks about, and this page's own, which the XAML filter measures steps against.
            if (CursorScreen(out var hiding))
            {
                _chrome.AnchorHidden(hiding.X, hiding.Y, Now);
                _polled = hiding;
                _polledKnown = true;
                hideAnchor = $"{hiding.X},{hiding.Y}";
            }

            if (CursorPoint(out var onPicture)) _pointerAt = onPicture;

            Nudge();
        }
        else
        {
            // Shown again: the pointer is back to being followed rather than held to a point, so the anchor
            // stops meaning anything and the poll's own reading becomes the reference once more.
            _chrome.ReleaseHiddenAnchor();
        }

        // Written to the log because this is the one thing in the player a probe can only ask about under
        // conditions it made up, and 「没有变化」 three times over is what asking the wrong conditions costs.
        // Two transitions a film, so the cost is nothing.
        // <para>
        // The hide line carries who owns the pixels, because the last round proved the rule right and the
        // screen wrong: it fired on time, this thread's queue went to 「no shape」, and the arrow stayed. A
        // shape set here only reaches the screen while the pointer is over a window this thread owns, and
        // during playback the window under the pointer may be the island's or libmpv's rather than ours —
        // which is a fact about a real film, unavailable to any probe. The show line carries the count of
        // ticks that found a shape back while we still wanted none, which is the other way this can fail,
        // and now also who moved the pointer: 「鼠标隐藏了一会又会自动跑出来」 was reported against a log that
        // said the cursor was back and never said what had brought it, and one number — tens of pixels is a
        // hand, one is a leak — is the whole difference.
        // </para>
        Log.Debug(Category, hidden
            ? $"鼠标藏起来了：静止 {Now - _pointerMovedAt}ms，其间空事件 {_stillMoves} 次，线程形状"
              + $"{(_window?.CursorShapeGone == true ? "无" : "还在")}，计数 {_cursorCount}"
              + $"，藏点屏幕 {hideAnchor}"
              + $"，框架光标{(Root.Cursor is null ? "＝默认（没换上）" : "＝透明")}，{PointerOwner()}"
              + $"，{PointerElements()}"
            : $"鼠标又显示了：{_woke}；轮询问出的移动共 {_polledMoves} 次、XAML 事件 {_pointerMoves} 次，计数 {_cursorCount}"
              + $"，藏着期间重申了 {_nudgesThisHide} 次、有 {_shapeBack} 拍发现形状又被放回来了"
              + $"，挡回去 {_hiddenNoise} 次（阈值以下的抖动）"
              + $"，判掉合成事件 {_syntheticMoves} 次（指针没动而 XAML 声称动了）"
              + $"，桌面光标失同步刷新共 {_window?.CursorDisplayRefreshes ?? 0} 次"
              // 第九报（2026-09-15）：本藏匿期全局快照的变化次数与判定为外部画回的次数——「出现≠移动」
              // 盲区的对账读数。两者都只取证（Nudge 压回成环已撤）。
              + $"，全局形状变化 {(_window?.GlobalShapeChanges ?? 0) - _globalBase} 次"
              + $"，其中判外部画回 {(_window?.ExternalShapeRestores ?? 0) - _externalBase} 次"
              // 第九报（2026-09-15）：负计数锁被抬回又被压回的次数（HC-Player 式第五杠杆）。>0 ＝藏匿期里
              // 有谁把队列计数抬回过非负——画回还能亮起来的「灯是谁开的」就有数了。
              + $"，负计数锁被抬回又压回 {_window?.CursorSuppressRestates ?? 0} 次");
    }

    /// <summary>Maintains the page cursor and repairs a stale desktop arrow through the host.</summary>
    private void Nudge()
    {
        _nudgesThisHide++;
        _cursorNudges++;

        _window?.KeepCursorHidden();

        // And the framework's own lever, said again with the rest of them. This is the assignment that
        // actually covers a pointer over XAML content, and it is the one WinUI is free to overwrite the next
        // time it runs its 「who is the pointer over, what shape does he want」 round — so it is the one that
        // most needs repeating. Guarded on identity: assigning the same value is already a no-op in the
        // framework's own setter, but the comparison keeps the tick free of a property write it does not need.
        var blank = _window?.BlankInputCursor;
        if (!ReferenceEquals(Root.Cursor, blank)) Root.Cursor = blank;
    }

    /// <summary>
    /// 框架此刻认为指针压在哪几个元素上，最上面那个写在最前。
    /// <para>
    /// 「透明光标设在 <c>Root</c> 上、屏上却还是箭头」的头一个嫌疑就是<b>框架命中的根本不是 <c>Root</c></b>：压在
    /// 它上面还有一个可命中的元素（换集时那块不透明的遮挡、钉住的浮层、一颗按钮），那个元素的
    /// <c>ProtectedCursor</c> 是空的，于是框架照旧画它自己的箭头。这一句就是为了让下一次报告能直接指出是哪一个，
    /// 而不用再猜一轮。
    /// </para>
    /// <para>
    /// 只写日志，不判任何东西：这一读数要一只真手压在真片子上才算数，而那种时刻自检到不了 —— 这台机器上注不进
    /// 真实指针输入（自检报告里那句「真实输入注不进」）。
    /// </para>
    /// </summary>
    private string PointerElements()
    {
        if (!CursorPoint(out var point)) return "问不出指针压在哪个元素上";

        try
        {
            var hits = VisualTreeHelper.FindElementsInHostCoordinates(point, Root)
                .OfType<FrameworkElement>()
                .Take(4)
                .Select(element => element.Name is { Length: > 0 } name
                    ? $"{element.GetType().Name}#{name}"
                    : element.GetType().Name)
                .ToList();

            return hits.Count == 0
                ? "框架说指针不压在任何元素上"
                : $"框架命中 {string.Join(" ← ", hits)}";
        }
        catch (Exception error)
        {
            return $"命中测试问不出（{Failure.Describe(error)}）";
        }
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
        Search = 8,

        /// <summary>
        /// 工具用：<c>--show-osd</c> 把浮层钉在屏上好拍照。<see cref="ChromeReveal.WakeFully"/> 的宽限期只有一秒多，
        /// 等不到窗口稳下来、更等不到截图脚本按下快门。
        /// </summary>
        Shot = 16
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
        if (!PointerInside())
        {
            // The departure the events can miss gets a name too: 「指针走了」 is a different wake from
            // 「按键」 or 「点击」, and the show line is where that difference has to survive.
            if (_cursorHidden) _woke = "指针离开了画面";
            if (_chrome.PointerLeft(Now)) Render();
        }
        else if (_chrome.PointerGone)
        {
            // The homecoming the events and the poll both miss. A hand that comes back and stops within
            // five pixels of where it left raises no pointer event at all, and the poll's two early
            // returns (a zero step; one under the threshold against its last accepted reading) both fire
            // before a position ever reaches the rule — so the departure above would stand forever, and
            // 「有时候要点一下暂停再播放鼠标才会自动隐藏」 was the click doing this reseed's job by hand.
            // Not a movement: the pointer is where it is, 位置是位置、动是动, and the settle clock keeps
            // running on its own terms (a report with the cursor showing restamps it anyway, which is the
            // same patience a parked pointer always bought).
            //
            // 第八报（2026-09-15）补的名牌：这条 reseed 展开控件栏时就是一次显示（chrome 出、光标随它
            // 出），22:56:59 两条「未标注的显示路径」正是它和下一行在进退全屏的同一毫秒里留下的。
            if (_cursorHidden) _woke = "指针回到了画面";
            ReseedPointer(moved: false);
        }

        // A held mouse button with chrome on screen is a drag on one of the two sliders — or at least may
        // be — and a drag reports nothing at all while the hand holds still. The reveal rule's patience for
        // a still pointer is finite now, and a slider collapsing under a held thumb would lose the pointer
        // capture with it, so the button's own state stands in for the events that are not coming.
        //
        // 第八报（2026-09-15）补的名牌：这条 reseed 也会展开控件栏（chrome 出、光标随它出），是 22:56:59
        // 「未标注」的另一半。只在光标已藏时挂名——常态（chrome 在屏、光标本就显示）不给 _woke 塞旧账。
        if (Native.MouseButtonDown() && _chrome.State.Any)
        {
            if (_cursorHidden) _woke = "按住鼠标（滑块拖动）";
            ReseedPointer(moved: true);
        }

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

            // And say the policy again — every tick, for as long as the hide lasts. That is not belt and
            // braces: the framework re-reads ProtectedCursor every time it handles pointer input, and a film
            // produces pointer input continuously, so a policy announced once is a policy that is overwritten
            // at some arbitrary later moment. Three announces and then silence was tried for a few hours on
            // 2026-09-14 and produced 「隐藏后过两三秒又会自动冒出来」 with hides that lasted fifteen seconds,
            // four seconds and one hundred and eighty milliseconds in the same film. See Nudge.
            Nudge();

            // 第九报（2026-09-15）：全局快照的独立日志。用户拔掉鼠标后仍确认「没错哦17-18 秒鼠标出现在了
            // 画面之上」，独立监视器同刻量到指针没动——「出现≠移动」的盲区：外部画出的指针只要静止就
            // 不产生位移事件，线程局部读数（_shapeBack）与轮询同时失明。HostWindow 每拍看全局快照并
            // 与上一拍差分：状态变了记「全局光标状态变了」（纯取证），判定为外部形状压在本窗口记
            // 「指针被外部画出来了」。两层都只取证、不回击——Nudge 压回实测把形状请出来成环（14:03，
            // 用户报「每过一会鼠标就会闪一下，然后消失」，每 ~1.15s 一轮），已撤。都放在 Nudge 之后读，
            // 读到的是本拍最新值；独立成行让取证事件直接对上用户看见的时刻。
            if (_window is { } window)
            {
                if (window.GlobalShapeChanges != _globalSeen)
                {
                    _globalSeen = window.GlobalShapeChanges;
                    Log.Debug(Category, $"全局光标状态变了：{window.LastGlobalChange}"
                        + $"（本藏匿期第 {window.GlobalShapeChanges - _globalBase} 次）");
                }
                if (window.ExternalShapeRestores != _externalSeen)
                {
                    _externalSeen = window.ExternalShapeRestores;
                    Log.Debug(Category, $"指针被外部画出来了：{window.LastExternalShape}"
                        + $"（本藏匿期第 {window.ExternalShapeRestores - _externalBase} 次；只取证）");
                }
            }
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
            var brush = BrushFor("PlayerTickBrush");

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

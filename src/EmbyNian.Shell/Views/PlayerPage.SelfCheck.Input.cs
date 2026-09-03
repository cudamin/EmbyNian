using EmbyNian.Playback;
using EmbyNian.Shell.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 按下去之后会动的那三关：跳过 那个提议和进度条上的章节刻度（<c>ProbeSkipAndChapters</c>）、点画面暂停和点
/// 控制条上的空白各归各的（<c>ProbeTap</c>）、暂停／播放 那一秒的角标（<c>ProbePulse</c>）。
/// <para>
/// 这三关问的都是几何和时序 —— 一个点落在谁身上、一段动画到底跑没跑完 —— 这两样都不是单元测试能替的。
/// 拆成几个文件的缘由见 <c>PlayerPage.SelfCheck.cs</c> 的类注释。
/// </para>
/// </summary>
public sealed partial class PlayerPage
{
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
    /// <para>
    /// While it is laid out, the same pass measures the arithmetic underneath all of those answers:
    /// <see cref="OriginIn"/> against the <c>TransformToVisual</c> it replaced, element by element. See that
    /// method for why the two agree here and what would stop them.
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

        // The origin every hit test above was computed from, checked against WinUI's own answer for it.
        // Covers sums ActualOffset up the tree rather than asking for a GeneralTransform, which is exact
        // only while nothing between the element and Root is scaled or render-transformed — a property of
        // this page's layout, not a law, and one a future overlay could quietly break. So it is measured
        // here rather than asserted in a comment: TransformToVisual is deliberately still used, because it
        // is the thing being compared against.
        var walked = 0;
        var offBy = 0d;
        var skewed = new List<string>();

        foreach (var (name, element) in new (string Name, FrameworkElement Element)[]
                 {
                     ("控制条", Bar),
                     ("标题条", TitleStrip),
                     ("音量条", Rail),
                     ("进度条", SeekTrack),
                     ("跳过", SkipButton),
                     ("统计", StatsPanel),
                     ("切换遮罩", Cover)
                 })
        {
            // Only the ones Covers would really ask about: it short-circuits on anything unmeasured, and a
            // collapsed element's two answers are both about a layout slot it was never given.
            if (element.ActualWidth <= 0 || element.ActualHeight <= 0) continue;

            var mine = OriginIn(element);
            var theirs = element.TransformToVisual(Root).TransformPoint(new Point(0, 0));
            var gap = Math.Max(Math.Abs(mine.X - theirs.X), Math.Abs(mine.Y - theirs.Y));

            walked++;
            offBy = Math.Max(offBy, gap);

            // A twentieth of a pixel: ActualOffset is a float and the transform is doubles, so a layout
            // rounded onto a fractional scale differs in the sixth decimal and nothing else may.
            if (gap > 0.05) skewed.Add($"{name}差 {gap:0.###}");
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
                 && !ChromeShown && Visibility == was
                 && walked >= 4 && skewed.Count == 0;

        return (ok, $"{width:0}×{height:0} 逻辑像素：画面中央→{(centre ? "暂停" : "不暂停")}"
                    + $"；浮层五处控件{(missed.Count == 0 ? "都不暂停" : $"有 {string.Join('、', missed)} 会误触")}"
                    + $"；浮层收起后底边→{(uncovered ? "暂停" : "不暂停")}"
                    + $"；{walked} 处控件的原点与 TransformToVisual "
                    + (skewed.Count == 0 ? $"一致（最大差 {offBy:0.###} 像素）" : $"不一致：{string.Join('、', skewed)}"));
    }

    /// <summary>The centre of <paramref name="element"/> in <c>Root</c>'s own coordinates.</summary>
    private Point Middle(FrameworkElement element)
    {
        var origin = element.TransformToVisual(Root).TransformPoint(new Point(0, 0));
        return new Point(origin.X + element.ActualWidth / 2, origin.Y + element.ActualHeight / 2);
    }

    /// <summary>
    /// 暂停/播放 角标: that the fifth of a second of acknowledgement really runs — 「暂停后显示一秒暂停图标就行
    /// （开启播放也弄个一秒的动画）」, shortened by 「把暂和开始的动画改为 0.2 秒」 — and draws the right shape
    /// for what just happened.
    /// <para>
    /// The animation is begun for real, which is the whole point of the probe. A
    /// <c>Storyboard.TargetName</c> is resolved against a namescope at <c>Begin</c> and not before, so a
    /// badge renamed, or moved inside a template, throws 「Cannot resolve TargetName」 the first time somebody
    /// presses space — mid-film, on the frame they just paused, which is the least forgiving moment this app
    /// has. A build cannot see it and neither can a unit test: the storyboard is markup and the names it
    /// reaches for only exist once the page has been loaded.
    /// </para>
    /// <para>
    /// Both shapes are asked for, in the order a pause and a resume produce them, because they are
    /// deliberately the opposite way round from the transport button's: a button says what pressing it will
    /// do, and this says what just happened. A crossed pair would tell every pause it had resumed, and would
    /// look entirely deliberate.
    /// </para>
    /// <para>
    /// The two size readings are a debt this design owes: the shapes live in a <c>Canvas</c>, which does not
    /// measure its children, so a Width or Height dropped from the markup measures the whole badge as 0×0 —
    /// nothing on screen at all, with every other reading here still perfectly correct.
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) ProbePulse()
    {
        // Laid out for the duration and put back, as ProbeTap does: a collapsed page has no namescope
        // trouble to run into because nothing it names has been realised.
        var was = Visibility;
        var wasMuted = _pulseMutedAt;

        // A double tap in the last fraction of a second silences the badge on purpose, and this probe asks the
        // badge to speak. Cleared rather than worked around, and put back on the way out.
        _pulseMutedAt = null;
        Visibility = Visibility.Visible;
        UpdateLayout();

        var seen = new List<string>();
        var wrong = new List<string>();

        void Beat(string what, bool paused, (Geometry Shape, Geometry Rim) want)
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

            UpdateLayout();

            var shown = PulseBadge.Visibility == Visibility.Visible;
            var running = _pulse.GetCurrentState() is ClockState.Active or ClockState.Filling;
            var right = ReferenceEquals(PulseShape.Data, want.Shape);

            // 「不要黑色的圆形边框，只要白色的三角形」: the plate is gone, so what is asserted now is that
            // nothing draws one — no fill, no ring — and that the shape is the size the user asked for
            // rather than the 40 it was inside the circle, with the rim behind it carrying the same geometry.
            var bare = PulseBadge.Background is null
                       && PulseBadge.BorderBrush is null
                       && PulseBadge.BorderThickness.Left == 0
                       && PulseBadge.BorderThickness.Top == 0
                       && PulseBadge.BorderThickness.Right == 0
                       && PulseBadge.BorderThickness.Bottom == 0;

            var box = PulseShape.Data?.Bounds ?? default;
            var big = box.Height >= 90
                      && PulseRim.StrokeThickness > PulseShape.StrokeThickness
                      && PulseBadge.ActualWidth >= 130
                      && PulseBadge.ActualHeight >= 130;
            var rimmed = ReferenceEquals(PulseRim.Data, want.Rim)
                         && PulseRim.Data?.Bounds == PulseShape.Data?.Bounds;

            seen.Add($"{what}→{(shown ? "出角标" : "没出角标")}"
                     + $"，{(running ? "动画在跑" : "动画没跑")}"
                     + $"，形状{(right ? "对" : "不对")}"
                     + $"，{(bare ? "没有底板" : "还有底板")}"
                     + $"，外框 {box.Width:0}×{box.Height:0}"
                     + $"／描边 {PulseShape.StrokeThickness:0} 与 {PulseRim.StrokeThickness:0}"
                     + $"／徽标 {PulseBadge.ActualWidth:0}×{PulseBadge.ActualHeight:0}"
                     + (rimmed ? "" : "（描边形状不一样）"));

            if (!shown || !running || !right || !bare || !big || !rimmed) wrong.Add(what);
        }

        Beat("暂停", paused: true, _pauseArt);
        Beat("恢复", paused: false, _playArt);

        // Back to how a player nobody has paused looks: no clock in flight, and nothing drawn over whatever
        // page the shell is really on.
        HidePulse();
        _pulseMutedAt = wasMuted;
        Visibility = was;
        UpdateLayout();

        var ok = wrong.Count == 0 && PulseBadge.Visibility == Visibility.Collapsed && Visibility == was;

        return (ok, string.Join("；", seen)
                    + (wrong.Count == 0 ? "；结束后收起" : $"；不符：{string.Join('、', wrong)}"));
    }

    /// <summary>
    /// 双击不触发暂停: 「双击画面全屏的时候会触发暂停和开始」, driven through the page's own three methods.
    /// <para>
    /// The net playback state has been right since August — the double tap put pause back where it found it —
    /// so what was wrong was everything around it: the badge flashed 暂停 and then 播放 across the middle of
    /// the picture, and mpv really did stop and start. The fix holds the tap back instead, and that turns one
    /// invisible risk into two: 「a single click no longer pauses at all」 now rests on a timer being hooked up,
    /// and 「the badge stays quiet afterwards」 on one guard at the top of <see cref="Pulse"/>. Neither the
    /// build, nor a unit test, nor any other probe here can see either of them.
    /// </para>
    /// <para>
    /// Nothing is faked. <c>TappedRoutedEventArgs</c> cannot be constructed, which is exactly why the handlers
    /// were split into <see cref="TapPicture"/>, <see cref="OnTapHoldElapsed"/> and
    /// <see cref="SecondTapOnPicture"/> — the last of which leaves fullscreen to its caller so this can walk
    /// the gesture without moving the window. Nothing reaches mpv either: with no file open
    /// <c>PlaybackService</c> drops a property write on the floor.
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) ProbeTapGesture()
    {
        if (!Attached) return (false, "播放层未接线");

        var was = Visibility;
        var wasMuted = _pulseMutedAt;
        var wasBadge = PulseBadge.Visibility;

        Visibility = Visibility.Visible;
        UpdateLayout();

        var report = new List<string>();
        var wrong = new List<string>();

        void Want(string what, bool ok)
        {
            if (!ok) wrong.Add(what);
        }

        // ① 接线. A tap has to arm the timer and hold the pause — a timer that was never hooked up shows up
        // as 「点画面不再暂停了」, which is the one regression this change could introduce.
        _pulseMutedAt = null;
        TapPicture();
        Want("单击攥住了", _tapHold.IsEnabled && _tap.Pending && !_tap.Issued);
        report.Add($"攥住 {_tapHold.Interval.TotalMilliseconds:0} 毫秒（系统双击 {Native.GetDoubleClickTime()}）");

        // ② 到期. Called the way PlayerPage.SelfCheck.Cursor calls OnTick — the timer's own handler, by hand.
        OnTapHoldElapsed(this, EventArgs.Empty);
        Want("攥够了才下发", !_tapHold.IsEnabled && _tap.Issued);

        // ③ 快的双击: the tap is still held, so there is nothing to undo and nothing was ever issued.
        TapPicture();
        SecondTapOnPicture();
        Want("快双击一次暂停都不发", !_tapHold.IsEnabled && !_tap.Pending && !_tap.Issued);
        report.Add("快双击→一次暂停都不发");

        // ④ 徽标真的闭嘴. Four steps, because the guard is a span rather than a counter and a counter that
        // never came down would eat the badge for every real pause afterwards.
        _pulseMutedAt = null;
        Pulse(true);
        var spoke = PulseBadge.Visibility == Visibility.Visible;

        SecondTapOnPicture();
        var hushed = PulseBadge.Visibility == Visibility.Collapsed;

        Pulse(true);
        var stillHushed = PulseBadge.Visibility == Visibility.Collapsed;

        _pulseMutedAt = null;
        Pulse(true);
        var speaksAgain = PulseBadge.Visibility == Visibility.Visible;

        Want("徽标本来出得来", spoke);
        Want("双击当场把徽标收起来", hushed);
        Want("静音期内不再出徽标", stillHushed);
        Want("静音期过了又出得来", speaksAgain);
        report.Add($"慢双击→撤回并静音（{PictureTap.PulseMuteMilliseconds} 毫秒），徽标当场收起、期内不再出、期后又出得来");

        // ⑤ 点在控件上那一下要作废，否则接着一次落在控制条上的双击会拿它去「撤回」，把正在放的片子停掉。
        TapPicture();
        DropTapHold();
        Want("落在控件上的那一下作废", !_tapHold.IsEnabled && !_tap.Pending && !_tap.Issued);
        report.Add("点在控件上→作废");

        DropTapHold();
        HidePulse();
        _pulseMutedAt = wasMuted;
        PulseBadge.Visibility = wasBadge;
        Visibility = was;
        UpdateLayout();

        Want("跑完复位了", !_tapHold.IsEnabled && !_tap.Pending && !_tap.Issued
            && PulseBadge.Visibility == wasBadge && Visibility == was);

        return (wrong.Count == 0,
            string.Join("；", report) + (wrong.Count == 0 ? "；复位正常" : $"；不符：{string.Join('、', wrong)}"));
    }
}

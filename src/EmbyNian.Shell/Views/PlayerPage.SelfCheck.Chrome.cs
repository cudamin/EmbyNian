using EmbyNian.Playback;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 管「什么时候看得见」的那两关：<c>ProbeReveal</c> 把显隐规则按一只指针的走法推一遍，<c>ProbeRailFade</c> 问
/// 右边那条音量条的淡入淡出。
/// <para>
/// 规则本身是 Core 的、在那儿由单元测试钉着；这两关钉的是这一页把一个状态铺到三个 <c>Visibility</c> 上的那段
/// 接线 —— 把 <c>Bar</c> 接到轨道那一支上的写法编译得过、读起来也对，直到有人按了播放。拆成几个文件的缘由见
/// <c>PlayerPage.SelfCheck.cs</c> 的类注释。
/// </para>
/// </summary>
public sealed partial class PlayerPage
{
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
}

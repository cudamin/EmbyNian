using EmbyNian.Configuration;
using EmbyNian.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 管「什么时候看得见」和「看得见的时候摆在哪」：<c>ProbeReveal</c> 把显隐规则按一只指针的走法推一遍，
/// <c>ProbeRailFade</c> 问右边那条音量条的淡入淡出，<c>ProbeClearance</c> 量两条浮层跟它们要让开的那两条带
/// 之间的距离。
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

            // 三条边现在都分级淡入（用户令 2026-09-27「参独占模式……」），所以每一样的读数都带上它此刻的浓度，
            // 不再只报「在/不在」——「越明显」这件事只有把 Opacity 读出来才看得见。
            var up = new List<string>(3);
            if (isBar) up.Add($"进度条 {TransportRow.Opacity:P0}");
            if (isTitle) up.Add($"标题栏 {TitleStrip.Opacity:P0}");
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

            // 标题条与按钮行同理：这两关里凡是「出现」的都在强度大于零的位置（底部近处、停在条上、拖动钉住），
            // 一块看不见却还能点的条子会吞掉画面上的点击，标题条里更压着窗口按钮。
            if (isBar && !TransportRow.IsHitTestVisible) wrong.Add($"{what}的按钮行看得见却点不了");
            if (isTitle && !TitleStrip.IsHitTestVisible) wrong.Add($"{what}的标题条看得见却点不了");

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

        // 这一档是 2026-09-28 与 09-29 两条用户令合起来的样子，**两半各认各的**：
        //   · 控件（09-28）：「当鼠标停留在对应控件的渐变触发位置时，不要自动隐藏这些控件」—— 只认位置，
        //     从前静止 650ms / 停在控件上 2000ms 就收的那套已经删掉，指针撂在唤出带里多久都还在。
        //   · 光标（09-29）：「只有鼠标停在控件，进度条和上方的按钮还有音量条上的时候才不隐藏鼠标，触发
        //     渐变的时候不隐藏控件，但是要隐藏鼠标」—— 只认**本体**（ChromePart 那四处命中），唤出带里照走。
        // 三个位置都要量：带里（控件留、光标走）、死区（都收）、本体上（都不动）。
        Settle();
        var still = ++clock;
        _chrome.Pointer(height - 10, height, ChromePart.None, railNear: -1, still);
        Sample("指针停在底部唤出带里", bar: true, title: false, rail: false, cursorHidden: false);

        clock = still + (ChromeReveal.CursorIdleMilliseconds * 4);
        _chrome.Tick(clock);
        Sample("在唤出带里停四秒", bar: true, title: false, rail: false, cursorHidden: true);

        // 回到死区：控件当场收（位置说了算）。**光标在这一记上会回来** —— 指针横越画面是一次真移动，
        // 空闲钟从这一记重数，所以此刻是「控件已收、光标还在」；它再静止一格空闲钟才走，见下一句。
        // （早先这里写的是 cursorHidden: true，那是把「藏匿期不许被自己这一记唤醒」的老规矩用到了
        // 显示期上 —— 09-29 起光标只认本体，带里的那一秒已经走过了，回来是一记新手。）
        _chrome.Pointer(height / 2, height, ChromePart.None, railNear: -1, ++clock);
        Sample("指针回到画面中间", bar: false, title: false, rail: false, cursorHidden: false);

        clock += ChromeReveal.CursorIdleMilliseconds + 1;
        _chrome.Tick(clock);
        Sample($"画面中间静止 {ChromeReveal.CursorIdleMilliseconds}ms 后",
            bar: false, title: false, rail: false, cursorHidden: true);

        // 压在进度条本体上（那一段底部带里最实的位置）与音量条本体上：光标同样不许走 —— 这就是 09-29
        // 那半句话的正题（`ChromePart.Bar` / `ChromePart.Volume` 就是「本体」）。
        _chrome.Pointer(height - 10, height, ChromePart.Bar, railNear: -1, ++clock);
        Sample("压在进度条上", bar: true, title: false, rail: false, cursorHidden: false);

        clock += ChromeReveal.CursorIdleMilliseconds * 4;
        _chrome.Tick(clock);
        Sample("压在进度条上四秒", bar: true, title: false, rail: false, cursorHidden: false);

        _chrome.Pointer(height / 2, height, ChromePart.Volume, railNear: 1, ++clock);
        Sample("压在音量条上", bar: false, title: false, rail: true, cursorHidden: false);

        clock += ChromeReveal.CursorIdleMilliseconds * 4;
        _chrome.Tick(clock);
        Sample("压在音量条上四秒", bar: false, title: false, rail: true, cursorHidden: false);

        // 拖动标题移动窗口: 「在播放页面中，当用户长按标题并拖动播放窗口时，拖动过程中不要显示进度条和音量条」
        // (2026-09-22). 走页面自己的 Hold 而不是直接拧规则：这一关要钉的正是「拖动这个理由接上规则了没有」，
        // 只拧规则的话接线断了它照样绿。**先把指针报回底部带上**（前几步把它停在音量条上了）：位置是在要
        // 进度条，而拖动不许给 —— 松开那一拍要读的也正是这个位置。
        //
        // 把这套合成时钟传进 Hold（它默认按真的 Environment.TickCount64 落账）：这一关早把空闲窗口与
        // 宽限期快进了好几秒，若 Hold 仍按真 Now 记这一记活动，松手那一拍规则会拿「合成的现在」减「真的
        // 刚才」算出好几秒空闲，光标当场被收 —— 松开那一样就永远读不到「交回给指针」。
        _chrome.Pointer(height - 10, height, ChromePart.None, railNear: -1, ++clock);
        Hold(true, ChromeHold.Drag, ++clock);
        Sample("拖动标题移动窗口", bar: false, title: true, rail: false, cursorHidden: false);

        // 松开：两把一起放，照旧交回给指针的位置。松手那一记（SetWindowDrag/SetHold 放开时重盖空闲钟）就在
        // 合成时钟的此刻，指针仍报在底部带上 —— 直接读就是位置自己的答案（进度条在、光标回来）。不再补 Settle：
        // 一个 Settle 要走满 SettleMilliseconds（2201ms），比光标那一秒还长，读到的会是「已经藏了」。
        Hold(false, ChromeHold.Drag, ++clock);
        Sample("松开标题之后", bar: true, title: false, rail: false, cursorHidden: false);

        // 状态推送: the one thing that arrives at this rate while a film is actually playing, and the one
        // thing this probe never used to drive. mpv publishes four or more snapshots a second and the page
        // hands every one of them to the loading latch, which used to read 「not loading」 as activity — so
        // the idle clock was restamped four times a second and nothing ever expired:
        // 「鼠标指针还是不会自动隐藏」. 2026-09-28 起这条钟只量光标，所以这一拍挪到画面中间量它：
        // 指针在死区里，每 250ms 推一份状态，一秒之后光标仍然必须走。Driven at the real cadence rather
        // than asserted about, because the arithmetic was never the part that was wrong.
        var pushing = ++clock;
        _chrome.Pointer(height / 2, height, ChromePart.None, railNear: -1, pushing);

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
    /// 「加大音量条的尺寸，显示方式改为淡入淡出，鼠标指针越接近右边的中心显示越明显」 plus the two things
    /// 「音量条的位置是歪的，还有音量条不需要边框和上方的数字」 asked for, checked as five separate claims
    /// because they fail separately: the rail's measured size, the transition that draws it, the strength curve
    /// that decides how strongly, the track sitting in the middle, and the absence of a ring around it.
    /// <para>
    /// The last two are here rather than in a unit test because they are measurements of a live visual tree
    /// after the framework has applied its own template: the offset that made the rail look crooked is
    /// WinUI's arithmetic over three hard-coded template columns, and the test project reaches Core only.
    /// </para>
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
    /// </para>
    /// <para>
    /// 这一关要过两支画面：<b>够大</b>时量的是强度那一套（下面那些点），<b>小到尺寸线以下</b>时音量条
    /// 一个像素都不该有（用户令 2026-09-23「集成模式下窗口小于一定程度的时候自动隐藏音量条」）。
    /// 两支得在同一趟里量完，因为自检窗口的大小不由探针决定 —— 这一页读「画面多大」的两口是
    /// <see cref="PictureWidth"/>／<see cref="PictureHeight"/>，探针因此能把那个读数临时摆到想量的
    /// 那一档（<see cref="_probePictureSize"/>），生产路径永远是 <c>Root</c> 上的真实读数。
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

        // 强度那一套只在大画面上成立，而自检窗口未必够大 —— 所以先把画面读数按「刚过阈值」摆好，
        // 阈值以下那一支到末尾单独量。动的只是读数，真窗口一个像素都不动。
        var width = Math.Max(Root.ActualWidth, ChromeReveal.RailMinPictureWidth + 1);
        var height = Math.Max(Root.ActualHeight, ChromeReveal.RailMinPictureHeight + 1);
        _probePictureSize = new Size(width, height);
        var clock = Now;
        var report = new List<string>();
        var wrong = new List<string>();

        void Want(string what, bool ok)
        {
            if (!ok) wrong.Add(what);
        }

        // 加大尺寸: measured rather than declared, because the numbers are in XAML and the layout is what
        // decides whether they survived — a rail crowded out by its own margin measures small with the
        // markup still reading 300. 「把音量条再改大一点」 raised both floors: the slider is 300 tall and the
        // grab band 44 wide (18 + 8 + 18), so the pill comes to 68 with its padding.
        report.Add($"音量条 {Rail.ActualWidth:F0}×{Rail.ActualHeight:F0}，滑杆高 {VolumeSlider.ActualHeight:F0}");
        Want("填充音量条尺寸", VolumeSlider.ActualHeight >= 80 && Math.Abs(Rail.ActualWidth - 40) < 1);

        Want("音量滑杆上限跟随真实音量", Math.Abs(VolumeSlider.Maximum - AudioSettings.MaxVolume) < 0.5);
        var track = PartNamed(VolumeSlider, "VerticalTrackRect");
        var rail = BoundsOf(Rail);
        var box = track is null ? default : BoundsOf(track);
        var offset = track is null ? double.NaN : box.Left + box.Width / 2 - (rail.Left + rail.Width / 2);
        report.Add($"轨道 {box.Width:F0} 宽，中心偏移 {offset:0.0}，音量 {VolumeSlider.Value:0}/{VolumeSlider.Maximum:0}");
        Want("填充轨道与音量条同宽且居中", track is not null && Math.Abs(offset) <= 1 && Math.Abs(box.Width - rail.Width) < 1);
        Want("音量条不描边", Rail.BorderThickness is { Left: 0, Top: 0, Right: 0, Bottom: 0 });
        var says = VolumeText.Text;
        Want("音量数字与滑杆一致", int.TryParse(says, out var shown) && Math.Abs(shown - VolumeSlider.Value) < 0.5);
        Want("音量数字在轨道内，静音键在下方", BoundsOf(VolumeText).Bottom <= box.Bottom
            && BoundsOf(VolumeText).Top >= box.Top && BoundsOf(MuteButton).Top >= box.Bottom - 1);
        Want("100刻度与线性比例一致", Math.Abs(VolumeHundredMark.Margin.Top - VolumeTrack.ActualHeight * (1 - 100 / VolumeSlider.Maximum)) < 1);

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

        // Sampled against the volume rail's own rectangle — uosc measures proximity to that rect, and so does
        // RailNear now (2026-09-28「参考独占模式修复」, no more RailZoneWidth strip). rail = BoundsOf(Rail),
        // in Root coordinates like the points Strength feeds in; reachStart is where proximity crosses 0.
        var railMidX = (rail.Left + rail.Right) / 2;
        var railMidY = rail.Top + rail.Height / 2;
        var reachStart = rail.Left - ChromeReveal.ProximityOutPixels;

        var middle = Strength(width / 2, height / 2);
        var entering = Strength(reachStart + 1, railMidY);
        var edgeCentre = Strength(railMidX, railMidY);
        var edgeTop = Strength(rail.Right, 8);

        report.Add($"画面中间={middle:P0}，刚进右侧带={entering:P0}，右缘中央={edgeCentre:P0}，右缘靠上={edgeTop:P0}");

        Want("画面中间不显示音量条", middle == 0);

        // 刚进右侧带几乎不显示：音量条照独占没有下限（RailFloor=0），离矩形 proximity_out（120px）那条反达线上
        // proximity 恰好从 0 起，往里一像素还不足百分之二 —— 跟标题条、控制条一样从近乎零起淡（旧版「刚进带就到
        // 35% 下限」已退）。
        Want("刚进右侧带几乎不显示", entering < 0.05);
        Want("右缘中央最明显", edgeCentre > 0.98);
        // 右缘的上下两角是去顶部按钮、去进度条的路：指针到纵向居中的音量条矩形欧氏距离一远，proximity 自然落回近零，
        // 「越偏离中心越淡」这项覆盖就落在这里（旧的 Core 竖向中心偏置 Centred 已退役，改由这条真几何承担）。
        Want("越偏离中心越淡", edgeTop < edgeCentre - 0.1 && edgeTop >= 0);

        // 越接近…越明显 as a curve rather than as four points: nine samples from the reveal reach inward to the
        // rail's centre at the vertical middle, each at least as strong as the one to its left. Quantised to
        // hundredths at the source, so this is an ordering over exact values and not a tolerance.
        var rising = true;
        var previous = -1.0;
        for (var step = 0; step <= 8; step++)
        {
            var value = Strength(reachStart + step * (railMidX - reachStart) / 8.0, railMidY);
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

        // 小画面：音量条一个像素都不画（用户令 2026-09-23）。三件一起量：尺寸线高过音量条自己、窄过线时
        // 不画、矮过线时也不画（连滚轮那次读数都不画 —— 小窗口里的音量从此没有数字可看，这个代价是那句话
        // 的正面含义，见 ChromeReveal.RailRoom）。
        report.Add($"音量条自己 {Rail.ActualWidth:F0}×{Rail.ActualHeight:F0}，"
            + $"尺寸线 {ChromeReveal.RailMinPictureWidth:F0}×{ChromeReveal.RailMinPictureHeight:F0}");
        Want("尺寸线容得下音量条自己",
            Rail.ActualHeight > 0 && Rail.ActualHeight <= ChromeReveal.RailMinPictureHeight);

        _probePictureSize = new Size(ChromeReveal.RailMinPictureWidth - 1, height);
        clock += SettleMilliseconds;
        _chrome.Tick(clock);
        var narrow = Strength(ChromeReveal.RailMinPictureWidth - 2, height / 2);
        _chrome.FlashRail(++clock);
        Render();
        var narrowWheel = Rail.Opacity;
        report.Add($"窄画面（{ChromeReveal.RailMinPictureWidth - 1:F0} 宽）指针压在最右缘={narrow:P0}，滚轮后={narrowWheel:P0}");
        Want("画面窄过尺寸线时音量条不画", narrow == 0);
        Want("画面窄过尺寸线时滚轮也不画音量条", narrowWheel == 0);

        _probePictureSize = new Size(width, ChromeReveal.RailMinPictureHeight - 1);
        clock += SettleMilliseconds;
        _chrome.Tick(clock);
        var squat = Strength(width - 1, (ChromeReveal.RailMinPictureHeight - 1) / 2.0);
        report.Add($"矮画面（{ChromeReveal.RailMinPictureHeight - 1:F0} 高）指针压在最右缘={squat:P0}");
        Want("画面矮过尺寸线时音量条不画", squat == 0);

        // Put back the way ProbeThinLine does it: the page first, so the last Render leaves nothing of the
        // player's over the library grid behind it. 画面读数也在这里还给窗口 —— 这一趟之后
        // PictureWidth/PictureHeight 又只是 Root 上的两个数了。
        Visibility = was;
        _probePictureSize = null;
        _chrome.Reset(++clock);
        _chrome.Tick(clock + SettleMilliseconds);
        SetCursorHidden(false);
        Render();

        return (wrong.Count == 0,
            string.Join("；", report) + (wrong.Count == 0 ? string.Empty : $"；不符：{string.Join('、', wrong)}"));
    }

    /// <summary>
    /// Puts the 跳过 offer up and measures what it was supposed to be clearing: the transport bar.
    /// <para>
    /// That inset used to be written down — 148 — and was wrong in a way no build could see: it was a guess
    /// at a height nothing declares at all, because the bar's height comes out of its fonts and its padding,
    /// and one larger font in the transport row would have drawn the offer across the seek slider. It is
    /// computed now, off the thing it has to clear, which is why this probe asserts a distance rather than a
    /// number — the numbers are printed for the record and are free to change.
    /// </para>
    /// <para>
    /// 统计 used to be the second overlay here (its own inset was the same kind of restated number, 104 for
    /// the strip's 96). Since 2026-09-22 the panel is mpv's own OSD rather than a page element, so there is
    /// nothing of it to place and nothing of it to measure.
    /// </para>
    /// <para>
    /// The measure fallback gets driven on purpose too. Before a film's first frame the bar is visible and
    /// has never been arranged, which is the one moment <see cref="BarHeight"/> has to measure it by hand;
    /// here the arranged height is already known, so the two can be put side by side. That comparison is
    /// the only way to find out that the hand measurement is measuring the same bar.
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) ProbeClearance()
    {
        if (!Attached) return (false, "播放层未接线");

        // 这一关量的是**窗口档**那一档的数（返回键玻璃 30 见方、上沿 5、标题字号 20/12.75、图标 13.125/16.5、
        // 两行之间那几条缝 …）。2026-09-28 更晚把两档的判据放宽成「全屏**或最大化**」（＝独占的
        // `fullormaxed`；时间轴一直这么判，见 `TimelineFullHeight`）之后，一个最大化的自检窗口会让这一整关
        // 按大档画、于是整关全红 —— 所以先把窗口摆成**普通窗口**，量完还原。这一关的假设由此从隐含变成写明。
        var wasFullscreen = _window!.Fullscreen;
        var wasMaximized = _window.IsMaximized;
        if (wasFullscreen) { SetFullscreen(false); DrainWindowChange(); }
        if (_window.IsMaximized) { RequestMaximize(false); DrainWindowChange(); }
        ApplyChromeScale();
        UpdateLayout();

        var was = Visibility;
        var wasOffer = ViewModel.SkipOffered;
        var wasCaption = ViewModel.SkipCaption;

        Visibility = Visibility.Visible;
        UpdateLayout();

        // Everything up at once, which no reveal state produces on its own: the offer stands outside the
        // rule and is the one thing that can be on screen while the bar is down.
        var clock = Now;
        _chrome.WakeFully(clock);
        Render();

        ViewModel.SkipOffered = true;
        ViewModel.SkipCaption = "跳过片头";
        UpdateLayout();

        // The placement itself, called the way EnterPlayer and the bar's own SizeChanged call it.
        PlaceOverlays();
        UpdateLayout();

        var picture = BoundsOf(Root);
        var strip = BoundsOf(TitleStrip);
        var bar = BoundsOf(Bar);
        var skip = BoundsOf(SkipButton);

        var report = new List<string>();
        var wrong = new List<string>();

        void Want(string what, bool ok)
        {
            if (!ok) wrong.Add(what);
        }

        // 模板里那层底与那支前景：框架的 Button 模板进 PointerOver/Pressed 两态写的就是这两个部件属性。
        Brush? Bed(Button button) =>
            PartNamed(button, "ContentPresenter") is ContentPresenter presenter ? presenter.Background : null;

        Brush? Ink(Button button) =>
            PartNamed(button, "ContentPresenter") is ContentPresenter presenter ? presenter.Foreground : null;

        var below = bar.Top - skip.Bottom;

        report.Add($"进度条高 {bar.Height:F0}，跳过按钮 {skip.Width:F0}×{skip.Height:F0} 让开 {below:F1}");

        Want("三样都得有尺寸", strip.Height > 0 && bar.Height > 0 && skip.Height > 0);

        // 标题那一行背后那块玻璃：紧贴文字（Padding 撑开、上下留白相等），而且**不是整条**。
        // 量的是几何而不是照片 —— 照片量不了：--show-osd 那一路 ViewModel 是空的、那一栏没有字，玻璃跟着缩到
        // 最矮，看不出「有标题时」的样子。上一版那块底是与文字栏分开的一层整宽 Border，所以量得出「底比文字栏
        // 高」；这一版那块底就是每一行字自己的容器（TitleBox／SubtitleBox），比的是它里面的那个 TextBlock。
        // **这里给两行字各摆一行真文字**（跟跳过按钮那一关同一个做法：摆起来、量完、收场还回去）：空标题时
        // 玻璃只有两侧的 Padding 宽，「不铺满整条」那一问等于在量空气；摆一行长片名才是用户截图里的场面。
        var wasTitle = ViewModel.Title;
        var wasSubtitle = ViewModel.Subtitle;
        ViewModel.Title = "S01E05 塔 | 糟糕时间 | 算盘与麻花辫 | 飞驰而过的青春";
        ViewModel.Subtitle = "1920 x 1080  ·  HEVC  ·  AAC  ·  Studio GreenTea";
        UpdateLayout();

        var titleText = BoundsOf(TitleText);
        var box = BoundsOf(TitleBox);
        var above = titleText.Top - box.Top;
        var under = box.Bottom - titleText.Bottom;

        report.Add($"标题那块玻璃 {box.Width:F0}×{box.Height:F0}，文字 {titleText.Width:F0}×{titleText.Height:F0}，"
            + $"上留 {above:F1} 下留 {under:F1}");

        Want("标题那块玻璃比文字大", box.Height > titleText.Height && box.Width > titleText.Width);

        // 上下留白这一问的容差比别处（GeometrySlack 半像素）松一档：这里量的是**文字**的墨框，20 号那行的行框
        // 是小数（25.4 上下），Border 按 Padding 撑开之后两边各带半点取整 —— 那 0.6 是字体度量不是布局错。
        // 真正要防的是「有人把 Padding 写成上下不等」那种不对称，那一问下面单独钉。
        Want("标题那块玻璃的上下留白相等", Math.Abs(above - under) < 1.0);
        Want("标题那块玻璃的 Padding 上下对称", TitleBox.Padding.Top == TitleBox.Padding.Bottom);

        // 用户令 2026-09-27「不要一大块」：一行长片名也到不了右上角那一栏。
        Want("标题那块玻璃不铺满整条",
            box.Right < BoundsOf(WindowButtons).Left + GeometrySlack && box.Width < strip.Width - 40);

        // **两条线**（用户令 2026-09-28 更晚「把独占模式的标题复刻到集成模式」之后的形状）：左上那一簇
        // 自己一条 —— 返回键玻璃与标题玻璃同高同顶（上沿都是 5、都是 30 高，中线 20）；右上那一排自己
        // 一条 —— 置顶＋三颗窗口命令 40 见方、往下让 4（中线 24）。
        // 历史上那条「顶部一排在同一条基线上」量的是「左上＋右上五处一个中线」，随复刻退役：左上按独占
        // 收成 30 见方、让 5 之后两条线差 4 像素。独占那头本来也是两条（它那三颗窗口键的可见底 35 见方、
        // 中心 22.5，左边这三块中心 20）—— 所以「复刻」指的是左上角这三块的内部关系，不是整条顶栏一刀切。
        var back = BoundsOf(BackButton);

        double Centre(FrameworkElement element)
        {
            var bounds = BoundsOf(element);
            return bounds.Top + (bounds.Height / 2);
        }

        var upperGap = Math.Abs(Centre(BackButton) - Centre(TitleBox));
        var rightRow = new[] { PinButton, MinimizeButton, CloseButton }.Select(Centre).ToArray();
        var rightSpread = rightRow.Max() - rightRow.Min();

        report.Add($"中线：返回键 {Centre(BackButton):F1}、标题框 {Centre(TitleBox):F1}（差 {upperGap:F1}）；"
            + $"右上那一排 {string.Join('、', rightRow.Select(value => value.ToString("F1")))}（差 {rightSpread:F1}）");

        Want("左上那一簇自己一条中线：返回键玻璃与标题玻璃同高同顶", upperGap < 2.0);
        Want("右上那一排在同一条基线上", rightSpread < 2.0);

        // 右上那三颗窗口命令的**图标字号**（用户令 2026-09-28 晚「集成模式窗口化的时候右上角的图标太大了，
        // 改成跟独立模式窗口化时一样大」；同日深夜先「缩小 1.3 倍」缩过一轮，第五批「把集成模式右上角的
        // 四个图标还有这四个图标的背景改成跟独占模式的窗口模式下右上角的一样大」又把那一笔整段撤回）：
        // 窗口档 13.125 ＝ 独占那一头的 17.5 × 0.75（`\fs` 是 72 DPI 的 pt、这里的 FontSize 是 96 DPI 的
        // px），全屏档 17.25。数的来历与实拍读数在 `WindowGlyphStyle` 那段标记里；这一关只问「真摆上去了
        // 没有」—— 更早那三颗是照首页 caption 抄来的 16（实拍：关闭叉 16×16 对独占 13×13、最小化横杠
        // 16 对 11）。置顶那颗不在这条判据里（它是 PathIcon、不吃字号，见下面单立的那一条）。
        const double WindowCommandGlyph = 13.125;
        var stripGlyphs = new[] { (Name: "最小化", Glyph: MinimizeGlyph), (Name: "最大化", Glyph: MaximizeGlyph),
                                  (Name: "关闭", Glyph: CloseGlyph) };
        var offSize = stripGlyphs.Where(item => Math.Abs(item.Glyph.FontSize - WindowCommandGlyph) > 0.01)
            .Select(item => $"{item.Name} {item.Glyph.FontSize:0.###}").ToList();

        report.Add(offSize.Count == 0
            ? $"右上三颗窗口命令的图标字号都是 {WindowCommandGlyph}（窗口档；＝独占 17.5 × 0.75）"
            : $"右上三颗窗口命令的图标字号不对：{string.Join('、', offSize)}");
        Want("右上三颗窗口命令的图标是窗口档那一档字号", offSize.Count == 0);

        // **四个**图标里的第四个：置顶那颗图钉。它 2026-09-28 深夜第五批换成了独占同一支字体的同一颗
        // （用户令「把集成模式右上角的置顶图标换成跟独占模式一样的」），尺寸于是跟三颗窗口命令分开量 —— 它是
        // `PathIcon`、几何自己带比例、不吃 FontSize，报的是一对实框（`WindowPinGlyph`，窗口档 9.793 × 14
        // ＝ 独占实拍的白核 9 × 14）。判据读的是**布局之后**的 `ActualWidth`／`ActualHeight`，不是 XAML 里
        // 写的 `Width`：这一栏的可点格是 40 的步进，若这层布局没吃到那个数（Stretch 填满格、或外面套了别的
        // 东西），只读设置值就会「写着 9.793、画出来却是 40」而这一关照样绿。
        var pinWant = BigChrome ? FullscreenPinGlyph : WindowPinGlyph;

        report.Add($"置顶图钉：实框 {PinGlyphBox.ActualWidth:0.###}×{PinGlyphBox.ActualHeight:0.###}"
            + $"（写的是 {PinGlyphBox.Width:0.###}×{PinGlyphBox.Height:0.###}；这一档要 "
            + $"{pinWant.Width}×{pinWant.Height}；里面那份几何是 {PinGlyph.Data?.Bounds.Width:0.###}×"
            + $"{PinGlyph.Data?.Bounds.Height:0.###}）");
        Want("置顶那颗图钉是这一档那一对实框（窗口档 9.793×14，与独占实拍同数）",
            Math.Abs(PinGlyphBox.ActualWidth - pinWant.Width) < GeometrySlack
            && Math.Abs(PinGlyphBox.ActualHeight - pinWant.Height) < GeometrySlack);
        Want("置顶那颗图钉的实框比它自己那一格小（40 的步进没有被拉伸填满）",
            PinGlyphBox.ActualWidth < 40 - GeometrySlack);

        // 同一条令的另一半：**整排左移五个像素**。两条判据一起看：设置值（那一栏的右边距 5、左边距 0）与
        // 布局结果（那一栏的右缘离客户区右缘 5）。左边距必须是 0：左移靠右缘让位，不是靠左缘推（后者会把
        // 这一栏从右缘顶开，看起来就是「没对齐」）。
        var rightGap = Root.ActualWidth - BoundsOf(WindowButtons).Right;
        report.Add($"那一栏实框右缘离客户区右缘 {rightGap:F1}（客户区宽 {Root.ActualWidth:F0}）");
        Want("右上那一排左移了 5 像素（右边距 5、左边距 0）",
            Math.Abs(WindowButtons.Margin.Right - 5) < GeometrySlack && WindowButtons.Margin.Left == 0);
        Want("右上那一排的右缘离客户区右缘 5 像素", Math.Abs(rightGap - 5) < 1.0);

        // 独占模式顶栏那颗返回键（`elements/TopBar.lua` 的 EMBYNIAN[topbar-back-glass]）：可见底是
        // **整格 top_bar_size＝40 里上下左右各让 margin＝5** ＝ 30 见方（2026-09-28 晚「返回按钮的背景要和
        // 标题的背景一致」＋「把标题的大小改回跟 mpv_config 项目一样大小」），那块玻璃正好与它自己的标题
        // 玻璃同形同色。2026-09-28 更晚「把独占模式的标题复刻到集成模式」把集成这一头也照它办了 ——
        // 下面这几条量的就是这一头，数值与独占逐像素一致。
        const double BackSquare = 30;
        const double BackInset = 5;
        const double TopDrop = 5;

        report.Add($"返回键 {back.Width:F0}×{back.Height:F0}，在客户区左上角 {back.Left:F0},{back.Top:F0}，"
            + $"中线 {back.Top + (back.Height / 2):F1}；标题那一行字中线 "
            + $"{titleText.Top + (titleText.Height / 2):F1}（往下让了 {box.Top:F0}）");

        Want("返回键玻璃 30 见方、四周各让 5（与独占同一档）",
            Math.Abs(back.Width - BackSquare) < GeometrySlack
            && Math.Abs(back.Height - BackSquare) < GeometrySlack
            && Math.Abs(back.Left - BackInset) < GeometrySlack
            && Math.Abs(back.Top - BackInset) < GeometrySlack);

        // 右缘＝5 ＋ 30 ＝ 35；标题玻璃的左缘正是它再加那条 1px 的缝（＝独占的 `title_spacing`）。
        Want("返回键玻璃右缘在 35 上", Math.Abs(back.Right - (BackInset + BackSquare)) < GeometrySlack);

        // 「返回键边长＝标题框高度」（用户令 2026-09-28，两模式同一条）：30 见方对 30 高的标题框 ——
        // 这也是「两块玻璃同形」里的一半，全屏档（玻璃 40、标题框 40）同样成立。
        Want("返回键边长＝标题框高度",
            Math.Abs(back.Height - box.Height) < GeometrySlack);

        // 用户令 2026-09-27 傍晚第五批「返回和集名/片名往下移动一点点」：这一簇**一起**往下，量的是
        // 「两处挪的是同一个数」——数字是多少不重要（复刻之后是 5，＝独占的 margin），一个挪一个不挪
        // 才是屏上看得出的错位。
        Want("返回键与集名/片名一起往下让了同一个数",
            Math.Abs(back.Top - TopDrop) < GeometrySlack && Math.Abs(box.Top - TopDrop) < GeometrySlack);

        // 圆角（用户令 2026-09-28「左上角标题太圆了，改成跟独占一样」，问实了＝照独占 border_radius=2）：量的是
        // **互相相等**、不是写死的数 —— 左上角那三块（返回键那块玻璃、集名、片名）与右上那一排同一个数（8 换 2
        // 它照样绿）。2026-09-27 晚「统计」摘掉后，右上那一排改由置顶那一颗代表（同款按钮、同一个圆角）。
        var backGlass = BackButton.Parent is Grid backCell && backCell.Children.Count > 0
            ? backCell.Children[0] as Border
            : null;

        report.Add($"圆角：返回键那块玻璃 {backGlass?.CornerRadius.TopLeft}、集名 {TitleBox.CornerRadius.TopLeft}、"
            + $"片名 {SubtitleBox.CornerRadius.TopLeft}、右上那一排 {PinButton.CornerRadius.TopLeft}");

        Want("左上角那几块的圆角与右上那一排同一个数",
            backGlass is not null
            && backGlass.CornerRadius == TitleBox.CornerRadius
            && TitleBox.CornerRadius == SubtitleBox.CornerRadius
            && SubtitleBox.CornerRadius == PinButton.CornerRadius);

        // 返回与片名之间的那条缝（2026-09-27 傍晚第三批「让返回和片名紧凑一点」收窄过，2026-09-28 更晚
        // 「复刻标题」把它定成那个 1 —— ＝独占两行之间、以及返回键与标题之间用的同一个 `title_spacing`）。
        // 量的是两者之间那条缝，不是边距的字面值 —— 谁把 ColumnSpacing 加回去都拦得住。
        report.Add($"返回与片名之间 {box.Left - back.Right:F1}");

        Want("返回与片名之间是那条 1px 的缝", Math.Abs((box.Left - back.Right) - TitleGap) < GeometrySlack);

        // 第二行**自己一块**（用户令 2026-09-27「下方的剧名单独一个框」，2026-09-28 把内容换成文件信息那四段）。
        // 独占模式里正是两块：主标题一个框、副标题另起一个框画在**返回键的正下方**（TopBar.lua 的
        // Main title / Alt title）。2026-09-28 更晚「复刻标题」把这一块的字与落点也照独占办了 —— 斜体、
        // 中性浅灰（不再是亮白）、前面缀「└ 」、左缘跟返回键玻璃同一条、上沿＝返回键玻璃下沿＋1。
        const double SubtitleFontSize = 12.75;

        var subText = BoundsOf(SubtitleText);
        var subBox = BoundsOf(SubtitleBox);

        report.Add($"剧名那块玻璃 {subBox.Width:F0}×{subBox.Height:F0}（标题那块 {box.Width:F0}×{box.Height:F0}），"
            + $"在标题之下 {subBox.Top - box.Bottom:F1}；字号 {SubtitleText.FontSize:F2}、斜体 {SubtitleText.FontStyle}");

        Want("剧名自己一块玻璃", !ReferenceEquals(SubtitleBox, TitleBox)
            && ReferenceEquals(SubtitleBox.Background, TitleBox.Background)
            && subBox.Width > 0 && subBox.Height > 0);
        Want("剧名那块玻璃比它的字大", subBox.Height > subText.Height && subBox.Width > subText.Width);
        Want("剧名那块玻璃不与标题那块重叠", !Overlaps(subBox, box));
        Want("剧名那块玻璃在标题之下", subBox.Top >= box.Bottom - GeometrySlack);

        // 落点照独占：左缘＝返回键玻璃左缘；上沿＝返回键玻璃下沿＋1，那条缝就是同一个 `title_spacing`。
        Want("第二行挂在返回键玻璃正下方（左缘同一条、上沿＝它的下沿＋1）",
            Math.Abs(subBox.Left - back.Left) < GeometrySlack
            && Math.Abs(subBox.Top - (back.Bottom + TitleGap)) < GeometrySlack);

        // 字（用户令 2026-09-28 晚「元数据的字体加点灰色」＋「标题下方的视频元数据改为斜体」）：色是那一档
        // 中性浅灰、与独占同一个值；斜体照独占。**字号是 12.75 不是 18**：独占 uosc 那一行是
        // `round(alt_title_size * 0.71)` ＝ `\fs` 17（2026-09-29 令「缩小一点点」从 0.77/18 收小），
        // 而 libass 的 `\fs` 按 72 DPI 的 pt 渲染、这里的 FontSize 是 96 DPI 的 px，乘 0.75 才等大
        // （用户令 2026-09-28 更晚「集成模式下面的元数据体积太大了」修的换算）。
        Want("剧名的字是那一档中性浅灰（与独占同一个值）",
            ReferenceEquals(SubtitleText.Foreground, Resources["PlayerInkMetaBrush"]));
        Want("剧名的字走斜体", SubtitleText.FontStyle == Windows.UI.Text.FontStyle.Italic);
        Want("剧名的字是自己那一档字号", Math.Abs(SubtitleText.FontSize - SubtitleFontSize) < GeometrySlack);

        // 「深到底」那条线（用户令 2026-09-27 傍晚第四批「左上角的颜色深度在鼠标移动到剧名下方那条线之前
        // 一点的时候达到最大」）。两个数要一起看：**线在哪**（页面按左簇最下面那块玻璃的下沿量出来的）
        // 与**线之上是不是真的满了**（页面那条接线：指针 Y → 深度）。
        // 它必须在「两行字还摆着」这一段里量 —— 下面的收场把剧名收回之后，左簇最下面那块玻璃就变成返回键
        // 那颗，线跟着上移（返回键玻璃 30 高、上沿 5），量的就不是这句话说的那条线了。
        // 量法：把指针读数临时换成几个合成位置，走页面自己的 <see cref="TopGlassDepth"/>，量完原样放回去。
        var wasPointer = _pointerAt;
        var fullAt = TopGlassFullAt();
        var fullPx = fullAt * Root.ActualHeight;
        var bandPx = ChromeReveal.EdgeBandFraction * Root.ActualHeight;

        // 五个合成位置：线之上、就在线上、半路、带子下沿、带子外面。
        var spotAt = new[]
        {
            Math.Max(0, fullPx - 12),
            fullPx,
            (fullPx + bandPx) / 2,
            bandPx,
            bandPx + 6
        };

        var depthAt = new List<(double Y, double Depth)>();

        foreach (var spot in spotAt)
        {
            _pointerAt = new Point(4, spot);
            depthAt.Add((spot, TopGlassDepth()));
        }

        _pointerAt = wasPointer;
        ApplyTopGlass(TopGlassDepth());

        report.Add($"满深线 y={fullPx:F1}（剧名那块玻璃下沿 {subBox.Bottom:F1}、返回键下沿 {back.Bottom:F1}），"
            + $"顶部带下沿 y={bandPx:F1}；指针 y→深度 "
            + string.Join('、', depthAt.Select(read => $"{read.Y:F0}→{read.Depth:0.##}")));

        Want("满深线在剧名那块玻璃的下沿之上一点点",
            fullAt > 0 && fullAt < ChromeReveal.EdgeBandFraction
            && Math.Abs((subBox.Bottom - ChromeReveal.TopGlassFullInset) - fullPx) < 1.5);

        Want("指针到满深线就满了、退到带子下沿就最淡",
            Math.Abs(depthAt[0].Depth - 1) < 0.001
            && Math.Abs(depthAt[1].Depth - 1) < 0.001
            && depthAt[2].Depth is > 0 and < 1
            && Math.Abs(depthAt[3].Depth) < 0.001
            && Math.Abs(depthAt[4].Depth) < 0.001);

        // 两行字收回原样（下面那颗跳过按钮的几何、以及收场那一拍都按常态走）。
        ViewModel.Title = wasTitle;
        ViewModel.Subtitle = wasSubtitle;
        UpdateLayout();

        // 常驻玻璃整条只剩**左上角那三块**：返回键（2026-09-26 用户令「给返回键加背景」留下的），以及标题与
        // 剧名那两块。三块共用**同一支**画刷 —— 也就是跟着指针高度变深的那一支（用户令 2026-09-27 傍晚第三批
        // 「加深左上角亚克力背景的颜色，鼠标位置越靠上亚克力背景的颜色越深」，问实了＝返回键与片名两块一起跟
        // 指针走）。统计那颗的底在同一批按用户令去掉（「把统计的亚克力背景去掉」）。结构上量：控件住在一个
        // Grid 里、第一个孩子是那块 Border、不吃指针、不越出控件。
        var bare = new List<string>();
        var pillAlpha = 0;
        Brush? lead = null;

        foreach (var (name, control) in new (string Name, FrameworkElement Element)[]
                 {
                     ("返回", BackButton)
                 })
        {
            if (control.Parent is not Grid cell || cell.Children.Count < 2 || cell.Children[0] is not Border pill)
            {
                bare.Add($"{name}没有");
                continue;
            }

            // 三块共用那一支是**跟着指针高度变深**的那一支（用户令 2026-09-27 傍晚第三批「加深左上角亚克力
            // 背景的颜色，鼠标位置越靠上亚克力背景的颜色越深」）：它的值不是一个常数，所以这里比的是
            // 「是不是那一支」加「浓度是不是本页此刻这一档」——常数那条比法在这一支上必红，而「压根没画上」
            // （alpha 0）两种比法都拦得住。
            if (!ReferenceEquals(pill.Background, Resources[PlayerPalette.TopGlassKey])
                || pill.Background is not SolidColorBrush { Color.A: var alpha })
                bare.Add($"{name}那块不是左上角那支玻璃");
            else if (alpha != PlayerPalette.TopGlassAlphaAt(_topGlassDepth))
                bare.Add($"{name}那块的浓度 {alpha:X2} 不是本页此刻那一档 "
                    + $"{PlayerPalette.TopGlassAlphaAt(_topGlassDepth):X2}");
            else if (pill.IsHitTestVisible) bare.Add($"{name}那块会吃指针");
            else if (!Encloses(BoundsOf(control), BoundsOf(pill))) bare.Add($"{name}那块越出了按钮");
            else
            {
                lead = pill.Background;
                pillAlpha = alpha;
            }
        }

        report.Add(bare.Count == 0
            ? $"返回键那块常驻玻璃 alpha {pillAlpha:X2}（指针深度 {_topGlassDepth:0.##}）"
            : $"背后不对:{string.Join('、', bare)}");
        Want("返回那颗带的是左上角那支玻璃", bare.Count == 0);

        // 那三块（返回键、标题、剧名）是**同一支**画刷，而这支画刷是全表唯一一支拿不到常数的：
        // 不钉住这一条，返回键那块按「跟指针走」上色、片名那两块忘了换过来的话，屏上是左上角两档深浅并排，
        // 而上面那一问照样绿。
        Want("返回与片名共用同一支玻璃",
            lead is not null
            && ReferenceEquals(lead, TitleBox.Background)
            && ReferenceEquals(SubtitleBox.Background, TitleBox.Background));

        // 「鼠标位置越靠上，亚克力背景的颜色越深」这条直线本身，逐档钉住。量法走页面真正写画刷的那条路
        // （ApplyTopGlass），不是照 Core 那条纯函数自己乘一遍 —— 后者只是算术，而这里要证明的是「页面真的
        // 按它写」：写错了档、写错了画刷、写反了方向，三种都在这儿现形。
        var ramp = new List<string>();
        var rungs = new[] { 0.0, 0.25, 0.5, 0.75, 1.0 };
        var laid = true;

        foreach (var depth in rungs)
        {
            ApplyTopGlass(depth);

            var got = ((SolidColorBrush)Resources[PlayerPalette.TopGlassKey]).Color.A;
            var want = PlayerPalette.TopGlassAlphaAt(depth);

            if (got != want) laid = false;
            ramp.Add($"{depth:0.##}→{got:X2}");
        }

        // 收场把它推回指针此刻那一档：上面那五个值是探针自己摆上来的，不是屏上的样子。
        ApplyTopGlass(TopGlassDepth());

        var ladder = rungs.Select(PlayerPalette.TopGlassAlphaAt).ToList();

        report.Add($"左上角玻璃浓度（深度→alpha）{string.Join('、', ramp)}");
        Want("左上角玻璃跟着指针高度变深",
            laid
            && Enumerable.Range(1, ladder.Count - 1).All(rung => ladder[rung] > ladder[rung - 1])
            && ladder[0] == PlayerPalette.GlassAlpha
            && ladder[^1] < byte.MaxValue);

        // 右上角那一栏**不许**有常驻玻璃（用户令 2026-09-27 傍晚「鼠标没移到按钮上的时候不要显示背景」、
        // 「右上角的按钮照搬首页的就好」）。它们几颗现在直接
        // 住在 WindowButtons 那一栏里，头顶没有 Border；悬停那一层由框架的 Button 模板给。
        // 2026-09-27 晚「统计」摘掉后，这一栏剩四颗（置顶、最小化、最大化、关闭）。
        // **置顶那颗 2026-09-28 深夜第五批起是唯一的例外**：已置顶时它整颗常亮，那层底由 SetPinned 亲手写进
        // 控件自己的 Background（用的就是悬停那一支白 —— 独占 lit 那一档的画法是同一句）。所以这一关按
        // **置顶状态**分两支：没置顶时四颗都不画底，置顶时三颗不画、置顶那颗画的是那一支白。
        var beds = new List<string>();
        var litBed = Resources["PlayerStripHoverBrush"];

        foreach (var (name, control) in new (string Name, FrameworkElement Element)[]
                 {
                     ("置顶", PinButton),
                     ("最小化", MinimizeButton),
                     ("最大化", MaximizeButton),
                     ("关闭", CloseButton)
                 })
        {
            if (!ReferenceEquals(control.Parent, WindowButtons))
            {
                beds.Add($"{name}不在窗口命令那一栏");
                continue;
            }

            if (ReferenceEquals(control, PinButton) && _pinned)
            {
                if (control is not Button { Background: { } lit } || !ReferenceEquals(lit, litBed))
                    beds.Add($"{name}已置顶却没有常亮那一层底");
            }
            else if (control is not Button button || button.Background is not { } own
                || own is not SolidColorBrush { Color.A: 0 })
            {
                beds.Add($"{name}自己画了底");
            }
        }

        report.Add(beds.Count == 0
            ? $"右上角那几颗平时没有底（置顶那颗此刻 {(_pinned ? "亮着" : "没亮")}）"
            : $"有问题：{string.Join('、', beds)}");
        Want("右上角那几颗平时没有底（置顶那颗已置顶时例外，它那时常亮）", beds.Count == 0);

        // 尺寸复刻独占顶栏（用户令 2026-09-28「复刻独占模式右上角的最小化、窗口化、关闭三个按钮，替换掉
        // 集成模式右上角的 winui 按钮」）：独占 `elements/TopBar.lua` 那几颗各占 **top_bar_size＝40** 的一格，
        // 而**画出来的可见底**是 `size − margin`＝**35**（margin＝floor((40−20)/4)＝5）。
        // 2026-09-28 深夜第五批用户令「把集成模式右上角的四个图标还有这四个图标的背景改成跟独占模式的窗口
        // 模式下右上角的一样大」：集成的按钮从 40 见方收成 **35 见方**（＝那一层可见底），40 那个数改由这一栏
        // 的 `Spacing` ＋ 整栏右边距维持成**步进**（见 PlayerPage.xaml 那一栏的标记）—— 于是四颗的格距与占位
        // 仍与独占一样，屏幕上看得见的方块一样大。逐颗量。
        var oversized = new List<string>();

        foreach (var (name, button) in new[]
                 {
                     ("置顶", PinButton), ("最小化", MinimizeButton),
                     ("最大化", MaximizeButton), ("关闭", CloseButton)
                 })
        {
            var bounds = BoundsOf(button);
            if (Math.Abs(bounds.Width - 35) >= GeometrySlack || Math.Abs(bounds.Height - 35) >= GeometrySlack)
                oversized.Add($"{name} {bounds.Width:F0}×{bounds.Height:F0}");
        }

        report.Add(oversized.Count == 0 ? "右上四颗都是 35×35（复刻独占的可见底）" : $"尺寸不对：{string.Join('、', oversized)}");
        Want("右上四颗复刻独占的可见底是 35×35 正方形", oversized.Count == 0);

        // 格距那一条：35 是**画出来的底**，占位还得是 40（独占那 40 见方的格）—— 否则四颗会挤成更窄的一排、
        // 贴右缘的位置也跟着挪。量的是相邻两颗左缘之差，不读 `Spacing` 那个数本身。
        var steps = new[] { PinButton, MinimizeButton, MaximizeButton, CloseButton }
            .Select(button => BoundsOf(button).Left).ToArray();
        var gaps = steps.Zip(steps.Skip(1), (left, right) => right - left).ToArray();

        report.Add($"四颗的左缘步进 {string.Join('、', gaps.Select(gap => gap.ToString("F1")))}");

        Want("四颗的格距仍是 40（35 只是可见底，占位没变）",
            gaps.All(gap => Math.Abs(gap - 40) < GeometrySlack));

        // 悬停/按下那两档（用户令 2026-09-27 傍晚第四批「鼠标移动到右上角的关闭的时候背景要和首页一样变成
        // 红色，然后右上角另外几个按钮鼠标移动到按钮上的时候背景颜色太浅了容易和画面合在一起」；第五批
        // 「这四个按钮鼠标移到上面的时候要用白色亚克力背景」＝几颗改成一层半透明的白，并且问实了白底上的
        // 图标要转深色）。
        // **照片拍不到这两档**（这台机器注不进鼠标事件），所以把状态推上去读模板：框架的 Button 模板进
        // PointerOver/Pressed 两态会把 `ContentPresenter` 的底**与前景**换成那几个 ThemeResource，而那几颗
        // 各自在自己的资源字典里把它们按到了调色板上（与「跳过」那颗、滑杆白条同一手法）。键按回来了、
        // 模板却没取用，或者这一栏里漏了一颗（漏的那颗仍是框架那层白一成，压在画面上几乎看不见），都在这里红。
        // 关闭那颗最后一格是空的：**红底上的白叉本来就是对的**，它不该被顺手一起换成深色。
        // 2026-09-27 晚「统计」摘掉后，白底那几颗只剩置顶、最小化、最大化三颗。
        var hovers = new (string Name, Button Button, string Hover, string Pressed, string Ink)[]
        {
            ("置顶", PinButton, "PlayerStripHoverBrush", "PlayerStripPressedBrush", "PlayerStripHoverInkBrush"),
            ("最小化", MinimizeButton, "PlayerStripHoverBrush", "PlayerStripPressedBrush", "PlayerStripHoverInkBrush"),
            ("最大化", MaximizeButton, "PlayerStripHoverBrush", "PlayerStripPressedBrush", "PlayerStripHoverInkBrush"),
            ("关闭", CloseButton, "PlayerCloseHoverBrush", "PlayerClosePressedBrush", "")
        };

        var dull = new List<string>();

        foreach (var (name, button, hover, pressed, ink) in hovers)
        {
            foreach (var (state, key) in new[] { ("PointerOver", hover), ("Pressed", pressed) })
            {
                VisualStateManager.GoToState(button, state, false);

                if (!ReferenceEquals(Bed(button), Resources[key]))
                    dull.Add($"{name}的{state}不是{key}");

                if (ink.Length > 0)
                {
                    if (!ReferenceEquals(Ink(button), Resources[ink])) dull.Add($"{name}的{state}图标没有转深色");
                }
                else if (ReferenceEquals(Ink(button), Resources["PlayerStripHoverInkBrush"]))
                {
                    dull.Add($"{name}不该转深色（红底上的白叉本来就是对的）");
                }
            }

            VisualStateManager.GoToState(button, "Normal", false);
        }

        report.Add(dull.Count == 0
            ? "右上几颗的悬停/按下都按回了调色板（白底那几颗、关闭那颗是红的）"
            : $"悬停不对：{string.Join('、', dull)}");
        Want("右上几颗的悬停/按下都按回了调色板", dull.Count == 0);

        // 图标自己也要换（同一条令里问实的那一半）：白底上的白图标会糊成一片。
        // **这一条不走模板**：模板换的是 `ContentPresenter.Foreground`，而 `FontIcon`／`PathIcon` 的 Foreground
        // **不从那里继承下来** —— 2026-09-27 傍晚第五批实测：模板那一层已经换成深色了（上面那一问全绿），
        // 图标读回来还是白的。这个坑只有单独问一次图标自己才现形，所以它由页面那条线管：`SetStripGlyphInk`，
        // 由这一栏的 PointerMoved／PointerExited 推。**指针事件那一小段接线探针喂不了**（这台机器注不进鼠标
        // 事件），这里量的是它推到的那一端；接线本身只有读码与实机验收。
        // **一格一格地问**：第一版这里是「四颗一起转深色」，用户当场问「怎么是四个按钮一起变色」——
        // 所以现在每一颗都要单独推一次，而且要检查**其余几颗回到了白**（这一条才是那个 bug 的判据）。
        // 2026-09-27 晚「统计」摘掉后，这一份名单只剩置顶、最小化、最大化三颗；2026-09-28 深夜第五批起置顶那颗
        // 只有**一颗** PathIcon，而且**已置顶时它本来就该是深色**（常亮那一档是白底）—— 于是下面按「此刻置顶开
        // 着没有」算它的期望色，其余几颗照旧只看指针。
        var glyphSets = new (string Name, Button Button, IconElement[] Glyphs)[]
        {
            ("置顶", PinButton, [PinGlyph]),
            ("最小化", MinimizeButton, [MinimizeGlyph]),
            ("最大化", MaximizeButton, [MaximizeGlyph])
        };

        var mixed = new List<string>();
        var pinnedInk = _pinned ? "PlayerStripHoverInkBrush" : "PlayerInkBrush";

        foreach (var (name, button, _) in glyphSets)
        {
            SetStripGlyphInk(button);

            foreach (var (otherName, otherButton, theirs) in glyphSets)
            {
                var want = ReferenceEquals(button, otherButton)
                    ? "PlayerStripHoverInkBrush"
                    : ReferenceEquals(otherButton, PinButton) ? pinnedInk : "PlayerInkBrush";

                if (theirs.Any(glyph => !ReferenceEquals(glyph.Foreground, Resources[want])))
                    mixed.Add($"{name}压着的时候{otherName}那颗不是{want}");
            }

            // 关闭那颗**不该**跟着转（它悬停时是红的，红底上的白叉本来就是对的）。
            if (CloseButton.Content is FontIcon closeGlyph
                && ReferenceEquals(closeGlyph.Foreground, Resources["PlayerStripHoverInkBrush"]))
                mixed.Add($"{name}压着的时候关闭那颗也转深色了");
        }

        SetStripGlyphInk(null);

        // 指针不在这一栏时该回白的那几颗里，已置顶的置顶那颗不算 —— 它那时**常亮**着，深色才是对的。
        var leftDark = glyphSets
            .Where(set => !ReferenceEquals(set.Button, PinButton) || !_pinned)
            .SelectMany(set => set.Glyphs)
            .Count(glyph => !ReferenceEquals(glyph.Foreground, Resources["PlayerInkBrush"]));

        var pinWord = _pinned ? "常亮着、一直是深色" : "没置顶、照常回白";

        report.Add(mixed.Count == 0 && leftDark == 0
            ? $"四颗的图标一个一个换（压着的那颗转深色、其余几颗白；置顶那颗此刻{pinWord}）"
            : $"图标不对：{string.Join('、', mixed)}"
              + (leftDark == 0 ? string.Empty : $"；指针不在这一栏时有 {leftDark} 个还是深色"));

        Want("压着的那一颗图标转深色、其余几颗回白（置顶那颗已置顶时另算）", mixed.Count == 0 && leftDark == 0);

        Want("跳过按钮不压进度条", !Overlaps(skip, bar));
        Want("跳过按钮在画面里", Encloses(picture, skip));

        // The distances themselves, which is what says they were derived rather than typed: each overlay
        // sits exactly one gap off the edge of what it clears. A margin re-hardcoded to some number that
        // happens not to overlap today would pass the two checks above and fail these two.
        // 2026-09-26 起按钮在「让开」之上再抬 SkipLift（用户令「上移按钮」），量出来的间距是两者的和。
        Want("跳过按钮的间距是量出来的", Math.Abs(below - (OverlayGap + SkipLift)) < GeometrySlack);

        // 倒计时插值（用户令 2026-09-26「按钮上的倒计时进度条不是很顺滑」）。这一关开头的
        // SkipOffered=true 那一拍已经走真链路把表开起来（直接赋值也发 PropertyChanged）。这里再走
        // <see cref="PlayerViewModel.ShowSkipPrompt"/>（自检专用门）校准一拍，然后用合成时刻问插值读数
        // —— 表刚开、流速还是满速档：余值 1→0 立满 15 秒，750ms 后应在 0.95。
        ViewModel.ShowSkipPrompt(new SkipPrompt(true, "跳过片头", "", 1.0));
        var countdownAnchor = Now;
        report.Add($"倒计时 表开={_skipCountdownTimer?.IsRunning == true}，锚 {SkipCountdown.Value:0.###}，"
            + $"750ms 后应到 {SkipCountdownShown(countdownAnchor + 750):0.###}");
        Want("倒计时表开着", _skipCountdownTimer?.IsRunning == true);
        Want("倒计时锚点当拍铺上", Math.Abs(SkipCountdown.Value - 1.0) < 0.005);
        Want("倒计时沿满速走", Math.Abs(SkipCountdownShown(countdownAnchor + 750) - 0.95) < 0.02);

        // 按下判定（用户令 2026-09-26「点击按钮跳过片头/片尾的时候 进度条会出来闪一下」）：offer 立着时
        // 按在按钮上的那一下算按钮的、不叫控件 —— 判定（PressOnSkipButton）与几何在这里钉住；接线本身
        // 编译与读码为准，探针喂不了 PointerRoutedEventArgs。
        var skipCentre = new Point(skip.Left + skip.Width / 2, skip.Top + skip.Height / 2);
        Want("压在跳过按钮上的按下算按钮的", PressOnSkipButton(skipCentre));

        // 量具本身. Take the bar out of the layout so its ActualHeight really is 0 — the state it is in
        // before the first film — and make the fallback do the work.
        var arranged = bar.Height;
        var wasBar = Bar.Visibility;
        Bar.Visibility = Visibility.Collapsed;
        UpdateLayout();

        _barHeight = 0;
        var measured = BarHeight();

        Bar.Visibility = wasBar;
        UpdateLayout();

        report.Add($"没排版过时量得 {measured:F0}，排版后 {arranged:F0}");
        Want("量具跟排版结果对得上", Math.Abs(measured - arranged) < 1);

        // Put everything back, page first, so the last Render leaves nothing of the player's over the
        // library grid behind it.
        ViewModel.SkipOffered = wasOffer;
        ViewModel.SkipCaption = wasCaption;
        Visibility = was;
        _chrome.Reset(++clock);
        _chrome.Tick(clock + SettleMilliseconds);
        SetCursorHidden(false);
        Render();
        UpdateLayout();

        // And the remembered height back to the arranged one, since the fallback above left it holding a
        // measurement taken with the bar out of the tree.
        PlaceOverlays();

        // 收场那一问（offer 收掉 → 按钮 Collapsed → 同一坐标不再算它，倒计时表也停了）。上一关把
        // offer 摆起来过、这里已还回去，常态 wasOffer=false 才验 —— 免得探针自己把自己搞红。
        if (!wasOffer)
        {
            Want("offer 收掉后同一坐标不算按钮", !PressOnSkipButton(skipCentre));
            Want("offer 收掉后倒计时表停了", _skipCountdownTimer?.IsRunning != true);
        }

        // 窗口形态还回去（这一关开头把它摆成的是**普通窗口**），两档尺寸跟着回到用户进来时那一档。
        if (wasMaximized) { RequestMaximize(true); DrainWindowChange(); }
        if (wasFullscreen) { SetFullscreen(true); DrainWindowChange(); }
        ApplyChromeScale();
        UpdateLayout();

        return (wrong.Count == 0,
            string.Join("；", report) + (wrong.Count == 0 ? string.Empty : $"；不符：{string.Join('、', wrong)}"));
    }
}

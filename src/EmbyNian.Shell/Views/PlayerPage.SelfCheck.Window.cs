using System.Threading;
using EmbyNian.Infrastructure;
using EmbyNian.Shell.Interop;
using EmbyNian.Shell.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 窗口和画面那三关：标题栏右端的 最小化／最大化／关闭 在不在手够得着的地方、有没有互相压着
/// （<c>ProbeWindowCommands</c>）；章节预览那只悬停框落在哪、里面有什么、跟着控制条一起走没有（<c>ProbePeek</c>）；
/// 画面比例算出来的那个数有没有真的传到窗口、归零之后锁有没有解开（<c>ProbeAspect</c>）。
/// <para>
/// 几何这一头借主文件末尾那几个共用的量具（<c>BoundsOf</c>／<c>Overlaps</c>／<c>Encloses</c>），半个像素的余量
/// 也在那儿说明。拆成几个文件的缘由见 <c>PlayerPage.SelfCheck.cs</c> 的类注释。
/// </para>
/// </summary>
public sealed partial class PlayerPage
{
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
    /// 全屏那一档也一样三颗都在，只是最右边那颗换了图标、也换了点下去做的事（全屏时它是「窗口化」）。
    /// 前一版正好相反 —— 全屏时把那一颗收起来，理由是「窗口边就是显示器的边，最大化什么也做不了」；
    /// 理由没错，可**那个位置空着看上去就是少了东西**，2026-09-14 用户报的正是这一句。
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
            // 三颗**任何一档都在**，全屏也一样（2026-09-14 起）：右上角少一颗看上去就像少了东西，而全屏
            // 那一档的「还原」正是用户要的「窗口化」——鼠标移过去要退全屏的人，找的就是这个位置。
            if (button.Visibility != Visibility.Visible) trouble.Add($"{name}不见了");

            var box = BoundsOf(button);
            if (box.Width <= 0 || box.Height <= 0) trouble.Add($"{name}没有尺寸");
            else if (!Encloses(strip, box)) trouble.Add($"{name}越出了标题栏");
            else if (Overlaps(box, BoundsOf(StatsButton)) || Overlaps(box, BoundsOf(PinButton)))
                trouble.Add($"{name}压住了别的按钮");
        }

        // The glyphs the two toggles write. Read after Render, which is what calls UpdateMaximizeGlyph.
        // 全屏和最大化答的是同一支「还原」：「还原」在这里是两件事共用的一个手势 —— 全屏时点下去退出全屏
        // （窗口回到进全屏前的大小），最大化时点下去还原窗口。判据只有一处，两支各写各的图标就会在这里红。
        var restore = _window.OccupiesScreen;

        if (MaximizeGlyph.Glyph != Glyph(restore ? RestoreGlyphCode : MaximizeGlyphCode))
            trouble.Add("最大化图标与窗口不符");

        if (FullscreenGlyph.Glyph != Glyph(_window.Fullscreen ? FullscreenExitCode : FullscreenEnterCode))
            trouble.Add("全屏图标与窗口不符");

        // 全屏那一档也要量：三颗仍旧都在，最右那颗是「窗口化」（还原图标）。这一档坏起来的样子是
        // **少一颗** —— 从前的实现正是把它收起来，而那一档不进一次全屏根本看不见（2026-09-14 用户报的
        // 就是「全屏的时候右上角的窗口化怎么没了」）。切进去、量完、切回原样，前后都排版一次。
        var wasFullscreen = _window.Fullscreen;

        SetFullscreen(true);
        DrainWindowChange();
        UpdateLayout();

        foreach (var (name, button) in new (string Name, FrameworkElement Element)[]
                 {
                     ("最小化", MinimizeButton),
                     ("最大化", MaximizeButton),
                     ("关闭", CloseButton)
                 })
        {
            if (button.Visibility != Visibility.Visible) trouble.Add($"全屏时{name}不见了");
        }

        // 全屏那一档的「还原」：图标必须是还原那支 —— 它同时是「点下去会退出全屏」这件事唯一的可见证据。
        if (MaximizeGlyph.Glyph != Glyph(RestoreGlyphCode)) trouble.Add("全屏时窗口化图标不对");

        SetFullscreen(wasFullscreen);
        DrainWindowChange();
        UpdateLayout();

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
            + $"；右上角那颗={(_window.Fullscreen ? "窗口化" : restore ? "还原" : "最大化")}"
            + $"，窗口{(_window.IsMaximized ? "已最大化" : "未最大化")}"
            + $"，{(_window.Fullscreen ? "全屏" : "窗口化")}");
    }

    /// <summary>
    /// 把窗口切换那一趟异步走完再往下读。<c>ChangeWindowAsync</c> 自 2026-09-22 起会先冻住播放
    /// （<see cref="FreezeForHandoffAsync"/> 里 <c>await Task.Delay</c> 等 mpv 停住），而自检不真播放、那句
    /// <c>pause=yes</c> 落进空气 —— 于是那段 await 实打实占了约 200 毫秒，<c>SetFullscreen</c> 当拍不落地。
    /// 探针不等它，读到的还是切换前的状态（右上角图标、退全屏按比例整形都验不到，2026-09-23 闸门 4 两条回退
    /// 就是它）。照光标探针那一套泵消息、等 <see cref="WindowChange"/> 落地（<c>Pump</c> 见
    /// PlayerPage.SelfCheck.Cursor），给足冻结上限加抓帧兜底的余量。
    /// </summary>
    private void DrainWindowChange()
    {
        var until = Now + 2000;
        while (!WindowChange.IsCompleted && Now < until)
        {
            Pump();
            Thread.Sleep(16);
        }

        Pump();
    }

    /// <summary>
    /// 置顶开关: 「置顶开启后不要改变按键颜色，绘制一个置顶开启图标来替换」, both halves of it.
    /// <para>
    /// Three things fail invisibly here. The colour: the button is no longer a <c>ToggleButton</c>, so the
    /// framework's Checked storyboard has nothing to swap — but a style that stopped being applied, or a local
    /// background written back in, brings the accent block straight back, and this button is only on screen
    /// mid-film. The two pins: they are drawn geometry, so 「there are two of them and they are different」 is
    /// a question about the visual tree rather than about a font, and a copy-paste that left both states on the
    /// same path would look like a switch that does nothing. And the state: 置顶 lives on the window now,
    /// which is exactly what nobody looking at the screen can read back.
    /// </para>
    /// <para>
    /// The reset at the end is not housekeeping. A probe that left the main window in the topmost band would
    /// make 「全屏几何」 further down the report pass for the wrong reason.
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) ProbePin()
    {
        if (!Attached || _window is null) return (false, "播放层未接线");

        var was = Visibility;
        var wasTop = _window.TopMost;

        Visibility = Visibility.Visible;
        UpdateLayout();

        var clock = Now;
        _chrome.WakeFully(clock);
        Render();
        UpdateLayout();

        var report = new List<string>();
        var wrong = new List<string>();
        var reads = new List<(bool Pinned, string Back, string Shown, string Name, bool Top)>();

        foreach (var pinned in new[] { false, true })
        {
            SetPinned(pinned);
            UpdateLayout();

            var shown = (PinOnIcon.Visibility == Visibility.Visible, PinOffIcon.Visibility == Visibility.Visible) switch
            {
                (true, false) => "已置顶",
                (false, true) => "未置顶",
                (true, true) => "两颗都露着",
                _ => "两颗都收着"
            };

            reads.Add((pinned, Fill(PinButton), shown, PeerName(PinButton), _window.TopMost));
        }

        // The one reading the requirement is actually about, and it is compared against the eleven buttons
        // beside it rather than against a literal: 「the same as every other button in this strip」 is the claim,
        // and 返回 is the nearest one that has never been anything else.
        var reference = Fill(BackButton);

        report.Add($"底色：未置顶 {reads[0].Back}、已置顶 {reads[1].Back}（返回那颗 {reference}）");
        report.Add($"图标：{reads[0].Shown} / {reads[1].Shown}");
        report.Add($"名字：「{reads[0].Name}」/「{reads[1].Name}」");
        report.Add($"窗口置顶：{reads[0].Top} / {reads[1].Top}");

        Want("两档底色一样", string.Equals(reads[0].Back, reads[1].Back, StringComparison.Ordinal));
        Want("两档底色和别的按钮同一支", string.Equals(reads[0].Back, reference, StringComparison.Ordinal));
        Want("两档都不画底色", reads[0].Back is "不画" || reads[0].Back.StartsWith("00", StringComparison.Ordinal));
        Want("两档各露一颗图标", reads[0].Shown == "未置顶" && reads[1].Shown == "已置顶");
        Want("两档名字都不空", reads.All(read => read.Name.Trim().Length > 0));
        Want("两档名字不一样", !string.Equals(reads[0].Name, reads[1].Name, StringComparison.Ordinal));
        Want("状态跟着到了窗口", reads[0].Top == false && reads[1].Top);

        // 两颗真的是两个形状，而且都落在 16×16 的方框里、都居中 —— PathIcon 既不缩放几何也不居中（ShellPage
        // 那五颗图标的注释就是为这个坑写的），所以这三句只有量活的几何答得上。
        var offBox = PinOffIcon.Data?.Bounds ?? default;
        var onBox = PinOnIcon.Data?.Bounds ?? default;

        report.Add($"墨框：未置顶 {offBox.Width:0.0}×{offBox.Height:0.0} 中心 {offBox.X + offBox.Width / 2:0.0},{offBox.Y + offBox.Height / 2:0.0}"
            + $"；已置顶 {onBox.Width:0.0}×{onBox.Height:0.0} 中心 {onBox.X + onBox.Width / 2:0.0},{onBox.Y + onBox.Height / 2:0.0}");

        Want("两颗不是同一个形状", offBox != onBox && offBox.Width > 0 && onBox.Width > 0);
        Want("两颗都在 16×16 的框里", Inside(offBox) && Inside(onBox));
        Want("两颗都居中", Centred(offBox) && Centred(onBox));

        // 只报不判：拍照裁图要按这个框定位，而它跟着字体、缩放和这一排别的控件走。
        report.Add($"按钮 {BoundsOf(PinButton).Width:0}×{BoundsOf(PinButton).Height:0} @ {BoundsOf(PinButton).Left:0},{BoundsOf(PinButton).Top:0}");

        SetPinned(wasTop);
        _chrome.Reset(++clock);
        _chrome.Tick(clock + SettleMilliseconds);
        Render();
        SetCursorHidden(false);
        Visibility = was;
        UpdateLayout();

        Want("跑完复位了", _window.TopMost == wasTop);

        return (wrong.Count == 0,
            string.Join("；", report) + (wrong.Count == 0 ? string.Empty : $"；不符：{string.Join('、', wrong)}"));

        void Want(string what, bool ok)
        {
            if (!ok) wrong.Add(what);
        }

        // 半个像素的余量按主文件那一档；16 是标记里写死的方框边长，两颗几何都是按它算的。
        static bool Inside(Windows.Foundation.Rect box) =>
            box.Left >= -GeometrySlack && box.Top >= -GeometrySlack
            && box.Right <= 16 + GeometrySlack && box.Bottom <= 16 + GeometrySlack;

        static bool Centred(Windows.Foundation.Rect box) =>
            Math.Abs(box.X + box.Width / 2 - 8) <= 1 && Math.Abs(box.Y + box.Height / 2 - 8) <= 1;

        // 模板根上那一层的底色 —— Checked 那一族当年换的就是它。ContentPresenter 不是 Control（那一条第一趟
        // 读回来是「找不到模板根」），所以四种带 Background 的类型都要认。
        static string Fill(DependencyObject button)
        {
            if (VisualTreeHelper.GetChildrenCount(button) == 0) return "找不到模板根";

            var brush = VisualTreeHelper.GetChild(button, 0) switch
            {
                ContentPresenter presenter => presenter.Background,
                Control control => control.Background,
                Panel panel => panel.Background,
                Border border => border.Background,
                _ => null
            };

            return brush switch
            {
                null => "不画",
                SolidColorBrush solid => $"{solid.Color.A:X2}{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}",
                _ => "不是纯色"
            };
        }

        static string PeerName(UIElement element) =>
            Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer
                .CreatePeerForElement(element)?.GetName() ?? "";
    }

    /// <summary>
    /// 章节预览: where the hover box lands, what it contains, and that it leaves with the bar it belongs to.
    /// <para>
    /// Four separate things have been wrong here and none of them is visible to a compiler. The box is
    /// placed by a <c>TranslateTransform</c> this page computes rather than by layout, so an off-by-a-box-width
    /// sends it off the side of the picture — and the pointer is at the far end of the seek bar exactly when
    /// that happens, which is the one place a user will notice and the one place a centred test would not.
    /// The second is 视频进度条预览不会自动消失: the preview outliving its bar, stranded over the film with
    /// nothing to explain it. The third is 预览没有画面 — a fixed-size <c>Image</c> holding no source, which
    /// is what every file looks like on a server that never extracted chapter stills. The fourth is the
    /// floor under that one: no chapter marks at all, where the box is down to its time readout and an
    /// empty caption would leave a name-shaped gap above it.
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

        // And the floor under that: a file the server extracted no chapter marks from at all, which is a
        // whole class of server rather than an edge case. No picture and no name leaves the time on its
        // own — and it has to be left, because the slider's own tooltip only shows while the thumb is
        // being dragged, so hovering such a file used to produce nothing whatsoever. The empty caption
        // has to collapse rather than hold an empty line, or the chip is a name-shaped gap over a clock.
        ViewModel.ChapterCaption = "";
        UpdateLayout();

        var clockOnly = BoundsOf(ChapterPeek);
        var chip = ChapterCaption.Visibility == Visibility.Collapsed
                   && ChapterClock.Visibility == Visibility.Visible
                   && clockOnly.Height > 0
                   && clockOnly.Height < textOnly.Height - 8;

        ViewModel.ChapterCaption = "片头";
        UpdateLayout();

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

        var ok = track > 0 && strayed.Count == 0 && framed && hollow && chip && kept && gone;

        return (ok, $"进度条 {track:0} 逻辑像素宽，预览三处定位"
                    + (strayed.Count == 0 ? "都在画面内" : string.Join('、', strayed))
                    + $"；有缩略图时{(framed ? $"显示画面（{withStill.Height:0} 高）" : "没有显示画面")}"
                    + $"，没有缩略图时{(hollow ? $"只剩文字（{textOnly.Height:0} 高）" : "仍留着空画框")}"
                    + $"，连章节名也没有时{(chip ? $"只剩时间（{clockOnly.Height:0} 高）" : "仍留着空行")}"
                    + $"；浮层在时{(kept ? "保留" : "被收走")}"
                    + $"，浮层收起后{(gone ? "随之消失" : "仍然停留")}");
    }

    /// <summary>
    /// 画面比例: that the ratio the view model resolved actually reaches the window, that zero releases it, and
    /// that letting go puts the window back the size the user had.
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
    /// <para>
    /// 第三半是 2026-09-14 用户报的那一条：「进入播放页面然后再退出页面会保留播放页面的窗口大小比例」。
    /// 解锁只说明「能拉了」，说明不了「已经回去了」—— 从前窗口会一直留着片子整出来的那个形状（日志里一次
    /// 2.413:1 的宽银幕把窗口留成 1463×608）。这一段把整条路量出来：按画面比例整过之后让窗口回到播放前那一份
    /// （<see cref="HostWindow.RestoreBrowseGeometry"/>，页面在退出播放时调的就是它），窗口必须与整形前逐像素一致。
    /// </para>
    /// <para>
    /// 第四半是同一场事故的另一个入口：「窗口模式下调整窗口大小上下会出现黑边」。修的是「退全屏之后没按当前
    /// 画面比例再整形一次」—— 自动全屏开播后 40 毫秒就进去，mpv 半秒后才给出真比例，那句修正落在全屏里被丢掉，
    /// 退出全屏又把按猜错比例整好的矩形放了回来。这里把那趟走一遍（进全屏 → 退全屏），要求退出来之后客户区
    /// <b>真的是那个比例</b> —— 判据落在比例上而不是落在「有没有调 FitToPicture」上，因为要的正是屏幕上的结果。
    /// </para>
    /// <para>
    /// 第五半是 2026-09-18 用户报的那一条的自动全屏变体：播放停止时比例先归零，退出播放才退出全屏，退出全屏
    /// 那一下的 WM_SIZE 到来时「全屏已了、比例已零」，窗口若没有「播放占着窗口不记几何」的守卫（
    /// <see cref="HostWindow.RememberPlacement"/> 的 FreeSizing 一条），就会把退出全屏回到的播放形状记成浏览
    /// 几何 —— 紧跟着的还原看见矩形没变就静默收工，窗口从此留在片子的形状上（实录：0.0.14 的 14:56 场，
    /// 停在 1511×626，还原日志一行都没有）。这里把那趟原样重演：占窗 → 记账 → 按画面比例整形 → 全屏 → 归零
    /// → 退全屏 → 还窗，终点必须回到记账时那一份。
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) ProbeAspect()
    {
        if (!Attached || _window is null) return (false, "播放层未接线");
        if (ViewModel.PlayingNow) return (false, "比例探针不能在真实播放期间运行");

        var restore = _window.PictureAspect;

        // 整形之前那个矩形。第三半要比的就是它。
        if (_window.Handle == IntPtr.Zero) return (false, "窗口还没建");

        var haveOriginal = Native.GetWindowRect(_window.Handle, out var was);
        if (!haveOriginal) return (false, "读不到窗口矩形");

        var wasFullscreen = _window.Fullscreen;

        if (!ViewModel.PictureInHostWindow)
        {
            OnPictureAspectChanged(16d / 9d);
            OnPictureAspectChanged(0);
            SetFullscreen(true);
            SetFullscreen(false);
            var haveNativeNow = Native.GetWindowRect(_window.Handle, out var nativeNow);
            var unchanged = haveNativeNow
                && nativeNow.Left == was.Left && nativeNow.Top == was.Top
                && nativeNow.Width == was.Width && nativeNow.Height == was.Height
                && _window.PictureAspect == restore && _window.Fullscreen == wasFullscreen;
            return (unchanged, "原生管线：比例与全屏请求不改变 WinUI 控制窗口；"
                + (unchanged ? "几何和状态保持不变" : "控制窗口被误改"));
        }

        // Through the page's own handler rather than by writing the property: the wiring is the subject.
        OnPictureAspectChanged(16d / 9d);
        var took = Math.Abs(_window.PictureAspect - 16d / 9d) < 0.001;

        string fitted;
        var shaped = true;

        if (_window.OccupiesScreen)
        {
            // Both mean the edges belong to the monitor and FitToPicture stands aside, so asking about the
            // client shape here would be asking about the screen's. 两种形态同一个答主、同一句话报，
            // 所以这里的措辞也不必再分两支（归一那一句见 OccupiesScreen）。
            fitted = $"{WindowForms.Name(_window.Form)}，不整形";
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

        // 第四半。先故意把窗口摆成一个**和画面不一致**的形状（16:9 的窗口放 4:3 的画面），再走一趟全屏往返 ——
        // 进全屏时窗口被迫变成显示器的形状，出来时若没人按画面比例补一次，就会留下全屏前那个 16:9。
        //
        // 摆的是「窗口比画面宽」，所以退全屏之后若没整形，客户区会明显不是 4:3，这一关当场红。
        _window.PictureAspect = 4d / 3d;

        SetFullscreen(true);
        DrainWindowChange();
        UpdateLayout();
        SetFullscreen(false);
        DrainWindowChange();
        UpdateLayout();

        var afterSize = _window.ClientSize;
        var afterRatio = afterSize.Height > 0 ? (double)afterSize.Width / afterSize.Height : 0;
        var refitted = !_window.Fullscreen
            && Math.Abs(afterSize.Width - Math.Round(afterSize.Height * 4d / 3d)) <= 1;

        // 第三半要比的那一份，得先摆回探针进来时那个矩形再记 —— 上面那一趟全屏往返把窗口挪走了，而下面
        // RestoreBrowseGeometry 要还原到的正是**播放前**那一份。这里把窗口摆回去、再让窗口自己重记一次，
        // 于是「整形前 <-> 还原后」这两头量的是同一个矩形，这一段才是它声称在量的东西。
        if (haveOriginal)
        {
            Native.SetWindowPos(
                _window.Handle, Native.HwndTop,
                was.Left, was.Top, was.Width, was.Height,
                Native.SwpNoZOrder | Native.SwpNoActivate);
        }

        OnPictureAspectChanged(0);
        var cleared = _window.PictureAspect == 0;
        _window.CaptureBrowseGeometry();

        // 退出播放那一趟。窗口自己记着整形之前那份几何（<c>HostWindow.PictureAspect</c> 的 setter 里在写下一个
        // 非零比例时记的），这里只把还原走一遍。自检不真播片子，所以驱动的是页面在 <c>LeavePlayer</c> 里用的同一
        // 条路 —— 少了这一步，「解锁」会全绿，而屏幕上窗口的形状还一直是片子的。
        _window.RestoreBrowseGeometry();

        var haveNow = Native.GetWindowRect(_window.Handle, out var now);
        var back = haveNow
            && now.Left == was.Left && now.Top == was.Top
            && now.Width == was.Width && now.Height == was.Height;

        var put = haveNow ? $"{now.Width}×{now.Height} @ {now.Left},{now.Top}" : "读不到";

        // 第五半。此刻窗口在 was、比例已归零、FreeSizing 是 false —— 正好是自动全屏那场退出路的起点。
        // FreeSizing 先立起来（播放占窗），按画面比例整出播放形状（比例写入方顺手把 was 记成浏览几何），
        // 走一趟全屏；归零放在退全屏**之前**（播放停止先于退出播放，次序是这次事故的钥匙），退全屏那一下
        // 的 WM_SIZE 若没有 FreeSizing 守卫就会把播放形状写进浏览几何 —— 还原看见矩形没变，静默收工。
        var freeWas = _window.FreeSizing;
        _window.FreeSizing = true;

        OnPictureAspectChanged(2.413);
        var haveShaped = Native.GetWindowRect(_window.Handle, out var shapedRect);

        SetFullscreen(true);
        DrainWindowChange();
        OnPictureAspectChanged(0);
        SetFullscreen(false);
        DrainWindowChange();

        _window.FreeSizing = false;
        _window.RestoreBrowseGeometry();

        var haveAfter = Native.GetWindowRect(_window.Handle, out var afterReplay);
        var replay = haveShaped && haveAfter
            && afterReplay.Left == was.Left && afterReplay.Top == was.Top
            && afterReplay.Width == was.Width && afterReplay.Height == was.Height;

        _window.FreeSizing = freeWas;

        if (_window.Fullscreen != wasFullscreen) SetFullscreen(wasFullscreen);

        _window.PictureAspect = restore;

        return (took && shaped && refitted && cleared && back && replay,
            $"16:9 {(took ? "已交给窗口" : "没有传到窗口")}；{fitted}；"
            + $"全屏往返后（画面 4:3）客户区 {afterSize.Width}×{afterSize.Height} = {afterRatio:0.000}"
            + (refitted ? "，已按画面比例补整" : "，不是 4:3 —— 退全屏后没按当前画面比例再整形一次") + "；"
            + $"归零{(cleared ? "已解除" : "未解除")}；"
            + $"退出播放后窗口 {put}"
            + (back ? "，与整形前一致" : $"，整形前是 {was.Width}×{was.Height} @ {was.Left},{was.Top} —— 没还原") + "；"
            + $"自动全屏场重演（整形→全屏→归零→退全屏→还原）"
            + (replay ? "回到记账时的矩形"
                : $"停在 {(haveAfter ? $"{afterReplay.Width}×{afterReplay.Height} @ {afterReplay.Left},{afterReplay.Top}" : "读不到")}"
                  + $"（播放形状 {shapedRect.Width}×{shapedRect.Height}），应回到 {was.Width}×{was.Height} @ {was.Left},{was.Top}"
                  + " —— 播放形状被记成了浏览几何"));
    }
}

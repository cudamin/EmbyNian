using Microsoft.UI.Xaml;
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
}

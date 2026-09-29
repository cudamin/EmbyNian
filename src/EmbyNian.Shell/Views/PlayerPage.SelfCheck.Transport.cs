using EmbyNian.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace EmbyNian.Shell.Views;

public sealed partial class PlayerPage
{
    internal (bool Ok, string Detail) ProbeTransportLayout()
    {
        if (!Attached || ViewModel.PlayingNow) return (false, "不具备隔离检查条件");
        var was = Visibility;
        var oldWidth = Root.Width;
        var oldHeight = Root.Height;
        var episodes = ViewModel.EpisodeControlsVisible;
        var versions = ViewModel.VersionControlsVisible;
        var status = ViewModel.Status;
        var chapters = ViewModel.ChapterMarks;
        var issues = new List<string>();
        var sizes = new[] { 1280, 680, 560, 520, 420, 300, 240, 180 };
        try
        {
            Visibility = Visibility.Visible;
            Hold(true, ChromeHold.Shot);
            ViewModel.EpisodeControlsVisible = true;
            ViewModel.VersionControlsVisible = true;
            ViewModel.SetTransportProbeStatus(new PlayerStatus { Duration = 1200, Position = 360, Loaded = true },
                [new(0, "开始"), new(120, "第一章"), new(600, "第二章")]);
            Root.Height = 720;
            foreach (var width in sizes)
            {
                Root.Width = width;
                UpdateLayout();
                ArrangeTransport();
                UpdateLayout();
                var controls = new FrameworkElement[] { PreviousButton, PlayButton, NextButton, TransportStatsButton,
                    ChapterButton, EpisodeButton, VersionButton, PreviousChapterButton, SpeedStrip,
                    SpeedButton, NextChapterButton, SubtitleButton, AudioButton, FullscreenButton };
                Rect? previous = null;
                foreach (var control in controls.Where(control => control.Visibility == Visibility.Visible))
                {
                    var box = BoundsOf(control);
                    if (box.Left < -1 || box.Right > width + 1 || box.Width <= 0)
                        issues.Add($"{width}: {control.Name}越界或无尺寸");
                    if (previous is { } prior && prior.Right > box.Left + 1)
                        issues.Add($"{width}: {control.Name}与前一控件重叠");
                    previous = box;
                }
                if (width < 680 && PlayButton.Visibility == Visibility.Visible
                    && string.IsNullOrWhiteSpace(FrameworkElementAutomationPeer.CreatePeerForElement(PlayButton)?.GetName()))
                    issues.Add($"{width}: 播放键无读屏名称");
                if (Math.Abs(BoundsOf(SeekTrack).Bottom - BoundsOf(Bar).Bottom) > 1)
                    issues.Add($"{width}: 时间轴没有贴底");
                if (width >= 680 && PlayButton.Visibility != Visibility.Collapsed)
                    issues.Add($"{width}: 宽窗重复播放键未隐藏");
            }
            // 「更多」那一棵 2026-09-27 晚不再挂在控制条那颗按钮上（那颗按钮撤了），改拼进右键画面菜单 ——
            // 这里仍把它整棵拼一遍，只问「延迟入口已移除」那一条老判据；拼装目标改成本地一只临时浮层。
            var moreProbe = new MenuFlyout();
            OnMoreMenuOpening(moreProbe, Root);
            if (moreProbe.Items.OfType<MenuFlyoutSubItem>().Any(row => row.Text.Contains("延迟", StringComparison.Ordinal)))
                issues.Add("更多菜单仍有延迟入口");
            var layers = ProbeTimelineArtwork();
            if (!layers.Ok) issues.Add(layers.Detail);
            var timeline = ViewModel.ProbeTimelineState();
            if (!timeline.Ok) issues.Add(timeline.Detail);
            return (issues.Count == 0, $"{string.Join('/', sizes)} 宽度：按钮无重叠、贴底时间轴、窄窗播放键读屏；{layers.Detail}；延迟入口已移除；{timeline.Detail}"
                + (issues.Count == 0 ? "" : "；" + string.Join("；", issues)));
        }
        finally
        {
            ViewModel.EpisodeControlsVisible = episodes;
            ViewModel.VersionControlsVisible = versions;
            ViewModel.SetTransportProbeStatus(status, chapters);
            Root.Width = oldWidth;
            Root.Height = oldHeight;
            Visibility = was;
            Hold(false, ChromeHold.Shot);
            _chrome.Reset(Now);
            _chrome.Tick(Now + SettleMilliseconds);
            SetCursorHidden(false);
            Render();
            UpdateLayout();
            ArrangeTransport();
        }
    }

    private (bool Ok, string Detail) ProbeTimelineArtwork()
    {
        Root.Width = 1000;
        Root.Height = 600;
        var chapters = new SkipChapter[] { new(0, "片头"), new(90, "正片"), new(900, "片尾") };
        var status = new PlayerStatus
        {
            Loaded = true,
            Duration = 1000,
            Position = 250,
            LoopA = 200,
            LoopB = 400,
            Cache = new TimelineCache([new(0, 300), new(500, 600)], Ahead: 25),
            CacheMode = "yes"
        };
        ViewModel.SetTransportProbeStatus(status, chapters);
        GrowTimeline(1);
        UpdateLayout();
        RenderTimelineLayers();
        RenderChapterTicks();
        var width = SeekTrack.ActualWidth;
        var height = SeekTrack.ActualHeight;
        var issues = new List<string>();
        if (TimelineSections.Children.Count != 2) issues.Add("片头片尾没有画成两个色段");
        if (_uncached.Count != 2 || _cacheLightFigures.Count == 0) issues.Add("分离缓存没有纹理");
        if (TimelineLoops.Children.Count != 2) issues.Add("A/B 两端没有标记");
        if (ThinTimelineSections.Children.Count != 2 || ThinTimelineCache.Children.Count == 0)
            issues.Add("收起细条没有保留色段与缓存");
        if (ChapterTicks.Children.Count != 3) issues.Add("章节起点未全部保留");
        if (TimelineBufferedText.Text != "25s" || TimelineBufferedText.Visibility != Visibility.Visible) issues.Add("缓存秒数缺失");
        var chapterX = TimelineScale.XAt(90, width, status.Duration);
        UpdateTimelineHover(chapterX + 3, 1);
        if (_hoveredChapter != 1 || ViewModel.ChapterCaption != "正片"
            || Math.Abs(SeekHoverLine.Margin.Left - (chapterX - 0.5)) > 0.01) issues.Add("悬停章名或竖线没有吸附到落点");
        if (!PressTimelineChapter(chapterX + 3, 1) || Math.Abs(ViewModel.SeekValue - 90) > 0.01
            || ViewModel.TimelineBusy) issues.Add("菱形点击没有直接跳章");
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(SeekSlider);
        if (peer?.GetPattern(PatternInterface.RangeValue) is null) issues.Add("时间轴丢失 RangeValue 自动化接口");
        GrowTimeline(0.1);
        UpdateLayout();
        if (SeekTrack.ActualHeight >= height || TimelineLabels.Opacity > 0.01) issues.Add("收起时高度和文字未同步");
        GrowTimeline(1);
        UpdateLayout();
        HideChapterPeek();
        ViewModel.SetTransportProbeStatus(new PlayerStatus(), []);
        if (TimelineSections.Children.Count != 0 || TimelineLoops.Children.Count != 0 || ChapterTicks.Children.Count != 0
            || ThinTimelineSections.Children.Count != 0 || ThinTimelineCache.Children.Count != 0)
            issues.Add("换片后留下旧标记");
        return (issues.Count == 0, issues.Count == 0 ? "色段/缓存空洞/缓存秒数/A-B/二维吸附/读屏/收缩/换片清理通过"
            : string.Join("、", issues));
    }

    internal async Task CaptureTransportSamplesAsync(string directory)
    {
        if (!Attached || ViewModel.PlayingNow) return;
        var was = Visibility;
        var stage = Stage.Visibility;
        var oldWidth = Root.Width;
        var oldHeight = Root.Height;
        var episodes = ViewModel.EpisodeControlsVisible;
        var versions = ViewModel.VersionControlsVisible;
        var status = ViewModel.Status;
        var chapters = ViewModel.ChapterMarks;
        try
        {
            Visibility = Visibility.Visible;
            Stage.Visibility = Visibility.Visible;
            Hold(true, ChromeHold.Shot);
            ViewModel.EpisodeControlsVisible = true;
            ViewModel.VersionControlsVisible = true;
            ViewModel.SetTransportProbeStatus(new PlayerStatus
            {
                Duration = 5400,
                Position = 1620,
                CacheEnd = 2160,
                Volume = 75,
                Loaded = true,
                Cache = new TimelineCache([new(0, 1850), new(2200, 2800), new(3900, 4500)], Ahead: 28),
                CacheMode = "yes",
                LoopA = 2400,
                LoopB = 3000
            }, [new(0, "片头"), new(450, "第一章"), new(1350, "第二章"), new(3150, "第三章"), new(4800, "片尾")]);
            foreach (var (width, height) in new[] { (1280, 720), (560, 420), (320, 260) })
            {
                Root.Width = width;
                Root.Height = height;
                UpdateLayout();
                ArrangeTransport();
                _chrome.FlashRail(Now);
                Render();
                UpdateLayout();
                await Task.Delay(250).ConfigureAwait(true);
                var bitmap = new RenderTargetBitmap();
                await bitmap.RenderAsync(Root, width, height);
                var pixels = await bitmap.GetPixelsAsync();
                var bytes = new byte[pixels.Length];
                using (var reader = DataReader.FromBuffer(pixels)) reader.ReadBytes(bytes);
                using var stream = new InMemoryRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                    (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
                await encoder.FlushAsync();
                using var output = File.Create(Path.Combine(directory, $"player-transport-{width}.png"));
                stream.Seek(0);
                await stream.AsStreamForRead().CopyToAsync(output);
            }
        }
        finally
        {
            ViewModel.EpisodeControlsVisible = episodes;
            ViewModel.VersionControlsVisible = versions;
            ViewModel.SetTransportProbeStatus(status, chapters);
            Root.Width = oldWidth;
            Root.Height = oldHeight;
            Stage.Visibility = stage;
            Visibility = was;
            Hold(false, ChromeHold.Shot);
            _chrome.Reset(Now);
            _chrome.Tick(Now + SettleMilliseconds);
            SetCursorHidden(false);
            Render();
            UpdateLayout();
            ArrangeTransport();
        }
    }
}

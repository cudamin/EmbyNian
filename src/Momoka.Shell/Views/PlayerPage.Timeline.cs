using System.Globalization;
using Momoka.Playback;
using Momoka.Shell.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Momoka.Shell.Views;

public sealed partial class PlayerPage
{
    private readonly SolidColorBrush _timelineInk = new(ThemeHost.ToColor(TimelineChapterMap.Foreground));
    private readonly SolidColorBrush _timelinePaper = new(ThemeHost.ToColor(TimelineChapterMap.Background));
    private readonly SolidColorBrush _timelineOpening = new(ThemeHost.ToColor(TimelineChapterMap.OpeningColor));
    private readonly SolidColorBrush _timelineEnding = new(ThemeHost.ToColor(TimelineChapterMap.EndingColor));
    private readonly SolidColorBrush _timelineAd = new(ThemeHost.ToColor(TimelineChapterMap.AdvertisementColor));
    private readonly SolidColorBrush _timelineFallback = new(ThemeHost.ToColor(TimelineChapterMap.Background.WithAlpha(0x70)));
    private TimelineCache? _paintedCache;
    private IReadOnlyList<TimelineRange> _uncached = [];
    private string? _paintedCacheMode;
    private bool _paintedNetwork;
    private double _paintedDuration;
    private bool _timelineHovering;
    private int _hoveredChapter = -1;
    private double _timelineStrength = 1;
    private readonly RectangleGeometry _cacheClip = new();
    private readonly PathGeometry _cacheLightGeometry = new();
    private readonly PathGeometry _cacheDarkGeometry = new();
    private Path? _cacheLight;
    private Path? _cacheDark;
    private double _cacheWidth;
    private double _cacheHeight;
    private IReadOnlyList<TimelineRange>? _drawnUncached;
    private readonly List<PathFigure> _cacheLightFigures = [];
    private readonly List<PathFigure> _cacheDarkFigures = [];
    private readonly List<LineSegment> _cacheLightLines = [];
    private readonly List<LineSegment> _cacheDarkLines = [];

    private double TimelineFullHeight => _window is { Fullscreen: true } or { IsMaximized: true } ? 40 : 31;

    private void WireTimelineArtwork()
    {
        SeekSlider.Foreground = _timelineInk;
        TimelineMaterial.Background = _timelineFallback;
        PositionText.Foreground = DurationText.Foreground = _timelineInk;
        TimelineBufferedText.Foreground = _timelineInk;
        foreach (var label in TimelinePlayedLabels.Children.OfType<TextBlock>()) label.Foreground = _timelinePaper;
        Bar.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Bar.AddHandler(PointerMovedEvent, new PointerEventHandler(OnTimelineMarkerHover), true);
        Bar.AddHandler(PointerPressedEvent, new PointerEventHandler(OnTimelineMarkerPressed), true);
        CacheBar.Clip = _cacheClip;
        _cacheLight = new Path { Data = _cacheLightGeometry, Stroke = _timelineInk, StrokeThickness = 1, Opacity = 0.2 };
        _cacheDark = new Path { Data = _cacheDarkGeometry, Stroke = _timelinePaper, StrokeThickness = 1, Opacity = 0.4 };
        CacheBar.Children.Add(_cacheLight);
        CacheBar.Children.Add(_cacheDark);
    }

    private void OnSeekTrackResized(object sender, SizeChangedEventArgs e)
    {
        RenderChapterTicks();
        RenderTimelineLayers();
        PaintTransportReadouts();
    }

    private void RenderChapterTicks()
    {
        var duration = Attached && ViewModel.Status.HasDuration ? ViewModel.Status.Duration : 0;
        _ticksFor = duration;
        RenderChapterMap(Attached ? ViewModel.TimelineChapters : TimelineChapterMap.Empty, SeekTrack.ActualWidth, duration);
    }

    private void RenderChapterTicks(IReadOnlyList<SkipChapter> marks, double width, double duration) =>
        RenderChapterMap(TimelineChapterMap.Build(marks), width, duration);

    private void RenderChapterMap(TimelineChapterMap map, double width, double duration)
    {
        var drawn = 0;
        var height = SeekTrack.ActualHeight > 0 ? SeekTrack.ActualHeight : TimelineFullHeight;
        if (width > 1 && duration > 0)
        {
            for (var i = 0; i < map.Chapters.Count; i++)
            {
                var mark = map.Chapters[i];
                if (mark.Start > duration) continue;
                var radius = TimelineScale.ChapterRadius(height, TimelineFullHeight) * (i == _hoveredChapter ? 2 : 1);
                var path = GetPath(ChapterTicks, drawn++);
                path.Fill = _timelineInk;
                path.Stroke = _timelinePaper;
                path.StrokeThickness = 1;
                path.Opacity = 0.8;
                SetPolygon(path, [new(0, radius), new(radius, 0), new(radius * 2, radius), new(radius, radius * 2)]);
                Canvas.SetLeft(path, TimelineScale.XAt(mark.Start, width, duration) - radius);
                Canvas.SetTop(path, -radius);
                Canvas.SetZIndex(path, i == _hoveredChapter ? 1 : 0);
            }
        }
        TrimChildren(ChapterTicks, drawn);
    }

    private void RenderTimelineLayers()
    {
        if (!Attached || _cacheLight is null || _cacheDark is null) return;
        var status = ViewModel.Status;
        var width = SeekTrack.ActualWidth;
        var height = SeekTrack.ActualHeight;
        if (width <= 1 || height <= 0) return;
        if (!ReferenceEquals(_paintedCache, status.Cache) || _paintedCacheMode != status.CacheMode
            || _paintedNetwork != status.NetworkSource || _paintedDuration != status.Duration)
        {
            _paintedCache = status.Cache;
            _paintedCacheMode = status.CacheMode;
            _paintedNetwork = status.NetworkSource;
            _paintedDuration = status.Duration;
            _uncached = (status.Cache ?? TimelineCache.Empty).Uncached(status.Duration, status.CacheMode, status.NetworkSource);
        }
        ChapterPeek.MaxWidth = Math.Max(40, Root.ActualWidth - 16);
        var fontSize = (double)Application.Current.Resources["EgTimelineFontSize"] * (TimelineFullHeight / 31);
        foreach (var label in TimelineLabels.Children.OfType<TextBlock>().Concat(TimelinePlayedLabels.Children.OfType<TextBlock>()))
            label.FontSize = label == TimelineBufferedText || label == TimelineBufferedPlayedText ? fontSize * 0.8 : fontSize;
        ChapterClock.FontSize = ChapterCaption.FontSize = fontSize;
        PaintCacheTexture(width, height, status.Duration);
        var count = 0;
        if (status.HasDuration)
        {
            foreach (var section in ViewModel.TimelineChapters.Sections)
            {
                if (section.Start >= status.Duration || section.End <= section.Start) continue;
                var left = section.Start < 0.1 ? 0 : TimelineScale.XAt(section.Start, width, status.Duration);
                var right = section.End > status.Duration - 0.1 ? width : TimelineScale.XAt(section.End, width, status.Duration);
                var block = count < TimelineSections.Children.Count ? (Border)TimelineSections.Children[count]
                    : new Border();
                if (count == TimelineSections.Children.Count) TimelineSections.Children.Add(block);
                block.Width = Math.Max(0, right - left);
                block.Height = Math.Max(0, height - 1);
                block.Background = section.Kind switch
                {
                    TimelineSectionKind.Opening => _timelineOpening,
                    TimelineSectionKind.Ending => _timelineEnding,
                    _ => _timelineAd
                };
                Canvas.SetLeft(block, left);
                Canvas.SetTop(block, 1);
                count++;
            }
        }
        TrimChildren(TimelineSections, count);
        PaintLoopMarkers(width, height, status);
        PaintThinTimeline(status);
        PaintBufferedClock(width, status);
        var textOpacity = TimelineScale.TextOpacity(height, PositionText.FontSize);
        TimelineLabels.Opacity = TimelinePlayedLabels.Opacity = textOpacity;
        SeekSlider.IsEnabled = status.HasDuration;
        AutomationProperties.SetHelpText(SeekSlider, status.HasDuration
            ? $"已播放 {ViewModel.PositionClock}，剩余 {ViewModel.RemainingClock}；{ViewModel.TimelineChapters.CaptionAt(status.Position)}。滚轮前后 5 秒，章节标记可精确跳转。"
            : "媒体时长未知，暂不可跳转");
    }

    private void PaintCacheTexture(double width, double height, double duration)
    {
        _cacheLight!.Opacity = 0.4 - 0.2 * _timelineStrength;
        _cacheDark!.Opacity = 0.6 - 0.2 * _timelineStrength;
        if (ReferenceEquals(_drawnUncached, _uncached) && _cacheWidth == width && _cacheHeight == height) return;
        _drawnUncached = _uncached;
        _cacheWidth = width;
        _cacheHeight = height;
        _cacheClip.Rect = new Rect(0, 1, width, Math.Max(0, height - 1));
        var light = 0;
        var dark = 0;
        if (duration > 0)
        {
            foreach (var range in _uncached)
            {
                var left = range.Start < 0.5 ? 0 : Math.Floor(TimelineScale.XAt(range.Start, width, duration));
                var right = range.End > duration - 0.5 ? width : Math.Ceiling(TimelineScale.XAt(range.End, width, duration));
                for (var x = Math.Floor((left - height) / 8) * 8; x < right; x += 8)
                {
                    AddStripe(_cacheLightGeometry, _cacheLightFigures, _cacheLightLines, ref light, x, left, right, height);
                    AddStripe(_cacheDarkGeometry, _cacheDarkFigures, _cacheDarkLines, ref dark, x + 3, left, right, height);
                }
            }
        }
        for (var i = light; i < _cacheLightLines.Count; i++)
            _cacheLightFigures[i].StartPoint = _cacheLightLines[i].Point = new Point();
        for (var i = dark; i < _cacheDarkLines.Count; i++)
            _cacheDarkFigures[i].StartPoint = _cacheDarkLines[i].Point = new Point();
    }

    private static void AddStripe(PathGeometry geometry, List<PathFigure> figures, List<LineSegment> lines,
        ref int index, double x, double left, double right, double height)
    {
        var begin = Math.Max(left, x);
        var end = Math.Min(right, x + height);
        if (end <= begin) return;
        if (index == figures.Count)
        {
            var figure = new PathFigure { IsClosed = false, IsFilled = false };
            var line = new LineSegment();
            figure.Segments.Add(line);
            geometry.Figures.Add(figure);
            figures.Add(figure);
            lines.Add(line);
        }
        figures[index].StartPoint = new(begin, height - (begin - x));
        lines[index].Point = new(end, height - (end - x));
        index++;
    }

    private void PaintLoopMarkers(double width, double height, PlayerStatus status)
    {
        var count = 0;
        if (status.HasDuration)
        {
            Paint(status.LoopA, false);
            Paint(status.LoopB, true);
        }
        TrimChildren(TimelineLoops, count);
        void Paint(double? time, bool end)
        {
            if (time is null || !double.IsFinite(time.Value) || time < 0 || (end && time <= 0) || time > status.Duration) return;
            var path = GetPath(TimelineLoops, count++);
            var radius = Math.Round(Math.Min(Math.Max(8, height * 0.25), height));
            path.Fill = _timelineInk;
            path.Stroke = _timelinePaper;
            path.StrokeThickness = 1;
            path.Opacity = 0.8;
            SetPolygon(path, end ? [new(0, 0), new(3, 0), new(radius, radius), new(0, radius)]
                : [new(radius, 0), new(radius, radius), new(0, radius), new(radius - 3, 0)]);
            Canvas.SetLeft(path, TimelineScale.XAt(time.Value, width, status.Duration) - (end ? 0 : radius));
            Canvas.SetTop(path, height - radius);
        }
    }

    private void PaintThinTimeline(PlayerStatus status)
    {
        var width = Root.ActualWidth;
        if (width <= 1) return;
        ThinProgress.Foreground = _timelineInk;
        var segments = 0;
        if (status.HasDuration)
        {
            foreach (var section in ViewModel.TimelineChapters.Sections)
            {
                if (section.Start >= status.Duration || section.End <= section.Start) continue;
                var block = GetBorder(ThinTimelineSections, segments++);
                var left = TimelineScale.XAt(section.Start, width, status.Duration);
                var right = section.End >= status.Duration ? width : TimelineScale.XAt(section.End, width, status.Duration);
                block.Width = Math.Max(0, right - left);
                block.Height = 2;
                block.Background = section.Kind == TimelineSectionKind.Advertisement ? _timelineAd : _timelineOpening;
                Canvas.SetLeft(block, left);
            }
        }
        TrimChildren(ThinTimelineSections, segments);
        var ticks = 0;
        if (status.HasDuration)
        {
            foreach (var gap in _uncached)
            {
                var start = TimelineScale.XAt(gap.Start, width, status.Duration);
                var end = TimelineScale.XAt(gap.End, width, status.Duration);
                for (var x = Math.Ceiling(start / 4) * 4; x < end; x += 4)
                {
                    var tick = GetBorder(ThinTimelineCache, ticks++);
                    tick.Background = _timelinePaper;
                    tick.Opacity = 0.6;
                    tick.Width = 1;
                    tick.Height = 2;
                    Canvas.SetLeft(tick, x);
                }
            }
        }
        TrimChildren(ThinTimelineCache, ticks);
    }

    private static Border GetBorder(Canvas canvas, int index)
    {
        if (index < canvas.Children.Count) return (Border)canvas.Children[index];
        var border = new Border();
        canvas.Children.Add(border);
        return border;
    }

    private void PaintBufferedClock(double width, PlayerStatus status)
    {
        var seconds = (status.Cache ?? TimelineCache.Empty).BufferedSeconds(status.Speed);
        var show = status.HasDuration && (status.Cache ?? TimelineCache.Empty).IsVisible(status.Duration, status.CacheMode, status.NetworkSource)
            && seconds is >= 0 and < 60;
        var text = show ? Math.Round(seconds!.Value, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture) + "s" : "";
        TimelineBufferedText.Text = TimelineBufferedPlayedText.Text = text;
        var measure = new Size(double.PositiveInfinity, double.PositiveInfinity);
        PositionText.Measure(measure);
        DurationText.Measure(measure);
        TimelineBufferedText.Measure(measure);
        var low = PositionText.DesiredSize.Width + 18;
        var high = width - DurationText.DesiredSize.Width - 18 - TimelineBufferedText.DesiredSize.Width;
        show &= high >= low;
        TimelineBufferedText.Visibility = TimelineBufferedPlayedText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        var left = Math.Clamp(width * ViewModel.SeekValue / PlayerViewModel.SeekScale + 5, low, high);
        TimelineBufferedText.Margin = TimelineBufferedPlayedText.Margin = new Thickness(left, 0, 0, 0);
    }

    private int TimelineChapterAt(double x, double y) => !Attached ? -1 : TimelineScale.ChapterAt(x, y,
        SeekTrack.ActualWidth, SeekTrack.ActualHeight, TimelineFullHeight, ViewModel.Status.Duration,
        ViewModel.TimelineChapters.Chapters);

    private bool PressTimelineChapter(double x, double y)
    {
        var index = TimelineChapterAt(x, y);
        if (index < 0 || ViewModel.TimelineBusy) return false;
        ViewModel.SeekTimelineChapter(ViewModel.TimelineChapters.Chapters[index].Start);
        UpdateTimelineHover(x, y);
        return true;
    }

    private void OnTimelineMarkerHover(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(SeekTrack).Position;
        if (point.Y < 0)
        {
            if (TimelineChapterAt(point.X, point.Y) >= 0) UpdateTimelineHover(point.X, point.Y);
            else if (!_timelinePointerDown) HideChapterPeek();
        }
    }

    private void OnTimelineMarkerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Handled) return;
        var point = e.GetCurrentPoint(SeekTrack);
        if (point.Position.Y < 0 && point.Properties.IsLeftButtonPressed && PressTimelineChapter(point.Position.X, point.Position.Y))
            e.Handled = true;
    }

    private void OnSeekTrackHover(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(SeekTrack).Position;
        UpdateTimelineHover(point.X, point.Y);
    }

    private void UpdateTimelineHover(double x, double y)
    {
        if (!Attached || !ViewModel.Status.HasDuration || SeekTrack.ActualWidth <= 1 || _holds.HasFlag(ChromeHold.Speed)) return;
        _timelineHovering = true;
        FadeStrip(TransportRow, 0);
        var chapter = _timelinePointerDown ? -1 : TimelineChapterAt(x, y);
        if (chapter != _hoveredChapter)
        {
            _hoveredChapter = chapter;
            RenderChapterTicks();
        }
        var seconds = chapter >= 0 ? ViewModel.TimelineChapters.Chapters[chapter].Start
            : TimelineScale.TimeAt(x, SeekTrack.ActualWidth, ViewModel.Status.Duration);
        var at = TimelineScale.XAt(seconds, SeekTrack.ActualWidth, ViewModel.Status.Duration);
        SeekHoverLine.Background = seconds <= ViewModel.SeekValue / PlayerViewModel.SeekScale * ViewModel.Status.Duration
            ? _timelinePaper : _timelineInk;
        SeekHoverLine.Visibility = Visibility.Visible;
        SeekHoverLine.Margin = new Thickness(Math.Clamp(at - 0.5, 0, SeekTrack.ActualWidth - 1), 1, 0, 0);
        if (ViewModel.PeekChapterAt(seconds))
        {
            PositionChapterPeek(at);
            ChapterPeek.Visibility = Visibility.Visible;
        }
    }

    private void OnSeekTrackLeft(object sender, PointerRoutedEventArgs e)
    {
        if (!_timelinePointerDown && !PointerOverSeekTrack()) HideChapterPeek();
    }

    private bool PointerOverSeekTrack()
    {
        if (SeekTrack.ActualWidth <= 0 || !CursorPoint(out var point)) return false;
        var origin = OriginIn(SeekTrack);
        return point.X >= origin.X && point.X <= origin.X + SeekTrack.ActualWidth
            && point.Y >= origin.Y - 8 && point.Y <= origin.Y + SeekTrack.ActualHeight + 8;
    }

    private void PositionChapterPeek(double trackX)
    {
        ChapterPeek.Visibility = Visibility.Visible;
        var origin = OriginIn(SeekTrack);
        ChapterPeek.Measure(new Size(Math.Max(0, Root.ActualWidth - 16), double.PositiveInfinity));
        var boxWidth = ChapterPeek.DesiredSize.Width;
        var boxHeight = ChapterPeek.DesiredSize.Height;
        ChapterPeekOffset.X = Math.Clamp(origin.X + trackX - boxWidth / 2, 8, Math.Max(8, Root.ActualWidth - boxWidth - 8));
        ChapterPeekOffset.Y = Math.Max(8, origin.Y - boxHeight - 12);
    }

    private void HideChapterPeek()
    {
        _timelineHovering = false;
        SeekHoverLine.Visibility = Visibility.Collapsed;
        ChapterPeek.Visibility = Visibility.Collapsed;
        if (_hoveredChapter >= 0)
        {
            _hoveredChapter = -1;
            RenderChapterTicks();
        }
        if (Attached) ViewModel.ClearChapterPeek();
        if (Bar.Visibility == Visibility.Visible)
            FadeStrip(TransportRow, _chrome.State.Bar ? _chrome.BarStrength : 0);
    }

    private static Path GetPath(Canvas canvas, int index)
    {
        if (index < canvas.Children.Count) return (Path)canvas.Children[index];
        var path = new Path { Data = new PathGeometry() };
        canvas.Children.Add(path);
        return path;
    }

    private static void SetPolygon(Path path, ReadOnlySpan<Point> points)
    {
        var geometry = (PathGeometry)path.Data;
        if (geometry.Figures.Count == 0) geometry.Figures.Add(new PathFigure { IsClosed = true, IsFilled = true });
        var figure = geometry.Figures[0];
        var same = figure.StartPoint == points[0] && figure.Segments.Count == points.Length - 1;
        for (var i = 1; same && i < points.Length; i++) same = ((LineSegment)figure.Segments[i - 1]).Point == points[i];
        if (same) return;
        figure.StartPoint = points[0];
        for (var i = 1; i < points.Length; i++)
        {
            if (i - 1 == figure.Segments.Count) figure.Segments.Add(new LineSegment());
            ((LineSegment)figure.Segments[i - 1]).Point = points[i];
        }
    }

    private static void TrimChildren(Canvas canvas, int count)
    {
        while (canvas.Children.Count > count) canvas.Children.RemoveAt(canvas.Children.Count - 1);
    }
}

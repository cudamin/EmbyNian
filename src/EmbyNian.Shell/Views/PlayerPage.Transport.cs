using EmbyNian.Playback;
using EmbyNian.Shell.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace EmbyNian.Shell.Views;

public sealed partial class PlayerPage
{
    private bool _timelinePointerDown;

    private void WireTransport()
    {
        WireInlineSpeed();
        WireTimelineArtwork();
        SeekSlider.BeginDrag = BeginTimelineGesture;
        SeekSlider.PressPosition = PressTimelineChapter;
        SeekSlider.FinishDrag = FinishTimelineGesture;
        SeekSlider.PositionValue = TimelinePositionValue;
        SeekSlider.KeyCommand = key =>
        {
            if (!Attached || _inputSuspended || key == Windows.System.VirtualKey.Space || !Dispatch(key, out var wake)) return false;
            if (wake && _chrome.WakeFully(Now)) Render();
            return true;
        };
        Rail.RightTapped += OnRailReset;
        SpeedButton.PointerWheelChanged += OnSpeedButtonWheel;
        SpeedButton.RightTapped += OnSpeedReset;
        SeekSlider.ValueChanged += OnTransportValueChanged;
        VolumeSlider.ValueChanged += OnTransportValueChanged;
        foreach (var button in new ButtonBase[] { TransportStatsButton, PreviousChapterButton, NextChapterButton })
            button.Click += OnChromeClick;
        ChapterMenu.Opened += (_, _) => Hold(true, ChromeHold.Menu);
        ChapterMenu.Closed += (_, _) => Hold(false, ChromeHold.Menu);
    }

    private void OnTransportValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        PaintTransportReadouts();
        if (Attached && ReferenceEquals(sender, SeekSlider)) PaintBufferedClock(SeekTrack.ActualWidth, ViewModel.Status);
    }

    private void PaintTransportReadouts()
    {
        if (TimelineLabelClip is null || VolumeLabelClip is null) return;
        var width = SeekTrack.ActualWidth;
        var height = SeekTrack.ActualHeight;
        TimelineLabelClip.Rect = new Rect(0, 0, Math.Max(0, width * SeekSlider.Value / SeekSlider.Maximum), height);
        var volumeHeight = VolumeTrack.ActualHeight;
        var filled = volumeHeight * VolumeScale.Fraction(VolumeSlider.Value);
        // 裁剪宽＝轨宽。以前手抄 40，音量条随档涨到 52 之后数字会被裁掉一截 —— 改读现值；
        // 布局还没量过（ActualWidth 为 0）时拿滑杆的 Width 兜底，它俩同宽、同批同档。
        var trackWidth = VolumeTrack.ActualWidth;
        if (trackWidth <= 0) trackWidth = VolumeSlider.Width;
        VolumeLabelClip.Rect = new Rect(0, Math.Max(0, volumeHeight - filled), trackWidth, Math.Max(0, filled));
        VolumeHundredMark.Margin = new Thickness(0, volumeHeight * (1 - 100 / VolumeSlider.Maximum), 0, 0);
    }

    private void OnVolumeTrackResized(object sender, SizeChangedEventArgs e) => PaintTransportReadouts();

    private void OnTransportResized(object sender, SizeChangedEventArgs e) => ArrangeTransport();

    private void ArrangeTransport()
    {
        if (TransportRow is null || !Attached) return;
        var width = Root.ActualWidth;
        var compact = width < 680;
        var tiny = width < 520;
        var minimal = width < 300;
        var chapters = ViewModel.ChapterMarks.Count > 0;
        TransportStatsButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ChapterButton.Visibility = compact || !chapters ? Visibility.Collapsed : Visibility.Visible;
        PreviousChapterButton.Visibility = compact || !chapters ? Visibility.Collapsed : Visibility.Visible;
        NextChapterButton.Visibility = compact || !chapters ? Visibility.Collapsed : Visibility.Visible;
        PlayButton.Visibility = compact && width >= 188 ? Visibility.Visible : Visibility.Collapsed;
        PreviousButton.Visibility = tiny ? Visibility.Collapsed : ViewModel.EpisodeControlsVisibility;
        NextButton.Visibility = PreviousButton.Visibility;
        EpisodeButton.Visibility = PreviousButton.Visibility;
        VersionButton.Visibility = tiny ? Visibility.Collapsed : ViewModel.VersionControlsVisibility;
        AudioButton.Visibility = minimal ? Visibility.Collapsed : Visibility.Visible;
        SubtitleButton.Visibility = AudioButton.Visibility;
        // 倍速条宽度档 × 档位倍率（复刻独占 scale 策略的动态两处之一）：窗口档照旧 100/146，
        // 大档（全屏或最大化）×1.3。形态一变这里会被 ApplyTitleScale 与 OnRootResized 各请一遍。
        SpeedStrip.Width = (compact ? 100 : 146) * ChromeFactor;
        SpeedStrip.Visibility = width < 220 ? Visibility.Collapsed : Visibility.Visible;
        SpeedButton.Visibility = width is >= 160 and < 220 ? Visibility.Visible : Visibility.Collapsed;
        // 音量轨高度公式不随档（窗口矮时本来就被夹住），只有上限那一个数分两档：大档 364（=280×1.3）。
        VolumeTrack.Height = Math.Clamp((Root.ActualHeight - 176) * 0.8 - 40, 80,
            BigChrome ? FullscreenRailTrack : WindowRailTrack);
        PaintTransportReadouts();
        RenderSpeedNotches();
    }

    private bool BeginTimelineGesture()
    {
        if (!Attached || !ViewModel.BeginTimelineDrag()) return false;
        _timelinePointerDown = true;
        Hold(true, ChromeHold.Timeline);
        return true;
    }

    private double TimelinePositionValue(double x, double y)
    {
        if (!Attached) return SeekSlider.Value;
        var fraction = ViewModel.Status.HasDuration
            ? TimelineScale.TimeAt(x, SeekTrack.ActualWidth, ViewModel.Status.Duration) / ViewModel.Status.Duration : 0;
        ViewModel.MoveTimeline(fraction);
        UpdateTimelineHover(x, y);
        return fraction * PlayerViewModel.SeekScale;
    }

    private void FinishTimelineGesture()
    {
        if (!_timelinePointerDown) return;
        _timelinePointerDown = false;
        if (Attached) _ = ViewModel.EndTimelineDragAsync();
        Hold(false, ChromeHold.Timeline);
        if (!PointerOverSeekTrack()) HideChapterPeek();
    }

    private void OnRailReset(object sender, RightTappedRoutedEventArgs e)
    {
        if (!Attached) return;
        ViewModel.ResetVolume();
        if (_chrome.FlashRail(Now)) Render();
        e.Handled = true;
    }

    private void OnSpeedButtonWheel(object sender, PointerRoutedEventArgs e)
    {
        if (!Attached) return;
        var delta = e.GetCurrentPoint(SpeedButton).Properties.MouseWheelDelta;
        if (delta != 0) ViewModel.NudgeSpeed(delta > 0 ? 0.1 : -0.1);
        e.Handled = true;
    }

    private void OnSpeedReset(object sender, RightTappedRoutedEventArgs e)
    {
        if (!Attached) return;
        ViewModel.SetSpeed(1);
        e.Handled = true;
    }

    private void OnPreviousChapter(object sender, RoutedEventArgs e) => ViewModel.StepChapter(-1);
    private void OnNextChapter(object sender, RoutedEventArgs e) => ViewModel.StepChapter(1);

    private void OnChapterMenuOpening(object sender, object e)
    {
        ChapterMenu.Items.Clear();
        if (!Attached) return;
        var selected = ViewModel.TimelineChapters.IndexAt(ViewModel.Status.Position);
        for (var index = 0; index < ViewModel.TimelineChapters.Chapters.Count; index++)
        {
            var mark = ViewModel.TimelineChapters.Chapters[index];
            var row = new RadioMenuFlyoutItem
            {
                Text = $"{TimelineScale.Clock(mark.Start, ViewModel.Status.Duration)}  {mark.Title}",
                GroupName = "PlaybackChapters",
                IsChecked = index == selected
            };
            row.Click += (_, _) => ViewModel.SeekTimelineChapter(mark.Start);
            ChapterMenu.Items.Add(row);
        }
        if (ChapterMenu.Items.Count == 0)
            ChapterMenu.Items.Add(new MenuFlyoutItem { Text = "这个文件没有章节", IsEnabled = false });
    }
}

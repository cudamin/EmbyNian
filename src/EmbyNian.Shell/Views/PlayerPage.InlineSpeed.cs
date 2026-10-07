using EmbyNian.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyNian.Shell.Views;

public sealed partial class PlayerPage
{
    private readonly List<Border> _speedNotches = [];
    private double _speedDragX = double.NaN;
    private double _speedDragStart;

    private void WireInlineSpeed()
    {
        InlineSpeedSlider.BeginDrag = () =>
        {
            if (!Attached || _inputSuspended) return false;
            _speedDragX = double.NaN;
            _speedDragStart = ViewModel.SpeedValue;
            HideChapterPeek();
            Hold(true, ChromeHold.Speed);
            return true;
        };
        InlineSpeedSlider.PositionValue = (x, _) =>
        {
            if (double.IsNaN(_speedDragX)) _speedDragX = x;
            return Math.Round(Math.Clamp(_speedDragStart - (x - _speedDragX) / (SpeedStrip.ActualWidth / 11) * 0.1, 0.1, 20), 1);
        };
        InlineSpeedSlider.FinishDrag = () => Hold(false, ChromeHold.Speed);
        SpeedStrip.RightTapped += OnSpeedReset;
        SpeedStrip.SizeChanged += (_, _) => RenderSpeedNotches();
        for (var index = 0; index < 11; index++)
        {
            var tick = new Border { Width = 1, Background = BrushFor("PlayerInkBrush"), IsHitTestVisible = false };
            _speedNotches.Add(tick);
            SpeedNotches.Children.Add(tick);
        }
    }

    private void RenderSpeedNotches()
    {
        if (!Attached || SpeedStrip.ActualWidth <= 0) return;
        var value = ViewModel.SpeedValue;
        var nearest = Math.Round(value * 10) / 10;
        var spacing = SpeedStrip.ActualWidth / 11;
        for (var index = 0; index < _speedNotches.Count; index++)
        {
            var rate = Math.Round(nearest + (index - 5) * 0.1, 1);
            var tick = _speedNotches[index];
            var major = Math.Abs(rate - Math.Round(rate)) < 0.001;
            var height = major ? 13 : 7;
            tick.Height = height;
            tick.Opacity = Math.Max(0.1, 1 - Math.Abs(index - 5) / 6d);
            tick.Visibility = rate is >= 0.1 and <= 20 ? Visibility.Visible : Visibility.Collapsed;
            Canvas.SetTop(tick, 35 - height);
            Canvas.SetLeft(tick, SpeedStrip.ActualWidth / 2 + (rate - value) / 0.1 * spacing);
        }
    }
}

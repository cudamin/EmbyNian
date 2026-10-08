using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Momoka.Shell.Views;

public sealed class PlayerSlider : Slider
{
    internal Func<bool>? BeginDrag { get; set; }
    internal Action? FinishDrag { get; set; }
    internal Func<double, double, double>? PositionValue { get; set; }
    internal Func<double, double, bool>? PressPosition { get; set; }
    internal Func<Windows.System.VirtualKey, bool>? KeyCommand { get; set; }

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (KeyCommand?.Invoke(e.Key) == true) e.Handled = true;
        else base.OnKeyDown(e);
    }

    private uint? _dragPointer;

    // The player routes wheel input by region: seconds on the timeline, levels on the volume rail.
    protected override void OnPointerWheelChanged(PointerRoutedEventArgs e)
    {
    }

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (BeginDrag is null)
        {
            base.OnPointerPressed(e);
            return;
        }
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        if (PressPosition?.Invoke(point.Position.X, point.Position.Y) == true) return;
        if (!BeginDrag()) return;
        if (!CapturePointer(e.Pointer))
        {
            FinishDrag?.Invoke();
            return;
        }
        _dragPointer = e.Pointer.PointerId;
        Focus(FocusState.Pointer);
        UpdatePosition(e);
    }

    protected override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        if (_dragPointer != e.Pointer.PointerId)
        {
            base.OnPointerMoved(e);
            return;
        }
        UpdatePosition(e);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        if (_dragPointer != e.Pointer.PointerId)
        {
            base.OnPointerReleased(e);
            return;
        }
        UpdatePosition(e);
        EndDrag();
        ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerRoutedEventArgs e)
    {
        if (_dragPointer == e.Pointer.PointerId) EndDrag();
        base.OnPointerCaptureLost(e);
    }

    protected override void OnPointerCanceled(PointerRoutedEventArgs e)
    {
        if (_dragPointer == e.Pointer.PointerId) EndDrag();
        base.OnPointerCanceled(e);
    }

    internal void CancelDrag()
    {
        if (_dragPointer is null) return;
        EndDrag();
        ReleasePointerCaptures();
    }

    private void UpdatePosition(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this).Position;
        if (PositionValue is { } map) Value = map(point.X, point.Y);
    }

    private void EndDrag()
    {
        _dragPointer = null;
        FinishDrag?.Invoke();
    }
}

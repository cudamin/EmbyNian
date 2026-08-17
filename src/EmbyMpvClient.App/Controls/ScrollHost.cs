using EmbyMpvClient.App.Theme;

namespace EmbyMpvClient.App.Controls;

/// <summary>
/// A vertically scrolling container with a hand-drawn scrollbar. WinForms' AutoScroll uses the
/// system scrollbars, which stay light grey inside a dark window and are the one piece of chrome
/// that gives away that this is not a native dark app.
/// </summary>
public sealed class ScrollHost : Control
{
    private readonly SlimScrollBar _bar = new();
    private readonly WheelScroller _wheel;
    private int _offset;

    public ScrollHost()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        BackColor = Palette.Window;

        Content = new Panel { BackColor = Palette.Window, Location = Point.Empty };
        Controls.Add(Content);
        Controls.Add(_bar);
        _bar.BringToFront();
        _bar.Scrolled += fraction => ScrollTo((int)Math.Round(fraction * MaximumOffset));

        _wheel = new WheelScroller(this, delta => ScrollBy(-delta / 120 * Dpi.Scale(this, 60)));
    }

    /// <summary>Add page content here and set its <see cref="Control.Height"/>, then call <see cref="RefreshExtent"/>.</summary>
    public Panel Content { get; }

    private int MaximumOffset => Math.Max(0, Content.Height - Height);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _wheel.Attach();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _wheel.Detach();
        base.OnHandleDestroyed(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutContent();
    }

    /// <summary>Re-reads the content height; call after rebuilding or resizing the page.</summary>
    public void RefreshExtent()
    {
        LayoutContent();
        Invalidate(true);
    }

    public void ScrollToTop() => ScrollTo(0);

    public void ScrollBy(int amount) => ScrollTo(_offset + amount);

    private void ScrollTo(int value)
    {
        var clamped = Math.Clamp(value, 0, MaximumOffset);
        if (clamped == _offset) return;
        _offset = clamped;
        Content.Top = -_offset;
        UpdateBar();
    }

    private void LayoutContent()
    {
        var needsBar = Content.Height > Height;
        var barWidth = Dpi.Scale(this, 10);

        _bar.Visible = needsBar;
        _bar.SetBounds(Width - barWidth, 0, barWidth, Height);

        Content.Width = needsBar ? Math.Max(10, Width - barWidth - Dpi.Scale(this, 4)) : Width;
        _offset = Math.Clamp(_offset, 0, MaximumOffset);
        Content.Top = -_offset;
        UpdateBar();
    }

    private void UpdateBar()
    {
        if (!_bar.Visible) return;
        _bar.Update(Height / (double)Math.Max(1, Content.Height), MaximumOffset == 0 ? 0 : _offset / (double)MaximumOffset);
    }
}

/// <summary>The thin overlay scrollbar used by <see cref="ScrollHost"/> and the poster grid.</summary>
public sealed class SlimScrollBar : Control
{
    private double _visibleFraction = 1;
    private double _positionFraction;
    private bool _dragging;
    private int _dragOffset;
    private bool _hovered;

    public SlimScrollBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Palette.Window;
        Width = 10;
    }

    /// <summary>Reports a new scroll position as a 0..1 fraction of the scrollable range.</summary>
    public event Action<double>? Scrolled;

    public void Update(double visibleFraction, double positionFraction)
    {
        _visibleFraction = Math.Clamp(visibleFraction, 0.02, 1);
        _positionFraction = Math.Clamp(positionFraction, 0, 1);
        Invalidate();
    }

    private Rectangle Thumb()
    {
        var margin = Dpi.Scale(this, 2);
        var track = Height - margin * 2;
        var thumbHeight = Math.Max(Dpi.Scale(this, 28), (int)(track * _visibleFraction));
        var top = margin + (int)((track - thumbHeight) * _positionFraction);
        return new Rectangle(margin, top, Math.Max(4, Width - margin * 2), thumbHeight);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        var thumb = Thumb();
        if (thumb.Contains(e.Location))
        {
            _dragging = true;
            _dragOffset = e.Y - thumb.Y;
        }
        else
        {
            // Click on the track: jump so the thumb centres on the cursor.
            ReportFromThumbTop(e.Y - thumb.Height / 2, thumb.Height);
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging) ReportFromThumbTop(e.Y - _dragOffset, Thumb().Height);
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _dragging = false;
        base.OnMouseUp(e);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    private void ReportFromThumbTop(int top, int thumbHeight)
    {
        var margin = Dpi.Scale(this, 2);
        var range = Height - margin * 2 - thumbHeight;
        if (range <= 0) return;
        Scrolled?.Invoke(Math.Clamp((top - margin) / (double)range, 0, 1));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Draw.Smooth(e.Graphics);
        var thumb = Thumb();
        Draw.Fill(e.Graphics, thumb, thumb.Width / 2, _dragging || _hovered ? Palette.BorderStrong : Palette.Border);
    }
}

/// <summary>
/// Routes the mouse wheel to whatever the cursor is over instead of whatever has focus, which is
/// what every other app on the machine does. Controls that scroll on their own — combo boxes,
/// spinners, multi-line text — keep the wheel for themselves.
/// </summary>
/// <param name="gate">
/// Optional veto. A horizontally scrolling row nested in a vertically scrolling page has to decline
/// the plain wheel, otherwise the two filters race and hovering a shelf randomly scrolls sideways
/// instead of scrolling the page.
/// </param>
internal sealed class WheelScroller(Control target, Action<int> scroll, Func<bool>? gate = null) : IMessageFilter
{
    private const int WmMouseWheel = 0x020A;

    private bool _attached;

    public void Attach()
    {
        if (_attached) return;
        Application.AddMessageFilter(this);
        _attached = true;
    }

    public void Detach()
    {
        if (!_attached) return;
        Application.RemoveMessageFilter(this);
        _attached = false;
    }

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != WmMouseWheel) return false;
        if (gate is not null && !gate()) return false;
        if (!target.IsHandleCreated || !target.Visible || target.FindForm() is not { } form || !form.Visible) return false;

        var cursor = Cursor.Position;
        if (!target.ClientRectangle.Contains(target.PointToClient(cursor))) return false;
        if (WantsWheelItself(Leaf(target, cursor))) return false;

        scroll((short)(m.WParam.ToInt64() >> 16));
        return true;
    }

    private static Control Leaf(Control root, Point screenPoint)
    {
        var current = root;
        while (true)
        {
            var child = current.GetChildAtPoint(
                current.PointToClient(screenPoint),
                GetChildAtPointSkip.Invisible | GetChildAtPointSkip.Disabled | GetChildAtPointSkip.Transparent);

            if (child is null || ReferenceEquals(child, current)) return current;
            current = child;
        }
    }

    private static bool WantsWheelItself(Control control) =>
        control is ComboBox or NumericUpDown or ListBox or TextBoxBase { Multiline: true };
}

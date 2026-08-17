using EmbyMpvClient.App.Theme;

namespace EmbyMpvClient.App.Controls;

/// <summary>
/// Base class for the panels the control bar opens above itself: the 选集/音轨/字幕/着色器 menus
/// and the volume slider.
/// <para>
/// Each one is a borderless top-level window rather than a child control, for the same reason the
/// bar itself is one — a WinForms child cannot blend with the mpv video window beneath it, and a
/// panel that stands taller than the bar would not fit inside it anyway. v1 used ComboBoxes, whose
/// drop-down list the system parents to the desktop: over video it appeared <em>under</em> the whole
/// window, and the client kept a 100 ms timer that hunted for the ComboLBox window and forced it
/// back to the top of the z-order. Drawing the list here removes that hack entirely.
/// </para>
/// <para>
/// The panel never takes activation, so the shell keeps the keyboard while a menu is open — which
/// also means there is no lost-focus event to close on, hence the pointer watchdog.
/// </para>
/// </summary>
public abstract class PlayerFlyout : Form
{
    private const int NoActivate = 0x08000000;
    private const int ToolWindow = 0x00000080;

    /// <summary>How long the pointer has to stay away from the panel before it closes itself.</summary>
    private const long GraceMilliseconds = 400;

    private readonly System.Windows.Forms.Timer _watch = new() { Interval = 120 };
    private Region? _shape;
    private Rectangle _anchor;
    private long _awaySince;

    protected PlayerFlyout()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Palette.Surface;
        Opacity = 0.97;
        Visible = false;

        _watch.Tick += (_, _) => WatchPointer();
    }

    /// <summary>Raised whenever the panel goes away, however it was closed.</summary>
    public event Action? Dismissed;

    /// <summary>Appearing must never steal focus from the shell.</summary>
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var createParams = base.CreateParams;
            createParams.ExStyle |= NoActivate | ToolWindow;
            return createParams;
        }
    }

    /// <summary>Screen rectangle of the button that opened the panel.</summary>
    protected Rectangle AnchorBounds => _anchor;

    /// <summary>
    /// Puts the panel above <paramref name="anchor"/> and shows it. Falls back to below the anchor
    /// when the screen has no room above — in a window that is not fullscreen the bar can sit close
    /// enough to the top of the monitor for a long menu not to fit.
    /// </summary>
    protected void Place(Form owner, Rectangle anchor, Size size)
    {
        var work = Screen.FromRectangle(anchor).WorkingArea;
        var margin = Dpi.Scale(this, 8);

        var left = anchor.X + (anchor.Width - size.Width) / 2;
        left = Math.Clamp(left, work.Left + margin, Math.Max(work.Left + margin, work.Right - size.Width - margin));

        var top = anchor.Top - size.Height - margin;
        if (top < work.Top + margin) top = Math.Min(work.Bottom - size.Height - margin, anchor.Bottom + margin);

        _anchor = anchor;
        _awaySince = 0;
        Owner = owner;
        Bounds = new Rectangle(left, top, size.Width, size.Height);
        ApplyShape();
        Visible = true;
        BringToFront();
        _watch.Start();
        Invalidate();
    }

    /// <summary>Hides the panel; safe to call when it is already gone.</summary>
    public void Dismiss()
    {
        if (!Visible) return;
        _watch.Stop();
        Visible = false;
        OnDismissed();
        Dismissed?.Invoke();
    }

    /// <summary>Hook for state that only makes sense while the panel is open, such as a drag.</summary>
    protected virtual void OnDismissed()
    {
    }

    /// <summary>
    /// Closes the panel once the pointer has spent a moment away from both it and the button it
    /// hangs off. The union of the two counts as "near", so crossing the gap between them does not
    /// snap the panel shut halfway through.
    /// </summary>
    private void WatchPointer()
    {
        if (!Visible) return;

        var slack = Dpi.Scale(this, 10);
        var hot = Rectangle.Union(Bounds, Rectangle.Inflate(_anchor, slack, slack));
        if (hot.Contains(Cursor.Position))
        {
            _awaySince = 0;
            return;
        }

        if (_awaySince == 0)
        {
            _awaySince = Environment.TickCount64;
            return;
        }

        if (Environment.TickCount64 - _awaySince >= GraceMilliseconds) Dismiss();
    }

    /// <summary>
    /// Rounds the window itself rather than only its paint: a painted corner would leave the
    /// panel's own square background showing in the notch.
    /// </summary>
    private void ApplyShape()
    {
        var shape = RoundedRegion(new Rectangle(0, 0, Width, Height), Dpi.Scale(this, 10));
        Region = shape;
        _shape?.Dispose();
        _shape = shape;
    }

    internal static Region RoundedRegion(Rectangle bounds, int radius)
    {
        using var path = Draw.RoundedPath(bounds, radius);
        return new Region(path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Draw.Smooth(e.Graphics);
        e.Graphics.Clear(Palette.Surface);

        var bounds = new Rectangle(0, 0, Width, Height);
        Draw.Border(e.Graphics, bounds, Dpi.Scale(this, 10), Palette.BorderStrong);
        PaintContent(e.Graphics, Rectangle.Inflate(bounds, -Dpi.Scale(this, 8), -Dpi.Scale(this, 8)));
    }

    /// <summary>Draws the panel's contents inside its padding.</summary>
    protected abstract void PaintContent(Graphics graphics, Rectangle content);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _watch.Dispose();
            _shape?.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>One row of a <see cref="PlayerMenu"/>; <see cref="Value"/> is what the bar acts on.</summary>
public sealed record PlayerMenuItem(string Text, string? Detail = null, bool Selected = false, object? Value = null);

/// <summary>
/// The list the bar's 选集/音轨/字幕/着色器 buttons open: a title, one row per option, a check mark
/// on the current one, and — for a season that is longer than the space above the bar — scrolling,
/// which the shell drives from the wheel and the arrow keys because the panel has no focus of its own.
/// </summary>
public sealed class PlayerMenu : PlayerFlyout
{
    private readonly List<PlayerMenuItem> _items = [];
    private string _title = "";
    private int _hovered = -1;
    private int _scroll;
    private int _rowHeight = 30;
    private int _headerHeight;

    public PlayerMenu()
    {
        Cursor = Cursors.Hand;
    }

    /// <summary>Raised with the row the user picked; the menu has already closed itself.</summary>
    public event Action<PlayerMenuItem>? ItemChosen;

    /// <summary>Fills the menu and opens it above <paramref name="anchor"/>.</summary>
    public void Present(Form owner, Rectangle anchor, string title, IReadOnlyList<PlayerMenuItem> items)
    {
        _items.Clear();
        _items.AddRange(items);
        _title = title;
        _hovered = -1;
        _rowHeight = Dpi.Scale(this, 30);
        _headerHeight = title.Length > 0 ? Dpi.Scale(this, 24) : 0;

        var padding = Dpi.Scale(this, 8) * 2;
        var work = Screen.FromRectangle(anchor).WorkingArea;
        var room = Math.Max(
            _headerHeight + _rowHeight + padding,
            Math.Min(anchor.Top - work.Top - Dpi.Scale(this, 24), (int)(work.Height * 0.72)));

        var width = Math.Clamp(MeasureWidth(), Dpi.Scale(this, 190), Dpi.Scale(this, 460));
        var height = Math.Min(_headerHeight + Math.Max(1, _items.Count) * _rowHeight + padding, room);

        // Open with the current choice in view: 「选集」 on episode 20 would otherwise start at 1.
        var viewport = Math.Max(_rowHeight, height - padding - _headerHeight);
        var selected = _items.FindIndex(item => item.Selected);
        _scroll = selected < 0
            ? 0
            : Math.Clamp(selected * _rowHeight - (viewport - _rowHeight) / 2, 0, MaxScroll(viewport));

        Place(owner, anchor, new Size(width, height));
    }

    private int MaxScroll(int viewport) => Math.Max(0, _items.Count * _rowHeight - viewport);

    private int MeasureWidth()
    {
        var width = Draw.Measure(_title, Fonts.Small).Width;
        foreach (var item in _items)
        {
            var text = Draw.Measure(item.Text, Fonts.Body).Width;
            var detail = item.Detail is null ? 0 : Draw.Measure(item.Detail, Fonts.Small).Width + Dpi.Scale(this, 14);
            width = Math.Max(width, text + detail);
        }

        // The check column on the left plus the panel's own padding on both sides.
        return width + Dpi.Scale(this, 26) + Dpi.Scale(this, 24);
    }

    private Rectangle RowsArea()
    {
        var padding = Dpi.Scale(this, 8);
        var top = padding + _headerHeight;
        return new Rectangle(padding, top, Math.Max(10, Width - padding * 2), Math.Max(_rowHeight, Height - top - padding));
    }

    private int IndexAt(Point point)
    {
        var rows = RowsArea();
        if (!rows.Contains(point)) return -1;
        var index = (point.Y - rows.Y + _scroll) / _rowHeight;
        return index >= 0 && index < _items.Count ? index : -1;
    }

    /// <summary>
    /// Scrolls the list. Called by the shell rather than from a wheel message of our own: the panel
    /// is never activated, so the wheel keeps going to whatever the shell has focused.
    /// </summary>
    public bool ScrollBy(int wheelDelta)
    {
        var rows = RowsArea();
        var max = MaxScroll(rows.Height);
        if (max == 0) return false;

        var moved = Math.Clamp(_scroll - wheelDelta / 120 * _rowHeight * 2, 0, max);
        if (moved == _scroll) return true;
        _scroll = moved;
        _hovered = IndexAt(PointToClient(Cursor.Position));
        Invalidate();
        return true;
    }

    /// <summary>Moves the highlight with the arrow keys, keeping it inside the visible rows.</summary>
    public void MoveHighlight(int step)
    {
        if (_items.Count == 0) return;

        var start = _hovered < 0 ? _items.FindIndex(item => item.Selected) : _hovered;
        _hovered = Math.Clamp(start + step, 0, _items.Count - 1);

        var rows = RowsArea();
        var top = _hovered * _rowHeight;
        var lowest = Math.Max(0, top + _rowHeight - rows.Height);
        _scroll = Math.Clamp(_scroll, lowest, Math.Max(lowest, top));
        Invalidate();
    }

    /// <summary>Picks the highlighted row, for Enter; nothing happens when nothing is highlighted.</summary>
    public void ChooseHighlighted()
    {
        if (_hovered < 0 || _hovered >= _items.Count) return;
        Choose(_items[_hovered]);
    }

    private void Choose(PlayerMenuItem item)
    {
        Dismiss();
        ItemChosen?.Invoke(item);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var index = IndexAt(e.Location);
        if (index != _hovered)
        {
            _hovered = index;
            Invalidate();
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hovered >= 0)
        {
            _hovered = -1;
            Invalidate();
        }

        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        var index = IndexAt(e.Location);
        if (e.Button == MouseButtons.Left && index >= 0) Choose(_items[index]);
        base.OnMouseDown(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        ScrollBy(e.Delta);
        base.OnMouseWheel(e);
    }

    protected override void OnDismissed()
    {
        _hovered = -1;
    }

    protected override void PaintContent(Graphics graphics, Rectangle content)
    {
        if (_headerHeight > 0)
        {
            var header = new Rectangle(content.X, content.Y, content.Width, _headerHeight);
            Draw.Text(graphics, _title, Fonts.Small, Palette.TextFaint, header, Draw.LeftMiddle);
        }

        var rows = RowsArea();
        if (_items.Count == 0)
        {
            Draw.Text(graphics, "没有可选项", Fonts.Body, Palette.TextFaint, rows, Draw.Centered);
            return;
        }

        // Rows are drawn at their scrolled position, so the ones half past the edge have to be cut.
        using var saved = graphics.Clip;
        graphics.SetClip(rows);
        try
        {
            PaintRows(graphics, rows);
        }
        finally
        {
            graphics.Clip = saved;
        }

        PaintScrollHint(graphics, rows);
    }

    private void PaintRows(Graphics graphics, Rectangle rows)
    {
        var check = Dpi.Scale(this, 22);
        var gap = Dpi.Scale(this, 4);
        var radius = Dpi.Scale(this, 6);
        var reserve = MaxScroll(rows.Height) > 0 ? Dpi.Scale(this, 8) : 0;

        for (var index = 0; index < _items.Count; index++)
        {
            var top = rows.Y + index * _rowHeight - _scroll;
            if (top + _rowHeight <= rows.Y) continue;
            if (top >= rows.Bottom) break;

            var item = _items[index];
            var row = new Rectangle(rows.X, top, Math.Max(10, rows.Width - reserve), _rowHeight);
            if (index == _hovered) Draw.Fill(graphics, Rectangle.Inflate(row, 0, -1), radius, Palette.SurfaceHover);

            if (item.Selected)
            {
                Draw.Text(graphics, Glyphs.Check, Fonts.IconSmall, Palette.Accent,
                    new Rectangle(row.X, row.Y, check, row.Height), Draw.Centered);
            }

            var color = item.Selected
                ? Palette.Accent
                : index == _hovered ? Palette.Text : Palette.TextDim;

            var left = row.X + check + gap;
            var detail = item.Detail is null ? 0 : Draw.Measure(item.Detail, Fonts.Small).Width + Dpi.Scale(this, 10);
            var text = new Rectangle(left, row.Y, Math.Max(10, row.Right - left - detail), row.Height);
            Draw.Text(graphics, item.Text, Fonts.Body, color, text, Draw.LeftMiddle);

            if (item.Detail is not null)
            {
                Draw.Text(graphics, item.Detail, Fonts.Small, Palette.TextFaint,
                    new Rectangle(text.Right, row.Y, Math.Max(0, row.Right - text.Right), row.Height), Draw.RightMiddle);
            }
        }
    }

    /// <summary>
    /// A hairline on the right edge showing how much of the list is off-screen. Drawn only when it
    /// has something to say — a three-episode menu should not look like it scrolls.
    /// </summary>
    private void PaintScrollHint(Graphics graphics, Rectangle rows)
    {
        var max = MaxScroll(rows.Height);
        if (max == 0) return;

        var width = Dpi.Scale(this, 3);
        var track = new Rectangle(rows.Right - width, rows.Y, width, rows.Height);
        Draw.Fill(graphics, track, width / 2, Palette.Border);

        var total = _items.Count * _rowHeight;
        var height = Math.Max(Dpi.Scale(this, 18), (int)(rows.Height * (rows.Height / (double)total)));
        var offset = (int)((rows.Height - height) * (_scroll / (double)max));
        Draw.Fill(graphics, new Rectangle(track.X, track.Y + offset, width, height), width / 2, Palette.BorderStrong);
    }
}

/// <summary>
/// The volume slider the speaker button opens. v1 hung a vertical <c>VolumeRail</c> off the right
/// edge of the window, which had nothing to do with the button that controlled it; folding it into
/// a panel above the speaker keeps the whole control bar in one place.
/// </summary>
public sealed class VolumeFlyout : PlayerFlyout
{
    private int _value = 100;
    private bool _muted;
    private bool _dragging;

    public VolumeFlyout()
    {
        Cursor = Cursors.Hand;
    }

    /// <summary>Raised with 0-100 while the user drags; muting is the bar's own button.</summary>
    public event Action<int>? ValueChanged;

    public void Present(Form owner, Rectangle anchor, int value, bool muted)
    {
        _value = Math.Clamp(value, 0, 100);
        _muted = muted;
        _dragging = false;
        Place(owner, anchor, new Size(Dpi.Scale(this, 216), Dpi.Scale(this, 46)));
    }

    /// <summary>
    /// Follows mpv's own volume without raising <see cref="ValueChanged"/>. Ignored mid-drag, so a
    /// property change that is still in flight cannot yank the thumb back from under the finger.
    /// </summary>
    public void SetValue(int value, bool muted)
    {
        if (_dragging) return;

        var clamped = Math.Clamp(value, 0, 100);
        if (clamped == _value && muted == _muted) return;

        _value = clamped;
        _muted = muted;
        if (Visible) Invalidate();
    }

    private Rectangle TrackArea()
    {
        var padding = Dpi.Scale(this, 8);
        var thumb = Dpi.Scale(this, 7);
        var label = Dpi.Scale(this, 42);
        var content = Rectangle.Inflate(new Rectangle(0, 0, Width, Height), -padding, -padding);
        return new Rectangle(
            content.X + thumb,
            content.Y,
            Math.Max(Dpi.Scale(this, 20), content.Width - label - thumb * 2),
            content.Height);
    }

    private void Apply(int x)
    {
        var track = TrackArea();
        var fraction = (x - track.X) / (double)Math.Max(1, track.Width);
        var value = (int)Math.Round(Math.Clamp(fraction, 0, 1) * 100);
        if (value == _value && !_muted) return;

        _value = value;
        _muted = false;
        Invalidate();
        ValueChanged?.Invoke(value);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _dragging = true;
            Capture = true;
            Apply(e.X);
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging) Apply(e.X);
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            Capture = false;
            Apply(e.X);
            Invalidate();
        }

        base.OnMouseUp(e);
    }

    /// <summary>A drag that outlives the panel would keep the capture; drop both together.</summary>
    protected override void OnDismissed()
    {
        if (!_dragging) return;
        _dragging = false;
        Capture = false;
    }

    protected override void PaintContent(Graphics graphics, Rectangle content)
    {
        var track = TrackArea();
        var thickness = Dpi.Scale(this, 4);
        var line = new Rectangle(track.X, track.Y + (track.Height - thickness) / 2, track.Width, thickness);
        var fraction = _muted ? 0 : _value / 100.0;

        Draw.ProgressBar(graphics, line, fraction, Color.FromArgb(110, 255, 255, 255),
            _muted ? Palette.TextFaint : Palette.Accent);

        var thumb = Dpi.Scale(this, _dragging ? 14 : 11);
        var center = line.X + (int)Math.Round(line.Width * fraction);
        Draw.Fill(graphics, new Rectangle(center - thumb / 2, line.Y + line.Height / 2 - thumb / 2, thumb, thumb),
            thumb / 2, _muted ? Palette.TextDim : Palette.Text);

        var label = new Rectangle(track.Right, content.Y, Math.Max(0, content.Right - track.Right), content.Height);
        Draw.Text(graphics,
            _muted ? "静音" : _value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Fonts.Small,
            _muted ? Palette.TextFaint : Palette.TextDim,
            label,
            Draw.RightMiddle);
    }
}

/// <summary>
/// The bubble that follows the pointer along the seek bar: the time under the cursor plus, when
/// Emby has one, the chapter still for that moment.
/// <para>
/// Click-through (<c>WS_EX_TRANSPARENT</c>) as well as never-activated: it sits directly above the
/// seek bar, and a window that ate mouse messages there would make the bar impossible to grab where
/// the bubble overlaps it.
/// </para>
/// </summary>
public sealed class SeekPreview : Form
{
    /// <summary>Width the thumbnail is requested and drawn at; 16:9 gives the panel its height.</summary>
    public const int ThumbnailWidth = 176;

    private const int NoActivate = 0x08000000;
    private const int ToolWindow = 0x00000080;
    private const int ClickThrough = 0x00000020;

    private Image? _image;
    private string _time = "";
    private string _label = "";
    private Region? _shape;
    private Size _shaped;

    public SeekPreview()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Palette.Surface;
        Opacity = 0.95;
        Visible = false;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var createParams = base.CreateParams;
            createParams.ExStyle |= NoActivate | ToolWindow | ClickThrough;
            return createParams;
        }
    }

    /// <summary>
    /// Puts the bubble just above <paramref name="tip"/> — a screen point on the top edge of the seek
    /// bar, at the cursor — and keeps it inside the player window. <paramref name="image"/> stays
    /// owned by the caller; the bubble only borrows it for painting.
    /// </summary>
    public void Present(Form owner, Point tip, string time, string label, Image? image)
    {
        _time = time;
        _label = label;
        _image = image;

        var size = Measure();
        var margin = Dpi.Scale(this, 6);
        var screen = Screen.FromPoint(tip).WorkingArea;
        var limit = Rectangle.Intersect(owner.Bounds, screen);
        if (limit.Width < size.Width + margin * 2 || limit.Height < size.Height) limit = screen;

        var left = Math.Clamp(
            tip.X - size.Width / 2,
            limit.Left + margin,
            Math.Max(limit.Left + margin, limit.Right - size.Width - margin));

        var top = tip.Y - size.Height - margin;
        if (top < limit.Top) top = Math.Min(limit.Bottom - size.Height, tip.Y + margin);

        Owner = owner;
        Bounds = new Rectangle(left, top, size.Width, size.Height);
        ApplyShape(size);
        Visible = true;
        BringToFront();
        Invalidate();
    }

    /// <summary>Hides the bubble and drops the borrowed image; safe to call when already hidden.</summary>
    public void Dismiss()
    {
        _image = null;
        if (!Visible) return;
        Visible = false;
    }

    private string Caption() => _label.Length == 0 ? _time : $"{_time}  ·  {_label}";

    private Size Measure()
    {
        var padding = Dpi.Scale(this, 6);
        var caption = Dpi.Scale(this, 20);
        if (_image is null)
        {
            var width = Draw.Measure(Caption(), Fonts.Small).Width + padding * 2 + Dpi.Scale(this, 10);
            return new Size(Math.Max(Dpi.Scale(this, 72), width), caption + padding * 2);
        }

        var frame = Dpi.Scale(this, ThumbnailWidth);
        return new Size(frame + padding * 2, frame * 9 / 16 + padding + caption + padding * 2);
    }

    /// <summary>The bubble moves with every mouse message, so the region is rebuilt only on resize.</summary>
    private void ApplyShape(Size size)
    {
        if (_shape is not null && size == _shaped) return;

        var shape = PlayerFlyout.RoundedRegion(new Rectangle(0, 0, size.Width, size.Height), Dpi.Scale(this, 8));
        Region = shape;
        _shape?.Dispose();
        _shape = shape;
        _shaped = size;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Draw.Smooth(e.Graphics);
        e.Graphics.Clear(Palette.Surface);

        var radius = Dpi.Scale(this, 8);
        var padding = Dpi.Scale(this, 6);
        var caption = Dpi.Scale(this, 20);
        var bounds = new Rectangle(0, 0, Width, Height);
        Draw.Border(e.Graphics, bounds, radius, Palette.BorderStrong);

        var content = Rectangle.Inflate(bounds, -padding, -padding);
        if (_image is not null)
        {
            var frame = new Rectangle(content.X, content.Y, content.Width,
                Math.Max(1, content.Height - caption - padding));
            Draw.Fill(e.Graphics, frame, Dpi.Scale(this, 5), Palette.Window);
            Draw.ImageCover(e.Graphics, _image, frame, Dpi.Scale(this, 5));
        }

        Draw.Text(e.Graphics, Caption(), Fonts.Small, Palette.Text,
            new Rectangle(content.X, content.Bottom - caption, content.Width, caption), Draw.Centered);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _shape?.Dispose();
        base.Dispose(disposing);
    }
}

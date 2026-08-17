using EmbyMpvClient.App.Theme;

namespace EmbyMpvClient.App.Controls;

/// <summary>One entry in the left rail.</summary>
public sealed record NavigationItem(string Key, string Glyph, string Label);

/// <summary>
/// The left navigation rail: server header, the pages, and a footer for settings and sign-out.
/// Drawn as one control rather than a stack of buttons so the selection indicator and hover
/// bands line up exactly.
/// </summary>
public sealed class NavigationRail : Control
{
    private readonly List<NavigationItem> _items = [];
    private readonly List<NavigationItem> _footer = [];

    private string _selectedKey = "";
    private int _hoverIndex = -1;
    private string _serverName = "";
    private string _userName = "";

    public NavigationRail()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        BackColor = Palette.Surface;
        Font = Fonts.Body;
        Width = 208;
    }

    public event Action<string>? Selected;

    public void SetItems(IEnumerable<NavigationItem> items, IEnumerable<NavigationItem> footer)
    {
        _items.Clear();
        _items.AddRange(items);
        _footer.Clear();
        _footer.AddRange(footer);
        Invalidate();
    }

    public string SelectedKey
    {
        get => _selectedKey;
        set
        {
            _selectedKey = value ?? "";
            Invalidate();
        }
    }

    public void SetAccount(string serverName, string userName)
    {
        _serverName = serverName;
        _userName = userName;
        Invalidate();
    }

    private int RowHeight => Dpi.Scale(this, 40);

    private int HeaderHeight => Dpi.Scale(this, 76);

    private int FooterTop => Height - _footer.Count * RowHeight - Dpi.Scale(this, 12);

    private Rectangle RowBounds(int index) =>
        index < _items.Count
            ? new Rectangle(Dpi.Scale(this, 8), HeaderHeight + index * RowHeight, Width - Dpi.Scale(this, 16), RowHeight)
            : new Rectangle(Dpi.Scale(this, 8), FooterTop + (index - _items.Count) * RowHeight, Width - Dpi.Scale(this, 16), RowHeight);

    private int Total => _items.Count + _footer.Count;

    private NavigationItem ItemAt(int index) => index < _items.Count ? _items[index] : _footer[index - _items.Count];

    private int IndexAt(Point location)
    {
        for (var index = 0; index < Total; index++)
        {
            if (RowBounds(index).Contains(location)) return index;
        }

        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var index = IndexAt(e.Location);
        if (index != _hoverIndex)
        {
            _hoverIndex = index;
            Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hoverIndex = -1;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        var index = IndexAt(e.Location);
        if (index >= 0 && e.Button == MouseButtons.Left) Selected?.Invoke(ItemAt(index).Key);
        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Draw.Smooth(e.Graphics);
        e.Graphics.Clear(Palette.Surface);

        using (var pen = new Pen(Palette.Border))
        {
            e.Graphics.DrawLine(pen, Width - 1, 0, Width - 1, Height);
        }

        PaintHeader(e.Graphics);

        for (var index = 0; index < Total; index++) PaintRow(e.Graphics, index);
    }

    private void PaintHeader(Graphics graphics)
    {
        var left = Dpi.Scale(this, 18);
        var top = Dpi.Scale(this, 16);
        var width = Width - left - Dpi.Scale(this, 12);

        Draw.Text(
            graphics,
            _serverName.Length == 0 ? Composition.AppInfo.Title : _serverName,
            Fonts.Subtitle,
            Palette.Text,
            new Rectangle(left, top, width, Dpi.Scale(this, 22)),
            Draw.SingleLine);

        if (_userName.Length > 0)
        {
            Draw.Text(
                graphics,
                Glyphs.Person + "  " + _userName,
                Fonts.Small,
                Palette.TextDim,
                new Rectangle(left, top + Dpi.Scale(this, 24), width, Dpi.Scale(this, 18)),
                Draw.SingleLine);
        }
    }

    private void PaintRow(Graphics graphics, int index)
    {
        var item = ItemAt(index);
        var bounds = RowBounds(index);
        var selected = string.Equals(item.Key, _selectedKey, StringComparison.Ordinal);
        var hovered = index == _hoverIndex;

        if (selected) Draw.Fill(graphics, bounds, 8, Palette.AccentSoft);
        else if (hovered) Draw.Fill(graphics, bounds, 8, Palette.SurfaceHover);

        if (selected)
        {
            var barHeight = bounds.Height / 2;
            Draw.Fill(
                graphics,
                new Rectangle(bounds.X, bounds.Y + (bounds.Height - barHeight) / 2, Dpi.Scale(this, 3), barHeight),
                2,
                Palette.Accent);
        }

        var color = selected ? Palette.Accent : hovered ? Palette.Text : Palette.TextDim;
        var iconLeft = bounds.X + Dpi.Scale(this, 14);
        var iconWidth = Dpi.Scale(this, 22);

        Draw.Text(graphics, item.Glyph, Fonts.Icon, color, new Rectangle(iconLeft, bounds.Y, iconWidth, bounds.Height), Draw.Centered);
        Draw.Text(
            graphics,
            item.Label,
            selected ? Fonts.BodyStrong : Fonts.Body,
            color,
            new Rectangle(iconLeft + iconWidth + Dpi.Scale(this, 10), bounds.Y, bounds.Right - iconLeft - iconWidth - Dpi.Scale(this, 14), bounds.Height),
            Draw.LeftMiddle);
    }
}

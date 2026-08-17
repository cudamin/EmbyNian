using EmbyMpvClient.App.Theme;

namespace EmbyMpvClient.App.Controls;

/// <summary>A rounded surface panel with an optional title; the settings and detail pages are built from these.</summary>
public sealed class Card : Panel
{
    private string _title = "";
    private string _subtitle = "";

    public Card()
    {
        DoubleBuffered = true;
        BackColor = Palette.Window;
        ForeColor = Palette.Text;
        Padding = new Padding(16);
    }

    public string Title
    {
        get => _title;
        set
        {
            _title = value ?? "";
            Invalidate();
        }
    }

    public string Subtitle
    {
        get => _subtitle;
        set
        {
            _subtitle = value ?? "";
            Invalidate();
        }
    }

    /// <summary>Where a caller should start laying out children, below the title block.</summary>
    public int ContentTop
    {
        get
        {
            var top = Padding.Top;
            if (_title.Length > 0) top += Draw.Measure(_title, Fonts.Subtitle).Height + Dpi.Scale(this, 6);
            if (_subtitle.Length > 0) top += Draw.Measure(_subtitle, Fonts.Small).Height + Dpi.Scale(this, 8);
            return top;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Draw.Smooth(e.Graphics);
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        Draw.Fill(e.Graphics, bounds, 10, Palette.Surface);
        Draw.Border(e.Graphics, bounds, 10, Palette.Border);

        var y = Padding.Top;
        if (_title.Length > 0)
        {
            var height = Draw.Measure(_title, Fonts.Subtitle).Height;
            Draw.Text(
                e.Graphics,
                _title,
                Fonts.Subtitle,
                Palette.Text,
                new Rectangle(Padding.Left, y, Width - Padding.Horizontal, height),
                Draw.SingleLine);
            y += height + Dpi.Scale(this, 4);
        }

        if (_subtitle.Length > 0)
        {
            var height = Draw.Measure(_subtitle, Fonts.Small, Width - Padding.Horizontal).Height;
            Draw.Text(
                e.Graphics,
                _subtitle,
                Fonts.Small,
                Palette.TextDim,
                new Rectangle(Padding.Left, y, Width - Padding.Horizontal, height),
                Draw.Wrapped);
        }

        base.OnPaint(e);
    }
}

/// <summary>A page or section heading: bold title, dim subtitle, no box.</summary>
public sealed class SectionHeader : Control
{
    private string _subtitle = "";

    public SectionHeader(string title, string subtitle = "")
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Text = title;
        _subtitle = subtitle;
        Font = Fonts.Title;
        Height = subtitle.Length == 0 ? 30 : 48;
    }

    public string Subtitle
    {
        get => _subtitle;
        set
        {
            _subtitle = value ?? "";
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Draw.Smooth(e.Graphics);
        var titleHeight = Draw.Measure(Text, Font).Height;
        Draw.Text(e.Graphics, Text, Font, Palette.Text, new Rectangle(0, 0, Width, titleHeight), Draw.SingleLine);

        if (_subtitle.Length == 0) return;
        Draw.Text(
            e.Graphics,
            _subtitle,
            Fonts.Body,
            Palette.TextDim,
            new Rectangle(0, titleHeight + Dpi.Scale(this, 4), Width, Height - titleHeight - Dpi.Scale(this, 4)),
            Draw.Wrapped);
    }
}

/// <summary>
/// A settings row that pairs a label (and optional explanation) with one editor control.
/// Keeps every page's rows on the same grid without a TableLayoutPanel per card.
/// </summary>
public sealed class FieldRow : Control
{
    private readonly Control _editor;
    private readonly string _description;
    private readonly int _editorWidth;

    /// <param name="editorWidth">0 fills the space right of the label column.</param>
    public FieldRow(string title, Control editor, string description = "", int editorWidth = 0)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Font = Fonts.Body;
        Text = title;
        _description = description;
        _editor = editor;
        _editorWidth = editorWidth;
        Controls.Add(editor);
        Height = PreferredHeight();
    }

    /// <summary>Width reserved for the label; the editor starts after it.</summary>
    public int LabelWidth { get; set; } = 168;

    public Control Editor => _editor;

    public int PreferredHeight()
    {
        var rows = _description.Length == 0 ? 1 : 2;
        return Math.Max(_editor.Height + Dpi.Scale(this, 8), Dpi.Scale(this, rows == 1 ? 34 : 46));
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        var label = Dpi.Scale(this, LabelWidth);
        var width = _editorWidth > 0 ? Dpi.Scale(this, _editorWidth) : Math.Max(60, Width - label);
        var left = _editorWidth > 0 ? Width - width : label;
        _editor.SetBounds(left, (Height - _editor.Height) / 2, width, _editor.Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Draw.Smooth(e.Graphics);
        var width = Math.Max(10, Dpi.Scale(this, LabelWidth) - Dpi.Scale(this, 10));

        if (_description.Length == 0)
        {
            Draw.Text(e.Graphics, Text, Font, Palette.Text, new Rectangle(0, 0, width, Height), Draw.LeftMiddle);
            return;
        }

        var titleHeight = Draw.Measure(Text, Font).Height;
        var descriptionHeight = Draw.Measure(_description, Fonts.Small).Height;
        var top = (Height - titleHeight - descriptionHeight - Dpi.Scale(this, 2)) / 2;

        Draw.Text(e.Graphics, Text, Font, Palette.Text, new Rectangle(0, top, width, titleHeight), Draw.SingleLine);
        Draw.Text(
            e.Graphics,
            _description,
            Fonts.Small,
            Palette.TextDim,
            new Rectangle(0, top + titleHeight + Dpi.Scale(this, 2), width, descriptionHeight),
            Draw.SingleLine);
    }
}

/// <summary>Plain wrapped text, for overviews and explanations.</summary>
public sealed class TextBlock : Control
{
    public TextBlock(string text = "", Font? font = null, Color? color = null)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Font = font ?? Fonts.Body;
        ForeColor = color ?? Palette.TextDim;
        Text = text;
    }

    /// <summary>Grows the control to fit its text at the current width.</summary>
    public void FitHeight(int maximumHeight = 0)
    {
        var height = Draw.Measure(Text, Font, Math.Max(20, Width)).Height + Dpi.Scale(this, 2);
        Height = maximumHeight > 0 ? Math.Min(height, maximumHeight) : height;
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Draw.Smooth(e.Graphics);
        Draw.Text(e.Graphics, Text, Font, ForeColor, new Rectangle(0, 0, Width, Height), Draw.Wrapped);
    }
}

/// <summary>Positions controls in a column, so a page's layout reads as a list of rows.</summary>
internal sealed class Stack(Control parent, int left, int top, int width, int gap = 8)
{
    public int Bottom { get; private set; } = top;

    public Stack Add(Control child, int? height = null)
    {
        child.SetBounds(left, Bottom, width, height ?? child.Height);
        parent.Controls.Add(child);
        Bottom = child.Bottom + gap;
        return this;
    }

    /// <summary>Adds a row of controls side by side with their own widths.</summary>
    public Stack Row(int height, params Control[] children)
    {
        var x = left;
        foreach (var child in children)
        {
            child.SetBounds(x, Bottom, child.Width, height);
            parent.Controls.Add(child);
            x = child.Right + gap;
        }

        Bottom += height + gap;
        return this;
    }

    public Stack Space(int amount)
    {
        Bottom += amount;
        return this;
    }
}

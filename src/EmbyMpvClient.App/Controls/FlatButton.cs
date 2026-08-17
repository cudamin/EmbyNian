using EmbyMpvClient.App.Theme;

namespace EmbyMpvClient.App.Controls;

public enum ButtonVariant
{
    /// <summary>Filled accent; one per screen, for the action the user came to perform.</summary>
    Primary,
    /// <summary>Outlined surface; the normal button.</summary>
    Secondary,
    /// <summary>No fill until hovered; for toolbars and icon-only buttons.</summary>
    Ghost,
    Danger
}

/// <summary>
/// An owner-drawn button. WinForms' own button cannot be given a dark flat look without either
/// a visual-styles renderer fighting the colours or a bitmap per state, so it is drawn here:
/// one paint method, four variants, and an optional Segoe Fluent glyph in front of the text.
/// </summary>
public sealed class FlatButton : Control
{
    private bool _hovered;
    private bool _pressed;
    private bool _checked;
    private ButtonVariant _variant = ButtonVariant.Secondary;
    private string _glyph = "";
    private int _cornerRadius = 6;

    public FlatButton()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor,
            true);

        Font = Fonts.Body;
        ForeColor = Palette.Text;
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        TabStop = true;
        Size = new Size(96, 32);
    }

    public ButtonVariant Variant
    {
        get => _variant;
        set
        {
            _variant = value;
            Invalidate();
        }
    }

    /// <summary>A <see cref="Glyphs"/> codepoint drawn before the text; empty for none.</summary>
    public string Glyph
    {
        get => _glyph;
        set
        {
            _glyph = value ?? "";
            Invalidate();
        }
    }

    public int CornerRadius
    {
        get => _cornerRadius;
        set
        {
            _cornerRadius = Math.Max(0, value);
            Invalidate();
        }
    }

    /// <summary>Renders as an active/selected button — used for the filter chips.</summary>
    public bool Checked
    {
        get => _checked;
        set
        {
            _checked = value;
            Invalidate();
        }
    }

    /// <summary>Sizes the control to its content plus padding, for toolbar buttons.</summary>
    public void AutoSizeToContent(int horizontalPadding = 14, int minimumWidth = 0)
    {
        var width = Draw.Measure(Text, Font).Width;
        if (_glyph.Length > 0)
        {
            width += Draw.Measure(_glyph, Fonts.Icon).Width;
            if (Text.Length > 0) width += Dpi.Scale(this, 6);
        }

        Width = Math.Max(minimumWidth, width + Dpi.Scale(this, horizontalPadding) * 2);
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
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            Focus();
            Invalidate();
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        if (!Enabled)
        {
            _hovered = false;
            _pressed = false;
        }

        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        Invalidate();
        base.OnGotFocus(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        Invalidate();
        base.OnLostFocus(e);
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Space or Keys.Enter || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            e.Handled = true;
            PerformClick();
        }

        base.OnKeyDown(e);
    }

    public void PerformClick()
    {
        if (Enabled) OnClick(EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Draw.Smooth(e.Graphics);
        var bounds = new Rectangle(0, 0, Width, Height);
        var (fill, border, text) = Colors();

        Draw.Fill(e.Graphics, bounds, _cornerRadius, fill);
        Draw.Border(e.Graphics, bounds, _cornerRadius, border);

        if (Focused && Enabled)
        {
            // Win11 shows keyboard focus as an inner ring rather than a dotted rectangle.
            Draw.Border(e.Graphics, Rectangle.Inflate(bounds, -3, -3), Math.Max(0, _cornerRadius - 3), Palette.Mix(fill, Palette.Text, 0.55));
        }

        var content = Rectangle.Inflate(bounds, -Dpi.Scale(this, 8), 0);
        if (_glyph.Length == 0)
        {
            Draw.Text(e.Graphics, Text, Font, text, content, Draw.Centered);
            return;
        }

        var iconFont = Fonts.IconAt(Font.SizeInPoints + 2.5f);
        var iconWidth = Draw.Measure(_glyph, iconFont).Width;

        if (Text.Length == 0)
        {
            Draw.Text(e.Graphics, _glyph, iconFont, text, content, Draw.Centered);
            return;
        }

        var gap = Dpi.Scale(this, 6);
        var textWidth = Draw.Measure(Text, Font).Width;
        var start = content.X + Math.Max(0, (content.Width - iconWidth - gap - textWidth) / 2);

        Draw.Text(e.Graphics, _glyph, iconFont, text, new Rectangle(start, content.Y, iconWidth, content.Height), Draw.Centered);
        Draw.Text(
            e.Graphics,
            Text,
            Font,
            text,
            new Rectangle(start + iconWidth + gap, content.Y, content.Right - (start + iconWidth + gap), content.Height),
            Draw.LeftMiddle);
    }

    private (Color Fill, Color Border, Color Text) Colors()
    {
        if (!Enabled)
        {
            return _variant == ButtonVariant.Ghost
                ? (Color.Transparent, Color.Transparent, Palette.TextFaint)
                : (Palette.SurfaceAlt, Palette.Border, Palette.TextFaint);
        }

        return _variant switch
        {
            ButtonVariant.Primary => (
                _pressed ? Palette.AccentPressed : _hovered ? Palette.AccentHover : Palette.Accent,
                Color.Transparent,
                Palette.TextOnAccent),

            ButtonVariant.Danger => (
                _pressed ? Palette.Mix(Palette.Danger, Color.Black, 0.2) : _hovered ? Palette.Danger : Palette.Mix(Palette.Danger, Palette.Surface, 0.75),
                Palette.Danger,
                Palette.Text),

            ButtonVariant.Ghost => (
                _pressed ? Palette.SurfaceAlt : _hovered ? Palette.SurfaceHover : Checked ? Palette.AccentSoft : Color.Transparent,
                Checked ? Palette.Accent : Color.Transparent,
                Checked ? Palette.Accent : _hovered ? Palette.Text : Palette.TextDim),

            _ => (
                _pressed ? Palette.SurfaceAlt : _hovered ? Palette.SurfaceHover : Checked ? Palette.AccentSoft : Palette.Surface,
                Checked ? Palette.Accent : _hovered ? Palette.BorderStrong : Palette.Border,
                Checked ? Palette.Accent : Palette.Text)
        };
    }
}

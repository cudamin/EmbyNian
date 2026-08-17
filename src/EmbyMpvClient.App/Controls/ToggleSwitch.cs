using EmbyMpvClient.App.Theme;

namespace EmbyMpvClient.App.Controls;

/// <summary>
/// A Win11-style settings row: title (and optional explanation) on the left, a switch on the
/// right, the whole row clickable. The explanation line matters here — several of these switches
/// change what mpv is asked to do, and a one-line "why" beside the toggle is cheaper than a
/// manual nobody reads.
/// </summary>
public sealed class ToggleSwitch : Control
{
    private bool _checked;
    private bool _hovered;
    private string _description = "";

    public ToggleSwitch()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor, true);

        Font = Fonts.Body;
        ForeColor = Palette.Text;
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        TabStop = true;
        Size = new Size(420, 34);
    }

    public event EventHandler? CheckedChanged;

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Sets the state without raising <see cref="CheckedChanged"/>, for loading settings.</summary>
    public void SetCheckedSilently(bool value)
    {
        _checked = value;
        Invalidate();
    }

    public string Description
    {
        get => _description;
        set
        {
            _description = value ?? "";
            Height = PreferredHeight();
            Invalidate();
        }
    }

    public int PreferredHeight() => _description.Length == 0 ? Dpi.Scale(this, 30) : Dpi.Scale(this, 46);

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

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && Enabled)
        {
            Focus();
            Checked = !Checked;
        }

        base.OnMouseUp(e);
    }

    protected override bool IsInputKey(Keys keyData) => keyData == Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space && Enabled)
        {
            e.Handled = true;
            Checked = !Checked;
        }

        base.OnKeyDown(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
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

    protected override void OnPaint(PaintEventArgs e)
    {
        Draw.Smooth(e.Graphics);

        var trackWidth = Dpi.Scale(this, 38);
        var trackHeight = Dpi.Scale(this, 20);
        var track = new Rectangle(Width - trackWidth, (Height - trackHeight) / 2, trackWidth, trackHeight);

        if (_hovered && Enabled)
            Draw.Fill(e.Graphics, new Rectangle(-Dpi.Scale(this, 6), 0, Width + Dpi.Scale(this, 12), Height), 6, Palette.Mix(Palette.Window, Palette.SurfaceHover, 0.5));

        var titleColor = Enabled ? Palette.Text : Palette.TextFaint;
        var textWidth = Math.Max(10, track.X - Dpi.Scale(this, 12));

        if (_description.Length == 0)
        {
            Draw.Text(e.Graphics, Text, Font, titleColor, new Rectangle(0, 0, textWidth, Height), Draw.LeftMiddle);
        }
        else
        {
            var titleHeight = Draw.Measure(Text, Font).Height;
            var top = (Height - titleHeight - Draw.Measure(_description, Fonts.Small).Height - Dpi.Scale(this, 2)) / 2;
            Draw.Text(e.Graphics, Text, Font, titleColor, new Rectangle(0, top, textWidth, titleHeight), Draw.SingleLine);
            Draw.Text(
                e.Graphics,
                _description,
                Fonts.Small,
                Enabled ? Palette.TextDim : Palette.TextFaint,
                new Rectangle(0, top + titleHeight + Dpi.Scale(this, 2), textWidth, Height - top - titleHeight),
                Draw.SingleLine);
        }

        var fill = !Enabled
            ? Palette.SurfaceAlt
            : _checked
                ? _hovered ? Palette.AccentHover : Palette.Accent
                : _hovered ? Palette.SurfaceHover : Palette.SurfaceAlt;

        Draw.Fill(e.Graphics, track, track.Height / 2, fill);
        Draw.Border(e.Graphics, track, track.Height / 2, _checked ? fill : Enabled ? Palette.BorderStrong : Palette.Border);

        if (Focused && Enabled)
            Draw.Border(e.Graphics, Rectangle.Inflate(track, 3, 3), (track.Height + 6) / 2, Palette.Mix(Palette.Window, Palette.Text, 0.5));

        var knobSize = track.Height - Dpi.Scale(this, 8);
        var knobX = _checked ? track.Right - knobSize - Dpi.Scale(this, 4) : track.X + Dpi.Scale(this, 4);
        var knobColor = !Enabled ? Palette.TextFaint : _checked ? Palette.TextOnAccent : Palette.TextDim;
        using var knobBrush = new SolidBrush(knobColor);
        e.Graphics.FillEllipse(knobBrush, knobX, track.Y + Dpi.Scale(this, 4), knobSize, knobSize);
    }
}

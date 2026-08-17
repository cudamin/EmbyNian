using System.Diagnostics.CodeAnalysis;
using EmbyMpvClient.App.Theme;

namespace EmbyMpvClient.App.Controls;

/// <summary>
/// A single-line text field: a borderless <see cref="TextBox"/> inside a control that paints the
/// dark surface and the focus ring itself. WinForms' own border is drawn by the system in a
/// colour that cannot be changed, which is the one thing a dark theme cannot live with.
/// </summary>
public sealed class TextInput : Control
{
    private readonly TextBox _editor = new();
    private string _glyph = "";

    public TextInput()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        BackColor = Palette.SurfaceAlt;
        Font = Fonts.Body;
        Size = new Size(240, 32);

        _editor.BorderStyle = BorderStyle.None;
        _editor.BackColor = Palette.SurfaceAlt;
        _editor.ForeColor = Palette.Text;
        _editor.Font = Fonts.Body;
        _editor.GotFocus += (_, _) => Invalidate();
        _editor.LostFocus += (_, _) => Invalidate();
        _editor.TextChanged += (_, _) => OnTextChanged(EventArgs.Empty);
        _editor.KeyDown += OnEditorKeyDown;
        Controls.Add(_editor);
    }

    /// <summary>Raised when the user presses Enter in the field.</summary>
    public event EventHandler? Submitted;

    /// <summary>The wrapped box, for the rare case a caller needs a real TextBox member.</summary>
    public TextBox Editor => _editor;

    public string Placeholder
    {
        get => _editor.PlaceholderText;
        set => _editor.PlaceholderText = value ?? "";
    }

    public bool Password
    {
        get => _editor.UseSystemPasswordChar;
        set => _editor.UseSystemPasswordChar = value;
    }

    public bool ReadOnlyText
    {
        get => _editor.ReadOnly;
        set
        {
            _editor.ReadOnly = value;
            _editor.ForeColor = value ? Palette.TextDim : Palette.Text;
        }
    }

    /// <summary>A <see cref="Glyphs"/> codepoint drawn at the left edge, e.g. a search icon.</summary>
    public string Glyph
    {
        get => _glyph;
        set
        {
            _glyph = value ?? "";
            LayoutEditor();
            Invalidate();
        }
    }

    /// <summary>Matches <see cref="Control.Text"/>, which accepts null and means "empty".</summary>
    [AllowNull]
    public override string Text
    {
        get => _editor.Text;
        set => _editor.Text = value ?? "";
    }

    public void SelectAllText() => _editor.SelectAll();

    /// <summary>Moves the caret into the field; <c>Focus()</c> on the wrapper would land on the wrapper.</summary>
    public void FocusEditor() => _editor.Focus();

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        _editor.Focus();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutEditor();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        _editor.Font = Font;
        LayoutEditor();
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter) return;
        e.SuppressKeyPress = true;
        Submitted?.Invoke(this, EventArgs.Empty);
    }

    private void LayoutEditor()
    {
        var padding = Dpi.Scale(this, 10);
        var iconWidth = _glyph.Length == 0 ? 0 : Draw.Measure(_glyph, Fonts.Icon).Width + Dpi.Scale(this, 6);
        var left = padding + iconWidth;
        _editor.SetBounds(left, Math.Max(0, (Height - _editor.Height) / 2), Math.Max(10, Width - left - padding), _editor.Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Draw.Smooth(e.Graphics);
        var bounds = new Rectangle(0, 0, Width, Height);
        var focused = _editor.Focused;

        Draw.Fill(e.Graphics, bounds, 6, Palette.SurfaceAlt);
        Draw.Border(e.Graphics, bounds, 6, focused ? Palette.Accent : Palette.Border);

        if (_glyph.Length == 0) return;

        var padding = Dpi.Scale(this, 10);
        var iconWidth = Draw.Measure(_glyph, Fonts.Icon).Width;
        Draw.Text(
            e.Graphics,
            _glyph,
            Fonts.Icon,
            focused ? Palette.Text : Palette.TextFaint,
            new Rectangle(padding, 0, iconWidth, Height),
            Draw.Centered);
    }
}

/// <summary>
/// A <see cref="ComboBox"/> that draws its items in the app's colours — and, on top of that, paints
/// over the two pieces of chrome Windows insists on drawing itself: the light 3-D frame and the grey
/// drop-down button. Neither follows <see cref="Control.BackColor"/>, so on a dark page a stock
/// ComboBox shows up as a white-edged box.
/// </summary>
public sealed class DropDown : ComboBox
{
    private const int WmPaint = 0x000F;
    private const int WmPrint = 0x0317;
    private const int WmPrintClient = 0x0318;

    public DropDown()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        DropDownStyle = ComboBoxStyle.DropDownList;
        FlatStyle = FlatStyle.Flat;
        BackColor = Palette.SurfaceAlt;
        ForeColor = Palette.Text;
        Font = Fonts.Body;
        ItemHeight = 22;
        Size = new Size(200, 28);
    }

    /// <summary>Text shown for an item; defaults to <see cref="object.ToString"/>.</summary>
    public Func<object, string>? Describe { get; set; }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // Gives the drop-down list a dark scroll bar on Win11; ignored on anything older.
        Win11.UseDarkControls(this);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);

        // After the system has painted, not instead of it: the item text comes from OnDrawItem and
        // only the frame and the button need covering.
        switch (m.Msg)
        {
            case WmPaint:
            {
                using var graphics = CreateGraphics();
                PaintChrome(graphics);
                break;
            }

            // DrawToBitmap and the self-check screenshots come through here, with the target DC in
            // wParam — painting to CreateGraphics() would go to the screen instead of the bitmap.
            case WmPrint or WmPrintClient when m.WParam != IntPtr.Zero:
            {
                using var graphics = Graphics.FromHdc(m.WParam);
                PaintChrome(graphics);
                break;
            }
        }
    }

    private void PaintChrome(Graphics graphics)
    {
        Draw.Smooth(graphics);

        var button = Dpi.Scale(this, 20);
        var inner = new Rectangle(Width - button - 2, 2, button, Height - 4);
        Draw.Fill(graphics, inner, 0, Enabled ? Palette.SurfaceAlt : Palette.Surface);
        Draw.Text(
            graphics,
            Glyphs.ChevronDown,
            Fonts.IconSmall,
            Enabled ? Palette.TextDim : Palette.TextFaint,
            inner,
            Draw.Centered);

        Draw.Border(
            graphics,
            new Rectangle(0, 0, Width, Height),
            6,
            Focused || DroppedDown ? Palette.Accent : Enabled ? Palette.Border : Palette.Surface);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        var graphics = e.Graphics;
        var selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        var comboClosed = (e.State & DrawItemState.ComboBoxEdit) == DrawItemState.ComboBoxEdit;

        using var background = new SolidBrush(comboClosed ? Palette.SurfaceAlt : selected ? Palette.AccentSoft : Palette.Surface);
        graphics.FillRectangle(background, e.Bounds);

        if (e.Index < 0 || e.Index >= Items.Count) return;

        var item = Items[e.Index]!;
        var text = Describe?.Invoke(item) ?? item.ToString() ?? "";
        Draw.Text(
            graphics,
            text,
            Font,
            Enabled ? (selected && !comboClosed ? Palette.Accent : Palette.Text) : Palette.TextFaint,
            new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 10, e.Bounds.Height),
            Draw.LeftMiddle);
    }

    /// <summary>Replaces the items and selects <paramref name="selected"/> if it is present.</summary>
    public void Fill(IEnumerable<object> items, object? selected = null)
    {
        BeginUpdate();
        try
        {
            Items.Clear();
            foreach (var item in items) Items.Add(item);
            if (selected is not null && Items.Contains(selected)) SelectedItem = selected;
            else if (Items.Count > 0) SelectedIndex = 0;
        }
        finally
        {
            EndUpdate();
        }
    }
}

/// <summary>
/// A themed <see cref="NumericUpDown"/>. Same trick as <see cref="DropDown"/>: the system frame and
/// the spin buttons are painted over afterwards, because neither of them takes a colour.
/// </summary>
public sealed class NumberInput : NumericUpDown
{
    private const int WmPaint = 0x000F;
    private const int WmPrint = 0x0317;
    private const int WmPrintClient = 0x0318;

    public NumberInput(int minimum, int maximum, int value, string? suffix = null)
    {
        Minimum = minimum;
        Maximum = maximum;
        Value = Math.Clamp(value, minimum, maximum);
        BackColor = Palette.SurfaceAlt;
        ForeColor = Palette.Text;

        // None, not FixedSingle: the single-line border is drawn by the system in a fixed light grey.
        BorderStyle = BorderStyle.None;
        Font = Fonts.Body;
        TextAlign = HorizontalAlignment.Right;
        Width = 84;
        if (suffix is not null) AccessibleDescription = suffix;
    }

    public int IntValue
    {
        get => (int)Value;
        set => Value = Math.Clamp(value, Minimum, Maximum);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // The spin buttons are a child window of their own, so the parent's paint cannot reach them.
        // Their type is internal; they are the one child that is not the edit box.
        if (Controls.OfType<Control>().FirstOrDefault(child => child is not TextBoxBase) is { } buttons)
        {
            buttons.Paint += PaintSpinButtons;
            buttons.BackColor = Palette.SurfaceAlt;
        }
    }

    private void PaintSpinButtons(object? sender, PaintEventArgs e)
    {
        if (sender is not Control buttons) return;

        Draw.Smooth(e.Graphics);
        var half = buttons.Height / 2;
        var up = new Rectangle(0, 0, buttons.Width, half);
        var down = new Rectangle(0, half, buttons.Width, buttons.Height - half);

        using var background = new SolidBrush(Palette.SurfaceAlt);
        e.Graphics.FillRectangle(background, buttons.ClientRectangle);
        Draw.Text(e.Graphics, Glyphs.ChevronUp, Fonts.IconSmall, Palette.TextDim, up, Draw.Centered);
        Draw.Text(e.Graphics, Glyphs.ChevronDown, Fonts.IconSmall, Palette.TextDim, down, Draw.Centered);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);

        switch (m.Msg)
        {
            case WmPaint:
            {
                using var graphics = CreateGraphics();
                PaintBorder(graphics);
                break;
            }

            case WmPrint or WmPrintClient when m.WParam != IntPtr.Zero:
            {
                using var graphics = Graphics.FromHdc(m.WParam);
                PaintBorder(graphics);
                break;
            }
        }
    }

    private void PaintBorder(Graphics graphics)
    {
        Draw.Smooth(graphics);
        Draw.Border(
            graphics,
            new Rectangle(0, 0, Width, Height),
            6,
            ContainsFocus ? Palette.Accent : Palette.Border);
    }
}

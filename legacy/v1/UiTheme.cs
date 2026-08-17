using System.Drawing.Drawing2D;

namespace EmbyMpvClient;

internal static class UiTheme
{
    public static readonly Color Background = Color.FromArgb(15, 17, 21);
    public static readonly Color Surface = Color.FromArgb(23, 26, 32);
    public static readonly Color SurfaceRaised = Color.FromArgb(31, 35, 43);
    public static readonly Color Border = Color.FromArgb(48, 53, 64);
    public static readonly Color Text = Color.FromArgb(239, 241, 245);
    public static readonly Color TextMuted = Color.FromArgb(158, 164, 177);
    public static readonly Color Accent = Color.FromArgb(37, 177, 130);
    public static readonly Color AccentHover = Color.FromArgb(46, 196, 146);
    public static readonly Color Danger = Color.FromArgb(224, 92, 92);

    public static Font CreateFont(float size, FontStyle style = FontStyle.Regular) =>
        new("Microsoft YaHei UI", size, style, GraphicsUnit.Point);

    public static Button CreateButton(string text, bool primary = false)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(82, 36),
            Padding = new Padding(14, 0, 14, 0),
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Accent : SurfaceRaised,
            ForeColor = Text,
            Cursor = Cursors.Hand,
            Font = CreateFont(9F, FontStyle.Bold),
            Margin = new Padding(0, 0, 8, 0)
        };
        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.MouseOverBackColor = primary ? AccentHover : Color.FromArgb(40, 45, 55);
        return button;
    }

    public static TextBox CreateTextBox(string placeholder = "") => new()
    {
        PlaceholderText = placeholder,
        BackColor = SurfaceRaised,
        ForeColor = Text,
        BorderStyle = BorderStyle.FixedSingle,
        Font = CreateFont(10F),
        Margin = new Padding(0)
    };

    public static Label CreateLabel(string text, float size = 9F, bool strong = false) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = strong ? Text : TextMuted,
        Font = CreateFont(size, strong ? FontStyle.Bold : FontStyle.Regular)
    };
}

internal sealed class NavigationButton : Button
{
    private bool _selected;

    public NavigationButton(string text)
    {
        Text = text;
        Height = 44;
        Dock = DockStyle.Top;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        TextAlign = ContentAlignment.MiddleLeft;
        Padding = new Padding(18, 0, 0, 0);
        Font = UiTheme.CreateFont(9.5F, FontStyle.Bold);
        Cursor = Cursors.Hand;
        UpdateColors();
    }

    public bool Selected
    {
        get => _selected;
        set { _selected = value; UpdateColors(); }
    }

    private void UpdateColors()
    {
        BackColor = _selected ? UiTheme.SurfaceRaised : UiTheme.Surface;
        ForeColor = _selected ? UiTheme.AccentHover : UiTheme.TextMuted;
        FlatAppearance.MouseOverBackColor = UiTheme.SurfaceRaised;
    }
}

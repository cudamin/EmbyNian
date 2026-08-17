using EmbyMpvClient.App.Theme;

namespace EmbyMpvClient.App.Controls;

// The small pieces of chrome that are not the transport bar itself: the title above the video,
// transient messages, the busy overlay and the thin progress line. The bar lives in PlayerBar,
// and the pickers it used to delegate to its neighbours live in PlayerFlyout.

/// <summary>
/// The playing item's title and episode code, floating at the top-left of the video next to
/// the exit button. A borderless owned window like the bottom bar so its Opacity blends with
/// the video; shown and hidden together with the floating controls.
/// </summary>
public sealed class PlaybackTitleBar : Form
{
    private string _title = "";

    public PlaybackTitleBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Opacity = 0.65;
        BackColor = Color.FromArgb(18, 20, 24);
        Visible = false;
    }

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

    private const int NoActivate = 0x08000000;
    private const int ToolWindow = 0x00000080;

    public void SetTitle(string title)
    {
        _title = title ?? "";
        var padding = Dpi.Scale(this, 14);
        var textWidth = Draw.Measure(_title, Fonts.BodyStrong).Width;
        Width = textWidth + padding * 2;
        Height = Dpi.Scale(this, 40);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Draw.Smooth(e.Graphics);
        e.Graphics.Clear(BackColor);
        var padding = Dpi.Scale(this, 14);
        Draw.Text(
            e.Graphics,
            _title,
            Fonts.BodyStrong,
            Palette.Text,
            new Rectangle(padding, 0, Math.Max(10, Width - padding * 2), Height),
            Draw.LeftMiddle);
    }
}

public enum ToastKind
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>
/// A transient message in the bottom-right corner. Used instead of a MessageBox for anything the
/// user does not have to answer — a failed poster fetch or a finished playback should not steal
/// the keyboard.
/// </summary>
public sealed class Toast : Control
{
    private readonly System.Windows.Forms.Timer _timer = new();
    private ToastKind _kind = ToastKind.Info;

    public Toast()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Palette.SurfaceAlt;
        Font = Fonts.Body;
        Visible = false;
        Cursor = Cursors.Hand;

        _timer.Tick += (_, _) => Dismiss();
    }

    /// <summary>Shows a message; a second call replaces whatever is on screen.</summary>
    public void Show(string message, ToastKind kind = ToastKind.Info, int seconds = 5)
    {
        Text = message;
        _kind = kind;

        var width = Math.Min(Dpi.Scale(this, 520), Draw.Measure(message, Font).Width + Dpi.Scale(this, 64));
        var height = Math.Max(Dpi.Scale(this, 44), Draw.Measure(message, Font, width - Dpi.Scale(this, 64)).Height + Dpi.Scale(this, 24));
        Size = new Size(width, height);

        Visible = true;
        BringToFront();
        Invalidate();

        _timer.Stop();
        _timer.Interval = Math.Max(1500, seconds * 1000);
        _timer.Start();
    }

    public void Dismiss()
    {
        _timer.Stop();
        Visible = false;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Dismiss();
        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Draw.Smooth(e.Graphics);
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        var (accent, glyph) = _kind switch
        {
            ToastKind.Success => (Palette.Accent, Glyphs.Check),
            ToastKind.Warning => (Palette.Warning, Glyphs.Warning),
            ToastKind.Error => (Palette.Danger, Glyphs.Warning),
            _ => (Palette.Info, Glyphs.Info)
        };

        Draw.Fill(e.Graphics, bounds, 8, Palette.SurfaceAlt);
        Draw.Border(e.Graphics, bounds, 8, accent);
        Draw.Fill(e.Graphics, new Rectangle(0, Dpi.Scale(this, 8), Dpi.Scale(this, 3), Height - Dpi.Scale(this, 16)), 2, accent);

        var iconWidth = Dpi.Scale(this, 26);
        Draw.Text(e.Graphics, glyph, Fonts.Icon, accent, new Rectangle(Dpi.Scale(this, 12), 0, iconWidth, Height), Draw.Centered);
        Draw.Text(
            e.Graphics,
            Text,
            Font,
            Palette.Text,
            new Rectangle(Dpi.Scale(this, 12) + iconWidth + Dpi.Scale(this, 6), Dpi.Scale(this, 10), Width - Dpi.Scale(this, 58), Height - Dpi.Scale(this, 18)),
            Draw.Wrapped);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Covers the content area while a page loads. Opaque rather than translucent: a WinForms child
/// control can only blend with its parent's background, never with the sibling controls beneath
/// it, so a "half transparent" overlay would show the window colour and nothing else anyway.
/// </summary>
public sealed class LoadingOverlay : Control
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };
    private int _angle;

    public LoadingOverlay()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Palette.Mix(Palette.Window, Palette.Surface, 0.5);
        Font = Fonts.Body;
        Visible = false;
        _timer.Tick += (_, _) =>
        {
            _angle = (_angle + 12) % 360;
            Invalidate();
        };
    }

    public void Begin(string message = "正在加载…")
    {
        Text = message;
        Visible = true;
        BringToFront();
        _timer.Start();
        Invalidate();
    }

    public void End()
    {
        _timer.Stop();
        Visible = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Draw.Smooth(e.Graphics);
        e.Graphics.Clear(BackColor);

        var size = Dpi.Scale(this, 34);
        var arc = new Rectangle((Width - size) / 2, (Height - size) / 2 - Dpi.Scale(this, 14), size, size);

        using (var track = new Pen(Palette.Border, Dpi.Scale(this, 3)))
        {
            e.Graphics.DrawEllipse(track, arc);
        }

        using (var pen = new Pen(Palette.Accent, Dpi.Scale(this, 3)))
        {
            e.Graphics.DrawArc(pen, arc, _angle, 90);
        }

        Draw.Text(
            e.Graphics,
            Text,
            Font,
            Palette.TextDim,
            new Rectangle(0, arc.Bottom + Dpi.Scale(this, 12), Width, Dpi.Scale(this, 22)),
            Draw.Centered);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// The thin progress line at the very bottom of the video, shown while the cursor sits in
/// the middle of the screen and the full bottom bar is faded out.
/// </summary>
public sealed class ProgressStrip : Control
{
    private double _fraction;

    public ProgressStrip()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Visible = false;
        TabStop = false;
    }

    public void SetFraction(double fraction)
    {
        var clamped = Math.Clamp(fraction, 0, 1);
        if (Math.Abs(clamped - _fraction) < 0.001) return;
        _fraction = clamped;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Draw.Smooth(e.Graphics);
        using var track = new SolidBrush(Color.FromArgb(90, 255, 255, 255));
        using var fill = new SolidBrush(Palette.Accent);
        e.Graphics.FillRectangle(track, 0, 0, Width, Height);
        if (_fraction > 0)
        {
            e.Graphics.FillRectangle(fill, 0, 0, (int)(Width * _fraction), Height);
        }
    }
}

using EmbyNian.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace EmbyNian.Shell.Views;

/// <summary>
/// The HTML 颜色选择器, in the shape of the page it was modelled on (rapidtables'): a saturation×value
/// square over the current hue, a hue bar, the six numbers R/G/B/H/S/V, and the six-digit code. What
/// makes it this app's rather than a copy is the empty string: 「不设置」 is a value the three 字幕颜色
/// rows carry (it means mpv's own default stands), so a button for it is part of the control.
/// <para>
/// <see cref="Color"/> is a plain string dependency property so a settings row can two-way-bind it with
/// x:Bind and no glue code. Everything the pointer does updates only what is on screen; <see cref="Color"/>
/// is written on a commit — pointer release, a number box settling, six hex digits typed — because across
/// the two-way binding a write is a settings save, and a drag across the square must not be forty of them.
/// </para>
/// </summary>
public sealed partial class HtmlColorPicker : UserControl
{
    /// <summary>The current colour as H 0–360, S 0–100, V 0–100 — what the two drag surfaces edit.</summary>
    private int _h;
    private int _s;
    private int _v = 100;

    /// <summary>
    /// True while this control is putting a value on the screen or through <see cref="Color"/>, so its
    /// own echoes are not read as edits. One flag, not one per control: the refresh paths run together.
    /// </summary>
    private bool _quiet;

    /// <summary>
    /// Whether each drag surface currently holds the pointer. WinUI's <c>UIElement</c> has no
    /// 「is captured」 to ask, so the two drags keep their own answer — set on press, cleared on release
    /// and on the one press that never got its release (a pointer lifted outside the window).
    /// </summary>
    private bool _squareDragging;
    private bool _hueDragging;

    public HtmlColorPicker()
    {
        InitializeComponent();

        SatValSquare.PointerPressed += OnSquarePressed;
        SatValSquare.PointerMoved += OnSquareMoved;
        SatValSquare.PointerReleased += OnSquareReleased;
        HueBar.PointerPressed += OnHuePressed;
        HueBar.PointerMoved += OnHueMoved;
        HueBar.PointerReleased += OnHueReleased;

        RedBox.ValueChanged += OnRgbChanged;
        GreenBox.ValueChanged += OnRgbChanged;
        BlueBox.ValueChanged += OnRgbChanged;
        HueBox.ValueChanged += OnHsvChanged;
        SaturationBox.ValueChanged += OnHsvChanged;
        ValueBox.ValueChanged += OnHsvChanged;

        HexBox.TextChanged += OnHexTextChanged;
        HexBox.LostFocus += OnHexLostFocus;

        ClearButton.Click += (_, _) => SetColor("");

        // The thumbs are placed by arithmetic on the two surfaces' size, and the first Refresh runs before
        // any of that size exists. Re-placing them when it arrives is what keeps the circle off the corner
        // on the first open.
        SatValSquare.SizeChanged += (_, _) => Refresh();
        HueBar.SizeChanged += (_, _) => Refresh();

        Refresh();
    }

    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(string), typeof(HtmlColorPicker), new PropertyMetadata(string.Empty, OnColorPropertyChanged));

    /// <summary><c>#RRGGBB</c>, or the empty string for 「不设置」. Two-way-bound to a settings row.</summary>
    public string Color
    {
        get => (string)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    private static void OnColorPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var picker = (HtmlColorPicker)sender;
        if (picker._quiet) return;

        // From outside — the row the picker is bound to. Nothing here commits back: only the user's own
        // gestures do, and they go through SetColor.
        if (HtmlColor.TryParse(args.NewValue as string, out var rgb))
        {
            var (r, g, b) = HtmlColor.Rgb(rgb);
            (picker._h, picker._s, picker._v) = HtmlColor.ToHsv(r, g, b);
        }

        picker.Refresh();
    }

    /// <summary>Commits one colour through the binding — the one door every user gesture ends at.</summary>
    private void SetColor(string value)
    {
        _quiet = true;
        try { Color = value; }
        finally { _quiet = false; }
    }

    private void OnSquarePressed(object sender, PointerRoutedEventArgs e)
    {
        SatValSquare.CapturePointer(e.Pointer);
        _squareDragging = true;
        DragSquare(e);
    }

    private void OnSquareMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_squareDragging) DragSquare(e);
    }

    private void OnSquareReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_squareDragging) return;

        _squareDragging = false;
        SatValSquare.ReleasePointerCapture(e.Pointer);
        DragSquare(e);
        Commit();
    }

    private void OnHuePressed(object sender, PointerRoutedEventArgs e)
    {
        HueBar.CapturePointer(e.Pointer);
        _hueDragging = true;
        DragHue(e);
    }

    private void OnHueMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_hueDragging) DragHue(e);
    }

    private void OnHueReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_hueDragging) return;

        _hueDragging = false;
        HueBar.ReleasePointerCapture(e.Pointer);
        DragHue(e);
        Commit();
    }

    /// <summary>Where in the square the pointer sits, as saturation across and value down.</summary>
    private void DragSquare(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(SatValSquare).Position;
        _s = ToPercent(point.X, SatValSquare.ActualWidth);
        _v = 100 - ToPercent(point.Y, SatValSquare.ActualHeight);
        Refresh();
    }

    /// <summary>Where in the hue bar the pointer sits, as 0–360.</summary>
    private void DragHue(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(HueBar).Position;
        _h = (int)Math.Round(360 * Math.Clamp(point.Y / Math.Max(1, HueBar.ActualHeight), 0, 1)) % 360;
        Refresh();
    }

    private static int ToPercent(double position, double extent) =>
        (int)Math.Round(100 * Math.Clamp(position / Math.Max(1, extent), 0, 1));

    private void OnRgbChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_quiet) return;

        // An emptied number box reports NaN rather than zero; casting that to a byte would throw. A
        // cleared channel simply means nothing new was said, so the current one stands until a number is.
        var (currentR, currentG, currentB) = HtmlColor.FromHsv(_h, _s, _v);
        var r = Channel(RedBox.Value, currentR);
        var g = Channel(GreenBox.Value, currentG);
        var b = Channel(BlueBox.Value, currentB);
        (_h, _s, _v) = HtmlColor.ToHsv(r, g, b);
        Refresh();
        Commit();
    }

    private void OnHsvChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_quiet) return;

        _h = Axis(HueBox.Value, _h, max: 360) % 360;
        _s = Axis(SaturationBox.Value, _s, max: 100);
        _v = Axis(ValueBox.Value, _v, max: 100);
        Refresh();
        Commit();
    }

    /// <summary>A 0–255 channel as typed, clamped; NaN (a cleared box) keeps what it was.</summary>
    private static byte Channel(double value, byte current) =>
        double.IsFinite(value) ? (byte)Math.Clamp(value, 0, 255) : current;

    /// <summary>An H/S/V axis as typed, clamped to its own ceiling; NaN (a cleared box) keeps what it was.</summary>
    private static int Axis(double value, int current, int max) =>
        double.IsFinite(value) ? (int)Math.Clamp(Math.Round(value), 0, max) : current;

    private void OnHexTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_quiet) return;

        // Live, like the page it is modelled on — but only when what is typed is a whole colour. A half-
        // typed code is left in the box alone; the colour moves on when the sixth digit lands.
        if (!HtmlColor.TryParse(HexBox.Text, out var rgb)) return;

        var (r, g, b) = HtmlColor.Rgb(rgb);
        (_h, _s, _v) = HtmlColor.ToHsv(r, g, b);
        Refresh();
        Commit();
    }

    private void OnHexLostFocus(object sender, RoutedEventArgs e) => RedrawHex();

    /// <summary>The current state as a colour, out through the binding.</summary>
    private void Commit() => SetColor(HtmlColor.Format(CurrentColor()));

    /// <summary>The state as a packed <c>RRGGBB</c> — the one form every reader here wants.</summary>
    private int CurrentColor()
    {
        var (r, g, b) = HtmlColor.FromHsv(_h, _s, _v);
        return HtmlColor.Pack(r, g, b);
    }

    /// <summary>Draws the state onto everything, quietly — the number boxes included.</summary>
    private void Refresh()
    {
        _quiet = true;
        try
        {
            var rgb = CurrentColor();
            var (r, g, b) = HtmlColor.Rgb(rgb);

            var (hueR, hueG, hueB) = HtmlColor.FromHsv(_h, 100, 100);
            HueBase.Fill = new SolidColorBrush(ToColor(HtmlColor.Pack(hueR, hueG, hueB)));
            PreviewBlock.Background = new SolidColorBrush(ToColor(rgb));

            // The thumbs sit on Canvas, which sizes children by Left/Top rather than by alignment. Half a
            // thumb in from each edge keeps the circle inside the square at the corners.
            Canvas.SetLeft(SatValThumb, Math.Clamp(_s / 100.0 * (SatValSquare.ActualWidth - 12), 0, SatValSquare.ActualWidth - 12));
            Canvas.SetTop(SatValThumb, Math.Clamp((100 - _v) / 100.0 * (SatValSquare.ActualHeight - 12), 0, SatValSquare.ActualHeight - 12));
            Canvas.SetLeft(HueThumb, -2);
            Canvas.SetTop(HueThumb, Math.Clamp(_h / 360.0 * (HueBar.ActualHeight - 6), 0, HueBar.ActualHeight - 6));

            RedBox.Value = r;
            GreenBox.Value = g;
            BlueBox.Value = b;
            HueBox.Value = _h;
            SaturationBox.Value = _s;
            ValueBox.Value = _v;

            RedrawHex();
        }
        finally { _quiet = false; }
    }

    private void RedrawHex()
    {
        var hex = HtmlColor.Format(CurrentColor());
        if (!string.Equals(HexBox.Text, hex, StringComparison.Ordinal)) HexBox.Text = hex;
    }

    private static Windows.UI.Color ToColor(int rgb)
    {
        var (r, g, b) = HtmlColor.Rgb(rgb);
        return Windows.UI.Color.FromArgb(255, r, g, b);
    }
}

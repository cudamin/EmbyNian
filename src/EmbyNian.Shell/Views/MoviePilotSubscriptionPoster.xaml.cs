using EmbyNian.Shell.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;

namespace EmbyNian.Shell.Views;

public sealed partial class MoviePilotSubscriptionPoster : UserControl
{
    public static readonly DependencyProperty CardProperty = DependencyProperty.Register(nameof(Card),
        typeof(MoviePilotSubscriptionCard), typeof(MoviePilotSubscriptionPoster), new PropertyMetadata(null, OnCardChanged));
    private readonly HoverWatch _watch;
    private Storyboard? _motion;
    private bool _live;
    private bool _hovered;
    private bool _focused;
    private bool _pressed;

    public MoviePilotSubscriptionPoster()
    {
        InitializeComponent();
        _watch = new HoverWatch(Root, SetHovered);
        Root.AddHandler(PointerPressedEvent, new PointerEventHandler(OnPressed), true);
        Root.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnReleased), true);
        Root.AddHandler(PointerCanceledEvent, new PointerEventHandler(OnReleased), true);
        Root.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnReleased), true);
        Loaded += (_, _) => _live = true;
        Unloaded += (_, _) => { _live = false; ResetMotion(); };
    }

    public MoviePilotSubscriptionCard? Card
    {
        get => (MoviePilotSubscriptionCard?)GetValue(CardProperty);
        set => SetValue(CardProperty, value);
    }
    public event EventHandler? OpenRequested;
    public event EventHandler? MenuRequested;
    internal FrameworkElement MenuAnchor => SubscriptionMore.Visibility == Visibility.Visible ? SubscriptionMore : SubscriptionCover;
    internal double Zoom => PosterMotion.ScaleX;
    internal int PointerEntries { get; private set; }
    private static void OnCardChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((MoviePilotSubscriptionPoster)sender).ResetMotion();

    private void OnOpen(object sender, RoutedEventArgs args) => OpenRequested?.Invoke(this, EventArgs.Empty);
    private void OnMore(object sender, RoutedEventArgs args) => MenuRequested?.Invoke(this, EventArgs.Empty);
    private void OnContext(UIElement sender, ContextRequestedEventArgs args)
    { args.Handled = true; MenuRequested?.Invoke(this, EventArgs.Empty); }
    private void OnPointerEntered(object sender, PointerRoutedEventArgs args) { PointerEntries++; _watch.Enter(); }
    private void OnPointerExited(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(Root).Position;
        if (point.X < 0 || point.Y < 0 || point.X >= Root.ActualWidth || point.Y >= Root.ActualHeight) _watch.Leave();
    }
    private void OnGotFocus(object sender, RoutedEventArgs args) { _focused = true; Animate(); }
    private void OnLostFocus(object sender, RoutedEventArgs args) { _focused = false; Animate(); }
    private void OnPressed(object sender, PointerRoutedEventArgs args) { _pressed = true; Animate(); }
    private void OnReleased(object sender, PointerRoutedEventArgs args) { _pressed = false; Animate(); }
    internal void SetHovered(bool value)
    {
        _hovered = value;
        SubscriptionMore.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        if (!value) _pressed = false;
        Animate();
    }

    private void Animate()
    {
        var fromZoom = PosterMotion.ScaleX;
        var fromPress = PressMotion.ScaleX;
        _motion?.Stop();
        _motion = null;
        var active = _hovered || _focused;
        var enabled = HomeMotion.AnimationsEnabled;
        var zoom = enabled && active ? 1.045 : 1;
        var press = enabled && _pressed ? 0.985 : 1;
        Art.Style = (Style)Application.Current.Resources[active ? "EgFrameActiveStyle" : "EgFrameStyle"];
        void Settle()
        {
            PosterMotion.ScaleX = PosterMotion.ScaleY = zoom;
            PressMotion.ScaleX = PressMotion.ScaleY = press;
        }
        if (!_live || XamlRoot is null || !enabled) { Settle(); return; }
        var board = new Storyboard();
        Add(PosterMotion, "ScaleX", fromZoom, zoom); Add(PosterMotion, "ScaleY", fromZoom, zoom);
        Add(PressMotion, "ScaleX", fromPress, press); Add(PressMotion, "ScaleY", fromPress, press);
        _motion = board;
        board.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_motion, board)) return;
            Settle(); board.Stop(); _motion = null;
        };
        board.Begin();
        void Add(DependencyObject target, string property, double from, double to)
        {
            var animation = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(_pressed ? 90 : active ? 240 : 180)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(animation, target); Storyboard.SetTargetProperty(animation, property);
            board.Children.Add(animation);
        }
    }

    private void ResetMotion()
    {
        _watch?.Leave();
        _hovered = _focused = _pressed = false;
        SubscriptionMore.Visibility = Visibility.Collapsed;
        _motion?.Stop(); _motion = null;
        PosterMotion.ScaleX = PosterMotion.ScaleY = PressMotion.ScaleX = PressMotion.ScaleY = 1;
        Art.Style = (Style)Application.Current.Resources["EgFrameStyle"];
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace EmbyNian.Shell.Views;

/// <summary>
/// One row of a 列表视图: a 16:9 still on the left, the numbered title, the 「日期 · 时长」 line and a
/// two-line synopsis to its right. Both 媒体库's list mode and the detail page's 单集 list use it —
/// 「把季页中的集改回成列表显示」 — because a season read down the page is the same row either way.
/// <para>
/// The row body — click opens the item's page, right-click opens the card menu — is not in here. As
/// with a poster card, the page's DataTemplate wraps this control in a container whose click is the
/// page's own handler, so a row and a card are the same thing wherever the page hands them on.
/// </para>
/// <para>
/// What is in here is the recycling contract — artwork loads on realisation and is released on recycle,
/// the same three hooks <see cref="PosterCard"/> carries — the hover play button, raised through
/// <see cref="ActionRequested"/> rather than acted on, for the same reason PosterCard raises it (a row
/// has no session to call and no page to notify), and the 「you are here」 title colour, which is the one
/// thing here that cannot be a binding.
/// </para>
/// </summary>
public sealed partial class EpisodeRow : UserControl
{
    /// <summary>
    /// The two title styles, swapped in <see cref="OnCardChanged"/>. Keys rather than brushes because
    /// the accent colour is declared inside <c>Palette.xaml</c>'s <c>ThemeDictionaries</c>, and a
    /// theme-dictionary key fetched through <c>Resources[...]</c> throws — only the XAML parser resolves
    /// those. So the colour is stated in markup and code does nothing but pick which of the two to hang on.
    /// </summary>
    private const string TitleStyle = "EgRowTitleStyle";

    private const string CurrentTitleStyle = "EgRowTitleCurrentStyle";

    public static readonly DependencyProperty CardProperty = DependencyProperty.Register(
        nameof(Card),
        typeof(CardItem),
        typeof(EpisodeRow),
        new PropertyMetadata(null, OnCardChanged));

    /// <summary>
    /// Tracked rather than read from <c>IsLoaded</c>, as on <see cref="PosterCard"/>: the two events
    /// below are the authority on when a recycled container is in the tree, and a field cannot disagree
    /// with them.
    /// </summary>
    private bool _live;

    /// <summary>
    /// 「鼠标移出窗口后不会自动恢复」：the pointer's departure, checked against the OS rather than taken from
    /// the exit event alone. See <see cref="HoverWatch"/> for why the event is not enough.
    /// </summary>
    private readonly HoverWatch _watch;

    /// <summary>键盘焦点那一半，见 <see cref="FocusWatch"/>。和卡片一字不差的两个旗子加一个 Paint。</summary>
    private readonly FocusWatch _focus;

    private bool _hovered;

    private bool _focused;

    public EpisodeRow()
    {
        InitializeComponent();

        _watch = new HoverWatch(Root, up =>
        {
            Hover.Visibility = up ? Visibility.Visible : Visibility.Collapsed;
            _hovered = up;
            Paint();
        });

        _focus = new FocusWatch(Root, on =>
        {
            _focused = on;
            Paint();
        });

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// The row's hover play button was pressed. The card itself is not in here: the sender is the
    /// <see cref="EpisodeRow"/>, whose <see cref="Card"/> is the only copy that is certain to be the
    /// one on screen after a container has been recycled.
    /// </summary>
    public event EventHandler<CardActionEventArgs>? ActionRequested;

    public CardItem? Card
    {
        get => (CardItem?)GetValue(CardProperty);
        set => SetValue(CardProperty, value);
    }

    private static void OnCardChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var row = (EpisodeRow)sender;

        // The card being replaced is still in its page's list and would otherwise keep its bitmap.
        // keepWaiting: false —— 同 PosterCard.OnCardChanged：这一句才是权威的「它失去容器了」。
        (args.OldValue as CardItem)?.ReleasePoster(keepWaiting: false);

        // Applied on every hand-over rather than bound: a recycled container keeps whichever style it was
        // last given, so the row that used to be 「you are here」 has to be told it no longer is.
        row.RowTitle.Style = (Style)row.Resources[
            args.NewValue is CardItem { IsCurrentEpisode: true } ? CurrentTitleStyle : TitleStyle];

        // 无条件发起，同 PosterCard.OnCardChanged：_live 这道门信不过 —— 回收池对看得到的容器喊了一声
        // Unloaded 之后 Loaded 再也不来，门后就永远没人发请求，行就是一张灰占位。
        row.Begin();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _live = true;
        _focus.Attach();
        Begin();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // 同 PosterCard.OnUnloaded：这一声会对着还在树上的行喊出来，XamlRoot 现问一遍才算数。
        _live = XamlRoot is not null;
        if (_live) return;

        Card?.ReleasePoster();

        // A recycled container must not come back with the button already up: the pointer that revealed
        // it is nowhere near wherever this container is reused.
        _watch.Leave();
        _hovered = false;
        _focus.Detach();
    }

    /// <summary>胶片格那圈线，与 <see cref="PosterCard.Paint"/> 同一对样式键、同一个理由。</summary>
    private void Paint() =>
        Art.Style = (Style)Application.Current.Resources[
            _hovered || _focused ? "EgFrameActiveStyle" : "EgFrameStyle"];

    /// <summary>
    /// Deliberately not awaited: a container coming into view must not block the layout pass on a
    /// download. Everything that can go wrong is handled inside <see cref="CardItem.EnsurePosterAsync"/>.
    /// </summary>
    private void Begin() => _ = Card?.EnsurePosterAsync();

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e) => _watch.Enter();

    /// <summary>
    /// Hides the hover button, but only once the pointer has really left the row. PointerExited bubbles,
    /// and a click's pointer capture raises it without the pointer having moved at all — which would
    /// flicker the button out from under the one being pressed. Where the pointer actually is settles it.
    /// <para>
    /// Only the fast path, as on a card: an exit raised because the pointer left the window reports a
    /// position still inside the row, and <see cref="HoverWatch"/> is what catches that one.
    /// </para>
    /// </summary>
    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root).Position;

        if (point.X >= 0 && point.Y >= 0 && point.X < Root.ActualWidth && point.Y < Root.ActualHeight)
            return;

        _watch.Leave();
    }

    private void OnPlayClicked(object sender, RoutedEventArgs e)
    {
        if (Card is null || sender is not FrameworkElement anchor) return;

        ActionRequested?.Invoke(this, new CardActionEventArgs(CardAction.Play, anchor));
    }
}

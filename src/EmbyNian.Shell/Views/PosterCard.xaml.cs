using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 自检：what one card's hover buttons are and where they sit, with the pointer's part of it faked.
/// <paramref name="OffsetX"/> and <paramref name="OffsetY"/> are the play button's centre measured from
/// the artwork's centre, so 「in the middle of the cover」 is 0,0 and nothing else;
/// <paramref name="InsetRight"/> and <paramref name="InsetBottom"/> are the three icons' bottom-right
/// corner measured from the artwork's, so 「贴在封面的右下角」 is a small number and centred is half a
/// card. <paramref name="Chrome"/> is whether any of the three still paints a background in any of its
/// three states — the grey circles and the black pill that 「仅保留白色的图标按钮」 took away.
/// <paramref name="Watch"/> is whether the card can tell where the cursor really is, which is what takes
/// the buttons back down when the pointer leaves the window without saying so.
/// <para>
/// 最后三个说的是胶片格那圈线（重绘单元 4）：<paramref name="FrameRest"/> 是静止时的色，
/// <paramref name="FrameActive"/> 是指针上来之后的色 —— 「整圈变强调色」是个颜色的说法，而颜色对不对，
/// 截图里 1 像素宽的一根线看不出来。<paramref name="FrameFocus"/> 是键盘那一半接上了没有：卡片自己接不到
/// 焦点，得往上找到外层容器，找不到的话这条状态整个不存在，而那是完全没有症状的。
/// </para>
/// </summary>
internal sealed record CardHoverProbe(
    string Title,
    bool Play,
    bool Watched,
    bool Favorite,
    bool More,
    double Size,
    double OffsetX,
    double OffsetY,
    double ArtWidth,
    double ArtHeight,
    double InsetRight,
    double InsetBottom,
    bool Chrome,
    bool Watch,
    Windows.UI.Color FrameRest,
    Windows.UI.Color FrameActive,
    bool FrameFocus);

/// <summary>需求 6：which of the card's hover buttons was pressed.</summary>
public enum CardAction
{
    /// <summary>
    /// Starts the item straight away. In the middle of the artwork, where the eye already is, because
    /// the card body itself opens the detail page (「把播放按钮移动到媒体封面的中心」).
    /// </summary>
    Play,
    Watched,
    Favorite,
    More
}

/// <summary>
/// What <see cref="PosterCard.ActionRequested"/> carries. The card itself is not in here: the sender is
/// the <see cref="PosterCard"/>, whose <see cref="PosterCard.Card"/> is the only copy that is certain
/// to be the one on screen after a container has been recycled.
/// </summary>
public sealed class CardActionEventArgs(CardAction action, FrameworkElement anchor) : EventArgs
{
    public CardAction Action { get; } = action;

    /// <summary>The button that was pressed, so 更多 opens its menu at the button and not at the card.</summary>
    public FrameworkElement Anchor { get; } = anchor;
}

/// <summary>
/// The control every card in the app is drawn with. It owns the shape; <see cref="CardItem"/> owns
/// the content.
/// <para>
/// The two size properties are set from the DataTemplate rather than bound, so one control covers both
/// the 2:3 poster in a library grid and the 16:9 still in a 继续观看 row without a second copy of the
/// markup.
/// </para>
/// <para>
/// It is also where the recycling contract is honoured. A container is realized, handed a card, later
/// detached and handed a different one; each of those three moments has to move the artwork along with
/// it, and the events below are the only places that know when they happen.
/// </para>
/// </summary>
public sealed partial class PosterCard : UserControl
{
    public static readonly DependencyProperty CardProperty = DependencyProperty.Register(
        nameof(Card),
        typeof(CardItem),
        typeof(PosterCard),
        new PropertyMetadata(null, OnCardChanged));

    public static readonly DependencyProperty CardWidthProperty = DependencyProperty.Register(
        nameof(CardWidth),
        typeof(double),
        typeof(PosterCard),
        new PropertyMetadata(170d));

    public static readonly DependencyProperty PosterHeightProperty = DependencyProperty.Register(
        nameof(PosterHeight),
        typeof(double),
        typeof(PosterCard),
        new PropertyMetadata(255d));

    /// <summary>
    /// 这张卡压在一张剧照上，而不是站在页面那张纸上 —— 集页把同季那一带摆在头图底下的画面里
    /// （见 <c>DetailPage.PlaceEpisodes</c>）。底下那两行字于是得换成压在图上那一套不随主题走的墨：
    /// 主题自己的墨在晴昼（唯一那套浅色）下是近黑色，压在七成二的黑罩子上一个字都读不出来。
    /// </summary>
    public static readonly DependencyProperty OnScrimProperty = DependencyProperty.Register(
        nameof(OnScrim),
        typeof(bool),
        typeof(PosterCard),
        new PropertyMetadata(false, OnScrimChanged));

    /// <summary>
    /// Tracked rather than read from <c>IsLoaded</c>: the two events below are the authority on when a
    /// recycled container is in the tree, and a field cannot disagree with them.
    /// </summary>
    private bool _live;

    /// <summary>
    /// 「鼠标移出窗口后不会自动恢复」：the pointer's departure, checked against the OS rather than taken from
    /// the exit event alone. See <see cref="HoverWatch"/> for why the event is not enough.
    /// </summary>
    private readonly HoverWatch _watch;

    /// <summary>键盘焦点那一半，见 <see cref="FocusWatch"/>。</summary>
    private readonly FocusWatch _focus;

    /// <summary>
    /// 胶片格那圈线是不是该亮着的两个来源。分成两个旗子而不是一个 bool：指针和焦点会同时在这张卡上，
    /// 也会一前一后地走，谁都不能替对方回答 —— 用同一个 bool 的话，点了悬浮层里的按钮再把指针移开，
    /// 焦点还在这张卡上，线却熄了。
    /// </summary>
    private bool _hovered;

    private bool _focused;

    public PosterCard()
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
    /// 需求 6：one of the card's hover buttons was pressed. Raised rather than acted on, because a card
    /// has no session to call and no page to notify — see <see cref="ItemCommands"/>, which both grids
    /// hand this straight to.
    /// </summary>
    public event EventHandler<CardActionEventArgs>? ActionRequested;

    public CardItem? Card
    {
        get => (CardItem?)GetValue(CardProperty);
        set => SetValue(CardProperty, value);
    }

    /// <summary>Card width in device-independent pixels. Must match the layout's MinItemWidth.</summary>
    public double CardWidth
    {
        get => (double)GetValue(CardWidthProperty);
        set => SetValue(CardWidthProperty, value);
    }

    /// <summary>Height of the artwork above the two text lines.</summary>
    public double PosterHeight
    {
        get => (double)GetValue(PosterHeightProperty);
        set => SetValue(PosterHeightProperty, value);
    }

    /// <inheritdoc cref="OnScrimProperty"/>
    public bool OnScrim
    {
        get => (bool)GetValue(OnScrimProperty);
        set => SetValue(OnScrimProperty, value);
    }

    /// <summary>
    /// 底下那两行字换一套墨。换的是样式而不是画刷：压在图上那两支是写死的浅墨，可主题那两支是
    /// <c>ThemeResource</c>，抄一份画刷引用过来就再也不跟着换主题了。两个键都 <c>BasedOn</c> 主题那一套，
    /// 只改前景 —— 所以字体和字号在两种底上是同一份。
    /// </summary>
    private static void OnScrimChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var card = (PosterCard)sender;
        var scrim = (bool)args.NewValue;

        card.TitleLine.Style = Ink(scrim ? "EgOnScrimBodyStyle" : "EgBodyStyle");
        card.SubLine.Style = Ink(scrim ? "EgOnScrimDataStyle" : "EgDataStyle");

        static Style Ink(string key) => (Style)Application.Current.Resources[key];
    }

    private static void OnCardChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var card = (PosterCard)sender;

        // The card being replaced is still in its page's list and would otherwise keep its bitmap.
        (args.OldValue as CardItem)?.ReleasePoster();

        // A live container being handed a different item: ItemsRepeater reuses containers without
        // detaching them, so Loaded will not fire again and this is the only notice we get.
        if (card._live) card.Begin();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _live = true;
        _focus.Attach();

        // 这张卡站在什么底上，问的是它所在的那条带（ShelfStrip.OnScrim）。上树这一刻问是唯一一定问得到的时机：
        // 带那边翻开关时这张卡可能还没建出来，而建出来的那一刻它自己还没套上模板。找不到带就是别处用的卡片
        // （媒体库那一格的网格），保持本来那一档。
        if (Up<ShelfStrip>(this) is { } strip) OnScrim = strip.OnScrim;

        Begin();
    }

    /// <summary>往上找最近的一个 <typeparamref name="T"/>：卡片是模板里的内容，带在它外面好几层。</summary>
    private static T? Up<T>(DependencyObject node) where T : DependencyObject
    {
        for (var current = VisualTreeHelper.GetParent(node); current is not null;
            current = VisualTreeHelper.GetParent(current))
        {
            if (current is T found) return found;
        }

        return null;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _live = false;
        Card?.ReleasePoster();

        // A recycled container must not come back with the buttons already up: the pointer that revealed
        // them is nowhere near wherever this container is reused.
        _watch.Leave();
        _hovered = false;
        _focus.Detach();
    }

    /// <summary>
    /// 胶片格那圈线：静止是发丝灰，指针在上面或者焦点落上来就整圈换强调色。
    /// <para>
    /// 换的是整个 Style 而不是 BorderBrush 一个属性 —— 「亮起来的框」是什么样，词表里那两个 Style 说了算，
    /// 这里只负责挑哪一个。哪天亮的时候还要加粗一档，改词表就够了，这一行不动。
    /// </para>
    /// <para>
    /// 两个 Style 都在应用级（Theme/Styles.xaml，App.xaml 里并进来的），所以 Application.Current.Resources
    /// 取得到；它们内部引用的 EgBorderBrush/EgAccentBrush 是 ThemeResource，套到元素上的时候才解析，跟着
    /// 主题走。
    /// </para>
    /// </summary>
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
    /// Hides the hover buttons, but only once the pointer has really left the card.
    /// <para>
    /// PointerExited bubbles, so stepping off one of the buttons raises it here too, and a click's
    /// pointer capture raises it without the pointer having moved at all. Either would flicker the
    /// buttons out from under the one being pressed. Where the pointer actually is settles it.
    /// </para>
    /// <para>
    /// And where the event <em>says</em> the pointer is cannot settle it on its own: a pointer that leaves
    /// the window from over the card reports the last position inside it, so this returns and no further
    /// exit ever comes. <see cref="HoverWatch"/> is the net under that — this is only the fast path.
    /// </para>
    /// </summary>
    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root).Position;

        if (point.X >= 0 && point.Y >= 0 && point.X < Root.ActualWidth && point.Y < Root.ActualHeight)
            return;

        _watch.Leave();
    }

    /// <summary>
    /// 自检：reveals the hover buttons for one layout pass and reports what they are and where they
    /// landed, then puts them back. Measured rather than asserted from the markup because
    /// 「播放按钮移动到媒体封面的中心」 and 「已播放、收藏、更多移动到封面的右下角」 are claims about pixels,
    /// and the alignments that produce them are two nested layouts away from the artwork they are
    /// measured against.
    /// </summary>
    internal CardHoverProbe ProbeHover()
    {
        var restore = Hover.Visibility;

        Hover.Visibility = Visibility.Visible;
        UpdateLayout();

        var middle = PlayButton
            .TransformToVisual(Art)
            .TransformPoint(new Windows.Foundation.Point(PlayButton.ActualWidth / 2, PlayButton.ActualHeight / 2));

        // The icon group's own bottom-right corner, in the artwork's coordinates. Measured from that
        // corner rather than from the group's origin so the number does not move when a library card
        // drops two of the three buttons and the group becomes a third as wide.
        var corner = Actions
            .TransformToVisual(Art)
            .TransformPoint(new Windows.Foundation.Point(Actions.ActualWidth, Actions.ActualHeight));

        var (rest, active) = ProbeFrame();

        var probe = new CardHoverProbe(
            Card?.Title ?? "",
            PlayButton.Visibility == Visibility.Visible,
            WatchedButton.Visibility == Visibility.Visible,
            FavoriteButton.Visibility == Visibility.Visible,
            MoreButton.Visibility == Visibility.Visible,
            PlayButton.ActualWidth,
            middle.X - Art.ActualWidth / 2,
            middle.Y - Art.ActualHeight / 2,
            Art.ActualWidth,
            Art.ActualHeight,
            Art.ActualWidth - corner.X,
            Art.ActualHeight - corner.Y,
            Paints(WatchedButton) || Paints(FavoriteButton) || Paints(MoreButton),
            _watch.Probe(),
            rest,
            active,
            _focus.Probe());

        Hover.Visibility = restore;
        UpdateLayout();

        return probe;
    }

    /// <summary>
    /// 自检：胶片格那圈线的两种颜色，静止的和亮起来的，都是照实读回来的。
    /// <para>
    /// 自检既没有指针也没有焦点，所以两个旗子是摆出来的 —— 而且是先摆再读，包括静止那一次：真有指针停在
    /// 这张卡上的时候（自检跑起来鼠标就在屏幕上某处），不摆的话读到的「静止」就是亮的。读完照原样放回去。
    /// </para>
    /// </summary>
    /// <remarks>
    /// 元组的第一个名字不叫 <c>Rest</c>：那是 <c>ValueTuple</c> 给第八个字段留的名字，编译器不让用。
    /// </remarks>
    private (Windows.UI.Color Still, Windows.UI.Color Active) ProbeFrame()
    {
        var (hovered, focused) = (_hovered, _focused);

        (_hovered, _focused) = (false, false);
        Paint();
        var rest = Colour(Art.BorderBrush);

        _hovered = true;
        Paint();
        var active = Colour(Art.BorderBrush);

        (_hovered, _focused) = (hovered, focused);
        Paint();

        return (rest, active);

        // 一支不是纯色的画刷（渐变、图片）在这里就是「没读到颜色」，而全透明的黑正好是自检要判红的东西。
        static Windows.UI.Color Colour(Brush? brush) =>
            brush is SolidColorBrush solid ? solid.Color : default;
    }

    /// <summary>
    /// 自检：whether one of the three corner buttons still fills itself in any of the states the pointer
    /// puts it through — 「仅保留白色的图标按钮」 with nothing behind the glyph.
    /// <para>
    /// The button's own <c>Background</c> answers only for the resting state; the other two are the
    /// template's, set from theme resources that a Style cannot reach and that this card overrides in the
    /// group's own <c>Resources</c> instead. So each state is switched on and the template root — which
    /// for Button *is* the ContentPresenter the states paint — is read back. Getting one of those key
    /// names wrong is silent, and its only symptom is the grey chip coming back under the pointer.
    /// </para>
    /// </summary>
    private static bool Paints(Button button)
    {
        if (button.BorderThickness is { Left: > 0 } or { Top: > 0 } or { Right: > 0 } or { Bottom: > 0 })
            return true;

        var painted = false;

        foreach (var state in new[] { "Normal", "PointerOver", "Pressed" })
        {
            VisualStateManager.GoToState(button, state, false);
            painted |= Filled(Root(button));
        }

        VisualStateManager.GoToState(button, "Normal", false);

        // The button's own brush as well, in case the template has no ContentPresenter to read.
        return painted || Filled(button.Background);

        static Brush? Root(Button button) => VisualTreeHelper.GetChildrenCount(button) > 0
            ? (VisualTreeHelper.GetChild(button, 0) as ContentPresenter)?.Background
            : null;

        // Transparent is a brush, and one that paints nothing; null is no brush at all.
        static bool Filled(Brush? brush) => brush is not null and not SolidColorBrush { Color.A: 0 };
    }

    private void OnPlayClicked(object sender, RoutedEventArgs e) => Raise(sender, CardAction.Play);

    private void OnWatchedClicked(object sender, RoutedEventArgs e) => Raise(sender, CardAction.Watched);

    private void OnFavoriteClicked(object sender, RoutedEventArgs e) => Raise(sender, CardAction.Favorite);

    private void OnMoreClicked(object sender, RoutedEventArgs e) => Raise(sender, CardAction.More);

    /// <summary>
    /// Nothing here has to mark the press handled: ButtonBase already claims the pointer events it acts
    /// on, so the ItemContainer in a library grid and the wrapping Button on the home page never see the
    /// press as an invoke and never start playing an item the user was only marking watched.
    /// </summary>
    private void Raise(object sender, CardAction action)
    {
        if (Card is null || sender is not FrameworkElement anchor) return;

        ActionRequested?.Invoke(this, new CardActionEventArgs(action, anchor));
    }
}

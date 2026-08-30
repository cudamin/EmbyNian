using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Services;
using EmbyNian.Shell.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;

namespace EmbyNian.Shell.Views;

/// <summary>What <see cref="HomePage"/> needs; see <see cref="LibraryRequest"/> for why it is passed.</summary>
internal sealed record HomeRequest(IServiceProvider Services, IReadOnlyList<EmbyItem> LibraryViews);

/// <summary>
/// The home page's view. All of it: what is left here is the three translations XAML cannot do for
/// itself — a navigation parameter into an <c>Attach</c>, a <c>Click</c> into a view-model call, and a
/// context request into the shared command handler. Everything that used to be here, the three
/// requests and the filling of the rows and the wording of the subheading, is in
/// <see cref="HomeViewModel"/>, where it can be read without a window on screen.
/// </summary>
public sealed partial class HomePage : Page, IShellContent
{
    private const string Category = "主页";

    private HomeRequest? _request;

    /// <summary>
    /// Resolved on navigation, for the card menus. The page needs it for nothing else — every request
    /// this page makes is the view model's.
    /// </summary>
    private EmbySession? _session;
    private IShellActions? _actions;

    /// <summary>
    /// 自检那一条「点卡片不挪页」的现场：按焦点之前这一页停在哪儿（-1 是还没按过）、焦点交出去了没有、这一页
    /// 被要求滚了几次，以及按不成的那一句话。分两拍读，见 <see cref="StayFocus"/>。
    /// </summary>
    private double _stayFrom = -1;
    private bool _stayTook;
    private int _stayAsked;
    private string? _stayNote;

    /// <summary>
    /// 数请求的那只手。存成字段是因为它要跨一拍：挂在 <see cref="StayFocus"/> 里，摘在
    /// <see cref="StayRead"/> 里，中间隔着一次 500 毫秒的等待 —— 两处得是同一个委托实例，不然摘不掉。
    /// </summary>
    private readonly TypedEventHandler<UIElement, BringIntoViewRequestedEventArgs> _stayProbe;

    public HomePage()
    {
        InitializeComponent();
        _stayProbe = (_, _) => _stayAsked++;
    }

    /// <summary>
    /// Created with the page rather than on navigation, so <c>x:Bind</c> never has a null root. The
    /// alternative — assigning it in <c>OnNavigatedTo</c> — leaves every binding on this page
    /// evaluating against nothing for the first frame, which is silent and looks like a load that
    /// never finished.
    /// </summary>
    internal HomeViewModel ViewModel { get; } = new();

    internal int LoadedCount => ViewModel.LoadedCount;

    internal bool IsReady => ViewModel.IsReady;

    /// <summary>自检：顶上那一块现在是一张剧照，还是页面自己的底色。见 <see cref="HomeViewModel.HeroFilled"/>。</summary>
    internal bool HeroFilled => ViewModel.HeroFilled;

    /// <summary>
    /// The rows the page actually drew, as 「继续观看 12、媒体库 3」. For the self-check, which otherwise
    /// has no way to tell an empty row from a row that failed to build: <see cref="LoadedCount"/> counts
    /// only what the server's own rows returned, so a 媒体库 row that had gone missing would not move it
    /// by one.
    /// </summary>
    internal string ShelfSummary => ViewModel.Shelves.Count == 0
        ? "无"
        : string.Join("、", ViewModel.Shelves.Select(shelf => $"{shelf.Title} {shelf.Cards.Count}"));

    /// <summary>
    /// 自检：需求 5 的那条带 —— 几张幻灯片，背后那些条目有哪几种图，以及那条带在这一页上真正的样子。
    /// <para>
    /// 两半合成一行，因为它们各答一个问题，少一半都不够：数据那半（<see cref="HomeViewModel.BannerSummary"/>）
    /// 说服务器给了什么，屏上那半（<see cref="HomeBanner.State"/>）说那些东西有没有真变成一条有高度、有图的
    /// 带。八张幻灯片配一条量不到宽度的空带，是这条带唯一一种不出声的坏法。
    /// </para>
    /// </summary>
    internal string BannerSummary => $"{ViewModel.BannerSummary}；屏上 {Banner.State}";

    /// <summary>自检：那条带上的字体和键高，见 <see cref="HomeBanner.TypeRead"/>。</summary>
    internal (bool Ok, string Detail) BannerType() => Banner.TypeRead();

    /// <summary>
    /// 自检：顶上那一整块到底铺没铺满 —— 「红框框出来的地方全填充上海报」。
    /// <para>
    /// 量的是那一块自己在窗口里的位置：上沿要落在 y=0（页面把 <c>ContentHost</c> 留给标题栏的那 32 像素顶
    /// 回去了，见 HomePage.xaml），右沿要落在窗口的右边沿。这两件事在报告里都不出声 —— 少顶那 32 就是图上
    /// 一道黑边、右边差一截就是一条白缝，而张数、剧照、字体那几行读数一个都不会变。
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) BleedRead()
    {
        var at = Hero.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
        var window = XamlRoot?.Size.Width ?? 0;
        var right = at.X + Hero.ActualWidth;
        var ok = Hero.ActualWidth > 0 && window > 0 && at.Y <= 0.5 && Math.Abs(right - window) <= 1.5;

        return (ok, $"起点 ({at.X:0},{at.Y:0})、{Hero.ActualWidth:0}×{Hero.ActualHeight:0}"
            + $"，右沿 {right:0} 对窗口宽 {window:0}");
    }

    /// <summary>
    /// 自检：the cards this page has actually realised, in tree order. Which row each came from is not
    /// reported and not needed — the check asks what a card of a given <em>type</em> offers, and the type
    /// is on the card.
    /// </summary>
    internal IReadOnlyList<PosterCard> RealisedCards()
    {
        var cards = new List<PosterCard>();
        Collect(this, cards);
        return cards;
    }

    private static void Collect(DependencyObject node, List<PosterCard> into)
    {
        if (node is PosterCard card)
        {
            into.Add(card);
            return;
        }

        var children = VisualTreeHelper.GetChildrenCount(node);
        for (var index = 0; index < children; index++)
            Collect(VisualTreeHelper.GetChild(node, index), into);
    }

    /// <summary>
    /// 自检：按一张卡片不会让整页先滑走 —— 「点击主页继续观看、媒体库、最近添加的封面之后会先跳转到页面下方，
    /// 然后才会进入页面」。这一半把焦点交给第一张卡并记下页面停在哪儿，隔一拍由 <see cref="StayRead"/> 量。
    /// 返回值是「要不要多给一拍」：主页上一张卡都没渲染出来的时候不用。
    /// <para>
    /// 分两拍而不是一拍读完，因为 <c>StartBringIntoView</c> 是异步的 —— 坏的那一版在同一拍里什么都量不出来：
    /// 请求还没兑现，位置一点没动，读起来和修好的一模一样。这一条正是这么漏过一次的。
    /// </para>
    /// <para>
    /// 不用指针也测得出来，这很关键：这台机器上注不进鼠标事件。<c>Focus(FocusState.Pointer)</c> 走的是同一条
    /// 路 —— 同一个 <c>GotFocus</c>、同一段 <see cref="ShelfStrip.RevealFor"/>，只是没有那次点击，所以既不会
    /// 打开条目也不会碰服务器。
    /// </para>
    /// </summary>
    internal bool StayFocus()
    {
        var cards = RealisedCards();
        if (cards.Count == 0) return false;

        if (Up<Button>(cards[0]) is not { } card)
        {
            _stayNote = "卡片外面没有可获得焦点的按钮";
            return true;
        }

        // 连别人标了已处理的请求也收：这一条问的是「有没有人要求这一页滚」，中间那几层怎么处理都瞒不过去。
        Scroller.AddHandler(BringIntoViewRequestedEvent, _stayProbe, handledEventsToo: true);

        _stayFrom = Scroller.VerticalOffset;
        _stayTook = card.Focus(FocusState.Pointer);
        return true;
    }

    /// <summary>
    /// 自检：上一拍那次焦点之后，这一页挪没挪。两样东西：这一页有没有<em>被要求</em>滚，以及它自己现在停在
    /// 哪儿 —— 坏的那一版滑的是一大段带动画的距离，隔了一拍早就走完了。见 <see cref="StayFocus"/>。
    /// </summary>
    internal (bool Ok, string Detail)? StayRead()
    {
        if (_stayNote is { } note) return (false, note);
        if (_stayFrom < 0) return null;

        Scroller.RemoveHandler(BringIntoViewRequestedEvent, _stayProbe);

        var after = Scroller.VerticalOffset;
        var still = Math.Abs(after - _stayFrom) < 0.5;

        return (_stayTook && _stayAsked == 0 && still,
            $"指针焦点{(_stayTook ? "已交给" : "交不给")}第一张卡"
                + (_stayAsked == 0 ? "，隔一拍也没人要求这一页滚" : $"，这一页被要求滚了 {_stayAsked} 次")
                + $"，滚动位置 {_stayFrom:0}→{after:0}"
                + (still ? "" : "（整页自己滑走了）"));
    }

    /// <summary>往上找最近的一个 <typeparamref name="T"/>：卡片是模板里的内容，能拿焦点的按钮和带都在它外面。</summary>
    private static T? Up<T>(DependencyObject node) where T : DependencyObject
    {
        for (var current = VisualTreeHelper.GetParent(node); current is not null;
            current = VisualTreeHelper.GetParent(current))
        {
            if (current is T found) return found;
        }

        return null;
    }

    /// <inheritdoc />
    public object? NavigationRequest => _request;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not HomeRequest request)
        {
            Log.Warn(Category, "导航参数缺失");
            return;
        }

        _request = request;
        Tag = "home";

        // The one place this page resolves anything. It is deliberately a single block: it is the line
        // that goes when the shell stops handing a container around, and spreading it would turn one
        // deletion into a hunt.
        var services = request.Services;
        _session = services.GetRequiredService<EmbySession>();
        _actions = services.GetRequiredService<IShellActions>();

        ViewModel.Attach(
            _actions,
            request.LibraryViews,
            services.GetRequiredService<ISettingsService>(),
            _session,
            services.GetRequiredService<EmbyImageStore>());

        // 标题栏那几颗按钮要知道自己站在哪种底上。现在说一遍（回到这一页时那些幻灯片可能已经在手上了），
        // 之后每次那一块从「一张图」变成「页面的底色」或者反过来时再说一遍。
        ViewModel.PropertyChanged += OnViewModelChanged;
        _actions.SetTitleStrip(Strip());

        _ = ViewModel.ReloadAsync();
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(HomeViewModel.HeroFilled))
            _actions?.SetTitleStrip(Strip());
    }

    /// <summary>这一页顶上那一块：那条大图铺到窗口顶边就是一张剧照，没有幻灯片时是页面自己的底色。</summary>
    private TitleStrip Strip() => ViewModel.HeroFilled ? TitleStrip.OnScrim : TitleStrip.Plain;

    public void Release() => ViewModel.Cancel();

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        // 走出这一页，顶上那一块就是别的页面自己的底色了 —— 那几颗按钮得把墨还回来。
        ViewModel.PropertyChanged -= OnViewModelChanged;
        _actions?.SetTitleStrip(TitleStrip.Plain);

        Release();
        base.OnNavigatedFrom(e);
    }

    private void OnCardClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: PosterCard { Card: { } card } }) ViewModel.Open(card);
    }

    /// <summary>
    /// 需求 6：右键一张卡片。No sibling list: a home-page row is a handful of unrelated next-up episodes
    /// from different series, and offering them to each other as 选集 would be wrong. A metadata save
    /// reloads every row, because the item that changed may be in more than one of them.
    /// </summary>
    private void OnCardContextRequested(UIElement sender, ContextRequestedEventArgs args) =>
        ItemCommands.Handle(_session, _actions, sender, args, changed: Reload);

    /// <summary>需求 6：the same commands from the strip that appears on a card under the pointer.</summary>
    private void OnCardActionRequested(object? sender, CardActionEventArgs args) =>
        ItemCommands.Handle(_session, _actions, sender, args, changed: Reload);

    /// <summary>需求 5：播放 on the carousel. Fire and forget, same as every other play on this page.</summary>
    private void OnBannerPlayRequested(object? sender, BannerSlide slide) => _ = ViewModel.PlayAsync(slide);

    private void OnBannerOpenRequested(object? sender, BannerSlide slide) => ViewModel.Open(slide);

    private void Reload() => _ = ViewModel.ReloadAsync();
}

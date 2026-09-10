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
internal sealed record HomeRequest(
    IServiceProvider Services,
    IReadOnlyList<EmbyItem> LibraryViews,
    Windowing.HostWindow? Window);

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
    private Windowing.HostWindow? _window;

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

    /// <summary>
    /// The rows the page actually drew, as 「继续观看 12、媒体库 3」. For the self-check, which otherwise
    /// has no way to tell an empty row from a row that failed to build: <see cref="LoadedCount"/> counts
    /// only what the server's own rows returned, so a 媒体库 row that had gone missing would not move it
    /// by one.
    /// </summary>
    internal string ShelfSummary
    {
        get
        {
            var rows = ViewModel.Shelves.Select(shelf => $"{shelf.Title} {shelf.Cards.Count}").ToList();

            return rows.Count == 0 ? "无" : string.Join("、", rows);
        }
    }

    /// <summary>
    /// 自检：需求 5 的那条带 —— 几张幻灯片，背后那些条目有哪几种图，那条带在这一页上真正的样子。
    /// <para>
    /// 两半合成一行，因为它们各答一个问题，少一半都不够：数据那半（<see cref="HomeViewModel.BannerSummary"/>）
    /// 说服务器给了什么，屏上那半（<see cref="HomeBanner.State"/>）说那些东西有没有真变成一条有高度、有图的
    /// 带。八张幻灯片配一条量不到宽度的空带，是这条带唯一一种不出声的坏法。
    /// </para>
    /// </summary>
    internal string BannerSummary => $"{ViewModel.BannerSummary}；屏上 {Banner.State}";

    /// <summary>自检：那条带上的字体和键高，见 <see cref="HomeBanner.TypeRead"/>。</summary>
    internal (bool Ok, string Detail) BannerType() => Banner.TypeRead();

    /// <summary>自检：剧照整张画出来了没有，见 <see cref="HomeBanner.PictureRead"/>。</summary>
    internal (bool Ok, string Detail) BannerPicture() => Banner.PictureRead();

    /// <summary>自检：屏上那几排真按设置里那份版面来的，见 <see cref="HomeViewModel.LayoutRead"/>。</summary>
    internal (bool Ok, string Detail) LayoutRead() => ViewModel.LayoutRead();

    /// <summary>自检：页眉的墨色（见 <see cref="SlateRead"/>）；轮播 2026-09-10 框成卡片后页眉不再压在图上。</summary>
    internal string SlateRead() =>
        $"页眉{(Slate.OnScrim ? "在用图上那套墨（不该 —— 轮播已是卡片，页眉站在纸上）" : "跟主题的墨走")}";

    /// <summary>
    /// 自检：轮播那一块真被框起来了 —— 和窗口四边都留出了间隔（「弄个框把轮播图框起来」的真正意思：不占满
    /// 上半页、四边留空，徽标片名简介都在框里）。
    /// <para>
    /// 量 Banner 自己在窗口里的位置：左、右、上三边都该让出 PageInset（24）—— 左右对窗口两边沿，上对着
    /// 外壳那一行（32，标题栏）底下那一口气（12）加页眉和 16 的间距，所以只要「让出 ≥ PageInset − 4」就算
    /// 对，不钉死页眉自己的高度。收起来（没有幻灯片）时量的是收起之后的边距，那一档同样成立。
    /// </para>
    /// <para>
    /// 2026-09-10 之前判的正好相反：「红框框出来的地方全填充上海报」，上沿落在 0、右沿吃满窗口宽。那一版的
    /// SyncBleed（把 ContentHost 留给标题栏的 32 像素顶回去）随本改删除。
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) BleedRead()
    {
        var at = Banner.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
        var window = XamlRoot?.Size.Width ?? 0;
        var right = window - (at.X + Banner.ActualWidth);
        var slack = PageInset - 4;
        var ok = Banner.ActualWidth > 0
            && window > 0
            && at.X >= slack
            && right >= slack
            && at.Y >= ChromeHeight + 8;

        return (ok,
            $"起点 ({at.X:0},{at.Y:0})、{Banner.ActualWidth:0}×{Banner.ActualHeight:0}"
                + $"，左让 {at.X:0}、右让 {right:0}、上让 {at.Y:0}（标题栏 {ChromeHeight:0} 之下）"
                + (ok ? " —— 框起来了，四边留了间隔" : " —— 有哪条边还贴着窗口"));
    }

    /// <summary>轮播卡片四边的留白，和页面其余内容同一条边距线（XL，24）。BleedRead 拿它当基准。</summary>
    internal const double PageInset = 24;

    /// <summary>
    /// 自检：轮播卡片的高度就是 <see cref="HomeCarousel.Height"/> 按它自己的宽算出来的那个数，而横着的那几排
    /// 接在卡片下沿之后。
    /// <para>
    /// 带宽从「页宽」变成「页宽 − 48」（四边留 24 的卡片，2026-09-10），所以带高也矮一截；其余照旧：剧照贴右
    /// 沿放大一成画、上下各裁 5%，第一排接在卡片下沿之后而不是压在它上面。
    /// </para>
    /// <para>
    /// 大图和横排都没有时没得量，跳过而不是判红：一个从未播放过任何内容、又只勾了一排的账号就是那样，那是正常
    /// 数据。
    /// </para>
    /// </summary>
    internal (bool? Ok, string Detail) FoldRead()
    {
        if (Banner.Visibility != Visibility.Visible && ViewModel.Shelves.Count == 0)
            return (null, "这次屏上既没有大图也没有横排，跳过首屏边界读数");

        if (XamlRoot is not { } root || root.Size.Height <= 0)
            return (false, "量不到窗口视口高度");

        static double Top(FrameworkElement element) =>
            element.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;

        var viewport = root.Size.Height;

        // 卡片那一块的高度就是那条规则算出来的：带宽是它自己量到的宽（比页宽窄 48），一张幻灯片都没有时整条带
        // 收起，那一档不判。
        var heroBottom = Top(Banner) + Banner.ActualHeight;
        var wanted = HomeCarousel.Height(viewport, Banner.ActualWidth);
        var shaped = Banner.Visibility != Visibility.Visible
            || (Math.Abs(Banner.ActualHeight - wanted) <= 1.5 && heroBottom <= viewport + 0.5);

        // 横着的第一排接在大图下沿之后 —— 绝不该压在大图上：那正是 2026-09-05 之前那一叠玻璃的做法，删掉之后
        // 这一句是它不会悄悄回来的唯一保证。带子弄扁之后第一排一般露在第一屏里；窗口特别矮的时候才会整个在
        // 第一屏外。
        bool nextOk;
        string nextNote;

        if (ViewModel.Shelves.Count == 0)
        {
            nextOk = true;
            nextNote = "没有横着排的货架";
        }
        else
        {
            var first = ShelfRepeater.TryGetElement(0) ?? ShelfRepeater.GetOrCreateElement(0);
            UpdateLayout();
            UpdateLayout();

            if (first is not FrameworkElement front)
            {
                nextOk = false;
                nextNote = "第一排货架没有生成可测量的根元素";
            }
            else
            {
                var frontTop = Top(front);
                nextOk = frontTop >= heroBottom - 0.5;
                nextNote = $"{ViewModel.Shelves[0].Title}从 {frontTop:0} 起"
                    + (nextOk ? "，接在大图下沿之后" : "，压在大图上")
                    + (frontTop < viewport ? $"（露进第一屏 {viewport - frontTop:0}）" : "（在第一屏外，滚一下才露出来）");
            }
        }

        return (shaped && nextOk,
            $"视口 0–{viewport:0}；大图 {Banner.ActualWidth:0}×{Banner.ActualHeight:0}"
                + (shaped ? $"，正是这个宽度该有的高 {wanted:0}（剧照贴右沿整张画、上下不留底色）" : $"，该高 {wanted:0}")
                + $"，这一块下沿 {heroBottom:0}；{nextNote}");
    }

    /// <summary>
    /// <c>ShellPage.xaml</c> 里 <c>ContentHost</c> 给外壳留的那一段（<c>Padding="0,32,0,0"</c>）：标题栏那一行
    /// 的高。轮播 2026-09-10 框成卡片之后这一页不再顶回那 32 像素（那一版的 <c>SyncBleed</c> 已删），但这个数
    /// 还有两份用场：BleedRead 拿它判「卡片站在标题栏底下而不是塞进标题栏里」，自检那一关
    /// （<c>ShellPage.ProbeChrome</c>）拿它和屏上量出来的外壳高度对一遍 —— 对账要的正是「两边引的是同一个数」。
    /// <para>
    /// 2026-09-09 起外壳只有标题栏这一行：第 1 行（标签栏删掉后剩下的那条空带）删掉了，这个数从 80 收到 32。
    /// </para>
    /// </summary>
    internal const double ChromeHeight = 32;

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

    /// <summary>
    /// 工具：把第一张卡的「更多」菜单弹开（<c>--show-menu</c>），返回那张卡的片名；一张卡都没渲染出来时是 null。
    /// <para>
    /// 挑的是有续播位置的那一张 —— 那一档菜单最长（「从继续观看中移除」只在它上面出现），也正是要拍的那一张。
    /// </para>
    /// </summary>
    internal string? ShowFirstCardMenu()
    {
        if (_session is null || _actions is null) return null;

        var cards = RealisedCards();
        var target = cards.FirstOrDefault(poster => poster.Card?.Item.HasResumePosition == true)
            ?? cards.FirstOrDefault(poster => poster.Card?.Item.IsPlayable == true)
            ?? cards.FirstOrDefault();

        if (target?.Card is not { } card) return null;

        ItemCommands.Show(_session, _actions, target, card, position: null, changed: Reload);
        return card.Title;
    }

    /// <summary>
    /// 自检：「更多」点开的那张菜单真的搭出来了 —— 行数、每行的字、每行带的那条命令，都和 Core 排的那一份
    /// （<see cref="ItemMenu.For"/>）对得上，而且每一行都有图标、按得动。
    /// <para>
    /// 这一条是给「代码搭出来的界面」留的口子：菜单不在 XAML 里，搭空了、少接一行、某一行忘了接命令，屏上都
    /// 只是「菜单短了一条」，没人会去数。这个项目已经这么漏过一次（详情页那一行类型的 <c>Hyperlink</c>）。
    /// </para>
    /// <para>
    /// 只搭不弹（<see cref="ItemCommands.Build"/>）：真弹一张浮层出来会挡住自检接着要走的那几步。
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail)? MenuRead()
    {
        if (_session is null || _actions is null) return null;

        var cards = RealisedCards();
        var card = (cards.FirstOrDefault(poster => poster.Card?.Item.IsPlayable == true) ?? cards.FirstOrDefault())?.Card;
        if (card is null) return null;

        var plan = ItemMenu.For(card.Item);
        var menu = ItemCommands.Build(_session, _actions, this, card);
        var labels = new List<string>(menu.Items.Count);
        var ok = menu.Items.Count == plan.Count;

        for (var index = 0; index < Math.Min(menu.Items.Count, plan.Count); index++)
        {
            var wanted = plan[index];

            switch (menu.Items[index])
            {
                case MenuFlyoutSeparator:
                    ok &= wanted.IsRule;
                    labels.Add("──");
                    break;

                case MenuFlyoutItem entry:
                    ok &= !wanted.IsRule
                        && entry.Text == wanted.Label
                        && entry.Tag is ItemCommand command && command == wanted.Command
                        && entry.Icon is FontIcon { Glyph.Length: > 0 }
                        && entry.IsEnabled;
                    labels.Add(entry.Text);
                    break;

                default:
                    ok = false;
                    labels.Add("？");
                    break;
            }
        }

        return (ok, $"「{card.Title}」（{card.Item.DisplayTypeName}）搭了 {menu.Items.Count} 行"
            + $"，规矩说 {plan.Count} 行：{string.Join(" ｜ ", labels)}");
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
    /// 路 —— 同一个 <c>GotFocus</c>、同一段 <see cref="EmbyNian.Infrastructure.CardStrip.RevealFor"/>，只是
    /// 没有那次点击，所以既不会打开条目也不会碰服务器。
    /// </para>
    /// </summary>
    internal bool StayFocus()
    {
        // 挑一张横带里的卡：这一条问的是「横带会不会替卡片要一次 BringIntoView，把整页拽下去」。带子弄扁之后
        // 第一排一般露在第一屏里，可 ItemsRepeater 没排过的那一帧还是可能一张都没有，那时退回第一张。
        var cards = RealisedCards();
        var target = cards.FirstOrDefault(poster => Up<ShelfStrip>(poster) is not null) ?? cards.FirstOrDefault();

        if (target is null) return false;

        if (Up<Button>(target) is not { } card)
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
        _window = request.Window;
        Tag = "home";

        // The one place this page resolves anything. It is deliberately a single block: it is the line
        // that goes when the shell stops handing a container around, and spreading it would turn one
        // deletion into a hunt.
        var services = request.Services;
        _session = services.GetRequiredService<EmbySession>();
        _actions = services.GetRequiredService<IShellActions>();
        var settings = services.GetRequiredService<ISettingsService>();

        ViewModel.Attach(
            _actions,
            request.LibraryViews,
            settings,
            _session,
            services.GetRequiredService<EmbyImageStore>());

        // 版面（拖拽出来的次序、勾掉的那几排）改完当场生效：设置页在另一个窗口里，它改完喊一声，这一页重排。
        ShellPrefs.Changed -= OnShellPrefsChanged;
        ShellPrefs.Changed += OnShellPrefsChanged;

        _ = ViewModel.ReloadAsync();
    }

    public void Release()
    {
        ShellPrefs.Changed -= OnShellPrefsChanged;

        ViewModel.Cancel();
    }

    /// <summary>
    /// 设置里那份主页版面改了（拖拽排序或者勾选），照新的重排一遍。卡片尺寸那几个数不在这一句里 —— 它们是
    /// <see cref="HomeViewModel.Attach"/> 时的快照，下次开这一页才换（见那一段说明）。
    /// </summary>
    private void OnShellPrefsChanged(Configuration.UiSettings ui) => _ = ViewModel.ApplyLayoutAsync();

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
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

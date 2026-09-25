using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.MoviePilot;
using EmbyNian.Services;
using EmbyNian.Shell.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
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

    /// <summary>矮窗档的重查已经排了一拍还没跑。防止 <see cref="ViewModel"/> 那一头一连串增删把重查排成一把。</summary>
    private bool _foldScheduled;

    /// <summary>
    /// 上一次真的翻了档的时刻（毫秒，单调钟）。两次翻档之间要隔够一趟行程才许再翻一次 —— 见 <see cref="Fold"/>。
    /// </summary>
    private long _foldPlacedAt;

    /// <summary>
    /// 矮窗档那一段行程（见 <see cref="HomeFoldMotion"/>）：正在走的那一趟，没有就是 null。它同时是「这会儿屏上
    /// 正有东西在走」的记号 —— 在走的这一段里不要再摆档，见 <see cref="ApplyLibraryChrome"/>。
    /// </summary>
    private HomeFoldMotion? _fold;

    /// <summary>
    /// 补位／让位那半趟（迟一拍走的，见 <see cref="ApplyLibraryChrome"/>）。单独记一笔是因为它比宿主那一趟
    /// 晚起步：翻档翻得快的时候，宿主那一趟收了它还得活着。
    /// </summary>
    private HomeFoldMotion? _foldLate;

    /// <summary>上一次摆到屏上的是哪一档（true ＝ 媒体库那一排压在轮播上）。</summary>
    private bool _foldOn;

    /// <summary>
    /// 上一次那一档是不是**真在屏上摆过**的。页面刚起来那几拍轮播还没有幻灯片、视口还是零，那时摆下的是个假档
    /// （数据回来时还要再翻一次）—— 拿假档当动画的起点会凭空飞一趟，而自检正是在那几拍上读坐标的
    /// （「主页首屏」那一行量的是继续观看从哪儿起、大图下沿在哪儿），飞在半路的位移会把读数带偏。
    /// </summary>
    private bool _foldReal;

    /// <summary>翻档前量下的旧落点，交给紧接着那一次 <see cref="ApplyLibraryChrome"/> 消费，用完就清。</summary>
    private FoldSites? _before;

    /// <summary>
    /// 翻档前的现场，全在**搬之前**量 —— <see cref="HomeViewModel.SetLibraryOverlay"/> 一喊就把那一排搬进搬出，
    /// 搬完再量到的是新落点。三样东西各是动画里的一段位移的起点：
    /// <list type="bullet">
    /// <item><paramref name="Rows"/>：横排里每一排此刻的顶。**按排本身（<see cref="CardShelf"/>）记，不按元素、
    /// 不按序号** —— 搬走一排之后序号全错位，Repeater 还会把手里的元素换给别的排用（不认唯一 id 时它按序号重绑），
    /// 两样都对不上号；要的只是「这一排原来站在哪儿」，按排记才是那个答案。</item>
    /// <item><paramref name="Strip"/>：媒体库那一排**排卡**的上沿（压上那一趟的起飞点 ＝ 回默认那一趟的落点；
    /// 两个方向它都是「那排卡此刻/将来站在哪条线上」，见 <see cref="MeasureBeforeFold"/>）。</item>
    /// <item><paramref name="Info"/>：轮播里那块字的顶（它要让位给压上来的那一排）。</item>
    /// </list>
    /// </summary>
    private sealed record FoldSites(
        IReadOnlyDictionary<CardShelf, double> Rows,
        double? Strip,
        double? Info);

    public HomePage()
    {
        InitializeComponent();
        _stayProbe = (_, _) => _stayAsked++;

        // 这一页的两件事都挂在轮播上：剧照一上来就把外壳那 32 像素顶回去（SyncBleed），以及标题栏那一行的墨
        // 跟着带面走（PaintInk）。两者都只在带子可见时有意义 —— 没有幻灯片时 Apply 会把 Visibility 收起来，
        // 两条都在那之后重新对一遍。
        Banner.Loaded += (_, _) => SyncBleed();
        Banner.SizeChanged += (_, _) => SyncBleed();
        Banner.SlideChanged += (_, _) =>
        {
            SyncBleed();
            PaintInk();
        };

        // 矮窗档（媒体库压上轮播左下角）跟着窗口的高矮走：窗口、带子、货架尺寸变了都要重新量一遍。
        // 进度条和提示条一收一放也挪动货架的顶，一并重查。货架上下一拍才排得完（重载是清空再装回），
        // 所以集合一变先排一拍再查，不在事件里当场动。
        SizeChanged += (_, _) => UpdateLibraryOverlay();
        Banner.SizeChanged += (_, _) => UpdateLibraryOverlay();
        Banner.SlideChanged += (_, _) => UpdateLibraryOverlay();
        // 但**翻档自己搬那一排也会响这条事件**（HomeViewModel.ApplyLibraryOverlay）：那不是「货架集合变了」，
        // 据它再排一次重查就是自己喂自己 —— 翻档 → 重查 → 再翻档，滚起来就是 2026-09-18 两场、09-21 一场
        // 「矮窗档来回翻」把界面线程吃干的样子。搬动那一下挂的记号见 HomeViewModel.ShelvesChangeIsOverlay。
        ViewModel.Shelves.CollectionChanged += (_, _) =>
        {
            if (!ViewModel.ShelvesChangeIsOverlay) ScheduleFold();
        };
        // 「正在下载」一排来去（第一个任务出现、最后一个任务消失）也挪动货架的顶，矮窗档跟着重查一遍。
        // 行内进度跳动不响这条事件 —— 那不挪任何一排的位置。
        ViewModel.DownloadRows.CollectionChanged += (_, _) => ScheduleFold();
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ViewModel.BusyVisibility) or nameof(ViewModel.NoticeVisibility))
                ScheduleFold();
        };

        // 首页进场动画（「给轮播图和首页增加更多动画特效」，2026-09-11）：每一排货架逐排淡入上浮，卡片抬起。
        // 挂在 Loaded 上是因为 XAML 里写不了 —— 见 HomeShelfMotion 那一段。
        Loaded += (_, _) =>
        {
            HomeMotion.Enter(this, ShelfRepeater, ViewModel.Shelves.Count);
            UpdateLibraryOverlay();
        };
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

            // 矮窗档压上轮播的那一排不在 Shelves 里 —— 摘要在它原来的位置补一句，读的人不用猜少的那排去了哪儿。
            if (ViewModel.LibraryOnBanner && ViewModel.LibraryShelf is { } library)
                rows.Insert(Math.Min(ViewModel.LibraryFlowIndex < 0 ? rows.Count : ViewModel.LibraryFlowIndex, rows.Count),
                    $"{library.Title} {library.Cards.Count}（压在轮播上）");

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

    /// <summary>自检：剧照铺满整条带了没有（比例不变、只有一条轴被裁），见 <see cref="HomeBanner.PictureRead"/>。</summary>
    internal (bool Ok, string Detail) BannerPicture() => Banner.PictureRead();

    /// <summary>自检：屏上那几排真按设置里那份版面来的，见 <see cref="HomeViewModel.LayoutRead"/>。</summary>
    internal (bool Ok, string Detail) LayoutRead() => ViewModel.LayoutRead();

    /// <summary>
    /// 自检：屏上第一块点得动的牌子，门真的开着、这一排真的挂在它身上。这是 2026-09-14「继续观看点了没反应」
    /// 那次排查里没人看的一格 —— 主页版面量数据（该带 › 的排都带）、货架牌子进库量独立控件（Offer 一开门就
    /// 开），而「真树上那一块牌子」两头都没量：模板当时把这一排挂在 DataContext 上，而 ItemsRepeater + x:Bind
    /// 的模板实例上 DataContext 是空的 —— 数据全对、控件全对，点击无声。
    /// <para>
    /// 量三样：大于号露出、点击面铺开（<c>Offer</c> 的两个落点），和 <c>ReferenceEquals(head.Tag, shelf)</c>
    /// —— 最后这条就是修复本身（ShelfTemplate 的 <c>Tag="{x:Bind}"</c>）。DataContext 的读数只报不判：它在这
    /// 套模板里就是空的，报出来是让谁哪天要是把它设上了，读数里看得见。
    /// </para>
    /// </summary>
    /// <returns>屏上一块能进的排都没有时是 <see langword="null"/>（没数据的账号，跳过不判）。</returns>
    internal (bool Ok, string Detail)? HeadOnScreen()
    {
        CardShelf? shelf = null;
        var index = -1;

        for (var i = 0; i < ViewModel.Shelves.Count; i++)
        {
            if (ViewModel.Shelves[i].CanOpen)
            {
                index = i;
                shelf = ViewModel.Shelves[i];
                break;
            }
        }

        if (shelf is null) return null;

        var direct = ShelfRepeater.TryGetElement(index) as FrameworkElement;
        if (direct is null)
        {
            direct = ShelfRepeater.GetOrCreateElement(index) as FrameworkElement;
            UpdateLayout();
        }

        // 2026-09-16 起模板根外面包了一层 Grid（见 ShelfTemplate 与 HomeMotion.TargetOf）：Repeater 的
        // 直接子是框架 arrange 要操纵的对象，进场动画的目标和这里的量法都落在直接子里面的模板根上。
        var panel = direct is null
            ? null
            : VisualTreeHelper.GetChildrenCount(direct) > 0
                ? VisualTreeHelper.GetChild(direct, 0) as StackPanel
                : direct as StackPanel;

        if (panel is null) return (false, $"第 {index} 排（{shelf.Title}）没有可量的货架模板");

        if (panel.Children.OfType<ShelfHead>().FirstOrDefault() is not { } head)
            return (false, $"「{shelf.Title}」的货架上没有牌子");

        var (glyphState, scopeState) = head.DoorState;
        var glyph = glyphState == Visibility.Visible;
        var scope = scopeState == Visibility.Visible;
        var tagOk = ReferenceEquals(head.Tag, shelf);

        return (glyph && scope && tagOk,
            $"「{shelf.Title}」：大于号{(glyph ? "露着" : "收着")}、点击面{(scope ? "铺开" : "收着")}、"
                + (tagOk ? "这一排挂在牌子上" : $"这一排没挂上（Tag 是 {head.Tag?.GetType().FullName ?? "空"}）")
                + $"；DataContext 是 {head.DataContext?.GetType().FullName ?? "空"}");
    }

    /// <summary>
    /// 自检：轮播真铺满了窗口的上半部分 —— 左沿落在窗口左边（0）、右沿吃满窗口宽、上沿顶回到窗口顶边（也就是
    /// 把外壳留给标题栏的那 32 像素也吃掉，标题栏浮在剧照上）。「占满窗口的上半部分（包括窗口标题）」，2026-09-11。
    /// <para>
    /// 量 Banner 自己在窗口里的位置：左、上两条都该贴到 0，右沿也该到 0。下沿与内容之间的界线不在这里量 ——
    /// 那是画面上的渐变过渡，由 HomeBanner 的读数负责。
    /// </para>
    /// <para>
    /// 2026-09-10 到 09-11 之间判的正好相反：「弄个框把轮播图框起来」被落定成四边各留 24 的卡片，那一版要求
    /// 左、右、上三条都让出 PageInset。谁把 Banner 的负边距删掉（<c>Margin="0,-32,0,0"</c>）、或者给这一页
    /// 补回左右留白，这一条当场红。
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) BleedRead()
    {
        var at = Banner.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
        var window = XamlRoot?.Size.Width ?? 0;
        var right = window - (at.X + Banner.ActualWidth);
        var viewportTop = Scroller.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point()).Y;
        var ok = Banner.ActualWidth > 0
            && window > 0
            && at.X <= 0.5
            && right <= 0.5
            && at.Y <= 0.5
            && viewportTop <= 0.5;

        return (ok,
            $"起点 ({at.X:0},{at.Y:0})、{Banner.ActualWidth:0}×{Banner.ActualHeight:0}"
                + $"，左让 {at.X:0}、右让 {right:0}、上让 {at.Y:0}，滚动视口从 {viewportTop:0} 起（含标题栏 {ChromeHeight:0}）"
                + (ok ? " —— 通栏铺满，标题栏浮在图上" : " —— 有哪条边没顶到窗口的边"));
    }

    /// <summary>
    /// 把外壳留给标题栏的那 32 像素顶回来，让剧照铺到窗口的顶边去。这一页是整套里唯一这么干的一页（别的页
    /// 都把那 32 留白让给标题栏），所以它是这一页自己的事，不是外壳的开关。
    /// <para>
    /// 负边距必须落在 Scroller 本身：只把 Banner 往上移，布局坐标虽然到了 0，图片仍被滚动视口在 32 处裁掉。
    /// 连视口一起上移后，图像才真正铺进标题栏。没有幻灯片时归零，普通货架继续让开标题栏。
    /// </para>
    /// <para>
    /// 2026-09-10 到 09-11 之间这个方法不存在：那一版「框成卡片」之后大图不再贴窗口的边，没人要顶那 32。
    /// 「占满窗口的上半部分（包括窗口标题）」之后它回来了。
    /// </para>
    /// </summary>
    internal void SyncBleed()
    {
        var sheet = Scroller.Margin;
        var wanted = Banner.Visibility == Visibility.Visible ? -ChromeHeight : 0;

        if (Math.Abs(sheet.Top - wanted) > 0.5)
            Scroller.Margin = new Thickness(sheet.Left, wanted, sheet.Right, sheet.Bottom);
    }

    /// <summary>单调钟（毫秒）。这一页只拿它量「距上一趟摆档过了多久」，见 <see cref="Fold"/>。</summary>
    private static long Now => Environment.TickCount64;

    /// <summary>
    /// 矮窗档一趟行程要走多久：宿主那一趟（<see cref="HomeFoldMotion.DurationMilliseconds"/>）＋迟一拍起步的
    /// 补位那一趟。两次翻档之间至少隔这么久 —— 落定之前量到的都是过渡态几何，见 <see cref="Fold"/>。
    /// </summary>
    private static long FoldSettleMilliseconds => HomeFoldMotion.DurationMilliseconds * 2L;

    /// <summary>被「等上一趟落地」拦下的那一次判定，到点补跑一次（别把用户停手那一拍的决定丢掉）。</summary>
    private DispatcherQueueTimer? _foldRetry;

    /// <summary>
    /// 换上矮窗档的另一档：翻之前先把旧落点量下来，量完再搬 —— <see cref="HomeViewModel.SetLibraryOverlay"/> 当场
    /// 就把那一排搬进搬出，搬完再量就是新落点了。同一档上重复调用什么都不做（拉窗口时每一下 SizeChanged 都走到这儿，
    /// <see cref="HomeViewModel.SetLibraryOverlay"/> 自己也按这个早退）。
    /// <para>
    /// <b>两次翻档之间要等上一趟落地</b>（<see cref="FoldSettleMilliseconds"/>）。这一条 2026-09-21 才补上，补的是
    /// 「拿飞在半路的几何判档」：一趟的位移写在 <c>TranslateTransform.Y</c> 上，而量几何的
    /// <c>TransformToVisual</c> 吃这一支变换 —— 上一趟还在飞，下一拍量到的既不是旧档的几何也不是新档的几何，
    /// 判出来的档自然也不作数，于是翻一次、量歪一次、再翻一次。日志里那一万像素的行程
    /// （<c>矮窗档：媒体库那一排落回横排（行程 10011 像素…）</c>，排卡线被读成 −9541）就是这么来的。
    /// </para>
    /// <para>
    /// 这一条同时是这类暴走的速度上限。一天里三场「矮窗档来回翻」（09-18 两场共两万六千行、09-21 一场三万行／27 秒）
    /// 都是每 6 毫秒翻一次，把界面线程吃干 —— 播放页的遮罩揭不掉、键鼠没有一拍排得上队，用户看到的就是
    /// 「卡在背景图加载界面、退不出去，声音还在后台放」。隔开之后，最坏也只是半秒翻一次。
    /// </para>
    /// </summary>
    private void Fold(bool decided)
    {
        if (ViewModel.LibraryOnBanner == decided) return;

        var since = Now - _foldPlacedAt;
        if (since < FoldSettleMilliseconds)
        {
            // 拦下的这一次不能就这么丢了：用户拉着窗口停在半路，档位就会停在旧的那一档上。约在落地那一刻补一次。
            if (DispatcherQueue is { } queue && (_foldRetry ??= CreateFoldRetry(queue)) is { } retry)
            {
                retry.Stop();
                retry.Interval = TimeSpan.FromMilliseconds(FoldSettleMilliseconds - since);
                retry.Start();
            }

            return;
        }

        _foldPlacedAt = Now;
        MeasureBeforeFold();
        ViewModel.SetLibraryOverlay(decided);
    }

    private DispatcherQueueTimer CreateFoldRetry(DispatcherQueue queue)
    {
        var timer = queue.CreateTimer();
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            UpdateLibraryOverlay();
        };
        return timer;
    }

    /// <summary>
    /// 把翻档前那一刻的现场量下来（见 <see cref="FoldSites"/>）。量不到的（轮播收着、那一排还没实到屏上）留空 ——
    /// 动画那一头每一处都有退路。
    /// </summary>
    private void MeasureBeforeFold()
    {
        var rows = new Dictionary<CardShelf, double>();
        for (var index = 0; index < ViewModel.Shelves.Count; index++)
        {
            if (RowAt(index) is { } row) rows[ViewModel.Shelves[index]] = TopIn(row, ShelfRepeater);
        }

        // 排卡那条线两个方向都要：压上那一趟它是起飞点（媒体库那一排还在横排里），回默认那一趟它是落点
        // （那个位置此刻站着补上来的下一排，插回去之后排卡就落回同一条线 —— 插入不改这一格的 y）。
        var library = RowAt(ViewModel.LibraryFlowIndex);

        _before = new FoldSites(
            rows,
            library is null ? null : HomeFoldMotion.StripTop(library, BannerZone),
            Banner.Visibility == Visibility.Visible ? TopIn(Banner.InfoBlock, Banner) : null);

        Log.Info(Category, $"矮窗档旧账：在册 {rows.Count}/{ViewModel.Shelves.Count} 排"
            + $"，排卡线={_before.Strip?.ToString("0") ?? "量不到"}"
            + $"，字块={_before.Info?.ToString("0") ?? "量不到"}");
    }

    /// <summary>
    /// 横排里第 <paramref name="index"/> 排此刻能被动画碰的那个元素。Repeater 的直接子是框架 arrange/viewport
    /// 自己要操纵的对象（见 <see cref="HomeMotion.TargetOf"/>），进场动画落在它里面的模板根上，这里量、动的也是
    /// 同一处 —— 免得「量的是甲、动的是乙」。那一排还没实到屏上（视口外、或者刚搬走）时交回空。
    /// </summary>
    private FrameworkElement? RowAt(int index) =>
        index >= 0 && ShelfRepeater.TryGetElement(index) is FrameworkElement row
            ? HomeMotion.TargetOf(row) ?? row
            : null;

    /// <summary>一个元素在 <paramref name="space"/> 里的上沿。</summary>
    private static double TopIn(FrameworkElement element, UIElement space) =>
        element.TransformToVisual(space).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;

    /// <summary>
    /// 矮窗档：窗口拉矮到那条线被第一屏裁掉之后，把媒体库那一排压到轮播封面的左下角，下面那排向上补位；再矮到轮播
    /// 被一屏压矮（裁切超出设计形状）就恢复默认（用户的话，2026-09-13；2026-09-22 那条线又往下挪了一排 ——「改为
    /// 快要显示下方媒体库封面的时候上移」，见 <see cref="HomeFold"/> 开头那一段）。
    /// 界线本身是 <see cref="HomeFold"/> 的纯函数（Core 的单测盯着），这里出的是量几何和执行的那一半。
    /// <para>
    /// 量的全是「滚回顶上」的几何（<see cref="ScreenTopAtScrollZero"/> 添回滚动位移）：判定认的是窗口高矮，
    /// 不认滚动位置 —— 拿现场坐标判，用户往下滚一下这一档就会自己翻面。那条线由 <see cref="HomeFold.CoversLine"/>
    /// 从「媒体库下面那一排」的顶算出来（它自己的封面顶，或者再下面那一排的封面顶）；压上档里媒体库不在横排了，
    /// 补位那排站在它那一格上，顶就是「媒体库的顶」，而媒体库那一排的高要按默认摆法还原（牌子 ＋ 空当 ＋ 排卡）
    /// —— 牌子和空当从补位那排的模板上量（<see cref="HeadOf"/>），同一张模板同一个宽，两头一个数；排卡那一截
    /// 量轮播上那份宿主（<c>LibraryOverlay</c>，压上档里牌子收起、宿主里只有排卡）。
    /// </para>
    /// <para>
    /// 无声退出的几档：轮播收起来（没有幻灯片／设置里关掉）没有左下角可压；媒体库那一排不存在（勾掉或者空）；
    /// 它下面没有别的排（压上去没有意义）。这几档一律回到默认叠法。
    /// </para>
    /// </summary>
    private void UpdateLibraryOverlay()
    {
        if (XamlRoot is null) return;

        // 判定吃的是排完的树：带高、货架顶都要此刻的坐标（同 FoldRead 的两拍）。
        UpdateLayout();

        var viewport = Scroller.ActualHeight;

        if (viewport <= 0 || Banner.Visibility != Visibility.Visible)
        {
            Fold(false);
            ApplyLibraryChrome();
            return;
        }

        var gap = ShelfRepeater.Layout is StackLayout stack ? stack.Spacing : 0;
        double nextRowLine;
        bool hasNext;

        if (ViewModel.LibraryOnBanner)
        {
            // 压上档：横排里媒体库那个位置现在是补上来的下一排，它的顶就是「媒体库的顶」。补位的排没了
            // （重载之后媒体库成了最后一排之类）这一档没有意义，回默认。
            var index = ViewModel.LibraryFlowIndex;

            if (index < 0 || index >= ViewModel.Shelves.Count)
            {
                Fold(false);
                ApplyLibraryChrome();
                return;
            }

            if (ShelfRepeater.TryGetElement(index) is not FrameworkElement filler)
            {
                filler = (FrameworkElement)ShelfRepeater.GetOrCreateElement(index);
                UpdateLayout();
            }

            // 那条线按默认摆法还原：补位那排此刻站在媒体库那一格上，它的顶就是**媒体库**的顶；添回媒体库整排的
            // 高（牌子 ＋ 空当 ＋ 排卡）和一道排间空当，才是补位那排自己在默认摆法里的顶 —— 线由
            // <see cref="HomeFold.CoversLine"/> 从这个顶往下算，两个方向量到的因此是同一条线。宿主还没排完的
            // 那一拍退回排卡自己的高（RowHeight，条带绑的就是它），牌子量不到的那一拍退回排顶口径 —— 都有下一拍校正。
            var head = HeadOf(filler);
            var strip = LibraryOverlay.ActualHeight > 0
                ? LibraryOverlay.ActualHeight
                : ViewModel.LibraryShelf?.RowHeight ?? 0;

            nextRowLine = HomeFold.CoversLine(
                ScreenTopAtScrollZero(filler)
                    + (head?.Height ?? 0) + (head?.Gap ?? 0) + strip
                    + gap,
                head?.Height ?? 0,
                head?.Gap ?? 0,
                ViewModel.Shelves[index].RowHeight,
                gap,
                hasBelow: index + 1 < ViewModel.Shelves.Count);
            hasNext = true;
        }
        else
        {
            // 默认档：媒体库还在横排里，线 ＝ 它**下面那一排的再下面那一排**的封面顶（<see cref="HomeFold.CoversLine"/>；
            // 2026-09-22 从「下面那一排自己的封面顶」往下挪了一排，见 HomeFold 开头那一段）。牌子、空当、排卡
            // 都从排好的树上量 —— 同一张模板，每排的牌子一个高，不写死。
            if (ViewModel.LibraryShelf is not { } shelf || !ViewModel.Shelves.Contains(shelf)) return;

            var below = ViewModel.LibraryFlowIndex + 1;

            // 媒体库是最后一排：它下面没有排可以补位，压上去没有意义（同从前那一档）。
            if (below >= ViewModel.Shelves.Count)
            {
                Fold(false);
                ApplyLibraryChrome();
                return;
            }

            if (ShelfRepeater.TryGetElement(below) is not FrameworkElement next)
            {
                next = (FrameworkElement)ShelfRepeater.GetOrCreateElement(below);
                UpdateLayout();
            }

            var head = HeadOf(next);

            nextRowLine = HomeFold.CoversLine(
                ScreenTopAtScrollZero(next),
                head?.Height ?? 0,
                head?.Gap ?? 0,
                ViewModel.Shelves[below].RowHeight,
                gap,
                hasBelow: below + 1 < ViewModel.Shelves.Count);
            hasNext = true;
        }

        // 带子的设计形状有多高（不被一屏封住的那一份，<see cref="HomeCarousel.NaturalHeight"/>）：视口够不到
        // 它，带子就被压矮、剧照上下裁切超出默认档 —— 那是回默认的线（2026-09-13「轮播图上下裁切过多时隐藏」）。
        var bandNatural = HomeCarousel.NaturalHeight(Banner.ActualWidth);

        var decided = hasNext && HomeFold.LibraryOnBanner(viewport, nextRowLine, bandNatural);

        // 翻档前后各记一条线的账：验收这一档时不用去猜界线落在哪一排（<see cref="HomeFold.CoversLine"/>）。
        // 被「等上一趟落地」拦下的那一次也记 —— 那条日志正好说明这一拍量到的是哪条线。
        if (decided != ViewModel.LibraryOnBanner)
            Log.Info(Category, $"矮窗档界线：线 {nextRowLine:0}、视口 {viewport:0}"
                + $"（带子的设计形状 {bandNatural:0}）→ {(decided ? "压上轮播" : "回默认")}");

        Fold(decided);
        ApplyLibraryChrome();
    }

    /// <summary>
    /// 货架集合变了（重载、矮窗档搬进搬出）之后排一拍重查：事件当场树上还是半成品，量不出数。
    /// <see cref="_foldScheduled"/> 把排队的并成一拍。
    /// </summary>
    private void ScheduleFold()
    {
        if (_foldScheduled || DispatcherQueue is not { } queue) return;

        _foldScheduled = true;
        queue.TryEnqueue(() =>
        {
            _foldScheduled = false;
            UpdateLibraryOverlay();
        });
    }

    /// <summary>
    /// 按视图模型此刻的矮窗档状态拨这一页的屏上开关：宿主、浅墨、轮播那头的让位；翻档的那一趟还要把三样东西的
    /// 位移补一趟（见 <see cref="HomeFoldMotion"/>）。
    /// <para>
    /// 摆法本身和从前一样，差别只在「摆完把位移补回旧落点」：旧落点是翻档之前量下的（<see cref="FoldSites"/>，
    /// 量在搬之前），新落点是这里摆完才量得到的，中间那一段交给动画。
    /// </para>
    /// </summary>
    private void ApplyLibraryChrome()
    {
        var on = ViewModel.LibraryOnBanner && ViewModel.LibraryShelf is not null;
        var shelf = ViewModel.LibraryShelf;

        // 「真在屏上摆过」的档才配当动画的起点：页面刚起来那几拍轮播还没有幻灯片、视口还是零，那时 `Fold` 摆下的
        // 是个假档（数据回来时还要再翻一次）—— 拿假档当起点会凭空飞一趟，而自检正是在那几拍上读坐标的。
        var real = Banner.Visibility == Visibility.Visible && ViewModel.Slides.Count > 0 && Scroller.ActualHeight > 0;
        var flight = _foldReal && real && on != _foldOn && _before is not null
            && HomeFoldMotion.Enabled && XamlRoot is not null;

        _foldOn = on;
        _foldReal = real;

        if (_fold is not null && !flight)
        {
            // 上一趟还在走，而这一档没翻：什么都不摆。回默认那一趟里宿主自己就是动画的载体，把它收起来等于把托着的
            // 那一排卡从屏上撤掉 —— 让它走完，下一次翻档（或者它自己落地）自然会对上。
            return;
        }

        if (flight)
        {
            _fold?.Stop();
            _foldLate?.Stop();
            _foldLate = null;
            _fold = new HomeFoldMotion();
        }

        var before = _before;
        _before = null;

        // 翻档那一趟宿主是动画的载体：回默认时它还得站在原地托着那一排卡滑下去（走完由 <see cref="HandBack"/>
        // 收掉），所以这一拍不能先把内容清掉 —— 清了就是「载体先没了，然后什么都没有发生」。
        LibraryOverlay.Visibility = on || flight ? Visibility.Visible : Visibility.Collapsed;
        LibraryHost.Content = on || flight ? shelf : null;
        if (shelf is not null) shelf.OnScrim = on;
        UpdateLayout();

        // 刚插回横排的那一排会自己走一遍进场（HomeMotion 挂在 ElementPrepared 上）——那 20 像素的上浮是渲染
        // 位移，会把这一拍的几何量读歪、还喂给滚动视口的锚定；回默认这一趟它的模样归矮窗档的动画管，当场按掉。
        if (!on && RowAt(ViewModel.LibraryFlowIndex) is { } returned) HomeMotion.Stop(returned);

        // 排高一拍再量：宿主刚装上内容，没排过之前 ActualHeight 是 0 —— 轮播那头（字块让位）要的是排卡的总高。
        // 量不到的那一帧退回排卡自己的高，下一拍自会校正。
        if (on && shelf is not null)
        {
            Banner.SetLibraryOverlay(true, LibraryOverlay.ActualHeight > 0 ? LibraryOverlay.ActualHeight : shelf.RowHeight);
        }
        else
        {
            Banner.SetLibraryOverlay(false, 0);
        }

        UpdateLayout();

        if (_fold is null) return;

        if (shelf is null || before is null)
        {
            Log.Warn(Category, $"矮窗档这一趟没走成：排在={shelf is not null} 旧账在={before is not null}");
            return;
        }

        // 媒体库那一排要走的那一段，两头的「排卡上沿」都是**翻档前**量下的旧账（<see cref="FoldSites.Strip"/>）：
        // 压上那一趟它是起飞点（那一排还在横排里），回默认那一趟它是落点（补位的那一排此刻正站在那个位置上，
        // 插回去之后排卡就落回同一条线 —— 插入不改这一格的 y）。现场只量宿主此刻在哪，行程就是两头之差。
        // **不能等摆完再量落点**：Repeater 的重排跟着滚动视口慢一拍，当场量到的是没换过位置的旧数。
        var overlay = TopIn(LibraryOverlay, BannerZone);
        var hostDrift = HomeFoldMotion.DriftOf(LibraryOverlay);
        var distance = before.Strip is { } line ? line - overlay : 0;

        // 行程还得是这一页量得出来的数：矮窗档搬的就是屏上那几排，一趟最多穿过一屏多一点。量歪了
        // （从 Repeater 池子里取回来的一排停在虚拟化给的临时位置上）会算出上万像素 —— 这一趟放它走，那个位移
        // 又会被下一个重查读成「真几何」，正是 Fold 那条注释里说的自己喂自己的环。宁可一刀硬切，不要半截动画。
        var plausible = before.Strip is not null && Math.Abs(distance) <= Math.Max(Scroller.ActualHeight * 2, 320);

        if (hostDrift is null || !plausible)
        {
            // 宿主这一趟规划不成（宿主身上挂着别人的变换，旧账里没量到那条线，或者行程离谱）：整趟放弃，按老样子
            // 直接落定。走到这一行多半是谁又动了宿主的 RenderTransform，或者刚重排完的树上还没量出真位置。
            Log.Warn(Category, $"矮窗档这一趟规划不成：位移={(hostDrift is null ? "拿不到" : $"{distance:0} 像素")}"
                + $"（宿主变换={LibraryOverlay.RenderTransform?.GetType().Name ?? "空"}）"
                + $" 排卡线={(before.Strip.HasValue ? before.Strip.Value.ToString("0") : "量不到")} 压上={on}");
            if (!on) HandBack();
            _fold = null;
            return;
        }
        _fold.Slide(hostDrift, on ? distance : 0, on ? 0 : distance);

        // 回默认那一趟：宿主的淡出压在最后一百来毫秒里 —— 它要一路托着那一排卡滑到横排，落点上那一份
        // （迟一拍才实到）从暗里浮出来，两头在落点上换手。
        if (!on) _fold.FadeLate(LibraryOverlay);

        // 轮播里那块字：矮窗档要它让出底下一段（HomeBanner.PlaceInfo 整段换掉底边距），那一下同样是硬切，
        // 同样补一趟「先待在旧位置、再滑到新位置」。它的布局不归 Repeater 管，当场量当场动。
        if (before.Info is { } info && HomeFoldMotion.DriftOf(Banner.InfoBlock) is { } infoDrift)
        {
            var delta = info - TopIn(Banner.InfoBlock, Banner);
            if (Math.Abs(delta) > 0.5) _fold.Slide(infoDrift, delta, 0);
        }

        if (on) _fold.Play();
        else _fold.Play(HandBack);

        Log.Info(Category,
            $"矮窗档{(on ? "：媒体库那一排升上轮播" : "：媒体库那一排落回横排")}"
            + $"（行程 {Math.Abs(distance):0} 像素，{HomeFoldMotion.DurationMilliseconds} ms，字块"
            + $"{(before.Info is { } ? "跟着让位" : "没参与")}）；补位／让位迟一拍另记");

        // 补位／让位要**迟一拍**：Repeater 的重排跟着滚动视口走，同一拍里 UpdateLayout 两遍量到的还是没换过
        // 位置的旧数（差是零，什么都补不上）。推一拍，等它真排完了量差，再让那几排从旧位置滑过来；
        // 回默认那一趟里刚实到的媒体库那一份也在这儿收编（压掉它自己的进场）并从暗里浮出，接住落下的宿主。
        var book = before.Rows;
        var flowIndex = ViewModel.LibraryFlowIndex;
        var flyingIn = on;

        if (DispatcherQueue is not { } queue) return;

        queue.TryEnqueue(() =>
        {
            var late = new HomeFoldMotion();
            var moved = 0;

            for (var index = 0; index < ViewModel.Shelves.Count; index++)
            {
                if (!book.TryGetValue(ViewModel.Shelves[index], out var was)) continue;
                if (RowAt(index) is not { } row) continue;

                var delta = was - TopIn(row, ShelfRepeater);
                if (Math.Abs(delta) < 0.5) continue;
                if (HomeFoldMotion.DriftOf(row) is not { } rowDrift) continue;

                // 这一排的进场（HomeMotion 挂在 Repeater 的 ElementPrepared 上）如果是被这次换位顺带放上的，
                // 两支动画会抢同一支位移 —— 这一段里这一排归这一趟管。
                HomeMotion.Stop(row);
                late.Slide(rowDrift, delta, 0);
                moved++;
            }

            if (!flyingIn && RowAt(flowIndex) is { } landing)
            {
                HomeMotion.Stop(landing);
                late.FadeIn(landing);
            }

            _foldLate = late;
            late.Play();
            Log.Info(Category, $"矮窗档补位（迟一拍）：{moved} 排");
        });
    }

    /// <summary>
    /// 回默认那一趟走到头：把宿主收起来。它这一路上托着那一排卡（回默认那一趟的载体），此刻横排里那一份已经落在
    /// 同一个位置上，收掉看不出来 —— 但必须收：宿主是 <c>BannerZone</c> 的子元素、压在货架那一叠上面，留在那儿就
    /// 开始替横排那一份接指针（悬停浮出的按钮、点击都落在另一份上）。
    /// </summary>
    private void HandBack()
    {
        LibraryOverlay.Visibility = Visibility.Collapsed;
        LibraryHost.Content = null;
    }

    /// <summary>
    /// 元素顶边在「滚回顶上」之后的位置：现场坐标添回滚动位移。判定认窗口高矮不认滚动位置，见
    /// <see cref="UpdateLibraryOverlay"/>。
    /// </summary>
    private double ScreenTopAtScrollZero(FrameworkElement element) =>
        element.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0)).Y
        + Scroller.VerticalOffset;

    /// <summary>
    /// 一排货架的牌子有多高、牌子到排卡之间让了多大的空当：从那一排的子树里把牌子翻出来量，空当取挂牌子那层
    /// StackPanel 的间距 —— **不写死、也不假设模板根的形状**（09-16 模板根外面包了一层 Grid 之后，Repeater 的
    /// 直接子就不再是 StackPanel，老写法「直接子是不是 StackPanel」从那天起一直在交空，两条界线因此各差一个
    /// 牌子的高 —— 窗口高矮落进那条缝里，矮窗档就来回翻（2026-09-18「上移的时候程序会卡住」的根因））。
    /// 量不到（那排还没排完）交回空，调用方各有退路。
    /// </summary>
    private static (double Height, double Gap)? HeadOf(FrameworkElement shelf)
    {
        if (HomeFoldMotion.Find<ShelfHead>(shelf) is not { } head || head.ActualHeight <= 0) return null;
        var gap = VisualTreeHelper.GetParent(head) is StackPanel panel ? panel.Spacing : 0;
        return (head.ActualHeight, gap);
    }

    /// <summary>
    /// 自检：轮播这条带的高度就是 <see cref="HomeCarousel.Height"/> 按它自己的宽算出来的那个数，而横着的那几排
    /// 接在带子下沿之后。
    /// <para>
    /// 带宽就是整幅页宽（通栏，2026-09-11 从「页宽 − 48」回来）；其余照旧：剧照铺满整条带（2026-09-11），
    /// 第一排接在带子下沿之后而不是压在它上面。
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

        // 这条带的高度就是那条规则算出来的：带宽是它自己量到的宽（通栏之后就是整幅页宽），一张幻灯片都没有时
        // 整条带收起，那一档不判。
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
                + (shaped ? $"，正是这个宽度该有的高 {wanted:0}（剧照铺满整条带、按带子的形状裁）" : $"，该高 {wanted:0}")
                + $"，这条带下沿 {heroBottom:0}；{nextNote}");
    }

    /// <summary>
    /// <c>ShellPage.xaml</c> 里 <c>ContentHost</c> 给外壳留的那一段（<c>Padding="0,32,0,0"</c>）：标题栏那一行
    /// 的高。轮播通栏之后这一页要把它顶回去（<see cref="SyncBleed"/>，Banner 挂一条 −32 的上边距），所以这个数
    /// 有三份用场：SyncBleed 拿它当那口负边距的大小、BleedRead 拿它写读数、自检那一关
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

        var plan = ItemMenu.For(card.Item, _actions.MoviePilotEnabled);
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

        // 标题栏那一行浮在剧照上，所以那一行的墨走固定的浅墨（不跟主题走）—— 和详情页头图铺到顶边时一个规矩。
        // 带子还没有幻灯片时下面判成 Plain，见 PaintInk。
        _actions?.SetTitleStrip(TitleStrip.OnScrim);

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
            services.GetRequiredService<EmbyImageStore>(),
            services.GetRequiredService<MoviePilotService>());

        // 轮播的停留秒数（设置 → 主页 → 「封面轮换秒数」，2026-09-13）页面一进来就同步一次 —— 带子自己
        // 没有设置，它等的是这句话。
        Banner.ApplyDwell(settings.Settings.Ui.CarouselSeconds);

        // 版面（拖拽出来的次序、勾掉的那几排）改完当场生效：设置页在另一个窗口里，它改完喊一声，这一页重排。
        ShellPrefs.Changed -= OnShellPrefsChanged;
        ShellPrefs.Changed += OnShellPrefsChanged;

        _ = ViewModel.ReloadAsync();
    }

    public void Release()
    {
        ShellPrefs.Changed -= OnShellPrefsChanged;

        // 这一页走了，把标题栏还回它自己的规矩（别的页都按主题那支墨）。不还的话下一次它就一直浅着。
        _actions?.SetTitleStrip(TitleStrip.Plain);

        // 走到一半的那一趟也收掉：Storyboard 还握着这一页的元素，留着就是一间关了灯还在跑的屋子。
        _fold?.Stop();
        _fold = null;
        _foldLate?.Stop();
        _foldLate = null;
        _before = null;

        ViewModel.Cancel();
    }

    /// <summary>
    /// 标题栏那一行的墨：带子真在屏上（有幻灯片、并且这一块没被收起来）走 <see cref="TitleStrip.OnScrim"/>，
    /// 剧照顶上那层暗罩托着它；一张幻灯片都没有、这一块自己收起来了的时候，标题栏底下又变回页面底色，那时得还
    /// 回 <see cref="TitleStrip.Plain"/>，不然浅墨画在浅色页底上就是几颗看不见的按钮。
    /// <para>
    /// 这是 2026-09-10 删掉、2026-09-11 找回来的那条联动 —— 那时候「框成卡片」让剧照不再碰窗口的顶边，标题栏
    /// 底下永远是页面底色，联动没有意义。参照详情页的 <c>DetailPage.SyncTitleInk</c>。
    /// </para>
    /// </summary>
    private void PaintInk() =>
        _actions?.SetTitleStrip(Banner.Visibility == Visibility.Visible ? TitleStrip.OnScrim : TitleStrip.Plain);

    /// <summary>
    /// 设置里主页那一组改了（拖拽排序、勾选、轮播的来源媒体张数秒数），照新的重排一遍 —— 轮播那几行
    /// 写完也喊的是这一声：来源和张数随 <see cref="HomeViewModel.ApplyLayoutAsync"/> 重新取，秒数走
    /// <see cref="HomeBanner.ApplyDwell"/> 当场换钟。卡片尺寸那几个数不在这一句里 —— 它们是
    /// <see cref="HomeViewModel.Attach"/> 时的快照，下次开这一页才换（见那一段说明）。
    /// </summary>
    private void OnShellPrefsChanged(Configuration.UiSettings ui)
    {
        Banner.ApplyDwell(ui.CarouselSeconds);
        _ = ViewModel.ApplyLayoutAsync();
    }

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
    /// 2026-09-13：点「最近添加 · XXX」那一排的牌子（标题，或者它右端那个大于号）进那个库。
    /// 这一排从牌子自己身上取（<c>ShelfTemplate</c> 里 <c>Tag="{x:Bind}"</c> 挂上去的），**不走
    /// <c>DataContext</c>** —— ItemsRepeater + x:Bind 的模板实例上没人设过 DataContext（页面自己也没有），
    /// 它永远是空的，模式匹配必败、点击无声。2026-09-14 用户点「继续观看」没反应，排查一整轮才落在这里：
    /// 卡片那边（<see cref="OnCardClicked"/>）一直是好的，靠的是 <c>PosterCard.Card</c> 这条编译期通道，
    /// 牌子照卡片抄、抄了一半。
    /// <para>
    /// 三种情形各留一行账：这一排「点了没反应」排查过一回（用户点了「继续观看」，外壳零日志，断点在点击与
    /// 外壳之间），这三行就是为了下次再有同样的话，日志自己能说出断在哪一环 —— 点击到了没有、这一排认没
    /// 认出来、认出来的那一排目标是什么。
    /// </para>
    /// </summary>
    private void OnShelfHeadInvoked(object sender, EventArgs e)
    {
        if (sender is ShelfHead { Tag: CardShelf shelf })
        {
            Log.Info(Category, $"牌子点击：{shelf.Title}（目标 {shelf.Target}，可进 {shelf.CanOpen}）");
            ViewModel.OpenShelf(shelf);
        }
        else if (sender is ShelfHead head)
        {
            Log.Warn(Category, $"牌子点击但认不出这一排：{head.Title}，Tag 是 {head.Tag?.GetType().FullName ?? "空"}");
        }
        else
        {
            Log.Warn(Category, $"牌子点击但 sender 不是牌子：{sender?.GetType().FullName ?? "空"}");
        }
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

    private void Reload()
    {
        _ = ViewModel.ReloadAsync();

        // 重载之后是另一批货架，进场动画再放一遍（HomeMotion 那一拍是给「这一页刚画出来」用的，重载时那些
        // 排是新建的元素，也要从暗处上来，不然换一排就硬生生换掉）。
        HomeMotion.Enter(this, ShelfRepeater, ViewModel.Shelves.Count);
    }
}

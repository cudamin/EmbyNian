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

    /// <summary>矮窗档的重查已经排了一拍还没跑。防止 <see cref="Shelves"/> 一连串增删把重查排成一把。</summary>
    private bool _foldScheduled;

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
        ViewModel.Shelves.CollectionChanged += (_, _) => ScheduleFold();
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

    /// <summary>
    /// 矮窗档：窗口拉矮到「继续观看」牌子底下那条线被第一屏裁掉之后，把媒体库那一排压到轮播封面的左下角，下面那排
    /// 向上补位；再矮到轮播被一屏压矮（裁切超出设计形状）就恢复默认（用户的话，2026-09-13，触发线同日从
    /// 「下一排整个出屏」改成「窗口裁切超过继续观看下方的那条线」—— 线以下只剩一块有招牌没货的牌子，最难看）。
    /// 界线本身是 <see cref="HomeFold"/> 的纯函数（Core 的单测盯着），这里出的是量几何和执行的那一半。
    /// <para>
    /// 量的全是「滚回顶上」的几何（<see cref="ScreenTopAtScrollZero"/> 添回滚动位移）：判定认的是窗口高矮，
    /// 不认滚动位置 —— 拿现场坐标判，用户往下滚一下这一档就会自己翻面。那条线 ＝ 下一排的顶 ＋ 牌子的高；
    /// 压上档里媒体库不在横排了，下一排补在它那个位置，顶就是「媒体库的顶」，而媒体库那一排的高要按默认摆法
    /// 还原（牌子 ＋ 空当 ＋ 排卡）—— 牌子和空当从补位那排的模板上量（<see cref="HeadOf"/>），同一张模板
    /// 同一个宽，两头一个数；排卡那一截量轮播上那份宿主（<c>LibraryOverlay</c>，压上档里牌子收起、宿主里
    /// 只有排卡）。
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
            ViewModel.SetLibraryOverlay(false);
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
                ViewModel.SetLibraryOverlay(false);
                ApplyLibraryChrome();
                return;
            }

            if (ShelfRepeater.TryGetElement(index) is not FrameworkElement filler)
            {
                filler = (FrameworkElement)ShelfRepeater.GetOrCreateElement(index);
                UpdateLayout();
            }

            // 那条线按默认摆法还原：补位那排的顶 ＋ 默认摆法里媒体库那一排的高（牌子 ＋ 空当 ＋ 排卡）＋ 排间
            // 空当 ＋「继续观看」自己牌子的高。宿主还没排完的那一拍退回排卡自己的高（RowHeight，条带绑的就是
            // 它），牌子量不到的那一拍退回排顶口径 —— 都有下一拍校正。
            var head = HeadOf(filler);
            var strip = LibraryOverlay.ActualHeight > 0
                ? LibraryOverlay.ActualHeight
                : ViewModel.LibraryShelf?.RowHeight ?? 0;

            nextRowLine = ScreenTopAtScrollZero(filler)
                + (head?.Height ?? 0) + (head?.Gap ?? 0) + strip
                + gap
                + (head?.Height ?? 0);
            hasNext = true;
        }
        else
        {
            // 默认档：媒体库还在横排里，线 ＝ 它整排的下沿 ＋ 排间空当 ＋ 下一排牌子的高。牌子从媒体库自己
            // 那块上量 —— 同一张模板，每排的牌子一样高。
            if (ViewModel.LibraryShelf is not { } shelf || !ViewModel.Shelves.Contains(shelf)) return;

            if (ShelfRepeater.TryGetElement(ViewModel.LibraryFlowIndex) is not FrameworkElement element)
            {
                element = (FrameworkElement)ShelfRepeater.GetOrCreateElement(ViewModel.LibraryFlowIndex);
                UpdateLayout();
            }

            nextRowLine = ScreenTopAtScrollZero(element)
                + element.ActualHeight
                + gap
                + (HeadOf(element)?.Height ?? 0);
            hasNext = ViewModel.LibraryFlowIndex + 1 < ViewModel.Shelves.Count;
        }

        // 带子的设计形状有多高（不被一屏封住的那一份，<see cref="HomeCarousel.NaturalHeight"/>）：视口够不到
        // 它，带子就被压矮、剧照上下裁切超出默认档 —— 那是回默认的线（2026-09-13「轮播图上下裁切过多时隐藏」）。
        var bandNatural = HomeCarousel.NaturalHeight(Banner.ActualWidth);

        ViewModel.SetLibraryOverlay(
            hasNext && HomeFold.LibraryOnBanner(viewport, nextRowLine, bandNatural));
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

    /// <summary>按视图模型此刻的矮窗档状态拨这一页的屏上开关：宿主、牌子收不收、浅墨、轮播那头的让位。</summary>
    private void ApplyLibraryChrome()
    {
        var on = ViewModel.LibraryOnBanner && ViewModel.LibraryShelf is not null;
        var shelf = ViewModel.LibraryShelf;

        LibraryOverlay.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        LibraryHost.Content = on ? shelf : null;

        if (shelf is not null)
        {
            shelf.OnScrim = on;

            // 压上档不摆牌子：「媒体库压在轮播图上的时候不用显示那个媒体库标题」（2026-09-13）。
            // 同一份 VM 在横排和宿主两头搬，牌子跟着档收放。
            shelf.ShowHead = !on;
        }

        if (!on)
        {
            Banner.SetLibraryOverlay(false, 0);
            return;
        }

        // 排高一拍再量：宿主刚装上内容，没排过之前 ActualHeight 是 0 —— 轮播那头（字块让位）要的是
        // 排子的总高（牌子 ＋ 空当 ＋ 一排卡）。量不到的那一帧退回排卡自己的高，下一拍自会校正。
        UpdateLayout();
        Banner.SetLibraryOverlay(true, LibraryOverlay.ActualHeight > 0 ? LibraryOverlay.ActualHeight : shelf!.RowHeight);
    }

    /// <summary>
    /// 元素顶边在「滚回顶上」之后的位置：现场坐标添回滚动位移。判定认窗口高矮不认滚动位置，见
    /// <see cref="UpdateLibraryOverlay"/>。
    /// </summary>
    private double ScreenTopAtScrollZero(FrameworkElement element) =>
        element.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0)).Y
        + Scroller.VerticalOffset;

    /// <summary>
    /// 一排货架的牌子有多高、牌子到排卡之间让了多大的空当：从那一排的模板实例上量 —— 同一张模板、同一列宽，
    /// 每排的牌子一个高，不写死。量不到（那排还没排完，或者那块不是货架的 StackPanel 模板）交回空，调用方
    /// 各有退路。
    /// </summary>
    private static (double Height, double Gap)? HeadOf(FrameworkElement shelf) =>
        shelf is StackPanel panel
            && panel.Children.OfType<ShelfHead>().FirstOrDefault() is { } head
            && head.ActualHeight > 0
                ? (head.ActualHeight, panel.Spacing)
                : null;

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
            services.GetRequiredService<EmbyImageStore>());

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

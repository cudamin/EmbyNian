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

        // 右栏那两条翻页条的悬停网，见 SyncRailArrows。挂在整栏上而不是只挂在那个 ScrollView 上：指针落在
        // 「继续观看」那块牌子上时也算在这一栏里，而牌子和卡片之间那 10 像素的缝不该让翻页条闪一下。
        _railWatch = new HoverWatch(Rail, up =>
        {
            _railHover = up;
            SyncRailArrows();
        });
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
    /// The rows the page actually drew, as 「继续观看 12（右栏）、媒体库 3」. For the self-check, which otherwise
    /// has no way to tell an empty row from a row that failed to build: <see cref="LoadedCount"/> counts
    /// only what the server's own rows returned, so a 媒体库 row that had gone missing would not move it
    /// by one. 继续观看 is named first and marked, because it is not one of the horizontal rows any more —
    /// it is the column down the right of the first screen (<see cref="HomeViewModel.Rail"/>).
    /// </summary>
    internal string ShelfSummary
    {
        get
        {
            var rows = ViewModel.Shelves.Select(shelf => $"{shelf.Title} {shelf.Cards.Count}").ToList();

            if (ViewModel.Rail is { } rail) rows.Insert(0, $"{rail.Title} {rail.Cards.Count}（右栏）");

            return rows.Count == 0 ? "无" : string.Join("、", rows);
        }
    }

    /// <summary>
    /// 自检：需求 5 的那条带 —— 几张幻灯片，背后那些条目有哪几种图，那条带在这一页上真正的样子，以及右栏框出了
    /// 台上哪一张。
    /// <para>
    /// 三半合成一行，因为它们各答一个问题，少一半都不够：数据那半（<see cref="HomeViewModel.BannerSummary"/>）
    /// 说服务器给了什么，屏上那半（<see cref="HomeBanner.State"/>）说那些东西有没有真变成一条有高度、有图的
    /// 带。八张幻灯片配一条量不到宽度的空带，是这条带唯一一种不出声的坏法。第三半（<see cref="FrameSummary"/>）
    /// 是「轮播和右栏对上了没有」—— 那件事在屏上是一圈线，报告里得有句话说它到底框在了哪一张上。
    /// </para>
    /// </summary>
    internal string BannerSummary => $"{ViewModel.BannerSummary}；屏上 {Banner.State}；右栏 {FrameSummary}";

    /// <summary>自检：那条带上的字体和键高，见 <see cref="HomeBanner.TypeRead"/>。</summary>
    internal (bool Ok, string Detail) BannerType() => Banner.TypeRead();

    /// <summary>自检：剧照整张画出来了没有，见 <see cref="HomeBanner.PictureRead"/>。</summary>
    internal (bool Ok, string Detail) BannerPicture() => Banner.PictureRead();

    /// <summary>自检：屏上那几排真按设置里那份版面来的，见 <see cref="HomeViewModel.LayoutRead"/>。</summary>
    internal (bool Ok, string Detail) LayoutRead() => ViewModel.LayoutRead();

    /// <summary>
    /// 自检：顶上那一整块到底铺没铺满 —— 「红框框出来的地方全填充上海报」。
    /// <para>
    /// 量的是那一块自己在窗口里的位置：上沿要落在 y=0（页面把 <c>ContentHost</c> 留给外壳那两行的 80 像素顶
    /// 回去了，见 <see cref="SyncBleed"/>），右沿要落在窗口的右边沿。这两件事在报告里都不出声 —— 少顶那 80
    /// 就是图上一道黑边、右边差一截就是一条白缝，而张数、剧照、字体那几行读数一个都不会变。
    /// </para>
    /// <para>
    /// 这一块现在是并排两栏（大图 + 右栏那一列媒体库），所以「右沿」是右栏的右沿；两栏之间有没有缝、右栏
    /// 多宽，由 <see cref="FoldRead"/> 量。
    /// </para>
    /// <para>
    /// 顶上那一块不是图的时候（设置里关掉了轮播，或者服务器上一个带宽图的条目都没有）判的正好相反：那 80 像素
    /// 必须<em>留着</em>，这一页从第 80 行起画、和别的页面一样 —— 否则「HOME / 主页」那块牌子会塞进标题栏和
    /// 标签栏底下。
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) BleedRead()
    {
        var at = Hero.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
        var window = XamlRoot?.Size.Width ?? 0;
        var right = at.X + Hero.ActualWidth;
        var filled = ViewModel.HeroFilled;
        var top = filled ? at.Y <= 0.5 : Math.Abs(at.Y - ChromeHeight) <= 0.5;
        var ok = Hero.ActualWidth > 0 && window > 0 && top && Math.Abs(right - window) <= 1.5;

        return (ok, $"起点 ({at.X:0},{at.Y:0})、{Hero.ActualWidth:0}×{Hero.ActualHeight:0}"
            + $"，右沿 {right:0} 对窗口宽 {window:0}"
            + (filled
                ? $"；顶上是一张图，所以上沿该落在 0{(top ? "" : "（没顶掉标题栏那一条）")}"
                : $"；顶上不是图（轮播关着或者没有宽图），所以上沿该落在 {ChromeHeight:0}"
                    + $"{(top ? "" : "（不该顶掉标题栏那一条）")}"));
    }

    /// <summary>
    /// 自检：第一屏那一块就是并排两栏 —— 左边一整张不留上下底色的大图、右边一栏继续观看 —— 而横着的那几排接在
    /// 它的下沿之后（「参考上图修改轮播页面」＋「封面固定到最上方，上下不要有黑边」）。
    /// <para>
    /// 五件事：大图那一块的高度就是 <see cref="HomeCarousel.Height"/> 按它自己的宽算出来的那个数（也就是图正好
    /// 铺满、上下不留底色）、整块放得进第一屏、右栏和大图之间没有缝、右栏和大图一样高、右栏里至少有一张卡完整
    /// 落在窗口里，加上横着的第一排接在大图下沿之后而不是压在它上面。少了第三第四条，「继续观看在第一屏里」也
    /// 可能是靠一栏只露出半张卡换来的。
    /// </para>
    /// <para>
    /// 右栏不在屏上时不算错，但要和数据对得上：继续观看空着或者在设置里被勾掉了才允许它不在
    /// （<see cref="HomeViewModel.Rail"/> 是 null）—— 有内容却没显示是这一栏唯一一种不出声的坏法。
    /// </para>
    /// <para>
    /// 两栏和横排都没有时没得量，跳过而不是判红：一个从未播放过任何内容、又只勾了一排的账号就是那样，那是正常
    /// 数据。
    /// </para>
    /// </summary>
    internal (bool? Ok, string Detail) FoldRead()
    {
        if (ViewModel.Rail is null && ViewModel.Shelves.Count == 0)
            return (null, "这次屏上既没有右栏也没有横排，跳过首屏边界读数");

        if (XamlRoot is not { } root || root.Size.Height <= 0)
            return (false, "量不到窗口视口高度");

        static double Top(FrameworkElement element) =>
            element.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;

        static double Left(FrameworkElement element) =>
            element.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0)).X;

        var viewport = root.Size.Height;

        // 大图那一块的高度就是那条规则算出来的：图因此正好铺满它，上下一条底色都不留。一张幻灯片都没有时整条带
        // 收起（服务器上一个带宽图的条目都没有），那一档不判。
        var heroBottom = Top(Hero) + Hero.ActualHeight;
        var wanted = HomeCarousel.Height(viewport, Banner.ActualWidth);
        var shaped = Banner.Visibility != Visibility.Visible
            || (Math.Abs(Banner.ActualHeight - wanted) <= 1.5 && heroBottom <= viewport + 0.5);

        // 右栏：位置、宽、高，加上「至少一张卡完整在窗口里」。
        bool railOk;
        string railNote;

        if (Rail.Visibility != Visibility.Visible)
        {
            railOk = ViewModel.Rail is null;
            railNote = railOk
                ? "没有右栏（继续观看空着或者被勾掉了）"
                : $"继续观看有 {ViewModel.Rail!.Cards.Count} 项，右栏却没显示";
        }
        else
        {
            var seam = Banner.Visibility != Visibility.Visible
                || Math.Abs(Left(Rail) - (Left(Banner) + Banner.ActualWidth)) <= 1.5;
            var wide = ViewModel.RailWidth > 0 && Math.Abs(Rail.ActualWidth - ViewModel.RailWidth) <= 1.5;
            var tall = Math.Abs(Rail.ActualHeight - Hero.ActualHeight) <= 2;

            var railCards = new List<PosterCard>();
            Collect(Rail, railCards);
            var whole = railCards.Count(card =>
                card.Visibility == Visibility.Visible
                && card.ActualHeight > 0.5
                && Top(card) >= -0.5
                && Top(card) + card.ActualHeight <= viewport + 0.5);

            railOk = seam && wide && tall && whole > 0;
            railNote = $"右栏 {Rail.ActualWidth:0}×{Rail.ActualHeight:0} 从 {Left(Rail):0} 起"
                + (seam ? "，贴着大图" : "，和大图之间有缝")
                + (wide ? "" : $"（该 {ViewModel.RailWidth:0} 宽）")
                + (tall ? "" : $"（该和大图一样 {Hero.ActualHeight:0} 高）")
                + $"，完整露出 {whole} / {railCards.Count} 张卡"
                + (whole > 0 ? "" : "（一张都没完整露出来）");
        }

        // 横着的第一排接在大图下沿之后 —— 它会露进第一屏一截（那是「底下还有东西」的招牌），但绝不该压在大图上：
        // 那正是 2026-09-05 之前那一叠玻璃的做法，删掉之后这一句是它不会悄悄回来的唯一保证。
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
                    + (frontTop < viewport ? $"（露进第一屏 {viewport - frontTop:0}）" : "（在第一屏外）");
            }
        }

        return (shaped && railOk && nextOk,
            $"视口 0–{viewport:0}；大图 {Banner.ActualWidth:0}×{Banner.ActualHeight:0}"
                + (shaped ? $"，正是这个宽度该有的高 {wanted:0}（图铺满、上下不留底色）" : $"，该高 {wanted:0}")
                + $"，这一块下沿 {heroBottom:0}；{railNote}；{nextNote}");
    }

    /// <summary>
    /// 右栏那一列的宽和高。两个数都不写在标记里：宽是 <see cref="HomeCarousel.RailWidth"/> 按卡宽算的，高跟着
    /// 大图量出来的高走 —— 那一栏里是竖着一列卡片，不给高度这一格会长到十二张卡那么高，把第一屏顶出窗口。
    /// <para>
    /// 高度取的是屏上量到的那一个，而不是再算一遍：带高只有 <see cref="HomeBanner.Resize"/> 一个写手
    /// （<see cref="HomeCarousel.Height"/> 按带宽算），这里跟着它走就不会有第二个说法。一张幻灯片都没有、整条带
    /// 收起的那一档没有可量的，那时这一列按自己的内容高，但不许高过一屏 —— 否则七张卡就把这一格拉到窗口外面去。
    /// </para>
    /// </summary>
    private void SyncRail()
    {
        var width = ViewModel.RailWidth;
        if (Math.Abs(Rail.Width - width) > 0.5 || double.IsNaN(Rail.Width)) Rail.Width = width;

        var viewport = XamlRoot?.Size.Height ?? 0;
        var height = Banner.Visibility == Visibility.Visible && Banner.ActualHeight > 0
            ? Banner.ActualHeight
            : double.NaN;

        if (!double.IsNaN(height) != !double.IsNaN(Rail.Height)
            || (!double.IsNaN(height) && Math.Abs(Rail.Height - height) > 0.5))
        {
            Rail.Height = height;
        }

        var ceiling = viewport > 0 ? viewport : double.PositiveInfinity;
        if (double.IsNaN(Rail.MaxHeight) || Math.Abs(Rail.MaxHeight - ceiling) > 0.5) Rail.MaxHeight = ceiling;
    }

    private void OnBannerResized(object sender, SizeChangedEventArgs e) => SyncRail();

    /// <summary>
    /// 顶上那一块是不是真有一张图，决定这一页要不要把外壳那两行的 80 像素顶回去。见 HomePage.xaml 里 Scroller
    /// 那一段：有图才顶（「红框框出来的地方全填充上海报」），没图那一块就是页面自己的底色，顶回去只会把
    /// 「HOME / 主页」那块牌子塞进标题栏和标签栏底下。
    /// <para>
    /// 「没图」有两种：设置里关掉了轮播，以及服务器上一个带宽图的条目都没有 —— 屏上是同一件事，所以这里问的就是
    /// 屏上那一句（<see cref="HomeViewModel.HeroFilled"/>）。
    /// </para>
    /// </summary>
    private void SyncBleed()
    {
        var top = ViewModel.HeroFilled ? -ChromeHeight : 0;

        if (Math.Abs(Scroller.Margin.Top - top) > 0.5) Scroller.Margin = new Thickness(0, top, 0, 0);
    }

    /// <summary>
    /// <c>ShellPage.xaml</c> 里 <c>ContentHost</c> 给外壳那两行留的那一段（<c>Padding="0,80,0,0"</c>）：标题栏
    /// 32 加标签栏 48。这一页是整套里唯一会把它顶回去的，所以这个数在这儿有一份 —— 两处对不上的样子是图上留
    /// 一条底色，或者「HOME / 主页」那块牌子被切掉半行。
    /// <para>
    /// internal 而不是 private：自检那一关（<c>ShellPage.ProbeTabs</c>）拿它和屏上量出来的外壳高度对一遍，
    /// 而对账要的正是「两边引的是同一个数」。
    /// </para>
    /// </summary>
    internal const double ChromeHeight = 80;

    /// <summary>
    /// 台上换了一张幻灯片：右栏里对应的那一张框起来，并且滚到看得见的位置 ——「轮播图滚动到对应媒体时右边要自动
    /// 框出对应媒体」。
    /// <para>
    /// 谁对应谁由 <see cref="HomeCarousel.MatchIndex"/> 定、由视图模型翻到卡片上（<see cref="HomeViewModel.Frame"/>），
    /// 这里只剩「把它滚进视野」这一件屏上的事。
    /// </para>
    /// </summary>
    private void OnBannerSlideChanged(object? sender, BannerSlide? slide) => SyncFrame(slide?.Item);

    /// <inheritdoc cref="OnBannerSlideChanged"/>
    /// <remarks>
    /// 不叫 <c>Frame</c>：<see cref="Page"/> 上已经有一个 <c>Frame</c> 属性（这一页所在的那个导航框），同名会把它
    /// 藏起来 —— 编译器为此报 CS0108，而这个项目按 0 警告构建。
    /// </remarks>
    private void SyncFrame(EmbyItem? item)
    {
        _framed = ViewModel.Frame(item);
        RevealFramed();
    }

    /// <summary>被框着的那一张是右栏里的第几张（-1 是一张都没框）。存下来是因为右栏换一批之后要照它重新框一次。</summary>
    private int _framed = -1;

    /// <summary>
    /// 把被框着的那一张滚进右栏的视野里。八张幻灯片配一栏只放得下三张卡的右栏 —— 不滚的话后面那几张的框在屏外，
    /// 读起来就是「这个功能只对头三张有效」。
    /// <para>
    /// 位置按索引算，不按元素量，和 <see cref="ShelfStrip"/> 里那一处同一个理由：目标容器可能还没生成、更没量过，
    /// 问它自己在哪儿问到的是 0。一栏里的卡是同一个模板，所以第 n 张的上沿就是 n 个间距，而一张卡多高
    /// （<see cref="CardShelf.RowHeight"/>）本来就是那一排自己算出来的。
    /// </para>
    /// <para>
    /// 在右栏自己那个 <c>ScrollView</c> 里滚完，一句请求都不往外发（不用 <c>StartBringIntoView</c>）—— 那种请求会
    /// 一路冒到整页那个竖着滚的 <c>ScrollView</c> 上，被读成「把这张卡的上沿对到视口上沿」，于是整页往下滑一大段。
    /// 「点击封面之后会先跳转到页面下方」就是那件事，自检里「点卡片不挪页」那一关盯的也是它。
    /// </para>
    /// </summary>
    private void RevealFramed()
    {
        if (_framed < 0 || ViewModel.Rail is not { } rail) return;

        var target = Infrastructure.CardStrip.RevealFor(
            _framed,
            rail.RowHeight + RailSpacing,
            RailSpacing,
            RailScroller.VerticalOffset,
            RailScroller.ViewportHeight,
            RailScroller.ScrollableHeight);

        if (target >= 0) RailScroller.ScrollTo(RailScroller.HorizontalOffset, target);
    }

    /// <summary>右栏那一列卡片之间的间隔，和标记里 <c>RailRepeater</c> 的 <c>StackLayout.Spacing</c> 是同一个数。</summary>
    private const double RailSpacing = 16;

    // ---- 右栏翻页 ---------------------------------------------------------------
    //
    // 「把继续观看改成点击翻页的」（2026-09-05）：滚动条藏起来，指针进到这一栏时上下两头浮出一条翻页条，一次翻
    // 整整一屏卡片。翻多远、翻过去落在哪、哪一头该露出来 —— 三条规则一条都不在这里，它们就是横带那一套
    // （Core 的 CardStrip，CardStripTests 钉着），竖着用只是把「宽」换成「高」。这里剩下的是量：这一栏看得见的
    // 那一段多高、一张卡多高、还有多少可滚。
    //
    // **为什么不把 ShelfStrip 拿来竖着用**：那个控件从头到尾是横的（HorizontalOffset / ScrollableWidth /
    // ViewportWidth、贴左右两边的两颗箭头、左右方向键上那一整套虚拟化焦点补救），加一个 Orientation 就是十几处
    // 三元判断 —— 而它同时是主页每一排和详情页四条带的唯一实现，一处判断写反就是六个地方一起坏。这一栏要的只有
    // 「点一下翻一屏」，所以借规则、不借控件（连按钮的样子也是抄的，见 HomePage.xaml 里那一段）。

    /// <summary>指针在右栏上。翻页条是悬停才出现的，同横带。</summary>
    private bool _railHover;

    /// <summary>
    /// 指针到底还在不在右栏上，按 OS 说的算，不光信 <c>PointerExited</c> —— 「鼠标移出窗口后不会自动恢复」那一
    /// 条，为什么事件不够用见 <see cref="HoverWatch"/>。
    /// </summary>
    private readonly HoverWatch _railWatch;

    /// <summary>自检用：最近一次翻页要到的位置，-1 表示还没翻过。</summary>
    private double _railRequested = -1;

    /// <summary>一页翻多远：这一栏看得见的那一段里放得下几张卡，就翻几张卡。</summary>
    private double RailStep => Infrastructure.CardStrip.StepFor(
        RailScroller.ViewportHeight > 0 ? RailScroller.ViewportHeight : RailScroller.ActualHeight,
        RailPitch,
        RailSpacing);

    /// <summary>
    /// 一张卡占多高（卡片高加一个间隔）。不量屏上那一张，理由同 <see cref="RevealFramed"/>：这一栏里的卡是同一个
    /// 模板，而那一排自己早就算出过卡片高（<see cref="CardShelf.RowHeight"/>）—— 翻到后面时头一张卡已经被虚拟化
    /// 掉，量它问到的是 0。
    /// </summary>
    private double RailPitch => ViewModel.Rail is { RowHeight: > 0 } rail ? rail.RowHeight + RailSpacing : 0;

    /// <summary>
    /// 翻一页并返回要到的位置。<paramref name="direction"/> 是 -1 或 1。
    /// <para>
    /// 在这一栏自己那个 <c>ScrollView</c> 里滚完，一句请求都不往外发（不用 <c>StartBringIntoView</c>）—— 同
    /// <see cref="RevealFramed"/>：那种请求会一路冒到整页那层竖着滚的 <c>ScrollView</c> 上，于是整页跟着滑一大段。
    /// </para>
    /// </summary>
    private double RailTurn(int direction)
    {
        var scrollable = RailScroller.ScrollableHeight;
        var target = Infrastructure.CardStrip.TargetFor(RailScroller.VerticalOffset, direction, RailStep, scrollable);

        _railRequested = target;

        // 没有余量就不去请求：翻不动的时候两条翻页条本来就收着，所以这一句只在一种情况下管事 —— 这一页还没量过
        // 的那一拍，而 ScrollTo 要的是模板里那个 ScrollPresenter。
        if (scrollable > 0) RailScroller.ScrollTo(RailScroller.HorizontalOffset, target);

        return target;
    }

    /// <summary>
    /// 哪一条翻页条该在屏上：规则同横带（<see cref="Infrastructure.CardStrip.ArrowsFor"/>）—— 一屏放得下就两条
    /// 都不要，到头的那一头也收起来，而且都得指针在这一栏上才浮出来。
    /// <para>
    /// 下沿那道渐隐（<c>RailFade</c>）跟着同一条规则的**下**那一半走，只是不看指针：它问的是「往下还翻得动吗」，
    /// 也就是这一栏的下沿此刻是不是把一张卡切在半路上。翻不动的时候最后一张卡是完整的，那时压一道渐隐就是白白
    /// 把它第二行字压暗。借的是同一个纯函数、按指针在栏上问，所以「什么时候还有下一页」这件事全栏只有一份答案。
    /// </para>
    /// </summary>
    private void SyncRailArrows()
    {
        var arrows = Infrastructure.CardStrip.ArrowsFor(
            RailScroller.VerticalOffset, RailScroller.ScrollableHeight, _railHover);

        RailPrev.Visibility = arrows.Prev ? Visibility.Visible : Visibility.Collapsed;
        RailNext.Visibility = arrows.Next ? Visibility.Visible : Visibility.Collapsed;

        var cut = Infrastructure.CardStrip.ArrowsFor(
            RailScroller.VerticalOffset, RailScroller.ScrollableHeight, hover: true).Next;

        RailFade.Visibility = cut ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRailPrevClicked(object sender, RoutedEventArgs e) => RailTurn(-1);

    private void OnRailNextClicked(object sender, RoutedEventArgs e) => RailTurn(1);

    /// <summary>
    /// 指针进到右栏。<c>PointerMoved</c> 也接到这里，同 <c>ShelfStrip.OnPointerEntered</c>：这一栏是数据到了之后
    /// 才长出卡片的，指针一直停在原处不动的话 <c>PointerEntered</c> 早就过去了。
    /// </summary>
    private void OnRailPointerEntered(object sender, PointerRoutedEventArgs e) => _railWatch.Enter();

    /// <summary>
    /// 指针离开右栏。位置要现查一遍，同 <c>ShelfStrip.OnPointerExited</c>：这个事件会从子元素冒上来，而指针踩到
    /// 刚浮出来的翻页条上算「离开了底下那张卡」，直接信它就会把手底下的按钮收掉。
    /// </summary>
    private void OnRailPointerExited(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Rail).Position;

        if (point.X >= 0 && point.Y >= 0 && point.X < Rail.ActualWidth && point.Y < Rail.ActualHeight) return;

        _railWatch.Leave();
    }

    private void OnRailViewChanged(ScrollView sender, object e) => SyncRailArrows();

    /// <summary>
    /// 这一栏或者它装的那一叠卡尺寸变了：还能不能翻要重算 —— 窗口高了之后可能已经一屏放得下，那时两条翻页条都
    /// 该收起来。卡片刚长出来的那一刻也是这里。
    /// </summary>
    private void OnRailResized(object sender, SizeChangedEventArgs e) => SyncRailArrows();

    /// <summary>
    /// 工具：把右栏那两条翻页条摆出来留着（<c>--show-rail</c>），交回一句能进日志的话。
    /// <para>
    /// 摆的是「指针在这一栏上」这个状态本身，剩下的照旧交给规则 —— 所以摆完哪一条在屏上仍然是
    /// <see cref="Infrastructure.CardStrip.ArrowsFor"/> 说的（一屏放得下就两条都不浮，停在头上就只浮下面那条）。
    /// 真指针之后进出这一栏一次就会把它收掉，见 <see cref="_railWatch"/>。
    /// </para>
    /// </summary>
    internal string RevealRailPager()
    {
        if (Rail.Visibility != Visibility.Visible) return "右栏不在屏上（继续观看空着或者被勾掉了）";

        _railHover = true;
        SyncRailArrows();

        var up = RailPrev.Visibility == Visibility.Visible;
        var down = RailNext.Visibility == Visibility.Visible;

        return up || down
            ? $"翻页条摆出来了：上一页{(up ? "在" : "不在")}、下一页{(down ? "在" : "不在")}"
                + $"（这一栏滚在 {RailScroller.VerticalOffset:0}，还能滚 {RailScroller.ScrollableHeight:0}）"
            : $"没有可翻的余量，两条都不该在（还能滚 {RailScroller.ScrollableHeight:0}）";
    }

    /// <summary>
    /// 自检：右栏真的框出了台上那一张 —— 这一条在报告里只有一句话，因为它在屏上是一圈线，而截图看得见。
    /// </summary>
    internal string FrameSummary
    {
        get
        {
            if (ViewModel.Rail is not { } rail) return "没有右栏";
            if (Banner.Current is not { } slide) return "台上没有幻灯片";

            var framed = rail.Cards.Count(card => card.Framed);

            return _framed < 0
                ? $"「{slide.Title}」在右栏里没有对应的条目，一张都没框（右栏 {rail.Cards.Count} 项）"
                : $"「{slide.Title}」对上右栏第 {_framed + 1} 张，框着 {framed} 张"
                    + $"（右栏 {rail.Cards.Count} 项，滚到 {RailScroller.VerticalOffset:0}）";
        }
    }

    /// <summary>
    /// 右栏那一列的卡片站在和大图同一支深底上（<c>EgBannerBaseBrush</c>），所以底下那两行字要走压在图上那套不随
    /// 主题走的浅墨 —— 同 <see cref="ShelfStrip"/> 替自己那一排卡片做的事。
    /// <para>
    /// 从容器的 <c>Content</c> 往下拿，不走可视树：刚建出来的容器还没套上自己的模板，那一刻它在可视树里一个孩子
    /// 都没有（这一脚 <c>ShelfStrip.PaintInk</c> 已经踩过一次，症状是浅色主题下除第一张之外每张卡的片名整行消失）。
    /// </para>
    /// </summary>
    private void OnRailElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is ContentControl { Content: PosterCard card }) card.OnScrim = true;
    }

    /// <summary>
    /// 自检：右栏那一列是「点一下翻一屏」，不是「拖那条滚动条」（「把继续观看改成点击翻页的」，2026-09-05）。
    /// <para>
    /// 判五件事：滚动条真藏着（改回 <c>Auto</c> 的话屏上又多一条可拖的东西，而别的读数一个都不会变）、标记里那个
    /// 间隔和常数 <see cref="RailSpacing"/> 是同一个数（步长按它算，两处错开一档就是每翻一页多出或少掉一条缝）、
    /// **悬停时翻页条真浮得出来、不悬停时两条都收着**、点一下真的要到规则说的那个位置，以及那张悬停网量得到自己
    /// 的矩形（<see cref="HoverWatch.Probe"/> —— 量不到的话「鼠标移出窗口后不会自动恢复」在这一栏上就又回来了）。
    /// </para>
    /// <para>
    /// 悬停那两档是**摆出来的，不是等现场**：自检跑的时候真鼠标可能正停在这一栏上，那时翻页条本该在屏上，拿现场
    /// 当判据就会把它读成回归 —— 「鼠标真等两秒就藏」那一关的教训。所以这里把 <see cref="_railHover"/> 两档各摆
    /// 一次，读完按现场的值重新算一遍。
    /// </para>
    /// <para>
    /// 整页有没有跟着滑只报不判：翻页走的是这一栏自己那个 <c>ScrollView</c> 的 <c>ScrollTo</c>，它冒不到外面去，
    /// 而真要验「异步冒上去的那种请求」得隔一拍再量（见 <see cref="StayFocus"/>）—— 那条路由「点卡片不挪页」
    /// 那一关盯着，这里不重复一遍。翻完把这一栏放回原处（不带动画），后面还有截图和别的读数要用它。
    /// </para>
    /// </summary>
    internal (bool? Ok, string Detail) RailPageRead()
    {
        if (Rail.Visibility != Visibility.Visible || ViewModel.Rail is not { } rail)
            return (null, "没有右栏（继续观看空着或者被勾掉了），翻页这一读跳过");

        var bar = RailScroller.VerticalScrollBarVisibility;
        var spacing = RailRepeater.Layout is StackLayout stack ? stack.Spacing : double.NaN;
        var hidden = bar == ScrollingScrollBarVisibility.Hidden;
        var paced = Math.Abs(spacing - RailSpacing) < 0.01;

        var viewport = RailScroller.ViewportHeight;
        var scrollable = RailScroller.ScrollableHeight;
        var pitch = RailPitch;
        var step = RailStep;
        var perPage = pitch > 0 ? step / pitch : 0;
        var from = RailScroller.VerticalOffset;
        var pageFrom = Scroller.VerticalOffset;
        var live = _railHover;

        // 悬停两档各摆一次。翻得动的时候至少要浮出一条（哪一条由规则定，那是 CardStrip 的事），不悬停时两条都收。
        _railHover = true;
        SyncRailArrows();
        var shown = RailPrev.Visibility == Visibility.Visible || RailNext.Visibility == Visibility.Visible;
        var reveals = scrollable > Infrastructure.CardStrip.Edge ? shown : !shown;

        _railHover = false;
        SyncRailArrows();
        var quiet = RailPrev.Visibility != Visibility.Visible && RailNext.Visibility != Visibility.Visible;

        // 点一下：走按钮上挂的那条路，不是在这里另写一遍算术。
        var wanted = Infrastructure.CardStrip.TargetFor(from, 1, step, scrollable);
        OnRailNextClicked(RailNext, new RoutedEventArgs());
        var asked = Math.Abs(_railRequested - wanted) < 0.5;

        var moved = Math.Abs(Scroller.VerticalOffset - pageFrom) < 0.5;
        var watch = _railWatch.Probe();

        // 收场：这一栏放回原处，悬停那一档按现场重算。
        RailScroller.ScrollTo(
            RailScroller.HorizontalOffset, from, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled));
        _railHover = live;
        SyncRailArrows();

        return (hidden && paced && reveals && quiet && asked && watch,
            $"右栏 {rail.Cards.Count} 项：看得见 {viewport:0}、还能滚 {scrollable:0}，一张卡连间隔 {pitch:0}"
                + $"，一页 {perPage:0} 张（{step:0}）；"
                + $"滚动条{(hidden ? "藏着" : $"是 {bar}，没藏")}；"
                + $"间隔 {spacing:0}{(paced ? "＝常数" : $"，和常数 {RailSpacing:0} 对不上")}；"
                + (reveals
                    ? (scrollable > Infrastructure.CardStrip.Edge ? "悬停时翻页条浮出来" : "一屏放得下，悬停也不浮")
                    : "悬停那一档不对")
                + (quiet ? "、不悬停时两条都收着" : "、不悬停时却还露着") + "；"
                + $"点一下要到 {_railRequested:0}（规则说 {wanted:0}）{(asked ? "" : "，对不上")}；"
                + $"这一栏 {from:0}→{RailScroller.VerticalOffset:0}，整页停在 {Scroller.VerticalOffset:0}"
                + $"（同一拍读的，只作诊断{(moved ? "" : "：整页动了")}）；"
                + $"指针那一问：{(watch ? "量得到这一栏的矩形" : "量不到，悬停网是空跑")}"
                + $"（现场指针{(live ? "在" : "不在")}这一栏上）");
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
        // 挑一张横带里的卡：这一条问的是「横带会不会替卡片要一次 BringIntoView，把整页拽下去」，而第一屏右边那
        // 一栏不是横带 —— 它自己那个 ScrollView 里没有 CardStrip.RevealFor 这条路，拿它来问等于换了个题目。
        // 横排整个在第一屏外面，所以可能一张都还没渲染出来，那时退回第一张（右栏那一列的头一张）。
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

        SyncRail();
        SyncBleed();

        // 标题栏那几颗按钮要知道自己站在哪种底上。现在说一遍（回到这一页时那些幻灯片可能已经在手上了），
        // 之后每次那一块从「一张图」变成「页面的底色」或者反过来时再说一遍。
        ViewModel.PropertyChanged += OnViewModelChanged;
        _actions.SetTitleStrip(Strip());

        _ = ViewModel.ReloadAsync();
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(HomeViewModel.HeroFilled))
        {
            _actions?.SetTitleStrip(Strip());

            // 顶上那一块从「一张图」变成「页面的底色」或者反过来：标题栏那 32 像素跟着顶开或者还回去。
            SyncBleed();
        }

        // 右栏是从「有没有继续观看」加「轮播开没开」来的，所以它的宽度要跟着这一句走 —— 勾掉继续观看、或者关掉
        // 轮播之后那一栏该真的没了。换了一批卡之后还要照台上那张重新框一次：上一批那一张的框跟着上一批走了。
        else if (args.PropertyName == nameof(HomeViewModel.Rail))
        {
            SyncRail();
            SyncFrame(Banner.Current?.Item);

            // 换了一批卡，可翻的余量也跟着变（少到一屏放得下就该两条都收起来）。那一叠卡的高度真变了的话
            // OnRailResized 也会说一声，这一句管的是「张数变了而总高刚好没变」那一档。
            SyncRailArrows();
        }
    }

    /// <summary>这一页顶上那一块：那条大图铺到窗口顶边就是一张剧照，没有幻灯片时是页面自己的底色。</summary>
    private TitleStrip Strip() => ViewModel.HeroFilled ? TitleStrip.OnScrim : TitleStrip.Plain;

    public void Release()
    {
        ViewModel.PropertyChanged -= OnViewModelChanged;
        ShellPrefs.Changed -= OnShellPrefsChanged;

        // 这一页离开了，指针在不在右栏上已经无所谓 —— 留着的话十赫兹那一拍还会继续问一个量不到的矩形（同
        // ShelfStrip 的 Unloaded）。
        _railWatch.Leave();

        ViewModel.Cancel();
    }

    /// <summary>
    /// 设置里那份主页版面改了（拖拽排序或者勾选），照新的重排一遍。卡片尺寸那几个数不在这一句里 —— 它们是
    /// <see cref="HomeViewModel.Attach"/> 时的快照，下次开这一页才换（见那一段说明）。
    /// </summary>
    private void OnShellPrefsChanged(Configuration.UiSettings ui) => _ = ViewModel.ApplyLayoutAsync();

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        // 走出这一页，顶上那一块就是别的页面自己的底色了 —— 那几颗按钮得把墨还回来。
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

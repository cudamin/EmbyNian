using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Infrastructure;
using EmbyNian.Services;
using EmbyNian.Shell.Platform;
using EmbyNian.Shell.ViewModels;
using EmbyNian.Theming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Navigation;
using System.ComponentModel;
using Windows.Foundation;
using Windows.UI;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 电影、剧集、季和单集共用的详情页。All of it is <see cref="DetailViewModel"/>; what is left here is the
/// five things only a laid-out page can answer.
/// <para>
/// Those five: whether four lines were enough for 简介 (<c>IsTextTrimmed</c>, which depends on the width
/// the text was actually given), how tall this page's visible area is (the hero fills it), which element
/// the 更多 menu hangs off, which card a click came from — the sender is a laid-out <c>Button</c> and the
/// card is its content — and the context menu itself, which needs the <c>ContextRequestedEventArgs</c>
/// the shell hands over.
/// </para>
/// </summary>
public sealed partial class DetailPage : Page, IShellContent
{
    private const string Category = "详情";

    private DetailRequest? _request;

    /// <summary>
    /// Resolved on navigation, for the one thing this page cannot ask the view model for: the two card
    /// menus below open an item, and <see cref="ItemCommands"/> takes the session that does it.
    /// </summary>
    private EmbySession? _session;
    private IShellActions? _actions;
    private EmbyImageStore? _images;

    /// <summary>
    /// 外壳在窗口建好后递进来的那一个（<see cref="AttachWindow"/>）：纸面上沿那条线跟着显示器走
    /// （<see cref="DetailHero.PaperLineFor"/>），而「这台显示器多大」只有它知道。每次导航都是新实例，
    /// 离开可视树时解绑（见 Unloaded）—— 不解绑就是把旧窗口拴到下辈子。
    /// </summary>
    private Windowing.HostWindow? _window;

    /// <summary>标题栏那一条现在跟着头图罩子压到多浓，见 <see cref="PaintWash"/>。-1 是「还没画过一次」。</summary>
    private double _wash = -1;

    /// <summary>正文纸面从标题栏底边往上盖了多少，0..1。和 <see cref="_wash"/> 分开，黑色 HeroTail 正好夹在两者之间。</summary>
    private double _paperCover = -1;

    /// <summary>标题栏当前画的是空、罩子、黑色尾部、纸面边界还是整张纸；给滚动时的 1/255 级去重用。</summary>
    private int _paintPhase = -1;

    /// <summary>标题栏那一条算不算已经由正文纸面接管 —— 回差的另一半，见 <see cref="DetailHero.WashedOver"/>。</summary>
    private bool _washedInk;

    /// <summary>
    /// 洗那一层用的画刷，一支重复用。停点的颜色随滚动改，所以它不能是 XAML 里那种一次成形的画刷；换成每次
    /// 新建一支也行，但那是每一帧滚动一次分配。
    /// </summary>
    private readonly LinearGradientBrush _washBrush = new()
    {
        StartPoint = new Point(0.5, 0),
        EndPoint = new Point(0.5, 1)
    };

    /// <summary>
    /// 正文纸面的上沿滚进标题栏时用的硬边渐变：上半仍是 HeroTail 的黑，下半已经是主题纸色。
    /// 重用同一支画刷，理由和 <see cref="_washBrush"/> 一样。
    /// </summary>
    private readonly LinearGradientBrush _paperBrush = new()
    {
        StartPoint = new Point(0.5, 0),
        EndPoint = new Point(0.5, 1)
    };

    public DetailPage()
    {
        // Constructed with the page rather than in OnNavigatedTo, so x:Bind never has a null root: the
        // bindings are evaluated by InitializeComponent below, and a view model that arrives afterwards
        // would leave every one of them showing nothing until the first property changed.
        ViewModel = new DetailViewModel();
        InitializeComponent();
        PaintScrim();

        // 上树之后把背景那一层顶到窗口的上沿一次。第一次量得到「这一页坐在工作区的哪儿」就是这时候 ——
        // 构造的时候页面还不在树上，量出来的是 0。
        // 换主题也要重画标题栏那一条上的洗：洗的是正文那张纸的颜色，而 ThemeHost 换主题改的是那支画刷自己的
        // 颜色 —— 停点上的颜色是画那一刻抄下来的，不跟着改。挂在 Loaded 上而不是构造里：这一页的实例会被复用，
        // 挂在构造里的话第二次上树时那份订阅已经在 Unloaded 里解掉了。先减再加，重复上树也只挂一支。
        Loaded += (_, _) =>
        {
            LiftBackdrop();
            ThemeHost.Changed -= OnThemeChanged;
            ThemeHost.Changed += OnThemeChanged;
        };

        // Disposed rather than only cancelled, which the other pages do not need to do: this view model
        // owns a second cancellation source for the artwork, and Dispose is the one call that lets go of
        // both. It is idempotent and leaves the view model reusable, so an unload followed by a fresh
        // navigation is fine.
        Unloaded += (_, _) =>
        {
            ThemeHost.Changed -= OnThemeChanged;
            if (_window is not null)
            {
                _window.GeometryChanged -= OnWindowGeometryChanged;
                _window = null;
            }
            ViewModel.Dispose();
        };
    }

    /// <summary>换了主题：正文那张纸换了颜色，标题栏那一条洗的就是那个颜色。见 <see cref="PaintWash"/>。</summary>
    private void OnThemeChanged(UiTheme theme) => PaintWash(force: true);

    /// <summary>
    /// 把头图底下那道罩子画上 —— 高、五个停点、还有底下那块尾部的底色，全从 <see cref="DetailHero"/> 那张表来。
    /// <para>
    /// 这些数从前在 DetailPage.xaml 里另写了一份：五个 <c>GradientStop</c>、一个 <c>Height="440"</c>、一个
    /// <c>Background="#B80C0E11"</c>，而算得出的那一份（<see cref="DetailHero.TopWash"/>，标题条上那层洗照着
    /// 它算）在 Core 里。两份数没有谁管着谁，改一份忘一份的下场是标题条上重新长出那道横缝，而两边看着都「对」。
    /// 现在只有 Core 那一份，屏上这一份由它生成。
    /// </para>
    /// <para>
    /// 在构造里画而不是在 <c>Loaded</c> 里：这一页的实例会被复用，而这些数一辈子不变，画一次就够；上树之后才画
    /// 的话第一帧是一格没有罩子的亮画面，压在上面的白字那一帧读不出来。
    /// </para>
    /// <para>
    /// 不跟主题走，所以也不挂 <see cref="ThemeHost.Changed"/>：这道罩子压的是一张剧照，剧照不会因为用户挑了
    /// 浅色主题就变亮（同 <see cref="DetailHero.ScrimInk"/> 上那一段）。
    /// </para>
    /// </summary>
    private void PaintScrim()
    {
        var ink = DetailHero.ScrimInk;

        // 高不在这里设：它跟着带子走（集页那一格按里面那一叠实测给，比 460 矮），所以绑在 ViewModel.ScrimHeight
        // 上。停点是比例，收窄不改它们，所以这五个仍然画一次就够。
        HeroScrimBrush.GradientStops.Clear();
        foreach (var (along, alpha) in DetailHero.ScrimStops)
            HeroScrimBrush.GradientStops.Add(new GradientStop
            {
                Offset = along,
                Color = Color.FromArgb(alpha, ink.R, ink.G, ink.B)
            });

        // 尾部接的是罩子的末档 —— 同一个 alpha 同一个色，否则两块之间横着一道明暗接缝（自检里「尾部接住头图
        // 末色」读的就是这个）。取表上最后那一档而不是 ScrimCeiling：表要是哪天不以最浓收尾，这里跟着走。
        // 集页那块板上这一层不画（HeroCardShown）：那一段的底由 HeroCardBottom 给，再压一层近乎同色的黑白费，
        // 而且它会把板涂回罩子的末色 —— 板上那几块小面板的底边就对不上了。
        HeroTail.Background = ViewModel.HeroCardShown
            ? null
            : new SolidColorBrush(HeroScrimBrush.GradientStops[^1].Color);
    }

    internal DetailViewModel ViewModel { get; }

    public object? NavigationRequest => _request;

    /// <summary>
    /// Whether the page has finished its first load. Read by the startup self-check, which waits for it
    /// before it reads anything off the page.
    /// </summary>
    internal bool IsReady => ViewModel.IsReady;

    /// <summary>
    /// How many rows 单集 and how many faces 演职人员 really drew, counted off the visual tree rather than
    /// off the two shelves. The same distinction the servers and diagnostics pages count for, and here it is
    /// worth twice as much: these two use different <c>DataTemplate</c>s, both of them markup no build runs.
    /// A key that does not resolve inside either one throws when an item is realised and not before.
    /// <para>
    /// 单集 is whichever of its two shapes this page kind shows, so this adds them: the question here is only
    /// 「did the template draw」, and <see cref="EpisodeShapes"/> is the one that asks which shape drew.
    /// </para>
    /// </summary>
    internal (int Episodes, int Cast) Realised
    {
        get
        {
            var (rows, cards) = EpisodeShapes;
            return (rows + cards, Count(CastRow));
        }
    }

    /// <summary>
    /// 单集 split by shape: rows off the vertical list, cards off the horizontal strip. Exactly one of the two
    /// containers is visible — 季 pages list, 剧 and 集 pages page through a strip — and an
    /// <c>ItemsRepeater</c> inside a collapsed parent realises nothing, so the shape that is hidden reads 0.
    /// That is what makes 「the right shape drew」 checkable from outside: this page kind's own type decides
    /// which, and the pair says which one actually happened.
    /// </summary>
    internal (int Rows, int Cards) EpisodeShapes => (Count(EpisodeList), Count(EpisodeStrip));

    /// <summary>
    /// 自检：集页的第一屏 —— 那一叠字和键不能被带子挤出去、带子也不许比那一叠高（「集拉大窗口后会导致左上角
    /// 空空的」），音轨那一行和剧情说明要落在这一页看得见的那一段里、同季那一带集至少露出头，而那一带排在
    /// 剧情说明底下（「把集页面的剧情说明和集列表位置调换」）。
    /// <para>
    /// 次序和「谁该在屏里」是一起翻的：上一版是 音轨 → 集带 → 剧情说明，那时候钉的是「集带整块在屏里、剧情说明
    /// 露头」；这一版两块换了位置，于是这两句也跟着换 —— 现在进页面第一眼是「这一集讲什么」，那一带集是最后一块，
    /// 露出头就够。
    /// </para>
    /// <para>
    /// 比的是 <c>Body</c> 自己的下沿，不是窗口的下沿 —— 头上还压着标题栏和面包屑那两行。窗口连「带子加尾部」都
    /// 装不下的时候，落在屏里那几条只报不判：带子已经是它里面那一叠量出来的高（见
    /// <see cref="DetailHero.EpisodeHeight"/>），再没有可让的地方，那是「窗口太矮」而不是版面算错了。
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) FirstScreenRead()
    {
        if (XamlRoot?.Content is not UIElement root) return (false, "页面还没上树");

        var hero = Box(HeroBand);
        var page = Box(Body);
        var stack = Box(HeroStack);
        // 那排键此刻在哪一份：集页的宽版式在片名那一栏的叠里（ColumnActions，跟文字一起流），其余页面在
        // 带子的独立一行上（HeroActions）。两份只有一份在屏上 —— 读收着的那份只会量到 0，什么也钉不住。
        var actionsEl = ViewModel.HeroCardShown ? (FrameworkElement)ColumnActions : HeroActions;
        var actions = Box(actionsEl);
        var tail = Box(HeroTail);

        // 「这一格该多高」的两个出处：宽版式按内容量出来的那一叠（集页那一格的高就是它）；紧凑版式分两档 ——
        // 有画面可铺的时候带子就是背景那条等比画面（DetailHero.CompactHeight —— 页宽乘图的高宽比），它比字键
        // 那一叠高出来的部分是画面，不是空白，拿宽版式那笔账来比就是每一张窄页都「带子比内容高」；<em>没有
        // 画面可铺</em>的时候带子就是那一叠的高（DetailHero.EpisodeHeight），于是它同样要判「不多留空」
        // —— 集页在紧凑版式下永远没有画面可铺，而「窄窗口时候上方有大片空位」（2026-09-12）说的就是那一档
        // 从前写死 412 的样子。
        var strip = ViewModel.IsCompact && ViewModel.HeroArt;
        var room = ViewModel.IsCompact ? ViewModel.HeroRoom : Math.Max(ViewModel.HeroRoom, DetailHero.EpisodeFloor);
        var roomy = hero.Height + tail.Height <= page.Height + 0.5;

        // 一头一条，合起来是「带子正好那一叠那么高」：那一叠从这一格的上沿往下排、那排键在它底下那一行里，
        // 所以收窄过头两头都读得出来；而带子不许比那一叠还高 —— 高出来那一截全落在那一叠底下，也就是
        // 「左上角空空的」。两条都无条件判：带高跟窗口有多大没关系（见 DetailHero.EpisodeHeight）。
        // 紧凑版式里那排键整个不画（它的活交给 CompactBlock），第二条随之免读 —— 量一个收着的元素没有读数。
        var fits = stack.Top >= hero.Top - 0.5 && (ViewModel.IsCompact || actions.Bottom <= hero.Bottom + 0.5);
        var snug = strip || hero.Height <= room + 0.5;
        var read = new List<string>();
        var ok = fits && snug;

        foreach (var (name, panel) in new (string, FrameworkElement)[]
        {
            ("音轨", PickerSet.Panel),
            ("剧情说明", OverviewPanel),
            ("集带", EpisodePanel)
        })
        {
            if (panel.Visibility != Visibility.Visible)
            {
                read.Add($"{name}这一条目没有");
                continue;
            }

            var box = Box(panel);

            // 集带只要求「露头」：它现在是这一段的最后一块，整带两百来高，加上前面三块在矮窗口上凑不出一屏，
            // 而这一条问的是「进页面看不看得见它」—— 见得到开头就能往下读，整块都在屏外才是坏的。
            var inside = name == "集带"
                ? box.Top <= page.Bottom - 24
                : box.Bottom <= page.Bottom + 0.5;

            ok &= inside || !roomy;
            read.Add($"{name} {box.Top:0}–{box.Bottom:0}{(inside ? "" : "（出屏）")}");
        }

        // 集带那两句话：它排在音轨和剧情说明底下（纸的第一块），而且带着样式那一圈框 —— 那是「剧页面那种
        // 样式」（他 2026-09-12 先要框，随后「集的框要和上面分开」把上面那两块内层的框撤掉、这一圈留下）。
        // 同 BodySeal 里那两块读的 Rings。
        if (EpisodePanel.Visibility == Visibility.Visible)
        {
            var picker = PickerSet.Panel;
            var order = Box(EpisodePanel).Top >= Box(picker).Bottom - 0.5
                || picker.Visibility != Visibility.Visible;
            var below = OverviewPanel.Visibility != Visibility.Visible
                || Box(EpisodePanel).Top >= Box(OverviewPanel).Bottom - 0.5;
            var framed = Rings(EpisodePanel);

            ok &= order && below && framed;
            read.Add(order && below ? "集带排在音轨和剧情说明底下" : "集带没排在音轨和剧情说明底下");
            read.Add(framed ? "集带带着框" : "集带外圈没了");
        }

        return (ok, $"带高 {hero.Height:0}（内容量出来 {room:0}、其中字键那一叠 {stack.Height:0}）、"
            + $"尾部 {tail.Height:0}、可视段到 {page.Bottom:0}；{string.Join("、", read)}"
            + (fits ? "" : $"，那一叠 {stack.Top:0}–{actions.Bottom:0} 被挤出带子 {hero.Top:0}–{hero.Bottom:0}")
            + (snug ? "" : $"，带子比内容高出 {hero.Height - room:0}（左上角就空这么多）")
            + (roomy ? "" : "，窗口连带子加尾部都装不下（只报不判）"));

        Rect Box(FrameworkElement element) => element
            .TransformToVisual(root)
            .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
    }

    /// <summary>
    /// 自检：从播放回来那一趟走完之后，这一页还是刚打开的样子 —— 「点击开始播放后点击左上方的返回，集列表会跑到
    /// 下方去」。
    /// <para>
    /// 停止播放走的是三步：视图模型先喊 <c>RefreshRequested</c>（外壳照着重新导航一遍，见
    /// <c>ShellPage.RefreshActive</c>），再落 <c>LeavePlayer</c> 把导航外壳放回来。也就是说<em>那一次重新导航发生在
    /// 外壳还收着的时候</em>，这一页于是在一棵量不到尺寸的树上走完了 <c>OnNavigatedTo</c> —— 而正常那一路
    /// （点一张卡片进来）外壳一直是显着的。两条路上唯一不同的就是这个，所以这一条读的是那一趟走完之后的样子。
    /// </para>
    /// <para>
    /// 两句话，各对一种屏上看得见、别的读数一条都不会响的坏法。那一带集得是正文那张纸的第一块
    /// （<see cref="PlaceEpisodes"/>）：它掉到纸的最后、也就是被撑满第一屏的尾部之后时，屏上就是「集列表跑到下方
    /// 去了」。页面没有自己滚下去：滚下去那一版头图那一叠字被切在视口上沿外面，而屏上看着像「这一页的头图怎么
    /// 没了」。
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) ReturnRead()
    {
        var host = BodySheet.Children.IndexOf(EpisodePanel);
        var where = host == 0 ? "纸上第一块" : host > 0 ? $"纸上第 {host + 1} 块" : "纸上没有它";
        var offset = Body.VerticalOffset;
        var placed = host == 0;
        var top = offset < 1;

        var at = Body.Content is UIElement content
            ? EpisodePanel.TransformToVisual(content).TransformPoint(new Point(0, 0)).Y
            : double.NaN;

        return (placed && top,
            $"{EmbyItemType.ToChinese(ViewModel.ItemType)}页，那一带集在{where}、"
                + $"从内容 {at:0} 起；带高 {ViewModel.HeroHeight:0}、尾部 {HeroTail.ActualHeight:0}"
                + $"（下限 {ViewModel.TailMinHeight:0}）、纸面上沿 {PaperOffset():0}、视口 {ViewModel.Viewport:0}、"
                + $"页面滚在 {offset:0}"
                + (placed ? "" : "，集带没排在纸的第一块")
                + (top ? "" : "，页面自己滚下去了"));
    }

    /// <summary>
    /// 自检：集页上那行剧名按下去落在哪儿 —— 「点击剧名之后应该进[入]剧页面而不是季页面，季页面只能通过[剧页面上
    /// 「全部剧季」那一格]进入」。
    /// <para>
    /// 读的是屏上这颗按钮和它背后那个落点，不是 <see cref="ItemDetail.TitleTarget"/> 本身（那一头有单测）：这一条
    /// 要的是「服务器这一次真给了剧的 id、按钮真接上了、提示真说的是那部剧」。落点写成季的那一版屏上一样好看，
    /// 单测过不了它 —— 可反过来，命令没绑上、按钮被样式停用、剧名那一行根本不是这颗按钮，单测一个都拦不住。
    /// </para>
    /// <para>
    /// 集页之外这颗按钮该是停用的：那一行字说的就是本页自己（<c>DetailViewModel.CanOpenTitle</c>）。整条链上
    /// 唯一不判的是「这一集连剧的 id 都没有」—— 那是服务器给的答案缺一块，不是版面错了，所以只报。
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) TitleLinkRead()
    {
        var episode = ViewModel.ItemType == EmbyItemType.Episode;
        var link = ViewModel.TitleLink;
        var kind = link is null ? "没有落点" : $"{EmbyItemType.ToChinese(link.Type)}「{link.Name}」";
        var tip = ViewModel.TitleTip ?? "没有提示";
        var live = TitleButton.IsEnabled;

        if (!episode)
            return (link is null && !live,
                $"{ViewModel.ItemType} 页，剧名那一行说的就是本页自己：落点 {kind}、按钮{(live ? "还点得动" : "点不动")}");

        if (link is null)
            return (true, $"单集页，这一集没带剧的 id，剧名那一行点不动（只报不判）；按钮{(live ? "却还点得动" : "确实点不动")}");

        var ok = link.Type == EmbyItemType.Series && live;

        return (ok, $"单集页，剧名那一行落在 {kind}、按钮{(live ? "点得动" : "点不动")}、提示「{tip}」"
            + (link.Type == EmbyItemType.Series ? "" : "；落点该是剧而不是季（季只从剧页面那格「全部剧季」进）"));
    }

    /// <summary>
    /// 自检：片名那一行在不在。单独拎出来，是因为它换过形状 —— 最早徽标和片名是一对反的 <c>Visibility</c>（图到了
    /// 就顶掉文字标题），于是「头图上没有名字」和「一张没取到的图」在截图里长得一模一样。现在片名无条件在，两张
    /// 图各是可有可无的记号。
    /// </summary>
    internal bool TitleDrawn => TitleText.Visibility == Visibility.Visible;

    /// <summary>
    /// 自检：片名头上那一枚徽标 —— 「统一改为在剧名上方显示徽标」。画了没有、画多大、真落在片名的上方而且左沿跟
    /// 它对齐，以及它没顶出带子、没压到海报。
    /// <para>
    /// 读元素而不是读视图模型，因为这一条的说法就是关于标记本身的，而它换过三个位置：顶替文字标题 → 集页右上角、
    /// 别的页面左上角（那时候艺术图占先） → 四种页面一律片名上方。「记号压在字上」在截图里也像一张缺了的图，所以
    /// 这几何得有人读。
    /// </para>
    /// <para>
    /// 「没顶出带子」是这一版最要紧的那一句：这一枚站在那一栏字里，而电影、剧、季三页的带高写死 412 —— 那一栏撑过
    /// 让给它的 360 就从带子的上沿溢出去，屏上是徽标压在面包屑那一行上。集页反过来是带子跟着那一栏长高（那一页的
    /// 高按实测给），所以那一头不会溢，会溢的正是写死高度的这三页。
    /// </para>
    /// </summary>
    internal (bool Drawn, double Width, double Height, bool Placed, string Where) PlateShape
    {
        get
        {
            // 没东西可摆、也没东西可压：服务器两种名牌都不给的条目是常态，那一次这句话是空的，不是没过。
            if (TitlePlate.Visibility != Visibility.Visible)
                return (false, TitlePlate.ActualWidth, TitlePlate.ActualHeight, true, "剧名上方没画");

            var plate = BandBox(TitlePlate);
            var title = BandBox(TitleText);
            var still = BandBox(PosterStill);

            // 在片名的上方、左沿跟它对齐：这一枚和那行字是一组，错开一两像素屏上就是「歪了」。
            var above = plate.Bottom <= title.Top + 0.5;
            var aligned = Math.Abs(plate.Left - title.Left) <= 1.5;

            var inside = plate.Top >= -0.5 && plate.Bottom <= HeroBand.ActualHeight + 0.5;
            var clearOfStill = PosterStill.Visibility != Visibility.Visible || Apart(plate, still);

            return (true, TitlePlate.ActualWidth, TitlePlate.ActualHeight,
                above && aligned && inside && clearOfStill,
                $"徽标 {plate.Left:0},{plate.Top:0} 到 {plate.Right:0},{plate.Bottom:0}、"
                    + $"片名 {title.Left:0},{title.Top:0} 到 {title.Right:0},{title.Bottom:0}"
                    + $"（带 {HeroBand.ActualWidth:0}×{HeroBand.ActualHeight:0}，该在剧名上方）"
                    + (above ? "" : "，没在片名上方")
                    + (aligned ? "" : "，左沿没跟片名对齐")
                    + (inside ? "" : "，顶出了带子")
                    + (clearOfStill ? "" : "，压到海报了"));
        }
    }

    /// <summary>
    /// 把一个元素的盒子换算到头图那一格自己的坐标里 —— 报告里那几个数就是「在这张图上指哪儿」。
    /// </summary>
    private Rect BandBox(FrameworkElement element) => element
        .TransformToVisual(HeroBand)
        .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    /// <summary>
    /// 自检：片名左边那张海报一个像素都没裁 —— 「海报下方会被裁切，要能看到完整的海报」。
    /// <para>
    /// 三句话：那一格的形状和这张位图自己的形状一样（所以图整张画得下，四周不留空隙）、拉伸方式是
    /// <c>Uniform</c>（这是唯一一处「改回去就悄悄开始裁」的开关），以及那一格真有大小。前一句是这次改动本身
    /// （从前那一格写死 210×300 也就是 0.7:1，而海报是 2:3，于是上下各裁掉七八像素 —— 而海报底下那一条往往正是
    /// 片名和演员表），后一句是它的保险。
    /// </para>
    /// <para>
    /// 比的是解出来那张位图的像素尺寸，不是服务器给的那个比例：屏上画的是这一张，而它可能压根不是海报（没有海报
    /// 的条目退到缩略图，那是一张 16:9 的图）。两个数不一样的时候，对得上屏幕的是位图那一份。
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) StillShape()
    {
        // 图还没解出来的那一拍这一格整个是收着的（StillVisibility），这时候「没裁」是句空话，不是坏事。
        if (PosterStill.Visibility != Visibility.Visible) return (true, "海报还没解出来，这一格收着");

        if (PosterImage.Source is not BitmapImage { PixelWidth: > 0, PixelHeight: > 0 } bitmap)
            return (false, "这一格显着，可里面装的不是一张解好的位图");

        var width = PosterStill.ActualWidth;
        var height = PosterStill.ActualHeight;

        if (width <= 0 || height <= 0) return (false, $"这一格量出来是 {width:0}×{height:0}");

        // Uniform 之下图按短边装进这一格，另一边留出空隙 —— 那点空隙就是从前被裁掉的那一部分。取整差的半像素
        // 允许留着，再多就是这一格和这张图不同形。
        var scale = Math.Min(width / bitmap.PixelWidth, height / bitmap.PixelHeight);
        var slackWidth = width - (bitmap.PixelWidth * scale);
        var slackHeight = height - (bitmap.PixelHeight * scale);
        var snug = slackWidth <= 1 && slackHeight <= 1;
        var uniform = PosterImage.Stretch == Stretch.Uniform;

        return (snug && uniform,
            $"这一格 {width:0}×{height:0}、原图 {bitmap.PixelWidth}×{bitmap.PixelHeight}"
                + $"（{(double)bitmap.PixelWidth / bitmap.PixelHeight:0.000}:1）、"
                + $"空隙 {slackWidth:0.#}×{slackHeight:0.#}、{PosterImage.Stretch}"
                + (snug ? "" : "，这一格和这张图不同形（图缩在里面或者被裁了）")
                + (uniform ? "" : "，拉伸方式不是 Uniform（会裁）"));
    }

    /// <summary>
    /// 两个矩形不相交。问的是盒子而不是「谁在谁下面」：这几块靠的是栏而不是行，一块可能比另一块的下沿还高却
    /// 挨不着它（集页上两行片名之于右上角那枚记号）。
    /// </summary>
    private static bool Apart(Rect one, Rect other) => one.Right <= other.Left || other.Right <= one.Left
        || one.Bottom <= other.Top || other.Bottom <= one.Top;

    /// <summary>
    /// 自检：头图上那两行字真解析到的字体和字号 —— 片名和那行读数。
    /// <para>
    /// 值得单独读一遍，是因为这两行的字全交给样式给，而样式漏一个 <c>Setter</c> 属于屏上看得见、截图里
    /// 看不出的坏法：这一版之前的片名写死了 34 和 SemiBold 却没写字体，于是整页最大的一块字用的是继承
    /// 来的正文字。比的是元素真解析到的族名和它该有的那个键，所以 Bahnschrift 装没装都不影响这条的判断
    /// —— 那件事由 字体已解析 那条管。
    /// </para>
    /// <para>
    /// 片名那一档从 <c>EgDisplayFontSize</c>（52，轮播那一支）落到 <c>EgHeaderFontSize</c>（36，页面标题那
    /// 一档）之后，这一条跟着改口；行高也顺带钉一句 —— 这一支字体是 Bahnschrift，汉字从雅黑掉下来，行距
    /// 按前者算而字比它高，折行就会叠在一起（2026-09-12 他截图里那一处）。「行高不小于字号」是那一病根
    /// 的反面，也正是屏上最难看出来的那件事。
    /// </para>
    /// <para>
    /// 原来这里还读第三行眉字（电影／剧集／单集）。那一行整个删了 —— 「喜剧之王上面的那个电影太突兀了，删掉
    /// 或者移动到别的地方」 —— 剩下这两行每一页都在，所以这一条在四种页面上答的是同一句话。
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) HeroType()
    {
        var display = Face("EgDisplayFontFamily");
        var data = Face("EgDataFontFamily");
        var size = (double)Application.Current.Resources["EgHeaderFontSize"];

        var spacing = TitleText.LineHeight >= TitleText.FontSize;
        var ok = TitleText.FontFamily.Source == display
            && Math.Abs(TitleText.FontSize - size) < 0.01
            && HeroFacts.FontFamily.Source == data
            && spacing;

        return (ok,
            $"片名 {TitleText.FontFamily.Source} {TitleText.FontSize:0}（行高 {TitleText.LineHeight:0}）、"
                + $"读数 {HeroFacts.FontFamily.Source} {HeroFacts.FontSize:0}"
                + (ok ? "" : $" —— 要的是片名「{display}」{size:0}、读数「{data}」")
                + (spacing ? "" : "，片名的行高比字号还小（折行会叠在一起）"));

        static string Face(string key) => ((FontFamily)Application.Current.Resources[key]).Source;
    }

    /// <summary>
    /// What 媒体信息 has drawn: how many of its rows the repeater built, whether the panel is open, and the
    /// text of the first row it built. The same distinction as <see cref="Realised"/>, and the same reason —
    /// the row template is markup no build runs — with one more claim on top: the label column is what the
    /// table is aligned on, and a row that realised with nothing in it counts the same as one that drew.
    /// </summary>
    internal (int Drawn, bool Expanded, string First) InfoRealised
    {
        get
        {
            var drawn = 0;
            var first = string.Empty;

            for (var index = 0; index < ViewModel.InfoRows.Count; index++)
            {
                if (InfoTable.TryGetElement(index) is not { } row) continue;

                drawn++;
                if (first.Length == 0) first = Texts(row);
            }

            return (drawn, InfoPanel.IsExpanded, first);
        }
    }

    /// <summary>
    /// 自检：背景容器通栏且固定，那张图画在跟宽走的盒子里（<c>BackdropPicture</c>），按 UniformToFill、
    /// 顶部对齐、水平居中 —— 「背景图上下不要有黑边」加上「窗口收窄时背景图要等比例缩放」。
    /// <para>
    /// 盒高由 <see cref="DetailViewModel.PictureHeight"/> 给：宽的窗口上它被视口封顶，照旧铺满第一屏；窄下来
    /// 之后等于「页宽 × 图的高宽比」，整张图等比缩小。层本身照旧铺满整窗（底下那段露出的是层底，交给压暗的
    /// 尾部），所以「层顶到窗口上沿、层底到窗口下沿」两句照旧读在层上。
    /// </para>
    /// <para>
    /// 值得读，是因为这几件事在截图里看得见、在别的任何读数里都看不出来，而它们各自都能悄悄失效：盒高绑丢了
    /// （Height 退回 NaN 就是一格空图）、外边距归了零、头上那一格的高走了另一档、把背景那一层顶上去的那个负
    /// 边距（<see cref="LiftBackdrop"/>）。往下滚以后看不见这张图是正文遮的，不是它走了 —— 那件事是另一条
    /// 读数（<see cref="BodySeal"/>）。
    /// </para>
    /// <para>
    /// 关键是这一条读在自检把页面滚到底之后（<c>ShellSelfCheck</c> 先 <see cref="ScrollToEnd"/>，再读详情）：
    /// 那时背景层的上沿还量在窗口的 0 上，一句话答完两件事 —— 它没跟着滚走，而且它一直画进标题栏。跟着滚的
    /// 那一版在这里会报一个很负的数。
    /// </para>
    /// <para>
    /// 判据取 <see cref="DetailViewModel.BackdropShown"/>（服务器上有没有这张图）而不是解出来的位图：版面按前者分
    /// 档，拿后者当判据会在一次解码失败上要求 412，而页面正按那一档往上布着。没有那张图的条目上背景层整个是收
    /// 起的，量它的上沿没有意义，所以那一档只比带子那一格。
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) HeroFill()
    {
        var art = ViewModel.BackdropShown;

        // 视图模型算出来的那个数，不是 DetailHero.Height 那两档：集页那一格按里面那一叠实测给、落在两档之下
        // （见 DetailViewModel.HeroHeight），拿两档当判据的话那一页每次都会报「高该是 460」。
        var want = ViewModel.HeroHeight;
        var wide = HeroBand.ActualWidth >= Body.ActualWidth - 0.5;
        var tall = Math.Abs(HeroBand.ActualHeight - want) < 0.5;
        var detail = $"头图 {HeroBand.ActualWidth:0}×{HeroBand.ActualHeight:0}、"
            + $"页面 {Body.ActualWidth:0}×{Body.ActualHeight:0}";

        if (!art || XamlRoot?.Content is not UIElement root)
        {
            return (wide && tall,
                detail + (art ? "（还没上树，只比了带子那一格）" : "（没有剧照，退回带子那一档）")
                    + (wide ? "" : "，没通到两边")
                    + (tall ? "" : $"，高该是 {want:0}"));
        }

        // 量的是 Backdrop 容器而不是 ImageBrush 画出的图：图还在路上时这一层照样站着（见 HeroArtVisibility），
        // 而这一条问的是「那一层和里面那个盒子在窗口的哪儿」。左沿不比 —— 页面左边是侧边栏那道竖线，图本来
        // 就不该盖过去。
        var box = Backdrop.TransformToVisual(root)
            .TransformBounds(new Rect(0, 0, Backdrop.ActualWidth, Backdrop.ActualHeight));
        var picture = BackdropPicture.TransformToVisual(root)
            .TransformBounds(new Rect(0, 0, BackdropPicture.ActualWidth, BackdropPicture.ActualHeight));
        var window = XamlRoot.Size;
        var top = Math.Abs(box.Top) < 1.5;
        var full = box.Bottom >= window.Height - 1.5;

        // 容差 1.5 而不是半像素：绑定的数本身可以带小数（页宽 × 高宽比），而排出来的 ActualHeight 被布局取整到
        // 物理整像素，非整数缩放下两边能差到大半个 DIP —— 这里的对手是「绑丢了」（差出半个视口那种），不是
        // 半像素的取整零头。
        var sized = Math.Abs(picture.Height - ViewModel.PictureHeight) < 1.5 && picture.Top < 1.5;
        var strip = ViewModel.PictureHeight < window.Height - 1.5;

        // 下沿那道黑色渐变（「背景图下方的衔接处要用黑色渐变」）跟着图走，两档都在：盒高被视口封顶，盒子却
        // 站在窗口上沿（LiftBackdrop），铺满那一档的下沿也落在窗口下沿上方标题栏那一条里 —— 那一刀
        // UniformToFill 的硬边没有渐变接就是「背景图下方不是黑色渐变而是一条黑边」（2026-09-12）。图还没解出来
        // 时整个盒子（连渐变一起）收着，那一拍不判。
        var fadeWanted = ViewModel.HeroVisibility == Visibility.Visible;
        var fade = BackdropFade.Visibility == Visibility.Visible;
        var topAligned = BackdropImageBrush.Stretch == Stretch.UniformToFill
            && BackdropImageBrush.AlignmentX == AlignmentX.Center
            && BackdropImageBrush.AlignmentY == AlignmentY.Top;

        return (wide && tall && top && full && sized && fade == fadeWanted && topAligned,
            detail + $"、背景层 {box.Left:0},{box.Top:0} 到 {box.Right:0},{box.Bottom:0}、"
                + $"图盒 {picture.Width:0}×{picture.Height:0.##}（该 {ViewModel.PictureHeight:0.##}，上沿 {picture.Top:0.##}，"
                + $"{(strip ? "等比画面条" : "铺满视口")}，下沿渐变{(fade ? "在" : "不在")}（{(fadeWanted ? "该在" : "该不在")}），"
                + $"窗口 {window.Width:0}×{window.Height:0}，已滚到底）"
                + (wide ? "" : "，没通到两边")
                + (tall ? "" : $"，高该是 {want:0}")
                + (top ? "" : "，背景层没顶到窗口上沿（跟着滚走了？）")
                + (full ? "" : "，背景层没铺到窗口下沿")
                + (sized ? "" : "，图盒的高度或上沿不是视图模型算的那个（绑丢了？）")
                + (fade == fadeWanted ? "" : "，下沿渐变的显隐和背景图对不上")
                + (topAligned ? "，背景图铺满裁切、顶部对齐、上下不留边" : "，背景图不是铺满裁切加顶部对齐（上下会留黑边）"));
    }

    /// <summary>
    /// 自检：页尾那张横幅 —— 「在电影页面 剧页面 集页面的底部添加横幅」。
    /// <para>
    /// 三件屏上不容易看出来的事：规矩说该有哪一张（<see cref="ItemArtwork.Footer"/> 自己答，不是这个文件猜的）、
    /// 该有的时候屏上真画了而且没超出那张纸的左右、以及<em>该空的时候真空着</em>。最后那一句是这一条的正题：没有
    /// 徽标的条目上剧名头上那一枚已经是这张横幅，页尾再来一张就是同一张图在一页上出现两次 —— 而屏上单看每一处
    /// 都挺好，只有一起看才看出重了。季页也在这一句里（用户点的名只有电影、剧、集）。
    /// </para>
    /// <para>
    /// 「画了没有」本身只报不判：那一步要等一趟网络加一次解码，而一台答不出这张图的服务器不是版面的错。判的是
    /// 落点和那两个「该空」。这一条读在自检把页面滚到底之后，所以这张图这时候一定已经在视口里 —— 它就在最底下。
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) FooterRead()
    {
        if (XamlRoot?.Content is not UIElement root) return (false, "页面还没上树");

        var item = ViewModel.CurrentItem;
        var pick = ViewModel.FooterPick;
        var plate = ItemArtwork.Plate(item);
        var kind = EmbyItemType.ToChinese(ViewModel.ItemType);
        var page = ViewModel.ItemType is EmbyItemType.Movie or EmbyItemType.Series or EmbyItemType.Episode;
        var drawn = FooterBanner.Visibility == Visibility.Visible;

        // 规矩那一句用词说，同「详情徽标与艺术图」那一关：屏上那张图自己说不出它走了哪条路，而这一处正有两条路
        // （自己的、借剧集那一层的）。
        var says = item is null ? "没有条目"
            : !page ? $"无（{kind}页不摆）"
            : pick is null ? "无（这一条和剧集那一层都没有横幅图，或者名牌已经用了它）"
            : pick.Value.ItemId == item.Id ? "自己的横幅图"
            : $"借剧集那张（取自条目 {pick.Value.ItemId}）";

        // 防重那一句在这儿再对一遍：页尾那张不许和剧名头上那一枚是同一张图。规矩那一头有单测，这一条读的是
        // 「屏上这一次真的不是同一张」。
        var distinct = pick is null || plate is null || pick.Value != plate.Value;
        var allowed = page && pick is not null;

        if (!drawn)
        {
            return (distinct, $"{kind}页，规矩说该有的是{says}；屏上没画"
                + (allowed ? "（图还在路上或者服务器没给，只报不判）" : "")
                + (distinct ? "" : "，而且它和剧名头上那一枚是同一张"));
        }

        var box = FooterBanner.TransformToVisual(root)
            .TransformBounds(new Rect(0, 0, FooterBanner.ActualWidth, FooterBanner.ActualHeight));
        var sheet = BodySheet.TransformToVisual(root)
            .TransformBounds(new Rect(0, 0, BodySheet.ActualWidth, BodySheet.ActualHeight));
        var inside = box.Left >= sheet.Left + BodySheet.Padding.Left - 0.5
            && box.Right <= sheet.Right - BodySheet.Padding.Right + 0.5;
        var last = box.Bottom <= sheet.Bottom + 0.5 && box.Top >= Box(CastRow).Bottom - 0.5;

        return (allowed && distinct && inside && last,
            $"{kind}页，规矩说该有的是{says}；屏上画了 {box.Width:0}×{box.Height:0}，"
                + $"落在 {box.Left:0},{box.Top:0}（纸 {sheet.Left:0},{sheet.Top:0} 到 {sheet.Right:0},{sheet.Bottom:0}）"
                + (allowed ? "" : "，可规矩说这儿该空着")
                + (distinct ? "" : "，而且它和剧名头上那一枚是同一张")
                + (inside ? "" : "，超出了纸的左右")
                + (last ? "" : "，不在演职人员那一排之后或者越过了纸的下沿"));

        Rect Box(FrameworkElement element) => element
            .TransformToVisual(root)
            .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
    }

    /// <summary>
    /// 自检：往下滚的时候背景图是不是真被盖住了 —— 「滑到下面不用显示背景了，五颜六色的太丑了」。
    /// <para>
    /// 背景那一层铺满整个窗口而且固定不动（见 <see cref="HeroFill"/>），所以「看不见它」全靠正文那张纸遮：
    /// 这一条问的就是纸在最难的那个位置上有没有遮住 —— 页面拉到最底时，纸的四条边和这一页、和窗口下沿的关系。
    /// 上一版正文是几块浮在图上的玻璃，故意在四周留 28 的缝漏图，这一条在那一版上三个数都是负的。
    /// </para>
    /// <para>
    /// 底下那条是关键：纸只有内容那么高，而一部没有单集、相似又寥寥的电影可能填不满一屏，所以纸有个跟着视口
    /// 走的下限（<see cref="DetailViewModel.PaperMinHeight"/>）。这一条读的是那个下限真的生效了，不是内容碰巧
    /// 够长 —— 短页面上根本滚不动，纸的下沿就得自己站到窗口的下沿上。
    /// </para>
    /// <para>
    /// 另外两条读的是纸的上沿 ——「下方的媒体信息等，要往下滑才能看到」加上「拉大或拉小窗口会导致背景图被遮挡」。
    /// 那道边落在哪儿从前只由内容定，于是同一个条目在矮窗口上看不见它、拉高就有三百像素的纸浮在剧照上。现在
    /// 富余高度归尾部（<see cref="DetailHero.TailHeight"/>），所以要读两句：纸的上沿在第一屏外面，而且尾部撑到的
    /// 正是「内容和这一次该补的量两者取大」（封了顶就是那个顶）——多撑一分就是在剧情说明底下留一段没人要的空气，
    /// 少撑一分那道边就回到剧照上。屏上这两件事只在某些窗口尺寸下看得出来，而自检只跑一个尺寸，所以读的是数。
    /// </para>
    /// <para>
    /// 「在第一屏外面」那一句在纸面上沿过了线之后不问（<see cref="DetailHero.PaperLineFor"/>）：那一档纸本来就该
    /// 露在第一屏里 —— 窗口高过阈值，纸带着下一节的内容从那条线起往上长（「窗口大于1600*900后开始显示下方的
    /// 黑边，小于1600*900时海报占满整个窗口」）。过了线之后撑到多少仍旧由上一句（尾部撑得对不对）咬着，所以
    /// 这一档不是没人看，只是换了一句问法。
    /// </para>
    /// <para>
    /// 左右两条比的是这一页（<c>Body</c>）而不是窗口：页面左边是侧边栏，纸本来就不该盖过去。
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) BodySeal()
    {
        if (XamlRoot?.Content is not UIElement root) return (false, "页面还没上树");

        var box = BodySheet.TransformToVisual(root)
            .TransformBounds(new Rect(0, 0, BodySheet.ActualWidth, BodySheet.ActualHeight));
        var tail = HeroTail.TransformToVisual(root)
            .TransformBounds(new Rect(0, 0, HeroTail.ActualWidth, HeroTail.ActualHeight));
        var page = Body.TransformToVisual(root)
            .TransformBounds(new Rect(0, 0, Body.ActualWidth, Body.ActualHeight));
        var window = XamlRoot.Size;

        // 分界要贴在那一段最后一块的下沿上，而最后一块是哪一块跟着页面走：集页上剧情说明底下还接着同季那一带集
        // （「把集页面的剧情说明和集列表位置调换」），别的页面上剧情说明就是最后一块。问的是那一段现在真正的
        // 末尾，所以两种页面共用这一句 —— 写死读剧情说明的那一版会在集页上把这一条判成「分界没贴住」。
        var last = HeroTail.Children.OfType<FrameworkElement>()
            .LastOrDefault(child => child.Visibility == Visibility.Visible);
        var end = last is null
            ? tail.Bottom
            : last.TransformToVisual(root)
                .TransformBounds(new Rect(0, 0, last.ActualWidth, last.ActualHeight)).Bottom;
        var left = box.Left <= page.Left + 0.5;
        var right = box.Right >= page.Right - 0.5;
        var below = box.Bottom >= window.Height - 0.5;
        var opaque = BodySheet.Background is SolidColorBrush { Color.A: 255 };
        var moved = Math.Abs(box.Top - tail.Bottom) < 0.5;

        // 尾部该多高：内容和「这一次该补多少」两者取大（见 DetailHero.TailHeight）。内容那一头按最后一块的
        // 下沿加上尾部自己的下留白算 —— 那就是不撑的时候这一段的高。
        var content = last is null ? HeroTail.ActualHeight : end - tail.Top + HeroTail.Padding.Bottom;
        var want = Math.Max(content, ViewModel.TailMinHeight);
        var snug = Math.Abs(HeroTail.ActualHeight - want) < 0.5;

        // 纸的上沿不许落进第一屏。读的是内容坐标（带高加尾部实高）而不是屏上位置：这一条问的是「滚到顶时它在
        // 哪儿」，而自检读到这里时页面已经滚到底了。两档不问：没有剧照的那一档不撑尾部，以及纸面上沿到了
        // PaperLine 那条线之后的档 —— 那时候富余的高度归纸，纸本来就该露一条。封顶有这两个出处：纸面那条线
        // （窗口高过阈值），和背景画面的下沿（等比画面条底下没有画面，尾部撑过去就是空位，见
        // DetailHero.TailHeight）—— 哪一个顶住的都算「该露」。
        var paperTop = PaperOffset();
        // 两边都先取整再比：TailMinHeight 是 Core 那一支取整过的（DetailHero.TailHeight），直接拿小数去比，
        // 「正好在画面下沿封顶」的那一档会差出半个像素 —— 带高不缩小的今天（412 正好卡在 598.5 − 186.5 上）
        // 每一张有剧照的页面都会踩中。
        var byLine = Math.Abs(ViewModel.TailMinHeight - Math.Round(ViewModel.PaperLine - ViewModel.HeroHeight)) < 0.5;
        var byPicture = Math.Abs(ViewModel.TailMinHeight - Math.Round(ViewModel.PictureHeight - ViewModel.HeroHeight)) < 0.5;
        var capped = byLine || byPicture;
        var beyond = paperTop >= Body.ActualHeight - 0.5;
        var folded = !ViewModel.HeroArt || capped || beyond;
        var joined = ViewModel.HeroCardShown
            ? HeroTail.Background is null or SolidColorBrush { Color.A: 0 }
            : HeroTail.Background is SolidColorBrush { Color: var tailColor }
                && tailColor == HeroScrimBrush.GradientStops[^1].Color;
        // 音轨那一行和剧情说明都不带框 ——「移除音频和剧情说明那两个栏的框」（2026-09-12）。它们站在那块大板上，
        // 板本身就是一层，再给每一块描一圈发丝线就成了一层套一层；只剩集带那一圈（「剧页面那种样式」）。
        var bare = !Rings(PickerSet.Panel) && !Rings(OverviewPanel);

        return (left && right && below && opaque && moved && snug && folded && joined && bare,
            $"正文 {box.Left:0},{box.Top:0} 到 {box.Right:0},{box.Bottom:0}，"
                + $"页面 {page.Left:0},{page.Right:0}、窗口高 {window.Height:0}（已滚到底）"
                + (opaque ? "，底色不透明" : "，底色透光")
                + (moved ? "，分界紧接尾部那一段" : $"，分界没贴住尾部（尾部到底 {tail.Bottom:0}）")
                + (snug ? $"，尾部撑到 {HeroTail.ActualHeight:0}（内容 {content:0}、第一屏要 {ViewModel.TailMinHeight:0}）"
                    : $"，尾部撑得不对（实测 {HeroTail.ActualHeight:0}、该是 {want:0}）")
                + (beyond ? $"，纸的上沿 {paperTop:0} 在第一屏 {Body.ActualHeight:0} 外面"
                    : capped
                        ? $"，纸的上沿 {paperTop:0} 露在第一屏 {Body.ActualHeight:0} 里"
                            + (byLine ? $"（纸面上沿钉在视口 {ViewModel.PaperLine:0}，富余的高度归纸）" : "")
                            + (byPicture ? $"（尾部撑到背景画面下沿 {ViewModel.PictureHeight:0.##} 为止，底下归内容）" : "")
                        : $"，纸的上沿 {paperTop:0} 浮在第一屏 {Body.ActualHeight:0} 里")
                + (joined
                    ? ViewModel.HeroCardShown ? "，尾部让给那块板（自己不画底）" : "，尾部接住头图末色"
                    : "，尾部的底色不对")
                + (bare ? "，音轨和剧情说明都没框" : "，音轨或剧情说明还带着框")
                + (left ? "" : "，左边露出背景")
                + (right ? "" : "，右边露出背景")
                + (below ? "" : $"，底下露出背景 {window.Height - box.Bottom:0}"));
    }

    /// <summary>
    /// 一块板这一刻带不带框：底色不是透明的、四边都有发丝线。自检读的是元素上真正的值 —— 集带那一圈要在这儿
    /// 钉着，音轨和剧情说明那两圈要钉着没有（「移除音频和剧情说明那两个栏的框」）。
    /// <para>
    /// 收 <see cref="FrameworkElement"/> 而不是 <see cref="StackPanel"/>：同一行文件选项有两份，片名那一栏那一份
    /// 是一层 <c>WrapRow</c>（本来就没有底和框），尾部那一份才带着样式那圈。不是 <see cref="StackPanel"/> 的
    /// 那一份按「没有框」答，这正是它该有的样子。
    /// </para>
    /// </summary>
    private static bool Rings(FrameworkElement element) => element is StackPanel panel
        && panel.Background is SolidColorBrush { Color.A: > 0 }
        && panel.BorderThickness is { Left: > 0, Top: > 0, Right: > 0, Bottom: > 0 };

    /// <summary>
    /// 这一页那一行文件选项此刻是哪一份 —— 集页的宽版式在片名那一栏里（<c>ColumnPickers</c>，标签在下拉左边，
    /// 2026-09-12「按键布局参考上方截图」），其余页面和紧凑版式在尾部那一段（<c>PickerPanel</c>，标签在下拉
    /// 头上）。两份只有一份在屏上，判据是 <see cref="DetailViewModel.ColumnPickersVisibility"/> 那两半。
    /// <para>
    /// 自检读它是因为读数不能写死读哪一份：写死尾部那一份的那一版在集页上会读到「这一条目没有音轨」，而那一行
    /// 明明就在片名底下；反过来写死片名那一份的，在电影页和紧凑版式上读不到东西。
    /// </para>
    /// </summary>
    private (FrameworkElement Panel, Thickness Padding, ComboBox Source, ComboBox Audio, ComboBox Subtitle) PickerSet =>
        ViewModel.HeroCardShown
            ? (ColumnPickers, default, ColumnSourcePicker, ColumnAudioPicker, ColumnSubtitlePicker)
            : (PickerPanel, PickerPanel.Padding, SourcePicker, AudioPicker, SubtitlePicker);

    /// <summary>
    /// 自检：「媒体源／音频／字幕」那一行该不该有，以及有的那一次三个下拉有没有被窗口右沿切掉。
    /// <para>
    /// 该不该有：这一行讲的是「这一个文件放哪一条轨道」，所以它只摆在讲一个文件的页面上 —— 电影和单集有，剧和
    /// 季没有（判据在 <c>DetailViewModel.PickersVisibility</c>，和 媒体信息 那张表同一条理）。剧页上那三个下拉
    /// 底下什么都没有可挑：一部剧不是一个文件。收着的那一次这一句成立，而它在剧页上冒出来是这一条会红的那种坏法。
    /// </para>
    /// <para>
    /// 三个下拉的宽度是按各自最长那条轨道名撑出来的，所以「一行装得下」不是一句能算出来的话 —— 它跟这台
    /// 服务器上的轨道叫什么名字、跟窗口有多宽都有关。原来那个横排 <c>StackPanel</c> 在装不下的时候不换行也
    /// 不收窄，只是把最右边那个切在窗口右沿上：屏上是「字幕」那一格缺了一块，而报告里三条读数一条都不会响。
    /// 现在里面那一层是 <see cref="WrapRow"/>，装不下就换行，这一条读的就是「换完之后真的没人出界」。
    /// </para>
    /// <para>
    /// 比的是这一块板自己的内边距围出来的那条右沿，不是窗口的右沿 —— 板子有 18 的内边距，压着边框画到窗口
    /// 上就已经是错的。窗口宽一起报出来，因为这一条在宽窗口上永远成立：它只在窄窗口下才有话说，而读数里那句
    /// 「摆成几行」正是「这一次到底窄没窄」的证据。
    /// </para>
    /// <para>
    /// 顺带钉住第二件事：整排必须贴着那条左沿。换行的面板要是把「我占了多宽」当成摆完的尺寸交回去，框架会把
    /// 差出来的空当对半分（<see cref="WrapRow.ArrangeOverride"/> 那一段），整排就往右飘 —— 三个下拉照旧全在
    /// 界内，只有这一句会响。
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) PickerFit()
    {
        if (XamlRoot?.Content is not UIElement root) return (false, "页面还没上树");

        // 这一行说的是播放键指着的那个文件 —— 电影和单集是自己，剧页和季页是解析出来的那一集（判据在
        // DetailViewModel.PickersVisibility）。所以判的不再是「这一页是不是文件」，而是「有没有那个落点」：
        // 落点是空的（人物页，或者一部剧的集还没回来）还把这一行画出来，挑的就是另一个条目留下的轨道。
        // 屏上那一份由 PickerSet 给：集页的宽版式在片名那一栏里，别的页面和紧凑版式在尾部。
        var target = ViewModel.PlayTarget;
        var kind = EmbyItemType.ToChinese(ViewModel.ItemType);
        var (panel0, padding, source, audio, subtitle) = PickerSet;

        // 两份只该露一份 —— 都露是同一个问题在屏上问两遍（三个下拉 ×2），而 DetailViewModel 那两句判据是
        // 同一句 Pickable 的两半，这里钉的就是那两半真的对上了（都收着是另一回事：那一条没有可挑的）。
        var doubled = PickerPanel.Visibility == Visibility.Visible
            && ColumnPickers.Visibility == Visibility.Visible;

        if (panel0.Visibility != Visibility.Visible)
        {
            if (doubled) return (false, $"{kind}页，两份文件选项都露着");
            return (true, target is null ? $"{kind}页没有播放落点，那一行收着" : $"{kind}页，没什么可挑的，那一行收着");
        }

        if (doubled) return (false, $"{kind}页，两份文件选项都露着");
        if (target is null) return (false, $"{kind}页没有播放落点，可文件选项那一行画出来了");

        var panel = panel0.TransformToVisual(root)
            .TransformBounds(new Rect(0, 0, panel0.ActualWidth, panel0.ActualHeight));
        var edge = panel.Right - padding.Right;
        var window = XamlRoot.Size;

        var read = new List<string>();
        var rows = new List<double>();
        var lefts = new List<double>();
        var ok = true;

        foreach (var (name, picker) in new[]
        {
            ("媒体源", source),
            ("音频", audio),
            ("字幕", subtitle)
        })
        {
            if (picker.Visibility != Visibility.Visible) continue;

            var box = picker.TransformToVisual(root)
                .TransformBounds(new Rect(0, 0, picker.ActualWidth, picker.ActualHeight));
            var fits = box.Right <= edge + 0.5;
            ok &= fits;

            if (!rows.Any(top => Math.Abs(top - box.Top) < 1.5)) rows.Add(box.Top);
            lefts.Add(box.Left);
            read.Add($"{name} {box.Left:0}–{box.Right:0}{(fits ? "" : "（出界）")}");
        }

        if (read.Count == 0) return (true, "三个下拉这一次都没露面");

        // 每一行都从这一块板的内容左沿起排，所以最靠左那个必须正好贴着它。这一句钉的是另一种坏法：面板摆完之后
        // 要是交回「我占了多宽」而不是「给我的这一格多宽」，框架就把差出来的那点空当对半分到两边（Stretch 算对齐
        // 偏移时和 Center 同一支），整排往右挪 —— 挪多少还跟着轨道名的长短变。屏上是它比上下两段都缩进一块，
        // 而三个下拉全在界内、换行也对，只看上面那三条读数一个都不会响。
        var contentLeft = panel.Left + padding.Left;
        var drift = lefts.Min() - contentLeft;
        ok &= drift < 1.5;

        return (ok, $"{string.Join("、", read)}，可用 {contentLeft:0}–{edge:0}"
            + $"（窗口宽 {window.Width:0}），摆成 {rows.Count} 行"
            + $"，在{(ViewModel.HeroCardShown ? "片名那一栏" : "尾部")}"
            + (drift < 1.5 ? "，整排贴着左边" : $"，整排右移了 {drift:0}"));
    }

    /// <summary>
    /// 自检：标题栏那一条洗到正文那张纸的颜色了没有 —— 「往下拉之后标题颜色要渐变，变的和下方背景一样」。
    /// <para>
    /// 三件事，各对一种不出声的坏法。那一条的位置：它必须正好盖住标题栏让出来的那一格，短一像素就是原来那道
    /// 横缝的一截。洗的浓度和颜色：<see cref="DetailHero.TopWash"/> 是算得出的那一份，元素上真正那支画刷是屏上
    /// 那一份，两份对一遍。还有带子底下那道罩子的停点跟 Core 那张表对得上没有 —— 对不上的话曲线读的就不是屏上
    /// 这道罩子，缝会照样在，而前两条一个都不会响。
    /// </para>
    /// <para>
    /// 自检读它的时候页面已经滚到底（<see cref="ScrollToEnd"/>），落在洗满那一档：那一档的判据是「和正文同一支
    /// 画刷」，不是「颜色算得对」—— 那一档本来就不再是洗，见 <see cref="PaintWash"/>。洗到一半那一档在这个位置
    /// 上量不到，而它才是那道横缝的所在，所以这里按半程的浓度画一遍读一遍再画回去（<see cref="ApplyWash"/>）。
    /// </para>
    /// <para>
    /// 第四件事是外壳有没有在这一条上再涂一层自己的底色（<paramref name="trailBase"/>，就是 <c>ShellPage</c> 里
    /// 面包屑那一行的底）。有图的详情页上那一行永远不该自己上底 —— 涂了就是页面底色压在洗出来的纸色上，这一条
    /// 比正文亮四五级，而且横缝挪到了面包屑那一行的上沿：位置、浓度、曲线三条读数一条都不会响，屏上却看得见。
    /// 见 <see cref="SyncTitleInk"/> 与 <see cref="TitleStrip.PagePainted"/>。
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) WashRead(Brush? trailBase)
    {
        if (XamlRoot?.Content is not UIElement root) return (false, "页面还没上树");
        if (!ViewModel.HeroArt) return (true, "这一条目没有剧照，标题栏那一条不用洗");
        if (HeroTail.Background is not SolidColorBrush { Color: var tail })
            return (false, "头图尾部不是一支纯色画刷");
        if (BodySheet.Background is not SolidColorBrush { Color: var sheet })
            return (false, "正文那张纸不是一支纯色画刷");

        // 标题栏让出来的那一格有多高：背景层往上顶的就是这个数，那一条盖的也是这个数。见 LiftBackdrop。
        var lift = TransformToVisual(root).TransformPoint(new Point(0, 0)).Y;
        var box = TitleWash.TransformToVisual(root)
            .TransformBounds(new Rect(0, 0, TitleWash.ActualWidth, TitleWash.ActualHeight));
        var placed = box.Width > 0 && Math.Abs(box.Top) < 1.5 && Math.Abs(box.Bottom - lift) < 1.5;

        var offset = Body.VerticalOffset;
        var paperTop = PaperOffset();
        var paperCover = DetailHero.PaperCover(offset, paperTop, TitleWash.ActualHeight);
        var (painted, how) = ReadTitlePaint(offset, paperTop, tail, sheet);

        // 两个过渡都在滚到底的页面上量不到：先强制画头图罩子的半程，再强制让纸面上沿走到标题栏正中。
        // 两次都走真正的 ApplyWash，读完再按实际滚动位置画回去。
        var halfOffset = BandHeight / 2;
        var half = DetailHero.TopWash(halfOffset, true, BandHeight);
        ApplyWash(halfOffset, force: true);
        var (halfOk, halfHow) = ScrimPaint(half, tail);

        var paperHalfOffset = paperTop + (TitleWash.ActualHeight / 2);
        var paperHalf = DetailHero.PaperCover(paperHalfOffset, paperTop, TitleWash.ActualHeight);
        ApplyWash(paperHalfOffset, force: true);
        var (paperOk, paperHow) = PaperPaint(paperHalf, tail, sheet);
        PaintWash(force: true);

        // 曲线读的得是屏上这道罩子：Core 那张表和它算出来的高是同一组数的两份写法。集页的带子收窄时罩子跟着
        // 收（DetailHero.ScrimSpan），所以这里按屏上那一块的高和位置反算，不写死 440。
        var band = HeroScrim.ActualHeight;
        var inset = HeroBand.ActualHeight - band;
        var stops = HeroScrimBrush.GradientStops;
        var curve = band > 0 && stops.All(stop =>
            Math.Abs(DetailHero.TopWash(inset + (stop.Offset * band), true, BandHeight) - (stop.Color.A / 255d))
                < 0.005);

        // 外壳那一层：有图就一层都不该有，见上面那段和 SyncTitleInk。
        var bare = trailBase is null;

        return (placed && painted && halfOk && paperOk && curve && bare,
            $"滚到 {offset:0}、纸面从 {paperTop:0} 起并覆盖标题栏 {paperCover:0.00}；"
                + $"那一条 {box.Top:0}→{box.Bottom:0} 宽 {box.Width:0}（标题栏让出 {lift:0}）；{how}；"
                + $"罩子半程 {halfHow}；纸面半程 {paperHow}"
                + (curve ? $"；罩子那 {stops.Count} 个停点和 Core 那张表一致" : "；罩子的停点和 Core 那张表对不上")
                + (bare ? "；面包屑那一行没自己上底色" : $"；面包屑那一行又涂了一层{Paint(trailBase)}")
                + (placed ? "" : "，那一条没盖住标题栏那一格"));

        static string Paint(Brush? brush) => brush switch
        {
            SolidColorBrush solid => Hex(solid.Color),
            null => "空",
            _ => brush.GetType().Name
        };
    }

    /// <summary>
    /// 当前滚动位置应该落在哪个阶段，并读取那一阶段真正画出的画刷。
    /// </summary>
    private (bool Ok, string How) ReadTitlePaint(double offset, double paperTop, Color tail, Color sheet)
    {
        var cover = DetailHero.PaperCover(offset, paperTop, TitleWash.ActualHeight);
        if (cover >= 1)
        {
            var same = ReferenceEquals(TitleWash.Background, BodySheet.Background);
            return (same, same ? "纸面已盖满，整条复用正文画刷" : "纸面已盖满，可标题栏没有复用正文画刷");
        }

        if (cover > 0) return PaperPaint(cover, tail, sheet);

        var heroBottom = BandHeight;
        if (offset >= heroBottom)
        {
            var same = ReferenceEquals(TitleWash.Background, HeroTail.Background);
            return (same, same ? "黑色尾部经过，整条复用尾部画刷" : "黑色尾部经过，可标题栏不是同一支画刷");
        }

        return ScrimPaint(DetailHero.TopWash(offset, true, BandHeight), tail);
    }

    /// <summary>
    /// 头上那一格这一次多高。布好版面就读屏上那个，还没布好就读视图模型算出来的那个 —— 罩子的曲线、洗到
    /// 多浓、尾部从哪儿开始，三处都得按同一个数算，见 <see cref="DetailHero.ScrimSpan"/>。
    /// </summary>
    private double BandHeight => HeroBand.ActualHeight > 0 ? HeroBand.ActualHeight : ViewModel.HeroHeight;

    /// <summary>逐个停点核对头图罩子的半透明黑，差一级就可能重新长出横缝。</summary>
    private (bool Ok, string How) ScrimPaint(double want, Color tail)
    {
        if (TitleWash.Background != _washBrush || _washBrush.GradientStops.Count != 3)
            return (false, $"那一条不是罩子的三个停点渐变（{TitleWash.Background?.GetType().Name ?? "空"}）");

        var alpha = (byte)Math.Round(want * 255);
        var scrim = HeroScrimBrush.GradientStops[^1].Color;
        var face = Color.FromArgb(alpha, tail.R, tail.G, tail.B);
        var edge = Color.FromArgb(alpha, scrim.R, scrim.G, scrim.B);
        var stops = _washBrush.GradientStops;

        var ok = stops[0].Color == face && stops[1].Color == face && stops[2].Color == edge
            && Math.Abs(stops[1].Offset - want) < 0.01 && Math.Abs(stops[2].Offset - 1) < 0.001;

        return (ok, $"渐变 {Hex(stops[0].Color)}@{stops[0].Offset:0.00} → {Hex(stops[1].Color)}"
            + $"@{stops[1].Offset:0.00} → {Hex(stops[2].Color)}@{stops[2].Offset:0.00}"
            + $"，该是尾部 {Hex(face)}、罩子 {Hex(edge)}、拐点 {want:0.00}");
    }

    /// <summary>核对正文纸面上沿在标题栏里的真实位置，以及硬边两侧的颜色。</summary>
    private (bool Ok, string How) PaperPaint(double cover, Color tail, Color sheet)
    {
        if (TitleWash.Background != _paperBrush || _paperBrush.GradientStops.Count != 4)
            return (false, $"那一条不是纸面边界的四停点渐变（{TitleWash.Background?.GetType().Name ?? "空"}）");

        var edge = 1 - cover;
        var stops = _paperBrush.GradientStops;
        var ok = stops[0].Color == tail && stops[1].Color == tail
            && stops[2].Color == sheet && stops[3].Color == sheet
            && Math.Abs(stops[1].Offset - edge) < 0.01
            && Math.Abs(stops[2].Offset - edge) < 0.01;

        return (ok, $"黑 {Hex(tail)} 到纸 {Hex(sheet)} 的边界在 {edge:0.00}"
            + $"（实际 {stops[1].Offset:0.00}/{stops[2].Offset:0.00}）");
    }

    private static string Hex(Color color) => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>
    /// Clicks the first card in 单集 on the self-check's behalf; see
    /// <see cref="DetailViewModel.OpenFirstEpisode"/>.
    /// </summary>
    internal EmbyItem? OpenFirstEpisode() => ViewModel.OpenFirstEpisode();

    /// <summary>
    /// Scrolls the page to the end, for the self-check. An <c>ItemsRepeater</c> realises what its
    /// effective viewport covers, and the cast row starts below the fold on any normal window — asking
    /// what it drew without this would be asking about a row that has never been in view.
    /// </summary>
    internal void ScrollToEnd()
    {
        // Laid out first: the extent is what the scroll is measured against, and a page whose load
        // finished this same tick may not have been measured yet.
        Body.UpdateLayout();
        Body.ScrollTo(0, Body.ScrollableHeight, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled));
    }

    /// <summary>
    /// 把页面拉到剧照的一半高处，给 <c>--scroll-half</c> 用 —— 「往下拉之后标题颜色要渐变，变的和下方背景一样」
    /// 这一条只在这个位置上看得见。滚到 0 时标题栏那一条一点没洗；拉到底时它整条就是正文那张纸，也就看不出是
    /// 渐变；而那道横缝出在中间。
    /// <para>
    /// 偏移和 <see cref="WashRead"/> 试画那一档用的是同一个数（带子的一半高），所以这里拍下来的一张图和那一条
    /// 读数说的是同一个版面，不是两个看着差不多的状态。读带子自己的高而不是 <c>DetailHero.ArtHeight</c>：集页
    /// 的带子按里面那一叠实测给、比那一档矮，写死那一档就会拍到罩子的另一个位置。
    /// </para>
    /// </summary>
    internal void ScrollToWash()
    {
        Body.UpdateLayout();
        Body.ScrollTo(0, BandHeight / 2, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled));
    }

    /// <summary>
    /// 同 <see cref="ScrollToWash"/>，但先等页面真的长到滚得下去，滚完再把落点读出来。<c>--scroll-half</c> 走这一支。
    /// <para>
    /// 那个开关一度除了开窗什么都没做，根子在时机：它在页面报 <see cref="IsReady"/> 的那一下就下令，而那时候
    /// 内容还只有一个视口高 —— 滚不动，<c>ScrollTo</c> 把偏移夹回 0，而后来内容长高了也没人补滚，于是「洗到
    /// 一半」那一档的截图和滚到 0 的那一张一模一样，看不出坏了。所以这里先等 <c>ScrollableHeight</c> 够得着
    /// 目标，落地后再读一次真正的偏移：还是被夹住的话，报告里直接说出来，不假装拍到了。
    /// </para>
    /// </summary>
    internal async Task<string> ScrollToWashAsync()
    {
        var target = BandHeight / 2;

        var room = await SettleExtentAsync(target).ConfigureAwait(true);
        ScrollToWash();
        var landed = await SettleOffsetAsync(target).ConfigureAwait(true);

        return $"目标 {target:0}，可滚 {room:0}，落在 {landed:0}"
            + (Math.Abs(landed - target) < 1 ? "" : "（内容不够高，偏移被夹住了）");
    }

    /// <summary>
    /// 同 <see cref="ScrollToEnd"/>，但先等内容不再长高。<c>--scroll-end</c> 走这一支。
    /// <para>
    /// 「最底下」是拿当时的 <c>ScrollableHeight</c> 量的，所以带子还没回来时的「底」不是真的底 —— 那一张截图
    /// 里演职人员那条会整条缺掉，而画面本身挑不出错来。
    /// </para>
    /// </summary>
    internal async Task<string> ScrollToEndAsync()
    {
        var room = await SettleExtentAsync(double.PositiveInfinity).ConfigureAwait(true);
        ScrollToEnd();
        var landed = await SettleOffsetAsync(room).ConfigureAwait(true);

        return $"可滚 {room:0}，落在 {landed:0}";
    }

    /// <summary>
    /// 等页面长到滚得下 <paramref name="wanted"/>，或者等它不再长了；返回当时的 <c>ScrollableHeight</c>。
    /// <para>
    /// 「不再长了」的判据是连着五眼一样高、并且至少已经等过一秒 —— 一条带子回来得慢的时候，头几眼的「一样高」
    /// 只说明它还没回来。传 <see cref="double.PositiveInfinity"/> 就是「等它长完」，也就是拉到底要的那个。
    /// </para>
    /// </summary>
    private async Task<double> SettleExtentAsync(double wanted)
    {
        var last = double.NaN;
        var stable = 0;

        for (var attempt = 0; attempt < 60; attempt++)
        {
            Body.UpdateLayout();
            var room = Body.ScrollableHeight;
            if (room >= wanted) return room;

            stable = Math.Abs(room - last) < 0.5 ? stable + 1 : 0;
            last = room;
            if (attempt >= 10 && stable >= 5) return room;

            await Task.Delay(100).ConfigureAwait(true);
        }

        return Body.ScrollableHeight;
    }

    /// <summary>
    /// 等滚动真的走到 <paramref name="wanted"/>；返回停下来的偏移。<c>ScrollTo</c> 是排进队里的一次操作，不是
    /// 一句赋值，所以刚下令那一眼读到的还是原来那个数。
    /// </summary>
    private async Task<double> SettleOffsetAsync(double wanted)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(50).ConfigureAwait(true);
            if (Math.Abs(Body.VerticalOffset - wanted) < 1) break;
        }

        return Body.VerticalOffset;
    }

    /// <summary>
    /// Brings 媒体信息 into view, for the self-check. <see cref="ScrollToEnd"/> lands past it — the table sits
    /// between 单集 and the row of faces — and a repeater whose rows were never in the viewport has drawn
    /// none of them.
    /// <para>
    /// 自己算偏移，不走 <c>StartBringIntoView</c>：那一个走的是「最少滚多少就能看见」，而「最少」有时候是把
    /// 这一块的底对到视口的底 —— 于是表格上面那几行反倒压到视口外去了，而 <c>ItemsRepeater</c> 只建视口盖到
    /// 的那些，读出来就成了「10 行只画了 6 行、首行是第 5 行」。这一格的上沿对到视口的上沿，一句算式说完。
    /// 偏移在内容自己的坐标里量（<c>Body.Content</c>），那正是滚动偏移的单位，所以量出来的数直接就是要滚到
    /// 的地方，和现在滚到哪儿无关。
    /// </para>
    /// </summary>
    internal void ScrollToInfo()
    {
        Body.UpdateLayout();

        if (Body.Content is not UIElement content) return;

        var top = InfoPanel.TransformToVisual(content).TransformPoint(new Point(0, 0)).Y;
        Body.ScrollTo(0, top, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled));
    }

    /// <summary>
    /// Every non-empty piece of text under one node, joined. Used on a realised 媒体信息 row, where it is
    /// the label and its value: 「文 件： Silo.S03E09…」.
    /// </summary>
    private static string Texts(DependencyObject node)
    {
        if (node is TextBlock { Text.Length: > 0 } text) return text.Text;

        var parts = new List<string>();
        var children = VisualTreeHelper.GetChildrenCount(node);
        for (var index = 0; index < children; index++)
        {
            var part = Texts(VisualTreeHelper.GetChild(node, index));
            if (part.Length > 0) parts.Add(part);
        }

        return string.Join(" ", parts);
    }

    /// <summary>
    /// Counts the item controls under one repeater. 单集 draws an <see cref="EpisodeRow"/> and the other
    /// strips draw a <see cref="PosterCard"/>; the self-check's question — 「did the template really draw」 —
    /// is the same for either, so both count as one.
    /// </summary>
    private static int Count(DependencyObject node)
    {
        if (node is PosterCard or EpisodeRow) return 1;

        var cards = 0;
        var children = VisualTreeHelper.GetChildrenCount(node);
        for (var index = 0; index < children; index++)
            cards += Count(VisualTreeHelper.GetChild(node, index));

        return cards;
    }

    /// <summary>
    /// 外壳把窗口递进来 —— <c>ShellPage</c> 挂在 <c>ContentFrame.Navigated</c> 上，每个详情页实例（后退键
    /// 重建的那些也不例外）上树都能领到。领到的第一件事就是把纸面上沿那条线量一遍。
    /// </summary>
    internal void AttachWindow(Windowing.HostWindow window)
    {
        if (ReferenceEquals(_window, window)) return;

        if (_window is not null) _window.GeometryChanged -= OnWindowGeometryChanged;
        _window = window;
        window.GeometryChanged += OnWindowGeometryChanged;
        SyncPaperLine();
    }

    /// <summary>
    /// 换了显示器、改了窗口大小、全屏来回（<see cref="Windowing.HostWindow.GeometryChanged"/> 的全部场合）：
    /// 纸面上沿那条线跟着显示器走（<see cref="DetailHero.PaperLineFor"/>），这些时候都要重算一遍。
    /// </summary>
    private void OnWindowGeometryChanged() => SyncPaperLine();

    /// <summary>
    /// 把纸面上沿那条线送到视图模型：阈值窗口的高（<see cref="DetailHero.PaperLineFor"/>，跟着这台显示器走）
    /// 减掉标题栏加面包屑那一截 —— 那一截只有布好的版面量得出（同 <see cref="LiftBackdrop"/> 的那个数）。
    /// 窗口或显示器还没到手就什么都不做：那一档不撑尾部（<see cref="DetailHero.TailHeight"/> 的约定）。
    /// </summary>
    private void SyncPaperLine()
    {
        if (_window is not { } window) return;
        if (XamlRoot?.Content is not UIElement root) return;

        var (width, height) = window.MonitorSize();
        if (height <= 0) return;

        var lift = TransformToVisual(root).TransformPoint(new Point(0, 0)).Y;
        ViewModel.PaperLine = DetailHero.PaperLineFor(width, height) - lift;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not DetailRequest request)
        {
            Log.Warn(Category, "导航参数缺失，无法确定要显示哪个条目");
            return;
        }

        _request = request;

        // Cleared so the shell's breadcrumb reads the request rather than a leftover tag; see IShellContent.
        Tag = string.Empty;

        // The one place this page resolves anything. Deliberately a single block: it is the line that goes
        // when the shell stops handing a container around.
        var services = request.Services;
        _session = services.GetRequiredService<EmbySession>();
        _actions = services.GetRequiredService<IShellActions>();
        var images = services.GetRequiredService<EmbyImageStore>();
        _images = images;

        ViewModel.Attach(
            request,
            _actions,
            services.GetRequiredService<ISettingsService>(),
            _session,
            images,
            services.GetRequiredService<ISystemLauncher>());

        // 标题栏那几处的字得跟着这一页有没有背景图翻墨色 —— 图现在画进标题栏了（见 LiftBackdrop），黑字压在
        // 一张亮剧照上读不出来。订阅是为了「图是后到的」那一路；这一句显式的调用是为了「页面被复用」那一路：
        // 同一个实例第二次进来时 HeroArt 的值没变，不会有通知，而 OnNavigatedFrom 已经把外壳还回了黑墨。
        // 洗到哪一档也一起重来（复用的实例上是上一个条目留下的数），见 PaintWash。
        ViewModel.PropertyChanged += OnViewModelChanged;
        _wash = -1;
        _paperCover = -1;
        _paintPhase = -1;
        _washedInk = false;
        PaintWash(force: true);

        // 集带永远在纸的第一块（标记里就那么摆的），所以这里只是「万一不在」补一下；尾部那一段的底色跟着
        // 「这一页有没有那块板」走，而那一句要等 Apply 才知道种类（那时会再来一次通知，见 OnViewModelChanged）。
        PaintScrim();
        PlaceEpisodes();

        _ = ViewModel.ReloadAsync();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(DetailViewModel.BackdropShown)) PaintWash(force: true);
        if (args.PropertyName == nameof(DetailViewModel.SublineGenres)) PaintGenres();

        // 集页那块板在不在：尾部那一段的底跟着它换。宽窄跨过 1024 那一条线时变的就是它 —— 那一趟 LayoutPage
        // 也会走，可它管不到这一支。
        if (args.PropertyName == nameof(DetailViewModel.HeroCardShown)) PaintScrim();
    }

    /// <summary>
    /// 自检：详情页的第一屏是不是先用点进来那张卡片画上的，以及完整条目回来后那四张图没被白重取一遍。
    /// 判据全在视图模型里（<see cref="DetailViewModel.PreviewRead"/>），这里只是转一手。
    /// </summary>
    internal (bool Ok, string Detail) PreviewRead() => ViewModel.PreviewRead();

    /// <summary>
    /// 自检：那一行类型真的一个一个点得动 —— 视图模型说有几个，屏上就得有几段 <c>Hyperlink</c>，而且每一段都
    /// 挂着一个去处。
    /// <para>
    /// 值得读，是因为这一行是代码搭的（见 <see cref="PaintGenres"/>）：搭空了、或者只搭出中间那几个「·」，
    /// 屏上是一行看着和以前一模一样的字，点下去什么都不发生 —— 截图挑不出错，别的读数一个都不会响。
    /// 这一条目没有类型（服务器没给）时不判，只报：那一行本来就该是一行普通的字。
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) GenreRead()
    {
        var wanted = ViewModel.SublineGenres;
        if (wanted.Count == 0)
            return (true, $"这一条目没有类型可点（{ViewModel.ItemType} 页，那一行是「{ViewModel.Subline}」）");

        var links = GenreLine.Inlines.OfType<Hyperlink>().ToList();
        var texts = links
            .Select(link => string.Concat(link.Inlines.OfType<Run>().Select(run => run.Text)))
            .ToList();

        var drawn = GenreLine.Visibility == Visibility.Visible;
        var same = texts.Count == wanted.Count && texts.SequenceEqual(wanted, StringComparer.Ordinal);

        // 墨也要和上一行同一支。Hyperlink 自带框架的强调色，忘了压就是这一行独自变亮 —— 而它是整页最大那行片名
        // 底下紧贴的一行，屏上非常扎眼（用户为此提过一次）。比的是画刷对象本身：那一支不跟主题走，全程同一个。
        var wantedInk = ViewModel.SublineBrush;
        var ink = links.All(link => ReferenceEquals(link.Foreground, wantedInk));

        return (drawn && same && ink,
            $"那一行 {wanted.Count} 个类型，屏上 {links.Count} 段可点：{string.Join('、', texts)}"
                + (drawn ? "" : "，可那一行没画出来")
                + (same ? "" : $"，和视图模型那份对不上（要的是 {string.Join('、', wanted)}）")
                + (ink ? "，墨和副标题同一支" : "，墨不是副标题那一支 —— 框架的强调色没压住"));
    }

    /// <summary>
    /// 评分那一格：屏上画的是不是规矩说的那个数和那个署名。
    /// <para>
    /// 值得判红，而且两头都判。「有分的时候画对了」是这次新增那一档设置（评分来源）唯一的屏上出口，而它错起来是
    /// 一个看着挺正常的数：烂番茄的 92 画成「92.0」、一个来路不明的分署上「豆瓣」、或者署名那一格空着。而
    /// 「没分的时候整块收起来」是我们自己的规矩（<c>ScoreVisibility</c> 盯着 <c>Score</c> 是不是空串），漏了它的
    /// 样子是上一个条目留下的「★ 8.4 豆瓣」挂在一个没有评分的条目上 —— 截图看不出来，没人会去比。
    /// </para>
    /// <para>
    /// 服务器那一头有什么只报不判（那是服务器的事），可它是这一关最值得读的一句：这台服务器到底有没有豆瓣的痕迹、
    /// 有没有影评指数，从此每次自检都会自己回答一遍。
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) ScoreRead()
    {
        var wanted = ViewModel.ScoreBadge;
        var facts = ViewModel.ScoreFacts;

        if (!wanted.Any)
            return (ScoreStack.Visibility == Visibility.Collapsed && ScoreText.Text.Length == 0,
                $"这一条目服务器上没有评分，那一格{(ScoreStack.Visibility == Visibility.Collapsed ? "整块收起来了" : "却还画着")}"
                    + $"（{facts}）");

        var drawn = ScoreStack.Visibility == Visibility.Visible;
        var text = string.Equals(ScoreText.Text, wanted.Text, StringComparison.Ordinal);
        var label = string.Equals(ScoreSourceText.Text, wanted.Label, StringComparison.Ordinal);

        // 那一行事实是紧挨着它排的，星和署名一多就往右推 —— 而评分那一组的宽有上限（三位数加两到四个字），出界
        // 只可能是版面错了。事实那一行本身长短由服务器给的字决定，出界只报不判。
        var room = HeroStack.ActualWidth;
        var score = ScoreStack.TransformToVisual(HeroStack).TransformPoint(new Point(ScoreStack.ActualWidth, 0)).X;

        return (drawn && text && label && score <= room + 0.5,
            $"设置里选的是「{ViewModel.ScoreSourceName}」，规矩说画「{wanted.Text}」＋「{wanted.Label}」"
                + $"；屏上是「{ScoreText.Text}」＋「{ScoreSourceText.Text}」"
                + $"，占到 {score:F0}／可用 {room:F0}"
                + (drawn ? "" : "，可那一格没画出来")
                + $"；服务器给的：{facts}");
    }

    /// <summary>
    /// 副标题那一行的类型，一个类型一个入口 —— 点一个就是一格「这个类型下的全部影片和剧集」。
    /// <para>
    /// 在代码里搭而不是绑出来：能点的内联元素只有 <see cref="Hyperlink"/>，而内联元素不是
    /// <c>UIElement</c>，<c>ItemsControl</c> 装不了它们，<c>x:Bind</c> 也接不上它的 <c>Click</c>。
    /// 换成一排按钮就要放弃那一行的样子（一行字、中间「·」隔开、超长省略号），而那一行紧贴在整页最大的
    /// 片名底下，它多高、断在哪儿都是版面的事。
    /// </para>
    /// <para>
    /// **字色显式压成副标题那一支**（<see cref="DetailViewModel.SublineBrush"/>）。这一句非写不可：
    /// <see cref="Hyperlink"/> 自带框架的链接色（强调色，这套主题下是偏亮的绿），它盖在 <c>GenreLine</c> 从标记里
    /// 继承下来的那一支上面 —— 于是这一行从「和上一行同一支暗墨」变成了整个头部最扎眼的一行。用户看出来的就是这个
    /// （「想要它跟以前一样暗」）。不画下划线同理，变的只该是「指针移上去是一只手、按得下去」。
    /// </para>
    /// <para>
    /// 分隔符那几段用普通 <c>Run</c>，所以它们不可点 —— 点在两个类型中间的空当上不该开出一格来。
    /// </para>
    /// </summary>
    private void PaintGenres()
    {
        GenreLine.Inlines.Clear();

        foreach (var genre in ViewModel.SublineGenres)
        {
            if (GenreLine.Inlines.Count > 0) GenreLine.Inlines.Add(new Run { Text = "  ·  " });

            var link = new Hyperlink
            {
                UnderlineStyle = UnderlineStyle.None,
                Foreground = ViewModel.SublineBrush
            };
            link.Inlines.Add(new Run { Text = genre });

            // 捕获一份自己的：委托跑起来的时候循环那个变量已经走到下一个了。
            var name = genre;
            link.Click += (_, _) => ViewModel.OpenGenre(name);

            GenreLine.Inlines.Add(link);
        }
    }

    public void Release() => ViewModel.Cancel();

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelChanged;

        // 白墨是这一页的事，走的时候得还回去：下一页头上没有那张图，一行白字压在浅色的面上就没了。
        _actions?.SetTitleStrip(TitleStrip.Plain);

        Release();
        base.OnNavigatedFrom(e);
    }

    /// <summary>
    /// The one fact that has to travel from the view back to the view model. Whether the synopsis needed
    /// more than four lines is not knowable until the text has been measured at the width it was given,
    /// and only the element knows that width.
    /// </summary>
    private void OnOverviewTrimmedChanged(object sender, object e) =>
        ViewModel.OverviewTrimmed = Overview.IsTextTrimmed;

    /// <summary>
    /// 音轨显隐、简介折行或「阅读更多」都会改变黑色尾部的高度，也就会移动真正纸面的上沿。滚动位置没变时
    /// <see cref="ScrollView.ViewChanged"/> 不会再来一次，所以在这里主动重画标题栏。
    /// </summary>
    private void OnHeroTailSizeChanged(object sender, SizeChangedEventArgs e) => PaintWash(force: true);

    /// <summary>
    /// 带子里那一栏（徽标、片名、副标题、读数、那行视频）有多高 —— 片名折成两行、窗口换窄都会变。集页那一格
    /// 的高由它给，见 <see cref="DetailViewModel.HeroRoom"/>。
    /// <para>
    /// 这一栏靠上站（<see cref="DetailViewModel.HeroContentAlignment"/>），而且不是拉伸的那种，所以它的
    /// <c>ActualHeight</c> 就是它自己要的高，不是这一格给它的高 —— 拉伸的那种量出来永远等于带子，带高也就永远
    /// 等于当前值，一个自己咬着自己的数。
    /// </para>
    /// <para>
    /// 只报量出来的这一个数。「海报也算进去」「那一行键也算进去」和「加上这一格上下那两道留白」都搬去了视图模型
    /// （<see cref="DetailViewModel.HeroRoom"/>）：海报按图自己的形状收窄之后它的高会变，而那一下这一栏一个
    /// 像素没动、这个回调也就不会来 —— 算在这儿的那一版于是留着一格比内容高出一截的带子。
    /// </para>
    /// <para>
    /// 那排键 2026-09-12 从这一栏里搬出去、自己占一行，所以它的高由 <see cref="OnHeroActionsSizeChanged"/>
    /// 另报一次；这一栏量到的是搬完之后的那个（少了四十来个像素），两处加起来才是从前那一笔账。
    /// </para>
    /// </summary>
    private void OnHeroStackSizeChanged(object sender, SizeChangedEventArgs e) =>
        ViewModel.StackRoom = HeroStack.ActualHeight;

    /// <summary>
    /// 头图上那排键自己那一行有多高 —— 它 2026-09-12 从片名那一栏里搬出来、单独占一行（「继续播放 从头开始
    /// 还有后面的那些图标单独一行」），所以它的高不再算在 <see cref="DetailViewModel.StackRoom"/> 里，得单独
    /// 报上去（<see cref="DetailViewModel.ActionsRoom"/>，进带高的账）。报的是连头上那道 16 边距在内的
    /// 排版高：Auto 行按 desired 摆它（含边距），带高的账和排出来的行不一致的话，差的那一截就从带子底下
    /// 冒出去。
    /// <para>
    /// 紧凑版式里这一行整个收着（<c>WideActionsVisibility</c>），量出来就是 0 —— 「紧凑版式不多算这一行」
    /// 不用另判一次版式，收着的东西量出来本来就是零；收着的那一拍这里只报 0，不把边距带上。
    /// </para>
    /// </summary>
    private void OnHeroActionsSizeChanged(object sender, SizeChangedEventArgs e) =>
        ViewModel.ActionsRoom = HeroActions.Visibility == Visibility.Visible
            ? HeroActions.ActualHeight + HeroActions.Margin.Top
            : 0;

    /// <summary>
    /// 同季那一带集永远是正文那张纸的第一块 —— 媒体信息紧跟在它后面（见 BodySheet 那段注释）。
    /// <para>
    /// <b>它从前会搬家，2026-09-12 起不搬了。</b>旧版：集页那一档压在头图底下那段画面里、排在剧情说明后面
    /// （「把集页面的剧情说明和集列表位置调换」），别的页面上在纸上。集页整段上方合成一块大面板（<c>HeroCard</c>）
    /// 之后，一带集再压在那一格里就成了「板里套板」，他一句「集的框要和上面分开」定了这一版 —— 那一带对所有页面
    /// 都回到纸上，和剧页面那一带站在同一层、同一套墨、同一圈框（那也正是他要的「剧页面那种样式」）。
    /// </para>
    /// <para>
    /// 旧账记在这儿，因为它买过两个 bug：搬家（`d34275d`，2026-09-01）留下过「点击开始播放后点左上角返回，
    /// 集列表会跑到下方去」—— 停止播放时外壳先照着当前页面重新导航一遍、之后才把外壳放回来，那一趟
    /// <c>OnNavigatedTo</c> 在一棵收着的树上跑完；而「现在在哪一层」从前只能读 <c>Parent</c>（要等 <c>Loaded</c>，
    /// 那时候还没有值），于是搬家那一步悄悄什么都不做。现在不搬家，这一整类竞态不存在了。
    /// </para>
    /// <para>
    /// 标记里就是那么摆的（<c>EpisodePanel</c> 写在 <c>BodySheet</c> 的第一个），所以这一句现在只是把
    /// 「万一它不在那儿」补上 —— 页面实例会复用，搬过家的旧版本留下的位置不该跟着进下一页。
    /// </para>
    /// </summary>
    private void PlaceEpisodes()
    {
        if (BodySheet.Children.Contains(EpisodePanel)) return;

        BodySheet.Children.Insert(0, EpisodePanel);
    }

    /// <summary>
    /// The second fact that has to travel from the view back to the view model: how tall this page's visible
    /// area is. 正文那张纸的下限是它减掉头上那一格 —— 「滑到下面不用显示背景了」，而内容短的页面上只有这个数
    /// 能把看得见的那一段填满。Only a laid-out <c>ScrollView</c> knows its own viewport; what to do with the
    /// number is <see cref="DetailHero.BodyHeight"/>'s business, not this handler's.
    /// </summary>
    private void OnBodySizeChanged(object sender, SizeChangedEventArgs e)
    {
        ViewModel.Viewport = Body.ActualHeight;

        // 同一趟量的另一个数：这一页有多宽。紧凑版式换不换（DetailHero.IsCompact）、背景那一张画多高
        // （DetailViewModel.PictureHeight）都问它 —— 「多宽」只有布好的版面知道。
        ViewModel.PageWidth = Body.ActualWidth;

        // 宽高都变了，裁切模糊的档位跟着重算（DetailViewModel.UpdateHeroBlur）：换档它自己会用缓存的像素
        // 重糊，不换什么都不发生。
        ViewModel.UpdateHeroBlur();

        // 同一件事的另一头：这个滚动视图变高变矮，正是因为它上面那行面包屑出现或收起，也就是背景那一层该往上
        // 顶多少变了的那一刻。见 LiftBackdrop。
        LiftBackdrop();
    }

    /// <summary>
    /// 把背景那一层顶到窗口的上沿 —— 「跟主页一样，占满标题栏」。
    /// <para>
    /// 这一页坐在外壳的工作区里，头上压着两样东西：32 高的标题栏（外壳给内容区留的那道内边距），和面包屑那一
    /// 行。要让图画到窗口的最上面，就得把那一层往回挪这两样加起来那么多；而「加起来是多少」只有布好的版面知道
    /// （面包屑那行时有时无，行高还跟着主题的字号走），所以这里量出来再写回去，不写死一个数。
    /// </para>
    /// <para>
    /// 挪之前先比一比，一样就不写：改边距会再引一次布局，而这个方法自己就挂在布局的事件上 —— 不比就是一个自己
    /// 喂自己的循环。
    /// </para>
    /// </summary>
    private void LiftBackdrop()
    {
        if (XamlRoot?.Content is not UIElement root) return;

        var lift = TransformToVisual(root).TransformPoint(new Point(0, 0)).Y;

        // 纸面上沿那条线用的是同一个数（阈值窗口高减这一截），所以赶在提前返回之前同步 —— 面包屑显隐、换主题
        // 都会挪它，而那些时候边距未必变。见 SyncPaperLine。
        SyncPaperLine();

        if (Math.Abs(Backdrop.Margin.Top + lift) < 0.5) return;

        Backdrop.Margin = new Thickness(0, -lift, 0, 0);

        // 洗的那一层盖的就是让开的这一条 —— 同一个数，量到了一起写。见 PaintWash。
        TitleWash.Height = Math.Max(0, lift);
        PaintWash(force: true);
    }

    /// <summary>滚到哪儿了 —— 标题栏依次跟随头图罩子、黑色尾部和真正正文纸面。</summary>
    private void OnBodyViewChanged(ScrollView sender, object args) => PaintWash();

    /// <summary>
    /// 把标题栏那一条画成当前正从它下面经过的那一层。
    /// <para>
    /// 现在有三段而不是原来的两段：头图里的渐深罩子、用户要求延长到剧情说明下面的黑色 HeroTail、以及从红线
    /// 开始的主题 BodySheet。<see cref="DetailHero.TopWash"/> 只负责第一段；第二段直接复用 HeroTail 的画刷；
    /// 第三段的上沿滚进标题栏时，用一支硬边渐变按真实位置把黑色和纸色各画一半，完全盖住之后才直接复用纸面画刷。
    /// </para>
    /// <para>
    /// 真正纸面的位置不能再由固定的 460 推断：音轨会显隐，简介会折行和展开。<see cref="PaperOffset"/> 每次都
    /// 取布局后的实际高度，<see cref="OnHeroTailSizeChanged"/> 则保证高度变化但滚动位置没变时也会重画。
    /// </para>
    /// </summary>
    /// <param name="force">
    /// 换主题、换条目、量完高度之后要重画一遍 —— 那几处滚动位置没动，可是颜色或高度动了。
    /// </param>
    private void PaintWash(bool force = false) => ApplyWash(Body.VerticalOffset, force);

    /// <summary>真正主题纸面在滚动内容里的纵坐标：头图带子加上黑色尾部的实测高度。</summary>
    private double PaperOffset() => BandHeight + HeroTail.ActualHeight;

    /// <summary>
    /// <see cref="PaintWash"/> 的后半截。接收滚动位置而不是单独一个浓度，因为浓度爬到顶（
    /// <see cref="DetailHero.ScrimCeiling"/>）以后还隔着一整段同色的 HeroTail，只有位置同时越过
    /// <see cref="PaperOffset"/> 才能换成主题纸色。
    /// </summary>
    private void ApplyWash(double offset, bool force)
    {
        var artwork = ViewModel.BackdropShown;
        var wash = DetailHero.TopWash(offset, artwork, BandHeight);
        var paperTop = PaperOffset();
        var paperCover = artwork
            ? DetailHero.PaperCover(offset, paperTop, TitleWash.ActualHeight)
            : 0;
        var heroBottom = BandHeight;
        var phase = !artwork ? 0
            : paperCover >= 1 ? 4
            : paperCover > 0 ? 3
            : offset >= heroBottom ? 2
            : 1;

        // 一滚就会来一次；罩子和纸面覆盖都按 255 档去重，跨阶段则无条件换画刷。
        if (!force && phase == _paintPhase
            && Math.Abs(wash - _wash) * 255 < 1
            && Math.Abs(paperCover - _paperCover) * 255 < 1)
            return;

        _wash = wash;
        _paperCover = paperCover;
        _paintPhase = phase;
        SyncTitleInk(paperCover);

        if (!artwork)
        {
            TitleWash.Background = null;
            return;
        }

        if (HeroTail.Background is not SolidColorBrush { Color: var tail }
            || BodySheet.Background is not SolidColorBrush { Color: var sheet })
            return;

        if (phase == 4)
        {
            TitleWash.Background = BodySheet.Background;
            return;
        }

        if (phase == 3)
        {
            UsePaperBrush(paperCover, tail, sheet);
            return;
        }

        if (phase == 2)
        {
            TitleWash.Background = HeroTail.Background;
            return;
        }

        UseScrimBrush(wash, tail);
    }

    /// <summary>头图罩子经过标题栏时，按屏上同一条曲线压同样浓的黑。</summary>
    private void UseScrimBrush(double wash, Color tail)
    {
        var alpha = (byte)Math.Round(wash * 255);
        var scrim = HeroScrimBrush.GradientStops[^1].Color;

        if (TitleWash.Background != _washBrush)
        {
            _washBrush.GradientStops.Clear();
            _washBrush.GradientStops.Add(new GradientStop { Offset = 0 });
            _washBrush.GradientStops.Add(new GradientStop { Offset = 0 });
            _washBrush.GradientStops.Add(new GradientStop { Offset = 1 });
            TitleWash.Background = _washBrush;
        }

        _washBrush.GradientStops[0].Color = Color.FromArgb(alpha, tail.R, tail.G, tail.B);
        _washBrush.GradientStops[1].Color = Color.FromArgb(alpha, tail.R, tail.G, tail.B);
        _washBrush.GradientStops[1].Offset = wash;
        _washBrush.GradientStops[2].Color = Color.FromArgb(alpha, scrim.R, scrim.G, scrim.B);
    }

    /// <summary>纸面上沿滚进标题栏时，按它的真实位置画一道黑色到主题纸色的硬边。</summary>
    private void UsePaperBrush(double cover, Color tail, Color sheet)
    {
        if (TitleWash.Background != _paperBrush)
        {
            _paperBrush.GradientStops.Clear();
            _paperBrush.GradientStops.Add(new GradientStop { Offset = 0 });
            _paperBrush.GradientStops.Add(new GradientStop { Offset = 1 });
            _paperBrush.GradientStops.Add(new GradientStop { Offset = 1 });
            _paperBrush.GradientStops.Add(new GradientStop { Offset = 1 });
            TitleWash.Background = _paperBrush;
        }

        var edge = 1 - cover;
        _paperBrush.GradientStops[0].Color = tail;
        _paperBrush.GradientStops[1].Color = tail;
        _paperBrush.GradientStops[1].Offset = edge;
        _paperBrush.GradientStops[2].Color = sheet;
        _paperBrush.GradientStops[2].Offset = edge;
        _paperBrush.GradientStops[3].Color = sheet;
    }

    /// <summary>
    /// 标题栏那一条上的字用哪套墨。两件事一起决定：这一页头上有没有那张剧照，以及真正纸面已经覆盖标题栏多少
    /// （<see cref="DetailHero.WashedOver"/>）—— 黑色 HeroTail 上仍用白墨，浅色主题纸面盖上来之后才换回主题墨。
    /// <para>
    /// 洗过之后报的是 <see cref="TitleStrip.PagePainted"/>，不是「没有图」那一档：墨要跟着回到主题那支，可
    /// 面包屑那一行仍旧不能自己上底色 —— 这一条的底是这一页洗出来的，外壳再涂一层页面底色，它就比正文亮出
    /// 四五级，「变的和下方背景一样」那一条上会重新出一道横缝，横缝就在面包屑那一行的上沿。
    /// </para>
    /// </summary>
    private void SyncTitleInk(double paperCover)
    {
        _washedInk = DetailHero.WashedOver(paperCover, _washedInk);

        _actions?.SetTitleStrip(!ViewModel.BackdropShown
            ? TitleStrip.Plain
            : _washedInk ? TitleStrip.PagePainted : TitleStrip.OnScrim);
    }

    /// <summary>
    /// The menu is a view concern: its anchor is a laid-out control and its entries are WinUI flyout
    /// elements. The view model only supplies the currently loaded domain item and the sibling episodes
    /// needed by the shared command builder.
    /// <para>
    /// 锚点是<em>按下来的那一颗</em>，不是写死的某一名字：宽版式和紧凑版式各有一颗更多键（<c>MoreButton</c>、
    /// <c>CompactMoreButton</c>），菜单都要开在按下的那颗边上 —— 菜单挂错了锚，弹出来的位置就漂到另一套版式
    /// 那一头去。
    /// </para>
    /// </summary>
    private void OnMoreClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button anchor
            || _session is not { } session
            || _actions is not { } actions
            || _images is not { } images
            || ViewModel.CurrentItem is not { } item)
            return;

        var card = new CardItem(item, images, CardSize.PosterWidth);
        ItemCommands.Show(
            session,
            actions,
            anchor,
            card,
            position: null,
            siblings: ViewModel.Episodes,
            changed: ViewModel.Reload);
    }

    /// <summary>
    /// An item in any of the four sections — 单集, 全部剧季, 更多类似 and 演职人员. One handler because the
    /// question a click asks is the same everywhere on this page: open the thing that was clicked, which the
    /// shell answers. Two shapes of content because 单集 is a list of rows and the other three are strips of
    /// cards; what may happen *beside* a click still differs — a person gets no context menu and no hover
    /// strip — and that difference is in the markup, where it is visible.
    /// </summary>
    private void OnCardClicked(object sender, RoutedEventArgs e)
    {
        var card = sender switch
        {
            Button { Content: PosterCard { Card: { } poster } } => poster,
            Button { Content: EpisodeRow { Card: { } row } } => row,
            _ => null
        };

        if (card is not null) ViewModel.OpenCard(card);
    }

    private void OnCardContextRequested(UIElement sender, ContextRequestedEventArgs args) =>
        ItemCommands.Handle(_session, _actions, sender, args, ViewModel.Episodes, ViewModel.Reload);

    private void OnCardActionRequested(object? sender, CardActionEventArgs args) =>
        ItemCommands.Handle(_session, _actions, sender, args, ViewModel.Episodes, ViewModel.Reload);
}

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
        HeroTail.Background = new SolidColorBrush(HeroScrimBrush.GradientStops[^1].Color);
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
    /// 自检：集页的第一屏 —— 参考图上那两支往上的箭头加「音频字幕和集数的位置调换」：那一叠字和键不能被带子挤
    /// 出去、带子也不许比那一叠高（「集拉大窗口后会导致左上角空空的」），音轨那一行和同季那一带集要落在这一页看
    /// 得见的那一段里、剧情说明至少露出头，而那一带排在音轨底下、剧情说明上面。
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
        var actions = Box(HeroActions);
        var tail = Box(HeroTail);
        var room = Math.Max(ViewModel.HeroRoom, DetailHero.EpisodeFloor);
        var roomy = hero.Height + tail.Height <= page.Height + 0.5;

        // 一头一条，合起来是「带子正好那一叠那么高」：那一叠字往上撑（它是底对齐的）、那排键在它最底下，所以
        // 收窄过头两头都读得出来；而带子不许比那一叠还高 —— 高出来那一截全落在底对齐的那一叠头上，也就是
        // 「左上角空空的」。两条都无条件判：带高跟窗口有多大没关系（见 DetailHero.EpisodeHeight）。
        var fits = stack.Top >= hero.Top - 0.5 && actions.Bottom <= hero.Bottom + 0.5;
        var snug = hero.Height <= room + 0.5;
        var read = new List<string>();
        var ok = fits && snug;

        foreach (var (name, panel) in new[]
        {
            ("音轨", (FrameworkElement)PickerPanel),
            ("集带", EpisodePanel),
            ("剧情说明", OverviewPanel)
        })
        {
            if (panel.Visibility != Visibility.Visible)
            {
                read.Add($"{name}这一条目没有");
                continue;
            }

            var box = Box(panel);

            // 剧情说明只要求「露头」：四行折起来的一段加上前面那三块，在矮窗口上凑不出一屏，而这一条问的是
            // 「进页面看不看得见它」—— 见得到开头就能往下读，整段都在屏外才是坏的。
            var inside = name == "剧情说明"
                ? box.Top <= page.Bottom - 24
                : box.Bottom <= page.Bottom + 0.5;

            ok &= inside || !roomy;
            read.Add($"{name} {box.Top:0}–{box.Bottom:0}{(inside ? "" : "（出屏）")}");
        }

        // 顺序那一条：那一带在音轨底下、剧情说明上面。压在图上的那一档还要没有板底和外圈 ——
        // 「去掉集列表的黑边」，同 BodySeal 里那两块读的 Bare。
        if (EpisodePanel.Visibility == Visibility.Visible && ViewModel.EpisodesOnScrim)
        {
            var order = Box(EpisodePanel).Top >= Box(PickerPanel).Bottom - 0.5
                || PickerPanel.Visibility != Visibility.Visible;
            var above = OverviewPanel.Visibility != Visibility.Visible
                || Box(EpisodePanel).Bottom <= Box(OverviewPanel).Top + 0.5;
            var bare = EpisodePanel.Background is null or SolidColorBrush { Color.A: 0 }
                && EpisodePanel.BorderThickness is { Left: 0, Top: 0, Right: 0, Bottom: 0 };

            ok &= order && above && bare;
            read.Add(order && above ? "集带夹在音轨和剧情说明之间" : "集带没夹在音轨和剧情说明之间");
            read.Add(bare ? "集带外圈已去掉" : "集带仍有外圈或底色");
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
    /// 需求 4 as the title band came out: whether the 徽标 mark is on screen and how large it drew, and —
    /// since 「把当前页面徽标所在地方替换为剧名，徽标移动到右上角」 — whether it really is in the band's
    /// top-right corner with the words left to the text title.
    /// <para>
    /// Worth reading off the elements rather than off the view model because the claim is about the markup, and
    /// it changed shape: the two used to be bound to two opposite properties (plate or text, never both), so
    /// what needed checking was that they were opposite. Now the text title is unconditional and the mark is
    /// merely another element in the band, which makes the checkable claim geometric — the mark's box sits in
    /// the upper-right quarter of the band and does not touch the title's. 「no name on the hero band」 and
    /// 「the mark landed on the words」 both look like artwork that never arrived, and neither is visible in a
    /// screenshot of an item whose server holds no 徽标 at all.
    /// </para>
    /// <para>
    /// The size is what says the 220×64 cap did the shrinking — an unbounded 徽标 comes down from the server a
    /// thousand pixels wide. Both boxes are read in the band's own coordinates, so the numbers in the report
    /// are where a reader would point on the picture.
    /// </para>
    /// </summary>
    internal (bool Plate, bool Text, double Width, double Height, bool Corner, string Where) TitleShapes
    {
        get
        {
            var plate = CornerPlate.Visibility == Visibility.Visible;
            var text = TitleText.Visibility == Visibility.Visible;

            // Nothing to place and nothing to collide with: a page whose item has no 徽标 is the ordinary
            // case, and the corner claim is vacuous rather than failed.
            if (!plate)
            {
                return (false, text, CornerPlate.ActualWidth, CornerPlate.ActualHeight, true, "徽标没画");
            }

            var mark = Box(CornerPlate);
            var title = Box(TitleText);
            var upperRight = mark.Top < HeroBand.ActualHeight / 2 && mark.Right > HeroBand.ActualWidth / 2;

            // Two rects that do not intersect: whichever way the title wraps, the mark is not on it. Asked of
            // the boxes rather than of「the mark is above the title」 because a two-line title on an episode
            // page reaches higher than the mark's own bottom edge — they clear each other by column there.
            var clear = mark.Right <= title.Left || title.Right <= mark.Left
                || mark.Bottom <= title.Top || title.Bottom <= mark.Top;

            return (true, text, CornerPlate.ActualWidth, CornerPlate.ActualHeight, upperRight && clear,
                $"徽标 {mark.Left:0},{mark.Top:0} 到 {mark.Right:0},{mark.Bottom:0}、"
                    + $"片名 {title.Left:0},{title.Top:0} 到 {title.Right:0},{title.Bottom:0}"
                    + $"（带 {HeroBand.ActualWidth:0}×{HeroBand.ActualHeight:0}）"
                    + (upperRight ? "" : "，不在右上角")
                    + (clear ? "" : "，压到片名了"));

            // 两个盒子都换算到带自己的坐标里，报告里的数就是「在图上指哪儿」。
            Rect Box(FrameworkElement element) => element
                .TransformToVisual(HeroBand)
                .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }
    }

    /// <summary>
    /// 自检：头图上那两行字真解析到的字体和字号 —— 片名和那行读数。
    /// <para>
    /// 值得单独读一遍，是因为这两行的字全交给样式给，而样式漏一个 <c>Setter</c> 属于屏上看得见、截图里
    /// 看不出的坏法：这一版之前的片名写死了 34 和 SemiBold 却没写字体，于是整页最大的一块字用的是继承
    /// 来的正文字。比的是元素真解析到的族名和它该有的那个键，所以 Bahnschrift 装没装都不影响这条的判断
    /// —— 那件事由 字体已解析 那条管。
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
        var size = (double)Application.Current.Resources["EgDisplayFontSize"];

        var ok = TitleText.FontFamily.Source == display
            && Math.Abs(TitleText.FontSize - size) < 0.01
            && HeroFacts.FontFamily.Source == data;

        return (ok,
            $"片名 {TitleText.FontFamily.Source} {TitleText.FontSize:0}、"
                + $"读数 {HeroFacts.FontFamily.Source} {HeroFacts.FontSize:0}"
                + (ok ? "" : $" —— 要的是片名「{display}」{size:0}、读数「{data}」"));

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
    /// 自检：那张剧照是不是真铺满了这一页，而且真固定在背景里 —— 「让背景图填满页面，别只显示一个框」加上
    /// 「固定在背景中，而不是往下翻页就消失了，而且要跟主页一样，占满标题栏」。
    /// <para>
    /// 值得读，是因为这几件事在截图里看得见、在别的任何读数里都看不出来，而它们各自都能悄悄失效：外边距归了
    /// 零、头上那一格的高走了另一档、把背景那一层顶上去的那个负边距（<see cref="LiftBackdrop"/>）。往下滚以后
    /// 看不见这张图是正文遮的，不是它走了 —— 那件事是另一条读数（<see cref="BodySeal"/>）。
    /// </para>
    /// <para>
    /// 关键是这一条读在自检把页面滚到底之后（<c>ShellSelfCheck</c> 先 <see cref="ScrollToEnd"/>，再读详情）：
    /// 那时背景层的上沿还量在窗口的 0 上，一句话答完两件事 —— 它没跟着滚走，而且它一直画进标题栏。跟着滚的
    /// 那一版在这里会报一个很负的数。
    /// </para>
    /// <para>
    /// 判据取 <see cref="DetailViewModel.HeroArt"/>（服务器上有没有这张图）而不是解出来的位图：版面按前者分
    /// 档，拿后者当判据会在一次解码失败上要求 380，而页面正按 460 往上布着。没有那张图的条目上背景层整个是收
    /// 起的，量它的上沿没有意义，所以那一档只比带子那一格。
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) HeroFill()
    {
        var art = ViewModel.HeroArt;

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

        // 量的是 Backdrop 那一层而不是里面那张 Image：图还在路上时这一层照样站着（见 HeroArtVisibility），
        // 而这一条问的是「那一层在窗口的哪儿」。左沿不比 —— 页面左边是侧边栏那道竖线，图本来就不该盖过去。
        var box = Backdrop.TransformToVisual(root)
            .TransformBounds(new Rect(0, 0, Backdrop.ActualWidth, Backdrop.ActualHeight));
        var window = XamlRoot.Size;
        var top = Math.Abs(box.Top) < 1.5;
        var full = box.Bottom >= window.Height - 1.5;

        return (wide && tall && top && full,
            detail + $"、背景层 {box.Left:0},{box.Top:0} 到 {box.Right:0},{box.Bottom:0}"
                + $"（窗口 {window.Width:0}×{window.Height:0}，已滚到底）"
                + (wide ? "" : "，没通到两边")
                + (tall ? "" : $"，高该是 {want:0}")
                + (top ? "" : "，背景层没顶到窗口上沿（跟着滚走了？）")
                + (full ? "" : "，背景层没铺到窗口下沿"));
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
    /// 走的下限（<see cref="DetailViewModel.BodyMinHeight"/>）。这一条读的是那个下限真的生效了，不是内容碰巧
    /// 够长 —— 短页面上根本滚不动，纸的下沿就得自己站到窗口的下沿上。
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
        var overview = OverviewPanel.TransformToVisual(root)
            .TransformBounds(new Rect(0, 0, OverviewPanel.ActualWidth, OverviewPanel.ActualHeight));
        var page = Body.TransformToVisual(root)
            .TransformBounds(new Rect(0, 0, Body.ActualWidth, Body.ActualHeight));
        var window = XamlRoot.Size;

        var left = box.Left <= page.Left + 0.5;
        var right = box.Right >= page.Right - 0.5;
        var below = box.Bottom >= window.Height - 0.5;
        var opaque = BodySheet.Background is SolidColorBrush { Color.A: 255 };
        var moved = Math.Abs(box.Top - tail.Bottom) < 0.5
            && (OverviewPanel.Visibility != Visibility.Visible
                || box.Top >= overview.Bottom - 0.5 && box.Top - overview.Bottom <= HeroTail.Padding.Bottom + 0.5);
        var joined = HeroTail.Background is SolidColorBrush { Color: var tailColor }
            && tailColor == HeroScrimBrush.GradientStops[^1].Color;
        var pickersBare = Bare(PickerPanel);
        var overviewBare = Bare(OverviewPanel);

        return (left && right && below && opaque && moved && joined && pickersBare && overviewBare,
            $"正文 {box.Left:0},{box.Top:0} 到 {box.Right:0},{box.Bottom:0}，"
                + $"页面 {page.Left:0},{page.Right:0}、窗口高 {window.Height:0}（已滚到底）"
                + (opaque ? "，底色不透明" : "，底色透光")
                + (moved ? "，分界在剧情说明下面" : $"，分界没贴住尾部（尾部到底 {tail.Bottom:0}、简介到底 {overview.Bottom:0}）")
                + (joined ? "，尾部接住头图末色" : "，尾部和头图末色不同")
                + (pickersBare ? "，音轨外圈已去掉" : "，音轨仍有外圈或底色")
                + (overviewBare ? "，剧情说明外圈已去掉" : "，剧情说明仍有外圈或底色")
                + (left ? "" : "，左边露出背景")
                + (right ? "" : "，右边露出背景")
                + (below ? "" : $"，底下露出背景 {window.Height - box.Bottom:0}"));

        static bool Bare(StackPanel panel)
        {
            var border = panel.BorderThickness;
            var noBorder = border.Left == 0 && border.Top == 0 && border.Right == 0 && border.Bottom == 0;
            var noFill = panel.Background is null or SolidColorBrush { Color.A: 0 };
            return noBorder && noFill;
        }
    }

    /// <summary>
    /// 自检：「媒体源／音频／字幕」那三个下拉有没有被窗口右沿切掉。
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
        if (PickerPanel.Visibility != Visibility.Visible) return (true, "这一条目没有文件选项那一行");

        var panel = PickerPanel.TransformToVisual(root)
            .TransformBounds(new Rect(0, 0, PickerPanel.ActualWidth, PickerPanel.ActualHeight));
        var edge = panel.Right - PickerPanel.Padding.Right;
        var window = XamlRoot.Size;

        var read = new List<string>();
        var rows = new List<double>();
        var lefts = new List<double>();
        var ok = true;

        foreach (var (name, picker) in new[]
        {
            ("媒体源", SourcePicker),
            ("音频", AudioPicker),
            ("字幕", SubtitlePicker)
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
        var contentLeft = panel.Left + PickerPanel.Padding.Left;
        var drift = lefts.Min() - contentLeft;
        ok &= drift < 1.5;

        return (ok, $"{string.Join("、", read)}，可用 {contentLeft:0}–{edge:0}"
            + $"（窗口宽 {window.Width:0}），摆成 {rows.Count} 行"
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

        // 那一带集摆在图上还是纸上，也是复用实例那一路要显式说一遍的事：上一个条目可能是另一种页面，而这一次
        // 的种类要等 Apply 才知道 —— 那时会再来一次通知（见 OnViewModelChanged）。
        PlaceEpisodes(ViewModel.EpisodesOnScrim);

        _ = ViewModel.ReloadAsync();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(DetailViewModel.HeroArt)) PaintWash(force: true);
        if (args.PropertyName == nameof(DetailViewModel.EpisodesOnScrim))
            PlaceEpisodes(ViewModel.EpisodesOnScrim);
        if (args.PropertyName == nameof(DetailViewModel.SublineGenres)) PaintGenres();
    }

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

        return (drawn && same,
            $"那一行 {wanted.Count} 个类型，屏上 {links.Count} 段可点：{string.Join('、', texts)}"
                + (drawn ? "" : "，可那一行没画出来")
                + (same ? "" : $"，和视图模型那份对不上（要的是 {string.Join('、', wanted)}）"));
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
    /// 不画下划线：这一行的字色、字号跟着 <see cref="DetailViewModel.SublineBrush"/> 走，和改之前一模一样，
    /// 变的只有「指针移上去是一只手、按得下去」。分隔符那几段用普通 <c>Run</c>，所以它们不可点 —— 点在两个
    /// 类型中间的空当上不该开出一格来。
    /// </para>
    /// </summary>
    private void PaintGenres()
    {
        GenreLine.Inlines.Clear();

        foreach (var genre in ViewModel.SublineGenres)
        {
            if (GenreLine.Inlines.Count > 0) GenreLine.Inlines.Add(new Run { Text = "  ·  " });

            var link = new Hyperlink { UnderlineStyle = UnderlineStyle.None };
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
    /// 带子里那一叠字和键有多高 —— 片名折成两行、窗口换窄都会变。集页那一格的高就是它，见
    /// <see cref="DetailViewModel.HeroRoom"/>。
    /// <para>
    /// 那一叠是底对齐的，所以它的 <c>ActualHeight</c> 就是它自己要的高，不是这一格给它的高 —— 拉伸的那种
    /// 量出来永远等于带子，带高也就永远等于当前值，一个自己咬着自己的数。上下那两道留白从那一格自己的外边距上
    /// 取，不写死 44。海报也算进去：一张 16:9 剧照比那一叠矮，可短片名的条目上反过来。
    /// </para>
    /// </summary>
    private void OnHeroStackSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var pad = HeroContent.Margin.Top + HeroContent.Margin.Bottom;
        ViewModel.HeroRoom = Math.Max(HeroStack.ActualHeight, ViewModel.StillHeight) + pad;
    }

    /// <summary>
    /// 同季那一带集摆在哪儿 —— 集页压在头图底下那段画面里（音轨那一行底下、剧情说明上面），别的页面摆在
    /// 正文那张纸上。参考图上那两支往上的箭头加上「音频字幕和集数的位置调换」说的就是这件事。
    /// <para>
    /// 搬同一份 markup 而不是再复制一份：复制的那一版正是被退回的那一版。压在图上那一档还要去掉板底和外圈
    /// （「去掉集列表的黑边」），并把牌子和卡片那两行字换成压在图上那套墨 —— 主题自己的墨在晴昼下是近黑色，
    /// 压在那层黑罩子上就没了。回到纸上时用 <c>ClearValue</c> 把底和外圈还给样式，不是抄一份样式里的值。
    /// </para>
    /// </summary>
    private void PlaceEpisodes(bool onScrim)
    {
        var host = onScrim ? HeroTail : (Panel)BodySheet;

        if (EpisodePanel.Parent is Panel current && !ReferenceEquals(current, host))
        {
            current.Children.Remove(EpisodePanel);

            // 图上那一档插在剧情说明前面，纸上那一档回到第一块（媒体信息紧跟在它后面，见 BodySheet 那段注释）。
            var at = onScrim ? HeroTail.Children.IndexOf(OverviewPanel) : 0;
            host.Children.Insert(at < 0 ? host.Children.Count : at, EpisodePanel);
        }

        if (onScrim)
        {
            EpisodePanel.Background = null;
            EpisodePanel.BorderThickness = new Thickness(0);

            // 上下那两道内边距在图上是纯粹的空气（没有板底可言），而第一屏就差这么二三十像素；左右留着，
            // 这一带的字才和音轨、剧情说明落在同一条竖线上。
            EpisodePanel.Padding = new Thickness(EpisodePanel.Padding.Left, 0, EpisodePanel.Padding.Right, 0);
        }
        else
        {
            // 板自己那几支，不是这一页（Control）继承来的同名依赖属性 —— 清错了那一带回到纸上就一直是光的。
            EpisodePanel.ClearValue(Panel.BackgroundProperty);
            EpisodePanel.ClearValue(StackPanel.BorderThicknessProperty);
            EpisodePanel.ClearValue(StackPanel.PaddingProperty);
        }

        EpisodeHead.OnScrim = onScrim;
        EpisodeStrip.OnScrim = onScrim;
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
        var artwork = ViewModel.HeroArt;
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

        _actions?.SetTitleStrip(!ViewModel.HeroArt
            ? TitleStrip.Plain
            : _washedInk ? TitleStrip.PagePainted : TitleStrip.OnScrim);
    }

    /// <summary>
    /// The menu is a view concern: its anchor is a laid-out control and its entries are WinUI flyout
    /// elements. The view model only supplies the currently loaded domain item and the sibling episodes
    /// needed by the shared command builder.
    /// </summary>
    private void OnMoreClicked(object sender, RoutedEventArgs e)
    {
        if (_session is not { } session
            || _actions is not { } actions
            || _images is not { } images
            || ViewModel.CurrentItem is not { } item)
            return;

        var card = new CardItem(item, images, CardSize.PosterWidth);
        ItemCommands.Show(
            session,
            actions,
            MoreButton,
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

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbyNian.Configuration;
using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Infrastructure;
using EmbyNian.Playback;
using EmbyNian.Services;
using EmbyNian.Shell.Media;
using EmbyNian.Shell.Platform;
using EmbyNian.Shell.Views;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EmbyNian.Shell.ViewModels;

/// <summary>
/// 电影、剧集、季和单集共用的那一页，as state rather than as assignments to named elements.
/// <para>
/// The page this replaces had one method, <c>LayoutPage()</c>, that assigned twenty-odd
/// <c>Visibility</c> properties from four private flags and from the text of other elements, and every
/// method that changed anything ended by calling it. That arrangement had a documented rule attached —
/// 「可见性只在 LayoutPage() 里赋值，任何地方都不读回 Visibility」 — because reading a visibility back
/// gives the wrong answer while the page is still off screen. The rule is unnecessary here: nothing is
/// assigned, every row's visibility is a property computed from the value it depends on, and the value
/// is what a test can read.
/// </para>
/// <para>
/// The two 「are we the ones assigning this?」 flags are gone too, and were not replaced by one flag with
/// a better name. The season picker is guarded on <see cref="_loadedSeason"/> — the season whose episodes
/// are on screen, which is a fact about the page rather than about the call stack, so it is right no
/// matter who asks and there is nothing left set if an exception unwinds a load. The source picker is
/// guarded by construction: see <see cref="ShowTarget"/>.
/// </para>
/// </summary>
public sealed partial class DetailViewModel : PageViewModel
{
    private const string Category = "详情";

    /// <summary>
    /// 头图的解码宽度。原来是 520 —— WinForms 那一版的数，配的是一格 376 高、四周留边的带子，图铺在里面还
    /// 被 <c>UniformToFill</c> 放大一截，糊一点正好当成免费的模糊。现在这张图铺满整个窗口（背景那一层在滚动
    /// 视图外面），一张 520 宽的图撑到 2560 宽要放大五倍，那就不是模糊而是坏了。
    /// 1280 是「一屏够清楚」和「一页只留一张位图」之间的那一档。
    /// </summary>
    private const int HeroDecodeWidth = 1280;

    /// <summary>
    /// 剧名上方那一枚徽标 —— 徽标 or 横幅图, see <see cref="ItemArtwork.Plate"/>. Decoded at the widest it can be
    /// drawn: the markup caps it at 280×56 and stretches it uniformly, so a wide 横幅图 fills that width exactly
    /// and a squarer 徽标 is shrunk by the height cap instead. 从前它站在角上、按 220 解；再往前它顶替文字标题，
    /// 按将近两倍宽解。
    /// </summary>
    private const int PlateDecodeWidth = 280;

    /// <summary>
    /// 右上角那张艺术图（<see cref="ItemArtwork.Corner"/>）的解码宽度，同它画出来的那个盒子（320×180）。它是一幅
    /// 画而不是一行字 —— 字缩糊了还认得出，画糊了就是一块脏，而高分屏上一个逻辑像素不止一个物理像素，所以这个数
    /// 按盒子的宽给足，不再往下省。盒子从 260×146 放大到 320×180 是用户要的（「再把艺术图调大一些」），这个数
    /// 跟着走。
    /// </summary>
    private const int CornerDecodeWidth = 320;

    /// <summary>
    /// 页尾那张横幅（<see cref="ItemArtwork.Footer"/>）的解码宽度，同它画出来的那个上限（760）。横幅图上写着片名，
    /// 所以它是这一页上唯一一处「字」由位图承担、又画得很大的地方 —— 解窄了糊的是片名本身。
    /// </summary>
    private const int FooterDecodeWidth = 760;

    /// <summary>
    /// The still beside the title. Fixed rather than scaled with 设置 → 海报宽度, unlike every other card in
    /// the app. 头上那一格的高是按内容定死的（<see cref="DetailHero.ArtHeight"/> = 460），再让这一张的宽跟着
    /// 滑杆走，那格带子就装不下它了 —— 滑杆拉到 340 时一张 2:3 海报要 510 高。
    /// </summary>
    private const int PosterStillWidth = 210;

    private const int PosterStillHeight = 300;

    /// <summary>An episode's still is 16:9, and the same size a 继续观看 card is drawn at by default.</summary>
    private const int EpisodeStillWidth = CardSize.WideWidth;

    private const int EpisodeStillHeight = CardSize.WideHeight;

    /// <summary>How many lines of 简介 are shown before 阅读更多 appears.</summary>
    private const int CollapsedLines = 4;

    private IShellActions? _actions;
    private ISettingsService? _settings;
    private EmbySession? _session;
    private EmbyImageStore? _images;
    private ISystemLauncher? _launcher;

    /// <summary>What the grid or shelf handed over. Used for its id; everything else is re-fetched.</summary>
    private EmbyItem? _seed;

    /// <summary>The item as the server describes it in full — the thing the whole page is about.</summary>
    private EmbyItem? _detail;

    /// <summary>
    /// The season whose episodes are on screen. The 季 picker's changed handler compares against this
    /// rather than consulting a 「syncing」 flag: replacing the picker's items makes it report a
    /// selection it chose itself, and that report has to be distinguishable from a person picking a
    /// season without the two paths having to know about each other. It is updated only after the
    /// episode request succeeds, so a failed switch cannot leave the picker naming one season while
    /// the shelf still contains another.
    /// </summary>
    private EmbyItem? _loadedSeason;

    /// <summary>
    /// The episodes on screen, handed to the player so 下一集 works and to the context menu so
    /// 标记本季已看 knows what 本季 is.
    /// </summary>
    private readonly List<EmbyItem> _episodes = [];

    /// <summary>
    /// Artwork has its own cancellation, separate from the page's load. Switching seasons deliberately
    /// leaves it alone — the hero belongs to the show, not to the season — so cancelling it with the
    /// load would lose the picture on every flick of the drop-down.
    /// </summary>
    private CancellationTokenSource? _art;

    /// <summary>
    /// 自检用的三个数，都是**这一页从建出来到扔掉的总数**，不是某一次载入的：先画那一屏落下的片名
    /// （null = 这一页没先画，存根导航），先画过几次，和那四张图被起过几次。
    /// <para>
    /// 两个 1 是对的：先画只该发生一次（第一次载入时屏上是空的），图也只该取一次 —— 完整条目回来时图没变就不重取
    /// （见 <see cref="Apply"/>），而重新读取（标记已看之后、手动刷新）时屏上已经是完整那一份，不该再拿卡片盖一遍
    /// （见 <see cref="Preview"/>）。
    /// </para>
    /// <para>
    /// 屏上都看不出来：图起 2 次是点一张封面进来闪一下，一百毫秒的事，截图抓不住；先画 2 次是「标记为已观看」之后
    /// 那个勾先跳回未看、一趟往返之后才变回来 —— 也抓不住，而那两件正是这一条要修的东西。
    /// </para>
    /// </summary>
    private string? _previewed;

    private int _previews;

    private int _artStarts;

    public DetailViewModel()
    {
        StillWidth = PosterStillWidth;
        StillHeight = PosterStillHeight;

        // Four rows whose visibility is a count. [NotifyPropertyChangedFor] cannot watch a collection's
        // Count — it hangs off a property's setter, and these collections are never re-assigned — so the
        // notification is hung off the collection itself, where it cannot be forgotten at a call site.
        Seasons.CollectionChanged += (_, _) => PickerChanged(nameof(SeasonVisibility));
        Sources.CollectionChanged += (_, _) => PickerChanged(nameof(SourceVisibility));
        AudioTracks.CollectionChanged += (_, _) => PickerChanged(nameof(AudioVisibility));
        SubtitleTracks.CollectionChanged += (_, _) => PickerChanged(nameof(SubtitleVisibility));

        // 媒体信息 is the same problem and not a picker: its own visibility only, without the pickers row's.
        InfoRows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(InfoVisibility));
    }

    // ---------------------------------------------------------------- 头部

    [ObservableProperty]
    public partial string? Title { get; set; }

    /// <summary>
    /// Where the headline leads, or null when the headline is only a headline. On an episode page the
    /// title is the show's name rather than the episode's (see <see cref="ItemDetail.Title"/>), so it is
    /// a promise the reader can follow; on every other page the title names the page you are already on
    /// and the button is disabled by <see cref="CanOpenTitle"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TitleTip))]
    [NotifyCanExecuteChangedFor(nameof(OpenTitleCommand))]
    public partial EmbyItem? TitleLink { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SublineVisibility))]
    [NotifyPropertyChangedFor(nameof(SublineFontSize))]
    [NotifyPropertyChangedFor(nameof(SublineFontWeight))]
    [NotifyPropertyChangedFor(nameof(SublineBrush))]
    public partial string? Subline { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScoreVisibility))]
    public partial string? Score { get; set; }

    /// <summary>首播日期、时长、分级、季数, already joined. Empty is a row with nothing in it, not a gap.</summary>
    [ObservableProperty]
    public partial string? Facts { get; set; }

    /// <summary>
    /// 导演, the prose line under 简介. Empty for an item with no director credited, which collapses
    /// the line — same convention as every other string on this page.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DirectorsVisibility))]
    [NotifyPropertyChangedFor(nameof(OverviewVisibility))]
    public partial string? Directors { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VideoVisibility))]
    public partial string? VideoLine { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeroVisibility))]
    public partial ImageSource? HeroImage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StillVisibility))]
    public partial ImageSource? StillImage { get; set; }

    /// <summary>
    /// 徽标 or 横幅图 —— 剧名上方那一枚（「统一改为在剧名上方显示徽标」）。四种页面同一个位置，不再按页面的种类
    /// 分左上角右上角。服务器两种都没有的条目上是 null，那一格就空着 —— 片名那一行照旧把名字说全，这一页没有
    /// 一样东西等这张图。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlateVisibility))]
    public partial ImageSource? PlateImage { get; set; }

    /// <summary>
    /// 头图右上角那张艺术图 —— 「右上角显示艺术图」。哪一张归这儿是 <see cref="ItemArtwork.Corner"/> 的事（这个
    /// 条目自己的艺术图，而背后那一整页已经站在同一张上时空着）；这里只存解出来的那张。没有就是 null，那个角
    /// 空着，<em>不再退回徽标</em> —— 徽标自己有一格（<see cref="PlateImage"/>）。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CornerVisibility))]
    public partial ImageSource? CornerImage { get; set; }

    /// <summary>
    /// 页面最底下那张横幅 —— 「在电影页面 剧页面 集页面的底部添加横幅」。哪一张归这儿是
    /// <see cref="ItemArtwork.Footer"/> 的事（这个条目自己的横幅图，而剧名上方那一枚已经用掉它时空着）；这里
    /// 只存解出来的那张。摆在哪儿用户点过名：整页最底下、演职人员那一排之后，滑到底才看得见。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FooterVisibility))]
    public partial ImageSource? FooterImage { get; set; }

    /// <summary>
    /// 页尾那张横幅这一次取的是哪一张 —— 自己的，还是借剧集那一层的（见 <see cref="ItemArtwork.Footer"/>）。
    /// null 就是「这一页没有」。自检读它：屏上那张图自己说不出它走了哪条路，而「借来的那一档到底走没走」正是这
    /// 一处唯一看不见的判断。
    /// </summary>
    public ArtworkRef? FooterPick { get; private set; }

    /// <summary>
    /// 这一页看得见的那一段有多宽，由视图在每次改尺寸时量给（<c>DetailPage.OnBodySizeChanged</c>）。它自己不上屏，
    /// 是「右上角那张画摆不摆」的自变量（<see cref="DetailHero.CornerFits"/>）—— 「窗口缩小到一定程度自动隐藏」。
    /// <para>
    /// 量的是页面而不是窗口：侧边栏一展开页面会窄掉两百来像素，而挤着片名的正是页面这一头。
    /// </para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CornerVisibility))]
    public partial double PageWidth { get; set; }

    [ObservableProperty]
    public partial double StillWidth { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeroRoom))]
    [NotifyPropertyChangedFor(nameof(HeroHeight))]
    [NotifyPropertyChangedFor(nameof(ScrimHeight))]
    [NotifyPropertyChangedFor(nameof(BodyMinHeight))]
    [NotifyPropertyChangedFor(nameof(TailMinHeight))]
    public partial double StillHeight { get; set; }

    /// <summary>
    /// 服务器上有没有这一页要铺在背后的那张图。<em>不是</em>「图解出来了没有」（那是
    /// <see cref="HeroVisibility"/>），而是 <see cref="ItemArtwork.Hero"/> 问标签表的结果。
    /// <para>
    /// 分成两个值是为了「点击主页封面后窗口会闪一下，然后才会进入页面」。版面高按有没有这张图分两档
    /// （<see cref="DetailHero.Height"/>）；拿解好的位图当判据，那一下就是先按 380 布一遍、图到了再按 460 布
    /// 第二遍，屏上看着就是闪一下。列表接口回来的条目已经带着标签，所以这句话在 <see cref="Attach"/> 那一刻
    /// 就答得出：第一帧的版面就是最后的版面，之后到的只是画面本身。
    /// </para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeroHeight))]
    [NotifyPropertyChangedFor(nameof(ScrimHeight))]
    [NotifyPropertyChangedFor(nameof(BodyMinHeight))]
    [NotifyPropertyChangedFor(nameof(TailMinHeight))]
    [NotifyPropertyChangedFor(nameof(PaperMinHeight))]
    [NotifyPropertyChangedFor(nameof(HeroArtVisibility))]
    [NotifyPropertyChangedFor(nameof(HeroPlainVisibility))]
    public partial bool HeroArt { get; set; }

    /// <summary>
    /// 这一页看得见的那一段有多高，由视图在每次改尺寸时量给（<c>DetailPage.OnBodySizeChanged</c>）。它自己
    /// 不上屏，是底下那三个下限（<see cref="BodyMinHeight"/>、<see cref="TailMinHeight"/>、
    /// <see cref="PaperMinHeight"/>）共同的那个自变量：视口高只有布好的版面知道，而拿它算什么归这里。
    /// <para>
    /// 头上那一格不看它 —— 三种页面的带高都由内容给（见 <see cref="HeroHeight"/>）。跟着视口走过的两版，一版
    /// 把片名和那排键压到窗口下沿（「图一页面怎么改的一大片空白」），一版在高窗口上把带子撑到 460 而那一叠只有
    /// 两百来高（「集拉大窗口后会导致左上角空空的」）—— 同一个错的两种长相。
    /// </para>
    /// <para>
    /// 反过来，<em>纸的上沿</em>必须跟着它走：不跟的那一版在不同窗口上盖掉的剧照完全不一样（「拉大或拉小窗口
    /// 会导致背景图被遮挡」），见 <see cref="DetailHero.TailHeight"/>。
    /// </para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BodyMinHeight))]
    [NotifyPropertyChangedFor(nameof(TailMinHeight))]
    [NotifyPropertyChangedFor(nameof(PaperMinHeight))]
    public partial double Viewport { get; set; }

    /// <summary>
    /// 带子里那一叠字和键（片名、副标题、读数、那排键）实测有多高，由视图量给
    /// （<c>DetailPage.OnHeroStackSizeChanged</c>）。它自己不上屏，是 <see cref="HeroRoom"/> 的自变量。
    /// <para>
    /// 量出来而不是写死：片名折成两行的条目、窄窗口上那一叠会长高，写死一个数就会在那些页面上把字和键挤出
    /// 带子；而剧照比那一叠矮的条目上反过来 —— 写死的那个数在那些页面上就是一格空白。
    /// </para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeroRoom))]
    [NotifyPropertyChangedFor(nameof(HeroHeight))]
    [NotifyPropertyChangedFor(nameof(ScrimHeight))]
    [NotifyPropertyChangedFor(nameof(BodyMinHeight))]
    [NotifyPropertyChangedFor(nameof(TailMinHeight))]
    public partial double StackRoom { get; set; }

    /// <summary>
    /// 带子里那一叠（剧照、片名、副标题、读数、那排键）连上下留白实测要占多高。集页那一格的高就是它，见
    /// <see cref="DetailHero.EpisodeHeight"/>；别的页面不用它。
    /// <para>
    /// 算在这儿而不在视图里：视图量得到的只有那一叠字键有多高（<see cref="StackRoom"/>），而「剧照也算进去」
    /// 和「加上这一格自己的上下留白」两句是规矩。海报按图自己的形状收窄之后（<see cref="DetailHero.StillBox"/>）
    /// 剧照的高会变，写在视图那个尺寸回调里的那一版只在字键那一叠也跟着变的时候才会重算 —— 带子于是比内容高
    /// 出那么一截，正是「左上角空空的」那种坏法。
    /// </para>
    /// </summary>
    public double HeroRoom => Math.Max(StackRoom, StillHeight) + HeroInset.Top + HeroInset.Bottom;

    /// <summary>
    /// 头上那一格的高，三种页面同一条规矩：高由站在它里面那一叠东西定，跟窗口无关（「图一页面怎么改的一大片
    /// 空白，改回去」）。电影、剧、季走 <see cref="DetailHero.Height"/> —— 那两档 460／380 是手量出来的内容高，
    /// 判据 <see cref="HeroArt"/> 在导航那一刻就知道，所以第一帧的版面就是最后的版面。
    /// <para>
    /// 集页量在运行时（<see cref="HeroRoom"/> → <see cref="DetailHero.EpisodeHeight"/>）：单集配的是一张 16:9
    /// 剧照，比 2:3 海报矮一大截，那一叠字也少两行，跟着用 460 就等于在底对齐的那一叠头上留两百来像素只有画面
    /// 的地方 —— 「集拉大窗口后会导致左上角空空的，画面不协调，电影那边处理的就很好」。
    /// </para>
    /// </summary>
    public double HeroHeight => IsEpisodePage
        ? DetailHero.EpisodeHeight(HeroRoom)
        : DetailHero.Height(HeroArt);

    /// <summary>
    /// 同季那一带集摆在哪儿：集页压在头图底下那段画面里（音轨那一行底下、剧情说明上面），别的页面摆在正文
    /// 那张纸上。<c>DetailPage.PlaceEpisodes</c> 照着它搬，两处的墨也照着它换。
    /// </summary>
    public bool EpisodesOnScrim => IsEpisodePage;

    /// <summary>
    /// 带子里那一叠字和键四周的留白。集页把底下那道 64 收到 16 —— 「为什么中间要留空，导致下方的剧情说明
    /// 看不到？」：那 64 是给「这一格铺到窗口下沿」写的（贴着窗口边读着像被截了一截），可集页底下紧跟着音轨
    /// 那一行和那一带集，于是它就是纯粹的空气，而下面的剧情说明正差这一截。别的页面照旧。
    /// </summary>
    public Thickness HeroInset => new(60, 28, 60, IsEpisodePage ? 16 : 64);

    /// <summary>
    /// 带子底下那一段的内边距。同 <see cref="HeroInset"/>：集页把上面那道 28 收到 12，那一叠键和「音频」
    /// 之间因此只隔 28，和那一段里几块之间的 20 是同一个量级。
    /// </summary>
    public Thickness TailInset => new(28, IsEpisodePage ? 12 : 28, 28, 8);

    /// <summary>
    /// 带子下沿那道渐深罩子这一次有多高 —— <see cref="DetailHero.ScrimSpan"/>：带子收窄了它就跟着收，
    /// 不然它会从带子的上沿溢出去，而标题条上那层洗（<see cref="DetailHero.TopWash"/>）算的是收完的那一块。
    /// </summary>
    public double ScrimHeight => DetailHero.ScrimSpan(HeroHeight);

    /// <summary>
    /// 正文那张纸的下限 —— 见 <see cref="DetailHero.BodyHeight"/>：内容短的页面上把看得见的那一段补满，
    /// 「滑到下面不用显示背景」靠的是这张纸真的盖住了背后那张图。有剧照的那一档富余高度改由
    /// <see cref="TailMinHeight"/> 吃掉，这一支管的是没有剧照的那一档。
    /// </summary>
    public double BodyMinHeight => DetailHero.BodyHeight(Viewport, HeroHeight);

    /// <summary>
    /// 头图底下那段压暗的尾部的下限 —— 见 <see cref="DetailHero.TailHeight"/>：尾部先补满第一屏减掉带子那么多，
    /// 于是正文那张纸从第一屏的下沿起，「拉大或拉小窗口」都不会把那道不透明的边提到剧照上。撑到
    /// <see cref="DetailHero.TailCap"/> 就不再撑，富余的高度归纸 —— 不然剧情说明底下那段空画面会跟着窗口一起长
    /// （「下面越改空位越大」）。过了那个顶之后纸就跟着窗口一像素一像素地露出来，中间没有台阶（「拉大窗口之后
    /// 下面突然冒出一大截」）。
    /// </summary>
    public double TailMinHeight => DetailHero.TailHeight(Viewport, HeroHeight, HeroArt);

    /// <summary>
    /// 正文那张纸自己的下限 —— 见 <see cref="DetailHero.PaperHeight"/>：滚到底的那一屏只能有纸。
    /// <para>
    /// 只跟着 <see cref="Viewport"/> 和 <see cref="HeroArt"/> 走，所以不进 <see cref="AnnounceShape"/>：那两个
    /// 都是 observable 的，值真变了就会自己喊一声。<see cref="TailMinHeight"/> 不同 —— 它还看带高，而带高跟着
    /// 页面的种类走。
    /// </para>
    /// </summary>
    public double PaperMinHeight => DetailHero.PaperHeight(Viewport, HeroArt);

    /// <summary>
    /// The file 播放 would start: the item itself for a film, the next unwatched episode for a show.
    /// Null for a series or season whose episodes have not arrived, which is what collapses the button.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayText))]
    [NotifyPropertyChangedFor(nameof(PlayVisibility))]
    [NotifyPropertyChangedFor(nameof(RestartVisibility))]
    [NotifyPropertyChangedFor(nameof(InfoVisibility))]
    [NotifyPropertyChangedFor(nameof(PickersVisibility))]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestartCommand))]
    public partial EmbyItem? PlayTarget { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WatchedVisibility))]
    [NotifyPropertyChangedFor(nameof(NotWatchedVisibility))]
    public partial bool Watched { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FavoriteVisibility))]
    [NotifyPropertyChangedFor(nameof(NotFavoriteVisibility))]
    public partial bool Favorite { get; set; }

    /// <summary>
    /// 副标题那一行画的是一行字还是一排点得动的类型：有类型就是后者（见 <see cref="SublineGenres"/>），
    /// 单集页和只剩剧名的季页还是前者。
    /// </summary>
    public Visibility SublineVisibility => Show(!string.IsNullOrWhiteSpace(Subline) && SublineGenres.Count == 0);

    /// <inheritdoc cref="SublineVisibility"/>
    public Visibility GenreVisibility => Show(SublineGenres.Count > 0);

    /// <summary>
    /// 那一行里点得动的几个类型。空的时候那一行照旧是一行字 —— 规则在
    /// <see cref="ItemDetail.SublineGenres"/>，这里只是把它端上屏。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SublineVisibility))]
    [NotifyPropertyChangedFor(nameof(GenreVisibility))]
    public partial IReadOnlyList<string> SublineGenres { get; set; } = [];

    /// <summary>
    /// 点了一个类型：交给外壳开一格「这个类型下的全部影片和剧集」（<see cref="IShellActions.OpenGenre"/>）。
    /// 视图那边把这一行的每一个类型接到这儿来，同卡片上的点击接到 <see cref="OpenCard"/>。
    /// </summary>
    internal void OpenGenre(string genre) => _actions?.OpenGenre(genre);

    /// <summary>
    /// 副标题的字号. On an episode page this line is 「S2:E7 - 集名」, which is the one thing the reader
    /// came for — the headline above it only says which show — so it is set at 标题号 there. On a film or
    /// a show the same line is a genre list, background information, and stays at 正文号. Both numbers
    /// are read out of the theme rather than written here so the page keeps a single type scale.
    /// </summary>
    public double SublineFontSize => FontSize(IsEpisodePage ? "EgTitleFontSize" : "EgBodyFontSize");

    /// <summary>The weight that goes with <see cref="SublineFontSize"/>: emphasised on an episode page.</summary>
    public Windows.UI.Text.FontWeight SublineFontWeight =>
        IsEpisodePage ? FontWeights.SemiBold : FontWeights.Normal;

    /// <summary>
    /// 副标题的颜色，同 <see cref="SublineFontSize"/> 的道理往下推一步：单集页上这一行是人真正来看的
    /// 东西，它不该比它上面那行只说了剧名的标题更暗。其余页上这一行是背景信息，暗一档正合适。
    /// <para>
    /// 两支都是 EgOnScrim 画刷，不跟主题走 —— 头图上永远压着写死的黑罩子。
    /// </para>
    /// </summary>
    public Brush SublineBrush => Painted(IsEpisodePage ? "EgOnScrimBrush" : "EgOnScrimDimBrush");

    /// <summary>
    /// The 标题按钮's tooltip, or null when there is nowhere to go. Null rather than empty on purpose:
    /// <c>ToolTipService</c> shows nothing for a null, while an empty string pops an empty box over a
    /// headline that cannot be pressed.
    /// </summary>
    public string? TitleTip =>
        TitleLink is { } link ? $"转到{EmbyItemType.ToChinese(link.Type)}：{link.Name}" : null;

    /// <summary>Whether the page is about one episode — the case that moves the emphasis to the subline.</summary>
    private bool IsEpisodePage => _detail?.Type == EmbyItemType.Episode;

    /// <summary>
    /// Whether 单集 belongs down the page as rows rather than across it as cards. The rule itself is
    /// <see cref="ItemDetail.EpisodesAsList"/>; what is here is only 「asked of this page's own item」.
    /// </summary>
    private bool EpisodesAsList => ItemDetail.EpisodesAsList(_detail?.Type);

    private static double FontSize(string key) => (double)Application.Current.Resources[key];

    private static Brush Painted(string key) => (Brush)Application.Current.Resources[key];

    public Visibility ScoreVisibility => Show(!string.IsNullOrWhiteSpace(Score));

    public Visibility VideoVisibility => Show(!string.IsNullOrWhiteSpace(VideoLine));

    /// <summary>
    /// Whether there is a picture yet, rather than a flag set beside the assignment. The two used to be
    /// a <c>_hasHeroImage</c> and an <c>Image.Source</c> that a later edit could put out of step; here
    /// there is one value and the question is asked of it.
    /// </summary>
    public Visibility HeroVisibility => Show(HeroImage is not null);

    /// <summary>
    /// 固定在背景里那一层画不画 —— 「当前任务的背景要用亚克力遮罩，固定在背景中……占满标题栏」里那一层
    /// （<c>DetailPage.xaml</c> 的 <c>Backdrop</c>）。
    /// <para>
    /// 问的是 <see cref="HeroArt"/>「服务器上有没有这张图」，不是 <see cref="HeroVisibility"/>「位图解出来了
    /// 没有」：层先站好，压住标题栏那行字的罩子就先在了，图到了只是这一层里多一张画面。反过来就是「点击主页
    /// 封面后窗口会闪一下」—— 先按没有图布一遍、图到了再布第二遍。
    /// </para>
    /// </summary>
    public Visibility HeroArtVisibility => Show(HeroArt);

    /// <summary>
    /// 上面那一层的反面：没有那张图的时候，头图那一格自己当底的那一层（一层底色加顶上一道罩子）。有图的时候
    /// 这两样都在那张固定的图上，这一层就得让开，否则一块不透明的底色会把图盖掉。
    /// </summary>
    public Visibility HeroPlainVisibility => Show(!HeroArt);

    public Visibility StillVisibility => Show(StillImage is not null);

    /// <summary>
    /// 剧名上方那一枚徽标画不画 —— 只问一句：图解出来了没有。
    /// <para>
    /// 从前这里还有第二句「这一页是不是集页」：那一版里名牌在集页上摆右上角、别的页面上是左上角那个位置的第二档。
    /// 「统一改为在剧名上方显示徽标」把那个分岔去掉了 —— 四种页面同一个位置，也就没有「哪一页摆哪个角」可问。
    /// </para>
    /// <para>
    /// 算出来的而不是谁解完图顺手把元素显出来：这样「那一枚在不在」是页面上一个可读的值，自检能从树上读到它，
    /// 而不是去猜一张图有没有到。
    /// </para>
    /// </summary>
    public Visibility PlateVisibility => Show(PlateImage is not null);

    /// <summary>
    /// 右上角那张艺术图画不画。两句话：图解出来了没有，以及这一页还剩不剩地方（<see cref="DetailHero.CornerFits"/>
    /// —— 「窗口缩小到一定程度自动隐藏」）。哪一张该摆在那儿是取图那一遍的事（<see cref="LoadArtworkAsync"/> 问
    /// <see cref="ItemArtwork.Corner"/>）。
    /// <para>
    /// 图照旧取、照旧解，只是窄窗口上不画：那一张已经在缓存里，窗口一拉宽它立刻就在，用不着再等一趟网络。
    /// </para>
    /// </summary>
    public Visibility CornerVisibility => Show(CornerImage is not null && DetailHero.CornerFits(PageWidth));

    /// <summary>
    /// 页面最底下那张横幅画不画。两句话：图解出来了没有，以及这一页是不是那三种页面之一 —— 用户点的名是「电影
    /// 页面 剧页面 集页面」，季页不在里头。
    /// <para>
    /// 季页为什么不在：那一页整个是「这一季有哪些集」的一张清单，横幅上写的是剧名，摆在清单末尾说的是上一层的
    /// 事。这是用户给的范围，不是算出来的 —— 所以判据就照着他点的那三种写。
    /// </para>
    /// </summary>
    public Visibility FooterVisibility => Show(FooterImage is not null && FooterPage);

    /// <summary>
    /// 这一页在不在「页尾摆横幅」那三种里（电影、剧、集）。跟着页面的种类走，所以要在
    /// <see cref="AnnounceShape"/> 里喊一声。
    /// </summary>
    private bool FooterPage => ItemType is EmbyItemType.Movie or EmbyItemType.Series or EmbyItemType.Episode;


    /// <summary>「播放」, 「播放 S01E02」 or 「继续播放 20:34」 — the resume clock is on the button itself.</summary>
    public string PlayText => ItemDetail.PlayText(PlayTarget);

    public Visibility PlayVisibility => Show(PlayTarget is not null);

    public Visibility RestartVisibility => Show(PlayTarget?.HasResumePosition == true);

    public Visibility WatchedVisibility => Show(Watched);

    public Visibility NotWatchedVisibility => Show(!Watched);

    public Visibility FavoriteVisibility => Show(Favorite);

    public Visibility NotFavoriteVisibility => Show(!Favorite);

    // ---------------------------------------------------------------- 下拉

    public ObservableCollection<EmbyItem> Seasons { get; } = [];

    public ObservableCollection<SourceRow> Sources { get; } = [];

    public ObservableCollection<TrackRow> AudioTracks { get; } = [];

    public ObservableCollection<TrackRow> SubtitleTracks { get; } = [];

    [ObservableProperty]
    public partial EmbyItem? SelectedSeason { get; set; }

    [ObservableProperty]
    public partial SourceRow? SelectedSource { get; set; }

    [ObservableProperty]
    public partial TrackRow? SelectedAudio { get; set; }

    [ObservableProperty]
    public partial TrackRow? SelectedSubtitle { get; set; }

    /// <summary>
    /// Only a show with more than one season gets a picker. No conjunct on the item's type, unlike the
    /// page this replaces: <see cref="LoadSeriesAsync"/> is the only thing that fills
    /// <see cref="Seasons"/> and it only runs for a series, so a non-empty list already says so.
    /// </summary>
    public Visibility SeasonVisibility => Show(Seasons.Count > 1);

    public Visibility SourceVisibility => Show(Sources.Count > 1);

    /// <summary>One row is 自动 alone, which is not a choice.</summary>
    public Visibility AudioVisibility => Show(AudioTracks.Count > AutoRows);

    /// <summary>Two rows are 自动 and 不使用字幕, which is not a choice either.</summary>
    public Visibility SubtitleVisibility => Show(SubtitleTracks.Count > AutoRows + 1);

    /// <summary>
    /// 头图底下那一行文件选项画不画。两句话都得成立：播放键指着一个文件，而且那一行里真有的可选。
    /// <para>
    /// 判据是<em>播放键的落点</em>，不是「这一页自己是不是一个文件」（用户 2026-09-02：「给剧页面加上音频字幕
    /// 等等的选择项」）。剧页和季页的播放键指着解析出来的那一集，而那一集的源和轨道这一页早就问全了 —— 从前那一版
    /// 照读不照画，收起来的只是屏上那一行，于是「从剧页按下播放」用的是默认轨道而没有任何地方能改。同一页上「视频：
    /// 1080p · H264 · MKV」那一行讲的也正是这个文件，所以摆在一起并不突兀。
    /// </para>
    /// <para>
    /// 落点是空的时候一定要收起来（人物页，或者一部剧的集还没回来）：那时候 <see cref="Sources"/> 那几个集合是
    /// 上一个条目留下的，画出来就是拿另一个文件的轨道让人挑。<see cref="ShowTarget"/> 会清空它们，可清空和这一句
    /// 之间隔着一趟往返。
    /// </para>
    /// <para>
    /// 媒体信息 那张表照旧只在文件页上（见 <see cref="ShowSource"/>）：那是一整张表在讲某个文件的来龙去脉，
    /// 而这一行是「按下播放之前挑一下」。两件事一句话说不完，所以判据也不共用。
    /// </para>
    /// <para>
    /// 季选择器不在这一行里：它跟着那一带集走，摆在它替掉的那块内容边上。算进来的话，一部剧的选中集只有一个
    /// 媒体源、又没有可选轨道时，屏上会剩一行空的。
    /// </para>
    /// </summary>
    public Visibility PickersVisibility => Show(
        PlayTarget is not null
        && (Sources.Count > 1 || AudioTracks.Count > AutoRows || SubtitleTracks.Count > AutoRows + 1));

    /// <summary>The 自动 row every track picker carries whether the file has tracks or not.</summary>
    private const int AutoRows = 1;

    // ---------------------------------------------------------------- 正文

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverviewVisibility))]
    public partial string? Overview { get; set; }

    /// <summary>
    /// Whether 简介 is showing in full. Drives four bound properties instead of the four assignments
    /// <c>LayoutPage()</c> made, which is the same arithmetic with the ordering problem removed.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverviewMaxLines))]
    [NotifyPropertyChangedFor(nameof(OverviewTrimming))]
    [NotifyPropertyChangedFor(nameof(ReadMoreText))]
    [NotifyPropertyChangedFor(nameof(ReadMoreVisibility))]
    public partial bool Expanded { get; set; }

    /// <summary>
    /// Set by the page from <c>TextBlock.IsTextTrimmed</c>, which only the laid-out element knows. The
    /// one fact on this view model that has to come back from the view: whether four lines were enough
    /// depends on the width the text was given, and nothing here knows that.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReadMoreVisibility))]
    public partial bool OverviewTrimmed { get; set; }

    public int OverviewMaxLines => Expanded ? 0 : CollapsedLines;

    public TextTrimming OverviewTrimming => Expanded ? TextTrimming.None : TextTrimming.CharacterEllipsis;

    public string ReadMoreText => Expanded ? "收起" : "阅读更多";

    public Visibility ReadMoreVisibility => Show(OverviewTrimmed || Expanded);

    /// <summary>
    /// 简介那一块整块的显隐 —— 简介和导演两行都空的时候收起来。
    /// <para>
    /// 这一条是「正文那几块坐上亚克力」带出来的：这一块以前是版面里一段没有底的文字，简介为空就是零高，
    /// 屏上看不出来；现在它自己是一块玻璃板，空的一块玻璃是一条 37 高的空板，得自己收起来。判据里带上
    /// 导演那一行，因为它也长在这一块里 —— 有导演没简介的条目照样得有块板。
    /// </para>
    /// </summary>
    public Visibility OverviewVisibility => Show(
        !string.IsNullOrWhiteSpace(Overview) || !string.IsNullOrWhiteSpace(Directors));

    // ---------------------------------------------------------------- 单集列表、三条卡片带与媒体信息

    /// <summary>
    /// 单集, drawn either as a vertical list of rows or as a horizontal paging strip of cards — one shelf
    /// for both, filled through <see cref="CardShelf.FillRows"/> for the first and
    /// <see cref="CardShelf.Fill(IEnumerable{EmbyItem}, Func{EmbyItem, string?})"/> for the second. Which
    /// of the two is <see cref="ItemDetail.EpisodesAsList"/>, asked of this page's own item. Nullable
    /// because the shelf cannot exist before <see cref="Attach"/> — it needs the image store and the card
    /// width from 设置 — and the page binds to it from the moment it is constructed.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EpisodeVisibility))]
    [NotifyPropertyChangedFor(nameof(EpisodeListVisibility))]
    [NotifyPropertyChangedFor(nameof(EpisodeStripVisibility))]
    public partial CardShelf? EpisodeShelf { get; set; }

    /// <summary>
    /// Which card the strip opens on, bound to <c>ShelfStrip.FocusIndex</c>; 0 means 「leave it at the
    /// start」. Only ever non-zero on an episode's own page, where the strip lists that episode's siblings
    /// and the episode itself is somewhere in the middle of the season. See
    /// <see cref="ItemDetail.EpisodeFocus"/>.
    /// </summary>
    [ObservableProperty]
    public partial int EpisodeFocus { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CastVisibility))]
    public partial CardShelf? CastShelf { get; set; }

    /// <summary>
    /// 全部剧季, the horizontal strip of season cards a series page carries — Emby's own arrangement,
    /// where a series is browsed season by season and each card opens that season's page.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SeasonShelfVisibility))]
    public partial CardShelf? SeasonShelf { get; set; }

    /// <summary>更多类似, the recommendations row Emby's own detail page carries under the cast.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SimilarVisibility))]
    public partial CardShelf? SimilarShelf { get; set; }

    /// <summary>
    /// The 媒体信息 table, one <see cref="InfoRow"/> per line. A collection rather than the one long
    /// string this used to be so the page can right-align the labels into a column: a single
    /// <c>TextBlock</c> cannot align anything, and a table is what the reader is scanning.
    /// </summary>
    public ObservableCollection<InfoRow> InfoRows { get; } = [];

    public Visibility EpisodeVisibility => Show(EpisodeShelf is { Cards.Count: > 0 });

    /// <summary>
    /// Which of the two shapes 单集 takes on this page. Both are inside the block
    /// <see cref="EpisodeVisibility"/> governs — they share its heading and its 切换季 picker — so each one
    /// only has to answer 「is this page's shape mine」 on top of 「is there anything to draw」.
    /// <para>
    /// A pair of computed visibilities rather than one flag the markup inverts: a page that hid neither, or
    /// hid both, would be a page with the episodes drawn twice or not at all, and a <c>Visibility</c> the
    /// view model states outright is the thing the self-check can read back off the tree.
    /// </para>
    /// </summary>
    public Visibility EpisodeListVisibility => Show(EpisodeShelf is { Cards.Count: > 0 } && EpisodesAsList);

    public Visibility EpisodeStripVisibility => Show(EpisodeShelf is { Cards.Count: > 0 } && !EpisodesAsList);

    public Visibility CastVisibility => Show(CastShelf is { Cards.Count: > 0 });

    public Visibility SeasonShelfVisibility => Show(SeasonShelf is { Cards.Count: > 0 });

    public Visibility SimilarVisibility => Show(SimilarShelf is { Cards.Count: > 0 });

    public Visibility DirectorsVisibility => Show(!string.IsNullOrWhiteSpace(Directors));

    public Visibility InfoVisibility => Show(InfoRows.Count > 0);

    /// <summary>The episodes on screen, for the player's 下一集 and for the context menu's 本季 commands.</summary>
    internal IReadOnlyList<EmbyItem> Episodes => _episodes;

    /// <summary>
    /// The fully loaded item behind this page. The page reads it when it needs to build a view-only
    /// context menu; exposing the domain item keeps that menu's anchor and flyout construction out of
    /// the view model without duplicating the server-loaded state in the page.
    /// </summary>
    internal EmbyItem? CurrentItem => _detail;

    /// <summary>
    /// What kind of thing this page is showing, for the self-check's report. The item the page fetched
    /// rather than the one it was handed, since those two disagree in exactly the case worth reading about:
    /// a series whose play target is one of its episodes.
    /// </summary>
    internal string ItemType => _detail?.Type ?? _seed?.Type ?? "未定";

    /// <summary>
    /// 自检：「点封面进详情页要空等一趟服务器往返」那一条修好了没有，而且没修出新毛病来。三句话 —— 第一屏是拿点
    /// 进来那张卡片画的（落下的片名就是证据）、**这一整页只先画过一次**、那四张图**这一整页只取过一次**。
    /// <para>
    /// 三件屏上都看不出来。第一屏画得早不早，截图只能拍到已经载完的那一页。另两件是次数：图起 2 次是点一张封面进
    /// 来闪一下；先画 2 次说明重新读取时又拿卡片那一份盖了一遍，屏上是「标记为已观看」之后那个勾先跳回未看、一趟
    /// 往返之后才变回来 —— 两件都是一百毫秒的事，截图抓不住。所以这里报的是数，而自检会先重新读取一遍再问。
    /// </para>
    /// <para>
    /// 存根导航（集页那行剧名、演职人员那一排）故意不先画，那一档报「没先画」并且不判红 —— 见 <c>Preview</c>。
    /// </para>
    /// </summary>
    internal (bool Ok, string Detail) PreviewRead()
    {
        if (_seed is { IsStub: true })
            return (true, $"存根导航（{_seed.Type}），故意不先画；图起了 {_artStarts} 次");

        var painted = _previewed is { Length: > 0 };
        var onceEach = _previews == 1 && _artStarts == 1;

        return (painted && onceEach,
            (painted ? $"第一屏用卡片画的，片名「{_previewed}」" : "第一屏没先画（片名是空的）")
                + $"；这一页先画了 {_previews} 次、图起了 {_artStarts} 次"
                + (onceEach
                    ? "（各一次 —— 完整条目回来没重取，重新读取也没拿卡片盖一遍）"
                    : _previews > 1
                        ? "，先画多于 1 次 —— 重新读取时又拿卡片那一份盖了一遍，那个勾会先跳回旧值"
                        : "，图多于 1 次 —— 完整条目回来把图白重取了一遍"));
    }

    private bool CanAct => !Busy;

    private bool CanPlay => !Busy && PlayTarget is not null;

    /// <summary>
    /// Not gated on <see cref="PageViewModel.Busy"/>, unlike the buttons: following the headline is
    /// navigation, same as pressing a card, and leaving a page that is still loading is a reasonable
    /// thing to want.
    /// </summary>
    private bool CanOpenTitle => TitleLink is not null;

    /// <inheritdoc />
    protected override void BusyChanged()
    {
        PlayCommand.NotifyCanExecuteChanged();
        RestartCommand.NotifyCanExecuteChanged();
        ToggleWatchedCommand.NotifyCanExecuteChanged();
        ToggleFavoriteCommand.NotifyCanExecuteChanged();
    }

    // ---------------------------------------------------------------- 载入

    /// <summary>
    /// Whether <see cref="Attach"/> has run. The five services arrive in that one call as non-nullable
    /// parameters, so this single question answers for all of them; past this gate they are dereferenced
    /// with <c>!</c>. <see cref="_seed"/> is a separate question — 「which item」 — and the loads ask both.
    /// </summary>
    private bool Attached => _settings is not null;

    /// <summary>
    /// The live settings object, read through the service rather than copied: it is one instance for the
    /// process and the settings page edits it in place, so a snapshot taken here would go stale the moment
    /// 恢复上次播放位置 was switched off.
    /// </summary>
    private AppSettings Settings => _settings!.Settings;

    /// <summary>
    /// Handed the item to show, the shell it opens things through, the three services this page reads —
    /// the settings for the two rows it sizes and the two playback preferences, the session for every
    /// round trip, and the image store for the shelves — and the launcher 在浏览器中打开 goes through.
    /// </summary>
    internal void Attach(
        DetailRequest request,
        IShellActions actions,
        ISettingsService settings,
        EmbySession session,
        EmbyImageStore images,
        ISystemLauncher launcher)
    {
        _actions = actions;
        _seed = request.Item;
        _settings = settings;
        _session = session;
        _images = images;
        _launcher = launcher;

        // 版面高在这一句就定了下来，早于任何一次网络往返 —— 「点击主页封面后窗口会闪一下」说的就是这一句以前
        // 得等图。列表接口回来的条目已经带着 ImageTags，而这一页正是从那张列表上点进来的。
        HeroArt = ItemArtwork.Hero(request.Item).Count > 0;

        var ui = settings.Settings.Ui;

        // Clamped rather than trusted, as on the home page: 设置 offers 120–300, the migration clamps to
        // 120–340, and a hand-edited settings.json is a supported way to configure this app.
        var poster = Math.Clamp(ui.PosterWidth, 120, 340);

        // Both rows scale with 海报宽度. They were literals — 300 and 124 — so the 演职人员 row stayed
        // 124 wide whatever the slider said, next to a grid of posters that did not.
        var episodes = new CardShelf("更多单集", images, CardSize.WideFor(poster),
            wide: true, ui.ShowWatchedIndicators);
        var cast = new CardShelf("演职人员", images, CardSize.CastFor(poster),
            wide: false, indicators: false);
        var seasons = new CardShelf("全部剧季", images, poster,
            wide: false, ui.ShowWatchedIndicators);
        var similar = new CardShelf("更多类似", images, poster,
            wide: false, ui.ShowWatchedIndicators);

        if (EpisodeShelf is { } previous) previous.Cards.CollectionChanged -= OnEpisodeCardsChanged;
        if (CastShelf is { } before) before.Cards.CollectionChanged -= OnCastCardsChanged;
        if (SeasonShelf is { } oldSeasons) oldSeasons.Cards.CollectionChanged -= OnSeasonShelfChanged;
        if (SimilarShelf is { } oldSimilar) oldSimilar.Cards.CollectionChanged -= OnSimilarCardsChanged;

        episodes.Cards.CollectionChanged += OnEpisodeCardsChanged;
        cast.Cards.CollectionChanged += OnCastCardsChanged;
        seasons.Cards.CollectionChanged += OnSeasonShelfChanged;
        similar.Cards.CollectionChanged += OnSimilarCardsChanged;

        EpisodeShelf = episodes;
        CastShelf = cast;
        SeasonShelf = seasons;
        SimilarShelf = similar;
    }

    /// <inheritdoc />
    public override Task ReloadAsync() => LoadAsync();

    /// <summary>Re-reads the item and everything hanging off it. The one entry point; every path lands here.</summary>
    private async Task LoadAsync()
    {
        if (!Attached || _seed is not { } seed) return;

        var token = BeginLoad();

        try
        {
            // 先用点进来那张卡片画一屏，**在这一趟往返之前**。片名、副标题那一行类型、简介、已看和收藏、还有那四张
            // 图，卡片手上全都有（列表接口那份字段集就带着 Overview / ProductionYear / Genres / ImageTags），从前
            // 它们一律在等详情接口 —— 于是点一张封面进来先看一屏「正在读取」，而这是整个客户端里点得最多的动作。
            Preview(seed);

            var detail = await _session!
                .ExecuteAsync((client, ct) => client.GetItemAsync(seed.Id, ct), token).ConfigureAwait(true);
            if (!IsCurrent(token)) return;

            Apply(detail);

            // 更多类似 only needs the item this page is about, so it has no business waiting behind the
            // 剧季 → 单集 → 播放目标 chain — which is three round trips on a series page. Started here and
            // awaited at the end: one fewer trip on the way to a finished page, and it still has to be
            // finished before the page calls itself loaded. Started after Apply, which clears the shelf
            // it fills.
            var similar = LoadSimilarAsync(detail, token);

            switch (detail.Type)
            {
                case EmbyItemType.Series:
                    await LoadSeriesAsync(detail, token).ConfigureAwait(true);
                    break;

                case EmbyItemType.Season:
                    await LoadEpisodesAsync(detail, retarget: true, token).ConfigureAwait(true);
                    break;

                // An episode page lists its own siblings, which is what anyone who just finished one is
                // looking for. It keeps itself as the play target, so retarget is false.
                case EmbyItemType.Episode:
                    await LoadEpisodesAsync(null, retarget: false, token).ConfigureAwait(true);
                    break;
            }

            // Cannot throw: it swallows its own failures, on purpose — see its own note.
            await similar.ConfigureAwait(true);

            EndLoad(token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            Log.Warn(Category, "加载详情失败", error);
            if (!IsCurrent(token)) return;

            Report("加载详情失败", error);

            // Ready, not stuck: IsReady means the first load finished, whether it found anything or not,
            // and the startup self-check waits on it before it reads the page.
            EndLoad(token);
        }
    }

    /// <summary>
    /// 用点进来那张卡片先画一屏，早于详情那一趟往返。只写卡片真的知道的那几样 —— 列表接口要的字段集
    /// （<c>EmbyFields.Browse</c>）带着 Overview、ProductionYear、Genres、Tags 和全部图片标签，所以片名、副标题
    /// 那一行类型、简介、已看与收藏、以及四张图在这一拍就都齐了。
    /// <para>
    /// **不写详情接口才有的那几样**：评分（CommunityRating）、事实那一行（分级、完结年份、工作室）、导演和演职
    /// 人员（People）、还有播放目标和它那三个文件选项（MediaSources）。那些字段在卡片上不是「空的」而是「没问过」
    /// —— 拿空值先画一遍，屏上就是一行事实凭空长出来、一个播放按钮先指着一个没有片源的目标。
    /// </para>
    /// <para>
    /// <see cref="Apply"/> 紧接着会用完整条目把整页重写一遍，包括这里写过的每一样；那时候相同的值不会再通知一次
    /// （生成的 setter 只在真变了的时候通知），而那四张图在图没变的时候整个留着（见 <see cref="Apply"/>）。
    /// </para>
    /// </summary>
    private void Preview(EmbyItem seed)
    {
        // **只在第一次载入时画。** 屏上已经有东西了就一个字都不许改：重新读取（「标记为已观看」之后那一次、手动
        // 刷新）走的是同一个 LoadAsync，而手上那张卡片是当初点进来时的那一份 —— 拿它盖一遍就是把整页退回旧值。
        // 看得见的那一处是那个勾：点「标记为已观看」之后它会先跳回未看，一趟往返之后才变回来；而那一趟要是失败
        // （服务器打个嗝），屏上就一直停在「未看」，可服务器上已经是看过了。
        if (_detail is not null) return;

        // 存根不够画一屏：集页那行剧名点过去、演职人员那一排点过去，交出来的只有 id、名字和类型（见
        // EmbyItem.IsStub）。拿它先画就是先写一遍「暂无简介」、头图的位置先空一下再长出来 —— 那是闪，不是快。
        if (seed.IsStub) return;

        _previews++;
        _detail = seed;

        // 这一页讲的是哪一类东西，从这一拍起就定了 —— 带子的高、名牌落哪个角、那一行文件选项开不开。少喊这一句的
        // 下场是第一屏用「没有条目」那一档的答案画，详情回来再跳一次。
        AnnounceShape();

        // 这一份和 Apply 里那一段同源，所以两处的答案不会分叉 —— 这里少的正是「卡片答不出的那几样」。
        Title = ItemDetail.Title(seed);
        TitleLink = ItemDetail.TitleTarget(seed);
        Subline = ItemDetail.Subline(seed);
        SublineGenres = ItemDetail.SublineGenres(seed);
        Overview = string.IsNullOrWhiteSpace(seed.Overview) ? "暂无简介。" : seed.Overview.Trim();

        Watched = seed.IsWatched;
        Favorite = seed.UserData?.IsFavorite == true;

        var episode = seed.Type == EmbyItemType.Episode;
        StillWidth = episode ? EpisodeStillWidth : PosterStillWidth;
        StillHeight = episode ? EpisodeStillHeight : PosterStillHeight;

        HeroArt = ItemArtwork.Hero(seed).Count > 0;

        // 四张图现在就开始取 —— 它们是最慢的一样，而卡片手上的标签和详情接口给的是同一批。
        _art?.Cancel();
        _art?.Dispose();
        _art = new CancellationTokenSource();
        _artStarts++;
        _ = LoadArtworkAsync(seed, _art);

        _previewed = Title;
    }

    /// <summary>
    /// 「这一页讲的是哪一类东西」变了之后要重新问一遍的那一批算得属性。<see cref="Preview"/> 和
    /// <see cref="Apply"/> 各喊一次 —— 前者是第一次（在这之前 <see cref="_detail"/> 是 null，那几个属性答的是
    /// 「没有条目」那一档，屏上就是一格算错高的带子），后者是完整条目到手之后。
    /// <para>
    /// 一处集中而不是散在两边：这几个属性的共同点是它们跟着**条目的种类**走，不跟着任何一个字段走，所以生成的
    /// setter 一个都不会替它们通知。少喊一个的下场是屏上某一格停在上一档 —— 编译看不见，测试也进不来。
    /// </para>
    /// <para>
    /// 剧名上方那枚徽标和右上角那张艺术图<em>不在</em>这一批里，从「统一改为在剧名上方显示徽标，右上角显示艺术图」
    /// 那次起就不在了：它们的显隐只问一句「图解出来了没有」（见 <see cref="PlateVisibility"/>），而那一句跟着
    /// <see cref="PlateImage"/> 自己的 setter 走。从前名牌摆哪个角是按页面的种类分的，那时候它确实要在这儿喊一声。
    /// </para>
    /// </summary>
    private void AnnounceShape()
    {
        // 单集 changes shape with the kind of page this is, and that is a fact about the item rather than
        // about the shelf — no collection has changed here, so nothing else would say it. Raised right after
        // the assignment, before anything can read a visibility off the item that is no longer showing.
        OnPropertyChanged(nameof(EpisodeListVisibility));
        OnPropertyChanged(nameof(EpisodeStripVisibility));

        // 带子的高也跟着页面的种类走（集页按里面那一叠实测给，见 HeroHeight），那一带集摆在图上还是纸上同理。
        // 同样是「条目的事」而不是「谁的值变了」：从一部电影翻到一集时 HeroArt 和视口都可能一个字没改，
        // 那两处的通知一次都不会来。
        OnPropertyChanged(nameof(HeroHeight));
        OnPropertyChanged(nameof(ScrimHeight));
        OnPropertyChanged(nameof(BodyMinHeight));
        OnPropertyChanged(nameof(TailMinHeight));
        OnPropertyChanged(nameof(EpisodesOnScrim));
        OnPropertyChanged(nameof(HeroInset));
        OnPropertyChanged(nameof(TailInset));
        OnPropertyChanged(nameof(HeroRoom));

        // 那一行文件选项跟着播放键的落点开合（见 PickersVisibility）：落点是空的（人物页、集还没回来）就收起来。
        // 轨道那几个集合是上一个条目留下的，从一部剧翻到一集时它们可能一个都没变，那边的通知一次不会来。
        OnPropertyChanged(nameof(PickersVisibility));

        // 页尾那张横幅只摆在三种页面上（电影、剧、集，见 FooterVisibility），也就是跟着页面的种类走。图本身可能
        // 还是上一个条目那张，那边的通知一次不会来。
        OnPropertyChanged(nameof(FooterVisibility));
    }

    /// <summary>
    /// Everything the item itself says, before anything else is fetched. Also the reset: every list and
    /// every string that belongs to the previous item is cleared here rather than left to whichever
    /// later step happens to overwrite it.
    /// </summary>
    private void Apply(EmbyItem item)
    {
        // 这一趟要不要重取那四张图：<see cref="Preview"/> 刚用卡片那一份起过一次，而完整条目回来时那几个图片标签
        // 通常一个字都没变 —— 那时候整批留着。重取的代价不是一次下载（缓存都在），是屏上的图先被清空再回来，
        // 也就是点一张封面进来看见的那一次闪。
        var keepArtwork = ItemArtwork.SamePictures(_detail, item);

        _detail = item;
        _loadedSeason = null;
        _episodes.Clear();

        AnnounceShape();

        EpisodeFocus = 0;

        EpisodeShelf?.Clear();
        CastShelf?.Clear();
        SeasonShelf?.Clear();
        SimilarShelf?.Clear();
        Seasons.Clear();

        Expanded = false;
        OverviewTrimmed = false;

        Title = ItemDetail.Title(item);
        TitleLink = ItemDetail.TitleTarget(item);
        Subline = ItemDetail.Subline(item);
        SublineGenres = ItemDetail.SublineGenres(item);
        Score = ItemDetail.Score(item);
        Facts = ItemDetail.Facts(item);
        Directors = ItemDetail.Directors(item);
        Overview = string.IsNullOrWhiteSpace(item.Overview) ? "暂无简介。" : item.Overview.Trim();

        Watched = item.IsWatched;
        Favorite = item.UserData?.IsFavorite == true;

        // A poster cropped to 16:9 loses the half with the title on it, so only an episode gets the wide
        // shape; the rest keep 2:3. 这两个数是上限，不是定值 —— 图解出来之后按它自己的形状收窄，见 ShowStill。
        var episode = item.Type == EmbyItemType.Episode;

        // 图留着的那一档连这两个数一起留着：海报到手时 ShowStill 已经把它们按图自己的形状收窄过，写回上限就是把那
        // 一格重新撑开一次，屏上是海报周围凭空多出一条边再收回去。
        if (!keepArtwork)
        {
            StillWidth = episode ? EpisodeStillWidth : PosterStillWidth;
            StillHeight = episode ? EpisodeStillHeight : PosterStillHeight;
        }

        // 再问一遍，因为完整条目才是权威的那一份 —— 列表上的条目偶尔比它少几个标签。多数时候两次的答案一样，
        // 于是这一句什么也不改：生成的 setter 只在值真变了的时候才通知。
        HeroArt = ItemArtwork.Hero(item).Count > 0;

        // 图一样就整批留着，连正在飞的那一趟一起（Fresh 比的是「要画的还是这批图」而不是「还是同一个对象」，
        // 所以卡片那一份起的解码回来照样贴得上）。不一样才清空重取 —— 换季、翻页、或者服务器上换过图。
        if (!keepArtwork)
        {
            HeroImage = null;
            StillImage = null;
            PlateImage = null;
            CornerImage = null;
            FooterImage = null;
            FooterPick = null;

            _art?.Cancel();
            _art?.Dispose();
            _art = new CancellationTokenSource();
            _artStarts++;
            _ = LoadArtworkAsync(item, _art);
        }

        CastShelf?.Fill(ItemDetail.Cast(item).Select(credit => (credit.Card, (string?)credit.Credit)));

        PlayTarget = item.IsPlayable ? item : null;
        ShowTarget(PlayTarget);
    }

    /// <summary>
    /// Fills in everything that describes the file 播放 would start: the 媒体源 picker, the two track
    /// pickers, the video line and the 媒体信息 panel. Null clears all of it — a show is not a file.
    /// <para>
    /// The last two lines are what replaces the 「are we the ones assigning this?」 flag the page used to
    /// keep. <see cref="ShowSource"/> has to run exactly once, and the generated setter raises its
    /// changed handler only when the value actually differs; so assigning a row the picker already holds
    /// would silently skip the work, and assigning a different one would do it twice if this method did
    /// it as well. Asking which case this is settles both, and the comparison is the same
    /// <c>EqualityComparer&lt;T&gt;.Default</c> the generated setter itself uses.
    /// </para>
    /// </summary>
    private void ShowTarget(EmbyItem? target)
    {
        Sources.Clear();

        if (target is null)
        {
            ShowSource(null);
            return;
        }

        foreach (var row in ItemDetail.SourceRows(target)) Sources.Add(row);

        var wanted = target.DefaultMediaSource ?? target.MediaSources.FirstOrDefault();
        var picked = ItemDetail.PickSource(Sources, wanted);

        if (Equals(SelectedSource, picked)) ShowSource(picked?.Source);
        else SelectedSource = picked;
    }

    /// <summary>
    /// Applies one media source: its tracks, its video line and its 媒体信息 panel. Runs for the source
    /// the page lands on by itself as well as for one the user picks, so the panel and the picker above
    /// it can never describe two different files.
    /// </summary>
    private void ShowSource(MediaSource? source)
    {
        VideoLine = ItemDetail.VideoLine(source);

        AudioTracks.Clear();
        SubtitleTracks.Clear();

        if (source is not null && Attached)
        {
            var playback = Settings.Playback;
            foreach (var row in ItemDetail.AudioRows(playback, source)) AudioTracks.Add(row);
            foreach (var row in ItemDetail.SubtitleRows(playback, source)) SubtitleTracks.Add(row);
        }

        // Assigned rather than left to the picker's own reset. A picker that has never been realised —
        // the page was navigated away from before it drew — writes nothing back, and the stale track it
        // would leave behind is one 播放 would then hand to mpv.
        SelectedAudio = AudioTracks.FirstOrDefault();
        SelectedSubtitle = SubtitleTracks.FirstOrDefault();

        // 媒体信息 describes a file, so it belongs on the page of a file: 电影 and 单集 get it, 剧 and 季 do
        // not. A show's page names a next-up episode in its play button, and a table of that episode's
        // codecs and subtitle tracks under a page about the whole show answered a question nobody asked —
        // the reader is one click from the episode's own page, where the table is opened by default.
        InfoRows.Clear();
        if (!Attached || _detail is not { IsPlayable: true }) return;

        foreach (var row in ItemDetail.MediaInfo(_detail, source)) InfoRows.Add(row);
    }

    private async Task LoadSeriesAsync(EmbyItem series, CancellationToken token)
    {
        if (!Attached) return;

        var seasons = await _session!
            .ExecuteAsync((client, ct) => client.GetSeasonsAsync(series.Id, ct), token).ConfigureAwait(true);
        if (!IsCurrent(token)) return;

        Seasons.Clear();
        foreach (var season in seasons) Seasons.Add(season);

        // 全部剧季 as cards, the way Emby's own series page browses seasons — each card opens that
        // season's page. The picker stays: it switches the episode list below without leaving the page.
        SeasonShelf?.Fill(seasons, ItemDetail.SeasonSubtitle);

        // Recorded before the picker is told, so the picker's report of the selection it was just handed
        // is not read back as a person choosing a season and does not start a second load of the very
        // episodes this call is about to fetch.
        var picked = ItemDetail.PickSeason(seasons);
        _loadedSeason = picked;
        SelectedSeason = picked;

        await LoadEpisodesAsync(picked, retarget: true, token).ConfigureAwait(true);
    }

    /// <param name="retarget">
    /// True when 播放 should follow the list — the first unwatched episode of the season just loaded.
    /// False on an episode's own page, where the target is the episode the reader opened.
    /// </param>
    private async Task LoadEpisodesAsync(EmbyItem? season, bool retarget, CancellationToken token)
    {
        if (!Attached || _detail is null || EpisodeShelf is not { } shelf) return;

        var scope = ItemDetail.EpisodeScope(_detail, season);
        var episodes = await _session!
            .ExecuteAsync((client, ct) => client.GetEpisodesAsync(scope.SeriesId, scope.SeasonId, ct), token)
            .ConfigureAwait(true);
        if (!IsCurrent(token)) return;

        // Commit the season and its rows together. Until the request has succeeded, _loadedSeason keeps
        // naming the rows still visible on screen, which also gives SwitchSeasonAsync a truthful value
        // to restore when the server rejects the new season.
        _loadedSeason = season;

        _episodes.Clear();
        _episodes.AddRange(episodes);

        // One shelf, two shapes. Rows on a season page — and the page itself goes in so the row that is this
        // page can mark itself, which on any other page matches nothing and marks nothing. Cards everywhere
        // else, where the second line is 「第 7 集 · 46 分钟」 rather than the card default's 「S02E09 ·
        // 剧名」: both halves of that are already on this page.
        if (EpisodesAsList) shelf.FillRows(episodes, _detail);
        else shelf.Fill(episodes, ItemDetail.EpisodeCardSubtitle);

        shelf.Title = ItemDetail.EpisodeHeading(scope.SeasonName, episodes);

        // Assigned after the cards, so the strip has something to measure itself against; it keeps the
        // request until the first card is laid out either way. Zero on every page but an episode's own.
        EpisodeFocus = ItemDetail.EpisodeFocus(episodes, _detail);

        if (!retarget) return;

        if (ItemDetail.PickEpisode(episodes) is not { } target)
        {
            PlayTarget = null;
            ShowTarget(null);
            return;
        }

        await ApplyPlayTargetAsync(target, token).ConfigureAwait(true);
    }

    /// <summary>
    /// 更多类似. Not awaited-failed: a server with no recommendations, or one that refuses the Similar
    /// call, leaves an empty row hidden rather than an error on a page whose whole job was done. A
    /// person is not offered 「items like this person」 either — Emby skips it there too.
    /// </summary>
    private async Task LoadSimilarAsync(EmbyItem item, CancellationToken token)
    {
        if (!Attached || SimilarShelf is not { } shelf) return;
        if (item.Type == EmbyItemType.Person) return;

        try
        {
            var similar = await _session!
                .ExecuteAsync((client, ct) => client.GetSimilarAsync(item.Id, ItemDetail.SimilarLimit, ct), token)
                .ConfigureAwait(true);
            if (!IsCurrent(token)) return;

            shelf.Fill(similar);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            Log.Debug(Category, $"加载更多类似失败（{item.Name}）：{error.Message}");
        }
    }

    /// <summary>
    /// Makes one episode the thing 播放 would start. Re-fetched when the list's copy carries no media
    /// sources, which is most of the time: the fields a list asks for leave them out, and the pickers,
    /// the video line and 媒体信息 are all about them.
    /// </summary>
    private async Task ApplyPlayTargetAsync(EmbyItem target, CancellationToken token)
    {
        if (!Attached) return;

        var detail = target.MediaSources.Count > 0
            ? target
            : await _session!.ExecuteAsync((client, ct) => client.GetItemAsync(target.Id, ct), token)
                .ConfigureAwait(true);
        if (!IsCurrent(token)) return;

        PlayTarget = detail;
        ShowTarget(detail);
    }

    private async Task LoadArtworkAsync(EmbyItem item, CancellationTokenSource art)
    {
        if (!Attached) return;

        // An episode's own Primary is the still from the episode; a film's is its poster, and its
        // Backdrop is the same picture already blurred behind the title, so it is not offered here.
        var episode = item.Type == EmbyItemType.Episode;
        var types = episode
            ? new[] { EmbyImageStore.Primary, EmbyImageStore.Thumb, EmbyImageStore.Backdrop }
            : [EmbyImageStore.Primary, EmbyImageStore.Thumb];

        // 三样图三个位置，四种页面同一套 —— 「统一改为在剧名上方显示徽标，右上角显示艺术图」加上「在电影页面
        // 剧页面 集页面的底部添加横幅」。不再按页面的种类分岔（从前集页问名牌、别的页面问「艺术图优先、没有就退
        // 名牌」那一支），也因此不再有「哪一张让哪一张」：服务器没有的那一样就是那一格空着。
        var plate = ItemArtwork.Plate(item);
        var corner = ItemArtwork.Corner(item);

        try
        {
            // 五张图分头去取。以前是一张接一张：名牌回来了才开始要头图，头图回来了才开始要剧照 —— 三次往返
            // 排成一队，慢的那一张拖住后面两张。它们之间没有任何依赖，谁先回来谁先显示，版面不看先后。
            await Task.WhenAll(
                    Paint(DecodeFirstAsync(Refs(plate), PlateDecodeWidth, art.Token),
                        picture => PlateImage = picture),
                    Paint(DecodeFirstAsync(ItemArtwork.Hero(item), HeroDecodeWidth, art.Token),
                        picture => HeroImage = picture),
                    Paint(DecodeFirstAsync(Refs(corner), CornerDecodeWidth, art.Token),
                        picture => CornerImage = picture),
                    Paint(FooterAsync(item, art.Token), picture => FooterImage = picture),
                    Paint(DecodeFirstAsync(item, types, episode ? EpisodeStillWidth : PosterStillWidth, art.Token),
                        picture => ShowStill(picture, episode)))
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            // A missing picture is not worth a notice bar; the page reads perfectly well without one.
            Log.Debug(Category, $"加载详情图片失败：{error.Message}");
        }

        // Every one of the four ends the same way: show it if it arrived and if this page is still the
        // page that asked. Both halves of that check matter — see Fresh.
        async Task Paint(Task<BitmapImage?> decode, Action<BitmapImage> assign)
        {
            var picture = await decode.ConfigureAwait(true);
            if (Fresh(art, item) && picture is not null) assign(picture);
        }

        // 页尾那张横幅比别的几张多一步：一集自己几乎不会有横幅图（服务器把它挂在剧集那一层，同徽标），所以那一档
        // 要多问一趟剧集才借得到（<see cref="ItemArtwork.Footer"/> —— 服务器不下发 ParentBanner）。多这一趟的代价
        // 认得清：只在集页、只在这一集自己没有横幅图的时候问，而它换来的是「集页面的底部」真的有东西。它误不了
        // 任何一件正事 —— 那张图在整页最底下、滑到底才看得见，问失败就是那儿空着（catch 掉，不往上抛）。
        async Task<BitmapImage?> FooterAsync(EmbyItem page, CancellationToken token)
        {
            var pick = ItemArtwork.Footer(page);

            if (pick is null && page.Type == EmbyItemType.Episode && page.SeriesId is { Length: > 0 } series)
            {
                try
                {
                    var show = await _session!
                        .ExecuteAsync((client, ct) => client.GetItemAsync(series, ct), token)
                        .ConfigureAwait(true);
                    pick = ItemArtwork.Footer(page, show);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
                catch (Exception error)
                {
                    Log.Debug(Category, $"取剧集那张横幅失败（{page.Name}）：{error.Message}");
                    return null;
                }
            }

            // 记下这一次取的是哪一张，给自检读（见 FooterPick）。图解不出来也照记：那时候「规矩说该有一张、屏上
            // 没画」是一句准确的读数，而记成 null 会把它说成「本来就没有」。
            if (Fresh(art, page)) FooterPick = pick;

            return await DecodeFirstAsync(Refs(pick), FooterDecodeWidth, token).ConfigureAwait(true);
        }

        // 一张图或者一张都没有，都写成那个重载要的那一串。null 就是「服务器上没有这一张」，走的是同一条空路 ——
        // 空串取不到东西，那个位置就一直空着。从前还有一档「这一页不摆这一张」，那是名牌按页面的种类分角的年代。
        static IReadOnlyList<ArtworkRef> Refs(ArtworkRef? one) => one is { } single ? [single] : [];
    }

    /// <summary>
    /// 海报（集页上是剧照）到了：连它该画多大一起换上 —— 「海报下方会被裁切，要能看到完整的海报」。
    /// <para>
    /// 那一格原来是写死的 210×300、图按 <c>UniformToFill</c> 铺满它，而服务器上的海报是 2:3，于是上下各裁掉七八
    /// 个像素。现在那一格按这张图自己的形状收窄（<see cref="DetailHero.StillBox"/>），一个像素都不裁。
    /// </para>
    /// <para>
    /// 换尺寸和换图是同一拍里的两件事，顺序也就要紧：先尺寸再图。反过来的那一版会先按上一档尺寸画一帧、再收窄，
    /// 屏上是海报出现之后抖一下。而在这之前这一格整个是收着的（<see cref="StillVisibility"/> 问的就是有没有图），
    /// 所以这一拍之前那个尺寸谁也没看见。
    /// </para>
    /// </summary>
    private void ShowStill(BitmapImage picture, bool episode)
    {
        var box = DetailHero.StillBox(picture.PixelWidth, picture.PixelHeight,
            episode ? EpisodeStillWidth : PosterStillWidth,
            episode ? EpisodeStillHeight : PosterStillHeight);

        if (box is { } fit)
        {
            StillWidth = fit.Width;
            StillHeight = fit.Height;
        }

        StillImage = picture;
    }

    /// <summary>
    /// Whether this artwork request is still the newest one and still describes the item on screen. Two
    /// questions rather than one: a season switch leaves the artwork alone on purpose, so the token
    /// alone would let a stale picture through, and a reload replaces the item object, so the item alone
    /// would let a cancelled one through.
    /// <para>
    /// 第二问比的是「要画的还不还是这批图」而不是「还不还是同一个对象」（<c>ItemArtwork.SamePictures</c>）：
    /// 详情页现在先用点进来那张卡片起这一趟，完整条目回来时 <see cref="_detail"/> 换成了另一个对象，而要取的是同
    /// 一批文件 —— 按对象比就等于把卡片那一份起的解码全部丢掉，也就白起了。换条目、换季、服务器上换过图，这几种
    /// id 或者标签会变，照旧判 false。
    /// </para>
    /// </summary>
    private bool Fresh(CancellationTokenSource art, EmbyItem item) =>
        ReferenceEquals(_art, art) && ItemArtwork.SamePictures(_detail, item);

    private async Task<BitmapImage?> DecodeFirstAsync(
        EmbyItem item, IReadOnlyList<string> types, int width, CancellationToken token)
    {
        if (!Attached) return null;

        foreach (var type in types)
        {
            var bytes = await _images!
                .GetAsync(item, type, EmbyImageStore.RequestWidth(width), token).ConfigureAwait(true);
            if (bytes is null || bytes.Length == 0) continue;

            var bitmap = await PosterLoader.DecodeAsync(bytes, width).ConfigureAwait(true);
            if (bitmap is not null) return bitmap;
        }

        return null;
    }

    /// <summary>
    /// 同上，只是这一串图各有各的主人：谁的哪一种、按哪个标签取，都由 <see cref="ItemArtwork"/> 事先定好
    /// （<see cref="ItemArtwork.Hero"/>）。集页和季页头上那张图是剧集那一层的，标签属于剧集那一头的 id，按这一页
    /// 自己的 id 去取只会取回一个空答案 —— 那不是错误，是一页安静地不显示背景图。
    /// <para>
    /// 挨着往下退这件事两个重载是一样的：某一张取不回来或者解不出来（服务器上那一版是坏的、格式不认），换下一张，
    /// 而不是让整页退回没有图的那一档版面。
    /// </para>
    /// </summary>
    private async Task<BitmapImage?> DecodeFirstAsync(
        IReadOnlyList<ArtworkRef> pictures, int width, CancellationToken token)
    {
        if (!Attached) return null;

        foreach (var picture in pictures)
        {
            var bytes = await _images!
                .GetAsync(picture.ItemId, picture.ImageType, picture.Tag,
                    EmbyImageStore.RequestWidth(width), token)
                .ConfigureAwait(true);
            if (bytes is null || bytes.Length == 0) continue;

            var bitmap = await PosterLoader.DecodeAsync(bytes, width).ConfigureAwait(true);
            if (bitmap is not null) return bitmap;
        }

        return null;
    }

    // ---------------------------------------------------------------- 选择

    /// <summary>
    /// The 季 picker moved. Null is the picker reporting that its items were replaced rather than a
    /// person choosing nothing, and <see cref="_loadedSeason"/> is the season already on screen — both
    /// are the picker talking about itself, and neither is worth a round trip.
    /// </summary>
    partial void OnSelectedSeasonChanged(EmbyItem? value)
    {
        if (value is null || ReferenceEquals(value, _loadedSeason)) return;

        _ = SwitchSeasonAsync(value);
    }

    partial void OnSelectedSourceChanged(SourceRow? value) => ShowSource(value?.Source);

    /// <summary>
    /// Another season's episodes, without re-reading the show. Its own load rather than a call into
    /// <see cref="LoadAsync"/>: the title, the artwork and the cast belong to the show and are already
    /// right, and re-fetching them would blank the hero for as long as the round trip takes.
    /// </summary>
    private async Task SwitchSeasonAsync(EmbyItem season)
    {
        if (!Attached) return;

        var token = BeginLoad();

        try
        {
            await LoadEpisodesAsync(season, retarget: true, token).ConfigureAwait(true);
            EndLoad(token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            Log.Warn(Category, "加载单集失败", error);
            if (!IsCurrent(token)) return;

            // The old shelf was deliberately left in place while loading. Put the picker back on the
            // season that shelf belongs to; its changed hook sees the same _loadedSeason and does not
            // start another request. The failed season remains selectable, so the user can retry it.
            SelectedSeason = _loadedSeason;
            _actions?.Notify($"加载单集失败：{Failure.Describe(error)}", InfoBarSeverity.Error);
            EndLoad(token);
        }
    }

    private void OnEpisodeCardsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        OnPropertyChanged(nameof(EpisodeVisibility));

        // Both shapes, because both are 「is there anything」 as well as 「is this shape mine」: the block
        // going from empty to filled has to reveal one of the two, and it is the count that just changed.
        OnPropertyChanged(nameof(EpisodeListVisibility));
        OnPropertyChanged(nameof(EpisodeStripVisibility));
    }

    private void OnCastCardsChanged(object? sender, NotifyCollectionChangedEventArgs args) =>
        OnPropertyChanged(nameof(CastVisibility));

    private void OnSeasonShelfChanged(object? sender, NotifyCollectionChangedEventArgs args) =>
        OnPropertyChanged(nameof(SeasonShelfVisibility));

    private void OnSimilarCardsChanged(object? sender, NotifyCollectionChangedEventArgs args) =>
        OnPropertyChanged(nameof(SimilarVisibility));

    private void PickerChanged(string which)
    {
        OnPropertyChanged(which);
        OnPropertyChanged(nameof(PickersVisibility));
    }

    // ---------------------------------------------------------------- 命令

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private Task PlayAsync() => StartAsync(fromStart: false);

    /// <summary>从头开始. A second button rather than a switch, because 播放 already states the time.</summary>
    [RelayCommand(CanExecute = nameof(CanPlay))]
    private Task RestartAsync() => StartAsync(fromStart: true);

    private async Task StartAsync(bool fromStart)
    {
        if (!Attached || _actions is null || PlayTarget is not { } target) return;

        var parent = target.Type == EmbyItemType.Episode
            && _detail is { Type: EmbyItemType.Series or EmbyItemType.Season }
            ? _detail
            : null;

        // No source at all is still playable: the player asks the server for one itself, and refusing
        // here would turn a server that reports its files late into a button that does nothing.
        if ((SelectedSource?.Source ?? target.DefaultMediaSource) is not { } source)
        {
            await _actions.PlayAsync(target, parent, episodes: _episodes).ConfigureAwait(true);
            return;
        }

        var start = fromStart || !Settings.Playback.ResumeFromSavedPosition ? 0 : target.ResumeTicks;
        var choice = new PlaybackChoice(source, SelectedAudio?.Stream?.Index, SelectedSubtitle?.Stream?.Index,
            SelectedSubtitle?.Disable == true, start);

        await _actions.PlayAsync(target, parent, choice, _episodes).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task ToggleWatchedAsync()
    {
        if (!Attached || _detail is not { } item) return;

        var played = item.IsWatched;

        try
        {
            await _session!.ExecuteAsync((client, token) => played
                    ? client.MarkUnplayedAsync(item.Id, token)
                    : client.MarkPlayedAsync(item.Id, token), CancellationToken.None)
                .ConfigureAwait(true);

            // Re-read rather than flipped locally: marking a season watched moves every episode's tick
            // and the show's unplayed count with it, and only the server knows what it did.
            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _actions?.Notify($"更新观看状态失败：{Failure.Describe(error)}", InfoBarSeverity.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task ToggleFavoriteAsync()
    {
        if (!Attached || _detail is not { } item) return;

        var favorite = item.UserData?.IsFavorite == true;

        try
        {
            await _session!.ExecuteAsync(
                    (client, token) => client.SetFavoriteAsync(item.Id, !favorite, token), CancellationToken.None)
                .ConfigureAwait(true);

            // One flag, flipped where it is kept. 观看状态 above re-reads the item because the server moves
            // other things with it; 收藏 moves nothing — and a reload here cost the page its artwork, its
            // 演职人员 row and its 更多类似 row, all re-fetched to answer a question the click already
            // answered. The item's own copy is updated too, so the next click reads the current value.
            item.UserData ??= new EmbyUserData();
            item.UserData.IsFavorite = !favorite;
            Favorite = !favorite;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _actions?.Notify($"更新收藏失败：{Failure.Describe(error)}", InfoBarSeverity.Error);
        }
    }

    /// <summary>Emby's own page for the item — the way to reach the fields this client cannot edit.</summary>
    [RelayCommand]
    private void OpenBrowser()
    {
        if (!Attached || _actions is null || _detail is not { } item) return;

        if (_session!.Connection is not { } connection)
        {
            _actions.Notify("尚未连接到服务器", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            _launcher!.OpenUrl(ItemDetail.WebUrl(connection.ApiBase, item));
        }
        catch (Exception error)
        {
            Log.Warn(Category, "打开网页端失败", error);
            _actions.Notify($"打开浏览器失败：{Failure.Describe(error)}", InfoBarSeverity.Error);
        }
    }

    [RelayCommand]
    private void ToggleOverview() => Expanded = !Expanded;

    /// <summary>
    /// The headline on an episode page — 「99.9 刑事专业律师」 above 「S2:E7 - …」 — opens 那部剧自己的页面.
    /// Where that goes is <see cref="ItemDetail.TitleTarget"/>'s decision; this only hands it to the
    /// shell, the same way a card click does. 季页面不从这儿进（见那边的注），它的入口是剧页面上
    /// 「全部剧季」那一格。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenTitle))]
    private void OpenTitle()
    {
        if (TitleLink is { } link) _actions?.OpenItem(link);
    }

    // ---------------------------------------------------------------- 导航

    /// <summary>
    /// An episode card opens that episode's own page; a face opens what that person is in. Both go
    /// through the shell, which is the one place the 「detail page, person, or another grid」 choice is
    /// made — this page used to make the person half of that choice again for itself, and now that the
    /// shell knows a person is not a folder there is no reason for two copies of the answer.
    /// </summary>
    internal void OpenCard(CardItem card) => _actions?.OpenItem(card.Item);

    /// <summary>
    /// Opens the first card in 单集, the same way a click on it would. For the startup self-check, whose
    /// walk arrives here off a library row — a show, on any TV library — while 媒体信息 only exists one page
    /// further in, on the episode's own. Null when this page has no episode strip to click.
    /// </summary>
    internal EmbyItem? OpenFirstEpisode()
    {
        if (EpisodeShelf?.Cards.FirstOrDefault(card => card.Item.Type == EmbyItemType.Episode) is not { } card)
            return null;

        OpenCard(card);
        return card.Item;
    }

    /// <summary>What a context menu calls after it has changed something on the server.</summary>
    internal void Reload() => _ = LoadAsync();

    /// <inheritdoc />
    public override void Cancel()
    {
        base.Cancel();
        _art?.Cancel();
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        base.Dispose();

        _art?.Cancel();
        _art?.Dispose();
        _art = null;
    }
}

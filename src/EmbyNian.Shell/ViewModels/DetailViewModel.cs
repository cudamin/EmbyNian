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
    /// 右上角那张艺术图（<see cref="ItemArtwork.Corner"/>）的解码宽度，同它画出来的那个盒子
    /// （<see cref="DetailHero.CornerWidth"/>×<see cref="DetailHero.CornerHeight"/>）。它是一幅装饰画，不是读数，
    /// 480 宽在它的上限盒子上已经够清晰 —— 2026-09-12 按他的「放大集页面右侧的艺术图」从 320 一起抬上来的。
    /// </summary>
    private const int CornerDecodeWidth = (int)DetailHero.CornerWidth;

    /// <summary>
    /// 页尾那张横幅（<see cref="ItemArtwork.Footer"/>）的解码宽度，同它画出来的那个上限（760）。横幅图上写着片名，
    /// 所以它是这一页上唯一一处「字」由位图承担、又画得很大的地方 —— 解窄了糊的是片名本身。
    /// </summary>
    private const int FooterDecodeWidth = 760;

    /// <summary>
    /// The still beside the title. Fixed rather than scaled with the grid's poster width, unlike every
    /// other card in the app. 头上那一格的高是按内容定死的（<see cref="DetailHero.ArtHeight"/> = 412，412 里
    /// 装着海报 300、那排键那一行 60 加两道边，正好装满），
    /// 再让这一张跟着海报一起宽，那格带子就装不下它了。
    /// </summary>
    private const int PosterStillWidth = 210;

    private const int PosterStillHeight = 300;

    /// <summary>
    /// 封面这一次按多宽解码 —— 画出来多宽就取多宽（集页的宽版式随窗口走，
    /// <see cref="DetailHero.StillWidth"/>；取图那一刻的宽说了算，窗口随后再变不重解码，同一张图缓存里只有
    /// 那一份）。集页的封面是剧的那张 2:3 海报（「集页面的封面改用剧页面的封面」），基宽同一张：210。
    /// 取整到像素。取图不看画不画（<see cref="DetailHero.ShowsStill"/>）：不画的档照取，窗口拉宽跨过
    /// <see cref="DetailHero.CompactFloor"/> 的那一下封面就地回来，不用再等一趟往返。
    /// </summary>
    private int StillDecodeWidth => IsEpisodePage
        ? (int)Math.Ceiling(IsCompact
            ? (double)PosterStillWidth
            : DetailHero.StillWidth(PageWidth, PosterStillWidth, HeroReferenceWidth))
        : PosterStillWidth;

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

        // 媒体源那一行多喊一声「视频：…」那一行：头图上那一行在下拉出现时收起来（同一份读数不在一屏上说两遍，
        // 见 VideoVisibility），所以这个集合一变，两处的显隐都可能翻。
        Sources.CollectionChanged += (_, _) =>
        {
            PickerChanged(nameof(SourceVisibility));
            OnPropertyChanged(nameof(VideoVisibility));
        };

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

    /// <summary>
    /// 分数旁边那句「这是谁给的分」——「豆瓣」「TMDB」「烂番茄」或者「公众评分」。
    /// <para>
    /// 单独一格而不是拼进 <see cref="Score"/>：那四个字要走暗墨、跟着事实那一行的层次，而分数走强墨。而且它必须说
    /// <b>实际取到的是哪一档</b>，不是设置里选的哪一档 —— 选了豆瓣而服务器认不出豆瓣，这里写的就是「公众评分」。
    /// </para>
    /// </summary>
    [ObservableProperty]
    public partial string? ScoreLabel { get; set; }

    /// <summary>
    /// 自检用：规矩说这一格该画什么。屏上那两格由绑定填，而「该是什么」只有视图模型算得出来 —— 探针拿这一份和屏上
    /// 比，两边一致才算这一条设置真的接上了。
    /// </summary>
    internal Emby.ScoreBadge ScoreBadge { get; private set; } = Emby.ScoreBadge.None;

    /// <summary>自检用：设置里选的那一档叫什么。</summary>
    internal string ScoreSourceName => Emby.ItemScore.Describe(Settings.Ui.ScoreSource);

    /// <summary>
    /// 自检用：服务器在这个条目上到底给了什么评分字段。**只报不判** —— 有没有豆瓣的痕迹是服务器上装了哪些插件的
    /// 事，判它就是把一关的成败交给别人的配置。可它是这一关最值得读的一句：这台服务器有没有影评指数、有没有对上
    /// 豆瓣，从此每次自检都自己答一遍。
    /// </summary>
    internal string ScoreFacts { get; private set; } = "还没读到条目";

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
    [NotifyPropertyChangedFor(nameof(VideoLineVisibility))]
    public partial string? VideoLine { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeroVisibility))]
    public partial ImageSource? HeroImage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StillVisibility))]
    [NotifyPropertyChangedFor(nameof(TailInset))]
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
    /// 右上角那张艺术图 —— 「给集页面右上角添加艺术图」（2026-09-12）。哪一张归这儿是
    /// <see cref="ItemArtwork.Corner"/> 的事（这一集自己的，没有就借剧集那一层的）；这里只存解出来的那张。
    /// 只在集页上画（见 <see cref="CornerVisibility"/>），服务器两头都没有的那一档这一格空着。
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
    /// 是两件事的自变量：紧凑版式换不换（<see cref="IsCompact"/>）、以及背景那一张画多高（<see cref="PictureHeight"/>）。
    /// <para>
    /// 量的是页面而不是窗口：将来哪一侧再长出占据宽度的东西，挤的也是页面这一头。
    /// </para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompact))]
    [NotifyPropertyChangedFor(nameof(HeroLayoutWidth))]
    [NotifyPropertyChangedFor(nameof(BackdropShown))]
    [NotifyPropertyChangedFor(nameof(HeroLayoutHeight))]
    [NotifyPropertyChangedFor(nameof(HeroHeight))]
    [NotifyPropertyChangedFor(nameof(ScrimHeight))]
    [NotifyPropertyChangedFor(nameof(BodyMinHeight))]
    [NotifyPropertyChangedFor(nameof(TailMinHeight))]
    [NotifyPropertyChangedFor(nameof(PaperMinHeight))]
    [NotifyPropertyChangedFor(nameof(PictureHeight))]
    [NotifyPropertyChangedFor(nameof(PictureFadeMargin))]
    [NotifyPropertyChangedFor(nameof(HeroArtVisibility))]
    [NotifyPropertyChangedFor(nameof(HeroPlainVisibility))]
    [NotifyPropertyChangedFor(nameof(HeroCardVisibility))]
    [NotifyPropertyChangedFor(nameof(HeroCardShown))]
    [NotifyPropertyChangedFor(nameof(ScrimVisibility))]
    [NotifyPropertyChangedFor(nameof(StillVisibility))]
    [NotifyPropertyChangedFor(nameof(CompactVisibility))]
    [NotifyPropertyChangedFor(nameof(WideActionsVisibility))]
    [NotifyPropertyChangedFor(nameof(CornerVisibility))]
    [NotifyPropertyChangedFor(nameof(HeroInset))]
    [NotifyPropertyChangedFor(nameof(TailInset))]
    [NotifyPropertyChangedFor(nameof(ActionsColumn))]
    [NotifyPropertyChangedFor(nameof(ActionsColumnSpan))]
    [NotifyPropertyChangedFor(nameof(ColumnPickersVisibility))]
    [NotifyPropertyChangedFor(nameof(PickersVisibility))]
    [NotifyPropertyChangedFor(nameof(VideoLineVisibility))]
    [NotifyPropertyChangedFor(nameof(HeroContentAlignment))]
    [NotifyPropertyChangedFor(nameof(ColumnActionsVisibility))]
    [NotifyPropertyChangedFor(nameof(WideProgressVisibility))]
    public partial double PageWidth { get; set; }

    // 版式排内容的参考宽：封面（DetailHero.StillWidth）和角图让位（DetailHero.CornerBoxWidth）的比值都以它算。
    private const double HeroReferenceWidth = 1280;

    /// <summary>
    /// 这一页这次走哪套版式 —— 页面窄过 <see cref="DetailHero.CompactFloor"/> 就换成单列的紧凑版式：封面按
    /// <see cref="DetailHero.ShowsStill"/> 让位（剧、电影两页不画，季、集照旧），播放和那排操作挪到画面底下
    /// 的暗区里（<c>DetailPage</c> 标记里的紧凑块）。桌面版式不再缩小任何东西 ——
    /// 「收窄窗口后组件要自动换行」（2026-09-12）：从前 1024 到 1280 那一段整体等比缩小（<c>HeroScale</c>，
    /// 那个属性随之退役），缩到八成的组件又小又松；现在组件保持原大小，行装不下就换行，换行的落点跟着
    /// <see cref="HeroLayoutWidth"/>（就是页面本身的宽）走。
    /// </summary>
    public bool IsCompact => DetailHero.IsCompact(PageWidth);

    /// <summary>
    /// 集页整段上方合成的那一块面板在不在 —— <see cref="HeroCardVisibility"/>、<see cref="HeroPlainVisibility"/>、
    /// <see cref="ScrimVisibility"/>、<see cref="HeroInset"/> 和 <see cref="TailInset"/> 五处都问它，判据只写一份。
    /// </summary>
    private bool HeroCard => IsEpisodePage && !IsCompact;

    /// <summary>
    /// 头图和尾部的内容按多宽排 —— 页面本身的宽。从前是「至少 1280、窄了由 Viewbox 整体缩下去」：那一版换行的
    /// 落点钉在 1280 上不动，窗口窄了只会整体变小（「组件又小又松」）。现在内容按真实页宽排，行装不下就地换行
    /// （按键行、文件选项那几块 chips），封面和角图各自随宽走（<see cref="DetailHero.StillWidth"/>／
    /// <see cref="DetailHero.CornerBoxWidth"/>）—— 0 是「还没量」（第一次布局之前）。
    /// </summary>
    public double HeroLayoutWidth => Math.Max(PageWidth, 0);

    /// <summary>
    /// 背景那张位图的高 ÷ 宽（16:9 是 0.5625），图解出来时跟着 <see cref="HeroImage"/> 一起换。它喂两个地方：
    /// 紧凑版式的带高（<see cref="DetailHero.CompactHeight"/>）和背景那一张的盒高
    /// （<see cref="DetailHero.PictureHeight"/>）—— 「窗口收窄时背景图要等比例缩放」要的就是图自己的形状。
    /// <para>
    /// 图还没解出来的时候按 16:9 兜底：背景那几张全是横图，第一帧按它布、真图到了再修正，跟别的版面数同一套
    /// 「先按估值布一遍」的道理。
    /// </para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeroLayoutHeight))]
    [NotifyPropertyChangedFor(nameof(HeroHeight))]
    [NotifyPropertyChangedFor(nameof(ScrimHeight))]
    [NotifyPropertyChangedFor(nameof(PictureHeight))]
    [NotifyPropertyChangedFor(nameof(PictureFadeMargin))]
    [NotifyPropertyChangedFor(nameof(TailMinHeight))]
    public partial double HeroHeightRatio { get; set; } = 9d / 16;

    // 裁切模糊的三个数：门槛（裁切不超过它不糊 —— 「提高拉长窗口后，背景被裁切触发背景图模糊的阈值」，
    // 拉长窗口头几档的轻微裁切不值得整张图陪着糊）、裁到最狠再加多少（BackdropBlur.CropRadius 的 extra）、
    // 按多大一档量化（step）。量化是给拖窗口的：占比一路变，档位不跟着一路变，重糊只发生在换档那一下。
    private const double CropBlurStart = 0.25;
    private const int CropBlurExtra = 32;
    private const int CropBlurStep = 8;

    /// <summary>解好的头图原图像素 —— 裁切模糊的原料。导航换条目、换图时整个作废。</summary>
    private HeroPictureLoader.HeroPixels? _heroPixels;

    /// <summary>
    /// 解好的封面（集页剧照、其余海报）原图像素 —— 随窗口宽重排那一格的原料
    /// （<see cref="FitStill"/>）。换条目、换图时作废。
    /// </summary>
    private (int Width, int Height)? _stillPixels;

    /// <summary>解好的角图原图像素，同 <see cref="_stillPixels"/>（<see cref="FitCorner"/> 的原料）。</summary>
    private (int Width, int Height)? _cornerPixels;

    /// <summary>上一次渲染用的模糊档位；-1 是「还没出过图」。</summary>
    private int _heroBlurRadius = -1;

    /// <summary>重糊的生代号：谁后到谁算数，路上换了档就作废先回来那一张。</summary>
    private int _heroBlurGeneration;

    /// <summary>当前的模糊档位 —— 裁切占比（<see cref="DetailHero.PictureCrop"/>）换算的半径。电影和剧没有
    /// 基础半径，裁多少加多少；集页没有背景图，这一手到不了它。</summary>
    private int HeroBlurTarget => BackdropBlur.CropRadius(
        DetailHero.PictureCrop(PageWidth, HeroHeightRatio, Viewport), CropBlurStart, 0, CropBlurExtra, CropBlurStep);

    /// <summary>
    /// 窗口形状变了（<c>DetailPage.OnBodySizeChanged</c> 量完宽高顺手喊一声）：按新的裁切占比算模糊档位，
    /// 换档就用缓存的那批像素重糊一遍。生代号让「谁后到谁算数」—— 重糊还在路上又换了一档，先回来那一张
    /// 作废。不做可等待：拖窗口不等一次几十毫秒的像素活，屏上短暂停在上一档是这条路的正常形态。
    /// </summary>
    public void UpdateHeroBlur()
    {
        if (_heroPixels is null) return;

        var target = HeroBlurTarget;
        if (target == _heroBlurRadius) return;

        var generation = ++_heroBlurGeneration;
        _heroBlurRadius = target;

        _ = RenderBlurAsync(generation, _heroPixels, target);
    }

    private async Task RenderBlurAsync(int generation, HeroPictureLoader.HeroPixels pixels, int radius)
    {
        try
        {
            var picture = await HeroPictureLoader
                .RenderAsync(pixels, radius, _art?.Token ?? default)
                .ConfigureAwait(true);

            if (generation == _heroBlurGeneration && picture is not null) HeroImage = picture;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            Log.Debug(Category, $"背景图按裁切重糊失败：{error.Message}");
        }
    }

    /// <summary>
    /// 海报那一栏（<c>PosterStill</c>）这一次画多宽 —— 图到手时按它自己的形状收窄
    /// （<see cref="DetailHero.StillBox"/>，见 <see cref="StillHeight"/>）。
    /// <para>
    /// 尾部那一段的内边距也跟着它走（<see cref="TailInset"/> → <see cref="StackLeft"/>）：集页的宽版式上剧情
    /// 说明要和片名那一栏同一条左沿，而那条沿就是这一栏的宽加一格间距决定的 —— 图到得晚一点，那一段也就晚
    /// 一点对齐，两个数读的是同一个来源。
    /// </para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TailInset))]
    public partial double StillWidth { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeroRoom))]
    [NotifyPropertyChangedFor(nameof(HeroLayoutHeight))]
    [NotifyPropertyChangedFor(nameof(HeroHeight))]
    [NotifyPropertyChangedFor(nameof(ScrimHeight))]
    [NotifyPropertyChangedFor(nameof(BodyMinHeight))]
    [NotifyPropertyChangedFor(nameof(TailMinHeight))]
    public partial double StillHeight { get; set; }

    /// <summary>
    /// 右上角那张艺术图这一次画多高 —— 图到手时由 <see cref="ShowCorner"/> 按它的形状算进这个上限盒子里
    /// （<see cref="DetailHero.StillBox"/>，480×270），带高就跟着它长。
    /// <para>
    /// 180 → 270 这一档是「放大集页面右侧的艺术图」（2026-09-12）的直接后果：从前的上限 180 比那一叠字键的
    /// 实测高（两百六七）矮，所以它从来不决定带子的高，也就没有这个数；抬到 270 之后它比那一叠还高，不把它算
    /// 进去就是这张画压在底下的音轨那一行上。
    /// </para>
    /// <para>
    /// 图还没到、或者这一条没有艺术图时是 0 —— 那一档带高仍由字键那一叠和剧照给。
    /// </para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeroRoom))]
    [NotifyPropertyChangedFor(nameof(HeroLayoutHeight))]
    [NotifyPropertyChangedFor(nameof(HeroHeight))]
    [NotifyPropertyChangedFor(nameof(ScrimHeight))]
    [NotifyPropertyChangedFor(nameof(BodyMinHeight))]
    [NotifyPropertyChangedFor(nameof(TailMinHeight))]
    public partial double CornerHeight { get; set; }

    /// <summary>
    /// 头图上那排键自己那一行有多高，由视图量给（<c>DetailPage.OnHeroActionsSizeChanged</c>）。它们从
    /// 片名那一栏里搬出来、单独占一行之后（「继续播放 从头开始还有后面的那些图标单独一行」，2026-09-12），
    /// 这一行的高度不再算在 <see cref="StackRoom"/> 里，得单独加进 <see cref="HeroRoom"/>。
    /// <para>
    /// 量出来而不是写死一个数：那一行的高是键自己的高（<c>EgActionHeight</c>）加上面那道间距，两个都跟着主题
    /// 词表走。紧凑版式里这一行整个收着，量出来就是 0 —— 那一档的带高因此一个像素都不多算，不用另判一次版式。
    /// </para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeroRoom))]
    [NotifyPropertyChangedFor(nameof(HeroLayoutHeight))]
    [NotifyPropertyChangedFor(nameof(HeroHeight))]
    [NotifyPropertyChangedFor(nameof(ScrimHeight))]
    [NotifyPropertyChangedFor(nameof(BodyMinHeight))]
    [NotifyPropertyChangedFor(nameof(TailMinHeight))]
    public partial double ActionsRoom { get; set; }

    /// <summary>
    /// 这一页要不要按「背后铺着一张图」那一档布 —— 服务器上有没有那张图（<see cref="ItemArtwork.Hero"/> 问标签
    /// 表的结果），集页除外：那一页不铺背景图（2026-09-12「去掉集页面的背景图」），无论服务器有什么，这里都是
    /// false，整页走「没有剧照」的那一档版面（见 <see cref="DetailHero.PlainHeight"/>）。
    /// <para>
    /// 分成两个值是为了「点击主页封面后窗口会闪一下，然后才会进入页面」。版面按有没有这张图分档
    /// （背景层、尾部、纸面下限，<see cref="DetailHero.Height"/>）；拿解好的位图当判据，那一下就是先按
    /// 没有图布一遍、图到了再翻一遍，屏上看着就是闪一下。列表接口回来的条目已经带着标签，所以这句话在
    /// <see cref="Attach"/> 那一刻就答得出：第一帧的版面就是最后的版面，之后到的只是画面本身。
    /// </para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackdropShown))]
    [NotifyPropertyChangedFor(nameof(HeroLayoutHeight))]
    [NotifyPropertyChangedFor(nameof(HeroHeight))]
    [NotifyPropertyChangedFor(nameof(ScrimHeight))]
    [NotifyPropertyChangedFor(nameof(BodyMinHeight))]
    [NotifyPropertyChangedFor(nameof(TailMinHeight))]
    [NotifyPropertyChangedFor(nameof(PaperMinHeight))]
    [NotifyPropertyChangedFor(nameof(PictureHeight))]
    [NotifyPropertyChangedFor(nameof(PictureFadeMargin))]
    [NotifyPropertyChangedFor(nameof(HeroArtVisibility))]
    [NotifyPropertyChangedFor(nameof(HeroPlainVisibility))]
    public partial bool HeroArt { get; set; }

    /// <summary>
    /// 背景那一层这一次画不画 —— 只问一句：服务器上有这张图（<see cref="HeroArt"/>）。
    /// <para>
    /// 从前这里还有第二句「页面还宽到压得住它」（<c>DetailHero.BackdropFits</c>，窄过 720 整层收起来）。
    /// 「窗口收窄时背景图要等比例缩放」把那个分岔去掉了：图改画在跟宽走的盒子里（<see
    /// cref="DetailHero.PictureHeight"/>）之后，任何宽度都摆得下一张等比的画面，收起来那一档没了 —— 窄到
    /// <see cref="DetailHero.CompactFloor"/> 以下换的是整套紧凑版式，背景那张图照画，只是缩成一条。
    /// </para>
    /// <para>
    /// 所有按「有没有背景图」分档的版面问题照旧问它、不直接问 <see cref="HeroArt"/>：没有图的条目上头图退回
    /// 那一档底色版面，尾部不撑、纸面不整屏、标题栏墨色当没有图处理 —— 那些迁就都还欠着。
    /// </para>
    /// </summary>
    public bool BackdropShown => HeroArt;

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
    [NotifyPropertyChangedFor(nameof(PictureHeight))]
    [NotifyPropertyChangedFor(nameof(PictureFadeMargin))]
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
    [NotifyPropertyChangedFor(nameof(HeroLayoutHeight))]
    [NotifyPropertyChangedFor(nameof(HeroHeight))]
    [NotifyPropertyChangedFor(nameof(ScrimHeight))]
    [NotifyPropertyChangedFor(nameof(BodyMinHeight))]
    [NotifyPropertyChangedFor(nameof(TailMinHeight))]
    [NotifyPropertyChangedFor(nameof(PictureHeight))]
    [NotifyPropertyChangedFor(nameof(PictureFadeMargin))]
    public partial double StackRoom { get; set; }

    /// <summary>
    /// 文字那一叠量出了新高 —— 封面的高上限（<see cref="FitStill"/>）跟着走。只在集页的宽版式上咬合
    /// （其余页面 FitStill 不看这个数），而 FitStill 对没变的数不喊通知，拖窗口的负载不在这里。
    /// </summary>
    partial void OnStackRoomChanged(double value) => FitStill();

    /// <summary>
    /// 带子里那一叠（剧照、片名、副标题、读数、那排键）连上下留白实测要占多高。集页那一格的高就是它，见
    /// <see cref="DetailHero.EpisodeHeight"/>；别的页面不用它。
    /// <para>
    /// 算在这儿而不在视图里：视图量得到的只有那一叠字键有多高（<see cref="StackRoom"/>）和那排键那一行有多高
    /// （<see cref="ActionsRoom"/>），而「剧照也算进去」「右上角那张艺术图也算进去」「加上这一格自己的上下留白」
    /// 三句是规矩。海报按图自己的形状收窄之后（<see cref="DetailHero.StillBox"/>）剧照的高会变，写在视图那个
    /// 尺寸回调里的那一版只在字键那一叠也跟着变的时候才会重算 —— 带子于是比内容高出那么一截，正是「左上角
    /// 空空的」那种坏法。
    /// </para>
    /// <para>
    /// 那排键那一行和艺术图都是**取大**而不是相加：它们和字键那一叠站在同一行里（艺术图在右栏、那排键在
    /// 底下那一行），谁高听谁的。从前那排键是字键那一叠的最后一个孩子，所以它那点高度在
    /// <see cref="StackRoom"/> 里；搬出来之后只能在这儿加回去。
    /// </para>
    /// <para>
    /// 艺术图只在集页的宽版式上有（<see cref="CornerVisibility"/>），所以 <see cref="CornerHeight"/> 在这里
    /// 按 <see cref="HeroCard"/> 分一次档 —— 紧凑版式里那一角收着，它的高不该算进带子。
    /// </para>
    /// </summary>
    public double HeroRoom =>
        Math.Max(Math.Max(StackRoom, StillHeight), HeroCard ? CornerHeight : 0)
        + ActionsRoom + HeroInset.Top + HeroInset.Bottom;

    /// <summary>
    /// 头上那一格的高。宽的一侧是老规矩：高由站在它里面那一叠东西定，跟窗口无关（「图一页面怎么改的一大片
    /// 空白，改回去」）—— 电影、剧、季走 <see cref="DetailHero.Height"/>（那一档 412 是手量出来的内容高，
    /// 判据 <see cref="HeroArt"/> 在导航那一刻就知道，所以第一帧的版面就是最后的版面），集页量在运行时
    /// （<see cref="HeroRoom"/> → <see cref="DetailHero.EpisodeHeight"/>：单集配的是一张 16:9 剧照，比 2:3
    /// 海报矮一大截，跟着用 412 就是在那一叠头上留上百像素只有画面的地方）。
    /// <para>
    /// 412 那一档之上唯一的一笔加项是宽版式的断点进度（<see cref="DetailHero.ProgressRoom"/>）：它跟着
    /// <see cref="PlayTarget"/> 走（有断点才画），所以这一格的高头一回有了一个数据面上的自变量 ——
    /// 数据到了带子长一格，没有断点的条目一个像素不动（「上方空位太多」是刚修完的病，不能为了进度条
    /// 请回来）。
    /// </para>
    /// <para>
    /// 紧凑版式（<see cref="IsCompact"/>）分两档，判据是有没有画面可铺 —— 都在
    /// <see cref="DetailHero.CompactHeight"/> 里，连同「没有画面那一档按内容给」的理由。海报在紧凑版式里
    /// 照画的是季、集两页（「剧页面和电影页面的封面怎么没了？」救回来，同日又两句把剧、电影两页收回去，
    /// 见 <see cref="DetailHero.ShowsStill"/>），
    /// <see cref="StillHeight"/> 随 <see cref="FitStill"/> 归零，<see cref="HeroRoom"/> 自然回落到文字那一叠。
    /// </para>
    /// </summary>
    public double HeroLayoutHeight => IsCompact
        ? DetailHero.CompactHeight(PageWidth, HeroHeightRatio, HeroRoom, HeroArt)
        : IsEpisodePage
            ? DetailHero.EpisodeHeight(HeroRoom)
            : DetailHero.Height(BackdropShown) + (WideProgressShown ? DetailHero.ProgressRoom : 0);

    /// <summary>
    /// 头部在视口中的高度。从前它等于排出来的高乘一个整体缩放（<c>HeroScale</c>，1024 到 1280 那一段最小缩到
    /// 八成）—— 桌面版式不再整体缩小之后（「收窄窗口后组件要自动换行」）没有这一手了，两者同值。
    /// </summary>
    public double HeroHeight => HeroLayoutHeight;

    /// <summary>
    /// 窗口宽变了（<c>DetailPage.OnBodySizeChanged</c> 量完喊）—— 封面和角图跟着新宽各重排一遍。每一像素都喊，
    /// 和带高、背景盒高那些绑定同一个节奏；两个 Fit 做的都是几次乘除加两次属性通知，拖窗口的负载不在这里。
    /// </summary>
    partial void OnPageWidthChanged(double value) => RefitArtwork();

    /// <summary>
    /// 封面和角图按现在的窗口宽各排一遍 —— 「集页面左上角的封面要跟随窗口的宽度放大和缩小」加角图的题栏保底，
    /// 规则都在 Core（<see cref="DetailHero.StillWidth"/>／<see cref="DetailHero.CornerBoxWidth"/>）。两个时机喊：
    /// 图到手（<see cref="ShowStill"/>／<see cref="ShowCorner"/> 先把像素尺寸记下来），和窗口宽变了
    /// （<see cref="OnPageWidthChanged"/>）。先封面后角图：角图让多少位（<see cref="CornerMaxWidth"/>）要看封面
    /// 这一次多宽。
    /// </summary>
    private void RefitArtwork()
    {
        FitStill();
        FitCorner();
    }

    /// <summary>
    /// 封面那一格按现在的宽排：不画的档（<see cref="CoverShown"/>）先把宽高归零退出去；集页的宽版式随窗口走
    /// （<see cref="DetailHero.StillWidth"/>，基宽是剧那张海报的 210 —— 「集页面的封面改用剧页面的封面」，
    /// 形状也同它，2:3），紧凑版式和其余页面照旧用基宽（紧凑那一档按参考图是贴着页宽的大封面）。图到手了就
    /// 按位图自己的形状收进盒子里（<see cref="DetailHero.StillBox"/>），还没到手就摆上限盒 —— 那一拍这一格
    /// 整个收着（<see cref="StillVisibility"/>），摆的数没人看见。
    /// <para>
    /// 集页的宽版式还有一道高上限：<b>不超过旁边那一叠文字</b>（<see cref="StackRoom"/>，里面已经含按键和
    /// 进度那几行）。封面是 2:3 的，随宽放大高得比文字快 —— 文字短的页面（片名一行、没有折行）上它会高出
    /// 一大截，而按键排在按键行等的是整行的结束，屏上就是文字和按键之间空出海报高出来的那一截（他圈的
    /// 那块空位，2026-09-12 死神 S2:E41 的截图）。盖住它，封面和文字栏同高，谁也不给谁留洞；文字多的页面
    /// （片名折行）上限跟着长，封面照旧随宽放大。
    /// </para>
    /// </summary>
    private void FitStill()
    {
        // 这一档不画封面（<see cref="CoverShown"/>，规则在 DetailHero.ShowsStill）：那一格整个收掉，宽高一并
        // 归零 —— 带高（HeroRoom 读 StillHeight）和片名那一栏的左沿（StackLeft 读 StillWidth）跟着回落到
        // 没有海报的那一档。摆一个看不见的宽高，带子就多垫一段「空海报」的高度。
        if (!CoverShown)
        {
            StillWidth = 0;
            StillHeight = 0;
            return;
        }

        var maxW = IsEpisodePage && !IsCompact
            ? DetailHero.StillWidth(PageWidth, PosterStillWidth, HeroReferenceWidth)
            : (double)PosterStillWidth;
        var maxH = maxW * PosterStillHeight / (double)PosterStillWidth;

        if (IsEpisodePage && !IsCompact)
            maxH = Math.Min(maxH, Math.Max(StackRoom, PosterStillHeight));

        if (_stillPixels is { } pixels
            && DetailHero.StillBox(pixels.Width, pixels.Height, maxW, maxH) is { } fit)
        {
            (StillWidth, StillHeight) = fit;
        }
        else
        {
            StillWidth = Math.Round(maxW);
            StillHeight = Math.Round(maxH);
        }
    }

    /// <summary>
    /// 角图那一格按现在的宽排 —— 让多少位由 <see cref="CornerMaxWidth"/> 给（封面的宽进去，题栏的保底出来），
    /// 高按位图自己的形状收进盒子里、喂带高（<see cref="CornerHeight"/> → <see cref="HeroRoom"/>）。
    /// 没有图的时候高回 0，那一栏收着 —— 同换条目时的那两句清空。
    /// </summary>
    private void FitCorner()
    {
        OnPropertyChanged(nameof(CornerMaxWidth));

        var maxW = CornerMaxWidth;

        if (_cornerPixels is { } pixels
            && DetailHero.StillBox(
                pixels.Width, pixels.Height,
                maxW, maxW * DetailHero.CornerHeight / DetailHero.CornerWidth) is { } fit)
        {
            CornerHeight = fit.Height;
        }
        else if (_cornerPixels is null)
        {
            CornerHeight = 0;
        }
    }

    /// <summary>
    /// 头图里那几样东西（封面、片名那一栏）靠上还是靠下站。判据就是 <see cref="HeroCard"/>：只有集页的宽版式靠上。
    /// <para>
    /// 集页靠上：那一格的高本来就是它里面那一叠量出来的（<see cref="HeroRoom"/>），靠上就是「内容从这一格的
    /// 上沿起」—— 「把集页面的封面向上移」（2026-09-12）说的就是它：从前靠下站，于是封面头顶上凭空留着百来
    /// 像素，而那正是他圈出来的那一块。右上角那张艺术图也能把这一格撑高（<see cref="CornerHeight"/> 比那一叠
    /// 还高的时候），靠上站的话多出来的一截落在底下，封面和片名照旧贴在上沿。
    /// </para>
    /// <para>
    /// 别的页面靠下，一个像素没动：电影、剧、季那三页的带高写死 <see cref="DetailHero.ArtHeight"/>，里面本来就
    /// 留着一截富余（海报 300 之外那点），靠下站就是海报和那一栏字的下沿对齐 —— 他夸过的就是这一页的样子
    /// （「电影那边处理的就很好」）。紧凑版式同理：那一档有画面可铺的时候带子就是那条等比的画面
    /// （<see cref="DetailHero.CompactHeight"/>，比内容高出来的部分是画面），片名压在画面的下沿上，参考图上
    /// 正是这个样子；没有画面可铺时带子等于内容的高，两个对齐摆出来一模一样。
    /// </para>
    /// </summary>
    public VerticalAlignment HeroContentAlignment => HeroCard ? VerticalAlignment.Top : VerticalAlignment.Bottom;

    /// <summary>
    /// 右上角那张艺术图这一次的上限盒宽，绑在标记里那个 max 上 —— 参考宽以上是 480 封顶
    /// （<see cref="DetailHero.CornerWidth"/>，这个数只在那里写一遍），以下让位给题栏
    /// （<see cref="DetailHero.CornerBoxWidth"/>：封面的宽进去，题栏的保底出来）。取图那一头
    /// （<see cref="ShowCorner"/> → <see cref="FitCorner"/>）读的也是它，两处不会分叉。
    /// 谁的手换了它的输入，谁负责喊它：<see cref="FitCorner"/>（页面宽和封面的宽都在它那儿汇齐）。
    /// </summary>
    public double CornerMaxWidth => DetailHero.CornerBoxWidth(PageWidth, StillWidth);

    /// <inheritdoc cref="CornerMaxWidth"/>
    public double CornerMaxHeight => DetailHero.CornerHeight;

    /// <summary>
    /// 背景那一张这次画多高 —— <see cref="DetailHero.PictureHeight"/>：宽窗铺满视口，窄窗整张等比缩，带子是
    /// 它的下限。画在 <c>DetailPage</c> 标记里那个跟宽走的盒子（<c>BackdropPicture</c>）上，层本身照旧铺满
    /// 整个视口 —— 盒子底下多出来的那段交给压暗的尾部。
    /// </summary>
    public double PictureHeight => DetailHero.PictureHeight(PageWidth, HeroHeightRatio, Viewport, HeroHeight);

    /// <summary>
    /// 背景盒子下沿那道黑色渐变摆多高 —— 盒子的下沿减去渐变自己的高（<see cref="PictureFadeHeight"/>），渐变的
    /// 底边正好压在画面的下沿上。它必须画在背景盒子<em>外面</em>：盒子带着 0.9 的不透明度，垫在里面的话
    /// 「满墨」其实只有九成，亮图上那半成透出来就是「还是能看到分界线」（2026-09-12 他指着的那条）—— 单独
    /// 一层才是真的满墨。显隐在标记里跟着 <see cref="HeroVisibility"/> 走（缘由记在 DetailPage 那一层的注释
    /// 里）：铺满那一档的下沿也在屏上，两档都靠这道渐变接缝。
    /// </summary>
    public Thickness PictureFadeMargin => new(0, Math.Max(0, PictureHeight - PictureFadeHeight), 0, 0);

    /// <summary>下沿渐变自己的高。矮了压不住亮图的边，高了把画面吃掉一大截 —— 120 在两者之间。</summary>
    private const double PictureFadeHeight = 120;

    /// <summary>
    /// 这一档版面要不要封面 —— 规则在 Core（<see cref="DetailHero.ShowsStill"/>）：紧凑版式下剧、电影两页
    /// 不画（2026-09-12 一天四句的最后一落，来龙去脉记在那边）。图解出来没有不算在这里 —— 那是
    /// <see cref="StillVisibility"/> 自己的另一半。
    /// </summary>
    private bool CoverShown => DetailHero.ShowsStill(IsCompact, _detail?.Type);

    /// <summary>
    /// 带子左边那张海报（集页上是剧照）显不显 —— 两句话合起来：「图解出来了没有」，和「这一档版面要不要它」
    /// （<see cref="CoverShown"/>）。
    /// <para>
    /// 第二句的规矩 2026-09-12 当天翻了几回：早上紧凑版式不画海报，他一句「剧页面和电影页面的封面怎么没了？」
    /// 整个救了回来（「单列归单列，封面是封面」）；看完实拍点走剧集两页，傍晚又把集页还回来，最后「电影页面
    /// 窄窗口也要隐藏左上角的封面」把电影页也收了回去。最终紧凑档不画的是剧、电影两页，季、集照旧。规则本体
    /// 在 <see cref="DetailHero.ShowsStill"/>，这里只照着问。
    /// </para>
    /// <para>
    /// 不画的那一档 <see cref="FitStill"/> 把那一格的宽高一并归零：带高（<see cref="HeroRoom"/> 读
    /// <see cref="StillHeight"/>）和片名那一栏的左沿（<see cref="StackLeft"/> 读 <see cref="StillWidth"/>）
    /// 跟着回落到没有海报的那一档。
    /// </para>
    /// </summary>
    public Visibility StillVisibility => Show(StillImage is not null && CoverShown);

    /// <summary>
    /// 带子里那一叠字和键四周的留白。集页把底下那道收到 16 —— 「为什么中间要留空，导致下方的剧情说明
    /// 看不到？」：底下紧跟着音轨那一行和那一带集，多留就是纯粹的空气。
    /// <para>
    /// 其余页面那道底下的留白是同一笔旧账：它本来是给「这一格铺到窗口下沿」写的（贴着窗口边读着像被截了
    /// 一截）。背景改成等比画面之后带子底下紧跟着尾部那几块，「组件不够紧凑」（2026-09-12）说的就是这一截 ——
    /// 收到 24，和页边那 60、顶上那 28 一个量级。
    /// </para>
    /// <para>
    /// 紧凑版式整档换成窄边：左右 20 是参考图上那种贴边一列的留法，60 的「压在剧照上的内容多让一点」是
    /// 宽页面的讲究 —— 单列的窄页面上它就是把字挤到中间去的空气。
    /// </para>
    /// <para>
    /// 集页那块板上（<see cref="HeroCard"/>）左右收到 48：板的外沿在 28，28 + 20 就是板上内容的左边 ——
    /// 和底下 <see cref="TailInset"/> 那 48 同一竖线，海报、片名、播放键和音轨那一行因此全落在板的同一条
    /// 边上。60 那一档的讲究是「压在剧照上的内容多让一点」，而板上没有剧照要躲。
    /// </para>
    /// </summary>
    public Thickness HeroInset => IsCompact
        ? new(20, 20, 20, 16)
        : new(HeroCard ? 48 : 60, 28, HeroCard ? 48 : 60, IsEpisodePage ? 16 : 24);

    /// <summary>
    /// 带子底下那一段的内边距。同 <see cref="HeroInset"/>：集页把上面那道收到 12，那一叠键和「音频」
    /// 之间因此只隔一小截，和那一段里几块之间的间距同一个量级；其余页面 14。底下 4 —— 和正文纸面之间
    /// 本来就没有可看的缝，纸面自己那 20 会接着垫。
    /// <para>
    /// 左右 20 是「组件不够紧凑」之后从 28 收下来的：24 的板内边距加在这里，字落在 44 的竖线上，和底下
    /// 纸面（20 + 24）正好同一条。
    /// </para>
    /// <para>
    /// 紧凑版式左右只给 2：这一段里每块板（<c>EgBodyPanelStyle</c>）自带 24 的内边距，2 + 24 正好是头图上
    /// 那一叠的 20（见 <see cref="HeroInset"/>）—— 片名、文件选项和播放键落在同一条竖线上，单列版式靠的
    /// 就是这条线站直。
    /// </para>
    /// <para>
    /// 集页那块板上（<see cref="HeroCard"/>）左右同样收到 48（28 的板沿加 20 的内边距，两处同一竖线），
    /// 底下给到 20 —— 那 20 就是板和它里面最后那一块之间的缝。板底到正文纸面之间不再另留：纸面和页底色
    /// 是同一支画刷，留一段也看不出来。
    /// </para>
    /// <para>
    /// 集页的宽版式再往右挪一栏（<see cref="StackLeft"/>）：那一页这一段里只剩剧情说明（音频字幕搬进片名
    /// 那一栏了，见 <see cref="ColumnPickersVisibility"/>），它得和片名、那一排键站在同一条左沿上 ——
    /// 截图里那几行就是这么对齐的（「按键布局参考上方截图」，2026-09-12）。
    /// </para>
    /// </summary>
    public Thickness TailInset => IsCompact
        ? new(2, 12, 2, 8)
        : HeroCard
            ? new(StackLeft, 12, StackLeft, 20)
            : new(20, IsEpisodePage ? 12 : 14, 20, 4);

    /// <summary>
    /// 片名那一栏里那两栏之间的一格间距（<c>HeroContent.ColumnSpacing</c>，标记里绑的是这一个数）。
    /// <see cref="StackLeft"/> 要它，所以它不能只写在标记里。
    /// </summary>
    public double ColumnSpacing => 22;

    /// <summary>
    /// 片名那一栏（<c>HeroStack</c>）的左沿离本页左边有多远 —— <see cref="HeroInset"/> 那一道，加上海报那一栏
    /// 自己的宽（<see cref="StillWidth"/>）和两栏之间那一格（<see cref="ColumnSpacing"/>）。
    /// <para>
    /// 海报没画出来的时候不加：那一栏是 Auto、收起来就是 0 宽（<see cref="StillVisibility"/>），片名那一栏
    /// 直接站到板的内沿上 —— 这里照着同一句话算，屏上那一条沿才不会差一格。
    /// </para>
    /// <para>
    /// 尾部那一段（<see cref="TailInset"/>）和那排键（<see cref="ActionsColumn"/>）都按它对齐：截图里片名、
    /// 音频字幕、那排键和剧情说明是同一条左沿。
    /// </para>
    /// </summary>
    private double StackLeft => HeroInset.Left
        + (StillVisibility == Visibility.Visible ? StillWidth + ColumnSpacing : 0);

    /// <summary>
    /// 那排键站在头图那一格的第几栏 —— 集页的宽版式站在片名那一栏里（截图那种排法：按键左沿跟片名对齐），
    /// 其余页面横着铺满三栏、贴着内容那一条左沿（那是他自己定的，见那排键上的注释）。
    /// </summary>
    public int ActionsColumn => HeroCard ? 1 : 0;

    /// <inheritdoc cref="ActionsColumn"/>
    public int ActionsColumnSpan => HeroCard ? 1 : 3;

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
    /// 纸面上沿的线，视口坐标 —— 阈值窗口的高（<see cref="DetailHero.PaperLineFor"/>，跟着显示器走：4K 屏
    /// 1080、2K 屏 900、1080p 屏 768）减掉标题栏加面包屑那截，由页面量好送进来（<c>DetailPage.SyncPaperLine</c>）。
    /// 0 是「还没送到」：那一档不撑尾部（<see cref="DetailHero.TailHeight"/> 的约定），纸面回到由内容定的位置。
    /// <para>
    /// 换显示器、换主题（面包屑那截的高跟着字号走）、窗口换尺寸都会让这个数变，所以由视图在那几处显式重写，
    /// 而不是算出来的属性。
    /// </para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TailMinHeight))]
    public partial double PaperLine { get; set; }

    /// <summary>
    /// 头图底下那段压暗的尾部的下限 —— 见 <see cref="DetailHero.TailHeight"/>：尾部先补满第一屏减掉带子那么多，
    /// 于是正文那张纸从第一屏的下沿起，「拉大或拉小窗口」都不会把那道不透明的边提到剧照上。撑到纸面那条线
    /// （<see cref="PaperLine"/>，窗口高过阈值）就不再撑，富余的高度归纸 —— 不然剧情说明底下那段空画面会跟着
    /// 窗口一起长（「下面越改空位越大」）。过了那条线之后纸就跟着窗口一像素一像素地露出来，中间没有台阶
    /// （「拉大窗口之后下面突然冒出一大截」）。
    /// <para>
    /// 还有第三道上限：背景那一张的下沿（<see cref="PictureHeight"/>）。尾部是「压暗的画面」，等比画面条底下
    /// 已经没有画面，撑过去就是一段空位 —— 纸面和货架被压到老下面去，中间空着一大块（他 2026-09-12 指着的
    /// 那张截图）。撑到画面下沿为止，底下的空当交给正文的内容自动补上。
    /// </para>
    /// </summary>
    public double TailMinHeight => DetailHero.TailHeight(Viewport, HeroHeight, BackdropShown, PaperLine, PictureHeight);

    /// <summary>
    /// 正文那张纸自己的下限 —— 见 <see cref="DetailHero.PaperHeight"/>：滚到底的那一屏只能有纸。
    /// <para>
    /// 只跟着 <see cref="Viewport"/> 和 <see cref="HeroArt"/> 走，所以不进 <see cref="AnnounceShape"/>：那两个
    /// 都是 observable 的，值真变了就会自己喊一声。<see cref="TailMinHeight"/> 不同 —— 它还看带高，而带高跟着
    /// 页面的种类走。
    /// </para>
    /// </summary>
    public double PaperMinHeight => DetailHero.PaperHeight(Viewport, BackdropShown);

    /// <summary>
    /// The file 播放 would start: the item itself for a film, the next unwatched episode for a show.
    /// Null for a series or season whose episodes have not arrived, which is what collapses the button.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayText))]
    [NotifyPropertyChangedFor(nameof(PlayVisibility))]
    [NotifyPropertyChangedFor(nameof(RestartVisibility))]
    [NotifyPropertyChangedFor(nameof(ProgressValue))]
    [NotifyPropertyChangedFor(nameof(ResumeRemain))]
    [NotifyPropertyChangedFor(nameof(ResumeVisibility))]
    [NotifyPropertyChangedFor(nameof(InfoVisibility))]
    [NotifyPropertyChangedFor(nameof(PickersVisibility))]
    [NotifyPropertyChangedFor(nameof(ColumnPickersVisibility))]
    [NotifyPropertyChangedFor(nameof(WideProgressVisibility))]
    // 有断点没断点翻了宽版式进度那一行（WideProgressShown），其余三页的带高跟着给它留房 ——
    // 带高一变，站在带高上的这一串都得重喊一遍（同 PageWidth 的名单）。
    [NotifyPropertyChangedFor(nameof(HeroLayoutHeight))]
    [NotifyPropertyChangedFor(nameof(HeroHeight))]
    [NotifyPropertyChangedFor(nameof(PictureHeight))]
    [NotifyPropertyChangedFor(nameof(PictureFadeMargin))]
    [NotifyPropertyChangedFor(nameof(ScrimHeight))]
    [NotifyPropertyChangedFor(nameof(BodyMinHeight))]
    [NotifyPropertyChangedFor(nameof(TailMinHeight))]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestartCommand))]
    public partial EmbyItem? PlayTarget { get; set; }

    /// <summary>紧凑版式进度条的位置 —— 断点占全片的比例乘满量，没有断点就是 0（那一行整个不画）。</summary>
    public double ProgressValue => (PlayTarget?.ProgressFraction ?? 0) * 100;

    /// <summary>进度条旁边「剩余 x 分钟」那一行 —— <see cref="ItemDetail.Remaining"/>，null 就不画。</summary>
    public string? ResumeRemain => ItemDetail.Remaining(PlayTarget);

    /// <summary>断点进度那一行画不画：有断点才有进度可言。</summary>
    public Visibility ResumeVisibility => Show(PlayTarget?.HasResumePosition == true);

    /// <summary>
    /// 宽版式那排键底下那一条断点进度（截图里的「剩余 5 分钟」）画不画 —— 两份宽版式绑的是同一个判据：
    /// 集页片名那一栏里的那份（<c>WideProgress</c>），和其余三页（电影、剧、季）带子底下那一行里新添的
    /// 那份（「宽窗口缺少播放进度条」，2026-09-12）。两份的容器各按各的页面收着，屏上永远只有一份在。
    /// <para>
    /// 从前只有集页画它：其余三页的带高写死 412，这一行没有地方给。现在带高那头按
    /// <see cref="DetailHero.ProgressRoom"/> 给有断点的条目留房（见 <see cref="HeroLayoutHeight"/>）——
    /// 没有断点这一行收着，带高一个像素不动。紧凑版式里这一对（条＋剩余时间）在画面底下那一块里，
    /// 绑的是不带版式判据的 <see cref="ResumeVisibility"/>。
    /// </para>
    /// </summary>
    public Visibility WideProgressVisibility => Show(WideProgressShown);

    /// <summary><see cref="WideProgressVisibility"/> 的 bool 面 —— 带高那头也读它，版面不吃 Visibility。</summary>
    private bool WideProgressShown => !IsCompact && PlayTarget?.HasResumePosition == true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WatchedVisibility))]
    [NotifyPropertyChangedFor(nameof(NotWatchedVisibility))]
    public partial bool Watched { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FavoriteVisibility))]
    [NotifyPropertyChangedFor(nameof(NotFavoriteVisibility))]
    public partial bool Favorite { get; set; }

    /// <summary>
    /// 连播 —— 「集页面加一个连播按钮」（2026-09-12）。开的是设置里那一档「自动播放下一集」
    /// （<see cref="PlaybackSettings.AutoPlayNextEpisode"/>：一集放完自动接下一集，跨季也接着放），
    /// 不是这一页自己的状态 —— 播放器 ⚙ 菜单和设置页读写的都是同一份，谁改了屏上到处都认；写回即存，
    /// 和播放器里那一颗的规矩一致（见 <c>PlayerViewModel.AutoPlayNextEpisode</c>）。
    /// <para>
    /// 没拿到设置（<see cref="Attached"/> 之前）读 false、写不进：页面还没接上服务，那一拍屏上那颗开关
    /// 是个占位。
    /// </para>
    /// </summary>
    public bool ContinuePlay
    {
        get => Attached && Settings.Playback.AutoPlayNextEpisode;
        set
        {
            if (!Attached || Settings.Playback.AutoPlayNextEpisode == value) return;

            Settings.Playback.AutoPlayNextEpisode = value;
            _settings!.Save();
            OnPropertyChanged(nameof(ContinuePlay));
        }
    }

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
    /// 这一页要不要按「背后铺着一张图」那一档布（<see cref="HeroArt"/> 的判据）。集页不铺背景图
    /// （2026-09-12「去掉集页面的背景图」），无论服务器有什么都是 false；其余页面照 <see cref="ItemArtwork.Hero"/>
    /// 问标签表。三处赋值（<see cref="Attach"/>、<see cref="Preview"/>、<see cref="Apply"/>）共用这一句 ——
    /// 答案分叉的后果是同一页里带高按两档各算各的。
    /// </summary>
    private static bool SpreadsBackdrop(EmbyItem item) =>
        item.Type != EmbyItemType.Episode && ItemArtwork.Hero(item).Count > 0;

    /// <summary>
    /// Whether 单集 belongs down the page as rows rather than across it as cards. The rule itself is
    /// <see cref="ItemDetail.EpisodesAsList"/>; what is here is only 「asked of this page's own item」.
    /// </summary>
    private bool EpisodesAsList => ItemDetail.EpisodesAsList(_detail?.Type);

    private static double FontSize(string key) => (double)Application.Current.Resources[key];

    private static Brush Painted(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>自检用：一个可空的评分字段读出来怎么写。缺失和 0 要分得清 —— 两者屏上都是「没有分」。</summary>
    private static string Describe(float? value) =>
        value is { } number ? number.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "没给";

    public Visibility ScoreVisibility => Show(!string.IsNullOrWhiteSpace(Score));

    /// <summary>
    /// 「视频：1080p · H264 · MP4 · 211 MB」这一行该不该有。两处用它：集页的宽版式里它是一枚 chips
    /// （和音频、字幕那几格一起换行，「按键布局参考上方截图」），其余版式里它是片名那一栏的独立一行
    /// （<see cref="VideoLineVisibility"/>）。
    /// <para>
    /// 有话说是第一句，第二句是**底下那个媒体源下拉没在屏上**
    /// —— 那个下拉每一行都以同一份读数结尾（<see cref="ItemDetail.SourceLabel"/> 和
    /// <see cref="ItemDetail.VideoLine"/> 问的是同一个 <c>ToQualityLabel()</c>），两样同时在屏上就是同一句话在
    /// 相隔两百像素的地方说了两遍（2026-09-05 界面复查）。
    /// </para>
    /// <para>
    /// 下拉只在有两个以上媒体源时才出现（<see cref="SourceVisibility"/>），而那才是这一行唯一多余的时候：一个文件
    /// 的条目上没有下拉，这一行是那份读数唯一的出处，收掉它就等于把分辨率和大小从页面上抹了。
    /// </para>
    /// </summary>
    private bool VideoShown => !string.IsNullOrWhiteSpace(VideoLine) && Sources.Count <= 1;

    public Visibility VideoVisibility => Show(VideoShown);

    /// <summary>
    /// 片名那一栏里那行「视频：…」显不显 —— 集页的宽版式不画（那一页它下到文件选项那排 chips 里了，见
    /// <see cref="VideoVisibility"/>），其余版式照旧。
    /// </summary>
    public Visibility VideoLineVisibility => Show(VideoShown && !HeroCard);

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
    /// 判据是 <see cref="BackdropShown"/>（服务器上有这张图），不是 <see cref="HeroVisibility"/>「位图解出来
    /// 了没有」：层先站好，压住标题栏那行字的罩子就先在了，图到了只是这一层里多一张画面。反过来就是「点击
    /// 主页封面后窗口会闪一下」—— 先按没有图布一遍、图到了再布第二遍。
    /// </para>
    /// </summary>
    public Visibility HeroArtVisibility => Show(BackdropShown);

    /// <summary>
    /// 上面那一层的反面：背景不画的时候（这一条目没有图），头图那一格自己当底的那一层（一层底色加顶上一道
    /// 罩子）。有图的时候这两样都在那张固定的图上，这一层就得让开，否则一块不透明的底色会把图盖掉。
    /// <para>
    /// 2026-09-12 起集页的宽版式不在这一档里：那一页那一段合成了一块面板（见 <see cref="HeroCard"/>），底色、
    /// 圆角和外圈由面板自己画。两层都画的话面板的圆角先被糊掉，左右那两条 28 的留白也会被填成底色。
    /// </para>
    /// </summary>
    public Visibility HeroPlainVisibility => Show(!BackdropShown && !HeroCard);

    /// <summary>
    /// 集页整段上方那一块面板显不显 —— 「把集页面上方框的组件框起来」（2026-09-12）。
    /// <para>
    /// 板是一整块、分两截画（见 <c>DetailPage.xaml</c> 的 <c>HeroCardTop</c> 和 <c>HeroCardBottom</c>）：上截贴在
    /// 头图那一格里（海报、片名、六颗键、右上角那张艺术图都站在这块板上），下截是尾部那一格（音轨字幕和剧情
    /// 说明）。两截的左右留白和下沿完全对上，四个角只有外侧那两个圆 —— 屏上是一条通栏的板。
    /// </para>
    /// <para>
    /// 只有宽版式有它：紧凑版式是单列，整页就没有「上方那一段」可框，而那一档每一块本来就自带面板 —— 多套
    /// 一层只是又一条边框。<see cref="HeroInset"/> 和 <see cref="TailInset"/> 都跟着它走（板边上要留出 20），
    /// 所以这三个属性是一组：宽窄一变、页面种类一变，三个一起重算。
    /// </para>
    /// <para>
    /// 判据不是「有没有剧照」：有剧照的那几页整段压在照片上，一块不透明的板会把照片盖掉；集页是唯一
    /// 「没有剧照、又整段挤在第一屏里」的那一页。
    /// </para>
    /// <para>
    /// 同日他还要去掉过一次（「去掉集页面最上面的框」），去完又要回来（「框加回去」）—— 同一个下午来回两趟，
    /// 落地的是有板的这一版。要撤的话，去的不是这一句，是 <c>DetailPage.xaml</c> 里那两块 Border、这两句
    /// 显隐和 <c>DetailPage.PaintScrim</c> 的分档，四样一起。
    /// </para>
    /// </summary>
    public Visibility HeroCardVisibility => Show(HeroCard);

    /// <summary>
    /// <see cref="HeroCardVisibility"/> 的布尔那一份 —— 视图在代码里要问它两句：尾部那一块的底色（有板时透明、
    /// 由板自己画，没板时接住头图末色），以及那四块内层小面板各自的框。判据只写在这儿一处。
    /// </summary>
    public bool HeroCardShown => HeroCard;

    /// <summary>
    /// 带子下沿那道渐深罩子画不画 —— 它压的是一张剧照，好让压在图上的白字读得出来。
    /// <para>
    /// 集页那块板上不画（<see cref="HeroCard"/>）：那一页不铺背景图（<see cref="SpreadsBackdrop"/>），没有照片
    /// 可压；留着它只会把板的下半截压暗，板底和自己那几块小面板的底边对不上。板上的底由板自己给。
    /// </para>
    /// </summary>
    public Visibility ScrimVisibility => Show(!HeroCard);

    /// <summary>紧凑版式里才画的那一块（全宽播放键、断点进度、带字的一排操作）显不显 —— <see cref="IsCompact"/>。</summary>
    public Visibility CompactVisibility => Show(IsCompact);

    /// <summary>
    /// 带子里独立一行的那排键显不显 —— 其余三个页面（电影、剧、季）的宽版式用它。集页的宽版式不画：那排键
    /// 搬进了片名那一栏的叠里（<c>DetailPage</c> 标记里的 <c>ColumnActions</c>，2026-09-12）—— 封面换成剧
    /// 海报之后比文字高出一大截，独立一行要等封面行结束，文字和按键之间就空出海报高出来的那一截（他圈的
    /// 那块空位）。紧凑版式里它的活交给画面底下那一块（<see cref="CompactVisibility"/>）。
    /// </summary>
    public Visibility WideActionsVisibility => Show(!IsCompact && !HeroCard);

    /// <summary>
    /// 片名那一叠里那排键（集页的宽版式的那一份，<c>ColumnActions</c>）显不显 ——
    /// <see cref="WideActionsVisibility"/> 的另一半，判据同 <see cref="HeroCardVisibility"/>。
    /// </summary>
    public Visibility ColumnActionsVisibility => Show(HeroCard);

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
    /// 右上角那张艺术图画不画。三句话：图解出来了没有；这一页是不是集页 —— 用户点的名是「集页面」，剧、电影
    /// 页面的那一角是他同一天下令去掉的（「去掉剧页面、电影页面右上角的艺术图」），季页从来不在里头；以及版式
    /// 还宽得摆得下 —— 紧凑版式是单列，一张 480 宽的画没有地方站，让位。
    /// <para>
    /// 「是不是集页」跟着页面的种类走，所以要在 <see cref="AnnounceShape"/> 里喊一声；「宽窄」跟着
    /// <see cref="PageWidth"/> 走，那一头的通知挂在它的 setter 上。
    /// </para>
    /// </summary>
    public Visibility CornerVisibility => Show(CornerImage is not null && IsEpisodePage && !IsCompact);

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
    /// <para>
    /// 2026-09-12 起集页的宽版式不在这一档里：那一页把这三行搬到片名那一栏底下（「按键布局参考上方截图」，
    /// 见 <see cref="ColumnPickersVisibility"/>），尾部那一份在这一页上收着 —— 两份只有一份在屏上，尾部那份
    /// 归这里的判据管，栏里那份归 <see cref="ColumnPickersVisibility"/>。
    /// </para>
    /// </summary>
    public Visibility PickersVisibility => Show(!HeroCard && Pickable);

    /// <summary>
    /// 片名那一栏里那一行文件选项（集页的宽版式）显不显 —— <see cref="PickersVisibility"/> 的另一半。
    /// <para>
    /// 「按键布局参考上方截图」（2026-09-12）：那一页的音频、字幕和那排键一路排在片名底下，所以那一份标记在
    /// <c>HeroStack</c> 里、标签在下拉的左边（尾部那一份的标签在下拉头上，是另一种排法）。判据和尾部那一份
    /// 同一句，只是各自要「是不是集页的宽版式」这一半。
    /// </para>
    /// </summary>
    public Visibility ColumnPickersVisibility => Show(HeroCard && Pickable);

    /// <summary>
    /// 那一行文件选项真有的可选且播放键指着一个文件 —— 两份共用的那一半判据（见
    /// <see cref="PickersVisibility"/> 上那几段）。
    /// </summary>
    private bool Pickable =>
        PlayTarget is not null
        && (Sources.Count > 1 || AudioTracks.Count > AutoRows || SubtitleTracks.Count > AutoRows + 1);

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
        HeroArt = SpreadsBackdrop(request.Item);

        var ui = settings.Settings.Ui;

        var episodes = new CardShelf("更多单集", images, CardSize.WideWidth,
            wide: true, ui.ShowWatchedIndicators);
        var cast = new CardShelf("演职人员", images, CardSize.CastWidth,
            wide: false, indicators: false);
        var seasons = new CardShelf("全部剧季", images, CardSize.PosterWidth,
            wide: false, ui.ShowWatchedIndicators);
        var similar = new CardShelf("更多类似", images, CardSize.PosterWidth,
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
        Overview = ItemDetail.Prose(seed.Overview) is { Length: > 0 } prose ? prose : "暂无简介。";

        Watched = seed.IsWatched;
        Favorite = seed.UserData?.IsFavorite == true;

        // 这一格先按上限盒摆（集页的宽版式随当前窗口宽走，见 FitStill）；图到手再按它自己的形状收窄。
        _stillPixels = null;
        _cornerPixels = null;
        RefitArtwork();

        HeroArt = SpreadsBackdrop(seed);

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
    /// 剧名上方那枚徽标<em>不在</em>这一批里，从「统一改为在剧名上方显示徽标」那次起就不在了：它的显隐只问一句
    /// 「图解出来了没有」（见 <see cref="PlateVisibility"/>），而那一句跟着 <see cref="PlateImage"/> 自己的
    /// setter 走。从前名牌摆哪个角是按页面的种类分的，那时候它确实要在这儿喊一声。（右上角那张艺术图也是这一句
    /// 的老主顾 —— 2026-09-12 整个退场，连着那一问一起走了。）
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
        OnPropertyChanged(nameof(HeroLayoutHeight));
        OnPropertyChanged(nameof(HeroHeight));
        OnPropertyChanged(nameof(ScrimHeight));
        OnPropertyChanged(nameof(BodyMinHeight));
        OnPropertyChanged(nameof(TailMinHeight));
        OnPropertyChanged(nameof(PictureHeight));
        OnPropertyChanged(nameof(PictureFadeMargin));
        OnPropertyChanged(nameof(HeroInset));
        OnPropertyChanged(nameof(TailInset));
        OnPropertyChanged(nameof(HeroRoom));

        // 集页那块面板、以及它替掉的两层（头图的底色和带子下沿的罩子），三句判据里都有一句是「这一页是不是
        // 集页」，所以和上面那五个同宗：从一集翻到一部电影时 HeroArt 和视口可能一个字没改。
        OnPropertyChanged(nameof(HeroCardVisibility));
        OnPropertyChanged(nameof(HeroCardShown));
        OnPropertyChanged(nameof(HeroPlainVisibility));
        OnPropertyChanged(nameof(ScrimVisibility));

        // 片名那一叠靠上还是靠下（HeroContentAlignment）也是这句「是不是集页」的一句话，同一宗。
        OnPropertyChanged(nameof(HeroContentAlignment));

        // 同一句「是不是集页的宽版式」还管着那排键站在第几栏、那一份画不画（带子里那份只在其余三个页面上
        // 站班，集页的那份在片名那一栏的叠里）、那一条断点进度画不画，「视频：…」那一行让不让位给片名那一栏
        // 的 chips（VideoLineVisibility），以及文件选项那一行是搬在片名那一栏里还是在尾部（两份只有一份在屏上）。
        OnPropertyChanged(nameof(ActionsColumn));
        OnPropertyChanged(nameof(ActionsColumnSpan));
        OnPropertyChanged(nameof(WideActionsVisibility));
        OnPropertyChanged(nameof(ColumnActionsVisibility));
        OnPropertyChanged(nameof(WideProgressVisibility));
        OnPropertyChanged(nameof(ColumnPickersVisibility));
        OnPropertyChanged(nameof(VideoLineVisibility));

        // 那一行文件选项跟着播放键的落点开合（见 PickersVisibility）：落点是空的（人物页、集还没回来）就收起来。
        // 轨道那几个集合是上一个条目留下的，从一部剧翻到一集时它们可能一个都没变，那边的通知一次不会来。
        OnPropertyChanged(nameof(PickersVisibility));

        // 页尾那张横幅只摆在三种页面上（电影、剧、集，见 FooterVisibility），也就是跟着页面的种类走。图本身可能
        // 还是上一个条目那张，那边的通知一次不会来。
        OnPropertyChanged(nameof(FooterVisibility));

        // 右上角那张艺术图只摆在集页上（见 CornerVisibility），同一句话对它也成立。
        OnPropertyChanged(nameof(CornerVisibility));
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
        // 「显示的评分」按设置里那一档来。规则在 Core（ItemScore）：哪个数、写谁的名字、这一档没有就回落到哪儿，
        // 都是有唯一正确答案的判断，而错起来屏上只是一个看着挺正常的数。
        var badge = ItemScore.Resolve(item, Settings.Ui.ScoreSource);
        Score = badge.Text;
        ScoreLabel = badge.Label;
        ScoreBadge = badge;
        ScoreFacts = $"CommunityRating={Describe(item.CommunityRating)}"
            + $"，CriticRating={Describe(item.CriticRating)}"
            + $"，ProviderIds={(item.ProviderIds.Count == 0 ? "空" : string.Join('/', item.ProviderIds.Keys))}";
        Facts = ItemDetail.Facts(item);
        Directors = ItemDetail.Directors(item);
        Overview = ItemDetail.Prose(item.Overview) is { Length: > 0 } prose ? prose : "暂无简介。";

        Watched = item.IsWatched;
        Favorite = item.UserData?.IsFavorite == true;

        // 图留着的那一档连这些一起留着：海报到手时 FitStill 已经把它们按图和当前窗口宽收窄过，写回上限就是把那
        // 一格重新撑开一次，屏上是海报周围凭空多出一条边再收回去。（集页宽版式那一档的上限随窗口走
        // （DetailHero.StillWidth），所以这里不写死两个数，交给 FitStill 按当前宽算。）
        if (!keepArtwork)
        {
            _stillPixels = null;
            _cornerPixels = null;
            RefitArtwork();
        }

        // 再问一遍，因为完整条目才是权威的那一份 —— 列表上的条目偶尔比它少几个标签。多数时候两次的答案一样，
        // 于是这一句什么也不改：生成的 setter 只在值真变了的时候才通知。
        HeroArt = SpreadsBackdrop(item);

        // 图一样就整批留着，连正在飞的那一趟一起（Fresh 比的是「要画的还是这批图」而不是「还是同一个对象」，
        // 所以卡片那一份起的解码回来照样贴得上）。不一样才清空重取 —— 换季、翻页、或者服务器上换过图。
        if (!keepArtwork)
        {
            HeroImage = null;
            StillImage = null;
            PlateImage = null;
            CornerImage = null;
            CornerHeight = 0;
            FooterImage = null;
            FooterPick = null;

            // 裁切模糊的原料和档位跟着这一批图一起作废：在路上的重糊回来时生代号对不上，自己会扔。
            _heroPixels = null;
            _heroBlurRadius = -1;
            _heroBlurGeneration++;

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

        // 左上角那一格封面（「集页面的封面改用剧页面的封面」，2026-09-12）：集页取剧集那一层的 Primary ——
        // 就是剧页面上那张 2:3 海报，借的也是同一条路（show 在 WhenAll 外头就已经在路上了）；借不到（没有
        // SeriesId、服务器没应答、或者剧自己连一张封面都没有）才退回本集自己的那几张 —— 有一张剧照总比
        // 空着一格强。其余页面照旧取自己的 Primary / Thumb。
        var episode = item.Type == EmbyItemType.Episode;

        // 集页不铺背景图（见 <see cref="HeroArt"/>），所以头图那一串整页不画 —— 那就一张都不取，省下的不止一次
        // 下载：从前那一档还要把图解到像素、过一遍模糊（<c>HeroPictureLoader</c>），全是没人看的功夫。
        // 集页多问的倒是另一趟：剧集那一层的条目。右上角那张艺术图（<see cref="ItemArtwork.Corner"/>）、页尾那张
        // 横幅（<see cref="ItemArtwork.Footer"/>）和左上角这张封面都可能要借它的 —— 服务器不下发 ParentArt /
        // ParentBanner，剧集那个条目只有自己取到才借得成。一趟往返三处共用；电影和剧自己就是答案，不用问。
        async Task<EmbyItem?> ShowAsync(CancellationToken token)
        {
            if (!episode || item.SeriesId is not { Length: > 0 } series) return null;

            try
            {
                return await _session!
                    .ExecuteAsync((client, ct) => client.GetItemAsync(series, ct), token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception error)
            {
                Log.Debug(Category, $"取剧集那一层失败（{item.Name}）：{error.Message}");
                return null;
            }
        }

        var show = ShowAsync(art.Token);

        var plate = ItemArtwork.Plate(item);

        try
        {
            // 四张图分头去取。以前是一张接一张：名牌回来了才开始要头图，头图回来了才开始要剧照 —— 三次往返
            // 排成一队，慢的那一张拖住后面两张。它们之间没有任何依赖，谁先回来谁先显示，版面不看先后。
            await Task.WhenAll(
                    Paint(DecodeFirstAsync(Refs(plate), PlateDecodeWidth, art.Token),
                        picture => PlateImage = picture),
                    HeroAsync(item, art.Token),
                    Paint(CornerAsync(item, show, art.Token), ShowCorner),
                    Paint(FooterAsync(item, show, art.Token), picture => FooterImage = picture),
                    Paint(StillAsync(item, show, art.Token), ShowStill))
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

        // 电影和剧的头图：解到像素存起来（_heroPixels —— 裁切模糊的原料），按当前的模糊档位渲染上屏，自己
        // 完成上屏（不经 Paint：它要多带一个生代号，重糊那一头比它新就不许盖上去）。「当窗口拉宽导致背景图
        // 下方被裁切的时候，触发背景图模糊，裁切越多越模糊」：半径由 DetailHero.PictureCrop ×
        // BackdropBlur.CropRadius 算（电影和剧没有基础半径，裁多少加多少；量化成档，拖窗口不为一个像素重糊），
        // 档位换了由 UpdateHeroBlur 用同一批像素重糊。集页不铺背景图（连模糊一起不存在），这一路到不了它。
        // 头图多带一个数上屏：这张图自己的高÷宽（ShowHero）——紧凑版式的带高和背景盒高都按它算
        // （DetailHero.CompactHeight / PictureHeight）；量不成话（0 或负）就不动兜底的那 0.5625。
        async Task HeroAsync(EmbyItem page, CancellationToken token)
        {
            foreach (var picture in ItemArtwork.Hero(page))
            {
                var bytes = await _images!
                    .GetAsync(picture.ItemId, picture.ImageType, picture.Tag,
                        EmbyImageStore.RequestWidth(HeroDecodeWidth), token)
                    .ConfigureAwait(true);
                if (bytes is null || bytes.Length == 0) continue;

                var source = await HeroPictureLoader
                    .DecodePixelsAsync(bytes, HeroDecodeWidth, token)
                    .ConfigureAwait(true);
                if (source is null) continue;
                if (!Fresh(art, page)) return;

                _heroPixels = source;

                var radius = HeroBlurTarget;
                var generation = ++_heroBlurGeneration;
                _heroBlurRadius = radius;

                var rendered = await HeroPictureLoader.RenderAsync(source, radius, token).ConfigureAwait(true);
                if (rendered is null || generation != _heroBlurGeneration) return;

                ShowHero(rendered);
                return;
            }
        }

        // 头图多带一个数上屏：这张图自己的高÷宽。紧凑版式的带高和背景那张的盒高都按它算
        // （DetailHero.CompactHeight / PictureHeight —— 「窗口收窄时背景图要等比例缩放」），解出来的是
        // 什么形状就照什么形状缩；量不成话（0 或负）就不动兜底的那 0.5625。集页不铺背景图，这一手到不了它。
        void ShowHero(BitmapImage picture)
        {
            if (picture.PixelWidth > 0 && picture.PixelHeight > 0)
                HeroHeightRatio = picture.PixelHeight / (double)picture.PixelWidth;
            HeroImage = picture;
        }

        // 左上角那格封面：集页取剧集那一层的 Primary（「集页面的封面改用剧页面的封面」—— 剧页面上那张 2:3
        // 海报），其余页面取自己的。借不到才退回本集自己的那几张 —— 有一张剧照总比空着一格强。
        async Task<BitmapImage?> StillAsync(EmbyItem page, Task<EmbyItem?> show, CancellationToken token)
        {
            if (page.Type != EmbyItemType.Episode)
            {
                return await DecodeFirstAsync(
                    page, [EmbyImageStore.Primary, EmbyImageStore.Thumb], StillDecodeWidth, token)
                    .ConfigureAwait(true);
            }

            var series = await show.ConfigureAwait(true);

            if (series is not null)
            {
                var poster = await DecodeFirstAsync(
                    series, [EmbyImageStore.Primary, EmbyImageStore.Thumb], StillDecodeWidth, token)
                    .ConfigureAwait(true);

                if (poster is not null) return poster;
            }

            return await DecodeFirstAsync(
                page,
                [EmbyImageStore.Primary, EmbyImageStore.Thumb, EmbyImageStore.Backdrop],
                StillDecodeWidth, token).ConfigureAwait(true);
        }

        // 右上角那张艺术图（「给集页面右上角添加艺术图」）：哪一张归这儿由 <see cref="ItemArtwork.Corner"/> 定 ——
        // 这一集自己的，没有就借剧集那一层的（show 在 WhenAll 外头就已经在路上了，这里等它回来再问一次标签）。
        // 只在集页上画，所以取图也只此一档：剧、电影页面的那一角是用户同一天去掉的，连问都不问。
        async Task<BitmapImage?> CornerAsync(EmbyItem page, Task<EmbyItem?> show, CancellationToken token)
        {
            if (page.Type != EmbyItemType.Episode) return null;

            var pick = ItemArtwork.Corner(page, await show.ConfigureAwait(true));

            return await DecodeFirstAsync(Refs(pick), CornerDecodeWidth, token).ConfigureAwait(true);
        }

        // 页尾那张横幅：一集自己几乎不会有横幅图（服务器把它挂在剧集那一层，同徽标），借哪一张等 show 回来
        // 一并问 <see cref="ItemArtwork.Footer"/>。它误不了任何一件正事 —— 那张图在整页最底下、滑到底才看得见，
        // 问失败就是那儿空着（ShowAsync 自己把错吞了）。
        async Task<BitmapImage?> FooterAsync(EmbyItem page, Task<EmbyItem?> show, CancellationToken token)
        {
            var pick = ItemArtwork.Footer(page, await show.ConfigureAwait(true));

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
    /// 封面到了：连它该画多大一起换上 —— 「海报下方会被裁切，要能看到完整的海报」。
    /// <para>
    /// 那一格原来是写死的 210×300、图按 <c>UniformToFill</c> 铺满它，而服务器上的海报是 2:3，于是上下各裁掉七八
    /// 个像素。现在那一格按这张图自己的形状收窄（<see cref="DetailHero.StillBox"/>），一个像素都不裁。
    /// 集页上这一张是剧的那张海报（「集页面的封面改用剧页面的封面」），取图那头已经换过源头了（见
    /// <c>StillAsync</c>），这里四种页面同一套：按图加当前窗口宽算盒子（<see cref="FitStill"/>）。
    /// </para>
    /// <para>
    /// 换尺寸和换图是同一拍里的两件事，顺序也就要紧：先尺寸再图。反过来的那一版会先按上一档尺寸画一帧、再收窄，
    /// 屏上是海报出现之后抖一下。而在这之前这一格整个是收着的（<see cref="StillVisibility"/> 问的就是有没有图），
    /// 所以这一拍之前那个尺寸谁也没看见。
    /// </para>
    /// </summary>
    private void ShowStill(BitmapImage picture)
    {
        _stillPixels = (picture.PixelWidth, picture.PixelHeight);
        RefitArtwork();
        StillImage = picture;
    }

    /// <summary>
    /// 右上角那张艺术图到了：连它该画多大一起换上 —— 「放大集页面右侧的艺术图」（2026-09-12）。
    /// <para>
    /// 和剧照那一张同一套写法（<see cref="ShowStill"/>），只是量出来的高喂的是带子
    /// （<see cref="CornerHeight"/> → <see cref="HeroRoom"/>）：这一格顶对齐摆在这一格里，带子矮过它就是这张画
    /// 压在底下的音轨那一行上。180 那一档不用管这件事 —— 它比那一叠字键矮，压不着。
    /// </para>
    /// <para>
    /// 位图的尺寸不成话（没解出来、或者报的是 0）时那个高留在 0：这一格照旧按 <c>Uniform</c> 缩在上限盒子里，
    /// 带子不因此长高，而不是拿一个量错的数去撑开版面。
    /// </para>
    /// </summary>
    private void ShowCorner(BitmapImage picture)
    {
        _cornerPixels = (picture.PixelWidth, picture.PixelHeight);
        FitCorner();
        CornerImage = picture;
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

    /// <summary>
    /// 三个下拉里有一个换了选择 —— 那一个属性的通知，加上「这一行画不画」。两份都要喊：尾部那一份和片名那一栏
    /// 那一份是同一句判据的两半（见 <see cref="PickersVisibility"/>／<see cref="ColumnPickersVisibility"/>）。
    /// </summary>
    private void PickerChanged(string which)
    {
        OnPropertyChanged(which);
        OnPropertyChanged(nameof(PickersVisibility));
        OnPropertyChanged(nameof(ColumnPickersVisibility));

        // 「视频：…」那一句跟媒体源的下拉互斥（见 VideoShown）：源多起来它让位给下拉，两边都得重问一遍。
        OnPropertyChanged(nameof(VideoVisibility));
        OnPropertyChanged(nameof(VideoLineVisibility));
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

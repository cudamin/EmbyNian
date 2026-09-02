using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Shell.ViewModels;
using EmbyNian.Shell.Views;
using EmbyNian.Shell.Windowing;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EmbyNian.Shell;

/// <summary>
/// Proves the shell actually came up, without a person having to look at it.
/// <para>
/// A <c>WinExe</c> has no console, so there is no way to print anything: the report file is the only
/// channel out. It answers the questions that a screenshot cannot — whether the island exists and has
/// a size, whether the palette overrides really shadowed the framework's brushes, what the island's
/// scale is, whether Mica is available on this machine — and one that only a screenshot could, by
/// sampling screen pixels to confirm something was genuinely composited to the display.
/// </para>
/// <para>
/// Under <c>--dump-ui</c> it also writes the visual tree and a PNG of the shell. The PNG comes from
/// <see cref="RenderTargetBitmap"/>, which renders the tree itself rather than reading the screen, so the
/// two together separate 「the app drew nothing」 from 「the app drew, and the display did not show it」 —
/// a distinction that otherwise takes probing the process from outside.
/// </para>
/// <para>
/// 这个类拆在九个文件里，都是同一个 <c>partial</c>：本文件是那些读数和按拍推进的那台机器；
/// <c>.Reads</c> 把活着的树读成读数；<c>.Frame</c> 问窗口边框和标题栏；<c>.Run</c> 攒报告；<c>.Pages</c> 和
/// <c>.Settings</c> 是一页一关；<c>.Fullscreen</c> 那几关会动真窗口；<c>.Theme</c> 是主题、样式和屏幕取样；
/// <c>.Output</c> 把报告、树和 PNG 落到磁盘。拆开只是搬家 —— 一行代码没改，验收就是自检报告一字不差。
/// </para>
/// </summary>
internal static partial class ShellSelfCheck
{
    private const string Category = "自检";

    /// <summary>
    /// Rooted for the same reason as the window procedure: a started timer whose only reference is a
    /// local would be collectable before it ticks.
    /// </summary>
    private static DispatcherQueueTimer? _timer;

    private static int _ticks;

    /// <summary>
    /// Which page the walk is looking at: 0 the home page it starts on, 1 a library, 2 one item's detail
    /// page, 3 the servers page, 4 the diagnostics page, 5 the settings page.
    /// <para>
    /// Everything past the home page has to be navigated to, because its content does not exist until
    /// then — requirement 4's sort menu and requirement 5's filter panel only live on a library, the
    /// servers page builds one card per saved server from a template the build never runs, and the
    /// diagnostics page's log rows come from another such template. The frame holds one page at a time,
    /// so each stage snapshots what the report needs before the next one navigates away.
    /// </para>
    /// </summary>
    private static int _stage;

    /// <summary>The last stage; <see cref="Advance"/> stops here and the report gets written.</summary>
    private const int LastStage = 5;

    /// <summary>
    /// What the home page looked like before the check walked off it, since the frame keeps one page at
    /// a time. Null when it never got that far.
    /// </summary>
    private static (bool Correct, int Cards, string Shelves, string Banner, bool TypeOk, string Type, bool PictureOk,
        string Picture, bool LayoutOk, string Layout, bool BleedOk, string Bleed, bool? FoldOk, string Fold)? _home;

    /// <summary>
    /// 需求 6 的两处改动：what the home page's own cards offer under the pointer. Snapshotted with the
    /// rest of stage 0 because the frame holds one page at a time, and null when this account's home page
    /// had realised no cards to look at.
    /// </summary>
    private static (bool Ok, string Detail)? _cards;

    /// <summary>
    /// 按一张卡片会不会让整页先滑一段 —— 「点击主页继续观看、媒体库、最近添加的封面之后会先跳转到页面下方，
    /// 然后才会进入页面」。跟着 stage 0 一起快照，理由和上面几条一样：框里一次只有一页。空的主页上是 null。
    /// </summary>
    private static (bool Ok, string Detail)? _stay;

    /// <summary>
    /// 那一条已经把焦点按到卡片上了没有。它分两拍：这一拍按，下一拍量 —— <c>StartBringIntoView</c> 是异步的，
    /// 同一拍里坏的那一版和好的那一版读出来一模一样。见 <see cref="HomePage.StayFocus"/>。
    /// </summary>
    private static bool _stayFocused;

    /// <summary>The same, for the library page. Null when this server reported no libraries at all.</summary>
    private static LibraryState? _library;

    /// <summary>
    /// 标题栏那三颗系统按钮的墨，趁主页还在框里量的。压在剧照上时它必须是那支固定的浅墨，而不是主题的墨
    /// —— 浅色主题（晴昼）的墨是深色，画在图顶上那层黑罩上就是三颗看不见的按钮，其中一颗是关闭。
    /// <para>
    /// 和别的快照一样存成字段：报告写出来的时候这份检查早就走到设置页了，那时候墨已经按规矩还回主题色，
    /// 现场再问一次问到的是另一件事。
    /// </para>
    /// </summary>
    private static (bool Ok, string Detail)? _ink;

    /// <summary>
    /// Everything the report says about the library page, read while that page was still the one in the
    /// frame. A record rather than a live read at report time: the walk goes on to another page, and a
    /// property read after that would be reading whatever replaced it.
    /// </summary>
    private sealed record LibraryState(
        bool Ready,
        string Heading,
        int Cards,
        string? Preset,
        string SortKey,
        bool Descending,
        IReadOnlyList<string> SortLabels,
        IReadOnlyList<string> FilterLabels,
        bool FilterTemplates,
        string Filters,
        int PosterSetting,
        int CardWidth,
        double CellWidth,
        double CellHeight,
        string View,
        IReadOnlyList<(string Label, string Layout, double Cell, double Row)> Views,
        bool ViewRestored,
        bool AlphaShown,
        string Artwork,
        (bool Found, string Still, string Over, string Pressed) Halo);

    /// <summary>
    /// The detail page the walk opened off the library's first row. Null when there was no such row, which
    /// is what a library of box sets or a server with no libraries looks like.
    /// </summary>
    private static DetailState? _detail;

    /// <summary>
    /// Whether the detail stage has already asked the page to scroll down. The stage is two ticks long
    /// because of it: see <see cref="ScrollDetail"/>.
    /// </summary>
    private static bool _detailScrolled;

    /// <summary>
    /// 「详情页重新读取过一遍了」。这一步存在的理由是 <c>DetailViewModel.Preview</c> 那条规矩只有重新读取时才验得到：
    /// 先用卡片画一屏是对的，可**重新读取时再拿那张卡片盖一遍就是把整页退回旧值** —— 屏上看得见的那一处是
    /// 「标记为已观看」之后那个勾先跳回未看、一趟往返之后才变回来。一百毫秒的事，截图抓不住，只有数得出来。
    /// <para>
    /// 摆在这一段的最后，在 <see cref="OpenFilePage"/> 和 <see cref="ShowInfo"/> 之后：重新读取会先清空集带再重新
    /// 填，夹在那两步之前会让「点第一张集卡片」找不到卡可点，把 媒体信息 那几关整片变成「这次没走到文件页」。
    /// </para>
    /// </summary>
    private static bool _detailReloaded;

    /// <summary>
    /// What 单集 had drawn before that scroll, split by shape — rows off the vertical list, cards off the
    /// horizontal strip. 单集 sits at the top of the page and 演职人员 at the bottom, so the two counts belong
    /// to different scroll positions; keeping this one lets <see cref="ReadDetail"/> report the best each ever
    /// managed rather than one tick's view. Split because which of the two shapes drew is itself a check.
    /// </summary>
    private static (int Rows, int Cards) _detailEpisodesDrawn;

    /// <summary>
    /// The same pair, read on the page of a file — the episode page the walk clicks through to. Null when
    /// the walk never got one. Kept apart from <see cref="_detailEpisodesDrawn"/> for the reason
    /// <see cref="InfoState"/> is its own record: it belongs to a different page, and one of the two page
    /// kinds the user reported is exactly this one. Taken before <see cref="ShowInfo"/> scrolls away from it.
    /// </summary>
    private static (string Type, int Models, int Rows, int Cards, bool ScreenOk, string Screen)? _fileEpisodesDrawn;

    /// <summary>
    /// 需求 4's name plate on the page of a file, kept apart from <see cref="_detail"/>'s reading for the
    /// reason above: it belongs to a different page. It is also the more interesting of the two — an episode
    /// almost never has a 徽标 of its own, so this is the only page where the inherited one
    /// (<see cref="EmbyItem.ParentLogoItemId"/>) is ever reached for, and the only way to find out whether
    /// this server sends it without asking it by hand.
    /// </summary>
    private static (string Type, ArtworkRead Artwork)? _fileArtwork;

    /// <summary>
    /// 「媒体源／音频／字幕」那三个下拉在文件页上的几何，同样和 <see cref="_detail"/> 那一份分开存：这一页才是三个
    /// 下拉都露面的那一页（媒体源只在文件上才有得选），所以窄窗口下一行装不下、最右边那个被切掉的事只在这里看得见。
    /// 和 <see cref="_fileArtwork"/> 一样在 <see cref="ShowInfo"/> 里读 —— 那是这一页滚走之前的最后一拍。
    /// </summary>
    private static (string Type, bool Ok, string Detail)? _filePickers;

    /// <summary>
    /// 集页上那行剧名按下去落在哪儿（<c>DetailPage.TitleLinkRead</c>）——「点击剧名之后应该进[入]剧页面而不是季
    /// 页面」。和 <see cref="_filePickers"/> 同一拍读，理由也一样：剧名那一行在头图里，页面滚去看媒体信息之后
    /// 它就出了视口，而这一条要的是那颗按钮真接上了、提示真说的是那部剧。
    /// </summary>
    private static (string Type, bool Ok, string Detail)? _fileTitleLink;

    /// <summary>
    /// 详情页那一行类型点不点得动。在剧页上量（<see cref="ScrollDetail"/> 那一拍）：那一行的每个类型都是一段
    /// <c>Hyperlink</c>，而它们是代码搭的 —— 搭空了屏上就是一行普通的字，看着和以前一模一样，点下去什么都不
    /// 发生，别的读数一个都不会响。
    /// </summary>
    private static (bool Ok, string Detail)? _detailGenres;

    /// <summary>
    /// 「第一屏是先用卡片画的、图没白重取」那一条的读数。和 <see cref="_detailGenres"/> 一起在走到详情页的那一拍
    /// 攒下来 —— 走完之后页面会被换掉，视图模型跟着走，那两个计数就问不到了。
    /// </summary>
    private static (bool Ok, string Detail)? _detailPreview;

    /// <summary>
    /// 文件页上那张头图到底取回来解出来了没有。规则那一半由 <see cref="_fileArtwork"/> 判 —— 「集页面要用这个剧
    /// 的背景图或缩略图」挑的是剧集那一头的标签，而那种标签只能按剧集自己的 id 去取；按本条目的 id 去取会拿回一个
    /// 空答案，而这一格照旧按有图那一档留着 460 的高，屏上就是一格空画面。这一句就是那件事的读数。
    /// <para>
    /// 不在走到这一页那一拍读：那时候图还在路上，读出来永远是「没有」。<see cref="ReadInfo"/> 每一眼写一次，报告
    /// 拿的是最后一眼看到的（同 <see cref="InfoLooks"/>）。
    /// </para>
    /// </summary>
    private static bool _fileHero;

    /// <summary>
    /// Whether the detail stage has already clicked through to the page of a file, and whether it has
    /// already sent that page down to 媒体信息. Two more ticks on the same stage: see
    /// <see cref="OpenFilePage"/> and <see cref="ShowInfo"/>.
    /// </summary>
    private static bool _fileOpened;

    /// <inheritdoc cref="_fileOpened"/>
    private static bool _infoScrolled;

    /// <summary>
    /// How many ticks have looked at 媒体信息 and found rows still missing. <see cref="ShowInfo"/>'s scroll is
    /// a queued operation and a repeater builds its rows during the layout pass that follows, so the tick
    /// after it has sometimes laid the table out and sometimes not — a single look that finds rows missing is
    /// reading the frame too early rather than reporting a table that failed. See <see cref="Advance"/>.
    /// </summary>
    private static int _infoLooks;

    /// <summary>
    /// 看几眼才认账。八眼就是八拍，一拍 500 毫秒，而且每一拍都要等这一页自己停下来（<see cref="Settled"/>）。
    /// <para>
    /// 四眼（两秒）在这台机器上红过两次，两次读数都是 <c>10 行（已渲染 0），首行「」</c> —— 一次滚动落在页面
    /// 量过尺寸之前就是滚不动，而这一页上面还压着一排会长高的演职人员头像和一条会长高的单集带：图还在路上时
    /// 表格的位置就还在变，滚到的那个地方过一会儿就不是表格了。图缓存热了的那一遍从来不红（同一份构建紧接着
    /// 重跑就是全绿），所以差的不是「表格画不出来」而是那几秒。
    /// </para>
    /// <para>
    /// 放宽不会把红改绿：一张真的画不出来的表八眼之后照旧是 0，报告写的永远是最后一眼看到的；而白等一眼的代价
    /// 只有半秒，还只花在本来要失败的那一遍上（第 2 阶段的预算是 60 拍）。
    /// </para>
    /// </summary>
    private const int InfoLooks = 8;

    /// <summary>
    /// The library row the walk clicked to get onto the detail stage, and the page that click actually
    /// landed on. Null when the walk never had a row to click.
    /// </summary>
    private static CardClick? _click;

    /// <param name="Landed">
    /// Filled in a tick later, when the navigation the click started has happened. The whole point of the
    /// pair: this grid used to answer a click on a series with another grid of season folders, and nothing
    /// noticed because the check opened the detail page itself instead of clicking anything.
    /// </param>
    private sealed record CardClick(string Item, string? Landed = null);

    /// <param name="DrawnRows">
    /// How many episode rows the vertical list drew, and <paramref name="DrawnCards"/> how many cards the
    /// horizontal strip drew. Split rather than summed because 单集 has two shapes and this page kind's type
    /// picks one: 季 lists, 剧 and 集 page through a strip. The shape that is hidden draws nothing, so the
    /// pair is what says which one happened — see 详情单集形状 in <see cref="ReportDetail"/>.
    /// </param>
    /// <param name="Artwork">需求 4's name plate and this item's artwork, see <see cref="ArtworkRead"/>.</param>
    private sealed record DetailState(
        bool Ready,
        string Type,
        string Title,
        int Overview,
        string Facts,
        int Seasons,
        int Sources,
        int Audio,
        int Subtitles,
        bool Chosen,
        string Picked,
        string? Target,
        string? TargetType,
        string Play,
        int Info,
        bool Hero,
        bool Still,
        ArtworkRead Artwork,
        int Episodes,
        int Cast,
        int DrawnRows,
        int DrawnCards,
        int DrawnCast,
        bool HeroTypeOk,
        string HeroType,
        bool HeroFillOk,
        string HeroFill,
        bool BodySealOk,
        string BodySeal,
        bool PickerFitOk,
        string PickerFit,
        bool WashOk,
        string Wash,
        bool StillShapeOk,
        string StillShape,
        bool FooterOk,
        string Footer);

    /// <summary>
    /// 一张记号在页面上的样子：规矩说该有哪一张、屏上画了没有、画多大、落对了没有，和它是从哪一片几何读出来的。
    /// <para>
    /// 两枚各一份而不是一份带个「哪个角」—— 「统一改为在剧名上方显示徽标，右上角显示艺术图」之后它们是两个各自
    /// 独立的位置，各空各的、各错各的，一份读数说不清是哪一样没落对。
    /// </para>
    /// </summary>
    /// <param name="Wanted">
    /// 规矩说这一格该有哪一张，用词说 —— 「自己的艺术图」、「自己的徽标」、「剧集的徽标（取自条目 5687）」、「无」。
    /// 由 <see cref="ItemArtwork"/> 自己那两支算出来（<see cref="ItemArtwork.Plate"/> 和
    /// <see cref="ItemArtwork.Corner"/>），所以这是应用自己那条规则的答案，不是这个文件对它的猜测。id 一样要紧：
    /// 集页和季页上徽标是剧集那一头发的，按这一页自己的 id 去取会取回一个空答案，而「徽标」两个字自己说不出走了
    /// 哪一条路。
    /// </param>
    /// <param name="Drawn">
    /// 屏上画了没有。服务器没有这一张的条目占大多数，那一次它是 false 而 <paramref name="Placed"/> 空着成立。
    /// </param>
    /// <param name="Placed">
    /// 真落在该落的地方没有：徽标要在片名的上方、左沿跟它对齐，艺术图要在带子的右上角，两者都不许顶出带子、也
    /// 不许压到海报。几何在 <paramref name="Where"/> 里，读的地方是 <see cref="DetailPage.PlateShape"/> 和
    /// <see cref="DetailPage.CornerShape"/>。
    /// </param>
    private sealed record MarkRead(
        string Wanted,
        bool Drawn,
        double Width,
        double Height,
        bool Placed,
        string Where);

    /// <summary>
    /// 一格记号在报告里的那一句：画了没有、画多大、规矩说该是哪一张，加上量到的几何。三处共用（详情页那一条、
    /// 文件页那一行信息、文件页那一条落点），所以措辞只有一份 —— 三处各写一遍的结果是同一件事在报告里三种说法。
    /// </summary>
    private static string Say(string what, MarkRead read) =>
        $"{what}{(read.Drawn ? $"画了（{read.Width:0}×{read.Height:0}）" : "没画")}"
            + $" —— 规矩说该有的是{read.Wanted}；{read.Where}";

    /// <summary>
    /// 需求 4 on the detail page: 「把媒体的徽标…融入对应媒体的 ui 界面」 as the page really came out.
    /// </summary>
    /// <param name="Plate">剧名上方那一枚徽标（徽标 or 横幅图）。</param>
    /// <param name="Corner">右上角那一张艺术图。</param>
    /// <param name="Text">
    /// Whether the text title is on screen. It is now unconditional and both pictures merely optional —
    /// see 详情徽标与艺术图 in <see cref="ReportDetail"/>.
    /// </param>
    /// <param name="Kinds">
    /// Which artworks the server holds for this one item, 「海报、缩略图、背景图」. Informational: a thin
    /// catalogue is not a defect, and what it answers is whether the new artwork will ever be seen here.
    /// </param>
    /// <param name="Hero">
    /// 头上那一格铺的是谁的哪一种，用词说 —— 「剧集的背景图（取自条目 5687）」、「自己的海报」、「无」。
    /// <see cref="ItemArtwork.Hero"/> 排的那一串的头一个，所以这是应用自己那条规则的答案，不是这个文件的猜测。
    /// </param>
    /// <param name="HeroOk">
    /// 「集页面要用这个剧的背景图或缩略图」：集页和季页上，只要服务器把剧集那一层的宽幅图发下来了
    /// （<see cref="ItemArtwork.Inherited"/>），铺在背后的那张就必须是剧集那一头的，不能是这一集自己的截图。
    /// 服务器一张都没发的那一次这句话空着成立 —— 那时候退回自己的画面是对的。
    /// </param>
    private sealed record ArtworkRead(
        MarkRead Plate,
        MarkRead Corner,
        bool Text,
        string Kinds,
        string Hero,
        bool HeroOk);

    /// <summary>
    /// 媒体信息 as it draws on the page of a file — a movie's page, or the episode page the walk clicks
    /// through to from a show's. Null when the walk never reached one; see <see cref="OpenFilePage"/>.
    /// <para>
    /// A record of its own rather than more fields on <see cref="DetailState"/>, because it is read on a
    /// different page than that one: the walk lands on a show, snapshots it, and only then clicks into an
    /// episode. Folding the two together would mean one of the two pages reporting the other's numbers.
    /// </para>
    /// </summary>
    private static InfoState? _info;

    /// <param name="Drawn">
    /// How many of <paramref name="Rows"/> the repeater really built. The table sits below the fold, so
    /// this is zero on arrival and stays zero without <see cref="ShowInfo"/>.
    /// </param>
    /// <param name="First">
    /// The text of the first row it built, label and value together. What proves the template's bindings
    /// fired rather than merely that the template resolved.
    /// </param>
    private sealed record InfoState(
        string Type,
        string Title,
        int Rows,
        int Drawn,
        bool Expanded,
        string Labels,
        string First);

    /// <summary>
    /// 跨季 as the real server answers it: one season boundary walked in the episode list the server
    /// returns for a whole show, and a 季 picker switched from one season to another on a detail page of
    /// this check's own. Null when the probe never ran; see <see cref="ProbeSeasonsAsync"/>.
    /// </summary>
    private static SeasonState? _seasons;

    /// <param name="Skipped">
    /// Non-null when there was nothing to ask — not signed in, or no show on this server with a crossable
    /// boundary. Reported as 「信息」, because neither is this build's fault.
    /// </param>
    /// <param name="PickerOk">Null for the same reason, asked of the detail half alone.</param>
    private sealed record SeasonState(
        string? Skipped,
        bool CrossOk = false,
        string Cross = "",
        bool? PickerOk = null,
        string Picker = "");

    /// <summary>
    /// The servers page, snapshotted for the same reason as the two above: it used to be where the walk
    /// ended and could therefore be read live, and since the diagnostics stage was added it no longer is.
    /// </summary>
    private static ServersState? _servers;

    private sealed record ServersState(
        bool Ready,
        int Bound,
        int BoundAccounts,
        string Cache,
        int Drawn,
        int DrawnAccounts);

    /// <summary>
    /// The diagnostics page, snapshotted for the same reason as the three above: it used to be where the walk
    /// ended and could therefore be read live, and since the settings stage was added it no longer is.
    /// </summary>
    private static DiagnosticsState? _diagnostics;

    private sealed record DiagnosticsState(
        bool Ready,
        int Rows,
        int Categories,
        bool Following,
        string Launch,
        int Drawn);

    /// <summary>
    /// 需求 8 的内嵌控制台, snapshotted like the pages above: the walk visits it after the cards and then goes
    /// back to a card, both because the settings checks are read live off a card and because leaving that
    /// category is what releases the browser. Null when the walk never got that far.
    /// <para>
    /// No token, no cookie and no user id in any of it — see <see cref="DashboardPage.Summary"/>.
    /// </para>
    /// </summary>
    private static DashboardState? _dashboard;

    private sealed record DashboardState(
        bool Ready,
        string Url,
        bool Core,
        bool Seeded,
        bool? Navigated,
        string Error,
        string Client);

    /// <summary>
    /// Where the console visit has got to: 0 not started, 1 selected and waiting for it to settle, 2 snapshotted
    /// and back on a card. Read at the top of <see cref="WalkSettings"/>, which is why it is a step rather than
    /// a bool — the middle state has to be told apart from both ends.
    /// </summary>
    private static int _dashboardStep;

    /// <summary>
    /// One entry per settings category the walk has opened, in the order it opened them. A list rather than
    /// a single snapshot because the settings stage is a walk of its own: see <see cref="WalkSettings"/>.
    /// Everything else about that page is read live, since it is the one the walk ends on.
    /// </summary>
    private static readonly List<SettingsStop> _settings = [];

    private sealed record SettingsStop(string Category, int Rows, int Drawn, int Expected);

    /// <summary>0 when every check passed. <c>Program</c> returns this as the process exit code.</summary>
    public static int ExitCode { get; private set; }

    public static void ScheduleFor(
        HostWindow window,
        ShellPage shell,
        IServiceProvider services,
        StartupOptions options)
    {
        // Invalidate the last run's report before this one has anything to say. Without this a crash
        // leaves the previous report on disk, and a passing report from twenty minutes ago is worse than
        // no report at all: it reads as "this build is fine" when the build under test never finished.
        ExitCode = 1;
        Write(options, "selfcheck-shell.txt", $"""
            {AppIdentity.TitleWithVersion} — WinUI 3 外壳自检
            时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}

            结果：未完成 —— 自检还没有走完就退出了。若无后续报告，请看同目录的 app-*.log。
            """);

        // Polled rather than one delayed shot. The shell now has round trips to the server between
        // being shown and being finished — a saved token, then the library list, then the home rows,
        // then a library's first page — and how long those take on someone else's network is not ours
        // to guess. The first three ticks are waited out regardless: shorter than that and the pixel
        // sample reads the desktop rather than the app, which looks like a failure.
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(500);
        _timer.IsRepeating = true;
        _timer.Tick += async (timer, _) =>
        {
            _ticks++;
            if (_ticks < 3) return;

            if (!Settled(shell) && _ticks < Deadline) return;

            // Walk on to the next page so its own surfaces get looked at too, then keep polling for it.
            if (!shell.SignInVisible && Advance(shell, window)) return;

            timer.Stop();

            try
            {
                // Before the report and after the walk: the only check with round trips of its own, so it
                // cannot ride along on a synchronous tick. Never throws — see ProbeSeasonsAsync.
                if (!shell.SignInVisible) _seasons = await ProbeSeasonsAsync(shell, services);

                Run(window, shell, services, options);

                // After the report, not inside it: the picture is the slow part and it is the report that
                // the exit code comes from, so a failure to encode a PNG must not be able to cost us one.
                if (options.DumpUi) await ShootAsync(shell, options);
            }
            catch (Exception error)
            {
                ExitCode = 1;
                Log.Error(Category, "自检本身出错", error);
            }
            finally
            {
                window.Close();
            }
        };
        _timer.Start();
    }

    /// <summary>
    /// Ticks allowed before the walk gives up on the page it is on and reports what it has. Cumulative,
    /// and one budget per stage: a page opened on the last tick of a shared one would be reported on
    /// before its first request came back.
    /// </summary>
    private static int Deadline => _stage switch
    {
        0 => 16,
        1 => 30,

        // The detail page has two round trips of its own — the item by id, then that season's episodes —
        // and this stage is four ticks long besides, one of them a second navigation with two more round
        // trips behind it: the show's page, then one episode's. Hence twice a library's budget.
        2 => 60,
        3 => 74,

        // The diagnostics page reads in-process state synchronously, so it is settled the moment it is
        // navigated to; this budget only has to cover the navigation itself.
        4 => 86,

        // The settings page is settled just as fast, but its stage is a walk rather than a look: eight cards,
        // one per tick, and then 需求 8's console — the one thing in this stage that waits on the network. That
        // page has a 15-second watchdog of its own, so this budget only has to be wide enough that it, and not
        // this, is what ends the wait: a deadline reached mid-console would report a console that had merely not
        // answered yet as a broken one.
        _ => 140
    };

    /// <summary>
    /// Snapshots the page the walk is on and opens the next one. False when there is nowhere left to go,
    /// which is when the report gets written.
    /// <para>
    /// The loop is for a server with no libraries: there is no library tag to navigate to, so that stage
    /// records 「未打开」 and the walk carries on to the one after it rather than stopping early. The detail
    /// stage skips itself the same way when the library it would have drilled into never opened.
    /// </para>
    /// </summary>
    private static bool Advance(ShellPage shell, HostWindow window)
    {
        while (_stage < LastStage)
        {
            if (_stage == 0)
            {
                _home ??= ReadHome(shell);
                _cards ??= ReadCards(shell);
                _ink ??= ReadInk(shell, window);

                // 多一拍：这一拍把焦点按到第一张卡上，下一拍才量这一页挪没挪。三个读数在按之前拿，所以焦点
                // 那一下动不到它们。
                if (StayAsk(shell)) return true;

                _stay = (shell.Pages.Content as HomePage)?.StayRead();
            }
            else if (_stage == 1) _library = ReadLibrary(shell);
            else if (_stage == 2)
            {
                // Where the click of the previous tick ended up, read before anything else looks at the
                // frame. A detail page is what it should be; a library page is the regression. Recorded
                // once, on arrival: this stage comes back here three more times, by which point the frame
                // may be holding a second detail page this click had nothing to do with.
                if (_click is { Landed: null } click)
                    _click = click with { Landed = shell.Pages.Content?.GetType().Name };

                // Four ticks on this stage rather than one. The first extra buys the only look anything
                // ever takes at the cast row's template; the other two walk one page further in, to a
                // file's own page, which is the only place 媒体信息 exists. Asking for another tick
                // without moving the stage on is safe: the page stays settled, so the next tick lands
                // back here — and each of the three steps below runs at most once.
                if (ScrollDetail(shell)) return true;

                _detail ??= ReadDetail(shell);

                if (OpenFilePage(shell)) return true;
                if (ShowInfo(shell)) return true;

                // 媒体信息 gets up to <see cref="InfoLooks"/> looks, for the reason on <see cref="_infoLooks"/>:
                // a table that has not drawn every row it built is the one reading that is worth taking again —
                // and worth asking for again, because a scroll issued before the page had been measured lands
                // nowhere. Whatever the last look saw is what the report gets, so a table that really never
                // draws still fails.
                //
                // 「一行都没画」不够：一次落在半路的滚动会把表格上面那几行留在视口外，读出来是「10 行画了 6
                // 行」—— 那和一行都没画是同一件事的两个程度，而这一关要的就是每一行都真画出来了。
                _info = ReadInfo(shell);
                if (_info is { Rows: > 0 } info && info.Drawn < info.Rows && ++_infoLooks < InfoLooks)
                {
                    (shell.Pages.Content as DetailPage)?.ScrollToInfo();
                    return true;
                }

                // 最后一步：重新读取一遍，再问那三个数。见 _detailReloaded 上的说明 —— 「先画」那条规矩的另一半
                // 只有在这里才验得到，而它必须排在上面两步之后。
                if (ReloadDetail(shell)) return true;

                _detailPreview ??= (shell.Pages.Content as DetailPage)?.PreviewRead();
            }
            else if (_stage == 3) _servers = ReadServers(shell);
            else if (_stage == 4) _diagnostics = ReadDiagnostics(shell);

            _stage++;

            // The detail page is the one stage with no navigation tag — it is a drill-down from a card,
            // not a pane entry — so it cannot go through Destination below.
            if (_stage == 2)
            {
                if (OpenFirstDetail(shell)) return true;
                continue;
            }

            if (Destination(shell) is not { } tag) continue;

            shell.GoTo(tag);
            return true;
        }

        // The last stage is not another page but a walk through the one it just opened.
        return WalkSettings(shell);
    }

    /// <summary>
    /// Opens the settings page's cards one per tick, recording what each one drew, and then looks at 需求 8's
    /// embedded console. True while there is somewhere left to go; the report is written on the call that runs
    /// out of places.
    /// <para>
    /// A stage of its own because one look at this page proves much less than one look at any other. It picks
    /// between eight cards and seven row templates, and a collapsed card never builds its rows — so arriving
    /// and reporting would exercise the first card's three rows and leave the slider and the switch group,
    /// which only later cards use, as untested as they were before the walk existed. Those templates are
    /// markup no build runs.
    /// </para>
    /// <para>
    /// The expected count is cumulative because collapsing a card hides it without discarding what it built:
    /// by the last card the tree should hold every row on the page.
    /// </para>
    /// </summary>
    private static bool WalkSettings(ShellPage shell)
    {
        if (shell.SettingsRoot is not { } page) return false;

        var cards = page.Cards;
        if (cards.Count == 0) return false;

        // 需求 8's console comes after the cards and takes the page off them while it lasts, so its step is
        // read before the guard below — which would otherwise see a non-card selection and read it as the walk
        // having arrived on 诊断.
        if (_dashboardStep > 0) return VisitDashboard(page);

        // The stage before this one was 诊断, and that is a category of this page now — with the page cached,
        // the walk arrives on it. There is nothing to record for a hosted category (both have snapshots of
        // their own), so the first tick here only has to open the first card.
        if (!cards.Any(card => string.Equals(card.Category, page.SelectedCategory, StringComparison.Ordinal)))
        {
            page.SelectedCategory = SettingsViewModel.FirstCardCategory;
            return true;
        }

        // Which cards have been opened, this one included. Taken from the page rather than from the index,
        // so a category the list decided not to select is reported as what it is instead of as its neighbour.
        var opened = _settings
            .Select(stop => stop.Category)
            .Append(page.SelectedCategory)
            .ToHashSet(StringComparer.Ordinal);

        _settings.Add(new SettingsStop(
            page.SelectedCategory,
            page.VisibleRows,
            page.Realised,
            cards.Where(card => opened.Contains(card.Category)).Sum(card => card.Rows)));

        var next = cards.FirstOrDefault(card => !opened.Contains(card.Category)).Category;
        if (!string.IsNullOrEmpty(next))
        {
            page.SelectedCategory = next;
            return true;
        }

        // Cards done; the last entry in this page's list is the embedded console. Selecting it navigates the
        // hosted frame there and then, so the next tick's <see cref="Settled"/> gate is the console's own.
        _dashboardStep = 1;
        page.SelectedCategory = SettingsViewModel.DashboardCategory;
        return true;
    }

    /// <summary>
    /// 需求 8：the one look anything takes at the embedded console — and the only thing that ever starts its
    /// browser outside a user's own visit, which is the point. Two ticks: one to read what it ended up with,
    /// one to let the card column lay itself out again afterwards.
    /// <para>
    /// It goes back to the last card rather than staying, for two reasons. The settings checks that follow read
    /// that page live — 设置行模板 counts row containers off the tree, and a hosted page showing means none of
    /// them are on it — and leaving the category is also what releases the WebView, so the report is not written
    /// with a browser process still up behind it.
    /// </para>
    /// </summary>
    private static bool VisitDashboard(SettingsPage page)
    {
        if (_dashboardStep == 1)
        {
            _dashboardStep = 2;

            if (page.Dashboard is { } console)
            {
                var (ready, url, core, seeded, navigated, error, client) = console.Summary;
                _dashboard = new DashboardState(ready, url, core, seeded, navigated, error, client);
            }

            page.SelectedCategory = _settings[^1].Category;
            return true;
        }

        return false;
    }

    /// <summary>Where the current stage lives, or null when this server has no such page.</summary>
    private static string? Destination(ShellPage shell) => _stage switch
    {
        1 => shell.FirstLibraryTag,
        3 => "servers",
        4 => "diagnostics",
        5 => "settings",
        _ => null
    };

    /// <summary>
    /// Clicks the library's first row that has a detail page, through the grid's own invoke handler.
    /// False when the walk is not on a library or that library holds nothing that drills down, which makes
    /// the detail stage skip itself.
    /// <para>
    /// A click rather than 「shell, open this item's detail page」, which is what this used to do. Opening
    /// the page directly meant the walk never travelled the path a user travels, and that is precisely
    /// where a real defect sat: the library grid answered a click on a series with another grid of season
    /// folders, and every claim on the detail stage went on passing about a page the user could not reach
    /// that way. What the click landed on is checked in <see cref="ReportDetail"/>.
    /// </para>
    /// </summary>
    private static bool OpenFirstDetail(ShellPage shell)
    {
        if (shell.Pages.Content is not LibraryPage library) return false;
        if (library.OpenFirstDetail() is not { } item) return false;

        _click = new CardClick(item.Name);
        return true;
    }

    /// <summary>
    /// 把焦点按到主页第一张卡上，然后要一拍。True 表示这一拍花在这上面 —— 主页上一张卡都没渲染出来时不花。
    /// <para>
    /// 同 <see cref="ScrollDetail"/> 那一手：一次问不出来的事就分两拍问。这一条要量的是「按了之后这一页会不会
    /// 自己滑走」，而滑是异步的、带动画的，同一拍里连坏的那一版都是一动不动。见 <see cref="HomePage.StayFocus"/>。
    /// </para>
    /// </summary>
    private static bool StayAsk(ShellPage shell)
    {
        if (_stayFocused || shell.Pages.Content is not HomePage page) return false;

        _stayFocused = true;
        return page.StayFocus();
    }

    /// <summary>
    /// Sends the detail page to the bottom on the stage's first tick, so the row of faces is in view by
    /// the second one. True while that is still worth waiting for.
    /// <para>
    /// An <c>ItemsRepeater</c> only builds what its effective viewport covers, and on any normal window
    /// the cast row starts below the fold: read on arrival, it has drawn nothing, and 「the person template
    /// works」 would be a claim about a template that never ran. The scroll is a queued operation rather
    /// than an assignment, so the count has to wait a tick for it either way.
    /// </para>
    /// <para>
    /// The episode count is taken here, before the scroll, for the mirror-image reason: 单集 is a list at the
    /// top of the page and 演职人员 the strip at the end of it, so no one scroll position has both in view.
    /// Whichever tick saw rows drawn is the honest answer, and <see cref="ReadDetail"/> takes the larger of
    /// the two.
    /// </para>
    /// </summary>
    private static bool ScrollDetail(ShellPage shell)
    {
        if (_detailScrolled || shell.Pages.Content is not DetailPage page) return false;

        _detailScrolled = true;
        _detailEpisodesDrawn = page.EpisodeShapes;
        _detailGenres = page.GenreRead();
        page.ScrollToEnd();
        return true;
    }

    /// <summary>
    /// 让屏上那一页重新读取一遍，然后交出一拍等它落定。见 <see cref="_detailReloaded"/>：这一步只为验
    /// <c>DetailViewModel.Preview</c> 那条规矩的另一半 —— 重新读取时不许再拿卡片那一份把整页盖一遍。
    /// <para>
    /// 只是重新读，不改服务器上的任何东西（那几个请求全是 GET）。真正会写状态的是「标记为已观看」，而它走的正是同
    /// 一个重新读取，所以这一步验的就是那条路。
    /// </para>
    /// </summary>
    private static bool ReloadDetail(ShellPage shell)
    {
        if (_detailReloaded || shell.Pages.Content is not DetailPage page) return false;

        _detailReloaded = true;
        page.ViewModel.Reload();
        return true;
    }

    /// <summary>
    /// Clicks the first card in 单集, so the walk ends the stage on the page of a file rather than on the
    /// page of a show. True while that navigation is still worth waiting for.
    /// <para>
    /// 媒体信息 only exists on a file's own page — 剧 and 季 deliberately have no such panel — and the
    /// library row this walk drills into is, on any TV library, a show. Without this step the table, its
    /// row template and 「打开就是展开的」 would all be markup nothing ever ran. A page that is already a
    /// file's (a film) is left where it is; a show with no episode strip to click means both this step and
    /// the next quietly do nothing, and the report says so.
    /// </para>
    /// </summary>
    private static bool OpenFilePage(ShellPage shell)
    {        if (_fileOpened || shell.Pages.Content is not DetailPage page) return false;

        _fileOpened = true;
        if (EmbyItemType.IsPlayable(page.ViewModel.ItemType)) return false;

        return page.OpenFirstEpisode() is not null;
    }

    /// <summary>
    /// Sends that page down to 媒体信息, for the same reason <see cref="ScrollDetail"/> exists: the table is
    /// an <c>ItemsRepeater</c> below the fold, and a repeater builds only what its viewport covers. Read on
    /// arrival it has drawn nothing at all.
    /// <para>
    /// 单集's shape is read here first, for the mirror of that reason: on this page the shelf is at the top,
    /// in view, and this scroll is what takes it out of view. It is the 集 page's own reading — the one
    /// <see cref="_detail"/> cannot hold, because that snapshot was taken on the show's page.
    /// </para>
    /// </summary>
    private static bool ShowInfo(ShellPage shell)
    {
        if (_infoScrolled || shell.Pages.Content is not DetailPage page) return false;

        _infoScrolled = true;

        var (rows, cards) = page.EpisodeShapes;
        var (screenOk, screen) = page.FirstScreenRead();
        _fileEpisodesDrawn = (
            page.ViewModel.ItemType,
            page.ViewModel.EpisodeShelf?.Cards.Count ?? 0,
            rows,
            cards,
            screenOk,
            screen);
        _fileArtwork = (page.ViewModel.ItemType, ReadArtwork(page, page.ViewModel));

        // 这一行也只有在这儿量得到：三个下拉都在头图尾巴上，滚下去看媒体信息表格之后它们就出了视口。
        var (fitOk, fit) = page.PickerFit();
        _filePickers = (page.ViewModel.ItemType, fitOk, fit);

        // 同一拍：剧名那一行也在头图里，滚下去就出了视口。
        var (linkOk, linkRead) = page.TitleLinkRead();
        _fileTitleLink = (page.ViewModel.ItemType, linkOk, linkRead);

        page.ScrollToInfo();
        return true;
    }

    /// <summary>
    /// Whichever page is really in front of the user: the settings window's when that window is up, the
    /// frame's otherwise.
    /// <para>
    /// 设置、服务器 and 诊断 all live in that window now (需求 1 和 2), and opening it does not disturb the
    /// frame — so the three stages that used to read <c>Pages.Content</c> would read whatever page the walk
    /// left behind there, find it settled, and report on it instead.
    /// </para>
    /// </summary>
    private static object? Face(ShellPage shell) => shell.SettingsContent ?? shell.Pages.Content;

    /// <summary>
    /// Whether the shell has stopped moving: either it is asking for a password, or whichever page it
    /// is on has finished its requests. Anything else means a report would be measuring a half-built
    /// page.
    /// </summary>
    private static bool Settled(ShellPage shell) => shell.SignInVisible
        ? !shell.SignInBusy
        : Face(shell) switch
        {
            HomePage home => home.IsReady,
            LibraryPage library => library.IsReady,
            DetailPage detail => detail.IsReady,
            ServersPage servers => servers.IsReady,
            DiagnosticsPage diagnostics => diagnostics.IsReady,
            SettingsPage settings => settings.IsReady && settings.FontsReady,

            // 需求 8's console, which is the slowest thing the walk waits on: an embedded browser to bring up,
            // a page to load off the server and one look at what the web client made of the seeded session. It
            // reports ready on every ending, the failures and its own 15-second give-up included, so this gate
            // cannot be the thing that hangs.
            DashboardPage dashboard => dashboard.IsReady,
            _ => false
        };
}

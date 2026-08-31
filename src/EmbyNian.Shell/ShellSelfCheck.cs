using System.Runtime.InteropServices;
using System.Text;
using EmbyNian.Configuration;
using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Infrastructure;
using EmbyNian.Playback;
using EmbyNian.Services;
using EmbyNian.Shell.Diagnostics;
using EmbyNian.Shell.Interop;
using EmbyNian.Shell.Platform;
using EmbyNian.Shell.ViewModels;
using EmbyNian.Shell.Views;
using EmbyNian.Shell.Windowing;
using EmbyNian.Theming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

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
/// </summary>
internal static class ShellSelfCheck
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
    private static (bool Correct, int Cards, string Shelves, string Banner, bool TypeOk, string Type, bool BleedOk,
        string Bleed)? _home;

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
    private static (string Type, int Rows, int Cards)? _fileEpisodesDrawn;

    /// <summary>
    /// 需求 4's name plate on the page of a file, kept apart from <see cref="_detail"/>'s reading for the
    /// reason above: it belongs to a different page. It is also the more interesting of the two — an episode
    /// almost never has a 徽标 of its own, so this is the only page where the inherited one
    /// (<see cref="EmbyItem.ParentLogoItemId"/>) is ever reached for, and the only way to find out whether
    /// this server sends it without asking it by hand.
    /// </summary>
    private static (string Type, ArtworkRead Artwork)? _fileArtwork;

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
        string NextUp,
        string Remaining,
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
        bool WashOk,
        string Wash);

    /// <summary>
    /// 需求 4 on the detail page: 「把媒体的徽标…融入对应媒体的 ui 界面」 as the page really came out.
    /// </summary>
    /// <param name="Wanted">
    /// What this page had to make a name plate out of, in words — 「自己的徽标」, 「剧集的徽标」, 「无」. Worked
    /// out through <see cref="ItemArtwork.Plate"/>, so it is the app's own rule saying what the page meant to
    /// show rather than this file's guess at it.
    /// </param>
    /// <param name="Plate">
    /// Whether the 徽标 mark is on screen, and <paramref name="Text"/> whether the text title is. The text
    /// title is now unconditional and the mark merely optional — see 详情名牌 in <see cref="ReportDetail"/>.
    /// </param>
    /// <param name="Corner">
    /// Whether the mark really landed in the band's top-right corner, clear of the title —
    /// 「徽标移动到右上角」. True by vacuity on an item the server holds no 徽标 for; the geometry it was read
    /// from is in <paramref name="Where"/>.
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
        string Wanted,
        bool Plate,
        bool Text,
        double Width,
        double Height,
        bool Corner,
        string Where,
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
        page.ScrollToEnd();
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
    {
        if (_fileOpened || shell.Pages.Content is not DetailPage page) return false;

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
        _fileEpisodesDrawn = (page.ViewModel.ItemType, rows, cards);
        _fileArtwork = (page.ViewModel.ItemType, ReadArtwork(page, page.ViewModel));

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

    /// <summary>
    /// The home page's own state, read from wherever the frame happens to be. One reader rather than
    /// the same tuple built at both call sites: the check reads this twice — once before it walks off
    /// the page and once at report time, whichever came first — and two copies of the 「is it even the
    /// home page」 test is one copy too many.
    /// </summary>
    private static (bool Correct, int Cards, string Shelves, string Banner, bool TypeOk, string Type, bool BleedOk,
        string Bleed) ReadHome(ShellPage shell)
    {
        if (shell.Pages.Content is not HomePage home)
            return (false, -1, "未读取", "未读取", false, "未读取", false, "未读取");

        var (typeOk, type) = home.BannerType();
        var (bleedOk, bleed) = home.BleedRead();
        return (shell.CurrentTag == "home", home.LoadedCount, home.ShelfSummary, home.BannerSummary, typeOk, type,
            bleedOk, bleed);
    }

    /// <summary>
    /// 自检：标题栏那三颗系统按钮的墨，对不对得上顶上那一块的底。两头都要问 —— 图铺过去了就必须是那支固定
    /// 的浅墨，没铺（这台账号一张宽图都没有，顶上那一块是页面自己的底色）就必须还是主题的墨。
    /// <para>
    /// 只问这三颗，不问我们自己那五颗：那五颗在左端，站在什么上面跟着侧边栏走 —— 侧边栏收着（这是起手那一
    /// 档，「侧边栏默认为折叠状态」）时它们落在页面那张大图上，张开时又回到侧边栏自己的底色上。这两档的墨由
    /// <c>PaintTitleInk</c> 自己换，换的是 XAML 里的画刷，不是这里问的那份标题栏配色。系统这三颗在右端，侧边栏
    /// 再宽也到不了那儿，所以只有它们的墨要跟顶上那一块的底对。
    /// </para>
    /// </summary>
    private static (bool Ok, string Detail) ReadInk(ShellPage shell, HostWindow window)
    {
        if (window.TitleBarColours is not { Glyph: { } glyph }) return (false, "没有自定义标题栏可问颜色");

        var filled = shell.Pages.Content is HomePage home && home.HeroFilled;
        var pale = glyph.R == 0xF3 && glyph.G == 0xF5 && glyph.B == 0xF8;
        var shown = $"#{glyph.A:X2}{glyph.R:X2}{glyph.G:X2}{glyph.B:X2}";

        return (pale == filled, filled
            ? $"顶上是一张剧照，按钮图标 {shown}{(pale ? "（图上那支浅墨）" : "，却还是主题那支")}"
            : $"顶上是页面底色，按钮图标 {shown}{(pale ? "，却用了图上那支浅墨" : "（主题那支）")}");
    }

    /// <summary>
    /// 需求 6：what the cards on the home page offer under the pointer, and where those buttons sit.
    /// Null when the page realised no cards — an empty server is a working server.
    /// <para>
    /// Two cards answer the two questions: a 媒体库 card, which must offer neither 已观看 nor 收藏
    /// (「主页的媒体库不要收藏和已观看」) and cannot offer 播放 either, and the first playable card, whose
    /// play button has to be dead centre on its cover (「把播放按钮移动到媒体封面的中心」). 更多 is on both:
    /// it is the menu, and a library still has 打开 and 编辑元数据 in it.
    /// </para>
    /// <para>
    /// Both cards then answer the same three questions about the icon group: that it is in the cover's
    /// bottom-right corner (「把已播放、收藏、更多移动到封面的右下角」), that nothing behind the glyphs is
    /// painted in any state (「去掉黑色和灰色的边框，仅保留白色的图标按钮」), and that the card can find out
    /// where the cursor really is — which is the whole of 「鼠标移出窗口后不会自动恢复」, because the events
    /// alone cannot tell that departure from a press, and a card that cannot ask the OS keeps its buttons
    /// up over a cover the pointer left minutes ago with nothing on screen to say why.
    /// </para>
    /// </summary>
    private static (bool Ok, string Detail)? ReadCards(ShellPage shell)
    {
        if (shell.Pages.Content is not HomePage home) return null;

        var cards = home.RealisedCards();
        if (cards.Count == 0) return null;

        var library = Probe(cards, item => item.Type == EmbyItemType.CollectionFolder);
        var playable = Probe(cards, item => item.IsPlayable);

        var ok = library is null || (!library.Play && !library.Watched && !library.Favorite && library.More);

        // Centred to the pixel, not to the eye: half a card's width off would still read as 「middle-ish」
        // in a screenshot. One logical pixel of slack is for the odd artwork height a rounded 16:9 gives.
        if (playable is { } play)
        {
            ok &= play.Play && play.Watched && play.Favorite && play.More
                && Math.Abs(play.OffsetX) <= 1 && Math.Abs(play.OffsetY) <= 1 && play.Size > 0;
        }

        // 贴角，同样是像素的说法：一组居中的按钮和一组贴在右下角的按钮，在截图里差着半张封面。上限给到 16
        // 像素，是那 6,4 的外边距加上封面自己那圈 1 像素描边还有得剩；下限是 0，越出封面就不叫贴在角上了。
        // 底色则要三个态都问过才算数 —— 悬停那一态的灰片是框架给的，不在这张卡的标记里。指针那一问是第三件：
        // 那十赫兹的一拍要是量不到光标和封面的相对位置，它就是白跑，而白跑没有任何症状。
        //
        // 后三问是胶片格那圈线（重绘单元 4）：静止不是强调色、指针上来正好是调色板里那个强调色、键盘那一半
        // 找到了外层容器。三样都是看不出来的 —— 1 像素的线在截图里认不出色号，而焦点那一半在自检里根本没有
        // 焦点可看。
        var accent = ColorOf("EgAccentBrush");

        foreach (var group in new[] { library, playable })
        {
            if (group is not { } corner) continue;

            ok &= !corner.Chrome
                && corner.InsetRight is > 0 and <= 16
                && corner.InsetBottom is > 0 and <= 16
                && corner.Watch
                && Format(corner.FrameActive) == accent
                && Format(corner.FrameRest) != accent
                && corner.FrameRest.A > 0
                && corner.FrameFocus;
        }

        return (ok, $"{Describe("媒体库卡片", library)}；{Describe("可播放卡片", playable)}");

        static CardHoverProbe? Probe(IReadOnlyList<PosterCard> cards, Func<EmbyItem, bool> wanted)
        {
            foreach (var card in cards)
            {
                if (card.Card is { } item && wanted(item.Item)) return card.ProbeHover();
            }

            return null;
        }

        static string Describe(string what, CardHoverProbe? probe) => probe is not { } card
            ? $"{what} 未出现"
            : $"{what}「{card.Title}」封面 {card.ArtWidth:0}×{card.ArtHeight:0}：播放 {Had(card.Play)}"
                + (card.Play ? $"（{card.Size:0} 像素圆按钮，中心偏封面中心 {card.OffsetX:0.#},{card.OffsetY:0.#}）" : "")
                + $"、已观看 {Had(card.Watched)}、收藏 {Had(card.Favorite)}、更多 {Had(card.More)}"
                + $"，图标组离封面右下 {card.InsetRight:0.#},{card.InsetBottom:0.#} 像素、"
                + (card.Chrome ? "仍有按钮画底色" : "三态都不画底色")
                + (card.Watch ? "、指针位置查得出来" : "、查不到指针位置")
                + $"，胶片格 {Format(card.FrameRest)} → 指针 {Format(card.FrameActive)}"
                + (card.FrameFocus ? "、焦点接在外层容器上" : "、找不到能接焦点的容器");

        static string Had(bool present) => present ? "有" : "无";
    }

    /// <summary>
    /// The library page's state, including the two surfaces built by its own handlers. Null when the
    /// frame is not on a library, which is what a server with no libraries looks like.
    /// </summary>
    private static LibraryState? ReadLibrary(ShellPage shell)
    {
        if (shell.Pages.Content is not LibraryPage library) return null;

        // Before the sizes below, and the order matters: the walk puts each view shape on the grid and then
        // puts back the one it found, so everything read after it is the restored page.
        var (views, restored) = library.WalkViews();

        var (setting, card, cellWidth, cellHeight) = library.Sizing;

        return new LibraryState(
            library.IsReady,
            library.HeadingText,
            library.LoadedCount,
            library.SortPreset,
            library.SortKey,
            library.SortDescending,
            library.BuildSortMenu(),
            library.BuildFilterPanel(),
            // After BuildFilterPanel, and arguments are evaluated left to right: there are no blocks to
            // ask about until the panel has been built.
            library.FilterTemplatesResolve,
            library.Filters.IsEmpty ? "无" : library.Filters.Describe(),
            setting,
            card,
            cellWidth,
            cellHeight,
            library.ViewName,
            views,
            restored,
            library.AlphaShown,
            library.ArtworkCensus,
            library.ContainerHalo());
    }

    /// <summary>
    /// The servers page's state, both counts taken while it is still the page on screen. Null when the
    /// walk never got onto it.
    /// </summary>
    private static ServersState? ReadServers(ShellPage shell)
    {
        if (Face(shell) is not ServersPage servers) return null;

        var (bound, boundAccounts, cache) = servers.Summary;
        var (drawn, drawnAccounts) = servers.Realised;
        return new ServersState(servers.IsReady, bound, boundAccounts, cache, drawn, drawnAccounts);
    }

    /// <summary>
    /// The diagnostics page's state, taken while it is still the page on screen. Null when the walk never
    /// got onto it.
    /// </summary>
    private static DiagnosticsState? ReadDiagnostics(ShellPage shell)
    {
        if (Face(shell) is not DiagnosticsPage diagnostics) return null;

        var (rows, categories, following, launch) = diagnostics.Summary;
        return new DiagnosticsState(diagnostics.IsReady, rows, categories, following, launch, diagnostics.Realised);
    }

    /// <summary>
    /// The detail page's state: what it put on screen, what each of the four pickers settled on, and what
    /// the two card rows drew. Null when the walk never opened one.
    /// <para>
    /// The pickers are the reason this stage exists. Each one's list is rebuilt from scratch every time the
    /// item, the season or the media source changes, and a realised <c>ComboBox</c> answers that by writing
    /// <c>null</c> back through its two-way binding — so 「the selection is one of the rows in its own
    /// list」 is a claim that can only be tested on a page that has really been drawn and really been
    /// through those rebuilds. <see cref="DetailState.Chosen"/> is that claim.
    /// </para>
    /// </summary>
    private static DetailState? ReadDetail(ShellPage shell)
    {
        if (shell.Pages.Content is not DetailPage page) return null;

        var model = page.ViewModel;

        // Episodes counted across both scroll positions, cast at whichever one this is. The list at the top
        // and the strip at the bottom cannot both be on screen, and an ItemsRepeater unbuilds what leaves
        // its viewport — so the pre-scroll reading taken in ScrollDetail is the one that has seen 单集, and
        // this one is the only one that ever sees 演职人员. Both of 单集's shapes are carried separately: the
        // hidden one realises nothing, and that zero is the evidence this page kind picked the other.
        var (rows, cards) = page.EpisodeShapes;
        var cast = page.Realised.Cast;

        // Empty is a legitimate answer for every one of these — a movie has no seasons, an item with no
        // cast has no faces — so what is being tested is that a list which does have rows has settled on
        // one of them, never on something that is not in it.
        var chosen = Holds(model.Seasons, model.SelectedSeason)
            && Holds(model.Sources, model.SelectedSource)
            && Holds(model.AudioTracks, model.SelectedAudio)
            && Holds(model.SubtitleTracks, model.SelectedSubtitle);

        var (typeOk, type) = page.HeroType();
        var (fillOk, fill) = page.HeroFill();
        var (sealOk, seal) = page.BodySeal();
        var (washOk, wash) = page.WashRead(shell.TrailBase);

        return new DetailState(
            page.IsReady,
            model.ItemType,
            model.Title ?? string.Empty,
            model.Overview?.Length ?? 0,
            model.Facts ?? string.Empty,
            model.Seasons.Count,
            model.Sources.Count,
            model.AudioTracks.Count,
            model.SubtitleTracks.Count,
            chosen,
            $"季「{model.SelectedSeason?.Name ?? "无"}」、源「{model.SelectedSource?.Text ?? "无"}」、"
                + $"音频「{model.SelectedAudio?.Text ?? "无"}」、字幕「{model.SelectedSubtitle?.Text ?? "无"}」",
            model.PlayTarget?.Name,
            model.PlayTarget?.Type,
            model.PlayText,
            model.NextUpText,
            model.NextUpRemaining,
            model.InfoRows.Count,
            model.HeroImage is not null,
            model.StillImage is not null,
            ReadArtwork(page, model),
            model.EpisodeShelf?.Cards.Count ?? 0,
            model.CastShelf?.Cards.Count ?? 0,
            Math.Max(rows, _detailEpisodesDrawn.Rows),
            Math.Max(cards, _detailEpisodesDrawn.Cards),
            cast,
            typeOk,
            type,
            fillOk,
            fill,
            sealOk,
            seal,
            washOk,
            wash);

        // A list with rows has to point at one of them; an empty list has to point at nothing.
        static bool Holds<T>(IReadOnlyList<T> rows, T? chosen) where T : class =>
            rows.Count == 0 ? chosen is null : chosen is not null && rows.Contains(chosen);
    }

    /// <summary>
    /// 需求 4 as this page came out: the 徽标 the item's artwork entitles it to, whether the mark or only the
    /// text title actually drew, where the mark landed, and which of the five artworks the server holds for it —
    /// plus whose picture the band behind it stands on（「集页面要用这个剧的背景图或缩略图」）.
    /// <para>
    /// Read off the elements rather than off the view model, because what is worth testing is the markup: the
    /// text title has to be there on every page (the mark used to replace it, and 「no name on the hero band」
    /// looks exactly like artwork that never arrived over the network), and the mark has to be in the corner it
    /// was moved to rather than back on the words. See <see cref="DetailPage.TitleShapes"/>.
    /// </para>
    /// </summary>
    private static ArtworkRead ReadArtwork(DetailPage page, DetailViewModel model)
    {
        var (plate, text, width, height, corner, where) = page.TitleShapes;
        var item = model.CurrentItem;
        var (heroOk, hero) = Band(item);

        return new ArtworkRead(Wanted(item), plate, text, width, height, corner, where,
            item is null ? "没有条目" : ItemArtwork.Kinds(item), hero, heroOk);

        // The id matters as much as the kind: on an episode or a season the logo is the show's, fetched
        // under the show's id, and 「徽标」 on its own would not say which of the two paths ran.
        static string Wanted(EmbyItem? item) => ItemArtwork.Plate(item) is not { } plate
            ? "无（只有文字标题可用）"
            : plate.ItemId == item!.Id
                ? $"自己的{ItemArtwork.Name(plate.ImageType)}"
                : $"剧集的{ItemArtwork.Name(plate.ImageType)}（取自条目 {plate.ItemId}）";

        // 同上一句，问的是铺在背后那张图 —— 「集页面要用这个剧的背景图或缩略图」。判据里的 Inherited 是「服务器
        // 发下来了什么」，所以这句话在一台没有剧集宽幅图的服务器上空着成立，而不是报一条修不了的失败。
        static (bool Ok, string Detail) Band(EmbyItem? item)
        {
            if (item is null) return (true, "没有条目");

            var picks = ItemArtwork.Hero(item);
            if (picks.Count == 0) return (true, "无（这一条一张宽幅图都没有，头上那一格退到没有画面的那一档）");

            var first = picks[0];
            var borrowed = item.Type is EmbyItemType.Episode or EmbyItemType.Season
                && ItemArtwork.Inherited(item).Count > 0;

            return (!borrowed || first.ItemId != item.Id,
                first.ItemId == item.Id
                    ? $"头图是自己的{ItemArtwork.Name(first.ImageType)}"
                    : $"头图是剧集的{ItemArtwork.Name(first.ImageType)}（取自条目 {first.ItemId}）");
        }
    }

    /// <summary>
    /// 媒体信息 as the page has it drawn, taken while that page is still in the frame. Null when the frame
    /// is not holding a file's page, which is when there was nothing to click through to.
    /// </summary>
    private static InfoState? ReadInfo(ShellPage shell)
    {
        if (shell.Pages.Content is not DetailPage page) return null;

        var model = page.ViewModel;
        if (!EmbyItemType.IsPlayable(model.ItemType)) return null;

        // 顺路的一句，见 _fileHero：这一页的头图取的是剧集那一层的标签，取回来了没有只有真服务器答得出，而这里是
        // 这一页上最后几眼里的一眼。
        _fileHero = model.HeroImage is not null;

        var (drawn, expanded, first) = page.InfoRealised;
        return new InfoState(
            model.ItemType,
            model.Title ?? string.Empty,
            model.InfoRows.Count,
            drawn,
            expanded,
            string.Concat(model.InfoRows.Select(row => row.Label)),
            first);
    }

    /// <summary>
    /// 切换季 and 跨季下一集, asked of the real server rather than of a hand-built list.
    /// <para>
    /// Two claims live here that no fixture can make. The first is that stepping off the end of a season
    /// lands on the next season's first episode — which holds only because the server's own ordering groups
    /// episodes by season index, a property of the server and not of <see cref="EpisodeNavigation"/>. The
    /// second is that the season list a step hands the player's 选集 menu is the very list the server
    /// returns when asked for that season directly, id for id and in the same order.
    /// </para>
    /// <para>
    /// Awaited from the timer's last tick, once it has stopped and before the report is written: the rest of
    /// the walk is synchronous, and this is the one check with round trips of its own. The detail half runs
    /// on a <see cref="DetailViewModel"/> built here rather than on the visible frame, because that frame is
    /// holding the settings page the report is about to read.
    /// </para>
    /// <para>
    /// Nothing outside this build is allowed to cost a failure: not signed in, no show with two seasons on
    /// disk, and a round trip that throws are all reported as 「信息」.
    /// </para>
    /// </summary>
    private static async Task<SeasonState> ProbeSeasonsAsync(ShellPage shell, IServiceProvider services)
    {
        var session = services.GetRequiredService<EmbySession>();
        if (!session.IsSignedIn) return new SeasonState("未登录");

        // A budget of its own. The walk's tick deadline has already been spent by the time this runs, and a
        // server that hangs here would hold the report file at 「未完成」 rather than report one failure.
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(40));

        try
        {
            var found = await session.ExecuteAsync((client, token) => client.GetItemsAsync(new ItemQuery
            {
                Recursive = true,
                IncludeItemTypes = [EmbyItemType.Series],
                Limit = 200
            }, token), budget.Token).ConfigureAwait(true);

            // ChildCount is the season count, so this narrows to 「shows that could have a boundary」 without
            // a round trip each. Capped because every candidate past the first costs two more.
            var candidates = found.Items.Where(series => series.ChildCount is > 1).Take(6).ToList();

            foreach (var series in candidates)
            {
                var episodes = await session
                    .ExecuteAsync((client, token) => client.GetEpisodesAsync(series.Id, null, token), budget.Token)
                    .ConfigureAwait(true);

                if (Boundary(episodes) is not { } boundary) continue;

                var last = episodes[boundary];
                var first = episodes[boundary + 1];

                var forward = EpisodeNavigation.Step(episodes, last.Id, 1);
                var back = forward is null ? null : EpisodeNavigation.Step(episodes, forward.Episode.Id, -1);

                var scoped = await session
                    .ExecuteAsync((client, token) => client.GetEpisodesAsync(series.Id, first.SeasonId, token),
                        budget.Token)
                    .ConfigureAwait(true);

                var seasonal = forward is not null
                    && forward.Siblings.Select(sibling => sibling.Id)
                        .SequenceEqual(scoped.Select(episode => episode.Id), StringComparer.Ordinal);

                var crossOk = forward?.Episode.Id == first.Id && back?.Episode.Id == last.Id && seasonal;

                var cross = $"《{series.Name}》{Code(last)} → 下一集 {Code(forward?.Episode)}（应为 {Code(first)}）"
                    + $"→ 上一集 {Code(back?.Episode)}；选集 {forward?.Siblings.Count ?? 0} 集，"
                    + $"服务端按季返回 {scoped.Count} 集，{(seasonal ? "两者逐个相同" : "两者不一致")}";

                var picker = await ProbePickerAsync(
                    shell, services, session, series, last.SeasonId, first.SeasonId, budget.Token)
                    .ConfigureAwait(true);

                return new SeasonState(null, crossOk, cross, picker.Ok, picker.Detail);
            }

            return new SeasonState($"{candidates.Count} 部多季剧集里没有可走的季边界");
        }
        catch (Exception error)
        {
            Log.Warn(Category, "季自检探针出错", error);
            return new SeasonState($"探针没跑完（{Failure.Describe(error)}）");
        }
    }

    /// <summary>
    /// Where a season ends in a whole-show episode list: the index of the last episode before the season
    /// changes, or null when there is no boundary to walk — one season on disk, or a server that put no
    /// season id on the episodes. The destination's id has to be a real one, because the check compares
    /// against the list the server returns for exactly that season.
    /// </summary>
    private static int? Boundary(IReadOnlyList<EmbyItem> episodes)
    {
        for (var index = 0; index + 1 < episodes.Count; index++)
        {
            if (episodes[index].SeasonId is not { Length: > 0 } here) continue;
            if (episodes[index + 1].SeasonId is not { Length: > 0 } next) continue;

            if (!string.Equals(here, next, StringComparison.Ordinal)) return index;
        }

        return null;
    }

    /// <summary>How an episode is named in the report. Falls back to its title for a special with no code.</summary>
    private static string Code(EmbyItem? episode) => episode is null
        ? "没有"
        : episode.EpisodeCode is { Length: > 0 } code ? code : episode.Name;

    /// <summary>
    /// The 季 picker, driven the way a person drives it: a detail page is loaded on the show and then handed
    /// another season, which is the one assignment the ComboBox's two-way binding makes.
    /// <para>
    /// Three things then have to have followed the picker — the rows, the heading above them and what 播放
    /// would start. The heading is worth its own clause because it is built from a scope rather than from the
    /// rows (see <c>ItemDetail.EpisodeScope</c>), so a page that fetched the right episodes under the
    /// previous season's name would otherwise pass. The 下一集 line is checked here for a fourth reason: it
    /// is the one thing on the page that has to be *announced* rather than merely correct when read.
    /// </para>
    /// </summary>
    /// <returns>Ok null when this show cannot answer the question, which is reported rather than failed.</returns>
    private static async Task<(bool? Ok, string Detail)> ProbePickerAsync(
        ShellPage shell,
        IServiceProvider services,
        EmbySession session,
        EmbyItem series,
        string? crossedFrom,
        string? crossedInto,
        CancellationToken token)
    {
        var seasons = await session
            .ExecuteAsync((client, ct) => client.GetSeasonsAsync(series.Id, ct), token).ConfigureAwait(true);

        using var page = new DetailViewModel();
        page.Attach(
            DetailRequest.For(services, series),
            services.GetRequiredService<IShellActions>(),
            services.GetRequiredService<ISettingsService>(),
            session,
            services.GetRequiredService<EmbyImageStore>(),
            services.GetRequiredService<ISystemLauncher>());

        await page.ReloadAsync().ConfigureAwait(true);

        var opened = page.SelectedSeason;
        var expected = ItemDetail.PickSeason(seasons);

        // The page's own choice against the rule applied to a list fetched separately — the same question
        // the poster on the home page answers, which is why the two have to agree.
        var openedOk = opened is not null
            && expected is not null
            && opened.Id == expected.Id
            && page.Seasons.Count == seasons.Count;

        // Whichever side of the boundary above the page did not open on. Both sides are known to hold
        // episodes — each contributed one to the list the boundary was found in — so 「no episodes」 and
        // 「the switch never happened」 stay two different readings. A show whose picker holds only the
        // season it opened on cannot answer this, and says so instead of failing.
        var target = page.Seasons.FirstOrDefault(season => season.Id == crossedInto && season.Id != opened?.Id)
            ?? page.Seasons.FirstOrDefault(season => season.Id == crossedFrom && season.Id != opened?.Id);

        if (target is null)
            return (null, $"季 {page.Seasons.Count} 行，边界两侧的季都不在选择器里（打开在「{opened?.Name ?? "无"}」）");

        // Which properties the switch announces, not merely which values it ends up with. 下一集 is a
        // computed property fanned out from PlayTarget by an attribute, and reading it after the fact would
        // pass whether or not that attribute is there — the page binds once and then listens.
        var announced = new HashSet<string>(StringComparer.Ordinal);
        page.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is { Length: > 0 } name) announced.Add(name);
        };

        page.SelectedSeason = target;

        // Waits for that load rather than for a guessed interval: the assignment runs the picker's hook
        // synchronously, and BeginLoad has already raised Busy by the time it comes back.
        for (var waited = 0; page.Busy && waited < 100; waited++)
            await Task.Delay(100, token).ConfigureAwait(true);

        var rows = page.Episodes;
        var strayed = rows.Count(episode => episode.SeasonId != target.Id);
        var heading = page.EpisodeShelf?.Title ?? "";
        var wanted = ItemDetail.PickEpisode(rows);

        var switched = openedOk
            && !page.Busy
            && page.SelectedSeason?.Id == target.Id
            && rows.Count > 0
            && strayed == 0
            && heading.Contains(target.Name, StringComparison.Ordinal)
            && wanted is not null
            && page.PlayTarget?.Id == wanted.Id
            && page.PlayTarget?.SeasonId == target.Id
            && page.NextUpText.Contains(wanted.Name, StringComparison.Ordinal)
            && announced.Contains(nameof(DetailViewModel.NextUpText));

        return (switched,
            $"《{series.Name}》季 {page.Seasons.Count} 行，打开在「{opened?.Name ?? "无"}」"
                + $"（规则应为「{expected?.Name ?? "无"}」）；切到「{target.Name}」后 {rows.Count} 集"
                + $"{(strayed > 0 ? $"，其中 {strayed} 集不属于该季" : "，全部属于该季")}，"
                + $"标题「{heading}」，播放目标「{page.PlayTarget?.Name ?? "无"}」{Code(page.PlayTarget)}，"
                + $"下一集「{page.NextUpText}」"
                + (announced.Contains(nameof(DetailViewModel.NextUpText)) ? "（已通知）" : "（**没有通知**）"));
    }

    /// <summary>
    /// The frame as the window itself reports it: how far below the window's top edge the client area
    /// starts, how big that client area is, and what the frame answers for four points along the top. All
    /// physical pixels, all asked of the live window — 「the picture reaches the top edge」 is a claim about
    /// the frame, and reading it off the style bits is what let a seven-pixel black band ship.
    /// </summary>
    private static (int TopInset, int Width, int Height, string Hits, bool HitsOk) FrameGeometry(IntPtr window)
    {
        Native.GetWindowRect(window, out var outer);
        Native.GetClientRect(window, out var client);

        var origin = new NativePoint { X = 0, Y = 0 };
        Native.ClientToScreen(window, ref origin);

        var midX = outer.Left + outer.Width / 2;
        var edge = Hit(midX, outer.Top + 1);
        var strip = Hit(midX, outer.Top + 10);
        var below = Hit(midX, outer.Top + 40);
        var corner = Hit(outer.Right - 30, outer.Top + 16);

        // The top row still resizes the window, and everything below it belongs to the page: the strip it
        // draws its own three window commands into, and the corner where the system's would otherwise sit.
        var ok = edge == Native.HitTop
            && strip == Native.HitClient
            && below == Native.HitClient
            && corner == Native.HitClient;

        return (origin.Y - outer.Top, client.Width, client.Height,
            $"顶+1 {Name(edge)}、顶+10 {Name(strip)}、顶+40 {Name(below)}、右上角 {Name(corner)}", ok);

        int Hit(int x, int y) => (int)(long)Native.SendMessage(
            window, Native.WmNcHitTest, IntPtr.Zero, new IntPtr(((y & 0xFFFF) << 16) | (x & 0xFFFF)));

        static string Name(int code) => code switch
        {
            Native.HitClient => "客户区",
            Native.HitCaption => "标题栏",
            Native.HitTop => "上边框",
            _ => $"码 {code}"
        };
    }

    /// <summary>
    /// 窗口在客户区某一点上答什么。逻辑像素进、命中码出，缩放和客户区原点都在里面算掉：<c>WM_NCHITTEST</c>
    /// 收的是屏幕坐标，而这份自检里问的每个点都是从外壳量出来的。
    /// </summary>
    private static int HitAt(HostWindow window, double x, double y)
    {
        var dpi = Native.GetDpiForWindow(window.Handle);
        if (dpi == 0) dpi = 96;
        var scale = (int)dpi / 96.0;

        var origin = new NativePoint { X = 0, Y = 0 };
        Native.ClientToScreen(window.Handle, ref origin);

        var px = origin.X + (int)(x * scale);
        var py = origin.Y + (int)(y * scale);

        return (int)(long)Native.SendMessage(
            window.Handle, Native.WmNcHitTest, IntPtr.Zero, new IntPtr(((py & 0xFFFF) << 16) | (px & 0xFFFF)));
    }

    private static string HitName(int code) => code switch
    {
        Native.HitClient => "客户区",
        Native.HitCaption => "标题栏",
        Native.HitTop => "上边框",
        Native.HitMinButton => "最小化按钮",
        Native.HitMaxButton => "最大化按钮",
        Native.HitClose => "关闭按钮",
        _ => $"码 {code}"
    };

    /// <summary>
    /// 右上角那三颗系统按钮：最小化、最大化、关闭。问的是**窗框**，因为会坏的那半在窗框 —— 按钮是框架自己画
    /// 的，画出来不代表按得动。客户区上的区域声明只要盖过它们那一块，或者把它们那三种区域也一并声明成「什么
    /// 都不占」，按钮就只剩一张图：指针落下去答的是客户区，点击交给底下那层 XAML，然后什么也不发生
    /// （「右上角的最小化 最大化 关闭 点不了」）。
    /// <para>
    /// 三个点是从框架自己报的右侧留白里算的（等分三格，最右边那格是关闭），所以换了 DPI 或者哪天按钮宽度变了
    /// 这条检查照样问得准。
    /// </para>
    /// </summary>
    private static (bool Ok, string Detail) SystemButtons(HostWindow window)
    {
        var (barHeight, inset) = window.TitleBarMetrics;
        if (inset <= 0) return (false, "框架没有报出右侧留白 —— 浏览态本该有三颗系统按钮");

        var (minimise, maximise, close) = CaptionButtonHits(window, inset, barHeight);

        var ok = close == Native.HitClose
            && maximise == Native.HitMaxButton
            && minimise == Native.HitMinButton;

        return (ok, $"右侧留白 {inset:0} 物理像素分三格：最小化 {HitName(minimise)}、"
            + $"最大化 {HitName(maximise)}、关闭 {HitName(close)}");
    }

    /// <summary>
    /// 右侧留白等分三格，每格中点问一次 <c>WM_NCHITTEST</c>，从左到右是最小化、最大化、关闭。留白宽度和标题栏
    /// 高度单独传进来，因为播放态框架把这两项都报成 0 —— 那时候要问的仍是浏览态那三块地方现在答什么。
    /// </summary>
    private static (int Minimise, int Maximise, int Close) CaptionButtonHits(
        HostWindow window, double inset, double barHeight)
    {
        var dpi = Native.GetDpiForWindow(window.Handle);
        if (dpi == 0) dpi = 96;
        var scale = (int)dpi / 96.0;

        Native.GetClientRect(window.Handle, out var client);

        var right = client.Width / scale;
        var column = inset / scale / 3;
        var middle = (barHeight > 0 ? barHeight / scale : 32) / 2;

        return (
            HitAt(window, right - (column * 2.5), middle),
            HitAt(window, right - (column * 1.5), middle),
            HitAt(window, right - (column / 2), middle));
    }

    /// <summary>
    /// 「点击其他窗口或桌面后会变色」：the four colours the custom title bar wears beside the four it wears
    /// once the user clicks something else. Each pair has to match and none of the eight may be unset — an
    /// unset inactive colour is not the active one, it is whatever the system thinks an inactive caption
    /// looks like, which under a light system theme is a pale strip across the top of a window whose every
    /// other pixel is #16181C.
    /// <para>
    /// Asked of the properties rather than of the screen, because seeing it needs the focus taken off this
    /// window — and a self-check that hands the foreground to somebody else has already broken every probe
    /// below it that measures what is in front.
    /// </para>
    /// </summary>
    private static (bool Ok, string Detail) ReportTitleBarColours(HostWindow window)
    {
        if (window.TitleBarColours is not { } bar) return (false, "没有自定义标题栏可问颜色");

        var same = Same(bar.Fill, bar.InactiveFill)
            && Same(bar.Text, bar.InactiveText)
            && Same(bar.Button, bar.InactiveButton)
            && Same(bar.Glyph, bar.InactiveGlyph);

        return (same,
            $"底色 {Show(bar.Fill)}→{Show(bar.InactiveFill)}、文字 {Show(bar.Text)}→{Show(bar.InactiveText)}"
            + $"、按钮底 {Show(bar.Button)}→{Show(bar.InactiveButton)}"
            + $"、按钮图标 {Show(bar.Glyph)}→{Show(bar.InactiveGlyph)}"
            + (same ? "，失焦不变" : "，失焦会变"));

        // Field by field: a projected WinRT struct is not something to trust an operator to.
        static bool Same(Windows.UI.Color? active, Windows.UI.Color? inactive) =>
            active is { } a && inactive is { } b && a.A == b.A && a.R == b.R && a.G == b.G && a.B == b.B;

        static string Show(Windows.UI.Color? colour) =>
            colour is { } c ? $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}" : "未设";
    }

    /// <summary>
    /// 标题栏上那一排按键：外壳把它们摆成什么样，窗口在那块地方答什么。两件事一起问才算数 —— 那一排必须是
    /// 客户区（不然指针压下去变成拖窗口，按钮永远收不到点击），而同一条带子上这一排以外的地方必须还是标题栏，
    /// 否则挖洞挖成了把整条拖动区挖掉。窗口自己报的洞还要和量出来的那个矩形对得上：洞开歪了在屏幕上完全看不
    /// 出来，直到有人去点按键。
    /// </summary>
    /// <param name="captionBefore">
    /// 播放/浏览标题栏来回切之前，标题栏左端答的是什么。带进来是因为这份自检自己就切过一轮：切回浏览以后那
    /// 条拖动区必须还在，否则「看完一部片回到界面，标题栏就拖不动了」。
    /// </param>
    private static (bool Ok, string Detail) TitleBarKeys(HostWindow window, ShellPage shell, int captionBefore)
    {
        var probe = shell.ProbeTitleActions();

        var centre = HitAt(window, probe.X + probe.Width / 2, probe.Y + probe.Height / 2);

        // 洞左边那块留白，和洞右边 40 像素处：一进一出，证明挖掉的只有这一排所占的那一块。
        var edge = HitAt(window, 20, 16);
        var beyond = HitAt(window, probe.X + probe.Width + 40, probe.Y + probe.Height / 2);

        var hole = window.TitleBarHole;
        var matched = hole is { } rect
            && Math.Abs(rect.X - probe.X) <= 1
            && Math.Abs(rect.Y - probe.Y) <= 1
            && Math.Abs(rect.Width - probe.Width) <= 1
            && Math.Abs(rect.Height - probe.Height) <= 1;

        var ok = probe.Ok
            && centre == Native.HitClient
            && edge == Native.HitCaption
            && beyond == Native.HitCaption
            && captionBefore == Native.HitCaption
            && matched;

        return (ok, $"{probe.Detail}；整排中点 {HitName(centre)}、左端留白 {HitName(edge)}、"
            + $"右侧 40 像素处 {HitName(beyond)}（切播放前 {HitName(captionBefore)}）；"
            + $"窗口留的洞 {(hole is { } r ? $"({r.X:0},{r.Y:0}) {r.Width:0}×{r.Height:0}" : "没有")}"
            + $"，{(matched ? "和按键对得上" : "和按键对不上")}");
    }

    private static void Run(HostWindow window, ShellPage shell, IServiceProvider services, StartupOptions options)
    {
        var report = new StringBuilder();
        var failures = 0;

        report.AppendLine($"{AppIdentity.TitleWithVersion} — WinUI 3 外壳自检");
        report.AppendLine($"时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine($"数据目录：{options.Paths.Root}");
        report.AppendLine();

        void Check(string name, bool ok, string detail)
        {
            if (!ok) failures++;
            report.AppendLine($"[{(ok ? "通过" : "失败")}] {name} — {detail}");
        }

        // ---- the window and the island -------------------------------------------
        var (width, height) = window.ClientSize;
        Check("主窗口", window.Handle != IntPtr.Zero, $"hwnd=0x{window.Handle:X}");
        Check("XAML 岛", window.IslandHandle != IntPtr.Zero, $"hwnd=0x{window.IslandHandle:X}");
        Check("自定义标题栏", window.UsesCustomTitleBar, "InputNonClientPointerSource 已初始化");

        var caption = ReportTitleBarColours(window);
        Check("标题栏失焦不变色", caption.Ok, caption.Detail);

        Check("客户区尺寸", width > 0 && height > 0, $"{width}x{height} 物理像素");

        // 「锁定主页的窗口长宽」：客户区真是那个形状。这一条比它看着重要 —— 主页那条带的高度按宽算，裁掉剧照
        // 多少只由带子的比例决定，而带子的比例只有在窗口形状不变时才不变（见 HomeCarousel.HeightShare）。
        // 开关关掉时（BrowseAspect 是 0）就只报形状不判：那时窗口本来就随便拉。
        var clientShape = height > 0 ? (double)width / height : 0;
        Check("锁定窗口比例",
            window.BrowseAspect <= 0 || Math.Abs(clientShape - window.BrowseAspect) < 0.01,
            $"客户区 {clientShape:0.000}:1"
                + (window.BrowseAspect > 0
                    ? $"，锁在 {window.BrowseAspect:0.000}:1（带子因此正好 {HomeCarousel.Aspect:0.0}:1，"
                        + $"16:9 的剧照裁掉 {1 - (16d / 9) / HomeCarousel.Aspect:P0}，与窗口大小无关）"
                    : "，未锁定"));

        // 上面那一条量的是窗口现在的形状，这一条量的是「拖边沿的时候还保持这个形状」—— 两回事：形状对可以只是
        // 启动时摆对了一次（FitToShape），而 WM_SIZING 没接上的话，第一次拖边就散了。真拖一次要注入指针，这台
        // 机器上注入是被挡着的，所以这里直接把一条 WM_SIZING 送进窗口自己的消息处理里看它回什么。
        var dragLock = window.ProbeShapeLock();
        Check("拖边保持比例", dragLock.Ok, dragLock.Detail);

        // Exercise the same title-bar transition playback uses, and measure what it does to the frame rather
        // than what it does to the style bits. Dropping WS_CAPTION for playback is exactly how
        // 「播放页面标题栏最上方有一行黑色」 happened: with the caption gone and the resize frame kept, the top
        // border stops being client area and becomes seven physical pixels DWM paints black above the picture.
        // Read before anything below touches the title bar: this is the browsing drag region as the window
        // came up. The check itself is about to run the playback transition twice, and 「the strip still
        // drags the window afterwards」 is only a claim worth making if it dragged it to begin with.
        var captionAtStartup = HitAt(window, 20, 16);
        var metricsAtStartup = window.TitleBarMetrics;

        var wasPlaybackTitleBar = window.PlaybackTitleBar;
        window.PlaybackTitleBar = true;
        var playbackStyle = (long)Native.GetWindowLongPtr(window.Handle, Native.GwlStyle);
        var playbackFrame = FrameGeometry(window.Handle);
        var playbackTitleBar = window.TitleBarMetrics;
        var playbackTopRight = CaptionButtonHits(window, metricsAtStartup.RightInset, metricsAtStartup.Height);
        window.PlaybackTitleBar = false;
        var browsingStyle = (long)Native.GetWindowLongPtr(window.Handle, Native.GwlStyle);
        var browsingFrame = FrameGeometry(window.Handle);
        var browsingTitleBar = window.TitleBarMetrics;
        window.PlaybackTitleBar = wasPlaybackTitleBar;

        var frameBits = Native.WsCaption | Native.WsThickFrame;
        Check("窗口框架两面一致",
            (playbackStyle & frameBits) == frameBits && (browsingStyle & frameBits) == frameBits,
            $"播放 WS_CAPTION={(playbackStyle & Native.WsCaption) != 0}、WS_THICKFRAME={(playbackStyle & Native.WsThickFrame) != 0}；"
                + $"浏览 WS_CAPTION={(browsingStyle & Native.WsCaption) != 0}、WS_THICKFRAME={(browsingStyle & Native.WsThickFrame) != 0}；"
                + $"两面样式相同={playbackStyle == browsingStyle}");

        // The bug stated as a measurement: the client area has to start at the window's own top edge, be the
        // size the browsing frame gets, and answer 「客户区」 everywhere the strip draws — while the top row
        // still answers 「上边框」, or the window could no longer be resized from the top.
        Check("播放帧顶边贴齐",
            playbackFrame.TopInset == 0
                && playbackFrame.Width == browsingFrame.Width
                && playbackFrame.Height == browsingFrame.Height
                && playbackFrame.HitsOk,
            $"客户区 {playbackFrame.Width}x{playbackFrame.Height}（浏览帧 {browsingFrame.Width}x{browsingFrame.Height}）；"
                + $"顶边内缩 {playbackFrame.TopInset} 物理像素（浏览帧 {browsingFrame.TopInset}）；命中 {playbackFrame.Hits}");

        // Which is only safe because the framework's own caption is collapsed away: the window keeps
        // WS_CAPTION now, so 「no system buttons over the picture」 is a claim about the framework's title bar
        // rather than about the style bits, and this is the framework answering it.
        Check("播放时没有系统按钮",
            playbackTitleBar.Height == 0 && playbackTitleBar.RightInset == 0 && browsingTitleBar.RightInset > 0,
            $"播放 高 {playbackTitleBar.Height:0}、右侧留白 {playbackTitleBar.RightInset:0}；"
                + $"浏览 高 {browsingTitleBar.Height:0}、右侧留白 {browsingTitleBar.RightInset:0}");

        // 同一块地方，反过来问一次。收起框架的按钮只是不画了；那三格要是还归窗框，播放浮层自己画在那儿的
        // 最小化/最大化/关闭就同样点不动 —— 和浏览态那条 bug 是同一件事的两面。
        Check("播放时右上角归页面",
            playbackTopRight is (Native.HitClient, Native.HitClient, Native.HitClient),
            $"浏览态右侧留白 {metricsAtStartup.RightInset:0} 物理像素那三格，播放时答：最小化 "
                + $"{HitName(playbackTopRight.Minimise)}、最大化 {HitName(playbackTopRight.Maximise)}、"
                + $"关闭 {HitName(playbackTopRight.Close)}");

        var root = shell.XamlRoot;
        Check("XamlRoot", root is not null,
            root is null ? "为空" : $"{root.Size.Width:0}x{root.Size.Height:0} 逻辑像素，缩放 {root.RasterizationScale:0.##}");

        Check("外壳内容", ReferenceEquals(window.Content, shell), window.Content?.GetType().Name ?? "为空");
        Check("外壳已布局", shell.ActualWidth > 0 && shell.ActualHeight > 0,
            $"{shell.ActualWidth:0}x{shell.ActualHeight:0}");

        ReportContainer(services, report, Check);

        // ---- which of the two faces is up ------------------------------------------
        // Both are a pass. Whether this machine has a working saved token is not something the shell
        // controls, and a self-check that only passes when someone is logged in tests the network.
        report.AppendLine($"[信息] 登录状态 — {shell.SessionSummary}");

        ReportSignIn(report, Check, shell);

        var navigation = shell.NavigationRoot;

        if (shell.SignInVisible)
        {
            Check("登录卡片已布局", shell.SignInRoot.ActualWidth > 0 && shell.SignInRoot.ActualHeight > 0,
                $"{shell.SignInRoot.ActualWidth:0}x{shell.SignInRoot.ActualHeight:0}");
            Check("导航栏已隐藏", navigation.Visibility == Visibility.Collapsed, navigation.Visibility.ToString());
            Check("尚未打开页面", shell.Pages.Content is null, shell.Pages.Content?.GetType().Name ?? "为空");
        }
        else
        {
            var destinations = navigation.MenuItems.OfType<NavigationViewItem>().Count();
            var footer = navigation.FooterMenuItems.OfType<NavigationViewItem>().Count();
            var account = navigation.PaneFooter as FrameworkElement;

            // 主页 plus one per library on the server; the libraries are added at sign-in. 需求 2 emptied the
            // footer menu — 服务器 and 诊断 were its two entries and are settings categories now — and put the
            // account button in the pane's footer slot instead, so 0 here is the requirement, not an absence.
            Check("导航项", destinations >= 1 && footer == 0 && account is not null,
                $"主菜单 {destinations} 项（含 {shell.LibraryCount} 个媒体库），页脚菜单 {footer} 项，"
                + $"页脚控件 {account?.GetType().Name ?? "为空"}");

            // 需求 2 的前半句：「用户和媒体服务器去掉头像，然后移动到窗口左下方」. Measured against the shell
            // rather than trusted to the markup: the account button sitting in PaneFooter is what puts it at
            // the bottom of the pane, and the pane is the left edge, so 「bottom-left of the window」 is a
            // claim about where it landed once everything above it had been laid out.
            if (account is { ActualWidth: > 0, ActualHeight: > 0 })
            {
                var at = account.TransformToVisual(shell).TransformPoint(new Windows.Foundation.Point(0, 0));
                var corner = at.X < shell.ActualWidth / 2 && at.Y + account.ActualHeight >= shell.ActualHeight - 24;

                Check("账号在左下角", corner,
                    $"({at.X:0},{at.Y:0}) 起 {account.ActualWidth:0}×{account.ActualHeight:0}"
                    + $"，外壳 {shell.ActualWidth:0}×{shell.ActualHeight:0}"
                    + $"，{(corner ? "贴着左下角" : "不在左下角")}");
            }
            else
            {
                Check("账号在左下角", false, account is null ? "页脚里没有控件" : "页脚控件没有尺寸");
            }

            // Read from the snapshot when the check has since walked into a library: the frame holds one
            // page, so the home page's own state is gone by then.
            var home = _home ?? ReadHome(shell);

            Check("初始页面", home.Correct, home.Correct ? "HomePage，导航标签 home" : $"当前 {shell.CurrentTag ?? "无"}");

            // 媒体库 is drawn from the view list the shell already read, so it is the one row that is
            // there for an account which has never played anything — and the only proof the shelves
            // built at all, since the card count deliberately leaves that row out.
            Check("主页分区", home.Shelves.Contains("媒体库", StringComparison.Ordinal), home.Shelves);

            // Informational: an account whose server is empty is a working account.
            report.AppendLine($"[信息] 主页卡片 — {(home.Cards < 0 ? "未读取" : $"{home.Cards} 张")}");

            // 需求 5 的实测那一半：轮播只收得下带宽图的条目，而「这台服务器到底有没有把剧集的背景图发下来」只有
            // 真账号答得出 —— 这行读数里 剧集背景图 的个数就是那个答案（见 ItemArtwork.Census）。
            report.AppendLine($"[信息] 主页轮播 — {home.Banner}");

            // 单元 5：那条带上的版式。跟 头图版式 是同一条断言的两个落点 —— 主页轮播和详情页头图是整个界面上
            // 最大的两块画面，各自一份标记，谁漏一个 FontFamily 谁就悄悄退回正文字。
            Check("轮播版式", home.TypeOk, home.Type);

            // 「红框框出来的地方全填充上海报」：那一块从窗口的顶边量起，一直到右边沿。图有没有解码是上面
            // 那行读数的事，这一行只问那块地方铺满了没有 —— 顶上少让开的 32 像素在图上就是一道黑边。
            Check("主页大图贴边", home.BleedOk, home.Bleed);

            // 同一块地方的第二问：图铺到标题栏底下以后，那三颗窗口按钮站在剧照上，墨得跟着换（见 ReadInk）。
            // 少了这一行，浅色主题下主页右上角就是三颗看不见的按钮，而上面那行读数一个数都不会变。
            if (_ink is { } ink) Check("标题栏墨色随大图", ink.Ok, ink.Detail);

            // 需求 6 的两处改动：卡片上那几个悬浮按钮。媒体库那一排不该有已观看和收藏 —— 一个媒体库既没看过
            // 也收藏不了，按下去只会挨服务器一句拒绝；播放按钮则要正正压在封面中心上，而不是原来贴在底边的那颗。
            if (_cards is { } hover) Check("卡片悬浮按钮", hover.Ok, hover.Detail);
            else report.AppendLine("[信息] 卡片悬浮按钮 — 主页没有已渲染的卡片");

            // 「点击主页继续观看、媒体库、最近添加的封面之后会先跳转到页面下方，然后才会进入页面」：按下去的那一
            // 刻卡片先拿到焦点，横带以前会替它要一次 BringIntoView，请求冒到这一页竖着滚的那层就把整页拽下去了。
            // 现在露出一张卡在带自己的滚动视图里做完（ShelfStrip.RevealFor），一句请求都不往外发。这条读数按
            // Focus(FocusState.Pointer) 走同一条路，不点，也就不会开条目；隔一拍再量，见 HomePage.StayFocus。
            if (_stay is { } stay) Check("点卡片不挪页", stay.Ok, stay.Detail);
            else report.AppendLine("[信息] 点卡片不挪页 — 主页没有已渲染的卡片");

            ReportLibrary(report, Check);
            ReportDetail(report, Check);
            ReportInfo(report, Check);
            ReportSeasons(report, Check);
            ReportServers(shell, report, Check);
            ReportDiagnostics(shell, report, Check);
            ReportSettings(shell, report, Check);
            ReportDashboard(report, Check);

            // 需求 1 的第二颗：「点击后弹出设置窗口」. The walk reached the settings by pressing that button, so
            // by here the window has been built, shown and navigated — what is left to say is that it is a
            // real second top-level window with a size, rather than the in-frame fallback.
            var settingsWindow = shell.SettingsWindowState;
            Check("设置窗口",
                settingsWindow is { Created: true, Open: true, Width: > 0, Height: > 0 },
                settingsWindow.Created
                    ? $"{(settingsWindow.Open ? "已弹出" : "已建但没弹出")}，{settingsWindow.Width}×{settingsWindow.Height} 物理像素"
                    : "没建出设置窗口（已回退到主窗口内的设置页）");

            // Put away before anything looks at the screen: everything past this point either samples the
            // display or photographs the shell, and a second window parked over the middle of the main one
            // would be in both. 屏幕像素 failing because the settings window was in the way is the exact
            // false alarm this avoids.
            shell.HideSettings();

            // 需求 1 的第三颗：「新增点击后进入搜索页面，直接在当前窗口跳转」. Pressed rather than read off the
            // tree, because the whole of that clause is which frame the page lands in — a search that opened
            // a window of its own would look identical on the page itself. Empty box, so nothing is asked of
            // the server: the page answers 「输入关键字开始搜索」 without a round trip.
            shell.OpenSearch();
            shell.UpdateLayout();

            var search = (shell.Pages.Content as LibraryPage)?.SearchState;
            var searched = search is { IsSearch: true, BoxShown: true }
                && shell.CurrentTag == LibraryRequest.SearchTag;

            Check("搜索页", searched, search is { } state
                ? $"当前窗口内跳转，导航标签 {shell.CurrentTag ?? "无"}，"
                  + $"搜索框{(state.BoxShown ? "在" : "不在")}、框里「{state.Text}」、页面提示「{state.Notice}」、{state.Rows} 行"
                : $"当前窗口里不是搜索页（{shell.Pages.Content?.GetType().Name ?? "空"}）");
        }

        // 需求 1 把设置搬到了标题栏那一排，所以导航栏自己那个设置项必须是关掉的 —— 两个入口就是两处要同步的
        // 地方，而其中一个还会把设置画成导航栏里的一个选中项。新的那颗由下面「标题栏按键」量。
        //
        // 问的是 IsSettingsVisible，不是 SettingsItem 在不在：那一项由控件模板建，关掉只是把它收起来，对象照旧
        // 存在，拿它当证据会答出反话。
        Check("设置入口", !navigation.IsSettingsVisible,
            navigation.IsSettingsVisible
                ? "导航栏里还留着一个设置项"
                : "导航栏的设置项已关掉，设置在标题栏那一排里");

        // 卡片带翻页. Data-independent on purpose, hence outside the branch above: a self-check has no
        // server, so the three strips on screen hold no cards at all, and what there is to be wrong about
        // is the arithmetic — how far one page is, where it stops at either end, which chevron is up. The
        // probe also parses the control's markup, which is the half no build checks.
        var strip = ShelfStrip.Probe();
        Check("卡片带翻页", strip.Ok, strip.Detail);

        // 需求 5 的那条大图轮播，同样和数据无关，同样在这个分支外面：自检没有服务器，带上一张幻灯片也没有，所以
        // 问的是「没有幻灯片时收不收起来」「底边那排横条造得出来吗」「带高落到布局上了吗」「两层剧照真的轮着上
        // 吗」，加上这份标记自己能不能解析 —— 那两条渐变里写死的颜色是高对比度下唯一活得下来的写法。
        var banner = HomeBanner.Probe();
        Check("主页轮播", banner.Ok, banner.Detail);

        // 标题栏上那一排按键. 一半问外壳、一半问窗口：外壳那半是各种状态摆得对不对（两头走不动时箭头照旧站着、
        // 只是暗下来，而不是一支消失把旁边的挪走；折叠、设置、搜索三颗任何时候都按得动），窗口那半是那块地方到
        // 底属于谁 —— 按键画在标题栏里，就必须在拖动区上挖出正好那么大一个洞，洞小了点不着按钮，洞大了或者挖
        // 错地方就整条拖不动窗口。
        var keys = TitleBarKeys(window, shell, captionAtStartup);
        Check("标题栏按键", keys.Ok, keys.Detail);

        // 同一条带子的另一头：框架画的那三颗系统按钮。和上面那条问的是同一件事的两面 —— 我们在这条带子上声明
        // 区域，声明多了就会把框架自己的按钮压死。放在切过一轮播放标题栏之后问，因为那一轮会把区域整个重摆。
        var systemButtons = SystemButtons(window);
        Check("系统按钮点得动", systemButtons.Ok, systemButtons.Detail);

        ReportPlayer(window, shell, report, Check);

        // ---- the palette actually taking effect ------------------------------------
        // The point of this one: Palette.xaml overrides the framework's own accent brushes by key,
        // and if the merge order in App.xaml were wrong the app would come up Windows-blue with
        // nothing else looking broken. Reading the resolved brush is the only way to tell.
        //
        // What is asserted is that the two keys agree, not that either is a particular green. These
        // resolve through Application.Current.Resources, which picks among the palette's
        // ThemeDictionaries by the application theme. 「Our accent is what the framework's accent role
        // resolves to」 is the claim without the theme in it: true in dark and light, false exactly when
        // the override did not take. High contrast maps both onto the system highlight, ours by the
        // palette's own HighContrast dictionary and the framework's by its, so it holds there too.
        var accent = ColorOf("EgAccentBrush");
        var systemAccent = ColorOf("AccentFillColorDefaultBrush");

        // Checked rather than reported, because two brush readers depend on it. Application scope used to
        // follow the Windows app mode while every element tree pinned Dark, so on a light-mode machine an
        // app-scope lookup handed light ink to a near-black surface: this check reported two failures with
        // nothing wrong, and the diagnostics page's log levels washed out. App.xaml pins Dark now, and
        // DiagnosticsViewModel.Resolve and PlayerPage.BrushFor are what the pin is holding up.
        Check("应用主题", Application.Current.RequestedTheme == ApplicationTheme.Dark,
            Application.Current.RequestedTheme == ApplicationTheme.Dark
                ? "应用级资源按深色解析，与各元素树一致"
                : $"应用级资源按{(Application.Current.RequestedTheme == ApplicationTheme.Light ? "浅色" : "未知")}解析，"
                  + "但外壳固定深色 — App.xaml 的 RequestedTheme 没有生效");
        Check("调色板（应用角色）", accent.StartsWith('#'), $"EgAccentBrush = {accent}");
        Check("调色板（框架覆盖）", systemAccent == accent, systemAccent == accent
            ? $"AccentFillColorDefaultBrush = {systemAccent}"
            : $"AccentFillColorDefaultBrush = {systemAccent}，与 EgAccentBrush（{accent}）不一致，调色板没有盖住框架的强调色");

        // Light is meant to be a full parallel of Default, not a patch on it: a key only Default defines
        // still resolves in light mode — from Default — and Default's colours are picked to sit on a
        // near-black surface. So the omission does not fail loudly anywhere, it just paints pale text on
        // white, on a theme nobody developing this app is looking at. Comparing the two key sets is what
        // turns that into a failure; it is how the whole TextFillColor* family was found missing.
        var dark = PaletteKeys("Default");
        var light = PaletteKeys("Light");
        var missing = dark.Except(light).Order().ToArray();
        Check("调色板浅色主题完整性", dark.Count > 0 && missing.Length == 0,
            dark.Count == 0
                ? "没找到调色板的 Default 主题字典"
                : missing.Length == 0
                    ? $"Light 覆盖了 Default 的全部 {dark.Count} 个键"
                    : $"Light 缺 {missing.Length} 个键，这些会回退到深色的值：{string.Join("、", missing.Take(8))}{(missing.Length > 8 ? " 等" : "")}");

        ReportTheme(services, shell, options, report, Check);
        ReportStyles(report, Check);

        var pageBackground = (shell.Pages.Content as Page)?.Background as SolidColorBrush;
        if (shell.SignInVisible)
            report.AppendLine("[信息] 页面底色 — 未打开页面（登录中）");
        else
            Check("页面底色", pageBackground is not null,
                pageBackground is null ? "未设置" : Format(pageBackground.Color));

        Check("界面字体", shell.FontFamily?.Source == "Microsoft YaHei UI",
            shell.FontFamily?.Source ?? "未设置");

        // ---- the material ---------------------------------------------------------
        var mica = false;
        try
        {
            mica = MicaController.IsSupported();
        }
        catch (Exception error)
        {
            Log.Warn(Category, "查询 Mica 支持失败", error);
        }

        // Not a failure either way: a machine with transparency effects off is a supported machine,
        // and HostWindow falls back to the flat base colour.
        report.AppendLine($"[信息] Mica 支持 — {(mica ? "可用" : "不可用（回退纯色底）")}，当前 UseBackdrop={window.UseBackdrop}");

        // ---- proof that something reached the screen --------------------------------
        // Which screen, first: everything from here down samples the desktop or photographs the shell, and
        // 「跑测试的时候能不能在第二屏幕跑」 means the reader can no longer assume which desktop that was.
        report.AppendLine($"[信息] 运行屏幕 — {window.ScreenSummary}");

        var screen = SampleScreen(window, width, height);
        Check("屏幕像素", screen.Rendered, screen.Detail);

        // Last, and after the pixels on purpose: this one moves the window over the whole monitor and back
        // twice, and DWM catches up on its own schedule. Sampling the screen in the wake of that would be
        // asking whether the compositor had finished, not whether the island drew anything.
        ReportFullscreen(window, Check);

        report.AppendLine();
        report.AppendLine(failures == 0 ? "结果：全部通过" : $"结果：{failures} 项失败");

        ExitCode = failures == 0 ? 0 : 1;
        Write(options, "selfcheck-shell.txt", report.ToString());

        if (options.DumpUi)
        {
            var tree = new StringBuilder();
            Dump(shell, tree, 0);
            Write(options, "selfcheck-shell-tree.txt", tree.ToString());
        }

        Log.Info(Category, failures == 0 ? "自检全部通过" : $"自检有 {failures} 项失败");
    }

    /// <summary>
    /// The container, and the one thing about it that a passing startup does not already prove.
    /// <para>
    /// That every registration resolves is checked at build time by
    /// <see cref="ServiceProviderOptions.ValidateOnBuild"/> — so the app reaching this point already says
    /// so for anything with an injected constructor. What it does not cover is the factory lambdas: a
    /// <c>provider =&gt; new X(...)</c> is opaque to validation and only runs when something asks for an
    /// <c>X</c>, so a mistake inside one waits for the first page that needs it. Asking for all of them
    /// here moves that to startup.
    /// </para>
    /// <para>
    /// The second half is the part worth having. Every service is registered as a singleton because the
    /// settings object is edited in place by the settings page and the session holds the token — two
    /// instances of either means an edit on one page invisible to the next, or a sign-in the rest of the
    /// app never sees. Resolving twice and comparing by reference is what proves that.
    /// </para>
    /// <para>
    /// And then one reading no amount of resolving can give: whether <see cref="IUiDispatcher"/> points at
    /// the thread that draws. A dispatcher over the wrong queue resolves, is a singleton, and marshals
    /// everything into a void.
    /// </para>
    /// </summary>
    private static void ReportContainer(
        IServiceProvider services,
        StringBuilder report,
        Action<string, bool, string> check)
    {
        // Every registration, named so a failure says which one. Resolved through the same
        // GetRequiredService the app uses, so this exercises the factory lambdas rather than inspecting
        // the descriptors.
        var registrations = new (string Name, Func<object> Resolve)[]
        {
            ("AppPaths", () => services.GetRequiredService<AppPaths>()),
            ("ISecretProtector", () => services.GetRequiredService<ISecretProtector>()),
            ("SettingsStore", () => services.GetRequiredService<SettingsStore>()),
            ("AppSettings", () => services.GetRequiredService<AppSettings>()),
            ("CredentialVault", () => services.GetRequiredService<CredentialVault>()),
            ("DeviceIdentity", () => services.GetRequiredService<DeviceIdentity>()),
            ("EmbySession", () => services.GetRequiredService<EmbySession>()),
            ("EmbyImageStore", () => services.GetRequiredService<EmbyImageStore>()),
            ("ShaderGroupResolver", () => services.GetRequiredService<ShaderGroupResolver>()),
            ("PlaybackPlanner", () => services.GetRequiredService<PlaybackPlanner>()),
            ("ShaderStaging", () => services.GetRequiredService<ShaderStaging>()),
            ("PlaybackBackendFactory", () => services.GetRequiredService<PlaybackBackendFactory>()),
            ("PlaybackService", () => services.GetRequiredService<PlaybackService>()),
            ("ISettingsService", () => services.GetRequiredService<ISettingsService>()),
            ("IServerCapabilities", () => services.GetRequiredService<IServerCapabilities>()),
            ("IUiDispatcher", () => services.GetRequiredService<IUiDispatcher>()),
            ("IClipboard", () => services.GetRequiredService<IClipboard>()),
            ("ISystemLauncher", () => services.GetRequiredService<ISystemLauncher>())
        };

        var unresolved = new List<string>();
        var duplicated = new List<string>();

        foreach (var (name, resolve) in registrations)
        {
            try
            {
                var first = resolve();
                // A singleton hands back the same object every time; anything else here would mean two
                // settings objects, or two sessions with two tokens.
                if (!ReferenceEquals(first, resolve())) duplicated.Add(name);
            }
            catch (Exception error)
            {
                unresolved.Add($"{name}（{error.GetType().Name}）");
            }
        }

        check("服务容器", unresolved.Count == 0 && duplicated.Count == 0,
            unresolved.Count > 0 ? $"解析失败：{string.Join("、", unresolved)}"
                : duplicated.Count > 0 ? $"不是单例：{string.Join("、", duplicated)}"
                : $"{registrations.Length} 项注册全部解析，且都是单例");

        // The one cross-registration fact left worth stating now that the pages resolve their own services:
        // the settings service reads the container's settings object rather than a second copy of the file.
        // A SettingsService that called Load() for itself would compile, pass every check above, and quietly
        // hand the settings page a document nothing else in the app was looking at.
        var settings = services.GetRequiredService<ISettingsService>();
        var sameSettings = ReferenceEquals(settings.Settings, services.GetRequiredService<AppSettings>());

        check("设置服务与容器同源", sameSettings,
            sameSettings ? "ISettingsService.Settings 就是容器里的那一个 AppSettings" : "设置服务持有的文档与容器里的不同");

        // Resolving the dispatcher proves it exists; this proves it points at the right thread, which is the
        // one thing about it that can be wrong without anything throwing. The check runs from the self-check
        // timer's tick, so it is on the UI thread — which means Run has to execute inline here. If it queues
        // instead, the container captured some other thread's queue, and every log append and every mpv
        // status update would be marshalled somewhere nothing is drawing. Post is asked the opposite
        // question: the diagnostics page needs it to hop even when it is already on the UI thread, so an
        // implementation that quietly ran inline would put a row into a bound collection mid-layout.
        var dispatcher = services.GetRequiredService<IUiDispatcher>();

        var ranInline = false;
        dispatcher.Run(() => ranInline = true);

        var postedInline = false;
        var accepted = dispatcher.Post(() => postedInline = true);

        check("界面线程调度器", ranInline && accepted && !postedInline,
            $"Run 在界面线程上{(ranInline ? "直接执行" : "改为排队，说明拿到的不是界面线程")}；"
                + $"Post {(accepted ? "已受理" : "被拒绝")}，{(postedInline ? "但立即执行了" : "并留到下一轮")}");

        // Informational: the two readings the pages get from a service rather than from a handle on
        // everything, which is what the container was for.
        var version = services.GetRequiredService<IServerCapabilities>().ServerVersion;
        report.AppendLine($"[信息] 服务读数 — 每页 {settings.PageSize} 条，服务器版本 {version?.ToString() ?? "未知"}");
    }

    /// <summary>
    /// The library page the check walked into: the grid, requirement 4's sort menu and requirement 5's
    /// filter panel, both built by the page's own code rather than by a copy of it here. Read from the
    /// snapshot <see cref="ReadLibrary"/> took, because the walk has since moved on.
    /// </summary>
    private static void ReportLibrary(StringBuilder report, Action<string, bool, string> check)
    {
        if (_library is not { } library)
        {
            report.AppendLine("[信息] 媒体库页面 — 未打开（服务器没有报告媒体库）");
            return;
        }

        check("媒体库页面", library.Ready, $"「{library.Heading}」{library.Cards} 张卡片");
        report.AppendLine($"[信息] 列表类型 — {library.Preset ?? "未定（混合内容）"}");

        var current = EmbySortBy.Find(library.SortKey);
        check("当前排序", current is not null,
            $"{current?.Label ?? "目录里没有的值"}（{library.SortKey}）{(library.Descending ? "降序" : "升序")}");

        // Ten is the floor for any real preset; the point is that the menu built and was filtered, not
        // that it came out a particular length.
        check("排序菜单", library.SortLabels.Count >= 10,
            $"{library.SortLabels.Count} 个键：{string.Join('、', library.SortLabels)}");

        // 需求 5. Built through the page's own handler, so this also proves the flyout's content parses:
        // a missing resource key in it would throw there rather than the first time a user clicks 筛选.
        // The panel's blocks are drawn by a DataTemplateSelector now, and a selector that returns no
        // template is legal and silent — so it is also asked whether every block has a shape, which a
        // count of the labels behind them cannot tell.
        check("筛选面板", library.FilterLabels.Count >= 8 && library.FilterTemplates,
            $"{library.FilterLabels.Count} 项{(library.FilterTemplates ? "" : "（有分组取不到模板，面板会是空的）")}"
                + $"：{string.Join('、', library.FilterLabels)}");

        report.AppendLine($"[信息] 当前筛选 — {library.Filters}");

        // 设置 → 海报宽度 used to reach every page except this one: the cell, the card and the bitmap the
        // card was decoded for were three literals in this grid's markup, so moving the slider resized the
        // home page's rows and left the library — the page the setting is obviously about — untouched.
        // The two cell numbers are read off the live layout, so a binding that never fired reads NaN here
        // rather than whatever this line could have worked out for itself.
        //
        // The cell is only asked to equal the card in 海报: the other two shapes have cells of their own,
        // and those are the next check's business.
        var expected = Math.Clamp(library.PosterSetting, 120, 340);

        check("海报尺寸随设置", library.CardWidth == expected
                && (library.View != "Poster"
                    || (library.CellWidth == library.CardWidth && library.CellHeight > library.CardWidth)),
            $"设置 {library.PosterSetting} → 卡片 {library.CardWidth}，网格单元 {library.CellWidth:0}×{library.CellHeight:0}（视图「{library.View}」）");

        // Each of the three shapes put on the live grid in turn — see LibraryPage.WalkViews for why nothing
        // a launch reads can stand in for this. 海报 is a portrait cell of the set width, 缩略图 a
        // wider-than-tall one, and 列表 is not a grid at all: a row spans the page, and a cell width is the
        // wrong question to ask about it. The shapes were computed from the view and announced to nobody
        // when it changed, so 缩略图 drew 16:9 stills into poster cells and 列表 drew rows into them, with
        // the title, the info line and the synopsis cut off past the cell's right edge.
        var walk = library.Views;
        var poster = walk.Count > 0 ? walk[0] : default;
        var thumb = walk.Count > 1 ? walk[1] : default;
        var list = walk.Count > 2 ? walk[2] : default;

        check("媒体库三种视图的布局", walk.Count == 3
                && poster.Layout == "UniformGridLayout" && poster.Cell == library.CardWidth && poster.Row > poster.Cell
                && thumb.Layout == "UniformGridLayout" && thumb.Cell > library.CardWidth && thumb.Row < thumb.Cell
                && list.Layout == "StackLayout"
                && library.ViewRestored,
            string.Join('；', walk.Select(one => one.Layout == "StackLayout"
                    ? $"{one.Label} {one.Layout}（整行占页宽，不吃格宽）"
                    : $"{one.Label} {one.Layout} 格 {one.Cell:0}×{one.Row:0}"))
                + $"；走完{(library.ViewRestored ? "回到" : "没回到")}「{library.View}」");

        // 格子在指针底下不铺灰底。这一套里「指针在这张卡上」是卡片外面那圈胶片格整圈变强调色说的，格子再自己
        // 铺一层圆角灰影就是两句话说同一件事 —— 而且那层灰影是圆角的，压在方框四角的外面，一面墙扫过去跟着
        // 指针走的是一块灰影而不是那张卡。覆盖写在本页资源里的两个键上（见 LibraryPage.xaml 顶上那段），而
        // 那两个键是模板从自己里头往外查的：查不到就还是灰底，截图看不出来，闸门也全绿。所以这一条量的是真
        // 格子在三个状态下各画什么。
        var halo = library.Halo;

        check("卡片格子不铺灰底", halo.Found && Blank(halo.Over) && Blank(halo.Pressed),
            $"静止 {halo.Still}、指针 {halo.Over}、按下 {halo.Pressed}");

        static bool Blank(string paint) => paint == "不画" || paint.StartsWith("#00", StringComparison.Ordinal);

        // Emby Theater's own library toolbar: a view shape and, on a name-ordered grid with rows to
        // spare, the letter jump bar. The bar's rule is the view model's (名称升序且总数 > 30)；what is
        // checked here is that the rule and the visibility the page actually drew agree, which is the
        // half a markup binding could get wrong on its own.
        var alphaWanted = library.SortKey == EmbySortBy.Name && !library.Descending && library.Cards > 30;
        check("媒体库视图与字母条", library.AlphaShown == alphaWanted,
            $"视图「{library.View}」、字母条{(library.AlphaShown ? "显示" : "收起")}（名称序 {library.Cards} 张 → 应{(alphaWanted ? "显示" : "收起")}）");

        // 需求 4, informational and the reason this line exists: 徽标/横幅图/艺术图 are drawn by this build
        // now, and nothing but a count off the real catalogue says whether they will ever be seen. Counted
        // over everything this page has loaded, not the handful the viewport realised. A 横幅图 column of 0
        // across a whole library is what would say a 横幅 view is not worth building.
        report.AppendLine($"[信息] 媒体库图片存量 — {library.Artwork}");
    }

    /// <summary>
    /// The sign-in card, read whichever face is in front. <c>Prepare</c> runs on every launch — the shell
    /// attaches the card before it knows whether the saved token still works — so what it filled in from
    /// settings and the vault is here to read even on a machine that signed straight in and never showed
    /// the card at all.
    /// </summary>
    private static void ReportSignIn(StringBuilder report, Action<string, bool, string> check, ShellPage shell)
    {
        var card = shell.SignInRoot;
        var model = card.ViewModel;

        // The card's own lists are bound; this one is not, and cannot be. Nothing else would notice a
        // flyout that stayed empty because the collection it is rebuilt from stopped being watched.
        check("登录卡片已填写", card.SavedMenuCount == model.SavedServers.Count,
            $"已保存 {model.SavedServers.Count} 台服务器（菜单 {card.SavedMenuCount} 项）、"
                + $"地址{(model.Address.Length > 0 ? "已填" : "为空")}、"
                + $"用户{(model.Username.Length > 0 ? $"「{model.Username}」" : "为空")}、"
                + $"记住密码={model.Remember}");

        report.AppendLine($"[信息] 登录卡片 — 当前 {model.Step}（忙={model.Busy}），"
            + (shell.SignInVisible ? "在前台" : "已让位给页面"));
    }

    /// <summary>
    /// 需求 6: the detail page the walk drilled into off the library's first row, read from the snapshot
    /// <see cref="ReadDetail"/> took.
    /// <para>
    /// This is the deepest page the walk reaches and the one with the most to get wrong. Everything on it
    /// is worked out from one item the page fetched itself — a series has to turn into its seasons, then
    /// one season's episodes, then the episode to play next — and four pickers, two card rows and one
    /// expander all hang off the result. None of that had ever been looked at outside a build.
    /// </para>
    /// <para>
    /// It opens with where the click went, because everything after it is worthless if a user cannot get
    /// here by clicking. That check is the one that was missing while a series opened as a grid of season
    /// folders: see <see cref="OpenFirstDetail"/>.
    /// </para>
    /// </summary>
    private static void ReportDetail(StringBuilder report, Action<string, bool, string> check)
    {
        if (_click is { } click)
            check("媒体库卡片点开", click.Landed == nameof(DetailPage),
                $"点「{click.Item}」→ {click.Landed ?? "没有页面"}"
                    + (click.Landed == nameof(DetailPage) ? "" : "（应当是详情页面，不是另一个网格）"));

        if (_detail is not { } detail)
        {
            report.AppendLine(_click is null
                ? "[信息] 详情页面 — 未打开（媒体库里没有可下钻的条目）"
                : "[信息] 详情页面 — 未打开（点开的卡片没有走到详情页面）");
            return;
        }

        check("详情页面", detail.Ready && detail.Title.Length > 0,
            $"「{detail.Title}」{detail.Type}，简介 {detail.Overview} 字");

        // The one thing on this page that no amount of looking at it would show. See ReadDetail.
        check("详情选择器", detail.Chosen,
            $"季 {detail.Seasons}、源 {detail.Sources}、音频 {detail.Audio}、字幕 {detail.Subtitles} 行 → {detail.Picked}");

        // Consistency rather than 「there is something to play」: a season with no episodes in it is the
        // server's business, and a page that says 播放 with nothing to play is ours. Both halves are the
        // page's own rules — no target means every picker was emptied, and a target means one file and its
        // tracks (自动 is always in that list, and 字幕 also carries 不使用字幕).
        var coherent = detail.Target is null
            ? detail.Sources == 0 && detail.Audio == 0 && detail.Subtitles == 0
            : detail.Play.Length > 0
                && detail.Sources >= 1
                && detail.Audio >= 1
                && detail.Subtitles >= 2;

        check("详情播放目标", coherent,
            detail.Target is null
                ? $"无可播放目标（{detail.Type}），四个选择器都是空的"
                : $"「{detail.Target}」·「{detail.Play}」");

        // 媒体信息 describes a file, so it is filled on the page of a file and empty everywhere else — the
        // panel's visibility is nothing but 「are there rows」, so this one claim covers both halves. It is
        // asked of a 剧 page here, which is the case that used to be wrong: the table named whichever
        // episode 播放 had resolved to, under a page about the whole show.
        var filePage = EmbyItemType.IsPlayable(detail.Type);

        check("详情媒体信息", filePage ? detail.Info > 0 : detail.Info == 0,
            filePage
                ? $"{detail.Type} 页，{detail.Info} 行"
                : $"{detail.Type} 页，没有这一块（{detail.Info} 行）");

        // 下一集 has to say what the button beside it does, and only where that is news: a show whose 播放
        // resolved to an episode names it, an episode page does not repeat its own headline, and a film has
        // no next thing. Both readings are legitimate, so this holds on any server's catalogue.
        var showPage = detail.Type is EmbyItemType.Series or EmbyItemType.Season;
        var nextUpOk = showPage && detail.TargetType == EmbyItemType.Episode
            ? detail.NextUp.Length > 0
            : detail.NextUp.Length == 0;

        check("详情下一集", nextUpOk,
            detail.NextUp.Length > 0
                ? $"「{detail.NextUp}」{(detail.Remaining.Length > 0 ? $"，{detail.Remaining}" : "，没有断点")}"
                : $"没有这一行（{detail.Type}，播放目标 {detail.TargetType ?? "无"}）");

        // Bound and realised for both, and 「realised」 is why the stage takes two ticks: see ScrollDetail.
        // A row the item genuinely has nothing for is not counted against the page. 单集 is a list on a 季 page
        // and a strip everywhere else, 演职人员 always a strip, so 「已渲染」 means whichever shape ran — and
        // for either only what the viewport covers is ever built, so any non-zero count is the claim.
        var drawnEpisodes = detail.DrawnRows + detail.DrawnCards;
        var rows = 0;
        var drawn = 0;
        if (detail.Episodes > 0) { rows++; if (drawnEpisodes > 0) drawn++; }
        if (detail.Cast > 0) { rows++; if (detail.DrawnCast > 0) drawn++; }

        check("详情单集与演职人员", drawn == rows,
            $"单集 {detail.Episodes} 行（已渲染 {drawnEpisodes}）、"
                + $"演职人员 {detail.Cast} 张（已渲染 {detail.DrawnCast}）");

        // 「只有季页的集使用竖置列表，另外两个页面使用横向翻页」. 电影/剧/季/集 share one DetailPage, so the
        // shape is not a statement in markup — it is derived from this page's own type (ItemDetail.EpisodesAsList),
        // and this is where that derivation is checked against what really drew. The wrong shape is not an
        // empty page, it is a full one of the other kind, so the assertion is that the shape not wanted drew
        // nothing at all; whether the wanted one drew is 详情单集与演职人员's question, just above.
        var wantsList = ItemDetail.EpisodesAsList(detail.Type);
        check("详情单集形状", wantsList ? detail.DrawnCards == 0 : detail.DrawnRows == 0,
            $"{detail.Type} 页要{(wantsList ? "竖置列表" : "横向翻页带")}，"
                + $"实渲染 {detail.DrawnRows} 行 / {detail.DrawnCards} 张卡");

        // The same claim on the page of a file, which is the other half of what was reported — and a claim
        // only that page can make: everything above was snapshotted on the show's page, before the walk
        // clicked into an episode. A 电影 page has no 单集 at all, so a page that drew neither shape is
        // reported rather than failed.
        if (_fileEpisodesDrawn is { } file && file.Rows + file.Cards > 0)
        {
            var fileList = ItemDetail.EpisodesAsList(file.Type);
            check("详情单集形状（文件页）", fileList ? file.Cards == 0 : file.Rows == 0,
                $"{file.Type} 页要{(fileList ? "竖置列表" : "横向翻页带")}，"
                    + $"实渲染 {file.Rows} 行 / {file.Cards} 张卡");
        }
        else
        {
            report.AppendLine("[信息] 详情单集形状（文件页）— "
                + (_fileEpisodesDrawn is { } empty ? $"{empty.Type} 页没有单集可数" : "这次没走到文件页"));
        }

        // Informational: what the server actually had to say about this item. 年份区间 and 制作方 are
        // Fields-gated (see EmbyFields.Detail), so an empty middle segment here is the first place a
        // dropped field name would show — but a library whose metadata is thin is not a defect.
        report.AppendLine($"[信息] 详情元数据 — 「{detail.Facts}」");

        // 需求 4，改成「片名归字、徽标归右上角」之后（「把当前页面徽标所在地方替换为剧名，徽标移动到右上角」）
        // 这一条要的是三句话：片名那一行永远在（没有名字的头图和「图没到」在截图里长得一模一样）、徽标这一枚落在
        // 带子的右上角、它和片名那个盒子不相交。服务器给不出徽标的条目占大多数，那一次前两句照样成立 —— 第二句
        // 是空的（没东西可放），第一句正是这次改动保住的东西。盒子的几何在 DetailPage.TitleShapes 里读。
        var artwork = detail.Artwork;

        check("详情名牌", artwork.Text && artwork.Corner,
            $"片名{(artwork.Text ? "在" : "没画")}、"
                + $"徽标{(artwork.Plate ? $"画在右上角（{artwork.Width:0}×{artwork.Height:0}）" : "没画")}"
                + $" —— 服务器给这一条的名牌是{artwork.Wanted}；{artwork.Where}");

        // 单元 5：头图上那三行字真解析到的字体和字号，见 DetailPage.HeroType。和 轮播版式 成对 —— 全屏最大的
        // 两块字各在一份标记里，样式漏一个 Setter 就是「更大的正文字」，屏上看得见、截图里看不出。
        check("头图版式", detail.HeroTypeOk, detail.HeroType);

        // 「让背景图填满页面，别只显示一个框」：背景那一层通到工作区的两条边、铺到窗口的上下沿；头上那一格按
        // 内容分两档（DetailHero），不再跟着视口走 —— 跟着视口走的那一版在大窗口上留出一屏没有字的画面，就是
        // 「图一页面怎么改的一大片空白」。截图里一眼的事，读数里一句也看不出来。几何在 DetailPage.HeroFill 里读。
        //
        // 后半句是「固定在背景中，而不是往下翻页就消失了，而且要跟主页一样，占满标题栏」：那一层不在滚动区里，
        // 它按窗口坐标报自己的上下沿，上沿贴 0（顶到窗口顶边，也就是压在标题栏和面包屑那行的底下）、下沿到窗口
        // 底边。这一条白拿的 —— 走到这里之前 ScrollDetail 已经把页面拖到了最底，所以「滚到底了它还在原处」就是
        // 那个 0 本身；量到的不是 0，那就是它跟着滚走了。
        check("头图铺满", detail.HeroFillOk, detail.HeroFill);

        // 「滑到下面不用显示背景了，五颜六色的太丑了」：背景那一层是固定的、铺满整窗的（上一条读的就是这件事），
        // 所以往下滚看不见它全靠正文那张纸遮。这一条读纸的四条边：左右到页面的两沿、下沿到窗口的下沿、底色不透光。
        // 最容易漏的是底下那条 —— 纸只有内容那么高，一部没有单集、相似又寥寥的电影填不满一屏，缺的那一段就漏出
        // 图来，所以纸有个跟着视口走的下限（DetailViewModel.BodyMinHeight）。上一版正文是几块浮在图上的玻璃，
        // 四周故意留 28 的缝，这一条在那一版上三条都不过。几何在 DetailPage.BodySeal 里读，同样是滚到底之后读的。
        check("正文盖住背景", detail.BodySealOk, detail.BodySeal);

        // 「往下拉之后标题颜色要渐变，变的和下方背景一样」：剧照和它顶上那层罩子钉在窗口上，而带子底下那道渐深的
        // 罩子跟着内容滚，于是滚到一半时视口上沿那一行就是一道横线 —— 线上面只有剧照，线下面多了那么浓的罩子，
        // 就是「一往下拉颜色就不一样了」。修法是把那一行的浓度洗到标题栏那一条上去（DetailPage.PaintWash，浓度
        // 见 DetailHero.TopWash），洗的颜色取正文那张纸自己的，所以拉到底整条就是纸的颜色。
        //
        // 这一条读三样：那一条盖住了标题栏让出来的那一格，画刷上每个停点的颜色和浓度都对得上算出来的那一份，以及
        // 屏上那道罩子的停点和 Core 那张表是同一组数。截图上这三样都只差一两级灰，而缝正是从那一两级里长出来的。
        check("标题条洗到正文色", detail.WashOk, detail.Wash);

        // Informational: both come off the network, and a server that has no backdrop for this item is a
        // working server. The page's own rule is that the element is hidden unless the bitmap arrived,
        // which is what makes 「没有」 safe rather than a black rectangle.
        report.AppendLine($"[信息] 详情图片 — 头图{(detail.Hero ? "已解码" : "没有")}，剧照{(detail.Still ? "已解码" : "没有")}"
            + $"；{artwork.Hero}");

        // 需求 4, informational: the five artworks this one item has on the server. What the hero band and the
        // corner mark can possibly show is a subset of this line, so 「徽标 没画」 above reads differently
        // depending on whether 徽标 appears here.
        report.AppendLine($"[信息] 详情图片种类 — {artwork.Kinds}");

        // 需求 4, informational and the half of it code alone could not settle: on the page of an episode the
        // 徽标 is the show's, sent under a different item's id, and whether this server fills that pair at all
        // is a question only its own answer settles. 「剧集的徽标」 here is that answer.
        report.AppendLine(_fileArtwork is { } shown
            ? $"[信息] 文件页名牌 — {shown.Type} 页，名牌是{shown.Artwork.Wanted}，"
                + $"{(shown.Artwork.Plate ? "右上角画着徽标" : "右上角空着")}；{shown.Artwork.Where}"
                + $"；这一条有 {shown.Artwork.Kinds}"
            : "[信息] 文件页名牌 — 这次没走到文件页");

        // 「集页面要用这个剧的背景图或缩略图」：单集自己那张图是海报，而单集的海报就是从视频里截的一帧 —— 铺到整页
        // 那么大，头上那张图就跟「更多单集」里它自己那张卡片是同一张。剧集那一层的背景图（发不发只有真账号答得出，
        // 上一行读数里的 剧集背景图／剧集缩略图 就是那个答案）本来就是画给「铺在背后」用的，所以集页和季页先要它。
        //
        // 判据在 ItemArtwork.Hero 那一串的头一个上，而不是在解出来的位图上：解不出来是网络的事，取谁的图是规则的事。
        // 服务器一张剧集宽幅图都没发的那一次这句话空着成立 —— 那时候退回这一集自己的画面才是对的。
        if (_fileArtwork is { } band)
            check("文件页头图取谁的图", band.Artwork.HeroOk,
                $"{band.Type} 页，{band.Artwork.Hero}；{(_fileHero ? "已解码" : "还没解出来")}");
        else report.AppendLine("[信息] 文件页头图 — 这次没走到文件页");
    }

    /// <summary>
    /// 媒体信息 on the page of a file, one page further in than the rest of <see cref="ReportDetail"/>.
    /// <para>
    /// The one thing on that page no build ever exercises: the table is an <c>ItemsRepeater</c> with a
    /// three-column row template, below the fold, on a page the walk only reaches by clicking an episode
    /// card. A wrong resource key in that template throws when a row is realised and not before, and a
    /// dropped <c>x:Bind</c> draws an empty column that a row count would call a success.
    /// </para>
    /// </summary>
    private static void ReportInfo(StringBuilder report, Action<string, bool, string> check)
    {
        if (_info is not { } info)
        {
            report.AppendLine("[信息] 媒体信息表格 — 未读到（走查没有走到某个文件自己的页面）");
            return;
        }

        // Three claims in one, because they only mean anything together: the panel is open without anyone
        // clicking it (图4 is a table you read, not one you open), every row the view model built really
        // drew, and the first of those rows drew its label — 「文 件：」 is the column the whole table is
        // aligned on, and an unrealised row and an empty one look identical from a count.
        check("媒体信息表格",
            info.Expanded && info.Rows > 0 && info.Drawn == info.Rows && info.First.Contains('：'),
            $"{info.Type}「{info.Title}」{info.Rows} 行（已渲染 {info.Drawn}，"
                + $"{(info.Expanded ? "默认展开" : "收着的")}），首行「{info.First}」");

        // Informational: which rows this file's own metadata filled. A server that sends no HDR fields for
        // an SDR file is a working server, and the row is simply not there.
        report.AppendLine($"[信息] 媒体信息行 — {info.Labels}");
    }

    /// <summary>
    /// 跨季下一集 and 详情页季切换, from the probe run just before this report. Both halves report rather than
    /// fail when this server has nothing to ask them with: see <see cref="ProbeSeasonsAsync"/>.
    /// </summary>
    private static void ReportSeasons(StringBuilder report, Action<string, bool, string> check)
    {
        if (_seasons is not { } seasons)
        {
            report.AppendLine("[信息] 跨季相邻单集 — 探针未运行");
            return;
        }

        if (seasons.Skipped is { } reason)
        {
            report.AppendLine($"[信息] 跨季相邻单集 — 跳过（{reason}）");
            return;
        }

        check("跨季相邻单集", seasons.CrossOk, seasons.Cross);

        if (seasons.PickerOk is { } ok) check("详情页季切换", ok, seasons.Picker);
        else report.AppendLine($"[信息] 详情页季切换 — 跳过（{seasons.Picker}）");
    }

    /// <summary>
    /// 需求 3: the servers page, read from the snapshot <see cref="ReadServers"/> took, because the walk
    /// has since moved on to the diagnostics page.
    /// <para>
    /// Both counts are here for a reason. The bound one says the view model built its rows; the realised
    /// one says XAML managed to draw them, and only the second would have caught the 「当前」 badge that
    /// was an <c>InfoBadge</c> given a word where it wanted a number. A card template is markup no build
    /// ever runs.
    /// </para>
    /// </summary>
    private static void ReportServers(ShellPage shell, StringBuilder report, Action<string, bool, string> check)
    {
        if (_servers is not { } servers)
        {
            report.AppendLine($"[信息] 服务器页面 — 未打开（当前 {shell.CurrentTag ?? "无"}）");
            return;
        }

        // At least one server: the account this session signed in with came from the settings file, so a
        // signed-in shell that shows none of them has lost the rows somewhere between the two.
        check("服务器页面", servers.Ready && servers.Bound > 0 && servers.Drawn == servers.Bound,
            $"{servers.Bound} 台服务器（已渲染 {servers.Drawn}）、{servers.BoundAccounts} 个账号（已渲染 {servers.DrawnAccounts}）");

        // 需求 12. Proves the measurement ran and returned, rather than the caption still saying so.
        check("图片缓存", !servers.Cache.Contains("正在", StringComparison.Ordinal), servers.Cache);
    }

    /// <summary>
    /// The diagnostics page, read from the snapshot <see cref="ReadDiagnostics"/> took, because the walk has
    /// since moved on to the settings page.
    /// <para>
    /// Bound and realised again, and for the harder case: the log list virtualises, so the two are not
    /// meant to match — what the realised count proves is that the row template ran at all. It is the only
    /// check here that could have caught a bad brush key or a mistyped literal inside that template, which
    /// is the failure this whole walk exists to find.
    /// </para>
    /// </summary>
    private static void ReportDiagnostics(ShellPage shell, StringBuilder report, Action<string, bool, string> check)
    {
        if (_diagnostics is not { } diagnostics)
        {
            report.AppendLine($"[信息] 诊断页面 — 未打开（当前 {shell.CurrentTag ?? "无"}）");
            return;
        }

        // Rows at all: this shell has been logging since Program started, so an empty log view means the
        // ring buffer never reached the page. 「全部」 plus one per category, hence at least two.
        check("诊断页面", diagnostics.Ready && diagnostics.Rows > 0 && diagnostics.Categories > 1,
            $"{diagnostics.Rows} 行日志（已渲染 {diagnostics.Drawn}）、{diagnostics.Categories} 个类别（含「全部」）");

        check("日志行模板", diagnostics.Drawn > 0,
            diagnostics.Drawn > 0 ? $"已实现 {diagnostics.Drawn} 行" : "一行都没画出来");

        // Informational: 实时跟随 defaults on, and nothing in the walk turns it off.
        report.AppendLine($"[信息] 日志实时跟随 — {(diagnostics.Following ? "开" : "关")}");

        // Informational either way: a self-check run has played nothing, so 「尚未播放」 is the expected
        // answer. It is here so that a --play run's report says what mpv was actually handed.
        report.AppendLine($"[信息] 播放参数 — {diagnostics.Launch}");
    }

    /// <summary>
    /// The settings page, which is where the walk now ends, so the page itself is read live and only the
    /// per-card counts come from the walk.
    /// <para>
    /// Bound and realised for the third time, and for the case that needed it most. This page is around sixty
    /// rows of seven shapes picked at runtime by a <c>DataTemplateSelector</c>, and nothing about that is
    /// checked by a build: a row shape the selector has no case for, a resource key one of the templates asks
    /// for that does not resolve, or a literal of the wrong type in one of them all come out as a row that is
    /// simply not on screen. Counting containers off the tree is what turns any of those into a failure.
    /// </para>
    /// <para>
    /// Unlike the log list nothing here virtualises, so the counts are expected to match exactly — but only
    /// against the cards the walk opened, since a collapsed card never builds its rows. Hence the walk, and
    /// hence the running total: by the last card every row on the page should be on the tree.
    /// </para>
    /// </summary>
    private static void ReportSettings(ShellPage shell, StringBuilder report, Action<string, bool, string> check)
    {
        if (shell.SettingsRoot is not { } page)
        {
            report.AppendLine($"[信息] 设置页面 — 未打开（当前 {shell.CurrentTag ?? "无"}）");
            return;
        }

        var (sections, rows, category, _, status) = page.Summary;

        // The left-hand list and the cards, against each other. Both are written out by hand and in different
        // places, so a category with no card is an entry that shows an empty pane and a card with no category
        // is a card no one can reach. 服务器 and 诊断 are the two entries that are deliberately not cards —
        // they are pages hosted in the same column (需求 2) — so they join the cards on that side.
        var reachable = page.Cards
            .Select(card => card.Category)
            .Concat(SettingsViewModel.HostedCategories)
            .ToList();

        var unreachable = reachable
            .Except(page.Categories, StringComparer.Ordinal)
            .Concat(page.Categories.Except(reachable, StringComparer.Ordinal))
            .ToList();

        check("设置页面", page.IsReady && rows > 0 && unreachable.Count == 0,
            unreachable.Count > 0
                ? $"分类与卡片对不上：{string.Join('、', unreachable)}"
                : $"{sections} 张卡片 + {SettingsViewModel.HostedCategories.Count} 个内嵌页面、{rows} 行设置，当前「{category}」");

        // Every category, not just the last: a card that drew nothing would otherwise be hidden by the ones
        // that drew before it, since the count is of the whole tree.
        var missing = _settings.Where(stop => stop.Drawn < stop.Expected).ToList();
        var walked = _settings.Count;

        var detail = walked != sections
            ? $"只走到 {walked} / {sections} 张卡片"
            : missing.Count > 0
                ? "少画了：" + string.Join('、', missing.Select(stop => $"{stop.Category} {stop.Drawn}/{stop.Expected}"))
                : $"{walked} 张卡片逐个打开，行容器累计 {_settings[^1].Drawn} 个";

        check("设置分类走查", walked == sections && missing.Count == 0, detail);

        // The end state of the walk, which is every card having been opened once: nothing virtualises here
        // and a collapsed card keeps what it built, so the tree should hold the page's every row.
        var drawn = page.Realised;
        check("设置行模板", drawn == rows, $"已渲染 {drawn} / {rows} 个行容器");

        foreach (var stop in _settings)
            report.AppendLine($"[信息] 设置卡片 — 「{stop.Category}」本卡 {stop.Rows} 行，累计已渲染 {stop.Drawn} / {stop.Expected}");

        // The config editor opened a file rather than sitting on a blank box. Which file it found is not
        // something the shell controls, so the check is only that the read came back at all: 「正在载入」
        // still showing means it did not.
        check("配置文件编辑器", !status.Contains("正在", StringComparison.Ordinal), status);

        // The 字幕 card's font picker: whether the machine's families really got into it, whether the search
        // box filters, and whether a search can lose the current value. The last one is the one worth a
        // check — the row's list is also where it shows what the setting is, so a filter that may hide the
        // selected family can leave the row looking unset, and the way out of that would be to pick another
        // font. Nothing here writes: this row only commits when a family is picked, and the probe picks none.
        var picker = page.MeasureFontPicker();
        check("字幕字体列表", picker is { Ok: true },
            picker is null ? "没有建出字体行" : picker.Detail);

        // 主题那一行的色板。什么都不点：这一行只在点中一块时写盘，而量它不需要真换一次主题 —— 角标落在存着
        // 的那一套上、几块颜色互不相同、屏上块数对得上，坏法就都在这三句里了（见 MeasureThemeSwatches）。
        var swatches = page.MeasureThemeSwatches();
        check("主题色板", swatches is { Ok: true },
            swatches is { } read ? read.Detail : "没有建出主题行");

        // The one path this page's cache guard exists for. Pressing 设置 again re-navigates the settings
        // window's frame to this same page type, and a page that rebuilt itself on the way in would throw
        // away whatever is unsaved in the config editor. Checked by object identity and not by counts: a
        // rebuilt page reports exactly the same numbers, so 「these are still the same objects」 is the only
        // question that can tell the two apart.
        var editor = page.ViewModel.ConfigEditor;
        var firstCard = page.ViewModel.Sections.FirstOrDefault();

        shell.ShowSettings();

        var kept = shell.SettingsRoot is { } revisited
            && ReferenceEquals(revisited, page)
            && ReferenceEquals(revisited.ViewModel.ConfigEditor, editor)
            && ReferenceEquals(revisited.ViewModel.Sections.FirstOrDefault(), firstCard);

        check("设置页重进不重建", kept,
            kept ? "再按一次设置，编辑器和卡片还是原来那些" : "再按一次设置把页面重建了，未保存的编辑会丢");
    }

    /// <summary>
    /// 需求 8：the Emby web console embedded in the settings page, read from the snapshot
    /// <see cref="VisitDashboard"/> took — the walk has since gone back to a card, which is what lets go of
    /// the browser.
    /// <para>
    /// Only the two things this app is responsible for can fail here: the control coming up on its own profile,
    /// and the sign-in seed being registered before the navigation that needs it. Whether that navigation then
    /// reached the server, and what the web client made of the seeded session, are 信息 lines — an Emby that is
    /// off or unreachable is not this build being broken, and failing the run over it would make every check in
    /// this report hostage to someone else's server.
    /// </para>
    /// <para>
    /// Nothing here can carry a credential. The URL is the console's own address, the seed lives only inside
    /// the injected script, and the sample reads a route, a title and whether the server id got filled in.
    /// </para>
    /// </summary>
    private static void ReportDashboard(StringBuilder report, Action<string, bool, string> check)
    {
        if (_dashboard is not { } console)
        {
            report.AppendLine("[信息] 服务器控制台 — 走查没走到（设置分类没走完）");
            return;
        }

        // No account, so nothing to embed and nothing to seed: the page says as much in place of the console,
        // which is what it is meant to do rather than something to fail over.
        if (console.Url.Length == 0 && console.Ready)
        {
            report.AppendLine("[信息] 服务器控制台 — 尚未登录，页面按设计显示提示语");
            return;
        }

        check("服务器控制台",
            console is { Ready: true, Core: true, Seeded: true } && console.Url.Length > 0,
            $"{(console.Core ? "内嵌浏览器已就绪" : "内嵌浏览器没起来")}、"
            + $"{(console.Seeded ? "已注入免登录" : "没注入免登录")}，"
            + (console.Url.Length > 0 ? console.Url : "没有地址")
            + (console.Error.Length > 0 ? $"（{console.Error}）" : ""));

        report.AppendLine("[信息] 控制台页面载入 — " + (console.Navigated switch
        {
            true => "成功",
            false => $"失败（{console.Error}）",
            _ => console.Error.Length > 0 ? $"没能开始（{console.Error}）" : "还没有结果"
        }));

        report.AppendLine($"[信息] 控制台网页状态 — {console.Client}");
    }


    /// <summary>
    /// The player, which no screenshot of a browsing shell can say anything about. Whether it is in the
    /// tree at all, whether it is out of the way while nothing is playing, whether the reveal rule's
    /// states really reach the three pieces of chrome — and then every surface that builds itself only
    /// when opened: the 统计 panel, the right-click 画面菜单, the five control-bar pickers, the 跳过 offer
    /// and the seek bar's chapter ticks. None of those exists until something asks for it, so without
    /// these probes the first thing to run them would be a user in the middle of a film.
    /// </summary>
    private static void ReportPlayer(
        HostWindow window,
        ShellPage shell,
        StringBuilder report,
        Action<string, bool, string> check)
    {
        var player = shell.PlayerRoot;

        check("播放层", player is not null, player is null ? "不在树里" : "PlayerPage 已就位");
        if (player is null) return;

        check("播放层已接线", player.Attached, player.Attached ? "已拿到播放服务、外壳与窗口" : "Attach 未执行");

        // Both must be down while browsing: the video surface only appears where the XAML above it is
        // transparent, so a player left visible would punch a hole in the library grid. Measured on the
        // tree rather than on the rule, which starts out saying the chrome is up because that is what
        // should be true the instant a film begins.
        check("播放层已让位", !player.PlayerVisible && !player.ChromeShown,
            $"可见={player.PlayerVisible}，控件在屏={player.ChromeShown}");

        // Requirement 3 in HostWindow's remarks: client-area Mica and a child HWND cannot both have the
        // pixels, so the backdrop is on while browsing and off during video.
        check("底材归属", window.UseBackdrop && !window.VideoVisible,
            $"UseBackdrop={window.UseBackdrop}，VideoVisible={window.VideoVisible}");

        // Requirements 9/10/11, driven through the page's own Render: each step now says what it expects,
        // so a bar that stopped hiding is a failed check rather than a line someone has to read.
        var reveal = player.ProbeReveal();
        check("显隐规则", reveal.Ok, reveal.Detail);

        // 「加大音量条的尺寸，显示方式改为淡入淡出，鼠标指针越接近右边的中心显示越明显」: the rail's own three
        // claims, which the reveal probe above cannot make — it reads 「up or not」, and the whole of this
        // requirement is the degrees in between.
        var railFade = player.ProbeRailFade();
        check("音量条淡入淡出", railFade.Ok, railFade.Detail);

        // The probe drove the rule; it must have put it back, or the bar it revealed is now over the grid.
        check("显隐规则已复位", !player.ChromeShown, $"控件在屏={player.ChromeShown}");

        // The one step the rule above cannot vouch for: that 「hide the cursor」 leaves the process. Every
        // cursor expectation in the reveal probe reads the page's own field back, which is why
        // 「鼠标指针还是不会自动隐藏」 could be true of a run that passed.
        var cursor = player.ProbeCursor();
        check("藏鼠标真的到了系统", cursor.Ok, cursor.Detail);

        // And the same hiding with nothing invented: real seconds, the real ten-hertz ticker, the pointer left
        // where a hand would leave it, in a window and full screen. Every other cursor check here supplies its
        // own timestamps, and a rule driven by a clock the probe made up cannot be caught restamping that
        // clock — which is what 「鼠标指针还是不会自动隐藏」 was, twice over.
        var alive = player.ProbeCursorAlive();
        check("鼠标真等两秒就藏", alive.Ok, alive.Detail);

        // 全屏时最下方会有进度条: the thin bottom line is the one piece of chrome the reveal rule cannot state
        // on its own, because its rule is the inverse — up when the bar is down — and because half of it is
        // the window's shape rather than the pointer's position. Driven through the real window, in and out
        // of full screen, since that is the half a rule in Core has no way to be asked about.
        var thinLine = player.ProbeThinLine();
        check("底边细线只在窗口化", thinLine.Ok, thinLine.Detail);

        // Everything below here is chrome that only builds itself when something asks it to, which means
        // none of it is touched by a build and all of it first runs in front of a user, mid-film. Each
        // probe drives the page's own code and puts it back; what they are proving is the wiring between
        // a button and the thing it opens, plus that every resource key those builders ask for resolves.
        var stats = player.ProbeStats();
        check("播放统计面板", stats.Ok, stats.Detail);

        var picture = player.ProbePictureMenu();
        check("右键画面菜单", picture.Ok, picture.Detail);

        var menus = player.ProbeControlMenus();
        check("控制栏菜单", menus.Ok, menus.Detail);

        // 需求 7. Three of the things this asks about are collisions with the player rather than faults in
        // the box: the strip's drag-guard list, the chrome hold a flyout used to be able to steal, and the
        // single letters the player owns. All three look fine until someone types into it mid-film.
        var font = player.ProbeSubtitleFont();
        check("播放页字幕字体栏", font.Ok, font.Detail);

        var skips = player.ProbeSkipAndChapters();
        check("跳过片头与章节刻度", skips.Ok, skips.Detail);

        var peek = player.ProbePeek();
        check("章节预览定位", peek.Ok, peek.Detail);

        var commands = player.ProbeWindowCommands();
        check("窗口命令按钮", commands.Ok, commands.Detail);

        var tap = player.ProbeTap();
        check("点击画面暂停", tap.Ok, tap.Detail);

        // The acknowledgement that click leaves behind. Its own probe because it is the one storyboard in
        // the app, and a storyboard's target names are resolved when it is begun — so the first thing to
        // find out that the badge no longer resolves would otherwise be the pause it was meant to confirm.
        var pulse = player.ProbePulse();
        check("暂停播放角标", pulse.Ok, pulse.Detail);

        var toast = shell.ToastState;
        check("提示通道", toast.Exists, toast.Exists ? $"InfoBar，当前{(toast.Open ? "已显示" : "未显示")}" : "缺失");

        ReportPictureAspect(window, player, check);
    }

    /// <summary>
    /// 画面比例, last of the player's probes and wrapped in a rect restore, because it is the only one that
    /// moves the window: <c>FitToPicture</c> reshapes the client area for real, and every geometry answer
    /// the probes above gave was measured against the size it had before. Modelled on
    /// <see cref="ReportFullscreen"/>, which drives the live window for the same reason and puts it back the
    /// same way.
    /// </summary>
    private static void ReportPictureAspect(HostWindow window, PlayerPage player, Action<string, bool, string> check)
    {
        var handle = window.Handle;
        var before = new NativeRect();
        var saved = handle != IntPtr.Zero && Native.GetWindowRect(handle, out before);

        (bool Ok, string Detail) aspect;
        try
        {
            aspect = player.ProbeAspect();
        }
        finally
        {
            if (saved)
            {
                Native.SetWindowPos(
                    handle, Native.HwndTop,
                    before.Left, before.Top, before.Width, before.Height,
                    Native.SwpNoZOrder | Native.SwpNoActivate);
            }
        }

        check("画面比例联动", aspect.Ok, aspect.Detail);
    }

    /// <summary>
    /// 全屏, driven on the real window: in and straight back out again, measuring the frame both times.
    /// <para>
    /// Three separate claims, so a failure says which one broke. The frame: covers the monitor exactly,
    /// both style bits gone, joins the topmost band, and coming back restores the rect, the style and the
    /// band it took away. The taskbar: whoever owns the pixels in the middle of the tray while the window
    /// is fullscreen had better be the window — that is 「全屏后 windows 任务栏还在」 stated as something
    /// measurable, and it is the only part of this the user can see. And the rule that decides whether the
    /// picture keeps that band when another application comes forward, which is 「屏幕1全屏播放时点击屏幕2的
    /// 应用」 — the one arrangement this cannot stage on the machine it is running on, so it is put as
    /// geometry, plus the question that comes before the geometry: whether the window in front is even
    /// somebody else's.
    /// </para>
    /// </summary>
    private static void ReportFullscreen(HostWindow window, Action<string, bool, string> check)
    {
        // First, and without needing a window at all: four arrangements put to the rule that decides whether
        // a fullscreen picture keeps the topmost band while another application is in front. The real
        // arrangement needs a second monitor with an app open on it, so the rule is asked about geometry
        // instead — a window on another screen keeps the band, and everything else gives it up, including
        // a window that is on another screen but reaches across into the picture and any monitor the OS
        // would not name.
        var here = new IntPtr(1);
        var there = new IntPtr(2);
        var picture = new NativeRect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        var beside = new NativeRect { Left = 2000, Top = 100, Right = 2600, Bottom = 700 };
        var across = new NativeRect { Left = 1800, Top = 100, Right = 2600, Bottom = 700 };
        var upon = new NativeRect { Left = 100, Top = 100, Right = 400, Bottom = 400 };

        var elsewhere = HostWindow.StandsClear(picture, here, beside, there);
        var straddling = HostWindow.StandsClear(picture, here, across, there);
        var sameScreen = HostWindow.StandsClear(picture, here, upon, here);
        var nameless = HostWindow.StandsClear(picture, here, beside, IntPtr.Zero);

        // And the question asked before any of that geometry: whose window is in front. Our own popups are
        // top-level windows sitting right over the picture, and a menu is not the user leaving.
        var mine = HostWindow.SameApp(window.Handle);
        var theirs = HostWindow.SameApp(Native.FindWindow("Shell_TrayWnd", null));

        check("全屏让位只看画面",
            elsewhere && !straddling && !sameScreen && !nameless && mine && !theirs,
            $"另一屏不重叠时保持置顶={elsewhere}，另一屏但压到画面={straddling}"
                + $"，同一屏={sameScreen}，问不出显示器={nameless}"
                + $"；自己的窗口算自家={mine}，任务栏算自家={theirs}");

        if (window.Handle == IntPtr.Zero || window.Fullscreen) return;

        var monitor = Native.MonitorFromWindow(window.Handle, Native.MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!Native.GetMonitorInfo(monitor, ref info))
        {
            check("全屏几何", false, "读不到显示器边界");
            return;
        }

        Native.GetWindowRect(window.Handle, out var before);
        var beforeStyle = (long)Native.GetWindowLongPtr(window.Handle, Native.GwlStyle);
        var beforeEx = (long)Native.GetWindowLongPtr(window.Handle, Native.GwlExStyle);

        NativeRect full;
        long fullStyle;
        long fullEx;
        bool dragRefused;
        (bool Ok, string Detail) taskbar;
        try
        {
            window.Fullscreen = true;

            Native.GetWindowRect(window.Handle, out full);
            fullStyle = (long)Native.GetWindowLongPtr(window.Handle, Native.GwlStyle);
            fullEx = (long)Native.GetWindowLongPtr(window.Handle, Native.GwlExStyle);

            // The title strip is still there in fullscreen, and dragging the window by it would pull the
            // frame off the monitor it is covering. Asked here because this is where a fullscreen window is.
            dragRefused = !window.BeginDrag(new NativePoint { X = full.Left + 100, Y = full.Top + 10 });
            window.EndDrag();

            taskbar = ReportTaskbarStandsAside(window.Handle, info.Monitor);
        }
        finally
        {
            window.Fullscreen = false;
        }

        Native.GetWindowRect(window.Handle, out var after);
        var afterStyle = (long)Native.GetWindowLongPtr(window.Handle, Native.GwlStyle);
        var afterEx = (long)Native.GetWindowLongPtr(window.Handle, Native.GwlExStyle);

        var covered = full.Left == info.Monitor.Left && full.Top == info.Monitor.Top
            && full.Width == info.Monitor.Width && full.Height == info.Monitor.Height;
        var stripped = (fullStyle & (Native.WsCaption | Native.WsThickFrame)) == 0;
        var raised = (fullEx & Native.WsExTopMost) != 0;
        var restored = after.Left == before.Left && after.Top == before.Top
            && after.Width == before.Width && after.Height == before.Height
            && afterStyle == beforeStyle && afterEx == beforeEx;

        check("全屏几何", covered && stripped && raised && restored,
            $"{full.Width}x{full.Height} 覆盖 {info.Monitor.Width}x{info.Monitor.Height}={covered}"
                + $"，去掉边框={stripped}，已置顶={raised}，退出后还原={restored}");

        check("全屏时的任务栏", taskbar.Ok, taskbar.Detail);

        // 拖动标题栏移动窗口, driven through the window's own three calls, because the OS move loop this
        // replaced could not be driven from anywhere at all: it was a modal loop inside DefWindowProc, and
        // 「点击标题后窗口会固定在鼠标上」 was the only way to find out it had been entered with no button held.
        // A synthetic grab point and one move — the window has to end up displaced by exactly that delta, and
        // has to stop following once the drag is over.
        Native.GetWindowRect(window.Handle, out var seat);
        var grab = new NativePoint { X = seat.Left + 100, Y = seat.Top + 10 };
        var began = window.BeginDrag(grab);

        window.DragTo(new NativePoint { X = grab.X + 37, Y = grab.Y + 23 });
        Native.GetWindowRect(window.Handle, out var moved);

        window.EndDrag();
        window.DragTo(new NativePoint { X = grab.X + 500, Y = grab.Y + 500 });
        Native.GetWindowRect(window.Handle, out var ignored);

        Native.SetWindowPos(
            window.Handle, Native.HwndTop,
            seat.Left, seat.Top, seat.Width, seat.Height,
            Native.SwpNoZOrder | Native.SwpNoActivate);

        var followed = moved.Left == seat.Left + 37 && moved.Top == seat.Top + 23
            && moved.Width == seat.Width && moved.Height == seat.Height;
        var stopped = ignored.Left == moved.Left && ignored.Top == moved.Top;

        check("拖动标题栏移动窗口", began && followed && stopped && !window.Dragging && dragRefused,
            $"按下={began}，移动 37,23 → {moved.Left - seat.Left},{moved.Top - seat.Top}（尺寸不变="
                + $"{moved.Width == seat.Width && moved.Height == seat.Height}）"
                + $"，松开后不再跟随={stopped}，全屏时不接受拖动={dragRefused}");
    }

    /// <summary>
    /// Whether the taskbar really is out of the way, phrased as the sentence the report prints.
    /// <para>
    /// Measured by asking who is on screen at the middle of the tray rather than by reading the tray's
    /// <c>WS_EX_TOPMOST</c> bit: the bit is explorer's private business and recent Windows keeps it set,
    /// while what the complaint was actually about — 「全屏后 windows 任务栏还在」 — is precisely whose
    /// pixels are at those coordinates. Polled, because explorer answers on its own thread.
    /// </para>
    /// </summary>
    private static (bool Ok, string Detail) ReportTaskbarStandsAside(IntPtr window, NativeRect monitor)
    {
        if (TrayOnScreen(monitor) is not { } tray)
            return (true, "这块屏幕上没有任务栏，无从遮挡");

        var (aside, waited) = PollTray(window, tray.Middle, 400);
        if (aside) return (true, $"任务栏中点上是本窗口（等了 {waited} 毫秒）");

        // 置顶 is tied to being the application in front, on purpose, so a window that lost activation
        // while this ran is behaving correctly by letting the taskbar back over it. Not a failure — but
        // said out loud, because it is also the one way this check can quietly stop meaning anything.
        return Native.GetForegroundWindow() == window
            ? (false, $"等满 {waited} 毫秒，任务栏仍压在画面上")
            : (true, $"等待期间窗口离开前台，任务栏本就该盖回来（等了 {waited} 毫秒）");
    }

    /// <summary>
    /// The taskbar standing on <paramref name="monitor"/> and the middle of it, or null when none of them is
    /// there.
    /// <para>
    /// Both classes, because Windows makes a separate bar for every screen that is not the main one and
    /// 「Shell_TrayWnd」 is only ever the main screen's. Asking about that one alone was enough while the shell
    /// always opened on the main screen; once a self-check run opens on another one (<c>--screen</c>) that
    /// question answers 「the taskbar is on the other monitor, it cannot be covering anything」 for a picture
    /// with a taskbar of its own sitting across the bottom of it.
    /// </para>
    /// </summary>
    private static (IntPtr Tray, NativePoint Middle)? TrayOnScreen(NativeRect monitor)
    {
        foreach (var tray in Trays())
        {
            if (!Native.GetWindowRect(tray, out var bar)) continue;

            var middle = new NativePoint { X = (bar.Left + bar.Right) / 2, Y = (bar.Top + bar.Bottom) / 2 };
            if (middle.X < monitor.Left || middle.X >= monitor.Right) continue;
            if (middle.Y < monitor.Top || middle.Y >= monitor.Bottom) continue;

            return (tray, middle);
        }

        return null;
    }

    /// <summary>Every taskbar there is: the main screen's, and then one per secondary display.</summary>
    private static IEnumerable<IntPtr> Trays()
    {
        var main = Native.FindWindow("Shell_TrayWnd", null);
        if (main != IntPtr.Zero) yield return main;

        var next = IntPtr.Zero;
        while ((next = Native.NextWindow(IntPtr.Zero, next, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            yield return next;
    }


    /// <summary>
    /// Waits for <paramref name="window"/> to be the window on screen at <paramref name="point"/>, keeping
    /// this thread's message loop turning meanwhile: a probe that blocks the UI thread is measuring a hung
    /// window, which is not the app whose behaviour is in question.
    /// </summary>
    private static (bool Ok, long Waited) PollTray(IntPtr window, NativePoint point, int milliseconds)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            while (Native.PeekMessage(out var message, IntPtr.Zero, 0, 0, Native.PmRemove))
            {
                Native.TranslateMessage(ref message);
                Native.DispatchMessage(ref message);
            }

            var hit = Native.WindowFromPoint(point);
            if (hit != IntPtr.Zero && Native.GetAncestor(hit, Native.GaRoot) == window)
                return (true, clock.ElapsedMilliseconds);

            if (clock.ElapsedMilliseconds >= milliseconds) return (false, clock.ElapsedMilliseconds);
            Thread.Sleep(25);
        }
    }

    /// <summary>
    /// 主题这一套真的画到界面上了没有。
    /// <para>
    /// 三条，都是「构造上无法自证」的那种。第一条问生效的是不是这一次该生效的那一套 —— 平常是设置文件里存的，
    /// 带了 <c>--theme</c> 就是命令行要的那一套（那个开关故意不写设置文件，所以拿存的去比会红一整片）——
    /// 顺带问外壳那棵树的深浅跟不跟着走：树没翻过去的话，浅色主题下就是白底配框架自己的深色控件。第二条问
    /// <c>ThemeHost</c> 那张表和 <c>Palette.xaml</c> 对不对得上：表里多一个键是白写，字典里多一个
    /// <c>Eg*Brush</c> 是界面上留了一块换不掉的颜色 —— 那块会永远停在第一帧的字面值上。第三条把几个锚点的
    /// 颜色读回来比一遍，两个字典都读，因为「两份写成同一套」正是那两个按应用级解析的读者能和元素树一致的
    /// 全部原因。
    /// </para>
    /// </summary>
    private static void ReportTheme(
        IServiceProvider services,
        ShellPage shell,
        StartupOptions options,
        StringBuilder report,
        Action<string, bool, string> check)
    {
        var theme = ThemeHost.Current;
        var stored = services.GetRequiredService<ISettingsService>().Settings.Ui.Theme;

        // 这一次该生效的是哪一套。--theme 只压住这一次运行看到的颜色，设置文件一个字不动，所以那个开关在的
        // 时候要跟它比 —— 并且把「存的那个还是原来那个」也报出来，那正是这个开关唯一容易出错的地方。
        var asked = options.Theme is { Length: > 0 } flag ? UiThemes.Resolve(flag).Id : null;
        var effective = asked ?? stored;

        report.AppendLine(
            $"[信息] 主题目录 — 共 {UiThemes.All.Count} 套：" +
            string.Join("、", UiThemes.All.Select(one => $"{one.Name}（{one.Id}，{(one.IsDark ? "深" : "浅")}）")));

        if (asked is not null)
            report.AppendLine(
                "[信息] 主题来自命令行 — "
                + (string.Equals(options.Theme, asked, StringComparison.OrdinalIgnoreCase)
                    ? $"--theme 要 {asked}"
                    : $"--theme 写的是「{options.Theme}」，认不出，回落到 {asked}")
                + $"，设置文件里仍是 {stored}，这一次运行不写它");

        // 正文压在窗口底上的对比度，按当前这套算。测试里六套都卡着 4.5:1，这里报的是「跑起来之后真的是这个数」。
        report.AppendLine(
            $"[信息] 主题正文对比度 — {theme.Name}：{ThemeColor.Contrast(theme.Colors.Text, theme.Colors.Window):0.00}:1");

        var wanted = theme.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        var sameId = string.Equals(theme.Id, effective, StringComparison.OrdinalIgnoreCase);
        var sameTree = shell.RequestedTheme == wanted;
        check("主题已生效", sameId && sameTree,
            !sameId
                ? $"{(asked is null ? "设置文件里" : "--theme 要的")}是 {effective}，生效的却是 {theme.Id}"
                  + $" — ThemeHost.Apply 没按{(asked is null ? "设置" : "命令行")}跑"
                : sameTree
                    ? $"{theme.Name}（{theme.Id}{(asked is null ? "" : "，来自命令行")}），外壳那棵树是 {shell.RequestedTheme}"
                    : $"{theme.Name}（{theme.Id}）是{(theme.IsDark ? "深" : "浅")}色，外壳那棵树却是 {shell.RequestedTheme} — 没登记到 ThemeHost");

        var painted = ThemeHost.BrushKeys.ToHashSet(StringComparer.Ordinal);
        var darkKeys = PaletteKeys("Default");
        var lightKeys = PaletteKeys("Light");

        var absent = painted
            .Where(key => !darkKeys.Contains(key) || !lightKeys.Contains(key))
            .Order()
            .ToArray();

        var unpainted = darkKeys
            .Where(key => key.StartsWith("Eg", StringComparison.Ordinal)
                          && key.EndsWith("Brush", StringComparison.Ordinal)
                          && !painted.Contains(key))
            .Order()
            .ToArray();

        check("主题角色覆盖", absent.Length == 0 && unpainted.Length == 0,
            absent.Length > 0
                ? $"ThemeHost 要涂 {absent.Length} 个调色板里没有的键：{string.Join("、", absent.Take(8))}{(absent.Length > 8 ? " 等" : "")}"
                : unpainted.Length > 0
                    ? $"调色板有 {unpainted.Length} 个 ThemeHost 不涂的角色，它们会一直停在第一帧的字面值上：{string.Join("、", unpainted.Take(8))}{(unpainted.Length > 8 ? " 等" : "")}"
                    : $"{painted.Count} 个键，Default 和 Light 两份都涂到了");

        (string Key, ThemeColor Want)[] anchors =
        [
            ("EgWindowBrush", theme.Colors.Window),
            ("EgSurfaceBrush", theme.Colors.Surface),
            ("EgTextBrush", theme.Colors.Text),
            ("EgAccentBrush", theme.Colors.Accent),

            // 框架那半的锚点：这个键错了，等于所有没被我们改过模板的控件的正文颜色都错了。
            ("TextFillColorPrimaryBrush", theme.Colors.Text)
        ];

        var wrong = new List<string>();
        foreach (var (key, want) in anchors)
        {
            var expected = ThemeHost.ToColor(want);
            foreach (var dictionary in PaintedDictionaries)
            {
                var got = ThemeHost.ColorOf(dictionary, key);
                if (got == expected) continue;

                wrong.Add($"{dictionary}/{key}={(got is null ? "未定义" : Format(got.Value))}（应为 {Format(expected)}）");
            }
        }

        check("主题色值落地", wrong.Count == 0,
            wrong.Count == 0
                ? $"{anchors.Length} 个锚点在两个字典里都是 {theme.Name} 的色值，" +
                  $"窗口底 {Format(ThemeHost.ToColor(theme.Colors.Window))}、强调色 {Format(ThemeHost.ToColor(theme.Colors.Accent))}"
                : string.Join("；", wrong));
    }

    /// <summary>
    /// That <c>Theme/Styles.xaml</c> really merged, and that the two faces it names really resolved.
    /// <para>
    /// The key table is the cheap half. The interesting half is the fonts: a <c>FontFamily</c> is a string,
    /// so asking one whether 「Bahnschrift SemiCondensed」 exists on the machine gets the string back either
    /// way — a missing face falls silently through to the next name in the list and the app comes up looking
    /// ordinary rather than broken. So both are answered by measuring instead: the display face is
    /// engineered-condensed and has to set the same Latin string narrower than the UI face, and the data
    /// face is monospaced, which means <c>IIII</c> and <c>MMMM</c> have to come out the same width. Equal
    /// widths in the first and unequal in the second are exactly what 「it fell through」 looks like.
    /// </para>
    /// </summary>
    private static void ReportStyles(StringBuilder report, Action<string, bool, string> check)
    {
        var resources = Application.Current.Resources;
        var wrong = new List<string>();

        foreach (var (key, want) in StyleKeys)
        {
            if (!resources.TryGetValue(key, out var found))
            {
                wrong.Add($"{key} 缺");
                continue;
            }

            if (!want.IsInstanceOfType(found))
                wrong.Add($"{key} 是 {found?.GetType().Name ?? "null"}（应为 {want.Name}）");
        }

        check("样式词表", wrong.Count == 0, wrong.Count == 0
            ? $"{StyleKeys.Length} 个键都在，类型都对"
            : string.Join("；", wrong.Take(6)) + (wrong.Count > 6 ? " 等" : ""));

        const string latin = "MEDIA LIBRARY 1080";
        var display = Measured(latin, "EgDisplayFontFamily", 34);
        var ui = Measured(latin, "EgUiFontFamily", 34);
        var narrower = display > 0 && ui > 0 && display < ui * 0.98;

        var thin = Measured("IIIIIIII", "EgDataFontFamily", 14);
        var wide = Measured("MMMMMMMM", "EgDataFontFamily", 14);
        var monospaced = thin > 0 && Math.Abs(thin - wide) < 0.5;

        check("字体已解析", narrower && monospaced,
            $"标题字 {display:0.#} vs 正文字 {ui:0.#}（窄 {(ui > 0 ? 1 - display / ui : 0):P0}）"
            + $"，数字字 I/M {thin:0.#}/{wide:0.#}"
            + (narrower ? "" : " — 标题字没窄下来，Bahnschrift SemiCondensed 大概没装上，落回了 YaHei")
            + (monospaced ? "" : " — 数字字不等宽，Cascadia Mono 和 Consolas 都没落上"));

        report.AppendLine($"[信息] 字号阶 — 眉 {resources["EgEyebrowFontSize"]}、说明 {resources["EgCaptionFontSize"]}、"
            + $"正文 {resources["EgBodyFontSize"]}、小标题 {resources["EgSubheadFontSize"]}、分区 {resources["EgTitleFontSize"]}、"
            + $"页面 {resources["EgHeaderFontSize"]}、片名 {resources["EgDisplayFontSize"]}");
    }

    /// <summary>
    /// How wide one string sets in one of the palette's faces. Measured off the tree — a <c>TextBlock</c>
    /// with no parent still measures, which is what makes this answerable without a page.
    /// </summary>
    private static double Measured(string text, string fontKey, double size)
    {
        if (!Application.Current.Resources.TryGetValue(fontKey, out var found) || found is not FontFamily font)
            return 0;

        var block = new TextBlock { Text = text, FontFamily = font, FontSize = size };
        block.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return block.DesiredSize.Width;
    }

    /// <summary>The two dictionaries <c>ThemeHost</c> writes; <c>HighContrast</c> is deliberately not one.</summary>
    private static readonly string[] PaintedDictionaries = ["Default", "Light"];

    /// <summary>
    /// Every key <c>Theme/Styles.xaml</c> is supposed to publish, and the type it has to be. A missing one is
    /// not a soft failure: <c>{StaticResource}</c> against an absent key throws while the page is being
    /// parsed, so the page it is on simply never comes up.
    /// </summary>
    private static readonly (string Key, Type Want)[] StyleKeys =
    [
        ("EgUiFontFamily", typeof(FontFamily)),
        ("EgDisplayFontFamily", typeof(FontFamily)),
        ("EgDataFontFamily", typeof(FontFamily)),
        ("ContentControlThemeFontFamily", typeof(FontFamily)),
        ("EgEyebrowFontSize", typeof(double)),
        ("EgCaptionFontSize", typeof(double)),
        ("EgBodyFontSize", typeof(double)),
        ("EgSubheadFontSize", typeof(double)),
        ("EgTitleFontSize", typeof(double)),
        ("EgHeaderFontSize", typeof(double)),
        ("EgDisplayFontSize", typeof(double)),
        ("EgEyebrowSpacing", typeof(int)),
        ("EgCardCornerRadius", typeof(CornerRadius)),
        ("EgPosterCornerRadius", typeof(CornerRadius)),
        ("EgBleedCornerRadius", typeof(CornerRadius)),
        ("EgHairline", typeof(Thickness)),
        ("EgPageMargin", typeof(Thickness)),
        ("EgCornerBadgeSize", typeof(double)),
        ("EgProgressThickness", typeof(double)),
        ("EgActionHeight", typeof(double)),
        ("EgEyebrowStyle", typeof(Style)),
        ("EgPageTitleStyle", typeof(Style)),
        ("EgDisplayTitleStyle", typeof(Style)),
        ("EgSectionTitleStyle", typeof(Style)),
        ("EgSubheadStyle", typeof(Style)),
        ("EgDataStyle", typeof(Style)),
        ("EgDataStrongStyle", typeof(Style)),
        ("EgBodyStyle", typeof(Style)),
        ("EgCaptionStyle", typeof(Style)),
        ("EgOnScrimEyebrowStyle", typeof(Style)),
        ("EgOnScrimBodyStyle", typeof(Style)),
        ("EgOnScrimDataStyle", typeof(Style)),
        ("EgOnScrimDataStrongStyle", typeof(Style)),
        ("EgOnScrimPageTitleStyle", typeof(Style)),
        ("EgRuleStyle", typeof(Style)),
        ("EgFrameStyle", typeof(Style)),
        ("EgFrameActiveStyle", typeof(Style)),
        ("EgCornerBadgeStyle", typeof(Style)),
        ("EgCornerBadgeTextStyle", typeof(Style))
    ];

    /// <summary>
    /// The keys the palette's <paramref name="theme"/> dictionary defines, empty when there is no such
    /// dictionary. Read off what the app merged rather than off the file, so what gets compared is what is
    /// really in force.
    /// </summary>
    private static HashSet<string> PaletteKeys(string theme)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var merged in Application.Current.Resources.MergedDictionaries)
        {
            // Ours, by Source. XamlControlsResources is merged right alongside it and brings a Default and
            // a Light of its own, and whether the framework's two agree is not this app's business.
            if (merged.Source is null ||
                !merged.Source.ToString().EndsWith("Palette.xaml", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!merged.ThemeDictionaries.TryGetValue(theme, out var found) ||
                found is not ResourceDictionary entries)
                continue;

            foreach (var key in entries.Keys)
                if (key is string name) keys.Add(name);
        }

        return keys;
    }

    private static string ColorOf(string key)
    {
        try
        {
            return Application.Current.Resources[key] is SolidColorBrush brush
                ? Format(brush.Color)
                : "未解析到画刷";
        }
        catch (Exception)
        {
            // An absent key throws rather than returning null, and "missing" is the answer we want.
            return "未定义";
        }
    }

    private static string Format(Windows.UI.Color color) =>
        $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>
    /// What the desktop really shows where our client area is.
    /// </summary>
    /// <param name="Rendered">
    /// True only when our own window is the one on screen there and what it shows is more than one flat
    /// colour. One colour across the whole client area is the window's own fallback fill: the XAML island
    /// covers that area whenever it composites anything at all, so seeing the fill means it composited
    /// nothing.
    /// </param>
    private sealed record ScreenReading(bool Rendered, string Detail);

    /// <summary>
    /// Reads the desktop at five points inside our own client area. GetPixel returns a COLORREF
    /// (0x00BBGGRR), so the bytes come back in the opposite order to the way colours are written.
    /// <para>
    /// Five points and a named owner rather than one bare colour, because the three ways this can fail
    /// need telling apart and the report is the only place they can be: nothing sampled at all, someone
    /// else's window sitting over ours — which says nothing about our own drawing — and our window
    /// showing only its fallback fill, which is the compositor having dropped the island. That last one
    /// used to read as a pass, and finding out what it really was took a session of probing from outside.
    /// </para>
    /// </summary>
    private static ScreenReading SampleScreen(HostWindow window, int width, int height)
    {
        if (window.Handle == IntPtr.Zero || width <= 0 || height <= 0)
            return new ScreenReading(false, "取样失败 —— 没有窗口");

        // Spread over regions that differ in any healthy layout: the pane, the header strip, the middle
        // of the content and two places well inside it.
        // 侧边栏那一点贴着窗口左边取（24 个物理像素），不取内容宽度的八分之一：侧边栏默认是收起来的
        // （「侧边栏默认为折叠状态」），窄条只有 48 逻辑像素宽，八分之一早就落到内容里去了。而这份取样要的
        // 就是「两块底色不一样」—— 走到这里时台面上是空的搜索页，一整片都是页面底色，少了侧边栏那一条，五个点
        // 会取出同一个颜色，于是这份检查会去报「XAML 岛什么也没合成」，说的却不是那件事。
        (int X, int Y)[] spots =
        [
            (width / 2, height / 2),
            (24, height / 2),
            (width / 2, height / 12),
            (width * 3 / 4, height / 4),
            (width / 2, height * 11 / 12)
        ];

        var deviceContext = Native.GetDC(IntPtr.Zero);
        if (deviceContext == IntPtr.Zero) return new ScreenReading(false, "取样失败 —— 取不到屏幕 DC");

        var colors = new List<string>();
        string? owner = null;
        var ours = false;

        try
        {
            foreach (var (x, y) in spots)
            {
                var point = new NativePoint { X = x, Y = y };
                if (!Native.ClientToScreen(window.Handle, ref point)) continue;

                if (owner is null) (ours, owner) = OwnerAt(window, point);

                var colorRef = Native.GetPixel(deviceContext, point.X, point.Y);
                if (colorRef == 0xFFFFFFFF) continue; // CLR_INVALID

                var r = colorRef & 0xFF;
                var g = (colorRef >> 8) & 0xFF;
                var b = (colorRef >> 16) & 0xFF;
                colors.Add($"#{r:X2}{g:X2}{b:X2}");
            }
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, deviceContext);
        }

        if (colors.Count == 0) return new ScreenReading(false, $"取样失败 —— {owner ?? "取不到坐标"}");

        var shown = string.Join("、", colors.Distinct());

        if (!ours)
            return new ScreenReading(false, $"{shown}，但屏幕上是{owner} —— 被遮挡，与本进程画了什么无关");

        return colors.Distinct().Count() > 1
            ? new ScreenReading(true, $"{shown}（{owner}）")
            : new ScreenReading(false, $"整片 {shown} —— {Explain(colors[0])}，XAML 岛没有合成任何东西");
    }

    /// <summary>Whose window is on screen at <paramref name="point"/>, and whether it is one of ours.</summary>
    private static (bool Ours, string Text) OwnerAt(HostWindow window, NativePoint point)
    {
        var hit = Native.WindowFromPoint(point);
        if (hit == IntPtr.Zero) return (false, "空窗口");

        var name = ClassOf(hit);

        // The island is a child window of our own, so the root is what identifies the app: a hit on the
        // island and a hit on the frame are both us.
        if (hit == window.Handle || hit == window.IslandHandle) return (true, name);

        return Native.GetAncestor(hit, Native.GaRoot) == window.Handle
            ? (true, name)
            : (false, $"{name}（hwnd=0x{hit:X}，不是本窗口）");
    }

    private static string ClassOf(IntPtr hwnd)
    {
        var buffer = new char[256];
        var length = Native.GetClassName(hwnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : "未知窗口类";
    }

    /// <summary>
    /// What a single flat colour over the whole client area means. Both answers are the window painting
    /// its own background, which <see cref="HostWindow"/> only ever gets to show when the island above it
    /// draws nothing.
    /// </summary>
    private static string Explain(string color)
    {
        // Read off the palette in force rather than written in, so a changed base colour cannot leave
        // this saying the wrong thing. ColorOf yields #AARRGGBB; the screen has no alpha.
        var window = ColorOf("EgWindowBrush");
        if (window.Length == 9 && string.Equals(color, $"#{window[3..]}", StringComparison.OrdinalIgnoreCase))
            return "正是窗口底色";

        return color == "#FFFFFF" ? "白底，连底色都没擦过" : "单一颜色";
    }

    private static void Dump(DependencyObject node, StringBuilder into, int depth)
    {
        // Depth-limited on purpose: a NavigationView's own template is around fifteen levels deep and
        // the whole point of the dump is to be readable.
        if (depth > 12) return;

        var name = node is FrameworkElement element && !string.IsNullOrEmpty(element.Name)
            ? $" x:Name={element.Name}"
            : string.Empty;

        var size = node is FrameworkElement sized
            ? $" {sized.ActualWidth:0}x{sized.ActualHeight:0}"
            : string.Empty;

        into.AppendLine($"{new string(' ', depth * 2)}{node.GetType().Name}{name}{size}");

        var children = VisualTreeHelper.GetChildrenCount(node);
        for (var index = 0; index < children; index++)
            Dump(VisualTreeHelper.GetChild(node, index), into, depth + 1);
    }

    /// <summary>
    /// Turns a UI-thread crash into the report the walk never got to write. Called from
    /// <see cref="App"/>'s XAML exception handler, which fires while the process is on its way down, so
    /// this does the least it can: name the page it died on and the exception, and stop the timer so a
    /// tick cannot arrive on a half-torn-down shell.
    /// </summary>
    public static void ReportCrash(StartupOptions options, Exception? error, string message)
    {
        _timer?.Stop();
        ExitCode = 1;

        var where = _stage switch
        {
            0 => "主页",
            1 => "媒体库页面",
            2 => "服务器页面",
            3 => "诊断页面",

            // The settings stage walks its own cards, so which one it died on is worth saying: a template
            // that throws does so the first time its card is opened, and that is the card named here.
            _ => _settings.Count > 0 ? $"设置页面（「{_settings[^1].Category}」之后）" : "设置页面"
        };
        Write(options, "selfcheck-shell.txt", $"""
            {AppIdentity.TitleWithVersion} — WinUI 3 外壳自检
            时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}
            数据目录：{options.Paths.Root}

            [失败] 界面线程未处理异常 — 走到「{where}」时崩溃
            {error?.GetType().FullName ?? "未知异常"}：{message}

            {error?.StackTrace ?? "（没有调用栈）"}

            结果：1 项失败
            """);
    }

    /// <summary>
    /// Writes a PNG of the shell beside the report, under <c>--dump-ui</c>.
    /// <para>
    /// <see cref="RenderTargetBitmap"/> draws the visual tree through the compositor into a bitmap of our
    /// own, which is a different question from the one <see cref="SampleScreen"/> asks: this says what the
    /// app drew, the pixel probe says what the display showed. When they disagree, the fault is between
    /// the two — the island, the compositor, the driver — and not in any of the pages.
    /// </para>
    /// <para>
    /// Never throws: an unwritten picture is a worse report, not a failed run, and the exit code has
    /// already been decided by then.
    /// </para>
    /// </summary>
    private static async Task ShootAsync(ShellPage shell, StartupOptions options)
    {
        try
        {
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(shell);

            var buffer = await bitmap.GetPixelsAsync();
            var pixels = new byte[buffer.Length];
            DataReader.FromBuffer(buffer).ReadBytes(pixels);

            var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);

            // RenderTargetBitmap hands back premultiplied BGRA at the island's own pixel size, so the
            // 96 dpi written here is nominal: the bitmap is already scaled and there is nothing to undo.
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth,
                (uint)bitmap.PixelHeight,
                96,
                96,
                pixels);

            await encoder.FlushAsync();

            var bytes = new byte[stream.Size];
            var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);

            options.Paths.EnsureCreated();
            var path = Path.Combine(options.Paths.LogDirectory, "selfcheck-shell.png");
            File.WriteAllBytes(path, bytes);

            Log.Info(Category, $"界面截图已写出 {bitmap.PixelWidth}x{bitmap.PixelHeight} → {path}");
        }
        catch (Exception error)
        {
            Log.Warn(Category, "界面截图失败", error);
        }
    }

    private static void Write(StartupOptions options, string name, string content)
    {
        try
        {
            options.Paths.EnsureCreated();
            File.WriteAllText(Path.Combine(options.Paths.LogDirectory, name), content, Encoding.UTF8);
        }
        catch (Exception error)
        {
            Log.Error(Category, $"写入自检报告 {name} 失败", error);
        }
    }
}

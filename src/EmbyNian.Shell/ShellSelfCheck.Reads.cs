using EmbyNian.Configuration;
using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Infrastructure;
using EmbyNian.Mpv;
using EmbyNian.Playback;
using EmbyNian.Services;
using EmbyNian.Shell.Interop;
using EmbyNian.Shell.Platform;
using EmbyNian.Shell.ViewModels;
using EmbyNian.Shell.Views;
using EmbyNian.Shell.Windowing;
using Microsoft.Extensions.DependencyInjection;

namespace EmbyNian.Shell;

/// <summary>
/// 自检的第二段：把活着的那棵树读成一份读数。首页、罩子的墨、卡片、媒体库、服务器、诊断、详情页、图集、
/// 媒体信息、季，还有那两个要等一等的（<c>ProbeSeasonsAsync</c>、<c>ProbePickerAsync</c>）。
/// <para>
/// 读和判分开是有原因的：读只能在那一页还在屏上的那一拍做，判要等到报告那一刻，中间隔着好几站 ——
/// 所以这里交回的都是一份记下来的读数（那些 <c>record</c> 在主文件上），断言在 <c>.Pages</c> 那一段。
/// 拆成几个文件的缘由见主文件 <see cref="ShellSelfCheck"/> 的类注释。
/// </para>
/// </summary>
internal static partial class ShellSelfCheck
{
    /// <summary>
    /// 自检：「窗口关了就忘了自己多大」那一条修好了没有。三件事，都是屏上和单测都看不见的：
    /// <list type="number">
    /// <item>窗口正在往下记 —— <c>HostWindow.Placement</c> 里那个矩形和现在真的量出来的一样，而且没把自检这一
    /// 次当成最大化。记漏了的话屏上一模一样，只有下次开窗才看得出，而那时候已经晚了。</item>
    /// <item>存档里那一份摆到这台机器上仍然落在某块真屏幕里。编三个矩形过一遍
    /// <c>ScreenPlacement.Restore</c>：一个正常的、一个整个在桌面外面的（显示器拔了）、一个比屏幕还大的
    /// （换了小屏）。**这一条只有真显示器答得出** —— 单测里的屏幕是我编的，这里的是这台机器现在接着的那几块。
    /// 答错的下场是一个标题栏在桌面外面的窗口：鼠标拖不动、也点不到关闭。</item>
    /// <item>这一次带着 <c>--screen</c>（自检默认就带），所以窗口**没有**去读存档 —— 报告里那些几何读数因此
    /// 每次可比。哪天有人把这个前提去掉，客户区尺寸和 16:9 那两行就会跟着用户上次拉到多大变。</item>
    /// </list>
    /// </summary>
    private static (bool Ok, string Detail) ReportRememberedWindow(
        HostWindow window,
        AppSettings settings,
        StartupOptions options)
    {
        var (recorded, maximized) = window.Placement;

        var live = Native.GetWindowRect(window.Handle, out var rect)
            ? new WindowBounds(rect.Left, rect.Top, rect.Right, rect.Bottom)
            : default;

        var tracks = live.Width > 0 && recorded == live;
        var ok = tracks && !maximized;

        var ui = settings.Ui;
        var archived = ui.WindowWidth > 0 && ui.WindowHeight > 0
            ? $"{ui.WindowWidth}×{ui.WindowHeight} @ {ui.WindowLeft},{ui.WindowTop}"
                + (ui.WindowMaximized ? "、最大化" : "")
            : "还没记过" + (ui.WindowMaximized ? "（但记着最大化）" : "");

        // 这台机器现在接着的那几块屏，主屏在前 —— 和开窗那一刻问的是同一份。
        var screens = HostWindow.WorkAreas();
        var seats = new List<string>();

        if (screens.Count == 0)
        {
            ok = false;
            seats.Add("问不出屏幕");
        }
        else
        {
            // 三种存档，一个都不许摆到桌面外面去。第一个照着这个窗口现在的样子编（正常那一档），另两个是真出过
            // 事的那两种：显示器拔掉、换成更小的屏。
            var main = screens[0];
            (string Name, WindowBounds Saved)[] cases =
            [
                ("原样", new WindowBounds(main.Left + 40, main.Top + 40, main.Left + 40 + 1200, main.Top + 40 + 700)),
                ("屏幕拔了", new WindowBounds(-9000, -9000, -9000 + 1200, -9000 + 700)),
                ("屏幕变小", new WindowBounds(main.Left, main.Top, main.Left + 30000, main.Top + 30000))
            ];

            foreach (var (name, saved) in cases)
            {
                var seat = ScreenPlacement.Restore(saved, screens, window.MinimumClientSize.Width, window.MinimumClientSize.Height);

                var inside = seat.Width > 0 && screens.Any(screen =>
                    seat.Left >= screen.Left && seat.Top >= screen.Top
                    && seat.Right <= screen.Right && seat.Bottom <= screen.Bottom);

                if (!inside) ok = false;
                seats.Add($"{name} → {seat.Width}×{seat.Height} @ {seat.Left},{seat.Top}{(inside ? "" : " 出界了")}");
            }
        }

        // 命令行点过名的那一次不许沿用存档，否则报告里的几何就没了基准。自检默认带 --screen（副屏），所以这
        // 一条正常总是走「没沿用」那一支；有人哪天把默认改成不带，它当场变成一句真的断言。
        var directed = options.Screen != ScreenPlacement.WhereverWindows;
        var adopted = ui.WindowWidth > 0 && recorded.Width == ui.WindowWidth && recorded.Height == ui.WindowHeight;
        if (directed && adopted) ok = false;

        return (ok,
            $"记下来的是 {recorded.Width}×{recorded.Height} @ {recorded.Left},{recorded.Top}"
                + $"（现场量 {live.Width}×{live.Height} @ {live.Left},{live.Top}，一致={tracks}）"
                + $"、最大化={maximized}；设置里存着 {archived}；"
                + $"{screens.Count} 块屏上试摆：{string.Join("、", seats)}；"
                + (directed
                    ? $"这一次 --screen 点了名，没沿用存档={!adopted}"
                    : "这一次是普通启动，开窗时沿用了存档"));
    }

    /// <summary>
    /// 自检：帧同步这一次会落在哪。两件屏上和单测都看不见的事：
    /// <list type="number">
    /// <item><b>这块屏的刷新率读得出来吗。</b> 三步 Win32（窗口 → 显示器句柄 → 设备名 → 当前显示模式）里任何一步
    /// 失败，<see cref="MpvOutputOptions.ResolveSync"/> 收到的就是 0，「超过 120Hz 回到音频同步」那条规则于是永远
    /// 不出手 —— 画面照旧、日志照旧、闸门照旧全绿，只有显卡占用悄悄高一倍（实测 24.7% → 50.1%）。这正是「只有真
    /// 显示器答得出」的那一类，和上面那条摆窗口的一样。</item>
    /// <item><b>按这块屏算出来的结论是什么。</b> 写进报告，因为它取决于自检窗口落在哪块屏上：默认带
    /// <c>--screen 1</c> 的话读的是副屏，不是他平时看片的那块。</item>
    /// </list>
    /// <para>
    /// 片源那半边留空（<c>source: null</c>）：自检不放片子，所以这里只能问「这块屏本身够不够格」。
    /// </para>
    /// </summary>
    private static (bool Ok, string Detail) ReportDisplaySync(HostWindow window, AppSettings settings)
    {
        var hz = window.RefreshHz();
        var (sync, interpolation, standDown) = MpvOutputOptions.ResolveSync(settings.Video, null, hz);

        // 接着显示器就该读得出来，所以读不出来是坏了而不是「这台机器没有」。上限拦的是明显不像刷新率的数；
        // 0 和 1 那两个「硬件默认」的回答 RefreshHz 已经折成 0。
        var ok = hz is > 0 and < 1000;

        var live = sync.Length == 0
            ? "音频同步（不发这个选项）"
            : MpvOutputOptions.Describe(MpvOutputOptions.VideoSync, sync);

        return (ok,
            (hz > 0 ? $"这块屏 {hz:0.###}Hz" : "读不出刷新率")
                + $"；设置里插值={(settings.Video.Interpolation ? "开" : "关")}"
                + $"、回退={(settings.Video.HighFrameRateAudioSync ? "开" : "关")}"
                + $"；算出来 video-sync={live}、插值{(interpolation ? "生效" : "不生效")}"
                + (standDown is null ? "" : $"；{standDown}"));
    }

    /// <summary>
    /// The home page's own state, read from wherever the frame happens to be. One reader rather than
    /// the same tuple built at both call sites: the check reads this twice — once before it walks off
    /// the page and once at report time, whichever came first — and two copies of the 「is it even the
    /// home page」 test is one copy too many.
    /// </summary>
    private static (bool Correct, int Cards, string Shelves, string Banner, bool TypeOk, string Type, bool PictureOk,
        string Picture, bool LayoutOk, string Layout, bool BleedOk, string Bleed, bool? FoldOk, string Fold) ReadHome(ShellPage shell)
    {
        if (shell.Pages.Content is not HomePage home)
            return (false, -1, "未读取", "未读取", false, "未读取", false, "未读取", false, "未读取", false, "未读取",
                false, "未读取");

        var (typeOk, type) = home.BannerType();
        var (pictureOk, picture) = home.BannerPicture();
        var (layoutOk, layout) = home.LayoutRead();
        var (bleedOk, bleed) = home.BleedRead();
        var (foldOk, fold) = shell.ProbeHomeFold();
        return (shell.CurrentTag == "home", home.LoadedCount, home.ShelfSummary, home.BannerSummary, typeOk, type,
            pictureOk, picture, layoutOk, layout, bleedOk, bleed, foldOk, fold);
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

        var (card, cellWidth, cellHeight) = library.Sizing;

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

        // Episodes counted across both scroll positions, cast at whichever one this is. 单集 sits near the top
        // of the paper and cast is at the bottom, and an ItemsRepeater unbuilds what leaves its viewport — so
        // the pre-scroll reading taken in ScrollDetail is the one that has seen 单集, and this one is the only
        // one that ever sees 演职人员. Both of 单集's shapes are carried separately: the hidden one realises
        // nothing, and that zero is the evidence this page kind picked the other.
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
        var (fitOk, fit) = page.PickerFit();
        var (washOk, wash) = page.WashRead(shell.TrailBase);
        var (stillOk, still) = page.StillShape();
        var (footerOk, footer) = page.FooterRead();

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
            fitOk,
            fit,
            washOk,
            wash,
            stillOk,
            still,
            footerOk,
            footer);

        // A list with rows has to point at one of them; an empty list has to point at nothing.
        static bool Holds<T>(IReadOnlyList<T> rows, T? chosen) where T : class =>
            rows.Count == 0 ? chosen is null : chosen is not null && rows.Contains(chosen);
    }

    /// <summary>
    /// 需求 4 as this page came out: which picture the rule says belongs above the title and which one in the
    /// top-right corner, whether each of them or only the text title actually drew, where they landed, and
    /// which of the five artworks the server holds for this item — plus whose picture the band behind them
    /// stands on（「集页面要用这个剧的背景图或缩略图」）.
    /// <para>
    /// Read off the elements rather than off the view model, because what is worth testing is the markup: the
    /// text title has to be there on every page (the plate used to replace it, and 「no name on the hero band」
    /// looks exactly like artwork that never arrived over the network), and each picture has to be in the place
    /// it was moved to rather than back on the words or on the poster. See <see cref="DetailPage.PlateShape"/>
    /// and <see cref="DetailPage.CornerShape"/>.
    /// </para>
    /// </summary>
    private static ArtworkRead ReadArtwork(DetailPage page, DetailViewModel model)
    {
        var item = model.CurrentItem;
        var (heroOk, hero) = Band(item);

        return new ArtworkRead(
            Mark(page.PlateShape, Wanted(item, ItemArtwork.Plate(item), "名牌")),
            Mark(page.CornerShape, Wanted(item, ItemArtwork.Corner(item), "角上那张画")),
            page.TitleDrawn,
            item is null ? "没有条目" : ItemArtwork.Kinds(item),
            hero,
            heroOk);

        // 屏上量到的那一片几何，配上规矩那一句答案，凑成一格读数。
        static MarkRead Mark(
            (bool Drawn, double Width, double Height, bool Placed, string Where) shape, string wanted) =>
            new(wanted, shape.Drawn, shape.Width, shape.Height, shape.Placed, shape.Where);

        // 「统一改为在剧名上方显示徽标，右上角显示艺术图」：这一句就是规矩给的答案，用词说。两个位置各问各的那一支
        // （ItemArtwork.Plate 和 ItemArtwork.Corner），因为它们之间不再互相退档。id 和种类一样要紧：集页和季页上
        // 徽标是剧集那一头发的，按这一页自己的 id 去取会取回一个空答案，而「徽标」两个字自己说不出走了哪一条路。
        static string Wanted(EmbyItem? item, ArtworkRef? pick, string what)
        {
            if (item is null) return "没有条目";
            if (pick is not { } chosen) return $"无（这一条没有能当{what}的图）";

            var kind = ItemArtwork.Name(chosen.ImageType);

            return chosen.ItemId == item.Id ? $"自己的{kind}" : $"剧集的{kind}（取自条目 {chosen.ItemId}）";
        }

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

        // Which properties the switch announces, not merely which values it ends up with. 播放按钮上的字是
        // 从 PlayTarget 上靠一个特性扇出来的算得属性，事后读一遍它的值不管那个特性在不在都会通过 —— 页面只绑
        // 一次，之后靠通知。
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
            && page.PlayText == ItemDetail.PlayText(wanted)
            && announced.Contains(nameof(DetailViewModel.PlayText));

        return (switched,
            $"《{series.Name}》季 {page.Seasons.Count} 行，打开在「{opened?.Name ?? "无"}」"
                + $"（规则应为「{expected?.Name ?? "无"}」）；切到「{target.Name}」后 {rows.Count} 集"
                + $"{(strayed > 0 ? $"，其中 {strayed} 集不属于该季" : "，全部属于该季")}，"
                + $"标题「{heading}」，播放目标「{page.PlayTarget?.Name ?? "无"}」{Code(page.PlayTarget)}，"
                + $"按钮「{page.PlayText}」"
                + $"（该季应播「{(wanted is null ? "无" : ItemDetail.EpisodeLabel(wanted))}」）"
                + (announced.Contains(nameof(DetailViewModel.PlayText)) ? "（已通知）" : "（**没有通知**）"));
    }

    /// <summary>
    /// 自检：「更多」菜单上那四条会弹表的命令，表本身搭得起来。
    /// <para>
    /// 只造不弹（造出来就够）：<c>InitializeComponent</c> 那一步会把标记整棵解析一遍，而这一族最可能的坏法正是
    /// 那里 —— 引用了一个不存在的样式键、或者一个模板的 <c>x:DataType</c> 指着一个非公开的类。它抛的是构造函数
    /// 里的异常，屏上的症状只有提示条上一句「……失败」，而菜单看着一切正常。
    /// </para>
    /// <para>
    /// 用编出来的条目而不是服务器上的：这四张表不问服务器（要问的那几趟在 <c>ItemCommands</c> 里，弹表之前就
    /// 问完了），所以这一关和网络无关，一台连不上服务器的机器上照样咬得住。
    /// </para>
    /// </summary>
    private static (bool Ok, string Detail) ReadDialogs()
    {
        var item = new EmbyItem { Id = "selfcheck", Name = "自检用的条目", Type = EmbyItemType.Movie };
        var source = new MediaSource { Id = "1", Container = "mkv" };
        source.MediaStreams.Add(new MediaStream
        {
            Index = 2,
            Type = "Subtitle",
            Codec = "srt",
            Language = "chi",
            IsExternal = true
        });

        var notes = new List<string>();
        var ok = true;

        Try("添加到合集", () => new CollectionDialog(item, [item]));

        Try("修改媒体封面图", () => new CoverDialog(
            item,
            new RemoteImageResult
            {
                Images = [new RemoteImageInfo { Url = "https://selfcheck.invalid/a.jpg", ProviderName = "自检" }],
                Providers = ["自检"]
            },
            _ => Task.FromResult<byte[]>([])));

        Try("搜索和修改字幕", () => new SubtitleDialog(
            item,
            source,
            _ => Task.FromResult(new List<RemoteSubtitleInfo>()),
            _ => Task.CompletedTask,
            _ => Task.CompletedTask));

        Try("编辑元数据", () => new MetadataDialog(item, new ItemMetadataEdit()));

        return (ok, string.Join("；", notes));

        void Try(string what, Func<Microsoft.UI.Xaml.Controls.ContentDialog> build)
        {
            try
            {
                var dialog = build();
                notes.Add($"{what}「{dialog.Title}」");
            }
            catch (Exception error)
            {
                ok = false;
                notes.Add($"**{what} 搭不起来**（{Failure.Describe(error)}）");
            }
        }
    }

    /// <summary>
    /// 「更多」菜单上那几条新命令背后的接口，在真服务器上问一遍。
    /// <para>
    /// 这一条钉的是<b>请求发对了没有</b> —— 路径、参数名、回来那份 JSON 认不认得。四道闸门里没有别的东西碰得到
    /// 它：单元测试里没有服务器，屏上一张菜单画得再好也不代表点下去那一趟发得对，而发错的样子是 400 或者 404，
    /// 只有点了才知道。
    /// </para>
    /// <para>
    /// <b>只问不改。</b>三条是 GET；刷新那一条走的是 <c>ValidationOnly</c> —— 那一档服务器什么都不改，用来确认
    /// 这条 POST 的路由和参数真的通着，而不用拿用户的真实媒体库去试一次覆盖。会写东西的那几条（刮削、扫库、
    /// 删除、下载、改封面、挂字幕）自检一律不碰。
    /// </para>
    /// <para>
    /// 判红只判「请求本身错了」：400、404、405，或者答回来的 JSON 解不动（那意味着 DTO 对不上）。连不上、超时、
    /// 403（这个账号不是管理员）都只报不判 —— 那些是网络和账号的事，把它们判红就是把一关的成败交给网络。
    /// </para>
    /// </summary>
    private static async Task<(bool Ok, string Detail)?> ProbeCommandsAsync(IServiceProvider services)
    {
        var session = services.GetRequiredService<EmbySession>();
        if (!session.IsSignedIn) return null;

        // 自己的一份预算：这一条排在整段走完之后，那时候按拍算的期限早就花完了。
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var notes = new List<string>();
        var ok = true;
        ItemsResult found;

        try
        {
            found = await session.ExecuteAsync((client, token) => client.GetItemsAsync(new ItemQuery
            {
                Recursive = true,
                IncludeItemTypes = [EmbyItemType.Movie, EmbyItemType.Episode],
                Fields = EmbyFields.Files,
                Limit = 1
            }, token), budget.Token).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            // 连找一个条目都没成，那是这台服务器或者这条网线的事，不是这几条命令的事。
            return (true, $"问不到可以拿来试的条目，跳过（{Failure.Describe(error)}）");
        }

        if (found.Items.FirstOrDefault() is not { } file) return (true, "服务器上没有可以拿来问的文件，跳过");


        await Step("合集列表", async token =>
        {
            var list = await session.ExecuteAsync((client, t) => client.GetCollectionsAsync(t), token)
                .ConfigureAwait(true);
            return $"合集 {list.Count} 个";
        }).ConfigureAwait(true);

        await Step("候选封面", async token =>
        {
            var images = await session
                .ExecuteAsync((client, t) => client.GetRemoteImagesAsync(file.Id, EmbyImageStore.Primary, t), token)
                .ConfigureAwait(true);

            return $"候选封面 {images.Images.Count} 张（刮削源 {images.Providers.Count} 家）";
        }).ConfigureAwait(true);

        await Step("刷新元数据", async token =>
        {
            await session
                .ExecuteAsync((client, t) => client.RefreshItemAsync(file.Id, replace: false, t, mode: "ValidationOnly"),
                    token)
                .ConfigureAwait(true);

            return "刷新接口通（只走 ValidationOnly，没改任何东西）";
        }).ConfigureAwait(true);

        // 最后一条：搜字幕要服务器去问字幕站，慢起来是十几秒，前面几条不该等它。
        if (file.DefaultMediaSource is { } source)
        {
            await Step("字幕搜索", async token =>
            {
                var subtitles = await session
                    .ExecuteAsync((client, t) => client.SearchSubtitlesAsync(file.Id, source.Id, "chi", t), token)
                    .ConfigureAwait(true);

                return $"中文字幕 {subtitles.Count} 条";
            }).ConfigureAwait(true);
        }
        else
        {
            notes.Add("字幕搜索 跳过（这个条目没有媒体源）");
        }

        // 「下载一整部剧」靠的是这一句：单集列表点名要 MediaSources 的时候到底带不带回来。带的话一趟就够，不带
        // 的话下每一集之前要各补问一次（见 ItemCommands 的 SaveAsync）—— 两条路都通，可这台服务器走的是哪一条
        // 只有问过才知道，而它决定的是一次下载发 1 趟还是 25 趟请求。
        await Step("单集列表带媒体源", async token =>
        {
            var shows = await session.ExecuteAsync((client, t) => client.GetItemsAsync(new ItemQuery
            {
                Recursive = true,
                IncludeItemTypes = [EmbyItemType.Series],
                Fields = "",
                Limit = 1
            }, t), token).ConfigureAwait(true);

            if (shows.Items.FirstOrDefault() is not { } show) return "单集列表带媒体源 没有剧集可问";

            var episodes = await session
                .ExecuteAsync((client, t) => client.GetEpisodesAsync(show.Id, null, t, EmbyFields.Files), token)
                .ConfigureAwait(true);

            var withSource = episodes.Count(episode => episode.DefaultMediaSource is not null);
            return $"《{show.Name}》{episodes.Count} 集里 {withSource} 集带媒体源";
        }).ConfigureAwait(true);

        return (ok, $"拿《{file.Name}》问的：{string.Join("；", notes)}");

        async Task Step(string what, Func<CancellationToken, Task<string>> work)
        {
            try
            {
                notes.Add(await work(budget.Token).ConfigureAwait(true));
            }
            catch (Exception error) when (Fatal(error))
            {
                ok = false;
                notes.Add($"**{what} 把请求发错了**（{Failure.Describe(error)}）");
            }
            catch (Exception error)
            {
                notes.Add($"{what} 没答上来（{Failure.Describe(error)}）");
            }
        }

        // 请求本身错了：路由不存在、参数服务器不认、或者答回来的 JSON 和 DTO 对不上。
        static bool Fatal(Exception error) => error switch
        {
            EmbyUnreachableException => false,
            EmbyApiException { InnerException: System.Text.Json.JsonException } => true,
            EmbyApiException
            {
                StatusCode: System.Net.HttpStatusCode.BadRequest
                    or System.Net.HttpStatusCode.NotFound
                    or System.Net.HttpStatusCode.MethodNotAllowed
            } => true,
            _ => false
        };
    }
}

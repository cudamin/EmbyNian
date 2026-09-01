using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Playback;
using EmbyNian.Services;
using EmbyNian.Shell.Diagnostics;
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
    /// The home page's own state, read from wherever the frame happens to be. One reader rather than
    /// the same tuple built at both call sites: the check reads this twice — once before it walks off
    /// the page and once at report time, whichever came first — and two copies of the 「is it even the
    /// home page」 test is one copy too many.
    /// </summary>
    private static (bool Correct, int Cards, string Shelves, string Banner, bool TypeOk, string Type, bool BleedOk,
        string Bleed, bool? FoldOk, string Fold) ReadHome(ShellPage shell)
    {
        if (shell.Pages.Content is not HomePage home)
            return (false, -1, "未读取", "未读取", false, "未读取", false, "未读取", false, "未读取");

        var (typeOk, type) = home.BannerType();
        var (bleedOk, bleed) = home.BleedRead();
        var (foldOk, fold) = shell.ProbeHomeFold();
        return (shell.CurrentTag == "home", home.LoadedCount, home.ShelfSummary, home.BannerSummary, typeOk, type,
            bleedOk, bleed, foldOk, fold);
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
            wash);

        // A list with rows has to point at one of them; an empty list has to point at nothing.
        static bool Holds<T>(IReadOnlyList<T> rows, T? chosen) where T : class =>
            rows.Count == 0 ? chosen is null : chosen is not null && rows.Contains(chosen);
    }

    /// <summary>
    /// 需求 4 as this page came out: the 徽标 the item's artwork entitles it to, whether the mark or only the
    /// text title actually drew, where the mark landed, and which of the five artworks the server holds for it —
    /// plus whose picture the band behind it stands on（「集页面要用这个剧的背景图或缩略图」）and whether the
    /// 艺术图 reached the band's bottom-right corner（「把艺术图添加到窗口右下」）.
    /// <para>
    /// Read off the elements rather than off the view model, because what is worth testing is the markup: the
    /// text title has to be there on every page (the mark used to replace it, and 「no name on the hero band」
    /// looks exactly like artwork that never arrived over the network), and the mark has to be in the corner it
    /// was moved to rather than back on the words. See <see cref="DetailPage.TitleShapes"/> and
    /// <see cref="DetailPage.CornerArtShape"/>.
    /// </para>
    /// </summary>
    private static ArtworkRead ReadArtwork(DetailPage page, DetailViewModel model)
    {
        var (plate, text, width, height, corner, where) = page.TitleShapes;
        var item = model.CurrentItem;
        var (heroOk, hero) = Band(item);
        var (cornerArtOk, cornerArt) = CornerArt(page, model, item);

        return new ArtworkRead(Wanted(item), plate, text, width, height, corner, where,
            item is null ? "没有条目" : ItemArtwork.Kinds(item), cornerArtOk, cornerArt, hero, heroOk);

        // 「把艺术图添加到窗口右下」：一句话里两件事 —— 规矩说该不该画（ItemArtwork.Corner，加上集页整个不摆：
        // 那一条带子只有 200 高，摆不下名牌加一张画），和屏上真画了没有、画在哪儿（DetailPage.CornerArtShape）。
        //
        // 「该画却没画」不算失败：位图是从网上解出来的，这一拍还没到手是常态。反过来「不该画却画了」是这一条真
        // 会红的那一种 —— 整页已经站在同一张艺术图上时角上再钉一张，屏上就是同一张图出现两次。
        static (bool Ok, string Detail) CornerArt(DetailPage page, DetailViewModel model, EmbyItem? item)
        {
            var (drawn, artWidth, artHeight, placed, geometry) = page.CornerArtShape;
            var episode = model.ItemType == EmbyItemType.Episode;
            var allowed = item is not null && !episode && ItemArtwork.Corner(item) is not null;
            var wanted = Wanted();

            var says = drawn
                ? $"该有的是{wanted}，画了 {artWidth:0}×{artHeight:0}；{geometry}"
                    + (allowed ? "" : "；**规矩说这个角该空着**")
                : $"该有的是{wanted}，右下角空着{(allowed ? "（有图，这一拍还没解出来）" : "")}";

            return (placed && (!drawn || allowed), says);

            string Wanted()
            {
                if (item is null) return "没有条目";
                if (episode) return "无（单集这一条带子摆不下）";
                if (allowed) return "自己的艺术图";

                return ItemArtwork.Has(item, EmbyImageStore.Art)
                    ? "无（整页已经站在这张艺术图上）"
                    : "无（这一条没有艺术图）";
            }
        }

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
}

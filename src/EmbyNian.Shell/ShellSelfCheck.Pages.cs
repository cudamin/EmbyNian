using System.Text;
using EmbyNian.Configuration;
using EmbyNian.Emby;
using EmbyNian.Infrastructure;
using EmbyNian.Playback;
using EmbyNian.Services;
using EmbyNian.Shell.Platform;
using EmbyNian.Shell.Views;
using Microsoft.Extensions.DependencyInjection;

namespace EmbyNian.Shell;

/// <summary>
/// 一页一关，前半：容器（依赖注入那一箱）、媒体库、登录、详情页、媒体信息、季。判的是 <c>.Reads</c> 那一段
/// 记下来的读数，所以这里一行都不去碰活着的树。
/// <para>
/// 拆成几个文件的缘由见主文件 <see cref="ShellSelfCheck"/> 的类注释。
/// </para>
/// </summary>
internal static partial class ShellSelfCheck
{
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
                : $"「{detail.Target}」（{EmbyItemType.ToChinese(detail.TargetType)}）·「{detail.Play}」");

        // 媒体信息 describes a file, so it is filled on the page of a file and empty everywhere else — the
        // panel's visibility is nothing but 「are there rows」, so this one claim covers both halves. It is
        // asked of a 剧 page here, which is the case that used to be wrong: the table named whichever
        // episode 播放 had resolved to, under a page about the whole show.
        var filePage = EmbyItemType.IsPlayable(detail.Type);

        check("详情媒体信息", filePage ? detail.Info > 0 : detail.Info == 0,
            filePage
                ? $"{detail.Type} 页，{detail.Info} 行"
                : $"{detail.Type} 页，没有这一块（{detail.Info} 行）");

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
        // clicked into an episode. A 电影 page has no 单集 at all, so a model with no rows is reported rather
        // than failed. When the model does have rows, drawing none is a real failure — it is exactly the
        // async-visibility regression this page used to hide as 「没有单集可数」.
        if (_fileEpisodesDrawn is { Models: > 0 } file)
        {
            var fileList = ItemDetail.EpisodesAsList(file.Type);
            check("详情单集形状（文件页）",
                file.Rows + file.Cards > 0 && (fileList ? file.Cards == 0 : file.Rows == 0),
                $"{file.Type} 页要{(fileList ? "竖置列表" : "横向翻页带")}，"
                    + $"模型 {file.Models} 集、实渲染 {file.Rows} 行 / {file.Cards} 张卡");

            if (file.Type == EmbyItemType.Episode)
                check("集页首屏（文件页）", file.ScreenOk, file.Screen);
        }
        else
        {
            report.AppendLine("[信息] 详情单集形状（文件页）— "
                + (_fileEpisodesDrawn is { } empty ? $"{empty.Type} 页模型没有同季单集" : "这次没走到文件页"));
        }

        // Informational: what the server actually had to say about this item. 年份区间 and 制作方 are
        // Fields-gated (see EmbyFields.Detail), so an empty middle segment here is the first place a
        // dropped field name would show — but a library whose metadata is thin is not a defect.
        report.AppendLine($"[信息] 详情元数据 — 「{detail.Facts}」");

        // 需求 4，一路改到「统一改为在剧名上方显示徽标，右上角显示艺术图」：这一条要的是三句话 —— 片名那一行永远
        // 在（没有名字的头图和「图没到」在截图里长得一模一样）、徽标落在片名的正上方（左沿还得跟它对齐）、艺术图
        // 落在带子的右上角，而两张都不许顶出带子、压到海报或者压到对方。服务器给不出某一张的条目占大多数，那一次
        // 那一格空着成立 —— 两个位置各空各的，这正是「不再互相退档」的意思：从前那一版里艺术图缺席就由徽标顶上，
        // 于是同一枚牌子在不同条目上出现在不同地方。几何在 DetailPage.PlateShape 和 CornerShape 里读，「该有哪一
        // 张」是 ItemArtwork.Plate 和 ItemArtwork.Corner 给的答案。
        var artwork = detail.Artwork;

        check("详情徽标与艺术图", artwork.Text && artwork.Plate.Placed && artwork.Corner.Placed,
            $"片名{(artwork.Text ? "在" : "没画")}；{Say("徽标", artwork.Plate)}；{Say("艺术图", artwork.Corner)}");

        // 「海报下方会被裁切，要能看到完整的海报」：那一格原来写死 210×300（0.7:1）、图按 UniformToFill 铺满它，
        // 而服务器上的海报是 2:3，于是上下各裁掉七八像素 —— 海报底下那一条往往正是片名和演员表。现在那一格按
        // 图自己的形状收窄（DetailHero.StillBox），这一条读的就是「这一格和这张图同形、而且拉伸方式没有再改回会
        // 裁的那一种」。几何在 DetailPage.StillShape 里读。
        check("详情海报不裁切", detail.StillShapeOk, $"{detail.Type} 页，{detail.StillShape}");

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

        // 「媒体源／音频／字幕」那三个下拉有没有被窗口右沿切掉。它们的宽度按各自最长那条轨道名撑，所以「一行装
        // 得下」跟这台服务器上的轨道叫什么名字、跟窗口有多宽都有关 —— 原来那个横排 StackPanel 装不下的时候既不
        // 换行也不收窄，只把最右边那个切在窗口边上，而这台机器的副屏是竖屏、浏览区锁了 16:9，窗口就只有一千零
        // 几十像素宽，屏上真的缺了一块。现在里面那一层是 WrapRow，装不下换行；这一条读换完之后没人出界，读数里
        // 那句「摆成几行」就是「这一次窄没窄」的证据。几何在 DetailPage.PickerFit 里读。
        //
        // 这一份是这一页（剧集那一层）的读数。剧页现在也摆这一行了（用户 09-02：「给剧页面加上音频字幕等等的
        // 选择项」）—— 它说的是播放键指着的那一集，所以这一条在剧页上钉的是「有落点才画、而且没人被右沿切掉」。
        // 文件页那一份读数在下面「文件页文件选项」那一条上，见 _filePickers。
        check("文件选项没出界", detail.PickerFitOk, detail.PickerFit);

        // 页尾那张横幅 —— 「在电影页面 剧页面 集页面的底部添加横幅」。判的是落点和两个「该空」（季页不摆、名牌
        // 已经用了这张横幅的条目不摆）：同一张图在一页上出现两次，屏上单看每一处都挺好，只有一起看才看出重了。
        // 几何在 DetailPage.FooterRead 里读，也是滚到底之后读的 —— 那张图就在最底下。
        check("页尾横幅", detail.FooterOk, detail.Footer);

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

        // 需求 4, informational: the five artworks this one item has on the server. 头图、片名头上那一枚和右上角
        // 那一张能画出来的东西都是这一行的子集，所以上面那两句「没画」得对着这一行读 —— 「这一条服务器上没有徽标」
        // 和「徽标没画出来」在屏上长得一模一样，而只有后一种是这份代码的错。
        report.AppendLine($"[信息] 详情图片种类 — {artwork.Kinds}");

        // 需求 4, informational and the half of it code alone could not settle: on the page of an episode the
        // 徽标 is the show's, sent under a different item's id, and whether this server fills that pair at all
        // is a question only its own answer settles. 「剧集的徽标」 here is that answer.
        report.AppendLine(_fileArtwork is { } shown
            ? $"[信息] 文件页徽标与艺术图 — {shown.Type} 页，{Say("徽标", shown.Artwork.Plate)}"
                + $"；{Say("艺术图", shown.Artwork.Corner)}；这一条有 {shown.Artwork.Kinds}"
            : "[信息] 文件页徽标与艺术图 — 这次没走到文件页");

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

        // 同一条规矩在文件页上的那一半。这一页值得单独钉一遍，因为四种页面里它的带子最矮（高按里面那一叠字键实测
        // 给，见 DetailHero.EpisodeHeight），而两张图的高上限都是写死的 —— 「顶出了带子」真有机会发生的就是这一页。
        if (_fileArtwork is { } fileMark)
            check("文件页徽标与艺术图落点",
                fileMark.Artwork.Text && fileMark.Artwork.Plate.Placed && fileMark.Artwork.Corner.Placed,
                $"{fileMark.Type} 页，{Say("徽标", fileMark.Artwork.Plate)}"
                    + $"；{Say("艺术图", fileMark.Artwork.Corner)}");

        // 上面那一条的正主：只有文件页上「媒体源」才有得挑，所以只有这一页会把三个下拉一齐摆出来，也只有这一页
        // 会在窄窗口下真的排不下。读的那一拍是 ShowInfo —— 页面往下滚去看媒体信息表格之前的最后一拍，那之后这
        // 一行就出了视口、量出来的是滚过之后的坐标。读数里「摆成 2 行」就是换行真的接住了；换回横排会直接红。
        if (_filePickers is { } picks)
            check("文件页文件选项", picks.Ok, $"{picks.Type} 页，{picks.Detail}");
        else report.AppendLine("[信息] 文件页文件选项 — 这次没走到文件页");

        // 「点击剧名之后应该进[入]剧页面而不是季页面」：集页上那行大字写的是剧名，落点就得是那部剧。落成季的那一版
        // 屏上一模一样，截图也看不出来 —— 分别只在悬停提示那一句和按下去开的那一页里。
        if (_fileTitleLink is { } titleLink)
            check("集页剧名落点", titleLink.Ok, titleLink.Detail);
        else report.AppendLine("[信息] 集页剧名落点 — 这次没走到文件页");

        // 那一行类型点不点得动。搭空了屏上就是一行看着一模一样的字，点下去什么都不发生 —— 别的读数一个都不响。
        if (_detailGenres is { } genres) check("详情类型可点", genres.Ok, genres.Detail);

        // 「加入显示评分改为豆瓣评分的功能，可在设置使用豆瓣、tmdb、烂番茄等平台的评分」。判的是本应用自己的规矩
        // （规矩说该画什么、屏上就得画什么；服务器没给分就整块收起来）；服务器那一头给了什么字段只报不判 —— 有没有
        // 豆瓣的痕迹是那台机器上装了哪些插件的事，而那一句正是这一关最值得读的：从此每次自检都自己答一遍。
        if (_detailScore is { } score) check("详情评分来源", score.Ok, score.Detail);
        else report.AppendLine("[信息] 详情类型可点 — 这次没走到详情页");

        // 「点封面进详情页要空等一趟服务器往返」：第一屏是拿点进来那张卡片画的，而完整条目回来之后那四张图不许被
        // 重取一遍。两件屏上都看不见 —— 截图只能拍到已经载完的那一页，而白重取一遍是一百毫秒的闪。
        if (_detailPreview is { } preview) check("详情页先用卡片画一屏", preview.Ok, preview.Detail);
        else report.AppendLine("[信息] 详情页先用卡片画一屏 — 这次没走到详情页");
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
}

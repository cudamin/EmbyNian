using System.Text;
using EmbyNian.Shell.ViewModels;
using EmbyNian.Shell.Views;
using EmbyNian.Shell.Windowing;
using Microsoft.UI.Xaml.Controls;

namespace EmbyNian.Shell;

/// <summary>
/// 一页一关，后半：服务器、诊断、设置那几张卡、嵌进设置里的 Emby 控制台、播放器。
/// <para>
/// 拆成几个文件的缘由见主文件 <see cref="ShellSelfCheck"/> 的类注释。
/// </para>
/// </summary>
internal static partial class ShellSelfCheck
{
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
    /// rows of a handful of shapes picked at runtime by a <c>DataTemplateSelector</c>, and nothing about that is
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
    private static void ReportSettings(
        ShellPage shell,
        EmbyNian.Emby.EmbyImageStore images,
        EmbyNian.Playback.AudioDeviceCatalogue audioDevices,
        StringBuilder report,
        Action<string, bool, string> check)
    {
        if (shell.SettingsRoot is not { } page)
        {
            report.AppendLine($"[信息] 设置页面 — 未打开（当前 {shell.CurrentTag ?? "无"}）");
            return;
        }

        // 「新增可在设置中调整图片缓存大小的功能」. Asserted here rather than beside the servers page's own cache
        // line, because what can go wrong is the rope between the two windows: the settings page writes the
        // number into the document and shouts (ShellPrefs), and App hands it to the store. Break that and every
        // reading stays green — the box shows the new number, the file holds it, and the cache goes on trimming
        // to the old one. Nothing on screen says which budget is in force.
        if (page.ViewModel.MeasureImageBudget(images) is { } budget)
            check("图片缓存上限改完当场生效", budget.Ok, budget.Detail);

        var (sections, rows, category, _) = page.Summary;

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

        // The 字幕 card's font picker: whether the machine's families really got into it, whether the search
        // box filters, and whether a search can lose the current value. The last one is the one worth a
        // check — the row's list is also where it shows what the setting is, so a filter that may hide the
        // selected family can leave the row looking unset, and the way out of that would be to pick another
        // font. Nothing here writes: this row only commits when a family is picked, and the probe picks none.
        var picker = page.MeasureFontPicker();
        check("字幕字体列表", picker is { Ok: true },
            picker is null ? "没有建出字体行" : picker.Detail);

        // 「点击选择字幕后要把下方的列表收起来」（2026-09-06）。收和展都是那行的两个状态翻来翻去，哪一边
        // 卡住了屏上都只是那一列名单的形状不对，别的读数一个都不会动 —— 所以这一关就地拨一遍（见
        // SettingFontRow.CollapseProbe），不点屏上那一行。
        var collapse = SettingFontRow.CollapseProbe();
        check("字体列表选完收得起", collapse.Ok, collapse.Detail);

        // 三行字幕颜色（文字/描边/底板）现在装的是任意 HTML 颜色代码。这一关问三件事：三行都在、每一行
        // 存的都是 #RRGGBB 或者「不设置」的空串、以及那一行选个颜色真的写一次盘（SettingColorRow.Probe
        // 在假行上拨）。写盘那根线断了的样子是：拾色器里色块跟着变、设置文件停在旧值上，屏上什么都说。
        var subtitleRows = page.ViewModel.Sections
            .FirstOrDefault(section => section.Category == "字幕")?.Rows ?? [];
        var colorRows = subtitleRows.OfType<SettingColorRow>().ToList();
        var colorsValid = colorRows.Count == 3
            && colorRows.All(row => row.Color.Length == 0
                || EmbyNian.Infrastructure.HtmlColor.TryParse(row.Color, out _));
        var colorProbe = SettingColorRow.Probe();

        check("字幕颜色行装的是 HTML 颜色代码", colorsValid && colorProbe.Ok,
            $"{colorRows.Count} 行颜色（应当 3 行），存值 "
                + string.Join("、", colorRows.Select(row => $"<{row.Label}>={(row.Color.Length == 0 ? "不设置" : row.Color)}"))
                + $"；{colorProbe.Detail}");

        // 「参考图2新增字幕外观功能」（2026-09-06）：字幕卡顶上那条「字幕示例」。问两件事：屏上这一行
        // 真在画（行在、模型读得出示例文字和层数），以及「改设置 → 重画」这根线通不通 —— 在假行上拨一遍
        // （见 SettingSubtitlePreviewRow.Probe），因为这台机器注不进鼠标，而预览不跟着改的坏法恰恰是
        // 什么读数都不动的：外观怎么调，那条示例永远一个样子。
        var previewRow = subtitleRows.OfType<SettingSubtitlePreviewRow>().FirstOrDefault();
        var previewProbe = SettingSubtitlePreviewRow.Probe();
        var previewAlive = previewRow is not null
            && previewRow.Model.Text == SettingSubtitlePreviewRow.Sample
            && previewRow.Model.FontFamily.Length > 0;

        check("字幕示例预览跟着外观走", previewAlive && previewProbe.Ok,
            previewRow is null
                ? "字幕卡上没有预览那一行"
                : $"屏上那条画的是「{previewRow.Model.Text}」（{previewRow.Model.FontFamily}）；{previewProbe.Detail}");

        // 主题那一行的色板。什么都不点：这一行只在点中一块时写盘，而量它不需要真换一次主题 —— 角标落在存着
        // 的那一套上、几块颜色互不相同、屏上块数对得上，坏法就都在这三句里了（见 MeasureThemeSwatches）。
        var swatches = page.MeasureThemeSwatches();
        check("主题色板", swatches is { Ok: true },
            swatches is { } read ? read.Detail : "没有建出主题行");

        // 主页那张卡片里那张换得了次序的表：存着的每一排在表里都在，屏上也都画出来了、也都装得下（见
        // MeasureHomeRows）。什么都不拖也不点 —— 这一行只在换过次序或者点过勾之后写盘。
        var homeRows = page.MeasureHomeRows();
        check("主页版面设置表", homeRows is { Ok: true },
            homeRows is { } layout ? layout.Detail : "没有建出主页版面行");

        // 那张表换得了次序没有 —— 「设置里新增拖拽排序」。两条路一起问：拖那条路按官方配方接齐了没有，以及那两颗
        // 箭头在一张假表上真按得动次序（见 MeasureHomeDrag）。这台机器上注不进鼠标事件，拖的那一下没法自动做一遍，
        // 所以箭头那条路是这件事唯一验得到的形式；一张换不了次序的表在屏上和换得了的一模一样，上面那一行读数也一个
        // 字不差。
        var homeDrag = page.MeasureHomeDrag();
        check("主页版面表换得了次序", homeDrag is { Ok: true },
            homeDrag is { } drag ? drag.Detail : "没有建出主页版面表");

        // 视频同步那一行的说明写的是「此刻真正生效的值」，而改得动它的有三处：它自己、启用插值、高帧率回退。
        // 少接一处，屏上一点区别都看不出来 —— 说明还在，只是说的是上一次的事，也就是这一行本来要治的那个
        // 「界面在骗人」。所以这里问两件：那根线本身通不通（假下拉行按一下，SettingChoiceRow.Probe），以及屏上
        // 这一行真的是「此刻生效」那种说明、而不是一句静态介绍。点不了真下拉：这台机器上注不进鼠标事件。
        var restate = SettingChoiceRow.Probe();
        var liveNote = page.ViewModel.Sections
            .FirstOrDefault(section => section.Category == "视频输出")?.Rows
            .FirstOrDefault(row => row.Label == "视频同步")?.Note ?? "";

        check("视频同步那一行说的是此刻生效的值", restate.Ok && liveNote.Contains("此刻生效"),
            $"{restate.Detail}；屏上那一行写着「{liveNote}」");

        // 「动态范围压缩」下发的是 ad-lavc-ac3drc，那是 AC-3 解码器的选项 —— DTS / TrueHD / AAC / FLAC 轨一律
        // 没有反应。而那一行读起来像是通用的「让对白清楚一点」，所以说明里必须写着适用范围，否则它就是在骗人。
        // 单测进不到外壳这个程序集，而这句话最可能的坏法是哪天被人「整理」掉，所以由自检钉着。同一关顺带确认
        // 「音量均衡」那一行真的在这张卡上 —— 它是那句话里指向的去处，指了个不存在的地方比不指更糟。
        var audioRows = page.ViewModel.Sections
            .FirstOrDefault(section => section.Category == "音频输出")?.Rows ?? [];
        var drcNote = audioRows.FirstOrDefault(row => row.Label == "动态范围压缩")?.Note ?? "";
        var hasNormalize = audioRows.Any(row => row.Label == "音量均衡");

        check("动态范围压缩那一行写明只对 AC-3 有效", drcNote.Contains("AC-3") && hasNormalize,
            $"说明里{(drcNote.Contains("AC-3") ? "有" : "没有")}「AC-3」字样、"
                + $"{(hasNormalize ? "并且" : "但是没有")}「音量均衡」那一行；这张卡 {audioRows.Count} 行");

        // 「mpv.exe 路径」那一行的说明必须写着「命令行」。外部 mpv.exe 那条路把 X-Emby-Token 写在
        // --http-header-fields-append= 上，也就是写在另一个进程的命令行上；内置 libmpv 在进程里设那个头，
        // 所以默认后端没有这件事。三条处置里（临时配置文件、IPC 注入、把话说出来）只有最后一条在这台机器上
        // 验得住 —— 不许真实播放，本机也没装外部 mpv.exe。而一句「把话说出来」的处置全靠那句话真的在屏上，
        // 所以它和「动态范围压缩只对 AC-3 有效」是同一类：最可能的坏法是哪天被人「整理」掉，而单测进不到外壳
        // 这个程序集。顺带确认这张卡三行都在 —— 说明挂在一行不存在的行上等于没有说明。
        var playerRows = page.ViewModel.Sections
            .FirstOrDefault(section => section.Category == "播放器")?.Rows ?? [];
        var pathNote = playerRows.FirstOrDefault(row => row.Label == "mpv.exe 路径")?.Note ?? "";
        var saysCommandLine = pathNote.Contains("命令行", StringComparison.Ordinal);

        check("mpv.exe 路径那一行写明令牌会上命令行", saysCommandLine && playerRows.Count == 3,
            $"说明里{(saysCommandLine ? "有" : "没有")}「命令行」字样；这张卡 {playerRows.Count} 行"
                + $"（应当 3 行：后端、路径、IPC）");

        // 音频输出设备那一行必须始终可用。设备列表要从 mpv 读（临时开一个 libmpv 句柄只为枚举），而那一趟可能
        // 答不上来 —— 找不到 libmpv、没装 WASAPI 输出、机器上没有声卡。那时候这一行必须还剩「跟随系统默认设备」
        // 并且真的选中了一项：一个没选中的 ComboBox 会吃掉下一次点击，而存着的值一个字都不会写回去。
        // 「拔掉的耳机」走的是同一条路 —— Options() 那个「设置文件中的值」单独成项的分支。
        var deviceRow = audioRows.FirstOrDefault(row => row.Label == "音频输出设备") as SettingChoiceRow;
        var devices = audioDevices.Known;

        check("音频输出设备那一行始终选中一项",
            deviceRow is { Choices.Count: > 0, Selected: not null },
            deviceRow is null
                ? "音频输出那张卡上没有这一行"
                : $"下拉 {deviceRow.Choices.Count} 项、选中「{deviceRow.Selected?.Label ?? "（没选中）"}」；"
                    + $"这台机器上可选 {devices.Count} 个设备"
                    + (devices.Count > 0 ? $"，第一个是「{devices[0].Label}」" : "（还没读到或者读不到）"));

        // 「恢复默认设置」那颗按钮，页头右上角。什么都不按 —— 按下去就是把用户这台机器上的设置全清一遍，
        // 而这一关要问的三件事都不需要真按：那颗按钮在不在页头上，指针停上去那句说明写没写清服务器和账号
        // 不动，以及这一页问得出那次确认没有。
        //
        // 说明是这一关盯的东西。按钮原先自己占一张卡，说明铺在卡上 —— 那也是按下之前屏上唯一一处把「哪些
        // 回默认、哪些不动」写全的地方。2026-09-06 按他一句「恢复默认按钮移到右上角，下方的恢复默认页面删除」
        // 卡整个删了、按钮搬进页头，说明缩成那句 ToolTip；按下之后还有对话框把同一件事再讲一遍，所以这句话
        // 不能丢，丢了它按钮就成了「按下去才知道会发生什么」。x:Name 生成的那颗字段是页和自检同程序集里
        // 最直接的读法；ToolTip 从词表来（SettingsPage_ResetButton），读回来看看那句话还在不在。
        //
        // 再往后那半句是这一关存在的理由。对话框要页面的 XamlRoot，所以视图模型只能等页面把 ConfirmRequest
        // 递过来；页面漏了那一句，ConfirmAsync 一律答「否」—— 按钮按下去什么都不发生，屏上一个字都不说，
        // 行数、模板、渲染读数一个都不会差。单测进不到外壳这个程序集，这台机器上也注不进鼠标事件，所以这是
        // 那件事唯一验得到的形式。至于「哪些回默认、哪些不动」，那是 Core 那一头的事（SettingsReset.Restore，
        // 单测钉着）。
        var resetButton = page.ResetButton;
        var resetNote = resetButton is null ? "" : ToolTipService.GetToolTip(resetButton) as string ?? "";
        var saysAccountsStay = resetNote.Contains("不会退出登录", StringComparison.Ordinal);
        var canAsk = page.ViewModel.CanConfirm;

        check("恢复默认设置那颗按钮问得出确认", resetButton is not null && saysAccountsStay && canAsk,
            resetButton is null
                ? "页头右上角没有那颗按钮"
                : $"「{(resetButton.Content as string ?? "")}」，说明里"
                    + $"{(saysAccountsStay ? "写明了服务器和账号不动" : "没写服务器和账号会怎样")}；"
                    + $"确认对话框{(canAsk ? "已接上页面" : "没接上，按下去会一律当成「取消」")}");

        // The one path this page's cache guard exists for. Pressing 设置 again re-navigates the settings
        // window's frame to this same page type, and a page that rebuilt itself on the way in would throw
        // away which card was open and whatever is half-typed in a box. Checked by object identity and not by
        // counts: a rebuilt page reports exactly the same numbers, so 「these are still the same objects」 is
        // the only question that can tell the two apart.
        var viewModel = page.ViewModel;
        var firstCard = page.ViewModel.Sections.FirstOrDefault();

        shell.ShowSettings();

        var kept = shell.SettingsRoot is { } revisited
            && ReferenceEquals(revisited, page)
            && ReferenceEquals(revisited.ViewModel, viewModel)
            && ReferenceEquals(revisited.ViewModel.Sections.FirstOrDefault(), firstCard);

        check("设置页重进不重建", kept,
            kept ? "再按一次设置，页面和卡片还是原来那些" : "再按一次设置把页面重建了，没提交的输入会丢");
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

        // Before anything reads a colour off this page: every brush in it is declared empty in the markup
        // and painted from PlayerPalette in the constructor, so one the painter missed resolves fine and
        // draws nothing at all. Transparent OSD text over a film is not something a build or a unit test
        // can see — and the test project cannot reference this assembly to begin with.
        var palette = player.ProbePalette();
        check("播放层配色", palette.Ok, palette.Detail);

        // The same kind of fact, one layer over: a name is markup too, and a button whose whole content is a
        // glyph from the icon font's private-use area is announced as 「按钮」 and nothing else. Nine of the
        // transport's twelve were exactly that. Asked of the automation peer, which is what a screen reader
        // asks — the attached property alone would not tell the two kinds of button apart. The handover
        // ring rides along because it fails the same invisible way: it used to spin behind a collapsed grid
        // from launch until exit.
        var narration = player.ProbeNarration();
        check("读屏与焦点", narration.Ok, narration.Detail);

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
        // The cursor goes in the same gate: a probe that hid it and forgot to give it back leaves the whole
        // application without a pointer, and that one is not something a user can work around.
        check("显隐规则已复位",
            !player.ChromeShown && player.CursorRestored,
            $"控件在屏={player.ChromeShown}，指针形状={(player.CursorRestored ? "已交还框架" : "还压着透明光标")}");

        // 「去掉鼠标移到进度条上时进度条出现的白色填充物」. Three layers are stacked along the seek bar and the
        // top one is the framework's slider template: in PointerOver and Pressed it paints its track rectangle
        // 54% white, exactly the buffered bar's own rectangle, so the unplayed half turned white under the
        // pointer. The override lives in SeekSlider's own resources, and the template looks those keys up by
        // walking outward from itself — a lookup that fails leaves the white in place. Which nothing else can
        // see: the seek bar is only on screen mid-film, this machine cannot inject a mouse event, and playing
        // something on the real library while verifying is not allowed. So the states are pushed by hand.
        //
        // Registered after the reveal group rather than before it, with the other probes that raise the chrome
        // and put it back: the rule compares timestamps and only ever moves forward, and a probe that reset it
        // before 显隐规则 asked its first question would be answering about a past it had already left.
        var seek = player.ProbeSeekTrack();
        check("进度条指针下不铺白", seek.Ok, seek.Detail);

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

        // The two overlays that have to clear another overlay, put up together — which no state of the reveal
        // rule produces on its own. Both insets used to be written down; the panel's restated the strip's own
        // height in a second file, and the button's was a guess at a height nothing declares at all, so one
        // larger font in the transport row would have drawn the 跳过 offer across the seek slider. Asserted as
        // distances, with the numbers printed rather than expected.
        var clearance = player.ProbeClearance();
        check("浮层让开控制条", clearance.Ok, clearance.Detail);

        var peek = player.ProbePeek();
        check("章节预览定位", peek.Ok, peek.Detail);

        var commands = player.ProbeWindowCommands();
        check("窗口命令按钮", commands.Ok, commands.Detail);

        // 「置顶开启后不要改变按键颜色，绘制一个置顶开启图标来替换」. With the accent block gone the icon is the
        // only thing on screen that says which way the switch is thrown, and three ways of getting that wrong
        // are all invisible: a style that stopped applying puts the block back, a copy-paste can leave both
        // states drawing the same pin, and 置顶 itself now lives on the window where nothing on screen reads it
        // back. None of it can be caught by compiling, by a unit test (the test project reaches Core only) or
        // by a screenshot, which shows one state at a time.
        var pin = player.ProbePin();
        check("置顶开关", pin.Ok, pin.Detail);

        var tap = player.ProbeTap();
        check("点击画面暂停", tap.Ok, tap.Detail);

        // The acknowledgement that click leaves behind. Its own probe because it is the one storyboard in
        // the app, and a storyboard's target names are resolved when it is begun — so the first thing to
        // find out that the badge no longer resolves would otherwise be the pause it was meant to confirm.
        var pulse = player.ProbePulse();
        check("暂停播放角标", pulse.Ok, pulse.Detail);

        // 「双击画面全屏的时候会触发暂停和开始」. The net playback state was already right — the double tap put
        // pause back where it found it — so what was wrong was the badge flashing twice and mpv really stopping
        // and starting. Holding the tap back instead moves the risk: 「a single click no longer pauses」 now
        // rests on one timer being hooked up, and 「the badge stays quiet afterwards」 on one guard. Registered
        // after 暂停播放角标 on purpose — this probe leaves a silence behind, and that probe asks the badge to
        // speak.
        var taps = player.ProbeTapGesture();
        check("双击不触发暂停", taps.Ok, taps.Detail);

        var toast = shell.ToastState;
        check("提示通道", toast.Exists, toast.Exists ? $"InfoBar，当前{(toast.Open ? "已显示" : "未显示")}" : "缺失");

        ReportPictureAspect(window, player, check);
    }
}

using System.Text;
using EmbyNian.Shell.ViewModels;
using EmbyNian.Shell.Views;
using EmbyNian.Shell.Windowing;

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
}

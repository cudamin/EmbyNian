using System.Text;
using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Shell.Interop;
using EmbyNian.Shell.Views;
using EmbyNian.Shell.Windowing;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EmbyNian.Shell;

/// <summary>
/// 自检的正文那一趟：<c>Run</c>。哪一关先问、每一关怎么记进报告、退出码怎么定，都在这一个方法里，顺序就是
/// 报告上从上往下的顺序。
/// <para>
/// 它自己不读页面也不下判断 —— 读数在 <c>.Reads</c> 那一段攒好，一页一关的断言在 <c>.Pages</c> 和
/// <c>.Settings</c>，会动真窗口的那几关在 <c>.Fullscreen</c>。拆成几个文件的缘由见主文件
/// <see cref="ShellSelfCheck"/> 的类注释。
/// </para>
/// </summary>
internal static partial class ShellSelfCheck
{
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

        // 「锁定主页的窗口长宽」：客户区真是那个形状。主页从 HostWindow 的真实锁定状态启用严格首屏；侧边栏
        // 两档各自有没有完整放下继续观看，由下面「主页首屏只露继续观看」在真实 XAML 树上另量。
        // 开关关掉时（BrowseAspect 是 0）就只报形状不判：那时窗口本来就随便拉。
        var clientShape = height > 0 ? (double)width / height : 0;
        Check("锁定窗口比例",
            window.BrowseAspect <= 0 || Math.Abs(clientShape - window.BrowseAspect) < 0.01,
            $"客户区 {clientShape:0.000}:1"
                + (window.BrowseAspect > 0
                    ? $"，锁在 {window.BrowseAspect:0.000}:1（主页首屏按完整继续观看排版）"
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

            // 锁定窗口比例时侧边栏收放会改内容宽度；首屏不能跟着变成一档截掉继续观看、另一档又露出媒体库。
            // 这条在真实 XAML 树上把两档各摆一次，量的是两排货架相对窗口下沿的坐标。
            if (home.FoldOk is { } foldOk) Check("主页首屏只露继续观看", foldOk, home.Fold);
            else report.AppendLine($"[信息] 主页首屏只露继续观看 — {home.Fold}");

            // 同一块地方的第二问：图铺到标题栏底下以后，那三颗窗口按钮站在剧照上，墨得跟着换（见 ReadInk）。
            // 少了这一行，浅色主题下主页右上角就是三颗看不见的按钮，而上面那行读数一个数都不会变。
            if (_ink is { } ink) Check("标题栏墨色随大图", ink.Ok, ink.Detail);

            // 需求 6 的两处改动：卡片上那几个悬浮按钮。媒体库那一排不该有已观看和收藏 —— 一个媒体库既没看过
            // 也收藏不了，按下去只会挨服务器一句拒绝；播放按钮则要正正压在封面中心上，而不是原来贴在底边的那颗。
            if (_cards is { } hover) Check("卡片悬浮按钮", hover.Ok, hover.Detail);
            else report.AppendLine("[信息] 卡片悬浮按钮 — 主页没有已渲染的卡片");

            // 「点击主页继续观看、媒体库、最近添加的封面之后会先跳转到页面下方，然后才会进入页面」：按下去的那一
            // 刻卡片先拿到焦点，横带以前会替它要一次 BringIntoView，请求冒到这一页竖着滚的那层就把整页拽下去了。
            // 现在露出一张卡在带自己的滚动视图里做完（CardStrip.RevealFor），一句请求都不往外发。这条读数按
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
        // server, so the three strips on screen hold no cards at all. The arithmetic — how far one page is,
        // where it stops at either end, which chevron is up — now lives in Core's CardStrip and is pinned by
        // CardStripTests, so what is left here is the half no unit test can reach: the control's markup
        // parses, ItemSpacing reached the layout, and the click and focus paths record the offset they want.
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
}

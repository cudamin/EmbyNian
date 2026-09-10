using System.Collections.ObjectModel;
using EmbyNian.Configuration;
using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Playback;
using EmbyNian.Services;
using EmbyNian.Shell.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace EmbyNian.Shell.Views;

/// <summary>One entry in the header trail. <c>ToString</c> is what <c>BreadcrumbBar</c> renders.</summary>
public sealed record Crumb(string Label, string Tag)
{
    public override string ToString() => Label;
}

/// <summary>
/// The root of the XAML island: sign-in, navigation, header, and the frame pages are shown in.
/// <para>
/// It owns routing and the session's lifecycle, but no browsing data: pages ask it to navigate, and it
/// never reaches into a page. That is the same split <c>IShell</c> drew in the WinForms shell, kept
/// deliberately so the page-by-page port can move one view at a time.
/// </para>
/// </summary>
public sealed partial class ShellPage : UserControl, IShellActions
{
    private const string Category = "外壳";

    /// <summary>
    /// The three tags that no longer name a page in this frame, and the settings-window category each of
    /// them opens on. 需求 2 是「诊断和服务器移动到设置里」 and 需求 1 makes 设置 a window of its own, so all
    /// three end up in the same place. The empty string means 「whichever card comes first」, which is what
    /// the title bar's gear asks for.
    /// </summary>
    private static readonly Dictionary<string, string> Hosted = new(StringComparer.Ordinal)
    {
        ["servers"] = "服务器",
        ["diagnostics"] = "诊断",
        ["settings"] = ""
    };

    private readonly ObservableCollection<Crumb> _trail = [];

    /// <summary>
    /// The crumbs 返回 has walked off the end of the trail, newest last — the trail's other half, kept for
    /// 前进 to put back. The frame remembers the pages by itself; nothing remembers what they were called.
    /// <para>
    /// Only <see cref="GoBack"/> ever adds to this. A breadcrumb jump and any fresh navigation drop it,
    /// which is also what the frame does to its own forward stack, so the two cannot disagree about how far
    /// forward there is to go.
    /// </para>
    /// </summary>
    private readonly List<Crumb> _forward = [];

    /// <summary>Library tag to the request that opens it, filled from the server's own view list.</summary>
    private readonly Dictionary<string, LibraryRequest> _libraries = new(StringComparer.Ordinal);

    /// <summary>
    /// The same libraries as the items the server described them with, in the order it listed them.
    /// Kept because <see cref="LibraryRequest"/> carries only an id and a title, and the home page's
    /// 媒体库 row needs the artwork tags — reading the view list a second time for that would be a
    /// second round trip for something already in hand.
    /// </summary>
    private readonly List<EmbyItem> _libraryViews = [];

    /// <summary>Dismisses the toast on its own. One timer, restarted, so two toasts cannot race.</summary>
    private readonly DispatcherTimer _toast = new() { Interval = TimeSpan.FromSeconds(6) };

    /// <summary>
    /// The container, because handing each destination what it needs is this class's job: nine of the
    /// requests below are built here, and every page resolves its own services out of the provider they
    /// carry. Nullable rather than <c>= null!</c> for the reason the other pages give — the shell's XAML
    /// constructs this and <see cref="Attach"/> arrives afterwards.
    /// </summary>
    private IServiceProvider? _services;

    /// <summary>
    /// The three services routing itself asks something of: who is signed in, which server and account were
    /// last used, and — once, in the background — what version the server is. Resolved in
    /// <see cref="Attach"/> alongside the container, so any one of them says whether that has run.
    /// </summary>
    private EmbySession? _session;
    private ISettingsService? _settings;
    private IServerCapabilities? _capabilities;

    private Windowing.HostWindow? _window;
    private string? _current;

    /// <summary>
    /// 设置 as its own window (需求 1：「点击后弹出设置窗口」), built the first time the gear is pressed and
    /// then kept. Nullable because it may never exist: creating a second XAML window can fail, and the
    /// fallback is the settings page in this frame, which is where it used to live anyway.
    /// </summary>
    private SettingsWindow? _settingsWindow;

    /// <summary>
    /// 标题栏那五颗按钮的墨，静止／悬停／按下三档共用一支，停用的两支箭头另用一支淡的。
    /// <para>
    /// 自己立两支画刷而不是把 <c>EgTextBrush</c> 直接塞到键上：这里要换的不是颜色跟着主题走，而是同一颗
    /// 按钮在两种底上换两种墨（页面的底色 vs 剧照顶上那层暗罩）。而字典里的键换一支新画刷是不会重画的 ——
    /// 已经解析过的引用认的是对象，不是键。所以键上永远是这两个对象，改的是它们的 <c>Color</c>。
    /// </para>
    /// </summary>
    private readonly SolidColorBrush _titleInk = new();

    /// <inheritdoc cref="_titleInk"/>
    private readonly SolidColorBrush _titleInkDim = new();

    /// <summary>
    /// 面包屑那一行的墨。和 <see cref="_titleInk"/> 是同一件事 —— 同一格字在两种底上换两种墨 —— 判断不同：
    /// 那一排按键要等侧边栏收成窄条才压到图上，这一行在工作区里，图铺过来它就在图上了。
    /// <para>
    /// 对象立在 XAML 里（<c>TrailBar.Resources</c> 的 <c>EgTrailInkBrush</c>），这里只是拿来改颜色：格子里
    /// 那个 TextBlock 在解析模板时就绑到了它，此刻再 new 一支塞进字典已经晚了，绑上去的还是原来那个。
    /// </para>
    /// </summary>
    private readonly SolidColorBrush _trailInk;

    /// <summary>标题栏那一条底下是什么，见 <see cref="SetTitleStrip"/>。</summary>
    private TitleStrip _titleStrip;

    public ShellPage()
    {
        InitializeComponent();

        // 深浅跟着当前主题走。登记之后换主题会连带把这棵树翻过去 —— 框架自己那些没被调色板覆盖的刷子
        // 只认 ElementTheme，不登记的话浅色主题下就是「白面配深色控件」。
        ThemeHost.Register(this);

        PaintTitleActions();

        // 面包屑那一行的墨：字是我们那个格子自己画的（模板里绑的就是这一支画刷），中间那个人字尖归框架的
        // 模板画、认的是它自己那个键 —— 把同一支挂到那个键上，之后改一次颜色，字和尖一起跟着走。
        _trailInk = (SolidColorBrush)TrailBar.Resources["EgTrailInkBrush"];
        TrailBar.Resources["BreadcrumbBarNormalForegroundBrush"] = _trailInk;

        // 标题栏那一排、账号那一块的墨算一遍。**这一句从前叫 SyncPane**，那里面还带着侧边栏收放要跟着走的三
        // 样东西；栏删掉之后只剩墨这一件，而它和「初值不发事件」那件事无关了 —— 缩进现在是标记里一个常数。
        PaintTitleInk();

        // 换主题时那几支墨要跟着改。这里跟 PaintTitleActions 不一样，没法靠共用对象自动跟着走 —— 那几支
        // 是这一页自己的画刷，压在剧照上时故意不跟主题走，所以只能收到通知后再算一遍。
        ThemeHost.Changed += _ => PaintTitleInk();

        Trail.ItemsSource = _trail;

        // 鼠标上那两颗侧键。从前它们由 NavigationView 认（连 Alt+← 一起，走 BackRequested）；栏删掉之后
        // Alt+← / Alt+→ 挂在两支箭头自己的加速器上，而鼠标侧键没有加速器可挂，只能自己听。
        // handledEventsToo，因为卡片、列表、滚动视图都会把 PointerPressed 标成已处理 —— 不加这一句，只有
        // 点在空白处才退得回去。
        Root.AddHandler(PointerPressedEvent, new PointerEventHandler(OnRootPointerPressed), handledEventsToo: true);

        // The window has to be told which rectangles of its title bar the shell's buttons occupy, and the
        // answers move with the strip's layout: the theme's font, the scale factor, the account name's
        // own width. Measured whenever either changes rather than worked out once here.
        TitleActions.SizeChanged += (_, _) => ReportTitleBarHoles();
        AccountButton.SizeChanged += (_, _) => ReportTitleBarHoles();

        _toast.Tick += (_, _) =>
        {
            _toast.Stop();
            Toast.IsOpen = false;
        };
    }

    /// <summary>
    /// 鼠标上那两颗侧键：后退、前进。<see cref="PointerUpdateKind"/> 而不是
    /// <c>IsXButton1Pressed</c> —— 后者在按住不放的每一次移动上都是 true，那就是按一下退好几页。
    /// </summary>
    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        switch (e.GetCurrentPoint(this).Properties.PointerUpdateKind)
        {
            case PointerUpdateKind.XButton1Pressed:
                GoBack();
                break;
            case PointerUpdateKind.XButton2Pressed:
                GoForward();
                break;
        }
    }

    /// <summary>
    /// Puts the shared overlay brushes under the framework's two hover keys for the title-bar buttons —
    /// the five icon buttons and the account button.
    /// <para>
    /// Done here rather than in the markup because of what has to be under the key: the <em>same</em>
    /// <c>SolidColorBrush</c> object <c>ThemeHost</c> mutates. A <c>&lt;SolidColorBrush Color="…"/&gt;</c>
    /// written in <c>Grid.Resources</c> is a new object with a colour frozen at parse time, and a switch to a
    /// light theme would leave these two translucent white on a white strip — invisible. Handing over the
    /// object itself means one assignment to its <c>Color</c> repaints the hover of all the buttons.
    /// </para>
    /// <para>
    /// Read from application scope, which is the <c>Default</c> dictionary whatever the theme is (App.xaml
    /// pins <c>RequestedTheme</c>). That is correct precisely because <c>ThemeHost</c> writes the chosen
    /// theme's colours into both dictionaries — the same reason <c>PlayerPage.BrushFor</c> may read from
    /// there.
    /// </para>
    /// <para>
    /// Scoped to <see cref="Chrome"/> rather than <c>AppTitleBar</c>: the account button is a sibling of
    /// that grid (it must not pick up the 34×26 tray style its resources hand the five icon buttons), and
    /// resource lookup walks ancestors — a sibling's resources are invisible to it.
    /// </para>
    /// </summary>
    private void PaintTitleActions()
    {
        var resources = Application.Current.Resources;
        Chrome.Resources["ButtonBackgroundPointerOver"] = resources["EgOverlayHoverBrush"];
        Chrome.Resources["ButtonBackgroundPressed"] = resources["EgOverlayPressedBrush"];
    }

    /// <inheritdoc />
    public void SetTitleStrip(TitleStrip strip)
    {
        if (_titleStrip == strip) return;

        _titleStrip = strip;
        PaintTitleInk();
    }

    /// <summary>
    /// 面包屑那一行此刻自己上没上底色，给自检读（<see cref="DetailPage.WashRead"/>）：详情页把标题栏那一条洗成
    /// 正文的纸色之后，这里再涂一层就又是一道横缝，见 <see cref="PaintTitleInk"/>。
    /// </summary>
    internal Brush? TrailBase => TrailBar.Background;

    /// <summary>
    /// 外壳那一行上的墨 —— 标题栏那一排按键、账号那一块，外加它们底下那行面包屑。
    /// <para>
    /// 判断只有一条：<em>这一块底下是不是一张剧照</em>（<see cref="_titleStrip"/>）。从前那四拨各有各的门 ——
    /// 我们那几颗按键要等侧边栏收成窄条才压到图上，系统那三颗一直在图上，面包屑不经侧边栏那道门 —— 而侧边栏
    /// 2026-09-06 删掉之后，外壳整个浮在页面上，四拨于是走同一句判断。**这是删掉那条栏换来的净简化**，
    /// 不是漏掉了一档。
    /// </para>
    /// <para>
    /// 压在图上的那一档用固定的浅墨（<c>EgOnScrim*</c>），不跟主题走：剧照顶上那层暗罩是黑的，而一套浅色
    /// 主题的墨是深色。停用的两支箭头另用淡的那一支 —— 「走不动的箭头是淡墨，不是一块灰底」，见标记那一段。
    /// 面包屑在图上时用的是亮的那一支而不是淡的：平底上它是二等的陪衬字（跟着 EgDataStyle 走暗墨），压到
    /// 一张有明有暗的画面上就得自己站得住。页面自己洗过那一条的那一档（<see cref="TitleStrip.PagePainted"/>）
    /// 墨回到主题那支，因为那时它压的已经是正文的底色。
    /// </para>
    /// <para>
    /// 账号那颗按钮那行字跟着同一支墨：直接写在元素上（见下面 <c>AccountUser</c> 那几行），所以这里改一次
    /// 颜色它们就跟着走。
    /// </para>
    /// </summary>
    private void PaintTitleInk()
    {
        var onScrim = _titleStrip == TitleStrip.OnScrim;

        _titleInk.Color = Ink(onScrim ? "EgOnScrimBrush" : "EgTextBrush");
        _titleInkDim.Color = Ink(onScrim ? "EgOnScrimDimBrush" : "EgTextDimBrush");
        _trailInk.Color = Ink(onScrim ? "EgOnScrimBrush" : "EgTextDimBrush");

        TrailBar.Background = _titleStrip == TitleStrip.Plain
            ? Application.Current.Resources["LayerFillColorDefaultBrush"] as Brush
            : null;

        // 四个键，两支画刷，只在第一遍时挂上去：换的是它们的颜色，键上的对象一直是这两个。
        AppTitleBar.Resources["ButtonForeground"] = _titleInk;
        AppTitleBar.Resources["ButtonForegroundPointerOver"] = _titleInk;
        AppTitleBar.Resources["ButtonForegroundPressed"] = _titleInk;
        AppTitleBar.Resources["ButtonForegroundDisabled"] = _titleInkDim;

        // 账号那几样直接写在元素上，不走键：那行字一个走 FontWeight、一个走 EgDataStyle，而样式自己设了
        // Foreground —— 写在元素上的值压得过样式里的 setter，写进字典里的键则压不过。
        AccountUser.Foreground = _titleInk;
        AccountDot.Foreground = _titleInkDim;
        AccountServer.Foreground = _titleInkDim;
        AccountChevron.Foreground = _titleInkDim;

        // 系统那三颗归窗口画：它们不在这棵树上，是 Win32 的非客户区。洗过那一档跟着「不在图上」走 ——
        // 那时它们压的是页面洗出来的正文底色，白墨在一套浅色主题上就没了。
        _window?.SetCaptionOnScrim(onScrim);

        static Windows.UI.Color Ink(string key) =>
            Application.Current.Resources[key] is SolidColorBrush brush
                ? brush.Color
                : Microsoft.UI.Colors.White;
    }

    /// <summary>
    /// The settings document, for the three places routing reads it: the auto-login decision, the server
    /// switch menu, and which account that menu picks. Only ever reached after a guard on
    /// <see cref="_settings"/> or from a private helper the guarded path called.
    /// </summary>
    private AppSettings Settings => _settings!.Settings;

    /// <summary>外壳那一行，自检要问它在不在（登录页和播放时整块收起）。</summary>
    internal FrameworkElement ChromeRoot => Chrome;

    /// <summary>账号那颗按钮 —— 挪进标题栏之后它的收／放跟外壳一整块走，但自检还要量它在不在屏上。</summary>
    internal FrameworkElement AccountRoot => AccountButton;

    /// <summary>浏览态那一块（面包屑 + 页面）。</summary>
    internal FrameworkElement BrowseRoot => ContentHost;

    internal Frame Pages => ContentFrame;

    internal string? CurrentTag => _current;

    internal bool SignInVisible => SignIn.Visibility == Visibility.Visible;

    internal SignInPage SignInRoot => SignIn;

    /// <summary>Whether the sign-in card still has something in flight; the self-check waits on it.</summary>
    internal bool SignInBusy => SignIn.IsBusy;

    /// <summary>One line for the self-check report: who is signed in, or why nobody is.</summary>
    internal string SessionSummary => _session is { IsSignedIn: true } session
        ? $"已登录 {session.Account?.Username} @ {session.ServerDisplayName}"
        : "未登录（显示登录页）";

    internal int LibraryCount => _libraries.Count;

    /// <summary>This account's libraries, for the home page's own row of them.</summary>
    internal IReadOnlyList<EmbyItem> LibraryViews => _libraryViews;

    /// <summary>
    /// Opens the library whose view has this id, and says whether it was one. Goes through
    /// <see cref="GoTo"/> rather than navigating directly, so a library reached from a card on the home
    /// page ends up in exactly the state one clicked in the pane does — same highlight, same trail.
    /// <para>
    /// Here rather than in the caller because the tag format is this class's business: the home page
    /// rebuilding <c>$"library:{id}"</c> for itself would be a second copy of a string only this file
    /// writes, and the kind that stays right until the day it does not.
    /// </para>
    /// </summary>
    internal bool TryOpenLibrary(string id)
    {
        foreach (var (tag, request) in _libraries)
        {
            if (!string.Equals(request.ParentId, id, StringComparison.Ordinal)) continue;

            GoTo(tag);
            return true;
        }

        return false;
    }

    /// <summary>The first library the server reported, for the self-check to open. Null before sign-in.</summary>
    internal string? FirstLibraryTag => _libraries.Keys.FirstOrDefault();

    /// <summary>The player, for the self-check. It is a sibling of the frame, so nothing else can reach it.</summary>
    internal PlayerPage PlayerRoot => Player;

    /// <summary>Whether the notification host exists and is currently up.</summary>
    internal (bool Exists, bool Open) ToastState => (Toast is not null, Toast?.IsOpen == true);

    /// <summary>
    /// What the settings window is showing, or null when it is not up. The self-check reads whichever page
    /// it is walking through this first and <see cref="Pages"/> second, because 设置, 服务器 and 诊断 are all
    /// in the other window now and none of them is ever the frame's content again.
    /// </summary>
    internal object? SettingsContent => _settingsWindow is { IsOpen: true } window ? window.CurrentPage : null;

    /// <summary>Whether the settings window was ever built, whether it is up, and how big it is.</summary>
    internal (bool Created, bool Open, int Width, int Height) SettingsWindowState =>
        (_settingsWindow is not null,
            _settingsWindow?.IsOpen == true,
            _settingsWindow?.Size.Width ?? 0,
            _settingsWindow?.Size.Height ?? 0);

    /// <summary>
    /// The settings page wherever it ended up: in its own window, or in this frame on a machine where that
    /// window could not be built. What the self-check drives its walk through the cards with —
    /// <see cref="SettingsContent"/> is for reading whichever tree is on screen, and for 服务器 and 诊断
    /// that is not this page.
    /// </summary>
    internal SettingsPage? SettingsRoot =>
        (_settingsWindow is { IsOpen: true } settings ? settings.Page : null) ?? Pages.Content as SettingsPage;

    /// <summary>
    /// Handed the container once, before the window is shown. The shell's one resolve block, the same as
    /// every page's <c>OnNavigatedTo</c>: what this class needs comes out here and nowhere else, and the
    /// container itself is kept because handing it on is half of what routing does.
    /// </summary>
    internal void Attach(IServiceProvider services)
    {
        _services = services;
        _session = services.GetRequiredService<EmbySession>();
        _settings = services.GetRequiredService<ISettingsService>();
        _capabilities = services.GetRequiredService<IServerCapabilities>();

        SignIn.Attach(services);
        SignIn.SignedIn += OnSignedIn;

        // Marshalled: a token can expire on any thread that happens to be running a request, and this
        // handler swaps the visual tree.
        _session.SignedOut += (_, reason) => DispatcherQueue.TryEnqueue(() => OnSignedOut(reason));
    }

    /// <summary>
    /// Handed the window after it exists, which is later than <see cref="Attach"/> because the player
    /// needs the video child HWND and there is no client area to put one in until the window is up.
    /// <para>
    /// The player's state is resolved here rather than by the page itself: this is the one place in the
    /// shell that holds a container, so nothing inside <see cref="PlayerPage"/> has to ask for anything.
    /// </para>
    /// </summary>
    internal void AttachWindow(Windowing.HostWindow window)
    {
        if (_services is null) return;
        _window = window;
        Player.Attach(_services.GetRequiredService<PlayerViewModel>(), this, window);

        // 详情页要认显示器多大（纸面上沿那条线跟着显示器走，见 DetailHero.PaperLineFor），挂在这一个事件上
        // 而不是 OpenDetail 里：后退/前进重建的详情页实例不走 OpenDetail，却一样要从外壳领窗口。
        ContentFrame.Navigated += (_, _) =>
        {
            if (ContentFrame.Content is DetailPage detail) detail.AttachWindow(window);
        };

        // The buttons may well have been measured already, before there was a window to tell.
        ReportTitleBarHoles();
    }

    /// <summary>
    /// Cancels what the player has in flight on the way out of the process, and lets the settings window
    /// really close. Its own close is turned into a hide (see <see cref="SettingsWindow"/>), so this is the
    /// one place that is allowed to end it — otherwise the process would be kept alive by a window nobody
    /// can see.
    /// </summary>
    internal void Shutdown()
    {
        Player.Shutdown();

        _settingsWindow?.Close();
        _settingsWindow = null;
    }

    /// <summary>
    /// 播放. Routed through the shell rather than reached directly, so a page never has to know the
    /// player exists — the same split every other navigation already follows.
    /// </summary>
    /// <param name="choice">
    /// The media source and the tracks the detail page's pickers were showing, or null to let the player
    /// resolve them itself. Not an overload: two methods differing only in this would be ambiguous at
    /// every existing call site, all of which pass <c>episodes</c> by name.
    /// </param>
    internal Task PlayAsync(
        EmbyItem item,
        EmbyItem? parent = null,
        PlaybackChoice? choice = null,
        IReadOnlyList<EmbyItem>? episodes = null) => Player.PlayAsync(item, parent, choice, episodes);

    Task IShellActions.PlayAsync(
        EmbyItem item,
        EmbyItem? parent,
        PlaybackChoice? choice,
        IReadOnlyList<EmbyItem>? episodes) => PlayAsync(item, parent, choice, episodes);

    /// <summary>
    /// Gives the window to the player, or takes it back. The browse container is collapsed rather than
    /// merely covered: the XAML island is a single surface, so an opaque background behind the player
    /// would paint over exactly the region mpv's child window shows through.
    /// </summary>
    internal void ShowPlayer(bool playing)
    {
        if (playing)
        {
            Chrome.Visibility = Visibility.Collapsed;
            if (_window is not null) _window.PlaybackTitleBar = true;
            ContentHost.Visibility = Visibility.Collapsed;
            SignIn.Visibility = Visibility.Collapsed;
            return;
        }

        Chrome.Visibility = Visibility.Visible;
        if (_window is not null) _window.PlaybackTitleBar = false;

        // The player leaves the page it was started from behind it, so both arrows are exactly as available
        // as they were — but the row was laid out before playback took the window, so it is re-decided here
        // rather than trusted.
        SyncChrome();

        // Not unconditionally the shell: a token can expire while a film is playing, and coming back to
        // a browsing shell belonging to an account that is no longer signed in would be worse than the
        // sign-in card the session already asked for.
        if (_session is { IsSignedIn: true })
        {
            ContentHost.Visibility = Visibility.Visible;
        }
        else
        {
            SignIn.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// The shell's one notification channel. Replaces the WinForms toast window, which was a layered
    /// top-level form that had to be positioned, timed and repainted by hand.
    /// </summary>
    internal void Notify(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        Toast.Severity = severity;
        Toast.Message = message;
        Toast.IsOpen = true;

        // Restarted rather than started: a second toast within the window replaces the first, and it
        // should get its own six seconds rather than inheriting what was left of them.
        _toast.Stop();
        _toast.Start();

        Log.Debug(Category, $"提示：{message}");
    }

    void IShellActions.Notify(string message, InfoBarSeverity severity) => Notify(message, severity);

    /// <summary>
    /// Rebuilds whatever page is showing. Called after a playback, because the item's 已看 mark and its
    /// resume position have both just changed on the server and the grid behind the player is still
    /// drawing the values it was opened with.
    /// <para>
    /// Re-navigation rather than a refresh method on every page: each page already builds itself from
    /// its request in <c>OnNavigatedTo</c>, so this cannot drift from the way the page loads normally.
    /// The entry the re-navigation pushes is then dropped, or 「返回」 would walk back through the same
    /// page a second time.
    /// </para>
    /// </summary>
    internal void RefreshActive()
    {
        if (ContentFrame.Content is not IShellContent page) return;

        ContentFrame.Navigate(page.GetType(), page.NavigationRequest, new SuppressNavigationTransitionInfo());

        if (ContentFrame.BackStack.Count > 0) ContentFrame.BackStack.RemoveAt(ContentFrame.BackStack.Count - 1);
        SyncChrome();
    }

    /// <summary>
    /// Decides which of the two faces the app opens with: the saved token if there is one that still
    /// works, otherwise the sign-in card. Awaited by nobody — the window is already up, and this is
    /// what fills it.
    /// </summary>
    internal async Task StartAsync()
    {
        if (_settings is null || _session is null) return;

        var settings = Settings;
        var server = settings.ResolveLastServer();
        var account = settings.ResolveLastAccount(server);

        if (server is not null && account is not null && (account.HasSavedToken || account.HasSavedPassword))
        {
            ShowSignIn();
            SignIn.ShowRestoring(server.Name);

            try
            {
                if (await _session.TryRestoreAsync(server, account, CancellationToken.None).ConfigureAwait(true))
                {
                    await EnterShellAsync().ConfigureAwait(true);
                    return;
                }
            }
            catch (Exception error)
            {
                Log.Warn(Category, "自动登录失败", error);
            }
        }

        ShowSignIn();
    }

    /// <summary>
    /// Shows the signed-in identity at the right end of the title bar. Called by whoever owns the session;
    /// the shell does not go looking for one. 需求 2 took the avatar away, so this is one line of text and
    /// nothing else — there is no picture left to hand a display name to.
    /// </summary>
    public void ShowAccount(string? user, string? server)
    {
        AccountUser.Text = string.IsNullOrWhiteSpace(user) ? "未登录" : user;
        AccountServer.Text = string.IsNullOrWhiteSpace(server) ? "未连接服务器" : server;
    }

    /// <summary>Navigates to a tag, whether the request came from the tab row or from a page.</summary>
    public void GoTo(string tag)
    {
        if (_services is null) return;

        // Before the 「already there」 guard, and deliberately: these three are not this frame's content any
        // more, so being 「already on」 one says nothing about whether the user can see it. Pressing the gear
        // while the settings window sits behind the main one has to raise it, every time.
        if (Hosted.TryGetValue(tag, out var category))
        {
            ShowSettings(category);
            return;
        }

        if (_current == tag) return;

        if (tag == "home")
        {
            Open(typeof(HomePage), new HomeRequest(_services, _libraryViews.ToArray(), _window), "主页", tag);
            return;
        }

        if (tag == LibraryRequest.SearchTag)
        {
            OpenSearch();
            return;
        }

        if (_libraries.TryGetValue(tag, out var library))
        {
            Open(typeof(LibraryPage), library, library.Title, tag);
            return;
        }

        Log.Warn(Category, $"未知的导航目标 {tag}");
    }

    /// <summary>
    /// 设置 (需求 1：「点击后弹出设置窗口」), and with it 服务器 and 诊断 (需求 2：「诊断和服务器移动到设置里」).
    /// <para>
    /// A second window rather than a page in the frame, and a plain <c>Microsoft.UI.Xaml.Window</c> rather
    /// than another <see cref="Windowing.HostWindow"/>: the host window is built around one video child HWND,
    /// a cursor hook and a fullscreen z-order rule, and it keeps its instances in static maps. None of that
    /// belongs to a settings dialog.
    /// </para>
    /// <para>
    /// If the window cannot be created the settings still have to be reachable, so the page opens in the frame
    /// the way it always did. A missing window is then a logged fallback rather than a dead button.
    /// </para>
    /// </summary>
    /// <param name="category">
    /// Which category to open on, or the empty string for the first card. 服务器 and 诊断 come in this way,
    /// which is what makes them 「in the settings」 rather than pages of their own.
    /// </param>
    internal void ShowSettings(string category = "")
    {
        if (_services is null) return;

        _settingsWindow ??= SettingsWindow.TryCreate(_window);

        if (_settingsWindow is null)
        {
            // The frame keeps 设置 reachable, but not 服务器 or 诊断 as separate pages: they are cards in the
            // settings page now, so the fallback opens the settings page on the category that was asked for.
            Open(typeof(SettingsPage), new SettingsRequest(_services, category), "设置", "settings");
            return;
        }

        _settingsWindow.Show(new SettingsRequest(_services, category));
    }

    /// <summary>
    /// Puts the settings window away and gives the main window the foreground back. Called when the shell
    /// changes underneath it — signing out — and by the self-check, which cannot photograph the main window
    /// with another one sitting over it.
    /// </summary>
    internal void HideSettings()
    {
        if (_settingsWindow is not { IsOpen: true }) return;

        _settingsWindow.Hide();
        _window?.Activate();
    }

    /// <summary>
    /// Opens one item, choosing between its detail page and another grid. The one place that choice is
    /// made, so a poster on the home page, one in a library and 打开 in the context menu all agree.
    /// </summary>
    internal void OpenItem(EmbyItem item)
    {
        if (_services is null) return;

        if (DetailRequest.Supports(item))
        {
            OpenDetail(DetailRequest.For(_services, item));
            return;
        }

        // A person is the one thing here that is not a folder. Asked for as a parent it answers an empty
        // grid — nothing on the server has an actor as its parent — so it goes in as 「什么里有这个人」
        // instead. Everything else that lands here really is a container: a library, a box set, a folder.
        if (item.Type == EmbyItemType.Person)
        {
            OpenChild(LibraryRequest.ForPerson(_services, item));
            return;
        }

        OpenChild(LibraryRequest.For(_services, item));
    }

    /// <summary>
    /// One item's detail page, one level deeper. Same trail behaviour as <see cref="OpenChild"/> —
    /// a detail page is a drill-down that no pane entry points at.
    /// </summary>
    internal void OpenDetail(DetailRequest request)
    {
        ContentFrame.Navigate(typeof(DetailPage), request, new SuppressNavigationTransitionInfo());

        _trail.Add(new Crumb(request.Title, string.Empty));

        // A drill-down is not a top-level destination: clear _current so 主页 (the home button) lights up
        // again and 「already there」 in GoTo cannot swallow the next navigation. Requirement 6's other half.
        _current = null;
        SyncChrome();
    }

    /// <summary>
    /// Opens a folder, series or season from inside a page: same page type, one level deeper, and the
    /// trail grows instead of being replaced. This is the one navigation the tab row knows nothing about.
    /// </summary>
    internal void OpenChild(LibraryRequest request)
    {
        ContentFrame.Navigate(typeof(LibraryPage), request, new SuppressNavigationTransitionInfo());

        _trail.Add(new Crumb(request.Title, request.Tag));

        // A drill-down is not a top-level destination: clear _current so the home button lights up again.
        _current = null;
        SyncChrome();
    }

    void IShellActions.OpenItem(EmbyItem item) => OpenItem(item);

    bool IShellActions.TryOpenLibrary(string id) => TryOpenLibrary(id);

    void IShellActions.OpenGenre(string genre) => OpenGenre(genre);

    void IShellActions.OpenSignIn(ServerProfile? server) => OpenSignIn(server);

    Task IShellActions.SwitchProfileAsync(ServerProfile server, AccountProfile? account) =>
        SwitchProfileAsync(server, account);

    /// <summary>
    /// The first half of every tooling entry below: get onto the first library and wait for it to finish
    /// loading. Polled rather than awaited on an event because the page starts its own request in
    /// <c>OnNavigatedTo</c> and reports being finished through a property, not a completion. Null when the
    /// server has no library or the navigation did not land on one, both logged against
    /// <paramref name="tool"/>.
    /// </summary>
    private async Task<LibraryPage?> OpenFirstLibraryAsync(string tool)
    {
        if (FirstLibraryTag is not { } tag)
        {
            Log.Warn(Category, $"{tool}：没有可打开的媒体库");
            return null;
        }

        GoTo(tag);

        for (var attempt = 0; attempt < 40 && ContentFrame.Content is not LibraryPage { IsReady: true }; attempt++)
            await Task.Delay(250).ConfigureAwait(true);

        if (ContentFrame.Content is LibraryPage library) return library;

        Log.Warn(Category, $"{tool}：{tag} 没有打开成 LibraryPage");
        return null;
    }

    /// <summary>
    /// Tooling: opens the first library and leaves its sort menu showing.
    /// </summary>
    internal async Task ShowLibrarySortAsync()
    {
        if (await OpenFirstLibraryAsync("--show-library").ConfigureAwait(true) is not { } library) return;

        library.ShowSortMenu();
        Log.Info(Category, $"--show-library：已打开「{library.HeadingText}」并展开排序菜单");
    }

    /// <summary>
    /// Tooling: 把主页第一张卡的「更多」菜单弹开，好给它拍一张。
    /// <para>
    /// 有这个开关是因为这张菜单是代码搭的、又只能靠指针打开，而这台机器上注不进鼠标事件（<c>SendInput</c> 返回
    /// 成功而光标不动）。自检只读得出「搭了几行、每行写什么」，字形在那台机器的字体里到底有没有、菜单在浅色
    /// 主题下读不读得出来，都得看照片。
    /// </para>
    /// </summary>
    internal async Task ShowCardMenuAsync()
    {
        // 等主页把该有的几排读回来、卡片真的画出来。它自己报一次 IsReady，而卡片是那之后一个布局回合的事。
        for (var attempt = 0; attempt < 40 && ContentFrame.Content is not HomePage { IsReady: true }; attempt++)
            await Task.Delay(250).ConfigureAwait(true);

        await Task.Delay(400).ConfigureAwait(true);

        if (ContentFrame.Content is not HomePage home)
        {
            Log.Warn(Category, "--show-menu：这一刻框里不是主页");
            return;
        }

        if (home.ShowFirstCardMenu() is not { } title)
        {
            Log.Warn(Category, "--show-menu：主页上还没有渲染出卡片");
            return;
        }

        Log.Info(Category, $"--show-menu：已弹开「{title}」的更多菜单");
    }

    /// <summary>
    /// 工具用：把播放器的浮层摆到屏上留着，好给它拍一张（<c>--show-osd [pinned|paused|playing]</c>）。
    /// <para>
    /// 和 <see cref="ShowCardMenuAsync"/> 同一个理由：这几层只有指针走到对应的位置才浮上来，而这台机器注不进
    /// 鼠标事件。故意不调 <c>ShowPlayer(true)</c> —— 不收起导航外壳，浮层就叠在当前那一页上，照片里有真内容
    /// 当背景；收起来反倒是一层透明浮层背后什么都没有。
    /// </para>
    /// </summary>
    internal void ShowPlayerChrome(string? state)
    {
        Player.ShowChromeForShot(state);
        Log.Info(Category, $"--show-osd：浮层已摆上来（{state ?? "默认"}）");
    }

    /// <summary>
    /// 工具用：把播放层摆上来、计时器照常跑，然后什么都不动（<c>--hide-cursor</c>）—— 两秒后指针就该消失。
    /// 一个字节的视频都不播：一次真播放会写进用户的观看历史和续播位置。
    /// </summary>
    internal void HoldCursorForDemo() => Player.HoldCursorForDemo();

    /// <summary>
    /// Tooling: opens the first library, clicks the first row in it that has a detail page, and stays
    /// wherever that click went. A click rather than a navigation of its own, because the point is what
    /// clicking a poster really opens — which for a series was once another grid, of season folders.
    /// <para>
    /// With <c>--show-episode</c> it clicks one card further, the first in 单集, and stays on that episode's
    /// own page. Two flags rather than one because the two pages are deliberately not the same page any
    /// more: 媒体信息 exists only on the page of a file, so the show's page is what proves it is absent and
    /// the episode's is what proves it is there.
    /// </para>
    /// <para>
    /// With <c>--scroll-end</c> the page is left scrolled to the bottom, which is the only place a
    /// screenshot can show the body: at the top the hero deliberately fills the viewport.
    /// <c>--scroll-half</c> stops half a hero band down instead —— 标题栏那一条洗到一半的样子，也就是那道横缝
    /// 出过的地方，见 <see cref="DetailPage.ScrollToWash"/>。
    /// </para>
    /// </summary>
    internal async Task ShowDetailAsync(bool episode, bool scrollEnd = false, bool scrollHalf = false)
    {
        var flag = episode ? "--show-episode" : "--show-detail";
        if (await OpenFirstLibraryAsync(flag).ConfigureAwait(true) is not { } library) return;

        if (library.OpenFirstDetail() is not { } item)
        {
            Log.Warn(Category, $"{flag}：「{library.HeadingText}」里没有可下钻的条目");
            return;
        }

        await SettleAsync().ConfigureAwait(true);

        if (episode && ContentFrame.Content is DetailPage show)
        {
            if (show.OpenFirstEpisode() is not { } first)
            {
                Log.Warn(Category, $"{flag}：「{show.ViewModel.Title}」上没有可点的单集");
                return;
            }

            item = first;
            await SettleAsync().ConfigureAwait(true);
        }

        Log.Info(Category, ContentFrame.Content is DetailPage detail
            ? $"{flag}：点「{item.Name}」（{item.Type}）进入详情页面「{detail.ViewModel.Title}」"
            : $"{flag}：点「{item.Name}」落在 {ContentFrame.Content?.GetType().Name}，不是详情页面");

        // The same primitive the self-check scrolls with, so a screenshot and the 正文留缝 reading are
        // looking at the same layout rather than two states that merely resemble each other. Both wait for
        // the page to finish growing first: 「拉到底」 measured against a page whose shelves have not come
        // back yet is not the bottom, and 「一半」 was silently clamped to 0 by exactly that.
        if (scrollEnd && ContentFrame.Content is DetailPage bottom)
        {
            var how = await bottom.ScrollToEndAsync().ConfigureAwait(true);
            await Task.Delay(400).ConfigureAwait(true);
            Log.Info(Category, $"--scroll-end：已把详情页面拉到最底下 —— {how}");
        }

        // Half a hero band down, which is the offset the 标题条洗到正文色 reading dry-runs the wash at: a shot
        // taken here and that reading are then the same layout, not two states that merely resemble each
        // other. Only meaningful on its own —— 拉到底的时候那一条整条就是正文那张纸，看不出它是渐变。
        if (scrollHalf && !scrollEnd && ContentFrame.Content is DetailPage middle)
        {
            var how = await middle.ScrollToWashAsync().ConfigureAwait(true);
            await Task.Delay(400).ConfigureAwait(true);
            Log.Info(Category, $"--scroll-half：已把详情页面拉到剧照的一半高处 —— {how}");
        }

        // A detail page reports ready once, when its own load finishes; a click that opens another one
        // builds a fresh page, so this is waited out per click rather than once at the end.
        async Task SettleAsync()
        {
            for (var attempt = 0; attempt < 40 && ContentFrame.Content is not DetailPage { IsReady: true }; attempt++)
                await Task.Delay(250).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Tooling: opens the first library and plays the first thing in it that can be played. The only way
    /// to get real video on screen without a person clicking, and therefore the only way any of the
    /// player's runtime behaviour — the embedded child HWND, the reveal rule over a live picture,
    /// requirement 12's handover — can be checked at all.
    /// </summary>
    internal async Task PlayFirstAsync()
    {
        if (await OpenFirstLibraryAsync("--play").ConfigureAwait(true) is not { } library) return;

        // A series or a season is fine to hand over: the player resolves its way down to an episode by
        // itself, which is worth exercising rather than avoiding.
        if (library.FirstItem is not { } item)
        {
            Log.Warn(Category, $"--play：「{library.HeadingText}」里没有内容");
            return;
        }

        Log.Info(Category, $"--play：播放「{item.Name}」（{item.Type}）");
        await PlayAsync(item).ConfigureAwait(true);
    }

    /// <summary>A top-level destination: one crumb, a synced tab highlight, no fade.</summary>

    private void Open(Type page, object parameter, string title, string tag)
    {
        _current = tag;

        // SuppressNavigationTransitionInfo, not the default: requirement 9 rules out the fade, and
        // on a page whose first paint is a grid of posters the fade is exactly where it shows.
        ContentFrame.Navigate(page, parameter, new SuppressNavigationTransitionInfo());

        _trail.Clear();
        _trail.Add(new Crumb(title, tag));

        SyncChrome();
    }

    /// <summary>
    /// The one place the toolbar row is decided, called after every navigation: the two arrows, the
    /// breadcrumb and the gestures NavigationView routes all read the same facts here, so none of them can
    /// drift from the frame's own two stacks.
    /// </summary>
    private void SyncChrome()
    {
        // Navigate() empties the frame's forward stack. Trimming here rather than at each of the six call
        // sites is what keeps the crumbs held for 前进 from outliving the pages they name.
        while (_forward.Count > ContentFrame.ForwardStack.Count) _forward.RemoveAt(_forward.Count - 1);

        // 主页那颗按钮在主页上自己变暗（同两支箭头「走不动就暗」的规矩）：_current 是 "home" 就是已经在主页，
        // 下钻和别的页面会把它清空，那时按钮亮着、一按回主页。
        HomeButton.IsEnabled = _current != "home";

        ApplyChrome(ContentFrame.CanGoBack, ContentFrame.CanGoForward, _trail.Count);
    }

    /// <summary>
    /// The chrome above the frame, from the three facts that decide it: whether the frame has somewhere to go
    /// back to, somewhere to go forward to, and how deep the trail is. Split out from
    /// <see cref="SyncChrome"/> so the self-check can drive every state — nothing can make
    /// <c>Frame.CanGoBack</c> say what a check needs it to say.
    /// <para>
    /// Both arrows stay in place and go dim rather than disappearing, the way the Claude app's own title bar
    /// does it: an arrow that comes and goes moves the one beside it, and the strip they sit in is the
    /// window's drag area with a hole cut for exactly their rectangle, which a moving button would put in
    /// the wrong place.
    /// </para>
    /// </summary>
    private void ApplyChrome(bool canGoBack, bool canGoForward, int crumbs)
    {
        BackButton.IsEnabled = canGoBack;
        ForwardButton.IsEnabled = canGoForward;

        // The whole row, not just the trail inside it: one crumb only repeats the heading the page draws
        // right below, and a row left standing empty is a band of nothing 38 pixels tall.
        TrailBar.Visibility = crumbs > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Tells the window which rectangles of its title bar the shell's buttons occupy, in the island's
    /// logical pixels, so the frame stops answering 「caption」 there and their clicks arrive. Two of them:
    /// the row of five on the left, and the account button at the right end — which moved into this strip
    /// when its own row below went away (2026-09-09), and would otherwise read as a drag handle instead of
    /// a button. Measured rather than assumed: the positions depend on the current scale, the icons' own
    /// metrics, and the account name's own width.
    /// </summary>
    private void ReportTitleBarHoles()
    {
        // Zero while the strip is collapsed for playback. Reporting that would hand the whole strip back to
        // the frame, which is right for playback and wrong the moment browsing returns — and playback has
        // already claimed the strip whole by then anyway, so the last real rectangle is the one to keep.
        if (_window is null) return;

        if (TitleActions.ActualWidth > 0 && TitleActions.ActualHeight > 0)
        {
            var origin = TitleActions.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, 0));
            _window.SetTitleBarHoles(
                (origin.X, origin.Y, TitleActions.ActualWidth, TitleActions.ActualHeight),
                AccountRect());
        }
    }

    /// <summary>
    /// 账号那颗按钮落在窗口里的矩形，给自检和窗口挖洞用（见 <see cref="ReportTitleBarHoles"/>）。
    /// null 是「不在屏上」：没登录它收着，窗口那一头也就没有为它挖的洞。
    /// </summary>
    private (double X, double Y, double Width, double Height)? AccountRect()
    {
        if (AccountButton.Visibility != Visibility.Visible
            || AccountButton.ActualWidth <= 0
            || AccountButton.ActualHeight <= 0) return null;

        var origin = AccountButton.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, 0));
        return (origin.X, origin.Y, AccountButton.ActualWidth, AccountButton.ActualHeight);
    }

    /// <summary>需求 1 的第一颗：「点击后弹出设置窗口」.</summary>
    private void OnSettingsClicked(object sender, RoutedEventArgs e) => ShowSettings();

    /// <summary>需求 1 的第二颗：「点击后进入搜索页面，直接在当前窗口跳转」 — a page in this frame, not a window.</summary>
    private void OnSearchClicked(object sender, RoutedEventArgs e) => OpenSearch();

    /// <summary>
    /// 需求 6 的那颗「主页」：回到主页那一屏。顶上标签栏 2026-09-08 删掉之后，回主页只此一颗
    /// （在主页上它自己变暗，见 <see cref="SyncChrome"/>）。<see cref="GoTo"/> 里 <c>_current == "home"</c>
    /// 那道门保证「已经在主页又点一次」什么都不做。
    /// </summary>
    private void OnHomeClicked(object sender, RoutedEventArgs e) => GoTo("home");

    /// <summary>The row's own arrows. Same walk as the keyboard's and the mouse's, so none can drift.</summary>
    private void OnBackClicked(object sender, RoutedEventArgs e) => GoBack();

    private void OnForwardClicked(object sender, RoutedEventArgs e) => GoForward();

    /// <summary>
    /// One step back: the frame, the trail and the tab highlight. Reached from the row's own button, from
    /// its Alt+← accelerator, and from the mouse's own back button (<see cref="OnRootPointerPressed"/>) —
    /// 从前那三条路都由 <c>NavigationView.BackRequested</c> 一处收口，侧边栏删掉之后各自接线。
    /// </summary>
    private void GoBack()
    {
        if (!ContentFrame.CanGoBack) return;

        // Read before the walk: the crumb being left behind is what 前进 has to put back. Null when the
        // trail is already down to its last entry, which is a top-level page — the frame can still step
        // back out of one, and stepping forward into it again needs no crumb.
        var leaving = _trail.Count > 1 ? _trail[^1] : null;

        ContentFrame.GoBack(new SuppressNavigationTransitionInfo());

        if (leaving is not null)
        {
            _trail.RemoveAt(_trail.Count - 1);
            _forward.Add(leaving);
        }

        SyncChrome();

        AdoptContentTag();
    }

    /// <summary>
    /// One step forward: undoes exactly one <see cref="GoBack"/>. The frame keeps the page, this keeps the
    /// name of it. Nothing else in the shell adds to the forward stack, so 前进 can only ever retrace a
    /// walk the user has just taken backwards.
    /// </summary>
    private void GoForward()
    {
        if (!ContentFrame.CanGoForward) return;

        ContentFrame.GoForward();

        if (_forward.Count > 0)
        {
            _trail.Add(_forward[^1]);
            _forward.RemoveAt(_forward.Count - 1);
        }

        SyncChrome();

        AdoptContentTag();
    }

    private void OnBreadcrumbClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs e)
    {
        var steps = _trail.Count - 1 - e.Index;
        if (steps <= 0) return;

        // The intermediate entries are dropped rather than walked back through: every GoBack rebuilds a
        // page, and a page rebuilt only to be left again is one more query the server did not need.
        for (var i = 0; i < steps - 1 && ContentFrame.BackStack.Count > 0; i++)
            ContentFrame.BackStack.RemoveAt(ContentFrame.BackStack.Count - 1);

        if (!ContentFrame.CanGoBack) return;

        ContentFrame.GoBack(new SuppressNavigationTransitionInfo());

        while (_trail.Count > e.Index + 1) _trail.RemoveAt(_trail.Count - 1);

        // A jump is not a step: the pages between here and where the trail was have just been thrown away,
        // so there is no coherent one-step-forward left to offer. 前进 goes dim rather than promising to
        // retrace a path that no longer exists.
        ContentFrame.ForwardStack.Clear();
        _forward.Clear();

        SyncChrome();

        AdoptContentTag();
    }

    /// <summary>
    /// Reads the tag off whatever page the frame landed on. Pages set it themselves in
    /// <c>OnNavigatedTo</c>, which is the only way back navigation can know where it ended up: the
    /// frame restores a page, not a shell state.
    /// </summary>
    private void AdoptContentTag()
    {
        var tag = (ContentFrame.Content as FrameworkElement)?.Tag as string;

        // Empty is a drill-down: a real page, but not a top-level destination. _current drives the home
        // button's dim state and GoTo's 「already there」 guard.
        _current = tag is { Length: > 0 } ? tag : null;
    }

    /// <summary>
    /// The search page: the same grid as every library, with a term behind it instead of a folder.
    /// <para>
    /// Three callers. 需求 1 的搜索键 opens it with nothing typed yet, and the page focuses its own box; the
    /// box on that page re-queries in place rather than coming back through here; and a cast card on a detail
    /// page arrives with a name already, because Emby's <c>People</c> entries carry a name and a portrait but
    /// no browsable id, so a person's other work is a search and not a folder.
    /// </para>
    /// <para>
    /// A top-level destination with a tag of its own, so the trail and 「already there」 all behave the way
    /// they do for 主页 — which is what lets the button be pressed a second time without throwing away
    /// what has been typed into the page. It is a drill-down for the home button: the home button lights
    /// up while search is showing.
    /// </para>
    /// </summary>
    internal void OpenSearch(string term = "")
    {
        if (_services is null) return;

        var query = term.Trim();
        if (query.Length == 0 && _current == LibraryRequest.SearchTag) return;

        Log.Info(Category, query.Length == 0 ? "打开搜索页" : $"搜索：{query}");

        Open(
            typeof(LibraryPage),
            LibraryRequest.Search(_services, query),
            LibraryRequest.SearchTitle(query),
            LibraryRequest.SearchTag);
    }

    private void OnSignedIn(object? sender, EventArgs e) => _ = EnterShellAsync();

    /// <summary>
    /// 按类型浏览，见 <see cref="IShellActions.OpenGenre"/>。和搜索页同一个形状：一格网格，排序、筛选、三种
    /// 视图、翻页、字母条都跟着来 —— 所以它是一个请求，不是又一页。
    /// <para>
    /// 不带 tag：侧边栏没有一项指着某个类型，所以这是一次下钻（面包屑上多一节，高亮不动），同往剧里点进去。
    /// </para>
    /// </summary>
    public void OpenGenre(string genre)
    {
        if (_services is null) return;

        var name = genre.Trim();
        if (name.Length == 0) return;

        Log.Info(Category, $"按类型浏览：{name}");

        Open(typeof(LibraryPage), LibraryRequest.ForGenre(_services, name), name, tag: "");
    }

    /// <summary>
    /// Swaps the sign-in card for the browsing shell: reads this account's libraries, fills the two switch
    /// menus, and opens the home page.
    /// </summary>
    private async Task EnterShellAsync()
    {
        if (_session is null || _capabilities is null) return;

        SignIn.Detach();
        SignIn.Visibility = Visibility.Collapsed;
        ContentHost.Visibility = Visibility.Visible;

        // 账号那颗按钮（现在站在标题栏里）只在登录之后露面：登录卡上它没的可切，而它收着的时候窗口那一头
        // 挖给它的洞也就没了 —— ShowAccount 一改字宽度变了，SizeChanged 会再把洞报对。
        AccountButton.Visibility = Visibility.Visible;

        ShowAccount(_session.Account?.Username, _session.ServerDisplayName);
        FillSwitchMenus();

        // Whatever the previous account was looking at belongs to the previous account.
        ReleaseContent();

        // Not awaited, and on purpose: only the sort menu wants the version, and it opens long after
        // this. Awaiting it here would put another round trip in front of the home page.
        _ = _capabilities.ProbeAsync();

        await LoadLibrariesAsync().ConfigureAwait(true);

        GoTo("home");
    }

    private async Task LoadLibrariesAsync()
    {
        _libraries.Clear();
        _libraryViews.Clear();

        List<EmbyItem> views;
        try
        {
            // 恢复登录那一趟已经取回过一份（它拿这个接口当令牌探针），领得到就用它 —— 每次启动省掉一趟往返，
            // 而这一趟压在「看到第一屏」的路上。只给一次，所以下面每一次刷新照旧真去问服务器。
            views = _session!.TakeRestoredViews()
                ?? await _session
                    .ExecuteAsync((client, token) => client.GetViewsAsync(token), CancellationToken.None)
                    .ConfigureAwait(true);
        }
        catch (Exception error)
        {
            // 主页那一排媒体库于是空着；主页那一页会在用户真正在看的地方报同一件事。
            Log.Warn(Category, "读取媒体库列表失败", error);
            return;
        }

        foreach (var view in views)
        {
            // 需求 3：「屏蔽媒体库里音乐的内容」. Filtered here rather than in each place a library can be
            // reached, because this loop is the only source of both: the requests that open a library, and
            // the 媒体库 row on the home page, which is this same list drawn as cards —— 顶部标签栏 2026-09-08
            // 删掉之后，那一排卡片是进各媒体库唯一的入口。A music library dropped here cannot be reached from
            // anywhere, and 继续观看/接下来看/最近添加 drop their own music rows in HomeViewModel because those
            // three span every library.
            if (EmbyItemType.IsMusicLibrary(view.CollectionType)) continue;

            var tag = $"library:{view.Id}";

            _libraryViews.Add(view);

            _libraries[tag] = new LibraryRequest
            {
                Services = _services!,
                Title = view.Name,
                Tag = tag,
                ParentId = view.Id,
                CollectionType = view.CollectionType
            };
        }

        Log.Info(Category, views.Count == _libraries.Count
            ? $"媒体库 {_libraries.Count} 个已读入"
            : $"媒体库 {_libraries.Count} 个已读入（服务器共 {views.Count} 个，音乐库已屏蔽）");
    }

    /// <summary>
    /// 切换服务器 / 切换用户: the saved servers and the accounts on the current one, both one click away from
    /// the button at the right end of the tab row. A switch that has a saved token or password does not ask
    /// for anything; one that does not lands on the sign-in card with the address already filled in.
    /// </summary>
    private void FillSwitchMenus()
    {
        var session = _session!;

        ServerMenu.Items.Clear();
        foreach (var server in Settings.Servers)
        {
            var entry = new MenuFlyoutItem
            {
                Text = server.Name,
                Tag = server,
                IsEnabled = !ReferenceEquals(server, session.Server)
            };

            entry.Click += OnSwitchServer;
            ServerMenu.Items.Add(entry);
        }

        ServerMenu.IsEnabled = ServerMenu.Items.Count > 0;

        UserMenu.Items.Clear();
        var current = session.Server;

        if (current is not null)
        {
            foreach (var account in current.Accounts)
            {
                var entry = new MenuFlyoutItem
                {
                    Text = account.Username,
                    Tag = account,
                    IsEnabled = !ReferenceEquals(account, session.Account)
                };

                entry.Click += OnSwitchUser;
                UserMenu.Items.Add(entry);
            }
        }

        UserMenu.IsEnabled = UserMenu.Items.Count > 0;
    }

    private void OnSwitchServer(object sender, RoutedEventArgs e)
    {
        if (_settings is null || sender is not MenuFlyoutItem { Tag: ServerProfile server }) return;

        var account = server.FindAccount(Settings.LastAccountId) ?? server.Accounts.FirstOrDefault();
        _ = SwitchAsync(server, account);
    }

    private void OnSwitchUser(object sender, RoutedEventArgs e)
    {
        if (_session?.Server is not { } server || sender is not MenuFlyoutItem { Tag: AccountProfile account })
            return;

        _ = SwitchAsync(server, account);
    }

    private async Task SwitchAsync(ServerProfile server, AccountProfile? account)
    {
        if (_session is null || _settings is null) return;

        if (account is not null && (account.HasSavedToken || account.HasSavedPassword))
        {
            ShowSignIn(server);
            SignIn.ShowRestoring(server.Name);

            try
            {
                if (await _session.TryRestoreAsync(server, account, CancellationToken.None).ConfigureAwait(true))
                {
                    _settings.Save();
                    await EnterShellAsync().ConfigureAwait(true);
                    return;
                }
            }
            catch (Exception error)
            {
                Log.Warn(Category, $"切换到 {server.Name} 失败", error);
            }
        }

        ShowSignIn(server);
    }

    /// <summary>Lets management pages reuse the same restore/sign-in path as the account menu.</summary>
    internal Task SwitchProfileAsync(ServerProfile server, AccountProfile? account) => SwitchAsync(server, account);

    /// <summary>Opens the existing sign-in page for adding a server or entering credentials.</summary>
    internal void OpenSignIn(ServerProfile? server = null) => ShowSignIn(server);

    private void OnSignOut(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;

        Log.Info(Category, "请求注销");
        _session.SignOut();
    }

    private void OnSignedOut(string reason)
    {
        Log.Info(Category, $"返回登录页：{reason}");
        ShowSignIn();
    }

    /// <summary>Puts the sign-in card back in front, pointed at a particular server when there is one.</summary>
    private void ShowSignIn(ServerProfile? server = null)
    {
        ContentHost.Visibility = Visibility.Collapsed;

        // 账号那颗也收起来：没登录它没的可切，而且在登录卡上它上面的菜单（切换服务器／注销）一个都不通。
        // 标题栏那一排（主页、设置、搜索、两支箭头）照旧留着 —— 它是窗口的外壳。
        AccountButton.Visibility = Visibility.Collapsed;
        SignIn.Visibility = Visibility.Visible;

        // The settings window belongs to the account being left: 服务器 lists this session's profiles and
        // 诊断 describes this session. Hidden rather than closed, because closing the last XAML window ends
        // the process — and the next sign-in will want it back anyway.
        HideSettings();

        // Held bitmaps and pending requests both belong to the page being left.
        ReleaseContent();
        _trail.Clear();
        SyncChrome();

        ShowAccount(null, null);
        SignIn.Prepare(server);
    }

    /// <summary>
    /// Drops whatever the frame is showing, and gives that page the same teardown navigating away would.
    /// <para>
    /// Assigning <c>ContentFrame.Content</c> does not run the frame's navigation pipeline, so the page's
    /// <c>OnNavigatedFrom</c> never fires; that pipeline writes <c>Content</c> as its own last step, which
    /// is why the setter cannot also raise it. Both callers here — signing out and switching accounts —
    /// drop the page this way rather than navigating, so without the explicit
    /// <see cref="IShellContent.Release"/> the outgoing page keeps its loads and its subscriptions for the
    /// rest of the process.
    /// </para>
    /// </summary>
    private void ReleaseContent()
    {
        if (ContentFrame.Content is IShellContent page) page.Release();
        ContentFrame.Content = null;
        ContentFrame.BackStack.Clear();
        ContentFrame.ForwardStack.Clear();
        _forward.Clear();
        _current = null;
    }
}

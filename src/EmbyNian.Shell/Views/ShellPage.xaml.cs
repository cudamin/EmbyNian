using System.Collections.ObjectModel;
using EmbyNian.Configuration;
using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Playback;
using EmbyNian.Services;
using EmbyNian.Shell.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    /// <summary>主页, the separator and the 媒体库 header; everything after these is a real library.</summary>
    private const int FixedPaneItems = 3;

    private const int MovieGlyph = 0xE8B2;
    private const int SeriesGlyph = 0xE7F4;
    private const int PhotoGlyph = 0xE91B;
    private const int FolderGlyph = 0xE8B7;

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

    /// <summary>
    /// 侧边栏张开时那一排按键的左缩进。留白不是装饰：窗口在标题栏上挖的洞就是那一排的矩形，挖到窗口左边沿
    /// 就没地方拖窗口了。窄条那一档是另一个数，见 <see cref="SyncPane"/>。
    /// </summary>
    private const double TitleActionsInset = 40;

    /// <summary>
    /// 侧边栏收成窄条时，那一排按键要让开的距离 —— 从窄条的右沿算起。1 是窄条和工作区之间那道竖线自己的粗细，
    /// 剩下 9 让第一颗图标离线 16 像素，和这一排里相邻两颗图标之间的距离一样，见 <see cref="SyncPane"/>。
    /// </summary>
    private const double PaneClearance = 10;

    public ShellPage()
    {
        InitializeComponent();

        // 深浅跟着当前主题走。登记之后换主题会连带把这棵树翻过去 —— 框架自己那些没被调色板覆盖的刷子
        // 只认 ElementTheme，不登记的话浅色主题下就是「白面配深色控件」。
        ThemeHost.Register(this);

        PaintTitleActions();
        PaintNavigationPane();

        // 面包屑那一行的墨：字是我们那个格子自己画的（模板里绑的就是这一支画刷），中间那个人字尖归框架的
        // 模板画、认的是它自己那个键 —— 把同一支挂到那个键上，之后改一次颜色，字和尖一起跟着走。
        _trailInk = (SolidColorBrush)TrailBar.Resources["EgTrailInkBrush"];
        TrailBar.Resources["BreadcrumbBarNormalForegroundBrush"] = _trailInk;

        // 侧边栏一开始就是收着的（「侧边栏默认为折叠状态」），而初值不会引出 PaneClosed —— 事件只在「变了」的
        // 时候来。所以跟着侧边栏走的那三样（账号那块只留字形、那一排按键的缩进、标题栏的墨）在这里先算一遍；
        // PaintTitleInk 也在它里面，不用再单独叫一次。
        SyncPane();

        // 光在 XAML 里写 IsPaneOpen="False" 收不住，而且不止一次收不住：NavigationView 每回改大小都照
        // PaneDisplayMode="Left" 把自己判成「张开」那一档，然后 —— 除非它记得「这是有人自己收起来的」——
        // 顺手把侧边栏张开。标记里那个 False 是套模板之前的事，第一次重排就被推回去了（改之前自检量到的就是
        // 张开那一档：整排从 40 起，而自检中途本来就会把窗口拉成 1100、500 几种宽度）。
        // 它记住这件事的唯一时机是「张着的时候被关掉」，所以这里走一趟张开再关上：模板套完（Loaded 到的时候
        // 已经套完）先把它按到张开，紧接着关掉，这一关就被当成用户的意思，往后重排不再擅自张开。两次赋值在
        // 同一拍里，中间没有一帧渲染，屏幕上看不见它张开过。
        // 只走这一趟，之后侧边栏是张是收由那颗折叠键说了算 —— 所以一进来先把自己从事件上摘掉。
        void FoldOnce(object sender, RoutedEventArgs args)
        {
            Navigation.Loaded -= FoldOnce;

            // 「默认收起侧边栏」关掉的时候就不收 —— Attach 比 Loaded 早，所以这里问得到那份设置；问不到
            // （测试里单独立一页）当收起算，那是这个开关的默认值。
            ApplyPaneDefault(_settings?.Settings.Ui.CollapseSidebar != false);
        }

        Navigation.Loaded += FoldOnce;

        // 开关改完当场生效，见 ShellPrefs。这一页和主窗口活得一样久，所以和下面那句 ThemeHost 一样不退订。
        ShellPrefs.Changed += ui => ApplyPaneDefault(ui.CollapseSidebar);

        // 换主题时那两支墨要跟着改。这里跟 PaintTitleActions 不一样，没法靠共用对象自动跟着走 —— 那两支
        // 是这一页自己的画刷，压在剧照上时故意不跟主题走，所以只能收到通知后再算一遍。
        ThemeHost.Changed += _ => PaintTitleInk();

        Trail.ItemsSource = _trail;

        // The window has to be told which rectangle of its title bar the five buttons occupy, and the answer
        // moves with the strip's layout: the theme's font, the scale factor, the pane button appearing at all.
        // Measured whenever it changes rather than worked out once here.
        TitleActions.SizeChanged += (_, _) => ReportTitleBarHole();

        _toast.Tick += (_, _) =>
        {
            _toast.Stop();
            Toast.IsOpen = false;
        };
    }

    /// <summary>
    /// Puts the shared overlay brushes under the framework's two hover keys for the five title-bar buttons.
    /// <para>
    /// Done here rather than in the markup because of what has to be under the key: the <em>same</em>
    /// <c>SolidColorBrush</c> object <c>ThemeHost</c> mutates. A <c>&lt;SolidColorBrush Color="…"/&gt;</c>
    /// written in <c>Grid.Resources</c> is a new object with a colour frozen at parse time, and a switch to a
    /// light theme would leave these two translucent white on a white strip — invisible. Handing over the
    /// object itself means one assignment to its <c>Color</c> repaints the hover of all five buttons.
    /// </para>
    /// <para>
    /// Read from application scope, which is the <c>Default</c> dictionary whatever the theme is (App.xaml
    /// pins <c>RequestedTheme</c>). That is correct precisely because <c>ThemeHost</c> writes the chosen
    /// theme's colours into both dictionaries — the same reason <c>PlayerPage.BrushFor</c> may read from
    /// there.
    /// </para>
    /// </summary>
    private void PaintTitleActions()
    {
        var resources = Application.Current.Resources;
        AppTitleBar.Resources["ButtonBackgroundPointerOver"] = resources["EgOverlayHoverBrush"];
        AppTitleBar.Resources["ButtonBackgroundPressed"] = resources["EgOverlayPressedBrush"];
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
    /// 标题栏那一条上的墨 —— 两拨按钮，两种底，外加它底下那行面包屑。
    /// <para>
    /// 系统那三颗（最小化、最大化、关闭）在窗口的右上角，也就是工作区那一列的顶上：主页那张大图铺过去
    /// 之后它们永远站在剧照上。我们那五颗在左端，侧边栏张开时它们站在侧边栏自己的底色上，收成 48 像素的
    /// 窄条时才压到图上 —— 所以这两拨的判断不是同一个。
    /// </para>
    /// <para>
    /// 面包屑那一行是第三拨，跟的是没经侧边栏那道门的 <c>_titleStrip</c>：它整条都在工作区里，图铺过来
    /// 它就在图上，跟侧边栏是张是收无关。它的底色也在这里换 —— 图上那一档不上底色，一条实心条压在剧照上
    /// 就是「跟主页一样，占满标题栏」要去掉的那道横边；页面自己洗过那一条的那一档（<see cref="TitleStrip.PagePainted"/>）
    /// 同样不上，那时候洗出来的就是正文那张纸的颜色，外壳再涂一层页面底色，这一条会比正文亮出四五级。
    /// </para>
    /// <para>
    /// 压在图上的那一档用固定的浅墨（<c>EgOnScrim*</c>），不跟主题走：剧照顶上那层暗罩是黑的，而三套浅色
    /// 主题的墨是深色。停用的两支箭头另用淡的那一支 —— 「走不动的箭头是淡墨，不是一块灰底」，见标记那一段。
    /// 面包屑在图上时用的是亮的那一支而不是淡的：平底上它是二等的陪衬字（跟着 EgDataStyle 走暗墨），压到
    /// 一张有明有暗的画面上就得自己站得住。洗过那一档的墨回到主题那支，因为那时它压的已经是正文的底色。
    /// </para>
    /// </summary>
    private void PaintTitleInk()
    {
        var onScrim = _titleStrip == TitleStrip.OnScrim;
        var onImage = onScrim && !Navigation.IsPaneOpen;

        _titleInk.Color = Ink(onImage ? "EgOnScrimBrush" : "EgTextBrush");
        _titleInkDim.Color = Ink(onImage ? "EgOnScrimDimBrush" : "EgTextDimBrush");
        _trailInk.Color = Ink(onScrim ? "EgOnScrimBrush" : "EgTextDimBrush");

        TrailBar.Background = _titleStrip == TitleStrip.Plain
            ? Application.Current.Resources["LayerFillColorDefaultBrush"] as Brush
            : null;

        // 四个键，两支画刷，只在第一遍时挂上去：换的是它们的颜色，键上的对象一直是这两个。
        AppTitleBar.Resources["ButtonForeground"] = _titleInk;
        AppTitleBar.Resources["ButtonForegroundPointerOver"] = _titleInk;
        AppTitleBar.Resources["ButtonForegroundPressed"] = _titleInk;
        AppTitleBar.Resources["ButtonForegroundDisabled"] = _titleInkDim;

        // 系统那三颗归窗口画：它们不在这棵树上，是 Win32 的非客户区。洗过那一档跟着「不在图上」走 ——
        // 那时它们压的是页面洗出来的正文底色，白墨在晴昼那套上就没了。
        _window?.SetCaptionOnScrim(onScrim);

        static Windows.UI.Color Ink(string key) =>
            Application.Current.Resources[key] is SolidColorBrush brush
                ? brush.Color
                : Microsoft.UI.Colors.White;
    }

    /// <summary>
    /// 侧边栏那些行的底和字，一行一行接到我们自己的画刷上。默认那套是中性灰的圆角药丸 + 一律全亮的字，
    /// 于是「你在哪一页」只靠一小块灰色说，而没选中的行和选中的行一样亮。改成：没选中的行是暗字、悬停跟
    /// 标题栏那五颗按钮同一层白、选中的行是一层薄强调色加亮字，左边那根指示条是强调色。
    /// <para>
    /// 和 <see cref="PaintTitleActions"/> 同一个理由放在代码里：键下面必须是 <c>ThemeHost</c> 正在改的
    /// <em>那一个</em> brush 对象。这里还多一条 —— 指示条默认是从 <c>SystemAccentColor</c> 派生的，而
    /// Palette.xaml 覆盖的是那个 <em>Color</em>，框架的画刷在解析自己字典时就把它取走冻住了，换主题不会动。
    /// 把共用对象塞到键上，指示条才跟着主题走。
    /// </para>
    /// </summary>
    private void PaintNavigationPane()
    {
        var resources = Application.Current.Resources;

        // 键名 → 我们的画刷名。成对写在一处，比十几行赋值好读，也好数。
        (string Key, string Brush)[] roles =
        [
            ("NavigationViewItemForeground", "EgTextDimBrush"),
            ("NavigationViewItemForegroundPointerOver", "EgTextBrush"),
            ("NavigationViewItemForegroundPressed", "EgTextBrush"),
            ("NavigationViewItemForegroundSelected", "EgTextBrush"),
            ("NavigationViewItemForegroundSelectedPointerOver", "EgTextBrush"),
            ("NavigationViewItemForegroundSelectedPressed", "EgTextBrush"),

            ("NavigationViewItemBackgroundPointerOver", "EgOverlayHoverBrush"),
            ("NavigationViewItemBackgroundPressed", "EgOverlayPressedBrush"),
            ("NavigationViewItemBackgroundSelected", "EgAccentMutedBrush"),
            ("NavigationViewItemBackgroundSelectedPointerOver", "EgAccentSoftBrush"),
            ("NavigationViewItemBackgroundSelectedPressed", "EgAccentSoftBrush"),

            ("NavigationViewSelectionIndicatorForeground", "EgAccentBrush"),
            ("NavigationViewItemSeparatorForeground", "EgBorderBrush")
        ];

        foreach (var (key, brush) in roles) Navigation.Resources[key] = resources[brush];
    }

    /// <summary>
    /// The settings document, for the three places routing reads it: the auto-login decision, the server
    /// switch menu, and which account that menu picks. Only ever reached after a guard on
    /// <see cref="_settings"/> or from a private helper the guarded path called.
    /// </summary>
    private AppSettings Settings => _settings!.Settings;

    /// <summary>For the self-check, which has to inspect the live tree from outside.</summary>
    internal NavigationView NavigationRoot => Navigation;

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

        // The arrows may well have been measured already, before there was a window to tell.
        ReportTitleBarHole();
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
    /// Gives the window to the player, or takes it back. The navigation shell is collapsed rather than
    /// merely covered: the XAML island is a single surface, so NavigationView's own opaque background
    /// would paint over exactly the region mpv's child window shows through.
    /// </summary>
    internal void ShowPlayer(bool playing)
    {
        if (playing)
        {
            AppTitleBar.Visibility = Visibility.Collapsed;
            if (_window is not null) _window.PlaybackTitleBar = true;
            Navigation.Visibility = Visibility.Collapsed;
            SignIn.Visibility = Visibility.Collapsed;
            return;
        }

        AppTitleBar.Visibility = Visibility.Visible;
        if (_window is not null) _window.PlaybackTitleBar = false;

        // The player leaves the page it was started from behind it, so both arrows are exactly as available
        // as they were — but the row was laid out before playback took the window, so it is re-decided here
        // rather than trusted.
        SyncChrome();

        // Not unconditionally the shell: a token can expire while a film is playing, and coming back to
        // a navigation pane belonging to an account that is no longer signed in would be worse than the
        // sign-in card the session already asked for.
        if (_session is { IsSignedIn: true }) Navigation.Visibility = Visibility.Visible;
        else SignIn.Visibility = Visibility.Visible;
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
    /// Shows the signed-in identity at the foot of the pane. Called by whoever owns the session; the
    /// shell does not go looking for one. 需求 2 took the avatar away, so this is two lines of text and
    /// nothing else — there is no picture left to hand a display name to.
    /// </summary>
    public void ShowAccount(string? user, string? server)
    {
        AccountUser.Text = string.IsNullOrWhiteSpace(user) ? "未登录" : user;
        AccountServer.Text = string.IsNullOrWhiteSpace(server) ? "未连接服务器" : server;
    }

    /// <summary>Navigates to a tag, whether the request came from the pane or from a page.</summary>
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

        // Requirement 6's other half. Nothing in the pane points at a detail page, so leaving the highlight
        // on the entry the drill-down started from is a lie — and it used to be a lie with consequences:
        // clicking 主页 while it was still highlighted moved no selection, so under SelectionChanged it
        // navigated nowhere and the poster the user had opened stayed on screen.
        _current = null;
        Navigation.SelectedItem = null;
        SyncChrome();
    }

    /// <summary>
    /// Opens a folder, series or season from inside a page: same page type, one level deeper, and the
    /// trail grows instead of being replaced. This is the one navigation the pane knows nothing about.
    /// </summary>
    internal void OpenChild(LibraryRequest request)
    {
        ContentFrame.Navigate(typeof(LibraryPage), request, new SuppressNavigationTransitionInfo());

        _trail.Add(new Crumb(request.Title, request.Tag));

        // Nothing in the pane corresponds to a drill-down, so the next pane click must always navigate —
        // including a click on the entry that is still highlighted, which is what requirement 6 is about.
        _current = null;
        Navigation.SelectedItem = null;
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

    /// <summary>A top-level destination: one crumb, a synced pane highlight, no fade.</summary>

    private void Open(Type page, object parameter, string title, string tag)
    {
        _current = tag;

        // SuppressNavigationTransitionInfo, not the default: requirement 9 rules out the fade, and
        // on a page whose first paint is a grid of posters the fade is exactly where it shows.
        ContentFrame.Navigate(page, parameter, new SuppressNavigationTransitionInfo());

        _trail.Clear();
        _trail.Add(new Crumb(title, tag));

        SyncSelection(tag);
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
        // Still wired: this is what answers Alt+Left and the mouse's own back button.
        Navigation.IsBackEnabled = canGoBack;

        BackButton.IsEnabled = canGoBack;
        ForwardButton.IsEnabled = canGoForward;

        // The whole row, not just the trail inside it: one crumb only repeats the heading the page draws
        // right below, and a row left standing empty is a band of nothing 38 pixels tall.
        TrailBar.Visibility = crumbs > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Tells the window which rectangle of its title bar the five buttons occupy, in the island's logical
    /// pixels, so the frame stops answering 「caption」 there and their clicks arrive. Measured rather than
    /// assumed: the panel's position depends on the mark's own width and the current scale.
    /// </summary>
    private void ReportTitleBarHole()
    {
        // Zero while the strip is collapsed for playback. Reporting that would hand the whole strip back to
        // the frame, which is right for playback and wrong the moment browsing returns — and playback has
        // already claimed the strip whole by then anyway, so the last real rectangle is the one to keep.
        if (_window is null || TitleActions.ActualWidth <= 0 || TitleActions.ActualHeight <= 0) return;

        var origin = TitleActions.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, 0));
        _window.SetTitleBarHole(origin.X, origin.Y, TitleActions.ActualWidth, TitleActions.ActualHeight);
    }

    /// <summary>
    /// Keeps the pane's highlight on the tag actually being shown. Needed because a page can
    /// navigate without the pane being touched, and NavigationView will otherwise keep highlighting
    /// wherever the user last clicked.
    /// </summary>
    private void SyncSelection(string tag)
    {
        // MenuItems alone. The footer is a button rather than a list of destinations now (需求 2) and the
        // pane's settings entry is off (需求 1), so 主页 and the libraries are the whole of what the pane can
        // point at — and 搜索, which no entry points at, correctly matches nothing and clears the highlight.
        var match = Navigation.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(item => (item.Tag as string) == tag);

        Navigation.SelectedItem = match;
    }

    /// <summary>
    /// 需求 6：「点击主页或者左侧的家园图标后，要直接回到主页界面」.
    /// <para>
    /// ItemInvoked, not SelectionChanged. A click on the entry that is already highlighted moves no
    /// selection, so SelectionChanged never fired for it and the click did nothing — which is exactly the
    /// case the requirement describes, because drilling into a poster leaves 主页 highlighted while the
    /// frame shows a detail page. <see cref="OpenDetail"/> now clears the highlight as well, so the two
    /// halves agree whichever way the user gets back.
    /// </para>
    /// </summary>
    private void OnItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs e)
    {
        if (e.InvokedItemContainer is NavigationViewItem { Tag: string tag }) GoTo(tag);
    }

    /// <summary>需求 1 的第一颗按键。The pane's own toggle is off, so this is the only thing that folds it.</summary>
    private void OnPaneToggleClicked(object sender, RoutedEventArgs e) =>
        Navigation.IsPaneOpen = !Navigation.IsPaneOpen;

    /// <summary>需求 1 的第二颗：「点击后弹出设置窗口」.</summary>
    private void OnSettingsClicked(object sender, RoutedEventArgs e) => ShowSettings();

    /// <summary>需求 1 的第三颗：「点击后进入搜索页面，直接在当前窗口跳转」 — a page in this frame, not a window.</summary>
    private void OnSearchClicked(object sender, RoutedEventArgs e) => OpenSearch();

    /// <summary>
    /// Keeps the pane's footer honest about the room it has, and keeps the title-bar row clear of the pane's
    /// edge. Both pane events land here because it is the same question either way — and so does the
    /// constructor, because the pane starts closed (「侧边栏默认为折叠状态」) and an initial value raises
    /// neither event.
    /// <para>
    /// 页脚那半：收起来的时候侧边栏只有 48 像素宽，而「用户名 ＋ 服务器名」两行字是不会自己截断的 —— 它会直接
    /// 画过侧边栏的边、横到页面上。所以窄条那一档只留一颗人形字形。
    /// </para>
    /// <para>
    /// 缩进那半（「折叠状态下图标要向右移动一些，防止图标和侧边栏重合」）：那一排按键的左缩进是 40，侧边栏
    /// 张开时它们整排站在侧边栏自己的底色上，好看；收成窄条之后 48 就成了窄条和工作区的分界，40 让第一颗托盘
    /// 正好跨在那道竖线上 —— 半边在窄条里、半边在片场里。窄条那一档因此把整排让到线的右边：
    /// <c>CompactPaneLength</c>（48）＋ 那道线自己的 1 ＋ 9，第一颗图标离线 16 像素，正好等于这一排里相邻两颗
    /// 图标之间的距离（托盘 34 装 20 的图标，两边各 7，加上排距 2）—— 那道线于是读成这一排的又一个邻居。
    /// </para>
    /// <para>
    /// 改缩进要自己再报一次窗口那个洞：<c>Margin</c> 变的是位置不是尺寸，<c>SizeChanged</c> 不响，而洞是照这
    /// 一排的矩形挖的。少报这一次，屏上一切正常，但整排右边那 18 像素会变回「拖动区」—— 点前进键会把窗口拖走。
    /// </para>
    /// </summary>
    private void SyncPane()
    {
        var open = Navigation.IsPaneOpen;

        AccountDetails.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        AccountChevron.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        AccountGlyph.Visibility = open ? Visibility.Collapsed : Visibility.Visible;

        var inset = open ? TitleActionsInset : Navigation.CompactPaneLength + PaneClearance;
        if (Math.Abs(TitleActions.Margin.Left - inset) > 0.5)
        {
            TitleActions.Margin = new Thickness(inset, 0, 0, 0);

            // 量之前先让布局跑一遍：TransformToVisual 读的是当前的排布，改完立刻量会量到旧位置。
            TitleActions.UpdateLayout();
            ReportTitleBarHole();
        }

        // 收成窄条时左端那五颗按钮就从侧边栏的底色上挪到了主页那张大图上，墨要跟着换（见 PaintTitleInk）。
        PaintTitleInk();
    }

    /// <inheritdoc cref="SyncPane"/>
    private void OnPaneToggled(NavigationView sender, object args) => SyncPane();

    /// <summary>
    /// 「默认收起侧边栏」这一句摆到屏幕上：<paramref name="collapsed"/> 是收起，反过来是张开。第一次是
    /// <c>Navigation.Loaded</c> 叫的（那时才有模板可按），之后每次那个开关被改都再叫一遍
    /// （<see cref="ShellPrefs"/>）——「改完要重启才算」的开关读起来就是个坏开关。
    /// <para>
    /// 收起那一档故意先张开再关上，理由写在构造器里那一段：NavigationView 只在「张着的时候被关掉」这一下才
    /// 记住是人要关的，不记住的话它每次重排都会自己张开。张开那一档不需要这一下 —— 它本来就爱张开。
    /// </para>
    /// </summary>
    private void ApplyPaneDefault(bool collapsed)
    {
        Navigation.IsPaneOpen = true;
        if (collapsed) Navigation.IsPaneOpen = false;

        SyncPane();
    }

    private void OnBackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs e) => GoBack();

    /// <summary>The row's own arrows. Same walk as the keyboard's, so neither can drift.</summary>
    private void OnBackClicked(object sender, RoutedEventArgs e) => GoBack();

    private void OnForwardClicked(object sender, RoutedEventArgs e) => GoForward();

    /// <summary>
    /// One step back: the frame, the trail and the pane highlight. Reached from the row's own button and
    /// from <c>NavigationView.BackRequested</c>, which is still wired because that is what raises Alt+Left
    /// and the mouse's own back button — the pane's arrow is gone, the gestures behind it are not.
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

        // Empty is a drill-down: a real page, but not one the pane can point at.
        _current = tag is { Length: > 0 } ? tag : null;
        if (_current is null) Navigation.SelectedItem = null;
        else SyncSelection(_current);
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
    /// A top-level destination with a tag of its own, so the trail, the pane highlight and 「already there」
    /// all behave the way they do for 主页 — which is what lets the button be pressed a second time without
    /// throwing away what has been typed into the page.
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
    /// Swaps the sign-in card for the browsing shell: reads this account's libraries, fills the pane
    /// and the two switch menus, and opens the home page.
    /// </summary>
    private async Task EnterShellAsync()
    {
        if (_session is null || _capabilities is null) return;

        SignIn.Detach();
        SignIn.Visibility = Visibility.Collapsed;
        Navigation.Visibility = Visibility.Visible;

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
        while (Navigation.MenuItems.Count > FixedPaneItems)
            Navigation.MenuItems.RemoveAt(Navigation.MenuItems.Count - 1);

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
            // The pane is left with its fixed entries; the home page will report the same failure in
            // the place the user is actually looking.
            Log.Warn(Category, "读取媒体库列表失败", error);
            LibraryHeader.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (var view in views)
        {
            // 需求 3：「屏蔽媒体库里音乐的内容」. Filtered here rather than in each place a library can be
            // reached, because this loop is the only source of all three: the pane's entries, the requests
            // those entries open, and the 媒体库 row on the home page, which is this same list drawn as
            // cards. A music library dropped here cannot be reached from anywhere, and 继续观看/接下来看/
            // 最近添加 drop their own music rows in HomeViewModel because those three span every library.
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

            Navigation.MenuItems.Add(new NavigationViewItem
            {
                Content = view.Name,
                Tag = tag,
                Icon = new FontIcon { Glyph = char.ConvertFromUtf32(GlyphFor(view.CollectionType)) }
            });
        }

        LibraryHeader.Visibility = _libraries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Log.Info(Category, views.Count == _libraries.Count
            ? $"媒体库 {_libraries.Count} 个已进入导航栏"
            : $"媒体库 {_libraries.Count} 个已进入导航栏（服务器共 {views.Count} 个，音乐库已屏蔽）");
    }

    /// <summary>
    /// A library's icon. No music case: 需求 3 filters those libraries out before this is asked, so a glyph
    /// for one would be a promise the pane never keeps.
    /// </summary>
    private static int GlyphFor(string? collectionType) => collectionType switch
    {
        "movies" => MovieGlyph,
        "tvshows" or "livetv" => SeriesGlyph,
        "photos" or "homevideos" => PhotoGlyph,
        _ => FolderGlyph
    };

    /// <summary>
    /// 切换服务器 / 切换用户: the saved servers and the accounts on the current one, both one click away from
    /// the button at the foot of the pane. A switch that has a saved token or password does not ask for
    /// anything; one that does not lands on the sign-in card with the address already filled in.
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
        Navigation.Visibility = Visibility.Collapsed;
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

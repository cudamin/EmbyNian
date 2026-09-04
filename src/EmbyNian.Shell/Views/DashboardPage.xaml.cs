using System.Text.Json;
using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Infrastructure;
using EmbyNian.Shell.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;

namespace EmbyNian.Shell.Views;

/// <summary>导航到服务器控制台页时传入的容器。</summary>
internal sealed record DashboardRequest(IServiceProvider Services);

/// <summary>
/// 需求 8：Emby 网页端的控制台，用 <c>WebView2</c> 嵌在设置里，并且是已经登录好的状态。
/// <para>
/// The WebView2 assembly comes with the Windows App SDK, so there is no package reference to add; what the
/// app does have to bring is a browser profile directory of its own (see
/// <see cref="AppPaths.WebViewDirectory"/>) — a non-packaged app otherwise gets one next to the executable,
/// which in the publish directory need not be writable.
/// </para>
/// <para>
/// Sign-in is seeded, not typed: <see cref="EmbyWebConsole.SignInScript"/> runs at document start and leaves
/// this session's credentials exactly where the web client's own sign-in would have. The token therefore
/// never appears in a URL, in a log line or in the self-check report — only inside that script string.
/// </para>
/// <para>
/// 深浅跟着本应用当前的主题走，同样是文档开始脚本加一位浏览器设置 —— 见
/// <see cref="EmbyWebConsole.ThemeScript"/> 和 <see cref="StartAsync"/> 里那一段。
/// </para>
/// </summary>
public sealed partial class DashboardPage : Page, IShellContent
{
    private const string Category = "控制台";

    /// <summary>How long to wait for a page that neither loads nor fails before saying so.</summary>
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Long enough for the client's boot to have restored the seeded session before sampling it.</summary>
    private static readonly TimeSpan SampleDelay = TimeSpan.FromSeconds(2.5);

    private object? _request;
    private ISystemLauncher? _launcher;
    private string _url = "";
    private bool _core;
    private bool _seeded;
    private bool? _navigated;
    private string _error = "";
    private string _client = "尚未取样";
    private bool _settled;
    private bool _released;

    /// <summary>页头读数的两半：控制台地址，和它此刻的载入状态。见 <see cref="Say"/>。</summary>
    private string _address = "尚未确定地址";
    private string _state = "";

    public DashboardPage() => InitializeComponent();

    /// <summary>
    /// True once there is nothing more to wait for: the console is up and sampled, or it has failed and said
    /// why. The self-check's <c>Settled</c> gate reads this, so every terminal path has to set it — including
    /// the ones that never reach the network.
    /// </summary>
    internal bool IsReady => _settled;

    /// <summary>
    /// What the self-check reports. <see cref="Client"/> is a sentence about the web client's own state,
    /// sampled out of the page; deliberately no token, no cookie and no user id in any of it.
    /// </summary>
    internal (bool Ready, string Url, bool Core, bool Seeded, bool? Navigated, string Error, string Client) Summary =>
        (_settled, _url, _core, _seeded, _navigated, _error, _client);

    public object? NavigationRequest => _request;

    /// <summary>
    /// 把地址和载入状态写到页头右上角那一行读数上。两个都是机器串，等宽字里它们是同一种东西，所以合成
    /// 一行而不是各占一行 —— 页头不该为「正在载入…」这种一闪而过的字长出第三行来。
    /// </summary>
    private void Say(string state)
    {
        _state = state;
        Slate.Note = (_address, _state) switch
        {
            ("", var only) => only,
            (var only, "") => only,
            var (address, status) => $"{address} · {status}"
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not DashboardRequest request)
        {
            Log.Warn(Category, "导航参数缺失");
            return;
        }

        _request = request;
        Tag = "dashboard";

        var services = request.Services;
        _launcher = services.GetRequiredService<ISystemLauncher>();
        var session = services.GetRequiredService<EmbySession>();

        // Nothing to embed and nothing to seed: the console is per-user, and this app has no user yet.
        if (session.Connection is not { } connection)
        {
            _url = "";
            _address = "尚未登录 Emby 服务器";
            Say("");
            Show("先在「服务器」里登录一个账号，Emby 自己的控制台就会显示在这里。");
            _settled = true;
            return;
        }

        _url = EmbyWebConsole.Url(connection.ApiBase);
        _address = _url;
        Say("正在启动内嵌浏览器…");
        _ = StartAsync(connection, services.GetRequiredService<AppPaths>().WebViewDirectory);
    }

    /// <summary>
    /// Brings the control up on its own profile, registers the sign-in seed, then navigates.
    /// <para>
    /// Order matters: the seed has to be registered before the navigation that needs it, because
    /// <c>AddScriptToExecuteOnDocumentCreatedAsync</c> only affects documents created after it returns.
    /// Every <c>await</c> is followed by a <see cref="_released"/> check — the settings window can be closed
    /// while the browser is still starting, and continuing then would touch a closed control.
    /// </para>
    /// </summary>
    private async Task StartAsync(EmbyConnection connection, string profile)
    {
        try
        {
            Directory.CreateDirectory(profile);

            var environment = await CoreWebView2Environment
                .CreateWithOptionsAsync("", profile, new CoreWebView2EnvironmentOptions());
            if (_released) return;

            await Web.EnsureCoreWebView2Async(environment);
            if (_released || Web.CoreWebView2 is not { } core) return;
            _core = true;

            // 控制台跟着当前主题的深浅走，两半都在这儿：这一位是浏览器的 prefers-color-scheme，另一半是
            // 下面那段脚本把网页端的主题设成 auto（见 EmbyWebConsole.ThemeScript）。设在导航之前，因为
            // 网页端是在启动时读一次深浅来挑主题的。
            //
            // 只在这里读一次 ThemeHost.Current，不订阅换主题的通知：主题方块在设置的「界面」那张卡上，
            // 而这一页和那张卡在设置里是互斥的两个选项 —— 换主题必然先离开这一页（离开就 Release），
            // 回来时 SettingsPage.ShowHosted 是重新导航一次，于是这一行本来就会重新跑。
            core.Profile.PreferredColorScheme = ThemeHost.Current.IsDark
                ? CoreWebView2PreferredColorScheme.Dark
                : CoreWebView2PreferredColorScheme.Light;

            Web.NavigationCompleted += OnNavigationCompleted;
            core.NewWindowRequested += OnNewWindowRequested;

            await core.AddScriptToExecuteOnDocumentCreatedAsync(EmbyWebConsole.SignInScript(
                connection.ApiBase, connection.UserId, connection.AccessToken, connection.ServerName));
            if (_released) return;
            _seeded = true;

            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                EmbyWebConsole.ThemeScript(connection.ApiBase, connection.UserId));
            if (_released) return;

            Say("正在载入控制台…");
            core.Navigate(_url);
            _ = GiveUpAsync();
        }
        catch (Exception error)
        {
            // Most likely no WebView2 runtime on the machine. Say so instead of showing an empty box.
            _error = error.Message;
            _settled = true;
            Log.Warn(Category, $"内嵌控制台启动失败：{error.Message}");
            if (!_released) Show($"内嵌浏览器没能启动：{error.Message}\n可以点右上角「用浏览器打开」，用系统浏览器看同一个页面。");
        }
    }

    /// <summary>
    /// Records how the navigation ended and, when it worked, asks the page what the web client made of the
    /// seeded credentials. A failure is reported rather than retried: retrying an unreachable server just
    /// makes the self-check wait longer for the same answer.
    /// </summary>
    private void OnNavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_released) return;

        _navigated = e.IsSuccess;
        if (e.IsSuccess)
        {
            Say("已载入");
            _ = SampleAsync();
            return;
        }

        _error = e.WebErrorStatus.ToString();
        _settled = true;
        Log.Warn(Category, $"控制台页面没能打开：{e.WebErrorStatus}");
        Show($"打不开 {_url}\n（{e.WebErrorStatus}）");
    }

    /// <summary>
    /// A link the console wants to open in a new window — its 「帮助」 entries, mostly. Hands it to the
    /// system browser: a second WebView with no chrome is a dead end for the person looking at it.
    /// </summary>
    private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        Open(e.Uri);
    }

    /// <summary>
    /// One look at what the web client ended up doing with the seeded session: which route it settled on,
    /// which theme it resolved to, and whether it filled in the server id — which it only does once a
    /// connection has been validated, so that is the readable proof that auto sign-in worked. Reads no
    /// credential and returns none.
    /// <para>
    /// 主题读的是 <c>&lt;html&gt;</c> 上那个 <c>theme-*</c> 类名（<c>skinmanager.js</c> 挑好主题后就加在
    /// 那儿），所以它答的是「网页端最后真的用了哪套」，而不是「我们请求了哪套」—— 跟随主题这件事只有
    /// 这一句话能证明成或不成。
    /// </para>
    /// </summary>
    private async Task SampleAsync()
    {
        await Task.Delay(SampleDelay);
        if (_released || Web.CoreWebView2 is null) return;

        try
        {
            var json = await Web.ExecuteScriptAsync($$"""
                (function () {
                  try {
                    var data = JSON.parse(localStorage.getItem({{JsonSerializer.Serialize(EmbyWebConsole.StorageKey)}}) || "null");
                    var servers = data && Array.isArray(data.Servers) ? data.Servers : [];
                    var id = "未填";
                    for (var i = 0; i < servers.length; i++) if (servers[i] && servers[i].Id) { id = "已填"; break; }

                    var theme = "(无)";
                    var names = (document.documentElement.className || "").split(" ");
                    for (var j = 0; j < names.length; j++)
                      if (names[j].indexOf("theme-") === 0) { theme = names[j].substring(6); break; }

                    return "路由 " + (location.hash || "(无)") + "  ·  主题 " + theme + "  ·  服务器 id " + id;
                  } catch (e) { return "取样出错：" + e; }
                })();
                """);
            if (_released) return;

            var text = JsonSerializer.Deserialize<string>(json);
            _client = string.IsNullOrWhiteSpace(text) ? "没有取到" : text!;
            Say(_client);
        }
        catch (Exception error)
        {
            _client = $"取样失败：{error.Message}";
        }
        finally
        {
            _settled = true;
        }
    }

    /// <summary>
    /// The 自检 must not sit on a page that will never answer. An unreachable address can leave a navigation
    /// hanging for far longer than the walk's own budget, so past this point the page settles and says it did
    /// not finish rather than pretending it is still working.
    /// </summary>
    private async Task GiveUpAsync()
    {
        await Task.Delay(LoadTimeout);
        if (_released || _settled) return;

        _settled = true;
        _client = $"{LoadTimeout.TotalSeconds:0} 秒内没有结果";
        Log.Warn(Category, $"控制台 {LoadTimeout.TotalSeconds:0} 秒内既没载入也没报错");
        Say("载入很慢，还在等服务器…");
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (Web.CoreWebView2 is not { } core || _url.Length == 0) return;

        _navigated = null;
        _settled = false;
        _error = "";
        Notice.Visibility = Visibility.Collapsed;
        Web.Visibility = Visibility.Visible;
        Say("正在重新载入…");
        core.Navigate(_url);
        _ = GiveUpAsync();
    }

    private void OnOpenInBrowser(object sender, RoutedEventArgs e) => Open(_url);

    private void Open(string url)
    {
        if (_launcher is null || string.IsNullOrWhiteSpace(url)) return;

        try
        {
            _launcher.OpenUrl(url);
        }
        catch (Exception error)
        {
            Log.Warn(Category, $"打不开 {url}：{error.Message}");
        }
    }

    /// <summary>Puts a sentence where the console would have been, and takes the empty control off screen.</summary>
    private void Show(string message)
    {
        Notice.Text = message;
        Notice.Visibility = Visibility.Visible;
        Web.Visibility = Visibility.Collapsed;
        Say("");
    }

    /// <summary>
    /// Lets go of the browser. Called both on navigating away and when the settings window is hidden, because
    /// a hidden window holding a live WebView2 is a browser process the user cannot see and did not ask for.
    /// The page instance is not reused afterwards — the hosted frame builds a new one on the next visit — so
    /// closing rather than merely stopping is the right cleanup.
    /// </summary>
    public void Release()
    {
        if (_released) return;
        _released = true;

        try
        {
            Web.NavigationCompleted -= OnNavigationCompleted;
            if (Web.CoreWebView2 is { } core) core.NewWindowRequested -= OnNewWindowRequested;
            Web.Close();
        }
        catch (Exception error)
        {
            Log.Debug(Category, $"关闭内嵌控制台时出错：{error.Message}");
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Release();
        base.OnNavigatedFrom(e);
    }
}

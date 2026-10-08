using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Momoka.Configuration;
using Momoka.Emby;
using Momoka.Infrastructure;
using Momoka.MoviePilot;
using Momoka.Playback;
using Momoka.Services;
using Momoka.Shell.Platform;
using Momoka.Shell.ViewModels;
using Momoka.Shell.Views;
using Momoka.Shell.Windowing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Momoka.Shell;

/// <summary>真实 Shell 状态和控件的离线回归；只允许假传输、临时设置及拒绝播放的外壳。</summary>
internal sealed partial class ShellNavigationProbe
{
    internal static int ExitCode { get; private set; } = 1;
    private readonly StartupOptions _options;
    private readonly List<Check> _checks = [];
    private readonly Grid _root = new();
    private HostWindow? _window;

    private ShellNavigationProbe(StartupOptions options) => _options = options;
    internal static Task RunAsync(StartupOptions options) => new ShellNavigationProbe(options).RunAsync();

    private async Task RunAsync()
    {
        try
        {
            ThemeHost.Apply(_options.Theme ?? "midnight");
            _window = new HostWindow { Content = _root, FreeSizing = true };
            _window.Show(false, _options.Screen, new WindowBounds(80, 80, 1420, 980));
            await LayoutAsync(_root);
            await CaseAsync("切回 Emby 恢复查询代次", ResumeQueryAsync);
            await CaseAsync("切季与整页刷新交错不清空", SeasonRefreshRaceAsync);
            await CaseAsync("旧导航恢复不补新搜索", NavigationRestoreRaceAsync);
            await CaseAsync("分页失败立即停止", PagingFailureAsync);
            await CaseAsync("离页不会复活补页", PagingCancelAsync);
            await CaseAsync("新查询失败不混旧页", QueryFailureAsync);
            await CaseAsync("同词重试并保留导航参数", SearchStateAsync);
            await CaseAsync("搜索居中、工具栏换行和来源切换", SearchHeaderLayoutAsync);
            await CaseAsync("加载中禁止播放全部", PlaybackBusyAsync);
            await CaseAsync("字母查询过期不继续", JumpCancelAsync);
            await CaseAsync("切季回到原季撤销旧请求", SeasonCancelAsync);
            await CaseAsync("切季目标失败不部分提交", SeasonFailureAsync);
            await CaseAsync("空季保留选择器", EmptySeasonAsync);
            await CaseAsync("离页后写操作不重开详情", DetailWriteAsync);
            await CaseAsync("隐藏首页行不请求", HiddenHomeAsync);
            await CaseAsync("通知空态属性通知", NotificationEmptyAsync);
            await CaseAsync("离开通知页不弹旧编辑器", NotificationCancelAsync);
            await CaseAsync("过期卡片不发送菜单命令", StaleCardAsync);
            await CaseAsync("封面选择取消不接受迟到上传", CoverPickRaceAsync);
            await CaseAsync("封面列表及绑定跟随刷新", CoverRefreshAsync);
            await CaseAsync("封面确认复用对话框且取消安全", CoverCancelAsync);
            await CaseAsync("封面写入期间等待关闭", CoverWriteAsync);
            await CaseAsync("封面结果不明须先刷新", CoverUncertainAsync);
            await CaseAsync("封面迟到图片不复活", CoverLateImageAsync);
            await CaseAsync("完整返回历史和主页按钮", ShellHistoryAsync);
            await CaseAsync("播放返回主页不补播入场", HomePlaybackReturnAsync);
            await CaseAsync("设置分类动效与快速切换", SettingsEntranceAsync);
            await CaseAsync("原生控制台分区、响应布局与离页取消", ServerDashboardAsync);
            await CaseAsync("服务器管理菜单、确认与身份隔离", ServerAdministrationAsync);
            await CaseAsync("用户管理导航、页签与权限保存", ServerUsersAsync);
            await CaseAsync("用户创建、复制、头像、删除与重试", ServerUsersMutationsAsync);
            await CaseAsync("用户管理离页、切服与普通账号隔离", ServerUsersIdentityAsync);
            await CaseAsync("媒体库页面、动态选项与并发保存", ServerLibrariesAsync);
            await CaseAsync("媒体库新建、目录、扫描与移除", ServerLibraryOperationsAsync);
            await CaseAsync("媒体库高级设置和部分失败重试", ServerLibraryAdvancedAsync);
            await CaseAsync("媒体库离页、切服与管理权限", ServerLibraryIdentityAsync);
            await CaseAsync("MoviePilot 订阅分区、封面与管理操作", MoviePilotSubscriptionsAsync);
            await CaseAsync("MoviePilot 订阅详情、入库缺集与编辑", MoviePilotSubscriptionDetailAsync);
            await CaseAsync("MoviePilot 订阅切服、旧确认与离页取消", MoviePilotSubscriptionIdentityAsync);
            await CaseAsync("季页面右侧播放图标与原播放路由", SeasonTrailingPlayAsync);
            await CaseAsync("旧登录目录不覆盖新身份", ShellIdentityAsync);
            await CaseAsync("切服逆序完成不退旧登录", ShellSwitchRaceAsync);
            await CaseAsync("目录首次失败可重试", ShellDirectoryRetryAsync);
            await CaseAsync("日志淘汰后显示空态", DiagnosticsEmptyAsync);
            await CaseAsync("对话框构造自检路径", DialogConstructionAsync);
            if (_options.InspectShell) await InspectAsync();
            ExitCode = _checks.All(check => check.Passed) ? 0 : 1;
        }
        catch (Exception error)
        {
            _checks.Add(new Check("探针宿主", false, error.ToString()));
        }
        finally
        {
            Save();
            _window?.Dispose();
            Application.Current.Exit();
        }
    }

    private async Task CaseAsync(string name, Func<Task> run)
    {
        try
        {
            await run().WaitAsync(TimeSpan.FromSeconds(12));
            _checks.Add(new Check(name, true, ""));
        }
        catch (Exception error) { _checks.Add(new Check(name, false, error.ToString())); }
        Save();
    }

    private void Save() => File.WriteAllText(Path.Combine(_options.Paths.LogDirectory, "shell-probe.json"),
        JsonSerializer.Serialize(_checks, new JsonSerializerOptions { WriteIndented = true }));

    private Fixture CreateFixture() => new(Path.Combine(_options.Paths.Root, "fixtures", Guid.NewGuid().ToString("N")));
    private static TaskCompletionSource<T> Pending<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static object? Call(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void Field(object target, string name, object? value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static T Get<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static async Task SettleAsync() => await Task.Delay(80);
    private static async Task UntilAsync(Func<bool> ready)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            if (ready()) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("未等到预期状态");
    }
    private static async Task LayoutAsync(FrameworkElement element)
    {
        await UntilAsync(() =>
        {
            element.UpdateLayout();
            return element.XamlRoot is not null && element.ActualWidth > 0 && element.ActualHeight > 0;
        });
        await Task.Delay(150);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private sealed record Check(string Name, bool Passed, string Detail);

    private sealed class Fixture : IDisposable
    {
        public AppSettings Settings { get; } = new();
        public FakeTransport Transport { get; } = new();
        public FakeActions Actions { get; } = new();
        public EmbySession Session { get; }
        public ISettingsService SettingsService { get; }
        public EmbyImageStore Images { get; }
        public IServerCapabilities Capabilities { get; } = new FakeCapabilities();
        public ISystemLauncher Launcher { get; } = new FakeLauncher();
        public IServiceProvider Services { get; }
        public Fixture(string root)
        {
            Settings.Servers.Clear();
            Settings.Ui.PageSize = 20;
            Settings.Ui.ShowHomeBanner = false;
            var store = new SettingsStore(new AppPaths(root), PassthroughSecretProtector.Instance);
            var vault = new CredentialVault(PassthroughSecretProtector.Instance);
            SettingsService = new SettingsService(store, Settings);
            Session = new EmbySession(Settings, store, vault, DeviceIdentity.Create("offline-shell", "test"), Transport);
            Images = new EmbyImageStore(Session, Path.Combine(root, "images"));
            var moviePilot = new MoviePilotService(new MoviePilotClient(new RejectNetwork()),
                new MoviePilotCredentials(PassthroughSecretProtector.Instance), Settings);
            Services = new ServiceCollection().AddSingleton(Session).AddSingleton(SettingsService)
                .AddSingleton(Images).AddSingleton(Capabilities).AddSingleton(vault).AddSingleton(moviePilot)
                .AddSingleton<IShellActions>(Actions).AddSingleton(Launcher).BuildServiceProvider();
        }
        public async Task SelectAsync(string host = "a.invalid", string user = "user")
        {
            var server = new ServerProfile { Name = host, Url = $"https://{host}/media" };
            var account = new AccountProfile
            {
                Username = user,
                UserId = user,
                ProtectedAccessToken = PassthroughSecretProtector.Instance.Protect("offline-fixture-token")
            };
            server.Accounts.Add(account);
            Settings.Servers.Add(server);
            Require(await Session.TryRestoreAsync(server, account, CancellationToken.None), "假身份恢复失败");
        }
        public LibraryViewModel Library(string term = "first")
        {
            var vm = new LibraryViewModel();
            vm.Attach(LibraryRequest.Search(Services, term), Actions, SettingsService, Session, Images, Capabilities);
            return vm;
        }
        public DetailViewModel Detail()
        {
            var vm = new DetailViewModel();
            vm.Attach(DetailRequest.For(Services, Series()), Actions, SettingsService, Session, Images, Launcher);
            return vm;
        }
        public void Dispose()
        {
            Session.Dispose();
            (Services as IDisposable)?.Dispose();
        }
    }

    private sealed class RejectNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new InvalidOperationException("离线探针禁止真实网络"));
    }

    private sealed class FakeTransport : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>?>? Reply { get; set; }
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Require(request.RequestUri!.Host.EndsWith(".invalid", StringComparison.Ordinal), "探针拒绝真实网络目标");
            Requests.Add(request.Method.Method + " " + request.RequestUri.PathAndQuery);
            return Reply?.Invoke(request, cancellationToken) ?? Task.FromResult(Standard(request));
        }
        internal static HttpResponseMessage Standard(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/Views", StringComparison.Ordinal)) return Json(new { Items = Array.Empty<EmbyItem>(), TotalRecordCount = 0 });
            if (path.EndsWith("/System/Info/Public", StringComparison.Ordinal)) return Json(new { ServerName = "离线服务器", Version = "4.9.0" });
            if (path.Contains("/Images/", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (path.EndsWith("/Seasons", StringComparison.Ordinal)) return Json(new { Items = new[] { Season("a"), Season("b") } });
            if (path.EndsWith("/Episodes", StringComparison.Ordinal))
            {
                var season = Query(request, "SeasonId") ?? "a";
                return Json(new { Items = new[] { Episode(season) } });
            }
            if (path.EndsWith("/Similar", StringComparison.Ordinal)) return Items(0, 0);
            if (path.EndsWith("/Items/show", StringComparison.Ordinal)) return Json(Series());
            if (path.Contains("/Items/ep-", StringComparison.Ordinal)) return Json(Episode(path.Split("ep-")[1]));
            if (path.Contains("PlayedItems", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.NoContent);
            if (path.EndsWith("/Items", StringComparison.Ordinal)) return Items(2, 2);
            return Json(Array.Empty<object>());
        }
    }

    private static string? Query(HttpRequestMessage request, string name) => request.RequestUri!.Query.TrimStart('?')
        .Split('&').Select(value => value.Split('=', 2)).Where(pair => pair.Length == 2 && pair[0].Equals(name, StringComparison.OrdinalIgnoreCase))
        .Select(pair => Uri.UnescapeDataString(pair[1])).FirstOrDefault();
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Items(int count, int total, string prefix = "item") => Json(new
    {
        Items = Enumerable.Range(0, count).Select(index => new EmbyItem { Id = prefix + index, Name = prefix + index, Type = EmbyItemType.Movie }).ToArray(),
        TotalRecordCount = total
    });
    private static EmbyItem Series() => new() { Id = "show", Name = "离线测试剧集", Type = EmbyItemType.Series };
    private static EmbyItem Season(string id) => new() { Id = id, Name = "第 " + id + " 季", Type = EmbyItemType.Season, SeriesId = "show", IndexNumber = id == "a" ? 1 : 2 };
    private static EmbyItem Episode(string season) => new()
    { Id = "ep-" + season, Name = "离线单集 " + season, Type = EmbyItemType.Episode, SeriesId = "show", SeasonId = season, IndexNumber = 1 };

    private sealed class FakeCapabilities : IServerCapabilities
    {
        public Version? ServerVersion => new(4, 9);
        public Task ProbeAsync(CancellationToken token = default) => Task.CompletedTask;
    }
    private sealed class FakeLauncher : ISystemLauncher
    {
        public void OpenFolder(string path) => throw new InvalidOperationException("探针不打开外部程序");
        public void OpenUrl(string url) => throw new InvalidOperationException("探针不打开外部程序");
    }
    private sealed class FakeActions : IShellActions
    {
        public int Plays { get; private set; }
        public int Opened { get; private set; }
        public Action<EmbyItem, IReadOnlyList<EmbyItem>?>? CapturePlay { get; set; }
        public void OpenItem(EmbyItem item) => Opened++;
        public Task PlayAsync(EmbyItem item, EmbyItem? parent = null, PlaybackChoice? choice = null, IReadOnlyList<EmbyItem>? episodes = null)
        {
            Plays++;
            if (CapturePlay is { } capture) { capture(item, episodes); return Task.CompletedTask; }
            throw new InvalidOperationException("探针禁止播放");
        }
        public void Notify(string message, InfoBarSeverity severity = InfoBarSeverity.Informational) { }
        public bool TryOpenLibrary(string id) => false;
        public void OpenGenre(string genre) { }
        public void OpenRow(HomeLayout.HomeRowTarget target) { }
        public void OpenSignIn(ServerProfile? server = null) { }
        public Task SwitchProfileAsync(ServerProfile server, AccountProfile? account) => Task.CompletedTask;
        public void SetTitleStrip(TitleStrip strip) { }
        public bool MoviePilotEnabled => false;
        public void SearchMoviePilotVersions(EmbyItem item) { }
        public void ShowMoviePilotReorganize(Momoka.MoviePilot.MoviePilotTransferContext context) { }
    }
}

using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Services;
using EmbyNian.Shell.Composition;
using EmbyNian.Shell.Views;
using EmbyNian.Shell.Windowing;
using EmbyNian.Theming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace EmbyNian.Shell;

/// <summary>
/// The XAML application object. It owns no window of its own — <see cref="HostWindow"/> is a plain
/// Win32 HWND and the UI lives in an island inside it — so this class exists mainly to give XAML a
/// resource scope and to be the thing <c>Application.Start</c> constructs.
/// </summary>
public partial class App : Application
{
    private const string Category = "启动";

    private readonly StartupOptions _options;

    /// <summary>
    /// The container, built here and owned here. This is the composition root: the one place that knows
    /// there is a container at all — the shell is handed it so each page can resolve what it needs, and
    /// nothing else in the app reaches for a provider.
    /// </summary>
    private ServiceProvider? _container;

    private HostWindow? _window;
    private ShellPage? _shell;
    private IDisposable? _activation;

    public App(StartupOptions options)
    {
        _options = options;
        InitializeComponent();

        // Program hooks AppDomain.UnhandledException, which does not see this: an exception thrown on the
        // UI thread — a template that fails to realise, a converter that throws — is raised here by XAML
        // and then fail-fasts the process, bypassing the AppDomain event entirely. Without this handler
        // such a crash left no trace at all: no log line, no report, just an exit code. That is how an
        // `InfoBadge Value="当前"` (an int property given a string, which the XAML compiler accepts and
        // the row realiser does not) sat in the servers page unnoticed.
        //
        // Handled stays false on purpose. Swallowing it would leave the UI in a state nobody designed,
        // and the point here is to say what happened, not to carry on regardless.
        UnhandledException += OnXamlException;
    }

    internal static App? Instance { get; private set; }

    internal HostWindow? Window => _window;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Instance = this;

        try
        {
            var services = ShellServices.Build(_options.Paths);
            _container = services;

            var ui = services.GetRequiredService<ISettingsService>().Settings.Ui;

            // 主题在任何一个控件出现之前先落地。晚一步就会看见「先绿一下再变成紫」那种闪色，因为
            // Palette.xaml 里的字面值先画了第一帧。这里只改画刷的颜色，不动 Application.RequestedTheme
            // —— 那个属性只在内容加载前可写，而浅色主题靠的是各元素树的 ElementTheme（见 ThemeHost）。
            //
            // --theme 只压住这一次运行看到的颜色，故意不写回 ui.Theme：关窗时 OnWindowClosed 会把整份设置
            // 存盘，赋一次值就等于替用户改了主题。
            var theme = UiThemes.Resolve(_options.Theme ?? ui.Theme);
            ThemeHost.Apply(theme);

            if (_options.Theme is { Length: > 0 } asked)
                Log.Info(Category, string.Equals(theme.Id, asked, StringComparison.OrdinalIgnoreCase)
                    ? $"--theme：这一次用「{theme.Name}」（{theme.Id}），设置文件里仍是「{ui.Theme}」"
                    : $"--theme：认不出「{asked}」，回落到「{theme.Name}」（{theme.Id}）");

            // The one piece of startup housekeeping, started and not awaited: pruning the image cache is a
            // walk of a directory tree, and nothing on screen is waiting for it.
            services.GetRequiredService<EmbyImageStore>().PruneInBackground();

            var shell = services.GetRequiredService<ShellPage>();
            shell.Attach(services);
            _shell = shell;

            _window = new HostWindow { Content = shell };
            _window.Closed += OnWindowClosed;

            // 锁定窗口比例大小, before the window is on screen: the first drag of an edge asks for this value,
            // and a window that opened unlocked and got locked on its first WM_SIZING would jump.
            _window.BrowseAspect = ui.LockWindowShape ? HomeCarousel.WindowAspect : 0;
            _window.Show(_options.StartMaximized, _options.Screen);

            // And once it is: the default client area is already the locked shape exactly — a 16:9 browsing
            // area plus the rail the ratio does not count — so this normally changes nothing. It is here for
            // the monitor whose DPI rounding leaves the centred window a pixel off, for the screen too small
            // to hold that window at all, and it bows out on its own when started --maximized.
            _window.FitToShape();

            // The entire video contract, closed here: the host window hands out a child HWND and
            // LibMpvBackend gives it to mpv as `wid`. Set after Show, because there is no client area to
            // put a surface in until the window exists — and the backend only ever calls it from inside a
            // playback, which cannot happen before the user has navigated somewhere.
            services.GetRequiredService<PlaybackBackendFactory>().EmbeddedWindow = _window.VideoHandle;
            shell.AttachWindow(_window);

            _activation = ActivationListener.Start(
                _options.ActivationEventName,
                DispatcherQueue.GetForCurrentThread(),
                () => _window?.Activate());

            if (_options.SelfCheck) ShellSelfCheck.ScheduleFor(_window, shell, services, _options);

            // After the window is up, not before: restoring a saved token is a round trip to the
            // server, and there is no reason for the user to watch a blank screen during it.
            _ = StartAsync(shell);
        }
        catch (Exception error)
        {
            Log.Error(Category, "创建主窗口失败", error);
            Exit();
        }
    }

    private async Task StartAsync(ShellPage shell)
    {
        await shell.StartAsync().ConfigureAwait(true);

        // Tooling: --show-settings presses 设置, which is a second window and therefore the one part of the
        // UI a screenshot cannot reach by waiting. Defaults to 界面 rather than the card the button opens on,
        // because what a shot of this window is usually wanted for is the theme swatches; another card is
        // 「--show-settings 关于」. First of the four, because it takes activation: a flyout left open by
        // --show-library would light-dismiss the moment it appeared.
        if (_options.ShowSettings) shell.ShowSettings(_options.SettingsCategory ?? "界面");

        // Tooling: --show-library leaves the app on a library with its sort menu open, which is the
        // one state a screenshot has to be taken from and cannot be reached by waiting.
        if (_options.ShowLibrary) await shell.ShowLibrarySortAsync().ConfigureAwait(true);

        // Tooling: --show-detail clicks a poster and stays on whatever that click opened, which is how a
        // screenshot can be compared against Emby Theater's own detail page. --show-episode clicks one
        // card further, onto the page of a file, which is the only page 媒体信息 appears on. --scroll-end
        // then pulls that page to the bottom, where the body's glass panels and the picture behind them are;
        // --scroll-half stops halfway, which is the only place the title strip's wash is visibly a gradient.
        if (_options.ShowDetail || _options.ShowEpisode)
            await shell.ShowDetailAsync(_options.ShowEpisode, _options.ScrollEnd, _options.ScrollHalf)
                .ConfigureAwait(true);

        // Tooling: --play puts real video on screen. Last, because it does not come back until playback
        // has ended, and because it collapses everything --show-library was for.
        if (_options.PlayFirst) await shell.PlayFirstAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Records a XAML-thread crash on its way past. <c>e.Handled</c> is left alone, so the process still
    /// ends exactly as it did before — the only difference is that now there is a log line, and a
    /// self-check report that says 失败 instead of the previous run's stale 全部通过.
    /// </summary>
    private void OnXamlException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log.Error(Category, $"界面线程未处理异常：{e.Message}", e.Exception);
        if (_options.SelfCheck) ShellSelfCheck.ReportCrash(_options, e.Exception, e.Message);
    }

    private void OnWindowClosed()
    {        _activation?.Dispose();
        _activation = null;

        // Before the container: the player holds a cancellation source tied to the session, and a poll still
        // waiting on a half-second delay would otherwise come back to a session that had been disposed.
        _shell?.Shutdown();
        _shell = null;

        // The settings document is edited in place by the settings page and written on the way out, which is
        // what makes an edit survive an exit. Done here rather than by anything in the container: no
        // registration writes it on dispose, and this is the one place that knows the app is going away.
        _container?.GetService<ISettingsService>()?.Save();

        // Closes the session's sockets. The session is the only registration that implements IDisposable —
        // everything else holds nothing the OS will not clean up on its own.
        _container?.Dispose();
        _container = null;

        Log.Info(Category, "主窗口已关闭");
        Exit();
    }
}

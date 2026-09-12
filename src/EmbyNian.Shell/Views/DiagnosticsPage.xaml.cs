using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Infrastructure;
using EmbyNian.Playback;
using EmbyNian.Services;
using EmbyNian.Shell.Platform;
using EmbyNian.Shell.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace EmbyNian.Shell.Views;

/// <summary>导航到诊断页时传入的容器和外壳。</summary>
internal sealed record DiagnosticsRequest(IServiceProvider Services);

/// <summary>
/// 显示当前会话、播放器配置和内存日志的诊断页。页面只读，不会修改设置或播放状态。
/// <para>
/// Everything that decides what this page says now lives in <see cref="DiagnosticsViewModel"/>. What is
/// left here is what only a page can do: read the navigation parameter, hand the view model the log sink
/// it cannot reach on its own, and let go of the log subscription on the way out.
/// </para>
/// </summary>
public sealed partial class DiagnosticsPage : Page, IShellContent
{
    private const string Category = "诊断";

    private object? _request;

    public DiagnosticsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => HomeMotion.Reveal(PageLayout);
        Unloaded += (_, _) => HomeMotion.Stop(PageLayout);
    }

    /// <summary>The settings pane can be much narrower than its window; respond to its actual width.</summary>
    private void OnLayoutSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wide = e.NewSize.Width >= 820;
        DiagnosticsSideColumn.Width = new GridLength(wide ? 300 : 0);
        Grid.SetColumnSpan(StatusCards, wide ? 1 : 2);
        Grid.SetRowSpan(StatusCards, wide ? 2 : 1);
        StatusCards.MaxHeight = wide ? double.PositiveInfinity : Math.Clamp(e.NewSize.Height * 0.3, 120, 240);
        Grid.SetColumn(LogPanel, wide ? 1 : 0);
        Grid.SetRow(LogPanel, wide ? 0 : 1);
        Grid.SetColumnSpan(LogPanel, wide ? 1 : 2);
        Grid.SetRowSpan(LogPanel, wide ? 2 : 1);
    }

    internal DiagnosticsViewModel ViewModel { get; } = new();

    internal bool IsReady => ViewModel.IsReady;

    /// <summary>
    /// What the self-check reports. Row count rather than the rows themselves, because the point is that
    /// the log view was populated at all.
    /// </summary>
    internal (int Rows, int Categories, bool Following, string Launch) Summary => (
        ViewModel.Rows.Count,
        ViewModel.Categories.Count,
        ViewModel.Following,
        ViewModel.LaunchOptions is { Length: > 0 } ? ViewModel.LaunchHeading ?? "有参数" : "尚未播放");

    /// <summary>
    /// How many log rows the list really built, counted off the visual tree rather than off
    /// <see cref="DiagnosticsViewModel.Rows"/>.
    /// <para>
    /// The same distinction <see cref="ServersPage.Realised"/> exists for, and for the same reason: a
    /// <c>DataTemplate</c> is markup no build runs, so a resource key that does not resolve or a literal
    /// of the wrong type inside it throws when a row is realised and not before. Unlike the servers page
    /// this is not expected to equal the bound count — a <c>ListView</c> virtualises, so several hundred
    /// entries realise about a screenful — which is why the self-check asks for 「more than none」 rather
    /// than for a match.
    /// </para>
    /// </summary>
    internal int Realised
    {
        get
        {
            var rows = 0;
            Count(this, ref rows);
            return rows;
        }
    }

    public object? NavigationRequest => _request;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // The old page also accepted a bare container handle. Nothing ever sent one — ShellPage.Open is the
        // only way in and it always builds a DiagnosticsRequest — so that branch is gone rather than kept as
        // a second shape to maintain.
        if (e.Parameter is not DiagnosticsRequest request)
        {
            Log.Warn(Category, "导航参数缺失");
            return;
        }

        _request = request;
        Tag = "diagnostics";

        // The one place this page resolves anything. Deliberately a single block: it is the line that goes
        // when the shell stops handing a container around.
        var services = request.Services;

        // Program owns the ring buffer, and the view model has no way to reach a static on the shell's
        // entry point without knowing about it; handing it over here keeps that knowledge in the view.
        // Named arguments past the first few: ten positional GetRequiredService calls read as a wall.
        ViewModel.Attach(
            services.GetRequiredService<IShellActions>(),
            services.GetRequiredService<ISettingsService>(),
            services.GetRequiredService<EmbySession>(),
            services.GetRequiredService<IServerCapabilities>(),
            services.GetRequiredService<PlaybackService>(),
            services.GetRequiredService<AppPaths>(),
            ui: services.GetRequiredService<IUiDispatcher>(),
            clipboard: services.GetRequiredService<IClipboard>(),
            launcher: services.GetRequiredService<ISystemLauncher>(),
            sink: Program.LogBuffer);

        _ = ViewModel.ReloadAsync();
    }

    public void Release() => ViewModel.Cancel();

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Release();
        base.OnNavigatedFrom(e);
    }

    /// <summary>
    /// Counts realised item containers. <c>Content</c> rather than <c>DataContext</c>, for the reason
    /// spelled out on <see cref="ServersPage"/>'s own counter: everything inside a realised row inherits
    /// that row as its data context, so counting by data context counts the controls in a row instead.
    /// </summary>
    private static void Count(DependencyObject node, ref int rows)
    {
        if (node is ContentPresenter { Content: LogRow }) rows++;

        var children = VisualTreeHelper.GetChildrenCount(node);
        for (var index = 0; index < children; index++)
            Count(VisualTreeHelper.GetChild(node, index), ref rows);
    }
}

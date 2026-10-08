using Momoka.Emby;
using Momoka.Shell.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Momoka.Shell.Views;

internal sealed record ServerDashboardRequest(IServiceProvider Services, Action OpenWebConsole);

public sealed partial class ServerDashboardPage : Page, IShellContent
{
    private ServerDashboardRequest? _request;
    private readonly DispatcherQueueTimer _refresh;
    internal ServerDashboardViewModel ViewModel { get; } = new();
    public object? NavigationRequest => _request;

    public ServerDashboardPage()
    {
        InitializeComponent();
        ViewModel.UseConfirm(ConfirmServerActionAsync);
        ViewModel.EditServerName = EditServerNameAsync;
        _refresh = DispatcherQueue.CreateTimer();
        _refresh.Interval = TimeSpan.FromSeconds(15);
        _refresh.Tick += async (_, _) =>
        {
            if (ViewModel.CanAutoRefresh) await ViewModel.ReloadAsync();
        };
        Loaded += (_, _) =>
        {
            HomeMotion.Reveal(PageLayout);
            _refresh.Start();
        };
        Unloaded += (_, _) => Release();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not ServerDashboardRequest request) return;
        _request = request;
        // 页面唯一的服务解析点；网络与读取状态由视图模型处理。
        ViewModel.Attach(request.Services.GetRequiredService<EmbySession>());
        _ = ViewModel.ReloadAsync();
    }

    private void OnOpenWebConsole(object sender, RoutedEventArgs e) => _request?.OpenWebConsole();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wide = e.NewSize.Width >= 720;
        DashboardColumns.ColumnDefinitions[1].Width = new GridLength(wide ? 1 : 0, wide ? GridUnitType.Star : GridUnitType.Pixel);
        DashboardColumns.ColumnSpacing = wide ? 16 : 0;
        Grid.SetColumn(ActivityCard, wide ? 1 : 0);
        Grid.SetRow(ActivityCard, wide ? 0 : 1);
    }

    public void Release()
    {
        _refresh.Stop();
        HomeMotion.Stop(PageLayout);
        ViewModel.Cancel();
        _dialog?.Hide();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Release();
        base.OnNavigatedFrom(e);
    }
}

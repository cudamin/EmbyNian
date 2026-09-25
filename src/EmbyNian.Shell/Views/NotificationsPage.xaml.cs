using EmbyNian.Diagnostics;
using EmbyNian.Shell.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace EmbyNian.Shell.Views;

/// <summary>What <see cref="NotificationsPage"/> needs; see <see cref="LibraryRequest"/> for why it is passed.</summary>
internal sealed record NotificationsRequest(IServiceProvider Services);

/// <summary>
/// 设置 → 通知：Emby 服务器上当前用户的通知条目，本页是官方网页端通知设置的应用内复刻 ——
/// 列表、添加、编辑、删除、测试，全部经 <see cref="NotificationsViewModel"/> 走服务器，本地一个字节不存。
/// <para>
/// 本页留在 code-behind 的只有三件事：导航参数进 <c>Attach</c>、列表点击进视图模型、以及把编辑器对话框
/// 和服务挑选对话框接给视图模型 —— 那两样需要这里的 <c>XamlRoot</c>。
/// </para>
/// </summary>
public sealed partial class NotificationsPage : Page, IShellContent
{
    private const string Category = "通知";

    private NotificationsRequest? _request;

    public NotificationsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => HomeMotion.Reveal(PageLayout);
        Unloaded += (_, _) => HomeMotion.Stop(PageLayout);

        ViewModel.UseConfirm(ConfirmDialog.For(this));
        ViewModel.ShowEditor = ShowEditorAsync;
        ViewModel.PickService = PickServiceAsync;
    }

    internal NotificationsViewModel ViewModel { get; } = new();

    /// <summary>True once the first load has finished; read by the self-check.</summary>
    internal bool IsReady => ViewModel.IsReady;

    /// <summary>多少条通知列表建了行，以及树上一共画出了几行 —— 两个数对不上就是模板出的事。</summary>
    internal (int Bound, int Drawn) Realised
    {
        get
        {
            var drawn = 0;
            Count(this, ref drawn);
            return (ViewModel.Entries.Count, drawn);
        }
    }

    public object? NavigationRequest => _request;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not NotificationsRequest request)
        {
            Log.Warn(Category, "导航参数缺失");
            return;
        }

        _request = request;
        Tag = "notifications";

        var services = request.Services;
        ViewModel.Attach(services.GetRequiredService<EmbyNian.Emby.EmbySession>());
        _ = ViewModel.ReloadAsync();
    }

    public void Release() => ViewModel.Cancel();

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Release();
        base.OnNavigatedFrom(e);
    }

    private void OnEntryPicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is NotificationRow row) ViewModel.EditCommand.Execute(row);
    }

    /// <summary>编辑器对话框。XamlRoot 在这里读 —— 构造时页面还没有树，早读是空的。</summary>
    private async Task ShowEditorAsync(NotificationEditorContext context)
    {
        if (XamlRoot is not { } root) return;

        var dialog = new NotificationEditDialog(context)
        {
            XamlRoot = root,
            RequestedTheme = ThemeHost.Current.IsDark ? ElementTheme.Dark : ElementTheme.Light
        };

        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception error)
        {
            // 一次只许一个对话框；撞上另一个就当作用户收了手，编辑器里没保存的东西本来就不该落库。
            Log.Warn(Category, "无法显示通知编辑器", error);
        }
    }

    /// <summary>多于一个通知服务时挑一个。一个就径直用它 —— 这台服务器上只有 Webhooks 一种，大多数人也一样。</summary>
    private async Task<EmbyNian.Emby.NotificationServiceInfo?> PickServiceAsync(
        List<EmbyNian.Emby.NotificationServiceInfo> services)
    {
        if (XamlRoot is not { } root) return null;

        var dialog = new NotificationServicePicker(services)
        {
            XamlRoot = root,
            RequestedTheme = ThemeHost.Current.IsDark ? ElementTheme.Dark : ElementTheme.Light
        };

        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception error)
        {
            Log.Warn(Category, "无法显示通知服务挑选框", error);
            return null;
        }

        return dialog.Picked;
    }

    /// <summary>Counts realised entry containers, off the tree rather than off the collection — the same
    /// reasoning as <see cref="ServersPage.Realised"/>.</summary>
    private static void Count(DependencyObject node, ref int drawn)
    {
        if (node is ContentPresenter presenter && presenter.Content is NotificationRow) drawn++;

        var children = VisualTreeHelper.GetChildrenCount(node);
        for (var index = 0; index < children; index++)
            Count(VisualTreeHelper.GetChild(node, index), ref drawn);
    }
}

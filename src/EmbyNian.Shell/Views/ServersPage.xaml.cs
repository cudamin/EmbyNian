using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Services;
using EmbyNian.Shell.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace EmbyNian.Shell.Views;

/// <summary>What <see cref="ServersPage"/> needs; see <see cref="LibraryRequest"/> for why it is passed.</summary>
internal sealed record ServerRequest(IServiceProvider Services);

/// <summary>
/// Management view for saved Emby servers and their accounts.
/// <para>
/// What is left here is the three things XAML cannot do for itself: a navigation parameter into an
/// <c>Attach</c>, a list click into a view-model call, and the <see cref="ConfirmDialog"/> the delete
/// commands ask through — which needs this page's <c>XamlRoot</c>, so this is the only place that can hand
/// one out. The nine <c>Click</c> handlers that used to pattern-match a <c>Tag</c> back into a row are
/// commands on <see cref="ServersViewModel"/> now.
/// </para>
/// </summary>
public sealed partial class ServersPage : Page, IShellContent
{
    private const string Category = "服务器";

    private ServerRequest? _request;

    public ServersPage()
    {
        InitializeComponent();
        Loaded += (_, _) => HomeMotion.Reveal(PageLayout);
        Unloaded += (_, _) => HomeMotion.Stop(PageLayout);

        // Wired here rather than on navigation because the dialog captures nothing but this page: the
        // XamlRoot to host it in is read when a question is asked, which is long after it is null.
        ViewModel.UseConfirm(ConfirmDialog.For(this));
    }

    /// <inheritdoc cref="HomePage.ViewModel" />
    internal ServersViewModel ViewModel { get; } = new();

    /// <summary>True once the rows are built and the cache has been counted; read by the self-check.</summary>
    internal bool IsReady => ViewModel.IsReady;

    /// <summary>How many saved servers the page drew, and the cache line beneath them.</summary>
    internal (int Servers, int Accounts, string Cache) Summary => (
        ViewModel.Servers.Count,
        ViewModel.Servers.Sum(server => server.Accounts.Count),
        ViewModel.CacheSummary ?? "未读取");

    /// <summary>
    /// How many server and account cards the lists actually built, counted off the visual tree rather
    /// than off the bound collections.
    /// <para>
    /// That difference is the whole point of the probe. A <c>DataTemplate</c> is markup the build never
    /// runs, so a resource key that does not resolve or a property that is not there only fails when a
    /// row is realised — which, before this, first happened in front of a user. It is how the 「当前」
    /// badge on this page stayed broken: an <c>InfoBadge</c> whose <c>Value</c> is an <c>int</c>, handed
    /// the string 「当前」, compiled and shipped.
    /// </para>
    /// </summary>
    internal (int Servers, int Accounts) Realised
    {
        get
        {
            var servers = 0;
            var accounts = 0;
            Count(this, ref servers, ref accounts);
            return (servers, accounts);
        }
    }

    /// <inheritdoc />
    public object? NavigationRequest => _request;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not ServerRequest request)
        {
            Log.Warn(Category, "导航参数缺失");
            return;
        }

        _request = request;
        Tag = "servers";

        // The one place this page resolves anything. Deliberately a single block: it is the line that goes
        // when the shell stops handing a container around.
        var services = request.Services;

        ViewModel.Attach(
            services.GetRequiredService<IShellActions>(),
            services.GetRequiredService<ISettingsService>(),
            services.GetRequiredService<EmbySession>(),
            services.GetRequiredService<EmbyImageStore>());

        _ = ViewModel.ReloadAsync();
    }

    public void Release() => ViewModel.Cancel();

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Release();
        base.OnNavigatedFrom(e);
    }

    /// <summary>
    /// 需求 1：picking one of the servers the network scan found. Not a command, because the item in that
    /// list is a Core record with no way back to the view model; see <see cref="ServersViewModel.OpenDiscovered"/>.
    /// </summary>
    private void OnDiscoveredPicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is EmbyDiscoveredServer discovered) ViewModel.OpenDiscovered(discovered);
    }

    /// <summary>
    /// Counts item containers. <c>Content</c> rather than <c>DataContext</c>: everything inside a
    /// realised row inherits the row as its data context, including the <c>ContentPresenter</c> in every
    /// button's own template, so a count by data context reports the number of controls in a card.
    /// </summary>
    private static void Count(DependencyObject node, ref int servers, ref int accounts)
    {
        if (node is ContentPresenter presenter)
        {
            if (presenter.Content is ServerRow) servers++;
            else if (presenter.Content is AccountRow) accounts++;
        }

        var children = VisualTreeHelper.GetChildrenCount(node);
        for (var index = 0; index < children; index++)
            Count(VisualTreeHelper.GetChild(node, index), ref servers, ref accounts);
    }
}

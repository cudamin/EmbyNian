using EmbyNian.MoviePilot;
using EmbyNian.Shell.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 搜索页里 MoviePilot 那一段的视图。全部内容都是 <see cref="MoviePilotSearchViewModel"/> 的；这里留的只有
/// 视图才做得了的几件事：把连接服务和确认对话框接进视图模型、把页面共享的搜索框提交转成一次搜索、以及点某条
/// 结果的「搜索资源」时把资源覆盖面板盖上来（那要 <c>XamlRoot</c>，只有在树里的控件给得出）。
/// <para>
/// 和别的页一样自建视图模型、构造时就建好（<c>x:Bind</c> 不许有 null 根，见 <see cref="FilterPanel"/>）；确认
/// 对话框走 <see cref="ConfirmDialog.For"/>，和其它页问「不可撤销的事」是同一条路。
/// </para>
/// </summary>
public sealed partial class MoviePilotSearchPanel : UserControl
{
    private MoviePilotService? _service;

    public MoviePilotSearchPanel()
    {
        InitializeComponent();
    }

    internal MoviePilotSearchViewModel ViewModel { get; } = new();

    /// <summary>
    /// 由 <see cref="LibraryPage"/> 在它那唯一一处解析块里调一次：把连接服务交给视图模型、把「订阅前二次确认」
    /// 接到本控件的 <c>XamlRoot</c> 上，并把「搜索资源」的入口交给视图模型（它只把媒体递回来，开面板归这里）。
    /// </summary>
    internal void Attach(MoviePilotService service)
    {
        _service = service;
        ViewModel.Attach(service);
        ViewModel.UseConfirm(ConfirmDialog.For(this));
        ViewModel.UseResourceOpener(ShowResourcesAsync);
    }

    /// <summary>页面上那个共享搜索框提交、且此刻在 MoviePilot 段时，由页面把词转到这里。</summary>
    internal Task SearchAsync(string term) => ViewModel.SearchAsync(term);

    /// <summary>离开搜索页时收手：取消在飞的搜索，收起资源面板。</summary>
    internal void Release()
    {
        ViewModel.Cancel();
        ResourceOverlay.Release();
        ResourceOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>点某条结果的「搜索资源」：把资源覆盖面板盖上来并开搜。</summary>
    private Task ShowResourcesAsync(MoviePilotMedia media)
    {
        if (_service is null) return Task.CompletedTask;

        ResourceOverlay.Open(_service, media);
        ResourceOverlay.Visibility = Visibility.Visible;
        return Task.CompletedTask;
    }

    /// <summary>资源面板的返回键：收起它，回到片子列表。</summary>
    private void OnResourceClosed(object? sender, EventArgs e)
    {
        ResourceOverlay.Visibility = Visibility.Collapsed;
        ResourceOverlay.Release();
    }
}

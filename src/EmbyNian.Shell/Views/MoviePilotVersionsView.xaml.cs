using EmbyNian.MoviePilot;
using EmbyNian.Shell.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 「在 MoviePilot 搜索其他版本」独立窗口里装的内容。全部内容在 <see cref="MoviePilotVersionsViewModel"/>；这里
/// 留的是视图才做得了的几件事：把连接服务和确认对话框接进视图模型、把搜索框的提交转成一次搜索、以及进来时按
/// 算好的关键字填框并立刻开搜。
/// <para>
/// 和别的页一样自建视图模型、构造时就建好（<c>x:Bind</c> 不许有 null 根）；确认对话框走
/// <see cref="ConfirmDialog.For"/>，和其它页问「不可撤销的事」是同一条路。
/// </para>
/// </summary>
public sealed partial class MoviePilotVersionsView : UserControl
{
    public MoviePilotVersionsView()
    {
        InitializeComponent();
    }

    internal MoviePilotVersionsViewModel ViewModel { get; } = new();

    /// <summary>
    /// 由 <see cref="MoviePilotWindow"/> 每次弹出前调一次：接上连接服务与确认对话框，把算好的关键字填进搜索框
    /// 并立刻搜。填进框里而不是只搜一次，是为了让用户看得见发的是什么词、也好在原地改词放宽范围。
    /// </summary>
    internal void Search(MoviePilotService service, string keyword)
    {
        ViewModel.Attach(service);
        ViewModel.UseConfirm(ConfirmDialog.For(this));
        SearchInput.Text = keyword;
        _ = ViewModel.SearchAsync(keyword);
    }

    /// <summary>窗口收起时收手：取消在飞的搜索。</summary>
    internal void Release() => ViewModel.Cancel();

    private void OnSearch(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) =>
        _ = ViewModel.SearchAsync(args.QueryText ?? string.Empty);
}

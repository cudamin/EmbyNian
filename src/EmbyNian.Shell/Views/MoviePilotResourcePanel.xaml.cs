using EmbyNian.MoviePilot;
using EmbyNian.Shell.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 一部片的资源（种子）列表，作为 <see cref="MoviePilotSearchPanel"/> 之上的一层覆盖面板。内容全在
/// <see cref="MoviePilotResourceViewModel"/>；这里留的是：把服务和媒体交给视图模型并开搜、把下载确认接到本控件的
/// <c>XamlRoot</c>、以及那颗返回键。
/// <para>
/// 是覆盖面板而不是 <see cref="Windows.UI.Popups"/> 或 ContentDialog：下载要弹 <see cref="ConfirmDialog"/>（一个
/// ContentDialog），而 WinUI 同时只让一个 ContentDialog 在场，列表若也是对话框就打架。做成面板，确认框就能照弹。
/// </para>
/// </summary>
public sealed partial class MoviePilotResourcePanel : UserControl
{
    public MoviePilotResourcePanel()
    {
        InitializeComponent();
    }

    /// <summary>返回键按下：由 <see cref="MoviePilotSearchPanel"/> 收起这层覆盖面板。</summary>
    public event EventHandler? Closed;

    internal MoviePilotResourceViewModel ViewModel { get; } = new();

    /// <summary>接上服务与要搜的片，接好确认对话框，立刻开搜。</summary>
    internal void Open(MoviePilotService service, MoviePilotMedia media)
    {
        ViewModel.Attach(service, media);
        ViewModel.UseConfirm(ConfirmDialog.For(this));
        _ = ViewModel.SearchAsync();
    }

    /// <summary>收起时收手：取消在飞的搜索。</summary>
    internal void Release() => ViewModel.Cancel();

    private void OnClose(object sender, RoutedEventArgs e) => Closed?.Invoke(this, EventArgs.Empty);
}

using EmbyNian.Emby;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 「添加通知」的服务挑选框 —— 服务器上装了不止一种通知服务时才弹（官方网页端每次都弹，这里一种服务时径直
/// 用它，少一跳）。纯 code 构造：一行一个服务名，点中即走。
/// </summary>
internal sealed class NotificationServicePicker : ContentDialog
{
    /// <summary>用户点中的那个服务；关掉框没点就是 null。</summary>
    public NotificationServiceInfo? Picked { get; private set; }

    public NotificationServicePicker(List<NotificationServiceInfo> services)
    {
        Title = "添加通知";
        CloseButtonText = "取消";

        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.None,
            IsItemClickEnabled = true,
            Margin = new Thickness(0, 4, 0, 0)
        };
        foreach (var service in services)
        {
            list.Items.Add(new ListViewItem { Content = service.Name, Tag = service });
        }
        list.ItemClick += (_, args) =>
        {
            Picked = (NotificationServiceInfo)((ListViewItem)args.ClickedItem).Tag;
            Hide();
        };

        Content = list;
    }
}

using EmbyNian.Emby;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 「添加到合集」那张表。它只回答一件事：<b>放进哪个合集</b> —— 一个已有的（<see cref="Chosen"/>），还是一个
/// 新起名字的（<see cref="NewName"/>）。请求由 <see cref="ItemCommands"/> 发，这里一趟都不发。
/// </summary>
public sealed partial class CollectionDialog : ContentDialog
{
    /// <summary>两处互相清空的时候不要再触发对方的处理，见 <see cref="OnPicked"/>。</summary>
    private bool _syncing;

    public CollectionDialog(EmbyItem item, IReadOnlyList<EmbyItem> collections)
    {
        InitializeComponent();

        // 对话框摆在 XamlRoot 的浮层根上，从提出它的那棵树上继承不到主题 —— 不点明的话，它会按 Windows 的
        // 应用模式来（见 ConfirmDialog 里那段说明）。
        RequestedTheme = ThemeHost.Current.IsDark ? ElementTheme.Dark : ElementTheme.Light;

        // 标题说做什么，这一句说对谁做：菜单在这张表弹出来的时候已经没了，而一张列着别人合集的表很容易被
        // 当成是对的那一张。
        Title = $"添加到合集 — {item.Name}";

        List.ItemsSource = collections;
        List.Visibility = collections.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Empty.Visibility = collections.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

        Validate();
    }

    /// <summary>挑中的那个已有合集，没挑就是空。</summary>
    public EmbyItem? Chosen => List.SelectedItem as EmbyItem;

    /// <summary>要新建的合集名字，没打字就是空。</summary>
    public string? NewName => Chosen is null && NewBox.Text.Trim() is { Length: > 0 } name ? name : null;

    private void OnPicked(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;

        _syncing = true;
        if (List.SelectedItem is not null) NewBox.Text = "";
        _syncing = false;

        Validate();
    }

    private void OnTyped(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;

        _syncing = true;
        if (NewBox.Text.Trim().Length > 0) List.SelectedItem = null;
        _syncing = false;

        Validate();
    }

    /// <summary>
    /// 两处都空着的时候「确定」是灰的。按下去再报「你还没选」是把已经能拦住的事推给一句提示。
    /// </summary>
    private void Validate() => IsPrimaryButtonEnabled = Chosen is not null || NewName is not null;
}

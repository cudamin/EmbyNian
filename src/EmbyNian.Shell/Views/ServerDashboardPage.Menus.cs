using EmbyNian.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace EmbyNian.Shell.Views;

public sealed partial class ServerDashboardPage
{
    private ContentDialog? _dialog;

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (_dialog is not null || XamlRoot is null) return ContentDialogResult.None;
        _dialog = dialog;
        dialog.XamlRoot = XamlRoot;
        dialog.RequestedTheme = ThemeHost.Current.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        try { return await dialog.ShowAsync(); }
        catch (Exception error)
        {
            Log.Warn("控制台", "无法显示服务器对话框", error);
            return ContentDialogResult.None;
        }
        finally { _dialog = null; }
    }

    private async Task<bool> ConfirmServerActionAsync(string title, string message, string primary) =>
        await ShowDialogAsync(new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        }) == ContentDialogResult.Primary;

    private async Task<string?> EditServerNameAsync(string current)
    {
        var input = new TextBox { Header = "服务器的显示名称", Text = current };
        AutomationProperties.SetAutomationId(input, "ServerDisplayNameBox");
        AutomationProperties.SetName(input, "服务器的显示名称");
        var dialog = new ContentDialog
        {
            Title = "更改服务器的显示名称",
            Content = input,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false
        };
        input.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled =
            !string.IsNullOrWhiteSpace(input.Text) && input.Text.Trim() != current;
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary ? input.Text.Trim() : null;
    }

    private async void OnServerInformation(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SystemInfo is not { } info) return;
        var content = new StackPanel { Spacing = 12 };
        Add("显示名称", info.ServerName);
        Add("版本", info.Version);
        Add("操作系统", info.OperatingSystemDisplayName ?? info.OperatingSystem);
        Add("服务器安装包", info.PackageName);
        Add("服务器 ID", info.Id);
        Add("连接地址", ViewModel.Address);
        Add("局域网地址", info.LocalAddress);
        Add("远程访问地址", info.WanAddress);
        Add("程序数据目录", info.ProgramDataPath);
        Add("缓存目录", info.CachePath);
        Add("日志目录", info.LogPath);
        Add("元数据目录", info.InternalMetadataPath);
        Add("转码临时目录", info.TranscodingTempPath);
        await ShowDialogAsync(new ContentDialog
        {
            Title = "服务器信息",
            Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close
        });

        void Add(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var row = new StackPanel { Spacing = 4 };
            row.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.Resources["EgCaptionStyle"] });
            row.Children.Add(new TextBlock
            {
                Text = value,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Style = (Style)Application.Current.Resources["EgBodyStyle"]
            });
            content.Children.Add(row);
        }
    }
}

using System.Text.Json.Nodes;
using EmbyNian.Emby;
using EmbyNian.Shell.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace EmbyNian.Shell.Views;

internal sealed record ServerLibrariesRequest(IServiceProvider Services);

public sealed partial class ServerLibrariesPage : Page, IShellContent
{
    private ServerLibrariesRequest? _request;
    private ContentDialog? _dialog;
    internal ServerLibrariesViewModel ViewModel { get; } = new();
    public object? NavigationRequest => _request;

    public ServerLibrariesPage()
    {
        InitializeComponent();
        ViewModel.UseConfirm(ConfirmAsync);
        LibraryTabs.SelectedItem = LibraryListTab;
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ServerLibrariesViewModel.Editing)) LibraryEditorScroll.ScrollTo(0, 0, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled));
        };
        Loaded += (_, _) => HomeMotion.Reveal(PageLayout);
        Unloaded += (_, _) => Release();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not ServerLibrariesRequest request) return;
        _request = request;
        // 此页唯一的服务解析点。业务调用经视图模型和 EmbyClient。
        ViewModel.Attach(request.Services.GetRequiredService<EmbySession>());
        _ = ViewModel.ReloadAsync();
    }

    private async void OnTabChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string tab }) return;
        if (tab == "advanced") await ViewModel.OpenAdvancedAsync(); else ViewModel.Tab = "library";
    }

    private async void OnLibraryClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ManagedLibraryRow row) await ViewModel.OpenAsync(row);
    }

    private async void OnContentTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LibraryContentType.SelectedIndex != ViewModel.ContentTypeIndex)
            await ViewModel.ChangeContentTypeAsync(LibraryContentType.SelectedIndex);
        LibraryContentType.SelectedIndex = ViewModel.ContentTypeIndex;
    }

    private void OnLibraryGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (LibrariesGrid.ItemsPanelRoot is not ItemsWrapGrid panel) return;
        var width = Math.Max(220, LibrariesGrid.ActualWidth - 8);
        var columns = Math.Max(1, (int)(width / 280));
        panel.ItemWidth = Math.Floor(width / columns);
    }

    private void OnLibraryMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ManagedLibraryRow row } button || !ViewModel.CanUse) return;
        var menu = new MenuFlyout();
        void Add(string title, string id, Func<Task> action)
        {
            var item = new MenuFlyoutItem { Text = title };
            AutomationProperties.SetAutomationId(item, id);
            item.Click += async (_, _) => await action();
            menu.Items.Add(item);
        }
        Add("编辑媒体库", "LibraryMenuEdit", () => ViewModel.OpenAsync(row));
        Add("编辑图片", "LibraryMenuImages", () => ShowImagesAsync(row));
        Add("重命名", "LibraryMenuRename", async () => { await ViewModel.OpenAsync(row); LibraryNameBox.Focus(FocusState.Programmatic); LibraryNameBox.SelectAll(); });
        if (row.Folder.CanRefresh)
        {
            Add("刷新元数据", "LibraryMenuRefreshMetadata", () => ShowScanAsync(row));
            Add("扫描媒体库文件", "LibraryMenuScan", () => ViewModel.ScanAsync(row, "scan", false));
        }
        if (row.Folder.CanRemove)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            Add("移除媒体库", "LibraryMenuRemove", () => ViewModel.RemoveAsync(row));
        }
        menu.ShowAt(button);
    }

    private async Task ShowScanAsync(ManagedLibraryRow row)
    {
        if (!ViewModel.CanUse) return;
        var modes = new ComboBox { Header = "刷新模式", ItemsSource = new[] { "搜索缺失的元数据", "替换全部元数据" }, SelectedIndex = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
        var images = new CheckBox { Content = "替换现有图片" };
        var thumbnails = new CheckBox { Content = "替换现有视频预览缩略图" };
        AutomationProperties.SetAutomationId(modes, "LibraryScanMode"); AutomationProperties.SetAutomationId(images, "LibraryScanReplaceImages");
        AutomationProperties.SetAutomationId(thumbnails, "LibraryScanReplaceThumbnails");
        var panel = new StackPanel { Spacing = 16, MinWidth = 280 };
        panel.Children.Add(new TextBlock { Text = row.Name, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(modes); panel.Children.Add(images); panel.Children.Add(thumbnails);
        if (await ShowDialogAsync(CreateDialog("刷新元数据", panel, "刷新")) == ContentDialogResult.Primary)
            await ViewModel.ScanAsync(row, new[] { "missing", "all" }[modes.SelectedIndex], images.IsChecked == true, thumbnails.IsChecked == true);
    }

    private async void OnAddPath(object sender, RoutedEventArgs e) => await ShowDirectoryAsync(null, false);
    private async void OnEditPath(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: LibraryPathRow row }) await ShowDirectoryAsync(row, false);
    }
    private async void OnRemovePath(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: LibraryPathRow row }) await ViewModel.RemovePathAsync(row);
    }
    private async void OnMetadataDirectory(object sender, RoutedEventArgs e) => await ShowDirectoryAsync(null, true);

    private async Task ShowDirectoryAsync(LibraryPathRow? original, bool metadata)
    {
        if (!ViewModel.CanUse || _dialog is not null) return;
        var path = new TextBox { Header = "服务器文件夹路径", Text = original?.Path ?? "", IsReadOnly = original is not null && !ViewModel.IsNew, MinWidth = 280 };
        var network = new TextBox { Header = "共享网络路径（可选）", Text = original?.NetworkPath ?? "" };
        var username = new TextBox { Header = "网络访问用户名（可选）", Text = original is null ? "" : EmbyLibraryOptions.Text(original.Document, "Username") };
        var password = new PasswordBox { Header = "网络访问密码（可选）", Password = original is null ? "" : EmbyLibraryOptions.Text(original.Document, "Password") };
        var browse = new Button { Content = "浏览服务器目录", IsEnabled = !path.IsReadOnly };
        var up = new Button { Content = "上一级", IsEnabled = !path.IsReadOnly };
        var roots = new Button { Content = "根目录", IsEnabled = !path.IsReadOnly };
        var folders = new ListView { MaxHeight = 230, SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true, DisplayMemberPath = "Name", IsEnabled = !path.IsReadOnly };
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap };
        foreach (var pair in new (DependencyObject Control, string Id)[] { (path, "LibraryDirectoryPath"), (network, "LibraryDirectoryNetwork"), (username, "LibraryDirectoryUser"), (password, "LibraryDirectoryPassword"), (browse, "LibraryDirectoryBrowse"), (up, "LibraryDirectoryUp"), (roots, "LibraryDirectoryRoot"), (folders, "LibraryDirectoryList") })
            AutomationProperties.SetAutomationId(pair.Control, pair.Id);
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "选择运行 Emby Server 的设备上的文件夹。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(path);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        buttons.Children.Add(browse); buttons.Children.Add(up); buttons.Children.Add(roots);
        panel.Children.Add(buttons); panel.Children.Add(folders); panel.Children.Add(network);
        if (!metadata) { panel.Children.Add(username); panel.Children.Add(password); }
        panel.Children.Add(message);
        var dialog = CreateDialog(metadata ? "选择元数据目录" : original is null ? "添加媒体文件夹" : "编辑媒体文件夹", new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 650 }, "确定");
        async Task BrowseAsync()
        {
            dialog.IsPrimaryButtonEnabled = false; browse.IsEnabled = up.IsEnabled = roots.IsEnabled = folders.IsEnabled = false;
            var result = await ViewModel.BrowseAsync(path.Text, username.Text, password.Password);
            if (ReferenceEquals(_dialog, dialog))
            {
                if (result is not null) { folders.ItemsSource = result.OfType<JsonObject>().Select(item => new LibraryChoice(EmbyLibraryOptions.Text(item, "Path"), EmbyLibraryOptions.Text(item, "Name"))).ToArray(); message.Text = ""; }
                else message.Text = ViewModel.NoticeMessage ?? "目录读取失败。";
                dialog.IsPrimaryButtonEnabled = true; browse.IsEnabled = up.IsEnabled = roots.IsEnabled = folders.IsEnabled = !path.IsReadOnly;
            }
        }
        browse.Click += async (_, _) => await BrowseAsync();
        roots.Click += async (_, _) => { path.Text = ""; await BrowseAsync(); };
        up.Click += async (_, _) => { path.Text = await ViewModel.ParentDirectoryAsync(path.Text); await BrowseAsync(); };
        folders.ItemClick += async (_, args) => { if (args.ClickedItem is LibraryChoice choice) { path.Text = choice.Value; await BrowseAsync(); } };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            var deferral = args.GetDeferral();
            try
            {
                if (!ViewModel.CanUse) return;
                if (metadata)
                {
                    args.Cancel = !await ViewModel.SetMetadataDirectoryAsync(path.Text, network.Text);
                }
                else
                {
                    var value = original is null ? new JsonObject() : (JsonObject)original.Document.DeepClone();
                    value["Path"] = path.Text; value["NetworkPath"] = network.Text; value["Username"] = username.Text; value["Password"] = password.Password;
                    args.Cancel = !await ViewModel.SavePathAsync(value, original);
                }
                if (args.Cancel) message.Text = ViewModel.NoticeMessage ?? "尚未保存，请检查填写内容。";
            }
            finally { deferral.Complete(); }
        };
        await ShowDialogAsync(dialog);
        password.Password = "";
    }

    private async void OnLanguages(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LibraryFieldRow field } || !ViewModel.CanEditFields) return;
        var choices = field.Choices.ToList();
        var selected = field.SelectedLanguages;
        choices = choices.OrderBy(choice => { var index = Array.IndexOf(selected, choice.Value); return index < 0 ? int.MaxValue : index; }).ToList();
        var list = new ListView { ItemsSource = choices, DisplayMemberPath = "Name", SelectionMode = ListViewSelectionMode.Multiple, Height = 420, MinWidth = 300 };
        AutomationProperties.SetAutomationId(list, "LibraryDownloadLanguages");
        foreach (var choice in choices.Where(choice => selected.Contains(choice.Value))) list.SelectedItems.Add(choice);
        if (await ShowDialogAsync(CreateDialog(field.Label, list, "确定")) == ContentDialogResult.Primary && ViewModel.CanEditFields)
            field.SetLanguages(list.SelectedItems.OfType<LibraryChoice>().Select(choice => choice.Value));
    }
    private void OnProviderUp(object sender, RoutedEventArgs e) { if (ViewModel.CanEditFields && sender is Button { Tag: LibraryProviderRow row }) row.Group.Move(row, -1); }
    private void OnProviderDown(object sender, RoutedEventArgs e) { if (ViewModel.CanEditFields && sender is Button { Tag: LibraryProviderRow row }) row.Group.Move(row, 1); }

    private void OnProviderSetup(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LibraryProviderRow row } || _request is null || !ViewModel.CanEditFields) return;
        LibraryWebFrame.Navigate(typeof(DashboardPage), new DashboardRequest(_request.Services, ReturnToUsers: ReturnFromWeb, Route: row.SetupUrl, ReturnLabel: "返回媒体库"));
        LibraryWebFrame.BackStack.Clear();
        LibraryWebFrame.Visibility = Visibility.Visible;
        PageLayout.Visibility = Visibility.Collapsed;
    }

    private void ReturnFromWeb()
    {
        (LibraryWebFrame.Content as IShellContent)?.Release();
        LibraryWebFrame.Content = null;
        LibraryWebFrame.Visibility = Visibility.Collapsed;
        PageLayout.Visibility = Visibility.Visible;
    }

    private async Task ShowImagesAsync(ManagedLibraryRow row)
    {
        var context = await ViewModel.OpenArtworkAsync(row);
        if (context is null) return;
        var dialog = new CoverDialog(context.Item,
            (_, type) => context.FindAsync(type),
            async (_, type, tag, width, index) => await context.FetchAsync(type, tag, width, index),
            (_, type, index, chosen) => context.ApplyAsync(type, index, chosen),
            (_, type, index) => context.DeleteAsync(type, index),
            (_, type, picked) => context.UploadAsync(type, picked.Bytes, ArtworkFile.ContentType(picked.FileName)),
            async _ => await context.ReloadAsync(), async address => await context.FetchRemoteAsync(address))
        { XamlRoot = XamlRoot };
        await ShowDialogAsync(dialog);
        if (ViewModel.CanUse) await ViewModel.ReloadAsync();
    }

    private ContentDialog CreateDialog(string title, object content, string primary) => new()
    {
        XamlRoot = XamlRoot,
        Title = title,
        Content = content,
        PrimaryButtonText = primary,
        CloseButtonText = "取消",
        DefaultButton = ContentDialogButton.Close,
        RequestedTheme = ThemeHost.Current.IsDark ? ElementTheme.Dark : ElementTheme.Light
    };
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (_dialog is not null) return ContentDialogResult.None;
        _dialog = dialog;
        try { return await dialog.ShowAsync(); }
        finally { _dialog = null; }
    }
    private async Task<bool> ConfirmAsync(string title, string message, string primary) =>
        await ShowDialogAsync(CreateDialog(title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 480 }, primary)) == ContentDialogResult.Primary;

    public void Release()
    {
        ViewModel.Cancel(); _dialog?.Hide();
        (LibraryWebFrame.Content as IShellContent)?.Release();
        HomeMotion.Stop(PageLayout);
    }
    protected override void OnNavigatedFrom(NavigationEventArgs e) { Release(); base.OnNavigatedFrom(e); }
}

using EmbyNian.Emby;
using EmbyNian.Shell.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace EmbyNian.Shell.Views;

internal sealed record ServerUsersRequest(IServiceProvider Services, Action<string> OpenPreferences);

public sealed partial class ServerUsersPage : Page, IShellContent
{
    private ServerUsersRequest? _request;
    private ContentDialog? _dialog;
    internal ServerUsersViewModel ViewModel { get; } = new();
    public object? NavigationRequest => _request;

    public ServerUsersPage()
    {
        InitializeComponent();
        UserTabs.SelectedItem = UserProfileTab;
        ViewModel.UseConfirm(ConfirmAsync);
        ViewModel.PickAvatar = () => ArtworkFile.PickAsync(XamlRoot);
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ServerUsersViewModel.Tab))
            {
                UserTabs.SelectedItem = UserTabs.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(item => item.Tag as string == ViewModel.Tab);
                UserEditorScroll.ScrollTo(0, 0, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled));
            }
        };
        Loaded += (_, _) => HomeMotion.Reveal(PageLayout);
        Unloaded += (_, _) => Release();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not ServerUsersRequest request) return;
        _request = request;
        // 唯一服务解析点；用户操作和 HTTP 都在视图模型及 Core 中。
        ViewModel.Attach(request.Services.GetRequiredService<EmbySession>());
        _ = ViewModel.ReloadAsync();
    }

    private async void OnUserClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ManagedUserRow row) await ViewModel.OpenAsync(row);
    }

    private void OnUserMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ManagedUserRow row } anchor || !ViewModel.CanUse) return;
        var menu = new MenuFlyout();
        void Add(string label, string id, Func<Task> action)
        {
            var item = new MenuFlyoutItem { Text = label };
            AutomationProperties.SetAutomationId(item, id);
            item.Click += async (_, _) => await action();
            menu.Items.Add(item);
        }
        Add("编辑用户", "UserCardEdit", () => ViewModel.OpenAsync(row));
        Add("上传头像", "UserCardUpload", async () => { await ViewModel.OpenAsync(row); if (ViewModel.CurrentUserId == row.Id) await ViewModel.UploadAvatarCommand.ExecuteAsync(null); });
        Add("复制用户数据", "UserCardCopy", async () => { await ViewModel.OpenAsync(row); if (ViewModel.CurrentUserId == row.Id) await ShowCopyDialogAsync(); });
        Add("删除用户", "UserCardDelete", async () => { await ViewModel.OpenAsync(row); if (ViewModel.CurrentUserId == row.Id) await ViewModel.DeleteCommand.ExecuteAsync(null); });
        menu.ShowAt(anchor);
    }

    private void OnTabChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tab }) ViewModel.Tab = tab;
    }
    private void OnAddTag(object sender, RoutedEventArgs e) => ViewModel.AddTag();
    private void OnRemoveTag(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag }) ViewModel.Tags.Remove(tag);
    }
    private void OnRemoveSchedule(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: UserScheduleRow row }) ViewModel.Schedules.Remove(row);
    }

    private async void OnAddSchedule(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanSave) return;
        var days = Enum.GetNames<DayOfWeek>();
        var day = new ComboBox { Header = "星期", ItemsSource = days.Select(UserScheduleRow.Day).ToArray(), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var times = Enumerable.Range(0, 25).Select(hour => $"{hour:00}:00").ToArray();
        var start = new ComboBox { Header = "开始时间", ItemsSource = times, SelectedIndex = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        var end = new ComboBox { Header = "结束时间", ItemsSource = times, SelectedIndex = 22, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(day, "UserScheduleDay");
        AutomationProperties.SetAutomationId(start, "UserScheduleStart");
        AutomationProperties.SetAutomationId(end, "UserScheduleEnd");
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(day); panel.Children.Add(start); panel.Children.Add(end); panel.Children.Add(message);
        var dialog = CreateDialog("添加访问时段", panel, "添加");
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (start.SelectedIndex >= end.SelectedIndex) { args.Cancel = true; message.Text = "结束时间必须晚于开始时间。"; }
        };
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary && ViewModel.CanSave)
            ViewModel.Schedules.Add(new(EmbyUserPermissions.Schedule(days[day.SelectedIndex], start.SelectedIndex, end.SelectedIndex)));
    }

    private async void OnCopyData(object sender, RoutedEventArgs e) => await ShowCopyDialogAsync();

    private async Task ShowCopyDialogAsync()
    {
        if (!ViewModel.CanManageExisting) return;
        var candidates = ViewModel.CopyUsers.Where(user => user.Id.Length > 0).ToArray();
        var source = new ComboBox { Header = "从哪个用户复制", ItemsSource = candidates.Select(user => user.Name).ToArray(), SelectedIndex = candidates.Length > 0 ? 0 : -1, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(source, "CopyUserSource");
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(source);
        var choices = new List<(string Id, CheckBox Check)>();
        foreach (var option in ViewModel.CopyOptions)
        {
            var check = new CheckBox { Content = option.Name, IsChecked = option.Selected };
            AutomationProperties.SetAutomationId(check, "CopyUserData_" + option.Id);
            panel.Children.Add(check);
            choices.Add((option.Id, check));
        }
        var dialog = CreateDialog("复制用户数据", panel, "下一步");
        dialog.IsPrimaryButtonEnabled = candidates.Length > 0;
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary && ViewModel.CanManageExisting && source.SelectedIndex >= 0)
            await ViewModel.CopyDataAsync(candidates[source.SelectedIndex].Id, choices.Where(choice => choice.Check.IsChecked == true).Select(choice => choice.Id));
    }

    private void OnPreferences(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CanManageExisting) _request?.OpenPreferences(ViewModel.CurrentUserId);
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
        ViewModel.Cancel();
        _dialog?.Hide();
        HomeMotion.Stop(PageLayout);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Release();
        base.OnNavigatedFrom(e);
    }
}

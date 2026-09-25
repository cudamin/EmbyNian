using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Shell.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 一条通知的编辑器，官方网页端通知编辑器的复刻：名称、服务专属字段（Webhooks ＝ 地址＋请求类型，抄它自家
/// 编辑页 <c>webhookeditorjs</c> 的三个字段）、按类分节的事件矩阵（类全选框带三态联动，媒体库一节底下挂
/// 「按剧集/专辑分组」，同官方）、用户/媒体库/设备三个限定名单。保存与测试由 <see cref="NotificationEditorContext"/>
/// 交进来的委托走服务器，对话框自己不发请求。
/// <para>
/// 编辑动的是 <see cref="NotificationEditorContext.Entry"/> —— 视图模型开编辑器前换上的副本；服务器收下才
/// 重读列表，取消不落任何东西。
/// </para>
/// </summary>
public sealed partial class NotificationEditDialog : ContentDialog
{
    private readonly NotificationEditorContext _context;

    /// <summary>事件矩阵：类别全选框 → 它那排子事件。子勾选变化把类别拨回三态。</summary>
    private readonly List<(CheckBox Parent, List<CheckBox> Children)> _eventGroups = [];

    private readonly List<(CheckBox Box, string Id)> _userChecks = [];
    private readonly List<(CheckBox Box, string Id)> _libraryChecks = [];
    private readonly List<(CheckBox Box, string Id)> _deviceChecks = [];
    private CheckBox? _groupItems;

    /// <summary>code 建出来的文字用的两条样式。构造时从对话框自己的资源表里取好 —— 元素自己的 Resources
    /// 索引器不做树上查找，往子元素身上要会直接 KeyNotFound（真机上炸过一回，UIA 开编辑器抓到的）。</summary>
    private readonly Style _noteStyle;
    private readonly Style _faintStyle;

    private bool _saving;
    private bool _testing;

    public NotificationEditDialog(NotificationEditorContext context)
    {
        _context = context;
        InitializeComponent();

        Title = context.IsNew ? "添加通知" : "编辑通知";
        NameBox.Text = context.Entry.FriendlyName ?? "";

        var webhook = context.Entry.NotifierKey == NotificationServiceInfo.WebhooksKey;
        WebhookFields.Visibility = webhook ? Visibility.Visible : Visibility.Collapsed;
        OtherServiceNote.Visibility = webhook ? Visibility.Collapsed : Visibility.Visible;
        if (webhook)
        {
            UrlBox.Text = context.Entry.Options.TryGetValue("Url", out var url) ? url : "";
            RequestType.SelectedIndex = context.Entry.Options.TryGetValue("EnableMultipartFormData", out var multi)
                && string.Equals(multi, "true", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }

        PrimaryButtonClick += OnPrimary;
        _noteStyle = (Style)Resources["DlgNote"];
        _faintStyle = (Style)Resources["DlgFaint"];
        BuildEventList();
        BuildFilterLists();
    }

    /// <summary>
    /// 事件矩阵，官方编辑器的节拍：每个类别一节 —— 类别名的全选框（三态）＋它底下一排子事件；「媒体库」一节
    /// 额外挂「按剧集/专辑分组」（官方只在这一类下有这档，条目级选项、不属于某一类子事件）。
    /// </summary>
    private void BuildEventList()
    {
        var subscribed = _context.Entry.EventIds;

        foreach (var category in _context.Categories)
        {
            var children = category.Events
                .Select(@event => new CheckBox
                {
                    Content = @event.Name,
                    IsChecked = subscribed.Contains(@event.Id, StringComparer.Ordinal),
                    Margin = new Thickness(24, 0, 0, 0),
                    MinWidth = 0
                })
                .ToList();

            // 三态全选框：子事件里有勾的就是勾/半，一个没有就是空。官方是两态框（半勾画成全勾），
            // 这里拨三态是把「半订阅」画明白，行为不变。
            var parent = new CheckBox
            {
                Content = category.Name,
                IsThreeState = true,
                MinWidth = 0
            };
            RollUp(parent, children);

            parent.Click += (_, _) =>
            {
                // 点了三态框：空 → 全勾，全勾/半勾 → 全空（再点一次循环回全勾）。官方两态框的两条路都在这里。
                var check = parent.IsChecked != false;
                foreach (var child in children) child.IsChecked = check;
                parent.IsChecked = check;
            };
            foreach (var child in children)
                child.Click += (_, _) => RollUp(parent, children);

            var section = new StackPanel { Spacing = 6 };
            section.Children.Add(parent);
            foreach (var child in children) section.Children.Add(child);

            EventList.Children.Add(section);
            _eventGroups.Add((parent, children));

            if (category.Id == "library")
            {
                _groupItems = new CheckBox
                {
                    Content = "按剧集/专辑分组",
                    IsChecked = _context.Entry.GroupItems,
                    Margin = new Thickness(24, 0, 0, 0),
                    MinWidth = 0
                };
                section.Children.Add(_groupItems);
            }
        }
    }

    /// <summary>子事件全勾 → 全选框勾；一个没勾 → 空；中间态 → 半勾。</summary>
    private static void RollUp(CheckBox parent, List<CheckBox> children)
    {
        if (children.Count == 0) return;
        var checkedCount = children.Count(child => child.IsChecked == true);
        parent.IsChecked = checkedCount == 0 ? false : checkedCount == children.Count ? true : null;
    }

    /// <summary>用户/媒体库/设备三个限定名单。名单拿不到时那一节写明拿不到，不画空架子。</summary>
    private void BuildFilterLists()
    {
        BuildFilterSection(FilterList, "用户", _context.Users.Select(u => (u.Name, u.Id)), _context.Entry.UserIds, _userChecks);
        BuildFilterSection(FilterList, "媒体库", _context.Libraries.Select(l => (l.Name, l.PickId)), _context.Entry.LibraryIds, _libraryChecks);
        BuildFilterSection(FilterList, "设备", _context.Devices.Select(d => (d.Name, d.Id)), _context.Entry.DeviceIds, _deviceChecks);
    }

    private void BuildFilterSection(
        StackPanel host,
        string title,
        IEnumerable<(string Name, string Id)> items,
        List<string> selected,
        List<(CheckBox Box, string Id)> checks)
    {
        host.Children.Add(new TextBlock { Text = title, Style = _noteStyle });

        var boxes = items
            .Select(item => (Box: new CheckBox
            {
                Content = item.Name,
                IsChecked = selected.Contains(item.Id, StringComparer.Ordinal),
                Margin = new Thickness(12, 0, 0, 0),
                MinWidth = 0
            }, item.Id))
            .ToList();

        if (boxes.Count == 0)
        {
            host.Children.Add(new TextBlock
            {
                Text = "（名单没取到 —— 这一栏留空，不限定）",
                Style = _faintStyle,
                Margin = new Thickness(12, 0, 0, 0)
            });
            return;
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Thickness(12, 0, 0, 0) };
        foreach (var (box, id) in boxes)
        {
            row.Children.Add(box);
            checks.Add((box, id));
        }
        host.Children.Add(row);
    }

    /// <summary>表单 → 条目。动的是视图模型换上的那份副本。</summary>
    private UserNotificationInfo Build()
    {
        var entry = _context.Entry;
        entry.FriendlyName = NameBox.Text.Trim();

        if (entry.NotifierKey == NotificationServiceInfo.WebhooksKey)
        {
            entry.Options["Url"] = UrlBox.Text.Trim();
            entry.Options["EnableMultipartFormData"] = RequestType.SelectedIndex == 1 ? "true" : "false";
        }

        entry.EventIds =
        [
            .. _eventGroups
                .SelectMany(group => group.Children)
                .Where(box => box.IsChecked == true)
                .Select(box => (string)box.Tag!)
        ];

        entry.UserIds = [.. Collect(_userChecks)];
        entry.LibraryIds = [.. Collect(_libraryChecks)];
        entry.DeviceIds = [.. Collect(_deviceChecks)];
        entry.GroupItems = _groupItems?.IsChecked == true;
        return entry;
    }

    /// <summary>筛选框的值在建框时就记进元组（id 与框同行），收集只是把勾着的挑出来。</summary>
    private static IEnumerable<string> Collect(List<(CheckBox Box, string Id)> checks) =>
        checks.Where(one => one.Box.IsChecked == true).Select(one => one.Id);

    private async void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // 协议按钮的「关」由这段接手：存成了才收，存砸了把原因留在屏上、表单原样待着。
        args.Cancel = true;
        if (_saving) return;

        if (_context.Entry.NotifierKey == NotificationServiceInfo.WebhooksKey && UrlBox.Text.Trim().Length == 0)
        {
            ShowResult(InfoBarSeverity.Warning, "先填上 Webhook 地址 —— 通知总得有个去处。");
            return;
        }

        _saving = true;
        IsPrimaryButtonEnabled = false;
        TestButton.IsEnabled = false;
        try
        {
            await _context.SaveAsync(Build()).ConfigureAwait(true);
            Hide();
        }
        catch (Exception error)
        {
            ShowResult(InfoBarSeverity.Error, Failure.Describe(error));
        }
        finally
        {
            _saving = false;
            IsPrimaryButtonEnabled = true;
            TestButton.IsEnabled = true;
        }
    }

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        if (_testing) return;

        _testing = true;
        IsPrimaryButtonEnabled = false;
        TestButton.IsEnabled = false;
        try
        {
            await _context.TestAsync(Build()).ConfigureAwait(true);
            ShowResult(InfoBarSeverity.Success, "已让服务器发了一条测试通知 —— 到目的地看看收到没有。");
        }
        catch (Exception error)
        {
            ShowResult(InfoBarSeverity.Error, Failure.Describe(error));
        }
        finally
        {
            _testing = false;
            IsPrimaryButtonEnabled = true;
            TestButton.IsEnabled = true;
        }
    }

    private void ShowResult(InfoBarSeverity severity, string message)
    {
        ResultBar.Severity = severity;
        ResultBar.Message = message;
        ResultBar.IsOpen = true;
    }
}

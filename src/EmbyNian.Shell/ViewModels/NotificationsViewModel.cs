using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbyNian.Configuration;
using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyNian.Shell.ViewModels;

/// <summary>通知列表里的一行。名字、服务名、事件摘要在装载时算好；整行没有会变的东西 —— 编辑之后整页重读，
/// 行对象随旧列表一起换掉，所以这里不需要可观察属性。</summary>
public sealed class NotificationRow(UserNotificationInfo info, string eventsSummary, NotificationsViewModel owner)
{
    /// <summary>服务器回读的那一条。列表按钮的命令参数就是它。</summary>
    public UserNotificationInfo Info { get; } = info;

    /// <summary>列表上显示的名字：自定义名优先，没有就用服务名。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Info.FriendlyName)
        ? Info.ServiceName ?? Info.NotifierKey ?? "通知"
        : Info.FriendlyName!;

    /// <summary>背后那个渠道（如「Webhooks」）。</summary>
    public string ServiceName => Info.ServiceName ?? Info.NotifierKey ?? "";

    /// <summary>订阅的事件名，顿号相连，服务器给的名字已本地化。一条都没订就说明白它。</summary>
    public string EventsSummary { get; } = eventsSummary;

    public NotificationsViewModel Owner { get; } = owner;
}

/// <summary>
/// 「通知」设置页（设置左侧名单里的一个内嵌页）的视图模型 —— 2026-09-25 用户令「把本项目的通知改为 emby 的，
/// 让 emby 负责转发；复刻 emby 网页设置中的通知界面」之后的形状。复刻的是官方网页端
/// <c>/settings/notifications.html</c> 那一页的语义：条目列表（<c>Configured</c>）、添加（服务 →
/// <c>Defaults</c> 铺底 → 编辑器）、编辑、删除、发送测试；事件表（<c>Types</c>）是服务器按用户语言给的，
/// 这里不自带词表。
/// <para>
/// 条目数据全部在服务器上，本页只做增删改查，不落 settings.json —— 与「服务器」页同一个理由：会说话的东西
/// 不进设置文档。
/// </para>
/// <para>
/// <b>一个例外（2026-09-29 起，2026-09-30 定名「通知接管」）</b>：列表上面那一格。它不是服务器上的通知条目，
/// 而是客户端自己的行为（够到「标记已看」阈值就替服务器补报一趟停止，好让那边的通知早点转发出去），
/// 所以它只能落在本机的 settings.json 里，见 <see cref="StopReportEnabled"/>。这一页的其余一切仍然一个
/// 字节都不存。
/// </para>
/// </summary>
public sealed partial class NotificationsViewModel : PageViewModel
{
    private const string Category = "通知";

    private EmbySession? _session;
    private ISettingsService? _settings;

    private bool _stopReportEnabled;
    private bool _stopReportSeeded;

    /// <summary>事件表缓存：装载时拿一次，编辑器与行摘要共用。按 id 找名字的表随取随建。</summary>
    internal List<NotificationCategoryInfo> Categories { get; private set; } = [];

    public ObservableCollection<NotificationRow> Entries { get; } = [];

    [ObservableProperty]
    public partial bool ShowEmpty { get; set; }

    [ObservableProperty]
    public partial string? Subheading { get; set; }

    public Visibility EmptyVisibility => Show(ShowEmpty);

    public bool CanRun => !Busy && _session is not null;

    /// <summary>把编辑器弹出来的那一位。编辑器要页面的 <c>XamlRoot</c>，所以由页面指派 —— 同 <see cref="PageViewModel.UseConfirm"/>
    /// 的道理，不造接口。</summary>
    internal Func<NotificationEditorContext, Task>? ShowEditor;

    /// <summary>多于一个通知服务时挑一个。同样是页面指派；返回 null 表示用户收了手。</summary>
    internal Func<List<NotificationServiceInfo>, Task<NotificationServiceInfo?>>? PickService;

    /// <summary>
    /// 页面进来时交来的两样东西：这次要问的服务器会话，与本地那一格的设置文档（2026-09-29 起）。
    /// <para>
    /// 设置文档是这一页第一次出现的依赖 —— 上面那份列表整个住在服务器上，而「通知接管」是
    /// 客户端行为，只能落在 settings.json 里。<b>读初值、读完才允许写回</b>，同
    /// <see cref="SettingToggleRow"/> 的 <c>_seeded</c>：控件装载时的一次回推不该被当成用户拨了一下。
    /// </para>
    /// </summary>
    internal void Attach(EmbySession session, ISettingsService settings)
    {
        _session = session;
        _settings = settings;

        _stopReportEnabled = settings.Settings.Playback.StopReportEnabled;
        _stopReportSeeded = true;

        OnPropertyChanged(nameof(StopReportEnabled));

        // 说明那一行的收放也在这儿对齐一次：它是页级状态（「隐藏功能下方说明」，SettingRow.NotesHidden），
        // 而这张卡片是手写的、不经过设置行的模板 —— 每次导航进来重读一遍，屏上才跟上设置里那一档。
        OnPropertyChanged(nameof(NoteVisibility));
    }

    /// <summary>
    /// 说明那一行收不收起来 —— 跟「隐藏功能下方说明」（设置 → 界面）走，规则在
    /// <see cref="SettingRow.NotesVisibility"/> 那一处（一条规则、两个消费方：设置行模板与这张手写卡片）。
    /// <para>
    /// 2026-09-30 用户报「我不是开启了 隐藏功能下方说明 为什么下面的说明不隐藏？」—— 这张卡片当时一个字节都
    /// 没接这条开关：它不走行的模板，绑不绑是它自己的事，而它没绑。求值时机与设置行同一条道理：容器落到树上
    /// 那一刻读一次（<see cref="Attach"/> 里喊一声），不在别处缓存第二份。
    /// </para>
    /// </summary>
    public Visibility NoteVisibility => SettingRow.NotesVisibility;

    /// <summary>
    /// 「通知接管」那一格的开关（2026-09-29 加、2026-09-30 用户令改名；用户令 2026-09-29：「在设置的通知中
    /// 新增功能，播放进度达到自定义百分比的时候，自动触发发送 播放-停止」，同日续令「发送通知的判断标准改为
    /// 播放行为中的 标记已观看阈值(%)，要区分国漫」）。落到 <see cref="PlaybackSettings.StopReportEnabled"/> ——
    /// 阈值本身不在这一页，就是「播放行为」里那两档标记已看阈值（国漫走国漫那一档）。
    /// </summary>
    public bool StopReportEnabled
    {
        get => _stopReportEnabled;
        set
        {
            if (_stopReportEnabled == value) return;

            _stopReportEnabled = value;
            OnPropertyChanged();
            if (_stopReportSeeded) WriteStopReportSettings();
        }
    }

    private void WriteStopReportSettings()
    {
        if (_settings is null) return;

        // 拨一下就存：这一页没有「保存」按钮，跟设置页那些行一个道理（TrySave 不抛，失败只记日志）。
        _settings.Settings.Playback.StopReportEnabled = _stopReportEnabled;
        _settings.TrySave();
    }

    public override async Task ReloadAsync()
    {
        if (_session is null) return;

        var token = BeginLoad();
        try
        {
            var client = _session.Client;

            // 事件表与条目表一起问：互不依赖，谁先回来都是它。
            var typesTask = client.GetNotificationTypesAsync(token);
            var entriesTask = client.GetNotificationsAsync(token);
            await Task.WhenAll(typesTask, entriesTask).ConfigureAwait(true);

            if (!IsCurrent(token)) return;

            Categories = typesTask.Result;
            var names = EventsLookup();

            Entries.Clear();
            foreach (var info in entriesTask.Result)
                Entries.Add(new NotificationRow(info, Summarize(info, names), this));

            ShowEmpty = Entries.Count == 0;
            Subheading = $"{Entries.Count} 条通知 · 由 Emby 服务器负责发送；播放事件在播放时上报服务器，"
                + "由服务器上装的通知服务转发到目的地";
            EndLoad(token);
        }
        catch (Exception error)
        {
            if (!IsCurrent(token)) return;
            EndLoad(token);
            Report("读取通知条目失败", error);
        }
    }

    /// <summary>事件 id → 名字（本地化名）。事件表没拿到时给空表 —— 行摘要显示原始 id 而不是崩。</summary>
    internal Dictionary<string, string> EventsLookup() => Categories
        .SelectMany(category => category.Events)
        .Where(@event => !string.IsNullOrEmpty(@event.Id))
        .GroupBy(@event => @event.Id, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.Ordinal);

    private static string Summarize(UserNotificationInfo info, Dictionary<string, string> names)
    {
        if (info.EventIds.Count == 0) return "未订阅任何事件";
        var known = info.EventIds
            .Select(id => names.TryGetValue(id, out var name) ? name : id)
            .ToList();
        return string.Join("、", known);
    }

    protected override void BusyChanged()
    {
        base.BusyChanged();
        AddNotificationCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
        EditCommand.NotifyCanExecuteChanged();
        TestCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task AddNotificationAsync()
    {
        if (_session is null) return;

        try
        {
            var services = await _session.Client.GetNotificationServicesAsync(CancellationToken.None).ConfigureAwait(true);
            if (services.Count == 0)
            {
                Notify(null, "服务器上还没有装任何通知服务（比如官方的 Webhooks 插件），先到 Emby 控制台装一个。",
                    InfoBarSeverity.Warning);
                return;
            }

            var service = services.Count == 1
                ? services[0]
                : await (PickService?.Invoke(services) ?? Task.FromResult<NotificationServiceInfo?>(null))
                    .ConfigureAwait(true);
            if (service is null) return;

            var context = await BuildEditorContextAsync(service.Id, isNew: true, entry: null).ConfigureAwait(true);
            if (ShowEditor is { } show) await show(context).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            Report("添加通知失败", error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RefreshAsync() => ReloadAsync();

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task EditAsync(NotificationRow? row)
    {
        if (_session is null || row is null) return;

        try
        {
            // 编辑器动的是副本：取消编辑不能把没保存的改动留在列表那一条上 —— 列表的重读是唯一把改动
            // 「落屏」的路，服务器收下了才重读。
            var context = await BuildEditorContextAsync(
                row.Info.NotifierKey ?? "", isNew: false, entry: row.Info.Clone()).ConfigureAwait(true);
            if (ShowEditor is { } show) await show(context).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            Report("打开通知编辑器失败", error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task TestAsync(NotificationRow? row)
    {
        if (_session is null || row is null) return;

        try
        {
            await _session.Client.TestNotificationAsync(row.Info.Clone(), CancellationToken.None).ConfigureAwait(true);
            Notify(null, $"已让服务器发了一条测试通知（{row.DisplayName}）—— 到目的地看看收到没有。", InfoBarSeverity.Success);
        }
        catch (Exception error)
        {
            Report($"测试通知没有发出去（{row.DisplayName}）", error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task DeleteAsync(NotificationRow? row)
    {
        if (_session is null || row is null) return;

        var confirmed = await ConfirmAsync(
            "删除通知", $"确定删除通知「{row.DisplayName}」吗？此后这些事件不再转发，删除不能撤销。", "删除")
            .ConfigureAwait(true);
        if (!confirmed) return;

        try
        {
            await _session.Client.DeleteNotificationAsync(row.Info.Id ?? "", CancellationToken.None).ConfigureAwait(true);
            Log.Info(Category, $"已删除通知条目（{row.DisplayName}）");
            await ReloadAsync().ConfigureAwait(true);
        }
        catch (Exception error)
        {
            Report("删除通知失败", error);
        }
    }

    /// <summary>编辑器要的一切：铺底条目（新建时来自 Defaults）、事件表、三个筛选名单、保存与测试的路。</summary>
    private async Task<NotificationEditorContext> BuildEditorContextAsync(string notifierKey, bool isNew, UserNotificationInfo? entry)
    {
        if (_session is null) throw new InvalidOperationException("尚未登录 Emby");

        var client = _session.Client;

        if (isNew)
        {
            entry = await client.GetNotificationDefaultsAsync(notifierKey, CancellationToken.None).ConfigureAwait(true);
        }
        entry ??= new UserNotificationInfo { NotifierKey = notifierKey, Enabled = true };

        // 三个筛选名单尽力而为：拿不到就让对应一栏空着（编辑器里说明拿不到），条目照建 —— 名单不是编辑器
        // 的门槛，事件表才是。
        var users = await TryAsync(client.GetNotificationUsersAsync, "用户").ConfigureAwait(true);
        var libraries = await TryAsync(client.GetNotificationLibrariesAsync, "媒体库").ConfigureAwait(true);
        var devices = await TryAsync(client.GetNotificationDevicesAsync, "设备").ConfigureAwait(true);

        return new NotificationEditorContext(
            entry, isNew, Categories, users, libraries, devices,
            SaveAsync: async saved =>
            {
                await client.SaveNotificationAsync(saved, CancellationToken.None).ConfigureAwait(true);
                Log.Info(Category, $"已保存通知条目（{saved.FriendlyName ?? saved.ServiceName}，{saved.EventIds.Count} 个事件）");
                await ReloadAsync().ConfigureAwait(true);
            },
            TestAsync: async tested =>
            {
                await client.TestNotificationAsync(tested, CancellationToken.None).ConfigureAwait(true);
                return true;
            });
    }

    private static async Task<List<T>> TryAsync<T>(
        Func<CancellationToken, Task<List<T>>> fetch, string what)
    {
        try
        {
            return await fetch(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception)
        {
            Log.Warn(Category, $"读取{what}名单失败，编辑器里这一栏留空");
            return [];
        }
    }
}

/// <summary>
/// 交给编辑器对话框的一整包：条目副本（对话框只动它）、事件表、筛选名单，以及「保存」「测试」两条出路 ——
/// 都由视图模型备好，对话框自己不碰服务器。保存成功由 <see cref="NotificationsViewModel"/> 那边的委托负责
/// 重读列表；测试只问「发出去没有」，真话由服务器说。
/// </summary>
/// <remarks>public 是给编译器看的：编辑器对话框是公开的 ContentDialog 子类，构造函数吃到它，类型就得一样
/// 公开（否则 CS0051）。</remarks>
public sealed record NotificationEditorContext(
    UserNotificationInfo Entry,
    bool IsNew,
    List<NotificationCategoryInfo> Categories,
    IReadOnlyList<NotificationUser> Users,
    IReadOnlyList<NotificationLibrary> Libraries,
    IReadOnlyList<NotificationDevice> Devices,
    Func<UserNotificationInfo, Task> SaveAsync,
    Func<UserNotificationInfo, Task<bool>> TestAsync);

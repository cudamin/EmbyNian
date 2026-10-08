using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbyNian.Emby;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyNian.Shell.ViewModels;

public sealed class DashboardSessionRow(EmbyDashboardSession session)
{
    public string Device => Value(session.DeviceName, "未知设备");
    public string Client => string.Join(" · ", new[] { session.Client, session.ApplicationVersion }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public string User => Value(session.UserName, "未登录用户");
    public bool IsPlaying => session.NowPlayingItem is not null;
    public string Media => session.NowPlayingItem is { } item
        ? string.Join(" · ", new[] { item.SeriesName, item.Name }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct())
        : "当前未播放";
    public string State => !IsPlaying ? "在线" : session.PlayState?.IsPaused == true ? "已暂停" : "正在播放";
    public string Playback => !IsPlaying ? "" : string.Join(" · ", new[] { Method, Position }.Where(value => value.Length > 0));
    public string LastActive => session.LastActivityDate?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "未知";
    public Visibility PlaybackVisibility => IsPlaying ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProgressVisibility => IsPlaying && session.NowPlayingItem?.RunTimeTicks is > 0
        ? Visibility.Visible : Visibility.Collapsed;
    public double Progress => session.NowPlayingItem?.RunTimeTicks is > 0 and var duration
        ? Math.Clamp((session.PlayState?.PositionTicks ?? 0) / (double)duration * 100, 0, 100) : 0;
    private string Method => session.PlayState?.PlayMethod switch
    {
        "Transcode" => "转码",
        "DirectStream" => "直接串流",
        "DirectPlay" => "直接播放",
        var method => method ?? ""
    };
    private string Position => session.PlayState?.PositionTicks is { } ticks
        ? Clock(ticks) + (session.NowPlayingItem?.RunTimeTicks is > 0 and var duration ? $" / {Clock(duration)}" : "")
        : "";
    private static string Clock(long ticks) => TimeSpan.FromTicks(Math.Max(0, ticks)).ToString(@"hh\:mm\:ss");
    private static string Value(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
}

public sealed class DashboardActivityRow(EmbyActivityEntry entry)
{
    public string Title => string.IsNullOrWhiteSpace(entry.Name) ? "服务器活动" : entry.Name;
    public string Detail => entry.ShortOverview ?? entry.Overview ?? "";
    public string Date => entry.Date?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "时间未知";
    public string Severity => entry.Severity switch
    {
        "Error" or "Fatal" => "错误",
        "Warn" or "Warning" => "警告",
        _ => "信息"
    };
    public Visibility DetailVisibility => string.IsNullOrWhiteSpace(Detail) ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>固定登录身份的控制台；离页后取消请求，管理操作在确认后再次核对身份。</summary>
public sealed partial class ServerDashboardViewModel : PageViewModel
{
    private EmbySessionScope? _scope;
    private bool _attached;
    private bool _powerRequested;
    internal EmbySystemInfo? SystemInfo { get; private set; }
    internal Func<string, Task<string?>>? EditServerName { get; set; }

    public ObservableCollection<DashboardSessionRow> Sessions { get; } = [];
    public ObservableCollection<DashboardActivityRow> Activities { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContentVisibility))]
    public partial bool HasSnapshot { get; set; }

    [ObservableProperty] public partial string ServerName { get; set; } = "Emby Server";
    [ObservableProperty] public partial string Version { get; set; } = "—";
    [ObservableProperty] public partial string OperatingSystem { get; set; } = "—";
    [ObservableProperty] public partial string Address { get; set; } = "尚未登录服务器";
    [ObservableProperty] public partial string LocalAddress { get; set; } = "未提供";
    [ObservableProperty] public partial string WanAddress { get; set; } = "未提供";
    [ObservableProperty] public partial string Health { get; set; } = "等待连接";
    [ObservableProperty] public partial string Updated { get; set; } = "自动刷新间隔 15 秒";
    [ObservableProperty] public partial string OnlineCount { get; set; } = "—";
    [ObservableProperty] public partial string PlayingCount { get; set; } = "—";
    [ObservableProperty] public partial string SessionsMessage { get; set; } = "正在读取在线设备…";
    [ObservableProperty] public partial string ActivityMessage { get; set; } = "正在读取活动记录…";
    [ObservableProperty] public partial string ActivitySummary { get; set; } = "最近 20 条";

    public Visibility ContentVisibility => Show(HasSnapshot);
    public bool CanRefresh => _attached && _scope?.IsCurrent == true && !Busy;
    public bool CanAutoRefresh => _attached && !Busy && !_powerRequested;
    public bool CanManage => CanRefresh && HasSnapshot && SystemInfo is { IsRestricted: false, IsShuttingDown: false } && !_powerRequested;
    public bool CanRestart => CanManage && SystemInfo?.CanSelfRestart != false;

    partial void OnHasSnapshotChanged(bool value) => BusyChanged();

    internal void Attach(EmbySession session)
    {
        Cancel();
        _attached = true;
        _powerRequested = false;
        _scope = session.IsSignedIn ? session.Capture() : null;
        if (_scope is { } scope)
        {
            ServerName = scope.Connection.ServerName;
            Address = EmbyServerAddress.ToDisplayString(scope.Connection.ApiBase);
        }
        RefreshCommand.NotifyCanExecuteChanged();
    }

    public override async Task ReloadAsync()
    {
        if (!_attached) return;
        if (_scope is not { IsCurrent: true } scope)
        {
            HasSnapshot = false;
            Sessions.Clear();
            Activities.Clear();
            IsReady = true;
            Notify("尚未连接服务器", "请在 EmbyNian → 服务器中登录，再打开控制台。", InfoBarSeverity.Informational);
            return;
        }

        var token = BeginLoad();
        try
        {
            var data = await scope.ExecuteAsync((client, ct) => client.GetDashboardAsync(ct), token).ConfigureAwait(true);
            if (!IsCurrent(token) || !scope.IsCurrent) return;

            var info = data.System;
            SystemInfo = info;
            _powerRequested = false;
            ServerName = string.IsNullOrWhiteSpace(info.ServerName) ? scope.Connection.ServerName : info.ServerName;
            Version = info.Version ?? "未知版本";
            OperatingSystem = info.OperatingSystemDisplayName ?? info.OperatingSystem ?? "未提供";
            LocalAddress = info.LocalAddress ?? "未提供";
            WanAddress = info.WanAddress ?? "未提供";
            Health = info.IsRestricted ? "已连接 · 受限访问"
                : info.IsShuttingDown ? "正在关闭"
                : info.HasPendingRestart ? "等待重启"
                : info.HasUpdateAvailable ? "有可用更新" : "运行正常";
            Sessions.Clear();
            foreach (var session in (data.Sessions.Value ?? []).OrderByDescending(value => value.NowPlayingItem is not null))
                Sessions.Add(new(session));
            Activities.Clear();
            foreach (var entry in (data.Activity.Value?.Items ?? []).OrderByDescending(value => value.Date))
                Activities.Add(new(entry));
            OnlineCount = data.Sessions.Value is null ? "—" : Sessions.Count.ToString();
            PlayingCount = data.Sessions.Value is null ? "—" : Sessions.Count(row => row.IsPlaying).ToString();
            SessionsMessage = data.Sessions.Error ?? (Sessions.Count == 0 ? "当前没有在线设备" : "服务器报告的活动会话");
            ActivityMessage = data.Activity.Error ?? (Activities.Count == 0 ? "暂无活动记录" : "");
            ActivitySummary = data.Activity.Value is null ? "不可用" : $"最近 {Activities.Count} 条";
            Updated = $"更新于 {DateTime.Now:HH:mm:ss} · 每 15 秒刷新";
            HasSnapshot = true;
        }
        catch (Exception error)
        {
            if (IsCurrent(token) && scope.IsCurrent) Report("控制台更新失败", error);
        }
        finally
        {
            EndLoad(token);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => ReloadAsync();

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task RenameServerAsync()
    {
        if (!CanManage || _scope is not { } scope || EditServerName is null) return;
        var token = BeginLoad();
        try
        {
            var name = await EditServerName(ServerName).ConfigureAwait(true);
            if (!IsCurrent(token) || !scope.IsCurrent || string.IsNullOrWhiteSpace(name) || name.Trim() == ServerName) return;
            await EmbyServerAdministration.RenameAsync(scope, name, token).ConfigureAwait(true);
            if (!IsCurrent(token) || !scope.IsCurrent) return;
            ServerName = name.Trim();
            if (SystemInfo is { } info) info.ServerName = ServerName;
            Notify("名称已更新", "服务器的显示名称已保存。", InfoBarSeverity.Success);
        }
        catch (Exception error)
        {
            if (IsCurrent(token) && scope.IsCurrent) Report("更改服务器名称失败", error);
        }
        finally { EndLoad(token); }
    }

    [RelayCommand(CanExecute = nameof(CanRestart))]
    private Task RestartServerAsync() => SendPowerAsync(restart: true);

    [RelayCommand(CanExecute = nameof(CanManage))]
    private Task ShutdownServerAsync() => SendPowerAsync(restart: false);

    private async Task SendPowerAsync(bool restart)
    {
        if (!(restart ? CanRestart : CanManage) || _scope is not { } scope) return;
        var token = BeginLoad();
        var verb = restart ? "重启" : "关闭";
        var sent = false;
        try
        {
            var message = $"确定{verb}「{ServerName}」吗？这会中断这台服务器上的所有播放和客户端连接。"
                + (restart ? "" : "关闭后需要在服务器所在设备上重新启动 Emby Server。");
            if (!await ConfirmAsync($"{verb} Emby Server", message, verb).ConfigureAwait(true)
                || !IsCurrent(token) || !scope.IsCurrent) return;
            sent = true;
            await scope.ExecuteAsync((client, ct) => restart ? client.RestartServerAsync(ct) : client.ShutdownServerAsync(ct), token)
                .ConfigureAwait(true);
            if (!IsCurrent(token) || !scope.IsCurrent) return;
            _powerRequested = true;
            Health = $"已发送{verb}请求";
            Notify(Health, "自动刷新已暂停。确认服务器恢复运行后，可点击「刷新」重新连接。", InfoBarSeverity.Informational);
        }
        catch (Exception error)
        {
            if (!IsCurrent(token) || !scope.IsCurrent) return;
            _powerRequested = sent;
            Report($"未能确认服务器{verb}结果，请检查服务器状态后刷新", error);
        }
        finally { EndLoad(token); }
    }

    protected override void BusyChanged()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        RenameServerCommand.NotifyCanExecuteChanged();
        RestartServerCommand.NotifyCanExecuteChanged();
        ShutdownServerCommand.NotifyCanExecuteChanged();
    }

    public override void Cancel()
    {
        _attached = false;
        base.Cancel();
        BusyChanged();
    }
}

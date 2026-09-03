using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbyNian.Configuration;
using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Infrastructure;
using EmbyNian.Services;
using EmbyNian.Shell.Media;
using EmbyNian.Shell.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyNian.Shell.ViewModels;

/// <summary>
/// The servers page: saved servers and their accounts, a scan of the local network, and what the poster
/// cache has taken up on disk.
/// <para>
/// Every button on the page is a command here. That is the part worth having — the page used to carry
/// nine <c>Click</c> handlers, three of which began with <c>if (_busy) return;</c>, so pressing 「测试」
/// while a scan was running looked exactly like pressing a button that did not work. A command that
/// cannot run says so by being greyed out, and the guard and the greying are the same line of code.
/// </para>
/// </summary>
public sealed partial class ServersViewModel : PageViewModel
{
    private const string Category = "服务器";

    private ISettingsService? _settings;
    private EmbySession? _session;
    private EmbyImageStore? _images;
    private IShellActions? _actions;

    /// <summary>The saved servers, rebuilt whenever any of them changes. See <see cref="ServerRow"/>.</summary>
    public ObservableCollection<ServerRow> Servers { get; } = [];

    /// <summary>What the last network scan answered. Cleared at the start of the next one.</summary>
    public ObservableCollection<EmbyDiscoveredServer> Discovered { get; } = [];

    [ObservableProperty]
    public partial string? Subheading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyVisibility))]
    public partial bool ShowEmpty { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiscoveredVisibility))]
    public partial bool ShowDiscovered { get; set; }

    /// <summary>The cache line: 「1.1 GB · 3,481 个文件 · 上限 400 MB」 — the budget being whatever the
    /// 界面 card's 图片缓存上限 row currently says, not a fixed 400.</summary>
    [ObservableProperty]
    public partial string? CacheSummary { get; set; }

    public Visibility EmptyVisibility => Show(ShowEmpty);

    public Visibility DiscoveredVisibility => Show(ShowDiscovered);

    /// <summary>
    /// Whether the page is free to start something. One flag for the network scan, the connection test
    /// and the cache wipe together: they all write the same notice bar and the same progress bar, so two
    /// at once would have the second one's result overwrite the first one's.
    /// </summary>
    public bool CanRun => !Busy;

    private bool CanTest(ServerRow? row) => CanRun && row is not null;

    /// <summary>
    /// Whether <see cref="Attach"/> has run. All four collaborators arrive in that one call as
    /// non-nullable parameters, so this single question answers for every one of them — including for the
    /// network scan, which happens to touch none of them and must still do nothing on a page nobody
    /// attached. Past this gate the fields are dereferenced with <c>!</c>.
    /// </summary>
    private bool Attached => _settings is not null;

    /// <summary>
    /// The live settings object, read through the service rather than copied: it is one instance for the
    /// process and the settings page edits it in place.
    /// </summary>
    private AppSettings Settings => _settings!.Settings;

    /// <inheritdoc />
    protected override void BusyChanged()
    {
        OnPropertyChanged(nameof(CanRun));
        DiscoverCommand.NotifyCanExecuteChanged();
        TestCommand.NotifyCanExecuteChanged();
        ClearCacheCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Handed the shell and the three capabilities this page works through, once per navigation to it.
    /// <para>
    /// All three are kept rather than read once: this page is the only one that writes servers and accounts
    /// back, and every button on it is a fresh read of the same three.
    /// </para>
    /// </summary>
    internal void Attach(IShellActions actions, ISettingsService settings, EmbySession session, EmbyImageStore images)
    {
        _actions = actions;
        _settings = settings;
        _session = session;
        _images = images;
    }

    /// <inheritdoc />
    public override Task ReloadAsync()
    {
        Refresh();
        return MeasureCacheAsync();
    }

    /// <summary>
    /// Rebuilds the rows from the settings file. Synchronous on purpose: this is a read of an object
    /// graph already in memory, and a progress bar for it would flash rather than inform.
    /// </summary>
    [RelayCommand]
    internal void Refresh()
    {
        if (!Attached) return;

        var current = _session!.Server;
        var account = _session.Account;

        Servers.Clear();
        foreach (var server in Settings.Servers)
            Servers.Add(new ServerRow(this, server, ReferenceEquals(server, current), account));

        ShowEmpty = Servers.Count == 0;
        Subheading = Servers.Count == 0 ? "管理服务器与登录用户" : $"已保存 {Servers.Count} 台服务器";
    }

    [RelayCommand]
    private void AddServer() => _actions?.OpenSignIn();

    /// <summary>需求 1：find servers on this network without anyone typing an address.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task DiscoverAsync()
    {
        if (!Attached) return;

        var token = BeginLoad();
        Discovered.Clear();
        ShowDiscovered = false;

        try
        {
            var found = await new EmbyServerDiscovery()
                .DiscoverAsync(cancellationToken: token)
                .ConfigureAwait(true);

            if (!IsCurrent(token)) return;

            foreach (var server in found) Discovered.Add(server);
            ShowDiscovered = Discovered.Count > 0;

            Notify(
                Discovered.Count > 0 ? "发现局域网服务器" : "未发现服务器",
                Discovered.Count > 0
                    ? $"找到 {Discovered.Count} 台 Emby 服务器，请选择要添加的服务器。"
                    : "当前网络没有响应 Emby 发现请求。",
                Discovered.Count > 0 ? InfoBarSeverity.Informational : InfoBarSeverity.Warning);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            Log.Warn(Category, "搜索局域网服务器失败", error);
            if (IsCurrent(token)) Report("搜索局域网服务器失败", error);
        }
        finally
        {
            EndLoad(token);
        }
    }

    /// <summary>
    /// Saves a discovered server and goes to sign in. Called from the page rather than bound as a
    /// command: the item in that list is a Core record with no way back to this view model, and a
    /// <c>ListView</c> whose rows are pickable is the right control for choosing one of several.
    /// </summary>
    internal void OpenDiscovered(EmbyDiscoveredServer discovered)
    {
        if (!Attached) return;

        try
        {
            var profile = _settings!.ResolveServer(discovered.Url);
            if (profile.HasPlaceholderName && !string.IsNullOrWhiteSpace(discovered.Name))
                profile.Name = discovered.Name;

            _settings.Save();
            ShowDiscovered = false;
            _actions!.OpenSignIn(profile);
        }
        catch (Exception error)
        {
            Log.Warn(Category, "保存发现的服务器失败", error);
            Report("保存发现的服务器失败", error);
        }
    }

    /// <summary>
    /// Asks the server who it is, without signing in. Doubles as the way a placeholder name gets
    /// replaced with the real one, which is the only reason a successful test rebuilds the rows.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestAsync(ServerRow? row)
    {
        if (!Attached || row is null) return;

        if (!EmbyServerAddress.TryNormalize(row.Profile.Url, out var apiBase, out var complaint))
        {
            Notify("连接失败", complaint, InfoBarSeverity.Error);
            return;
        }

        var token = BeginLoad();

        try
        {
            var info = await _session!.Gateway
                .GetPublicSystemInfoAsync(apiBase, token)
                .ConfigureAwait(true);

            if (!IsCurrent(token)) return;

            if (info.ServerName is { Length: > 0 } name && row.Profile.HasPlaceholderName)
            {
                row.Profile.Name = name;
                _settings!.Save();
                Refresh();
            }

            var version = string.IsNullOrWhiteSpace(info.Version) ? "" : $" · 版本 {info.Version}";
            Notify("连接成功", $"{info.ServerName ?? apiBase.Host}{version}", InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            Log.Warn(Category, $"测试服务器连接失败：{row.Profile.Url}", error);
            if (IsCurrent(token)) Report("连接失败", error);
        }
        finally
        {
            EndLoad(token);
        }
    }

    /// <summary>Signs in to a server, reusing whichever account was last used on it.</summary>
    [RelayCommand]
    private void LoginServer(ServerRow? row)
    {
        if (!Attached || row is null) return;

        var account = row.Profile.FindAccount(Settings.LastAccountId)
            ?? row.Profile.Accounts.FirstOrDefault();
        _ = _actions!.SwitchProfileAsync(row.Profile, account);
    }

    [RelayCommand]
    private void LoginAccount(AccountRow? row)
    {
        if (_actions is null || row is null) return;
        _ = _actions.SwitchProfileAsync(row.Server, row.Account);
    }

    [RelayCommand]
    private async Task DeleteServerAsync(ServerRow? row)
    {
        if (!Attached || row is null) return;

        var confirmed = await ConfirmAsync(
            "删除服务器",
            $"确定删除“{row.Name}”及其保存的 {row.Accounts.Count} 个账号吗？",
            "删除").ConfigureAwait(true);
        if (!confirmed || !Attached) return;

        if (ReferenceEquals(row.Profile, _session!.Server)) _session.SignOut();

        Settings.Servers.Remove(row.Profile);
        if (Settings.LastServerId == row.Profile.Id)
        {
            Settings.LastServerId = null;
            Settings.LastAccountId = null;
        }

        _settings!.Save();
        Refresh();
    }

    [RelayCommand]
    private async Task DeleteAccountAsync(AccountRow? row)
    {
        if (!Attached || row is null) return;

        var confirmed = await ConfirmAsync("删除账号", $"确定删除账号“{row.Username}”吗？", "删除")
            .ConfigureAwait(true);
        if (!confirmed || !Attached) return;

        if (ReferenceEquals(row.Server, _session!.Server) &&
            ReferenceEquals(row.Account, _session.Account))
            _session.SignOut();

        row.Server.Accounts.Remove(row.Account);
        if (Settings.LastAccountId == row.Account.Id) Settings.LastAccountId = null;

        _settings!.Save();
        Refresh();
    }

    /// <summary>
    /// Counts the poster cache. Not gated on <see cref="CanRun"/> and does not raise the progress bar:
    /// it is a caption, it runs on arrival, and it has no business disabling the buttons around it.
    /// <para>
    /// This is also where <see cref="PageViewModel.IsReady"/> is set, rather than in
    /// <see cref="Refresh"/>: the flag means 「everything on this page has its final content」, and the
    /// startup self-check reads the page the moment it goes true. Setting it as soon as the rows exist
    /// would have the report quote 「正在统计…」 as the cache size.
    /// </para>
    /// </summary>
    internal async Task MeasureCacheAsync()
    {
        if (!Attached) return;

        CacheSummary = "正在统计…";
        var usage = await _images!.MeasureAsync().ConfigureAwait(true);
        CacheSummary = Describe(usage, _images.MaxBytes);
        IsReady = true;
    }

    /// <summary>
    /// 需求 12：throw the cached artwork away. Wired here rather than on the settings page because this
    /// is where the local traces of a server are — the accounts, the saved tokens, and the pictures.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ClearCacheAsync()
    {
        if (!Attached) return;

        var confirmed = await ConfirmAsync(
            "清除图片缓存",
            $"将删除已下载的海报与缩略图（{CacheSummary}）。下次浏览会重新下载，不影响服务器和账号。",
            "清除").ConfigureAwait(true);
        if (!confirmed || !Attached) return;

        var token = BeginLoad();
        CacheSummary = "正在清除…";

        try
        {
            var removed = await _images!.ClearAsync().ConfigureAwait(true);
            if (!IsCurrent(token)) return;

            // The decoded ones too, or the covers already on screen keep showing from memory and the
            // 「清除了」 notice is about files the user cannot see.
            PosterCache.Clear();

            Notify("已清除图片缓存", $"删除了 {removed:N0} 个文件。", InfoBarSeverity.Success);
        }
        catch (Exception error)
        {
            Log.Warn(Category, "清除图片缓存失败", error);
            if (IsCurrent(token)) Report("清除图片缓存失败", error);
        }
        finally
        {
            EndLoad(token);
            await MeasureCacheAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 「上限」 comes along because a size on its own says nothing: 1.1 GB is either a problem or exactly
    /// what was asked for, and the number that decides which is the budget beside it.
    /// </summary>
    private static string Describe(ImageCacheUsage usage, long max)
    {
        var limit = $"上限 {TimeFormat.FileSize(max)}";
        return usage.Files == 0
            ? $"暂无缓存 · {limit}"
            : $"{TimeFormat.FileSize(usage.Bytes)} · {usage.Files:N0} 个文件 · {limit}";
    }
}

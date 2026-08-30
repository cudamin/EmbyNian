using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbyNian.Configuration;
using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Infrastructure;
using EmbyNian.Playback;
using EmbyNian.Services;
using EmbyNian.Shell.Platform;
using EmbyNian.Shell.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EmbyNian.Shell.ViewModels;

/// <summary>
/// One line in the log view. Immutable, because a log entry is a thing that happened: the only way it
/// changes is by being replaced.
/// </summary>
public sealed class LogRow
{
    internal LogRow(LogEntry entry)
    {
        Entry = entry;
        Header = $"{entry.Timestamp.LocalDateTime:HH:mm:ss.fff}  {entry.Level.ToString().ToUpperInvariant(),-5}  {entry.Category}";
        Message = entry.Message;
        Detail = entry.Detail ?? string.Empty;
        LevelBrush = Resolve(entry.Level);
    }

    public LogEntry Entry { get; }

    public string Header { get; }

    public string Message { get; }

    public string Detail { get; }

    /// <summary>Collapsed when there is no detail, so an empty block does not pad every row.</summary>
    public Visibility DetailVisibility => Detail.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// A warning or error is what someone opened this page to find, so its header gets a colour instead of
    /// the uniform accent every line used to have.
    /// <para>
    /// Resolved here rather than through a converter, and once per row: the alternative is a
    /// <c>LogLevel</c>-to-<c>Brush</c> converter file plus an entry in every page's resources, for three
    /// cases. A row built before a light/dark switch keeps the brush it was built with, which is fine
    /// because 刷新 rebuilds them all and this page is not where anyone changes themes.
    /// </para>
    /// <para>
    /// What makes an application-scope read safe here is that App.xaml pins <c>RequestedTheme="Dark"</c>,
    /// so this always lands in the palette's <c>Default</c> dictionary — and <c>ThemeHost</c> writes the
    /// chosen theme into <em>both</em> dictionaries for exactly this reason, so the brush this hands back is
    /// the current theme's danger red whichever theme that is. It did not always work: application scope
    /// picks among the palette's <c>ThemeDictionaries</c> by the Windows app mode, while these rows are
    /// drawn under <c>ShellPage</c>. On a light-mode machine that handed back the light dictionary's ink —
    /// #FFC0332B for an error against a #FF1E2126 card, 2.9:1 where the dark theme's green gets 6.2:1 — so
    /// the level colouring, the whole point of this property, was the first thing to wash out, on exactly
    /// the machines nobody developing this app was looking at. The sibling <c>TextBlock</c> in the same
    /// template uses <c>{ThemeResource}</c> and resolves off the element tree, so the row came out half
    /// light and half dark.
    /// </para>
    /// </summary>
    public Brush? LevelBrush { get; }

    /// <summary>What 复制日志 puts on the clipboard: the same text the log file has.</summary>
    public string FullText => Entry.ToString();

    private static Brush? Resolve(LogLevel level)
    {
        var key = level switch
        {
            LogLevel.Error => "EgDangerBrush",
            LogLevel.Warn => "EgWarningBrush",
            _ => "EgAccentBrush"
        };

        return Application.Current?.Resources.TryGetValue(key, out var found) == true ? found as Brush : null;
    }
}

/// <summary>
/// The diagnostics page: the current session, what mpv was last launched with, and the in-memory log.
/// <para>
/// The log half is the reason this page exists, and it now follows the log live rather than showing
/// whatever the buffer held at the moment the page opened — a page you have to keep pressing 刷新 on is a
/// page that misses the thing you were waiting to see. <see cref="RingBufferLogSink.Written"/> arrives on
/// whichever thread logged it, so every append is marshalled onto the UI thread through
/// <see cref="IUiDispatcher"/>.
/// </para>
/// </summary>
public sealed partial class DiagnosticsViewModel : PageViewModel
{
    private const string Category = "诊断";

    /// <summary>「全部」 in the two filter drop-downs. Not a category any caller uses.</summary>
    private const string AnyCategory = "全部";

    /// <summary>Everything the buffer has given us, newest last. <see cref="Rows"/> is this, filtered.</summary>
    private readonly List<LogEntry> _entries = [];

    private IShellActions? _actions;
    private ISettingsService? _settings;
    private EmbySession? _session;
    private IServerCapabilities? _capabilities;
    private PlaybackService? _playback;
    private AppPaths? _paths;
    private IUiDispatcher? _ui;
    private IClipboard? _clipboard;
    private ISystemLauncher? _launcher;
    private RingBufferLogSink? _sink;
    private EmbyItem? _nowPlaying;

    /// <summary>True between attaching and navigating away; stops a late append touching a dead page.</summary>
    private bool _live;

    public DiagnosticsViewModel()
    {
        Categories.Add(AnyCategory);
        SelectedCategory = AnyCategory;
    }

    // ---- the log view -------------------------------------------------------

    /// <summary>
    /// The filtered log, newest first. Newest first because this is a live view: the line that just
    /// arrived is the one being waited for, and it should not require a scroll to the bottom to see.
    /// 复制日志 still copies oldest first, which is the order a log file is read in.
    /// </summary>
    public ObservableCollection<LogRow> Rows { get; } = [];

    /// <summary>Every category the buffer has seen, plus 「全部」 at the front.</summary>
    public ObservableCollection<string> Categories { get; } = [];

    /// <summary>「全部」／「信息以上」／「警告以上」／「仅错误」, by index rather than by enum.</summary>
    public IReadOnlyList<string> Levels { get; } = ["全部", "信息以上", "警告以上", "仅错误"];

    [ObservableProperty]
    public partial string? SelectedCategory { get; set; }

    [ObservableProperty]
    public partial int SelectedLevel { get; set; }

    /// <summary>
    /// Whether new entries are appended as they happen. On by default; a switch, because reading a stack
    /// trace while the list reorders under the pointer is worse than a stale list.
    /// </summary>
    [ObservableProperty]
    public partial bool Following { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyLogsVisibility))]
    public partial bool ShowEmptyLogs { get; set; }

    [ObservableProperty]
    public partial string? LogCount { get; set; }

    [ObservableProperty]
    public partial string? UpdatedAt { get; set; }

    [ObservableProperty]
    public partial string? Subheading { get; set; }

    public Visibility EmptyLogsVisibility => Show(ShowEmptyLogs);

    // ---- the session, player and mpv cards ----------------------------------

    [ObservableProperty]
    public partial string? SessionState { get; set; }

    [ObservableProperty]
    public partial string? Account { get; set; }

    [ObservableProperty]
    public partial string? Server { get; set; }

    [ObservableProperty]
    public partial string? ServerVersion { get; set; }

    [ObservableProperty]
    public partial string? LogPath { get; set; }

    [ObservableProperty]
    public partial string? PlayerState { get; set; }

    [ObservableProperty]
    public partial string? Position { get; set; }

    [ObservableProperty]
    public partial string? NowPlaying { get; set; }

    [ObservableProperty]
    public partial string? Backend { get; set; }

    [ObservableProperty]
    public partial string? Ipc { get; set; }

    [ObservableProperty]
    public partial string? MpvPath { get; set; }

    [ObservableProperty]
    public partial string? Shader { get; set; }

    /// <summary>The heading over the launch options: which playback they belong to, and when.</summary>
    [ObservableProperty]
    public partial string? LaunchHeading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LaunchVisibility))]
    public partial string? LaunchOptions { get; set; }

    /// <summary>Hidden until something has actually been played, rather than showing an empty box.</summary>
    public Visibility LaunchVisibility => Show(!string.IsNullOrEmpty(LaunchOptions));

    // ---- wiring -------------------------------------------------------------

    /// <summary>
    /// Whether <see cref="Attach"/> has run. Everything below arrives in that one call as non-nullable
    /// parameters, so this single question answers for all of them; past this gate they are dereferenced
    /// with <c>!</c>. Deliberately not <see cref="_live"/>, which is a different fact — 刷新 still reads a
    /// page that has been navigated away from, and that is how the self-check gets its report.
    /// </summary>
    private bool Attached => _settings is not null;

    /// <summary>
    /// The live settings object, read through the service rather than copied: it is one instance for the
    /// process and the settings page edits it in place, which is exactly what this page is here to show.
    /// </summary>
    private AppSettings Settings => _settings!.Settings;

    /// <summary>
    /// Handed the shell, the services this page reads, the three platform capabilities it used to call
    /// directly, and the log buffer.
    /// <para>
    /// The buffer is the one thing that does not come from the container: it is a static on the shell's
    /// entry point, which only a view has any business knowing about, so the page passes it in alongside
    /// the rest. It stays nullable because a run without file logging has none.
    /// </para>
    /// </summary>
    internal void Attach(
        IShellActions actions,
        ISettingsService settings,
        EmbySession session,
        IServerCapabilities capabilities,
        PlaybackService playback,
        AppPaths paths,
        IUiDispatcher ui,
        IClipboard clipboard,
        ISystemLauncher launcher,
        RingBufferLogSink? sink)
    {
        Detach();

        _actions = actions;
        _settings = settings;
        _session = session;
        _capabilities = capabilities;
        _playback = playback;
        _paths = paths;
        _ui = ui;
        _clipboard = clipboard;
        _launcher = launcher;
        _sink = sink;
        _live = true;

        playback.NowPlayingChanged += OnNowPlaying;
        playback.StatusChanged += OnStatus;
        if (_sink is not null) _sink.Written += OnWritten;
    }

    /// <summary>
    /// Unhooks everything. Called on navigating away and again from <see cref="Attach"/>, because the
    /// frame reuses one page instance and a second attach would otherwise double every handler.
    /// </summary>
    internal void Detach()
    {
        _live = false;

        if (_playback is not null)
        {
            _playback.NowPlayingChanged -= OnNowPlaying;
            _playback.StatusChanged -= OnStatus;
        }

        if (_sink is not null) _sink.Written -= OnWritten;
        _sink = null;
    }

    public override void Cancel()
    {
        Detach();
        base.Cancel();
    }

    public override void Dispose()
    {
        Detach();
        base.Dispose();
    }

    /// <summary>
    /// Reads everything the page shows. Synchronous — every source is in this process — so it returns a
    /// completed task rather than pretending otherwise.
    /// </summary>
    public override Task ReloadAsync()
    {
        Refresh();
        return Task.CompletedTask;
    }

    [RelayCommand]
    internal void Refresh()
    {
        if (!Attached) return;

        ReadLog();
        ReadSession();
        ReadPlayback(_playback!.Status);
        ReadMpv();
        ClearNotice();
        IsReady = true;
    }

    // ---- reading ------------------------------------------------------------

    private void ReadLog()
    {
        _entries.Clear();
        _entries.AddRange(_sink?.Snapshot() ?? []);

        RebuildCategories();
        ApplyFilter();
    }

    /// <summary>
    /// One combo entry per category the buffer has seen, in the order the framework's own pages use:
    /// alphabetical, with 「全部」 pinned first. The selection survives a refresh unless its category has
    /// aged out of the ring buffer entirely.
    /// </summary>
    private void RebuildCategories()
    {
        var wanted = _entries
            .Select(entry => entry.Category)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.CurrentCulture)
            .ToList();
        wanted.Insert(0, AnyCategory);

        if (Categories.SequenceEqual(wanted, StringComparer.Ordinal)) return;

        var keep = SelectedCategory;
        Categories.Clear();
        foreach (var name in wanted) Categories.Add(name);

        // Assigning Categories cleared the ComboBox's selection; put it back, or fall back to 全部 when
        // the category it was showing is no longer in the buffer.
        SelectedCategory = keep is not null && wanted.Contains(keep, StringComparer.Ordinal) ? keep : AnyCategory;
    }

    partial void OnSelectedCategoryChanged(string? value) => ApplyFilter();

    partial void OnSelectedLevelChanged(int value) => ApplyFilter();

    private LogLevel Minimum => SelectedLevel switch
    {
        1 => LogLevel.Info,
        2 => LogLevel.Warn,
        3 => LogLevel.Error,
        _ => LogLevel.Debug
    };

    private bool Passes(LogEntry entry) =>
        entry.Level >= Minimum
        && (SelectedCategory is null or AnyCategory
            || string.Equals(entry.Category, SelectedCategory, StringComparison.Ordinal));

    private void ApplyFilter()
    {
        Rows.Clear();

        // Reversed as it is built rather than sorted afterwards: the source list is already in order.
        for (var index = _entries.Count - 1; index >= 0; index--)
            if (Passes(_entries[index])) Rows.Add(new LogRow(_entries[index]));

        ShowEmptyLogs = Rows.Count == 0;
        RefreshCounts();
    }

    /// <summary>
    /// The three lines that describe the list rather than being in it. Shared with <see cref="Append"/>
    /// because they were written out twice, and a count that is maintained in two places is a count that
    /// eventually disagrees with itself.
    /// </summary>
    private void RefreshCounts()
    {
        LogCount = Rows.Count == _entries.Count
            ? $"{_entries.Count} 条内存日志"
            : $"{Rows.Count} / {_entries.Count} 条内存日志";
        UpdatedAt = $"更新于 {DateTime.Now:HH:mm:ss}";
        Subheading = $"日志、播放参数与 mpv 状态 · 最近 {_entries.Count} 条";
    }

    /// <summary>
    /// A log entry, from whatever thread wrote it. Nothing here touches bound state — that happens on
    /// the UI queue — because this can be called from an HTTP continuation or an mpv IPC read.
    /// <para>
    /// <see cref="IUiDispatcher.Post"/> rather than <see cref="IUiDispatcher.Run"/>, and that is not an
    /// oversight: a log line written on the UI thread would otherwise insert into <see cref="Rows"/> from
    /// inside whatever operation logged it, which is a bound collection changing under a layout pass.
    /// </para>
    /// </summary>
    private void OnWritten(LogEntry entry)
    {
        if (!_live || !Following) return;
        _ui!.Post(() => Append(entry));
    }

    private void Append(LogEntry entry)
    {
        if (!_live || !Following) return;

        _entries.Add(entry);

        // The buffer drops its own oldest entry past capacity; drop the row that entry was showing along
        // with it. That pairing is the point: this used to trim _entries here and trim Rows on its own
        // count further down, which agrees only while nothing is filtered — Rows then gains one item per
        // entry and the two reach capacity together. Select 仅错误 and Rows gains one per *matching*
        // entry, so it never reaches the threshold and keeps rows whose entries left the buffer long ago.
        // 「N / M 条内存日志」 then claims N of the M entries in memory match the filter, which is false,
        // and 刷新 — which rebuilds Rows from a fresh snapshot — silently deletes the difference, making
        // the refresh button look like it threw log data away.
        //
        // Rows[^1] is the row to drop and no search is needed: Rows is this list reversed and filtered,
        // so the oldest entry that passes the filter is its last row, and the entry being evicted is the
        // oldest there is. Passes() is asked with the current filter, which is the right one — changing
        // either filter rebuilds Rows outright.
        var capacity = _sink?.Capacity ?? 500;
        while (_entries.Count > capacity)
        {
            var dropped = _entries[0];
            _entries.RemoveAt(0);
            if (Passes(dropped) && Rows.Count > 0) Rows.RemoveAt(Rows.Count - 1);
        }

        if (!Categories.Contains(entry.Category, StringComparer.Ordinal))
            InsertCategory(entry.Category);

        if (Passes(entry))
        {
            Rows.Insert(0, new LogRow(entry));
            ShowEmptyLogs = false;
        }

        RefreshCounts();
    }

    /// <summary>
    /// Slots a newly-seen category into the sorted list rather than rebuilding it, which would reset the
    /// ComboBox's selection out from under whoever is reading.
    /// </summary>
    private void InsertCategory(string category)
    {
        var at = 1;
        while (at < Categories.Count
            && string.Compare(Categories[at], category, StringComparison.CurrentCulture) < 0) at++;
        Categories.Insert(at, category);
    }

    private void ReadSession()
    {
        if (!Attached) return;

        var session = _session!;
        SessionState = session.IsSignedIn ? "已登录" : "未登录";
        Account = session.Account?.Username is { Length: > 0 } username ? username : "未登录";
        Server = session.Connection is { } connection
            ? string.IsNullOrWhiteSpace(session.ServerDisplayName)
                ? connection.ApiBase.AbsoluteUri
                : $"{session.ServerDisplayName}\n{connection.ApiBase.AbsoluteUri}"
            : "未连接";
        ServerVersion = _capabilities!.ServerVersion?.ToString() ?? "尚未读取";
        LogPath = _paths!.LogDirectory;
    }

    private void ReadMpv()
    {
        if (!Attached) return;

        var settings = Settings.Mpv;
        Backend = settings.Backend switch
        {
            MpvBackendKind.BuiltInLibMpv => "内置 libmpv（窗口内播放）",
            MpvBackendKind.ExternalMpv => "外部 mpv.exe（独立窗口）",
            _ => settings.Backend.ToString()
        };
        Ipc = settings.EnableIpc ? "已启用" : "已关闭";
        MpvPath = string.IsNullOrWhiteSpace(settings.ExecutablePath) ? "未设置" : settings.ExecutablePath;

        // LastLaunch rather than the live Launch* properties: those are cleared when playback ends, and
        // this page is read after a playback went wrong.
        var launch = _playback!.LastLaunch;
        Shader = launch?.ShaderProfile is { Length: > 0 } profile
            ? string.IsNullOrWhiteSpace(launch.ShaderReason) ? profile : $"{profile}\n{launch.ShaderReason}"
            : "未播放或未启用";

        if (launch is null || launch.Options.Count == 0)
        {
            LaunchHeading = null;
            LaunchOptions = null;
            return;
        }

        var playing = _playback.IsPlaying;
        LaunchHeading = playing
            ? $"本次播放参数 · {launch.Title}"
            : $"上次播放参数 · {launch.Title} · {launch.StartedAt.LocalDateTime:HH:mm:ss}";

        var builder = new StringBuilder();
        if (launch.QualityPreset is { Length: > 0 } preset) builder.AppendLine($"画质预设 = {preset}");
        foreach (var option in launch.Options) builder.AppendLine($"{option.Key} = {option.Value}");
        LaunchOptions = builder.ToString().TrimEnd();
    }

    private void ReadPlayback(PlayerStatus status)
    {
        if (!Attached) return;

        if (!_playback!.IsPlaying)
        {
            PlayerState = "未播放";
            Position = "—";
            NowPlaying = "没有正在播放的项目";
            return;
        }

        PlayerState = status.Buffering
            ? "缓冲中"
            : status.Paused ? "已暂停" : status.Loaded ? "播放中" : "正在打开";
        Position = status.HasDuration
            ? $"{status.PositionClock} / {status.DurationClock}"
            : status.HasPosition ? status.PositionClock : "正在读取";
        NowPlaying = _nowPlaying is { } item ? item.ToPlaybackTitle() : "正在播放（项目标题尚未收到）";
    }

    private void OnNowPlaying(EmbyItem? item)
    {
        _nowPlaying = item;
        OnUi(() =>
        {
            if (!Attached) return;
            ReadPlayback(_playback!.Status);
            ReadMpv();
        });
    }

    private void OnStatus(PlayerStatus status) => OnUi(() => ReadPlayback(status));

    /// <summary>
    /// Onto the UI thread, and only for as long as the page is live. Checked twice on purpose: once before
    /// the hop, and again inside it, because a status change queued while the page was up can arrive after
    /// it has been navigated away from.
    /// </summary>
    private void OnUi(Action action)
    {
        if (!_live) return;
        _ui!.Run(() =>
        {
            if (_live) action();
        });
    }

    // ---- commands -----------------------------------------------------------

    /// <summary>
    /// Copies what is on screen, filters included: someone who narrowed the list to one category did so
    /// because that is the part they want to paste somewhere. Oldest first, the order a log is read in.
    /// </summary>
    [RelayCommand]
    private void Copy()
    {
        if (!Attached) return;

        if (Rows.Count == 0)
        {
            Announce("当前没有可复制的日志", InfoBarSeverity.Informational);
            return;
        }

        try
        {
            _clipboard!.SetText(string.Join(Environment.NewLine, Rows.Reverse().Select(row => row.FullText)));
            Announce($"已复制 {Rows.Count} 条日志", InfoBarSeverity.Success);
        }
        catch (Exception error)
        {
            Log.Warn(Category, "复制日志失败", error);
            Report("复制日志失败", error);
        }
    }

    [RelayCommand]
    private void OpenLogDirectory()
    {
        if (!Attached) return;

        try
        {
            _launcher!.OpenFolder(_paths!.LogDirectory);
        }
        catch (Exception error)
        {
            Log.Warn(Category, "打开日志目录失败", error);
            Report("打开日志目录失败", error);
        }
    }

    /// <summary>Back to 「全部」 on both filters, without three separate presses.</summary>
    [RelayCommand]
    private void ClearFilter()
    {
        SelectedCategory = AnyCategory;
        SelectedLevel = 0;
    }

    /// <summary>
    /// Success and information go to the shell's own bar when there is one, matching what the page did
    /// before: this page's InfoBar is where its own failures go, and a 已复制 message that stays until
    /// dismissed reads like a problem.
    /// </summary>
    private void Announce(string message, InfoBarSeverity severity)
    {
        if (_actions is not null) _actions.Notify(message, severity);
        else Notify(message, null, severity);
    }
}

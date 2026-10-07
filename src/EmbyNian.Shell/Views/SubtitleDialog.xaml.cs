using System.Collections.ObjectModel;
using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyNian.Shell.Views;

/// <summary>这个文件现在挂着的一条外挂字幕。公开的，因为模板里的 <c>x:Bind</c> 按类型名编译。</summary>
public sealed class SubtitleTrack(MediaStream stream)
{
    public MediaStream Stream { get; } = stream;

    public string Label { get; } = stream.ToDisplayLabel();
}

/// <summary>搜出来的一条候选字幕。</summary>
public sealed class SubtitleCandidate(RemoteSubtitleInfo info)
{
    public RemoteSubtitleInfo Info { get; } = info;

    public string Name { get; } = info.Name is { Length: > 0 } name ? name : "未命名的字幕";

    public string Detail { get; } = info.Describe();
}

/// <summary>
/// 字幕管理只持有固定文件的操作委托。写入结果确定、列表刷新结束后才允许关闭，
/// <see cref="Touched"/> 让调用方在关闭后重读页面。
/// </summary>
public sealed partial class SubtitleDialog : ContentDialog
{
    private const string Category = "ui";

    private static readonly (string Label, string Code)[] Languages =
    [
        ("中文", "chi"),
        ("英语", "eng"),
        ("日语", "jpn"),
        ("韩语", "kor")
    ];

    private readonly Func<string, Task<List<RemoteSubtitleInfo>>> _search;
    private readonly Func<RemoteSubtitleInfo, Task> _download;
    private readonly Func<MediaStream, Task> _delete;
    private readonly Func<Task<MediaSource>>? _reload;
    private string _sourceLabel;
    private SubtitleTrack? _pendingDelete;
    private Button? _deleteButton;
    private bool _deleting;
    private bool _busy;
    private bool _mutating;
    private bool _closed;
    private bool _tracksStale;

    private readonly ObservableCollection<SubtitleTrack> _tracks = [];
    private readonly ObservableCollection<SubtitleCandidate> _results = [];

    public SubtitleDialog(
        EmbyItem item,
        MediaSource source,
        Func<string, Task<List<RemoteSubtitleInfo>>> search,
        Func<RemoteSubtitleInfo, Task> download,
        Func<MediaStream, Task> delete,
        Func<Task<MediaSource>>? reload = null)
    {
        InitializeComponent();
        RequestedTheme = ThemeHost.Current.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        Title = $"搜索和修改字幕 — {item.Name}";
        _search = search;
        _download = download;
        _delete = delete;
        _reload = reload;
        _sourceLabel = ItemDetail.SourceLabel(source);
        RefreshTracksButton.Visibility = reload is null ? Visibility.Collapsed : Visibility.Visible;
        Closing += (_, args) =>
        {
            if (_mutating) args.Cancel = true;
            else
            {
                _closed = true;
                CancelDelete();
            }
        };

        Tracks.ItemsSource = _tracks;
        Results.ItemsSource = _results;
        FillTracks(source);
        foreach (var (label, code) in Languages)
            LanguageBox.Items.Add(new ComboBoxItem { Content = label, Tag = code });
        LanguageBox.SelectedIndex = 0;
    }

    public bool Touched { get; private set; }

    public bool NeedsRefresh { get; private set; }

    private void FillTracks(MediaSource source)
    {
        _sourceLabel = ItemDetail.SourceLabel(source);
        _tracks.Clear();
        foreach (var stream in source.SubtitleStreams.Where(stream => stream.IsExternal))
            _tracks.Add(new SubtitleTrack(stream));
        _tracksStale = false;
        ShowTracks();
    }

    private void ShowTracks()
    {
        Tracks.Visibility = _tracks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoTracks.Visibility = _tracks.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Say(string message)
    {
        if (_closed) return;
        Status.Text = message;
        Status.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetBusy(bool busy, bool mutating = false)
    {
        _busy = busy;
        _mutating = busy && mutating;
        SearchButton.IsEnabled = !busy;
        RefreshTracksButton.IsEnabled = !busy;
        LanguageBox.IsEnabled = !busy;
        Results.IsEnabled = !busy && !_tracksStale;
        Tracks.IsEnabled = !busy && !_tracksStale;
        ConfirmDeleteButton.IsEnabled = !busy && !_tracksStale;
        CancelDeleteButton.IsEnabled = !busy;
        Busy.IsActive = busy;
    }

    private async void OnSearch(object sender, RoutedEventArgs e)
    {
        if (_busy || _closed || LanguageBox.SelectedItem is not ComboBoxItem { Tag: string code }) return;
        CancelDelete();
        SetBusy(true);
        Say("正在搜索…");
        _results.Clear();
        try
        {
            var found = await _search(code).ConfigureAwait(true);
            if (_closed) return;
            foreach (var candidate in found) _results.Add(new SubtitleCandidate(candidate));
            Say(_results.Count > 0
                ? $"找到 {_results.Count} 条，点「下载」挂到这个文件上。"
                : "没有找到字幕。服务器上要装并启用字幕刮削插件（如 OpenSubtitles）才搜得到。");
        }
        catch (OperationCanceledException) { Say("登录身份已改变，请关闭后重新打开字幕管理。"); }
        catch (Exception error)
        {
            Log.Warn(Category, "搜索字幕失败", error);
            Say($"搜索字幕失败：{Failure.Describe(error)}");
        }
        finally { SetBusy(false); }
    }

    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        if (_busy || _closed || _tracksStale || sender is not Button { DataContext: SubtitleCandidate candidate }) return;
        CancelDelete();
        SetBusy(true, mutating: true);
        Say($"正在下载「{candidate.Name}」，完成后可关闭…");
        try
        {
            await _download(candidate.Info).ConfigureAwait(true);
            Touched = true;
            await RefreshAfterMutationAsync($"已挂上「{candidate.Name}」。关掉这张表之后就能在字幕里选它。");
        }
        catch (OperationCanceledException) { Say("登录身份已改变，请关闭后重新打开字幕管理。"); }
        catch (Exception error)
        {
            _tracksStale = _reload is not null;
            Log.Warn(Category, "下载字幕失败", error);
            Say($"下载字幕未完成确认：{Failure.Describe(error)}。请刷新列表后再操作。");
        }
        finally { SetBusy(false); }
    }

    private async void OnRefreshTracks(object sender, RoutedEventArgs e)
    {
        if (_busy || _closed || _reload is null) return;
        CancelDelete();
        SetBusy(true);
        try
        {
            await ReloadTracksAsync();
            Say("字幕列表已刷新。");
        }
        catch (OperationCanceledException) { Say("登录身份已改变，请关闭后重新打开字幕管理。"); }
        catch (Exception error)
        {
            _tracksStale = true;
            Log.Warn(Category, "刷新字幕列表失败", error);
            Say($"刷新字幕列表失败：{Failure.Describe(error)}。请重试刷新。");
        }
        finally { SetBusy(false); }
    }

    private async Task ReloadTracksAsync()
    {
        if (_reload is null) return;
        var source = await _reload().ConfigureAwait(true);
        if (!_closed)
        {
            NeedsRefresh = true;
            FillTracks(source);
        }
    }

    private async Task RefreshAfterMutationAsync(string success)
    {
        _tracksStale = _reload is not null;
        try
        {
            await ReloadTracksAsync();
            Say(success);
        }
        catch (Exception error)
        {
            Log.Warn(Category, "字幕操作完成后刷新失败", error);
            Say(success + " 列表未刷新，请先点「刷新字幕列表」；不要重复提交刚才的操作。");
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SubtitleTrack track } button) BeginDelete(track, button);
    }

    private void BeginDelete(SubtitleTrack track, Button? button = null)
    {
        if (_busy || _closed || _tracksStale || !_tracks.Contains(track)) return;
        CancelDelete();
        _pendingDelete = track;
        _deleteButton = button;
        if (button is not null) button.IsEnabled = false;
        // ContentDialog cannot open another ContentDialog on its UI thread.
        DeleteConfirmationText.Text = $"字幕：{track.Label}\n媒体文件：{_sourceLabel}\n"
            + "将永久删除服务器上的字幕文件，影响此媒体库中的其他用户；不是仅从本次播放移除，无法撤销。";
        DeleteConfirmation.Visibility = Visibility.Visible;
        CancelDeleteButton.Focus(FocusState.Programmatic);
    }

    private void OnCancelDelete(object sender, RoutedEventArgs e) => CancelDelete();

    private void CancelDelete()
    {
        if (_deleting) return;
        _pendingDelete = null;
        DeleteConfirmation.Visibility = Visibility.Collapsed;
        if (_deleteButton is { } button)
        {
            button.IsEnabled = !_busy && !_tracksStale;
            if (!_closed && button.XamlRoot is not null) button.Focus(FocusState.Programmatic);
        }
        _deleteButton = null;
    }

    private async void OnConfirmDelete(object sender, RoutedEventArgs e) => await DeleteConfirmedAsync();

    internal void ShowDeleteConfirmationForProbe()
    {
        if (App.Instance?.SubtitleProbeActive == true && _tracks.Count > 0) BeginDelete(_tracks[0]);
    }

    internal static async Task<(bool Ok, string Detail)> ProbeDeleteConfirmationAsync()
    {
        var stream = new MediaStream { Index = 1, Type = "Subtitle", Codec = "srt", IsExternal = true, Title = "离线字幕" };
        var source = new MediaSource { Id = "fixture", Name = "离线文件", MediaStreams = [stream] };
        var calls = 0;
        var fail = false;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new SubtitleDialog(new EmbyItem { Id = "fixture", Name = "离线夹具" }, source,
            _ => Task.FromResult(new List<RemoteSubtitleInfo>()), _ => Task.CompletedTask,
            _ => { calls++; return fail ? Task.FromException(new IOException("模拟失败")) : completion.Task; });
        var track = dialog._tracks[0];
        dialog.BeginDelete(track);
        var asked = calls == 0 && !dialog.Touched && dialog.DeleteConfirmation.Visibility == Visibility.Visible
            && dialog.DeleteConfirmationText.Text.Contains("无法撤销");
        dialog.CancelDelete();
        await dialog.DeleteConfirmedAsync();
        var cancelled = calls == 0 && dialog._tracks.Count == 1 && !dialog.Touched;
        fail = true;
        dialog.BeginDelete(track);
        await dialog.DeleteConfirmedAsync();
        var failed = calls == 1 && dialog._tracks.Count == 1 && !dialog.Touched && !dialog._deleting;
        fail = false;
        dialog.BeginDelete(track);
        var pending = dialog.DeleteConfirmedAsync();
        await dialog.DeleteConfirmedAsync();
        var once = calls == 2 && dialog._deleting;
        completion.SetResult();
        await pending;
        var deleted = dialog._tracks.Count == 0 && dialog.Touched && !dialog._deleting;
        return (asked && cancelled && failed && once && deleted,
            $"假委托：确认前零请求={asked}、取消={cancelled}、失败保留={failed}、重复点击只发一次={once}、确认后删除={deleted}");
    }

    private async Task DeleteConfirmedAsync()
    {
        if (_busy || _closed || _tracksStale || _pendingDelete is not { } track || !_tracks.Contains(track)) return;
        _deleting = true;
        SetBusy(true, mutating: true);
        try
        {
            await _delete(track.Stream).ConfigureAwait(true);
            _tracks.Remove(track);
            ShowTracks();
            Touched = true;
            await RefreshAfterMutationAsync($"已删除服务器字幕「{track.Label}」。");
        }
        catch (OperationCanceledException) { Say("登录身份已改变，请关闭后重新打开字幕管理。"); }
        catch (Exception error)
        {
            _tracksStale = _reload is not null;
            Log.Warn(Category, "删除字幕失败", error);
            Say($"删除字幕未完成确认：{Failure.Describe(error)}。请刷新列表后重新确认。");
        }
        finally
        {
            _deleting = false;
            SetBusy(false);
            CancelDelete();
        }
    }
}

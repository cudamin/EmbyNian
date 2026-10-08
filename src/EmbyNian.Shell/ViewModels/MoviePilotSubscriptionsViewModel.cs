using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbyNian.Infrastructure;
using EmbyNian.Emby;
using EmbyNian.MoviePilot;
using EmbyNian.Shell.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EmbyNian.Shell.ViewModels;

public sealed partial class MoviePilotSubscriptionCard : ObservableObject
{
    private CancellationTokenSource? _imageLoad;
    public MoviePilotSubscriptionCard(MoviePilotSubscription subscription) => Subscription = subscription;
    public MoviePilotSubscription Subscription { get; private set; }
    public string Title => Subscription.Title;
    public string Status => Subscription.StateLabel;
    public string Progress => Subscription.ProgressLabel;
    public string ProgressCount => Subscription.ProgressCount;
    public Visibility ProgressVisibility => Progress.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string Overview => Subscription.Media.Overview ?? "";
    public string Identity => Subscription.Media.Confirmation;
    public string Glyph => Subscription.IsSeries ? "\uE7F4" : "\uE8B2";
    public double CardWidth => CardSize.PosterWidth;
    public double PosterHeight => CardSize.HeightFor(CardSize.PosterWidth, false);
    public double RowHeight => PosterHeight + CardSize.Chrome;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyPosterVisibility))]
    public partial BitmapImage? Poster { get; set; }
    public Visibility EmptyPosterVisibility => Poster is null ? Visibility.Visible : Visibility.Collapsed;

    public void Update(MoviePilotSubscription subscription)
    {
        if (Subscription.Media.PosterUrl != subscription.Media.PosterUrl) Release();
        Subscription = subscription;
        OnPropertyChanged(string.Empty);
    }

    public async Task LoadPosterAsync(MoviePilotService service)
    {
        if (Poster is not null || _imageLoad is not null || !service.IsCurrentSubscription(Subscription)
            || !Uri.TryCreate(Subscription.Media.PosterUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https")) return;
        using var pending = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        _imageLoad = pending;
        try
        {
            var bytes = await service.FetchImageAsync(url.AbsoluteUri, pending.Token);
            var image = await PosterLoader.DecodeAsync(bytes, CardSize.PosterWidth);
            if (!pending.IsCancellationRequested && ReferenceEquals(_imageLoad, pending) && service.IsCurrentSubscription(Subscription)) Poster = image;
        }
        catch (Exception) { /* 缺封面仍保留可访问的标题和操作。 */ }
        finally { if (ReferenceEquals(_imageLoad, pending)) _imageLoad = null; }
    }

    public void Release() { _imageLoad?.Cancel(); _imageLoad = null; Poster = null; }
}

public sealed partial class MoviePilotSubscriptionsViewModel : PageViewModel
{
    private MoviePilotService? _service;
    private CancellationTokenSource? _lifetime;
    private string _connection = "";
    private bool _detail;
    private bool Live => _lifetime is { IsCancellationRequested: false } && _service?.IsCurrentConnection(_connection) == true;
    private IReadOnlyList<MoviePilotSubscriptionEpisodeRow> _episodes = [];
    private EmbySessionScope? _playbackScope;
    private Func<MoviePilotPlaybackTarget, Task>? _play;

    public ObservableCollection<MoviePilotSubscriptionCard> Movies { get; } = [];
    public ObservableCollection<MoviePilotSubscriptionCard> Series { get; } = [];
    public ObservableCollection<MoviePilotSubscriptionEpisodeRow> Episodes { get; } = [];
    public string MoviesNote => Movies.Count == 0 ? "暂无电影订阅" : $"{Movies.Count} 部电影";
    public string SeriesNote => Series.Count == 0 ? "暂无电视剧订阅" : $"{Series.Count} 条订阅";
    public Visibility Visible => Show(_service?.Enabled == true);
    public Visibility MoviesVisibility => Show(Movies.Count > 0);
    public Visibility SeriesVisibility => Show(Series.Count > 0);
    public double RowHeight => CardSize.HeightFor(CardSize.PosterWidth, false) + CardSize.Chrome;

    [ObservableProperty]
    public partial MoviePilotSubscriptionCard? Selected { get; set; }
    public string DetailTitle => Selected?.Title ?? "订阅媒体详情";
    public string DetailStatus => Selected?.Status ?? "";
    public string DetailIdentity => Selected?.Identity ?? "";
    public string DetailOverview => Selected?.Overview ?? "";
    public BitmapImage? DetailPoster => Selected?.Poster;
    partial void OnSelectedChanged(MoviePilotSubscriptionCard? oldValue, MoviePilotSubscriptionCard? newValue)
    {
        if (oldValue is not null) oldValue.PropertyChanged -= SelectedChanged;
        if (newValue is not null) newValue.PropertyChanged += SelectedChanged;
        SelectedChanged(null, null);
    }
    private void SelectedChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs? args)
    {
        OnPropertyChanged(nameof(DetailTitle)); OnPropertyChanged(nameof(DetailStatus));
        OnPropertyChanged(nameof(DetailIdentity)); OnPropertyChanged(nameof(DetailOverview)); OnPropertyChanged(nameof(DetailPoster));
    }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanManage))]
    public partial bool Working { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanManage))]
    public partial bool Deleted { get; set; }
    [ObservableProperty]
    public partial string FileSummary { get; set; } = "正在读取文件统计…";
    [ObservableProperty]
    public partial string PlaybackStatus { get; set; } = "";
    [ObservableProperty]
    public partial int FilterIndex { get; set; }
    public bool CanManage => Live && !Working && !Busy && !Deleted;
    partial void OnFilterIndexChanged(int value) => ApplyFilter();
    protected override void BusyChanged() { OnPropertyChanged(nameof(CanManage)); RefreshPlayback(); }
    partial void OnWorkingChanged(bool value) => RefreshPlayback();
    partial void OnDeletedChanged(bool value) => RefreshPlayback();
    private void RefreshPlayback() { foreach (var row in _episodes) row.Refresh(); }

    internal void AttachPlayback(EmbySessionScope? scope, Func<MoviePilotPlaybackTarget, Task>? play)
    { _playbackScope = scope; _play = play; }

    internal void Attach(MoviePilotService service, MoviePilotSubscription? selected = null)
    {
        Cancel();
        _service = service;
        _detail = selected is not null;
        _lifetime = new CancellationTokenSource();
        Deleted = false;
        Selected = selected is null ? null : new(selected);
        try { _connection = selected?.ConnectionStamp ?? service.ConnectionStamp; }
        catch (MoviePilotException error) { Report("MoviePilot 未连接", error); }
        OnPropertyChanged(nameof(Visible));
    }

    public override async Task ReloadAsync()
    {
        if (Working || _service is null || _lifetime is not { IsCancellationRequested: false }) return;
        OnPropertyChanged(nameof(Visible));
        if (!_service.Enabled) { ClearCards(); OnPropertyChanged(nameof(CanManage)); return; }
        var token = BeginLoad();
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        try
        {
            if (!Live)
            {
                ClearCards();
                if (_detail) throw new MoviePilotException("MoviePilot 连接已改变，请返回主页重新打开订阅");
                _connection = _service.ConnectionStamp;
            }
            if (_detail && Selected is { } selected)
            {
                var current = await _service.SubscriptionAsync(selected.Subscription, pending.Token);
                if (!IsCurrent(token) || !Live) return;
                selected.Update(current);
                _ = selected.LoadPosterAsync(_service);
                foreach (var row in _episodes) row.Retire();
                _episodes = [];
                Episodes.Clear();
                PlaybackStatus = "";
                FileSummary = "正在读取文件统计…";
                var files = await _service.SubscriptionFilesAsync(current, pending.Token);
                if (!IsCurrent(token) || !Live) return;
                _episodes = files.Episodes.Select(episode => new MoviePilotSubscriptionEpisodeRow(episode,
                    () => CanManage && _playbackScope?.IsCurrent == true && _play is not null, PlayEpisodeAsync)).ToArray();
                FileSummary = current.IsSeries ? files.Summary
                    : files.Episodes.Count == 0 ? files.Summary : files.LibraryCount > 0 ? "电影已入库" : "电影尚未入库";
                ApplyFilter();
                await LoadPlaybackAsync(current, files, token, pending.Token);
            }
            else
            {
                var items = await _service.SubscriptionsAsync(pending.Token);
                if (!IsCurrent(token) || !Live) return;
                Apply(Movies, items.Where(item => !item.IsSeries).ToArray());
                Apply(Series, items.Where(item => item.IsSeries).ToArray());
                NotifyRows();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (IsCurrent(token))
            {
                if (_detail) FileSummary = "文件统计未读取成功，不能据此判断是否入库";
                Report("读取 MoviePilot 订阅失败", error);
            }
        }
        finally { EndLoad(token); OnPropertyChanged(nameof(CanManage)); }
    }

    private static void Apply(ObservableCollection<MoviePilotSubscriptionCard> cards, IReadOnlyList<MoviePilotSubscription> items)
    {
        foreach (var old in cards.Where(card => items.All(item => item.Id != card.Subscription.Id)).ToArray())
        { old.Release(); cards.Remove(old); }
        for (var index = 0; index < items.Count; index++)
        {
            var existing = cards.FirstOrDefault(card => card.Subscription.Id == items[index].Id);
            if (existing is null) cards.Insert(index, new(items[index]));
            else { existing.Update(items[index]); if (cards.IndexOf(existing) != index) cards.Move(cards.IndexOf(existing), index); }
        }
    }

    public bool Owns(MoviePilotSubscriptionCard card) => Live && (_detail ? ReferenceEquals(Selected, card) : Movies.Contains(card) || Series.Contains(card));
    public bool NeedsReview(MoviePilotSubscriptionCard card) => _service?.SubscriptionNeedsReview(card.Subscription) == true;
    public Task LoadPosterAsync(MoviePilotSubscriptionCard card) => Owns(card) && _service is { } service ? card.LoadPosterAsync(service) : Task.CompletedTask;
    public Task LoadEpisodeArtworkAsync(MoviePilotSubscriptionEpisodeRow row) => Live && _episodes.Contains(row)
        && _service is { } service && _lifetime is { } lifetime ? row.LoadArtworkAsync(service, lifetime.Token) : Task.CompletedTask;

    internal async Task ReviewAsync(MoviePilotSubscriptionCard card)
    {
        if (!CanManage || !Owns(card) || _service is null || _lifetime is null) return;
        var token = _lifetime.Token;
        Working = true;
        try
        {
            if (await ConfirmAsync("核对订阅操作", card.Identity + "\n\n上次操作的结果不明确。请先核对 MoviePilot 中的订阅状态和下载任务，确认不会重复执行后再继续。", "已核对，允许继续")
                && !token.IsCancellationRequested && Owns(card))
            {
                _service.ConfirmSubscriptionReviewed(card.Subscription);
                ClearNotice();
            }
        }
        finally { if (!token.IsCancellationRequested) Working = false; }
    }

    public async Task ManageAsync(MoviePilotSubscriptionCard card, MoviePilotSubscriptionAction action)
    {
        if (!CanManage || !Owns(card) || _service is null || _lifetime is null) return;
        var subscription = card.Subscription;
        var token = _lifetime.Token;
        var verb = action switch
        {
            MoviePilotSubscriptionAction.Pause => "暂停订阅",
            MoviePilotSubscriptionAction.Resume => "恢复订阅",
            MoviePilotSubscriptionAction.Search => "搜索订阅",
            MoviePilotSubscriptionAction.Reset => "重置订阅",
            _ => "取消订阅"
        };
        var note = action switch
        {
            MoviePilotSubscriptionAction.Delete => "将取消这条 MoviePilot 订阅，已入库文件保留。",
            MoviePilotSubscriptionAction.Reset => "将重置订阅进度并重新处理，可能触发下载。",
            MoviePilotSubscriptionAction.Search => "将立即搜索这条订阅，并可能自动加入下载。",
            MoviePilotSubscriptionAction.Resume => "恢复后 MoviePilot 将继续处理这条订阅。",
            _ => "暂停后 MoviePilot 将停止处理这条订阅。"
        };
        Working = true;
        var changed = false;
        try
        {
            if (!await ConfirmAsync(verb, subscription.Media.Confirmation + "\n\n" + note, verb)) return;
            if (token.IsCancellationRequested || !Owns(card)) return;
            await _service.ManageSubscriptionAsync(subscription, action, token);
            if (token.IsCancellationRequested || !Owns(card)) return;
            changed = true;
            if (action == MoviePilotSubscriptionAction.Delete)
            {
                Deleted = _detail;
                if (!_detail) { card.Release(); Movies.Remove(card); Series.Remove(card); }
            }
            Notify(verb, action == MoviePilotSubscriptionAction.Search ? "搜索请求已提交，可稍后刷新查看进度。" : "MoviePilot 已确认操作成功。", InfoBarSeverity.Success);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!token.IsCancellationRequested && Live) Report(verb, error); }
        finally { if (!token.IsCancellationRequested) Working = false; }
        if (changed && !Deleted && Live && action != MoviePilotSubscriptionAction.Search) await ReloadAsync();
        NotifyRows();
    }

    internal async Task EditAsync(MoviePilotSubscriptionCard card, Func<MoviePilotSubscription, Task<MoviePilotSubscriptionEdit?>> edit)
    {
        if (!CanManage || !Owns(card) || _service is null || _lifetime is null) return;
        var token = _lifetime.Token;
        Working = true;
        var changed = false;
        try
        {
            var original = await _service.SubscriptionAsync(card.Subscription, token);
            if (token.IsCancellationRequested || !Owns(card)) return;
            var draft = await edit(original);
            if (draft is null || token.IsCancellationRequested || !Owns(card)) return;
            await _service.UpdateSubscriptionAsync(original, draft, token);
            changed = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!token.IsCancellationRequested && Live) Report("保存订阅失败", error); }
        finally { if (!token.IsCancellationRequested) Working = false; }
        if (changed && Live) await ReloadAsync();
    }

    private void ApplyFilter()
    {
        Episodes.Clear();
        foreach (var episode in _episodes.Where(episode => FilterIndex switch { 1 => episode.InLibrary, 2 => !episode.InLibrary, _ => true })) Episodes.Add(episode);
    }

    private async Task LoadPlaybackAsync(MoviePilotSubscription subscription, MoviePilotSubscriptionFiles files,
        CancellationToken load, CancellationToken cancellationToken)
    {
        PlaybackStatus = files.LibraryCount == 0 ? "" : "正在匹配当前 Emby 的播放条目…";
        if (files.LibraryCount == 0) return;
        if (_playbackScope is not { IsCurrent: true } scope || _play is null)
        { PlaybackStatus = "连接对应的 Emby 服务器后即可播放已入库内容。"; return; }
        try
        {
            var targets = await scope.ExecuteAsync((client, token) => MoviePilotLibraryPlayback.FindAsync(client, subscription, files, token), cancellationToken);
            if (!IsCurrent(load) || !Live || !scope.IsCurrent) return;
            foreach (var row in _episodes) row.SetTarget(targets.GetValueOrDefault(row.Episode.Number));
            PlaybackStatus = $"当前 Emby 可播放 {targets.Count} / {files.LibraryCount} 项";
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        { if (IsCurrent(load) && Live) PlaybackStatus = "播放条目匹配失败，请刷新重试；文件统计仍保留。"; }
    }

    private async Task PlayEpisodeAsync(MoviePilotSubscriptionEpisodeRow row)
    {
        if (!row.CanPlay || !_episodes.Contains(row) || row.Target is not { } target || _play is null || _lifetime is null) return;
        var token = _lifetime.Token;
        Working = true;
        try
        {
            if (!token.IsCancellationRequested && Live && _playbackScope?.IsCurrent == true) await _play(target);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!token.IsCancellationRequested && Live) Report("播放失败", error); }
        finally { if (!token.IsCancellationRequested) Working = false; }
    }

    private void ClearCards()
    {
        foreach (var card in Movies.Concat(Series)) card.Release();
        Movies.Clear(); Series.Clear();
        Selected?.Release();
        NotifyRows();
    }

    private void NotifyRows()
    {
        OnPropertyChanged(nameof(MoviesNote)); OnPropertyChanged(nameof(SeriesNote));
        OnPropertyChanged(nameof(MoviesVisibility)); OnPropertyChanged(nameof(SeriesVisibility));
    }

    [RelayCommand]
    private Task RefreshAsync() => ReloadAsync();

    public override void Cancel()
    {
        _lifetime?.Cancel(); _lifetime?.Dispose(); _lifetime = null;
        _playbackScope = null; _play = null;
        foreach (var row in _episodes) row.Retire();
        _episodes = []; Episodes.Clear();
        Working = false;
        ClearCards();
        base.Cancel();
        OnPropertyChanged(nameof(CanManage));
    }
}

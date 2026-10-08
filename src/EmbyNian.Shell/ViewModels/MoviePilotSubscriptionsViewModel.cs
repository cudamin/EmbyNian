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

    /// <summary>
    /// 整块板块（两个标题、两条空态说明、刷新键）在不在屏上。
    /// <para>
    /// **这一条曾经只认 <c>_service.Enabled</c>**：MoviePilot 一开着，<see cref="Attach"/> 和
    /// <see cref="ReloadAsync"/> 就在数据到达之前把整块摆上屏，屏上先是「MoviePilot订阅电影／暂无电影订阅」＋
    /// 「MoviePilot订阅电视剧／暂无电视剧订阅」两块空牌子，等到那一次异步请求回来才填进卡片 —— 开应用和
    /// 从播放返回主页都会重走一遍，用户看到的就是订阅板块闪一下（2026-10-08「打开应用和返回主页的时候会显示
    /// MoviePilot订阅电影和电影然后再播放ui动画」）。
    /// </para>
    /// <para>
    /// 改成**真有内容才显示**：电影或电视剧至少排出一张卡，整块才出现，出现时就是最终样子，没有「先空后满」那
    /// 一帧。一条订阅都没有的账号本来看不到这块，刷新键和空态说明都不必存在 —— 没有内容时它们也点不出东西。
    /// </para>
    /// <para>
    /// <b>失败也要立着</b>：这一块里唯一的报错出口是那条 <c>InfoBar</c>，而它就在这一块里面。「读订阅失败」
    /// 一旦真的发生，多半正是一条订阅都没排出来的那一趟 —— 只看卡片的话，用户看到的就是「主页上什么都没少，
    /// 也没人告诉他出了什么事」。所以提示条开着的时候整块一律显出来（那时两块空牌子是应该看见的：它们在说
    /// 「这里本来该有内容」）。
    /// </para>
    /// <para>
    /// 明细模式下（从主页点订阅卡进来）这一块本来就不在主页上，<see cref="Visible"/> 与它无关。
    /// </para>
    /// </summary>
    public Visibility Visible => Show(Movies.Count > 0 || Series.Count > 0 || NoticeOpen);
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

    public MoviePilotSubscriptionsViewModel() =>
        // Visible 也看 NoticeOpen（见它的说明），而那条属性住在基类上 —— 生成器的 On…Changed 只长在声明它的
        // 那个类里，这里够不着，就听 PropertyChanged 这一路。提示条一开一关，整块板块跟着立起来或塌下去。
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(NoticeOpen)) OnPropertyChanged(nameof(Visible));
        };

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

        // 进这一页/返回主页时先把上一批卡片收干净，整块板块跟着塌下来；真的取到订阅时再由 NotifyRows 立起来。
        // 不能在这里凭 Enabled 就把整块摆上屏 —— 那正是「先看到两块空牌子」的那一帧（见 Visible）。
        ClearCards();
    }

    public override async Task ReloadAsync()
    {
        if (Working || _service is null || _lifetime is not { IsCancellationRequested: false }) return;
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
        // Visible 是 Movies/Series 的派生读数（见它的说明），卡片一增一减这里必须一起报 —— 漏掉它的症状是
        // 「数据回来了，板块却不出现」，比闪现更难查。
        OnPropertyChanged(nameof(Visible));
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

        // 离页/换连接时把上一次的报错一起收掉：那条提示条也在 Visible 的判据里（见它），留着就是「返回主页
        // 又弹出一块已经过时的空板块」。这一句放在 ClearCards 前面，收起卡片那一下按的是同一条新判据。
        ClearNotice();
        ClearCards();
        base.Cancel();
        OnPropertyChanged(nameof(CanManage));
    }
}

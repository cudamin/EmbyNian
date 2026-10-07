using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbyNian.Diagnostics;
using EmbyNian.Infrastructure;
using EmbyNian.MoviePilot;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EmbyNian.Shell.ViewModels;

/// <summary>一条结果的订阅走到哪了。四档，因为那颗键要说四种话，而按下去能不能按也跟着它走。</summary>
public enum MoviePilotSubscribeState
{
    Idle,
    Working,
    Done,
    Failed,
    Uncertain
}

/// <summary>
/// 搜索页 MoviePilot 那一片里的一张卡：包一条 <see cref="MoviePilotMedia"/>，加上海报和那颗订阅键要的东西。
/// <para>
/// 仿 <see cref="Views.CardItem"/>／<see cref="FilterChipRow"/>：Core 的模型是「这是什么」，这里补的只有
/// <c>x:Bind</c> 要的可见性和命令。订阅逻辑不在这儿（它要服务和确认对话框，都不归一张卡管），而是把动作回调给
/// 视图模型 —— 同 <see cref="FilterChipRow"/> 收一个 <c>drop</c> 委托的做法。
/// </para>
/// </summary>
public sealed partial class MoviePilotResult : ObservableObject
{
    /// <summary>海报解码宽度：卡片画得比这个小，解到这个宽够清楚又不费内存。</summary>
    private const int PosterDecodeWidth = 240;

    private readonly Func<MoviePilotResult, Task> _subscribe;
    private readonly Func<MoviePilotResult, Task> _searchResources;

    public MoviePilotResult(
        MoviePilotMedia media,
        Func<MoviePilotResult, Task> subscribe,
        Func<MoviePilotResult, Task> searchResources)
    {
        Media = media;
        _subscribe = subscribe;
        _searchResources = searchResources;

        // 海报是外站 URL（TMDB/豆瓣的图，或 MoviePilot 代理的），直接交给 BitmapImage 下载 —— 这一处不走
        // EmbyImageStore，那是 Emby 会话域的。只认绝对 http(s)：相对路径或空的就留空，卡片显示占位字形而不是抛。
        if (Uri.TryCreate(media.PosterUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            Poster = new BitmapImage(uri) { DecodePixelWidth = PosterDecodeWidth };
    }

    public MoviePilotMedia Media { get; }

    public string Title => Media.Display;

    public string TypeLabel => Media.Type ?? "";

    public string SourceLabel => Media.SourceLabel;

    public Visibility SourceVisibility => SourceLabel.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>类型和来源那一行的可见性：两样都没有就不留一行空的。</summary>
    public Visibility IdentityVisibility => SourceLabel.Length > 0 || Media.Type is { Length: > 0 }
        ? Visibility.Visible : Visibility.Collapsed;

    public Visibility TypeVisibility => Media.Type is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

    public string Overview => Media.Overview ?? "";

    public Visibility OverviewVisibility =>
        string.IsNullOrWhiteSpace(Media.Overview) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>海报，取不到就是 null；这时显示字形占位（见下面两个可见性）。构造时定好，不再变。</summary>
    public BitmapImage? Poster { get; }

    public Visibility PosterVisibility => Poster is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility GlyphVisibility => Poster is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>订阅到哪一步。改了之后那颗键的字、能不能按都跟着变。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubscribeLabel))]
    [NotifyPropertyChangedFor(nameof(CanSubscribe))]
    [NotifyCanExecuteChangedFor(nameof(SubscribeCommand))]
    public partial MoviePilotSubscribeState State { get; set; }

    /// <summary>能不能订阅：身份对齐、且此刻不是订阅中或已订阅。失败了允许再点一次。</summary>
    public bool CanSubscribe =>
        Media.CanSubscribe && State is MoviePilotSubscribeState.Idle or MoviePilotSubscribeState.Failed;

    public string SubscribeLabel => State switch
    {
        MoviePilotSubscribeState.Working => "订阅中…",
        MoviePilotSubscribeState.Done => "已订阅",
        MoviePilotSubscribeState.Failed => "重试订阅",
        MoviePilotSubscribeState.Uncertain => "请先核对",
        _ => "订阅"
    };

    internal static MoviePilotSubscribeState StateOf(MoviePilotOperationState state) => state switch
    {
        MoviePilotOperationState.Working => MoviePilotSubscribeState.Working,
        MoviePilotOperationState.Submitted => MoviePilotSubscribeState.Done,
        MoviePilotOperationState.Uncertain => MoviePilotSubscribeState.Uncertain,
        _ => MoviePilotSubscribeState.Idle
    };

    /// <summary>读屏软件念出来的那一句：光一个「订阅」念不出订的是哪一部；来源对了才不至于订错库。</summary>
    public string SubscribeAutomationName => SourceLabel.Length > 0
        ? $"订阅（{SourceLabel}）：{Media.Title}" : $"订阅：{Media.Title}";

    /// <summary>同上，给「搜索资源」那颗键。</summary>
    public string SearchResourcesAutomationName => SourceLabel.Length > 0
        ? $"搜索资源（{SourceLabel}）：{Media.Title}" : $"搜索资源：{Media.Title}";

    [RelayCommand(CanExecute = nameof(CanSubscribe))]
    private Task Subscribe() => _subscribe(this);

    /// <summary>能不能搜资源：和订阅同一个条件（都要身份对齐）。</summary>
    public bool CanSearchResources => Media.CanSubscribe;

    [RelayCommand(CanExecute = nameof(CanSearchResources))]
    private Task SearchResources() => _searchResources(this);
}

/// <summary>
/// 搜索页里 MoviePilot 那一段的视图模型：一个搜索词进来，去 MoviePilot 找片、把结果铺成卡片，并把每张卡的
/// 「订阅」接到二次确认加下单上。
/// <para>
/// 和 Emby 那半（<see cref="LibraryViewModel"/>）分开，不塞进去：两类结果性质不同 —— 那半是库里的
/// <c>EmbyItem</c> 网格（查询/翻页/排序/筛选），这半是「库里还没有、可去订阅」的片子。共用的只有
/// <see cref="PageViewModel"/> 的忙/通知条/载入代次。页面用分段切换在两半之间切显隐。
/// </para>
/// </summary>
public sealed partial class MoviePilotSearchViewModel : PageViewModel
{
    private const string Category = "moviepilot";

    /// <summary>还没搜时页面中间那句话。</summary>
    private const string IdlePrompt = "输入关键字，在 MoviePilot 上找库里还没有的片子";

    /// <summary>来源下拉的第一条哨兵：Id 为空表示不加 media_source，全来源都搜。</summary>
    internal static readonly MoviePilotMediaSource AllSources = new("全部来源", "", []);

    /// <summary>media/source 问不到时的兜底清单 —— 内置影视来源，别让来源筛选整个消失。</summary>
    private static readonly IReadOnlyList<MoviePilotMediaSource> FallbackSources =
    [
        new("TMDB", "themoviedb", ["电影", "电视剧"]),
        new("豆瓣", "douban", ["电影", "电视剧"]),
        new("IMDb", "imdb", ["电影", "电视剧"]),
        new("Bangumi", "bangumi", ["电视剧"]),
        new("AniList", "anilist", ["电视剧"]),
        new("TVDB", "tvdb", ["电视剧"])
    ];

    private MoviePilotService? _service;
    private readonly LoadGeneration _sourceLoads = new();
    private CancellationToken _query;

    /// <summary>上一次搜的词，供 <see cref="ReloadAsync"/> 重跑。</summary>
    private string _lastTerm = "";

    /// <summary>「搜资源」交给视图去开那层覆盖面板（要 XamlRoot），视图模型只管把媒体递出去。</summary>
    private Func<MoviePilotMedia, Task>? _openResources;

    public MoviePilotSearchViewModel()
    {
        EmptyNotice = IdlePrompt;
        ShowEmptyNotice = true;
    }

    public ObservableCollection<MoviePilotResult> Results { get; } = [];

    /// <summary>来源下拉：全部来源打底，其余按 media/source 的目录来（只留影视可用的）。</summary>
    public ObservableCollection<MoviePilotMediaSource> SourceChoices { get; } = [AllSources];

    [ObservableProperty]
    public partial MoviePilotMediaSource SelectedSource { get; set; } = AllSources;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyVisibility))]
    public partial bool ShowEmptyNotice { get; set; }

    public Visibility EmptyVisibility => Show(ShowEmptyNotice);

    [ObservableProperty]
    public partial string EmptyNotice { get; set; }

    internal void Attach(MoviePilotService service) => _service = service;

    /// <summary>
    /// 来源目录装进下拉：media/source 现问一遍、只留影视可用的（<see cref="MoviePilotMediaSource.IsVideo"/>）；
    /// 问不到（没连上、接口不在）就退到内置清单。第一项「全部来源」永远在。
    /// </summary>
    internal async Task LoadSourcesAsync()
    {
        var token = _sourceLoads.Begin();
        if (_service is null || !_service.Enabled) return;

        IReadOnlyList<MoviePilotMediaSource> sources;
        try
        {
            sources = await _service.MediaSourcesAsync(token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception error)
        {
            if (!_sourceLoads.IsCurrent(token)) return;
            Log.Warn(Category, "读取 MoviePilot 来源目录失败，退到内置来源", error);
            sources = FallbackSources;
        }
        if (!_sourceLoads.IsCurrent(token)) return;
        var choices = sources.Where(source => source.IsVideo)
            .DistinctBy(source => source.Id, StringComparer.OrdinalIgnoreCase).ToList();

        SourceChoices.Clear();
        SourceChoices.Add(AllSources);
        foreach (var source in choices) SourceChoices.Add(source);
        SelectedSource = AllSources;
    }

    /// <summary>换来源筛选：有上一次的词就重搜一遍，没有就只记住选择，下次搜索时生效。</summary>
    internal Task ApplySourceFilterAsync() =>
        SelectedSource is not null && _lastTerm.Length > 0 ? SearchAsync(_lastTerm) : Task.CompletedTask;

    /// <summary>这次搜索带不带来源筛选：选了具体来源就只搜那一家。</summary>
    private IReadOnlyList<string>? Sources =>
        SelectedSource is { Id: { Length: > 0 } id } ? [id] : null;

    /// <summary>视图交给它的「开资源面板」入口，见 <see cref="SearchResourcesAsync"/>。</summary>
    internal void UseResourceOpener(Func<MoviePilotMedia, Task> open) => _openResources = open;

    /// <summary>外壳刷新会调这一条；这一页的刷新就是把上一次的词重搜一遍。</summary>
    public override Task ReloadAsync() => SearchAsync(_lastTerm);

    /// <summary>搜一个词。空词回到「还没搜」的样子，不发请求。结果整片重填（集合实例保持稳定）。</summary>
    internal async Task SearchAsync(string term)
    {
        if (_service is null) return;

        var keyword = term.Trim();
        _lastTerm = keyword;

        if (keyword.Length == 0)
        {
            base.Cancel();
            ClearNotice();
            IsReady = true;
            Results.Clear();
            EmptyNotice = IdlePrompt;
            ShowEmptyNotice = true;
            return;
        }

        var token = BeginLoad();
        _query = token;
        Results.Clear();
        ShowEmptyNotice = false;

        try
        {
            var found = await _service.SearchAsync(keyword, token, Sources).ConfigureAwait(true);
            if (!IsCurrent(token)) return;

            Results.Clear();
            foreach (var media in found)
                Results.Add(new MoviePilotResult(media, SubscribeAsync, SearchResourcesAsync)
                {
                    State = MoviePilotResult.StateOf(_service.SubscribeState(media))
                });

            EmptyNotice = found.Count == 0 ? $"MoviePilot 上没搜到「{keyword}」" : IdlePrompt;
            ShowEmptyNotice = found.Count == 0;
            EndLoad(token);
        }
        catch (OperationCanceledException)
        {
            EndLoad(token);
        }
        catch (Exception error)
        {
            if (!IsCurrent(token)) return;

            Log.Warn(Category, $"MoviePilot 搜「{keyword}」失败", error);
            Report("搜索 MoviePilot 失败", error);
            Results.Clear();
            ShowEmptyNotice = false;
            EndLoad(token);
        }
    }

    /// <summary>
    /// 一张卡的「订阅」按下去：先二次确认（这是对外副作用 —— 会在用户的 MoviePilot 上真的建订阅、可能触发
    /// 下载），确认了才下单。成功置「已订阅」并在通知条报一句；失败置「重试订阅」，那句话已是给人看的。
    /// </summary>
    private async Task SubscribeAsync(MoviePilotResult result)
    {
        if (_service is null || !result.CanSubscribe || !IsCurrent(_query)) return;
        var token = _query;
        var confirmed = await ConfirmAsync(
            "订阅到 MoviePilot",
            $"确认在 MoviePilot 上订阅以下媒体吗？\n{result.Media.Confirmation}\n它会去找资源、可能开始下载。",
            "订阅").ConfigureAwait(true);
        if (!confirmed || !IsCurrent(token)) return;

        result.State = MoviePilotSubscribeState.Working;
        try
        {
            await _service.SubscribeAsync(result.Media, token).ConfigureAwait(true);
            if (!IsCurrent(token)) return;
            result.State = MoviePilotSubscribeState.Done;
            Notify(null, $"已在 MoviePilot 订阅《{result.Media.Title}》", InfoBarSeverity.Success);
        }
        catch (Exception error)
        {
            if (!IsCurrent(token)) return;
            result.State = error is MoviePilotOperationUncertainException or MoviePilotOperationBlockedException
                ? MoviePilotSubscribeState.Uncertain : MoviePilotSubscribeState.Failed;
            Log.Warn(Category, $"订阅《{result.Media.Title}》未完成", error);
            Report($"订阅《{result.Media.Title}》未完成", error);
        }
    }

    public override void Cancel()
    {
        _sourceLoads.Cancel();
        base.Cancel();
    }

    public override void Dispose()
    {
        _sourceLoads.Dispose();
        base.Dispose();
    }

    /// <summary>一张卡的「搜索资源」按下去：交给视图去开资源覆盖面板（那要 XamlRoot），视图模型只把媒体递出去。</summary>
    private Task SearchResourcesAsync(MoviePilotResult result) =>
        _openResources?.Invoke(result.Media) ?? Task.CompletedTask;
}

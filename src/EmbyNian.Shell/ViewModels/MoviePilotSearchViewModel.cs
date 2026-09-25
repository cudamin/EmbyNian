using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbyNian.Diagnostics;
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
    Failed
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
        _ => "订阅"
    };

    /// <summary>读屏软件念出来的那一句：光一个「订阅」念不出订的是哪一部。</summary>
    public string SubscribeAutomationName => $"订阅：{Media.Title}";

    /// <summary>同上，给「搜索资源」那颗键。</summary>
    public string SearchResourcesAutomationName => $"搜索资源：{Media.Title}";

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

    private MoviePilotService? _service;

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyVisibility))]
    public partial bool ShowEmptyNotice { get; set; }

    public Visibility EmptyVisibility => Show(ShowEmptyNotice);

    [ObservableProperty]
    public partial string EmptyNotice { get; set; }

    internal void Attach(MoviePilotService service) => _service = service;

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
            Results.Clear();
            EmptyNotice = IdlePrompt;
            ShowEmptyNotice = true;
            return;
        }

        var token = BeginLoad();
        ShowEmptyNotice = false;

        try
        {
            var found = await _service.SearchAsync(keyword, token).ConfigureAwait(true);
            if (!IsCurrent(token)) return;

            Results.Clear();
            foreach (var media in found)
                Results.Add(new MoviePilotResult(media, SubscribeAsync, SearchResourcesAsync));

            EmptyNotice = found.Count == 0 ? $"MoviePilot 上没搜到「{keyword}」" : IdlePrompt;
            ShowEmptyNotice = found.Count == 0;
            EndLoad(token);
        }
        catch (OperationCanceledException)
        {
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
        if (_service is null || result.State == MoviePilotSubscribeState.Working) return;

        var confirmed = await ConfirmAsync(
            "订阅到 MoviePilot",
            $"确认在 MoviePilot 上订阅《{result.Media.Title}》吗？它会去找资源、可能开始下载。",
            "订阅").ConfigureAwait(true);
        if (!confirmed) return;

        result.State = MoviePilotSubscribeState.Working;
        try
        {
            await _service.SubscribeAsync(result.Media, CancellationToken.None).ConfigureAwait(true);
            result.State = MoviePilotSubscribeState.Done;
            Notify(null, $"已在 MoviePilot 订阅《{result.Media.Title}》", InfoBarSeverity.Success);
        }
        catch (Exception error)
        {
            result.State = MoviePilotSubscribeState.Failed;
            Log.Warn(Category, $"订阅《{result.Media.Title}》失败", error);
            Report($"订阅《{result.Media.Title}》失败", error);
        }
    }

    /// <summary>一张卡的「搜索资源」按下去：交给视图去开资源覆盖面板（那要 XamlRoot），视图模型只把媒体递出去。</summary>
    private Task SearchResourcesAsync(MoviePilotResult result) =>
        _openResources?.Invoke(result.Media) ?? Task.CompletedTask;
}

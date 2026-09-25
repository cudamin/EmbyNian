using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbyNian.Diagnostics;
using EmbyNian.MoviePilot;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyNian.Shell.ViewModels;

/// <summary>一条资源的下载走到哪了。四档，那颗键要说四种话，能不能按也跟着它走。</summary>
public enum MoviePilotDownloadState
{
    Idle,
    Working,
    Done,
    Failed
}

/// <summary>
/// 资源列表里的一行（一个种子）：包一条 <see cref="MoviePilotResource"/>，加上下载键要的可见性和命令。
/// 下载逻辑不在这儿（要服务和确认），回调给视图模型 —— 同 <see cref="MoviePilotResult"/> 的路子。
/// </summary>
public sealed partial class MoviePilotResourceRow : ObservableObject
{
    private readonly Func<MoviePilotResourceRow, Task> _download;

    public MoviePilotResourceRow(MoviePilotResource resource, Func<MoviePilotResourceRow, Task> download)
    {
        Resource = resource;
        _download = download;
    }

    public MoviePilotResource Resource { get; }

    public string Title => Resource.Title;

    public string SiteName => Resource.SiteName ?? "";

    public Visibility SiteVisibility => string.IsNullOrWhiteSpace(Resource.SiteName) ? Visibility.Collapsed : Visibility.Visible;

    public string MetaLine => Resource.MetaLine;

    public Visibility MetaVisibility => Resource.MetaLine.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadLabel))]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    public partial MoviePilotDownloadState State { get; set; }

    public bool CanDownload => State is MoviePilotDownloadState.Idle or MoviePilotDownloadState.Failed;

    public string DownloadLabel => State switch
    {
        MoviePilotDownloadState.Working => "添加中…",
        MoviePilotDownloadState.Done => "已加入下载",
        MoviePilotDownloadState.Failed => "重试下载",
        _ => "下载"
    };

    public string DownloadAutomationName => $"下载：{Title}";

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private Task Download() => _download(this);
}

/// <summary>
/// 资源列表（一部片的可下载种子）的视图模型：进来就搜、铺成一行行，每行的「下载」接到二次确认加下单上。
/// 复用 <see cref="PageViewModel"/> 的忙/通知条/载入代次。
/// </summary>
public sealed partial class MoviePilotResourceViewModel : PageViewModel
{
    private const string Category = "moviepilot";

    private MoviePilotService? _service;
    private MoviePilotMedia? _media;

    public MoviePilotResourceViewModel()
    {
        EmptyNotice = "";
    }

    public ObservableCollection<MoviePilotResourceRow> Resources { get; } = [];

    /// <summary>对话框标题那一行——搜的是哪部片。</summary>
    public string Heading => _media?.Display ?? "资源";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyVisibility))]
    public partial bool ShowEmptyNotice { get; set; }

    public Visibility EmptyVisibility => Show(ShowEmptyNotice);

    [ObservableProperty]
    public partial string EmptyNotice { get; set; }

    internal void Attach(MoviePilotService service, MoviePilotMedia media)
    {
        _service = service;
        _media = media;
        OnPropertyChanged(nameof(Heading));
    }

    public override Task ReloadAsync() => SearchAsync();

    /// <summary>去搜这部片的资源。各站点现捞，可能要等几十秒——忙态那根线会一直转着。</summary>
    internal async Task SearchAsync()
    {
        if (_service is null || _media is null) return;

        var token = BeginLoad();
        ShowEmptyNotice = false;

        try
        {
            var found = await _service.SearchResourcesAsync(_media, token).ConfigureAwait(true);
            if (!IsCurrent(token)) return;

            Resources.Clear();
            foreach (var resource in found)
                Resources.Add(new MoviePilotResourceRow(resource, DownloadAsync));

            EmptyNotice = "没找到可下载的资源，换个别的片或稍后再试";
            ShowEmptyNotice = found.Count == 0;
            EndLoad(token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            if (!IsCurrent(token)) return;

            Log.Warn(Category, $"搜《{_media.Title}》资源失败", error);
            Report("搜索资源失败", error);
            Resources.Clear();
            ShowEmptyNotice = false;
            EndLoad(token);
        }
    }

    /// <summary>
    /// 一行的「下载」按下去：先二次确认（对外副作用 —— 真的把种子加进 MoviePilot 的下载器、开始下载），确认了
    /// 才下单。成功置「已加入下载」，失败置「重试下载」，那句话已是给人看的。
    /// </summary>
    private async Task DownloadAsync(MoviePilotResourceRow row)
    {
        if (_service is null || row.State == MoviePilotDownloadState.Working) return;

        var confirmed = await ConfirmAsync(
            "加入下载",
            $"确认把这个资源加进 MoviePilot 下载吗？\n{row.Title}",
            "下载").ConfigureAwait(true);
        if (!confirmed) return;

        row.State = MoviePilotDownloadState.Working;
        try
        {
            await _service.DownloadAsync(row.Resource, CancellationToken.None).ConfigureAwait(true);
            row.State = MoviePilotDownloadState.Done;
            Notify(null, $"已加入下载：{row.Title}", InfoBarSeverity.Success);
        }
        catch (Exception error)
        {
            row.State = MoviePilotDownloadState.Failed;
            Log.Warn(Category, $"下载「{row.Title}」失败", error);
            Report($"加入下载失败：{row.Title}", error);
        }
    }
}

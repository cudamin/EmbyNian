using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Momoka.Diagnostics;
using Momoka.MoviePilot;
using Momoka.Shell.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Momoka.Shell.ViewModels;

/// <summary>独立窗口的关键词资源搜索；过滤和结果行与精确搜索共用。</summary>
public sealed partial class MoviePilotVersionsViewModel : PageViewModel
{
    private const string IdlePrompt = "输入关键字，在 MoviePilot 上找可下载的版本";
    private MoviePilotService? _service;
    private ISystemLauncher? _launcher;
    private string _lastKeyword = "";
    private CancellationToken _query;

    public MoviePilotResourceBrowserViewModel Browser { get; } = new();
    public ObservableCollection<MoviePilotResourceRow> Versions => Browser.Rows;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyVisibility))]
    public partial bool ShowEmptyNotice { get; set; } = true;
    public Visibility EmptyVisibility => Show(ShowEmptyNotice);
    [ObservableProperty] public partial string EmptyNotice { get; set; } = IdlePrompt;

    internal void Attach(MoviePilotService service, ISystemLauncher launcher)
    {
        _service = service;
        _launcher = launcher;
    }

    public override Task ReloadAsync() => SearchAsync(_lastKeyword);

    internal async Task SearchAsync(string keyword)
    {
        if (_service is null) return;
        var text = keyword.Trim();
        _lastKeyword = text;
        var token = BeginLoad();
        _query = token;
        Browser.SetResults([]);
        ShowEmptyNotice = false;
        if (text.Length == 0)
        {
            EmptyNotice = IdlePrompt;
            ShowEmptyNotice = true;
            EndLoad(token);
            return;
        }
        try
        {
            var found = await _service.SearchByKeywordAsync(text, token).ConfigureAwait(true);
            if (!IsCurrent(token)) return;
            Browser.SetResults(found.Select(resource => new MoviePilotResourceRow(resource, DownloadAsync, OpenDetails)
            {
                State = MoviePilotResourceRow.StateOf(_service.DownloadState(resource))
            }));
            EmptyNotice = $"MoviePilot 上没搜到「{text}」的可下载版本，换个关键字或稍后再试";
            ShowEmptyNotice = found.Count == 0;
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!IsCurrent(token)) return;
            Report("搜索版本失败", error);
        }
        finally { EndLoad(token); }
    }

    private void OpenDetails(MoviePilotResourceRow row)
    {
        if (row.Resource.DetailsUri is not { } uri || _launcher is null) return;
        try { _launcher.OpenUrl(uri.AbsoluteUri); }
        catch (Exception error) { Report("打开种子页面失败", error); }
    }

    private async Task DownloadAsync(MoviePilotResourceRow row)
    {
        if (_service is null || !row.CanDownload || !IsCurrent(_query)) return;
        var token = _query;
        var confirmed = await ConfirmAsync("加入下载",
            $"确认把这个资源加进 MoviePilot 下载吗？\n{row.Resource.Confirmation}",
            "下载").ConfigureAwait(true);
        if (!confirmed || !IsCurrent(token)) return;
        row.State = MoviePilotDownloadState.Working;
        try
        {
            await _service.DownloadAsync(row.Resource, token).ConfigureAwait(true);
            if (!IsCurrent(token)) return;
            row.State = MoviePilotDownloadState.Done;
            Notify(null, $"已加入下载：{row.Title}", InfoBarSeverity.Success);
        }
        catch (Exception error)
        {
            if (!IsCurrent(token)) return;
            row.State = error is MoviePilotOperationUncertainException or MoviePilotOperationBlockedException
                ? MoviePilotDownloadState.Uncertain : MoviePilotDownloadState.Failed;
            Report($"加入下载未完成：{row.Title}", error);
        }
    }
}

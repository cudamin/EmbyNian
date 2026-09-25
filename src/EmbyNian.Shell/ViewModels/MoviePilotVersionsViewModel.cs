using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EmbyNian.Diagnostics;
using EmbyNian.MoviePilot;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyNian.Shell.ViewModels;

/// <summary>
/// 「在 MoviePilot 搜索其他版本」那个独立窗口的视图模型：一个关键字进来，走 MoviePilot 的关键词资源搜索
/// （<see cref="MoviePilotService.SearchByKeywordAsync"/>），把回来的种子铺成一行行，每行的「下载」接到二次
/// 确认加下单上。
/// <para>
/// 结果行复用 <see cref="MoviePilotResourceRow"/>（和资源覆盖面板同一种行）。和 <see cref="MoviePilotResourceViewModel"/>
/// 分开是因为入口不同 —— 那一个由一部确定的片（<c>MoviePilotMedia</c>）精确搜，这一个由一串关键字模糊搜，且带
/// 一个可改的搜索框（用户删掉 <c>S01</c>／<c>E01</c> 就把范围放宽，见 <c>MoviePilotVersionsView</c>）。下载那一段
/// 两者同构（都要服务和确认对话框），各留一份短的，不硬凑成一处。
/// </para>
/// </summary>
public sealed partial class MoviePilotVersionsViewModel : PageViewModel
{
    private const string Category = "moviepilot";

    /// <summary>还没搜、或空词时页面中间那句话。</summary>
    private const string IdlePrompt = "输入关键字，在 MoviePilot 上找可下载的版本";

    private MoviePilotService? _service;

    /// <summary>上一次搜的词，供 <see cref="ReloadAsync"/> 重跑。</summary>
    private string _lastKeyword = "";

    public MoviePilotVersionsViewModel()
    {
        EmptyNotice = IdlePrompt;
        ShowEmptyNotice = true;
    }

    public ObservableCollection<MoviePilotResourceRow> Versions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyVisibility))]
    public partial bool ShowEmptyNotice { get; set; }

    public Visibility EmptyVisibility => Show(ShowEmptyNotice);

    [ObservableProperty]
    public partial string EmptyNotice { get; set; }

    internal void Attach(MoviePilotService service) => _service = service;

    /// <summary>窗口刷新就是把上一次的词重搜一遍。</summary>
    public override Task ReloadAsync() => SearchAsync(_lastKeyword);

    /// <summary>
    /// 按一个关键字去 MoviePilot 搜可下载的版本。各站点现捞，可能等几十秒——忙态那圈环一直转。空词回到「输入
    /// 关键字」的样子、不发请求。结果整片重填（集合实例保持稳定）。
    /// </summary>
    internal async Task SearchAsync(string keyword)
    {
        if (_service is null) return;

        var text = keyword.Trim();
        _lastKeyword = text;

        if (text.Length == 0)
        {
            Versions.Clear();
            EmptyNotice = IdlePrompt;
            ShowEmptyNotice = true;
            return;
        }

        var token = BeginLoad();
        ShowEmptyNotice = false;

        try
        {
            var found = await _service.SearchByKeywordAsync(text, token).ConfigureAwait(true);
            if (!IsCurrent(token)) return;

            Versions.Clear();
            foreach (var resource in found)
                Versions.Add(new MoviePilotResourceRow(resource, DownloadAsync));

            EmptyNotice = found.Count == 0
                ? $"MoviePilot 上没搜到「{text}」的可下载版本，换个关键字或稍后再试"
                : IdlePrompt;
            ShowEmptyNotice = found.Count == 0;
            EndLoad(token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            if (!IsCurrent(token)) return;

            Log.Warn(Category, $"MoviePilot 关键词搜「{text}」失败", error);
            Report("搜索版本失败", error);
            Versions.Clear();
            ShowEmptyNotice = false;
            EndLoad(token);
        }
    }

    /// <summary>
    /// 一行的「下载」按下去：先二次确认（对外副作用——真的把种子加进 MoviePilot 的下载器、开始下载），确认了
    /// 才下单。成功置「已加入下载」，失败置「重试下载」，那句话已是给人看的。逻辑同 <see cref="MoviePilotResourceViewModel"/>。
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

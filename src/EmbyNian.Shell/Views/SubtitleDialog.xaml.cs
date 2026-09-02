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

    /// <summary>哪家、什么语言、什么格式、下过多少次 —— 挑哪一条全靠这一行。</summary>
    public string Detail { get; } = info.Describe();
}

/// <summary>
/// 「搜索和修改字幕」那张表。删和下都是当场做的（按下那一行的按钮就发请求），所以它没有「确定」键。
/// <para>
/// 三件事都是交进来的委托：这张表不该知道服务器是怎么连上的，也不该知道重试和重登那一套（那在
/// <see cref="EmbySession"/> 上）。<see cref="Touched"/> 是关掉之后告诉调用方的唯一一件事 —— 动过就得让这一页
/// 重读一遍，不然新下的那条字幕在播放器的轨道表里挑不到。
/// </para>
/// </summary>
public sealed partial class SubtitleDialog : ContentDialog
{
    private const string Category = "ui";

    /// <summary>
    /// 能搜的语言。写死一小张表而不是让人填三字母代码：那一头是 ISO 639-2（<c>chi</c> 而不是 <c>zh</c>），
    /// 填错了服务器只会答一个空列表，看着像「搜不到」。
    /// </summary>
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

    private readonly ObservableCollection<SubtitleTrack> _tracks = [];
    private readonly ObservableCollection<SubtitleCandidate> _results = [];

    public SubtitleDialog(
        EmbyItem item,
        MediaSource source,
        Func<string, Task<List<RemoteSubtitleInfo>>> search,
        Func<RemoteSubtitleInfo, Task> download,
        Func<MediaStream, Task> delete)
    {
        InitializeComponent();

        RequestedTheme = ThemeHost.Current.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        Title = $"搜索和修改字幕 — {item.Name}";

        _search = search;
        _download = download;
        _delete = delete;

        // 只列外挂的：内嵌在容器里的那些服务器删不掉，摆一颗按不动的「删除」在旁边只会让人以为坏了。
        foreach (var stream in source.SubtitleStreams.Where(stream => stream.IsExternal))
            _tracks.Add(new SubtitleTrack(stream));

        Tracks.ItemsSource = _tracks;
        Results.ItemsSource = _results;
        ShowTracks();

        foreach (var (label, code) in Languages)
            LanguageBox.Items.Add(new ComboBoxItem { Content = label, Tag = code });

        LanguageBox.SelectedIndex = 0;
    }

    /// <summary>删过或者下过 —— 也就是这一页得重读一遍。</summary>
    public bool Touched { get; private set; }

    /// <summary>没有外挂字幕时那一行说明顶上来，列表收起去。</summary>
    private void ShowTracks()
    {
        Tracks.Visibility = _tracks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoTracks.Visibility = _tracks.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Say(string message)
    {
        Status.Text = message;
        Status.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 搜一遍。搜的这一路可能要走十几秒（服务器要去问字幕站），所以转圈那颗要转起来，按钮要按不动 ——
    /// 不然连按三下就是三趟同样的请求。
    /// </summary>
    private async void OnSearch(object sender, RoutedEventArgs e)
    {
        if (LanguageBox.SelectedItem is not ComboBoxItem { Tag: string code }) return;

        SearchButton.IsEnabled = false;
        Busy.IsActive = true;
        Say("正在搜索…");
        _results.Clear();

        try
        {
            var found = await _search(code).ConfigureAwait(true);

            foreach (var candidate in found) _results.Add(new SubtitleCandidate(candidate));

            // 一条都没有和「服务器上没装字幕插件」在屏上是同一个样子，所以这句话把两种可能都说了。
            Say(_results.Count > 0
                ? $"找到 {_results.Count} 条，点「下载」挂到这个文件上。"
                : "没有找到字幕。服务器上要装并启用字幕刮削插件（如 OpenSubtitles）才搜得到。");
        }
        catch (Exception error)
        {
            Log.Warn(Category, "搜索字幕失败", error);
            Say($"搜索字幕失败：{Failure.Describe(error)}");
        }
        finally
        {
            SearchButton.IsEnabled = true;
            Busy.IsActive = false;
        }
    }

    /// <summary>
    /// 下一条挂上去。下完不把它从列表里去掉：同一批里常有好几条值得试，而下过的那一条再下一次只是覆盖。
    /// </summary>
    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SubtitleCandidate candidate } button) return;

        button.IsEnabled = false;
        Say($"正在下载「{candidate.Name}」…");

        try
        {
            await _download(candidate.Info).ConfigureAwait(true);

            Touched = true;
            Say($"已挂上「{candidate.Name}」。关掉这张表之后就能在字幕里选它。");
        }
        catch (Exception error)
        {
            Log.Warn(Category, "下载字幕失败", error);
            Say($"下载字幕失败：{Failure.Describe(error)}");
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    /// <summary>
    /// 删掉一条外挂字幕。删的是服务器上的那个字幕文件，不可撤销 —— 但它随时能再搜一条回来，所以这里不再问
    /// 一句：一层层确认会让「删掉三条不对的字幕」变成六次点击。
    /// </summary>
    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SubtitleTrack track } button) return;

        button.IsEnabled = false;

        try
        {
            await _delete(track.Stream).ConfigureAwait(true);

            _tracks.Remove(track);
            ShowTracks();
            Touched = true;
            Say($"已删除「{track.Label}」。");
        }
        catch (Exception error)
        {
            Log.Warn(Category, "删除字幕失败", error);
            Say($"删除字幕失败：{Failure.Describe(error)}");
            button.IsEnabled = true;
        }
    }
}

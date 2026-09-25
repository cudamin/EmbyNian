using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using EmbyNian.Diagnostics;
using EmbyNian.Infrastructure;
using EmbyNian.MoviePilot;
using EmbyNian.Shell.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EmbyNian.Shell.ViewModels;

/// <summary>
/// 首页「正在下载」一排里的一张卡：还没进 Emby 媒体库、正在 MoviePilot 下载器里跑的那部片。
/// <para>
/// 形状照着 <see cref="Views.CardItem"/> 抄，但**不是**它的子类，也没有共用基类 —— 卡片的一切都从
/// <see cref="MoviePilotDownloadTask"/> 来，而那不是 Emby 条目：没有 id，图片不在 EmbyImageStore 里，
/// 也没有「点开详情」的去处（条目进了库自然会在最近添加里出现，到那时它就是普通卡片了）。
/// </para>
/// <para>
/// 轮询一次、进度跳一格：一张卡从生到死都代表同一个下载任务（<see cref="Hash"/> 认人），新读数由
/// <see cref="Update"/> 就地改进来，**不**换对象 —— 换对象整排就得重建，进度条的跳动会变成整排闪烁。
/// </para>
/// </summary>
public sealed class DownloadCard : INotifyPropertyChanged
{
    private const string Category = "主页";

    /// <summary>Segoe Fluent 的电影／剧集字形，写数字的理由见 <see cref="Views.CardItem"/> 那段。</summary>
    private const int MovieGlyph = 0xE8B2;
    private const int SeriesGlyph = 0xE7F4;

    /// <summary>一张图的一次机会有多久。这是在给「直连不上 TMDB」掐表 —— 客户端的网络到不了图源时，等是等不来的。</summary>
    private static readonly TimeSpan ImageTimeout = TimeSpan.FromSeconds(8);

    private readonly int _width;
    private MoviePilotDownloadTask _task;
    private BitmapImage? _image;
    private CancellationTokenSource? _loading;

    public DownloadCard(MoviePilotDownloadTask task, int width)
    {
        _task = task;
        _width = width;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>这条任务跨轮询认人的钥匙。不随 <see cref="Update"/> 变。</summary>
    public string Hash => _task.Hash;

    /// <summary>这张卡此刻装着的读数。差量对账（<see cref="HomeViewModel"/>）从这儿读上一轮。</summary>
    public MoviePilotDownloadTask Current => _task;

    public string Title => _task.Title;

    public double CardWidth => _width;

    public double PosterHeight => CardSize.HeightFor(_width, wide: false);

    /// <summary>图没来之前垫底的字形。MoviePilot 的媒体类型就是「电影」「电视剧」这两个词。</summary>
    public string Glyph => char.ConvertFromUtf32(_task.MediaType is "电视剧" or "tv" ? SeriesGlyph : MovieGlyph);

    public BitmapImage? Image
    {
        get => _image;
        private set
        {
            if (ReferenceEquals(_image, value)) return;
            _image = value;
            Raise();
        }
    }

    public double Progress => _task.Progress;

    /// <summary>进度条的两段，与 <see cref="Views.CardItem"/> 同一套星号列 —— 「长度就是进度」是布局算出来的。</summary>
    public GridLength ProgressDone => new(Progress, GridUnitType.Star);

    public GridLength ProgressLeft => new(100 - Progress, GridUnitType.Star);

    /// <summary>标题底下那行读数：百分比打头，跟着速度和剩余时间；暂停就直说。</summary>
    public string InfoLine => MoviePilotDownload.InfoLine(_task);

    public Visibility PauseBadgeVisibility => Show(!_task.IsDownloading);

    /// <summary>
    /// 悬停才看的账：片名和身份之外，还有容量、站点这些排面放不下、但排查时正想要的东西。
    /// </summary>
    public string ToolTip
    {
        get
        {
            var identity = string.Join(" ", new[] { _task.Year, _task.SeasonEpisode, _task.SiteName }
                .Where(part => part.Length > 0));
            var line = $"{_task.Title}{(identity.Length > 0 ? $"　{identity}" : "")}\n{InfoLine}";
            var size = MoviePilotDownload.SizeText(_task.Size);
            return size.Length > 0 ? $"{line} · {size}" : line;
        }
    }

    /// <summary>
    /// 同一个任务的这一轮新读数就地改进来。集合意义上这张卡没换人（见类注释），屏上变的是数字和那根条。
    /// </summary>
    public void Update(MoviePilotDownloadTask task)
    {
        _task = task;
        Raise(nameof(Title));
        Raise(nameof(Progress));
        Raise(nameof(ProgressDone));
        Raise(nameof(ProgressLeft));
        Raise(nameof(InfoLine));
        Raise(nameof(PauseBadgeVisibility));
        Raise(nameof(ToolTip));
    }

    /// <summary>
    /// 把封面取来解好。**建卡时发一趟就够**（不等容器进树才发）：这一排只在没有下载任务时才不存在，卡数就
    /// 是正在下载的任务数（个位数到十几），犯不上套 <see cref="Views.CardItem"/> 那一套按需加载与回收。
    /// <para>
    /// 两级取法，代理优先：MoviePilot 服务器既然认出了这部片，它就抓到过这张图，而且服务器端还开着磁盘缓存；
    /// 客户端直连图源（TMDB）经常是够不着的，所以直连只当兜底。两级都取不到就是灰底字形，下一轮轮询也不会
    /// 自动重试 —— 一个下载任务的封面不会中途换，重试省了。
    /// </para>
    /// </summary>
    public async Task EnsureImageAsync(MoviePilotService service)
    {
        if (_image is not null || _loading is not null || _task.ImageUrl.Length == 0) return;

        var cts = new CancellationTokenSource();
        _loading = cts;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            timeout.CancelAfter(ImageTimeout);

            byte[] bytes;
            try
            {
                bytes = await service.FetchImageAsync(_task.ImageUrl, timeout.Token).ConfigureAwait(true);
            }
            catch (Exception proxyError) when (proxyError is not OperationCanceledException)
            {
                Log.Debug(Category, $"下载封面改直连（{_task.Title}）：代理没取到（{proxyError.Message}）");
                bytes = await service.FetchImageDirectAsync(_task.ImageUrl, timeout.Token).ConfigureAwait(true);
            }

            if (cts.IsCancellationRequested) return;

            Image = await PosterLoader.DecodeAsync(bytes, _width).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            Log.Debug(Category, $"下载封面取不到（{_task.Title}）：{error.Message}");
        }
        finally
        {
            if (ReferenceEquals(_loading, cts)) _loading = null;
            cts.Dispose();
        }
    }

    /// <summary>这一排散了：把手上的解码画面交回去，在路上那一趟取消。</summary>
    public void AbandonImage()
    {
        _loading?.Cancel();
        _loading = null;
        Image = null;
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private void Raise([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property ?? string.Empty));
}

/// <summary>
/// 首页「正在下载」那一排：牌子加一条横向卡片带。形状照 <see cref="CardShelf"/>，但只有一排、不进版面表
/// —— 它在不在屏上由「此刻有没有任务在下载」说了算，用户勾不掉它。
/// </summary>
public sealed partial class DownloadRow : ObservableObject
{
    /// <summary>这一排的卡片按海报宽画 —— 下载卡片带的是封面（海报），不是剧照。</summary>
    public const int CardWidth = CardSize.PosterWidth;

    public DownloadRow()
    {
        RowHeight = CardSize.HeightFor(CardWidth, wide: false) + CardSize.Chrome;

        // 读数跟着内容走，同 CardShelf：挂在集合事件上，谁往里加删都不漏喊。
        Cards.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Note));
    }

    public string Title => "正在下载";

    /// <summary>牌子右端的读数：「3 个任务」。空的时候是空串，牌子把那一格整个收起。</summary>
    public string Note => Cards.Count > 0 ? $"{Cards.Count} 个任务" : string.Empty;

    public double RowHeight { get; }

    public ObservableCollection<DownloadCard> Cards { get; } = [];
}

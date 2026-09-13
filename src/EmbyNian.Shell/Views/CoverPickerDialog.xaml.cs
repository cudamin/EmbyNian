using System.ComponentModel;
using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Shell.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 候选封面里的一张。缩略图是后到的，所以这个类得会发通知 —— 同 <c>CardItem</c>。
/// <para>
/// 公开的，因为 <c>x:Bind</c> 在 DataTemplate 里是按类型名编译的。
/// </para>
/// </summary>
public sealed class CoverChoice(RemoteImageInfo info) : INotifyPropertyChanged
{
    private BitmapImage? _picture;

    public event PropertyChangedEventHandler? PropertyChanged;

    public RemoteImageInfo Info { get; } = info;

    /// <summary>哪家、多大、什么语言。</summary>
    public string Caption { get; } = info.Describe();

    public BitmapImage? Picture
    {
        get => _picture;
        set
        {
            if (ReferenceEquals(_picture, value)) return;
            _picture = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Picture)));
        }
    }
}

/// <summary>
/// 「换一张」那张候选表。它回答的是<b>这一格用哪一张</b>，两种答案：从服务器列的候选里挑一个
/// （<see cref="Picked"/>），或者从本机传一张（<see cref="Uploaded"/>）—— 两者最多只有一个非空。
/// <para>
/// 缩略图是这张表自己取回来的：一次打开就那么十几二十张，取完就扔，没有必要进磁盘缓存（那一份是按「条目 +
/// 图片标签」存的，而这些图还不属于任何条目）。
/// </para>
/// <para>
/// 上传那一趟<b>不在这张表里做</b>：文件框和「往服务器写」都是外面那位的事，这里只把选中的字节带出去。
/// 这张表要知道服务器怎么连上，是从前那一版就立下的规矩。
/// </para>
/// </summary>
public sealed partial class CoverPickerDialog : ContentDialog
{
    private const string Category = "ui";

    /// <summary>
    /// 一次最多列几张。服务器那一头能给六十张，而每张都要经服务器代取一次 —— 挑封面用不到六十张，
    /// 二十四张已经是四行。
    /// </summary>
    private const int Most = 24;

    /// <summary>缩略图解到多宽。和模板里那一格的宽度一致，见 <see cref="PosterLoader.DecodeAsync"/>。</summary>
    private const int ThumbnailWidth = 150;

    private readonly List<CoverChoice> _choices = [];

    /// <summary>
    /// 外面那张面板。取候选缩略图的委托从它身上拿，免得同一份「按地址取图」的接线在这儿和那儿各写一遍 ——
    /// 那也保证了这一张表里所有网络上的事仍然只有那一个人在发。
    /// </summary>
    private readonly CoverDialog _owner;

    /// <param name="kind">正在换的是哪一种图。它只用来写标题 —— 剩下的事外面那位已经知道了。</param>
    /// <param name="found">服务器列的候选。</param>
    /// <param name="owner">外面那张面板，见 <see cref="_owner"/>。</param>
    public CoverPickerDialog(EmbyItem item, ArtworkKind kind, RemoteImageResult found, CoverDialog owner)
    {
        InitializeComponent();

        _owner = owner;

        RequestedTheme = ThemeHost.Current.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        Title = $"换一张{kind.Name} — {item.Name}";

        foreach (var image in found.Images.Where(image => !string.IsNullOrWhiteSpace(image.Url)).Take(Most))
            _choices.Add(new CoverChoice(image));

        Grid.ItemsSource = _choices;
        Grid.Visibility = _choices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_choices.Count == 0)
        {
            // 一家刮削源都没答上来和「答了但没有这一种图」是两件事，用户能做的处置不同：前者要去服务器上装
            // 或者配刮削插件，后者只能等那一家有图。
            Notice.Text = found.Providers.Count == 0
                ? "服务器上没有能提供图片的刮削源，或者这个条目没有对上任何一家的记录。底下那颗「从本机传一张」照样能用。"
                : $"刮削源（{string.Join("、", found.Providers)}）上没有这个条目的{kind.Name}。底下那颗「从本机传一张」照样能用。";
            Notice.Visibility = Visibility.Visible;
        }

        IsPrimaryButtonEnabled = false;

        // 不 await：一张张图慢慢回来，这张表先画出来。
        _ = LoadAsync();
    }

    /// <summary>挑中的那一张，没挑就是空。</summary>
    public RemoteImageInfo? Picked => (Grid.SelectedItem as CoverChoice)?.Info;

    /// <summary>从本机选中的那张图（字节 + 文件名），没选就是空。</summary>
    public PickedArtwork? Uploaded { get; private set; }

    private void OnPicked(object sender, SelectionChangedEventArgs e) => IsPrimaryButtonEnabled = Picked is not null;

    /// <summary>
    /// 从本机传一张。选完就把答案收下并关掉这张表 —— 上传那一趟由外面那张面板发，因为要往服务器写。
    /// <para>
    /// 文件框弹不出来（拿不到窗口句柄）时退化成一句人话，而不是一个静默不动的按钮。
    /// </para>
    /// </summary>
    private async void OnUpload(object sender, RoutedEventArgs e)
    {
        try
        {
            Uploaded = await _owner.PickArtworkAsync().ConfigureAwait(true);
            if (Uploaded is null) return;

            Hide();
        }
        catch (Exception error)
        {
            Log.Warn(Category, "选图片文件失败", error);
            Notice.Text = $"打不开文件选择框：{Failure.Describe(error)}";
            Notice.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// 把这几张缩略图取回来。一张失败只让那一格空着 —— 剩下的照旧能挑，而挑的是地址，不是这张缩略图。
    /// </summary>
    private async Task LoadAsync()
    {
        foreach (var choice in _choices)
        {
            var address = choice.Info.ThumbnailUrl is { Length: > 0 } thumbnail ? thumbnail : choice.Info.Url;

            try
            {
                var bytes = await _owner.FetchRemoteAsync(address).ConfigureAwait(true);
                if (bytes is not { Length: > 0 }) continue;

                choice.Picture = await PosterLoader.DecodeAsync(bytes, ThumbnailWidth).ConfigureAwait(true);
            }
            catch (Exception error)
            {
                Log.Debug(Category, $"候选封面取不回来（{choice.Info.ProviderName}）：{error.Message}");
            }
        }
    }
}

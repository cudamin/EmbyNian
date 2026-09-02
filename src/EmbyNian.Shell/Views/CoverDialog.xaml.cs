using System.ComponentModel;
using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Shell.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 候选封面里的一张。缩略图是后到的，所以这个类得会发通知 —— 同 <see cref="CardItem"/>。
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
/// 「修改媒体封面图」那张表。它只回答一件事：<b>用哪一张</b>（<see cref="Chosen"/>）。真正把图挂到条目上的
/// 那一趟请求由 <see cref="ItemCommands"/> 发。
/// <para>
/// 缩略图是这张表自己取的：一次打开就那么十几二十张，取完就扔，没有必要进磁盘缓存（那一份是按「条目 + 图片
/// 标签」存的，而这些图还不属于任何条目）。
/// </para>
/// </summary>
public sealed partial class CoverDialog : ContentDialog
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

    /// <param name="fetch">
    /// 按地址取一张图的字节。交进来而不是自己拿 <see cref="EmbySession"/>：这张表不该知道服务器是怎么连上的。
    /// </param>
    public CoverDialog(EmbyItem item, RemoteImageResult found, Func<string, Task<byte[]>> fetch)
    {
        InitializeComponent();

        RequestedTheme = ThemeHost.Current.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        Title = $"修改媒体封面图 — {item.Name}";

        foreach (var image in found.Images.Where(image => !string.IsNullOrWhiteSpace(image.Url)).Take(Most))
            _choices.Add(new CoverChoice(image));

        Grid.ItemsSource = _choices;
        Grid.Visibility = _choices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_choices.Count == 0)
        {
            // 一家刮削源都没答上来和「答了但没有这一种图」是两件事，用户能做的处置不同：前者要去服务器上装
            // 或者配刮削插件，后者只能等那一家有图。
            Notice.Text = found.Providers.Count == 0
                ? "服务器上没有能提供图片的刮削源，或者这个条目没有对上任何一家的记录。"
                : $"刮削源（{string.Join("、", found.Providers)}）上没有这个条目的封面图。";
            Notice.Visibility = Visibility.Visible;
        }

        IsPrimaryButtonEnabled = false;

        // 不 await：一张张图慢慢回来，这张表先画出来。
        _ = LoadAsync(fetch);
    }

    /// <summary>挑中的那一张，没挑就是空。</summary>
    public RemoteImageInfo? Chosen => (Grid.SelectedItem as CoverChoice)?.Info;

    private void OnPicked(object sender, SelectionChangedEventArgs e) => IsPrimaryButtonEnabled = Chosen is not null;

    /// <summary>
    /// 把这几张缩略图取回来。一张失败只让那一格空着 —— 剩下的照旧能挑，而挑的是地址，不是这张缩略图。
    /// </summary>
    private async Task LoadAsync(Func<string, Task<byte[]>> fetch)
    {
        foreach (var choice in _choices)
        {
            var address = choice.Info.ThumbnailUrl is { Length: > 0 } thumbnail ? thumbnail : choice.Info.Url;

            try
            {
                var bytes = await fetch(address).ConfigureAwait(true);
                if (bytes.Length == 0) continue;

                choice.Picture = await PosterLoader.DecodeAsync(bytes, ThumbnailWidth).ConfigureAwait(true);
            }
            catch (Exception error)
            {
                Log.Debug(Category, $"候选封面取不回来（{choice.Info.ProviderName}）：{error.Message}");
            }
        }
    }
}

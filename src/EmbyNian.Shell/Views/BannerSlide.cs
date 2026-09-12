using System.ComponentModel;
using System.Runtime.CompilerServices;
using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Shell.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EmbyNian.Shell.Views;

/// <summary>
/// One slide of the home page's carousel: what it says, and the two pictures it says it over.
/// <para>
/// The same split as <see cref="CardItem"/>, and for the same reason — the control is one band that every
/// slide takes a turn in, so the artwork has to belong to the slide rather than to the band, or moving on
/// would drop whatever the previous slide had decoded. Two pictures rather than one, because this band is
/// both halves of 需求 4 at once: 背景图 behind it (<see cref="ItemArtwork.Banner"/>) and 徽标 on top of it
/// (<see cref="ItemArtwork.Plate"/>).
/// </para>
/// <para>
/// Public because <c>x:Bind</c> compiles against the type by name.
/// </para>
/// </summary>
public sealed class BannerSlide : INotifyPropertyChanged
{
    private const string Category = "主页轮播";

    /// <summary>
    /// What the picture is decoded at. 1280 is <see cref="EmbyImageStore.RequestWidth"/>'s own ceiling, so
    /// asking for more would only upscale a 1280-wide download — and this band is the width of the window,
    /// which on this machine is past that ceiling before it is maximised.
    /// </summary>
    private const int PictureWidth = 1280;

    /// <summary>Same as the detail page's name plate: the logo is drawn a few inches wide at most.</summary>
    private const int LogoWidth = 320;

    private readonly EmbyImageStore _images;
    private readonly ArtworkRef? _picture;
    private readonly ArtworkRef? _logo;

    private CancellationTokenSource? _loading;
    private bool _asked;
    private BitmapImage? _pictureImage;
    private BitmapImage? _logoImage;

    public BannerSlide(EmbyItem item, EmbyImageStore images)
    {
        Item = item;
        _images = images;
        _picture = ItemArtwork.Banner(item);
        _logo = ItemArtwork.Plate(item);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The item behind the slide, for whoever opens or plays it.</summary>
    public EmbyItem Item { get; }

    /// <summary>The show's name on an episode, which is where the reader thinks they are.</summary>
    public string Title => ItemDetail.Title(Item);

    public string Caption => HomeCarousel.Caption(Item);

    public string Synopsis => HomeCarousel.Synopsis(Item);

    /// <summary>「继续播放 12:34」 on a half-watched item, which is most of what this band holds.</summary>
    public string PlayText => ItemDetail.PlayText(Item);

    public Visibility CaptionVisibility => Show(Caption.Length > 0);

    public Visibility SynopsisVisibility => Show(Synopsis.Length > 0);

    /// <summary>What a reader who cannot see the band is told it is: 「打开 命运石之门」.</summary>
    public string OpenTip => $"打开 {Title}";

    public BitmapImage? Picture
    {
        get => _pictureImage;
        private set
        {
            if (ReferenceEquals(_pictureImage, value)) return;
            _pictureImage = value;
            Raise();
        }
    }

    public BitmapImage? Logo
    {
        get => _logoImage;
        private set
        {
            if (ReferenceEquals(_logoImage, value)) return;
            _logoImage = value;
            Raise();
            Raise(nameof(LogoVisibility));
        }
    }

    public Visibility LogoVisibility => Show(_logoImage is not null);

    /// <summary>
    /// Starts both loads if they have not been asked for. Idempotent and asked once per slide: the band calls
    /// this on the slide it is about to show and on its two neighbours, so a slide the reader is walking
    /// towards is already decoded when it arrives, and every slide is asked at most once no matter how many
    /// times the carousel comes back around to it.
    /// </summary>
    public async Task EnsureAsync()
    {
        if (_asked || _loading is not null) return;

        var cts = new CancellationTokenSource();
        _loading = cts;
        _asked = true;

        try
        {
            // Sequential, and the picture first: it is the whole band, the logo is a stamp in one corner of
            // it, and the two together would otherwise race for the one connection the session holds.
            var bytes = await FetchAsync(_picture, PictureWidth, cts.Token).ConfigureAwait(true);

            if (bytes is { Length: > 0 } && !cts.IsCancellationRequested)
            {
                var picture = await BannerPictureLoader.DecodeAsync(bytes, PictureWidth, cts.Token).ConfigureAwait(true);
                if (!ReferenceEquals(_loading, cts) || cts.IsCancellationRequested) return;
                Picture = picture;
            }

            if (cts.IsCancellationRequested) return;

            var logo = await DecodeAsync(_logo, LogoWidth, cts.Token).ConfigureAwait(true);
            if (!ReferenceEquals(_loading, cts) || cts.IsCancellationRequested) return;
            Logo = logo;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            // 带 exception 而不只是 Message：这句文案（「没有检测到已安装的组件」）是 WinUI 的 0x800F1000
            // 误报 —— CBS 错误码和 XAML 撞了号，消息本身说不清是哪一层的事，只有堆栈说得清（2026-09-12 记）。
            Log.Warn(Category, $"加载轮播图片失败（{Item.Name}）：{error.Message}", error);
        }
        finally
        {
            if (ReferenceEquals(_loading, cts)) _loading = null;
            cts.Dispose();
        }
    }

    /// <summary>
    /// Drops the decoded picture and the logo, and abandons anything in flight. Called on the slides the band
    /// has moved away from: a backdrop is a megabyte-scale surface, and eight of them held at once is the whole
    /// reason this is a method rather than a load that happens once and stays.
    /// <para>
    /// <see cref="_asked"/> is cleared too, so a slide that comes back around asks again — the bytes are in
    /// the image store's disk cache by then, so the second ask is a read rather than a download.
    /// </para>
    /// </summary>
    public void Release()
    {
        _loading?.Cancel();
        _loading = null;
        _asked = false;
        Picture = null;
        Logo = null;
    }

    /// <summary>取字节那一半（缓存命中的话是一次磁盘读），解码那一半在调用方。</summary>
    private async Task<byte[]?> FetchAsync(ArtworkRef? artwork, int width, CancellationToken token)
    {
        if (artwork is not { } reference) return null;

        return await _images
            .GetAsync(reference.ItemId, reference.ImageType, reference.Tag, EmbyImageStore.RequestWidth(width), token)
            .ConfigureAwait(true);
    }

    private async Task<BitmapImage?> DecodeAsync(ArtworkRef? artwork, int width, CancellationToken token)
    {
        if (artwork is not { } reference) return null;

        var bytes = await _images
            .GetAsync(reference.ItemId, reference.ImageType, reference.Tag, EmbyImageStore.RequestWidth(width), token)
            .ConfigureAwait(true);

        return bytes is { Length: > 0 } && !token.IsCancellationRequested
            ? await PosterLoader.DecodeAsync(bytes, width).ConfigureAwait(true)
            : null;
    }

    private void Raise([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property ?? string.Empty));

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}

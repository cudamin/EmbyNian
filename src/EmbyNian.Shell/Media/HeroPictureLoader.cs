using EmbyNian.Emby;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace EmbyNian.Shell.Media;

/// <summary>
/// 详情页头图的解码：先解到像素，按需要过 <see cref="BackdropBlur"/>（集页的常驻模糊，加上「窗口拉宽
/// 裁切越多模糊越重」那道裁切驱动的模糊），再重编码成交给 <c>BitmapImage</c>。管线和 <see
/// cref="BannerPictureLoader"/> 是同一条 —— 解码、取像素、离开 UI 线程处理、BMP 无损交接。
/// <para>
/// 解出的像素留在 <see cref="HeroPixels"/> 里给视图模型：裁切模糊的半径跟着窗口的形状走（<see
/// cref="BackdropBlur.CropRadius"/>），档位一换就要用同一批像素重糊一遍 —— 没有这一手，每次档位切换都是
/// 一趟网络加一趟解码。
/// </para>
/// </summary>
internal static class HeroPictureLoader
{
    /// <summary>解好的原图像素：模糊的原料。Blur 时复制一份，原料本身不动，同一个半径重糊也站得住。</summary>
    public sealed record HeroPixels(byte[] Pixels, int Width, int Height);

    /// <summary>解到像素为止，不模糊。约 1280 宽（<paramref name="maxWidth"/> 封顶）、尊重照片方向。</summary>
    public static async Task<HeroPixels?> DecodePixelsAsync(byte[] bytes, int maxWidth, CancellationToken token)
    {
        if (bytes.Length == 0) return null;
        token.ThrowIfCancellationRequested();

        using var source = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(source))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        source.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(source);
        token.ThrowIfCancellationRequested();

        // 在解码时限制尺寸；尊重照片方向，避免服务器忽略宽度参数时持有原始大图。
        var ratio = Math.Min(1d, Math.Max(1, maxWidth) / (double)decoder.OrientedPixelWidth);
        var transform = new BitmapTransform
        {
            ScaledWidth = Math.Max(1, (uint)Math.Round(decoder.PixelWidth * ratio)),
            ScaledHeight = Math.Max(1, (uint)Math.Round(decoder.PixelHeight * ratio)),
            InterpolationMode = BitmapInterpolationMode.Fant
        };

        using var decoded = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        token.ThrowIfCancellationRequested();

        var width = decoded.PixelWidth;
        var height = decoded.PixelHeight;
        var buffer = new Windows.Storage.Streams.Buffer(checked((uint)(width * height * 4)));
        decoded.CopyToBuffer(buffer);
        var pixels = new byte[buffer.Length];
        using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);

        return new HeroPixels(pixels, width, height);
    }

    /// <summary>
    /// 按半径模糊（在像素副本上，原料不动）并重编码成 <see cref="BitmapImage"/>。模糊离开 UI 线程：
    /// 1280 宽的剧照三趟盒式模糊是二十来兆次整数运算，拖窗口和换季不等它。
    /// </summary>
    public static async Task<BitmapImage?> RenderAsync(HeroPixels source, int blurRadius, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var pixels = new byte[source.Pixels.Length];
        Array.Copy(source.Pixels, pixels, pixels.Length);
        await Task.Run(() => BackdropBlur.BoxBlur(pixels, source.Width, source.Height, blurRadius), token);
        token.ThrowIfCancellationRequested();

        // BMP 是内存中的无损交接，不写缓存、不做有损二次压缩。保持 BitmapImage，现有画刷和尺寸自检可复用。
        using var rendered = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, rendered);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            (uint)source.Width, (uint)source.Height, 96, 96, pixels);
        await encoder.FlushAsync();
        token.ThrowIfCancellationRequested();

        rendered.Seek(0);
        var picture = new BitmapImage
        {
            DecodePixelType = DecodePixelType.Physical,
            DecodePixelWidth = source.Width
        };
        await picture.SetSourceAsync(rendered);
        token.ThrowIfCancellationRequested();
        return picture;
    }
}

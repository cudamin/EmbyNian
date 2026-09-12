using EmbyNian.Emby;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace EmbyNian.Shell.Media;

/// <summary>将采色延长区和轮播原图一起解码，交叉淡入时两部分始终属于同一张图。</summary>
internal static class BannerPictureLoader
{
    public static async Task<BitmapImage?> DecodeAsync(byte[] bytes, int maxWidth, CancellationToken token)
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

        // 像素采样离开 UI 线程，拖动窗口和快速翻页不必等这段循环完成。
        var extended = await Task.Run(() => CarouselBackdrop.ExtendLeft(pixels, width, height), token);
        token.ThrowIfCancellationRequested();

        // BMP 是内存中的无损交接，不写缓存、不做有损二次压缩。保持 BitmapImage，现有画刷和尺寸自检可复用。
        using var rendered = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, rendered);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            (uint)extended.Width, (uint)height, 96, 96, extended.Pixels);
        await encoder.FlushAsync();
        token.ThrowIfCancellationRequested();

        rendered.Seek(0);
        var picture = new BitmapImage
        {
            DecodePixelType = DecodePixelType.Physical,
            DecodePixelWidth = extended.Width
        };
        await picture.SetSourceAsync(rendered);
        token.ThrowIfCancellationRequested();
        return picture;
    }
}

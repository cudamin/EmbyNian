using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace EmbyNian.Shell.Media;

/// <summary>
/// Turns the bytes <see cref="EmbyNian.Emby.EmbyImageStore"/> hands back into something an
/// <c>Image</c> can show.
/// </summary>
internal static class PosterLoader
{
    /// <summary>
    /// Decodes to a bitmap sized for a card <paramref name="logicalWidth"/> device-independent pixels
    /// wide.
    /// <para>
    /// <see cref="BitmapImage.DecodePixelWidth"/> is the whole point of this method. Without it the
    /// framework decodes at the file's own size and keeps every pixel in the surface cache: a 400 px
    /// poster shown in a 170 px card is roughly a fivefold waste, and a server that ignored the width
    /// hint and returned the original 1000 px artwork is closer to thirty. A screen of forty cards is
    /// the difference between a few megabytes and a few hundred.
    /// </para>
    /// <para>
    /// <see cref="DecodePixelType.Logical"/> so the number above is in the same units as the XAML
    /// layout: the framework multiplies by the rasterization scale itself, which means the card stays
    /// sharp at 150% without this code knowing the scale.
    /// </para>
    /// </summary>
    public static async Task<BitmapImage?> DecodeAsync(byte[] bytes, int logicalWidth)
    {
        if (bytes.Length == 0) return null;

        // A WinRT stream rather than bytes.AsBuffer(): the extension method lives in a package this
        // project does not reference, and copying through a DataWriter is a few lines with no
        // dependency at all. The copy costs nothing next to the decode.
        using var stream = new InMemoryRandomAccessStream();

        var writer = new DataWriter(stream);
        writer.WriteBytes(bytes);
        await writer.StoreAsync();
        await writer.FlushAsync();

        // Detached, not disposed: disposing the writer would take the stream with it, and the decode
        // below still needs to read from it.
        writer.DetachStream();
        stream.Seek(0);

        var bitmap = new BitmapImage
        {
            DecodePixelType = DecodePixelType.Logical,
            DecodePixelWidth = Math.Max(1, logicalWidth)
        };

        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }
}

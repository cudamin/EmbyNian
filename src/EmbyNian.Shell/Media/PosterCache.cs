using System.Diagnostics.CodeAnalysis;
using EmbyNian.Infrastructure;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EmbyNian.Shell.Media;

/// <summary>
/// The posters already decoded, so scrolling back up is instant.
/// <para>
/// A card drops its bitmap when its container is recycled — it has to, or a long scroll ends up holding
/// every cover it ever passed. The cost was that scrolling back up did the whole job again: read the
/// file, decode it, wait for the picture to appear. Keeping the last few hundred here is the middle
/// answer, and the ceiling is a count because that is what the eviction policy can count.
/// </para>
/// <para>
/// Static, because the cache is about the process rather than about any one page: the same poster
/// appears on the home page, in a library grid and on a detail page's 更多类似 row, and all three should
/// find it already decoded. Only the UI thread touches it, and <see cref="LruCache{TKey,TValue}"/> locks
/// anyway.
/// </para>
/// </summary>
internal static class PosterCache
{
    /// <summary>
    /// How many decoded covers to keep. A 170-wide poster at 150% scale is about 390 KB of surface and a
    /// 320-wide still about 520 KB, so 160 entries is roughly 60 MB in the worst case and well under half
    /// that in the usual mix — set against a screen of shelves holding 30 to 40 of them at once, which
    /// means this is several screens of scroll-back rather than a second copy of the library.
    /// </summary>
    private const int Capacity = 160;

    private static readonly LruCache<string, BitmapImage> Decoded = new(Capacity);

    /// <summary>
    /// The name one decoded picture goes under. The decode width is part of it, not just the width asked
    /// of the server: the server's widths come in steps of 80, so a 170-wide poster card and a 200-wide
    /// one share a download but not a bitmap, and handing one the other's would show a picture decoded
    /// for a different size.
    /// </summary>
    public static string Key(string itemId, string imageType, string tag, int decodeWidth) =>
        $"{itemId}|{imageType}|{tag}|{decodeWidth}";

    public static bool TryGet(string key, [MaybeNullWhen(false)] out BitmapImage bitmap) =>
        Decoded.TryGet(key, out bitmap);

    public static void Remember(string key, BitmapImage bitmap) => Decoded.Set(key, bitmap);

    /// <summary>Drops everything. 设置 → 清除图片缓存 also has to forget what is already decoded.</summary>
    public static void Clear() => Decoded.Clear();
}

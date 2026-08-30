using System.Text;

namespace EmbyNian.Emby;

/// <summary>
/// Builds every URL the app needs. Separated from the HTTP client so the exact
/// shape of a stream/subtitle/image URL can be asserted in a unit test.
/// </summary>
public static class EmbyUrl
{
    public static Uri Combine(Uri apiBase, string relativePath, params (string Key, string? Value)[] query)
    {
        var builder = new StringBuilder(relativePath.TrimStart('/'));
        var first = true;
        foreach (var (key, value) in query)
        {
            if (value is null) continue;
            builder.Append(first ? '?' : '&');
            builder.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
            first = false;
        }

        return new Uri(apiBase, builder.ToString());
    }

    /// <summary>
    /// The original file, remuxed by nothing. Deliberately carries no api_key: the token
    /// travels in an <c>X-Emby-Token</c> header that mpv sends via --http-header-fields,
    /// so it stays out of proxy and server access logs.
    /// </summary>
    public static Uri Stream(Uri apiBase, string itemId, string? mediaSourceId, string? container)
    {
        var extension = string.IsNullOrWhiteSpace(container) ? "" : "." + container.Split(',')[0].Trim();
        return Combine(apiBase, $"Videos/{itemId}/stream{extension}",
            ("Static", "true"),
            ("MediaSourceId", mediaSourceId));
    }

    /// <summary>An external subtitle track extracted by the server as a standalone file.</summary>
    public static Uri Subtitle(Uri apiBase, string itemId, string mediaSourceId, int streamIndex, string? codec)
    {
        var extension = string.IsNullOrWhiteSpace(codec) ? "srt" : NormalizeSubtitleExtension(codec);
        return Combine(apiBase, $"Videos/{itemId}/{mediaSourceId}/Subtitles/{streamIndex}/Stream.{extension}");
    }

    public static Uri Image(Uri apiBase, string itemId, string imageType, string? tag, int maxWidth)
    {
        return Combine(apiBase, $"Items/{itemId}/Images/{imageType}",
            ("maxWidth", maxWidth.ToString()),
            ("quality", "90"),
            ("tag", tag));
    }

    /// <summary>
    /// A chapter still, for the seek bar's hover preview. The chapter's position in the item's
    /// chapter list is part of the path, which is why this cannot go through
    /// <see cref="Image"/>'s image-type argument.
    /// </summary>
    public static Uri ChapterImage(Uri apiBase, string itemId, int index, string? tag, int maxWidth)
    {
        return Combine(apiBase, $"Items/{itemId}/Images/Chapter/{index}",
            ("maxWidth", maxWidth.ToString()),
            ("quality", "90"),
            ("tag", tag));
    }

    public static Uri UserImage(Uri apiBase, string userId, string? tag, int maxWidth)
    {
        return Combine(apiBase, $"Users/{userId}/Images/Primary",
            ("maxWidth", maxWidth.ToString()),
            ("quality", "90"),
            ("tag", tag));
    }

    internal static string NormalizeSubtitleExtension(string codec) => codec.ToLowerInvariant() switch
    {
        "subrip" or "srt" => "srt",
        "ass" => "ass",
        "ssa" => "ssa",
        "webvtt" or "vtt" => "vtt",
        _ => "srt"
    };
}

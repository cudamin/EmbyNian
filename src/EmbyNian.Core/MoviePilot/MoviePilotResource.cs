using System.Text.Json;

namespace EmbyNian.MoviePilot;

/// <summary>
/// 一条可下载的资源（种子），从 MoviePilot <c>search/media/{id}</c> 回来的一个 <c>Context</c> 里拆出来。
/// <para>
/// 和 <see cref="MoviePilotMedia"/> 分开：那是「这是哪部片」，这是「这部片有哪些种子可下」。屏上要的是站点、
/// 标题、清晰度、体积、做种数；下载要的是把整份 <c>torrent_info</c> 原样回传（MoviePilot 的 <c>download/add</c>
/// 收的是一个完整的 <c>torrent_in</c> 对象，不是一个磁力/种子链接），所以这里留着那份原始 JSON。
/// </para>
/// </summary>
public sealed record MoviePilotResource
{
    public required string Title { get; init; }

    public string? SiteName { get; init; }

    /// <summary>体积，字节。0 表示没报。</summary>
    public long Size { get; init; }

    public int Seeders { get; init; }

    /// <summary>清晰度（如 <c>1080p</c>），来自 <c>Context.meta_info.resource_pix</c>；种子本身不带这个字段。</summary>
    public string? Resolution { get; init; }

    public string? PageUrl { get; init; }

    /// <summary>
    /// 整份 <c>torrent_info</c> 原样留着，下载时原封回传给 <c>download/add</c> 的 <c>torrent_in</c>。已 Clone，
    /// 脱离原文档、可安全长留。
    /// </summary>
    public required JsonElement TorrentInfo { get; init; }

    public string? MediaSource { get; init; }

    public string? MediaId { get; init; }

    /// <summary>体积那一段人话；0 就空着。</summary>
    public string SizeText => Size > 0 ? HumanSize(Size) : "";

    /// <summary>标题底下那一行：清晰度 · 体积 · N 个做种，有哪段写哪段。</summary>
    public string MetaLine
    {
        get
        {
            var parts = new List<string>(3);
            if (!string.IsNullOrWhiteSpace(Resolution)) parts.Add(Resolution!);
            if (SizeText.Length > 0) parts.Add(SizeText);
            if (Seeders > 0) parts.Add($"{Seeders} 个做种");
            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>字节数换成人看的 GB/MB。就近够用，不追求精确到二进制/十进制之争。</summary>
    private static string HumanSize(long bytes)
    {
        double value = bytes;
        foreach (var unit in (string[])["B", "KB", "MB", "GB", "TB"])
        {
            if (value < 1024 || unit == "TB")
                return value >= 100 || unit is "B" or "KB" ? $"{value:0} {unit}" : $"{value:0.0} {unit}";
            value /= 1024;
        }

        return $"{bytes} B";
    }
}

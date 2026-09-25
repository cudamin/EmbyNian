using System.Globalization;
using EmbyNian.Emby;

namespace EmbyNian.MoviePilot;

/// <summary>
/// 从一个 Emby 条目收集「手动整理」要带走的身份和文件清单。
/// <para>
/// 菜单里的条目来自列表查询，只有名字和类型 —— 路径、媒体源、TMDB 号都是 Fields 点名才有的，这里按
/// <c>Files + ProviderIds</c> 那一档现问一遍。季和整部剧不带自己的文件：下面每一集才是一个文件，季号/
/// 剧号另从条目上读。单集和季的 TMDB 号不可信（那是「这一季」的号），一律回溯到剧集本身 —— MoviePilot
/// 认的是剧，季集另算。
/// </para>
/// <para>
/// 文件数有上限（<see cref="MaxFiles"/>）：一部几百集的剧整批发过去，预览就成了一堵墙。到顶就截断，多出来的
/// 让用户整季去点 —— 数量在弹窗里看得见，不是静默丢。
/// </para>
/// </summary>
public static class MoviePilotTransferCollect
{
    /// <summary>一批最多带多少个文件。</summary>
    public const int MaxFiles = 30;

    /// <summary>条目要补问哪些字段：文件路径加上身份那一小撮。</summary>
    private const string Fields = EmbyFields.Files + ",ProviderIds,IndexNumber,ParentIndexNumber,SeriesId,SeriesName";

    public static async Task<MoviePilotTransferContext> CollectAsync(
        EmbyClient client, EmbyItem item, CancellationToken cancellationToken)
    {
        var detail = await client.GetItemAsync(item.Id, cancellationToken, Fields).ConfigureAwait(false);

        var series = detail;
        if (detail.Type is EmbyItemType.Episode or EmbyItemType.Season &&
            detail.SeriesId is { Length: > 0 } seriesId)
        {
            try
            {
                series = await client.GetItemAsync(seriesId, cancellationToken, "ProviderIds,Name").ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 剧条目问不到时退回手上的：身份还可能从单集自己的 ProviderIds 上来，比直接放弃强。
            }
        }

        var television = detail.Type is not EmbyItemType.Movie;
        var mediaId = TmdbId(series) ?? TmdbId(detail) ?? "";
        var season = detail.Type switch
        {
            EmbyItemType.Season => detail.IndexNumber,
            EmbyItemType.Episode => detail.ParentIndexNumber,
            _ => null
        };

        var files = await FilesOf(client, detail, cancellationToken).ConfigureAwait(false);
        return new MoviePilotTransferContext(
            television ? series.Name is { Length: > 0 } ? series.Name : detail.SeriesName ?? detail.Name : detail.Name,
            television ? MoviePilotTransferRequest.TypeSeries : MoviePilotTransferRequest.TypeMovie,
            mediaId,
            season,
            detail.Type == EmbyItemType.Episode ? detail.IndexNumber?.ToString(CultureInfo.InvariantCulture) ?? "" : "",
            files);
    }

    /// <summary>这个条目在 TMDB 上对到了没有。上弹窗之前只用来决定默认值，缺了让用户在弹窗里自己找。</summary>
    public static string? TmdbId(EmbyItem item) =>
        item.ProviderIds.FirstOrDefault(pair =>
            string.Equals(pair.Key, "Tmdb", StringComparison.OrdinalIgnoreCase)).Value;

    private static async Task<IReadOnlyList<MoviePilotTransferFile>> FilesOf(
        EmbyClient client, EmbyItem detail, CancellationToken cancellationToken)
    {
        List<EmbyItem> leaves = detail.Type switch
        {
            EmbyItemType.Movie or EmbyItemType.Episode => [detail],
            EmbyItemType.Season => await Children(client, detail.Id, cancellationToken).ConfigureAwait(false),
            EmbyItemType.Series => await Episodes(client, detail, cancellationToken).ConfigureAwait(false),
            _ => []
        };

        var files = leaves
            .SelectMany(PathsOf)
            .DistinctBy(file => file.Path, StringComparer.Ordinal)
            .Take(MaxFiles)
            .ToList();
        return files;
    }

    /// <summary>一个条目背后的文件：媒体源一个一个来，媒体源没给路径就退到条目自己的 Path。</summary>
    private static IEnumerable<MoviePilotTransferFile> PathsOf(EmbyItem item) =>
        item.MediaSources
            .Where(source => !string.IsNullOrWhiteSpace(source.Path))
            .Select(source => new MoviePilotTransferFile(
                source.Name is { Length: > 0 } ? source.Name : MoviePilotTransfer.PathName(source.Path!),
                source.Path!))
            .Append(item.Path is { Length: > 0 } path
                ? new MoviePilotTransferFile(MoviePilotTransfer.PathName(path), path)
                : null)
            .Where(file => file is not null)
            .Select(file => file!);

    private static async Task<List<EmbyItem>> Children(
        EmbyClient client, string parentId, CancellationToken cancellationToken) =>
        await client.GetItemsAsync(new ItemQuery
        {
            ParentId = parentId,
            Recursive = false,
            Fields = EmbyFields.Files
        }, cancellationToken).ConfigureAwait(false) is { } result
            ? result.Items
            : [];

    private static async Task<List<EmbyItem>> Episodes(
        EmbyClient client, EmbyItem series, CancellationToken cancellationToken)
    {
        var seasons = await client.GetSeasonsAsync(series.Id, cancellationToken).ConfigureAwait(false);
        var episodes = new List<EmbyItem>();
        foreach (var season in seasons)
            episodes.AddRange(await Children(client, season.Id, cancellationToken).ConfigureAwait(false));

        // 一季都没有（可能是查不动）：整部剧条目自己的 Path 还在，至少让 MoviePilot 看一眼那个目录。
        if (episodes.Count == 0 && series.Path is { Length: > 0 })
            return [series];

        return episodes;
    }
}

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
/// 文件数有上限（<see cref="MaxFiles"/>）：超过上限明确停止，请用户按季或单集操作，不静默截断。
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
        var television = detail.Type is not EmbyItemType.Movie;
        string? mediaId = detail.Type is EmbyItemType.Movie or EmbyItemType.Series ? TmdbId(detail) : null;
        if (detail.Type is EmbyItemType.Episode or EmbyItemType.Season &&
            detail.SeriesId is { Length: > 0 } seriesId)
        {
            try
            {
                series = await client.GetItemAsync(seriesId, cancellationToken, "ProviderIds,Name").ConfigureAwait(false);
                mediaId = TmdbId(series);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                // 集和季的 TMDB 编号不是剧编号；查询失败时留空，不能拿它们冒充整部剧。
            }
        }
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
            mediaId ?? "",
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
            .Take(MaxFiles + 1)
            .ToList();
        if (files.Count > MaxFiles)
            throw new MoviePilotException($"此条目超过 {MaxFiles} 个文件，请按季或单集整理；不会只提交前 {MaxFiles} 个文件");
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

        return episodes;
    }
}

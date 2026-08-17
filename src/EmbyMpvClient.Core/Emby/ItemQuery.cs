namespace EmbyMpvClient.Emby;

/// <summary>Field sets requested from Emby. Only names that exist in Emby's ItemFields.</summary>
public static class EmbyFields
{
    /// <summary>Enough to draw a poster card without a second round trip.</summary>
    public const string Browse = "PrimaryImageAspectRatio,Overview,ProductionYear,ChildCount,DateCreated,Genres,Tags";

    /// <summary>
    /// Adds the media sources needed to build a stream URL and list tracks, plus the chapter
    /// markers the seek bar previews.
    /// </summary>
    public const string Detail =
        "PrimaryImageAspectRatio,Overview,ProductionYear,ChildCount,DateCreated,Genres,Tags,MediaSources,Path,OfficialRating,CommunityRating,ParentId,Chapters";
}

public static class EmbySortBy
{
    public const string Name = "SortName";
    public const string DateAdded = "DateCreated";
    public const string ReleaseDate = "PremiereDate";
    public const string Rating = "CommunityRating";
    public const string Random = "Random";
    public const string PlayCount = "PlayCount";

    /// <summary>Label/value pairs for a sort dropdown.</summary>
    public static readonly (string Label, string Value)[] Choices =
    [
        ("名称", Name),
        ("最近添加", DateAdded),
        ("上映日期", ReleaseDate),
        ("评分", Rating),
        ("随机", Random)
    ];
}

/// <summary>
/// A browse/search request. Kept as a value object with a testable
/// <see cref="ToQueryString"/> so paging and filter combinations can be asserted
/// without a server.
/// </summary>
public sealed class ItemQuery
{
    public string? ParentId { get; init; }

    public string? SearchTerm { get; init; }

    public bool Recursive { get; init; }

    public IReadOnlyList<string> IncludeItemTypes { get; init; } = [];

    public string Fields { get; init; } = EmbyFields.Browse;

    public string SortBy { get; init; } = EmbySortBy.Name;

    public bool Descending { get; init; }

    public int StartIndex { get; init; }

    public int Limit { get; init; } = 100;

    /// <summary>null = no filter, true = only watched, false = only unwatched.</summary>
    public bool? IsPlayed { get; init; }

    public string? Genre { get; init; }

    /// <summary>"Video" restricts a recursive search to things that can actually be played.</summary>
    public string? MediaTypes { get; init; }

    public static ItemQuery Children(string parentId, int startIndex, int limit, string sortBy, bool descending) => new()
    {
        ParentId = parentId,
        StartIndex = startIndex,
        Limit = limit,
        SortBy = sortBy,
        Descending = descending
    };

    public static ItemQuery Search(string term, int startIndex, int limit) => new()
    {
        SearchTerm = term,
        Recursive = true,
        StartIndex = startIndex,
        Limit = limit,
        IncludeItemTypes = [EmbyItemType.Movie, EmbyItemType.Series, EmbyItemType.Episode, EmbyItemType.Video, EmbyItemType.MusicVideo],
        SortBy = EmbySortBy.Name
    };

    public (string Key, string? Value)[] ToParameters()
    {
        var parameters = new List<(string, string?)>(12)
        {
            ("SortBy", SortBy),
            ("SortOrder", Descending ? "Descending" : "Ascending"),
            ("Fields", Fields),
            ("StartIndex", StartIndex.ToString()),
            ("Limit", Limit.ToString()),
            ("Recursive", Recursive ? "true" : "false")
        };

        if (!string.IsNullOrWhiteSpace(ParentId)) parameters.Add(("ParentId", ParentId));
        if (!string.IsNullOrWhiteSpace(SearchTerm)) parameters.Add(("SearchTerm", SearchTerm!.Trim()));
        if (IncludeItemTypes.Count > 0) parameters.Add(("IncludeItemTypes", string.Join(',', IncludeItemTypes)));
        if (IsPlayed is { } played) parameters.Add(("Filters", played ? "IsPlayed" : "IsUnplayed"));
        if (!string.IsNullOrWhiteSpace(Genre)) parameters.Add(("Genres", Genre));
        if (!string.IsNullOrWhiteSpace(MediaTypes)) parameters.Add(("MediaTypes", MediaTypes));

        return [.. parameters];
    }

    public string ToQueryString()
    {
        var pairs = ToParameters()
            .Where(pair => pair.Value is not null)
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}");
        return string.Join('&', pairs);
    }
}

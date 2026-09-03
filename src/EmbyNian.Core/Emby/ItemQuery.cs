namespace EmbyNian.Emby;

/// <summary>
/// How a library grid lays its rows out — Emby Theater's imageType setting, in the three shapes this
/// client draws. Which shapes exist is a fact about the catalogue; which one a library opens in is the
/// user's, remembered per library root alongside the sort (see <c>UiSettings.Views</c>).
/// </summary>
public enum LibraryView
{
    /// <summary>海报: the 2:3 poster grid every library opens in by default.</summary>
    Poster,

    /// <summary>缩略图: the 16:9 still grid, Emby's thumb view.</summary>
    Thumb,

    /// <summary>列表: compact rows, Emby's list view — a small still and the row's title and info.</summary>
    List
}

/// <summary>Field sets requested from Emby. Only names that exist in Emby's ItemFields.</summary>
public static class EmbyFields
{
    /// <summary>
    /// Enough to draw a poster card — or an episode list row — without a second round trip.
    /// <para>
    /// <c>PremiereDate</c> is here for the episode list: 「2016/4/17  ·  46 分钟」 is the reference's own
    /// row, and an air date the response did not carry is a row that silently loses half its line.
    /// </para>
    /// </summary>
    public const string Browse = "PrimaryImageAspectRatio,Overview,ProductionYear,PremiereDate,ChildCount,DateCreated,Genres,Tags";

    /// <summary>
    /// Adds the media sources needed to build a stream URL and list tracks, plus the chapter
    /// markers the seek bar previews.
    /// <para>
    /// <c>Studios</c> and <c>EndDate</c> are the facts line's 「TBS」 and its 「2016 – 2018」. Both are
    /// gated: a request that does not name them gets the item back with an empty studio list and no
    /// end date at all, whatever the server actually holds — which is why they are spelled out here
    /// rather than assumed to ride along with the rest of the item.
    /// </para>
    /// <para>
    /// <c>CriticRating</c> and <c>ProviderIds</c> are the 评分来源 setting's two: the first is the only rating
    /// on an Emby item that is independent of <c>CommunityRating</c>, and the second is the only thing that says
    /// which scraper this item was matched against. Both are named here rather than assumed — and naming a field
    /// the server's own <c>Options</c> list does not enumerate is safe, which this line already demonstrates:
    /// eight of the names in it (ProductionYear, PremiereDate, EndDate, ChildCount, Tags, MediaSources,
    /// OfficialRating, CommunityRating) are not on that list either, and this client has always worked.
    /// </para>
    /// <para>
    /// Only this one, deliberately. <see cref="Browse"/> is the list request and nothing in a list shows a
    /// rating; <see cref="Detail"/>'s callers fetch one item at a time.
    /// </para>
    /// </summary>
    public const string Detail =
        "PrimaryImageAspectRatio,Overview,ProductionYear,PremiereDate,EndDate,ChildCount,DateCreated,Genres,Tags,Studios,MediaSources,Path,OfficialRating,CommunityRating,CriticRating,ProviderIds,ParentId,Chapters";

    /// <summary>
    /// 只要「这个条目背后是哪个文件」—— 下载到设备和搜索字幕都只需要这一样。
    /// <para>
    /// 单独一份而不是借 <see cref="Detail"/>：下载一整部剧要一次问回二十几集，而 <see cref="Detail"/> 里的
    /// 章节表能把那一趟的响应撑到几百 KB，其中没有一个字节用得上。
    /// </para>
    /// </summary>
    public const string Files = "MediaSources,Path";
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

    /// <summary>
    /// One credited person, for 「这个人还演过什么」. Sent as Emby's <c>PersonIds</c>.
    /// <para>
    /// Singular, and named for the one value it carries rather than for the parameter it becomes. The
    /// server's own openapi.json describes this key as 「only those containing the specified person」 and,
    /// alone among the list-shaped keys declared beside it, names no delimiter — <c>PersonTypes</c> says
    /// comma and <c>Studios</c> says pipe, this one says nothing. Guessing wrong would not fail: Emby
    /// drops a query value it cannot parse and answers with the whole library, so a cast card would open
    /// onto every film on the server under one actor's name. One id needs no delimiter, and one id is
    /// what a cast card is.
    /// </para>
    /// </summary>
    public string? PersonId { get; init; }

    /// <summary>
    /// 需求 5：the filter panel's selection, or null for none. Merged with <see cref="IsPlayed"/> and
    /// <see cref="Genre"/> rather than sent alongside them: those two already occupy <c>Filters</c> and
    /// <c>Genres</c>, and a query string with the same key twice is a query where Emby reads one of them
    /// and drops the other.
    /// </summary>
    public ItemFilters? Filters { get; init; }

    /// <summary>"Video" restricts a recursive search to things that can actually be played.</summary>
    public string? MediaTypes { get; init; }

    /// <summary>
    /// 字母跳转：<c>NameStartsWithOrGreater</c>, the letter bar's query. Null for the normal browse.
    /// <para>
    /// Only ever used with <see cref="Limit"/> 0 and no <see cref="Fields"/> — the answer wanted is a
    /// count, not rows — so it never appears in a paged query. Its own property rather than a
    /// string-typed flag because the server drops an empty value silently and 「jump to the empty
    /// string」 must not be expressible.
    /// </para>
    /// </summary>
    public string? NameStartsWith { get; init; }

    public static ItemQuery Children(string parentId, int startIndex, int limit, string sortBy, bool descending) => new()
    {
        ParentId = parentId,
        StartIndex = startIndex,
        Limit = limit,
        SortBy = sortBy,
        Descending = descending
    };

    /// <summary>
    /// The item types a library kind flattens to, or empty when it has to be browsed folder by folder.
    /// <para>
    /// A movies library whose files sit in sub-directories answers a plain child query with those
    /// directories, so a grid built from it shows a folder's name and no cover art where each film
    /// should be. Emby's own clients ask a library root recursively for the types it exists to hold;
    /// only the levels below it are browsed as folders.
    /// </para>
    /// <para>
    /// <see cref="EmbyItemType.Video"/> is in the list on purpose: a file Emby could not match to
    /// anything is typed Video, not Movie, and those are exactly the items whose name is still a
    /// release filename. Dropping them from the grid would hide the very thing worth fixing.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> FlattenedTypes(string? collectionType) => collectionType switch
    {
        "movies" => [EmbyItemType.Movie, EmbyItemType.Video, EmbyItemType.BoxSet],
        "tvshows" => [EmbyItemType.Series, EmbyItemType.Video],
        "musicvideos" => [EmbyItemType.MusicVideo],
        "trailers" => ["Trailer"],
        _ => []
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

    /// <summary>
    /// What a person's credits are allowed to be. The same list a search spans, for the same reason: both
    /// sweep the whole server rather than one library, so neither can lean on a collection type to say
    /// what it should be finding.
    /// <para>
    /// <see cref="EmbyItemType.Episode"/> is included deliberately. A guest star is credited on the
    /// episodes they appear in and often not on the series at all, so dropping episodes would answer 「没有
    /// 内容」 for precisely the people whose credits are the most interesting to follow.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> CreditedTypes { get; } =
    [
        EmbyItemType.Movie,
        EmbyItemType.Series,
        EmbyItemType.Episode,
        EmbyItemType.Video,
        EmbyItemType.MusicVideo
    ];

    public (string Key, string? Value)[] ToParameters()
    {
        var parameters = new List<(string, string?)>(16)
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
        if (!string.IsNullOrWhiteSpace(PersonId)) parameters.Add(("PersonIds", PersonId!.Trim()));
        if (IncludeItemTypes.Count > 0) parameters.Add(("IncludeItemTypes", string.Join(',', IncludeItemTypes)));
        if (!string.IsNullOrWhiteSpace(MediaTypes)) parameters.Add(("MediaTypes", MediaTypes));
        if (!string.IsNullOrWhiteSpace(NameStartsWith)) parameters.Add(("NameStartsWithOrGreater", NameStartsWith!.Trim()));

        foreach (var filter in Filtering()) parameters.Add(filter);

        return [.. parameters];
    }

    /// <summary>
    /// The filtering parameters, one entry per key.
    /// <para>
    /// Several filters share a key — the four 状态 options are all <c>Filters</c>, the four disc types
    /// all <c>VideoTypes</c> — and Emby wants them as one parameter holding a delimited list. Which
    /// delimiter is per key and not negotiable: see <see cref="EmbyFilterBy.Delimiter"/>.
    /// </para>
    /// <para>
    /// A key that takes only one value keeps the first, which is catalogue order and therefore stable.
    /// That case only arises from a contradiction the panel prevents (高清 and 标清 are mutually
    /// exclusive) and settings.json could still contain from an older build, where answering 高清 is a
    /// better failure than sending <c>IsHD=true,false</c> for the server to reject.
    /// </para>
    /// </summary>
    private IEnumerable<(string Key, string? Value)> Filtering()
    {
        var pairs = new List<(string Key, string Value)>(8);

        if (IsPlayed is { } played) pairs.Add((EmbyFilterBy.Keys.Filters, played ? "IsPlayed" : "IsUnplayed"));
        if (!string.IsNullOrWhiteSpace(Genre)) pairs.Add((EmbyFilterBy.Keys.Genres, Genre!.Trim()));
        if (Filters is not null) pairs.AddRange(Filters.Contributions());

        // GroupBy yields groups in the order their keys were first seen, so the query string stays the
        // same from one call to the next.
        foreach (var group in pairs.GroupBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var values = group.Select(pair => pair.Value).Distinct(StringComparer.Ordinal).ToList();
            var delimiter = EmbyFilterBy.Delimiter(group.Key);

            yield return (group.Key, delimiter is null ? values[0] : string.Join(delimiter, values));
        }
    }

    public string ToQueryString()
    {
        var pairs = ToParameters()
            .Where(pair => pair.Value is not null)
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}");
        return string.Join('&', pairs);
    }
}

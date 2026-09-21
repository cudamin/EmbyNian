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

    /// <summary>
    /// true = 只收<b>有播放进度</b>的，作为 <c>Filters=IsResumable</c> 发出去 —— 和筛选面板里那颗
    /// 「可继续播放」（<see cref="EmbyFilterBy"/> 里 id <c>resumable</c> 那一格）是同一个词，出处也是同一份
    /// 服务器的 OpenAPI 文档。
    /// <para>
    /// Emby 没有「不可续播」的反向值，所以它是 <see langword="bool"/> 而不是可空：false 就是不发这一条，
    /// 与「没有这个概念」同义。它和 <see cref="IsPlayed"/> 一样占 <c>Filters</c> 键，多个值由
    /// <see cref="Filtering"/> 按键分组、逗号连接 —— 面板再勾一颗「收藏」就是
    /// <c>Filters=IsResumable,IsFavorite</c>，Emby 认逗号分隔的这一键。
    /// </para>
    /// <para>
    /// 2026-09-14「继续观看里面为什么里面东西那么多」：那一页从前用 <see cref="IsPlayed"/>=false
    /// （<c>IsUnplayed</c>）装「没看完的」，把一部都没开过头的也装了进来 —— 一整面墙。Emby 自己那条
    /// Resume 接口（<c>EmbyClient.GetResumeAsync</c>，主页那一排的来源）的语义是「有播放进度」，不是
    /// 「没看完」；这一页要的正是主页那一排的整个清单，判据必须跟它对齐。
    /// </para>
    /// </summary>
    public bool IsResumable { get; init; }

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
    /// 主页「继续观看」那一排点进去的那张网格 —— 2026-09-14「新增点击图中红框的标题可以进入对应的页面」。
    /// <para>
    /// 这一页要的是主页那一排的整个清单：主页上每一排只摆几项，点标题进去看全部。主页那一排走的是服务器
    /// 自己的 Resume 接口（<c>EmbyClient.GetResumeAsync</c>），这一页走的通用查询必须和它装同一批东西 ——
    /// 2026-09-14「继续观看里面为什么里面东西那么多」的病根就是两边没对齐：这里从前用
    /// <see cref="IsPlayed"/>=false（<c>Filters=IsUnplayed</c>）装「没看完的」，把一部都没开过头的也算了进去，
    /// 点进来是一整面海报墙；而那条接口的语义是「有播放进度」（<see cref="IsResumable"/>）。现在判据对齐了，
    /// 主页那排是六项，这一页就是这六项。
    /// </para>
    /// <para>
    /// 这里不走那条专用接口，是为了让这一页和搜索、类型、演职人员那几页同住 <c>LibraryPage</c>：那一页有排序、
    /// 有筛选、有翻页、有记忆键，另开一条专用接口等于把这些重写一遍。
    /// </para>
    /// <para>
    /// <see cref="MediaTypes"/> 还是写上 <c>Video</c>。<see cref="SweptTypes"/> 里的 <see cref="EmbyItemType.Series"/>
    /// 留着无害 —— 播放进度属于文件，一部剧自己没有进度条，<see cref="IsResumable"/> 这一刀天然把它切在外面。
    /// </para>
    /// </summary>
    public static ItemQuery Resume(
        int startIndex,
        int limit,
        string sortBy,
        bool descending,
        ItemFilters? filters,
        IReadOnlyList<string>? types = null) => new()
        {
            Recursive = true,
            IsResumable = true,
            IncludeItemTypes = types ?? SweptTypes,
            MediaTypes = "Video",
            SortBy = sortBy,
            Descending = descending,
            StartIndex = startIndex,
            Limit = limit,
            Filters = filters
        };

    /// <summary>
    /// 主页「接下来看」那一排点进去的那张网格。判据是「这部剧一集都没看过」—— Emby 的 NextUp 语义落到通用
    /// 查询上就是 <see cref="IsPlayed"/>=false（<c>Filters=IsUnplayed</c>）加一份把单集排除掉的类型名单：一集
    /// 继承整部剧的已看状态，单集留在名单里，一部看了一半的剧也会满足「没看完」，那一页就和「继续观看」长成
    /// 同一页了。
    /// <para>
    /// 2026-09-14 从 <see cref="Resume"/> 里拆出来的另一半：那一页的判据从「没看完」换成了「有播放进度」，而
    /// 这一页的语义本来就不该跟着变 —— 两页问的是两个问题，共用一个工厂只会让改其中一个的时候悄悄动了另一个。
    /// </para>
    /// </summary>
    /// <param name="types">这一页认哪几种条目类型；<c>LibraryViewModel.BuildQuery</c> 递的是
    /// <c>[Series, Movie, Video]</c>，理由见上面那段。</param>
    public static ItemQuery NextUp(
        int startIndex,
        int limit,
        string sortBy,
        bool descending,
        ItemFilters? filters,
        IReadOnlyList<string> types) => new()
        {
            Recursive = true,
            IsPlayed = false,
            IncludeItemTypes = types,
            MediaTypes = "Video",
            SortBy = sortBy,
            Descending = descending,
            StartIndex = startIndex,
            Limit = limit,
            Filters = filters
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

    /// <summary>
    /// The types a whole-server sweep is allowed to come back with — 搜索 and 演职人员, which are the only
    /// two queries that span every library at once. Neither has a <c>ParentId</c>, so neither can lean on a
    /// collection type to say what it should be finding; both have to name the types outright, and there is
    /// no reason for the two answers to differ.
    /// <para>
    /// <see cref="EmbyItemType.Episode"/> is included deliberately. A guest star is credited on the
    /// episodes they appear in and often not on the series at all, so dropping episodes would answer 「没有
    /// 内容」 for precisely the people whose credits are the most interesting to follow.
    /// </para>
    /// <para>
    /// <see cref="EmbyItemType.MusicVideo"/> is deliberately absent, and this is the half that was wrong
    /// until 2026-09-05. <see cref="EmbyItemType.IsMusic"/> counts it as music, and the shell hides music
    /// everywhere it can be reached — music libraries never enter the navigation pane, and the home rows
    /// drop their music items. A sweep is the one query with no library to be filtered by, so asking for
    /// the type here was the single path by which a card the shell is built to hide could still land on
    /// screen: a person with a music-video credit got one in their grid. A test pins this list against
    /// <see cref="EmbyItemType.IsMusic"/> rather than against a second copy of the four names — put a
    /// hidden type back in and it goes red, which is the moment somebody should think about it.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> SweptTypes { get; } =
    [
        EmbyItemType.Movie,
        EmbyItemType.Series,
        EmbyItemType.Episode,
        EmbyItemType.Video
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
        if (IsResumable) pairs.Add((EmbyFilterBy.Keys.Filters, "IsResumable"));
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

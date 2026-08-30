namespace EmbyNian.Emby;

/// <summary>
/// One on/off entry in the filter panel.
/// <para>
/// Every <see cref="Key"/> and <see cref="Value"/> here is transcribed from the server's own OpenAPI
/// document (<c>/emby/openapi.json</c>, which 4.9.x serves without a token), not from Jellyfin's
/// documentation or from Emby's web client. That matters more than it sounds: Emby ignores a query
/// parameter it does not recognise, so a filter with a misspelled or borrowed key does not fail — it
/// silently returns the unfiltered library, which looks like a bug in the panel.
/// </para>
/// </summary>
public sealed record EmbyFilterOption
{
    /// <summary>
    /// Stable identity, used in the selection set and written to settings.json. Deliberately not the
    /// <see cref="Value"/>: two options can share a value's key with opposite values (高清 and 标清 are
    /// both <c>IsHD</c>), and a stored selection has to survive a relabelling.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>Chinese label, matching Emby's own zh-CN wording where it has one.</summary>
    public required string Label { get; init; }

    /// <summary>Which section of the panel this belongs under; see <see cref="EmbyFilterBy.Groups"/>.</summary>
    public required string Group { get; init; }

    /// <summary>The query parameter name.</summary>
    public required string Key { get; init; }

    /// <summary>The value to send for that parameter.</summary>
    public required string Value { get; init; }

    /// <summary>
    /// Options this one turns off when it is turned on. 已播放 and 未播放 are not a contradiction the
    /// server rejects — it answers with nothing at all, which reads as an empty library.
    /// </summary>
    public IReadOnlyList<string> Excludes { get; init; } = [];

    /// <summary>Presets this option is hidden for, because the server cannot answer it for those rows.</summary>
    public IReadOnlyList<string> NotFor { get; init; } = [];

    /// <summary>When non-empty, the only presets this option is offered for.</summary>
    public IReadOnlyList<string> OnlyFor { get; init; } = [];

    /// <summary>Lowest server version that understands <see cref="Key"/>, or null for all of them.</summary>
    public string? MinServerVersion { get; init; }

    /// <summary>
    /// Whether to offer this option at all. Both unknowns fail open, for the same reasons as
    /// <see cref="EmbySortOption.AppliesTo"/>: an unrecognised preset is a mixed folder or a search
    /// result, and an unknown version means the probe has not answered yet.
    /// </summary>
    public bool AppliesTo(string? preset, Version? serverVersion)
    {
        if (MinServerVersion is { } minimum
            && serverVersion is not null
            && Version.TryParse(minimum, out var required)
            && serverVersion < required)
            return false;

        if (preset is null) return OnlyFor.Count == 0;

        if (OnlyFor.Count > 0 && !OnlyFor.Contains(preset)) return false;

        return !NotFor.Contains(preset);
    }
}

/// <summary>
/// A filter section whose choices come from the server rather than from this file: which genres, tags
/// and years the rows on screen actually have.
/// <para>
/// <see cref="Key"/> doubles as the endpoint name — <c>/Genres</c>, <c>/Tags</c> and <c>/Years</c> each
/// take the whole of <c>/Items</c>'s query and answer with the distinct values inside it, so one method
/// fetches all three and the panel stays a loop.
/// </para>
/// </summary>
public sealed record EmbyFilterList
{
    public required string Label { get; init; }

    public required string Key { get; init; }
}

/// <summary>
/// The filter catalogue. <see cref="All"/> is every on/off option; <see cref="For"/> is the subset a
/// particular grid should show, and <see cref="Lists"/> the sections filled in from the server.
/// <para>
/// Four things Emby's web client offers are deliberately absent, each for a reason worth keeping:
/// </para>
/// <list type="bullet">
///   <item>
///     喜欢 / 不喜欢 (<c>Filters=Likes,Dislikes</c>) — nothing in this client can set a thumb rating, and
///     a filter for a state the user cannot reach is a filter that always comes back empty.
///   </item>
///   <item>
///     缺失剧集 (<c>IsMissing</c>) — Jellyfin has it, Emby 4.9 does not. It is not in the server's
///     parameter list, so it would be silently dropped.
///   </item>
///   <item>
///     4K (<c>Is4K</c>) — same: the server understands <c>IsHD</c> and <c>Is3D</c>, and nothing else
///     about resolution. 源分辨率 in the sort menu is the honest way to get at it.
///   </item>
///   <item>
///     分级 (<c>OfficialRatings</c>) — the parameter exists and the panel could send it, but there is no
///     endpoint that says which ratings the rows on screen have. <c>/Localization/ParentalRatings</c> is
///     every rating system in the world at once, which is a worse list than no list.
///   </item>
/// </list>
/// </summary>
public static class EmbyFilterBy
{
    /// <summary>Panel section titles, in the order they should appear.</summary>
    public static class Groups
    {
        public const string State = "状态";
        public const string Features = "特色";
        public const string Quality = "画质";
        public const string Source = "媒体来源";
        public const string Series = "剧集";

        public static readonly string[] Order = [State, Features, Quality, Source, Series];
    }

    /// <summary>The query parameter names this file uses, so a typo is a compile error once rather than a silent no-op.</summary>
    public static class Keys
    {
        public const string Filters = "Filters";
        public const string Genres = "Genres";
        public const string Tags = "Tags";
        public const string Years = "Years";
        public const string VideoTypes = "VideoTypes";
        public const string SeriesStatus = "SeriesStatus";
        public const string HasSubtitles = "HasSubtitles";
        public const string HasTrailer = "HasTrailer";
        public const string HasSpecialFeature = "HasSpecialFeature";
        public const string HasThemeSong = "HasThemeSong";
        public const string HasThemeVideo = "HasThemeVideo";
        public const string HasOverview = "HasOverview";
        public const string IsHD = "IsHD";
        public const string Is3D = "Is3D";
        public const string IsUnaired = "IsUnaired";
    }

    /// <summary>
    /// How several values for one key are joined, or null when the key takes exactly one.
    /// <para>
    /// Emby is not consistent about this and the difference is not cosmetic: a pipe sent where a comma
    /// belongs is read as part of the value, so <c>Genres=动画,科幻</c> looks for one genre literally
    /// named 「动画,科幻」 and finds nothing. The split below is quoted from the parameter descriptions in
    /// the server's own OpenAPI document — 「pipe delimeted」 for the ones that name things, 「comma
    /// delimeted」 for the ones that name enum members.
    /// </para>
    /// </summary>
    public static string? Delimiter(string key) => key switch
    {
        Keys.Genres or Keys.Tags or "OfficialRatings" or "Studios" => "|",
        Keys.Filters or Keys.Years or Keys.VideoTypes or Keys.SeriesStatus => ",",
        _ => null
    };

    /// <summary>
    /// Shared by the four <c>VideoTypes</c> options; a folder has no container.
    /// <para>
    /// Declared before <see cref="All"/> on purpose: static initialisers run in declaration order, so a
    /// field used by the list below and written after it would still be null when the list is built.
    /// </para>
    /// </summary>
    private static readonly string[] SourceNotFor =
    [
        EmbySortPreset.Series, EmbySortPreset.BoxSet, EmbySortPreset.Album,
        EmbySortPreset.Song, EmbySortPreset.Photo
    ];

    /// <summary>
    /// Every on/off option, in panel order.
    /// <para>
    /// The exclusion lists say what the server can answer, not what sounds sensible. <c>HasSubtitles</c>
    /// asked of a tvshows library is asked of the Series rows, which have no media streams of their own,
    /// and comes back empty for a library full of subtitled episodes — so it is offered inside a season
    /// and not at the library root.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<EmbyFilterOption> All =
    [
        new()
        {
            Id = "played",
            Label = "已播放",
            Group = Groups.State,
            Key = Keys.Filters,
            Value = "IsPlayed",
            Excludes = ["unplayed"]
        },
        new()
        {
            Id = "unplayed",
            Label = "未播放",
            Group = Groups.State,
            Key = Keys.Filters,
            Value = "IsUnplayed",
            Excludes = ["played"]
        },
        new()
        {
            Id = "resumable",
            Label = "可继续播放",
            Group = Groups.State,
            Key = Keys.Filters,
            Value = "IsResumable",
            // A resume position belongs to a file. A series has none of its own, and asking for one
            // returns nothing however many part-watched episodes it contains.
            NotFor =
            [
                EmbySortPreset.Series, EmbySortPreset.BoxSet,
                EmbySortPreset.Album, EmbySortPreset.Photo
            ]
        },
        new()
        {
            Id = "favorite",
            Label = "收藏",
            Group = Groups.State,
            Key = Keys.Filters,
            Value = "IsFavorite"
        },
        new()
        {
            Id = "subtitles",
            Label = "有字幕",
            Group = Groups.Features,
            Key = Keys.HasSubtitles,
            Value = "true",
            NotFor =
            [
                EmbySortPreset.Series, EmbySortPreset.BoxSet, EmbySortPreset.Album,
                EmbySortPreset.Song, EmbySortPreset.Photo
            ]
        },
        new()
        {
            Id = "trailer",
            Label = "有预告片",
            Group = Groups.Features,
            Key = Keys.HasTrailer,
            Value = "true",
            NotFor =
            [
                EmbySortPreset.Episode, EmbySortPreset.Album,
                EmbySortPreset.Song, EmbySortPreset.Photo
            ]
        },
        new()
        {
            Id = "special",
            Label = "有花絮",
            Group = Groups.Features,
            Key = Keys.HasSpecialFeature,
            Value = "true",
            NotFor =
            [
                EmbySortPreset.Episode, EmbySortPreset.Album,
                EmbySortPreset.Song, EmbySortPreset.Photo
            ]
        },
        new()
        {
            Id = "themeSong",
            Label = "有主题曲",
            Group = Groups.Features,
            Key = Keys.HasThemeSong,
            Value = "true",
            NotFor = [EmbySortPreset.Episode, EmbySortPreset.Song, EmbySortPreset.Photo]
        },
        new()
        {
            Id = "themeVideo",
            Label = "有主题视频",
            Group = Groups.Features,
            Key = Keys.HasThemeVideo,
            Value = "true",
            NotFor = [EmbySortPreset.Episode, EmbySortPreset.Song, EmbySortPreset.Photo]
        },
        new()
        {
            Id = "overview",
            Label = "有简介",
            Group = Groups.Features,
            Key = Keys.HasOverview,
            Value = "true"
        },
        new()
        {
            Id = "hd",
            Label = "高清",
            Group = Groups.Quality,
            Key = Keys.IsHD,
            Value = "true",
            Excludes = ["sd"],
            NotFor =
            [
                EmbySortPreset.Series, EmbySortPreset.BoxSet, EmbySortPreset.Album,
                EmbySortPreset.Song, EmbySortPreset.Photo
            ]
        },
        new()
        {
            Id = "sd",
            Label = "标清",
            Group = Groups.Quality,
            Key = Keys.IsHD,
            Value = "false",
            Excludes = ["hd"],
            NotFor =
            [
                EmbySortPreset.Series, EmbySortPreset.BoxSet, EmbySortPreset.Album,
                EmbySortPreset.Song, EmbySortPreset.Photo
            ]
        },
        new()
        {
            Id = "3d",
            Label = "3D",
            Group = Groups.Quality,
            Key = Keys.Is3D,
            Value = "true",
            NotFor =
            [
                EmbySortPreset.Series, EmbySortPreset.BoxSet, EmbySortPreset.Album,
                EmbySortPreset.Song, EmbySortPreset.Photo
            ]
        },
        // VideoType's members, spelled the way the enum is rather than the way the parameter's own
        // description lowercases them: Emby parses either, and the canonical casing is what its other
        // clients send.
        new()
        {
            Id = "bluray",
            Label = "蓝光原盘",
            Group = Groups.Source,
            Key = Keys.VideoTypes,
            Value = "BluRay",
            NotFor = SourceNotFor
        },
        new()
        {
            Id = "dvd",
            Label = "DVD",
            Group = Groups.Source,
            Key = Keys.VideoTypes,
            Value = "Dvd",
            NotFor = SourceNotFor
        },
        new()
        {
            Id = "iso",
            Label = "镜像文件",
            Group = Groups.Source,
            Key = Keys.VideoTypes,
            Value = "Iso",
            NotFor = SourceNotFor
        },
        new()
        {
            Id = "videoFile",
            Label = "普通视频文件",
            Group = Groups.Source,
            Key = Keys.VideoTypes,
            Value = "VideoFile",
            NotFor = SourceNotFor
        },
        new()
        {
            Id = "continuing",
            Label = "连载中",
            Group = Groups.Series,
            Key = Keys.SeriesStatus,
            Value = "Continuing",
            OnlyFor = [EmbySortPreset.Series]
        },
        new()
        {
            Id = "ended",
            Label = "已完结",
            Group = Groups.Series,
            Key = Keys.SeriesStatus,
            Value = "Ended",
            OnlyFor = [EmbySortPreset.Series]
        },
        new()
        {
            Id = "unaired",
            Label = "未播出",
            Group = Groups.Series,
            Key = Keys.IsUnaired,
            Value = "true",
            OnlyFor = [EmbySortPreset.Episode, EmbySortPreset.Season]
        }
    ];

    /// <summary>
    /// The sections whose choices are read off the server. Order matters: 类型 first because it is the
    /// one anybody uses, 年份 last because it is the longest.
    /// </summary>
    public static readonly IReadOnlyList<EmbyFilterList> Lists =
    [
        new() { Label = "类型", Key = Keys.Genres },
        new() { Label = "标签", Key = Keys.Tags },
        new() { Label = "年份", Key = Keys.Years }
    ];

    /// <summary>The options a grid should offer, in panel order.</summary>
    public static IReadOnlyList<EmbyFilterOption> For(string? preset, Version? serverVersion = null) =>
        [.. All.Where(option => option.AppliesTo(preset, serverVersion))];

    /// <summary>The catalogue entry for a stored id, or null if it is no longer offered.</summary>
    public static EmbyFilterOption? Find(string? id) =>
        id is null ? null : All.FirstOrDefault(option => option.Id == id);

    /// <summary>The label to show for an id, falling back to the id itself.</summary>
    public static string LabelOf(string? id) => Find(id)?.Label ?? id ?? "";
}

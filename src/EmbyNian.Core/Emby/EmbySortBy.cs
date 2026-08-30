namespace EmbyNian.Emby;

/// <summary>
/// Which kind of thing a grid is listing. Emby calls this the display preset, and uses it to drop
/// sort keys that cannot mean anything for the rows on screen: a bitrate for a series, a resolution
/// for an album, a critic rating for a photo.
/// <para>
/// The values are Emby's own item type names so the exclusion lists in <see cref="EmbySortBy"/> can
/// be read straight across from its list controller.
/// </para>
/// </summary>
public static class EmbySortPreset
{
    public const string Movie = "Movie";
    public const string Series = "Series";
    public const string Season = "Season";
    public const string Episode = "Episode";
    public const string Album = "MusicAlbum";
    public const string Song = "Audio";
    public const string Photo = "Photo";
    public const string Video = "Video";
    public const string MusicVideo = "MusicVideo";
    public const string Trailer = "Trailer";
    public const string BoxSet = "BoxSet";

    /// <summary>
    /// The preset for a library root, or null when the collection type says nothing useful and every
    /// general key should stay on offer.
    /// </summary>
    public static string? ForCollection(string? collectionType) => collectionType switch
    {
        "movies" => Movie,
        "tvshows" => Series,
        "music" => Album,
        "musicvideos" => MusicVideo,
        "trailers" => Trailer,
        "photos" => Photo,
        "homevideos" => Video,
        "boxsets" => BoxSet,
        _ => null
    };

    /// <summary>
    /// The preset for the children of an item. A series lists seasons and a season lists episodes,
    /// and both want keys a library root does not; anything else is a mixed folder, where null keeps
    /// the whole general set on offer.
    /// </summary>
    public static string? ForChildrenOf(string? parentType) => parentType switch
    {
        EmbyItemType.Series => Season,
        EmbyItemType.Season => Episode,
        EmbyItemType.BoxSet => Movie,
        _ => null
    };
}

/// <summary>
/// One entry in the sort menu.
/// <para>
/// Transcribed from Emby Theater's own list controller rather than invented, so the composite
/// tie-breakers (<c>DateCreated,SortName</c> and not a bare <c>DateCreated</c>), the per-key default
/// direction and the exclusions are the server's behaviour instead of a guess at it. Two of them are
/// worth knowing about: 媒体码率 sorts on <c>TotalBitrate</c>, not on a video-only bitrate, and
/// 源分辨率 only exists from server 4.7.3.
/// </para>
/// </summary>
public sealed record EmbySortOption
{
    /// <summary>Chinese label, taken from Emby's own zh-CN string table.</summary>
    public required string Label { get; init; }

    /// <summary>The <c>SortBy</c> value, commas and all.</summary>
    public required string Value { get; init; }

    /// <summary>Which section of the menu this belongs under; see <see cref="EmbySortBy.Groups"/>.</summary>
    public required string Group { get; init; }

    /// <summary>
    /// The direction to switch to when this key is chosen. Newest first for a date, A to Z for a
    /// name: picking 加入日期 and getting the oldest thing in the library is never what was meant.
    /// </summary>
    public bool DescendingByDefault { get; init; }

    /// <summary>Presets this key is hidden for, because Emby returns null for them.</summary>
    public IReadOnlyList<string> NotFor { get; init; } = [];

    /// <summary>When non-empty, the only presets this key is offered for.</summary>
    public IReadOnlyList<string> OnlyFor { get; init; } = [];

    /// <summary>Presets that sort on something else, keyed by preset.</summary>
    public IReadOnlyDictionary<string, string> ValueFor { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Lowest server version that understands <see cref="Value"/>, or null for all of them.</summary>
    public string? MinServerVersion { get; init; }

    /// <summary>The value to send for a given preset.</summary>
    public string ValueOf(string? preset) =>
        preset is not null && ValueFor.TryGetValue(preset, out var special) ? special : Value;

    /// <summary>
    /// Whether to offer this key at all.
    /// <para>
    /// Both unknowns fail open. An unrecognised preset means a mixed folder or a search result, where
    /// hiding keys would be guessing at what the rows are; an unknown server version means the probe
    /// has not answered yet, and offering a key the server ignores is a smaller failure than hiding
    /// one it supports.
    /// </para>
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
/// The sort catalogue. <see cref="All"/> is the whole of it; <see cref="For"/> is what a particular
/// grid should show.
/// </summary>
public static class EmbySortBy
{
    // Kept as constants because they are referenced directly as defaults, and pointed at Emby's real
    // composite values: a bare DateCreated leaves items added in the same scan in arbitrary order.
    public const string Name = "SortName";
    public const string Number = "ParentIndexNumber,IndexNumber,SortName";
    public const string DateAdded = "DateCreated,SortName";
    public const string ReleaseDate = "ProductionYear,PremiereDate,SortName";
    public const string Rating = "CommunityRating,SortName";
    public const string PlayCount = "PlayCount,SortName";
    public const string Random = "Random";

    /// <summary>Menu section titles, in the order they should appear.</summary>
    public static class Groups
    {
        public const string Naming = "名称";
        public const string Time = "时间";
        public const string Rating = "评价";
        public const string Media = "媒体";
        public const string Other = "其他";

        public static readonly string[] Order = [Naming, Time, Rating, Media, Other];
    }

    /// <summary>
    /// Every key, in menu order rather than Emby's. Emby sorts its own menu alphabetically by
    /// localized name, which in Chinese produces an order nobody can predict, so these are grouped
    /// by what they sort on instead.
    /// <para>
    /// The exclusion lists are Emby's, narrowed to the presets this application can actually produce.
    /// Its Studio, Genre, Tag, MusicArtist, TvChannel, Program, Playlist, Game and GameSystem cases
    /// are dropped because no grid here ever lists those.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<EmbySortOption> All =
    [
        new()
        {
            Label = "标题名称",
            Value = Name,
            Group = Groups.Naming
        },
        new()
        {
            Label = "节目名称",
            Value = "SeriesSortName,ParentIndexNumber,IndexNumber,SortName",
            Group = Groups.Naming,
            OnlyFor = [EmbySortPreset.Episode]
        },
        new()
        {
            Label = "顺序编号",
            Value = Number,
            Group = Groups.Naming,
            NotFor =
            [
                EmbySortPreset.Movie, EmbySortPreset.Video, EmbySortPreset.Trailer,
                EmbySortPreset.MusicVideo, EmbySortPreset.Photo, EmbySortPreset.Album,
                EmbySortPreset.Series, EmbySortPreset.BoxSet
            ]
        },
        new()
        {
            Label = "文件名称",
            // IsFolder first is Emby's: it keeps sub-directories together at one end instead of
            // interleaving them with the files by name.
            Value = "IsFolder,Filename",
            Group = Groups.Naming,
            NotFor = [EmbySortPreset.Trailer, EmbySortPreset.Album, EmbySortPreset.Series, EmbySortPreset.BoxSet]
        },
        new()
        {
            Label = "加入日期",
            Value = DateAdded,
            Group = Groups.Time,
            DescendingByDefault = true
        },
        new()
        {
            Label = "最近更新",
            Value = "DateLastContentAdded,SortName",
            Group = Groups.Time,
            DescendingByDefault = true
        },
        new()
        {
            Label = "最近播放",
            Value = "DatePlayed,SortName",
            Group = Groups.Time,
            DescendingByDefault = true,
            // A series has no play date of its own; the server exposes its episodes' latest as a
            // separate key.
            ValueFor = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [EmbySortPreset.Series] = "SeriesDatePlayed,SortName"
            },
            NotFor = [EmbySortPreset.BoxSet]
        },
        new()
        {
            Label = "发行日期",
            Value = ReleaseDate,
            Group = Groups.Time,
            DescendingByDefault = true,
            NotFor = [EmbySortPreset.BoxSet]
        },
        new()
        {
            Label = "发行年份",
            Value = "ProductionYear,SortName",
            Group = Groups.Time,
            DescendingByDefault = true,
            NotFor = [EmbySortPreset.BoxSet]
        },
        new()
        {
            Label = "时间长度",
            Value = "Runtime,SortName",
            Group = Groups.Time,
            NotFor = [EmbySortPreset.Photo]
        },
        new()
        {
            Label = "公众评分",
            Value = Rating,
            Group = Groups.Rating,
            DescendingByDefault = true,
            NotFor = [EmbySortPreset.Photo, EmbySortPreset.BoxSet]
        },
        new()
        {
            Label = "影评指数",
            Value = "CriticRating,SortName",
            Group = Groups.Rating,
            DescendingByDefault = true,
            NotFor = [EmbySortPreset.Photo, EmbySortPreset.BoxSet, EmbySortPreset.Album, EmbySortPreset.Song]
        },
        new()
        {
            Label = "家长分级",
            Value = "OfficialRating,SortName",
            Group = Groups.Rating,
            DescendingByDefault = true
        },
        new()
        {
            Label = "播放次数",
            Value = PlayCount,
            Group = Groups.Rating,
            DescendingByDefault = true,
            NotFor = [EmbySortPreset.Series, EmbySortPreset.BoxSet]
        },
        new()
        {
            Label = "源分辨率",
            Value = "Resolution,SortName",
            Group = Groups.Media,
            DescendingByDefault = true,
            MinServerVersion = "4.7.3",
            NotFor = [EmbySortPreset.Series, EmbySortPreset.BoxSet, EmbySortPreset.Album, EmbySortPreset.Song]
        },
        new()
        {
            Label = "媒体码率",
            // TotalBitrate, not a video-only figure: this is the whole file's rate, which is what
            // Emby indexes and the only one it will sort on.
            Value = "TotalBitrate,SortName",
            Group = Groups.Media,
            DescendingByDefault = true,
            NotFor = [EmbySortPreset.Photo, EmbySortPreset.Series, EmbySortPreset.BoxSet, EmbySortPreset.Album]
        },
        new()
        {
            Label = "媒体容器",
            Value = "Container,SortName",
            Group = Groups.Media,
            DescendingByDefault = true,
            NotFor = [EmbySortPreset.Series, EmbySortPreset.BoxSet, EmbySortPreset.Album]
        },
        new()
        {
            Label = "文件大小",
            Value = "Size,SortName",
            Group = Groups.Media,
            DescendingByDefault = true,
            NotFor = [EmbySortPreset.Series, EmbySortPreset.BoxSet, EmbySortPreset.Album]
        },
        new()
        {
            // Not one of Emby's: it has no random sort in this menu. Ours, and worth keeping for a
            // library too big to browse.
            Label = "随机",
            Value = Random,
            Group = Groups.Other
        }
    ];

    /// <summary>The keys a grid should offer, in menu order.</summary>
    public static IReadOnlyList<EmbySortOption> For(string? preset, Version? serverVersion = null) =>
        [.. All.Where(option => option.AppliesTo(preset, serverVersion))];

    /// <summary>
    /// What to sort by before anyone chooses. Episodes and seasons go in broadcast order, because a
    /// season listed alphabetically is the one ordering that is always wrong; everything else by name.
    /// </summary>
    public static EmbySortOption Default(string? preset) =>
        preset is EmbySortPreset.Episode or EmbySortPreset.Season
            ? Find(Number) ?? All[0]
            : All[0];

    /// <summary>The catalogue entry for a stored <c>SortBy</c> value, or null if it is no longer offered.</summary>
    public static EmbySortOption? Find(string? value) =>
        value is null ? null : All.FirstOrDefault(option => option.Value == value);

    /// <summary>The label to show for a value, falling back to the value itself.</summary>
    public static string LabelOf(string? value) => Find(value)?.Label ?? value ?? "";

    /// <summary>Label/value pairs for a plain sort dropdown, for the shell that has no grouped menu.</summary>
    public static (string Label, string Value)[] Choices =>
        [.. All.Select(option => (option.Label, option.Value))];
}

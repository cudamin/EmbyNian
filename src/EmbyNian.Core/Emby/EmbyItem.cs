using System.Text.Json.Serialization;
using EmbyNian.Infrastructure;

namespace EmbyNian.Emby;

public static class EmbyItemType
{
    public const string Movie = "Movie";
    public const string Series = "Series";
    public const string Season = "Season";
    public const string Episode = "Episode";
    public const string Video = "Video";
    public const string MusicVideo = "MusicVideo";
    public const string Trailer = "Trailer";
    public const string Folder = "Folder";
    public const string CollectionFolder = "CollectionFolder";
    public const string BoxSet = "BoxSet";
    public const string Playlist = "Playlist";
    public const string Person = "Person";
    public const string Audio = "Audio";
    public const string MusicAlbum = "MusicAlbum";
    public const string MusicArtist = "MusicArtist";

    private static readonly HashSet<string> Playable =
        new(StringComparer.OrdinalIgnoreCase) { Movie, Episode, Video, MusicVideo, Trailer };

    /// <summary>
    /// The types this app deliberately does not show. It plays video through mpv and has no music face at
    /// all — no album grid, no track list, no queue — so a music library reached through it is a wall of
    /// folders that answer a click with nothing worth looking at. Hidden rather than half-built.
    /// </summary>
    private static readonly HashSet<string> Music =
        new(StringComparer.OrdinalIgnoreCase) { Audio, MusicAlbum, MusicArtist, MusicVideo };

    /// <summary>The <c>CollectionType</c> values Emby gives a music library.</summary>
    private static readonly HashSet<string> MusicCollections =
        new(StringComparer.OrdinalIgnoreCase) { "music", "musicvideos" };

    /// <summary>
    /// The types 已观看 and 收藏 are not a thing for. A 媒体库 is the shelf the items live on, not one of
    /// them, and a 演职人员 is a name — neither has a watched state to toggle, and the server's copy of
    /// them carries no user data to toggle it on.
    /// </summary>
    private static readonly HashSet<string> Stateless =
        new(StringComparer.OrdinalIgnoreCase) { CollectionFolder, Person };

    public static bool IsPlayable(string? type) => type is not null && Playable.Contains(type);

    /// <summary>
    /// Whether 已观看 and 收藏 apply to this type. Unknown types say yes: a server sending something we
    /// have no name for is still sending an item, and hiding its controls would be a guess.
    /// </summary>
    public static bool TracksUserState(string? type) => type is null || !Stateless.Contains(type);

    /// <summary>Whether this item's type is one of the music ones the shell hides.</summary>
    public static bool IsMusic(string? type) => type is not null && Music.Contains(type);

    /// <summary>Whether a library's <c>CollectionType</c> makes it a music library.</summary>
    public static bool IsMusicLibrary(string? collectionType) =>
        collectionType is not null && MusicCollections.Contains(collectionType);

    public static string ToChinese(string? type) => type switch
    {
        Movie => "电影",
        Series => "剧集",
        Season => "季",
        Episode => "单集",
        Video => "视频",
        MusicVideo => "音乐视频",
        Trailer => "预告片",
        Folder => "文件夹",
        CollectionFolder => "媒体库",
        BoxSet => "合集",
        Playlist => "播放列表",
        Person => "演职人员",
        Audio => "音乐",
        MusicAlbum => "专辑",
        MusicArtist => "艺术家",
        null or "" => "",
        _ => type
    };
}

public sealed class EmbyUserData
{
    public long PlaybackPositionTicks { get; set; }

    public int PlayCount { get; set; }

    public bool Played { get; set; }

    public bool IsFavorite { get; set; }

    public double? PlayedPercentage { get; set; }

    public int? UnplayedItemCount { get; set; }

    public DateTimeOffset? LastPlayedDate { get; set; }
}

/// <summary>
/// One chapter marker, as sent with <c>Fields=Chapters</c>. The ones that carry an
/// <see cref="ImageTag"/> also have a still on the server, which is what the seek bar shows
/// when the pointer hovers over that part of the timeline.
/// </summary>
public sealed class ChapterInfo
{
    public long StartPositionTicks { get; set; }

    public string? Name { get; set; }

    /// <summary>Image cache tag; null when the server extracted no still for this chapter.</summary>
    public string? ImageTag { get; set; }

    [JsonIgnore]
    public bool HasImage => !string.IsNullOrEmpty(ImageTag);

    [JsonIgnore]
    public double StartSeconds => TimeFormat.ToSeconds(StartPositionTicks);
}

public sealed class MediaStream
{
    public int Index { get; set; }

    /// <summary>"Video", "Audio", "Subtitle", "EmbeddedImage".</summary>
    public string Type { get; set; } = "";

    public string? Codec { get; set; }

    /// <summary>
    /// The encoder profile as the server spells it — "Main 10", "High", "dvhe.05.06". Shown in 媒体信息
    /// next to the codec, because "HEVC" alone does not say whether a file is 8-bit or 10-bit.
    /// <para>
    /// The sibling <c>Level</c> is deliberately not mapped: Emby sends it as an integer whose scale
    /// differs per codec (41 for H.264 L4.1, 150 for HEVC L5), so printing it would be guesswork.
    /// </para>
    /// </summary>
    public string? Profile { get; set; }

    public string? Language { get; set; }

    public string? Title { get; set; }

    public string? DisplayTitle { get; set; }

    public string? DisplayLanguage { get; set; }

    public string? ChannelLayout { get; set; }

    public int? Channels { get; set; }

    public int? Height { get; set; }

    public int? Width { get; set; }

    public int? BitRate { get; set; }

    /// <summary>Bits per colour component: 8 for an ordinary encode, 10 for HDR and most anime releases.</summary>
    public int? BitDepth { get; set; }

    /// <summary>Emby sends these as decimals such as 23.976023; either may be missing.</summary>
    public double? AverageFrameRate { get; set; }

    public double? RealFrameRate { get; set; }

    /// <summary>"SDR", "HDR", "DOVI", "HDR10", "HLG"… — Emby's own spelling varies by version.</summary>
    public string? VideoRange { get; set; }

    /// <summary>Display aspect ratio as text, e.g. "2.40:1" or "16:9"; often absent.</summary>
    public string? AspectRatio { get; set; }

    public bool IsDefault { get; set; }

    public bool IsForced { get; set; }

    public bool IsExternal { get; set; }

    public bool SupportsExternalStream { get; set; }

    public string? DeliveryUrl { get; set; }

    [JsonIgnore]
    public bool IsAudio => string.Equals(Type, "Audio", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsSubtitle => string.Equals(Type, "Subtitle", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsVideo => string.Equals(Type, "Video", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The frame rate to reason about, or 0 when the server reported none. Prefers the average, which
    /// is the honest number for a VFR file; the real rate on one of those is the peak, and treating a
    /// 24fps film as 60fps because a few frames were duplicated would switch display sync off.
    /// </summary>
    [JsonIgnore]
    public double FrameRate => AverageFrameRate ?? RealFrameRate ?? 0;

    /// <summary>
    /// True for any high-dynamic-range flavour. Matched as a substring because the field holds
    /// "HDR", "HDR10", "HDR10Plus" or "DOVI" depending on the Emby version and the file, and
    /// everything except a bare "SDR" wants tone mapping.
    /// </summary>
    [JsonIgnore]
    public bool IsHdr => VideoRange is { } range &&
        (range.Contains("HDR", StringComparison.OrdinalIgnoreCase) ||
         range.Contains("DOVI", StringComparison.OrdinalIgnoreCase) ||
         range.Contains("HLG", StringComparison.OrdinalIgnoreCase) ||
         range.Contains("Dolby Vision", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True for subtitle codecs Emby can hand over as a standalone text file. Image-based
    /// subtitles (PGS, VobSub) can only be selected inside the container.
    /// </summary>
    [JsonIgnore]
    public bool IsTextSubtitle => IsSubtitle && Codec is not null && Codec.ToLowerInvariant() is
        "srt" or "subrip" or "ass" or "ssa" or "webvtt" or "vtt" or "mov_text" or "text" or "microdvd" or "sami" or "smi";

    /// <summary>A label good enough for a track picker even when the server sends nothing useful.</summary>
    public string ToDisplayLabel()
    {
        if (!string.IsNullOrWhiteSpace(DisplayTitle)) return Decorate(DisplayTitle!);

        var parts = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(DisplayLanguage)) parts.Add(DisplayLanguage!);
        else if (!string.IsNullOrWhiteSpace(Language)) parts.Add(Language!);
        if (!string.IsNullOrWhiteSpace(Title)) parts.Add(Title!);
        if (!string.IsNullOrWhiteSpace(Codec)) parts.Add(Codec!.ToUpperInvariant());
        if (IsAudio && !string.IsNullOrWhiteSpace(ChannelLayout)) parts.Add(ChannelLayout!);
        if (parts.Count == 0) parts.Add($"轨道 {Index}");
        return Decorate(string.Join(" · ", parts));
    }

    private string Decorate(string label)
    {
        if (IsForced) label += "（强制）";
        if (IsExternal) label += "（外挂）";
        return label;
    }
}

public sealed class MediaSource
{
    public string Id { get; set; } = "";

    public string? Name { get; set; }

    public string? Path { get; set; }

    public string? Container { get; set; }

    public long? Size { get; set; }

    public long? RunTimeTicks { get; set; }

    public int? Bitrate { get; set; }

    public bool SupportsDirectPlay { get; set; }

    public bool SupportsDirectStream { get; set; }

    public int? DefaultAudioStreamIndex { get; set; }

    public int? DefaultSubtitleStreamIndex { get; set; }

    public List<MediaStream> MediaStreams { get; set; } = [];

    [JsonIgnore]
    public IEnumerable<MediaStream> AudioStreams => MediaStreams.Where(stream => stream.IsAudio);

    [JsonIgnore]
    public IEnumerable<MediaStream> SubtitleStreams => MediaStreams.Where(stream => stream.IsSubtitle);

    [JsonIgnore]
    public MediaStream? PrimaryVideoStream => MediaStreams.FirstOrDefault(stream => stream.IsVideo);

    /// <summary>"1080p · HEVC · 8.4 GB" for the detail page.</summary>
    public string ToQualityLabel()
    {
        var parts = new List<string>(4);
        var video = PrimaryVideoStream;
        if (video?.Height is > 0) parts.Add(DescribeHeight(video.Height.Value));
        if (!string.IsNullOrWhiteSpace(video?.Codec)) parts.Add(video!.Codec!.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(Container)) parts.Add(Container!.ToUpperInvariant());
        if (TimeFormat.FileSize(Size) is { Length: > 0 } size) parts.Add(size);
        return string.Join("  ·  ", parts);
    }

    private static string DescribeHeight(int height) => height switch
    {
        >= 2000 => "4K",
        >= 1000 => "1080p",
        >= 700 => "720p",
        >= 500 => "576p",
        _ => $"{height}p"
    };
}

/// <summary>
/// One credited person, as Emby sends it alongside a single item. The server leaves People out of
/// list responses, so this is only populated on an item fetched by id.
/// </summary>
public sealed class EmbyPerson
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>The character an actor plays; empty for crew.</summary>
    public string? Role { get; set; }

    /// <summary>The kind of credit: "Actor", "GuestStar", "Director", "Writer", "Producer".</summary>
    public string? Type { get; set; }

    /// <summary>Image cache tag for the portrait; null when the server holds no photo.</summary>
    public string? PrimaryImageTag { get; set; }

    /// <summary>What a portrait card shows under the name: the character, else the credit.</summary>
    [JsonIgnore]
    public string Credit => Role is { Length: > 0 } role ? role.Trim() : DescribeCredit(Type);

    private static string DescribeCredit(string? type) => type switch
    {
        "Actor" => "演员",
        "GuestStar" => "客串",
        "Director" => "导演",
        "Writer" => "编剧",
        "Producer" => "制片",
        "Composer" => "配乐",
        null or "" => "",
        _ => type
    };
}

/// <summary>
/// A production company as Emby sends it. Only the name is kept: 「TBS」 is a segment of the detail
/// page's facts line, and this client has nowhere to browse a studio to. The server also sends an
/// <c>Id</c> here, but as a JSON <em>number</em> rather than the string it uses for item ids — reading
/// it into a <see cref="string"/> throws and takes the whole detail response down with it, so the
/// field is deliberately absent and left to be ignored.
/// </summary>
public sealed class EmbyStudio
{
    public string Name { get; set; } = "";
}

public sealed class EmbyItem
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string? OriginalTitle { get; set; }

    public string? SortName { get; set; }

    public string Type { get; set; } = "";

    public string? ServerId { get; set; }

    public string? Overview { get; set; }

    public int? ProductionYear { get; set; }

    public DateTimeOffset? PremiereDate { get; set; }

    /// <summary>
    /// When a show finished, which is what turns its facts line into 「2016 – 2018」 rather than a bare
    /// premiere year. Sent only for a series that has ended, and only when asked for by name: this
    /// server omits it — along with the studios, the rating and the premiere date itself — from any
    /// response whose <c>Fields</c> did not list it (see <see cref="EmbyFields.Detail"/>).
    /// </summary>
    public DateTimeOffset? EndDate { get; set; }

    public DateTimeOffset? DateCreated { get; set; }

    public long? RunTimeTicks { get; set; }

    public int? IndexNumber { get; set; }

    public int? ParentIndexNumber { get; set; }

    public bool IsFolder { get; set; }

    public string? CollectionType { get; set; }

    public string? SeriesId { get; set; }

    public string? SeriesName { get; set; }

    public string? SeasonId { get; set; }

    public string? SeasonName { get; set; }

    public string? ParentId { get; set; }

    public int? ChildCount { get; set; }

    public float? CommunityRating { get; set; }

    public string? OfficialRating { get; set; }

    public double? PrimaryImageAspectRatio { get; set; }

    public List<string> Genres { get; set; } = [];

    /// <summary>Free-form server tags; some libraries carry 动画/动漫 here rather than in Genres.</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>
    /// Who made it, in the server's order. Emby lists every credited company, so a film can arrive with
    /// four of them; the facts line shows the first, which is the broadcaster or the lead studio.
    /// </summary>
    public List<EmbyStudio> Studios { get; set; } = [];

    public Dictionary<string, string> ImageTags { get; set; } = [];

    public List<string> BackdropImageTags { get; set; } = [];

    /// <summary>
    /// The show's logo, as the server hands it to an episode or a season. Emby fills these two on any item
    /// that has no logo of its own but whose parent does — which is the whole reason an episode page can
    /// show the show's name plate. Both or neither: the tag belongs to the other item's id.
    /// </summary>
    public string? ParentLogoItemId { get; set; }

    public string? ParentLogoImageTag { get; set; }

    /// <summary>
    /// The show's backdrop, as the server hands it to an episode or a season — the same inherited-image walk
    /// as <see cref="ParentLogoItemId"/>, for the one artwork an episode almost never has of its own. A
    /// backdrop is what the home page's banner stands on, and an episode without this one would drop to its
    /// own still: a frame out of the video where a picture made to be a background belongs.
    /// <para>
    /// A list rather than one tag, because that is how the server sends backdrops (see
    /// <see cref="BackdropImageTags"/>); the first is the one anything here asks for.
    /// </para>
    /// </summary>
    public string? ParentBackdropItemId { get; set; }

    public List<string> ParentBackdropImageTags { get; set; } = [];

    /// <summary>
    /// 这个剧的缩略图，服务器发给单集或季的那一份 —— 「集页面要用这个剧的背景图或缩略图」里的第二档，也是剧集
    /// 除了背景图之外唯一一张宽幅的画面。
    /// <para>
    /// 两套字段而不是一套，因为服务器就是发两套：<see cref="ParentThumbItemId"/> 是那趟继承图片的上溯 —— 它停在
    /// 手上有这张图的那一层，所以单集拿到的可能是季的；<see cref="SeriesThumbImageTag"/> 明说是这个剧的，和
    /// <see cref="SeriesId"/> 配成一对。先问后者（见 <see cref="ItemArtwork.Inherited"/>）：用户要的是「这个剧的」。
    /// </para>
    /// </summary>
    public string? ParentThumbItemId { get; set; }

    public string? ParentThumbImageTag { get; set; }

    public string? SeriesThumbImageTag { get; set; }

    public EmbyUserData? UserData { get; set; }

    public List<MediaSource> MediaSources { get; set; } = [];

    /// <summary>
    /// The tracks, flattened out of the media source. Browse and detail responses leave this empty —
    /// they carry <see cref="MediaSources"/> instead — but a session's NowPlayingItem is sent with
    /// the streams and no sources at all, which is how the 服务器 page names what is being decoded.
    /// </summary>
    public List<MediaStream> MediaStreams { get; set; } = [];

    /// <summary>Chapter markers, in timeline order; empty unless the request asked for them.</summary>
    public List<ChapterInfo> Chapters { get; set; } = [];

    /// <summary>
    /// Cast and crew in the server's own billing order. Emby returns this with a single-item
    /// request and omits it from list responses, so it is empty on anything the browse queries
    /// produced — the detail page is the only place with enough of the item to show it.
    /// </summary>
    public List<EmbyPerson> People { get; set; } = [];

    [JsonIgnore]
    public bool IsPlayable => EmbyItemType.IsPlayable(Type);

    /// <summary>Whether 已观看 and 收藏 mean anything here; see <see cref="EmbyItemType.TracksUserState"/>.</summary>
    [JsonIgnore]
    public bool TracksUserState => EmbyItemType.TracksUserState(Type);

    [JsonIgnore]
    public string DisplayTypeName => EmbyItemType.ToChinese(Type);

    [JsonIgnore]
    public long ResumeTicks => UserData?.PlaybackPositionTicks ?? 0;

    [JsonIgnore]
    public bool IsWatched => UserData?.Played ?? false;

    [JsonIgnore]
    public bool HasResumePosition => ResumeTicks > 0 && ProgressFraction is > 0.001 and < 0.995;

    /// <summary>0..1 watched fraction, from the server's own percentage when it sends one.</summary>
    [JsonIgnore]
    public double ProgressFraction
    {
        get
        {
            if (UserData?.PlayedPercentage is { } percentage and > 0) return Math.Clamp(percentage / 100d, 0, 1);
            if (RunTimeTicks is null or <= 0 || ResumeTicks <= 0) return 0;
            return Math.Clamp(ResumeTicks / (double)RunTimeTicks.Value, 0, 1);
        }
    }

    /// <summary>"S01E02" when both numbers are known.</summary>
    [JsonIgnore]
    public string EpisodeCode =>
        ParentIndexNumber is { } season && IndexNumber is { } episode ? $"S{season:00}E{episode:00}" : "";

    /// <summary>What a poster card shows under the title.</summary>
    [JsonIgnore]
    public string CardSubtitle
    {
        get
        {
            if (Type == EmbyItemType.Episode)
            {
                var code = EpisodeCode;
                return code.Length > 0 ? $"{code}  ·  {SeriesName}" : SeriesName ?? "";
            }

            var parts = new List<string>(2);
            if (ProductionYear is { } year and > 0) parts.Add(year.ToString());
            if (Type is EmbyItemType.Series && ChildCount is { } count and > 0) parts.Add($"{count} 季");
            else if (DisplayTypeName.Length > 0 && Type != EmbyItemType.Movie) parts.Add(DisplayTypeName);
            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>The episode/season aware title used in window titles and the now-playing bar.</summary>
    public string ToPlaybackTitle()
    {
        if (Type != EmbyItemType.Episode) return Name;
        var code = EpisodeCode;
        var prefix = string.IsNullOrWhiteSpace(SeriesName) ? "" : SeriesName + " ";
        return code.Length > 0 ? $"{prefix}{code} {Name}".Trim() : $"{prefix}{Name}".Trim();
    }

    public string? PrimaryImageTag => ImageTags.TryGetValue("Primary", out var tag) ? tag : null;

    public string? ThumbImageTag => ImageTags.TryGetValue("Thumb", out var tag) ? tag : null;

    public MediaSource? DefaultMediaSource => MediaSources.FirstOrDefault();
}

using System.Text.Json.Serialization;
using EmbyMpvClient.Infrastructure;

namespace EmbyMpvClient.Emby;

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

    private static readonly HashSet<string> Playable =
        new(StringComparer.OrdinalIgnoreCase) { Movie, Episode, Video, MusicVideo, Trailer };

    public static bool IsPlayable(string? type) => type is not null && Playable.Contains(type);

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

    public string? Language { get; set; }

    public string? Title { get; set; }

    public string? DisplayTitle { get; set; }

    public string? DisplayLanguage { get; set; }

    public string? ChannelLayout { get; set; }

    public int? Channels { get; set; }

    public int? Height { get; set; }

    public int? Width { get; set; }

    public int? BitRate { get; set; }

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

    public Dictionary<string, string> ImageTags { get; set; } = [];

    public List<string> BackdropImageTags { get; set; } = [];

    public EmbyUserData? UserData { get; set; }

    public List<MediaSource> MediaSources { get; set; } = [];

    /// <summary>Chapter markers, in timeline order; empty unless the request asked for them.</summary>
    public List<ChapterInfo> Chapters { get; set; } = [];

    [JsonIgnore]
    public bool IsPlayable => EmbyItemType.IsPlayable(Type);

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

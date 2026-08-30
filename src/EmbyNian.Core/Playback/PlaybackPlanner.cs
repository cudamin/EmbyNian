using EmbyNian.Configuration;
using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Infrastructure;
using EmbyNian.Mpv;

namespace EmbyNian.Playback;

/// <summary>
/// Assembles a <see cref="PlaybackRequest"/> from a ticket, the settings and the live
/// connection. Kept free of process and socket handling so the whole decision — URL, headers,
/// track ids, shader group, resume position — can be asserted in a unit test.
/// </summary>
/// <param name="shaderCacheDirectory">
/// Where mpv may cache compiled shaders; null omits the option, which is what a test wants.
/// </param>
public sealed class PlaybackPlanner(AppSettings settings, ShaderGroupResolver shaders, string? shaderCacheDirectory = null)
{
    private const string Category = "playback";

    /// <summary>Default track choices for the picker, before the user overrides them.</summary>
    public AutoTracks SuggestTracks(MediaSource source) => TrackSelection.Resolve(settings.Playback, source);

    public PlaybackRequest Plan(PlaybackTicket ticket, EmbyConnection connection)
    {
        var item = ticket.Item;
        var source = ticket.Source;
        var map = MpvTrackMap.Build(source);

        var externalSubtitles = map.ExternalSubtitleIndexes
            .Select(index => source.MediaStreams.First(stream => stream.Index == index))
            .Select(stream => EmbyUrl.Subtitle(connection.ApiBase, item.Id, source.Id, stream.Index, stream.Codec))
            .ToList();

        var decision = shaders.Resolve(item, source, ticket.Parent);
        var tracks = ResolveTracks(ticket, source);

        var request = new PlaybackRequest
        {
            MediaUrl = EmbyUrl.Stream(connection.ApiBase, item.Id, source.Id, source.Container),
            Title = item.ToPlaybackTitle(),
            HttpHeaders = BuildHeaders(connection),
            StartSeconds = TimeFormat.ToSeconds(ResumeFrom(ticket.StartTicks)),
            AudioId = map.AudioId(tracks.AudioIndex),
            SubtitleId = map.SubtitleId(tracks.SubtitleIndex),
            SubtitlesDisabled = tracks.SubtitlesDisabled,
            ExternalSubtitles = externalSubtitles,
            SubtitleLanguage = TrackLanguagePriority.FromTokens(settings.Playback.SubtitleLanguages),
            AudioLanguage = TrackLanguagePriority.FromTokens(AudioLanguageTokens()),
            SubtitleFont = ResolveFont(settings.Playback.SubtitleFontFamily),
            ShaderProfile = decision.Group,
            ShaderReason = decision.Reason,
            PlayerOptions = BuildPlayerOptions(DescribeSource(source), decision),
            RunTimeTicks = source.RunTimeTicks ?? item.RunTimeTicks ?? 0,
            ItemId = item.Id,
            MediaSourceId = source.Id,
            AudioStreamIndex = tracks.AudioIndex,
            SubtitleStreamIndex = tracks.SubtitlesDisabled ? null : tracks.SubtitleIndex
        };

        WarnAboutUnavailableTracks(tracks, map);
        return request;
    }

    /// <summary>
    /// Everything mpv is configured with, in the order the last-value-wins rule needs: the client's
    /// own floor first, then the 画质预设 and the settings page, then the shader group. The group comes
    /// last because its scalers are the point of choosing it — a group tuned around
    /// <c>ewa_lanczossharp</c> would be doing something else entirely if 视频输出 could overwrite that
    /// afterwards.
    /// <para>
    /// <see cref="ShaderDecision.Animated"/> is handed on rather than recomputed: 去色带 = 「在动画中开启」
    /// has to agree with what the shader rules decided the item was, or one video could be 动画 for the
    /// shader chain and live action for the deband setting.
    /// </para>
    /// </summary>
    private IReadOnlyList<KeyValuePair<string, string>> BuildPlayerOptions(SourceProfile? source, ShaderDecision decision)
    {
        var options = new List<KeyValuePair<string, string>>(48);
        options.AddRange(MpvBaseline.Build(shaderCacheDirectory));
        options.AddRange(MpvOutputOptions.Build(settings.Video, settings.Audio, settings.Playback, source, decision.Animated));

        if (ShaderGroupCatalog.Find(decision.Group) is { } group)
            options.AddRange(group.ToMpvOptions(ShaderGroupCatalog.ShaderRoot));
        else if (decision.HasGroup)
            Log.Warn(Category, $"着色器配置组「{decision.Group}」不存在，本次不应用着色器");

        return options;
    }

    /// <summary>
    /// What the client knows about the video, for the rules that depend on it. Null when the source
    /// carries no video stream at all — music, or a file Emby has not probed — where every one of
    /// those rules should stay out of the way.
    /// </summary>
    private static SourceProfile? DescribeSource(MediaSource source)
    {
        if (source.PrimaryVideoStream is not { } video) return null;

        return new SourceProfile(
            video.Width ?? 0,
            video.Height ?? 0,
            video.BitDepth ?? 0,
            video.FrameRate,
            video.IsHdr);
    }

    /// <summary>
    /// What the ticket asked for, with anything it left open filled in from the settings. Resolving
    /// the automatic choice here rather than leaving it to mpv's own <c>alang</c>/<c>slang</c> is what
    /// makes 「按优先级选字幕」 work at all: mpv cannot tell 简体 from 繁体 when both tracks are labelled
    /// <c>chi</c> and only their titles differ. It also means the index reported back to Emby is the
    /// track that is really playing.
    /// </summary>
    private (int? AudioIndex, int? SubtitleIndex, bool SubtitlesDisabled) ResolveTracks(PlaybackTicket ticket, MediaSource source)
    {
        // An explicit pick from the detail page's dropdowns is never second-guessed.
        if (ticket.AudioStreamIndex is not null && (ticket.SubtitlesDisabled || ticket.SubtitleStreamIndex is not null))
            return (ticket.AudioStreamIndex, ticket.SubtitleStreamIndex, ticket.SubtitlesDisabled);

        var auto = TrackSelection.Resolve(settings.Playback, source);
        var audio = ticket.AudioStreamIndex ?? auto.Audio?.Index;

        if (ticket.SubtitlesDisabled) return (audio, null, true);
        if (ticket.SubtitleStreamIndex is { } chosen) return (audio, chosen, false);

        var subtitle = auto.Subtitle;
        Describe(source, audio, subtitle);
        return (audio, subtitle.Stream?.Index, subtitle.Disabled);
    }

    private void Describe(MediaSource source, int? audioIndex, SubtitleChoice subtitle)
    {
        var audio = audioIndex is { } index ? source.MediaStreams.FirstOrDefault(stream => stream.Index == index) : null;
        var subtitleLabel = subtitle.Disabled
            ? "关闭"
            : subtitle.Stream?.ToDisplayLabel() ?? "由 mpv 决定";

        Log.Info(Category, $"自动选轨：音轨 {audio?.ToDisplayLabel() ?? "由 mpv 决定"}，字幕 {subtitleLabel}");
    }

    /// <summary>The single audio language, as a one-item list for the mpv <c>alang</c> fallback.</summary>
    private IEnumerable<string> AudioLanguageTokens()
    {
        if (settings.Playback.AudioTrack != AudioTrackMode.Language) return [];
        return string.IsNullOrWhiteSpace(settings.Playback.AudioLanguage) ? [] : [settings.Playback.AudioLanguage];
    }

    /// <summary>
    /// Backs the resume position up by 恢复播放自动快退 seconds so a file continues a moment before
    /// where it stopped. Applied here rather than in the caller so both the detail page's 继续播放 and
    /// the home row's play button go through it.
    /// </summary>
    private long ResumeFrom(long startTicks)
    {
        if (startTicks <= 0) return startTicks;
        var rewind = settings.Playback.ResumeRewindSeconds * TimeSpan.TicksPerSecond;
        return Math.Max(0, startTicks - rewind);
    }

    private static List<KeyValuePair<string, string>> BuildHeaders(EmbyConnection connection) =>
    [
        // The stream URL carries no api_key, so the token has to reach the server another way.
        // A header keeps it out of the server's access log and out of any proxy in between.
        new("X-Emby-Token", connection.AccessToken),
        new("X-Emby-Authorization", connection.Device.ToAuthorizationHeader())
    ];

    /// <summary>
    /// The 字幕字体 to give mpv's <c>--sub-font</c>, which takes a font *family* name. An empty choice
    /// falls back to 微软雅黑, the one CJK-capable family every Windows install has.
    /// <para>
    /// v3 stored the path of a file under C:\Windows\Fonts here and passed it straight through, which
    /// mpv quietly ignored: it looked for a family literally called "C:\Windows\Fonts\msyh.ttc", found
    /// none and fell back to sans-serif. Nobody noticed because the user's mpv.conf named a real family
    /// of its own, and mpv.conf was read after these arguments. A leftover path is still recognised
    /// here rather than sent as-is, because <see cref="Configuration.SettingsMigration"/> can only map
    /// the files it knows the family names of.
    /// </para>
    /// </summary>
    private static string? ResolveFont(string configured)
    {
        var value = configured.Trim();
        if (value.Length == 0) return FontFamilies.Default;

        // A path would be meaningless to mpv; the family behind it may not be, so it is looked up.
        if (value.Contains('\\') || value.Contains('/') || value.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
        {
            var family = FontFamilies.FromFileName(value);
            Log.Warn(Category, family is null
                ? $"字幕字体填的是文件路径，mpv 只认字体族名，已改用 {FontFamilies.Default}：{value}"
                : $"字幕字体填的是文件路径，已换成对应的字体族名「{family}」：{value}");
            return family ?? FontFamilies.Default;
        }

        return value;
    }

    private static void WarnAboutUnavailableTracks((int? AudioIndex, int? SubtitleIndex, bool SubtitlesDisabled) tracks, MpvTrackMap map)
    {
        foreach (var index in new[] { tracks.AudioIndex, tracks.SubtitleIndex })
        {
            if (index is { } value && map.UnavailableIndexes.Contains(value))
                Log.Warn(Category, $"选择的外挂轨道（索引 {value}）无法交给 mpv，已交由 mpv 自行选择");
        }
    }
}

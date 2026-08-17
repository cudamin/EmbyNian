using EmbyMpvClient.Configuration;
using EmbyMpvClient.Diagnostics;
using EmbyMpvClient.Emby;
using EmbyMpvClient.Infrastructure;
using EmbyMpvClient.Mpv;

namespace EmbyMpvClient.Playback;

/// <summary>
/// Assembles a <see cref="PlaybackRequest"/> from a ticket, the settings and the live
/// connection. Kept free of process and socket handling so the whole decision — URL, headers,
/// track ids, shader group, resume position — can be asserted in a unit test.
/// </summary>
public sealed class PlaybackPlanner(AppSettings settings, ShaderProfileResolver shaders)
{
    private const string Category = "playback";

    /// <summary>Default track choices for the picker, before the user overrides them.</summary>
    public (MediaStream? Audio, MediaStream? Subtitle) SuggestTracks(MediaSource source)
    {
        var audio = TrackPreference.Choose(
            source.AudioStreams,
            settings.Playback.PreferredAudioLanguage,
            source.DefaultAudioStreamIndex);

        var subtitle = TrackPreference.Choose(
            source.SubtitleStreams,
            settings.Playback.PreferredSubtitleLanguage,
            source.DefaultSubtitleStreamIndex,
            settings.Playback.PreferForcedSubtitles);

        return (audio, subtitle);
    }

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

        var request = new PlaybackRequest
        {
            MediaUrl = EmbyUrl.Stream(connection.ApiBase, item.Id, source.Id, source.Container),
            Title = item.ToPlaybackTitle(),
            HttpHeaders = BuildHeaders(connection),
            StartSeconds = TimeFormat.ToSeconds(ticket.StartTicks),
            AudioId = map.AudioId(ticket.AudioStreamIndex),
            SubtitleId = map.SubtitleId(ticket.SubtitleStreamIndex),
            SubtitlesDisabled = ticket.SubtitlesDisabled,
            ExternalSubtitles = externalSubtitles,
            SubtitleLanguage = TrackLanguagePriority.ToMpvValue(settings.Playback.SubtitleLanguagePriority),
            AudioLanguage = TrackLanguagePriority.ToMpvValue(settings.Playback.AudioLanguagePriority),
            SubtitleFont = ResolveFont(settings.Playback.SubtitleFontPath),
            ShaderProfile = decision.Profile,
            ShaderReason = decision.Reason,
            IncludeFile = ResolveIncludeFile(decision.HasProfile),
            ExtraArguments = CommandLine.Split(settings.Mpv.ExtraArguments),
            OverrideMpvResume = !settings.Playback.LetMpvManageResume,
            RunTimeTicks = source.RunTimeTicks ?? item.RunTimeTicks ?? 0,
            ItemId = item.Id,
            MediaSourceId = source.Id,
            AudioStreamIndex = ticket.AudioStreamIndex,
            SubtitleStreamIndex = ticket.SubtitlesDisabled ? null : ticket.SubtitleStreamIndex
        };

        WarnAboutUnavailableTracks(ticket, map);
        return request;
    }

    private static List<KeyValuePair<string, string>> BuildHeaders(EmbyConnection connection) =>
    [
        // The stream URL carries no api_key, so the token has to reach the server another way.
        // A header keeps it out of the server's access log and out of any proxy in between.
        new("X-Emby-Token", connection.AccessToken),
        new("X-Emby-Authorization", connection.Device.ToAuthorizationHeader())
    ];

    /// <summary>An empty choice falls back to 微软雅黑; a configured but missing font must not
    /// fail the whole launch, it just goes unused.</summary>
    private static string? ResolveFont(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            configured = @"C:\Windows\Fonts\msyh.ttc";
        if (File.Exists(configured)) return configured;

        Log.Warn(Category, $"设置的字幕字体不存在，已忽略：{configured}");
        return null;
    }

    /// <summary>
    /// The config file holding the client's own shader groups. Only needed when a profile is
    /// actually being applied, and only when the file exists — a missing --include makes mpv
    /// print an error on every launch.
    /// </summary>
    private string? ResolveIncludeFile(bool hasProfile)
    {
        if (!hasProfile) return null;

        var configured = settings.Shaders.IncludeFile;
        if (!string.IsNullOrWhiteSpace(configured))
            return File.Exists(configured) ? configured : Missing(configured);

        // The embedded backend includes the pack that ships with the program; the external
        // player keeps its pack next to mpv.conf.
        var directory = settings.Mpv.Backend == MpvBackendKind.BuiltInLibMpv
            ? AppContext.BaseDirectory
            : Path.GetDirectoryName(settings.Mpv.ConfigPath);
        if (string.IsNullOrWhiteSpace(directory)) return null;

        var candidate = Path.Combine(directory, ShaderPack.FileName);
        return File.Exists(candidate) ? candidate : null;

        string? Missing(string path)
        {
            Log.Warn(Category, $"设置里指定的着色器配置文件不存在，已忽略：{path}");
            return null;
        }
    }

    private static void WarnAboutUnavailableTracks(PlaybackTicket ticket, MpvTrackMap map)
    {
        foreach (var index in new[] { ticket.AudioStreamIndex, ticket.SubtitleStreamIndex })
        {
            if (index is { } value && map.UnavailableIndexes.Contains(value))
                Log.Warn(Category, $"选择的外挂轨道（索引 {value}）无法交给 mpv，已交由 mpv 自行选择");
        }
    }
}

using EmbyMpvClient.Emby;
using EmbyMpvClient.Infrastructure;

namespace EmbyMpvClient.Playback;

/// <summary>
/// What the user asked to play, after the track picker has run. A record so a caller can vary
/// one field of an existing ticket — "same episode, other audio track" — with <c>with</c>.
/// </summary>
public sealed record PlaybackTicket
{
    public required EmbyItem Item { get; init; }

    public required MediaSource Source { get; init; }

    /// <summary>Emby stream index of the chosen audio track; null lets mpv/alang decide.</summary>
    public int? AudioStreamIndex { get; init; }

    /// <summary>Emby stream index of the chosen subtitle track; null lets mpv/slang decide.</summary>
    public int? SubtitleStreamIndex { get; init; }

    /// <summary>Explicitly start with subtitles off, which is not the same as "no preference".</summary>
    public bool SubtitlesDisabled { get; init; }

    public long StartTicks { get; init; }

    /// <summary>The series row for an episode, so genre-based rules can see the show's metadata.</summary>
    public EmbyItem? Parent { get; init; }
}

/// <summary>
/// A fully resolved launch description: everything needed to start playback, with no Emby or
/// settings types left in it. This is the seam a libmpv backend would consume unchanged.
/// </summary>
public sealed record PlaybackRequest
{
    public required Uri MediaUrl { get; init; }

    /// <summary>Shown by mpv as the media title instead of the opaque stream URL.</summary>
    public required string Title { get; init; }

    public IReadOnlyList<KeyValuePair<string, string>> HttpHeaders { get; init; } = [];

    public double StartSeconds { get; init; }

    public int? AudioId { get; init; }

    public int? SubtitleId { get; init; }

    public bool SubtitlesDisabled { get; init; }

    /// <summary>Server-extracted subtitle files, in the order mpv will number them.</summary>
    public IReadOnlyList<Uri> ExternalSubtitles { get; init; } = [];

    /// <summary>
    /// mpv <c>--slang</c> value derived from the settings' subtitle-language priority, or
    /// null to leave mpv.conf alone. Only consulted when no explicit subtitle was chosen.
    /// </summary>
    public string? SubtitleLanguage { get; init; }

    /// <summary>mpv <c>--alang</c> value, same rules as <see cref="SubtitleLanguage"/>.</summary>
    public string? AudioLanguage { get; init; }

    /// <summary>Subtitle font file passed as mpv <c>--sub-font</c>; null lets mpv.conf decide.</summary>
    public string? SubtitleFont { get; init; }

    /// <summary>The mpv 「着色器配置组」 to activate, or null to leave mpv.conf alone.</summary>
    public string? ShaderProfile { get; init; }

    /// <summary>Config file holding the client's own profile groups, loaded with <c>--include</c>.</summary>
    public string? IncludeFile { get; init; }

    public IReadOnlyList<string> ExtraArguments { get; init; } = [];

    /// <summary>False when mpv's own watch_later state is allowed to win over the server's position.</summary>
    public bool OverrideMpvResume { get; init; } = true;

    public long RunTimeTicks { get; init; }

    public string ItemId { get; init; } = "";

    public string? MediaSourceId { get; init; }

    /// <summary>Emby stream indexes, kept for the playback reports rather than for mpv.</summary>
    public int? AudioStreamIndex { get; init; }

    public int? SubtitleStreamIndex { get; init; }

    public string? ShaderReason { get; init; }
}

public enum PlaybackEndReason
{
    /// <summary>mpv exited without saying why — the IPC channel was off or already gone.</summary>
    Unknown,
    /// <summary>Played to the end.</summary>
    EndOfFile,
    /// <summary>The user closed mpv or pressed q.</summary>
    UserQuit,
    /// <summary>The client asked mpv to stop, e.g. because the app is shutting down.</summary>
    Stopped,
    /// <summary>mpv could not play the file.</summary>
    Error
}

public sealed record PlaybackExit(PlaybackEndReason Reason, double? PositionSeconds, int ExitCode, string? Message)
{
    public bool IsFailure => Reason == PlaybackEndReason.Error;
}

/// <summary>
/// One entry of mpv's <c>track-list</c>, used to rebuild the track pickers while playback runs.
/// The id is mpv's own track id, which is what <c>aid</c>/<c>sid</c> want — not the Emby stream
/// index the launch ticket carries.
/// </summary>
public sealed record MpvTrack(int Id, string Type, string? Language, string? Title, bool Default, bool Selected)
{
    public bool IsAudio => Type == "audio";

    public bool IsSubtitle => Type == "sub";

    /// <summary>The language, or the title when there is none; an empty string when neither exists.</summary>
    public string DisplayLabel
    {
        get
        {
            var description = string.IsNullOrWhiteSpace(Language) ? Title : Language;
            return string.IsNullOrWhiteSpace(description) ? "" : description;
        }
    }
}

/// <summary>The outcome the UI shows once mpv is gone.</summary>
public sealed record PlaybackResult(PlaybackExit Exit, long PositionTicks, bool MarkedWatched, bool ProgressReported)
{
    public string ToChinese() => Exit.Reason switch
    {
        PlaybackEndReason.EndOfFile => MarkedWatched ? "播放完毕，已标记为已观看" : "播放完毕",
        PlaybackEndReason.UserQuit => MarkedWatched ? "已退出播放，已标记为已观看" : "已退出播放",
        PlaybackEndReason.Stopped => "播放已停止",
        PlaybackEndReason.Error => $"播放失败：{Exit.Message}",
        _ => "播放结束"
    };
}

/// <summary>
/// Everything the player chrome draws, as one immutable snapshot. It exists because the bar used
/// to ask the backend for each value separately, once every five seconds: the clock only moved
/// on progress reports and the seek bar had nothing to draw between them. The backend now pushes
/// this whole record whenever mpv says something changed, so the UI never polls and never shows a
/// value that came from a different moment than the one next to it.
/// </summary>
public readonly record struct PlayerStatus
{
    /// <summary>
    /// A struct's field initializers only run for <c>new PlayerStatus()</c>, never for
    /// <c>default</c>; every status in this codebase starts from this constructor, so the
    /// "nothing known yet" values below are the ones the UI actually sees.
    /// </summary>
    public PlayerStatus()
    {
    }

    /// <summary>Seconds into the file; negative means "not known yet".</summary>
    public double Position { get; init; } = -1;

    public double Duration { get; init; }

    /// <summary>How far the demuxer has read ahead, in seconds from the start of the file.</summary>
    public double CacheEnd { get; init; }

    public bool Paused { get; init; }

    /// <summary>Stalled waiting for data — drawn differently from a user pause.</summary>
    public bool Buffering { get; init; }

    public bool Muted { get; init; }

    /// <summary>mpv's own scale, where 100 is unattenuated.</summary>
    public double Volume { get; init; } = 100;

    public double Speed { get; init; } = 1;

    /// <summary>Set once mpv has the file open; before that the bar shows 「正在打开…」.</summary>
    public bool Loaded { get; init; }

    public bool HasPosition => Position >= 0;

    public bool HasDuration => Duration > 0.05;

    public double Fraction => HasDuration && HasPosition ? Math.Clamp(Position / Duration, 0, 1) : 0;

    public double CacheFraction => HasDuration ? Math.Clamp(CacheEnd / Duration, 0, 1) : 0;

    public string PositionClock => TimeFormat.Clock(TimeSpan.FromSeconds(Math.Max(0, Position)));

    public string DurationClock => TimeFormat.Clock(TimeSpan.FromSeconds(Math.Max(0, Duration)));

    /// <summary>
    /// True when the two snapshots differ in anything the chrome draws. The position is compared
    /// at a quarter of a second, which is finer than the bar can show and far coarser than mpv's
    /// per-frame notifications — without it every frame would cost a marshalled UI update.
    /// </summary>
    public bool DiffersFrom(PlayerStatus other) =>
        Paused != other.Paused
        || Buffering != other.Buffering
        || Muted != other.Muted
        || Loaded != other.Loaded
        || HasPosition != other.HasPosition
        || Math.Abs(Volume - other.Volume) > 0.5
        || Math.Abs(Speed - other.Speed) > 0.005
        || Math.Abs(Duration - other.Duration) > 0.05
        || Math.Abs(Position - other.Position) >= 0.25
        || Math.Abs(CacheEnd - other.CacheEnd) >= 1;
}

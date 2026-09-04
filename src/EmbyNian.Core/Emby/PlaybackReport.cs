namespace EmbyNian.Emby;

/// <summary>
/// Body for the Sessions/Playing endpoints. Emby ignores fields it does not know,
/// so one shape serves start, progress and stop.
/// </summary>
public sealed class PlaybackReport
{
    public required string ItemId { get; init; }

    public string? MediaSourceId { get; init; }

    public required string PlaySessionId { get; init; }

    public long PositionTicks { get; set; }

    public bool IsPaused { get; set; }

    public bool IsMuted { get; set; }

    public bool CanSeek { get; init; } = true;

    /// <summary>Always DirectStream: this client hands the original file to mpv.</summary>
    public string PlayMethod { get; init; } = "DirectStream";

    public int? AudioStreamIndex { get; init; }

    public int? SubtitleStreamIndex { get; init; }

    /// <summary>
    /// 音量, 0–130, or null when this playback has no control channel to ask. Emby's remote-control view shows
    /// it beside <see cref="IsMuted"/>.
    /// <para>
    /// Both of these were declared and never assigned for a long time, which is worse than not reporting them:
    /// the remote showed 100 and let the viewer adjust from that number. Null is the honest answer for a
    /// playback nobody can read the volume of — an external mpv with its IPC channel switched off.
    /// </para>
    /// </summary>
    public int? VolumeLevel { get; set; }

    /// <summary>"timeupdate", "pause" or "unpause" for progress reports.</summary>
    public string? EventName { get; set; }

    /// <summary>Set on the stop report when the user quit mpv rather than reaching the end.</summary>
    public bool Failed { get; set; }
}

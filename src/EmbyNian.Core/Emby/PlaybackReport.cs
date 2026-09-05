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

    /// <summary>
    /// 静音, or null when this playback has no control channel to ask — see <see cref="VolumeLevel"/>, which
    /// this one is only meaningful beside.
    /// <para>
    /// Nullable for the same reason, and it takes one extra step to be honest: Emby's own field is a plain
    /// <c>bool</c>, so 「不知道」 cannot be sent as a value. It is expressed by leaving the field out of the
    /// body altogether, which <see cref="EmbyHttp.Json"/> does for every null it is handed
    /// (<c>WhenWritingNull</c>) — the server then keeps whatever it had rather than being told 「没静音」.
    /// </para>
    /// </summary>
    public bool? IsMuted { get; set; }

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
    /// playback nobody can read the volume of — an external mpv with its IPC channel switched off. **两项都要
    /// 是这个待遇**：只把音量改成 null，静音那一头照旧报 false，遥控上就还是一个凑出来的读数，允许点、点了没用。
    /// </para>
    /// </summary>
    public int? VolumeLevel { get; set; }

    /// <summary>"timeupdate", "pause" or "unpause" for progress reports.</summary>
    public string? EventName { get; set; }

    /// <summary>Set on the stop report when the user quit mpv rather than reaching the end.</summary>
    public bool Failed { get; set; }
}

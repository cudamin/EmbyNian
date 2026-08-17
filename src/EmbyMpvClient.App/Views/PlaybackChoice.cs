using EmbyMpvClient.Emby;

namespace EmbyMpvClient.App.Views;

/// <summary>
/// What the user settled on before mpv is launched. Separate from <see cref="PlaybackTicket"/>
/// because the shell still has to resolve the parent series and the shader group afterwards.
/// </summary>
public sealed record PlaybackChoice(
    MediaSource Source,
    int? AudioStreamIndex,
    int? SubtitleStreamIndex,
    bool SubtitlesDisabled,
    long StartTicks);
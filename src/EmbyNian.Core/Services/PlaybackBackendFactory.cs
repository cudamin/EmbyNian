using EmbyNian.Configuration;
using EmbyNian.Playback;

namespace EmbyNian.Services;

/// <summary>
/// Builds the player the settings ask for. The one place that knows there are two backends, which is why
/// <see cref="PlaybackService"/> takes a <see cref="Func{TResult}"/> and never names either of them.
/// <para>
/// No interface: nothing but the container and the app's window hookup ever touches this, and a second
/// implementation would be a second pair of backends.
/// </para>
/// </summary>
public sealed class PlaybackBackendFactory(AppSettings settings, ShaderStaging shaders)
{
    /// <summary>
    /// The playing surface for this launch — the shell's answer to 「which panel carries the
    /// picture」. An <b>集成模式</b> member: the choice between the pipelines lives in the backend
    /// now (branched on the settings' <see cref="VideoPipelineKind"/>, 2026-09-16 第二形态), and the
    /// 独立播放 pipeline — mpv's own top-level window — never asks this delegate at all. What comes
    /// back is the attached page's <c>SwapChainVideoTarget</c>, read afresh for every launch.
    /// <para>
    /// This one delegate is the shell's side of the video contract. Core still names no XAML type
    /// and no window class: <see cref="IVideoSurface"/> lives there, the implementation in the
    /// shell's windowing layer.
    /// </para>
    /// </summary>
    public Func<IVideoSurface?>? VideoSurface { get; set; }

    /// <summary>
    /// Builds the player the settings ask for. Called once per playback, so switching between embedded
    /// libmpv and the standalone mpv.exe takes effect on the very next play.
    /// </summary>
    public IPlaybackBackend Create()
    {
        shaders.Ensure();

        return settings.Mpv.Backend switch
        {
            MpvBackendKind.ExternalMpv => new MpvProcessBackend(settings.Mpv),
            _ => new LibMpvBackend(settings.Mpv, () => VideoSurface?.Invoke())
        };
    }
}

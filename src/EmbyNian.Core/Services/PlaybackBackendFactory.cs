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
    /// The playing surface for this launch — the shell's answer to 「which pipeline carries the
    /// picture」. The integrated player hands over its page's SwapChainPanel (composition, mixed
    /// into the visual tree); the 独立播放窗口 hands over its video child HWND (mpv presents its
    /// own swapchain, straight to the DWM). Read afresh for every launch and re-pointed when the
    /// separate window opens or closes, which is the whole of the switch.
    /// <para>
    /// This one delegate is the shell's side of the video contract. Core still names no XAML type
    /// and no window class: <see cref="IVideoSurface"/> lives here, both implementations stay there.
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

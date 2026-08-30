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
    /// Where the embedded player draws. Set once, by the app, as soon as the host window exists; the
    /// window creates the surface on the first call and hands out the same HWND forever after.
    /// <para>
    /// This one delegate is the whole of the shell's side of the video contract, which is why porting the
    /// player was a matter of presentation rather than of engine work: everything below it already lived
    /// in Core with no reference to any UI framework.
    /// </para>
    /// </summary>
    public Func<IntPtr>? EmbeddedWindow { get; set; }

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
            _ => new LibMpvBackend(settings.Mpv, () => EmbeddedWindow?.Invoke() ?? IntPtr.Zero)
        };
    }
}

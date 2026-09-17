namespace EmbyNian.Playback;

/// <summary>
/// The UI-free surface contract for 集成模式: mpv's <c>d3d11-output-mode=composition</c>
/// swapchain becomes an <c>ICompositionSurface</c> drawn by the shell's <c>SpriteVisual</c>.
/// 独立播放 never uses this interface: mpv creates and manages its own top-level window.
/// </summary>
public interface IVideoSurface
{
    /// <summary>
    /// The current rendering size in physical pixels, not DIPs. The shell converts its layout
    /// size with the current <c>RasterizationScale</c> (see <see cref="VideoSurfaceSize.FromDips"/>);
    /// the visual's layout size remains in DIPs. A zero dimension means layout is not ready.
    /// The backend supplies a provisional 1x1 size at initialization so mpv can create its vo.
    /// Safe to read from any thread: return a cached UI measurement, not live XAML properties.
    /// </summary>
    (int Width, int Height) Size { get; }

    /// <summary>
    /// A layout size or rasterization scale changed. Raised on the UI thread and debounced
    /// during resize. The backend writes <see cref="Size"/> to <c>d3d11-composition-size</c>
    /// and refreshes the current swapchain.
    /// </summary>
    event Action? GeometryChanged;

    /// <summary>
    /// Attaches mpv's <c>display-swapchain</c> to the composition surface;
    /// <see cref="IntPtr.Zero"/> detaches the current picture.
    /// The nonzero COM pointer is borrowed and valid only for the duration of this call.
    /// An implementation retaining it or dispatching work to another thread must synchronously
    /// <c>AddRef</c> before returning and balance that reference with <c>Release</c>, including
    /// dispatcher failure and discarded/stale callbacks. Never release the caller's borrowed reference.
    /// <para>
    /// May be called from any thread. The implementation owns UI dispatch, must not synchronously
    /// wait for the UI thread, and must preserve the latest call's result: a queued old attachment
    /// must never overwrite a later detach or a newer playback's attachment.
    /// </para>
    /// </summary>
    void AttachSwapChain(IntPtr swapChain);
}

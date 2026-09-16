namespace EmbyNian.Playback;

/// <summary>
/// The playing surface the in-process player composites onto: a WinUI <c>SwapChainPanel</c> inside
/// a player page's visual tree. UI-free by design — Core never names a XAML type, so the shell
/// implements this around its panel and the backend reaches the panel only through the members
/// below.
/// <para>
/// One rendering pipeline serves both hosts. The integrated player composites onto the shell's
/// player page, video and chrome in one tree; the 独立播放窗口 runs its own PlayerPage — a second
/// top-level window, the shell's sibling rather than its child, freely movable, minimisable and
/// focused on its own — whose page answers with its own panel. Which host a playback gets is a
/// per-play decision the shell makes by pointing the surface delegate at one page or the other;
/// the pipeline underneath is the same D3D11 composition output either way.
/// </para>
/// <para>
/// The threading shape is mpv's, not ours. mpv creates the swapchain on its render thread and the
/// client learns of a new one from the <c>display-swapchain</c> property on the backend's event
/// thread — which is why <see cref="AttachSwapChain"/> may be called off the UI thread and why the
/// implementation, not the caller, owns the hop to the dispatcher.
/// </para>
/// </summary>
public interface IVideoSurface
{
    /// <summary>
    /// The panel's current rendering size in physical pixels (layout size × composition scale).
    /// <c>(0, 0)</c> means the panel has not been laid out yet — the backend skips the size option
    /// for that pass and waits for the next <see cref="GeometryChanged"/>.
    /// <para>
    /// Safe to read from any thread: the implementation keeps the last value the UI thread
    /// measured, rather than walking live XAML properties on the caller's thread.
    /// </para>
    /// </summary>
    (int Width, int Height) Size { get; }

    /// <summary>
    /// The geometry moved — a resize or a composition-scale change, debounced so a drag does not
    /// queue one call per pixel. Raised on the UI thread. The backend answers by writing
    /// <see cref="Size"/> back into <c>d3d11-composition-size</c> and re-attaching the current
    /// swapchain, which is also what refreshes the DPI matrix transform: one path, one answer.
    /// </summary>
    event Action? GeometryChanged;

    /// <summary>
    /// Composites mpv's swapchain onto the panel; <see cref="IntPtr.Zero"/> takes the current one
    /// down. The pointer is mpv's — read from the <c>display-swapchain</c> property, and only ever
    /// borrowed: mpv owns its lifetime, and a newer playback (or a device loss, which surfaces as
    /// the property changing) replaces it wholesale.
    /// <para>
    /// May be called from any thread. The implementation marshals to the UI thread itself, applies
    /// the inverse composition-scale matrix (so mpv's pixels map one-to-one onto the panel) and
    /// hands the swapchain to <c>ISwapChainPanelNative.SetSwapChain</c>.
    /// </para>
    /// </summary>
    void AttachSwapChain(IntPtr swapChain);
}

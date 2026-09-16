namespace EmbyNian.Playback;

/// <summary>
/// The playing surface of the <b>集成模式</b> pipeline — the WinUI <c>SwapChainPanel</c> inside a
/// player page's visual tree. mpv runs its D3D11 composition output and hands the swapchain over
/// through the members below; the picture is a child of the page, mixed into the same tree as the
/// chrome. UI-free by design — Core never names a XAML type, so the shell implements this around
/// its panel.
/// <para>
/// 独立播放 does not come through this interface at all (2026-09-16 reworked after its first
/// shape): that pipeline is mpv's default window mode — with no <c>wid</c> and no surface handed
/// over, mpv creates and manages its own top-level window and swapchain, and the client's only
/// share in the picture is a log line. The backend decides between the two on the settings'
/// <see cref="Configuration.VideoPipelineKind"/>, not on anything this interface used to answer —
/// the <c>WindowHandle</c> discriminator and its underlay window were retired with the wid path.
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
    /// The panel's current rendering size in DIPs (layout size; 缩放不参与——合成尺寸以 DIP 计，
    /// 见实现类的 DPI 段）。<c>(0, 0)</c> means the panel has not been laid out yet — the backend
    /// answers with a provisional size rather than skipping, because a missing
    /// <c>d3d11-composition-size</c> makes this mpv build fall back off the composition vo
    /// entirely (probed 2026-09-16).
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
    /// swapchain: one path, one answer.
    /// </summary>
    event Action? GeometryChanged;

    /// <summary>
    /// Composites mpv's swapchain onto the panel; <see cref="IntPtr.Zero"/> takes the current one
    /// down. The pointer is mpv's — read from the <c>display-swapchain</c> property, and only ever
    /// borrowed: mpv owns its lifetime, and a newer playback (or a device loss, which surfaces as
    /// the property changing) replaces it wholesale.
    /// <para>
    /// May be called from any thread. The implementation marshals to the UI thread itself and
    /// hands the swapchain to <c>ISwapChainPanelNative.SetSwapChain</c>. (The inverse-scale matrix
    /// this call once also set is gone for good: this mpv build's <c>display-swapchain</c> is a
    /// hand-rolled wrapper object whose vtable does not answer <c>SetMatrixTransform</c> — probed
    /// 2026-09-16 — and DIP sizing is geometrically correct without it.)
    /// </para>
    /// </summary>
    void AttachSwapChain(IntPtr swapChain);
}

namespace EmbyNian.Playback;

/// <summary>
/// The playing surface the in-process player renders onto — one of two shapes, one per rendering
/// pipeline (小幻影视's 「集成模式 / 独立播放」 split, 2026-09-16):
/// <list type="bullet">
///   <item><b>集成模式</b> — a WinUI <c>SwapChainPanel</c> inside a player page's visual tree. mpv
///   runs its D3D11 composition output and hands the swapchain over through the members below; the
///   picture is a child of the page, mixed into the same tree as the chrome. UI-free by design —
///   Core never names a XAML type, so the shell implements this around its panel.</item>
///   <item><b>独立播放</b> — a plain Win32 child window sitting under the XAML island. mpv is
///   pointed at it with <c>wid</c> and keeps its swapchain to itself (auto → window presentation):
///   it creates it, sizes it, presents it, and the client never sees a pointer. The only member
///   that pipeline answers is <see cref="WindowHandle"/>; the three below exist on the interface so
///   one surface type can flow from the shell to the backend, and the implementation says which of
///   them degenerate.</item>
/// </list>
/// Which pipeline a playback gets is the surface the shell hands over — read afresh every launch,
/// so switching the setting lands on the next play. The backend branches on
/// <see cref="WindowHandle"/>, not on a second switch of its own.
/// <para>
/// The threading shape is mpv's, not ours. In the integrated pipeline mpv creates the swapchain on
/// its render thread and the client learns of a new one from the <c>display-swapchain</c> property
/// on the backend's event thread — which is why <see cref="AttachSwapChain"/> may be called off the
/// UI thread and why the implementation, not the caller, owns the hop to the dispatcher. The
/// standalone pipeline crosses no such boundary: mpv drives its window itself.
/// </para>
/// </summary>
public interface IVideoSurface
{
    /// <summary>
    /// 独立播放管线的去处：交给 mpv 作 <c>wid</c> 的窗口句柄，播放开始前就绪、播放期间不变。
    /// 集成管线的实现答 <see cref="IntPtr.Zero"/>——后端以「有没有窗口」分流两条管线，这是判别式，
    /// 不是可有可无的注释。
    /// </summary>
    virtual IntPtr WindowHandle => IntPtr.Zero;
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

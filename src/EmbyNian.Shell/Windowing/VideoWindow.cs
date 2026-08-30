using System.Runtime.InteropServices;
using EmbyNian.Diagnostics;
using EmbyNian.Shell.Interop;

namespace EmbyNian.Shell.Windowing;

/// <summary>
/// The child HWND mpv draws into, a sibling of the XAML island inside <see cref="HostWindow"/>'s client
/// area and permanently below it in z-order.
/// <para>
/// This is all the video contract needs: <c>LibMpvBackend</c> takes a <c>Func&lt;IntPtr&gt;</c>, hands it
/// to mpv as <c>wid</c>, and mpv creates its own child inside this one and drives it. No render API, no
/// ANGLE, no SwapChainPanel — fact 1 in <see cref="HostWindow"/>'s remarks is what buys that.
/// </para>
/// <para>
/// It has no window procedure of its own. Everything it has to do — fill black, clip mpv's child, resize
/// with the parent — is either a class attribute or one <c>SetWindowPos</c>, so the message loop can go
/// straight to <c>DefWindowProc</c>. That also settles requirement 12 for free: the WinForms shell had to
/// call <c>BringToFront</c> on every overlay because mpv's window was created last and started on top of
/// the chrome, and the 「正在切换…」 cover kept losing that race and ending up behind a stale frame. Here
/// the island is above the video by construction and nothing can get between them.
/// </para>
/// <para>
/// Mouse input is the other thing this arrangement gives away. In WinForms mpv's child swallowed every
/// message that landed on the picture, so the chrome had to be driven from a 40 ms <c>Cursor.Position</c>
/// poll and a <c>SetWindowLongPtr</c> subclass of a window we did not own. The island covers the whole
/// client area and sits above this one, so hit-testing hands every click and every move to XAML first and
/// both of those disappear.
/// </para>
/// </summary>
internal sealed class VideoWindow : IDisposable
{
    private const string Category = "视频窗口";
    private const string ClassName = "EmbyNianVideo";

    private static bool _classRegistered;

    /// <summary>
    /// Forwards straight to <c>DefWindowProc</c> and is held for the process lifetime, because Windows
    /// keeps the raw thunk and a collected delegate would crash the first message after a GC. It exists at
    /// all only because managed code cannot name <c>DefWindowProcW</c> as a class procedure without a
    /// <c>GetProcAddress</c> dance, and this window genuinely has nothing to say: the class brush and
    /// <c>WS_CLIPCHILDREN</c> already cover everything it does.
    /// </summary>
    private static readonly WindowProcedure Procedure = Native.DefWindowProc;

    /// <summary>
    /// The class background brush, so the default erase paints black without a window procedure. Black
    /// rather than the app's base colour: this is the letterbox around the picture, and a video's surround
    /// is black everywhere else the user has ever watched one.
    /// </summary>
    private static readonly IntPtr BlackBrush = Native.CreateSolidBrush(0x00000000);

    private readonly IntPtr _parent;
    private bool _disposed;

    /// <summary>
    /// Creates the surface filling <paramref name="parent"/>'s client area and drops it below
    /// <paramref name="island"/>.
    /// </summary>
    public VideoWindow(IntPtr parent, IntPtr island)
    {
        _parent = parent;

        var instance = Native.GetModuleHandle(null);
        EnsureClassRegistered(instance);

        Native.GetClientRect(parent, out var client);

        Handle = Native.CreateWindowEx(
            0,
            ClassName,
            null,
            Native.WsChild | Native.WsVisible | Native.WsClipChildren,
            0,
            0,
            client.Width,
            client.Height,
            parent,
            IntPtr.Zero,
            instance,
            IntPtr.Zero);

        if (Handle == IntPtr.Zero)
            throw new InvalidOperationException($"创建视频窗口失败，错误码 {Marshal.GetLastWin32Error()}");

        // Created after the island, so it starts above it. Passing the island as the insert-after window
        // is what puts it directly underneath — the one z-order statement this class makes, and it is
        // made once rather than re-asserted on every frame the way the WinForms overlays had to be.
        Native.SetWindowPos(
            Handle, island, 0, 0, 0, 0,
            Native.SwpNoMove | Native.SwpNoSize | Native.SwpNoActivate);

        Log.Info(Category, $"视频窗口已创建 hwnd=0x{Handle:X}，位于 XAML 岛之下");
    }

    public IntPtr Handle { get; private set; }

    /// <summary>
    /// Resizes to the parent's client area. Called from <c>WM_SIZE</c>, once, next to the island's single
    /// <c>MoveAndResize</c>: two calls per resize against the WinForms shell's nine, which is most of why
    /// 「调整画面大小时窗口的变化」 is smoother here (requirement 1).
    /// </summary>
    public void Fill()
    {
        if (Handle == IntPtr.Zero) return;

        Native.GetClientRect(_parent, out var client);
        Native.SetWindowPos(
            Handle, IntPtr.Zero, 0, 0, client.Width, client.Height,
            Native.SwpNoZOrder | Native.SwpNoActivate | Native.SwpNoCopyBits);
    }

    private static void EnsureClassRegistered(IntPtr instance)
    {
        if (_classRegistered) return;

        // Never freed, like the host window's: RegisterClassEx keeps the pointer rather than copying the
        // string, and the class outlives every window in the process.
        var className = Marshal.StringToHGlobalUni(ClassName);

        var windowClass = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(),
            Procedure = Marshal.GetFunctionPointerForDelegate(Procedure),
            Instance = instance,
            // No class cursor: the island covers this window completely, so a mouse move never reaches it
            // and the cursor over the picture is XAML's to decide.
            Cursor = IntPtr.Zero,
            Background = BlackBrush,
            ClassName = className
        };

        if (Native.RegisterClassEx(ref windowClass) == 0)
            throw new InvalidOperationException($"注册视频窗口类失败，错误码 {Marshal.GetLastWin32Error()}");

        _classRegistered = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (Handle == IntPtr.Zero) return;

        var window = Handle;

        // Cleared before the destroy rather than after: mpv's own child goes with it, and anything that
        // asks for the handle in between must get zero rather than a freed HWND.
        Handle = IntPtr.Zero;
        Native.DestroyWindow(window);
    }
}

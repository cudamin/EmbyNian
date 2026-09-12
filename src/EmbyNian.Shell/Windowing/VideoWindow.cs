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
    /// 缩放到父窗口客户区大小。**只改自己，不碰 mpv 的子窗口** —— 那块子窗口归 mpv 管。
    /// <para>
    /// mpv 在嵌入模式往<b>我们这条线程</b>上装了一个 <c>WH_CALLWNDPROC</c> 钩子（mpv 源码
    /// w32_common.c 的 <c>resize_child_win</c>，mpv.net 同款机制）：我们对自己窗口的每一次
    /// <c>SetWindowPos</c> 都会发出 <c>WM_WINDOWPOSCHANGED</c>，钩子当场读它和 mpv 子窗口两边
    /// 的实际矩形，不一致就自己把差补上，暂停时也会重画。过渡期的每一拍它都记着，最后一拍
    /// 必是最终尺寸，所以它自己就收敛 —— mpv.net 对这件事一件事都不做，多年没出过病。
    /// </para>
    /// <para>
    /// 宿主反而不能亲手帮忙：我们同步改它的子窗口，会按 Win32 的规矩<b>丢弃 mpv 挂起的异步
    /// 追赶</b>；过渡期中间态的尺寸（比如退全屏时边框回收那一拍）先落了地、正确的最终一拍被
    /// 扔掉，mpv 就永远卡在中间尺寸 —— 「进全屏画面缩在左上角」「退全屏后画面卡着不放」
    /// 2026-09-11 的日志里每一起都是它。谁的手都别伸，是最好的修法。
    /// </para>
    /// </summary>
    public void Fill()
    {
        if (Handle == IntPtr.Zero) return;

        Native.GetClientRect(_parent, out var client);
        Native.SetWindowPos(
            Handle, IntPtr.Zero, 0, 0, client.Width, client.Height,
            Native.SwpNoZOrder | Native.SwpNoActivate | Native.SwpNoCopyBits);

        // 诊断（2026-09-11 深夜）：干净探针里 mpv 自己的钩子全程跟上，应用里却没跟 —— 打出两边
        // 的实际几何，让下一次复现直接告诉我们钩子到底 firing 没有。子窗口是 mpv 自己的，不碰。
        var child = Native.GetWindow(Handle, Native.GwChild);
        if (child != IntPtr.Zero && Native.GetWindowRect(child, out var r))
        {
            var corner = new NativePoint { X = r.Left, Y = r.Top };
            var origin = Native.ScreenToClient(_parent, ref corner) ? $"@({corner.X},{corner.Y})" : "?";
            Log.Debug(Category,
                $"Fill 后：客户区 {client.Width}×{client.Height}，mpv 子窗口 {r.Width}×{r.Height}{origin}");
        }
        else
        {
            Log.Debug(Category, $"Fill 后：客户区 {client.Width}×{client.Height}，尚无 mpv 子窗口");
        }
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

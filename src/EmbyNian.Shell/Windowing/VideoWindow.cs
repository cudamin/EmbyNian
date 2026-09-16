using System.Runtime.InteropServices;
using EmbyNian.Diagnostics;
using EmbyNian.Playback;
using EmbyNian.Shell.Interop;

namespace EmbyNian.Shell.Windowing;

/// <summary>
/// 独立播放管线的去处（2026-09-16，参考小幻影视的「集成模式 / 独立播放」两档）：HostWindow 客户区里、
/// XAML 岛正下方的一个原生子窗口。mpv 以 <c>wid</c> 接管它，在里面自建、自呈现自己的交换链——交换链
/// 从头到尾归 mpv，客户端一个指针都看不见。这与二十二报立起来的合成管线并存，是老 wid 路的原样复活，
/// 只是从此各管各的播放，谁也不替换谁。
/// </summary>
/// <remarks>
/// <para>
/// It has no window procedure of its own. Everything it has to do — fill black, clip mpv's child,
/// resize with the parent — is either a class attribute or one <c>SetWindowPos</c>, so the message
/// loop can go straight to <c>DefWindowProc</c>. The island stays above the video by construction:
/// created after the island it starts on top of it, and the constructor's one <c>SetWindowPos</c>
/// with the island as the insert-after window is the whole of the z-order statement this class
/// makes. Nothing gets between them.
/// </para>
/// <para>
/// Mouse input is given away by the same arrangement. The island covers the whole client area and
/// sits above this one, so hit-testing hands every click and every move to XAML first — the reveal
/// rule, the right-click menu and the cursor-hiding arrangements read the input they always did,
/// and this window never sees a mouse message (hence no class cursor).
/// </para>
/// <para>
/// resize 对账铁律（老路栽过两次，2026-09-11 每一起都是它）：<see cref="Fill"/> 只改自己，
/// <b>绝不碰 mpv 在里面建的子窗口</b>——mpv 在嵌入模式往我们的线程上装了 <c>WH_CALLWNDPROC</c> 钩子
/// （w32_common.c 的 <c>resize_child_win</c>，mpv.net 同款），宿主每一次 <c>SetWindowPos</c> 发出的
/// <c>WM_WINDOWPOSCHANGED</c> 它当场读、两边矩形不一致它自己补。宿主同步去帮，会把 mpv 挂起的异步
/// 追赶按 Win32 的规矩丢掉，画面就永远卡在中间尺寸——「进全屏画面缩在左上角」「退全屏后画面卡着
/// 不放」全是这么来的。谁的手都别伸，是最好的修法。
/// </para>
/// <para>
/// <see cref="IVideoSurface"/> 的三个合成成员在这里全是陪跑，接口形状只为让一张票从壳流到后端：
/// <see cref="Size"/> 答客户区的物理像素（独立管线里其实没人读它——mpv 自己量窗口）；几何变化由
/// <see cref="Fill"/> 举起 <see cref="GeometryChanged"/>（同样没有订阅者，但契约答数要真）；
/// <see cref="AttachSwapChain"/> 是接错线的警报——独立管线没有面板可挂。后端分流看的是
/// <see cref="WindowHandle"/>：非零即是「mpv 独占」。
/// </para>
/// </remarks>
internal sealed class VideoWindow : IDisposable, IVideoSurface
{
    private const string Category = "视频窗口";
    private const string ClassName = "EmbyNianVideo";

    private static bool _classRegistered;

    /// <summary>
    /// Forwards straight to <c>DefWindowProc</c> and is held for the process lifetime, because Windows
    /// keeps the raw thunk and a collected delegate would crash the first message after a GC. It exists
    /// at all only because managed code cannot name <c>DefWindowProcW</c> as a class procedure without
    /// a <c>GetProcAddress</c> dance, and this window genuinely has nothing to say: the class brush and
    /// <c>WS_CLIPCHILDREN</c> already cover everything it does.
    /// </summary>
    private static readonly WindowProcedure Procedure = Native.DefWindowProc;

    /// <summary>
    /// The class background brush, so the default erase paints black without a window procedure. Black
    /// rather than the app's base colour: this is the letterbox around the picture, and a video's
    /// surround is black everywhere else the user has ever watched one.
    /// </summary>
    private static readonly IntPtr BlackBrush = Native.CreateSolidBrush(0x00000000);

    private readonly IntPtr _parent;
    private bool _disposed;

    /// <summary>Creates the surface filling <paramref name="parent"/>'s client area and drops it below
    /// <paramref name="island"/>.</summary>
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

        // Created after the island, so it starts above it. Passing the island as the insert-after
        // window is what puts it directly underneath — the one z-order statement this class makes,
        // and it is made once rather than re-asserted on every frame the way the WinForms overlays
        // had to be.
        Native.SetWindowPos(
            Handle, island, 0, 0, 0, 0,
            Native.SwpNoMove | Native.SwpNoSize | Native.SwpNoActivate);

        Log.Info(Category, $"视频子窗口已创建 hwnd=0x{Handle:X}，位于 XAML 岛之下（独立播放管线的去处）");
    }

    public IntPtr Handle { get; private set; }

    /// <summary>
    /// <see cref="IVideoSurface.WindowHandle"/>：独立播放管线的判别式。非零即是「mpv 独占」——后端拿
    /// 它当 <c>wid</c>，合成的三个成员一个都不会被走。
    /// </summary>
    public IntPtr WindowHandle => Handle;

    /// <summary>
    /// 客户区的物理像素。独立管线里没有谁消费它（mpv 自己量窗口），但契约答的数要真：窗口没了给
    /// (0, 0)，跟面板尚未布局时的答案同一个形状。
    /// </summary>
    public (int Width, int Height) Size
    {
        get
        {
            if (Handle == IntPtr.Zero) return (0, 0);

            Native.GetClientRect(Handle, out var client);
            return (Math.Max(client.Width, 0), Math.Max(client.Height, 0));
        }
    }

    /// <summary>
    /// 几何动了。独立管线里没有订阅者（后端只在合成管线订阅），但 <see cref="Fill"/> 是这个窗口唯一
    /// 的几何事件，答案就从那里举。
    /// </summary>
    public event Action? GeometryChanged;

    /// <summary>
    /// 接错线的警报：独立播放管线没有面板，交换链也从来不在客户端手里。真到了这一步，问题在接线
    /// 不在渲染——喊出来，让人去找那根错的线。
    /// </summary>
    public void AttachSwapChain(IntPtr swapChain) =>
        Log.Warn(Category, $"独立播放管线不支持挂接交换链（0x{swapChain:X}）——mpv 独占自己的交换链");

    /// <summary>
    /// 缩放到父窗口客户区大小。<b>只改自己，不碰 mpv 的子窗口</b>——那块子窗口归 mpv 管（类的头注释
    /// 写着为什么宿主不能帮）。几何定案后举 <see cref="GeometryChanged"/>。
    /// </summary>
    public void Fill()
    {
        if (Handle == IntPtr.Zero) return;

        Native.GetClientRect(_parent, out var client);
        Native.SetWindowPos(
            Handle, IntPtr.Zero, 0, 0, client.Width, client.Height,
            Native.SwpNoZOrder | Native.SwpNoActivate | Native.SwpNoCopyBits);

        // 诊断探针（老路同款）：mpv 的钩子跟没跟上，一眼可见。子窗口是 mpv 自己的，只看不碰。
        var child = Native.GetWindow(Handle, Native.GwChild);
        if (child != IntPtr.Zero && Native.GetWindowRect(child, out var rect))
        {
            var corner = new NativePoint { X = rect.Left, Y = rect.Top };
            var origin = Native.ScreenToClient(_parent, ref corner) ? $"@({corner.X},{corner.Y})" : "?";
            Log.Debug(Category,
                $"Fill 后：客户区 {client.Width}×{client.Height}，mpv 子窗口 {rect.Width}×{rect.Height}{origin}");
        }
        else
        {
            Log.Debug(Category, $"Fill 后：客户区 {client.Width}×{client.Height}，尚无 mpv 子窗口");
        }

        GeometryChanged?.Invoke();
    }

    private static void EnsureClassRegistered(IntPtr instance)
    {
        if (_classRegistered) return;

        // Never freed, like the host window's: RegisterClassEx keeps the pointer rather than copying
        // the string, and the class outlives every window in the process.
        var className = Marshal.StringToHGlobalUni(ClassName);

        var windowClass = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(),
            Procedure = Marshal.GetFunctionPointerForDelegate(Procedure),
            Instance = instance,
            // No class cursor: the island covers this window completely, so a mouse move never
            // reaches it and the cursor over the picture is XAML's to decide.
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

        // Cleared before the destroy rather than after: mpv's own child goes with it, and anything
        // that asks for the handle in between must get zero rather than a freed HWND.
        Handle = IntPtr.Zero;
        Native.DestroyWindow(window);
    }
}

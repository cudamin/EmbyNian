using System.Runtime.InteropServices;
using EmbyNian.Diagnostics;
using EmbyNian.Playback;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT;

namespace EmbyNian.Shell.Windowing;

/// <summary>
/// The shell's half of the video contract: an <see cref="IVideoSurface"/> wrapped around the
/// player page's <see cref="SwapChainPanel"/>. mpv's D3D11 composition swapchain lands here and is
/// composited into the visual tree like any other XAML content — the picture is a child of the
/// page now, not a sibling HWND behind the island. Both hosts run this class: the integrated
/// player's page, and the page inside the 独立播放窗口.
/// <para>
/// Everything native is one COM call deep and handled locally: the panel is reached through
/// <c>ISwapChainPanelNative</c> (Queried off the panel's own IUnknown — the WinUI 3 IID differs
/// from the UWP header's, and this is the App SDK one). Both are plain vtable calls on pointers
/// this class Queries for and Releases; nothing is held across calls except the panel.
/// </para>
/// <para>
/// <b>DPI（2026-09-16 深夜的改法，有探针实证）</b>：合成尺寸按 <b>DIP</b> 喂给 mpv
/// （<c>d3d11-composition-size</c>），不再做「物理像素＋SetMatrixTransform 逆缩放」那套。原因：
/// 这份 libmpv 的 <c>display-swapchain</c> 交出来的不是真 DXGI 链，是一个手写包装对象——探针
/// （work/probe-composition2.txt）QI 电池显示它只答 IUnknown / IDXGISwapChain1 / IDXGISwapChain2
/// 三个 IID，连基接口 IDXGISwapChain 都不答（真 COM 继承下不可能），vtable 也不是标准布局，
/// 盲调 SetMatrixTransform 直接访问违例。DIP 尺寸不需要任何矩阵：「1 链像素＝1 DIP」与「拉伸铺满
/// 面板」两种合成映射模型下都几何正确，缩放屏上由 DWM 放大、画质略软——几何先对，锐度等换到
/// 交出真链的 dll 再说。
/// </para>
/// <para>
/// Threading. mpv calls <see cref="AttachSwapChain"/> from its event thread; the work hops to the
/// dispatcher here. <see cref="Size"/> is read from that same thread, so the live XAML properties
/// are never touched cross-thread — the last values the UI thread measured are cached instead, and
/// the cache is what callers get. Geometry changes funnel through a 100 ms debounce (the same
/// number mpv-winui uses) so a window drag cannot queue one mpv round trip per pixel.
/// </para>
/// </summary>
internal sealed class SwapChainVideoTarget : IVideoSurface
{
    private const string Category = "视频面板";

    /// <summary>
    /// ISwapChainPanelNative, Windows App SDK spelling (microsoft.ui.xaml.media.dxinterop.h). The
    /// UWP header gives the same interface a different IID — {F92F19D2-…} — and Querying the panel
    /// for that one fails; DirectN's author documented the swap and the App SDK header agrees.
    /// </summary>
    private static readonly Guid PanelNativeIid = new("63AAD0B8-7C24-40FF-85A8-640D944CC325");

    /// <summary>
    /// vtable slot. ISwapChainPanelNative adds SetSwapChain (3) directly on IUnknown. Only one
    /// native call is made through it — the class remark explains why the swapchain-side calls
    /// (SetMatrixTransform and friends) are gone.
    /// </summary>
    private const int SetSwapChainSlot = 3;

    private readonly SwapChainPanel _panel;
    private readonly DispatcherQueueTimer _debounce;

    /// <summary>
    /// The swapchain currently on the panel — the very pointer mpv handed over, held for
    /// comparison only. mpv owns its lifetime; a repeat attach of the same chain skips the
    /// SetSwapChain round trip, and a new chain replaces this wholesale.
    /// </summary>
    private IntPtr _attached;

    /// <summary>Last geometry the UI thread measured, in DIPs. <see cref="Size"/>'s whole answer, off-thread safe.</summary>
    private (int Width, int Height) _size;

    public SwapChainVideoTarget(SwapChainPanel panel)
    {
        _panel = panel;

        var dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("视频面板必须在界面线程上创建");

        // Debounce on the dispatcher: SizeChanged and CompositionScaleChanged both land here, the
        // timer folds a burst into one tick, and the tick is where GeometryChanged fires — on the
        // UI thread, as the interface promises. (CompositionScale 换档不再需要任何补偿动作——尺寸
        // 以 DIP 计，对缩放不敏感——但走一趟刷新让日志留痕，也让 mpv 有机会把链重排一次。)
        _debounce = dispatcher.CreateTimer();
        _debounce.Interval = TimeSpan.FromMilliseconds(100);
        _debounce.IsRepeating = false;
        _debounce.Tick += (_, _) => GeometryChanged?.Invoke();

        _panel.SizeChanged += OnPanelGeometryChanged;
        _panel.CompositionScaleChanged += OnPanelScaleChanged;
        Measure();
    }

    public event Action? GeometryChanged;

    public (int Width, int Height) Size => _size;

    public void AttachSwapChain(IntPtr swapChain)
    {
        // The hop the interface promises: mpv's event thread calls in, the panel is UI-thread land.
        _panel.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                Attach(swapChain);
            }
            catch (Exception error)
            {
                Log.Warn(Category, "挂接视频交换链失败", error);
            }
        });
    }

    /// <summary>UI-thread body of <see cref="AttachSwapChain"/>. Never throws past the catch above.</summary>
    private void Attach(IntPtr swapChain)
    {
        if (swapChain == IntPtr.Zero)
        {
            if (_attached != IntPtr.Zero) Log.Info(Category, "视频交换链已摘下");
            _attached = IntPtr.Zero;
            SetPanelSwapChain(IntPtr.Zero);
            return;
        }

        if (_attached == swapChain)
        {
            Log.Debug(Category, $"交换链未变，跳过重复挂接（{_size.Width}×{_size.Height} DIP）");
            return;
        }

        SetPanelSwapChain(swapChain);
        _attached = swapChain;
        Log.Info(Category, $"视频交换链已挂上 0x{swapChain:X}（合成尺寸 {_size.Width}×{_size.Height} DIP）");
    }

    /// <summary>
    /// Re-measures the panel into the cache, in DIPs. UI thread only. 缩放不再参与：composition-size
    /// 以 DIP 计（类的头注释写着为什么物理像素＋矩阵那套在这份 dll 上走不通）。
    /// </summary>
    private void Measure()
    {
        var width = (int)Math.Floor(_panel.ActualWidth);
        var height = (int)Math.Floor(_panel.ActualHeight);
        _size = (Math.Max(width, 0), Math.Max(height, 0));
    }

    private void OnPanelGeometryChanged(object sender, SizeChangedEventArgs e)
    {
        Measure();
        RestartDebounce();
    }

    private void OnPanelScaleChanged(SwapChainPanel sender, object args)
    {
        Measure();
        RestartDebounce();
    }

    private void RestartDebounce()
    {
        // One shot per burst: Stop re-arms the timer, so the last event of a drag is the one that
        // fires 100 ms later and the ones before it cost nothing.
        _debounce.Stop();
        _debounce.Start();
    }

    // ---- the COM call ------------------------------------------------------------

    private void SetPanelSwapChain(IntPtr swapChain)
    {
        var panelNative = QueryInterface(NativePointer(_panel), PanelNativeIid);
        try
        {
            GetMethod<SetSwapChainDelegate>(panelNative, SetSwapChainSlot)(panelNative, swapChain);
        }
        finally
        {
            Marshal.Release(panelNative);
        }
    }

    private static IntPtr NativePointer(object winrtObject) =>
        ((IWinRTObject)winrtObject).NativeObject.ThisPtr;

    private static IntPtr QueryInterface(IntPtr unknown, Guid iid)
    {
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out var pointer));
        return pointer;
    }

    private static T GetMethod<T>(IntPtr objectPtr, int slot) where T : notnull
    {
        var vtable = Marshal.ReadIntPtr(objectPtr);
        var slotPtr = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(slotPtr);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetSwapChainDelegate(IntPtr self, IntPtr swapChain);
}

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
/// Everything native is two COM calls deep and handled locally. The panel is reached through
/// <c>ISwapChainPanelNative</c> (Queried off the panel's own IUnknown — the WinUI 3 IID differs
/// from the UWP header's, and this is the App SDK one); the swapchain's DPI matrix goes through
/// <c>IDXGISwapChain2.SetMatrixTransform</c>, reached the same way. Both are plain vtable calls on
/// pointers this class Queries for and Releases; nothing is held across calls except the panel.
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

    /// <summary>IDXGISwapChain2, for SetMatrixTransform. The rest of the swapchain's surface is mpv's business.</summary>
    private static readonly Guid SwapChain2Iid = new("310D36A0-D2E7-4C91-AEE0-F443C77E5FB1");

    /// <summary>
    /// vtable slots. ISwapChainPanelNative adds SetSwapChain (3) directly on IUnknown;
    /// IDXGISwapChain2's SetMatrixTransform is slot 19 — IUnknown (3) + IDXGIObject (4) +
    /// IDXGISwapChain (6) + IDXGISwapChain1 (6), counted from the headers. A second method would be
    /// invented for no reader; the slots are stated where the delegates are declared.
    /// </summary>
    private const int SetSwapChainSlot = 3;
    private const int SetMatrixTransformSlot = 19;

    private readonly SwapChainPanel _panel;
    private readonly DispatcherQueueTimer _debounce;

    /// <summary>
    /// The swapchain currently on the panel — the very pointer mpv handed over, held for
    /// comparison only. mpv owns its lifetime; a repeat attach of the same chain re-derives the
    /// matrix and skips the SetSwapChain round trip, and a new chain replaces this wholesale.
    /// </summary>
    private IntPtr _attached;

    /// <summary>Last geometry the UI thread measured. <see cref="Size"/>'s whole answer, off-thread safe.</summary>
    private (int Width, int Height) _size;

    public SwapChainVideoTarget(SwapChainPanel panel)
    {
        _panel = panel;

        var dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("视频面板必须在界面线程上创建");

        // Debounce on the dispatcher: SizeChanged and CompositionScaleChanged both land here, the
        // timer folds a burst into one tick, and the tick is where GeometryChanged fires — on the
        // UI thread, as the interface promises.
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

        // The inverse composition scale: mpv sizes its swapchain in physical pixels, the panel
        // lays out in DIPs, and without this transform the picture draws scaled by the display's
        // DPI — mpv-winui's UpdateSwapChainScale is the same arithmetic. Re-applied on every
        // geometry pass, which is what makes a DPI change self-healing.
        SetMatrixTransform(swapChain, 1f / (float)_panel.CompositionScaleX, 1f / (float)_panel.CompositionScaleY);

        if (_attached == swapChain)
        {
            Log.Debug(Category, $"交换链未变，已刷新 DPI 矩阵（{_size.Width}×{_size.Height}）");
            return;
        }

        SetPanelSwapChain(swapChain);
        _attached = swapChain;
        Log.Info(Category, $"视频交换链已挂上 0x{swapChain:X}（{_size.Width}×{_size.Height}）");
    }

    /// <summary>Re-measures the panel into the cache. UI thread only.</summary>
    private void Measure()
    {
        var scale = _panel.CompositionScaleX is > 0 and var sx ? sx : 1.0;
        var scaleY = _panel.CompositionScaleY is > 0 and var sy ? sy : 1.0;

        var width = (int)Math.Floor(_panel.ActualWidth * scale);
        var height = (int)Math.Floor(_panel.ActualHeight * scaleY);
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

    // ---- the two COM calls ------------------------------------------------------

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

    private void SetMatrixTransform(IntPtr swapChain, float scaleX, float scaleY)
    {
        var chain2 = QueryInterface(swapChain, SwapChain2Iid);
        try
        {
            var matrix = new Matrix3x2 { M11 = scaleX, M22 = scaleY };
            GetMethod<SetMatrixTransformDelegate>(chain2, SetMatrixTransformSlot)(chain2, matrix);
        }
        finally
        {
            Marshal.Release(chain2);
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

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetMatrixTransformDelegate(IntPtr self, in Matrix3x2 matrix);

    /// <summary>DXGI_MATRIX_3X2_F. Only the diagonal is ever set; the rest is identity padding.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Matrix3x2
    {
        public float M11, M12;
        public float M21, M22;
        public float M31, M32;
    }
}

using System.Numerics;
using System.Runtime.InteropServices;
using EmbyNian.Diagnostics;
using EmbyNian.Playback;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using WinRT;

namespace EmbyNian.Shell.Windowing;

/// <summary>
/// Hosts mpv's composition swapchain as a SpriteVisual in the XAML compositor. The empty host
/// must precede the OSD siblings; attaching to the page root would draw above its XAML content.
/// Rendering uses physical pixels, while the visual inherits XAML's DIP layout and transforms.
/// </summary>
internal sealed class CompositionVideoTarget : IVideoSurface, IDisposable
{
    private const string Category = "视频合成层";

    // Microsoft.UI.Composition.Interop.h: IUnknown, CreateGraphicsDevice, ForHandle, ForSwapChain.
    // The Windows.UI (UWP) interface has a different IID and method order.
    private static readonly Guid SwapChainInteropIid = new("FC084699-67D8-40E1-ADE7-08901D84FFDA");
    private const int CreateSurfaceSlot = 5;

    private readonly FrameworkElement _host;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _debounce;
    private readonly object _gate = new();
    private XamlRoot? _root;
    private SpriteVisual? _visual;
    private CompositionSurfaceBrush? _brush;
    private IntPtr _attached;
    private IntPtr _pending;
    private bool _hasPending;
    private bool _queued;
    private bool _disposed;
    private (int Width, int Height) _size;

    public CompositionVideoTarget(FrameworkElement host)
    {
        _host = host;
        _dispatcher = host.DispatcherQueue;
        _debounce = _dispatcher.CreateTimer();
        _debounce.Interval = TimeSpan.FromMilliseconds(100);
        _debounce.IsRepeating = false;
        _debounce.Tick += OnDebounce;
        _host.Loaded += OnLoaded;
        _host.Unloaded += OnUnloaded;
        _host.SizeChanged += OnSizeChanged;
        Measure();
    }

    internal bool HasAttachedVisual => _attached != IntPtr.Zero
        && _brush?.Surface is not null
        && _visual is not null
        && ReferenceEquals(ElementCompositionPreview.GetElementChildVisual(_host), _visual);

    public event Action? GeometryChanged;

    public (int Width, int Height) Size
    {
        get { lock (_gate) return _size; }
    }

    public void AttachSwapChain(IntPtr swapChain)
    {
        lock (_gate)
        {
            if (_disposed) return;
            // mpv lends the pointer only for this call; own it before crossing the dispatcher.
            if (swapChain != IntPtr.Zero) Marshal.AddRef(swapChain);
            if (_pending != IntPtr.Zero) Marshal.Release(_pending);
            _pending = swapChain;
            _hasPending = true;
            if (!_dispatcher.HasThreadAccess && _queued) return;
            _queued = true;
        }

        if (_dispatcher.HasThreadAccess)
            DrainPending();
        else if (!_dispatcher.TryEnqueue(DrainPending))
        {
            lock (_gate)
            {
                if (_pending != IntPtr.Zero) Marshal.Release(_pending);
                _pending = IntPtr.Zero;
                _hasPending = _queued = false;
            }
        }
    }

    private void DrainPending()
    {
        lock (_gate)
        {
            _queued = false;
            if (_disposed || !_hasPending) return;
            var chain = _pending;
            _pending = IntPtr.Zero;
            _hasPending = false;
            try
            {
                // Keep taking the request and applying it atomic against a concurrent stop.
                Attach(chain);
            }
            catch (Exception error)
            {
                Log.Warn(Category, "挂接 Composition 视频交换链失败", error);
            }
            finally
            {
                if (chain != IntPtr.Zero) Marshal.Release(chain);
            }
        }
    }

    private void Attach(IntPtr chain)
    {
        if (chain == _attached && (chain == IntPtr.Zero || _brush is not null)) return;
        if (chain == IntPtr.Zero)
        {
            ReleaseVisual();
            ReleaseAttached();
            Log.Debug(Category, "视频交换链已摘下");
            return;
        }

        EnsureVisual();
        var surface = CreateSurface(_visual!.Compositor, chain);
        _brush!.Surface = surface;
        Marshal.AddRef(chain);
        ReleaseAttached();
        _attached = chain;
        var size = Size;
        Log.Info(Category, $"视频交换链已挂上 SpriteVisual（{size.Width}×{size.Height} px）");
    }

    private void EnsureVisual()
    {
        if (_visual is not null) return;
        var compositor = ElementCompositionPreview.GetElementVisual(_host).Compositor;
        _brush = compositor.CreateSurfaceBrush();
        // mpv already fits/letterboxes into the full output buffer. Map that buffer to the host,
        // rather than applying a second aspect-ratio policy in the compositor.
        _brush.Stretch = CompositionStretch.Fill;
        _visual = compositor.CreateSpriteVisual();
        _visual.Brush = _brush;
        _visual.RelativeSizeAdjustment = Vector2.One;
        ElementCompositionPreview.SetElementChildVisual(_host, _visual);
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        SubscribeRoot(_host.XamlRoot);
        Measure();
        RestartDebounce();
        if (_attached == IntPtr.Zero) return;
        try { Attach(_attached); }
        catch (Exception error) { Log.Warn(Category, "恢复 Composition 视频层失败", error); }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _debounce.Stop();
        SubscribeRoot(null);
        ReleaseVisual();
    }

    private void SubscribeRoot(XamlRoot? root)
    {
        if (_root == root) return;
        if (_root is not null) _root.Changed -= OnRootChanged;
        _root = root;
        if (_root is not null) _root.Changed += OnRootChanged;
    }

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (Measure()) RestartDebounce();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs args)
    {
        SubscribeRoot(_host.XamlRoot);
        if (Measure()) RestartDebounce();
    }

    private bool Measure()
    {
        var size = VideoSurfaceSize.FromDips(_host.ActualWidth, _host.ActualHeight,
            _host.XamlRoot?.RasterizationScale ?? 1);
        lock (_gate)
        {
            if (_size == size) return false;
            _size = size;
            return true;
        }
    }

    private void RestartDebounce()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnDebounce(DispatcherQueueTimer sender, object args) => GeometryChanged?.Invoke();

    private void ReleaseVisual()
    {
        if (_visual is null) return;
        ElementCompositionPreview.SetElementChildVisual(_host, null);
        _visual.Brush = null;
        _brush!.Surface = null;
        _visual.Dispose();
        _brush.Dispose();
        _visual = null;
        _brush = null;
    }

    private void ReleaseAttached()
    {
        if (_attached != IntPtr.Zero) Marshal.Release(_attached);
        _attached = IntPtr.Zero;
    }

    // The page/window owner calls this on the UI thread before disposing its XAML island.
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_pending != IntPtr.Zero) Marshal.Release(_pending);
            _pending = IntPtr.Zero;
            _hasPending = false;
            _size = default;
        }
        _host.Loaded -= OnLoaded;
        _host.Unloaded -= OnUnloaded;
        _host.SizeChanged -= OnSizeChanged;
        SubscribeRoot(null);
        _debounce.Stop();
        _debounce.Tick -= OnDebounce;
        GeometryChanged = null;
        ReleaseVisual();
        ReleaseAttached();
    }

    private static ICompositionSurface CreateSurface(Compositor compositor, IntPtr chain)
    {
        var unknown = ((IWinRTObject)compositor).NativeObject.ThisPtr;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in SwapChainInteropIid, out var interop));
        IntPtr surface = IntPtr.Zero;
        try
        {
            var vtable = Marshal.ReadIntPtr(interop);
            var method = Marshal.GetDelegateForFunctionPointer<CreateSurfaceDelegate>(
                Marshal.ReadIntPtr(vtable, CreateSurfaceSlot * IntPtr.Size));
            Marshal.ThrowExceptionForHR(method(interop, chain, out surface));
            return MarshalInterface<ICompositionSurface>.FromAbi(surface);
        }
        finally
        {
            if (surface != IntPtr.Zero) Marshal.Release(surface);
            Marshal.Release(interop);
            GC.KeepAlive(compositor);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateSurfaceDelegate(IntPtr self, IntPtr chain, out IntPtr surface);
}

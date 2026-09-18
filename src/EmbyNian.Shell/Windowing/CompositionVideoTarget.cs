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

    /// <summary>上一个落定的宿主尺寸，只用来判这一趟是「跳变」还是「拖动」—— 见 <see cref="Schedule"/>。</summary>
    private (int Width, int Height) _previous;

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

    /// <summary>
    /// The size the compositor is actually drawing the picture at, in DIPs, against the host's own
    /// <see cref="Size"/> in physical pixels. The two parting company is what 「画面缩在左上角一小块」
    /// reads as: a sprite smaller than its host draws the surface at its natural size from the host's
    /// top-left corner, and the window around it stays empty. Read on the UI thread only — the visual
    /// lives in that apartment. Zero when nothing is attached.
    /// </summary>
    internal (float Width, float Height) VisualSize
    {
        get
        {
            var visual = _visual;
            return visual is null ? default : (visual.Size.X, visual.Size.Y);
        }
    }

    /// <summary>The host element's layout size in DIPs — what <see cref="Size"/> is converted from.</summary>
    internal (double Width, double Height) HostSize => (_host.ActualWidth, _host.ActualHeight);

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
        Log.Info(Category, $"视频交换链已挂上 SpriteVisual（宿主 {size.Width}×{size.Height} px，{Describe()}）");
    }

    private void EnsureVisual()
    {
        if (_visual is not null) return;
        var compositor = ElementCompositionPreview.GetElementVisual(_host).Compositor;
        _brush = compositor.CreateSurfaceBrush();
        // mpv already fits/letterboxes into the full output buffer. Map that buffer to the host,
        // rather than applying a second aspect-ratio policy in the compositor.
        //
        // 2026-09-17 补一句实测：这一条在这个交换链上其实做不到 —— 探针里把缓冲缩到窗口的一半
        //（1280×720 放进 2560×1440），屏幕只有左上角有画面，中间和右下角是页面底色。交换链既不被
        // 画笔缩放，也不被 SpriteVisual 的尺寸缩放（显式写尺寸试过，一样），它只会一比一贴在宿主的
        // 左上角。所以画面铺满窗口靠的<b>只有</b>「交换链尺寸 = 宿主尺寸」这一件事，见 LibMpvBackend
        // 的合成尺寸派发；而尺寸落后于窗口的那几百毫秒，屏幕上就是「画面缩在左上角一小块」。
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
        if (Measure()) Schedule();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs args)
    {
        SubscribeRoot(_host.XamlRoot);
        if (Measure()) Schedule();
    }

    private bool Measure()
    {
        var size = VideoSurfaceSize.FromDips(_host.ActualWidth, _host.ActualHeight,
            _host.XamlRoot?.RasterizationScale ?? 1);
        lock (_gate)
        {
            if (_size == size) return false;
            _previous = _size;
            _size = size;
            return true;
        }
    }

    private void RestartDebounce()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>
    /// 宿主尺寸变了一次：等一拍再说，还是立刻说。
    /// <para>
    /// 拖动窗口的边时尺寸每帧都在变（十几毫秒一次），等那一百毫秒是为了不让 mpv 每帧重建一次交换链
    /// —— 那才是真正的卡。而进／退全屏、最大化／还原、换屏、DPI 变化都是一次性跳变：窗口那一跳是瞬时的，
    /// 等这一拍只是让画面多停在旧尺寸里一百毫秒，而 2026-09-17 量过这一等的代价 —— 探针里逐 50ms 取样，
    /// 屏幕上「画面缩在左上角、其余是页面底色」从 190ms 一直持续到 340ms，而 mpv 自己从收到新尺寸到
    /// 画面上生效只要 110~150ms（半尺寸实验：窗口不动、防抖不参与）。也就是说那一百毫秒是纯加的。
    /// </para>
    /// <para>
    /// 判据用面积比（涨到 1.5 倍以上、或掉到 2/3 以下）：一次性跳变远在其外（全屏进出是 4 倍／1/4），
    /// 连续拖动的相邻两帧几乎不可能越过它 —— 一帧之内把手里的窗口拉大一倍，指针得跳半屏。
    /// </para>
    /// </summary>
    private void Schedule()
    {
        if (IsJump(_previous, _size))
        {
            _debounce.Stop();
            Dispatch();
            return;
        }

        RestartDebounce();
    }

    /// <summary>一次性跳变还是连续拖动：面积比 1.5 倍为界。尺寸没量出来（0）时不跳变，按拖动等一拍。</summary>
    private static bool IsJump((int Width, int Height) was, (int Width, int Height) now)
    {
        if (was.Width <= 0 || was.Height <= 0 || now.Width <= 0 || now.Height <= 0) return false;

        var before = (double)was.Width * was.Height;
        var after = (double)now.Width * now.Height;
        return after >= before * 1.5 || after * 1.5 <= before;
    }

    /// <summary>防抖到点：一次连续变化（拖动）停了，派发。</summary>
    private void OnDebounce(DispatcherQueueTimer sender, object args) => Dispatch();

    /// <summary>把此刻的尺寸交给后端（它转手写进 mpv 的 <c>d3d11-composition-size</c>），并记一行读数。</summary>
    private void Dispatch()
    {
        Log.Debug(Category, $"几何落定：{Describe()}，派发合成尺寸");
        GeometryChanged?.Invoke();
    }

    /// <summary>
    /// 这一层此刻信的是什么尺寸，写成一行。四个数是四件事：宿主元素自己量到的（DIP）、XAML 给这个元素的
    /// 视觉树的尺寸、SpriteVisual 自己的尺寸与它相对父级的比例、画笔怎么把交换链贴上去。2026-09-17
    /// 「全屏时画面缩在左上角」量下来是「宿主已经长大、画面上却只有一枚旧尺寸的块」—— 那只可能是这四者
    /// 里有一个还停在旧值，而它们各自都不会说话，所以这一行把它们摆在一起（探针报告里能逐拍对读）。
    /// </summary>
    private string Describe()
    {
        var hostDip = $"{_host.ActualWidth:0}×{_host.ActualHeight:0}";
        var element = ElementCompositionPreview.GetElementVisual(_host).Size;
        var visual = _visual;
        var sprite = visual is null
            ? "未挂"
            : $"{visual.Size.X:0}×{visual.Size.Y:0}（相对 {visual.RelativeSizeAdjustment.X:0.##}）";
        var stretch = _brush is null ? "无画笔" : _brush.Stretch.ToString();
        return $"宿主 {hostDip} DIP，元素视觉 {element.X:0}×{element.Y:0}，Sprite {sprite}，画笔 {stretch}，"
            + $"宿主缓存 {_size.Width}×{_size.Height} px";
    }

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

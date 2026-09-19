using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using EmbyNian.Diagnostics;
using EmbyNian.Playback;
using EmbyNian.Shell.Interop;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using WinRT;

namespace EmbyNian.Shell.Windowing;

/// <summary>
/// mpv 的交换链留在独立 SpriteVisual 上，画笔把缓冲拉伸进 Visual 的尺寸，所以「按哪个矩形呈现」
/// 全部由 Offset/Scale 表达。客户区跳变时旧缓冲按旧客户区矩形起手、240ms 长到新矩形（mpv 的
/// 新缓冲中途落地就按当前进度重锚），等待期用 16ms 单计时器兼管：重读实际缓冲（QI
/// IDXGISwapChain1::GetDesc1，ResizeBuffers 可以原地复用同一个 COM 指针，不能只等换链事件）、
/// 推进跑动、缓冲与宿主一致就停表。
/// </summary>
internal sealed class CompositionVideoTarget : IVideoSurface, IDisposable
{
    private const string Category = "视频合成层";
    private static readonly Guid SwapChainInteropIid = new("FC084699-67D8-40E1-ADE7-08901D84FFDA");
    private static readonly Guid SwapChain1Iid = new("790A45F7-0D42-4876-983A-0A55CFE6F4AA");
    private const int CreateSurfaceSlot = 5;
    private const int GetDescriptionSlot = 18;

    /// <summary>呈现跑动一趟的时长。与 mpv 重建缓冲的实测窗口（110~150ms）错开半程。</summary>
    private const double MorphMilliseconds = 240;

    private const int TickMilliseconds = 16;
    private const int WatchCeilingMilliseconds = 1500;

    private readonly FrameworkElement _host;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _debounce;
    private readonly DispatcherQueueTimer _presentationTimer;
    private readonly Stopwatch _presentationClock = Stopwatch.StartNew();
    private readonly object _gate = new();
    private XamlRoot? _root;
    private SpriteVisual? _visual;
    private CompositionSurfaceBrush? _brush;
    private IntPtr _attached;
    private IntPtr _pending;
    private bool _hasPending;
    private bool _queued;
    private bool _disposed;
    private bool _retainLastFrame;
    private bool _retained;
    private long _watchStarted;
    private (int Width, int Height) _size;
    private (int Width, int Height) _previous;
    private (int Width, int Height) _attachedContent;
    private PresentationRun? _run;

    /// <summary>一趟呈现跑动：旧矩形摆放 → 新矩形摆放，按同一支缓动插值。</summary>
    private sealed class PresentationRun
    {
        public VideoPresentation.Placement From;
        public VideoPresentation.Placement To;
        public double StartedAtMs;
    }

    public CompositionVideoTarget(FrameworkElement host)
    {
        _host = host;
        _dispatcher = host.DispatcherQueue;
        _debounce = _dispatcher.CreateTimer();
        _debounce.Interval = TimeSpan.FromMilliseconds(100);
        _debounce.IsRepeating = false;
        _debounce.Tick += OnDebounce;
        _presentationTimer = _dispatcher.CreateTimer();
        _presentationTimer.Interval = TimeSpan.FromMilliseconds(TickMilliseconds);
        _presentationTimer.Tick += OnPresentationTick;
        _host.Loaded += OnLoaded;
        _host.Unloaded += OnUnloaded;
        _host.SizeChanged += OnSizeChanged;
        Measure();
    }

    internal bool HasAttachedVisual => !_retained && _attached != IntPtr.Zero
        && _brush?.Surface is not null && _visual is not null
        && ReferenceEquals(ElementCompositionPreview.GetElementChildVisual(_host), _visual);

    internal (float Width, float Height) VisualSize => _visual is { } visual
        ? (visual.Size.X, visual.Size.Y) : default;

    internal (int Width, int Height) AttachedContentSize => _attachedContent;
    internal (double Width, double Height) HostSize => (_host.ActualWidth, _host.ActualHeight);
    internal bool IsContentReady => HasAttachedVisual && VideoPresentation.Matches(
        _attachedContent.Width, _attachedContent.Height, _size.Width, _size.Height);

    /// <summary>退场的最后一帧可以比 mpv 会话多活一小段；收起页面时必须放掉引用。</summary>
    internal bool RetainLastFrame
    {
        get => _retainLastFrame;
        set
        {
            _retainLastFrame = value;
            if (!value && _retained) ClearSurface();
        }
    }

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
            // mpv 只在这个调用内借出指针，跨 dispatcher 之前先拥有它。
            if (swapChain != IntPtr.Zero) Marshal.AddRef(swapChain);
            if (_pending != IntPtr.Zero) Marshal.Release(_pending);
            _pending = swapChain;
            _hasPending = true;
            if (!_dispatcher.HasThreadAccess && _queued) return;
            _queued = true;
        }

        if (_dispatcher.HasThreadAccess) DrainPending();
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
            try { Attach(chain); }
            catch (Exception error) { Log.Warn(Category, "挂接 Composition 视频交换链失败", error); }
            finally
            {
                if (chain != IntPtr.Zero) Marshal.Release(chain);
            }
        }
    }

    private void Attach(IntPtr chain)
    {
        if (chain == IntPtr.Zero)
        {
            _presentationTimer.Stop();
            if (_retainLastFrame && _visual is not null && _attached != IntPtr.Zero)
            {
                _retained = true;
                return;
            }
            ClearSurface();
            return;
        }

        _retained = false;
        if (chain != _attached || _visual is null)
        {
            EnsureVisual();
            var surface = CreateSurface(_visual!.Compositor, chain);
            _brush!.Surface = surface;
            Marshal.AddRef(chain);
            ReleaseAttached();
            _attached = chain;
            Log.Debug(Category, "视频交换链已挂上 SpriteVisual");
        }
        RefreshPresentation();
        WatchPresentation();
    }

    private void EnsureVisual()
    {
        if (_visual is not null) return;
        var compositor = CompositionTarget.GetCompositorForCurrentThread();
        _brush = compositor.CreateSurfaceBrush();
        _brush.Stretch = CompositionStretch.Fill;
        _visual = compositor.CreateSpriteVisual();
        _visual.Brush = _brush;
        ElementCompositionPreview.SetElementChildVisual(_host, _visual);
    }

    /// <summary>
    /// 起一趟呈现跑动：旧缓冲按跳变前的客户区矩形呈现，240ms 长到新矩形。半路再触发从当前呈现
    /// 矩形接着走；缓冲不存在（还没开播、收摊之后）就没有可跑的画面，这一跳交给加载遮罩或页面本身。
    /// </summary>
    internal void BeginPresentationMorph(NativeRect fromScreen, NativeRect toScreen)
    {
        if (_disposed || _attached == IntPtr.Zero || _attachedContent.Width <= 0) return;
        if (fromScreen.Width <= 0 || fromScreen.Height <= 0 || toScreen.Width <= 0 || toScreen.Height <= 0) return;

        var raster = RasterScale();
        var now = _presentationClock.Elapsed.TotalMilliseconds;

        var from = VideoPresentation.ForRect(_attachedContent.Width, _attachedContent.Height,
            fromScreen.Left, fromScreen.Top, fromScreen.Width, fromScreen.Height,
            toScreen.Left, toScreen.Top, raster);
        var to = VideoPresentation.ForRect(_attachedContent.Width, _attachedContent.Height,
            toScreen.Left, toScreen.Top, toScreen.Width, toScreen.Height,
            toScreen.Left, toScreen.Top, raster);

        if (_run is { } active)
        {
            var e = Ease(Math.Clamp((now - active.StartedAtMs) / MorphMilliseconds, 0, 1));
            from = VideoPresentation.Between(active.From, active.To, e);
        }

        _run = new PresentationRun { From = from, To = to, StartedAtMs = now };
        RefreshPresentation();
        WatchPresentation();
        Log.Debug(Category, $"画面跑动：{(int)fromScreen.Width}×{(int)fromScreen.Height} → "
            + $"{(int)toScreen.Width}×{(int)toScreen.Height}（{MorphMilliseconds:0}ms）");
    }

    internal void RefreshPresentation()
    {
        if (_disposed || _visual is null || _attached == IntPtr.Zero) return;

        var raster = RasterScale();
        var observed = _retained ? default : ReadContentSize(_attached);
        if (observed is { Width: > 0, Height: > 0 } && observed != _attachedContent)
        {
            ReanchorRun(raster, observed);
            _attachedContent = observed;
            Log.Debug(Category, $"实际视频缓冲 {observed.Width}x{observed.Height}，宿主 {_size.Width}x{_size.Height}");
        }
        if (_attachedContent is not { Width: > 0, Height: > 0 }) return;

        VideoPresentation.Placement placement;
        if (_run is { } run)
        {
            var progress = (_presentationClock.Elapsed.TotalMilliseconds - run.StartedAtMs) / MorphMilliseconds;
            if (progress >= 1)
            {
                _run = null;
                placement = run.To;
            }
            else placement = VideoPresentation.Between(run.From, run.To, Ease(progress));
        }
        else if (IsContentReady) placement = VideoPresentation.Placement.Identity;
        else placement = VideoPresentation.Contain(_attachedContent.Width, _attachedContent.Height,
            _host.ActualWidth, _host.ActualHeight, raster);

        ApplyPlacement(_attachedContent, placement, raster);
    }

    /// <summary>
    /// 跑动途中缓冲换新（mpv 按新尺寸重建）：呈现矩形这条路径与缓冲无关，把「当前矩形」按新缓冲
    /// 重新表达成起点、终点改回单位摆放，跑动接着走——眼睛看到的只有变清晰，不是二次跳变。
    /// </summary>
    private void ReanchorRun(double raster, (int Width, int Height) observed)
    {
        if (_run is not { } run) return;
        var e = Ease(Math.Clamp((_presentationClock.Elapsed.TotalMilliseconds - run.StartedAtMs) / MorphMilliseconds, 0, 1));
        var current = VideoPresentation.Between(run.From, run.To, e);
        var rectWidth = _attachedContent.Width / raster * current.ScaleX;
        var rectHeight = _attachedContent.Height / raster * current.ScaleY;
        _run = new PresentationRun
        {
            From = new VideoPresentation.Placement(
                rectWidth / (observed.Width / raster), rectHeight / (observed.Height / raster),
                current.Left, current.Top),
            To = VideoPresentation.Placement.Identity,
            StartedAtMs = _presentationClock.Elapsed.TotalMilliseconds,
        };
    }

    private void ApplyPlacement((int Width, int Height) content, VideoPresentation.Placement placement, double raster)
    {
        var visual = _visual;
        if (visual is null) return;
        visual.Size = new Vector2((float)(content.Width / raster), (float)(content.Height / raster));
        visual.Offset = new Vector3((float)placement.Left, (float)placement.Top, 0);
        visual.Scale = new Vector3((float)placement.ScaleX, (float)placement.ScaleY, 1);
    }

    /// <summary>离散的窗口状态变化立即派发，不把拖动防抖的 100ms 加到最大化与全屏上。</summary>
    internal void SynchronizeGeometry()
    {
        if (_disposed) return;
        Measure();
        _debounce.Stop();
        RefreshPresentation();
        Dispatch();
        WatchPresentation();
    }

    private void WatchPresentation()
    {
        if (_disposed || _retained || _attached == IntPtr.Zero) return;
        _watchStarted = _presentationClock.ElapsedMilliseconds;
        _presentationTimer.Start();
    }

    private void OnPresentationTick(DispatcherQueueTimer sender, object args)
    {
        RefreshPresentation();
        if (_disposed || _retained || _attached == IntPtr.Zero
            || (_run is null && IsContentReady)
            || _presentationClock.ElapsedMilliseconds - _watchStarted > WatchCeilingMilliseconds)
            _presentationTimer.Stop();
    }

    private double RasterScale()
    {
        var raster = _host.XamlRoot?.RasterizationScale ?? 1;
        return raster > 0 ? raster : 1;
    }

    private static double Ease(double progress) => 1 - Math.Pow(1 - Math.Clamp(progress, 0, 1), 3);

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
        _presentationTimer.Stop();
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

    private void Schedule()
    {
        RefreshPresentation();
        WatchPresentation();
        var before = (double)_previous.Width * _previous.Height;
        var after = (double)_size.Width * _size.Height;
        if (before > 0 && after > 0 && (after >= before * 1.5 || after * 1.5 <= before))
        {
            _debounce.Stop();
            Dispatch();
        }
        else RestartDebounce();
    }

    private void OnDebounce(DispatcherQueueTimer sender, object args) => Dispatch();
    private void Dispatch() => GeometryChanged?.Invoke();

    private void ClearSurface()
    {
        ReleaseVisual();
        ReleaseAttached();
        _attachedContent = default;
        _retained = false;
        _run = null;
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
        _presentationTimer.Stop();
        _presentationTimer.Tick -= OnPresentationTick;
        GeometryChanged = null;
        ClearSurface();
    }

    private static (int Width, int Height) ReadContentSize(IntPtr chain)
    {
        if (Marshal.QueryInterface(chain, in SwapChain1Iid, out var swapChain1) < 0) return default;
        try
        {
            var method = Marshal.GetDelegateForFunctionPointer<GetDescriptionDelegate>(
                Marshal.ReadIntPtr(Marshal.ReadIntPtr(swapChain1), GetDescriptionSlot * IntPtr.Size));
            return method(swapChain1, out var description) >= 0
                && description.Width <= int.MaxValue && description.Height <= int.MaxValue
                ? ((int)description.Width, (int)description.Height) : default;
        }
        finally { Marshal.Release(swapChain1); }
    }

    private static ICompositionSurface CreateSurface(Compositor compositor, IntPtr chain)
    {
        var unknown = ((IWinRTObject)compositor).NativeObject.ThisPtr;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in SwapChainInteropIid, out var interop));
        IntPtr surface = IntPtr.Zero;
        try
        {
            var method = Marshal.GetDelegateForFunctionPointer<CreateSurfaceDelegate>(
                Marshal.ReadIntPtr(Marshal.ReadIntPtr(interop), CreateSurfaceSlot * IntPtr.Size));
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

    [StructLayout(LayoutKind.Sequential)]
    private struct SwapChainDescription
    {
        public uint Width, Height, Format, Stereo, SampleCount, SampleQuality;
        public uint BufferUsage, BufferCount, Scaling, SwapEffect, AlphaMode, Flags;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDescriptionDelegate(IntPtr self, out SwapChainDescription description);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateSurfaceDelegate(IntPtr self, IntPtr chain, out IntPtr surface);
}

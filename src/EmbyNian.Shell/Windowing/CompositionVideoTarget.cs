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
/// mpv 的交换链留在独立 SpriteVisual 上。呈现矩形按物理像素记，与缓冲尺寸、DPI 和窗口原点分开。
/// 拖边期间保持渲染缓冲不变，靠合成变换把画面缩放着跟住窗口；mpv 在这一段被页面冻住
/// （见 <see cref="ResizeFreeze"/>），松手后才交给它最终尺寸、再按设定放开。
/// 交换链画笔的 Stretch 不负责缩放，Size 是裁剪框，因此必须按实测缓冲尺寸设置 Scale。
/// 客户区跳变瞬时落位，不插值播放画面的几何。
/// </summary>
internal sealed class CompositionVideoTarget : IVideoSurface, IDisposable
{
    private const string Category = "视频合成层";
    private static readonly Guid SwapChainInteropIid = new("FC084699-67D8-40E1-ADE7-08901D84FFDA");
    private static readonly Guid SwapChain1Iid = new("790A45F7-0D42-4876-983A-0A55CFE6F4AA");
    private const int CreateSurfaceSlot = 5;
    private const int GetDescriptionSlot = 18;

    /// <summary>
    /// <c>IDXGISwapChain::GetLastPresentCount</c>（IDXGISwapChain1 继承来的那一格）：这条链被 Present 过
    /// 几次。「挂上链」与「链里有画面」是两件事，这一支是分开它们的唯一办法 —— 见 <see cref="PictureReveal"/>。
    /// </summary>
    private const int GetPresentCountSlot = 17;

    private const int TickMilliseconds = 16;
    private const int WatchCeilingMilliseconds = 1500;

    /// <summary>
    /// 客户区跳变之后，「窗口自己报的矩形」当作宿主事实的时长。见 <see cref="_snappedClient"/>。
    /// 取值与 mpv 重建缓冲的实测窗口（110~150ms）同量级再留一倍余量。
    /// </summary>
    private const int SnapTrustMilliseconds = 240;

    private readonly FrameworkElement _host;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _geometryTimer;
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
    private (int Width, int Height) _interactiveBuffer;

    /// <summary>留在屏上那一帧的压暗系数。见 <see cref="RetainDim"/>。</summary>
    private double _retainDim;
    private bool _retained;
    private long _watchStarted;
    private (int Width, int Height) _size;
    private (int Width, int Height) _previous;
    private (int Width, int Height) _attachedContent;

    /// <summary>最后一次实际读取的缓冲尺寸；留帧时仍需它来计算缩放，不能用请求尺寸或固定延时猜。</summary>
    private (int Width, int Height) _pixels;

    /// <summary>最近一次算出的呈现矩形（物理像素，以宿主客户区左上角为原点）。</summary>
    private VideoPresentation.Rect _clientRect;

    /// <summary>
    /// 跳变那一拍窗口报出来的新客户区（物理像素）与它到来的时刻；非零期间，逐拍那条路不读岛的尺寸。
    /// <para>
    /// <b>为什么岛的尺寸不能当场信（2026-09-20 探针实测）。</b>进全屏那一刻（<c>SetFullscreen</c> 刚
    /// 返回）窗口客户区已经是 1080×1872，而岛报出来的是 870×648 —— 正好是旧客户区（1064×792）除以
    /// 1.2225 之后的数，也就是 <c>HostWindow.PublishClientRect</c> 那次强制布局按 <c>WindowDpi</c>
    /// 折算出来的尺寸，而岛自己的栅格化比例是 1.0。它会在下一次真实的尺寸通知里纠正回来，但**中间这几
    /// 拍**若拿岛当基准，画面就会被按那个错尺寸摆一下 —— 观感正是「切过去闪一下」。所以这一段里一律用
    /// 窗口自己报的矩形；<see cref="SnapTrustMilliseconds"/> 是保险丝，免得哪一天它再也不纠正。
    /// </para>
    /// </summary>
    private (int Width, int Height) _snappedClient;
    private long _snappedAt;

    /// <summary>
    /// 拖动中：宿主报了一个新尺寸，mpv 还没把缓冲换过来。这一段里<b>画面照旧铺满宿主</b>，绝不
    /// 退回 contain —— 见 <see cref="FrameBox"/> 与 2026-09-20 记下的「拖动窗口边缘闪烁」。
    /// <para>
    /// <b>只服务拖动这一条路。</b>拖动时窗口被按画面比例锁着，窗口形状就是画面形状，铺满与按比例摆
    /// 本来是一回事；离散的客户区跳变（进出全屏、最大化／还原）里两者差得远，铺满＝把上一帧拉变形，
    /// 而跳变那一拍手上的缓冲往往正是被换掉的中间尺寸（探针实测见 <see cref="_snappedClient"/>）。
    /// 所以 <see cref="SnapPresentation"/> 会把它清掉，并且 <see cref="CurrentFrame"/> 在跳变后的
    /// 「信窗口」那一段里根本不看它。
    /// </para>
    /// </summary>
    private bool _resizePending;

    /// <summary>
    /// 摆放三件套被 <c>--probe-composition</c> 的「缩放定律」一节钉住了：逐拍与保留态两条路都必须让开，
    /// 否则它们会在 16ms 之内把探针写进去的值盖掉，量到的是生产代码那一版而不是实验那一版。
    /// </summary>
    private bool _probePinned;

    /// <summary>
    /// 保留态（最后一帧还在屏上、缓冲已交还）里那一帧的<b>形状</b>：宽高比，物理像素。
    /// <para>
    /// 这里刻意不记整块矩形，只记形状。退场时窗口还的是<b>浏览几何</b>，和播放时被
    /// <c>FitToPicture</c> 掰成的画面形状往往不是一个比例（播放是 16:9 的片子、浏览是用户上次拖的
    /// 窗口）。若把「播放时那一整窗矩形」当成画面本身去 contain，就会拿<b>窗口</b>的长宽比当
    /// <b>画面</b>的长宽比，退出那一刻画面被拉成浏览窗口的形状 —— 用户看到的是「画面在退出时
    /// 猛地变形」（2026-09-20 自查）。
    /// </para>
    /// <para>
    /// 来源优先级：<see cref="PictureAspect"/>（宿主写进来的真实视频比例，最准，全屏下也是对的）
    /// → 缓冲尺寸（mpv 的 composition 缓冲尺寸恒等于宿主，全屏时里面含黑边，比例会偏）→ 最近一次
    /// 算出的呈现矩形。
    /// </para>
    /// </summary>
    private (double Width, double Height) _retainedShape;

    /// <summary>
    /// 视频自己的宽高比（宽 ÷ 高），由宿主在每次比例变化时写进来。
    /// <para>
    /// 退出播放时保留的那一帧必须按<b>视频的</b>比例摆放，而缓冲尺寸在窗口化时恰好等于宿主
    /// （<c>FitToPicture</c> 把客户区整成画面比例），全屏时却含黑边 —— 两个都不如这个数直接。
    /// 零表示还不知道，那时退回缓冲尺寸、再退回呈现矩形。
    /// </para>
    /// </summary>
    internal double PictureAspect { get; set; }

    /// <summary>
    /// 片子<b>码流自己的</b>宽高比（宽 ÷ 高），由宿主在换片时写进来，与窗口无关。
    /// <para>
    /// 这是退场保留那一帧唯一一个「不会被窗口污染」的形状来源。另外三个都不行，2026-09-20 逐个
    /// 验过：<see cref="_clientRect"/> 在窗口与画面不同形时整幅等于宿主；mpv 的 composition 缓冲
    /// 尺寸恒等于宿主；<see cref="PictureAspect"/> 来自 mpv 的 <c>dwidth/dheight</c>，那是<b>显示</b>
    /// 尺寸，窗口里被 letterbox/panscan 之后就与宿主同形（竖屏档实测拿到 0.577 而片子是 16:9）。
    /// 只有码流尺寸是「画面本来长什么样」。
    /// </para>
    /// <para>零表示还不知道，那时退回 <see cref="PictureAspect"/>、再退回呈现矩形。</para>
    /// </summary>
    internal double SourceAspect { get; set; }

    /// <summary>保留态的保险丝起点；到点还没人来收就自己收，免得一台停摆的计时器一直跑着。</summary>
    private long _retainedWatchStarted;

    /// <summary>
    /// mpv 说过这一场播放真的开始了没有（它的 <c>playback-restart</c>，见 <see cref="PictureReveal"/>）。
    /// 由播放页在状态那一拍转交进来；遮罩亮起时从头清过一遍。
    /// </summary>
    private volatile bool _pictureStarted;

    /// <summary>
    /// 遮罩亮起之后，在这条交换链上数到的 Present 次数（<c>GetLastPresentCount</c> 的增量）。
    /// 与 <see cref="_pictureStarted"/> 一起构成 <see cref="HasPicture"/>，理由见
    /// <see cref="PictureReveal"/>。
    /// </summary>
    private int _presentsSinceArm;

    /// <summary>挂链那一刻这条链的 Present 计数，用来数增量。</summary>
    private int _presentsAtAttach = -1;

    /// <summary>保留态最多活多久。退场是 240ms 的轨道，这里给足余量再自己收。</summary>
    private const int RetainedCeilingMilliseconds = 1200;

    /// <summary>
    /// 此刻这一帧占的矩形（物理像素，以宿主客户区左上角为原点）。给探针读的读数：它不受缓冲
    /// 尺寸、DPI、窗口原点影响，所以「换缓冲时画面有没有跳」这类问题可以直接比对它。
    /// </summary>
    internal VideoPresentation.Rect PresentedRect => _clientRect;

    /// <summary>保留态正在跑（最后一帧还在屏上、缓冲已交还）。</summary>
    internal bool IsRetained => _retained;

    /// <summary>
    /// 保留态里那一帧的形状来源（见 <see cref="ClearAttachedForRetain"/>）。给探针读：退场期间它必须与
    /// 画面实际摆成的比例一致，否则最后一帧就是被按一个不属于画面的形状缩成窄带了。
    /// </summary>
    internal (double Width, double Height) RetainedShapeProbe => _retainedShape;

    /// <summary>宿主要了新尺寸、mpv 的缓冲还在追赶；这一段画面继续铺满，不回退 contain。</summary>
    internal bool IsResizePending => _resizePending;

    internal bool IsInteractiveResize => _interactiveBuffer is { Width: > 0, Height: > 0 };

    internal (int Width, int Height) PixelSize => _pixels;

    /// <summary>
    /// 从本地 Visual 属性反算的目标矩形，不代表合成器已经显示的像素。
    /// 实际空白、父级裁剪和交换链交接仍须用屏幕逐帧捕获验证。
    /// </summary>
    internal VideoPresentation.Rect PlacedRect
    {
        get
        {
            if (_visual is not { } visual) return default;
            var raster = RasterScale();
            var scale = visual.Scale.X;
            var left = visual.Offset.X * raster;
            var top = visual.Offset.Y * raster;
            var width = visual.Size.X * scale * raster;
            var height = visual.Size.Y * scale * raster;

            // 这一份读数的语义是「**画面**占的矩形」，不是「那 SpriteVisual 占的矩形」：视觉的 Size 是
            // 裁剪框、框的是**整块缓冲**（mpv 会在里面按画面比例留一圈信箱边），所以要把画面在缓冲里
            // 那一块的位置与大小折进来。不折的话，竖屏全屏那种「缓冲与画面不同形」的台位上，读数会把
            // 整块缓冲（含黑边）当成画面上报，与探针判「比例有没有被掰弯」的语义对不上（2026-09-22）。
            var picture = OutputAspect;
            if (picture > 0 && width > 0 && height > 0)
            {
                // 画面在「内容」里的位置：按画面自己的比例居中放进去（与 RefreshRetained 同一套算术）。
                var contentWidth = visual.Size.X * raster;
                var contentHeight = visual.Size.Y * raster;
                var unit = Math.Min(contentWidth / picture, contentHeight);
                if (double.IsFinite(unit) && unit > 0)
                {
                    var innerWidth = picture * unit;
                    var innerHeight = unit;
                    left += (contentWidth - innerWidth) / 2 * scale;
                    top += (contentHeight - innerHeight) / 2 * scale;
                    width = innerWidth * scale;
                    height = innerHeight * scale;
                }
            }

            return new VideoPresentation.Rect(left, top, width, height);
        }
    }

    /// <summary>
    /// 画面自己的宽高比（宽 ÷ 高），零＝还不知道。给 <see cref="PlacedRect"/> 折信箱边用：保留态用
    /// <see cref="_retainedShape"/>（退场那一刻定下来的画面形状），其余时候用宿主写进来的
    /// <see cref="SourceAspect"/>，最后才退回 <see cref="PictureAspect"/>（那是显示尺寸，窗口里被
    /// letterbox 之后会与宿主同形，只有前两个都没有时才用得上）。
    /// </summary>
    private double OutputAspect
    {
        get
        {
            if (_retained && _retainedShape.Width > 0 && _retainedShape.Height > 0)
                return _retainedShape.Width / _retainedShape.Height;
            return SourceAspect > 0 ? SourceAspect : PictureAspect;
        }
    }

    public CompositionVideoTarget(FrameworkElement host)
    {
        _host = host;
        _dispatcher = host.DispatcherQueue;
        _geometryTimer = _dispatcher.CreateTimer();
        _geometryTimer.Interval = TimeSpan.FromMilliseconds(TickMilliseconds);
        _geometryTimer.IsRepeating = false;
        _geometryTimer.Tick += OnGeometryTick;
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

    /// <summary>宿主的光栅化比例；探针把物理像素读数换算回 DIP 布局尺寸时要用同一个分母。</summary>
    internal double RasterizationScale => RasterScale();
    internal bool IsContentReady => HasAttachedVisual && VideoPresentation.Matches(
        _attachedContent.Width, _attachedContent.Height, _size.Width, _size.Height);

    /// <summary>
    /// 屏幕底下这块面上<b>已经有真画面了</b>没有 —— 加载遮罩（背景图）揭不揭就看它。
    /// 两个前提缺一不可，判据本身在 <see cref="PictureReveal.Ready"/>，理由与实测都在那里。
    /// </summary>
    internal bool HasPicture => PictureReveal.Ready(
        _pictureStarted, _attached != IntPtr.Zero, Volatile.Read(ref _presentsSinceArm));

    /// <summary>
    /// 这一场已经宣布开始、链也挂上了，但还没在这条链上数到一帧 —— 逐拍那条路要在这段里继续跑，
    /// 见 <see cref="NotePlaybackStarted"/> 与 <see cref="OnPresentationTick"/>。
    /// </summary>
    private bool AwaitingPicture =>
        _pictureStarted && _attached != IntPtr.Zero && Volatile.Read(ref _presentsSinceArm) == 0;

    /// <summary>给探针读的：遮罩亮起之后在这条链上数到了几帧。</summary>
    internal int PresentsSinceArm => Volatile.Read(ref _presentsSinceArm);

    /// <summary>
    /// 遮罩亮起（＝又要从头等一次首帧）时调用：把「有没有画面」这两个前提清零，重新数。
    /// <para>
    /// <b>为什么要在这一拍清，而不是等挂链/换会话时清。</b>「等首帧」这件事的起点就是遮罩亮起 —— 它由
    /// <c>ShowCover</c> 在起播、换集、换版本这三条路的<b>最前面</b>点亮，一定早于 mpv 开文件，所以清零
    /// 之后等到的必然是这一场的通知。反过来，靠「挂链时清」会漏掉会话交界：旧会话交还交换链那一下可以被
    /// 合并掉（<see cref="AttachSwapChain"/> 只保留最后一条待办），而新会话的链未必是新地址，两种漏法都会
    /// 让上一场的 <c>playback-restart</c> 直接当成这一场的答案 —— 于是遮罩又提前揭了，正是要修的那条。
    /// </para>
    /// </summary>
    internal void BeginPictureWait()
    {
        _pictureStarted = false;
        Interlocked.Exchange(ref _presentsSinceArm, 0);
    }

    /// <summary>
    /// mpv 说这一场播放真的开始了（它的 <c>playback-restart</c>）。播放页在状态那一拍转交进来 ——
    /// 它同时也是「独占那一档 mpv 自己的窗口带着画面立起来了」的答案。
    /// <para>
    /// <b>顺带把逐拍那条叫起来。</b>呈现计时器在这之前多半已经停了：挂链那一刻 <c>IsContentReady</c> 就为真，
    /// 它当场收工，而「这条链被押过帧没有」只有 <see cref="RefreshPresentation"/> 读得到 —— 少了这一句，
    /// 计数就永远停在 0，遮罩要一直等到那张 6 秒的网（2026-09-21 探针实测：`屏幕底下有真画面` 超时）。
    /// </para>
    /// </summary>
    internal void NotePlaybackStarted()
    {
        _pictureStarted = true;
        if (_attached != IntPtr.Zero) WatchPresentation();
    }

    /// <summary>退场的最后一帧可以比 mpv 会话多活一小段；收起页面时必须放掉引用。</summary>
    internal bool RetainLastFrame
    {
        get => _retainLastFrame;
        set
        {
            _retainLastFrame = value;
            if (!value && _retained)
            {
                // 收摊。保留态那条逐拍也到此为止 —— 页面已经收起，再摆也没有人看得见。
                _presentationTimer.Stop();
                ClearSurface();
            }
        }
    }

    /// <summary>
    /// 留在屏上那一帧的压暗系数，0 = 原样、1 = 黑。退出播放的 220ms 里由播放页拧到
    /// <see cref="PlayerPage"/> 那支常数，落定拍再拧回 0。
    /// <para>
    /// <b>为什么是压暗而不是叠一层遮罩。</b>退场时这一页是<b>整页淡出</b>（<c>PlayerPage.Transition</c> 的
    /// 16ms pose 计时器赋 <c>Opacity</c>），而画面是挂在宿主上的 SpriteVisual，不吃这一页的 Opacity ——
    /// 整页淡到零的那一拍，最后一帧仍以全亮度留在屏上，接着随页面一起 <c>Collapsed</c> 消失。于是「浏览页
    /// 换上来」在观感上不是一次溶解，而是一记跳变。能压暗这一帧的只有它自己的 <c>Opacity</c>（合成器的
    /// <c>Visual.Opacity</c> 是独立通道，与页面那层无关），所以旋钮长在这个类里，与 <c>Opacity</c> 一起被
    /// <see cref="RefreshRetained"/> 逐拍写进视觉。
    /// </para>
    /// <para>
    /// 与 <see cref="RetainLastFrame"/> 分开一个开关是刻意的：换集那一趟会把留帧关掉再打开
    /// （<c>ShowCoverPlate</c>），在那里顺手复位会把「换集」当成「退场」处理，而换集并不退场。
    /// </para>
    /// </summary>
    internal double RetainDim
    {
        get => _retainDim;
        set
        {
            var wanted = Math.Clamp(value, 0, 1);
            if (Math.Abs(_retainDim - wanted) < 0.001) return;

            _retainDim = wanted;

            // 分两种情形。保留态：下一拍 RefreshRetained 会写进去，这里不抢它的活，但要把那条逐拍保证
            // 起来（旋钮可能在两拍之间被拧动，而定时器此刻可能正为别的理由停着）。非保留态（换集、
            // 或者早于 Attach(0) 的那几毫秒）：直接写视觉，免得白等一拍。
            if (_retained) WatchRetained();
            else if (_visual is { } visual) visual.Opacity = (float)(1 - _retainDim);
        }
    }

    /// <summary>
    /// 保留态那一帧是<b>铺满</b>新宿主还是按比例留边。2026-09-20「重新设计退出播放动画」加的旋钮，
    /// 退场期间为真。
    /// <para>
    /// 为什么非要在退场期间铺满：那一刻窗口正从播放几何跳成浏览几何，而这一帧已经交还了缓冲、只剩
    /// 形状。按 contain 摆，它会在窗口变形的同一帧里缩成一条信匣（四周一圈近黑），正是用户说的
    /// 「退出播放页面的画面怎么这么丑」；铺满则是「同一幅满屏的画」，接着整幅溶解进浏览页。
    /// </para>
    /// <para>
    /// 与 <see cref="RetainDim"/> 是同一对（<c>BeginPlayerExit</c> 开、<c>EndPlayerExit</c> 收），但它
    /// 改的是<b>摆放几何</b>，所以换集那一趟（<c>ShowCoverPlate</c> 开关 <see cref="RetainLastFrame"/>）
    /// 不碰它 —— 留帧的默认摆法依旧是留边，那里没有「窗口正在变形」这事。
    /// </para>
    /// </summary>
    internal bool RetainFill { get; set; }

    public event Action? GeometryChanged;

    private Func<Task<VideoFrame?>>? _captureFrame;
    private bool _geometryHeld;

    public void SetFrameCapture(Func<Task<VideoFrame?>>? capture)
    {
        lock (_gate) _captureFrame = capture;
    }

    internal Task<VideoFrame?> CaptureFrameAsync()
    {
        Func<Task<VideoFrame?>>? capture;
        lock (_gate) capture = _disposed ? null : _captureFrame;
        return capture?.Invoke() ?? Task.FromResult<VideoFrame?>(null);
    }

    internal void HoldGeometry(bool hold)
    {
        _geometryHeld = hold;
        if (hold) _geometryTimer.Stop();
        else SynchronizeGeometry();
    }

    internal Task CommitPresentationAsync() => _visual is { } visual
        ? visual.Compositor.RequestCommitAsync().AsTask() : Task.CompletedTask;

    /// <summary>
    /// 拖边只改呈现尺寸，缓冲尺寸在这一段保持稳定（画面靠合成变换缩放着跟住窗口）。Size 也必须保持稳定，
    /// 因为后端除了响应 GeometryChanged，还会在交换链通知中主动读取它。
    /// <para>
    /// <b>这一段里 mpv 是被页面冻住的</b>（用户令 2026-09-23，判据在 <see cref="ResizeFreeze"/>）——
    /// 从前这里写的是「视频仍往同一块缓冲里连续出帧」，那话只说明缓冲不换、不断言内容在动：拖动中每一拍
    /// 都在换内容，正是收尾撤掉覆盖层时会跳的那一段。冻住之后这条链只被押一帧就静止，覆盖层上那帧与
    /// 撤掉时屏上那帧是同一帧。
    /// </para>
    /// </summary>
    internal void SetInteractiveResize(bool active)
    {
        if (_disposed || active == IsInteractiveResize) return;
        lock (_gate)
            _interactiveBuffer = active && _attached != IntPtr.Zero ? _size : default;
        if (active) _geometryTimer.Stop();
        else SynchronizeGeometry();
    }

    public (int Width, int Height) Size
    {
        get { lock (_gate) return IsInteractiveResize ? _interactiveBuffer : _size; }
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
            if (_retainLastFrame && _visual is not null && _attached != IntPtr.Zero)
            {
                // 保留最后一帧。缓冲已经交还（_attached 归零），从此没有任何东西会来告诉我们
                // 「画面该多大」——但退场期间窗口要变高，那一帧必须跟着走，否则就是「窗口起来了、
                // 画面还缩在旧的左下角，其余是空的」（2026-09-20 用户报「退出播放时下方瞬间出现
                // 大片空白」）。
                //
                // 驱动只有一个：呈现计时器。它原本在这里就被停掉，于是退场那条路上再没有任何
                // 一拍；现在留着它，由 RefreshRetained 按「宿主尺寸 vs 上一拍宿主尺寸」逐拍把
                // 那一帧摆到位。退场结束（RetainLastFrame=false）或保险丝到点自己收。
                _retained = true;
                ClearAttachedForRetain();
                WatchRetained();
                return;
            }
            _presentationTimer.Stop();
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
    /// 客户区跳变（进出全屏、最大化／还原）这一拍：画面<b>当场落位</b>，没有过渡。
    /// <para>
    /// 用户令 2026-09-20：「去掉集成模式下切换全屏和窗口化的画面动画，参考其他播放器正常切换就好」。
    /// 这里原先是一趟 240ms 的「画面跑动」—— 旧缓冲按旧客户区矩形起手，一路长到新矩形。窗口是瞬时
    /// 跳变的，画面却要慢慢爬过去，读起来就是「切一下慢半拍」。
    /// </para>
    /// <para>
    /// <b>为什么摆的是「按画面比例居中」而不是「铺满」。</b>跳变之后缓冲还在旧尺寸上（mpv 重建缓冲
    /// 实测 110~150ms），而这一跳的宿主形状与旧窗口形状恰恰不是一个比例（窗口化时窗口被整形为片子
    /// 形状，全屏是显示器形状）。铺满＝把上一帧按新窗口的长宽比拉变形，那 150ms 里看到的就是「画面
    /// 猛地扁一下」；按画面自己的比例摆进去，露出来的正好是这一帧最终该在的那个框（2.35:1 的片子进
    /// 16:9 全屏＝左右贴边、上下留黑），缓冲一到就换成铺满，两边的<b>可见画面在同一处</b>，中间一帧
    /// 都不跳。这两个落点由 <see cref="FrameBox"/> 一处算出。
    /// </para>
    /// <para>
    /// 这也是 <see cref="_resizePending"/>（「缓冲追赶期间照旧铺满」）不适用于此的原因 —— 那条规则
    /// 是给<b>拖动</b>的，见它自己的注释。所以这一拍把它清掉。
    /// </para>
    /// <para>
    /// 宿主矩形取入参里的新客户区、不读 <c>_host.ActualWidth</c>：<b>岛的尺寸通知晚于
    /// <c>SetWindowPos</c>，而且刚跳完那几拍报出来的可能是个中间尺寸</b>（2026-09-20 探针实测：
    /// 进全屏那一刻窗口已是 1080×1872，岛报的是 870×648）。窗口自己报的矩形才是这一拍的事实，
    /// 所以把它交给 <see cref="_snappedClient"/> 钉住一小段，见那里的实测记录。
    /// </para>
    /// </summary>
    internal void SnapPresentation(NativeRect toScreen)
    {
        if (_disposed || toScreen.Width <= 0 || toScreen.Height <= 0) return;

        _resizePending = false;
        _snappedClient = (toScreen.Width, toScreen.Height);
        _snappedAt = _presentationClock.ElapsedMilliseconds;
        SetSize(_snappedClient);
        _geometryTimer.Stop();
        RefreshPresentation();
        Dispatch();
        WatchPresentation();
        Log.Debug(Category, $"画面落位：{toScreen.Width}×{toScreen.Height}（无过渡）");
    }

    internal void ResizePresentation(NativeRect client)
    {
        if (_disposed || client.Width <= 0 || client.Height <= 0) return;
        _snappedClient = (client.Width, client.Height);
        _snappedAt = _presentationClock.ElapsedMilliseconds;
        if (!SetSize(_snappedClient)) return;
        Schedule();
    }

    internal void RefreshPresentation()
    {
        if (_disposed || _visual is null || _attached == IntPtr.Zero) return;

        var raster = RasterScale();
        var stats = _retained ? default : ReadSurfaceStats(_attached);
        CountPresents(stats.Presents);
        var observed = (Width: stats.Width, Height: stats.Height);
        if (observed is { Width: > 0, Height: > 0 } && observed != _attachedContent)
        {
            _attachedContent = observed;
            Log.Debug(Category, $"实际视频缓冲 {observed.Width}x{observed.Height}，宿主 {_size.Width}x{_size.Height}");
        }
        TrackPixels(observed);

        // 缓冲追上宿主了，这一趟尺寸追赶到此为止 —— 拖动的「正在追赶」解除。
        if (_resizePending && _attachedContent is { Width: > 0, Height: > 0 }
            && VideoPresentation.Matches(_attachedContent.Width, _attachedContent.Height, _size.Width, _size.Height))
        {
            _resizePending = false;
        }
        if (_attachedContent is not { Width: > 0, Height: > 0 }) return;

        var frame = CurrentFrame();
        if (frame.Width <= 0 || frame.Height <= 0) return;

        ApplyFrame(frame, raster);
    }

    /// <summary>缓冲以实际读数为准；交还交换链后仍保留尺寸供最后一帧使用。</summary>
    private void TrackPixels((int Width, int Height) observed)
    {
        if (observed is not { Width: > 0, Height: > 0 }) return;
        _pixels = observed;
    }

    /// <summary>
    /// 这一帧此刻该占的矩形（物理像素，以宿主客户区左上角为原点）。跳变刚过的那一小段拿
    /// <see cref="_snappedClient"/>（窗口报的矩形）当宿主，其余时候是岛这一刻的真实尺寸；
    /// 摆法一律交给 <see cref="FrameBox"/>。<see cref="_clientRect"/> 记的就是这一份。
    /// </summary>
    private VideoPresentation.Rect CurrentFrame()
    {
        var hostWidth = _host.ActualWidth * RasterScale();
        var hostHeight = _host.ActualHeight * RasterScale();

        if (_snappedClient is { Width: > 0, Height: > 0 } jumped)
        {
            var layout = VideoSurfaceSize.FromDips(_host.ActualWidth, _host.ActualHeight, RasterScale());
            if (VideoPresentation.ResizeSettled(_attachedContent.Width, _attachedContent.Height,
                    layout.Width, layout.Height, jumped.Width, jumped.Height)
                || _presentationClock.ElapsedMilliseconds - _snappedAt > SnapTrustMilliseconds)
                _snappedClient = default;
            else
                return _clientRect = FrameBox(jumped.Width, jumped.Height, _resizePending);
        }

        if (hostWidth <= 0 || hostHeight <= 0) return _clientRect;
        return _clientRect = FrameBox((int)hostWidth, (int)hostHeight, _resizePending);
    }

    /// <summary>
    /// 这一帧在<b>给定宿主矩形</b>里该占的框（物理像素，以宿主客户区左上角为原点）。缓冲与宿主一致
    /// ＝铺满整块宿主；<b>或缓冲正在追赶新宿主（<paramref name="resizePending"/>）也铺满宿主</b>；否则
    /// 按缓冲自身的比例居中摆进去（宁可留黑边，也不拉变形）。
    /// <para>
    /// <b>为什么「正在追赶」也要铺满（2026-09-20）。</b>拖动窗口边缘时每一拍 <c>WM_SIZE</c> 都会
    /// 把宿主尺寸换成新的，而 mpv 那边要 110~150ms 才把缓冲换过来；这中间 <c>IsContentReady</c> 是假
    /// 的，旧版于是掉进 contain 分支、把画面按自身比例缩进新宿主 —— 上下各出一条黑边，下一拍缓冲追上
    /// 了又铺满，如此反复，用户看到的就是「拖动时画面一直闪」。而拖动这一路窗口本来就被
    /// <c>WM_SIZING</c> 按画面比例锁着，窗口形状就是画面形状，画面本就该铺满它。所以「等缓冲」的这段
    /// 时间继续保持铺满，缓冲到位只是清晰度变好，中间一帧都不闪。
    /// </para>
    /// <para>
    /// 宿主矩形与「正在追赶」都做成入参而不是当场读：宿主有两个来源（逐拍那条读岛的真实尺寸，跳变那
    /// 一拍读窗口刚报出来的新矩形），而「追赶期铺满」在跳变那条路上必须关掉（见 <see cref="CurrentFrame"/>）
    /// —— 两处必须算出同一个落点，否则「缓冲一到就换成铺满」的那一下会看见画面挪位。
    /// </para>
    /// </summary>
    private VideoPresentation.Rect FrameBox(int hostWidth, int hostHeight, bool resizePending)
    {
        if (_attachedContent is not { Width: > 0, Height: > 0 } content) return default;
        if (hostWidth <= 0 || hostHeight <= 0) return default;

        if (VideoPresentation.ShouldFill(content.Width, content.Height, hostWidth, hostHeight, resizePending))
            return new VideoPresentation.Rect(0, 0, hostWidth, hostHeight);

        var contain = VideoPresentation.Contain(content.Width, content.Height, hostWidth, hostHeight, 1);
        return new VideoPresentation.Rect(
            contain.Left, contain.Top,
            content.Width * contain.ScaleX, content.Height * contain.ScaleY);
    }

    /// <summary>
    /// 把这一帧摆进目标矩形。
    /// <para>
    /// <b>这里必须用 Offset＋Scale，不能写 Size。</b>2026-09-22 的「缩放定律」实测（
    /// <c>--probe-composition</c> 一节，四拍读数就在报告里）：交换链的像素是被<b>一比一贴在宿主左上角</b>
    /// 的 —— <c>SpriteVisual.Size</c> 与画笔的 <c>Stretch=Fill</c> 都不改变它画出来的尺寸，
    /// <c>Size</c> 只当裁剪框（在本地坐标里、早于 <c>Scale</c>），真正决定画面多大、在哪儿的只有
    /// <c>Offset</c> 与 <c>Scale</c>，而缩放中心必须钉在原点。
    /// </para>
    /// <para>
    /// <b>为什么「写 Size」这个写法能在树里活这么久。</b>缓冲与宿主一致时两种写法算出同一个结果
    /// （Offset=0、Scale=1），所以稳定期永远看不出问题；只有<b>缓冲还在旧尺寸上的那几拍</b>才露馅 ——
    /// 放大时画面以旧尺寸缩在左上角、右下露出页面底色，缩小时被窗口切掉。用户 2026-09-22 报的
    /// 「放大短时铺不满、缩小短时被裁切」正是这两条，详见 <see cref="FrameBox"/> 与 <c>_resizePending</c>。
    /// </para>
    /// <para>
    /// 缩放是「两个物理像素长度之比」（与光栅化比例无关），落点按光栅化比例折算成 DIP ——
    /// 与 <see cref="VideoPresentation.ForFrame"/> 的约定一致，生产路径上唯一算摆放的地方也是它。
    /// </para>
    /// </summary>
    private void ApplyFrame(VideoPresentation.Rect frame, double raster)
    {
        if (_probePinned) return;
        if (_visual is not { } visual) return;
        // 请求尺寸可能已经改变；缩放的分母只能是实际缓冲，不能提前换成目标尺寸。
        if (_pixels is not { Width: > 0, Height: > 0 } content) return;
        if (frame.Width <= 0 || frame.Height <= 0 || raster <= 0) return;

        var placement = VideoPresentation.ForFrame(content.Width, content.Height, frame, raster);
        visual.CenterPoint = Vector3.Zero;
        visual.Size = new Vector2((float)(content.Width / raster), (float)(content.Height / raster));
        visual.Offset = new Vector3((float)placement.Left, (float)placement.Top, 0);
        visual.Scale = new Vector3((float)placement.ScaleX, (float)placement.ScaleY, 1);
    }

    /// <summary>
    /// <b>探针专用</b>（<c>--probe-composition</c> 的「缩放定律」一节）：把 SpriteVisual 的摆放三件套直接
    /// 钉在给定值上（DIP），并让逐拍那条路与保留态那条路都不再覆盖它。
    /// <para>
    /// <b>为什么必须有这个口子。</b>2026-09-22 的问题「集成模式放大时画面铺不满、缩小时被裁切」只可能来自
    /// 一句话：旧缓冲被换进新客户区的那几拍里，合成器到底认不认 <c>Size</c> 与 <c>Scale</c>。生产代码只会把
    /// 两者按同一个假设算出来（Size＝目标矩形、Scale＝1），永远得不出反证；这里把它们分开钉住，就能在
    /// <b>窗口完全不动</b>的条件下逐条量屏幕像素 —— 排除了 XAML 布局、岛裁剪和 mpv 重建时间三个干扰项。
    /// 生产代码永不调用；用完 <see cref="ReleaseProbePlacement"/> 放掉。
    /// </para>
    /// </summary>
    internal void PinProbePlacement(double left, double top, double width, double height, double scale)
    {
        if (_visual is not { } visual) return;
        _probePinned = true;
        visual.CenterPoint = Vector3.Zero;
        visual.Size = new Vector2((float)width, (float)height);
        visual.Offset = new Vector3((float)left, (float)top, 0);
        visual.Scale = new Vector3((float)scale, (float)scale, 1);
    }

    /// <summary>
    /// <b>探针专用</b>：换一条摆放路线 —— 把画笔自己钉成 <c>Stretch=None</c> 加一个纯缩放的
    /// <c>TransformMatrix</c>（这是 <see cref="CompositionSurfaceBrush"/> 文档里「不想让画笔替我缩放」时的
    /// 写法）。与 <see cref="PinProbePlacement"/> 二选一，用来分辨「像素缩放」这件事到底该由视觉的
    /// <c>Scale</c> 干还是由画笔的矩阵干 —— 2026-09-22 实测：<c>Size</c> 与 <c>Stretch=Fill</c> 都不干。
    /// </summary>
    internal void PinProbeBrushScale(double scale)
    {
        if (_brush is null || _visual is null) return;
        _probePinned = true;
        _visual.CenterPoint = Vector3.Zero;
        _visual.Offset = Vector3.Zero;
        _visual.Scale = Vector3.One;
        _brush.Stretch = CompositionStretch.None;
        _brush.TransformMatrix = Matrix3x2.CreateScale((float)scale);
    }

    /// <summary>放掉 <see cref="PinProbePlacement"/> 的钉子，逐拍那条路从此照旧写。</summary>
    internal void ReleaseProbePlacement()
    {
        _probePinned = false;
        if (_brush is not { } brush) return;
        brush.Stretch = CompositionStretch.Fill;
        brush.TransformMatrix = Matrix3x2.Identity;
    }

    /// <summary>摆放三件套被探针钉住了（此时生产代码算出来的那一版不会写进视觉）。</summary>
    internal bool IsProbePinned => _probePinned;

    /// <summary>
    /// 交还交换链但把画面留在屏上：视觉与画笔原样不动（断链不会让已经画上去的那一帧消失），
    /// 只把「这一帧占的矩形」记进 <see cref="_retainedRect"/>，交给 <see cref="RefreshRetained"/>
    /// 在退场期间继续摆放。
    /// </summary>
    private void ClearAttachedForRetain()
    {
        // 要的是「画面自己的形状」，不是「宿主当时的形状」。
        //
        // 2026-09-20 用户截图那条「丑」的三次修正，记在这里免得又绕回去。三个候选数里有两个会被
        // 窗口污染，只有一个是干净的：
        //  - _clientRect：窗口与画面同形时对；不同形时 mpv 的 composition 缓冲跟着宿主走，
        //    ShouldFill 为真、整幅等于宿主 —— 拿它当画面形状就是拿窗口比例当画面比例。
        //    竖屏档实测 shape=1080x1872 对 16:9 的片子，最后一帧被缩成 42% 宽的窄带。
        //  - PictureAspect：mpv 报的 dwidth/dheight 是**显示**尺寸，窗口里被 letterbox/panscan
        //    之后同样与宿主同形（竖屏档实测 0.577）。一样信不得。
        //  - SourceAspect：片子码流自己的宽高比，与窗口无关。**只有它是「画面本来长什么样」**。
        //
        // 所以顺序：SourceAspect（干净）→ PictureAspect（与宿主不同形时才算答了问题）→
        // _clientRect（与宿主不同形时）→ 缓冲尺寸 → 宿主。
        var hostWidth = _host.ActualWidth;
        var hostHeight = _host.ActualHeight;
        var hostAspect = hostWidth > 0 && hostHeight > 0 ? hostWidth / hostHeight : 0;

        // 「与宿主不同形」＝差 5% 以上。这是判断一个数是否只是把窗口比例抄了一遍的唯一办法。
        static bool Differs(double aspect, double host) =>
            aspect > 0 && (host <= 0 || Math.Abs(aspect / host - 1) > 0.05);

        var rectAspect = _clientRect.Width > 0 && _clientRect.Height > 0
            ? _clientRect.Width / _clientRect.Height
            : 0;

        (double Width, double Height) chosen;
        if (SourceAspect > 0)
            chosen = (SourceAspect, 1);
        else if (Differs(PictureAspect, hostAspect))
            chosen = (PictureAspect, 1);
        else if (Differs(rectAspect, hostAspect))
            chosen = (_clientRect.Width, _clientRect.Height);
        else if (_attachedContent is { Width: > 0, Height: > 0 } content)
            chosen = (content.Width, content.Height);
        else
            chosen = (hostWidth > 0 ? hostWidth : 1, hostHeight > 0 ? hostHeight : 1);

        // 兜底：前面每一路都被宿主污染时（竖屏档就是这么来的），宁可拿缓冲比例，也不要摆出一条带。
        if (hostAspect > 0 && Math.Abs(chosen.Width / chosen.Height / hostAspect - 1) <= 0.02
            && _attachedContent is { Width: > 0, Height: > 0 } spare
            && Math.Abs((double)spare.Width / spare.Height / hostAspect - 1) > 0.02)
            chosen = (spare.Width, spare.Height);

        _retainedShape = chosen;
        // 保留态要用的「这块像素有多大」就是准星 `_pixels`（RefreshRetained 自己读），不必再留一份。
        ReleaseAttached();
        _attachedContent = default;
    }

    /// <summary>
    /// 保留态的逐拍：把那一帧按<b>画面自己的比例</b>等比放进新宿主（contain，居中）。退场时窗口
    /// 一口气变回浏览几何（<see cref="HostWindow.RestorePlayerToBrowse"/>），画面必须跟着走到新
    /// 框里，否则下半屏就是空的 —— 用户看到的「退出播放时下方瞬间出现大片空白」（2026-09-20）。
    /// <para>
    /// 与正片那两条（<see cref="CurrentFrame"/> 逐拍摆、<see cref="SnapPresentation"/> 跳变落位）的
    /// 区别：这里没有缓冲、也没有新尺寸可等，手上的输入只有宿主尺寸、画面形状和那一块像素自己的尺寸，
    /// 所以是「把这张画放进变大了的框里」。退场那一跳本来就是窗口瞬变，画面跟着瞬变，两边才不会各走各的。
    /// </para>
    /// <para>
    /// 摆法照旧走 <see cref="ApplyFrame"/> 那条规矩（Size＝裁剪框、Scale 才是大小、CenterPoint 在原点）：
    /// 先在缓冲里按画面比例找出「画面占哪一块」（<c>inner</c>），再算出「上屏该占哪一块」（<c>fit</c>），
    /// 两块之比就是 Scale，两块左上角之差就是 Offset。画面四周那圈黑边是缓冲自带的，会一起跟着走。
    /// </para>
    /// </summary>
    private void RefreshRetained()
    {
        if (_probePinned || _disposed || !_retained || _visual is null) return;

        var raster = RasterScale();
        var hostWidth = _host.ActualWidth * raster;
        var hostHeight = _host.ActualHeight * raster;
        if (hostWidth <= 0 || hostHeight <= 0) return;

        var shape = _retainedShape;
        if (shape.Width <= 0 || shape.Height <= 0) return;

        // 缓冲已经交还，但**那块像素还在屏上**，而它是最后一帧的整块交换链 —— 画面只占其中一块
        //（mpv 在缓冲里按画面比例留过黑边）。所以按「画面在缓冲里的位置 → 画面上屏该占的矩形」算，
        // 不能把「形状」当成整块内容去铺：`_retainedShape` 是一个**比例**（比如 1.777×1），拿它当
        // SpriteVisual.Size 会得到一个不到两像素宽的裁剪框（Size 是裁剪框，2026-09-22 实测），
        // 最后一帧会被裁成一条线再放大 —— 屏上是一块糊掉的纯色。Size 取整块缓冲 ÷ 光栅化比例，
        // 那正是「把整块内容都留在框里」。准星（`_pixels`）说了不上来时退回形状，与从前一样。

        // 画面在缓冲里占哪一块：按画面比例居中放进去（mpv 留黑边也是居中的）。形状是一个**比例**
        //（比如 16/9 × 1），所以先把它放大到「贴着缓冲的那一边」的尺度，再居中。
        var (inner, innerScale, content) = RetainedInner();
        if (inner.Width <= 0 || inner.Height <= 0) return;

        // 画面上屏该占哪一块：默认**留边**（宁可留黑边也不把画面拉变形），退场那一趟是**铺满**
        //（RetainFill，理由见它自己的注释）。
        var fit = VideoPresentation.FitScale(shape.Width, shape.Height, hostWidth, hostHeight, RetainFill);
        if (fit <= 0) return;

        // 两头都是「画面那一块」，所以一个比例就够；落点要减掉画面在缓冲里的偏移。
        var scale = fit / innerScale;
        var targetLeft = (hostWidth - shape.Width * fit) / 2;
        var targetTop = (hostHeight - shape.Height * fit) / 2;

        var visual = _visual;
        visual.CenterPoint = Vector3.Zero;
        visual.Size = new Vector2((float)(content.Width / raster), (float)(content.Height / raster));
        visual.Scale = new Vector3((float)scale, (float)scale, 1);
        visual.Offset = new Vector3(
            (float)((targetLeft - inner.Left * scale) / raster),
            (float)((targetTop - inner.Top * scale) / raster), 0);

        // 压暗那一支也逐拍写：退场是「一边跟着宿主重摆、一边随着整页暗下去」，两个都要每拍对齐。
        // 正常路径下 _retainDim 是 0，这一句就是把它写回原亮度（换集的留帧因此绝不会继承上一趟的暗）。
        visual.Opacity = (float)(1 - _retainDim);
    }

    /// <summary>
    /// 保留态里「画面」在那一块缓冲里占哪一块（缓冲物理像素）、「形状的一个单位等于多少缓冲像素」，
    /// 以及那块缓冲本身多大。<see cref="RefreshRetained"/> 用它把画面摆到该在的位置上。
    /// </summary>
    private (VideoPresentation.Rect Inner, double Scale, (double Width, double Height) Content) RetainedInner()
    {
        var shape = _retainedShape;
        if (shape.Width <= 0 || shape.Height <= 0) return (default, 0, default);

        // 缓冲自己的尺寸（物理像素）：优先用准星（这是画面被画进去时的尺寸），退而用形状。
        var content = _pixels is { Width: > 0, Height: > 0 } known
            ? (Width: (double)known.Width, Height: (double)known.Height)
            : shape;

        var scale = Math.Min(content.Width / shape.Width, content.Height / shape.Height);
        if (!double.IsFinite(scale) || scale <= 0) return (default, 0, content);

        var width = shape.Width * scale;
        var height = shape.Height * scale;
        return (new VideoPresentation.Rect(
            (content.Width - width) / 2, (content.Height - height) / 2, width, height), scale, content);
    }

    private void WatchRetained()
    {
        if (_disposed) return;
        _retainedWatchStarted = _presentationClock.ElapsedMilliseconds;
        _presentationTimer.Start();
    }

    /// <summary>离散的窗口状态变化立即派发，不把拖动防抖的 100ms 加到最大化与全屏上。</summary>
    internal void SynchronizeGeometry()
    {
        if (_disposed) return;
        Measure();
        _geometryTimer.Stop();
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
        // 退场保留态走自己那条：没有缓冲，只有「跟着宿主重摆」这一件事，且不能因为
        // _attached 归零就被判停 —— 那正是它的常态。
        if (_retained)
        {
            RefreshRetained();
            if (_disposed || !_retained)
                _presentationTimer.Stop();
            else if (_presentationClock.ElapsedMilliseconds - _retainedWatchStarted > RetainedCeilingMilliseconds)
                RetainLastFrame = false;
            return;
        }

        RefreshPresentation();
        if (_disposed || _attached == IntPtr.Zero
            || (IsContentReady && _snappedClient == default && !AwaitingPicture)
            || _presentationClock.ElapsedMilliseconds - _watchStarted > WatchCeilingMilliseconds)
            _presentationTimer.Stop();
    }

    private double RasterScale()
    {
        var raster = _host.XamlRoot?.RasterizationScale ?? 1;
        return raster > 0 ? raster : 1;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        SubscribeRoot(_host.XamlRoot);
        Measure();
        QueueGeometry();
        if (_attached == IntPtr.Zero) return;
        try { Attach(_attached); }
        catch (Exception error) { Log.Warn(Category, "恢复 Composition 视频层失败", error); }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _geometryTimer.Stop();
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
        var size = VideoSurfaceSize.FromDips(_host.ActualWidth, _host.ActualHeight, RasterScale());
        if (_snappedClient is { Width: > 0, Height: > 0 } target
            && _presentationClock.ElapsedMilliseconds - _snappedAt <= SnapTrustMilliseconds
            && !VideoPresentation.Matches(size.Width, size.Height, target.Width, target.Height))
            return false;
        return SetSize(size);
    }

    private bool SetSize((int Width, int Height) size)
    {
        lock (_gate)
        {
            if (_size == size) return false;
            _previous = _size;
            _size = size;
            return true;
        }
    }

    private void QueueGeometry()
    {
        // 合并一拍内的变化，但后来的尺寸不能把已经排好的派发继续向后推。
        if (!_geometryHeld && !IsInteractiveResize && !_geometryTimer.IsRunning) _geometryTimer.Start();
    }

    private void Schedule()
    {
        // 保留态：宿主一变就当场重摆，不走防抖。退场时窗口是一口气变高的（不是拖动），
        // 拖上防抖就是「窗口已经到位、画面过一会儿才跟过来」——那正是要修的空白。
        if (_retained)
        {
            RefreshRetained();
            WatchRetained();
            return;
        }

        // 宿主报了新尺寸、缓冲还没跟上：这一段画面继续铺满（CurrentFrame 里的 _resizePending），
        // 中间一帧都不回退成 contain，拖边才不会闪（2026-09-20）。追赶由 RefreshPresentation 在
        // 缓冲到位那一拍解除；下面照旧把新尺寸交给 mpv。
        if (!IsContentReady && _attachedContent is { Width: > 0, Height: > 0 }) _resizePending = true;

        RefreshPresentation();
        WatchPresentation();
        var before = (double)_previous.Width * _previous.Height;
        var after = (double)_size.Width * _size.Height;
        if (before > 0 && after > 0 && (after >= before * 1.5 || after * 1.5 <= before))
        {
            _geometryTimer.Stop();
            Dispatch();
        }
        else QueueGeometry();
    }

    private void OnGeometryTick(DispatcherQueueTimer sender, object args) => Dispatch();
    private void Dispatch()
    {
        if (_geometryHeld || IsInteractiveResize) return;
        GeometryChanged?.Invoke();
    }

    private void ClearSurface()
    {
        ReleaseVisual();
        ReleaseAttached();
        _attachedContent = default;
        _retained = false;
        _retainedShape = default;
        _pixels = default;
        lock (_gate) _interactiveBuffer = default;
        _clientRect = default;
        _resizePending = false;
        _snappedClient = default;
        _snappedAt = 0;

        // 「有没有真画面」这一对也随这一场作废：收摊之后下一场从头等。（会话交界那一刀在
        // BeginPictureWait —— 那一条由遮罩亮起时砍，这一条只管收摊。）
        _pictureStarted = false;
        Interlocked.Exchange(ref _presentsSinceArm, 0);
        _presentsAtAttach = -1;
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
        _geometryTimer.Stop();
        _geometryTimer.Tick -= OnGeometryTick;
        _presentationTimer.Stop();
        _presentationTimer.Tick -= OnPresentationTick;
        GeometryChanged = null;
        ClearSurface();
        _retainDim = 0;
        RetainFill = false;
        SourceAspect = 0;
    }

    /// <summary>
    /// 这条链现在是什么尺寸、被押过几帧 —— 一次 QueryInterface 里问完（两支都在同一张表上）。
    /// Present 计数是「有没有真画面」的唯一证据，出处与实测见 <see cref="PictureReveal"/>。
    /// </summary>
    private static (int Width, int Height, int Presents) ReadSurfaceStats(IntPtr chain)
    {
        if (Marshal.QueryInterface(chain, in SwapChain1Iid, out var swapChain1) < 0) return default;
        try
        {
            var table = Marshal.ReadIntPtr(swapChain1);
            var describe = Marshal.GetDelegateForFunctionPointer<GetDescriptionDelegate>(
                Marshal.ReadIntPtr(table, GetDescriptionSlot * IntPtr.Size));
            var count = Marshal.GetDelegateForFunctionPointer<GetPresentCountDelegate>(
                Marshal.ReadIntPtr(table, GetPresentCountSlot * IntPtr.Size));

            var presents = count(swapChain1, out var presentCount) >= 0 && presentCount <= int.MaxValue
                ? (int)presentCount : -1;

            return describe(swapChain1, out var description) >= 0
                && description.Width <= int.MaxValue && description.Height <= int.MaxValue
                ? ((int)description.Width, (int)description.Height, presents) : (0, 0, presents);
        }
        finally { Marshal.Release(swapChain1); }
    }

    /// <summary>
    /// 数这条链被押过几帧。第一份读数只当基准 —— <b>建链那一下 mpv 就会连押两三帧空画面</b>（实测挂链
    /// 那一刻计数已经是 2），把基准当成「押过了」等于什么都没拦。链换了（计数比基准小）重新取基准。
    /// </summary>
    private void CountPresents(int presents)
    {
        if (presents < 0) return;
        if (_presentsAtAttach < 0 || presents < _presentsAtAttach)
        {
            _presentsAtAttach = presents;
            return;
        }
        if (presents == _presentsAtAttach) return;

        _presentsAtAttach = presents;
        Interlocked.Increment(ref _presentsSinceArm);
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

    /// <summary><c>IDXGISwapChain::GetLastPresentCount</c>：出参是这条链累计押过多少帧。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPresentCountDelegate(IntPtr self, out uint presentCount);
}
